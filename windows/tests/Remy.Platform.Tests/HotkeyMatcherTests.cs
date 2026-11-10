using Remy.Core.Hotkeys;
using Remy.Platform.Hotkeys;
using Xunit;

namespace Remy.Platform.Tests;

public class HotkeyMatcherTests
{
    const int LCtrl = HotkeyMatcher.VkLCtrl, RCtrl = HotkeyMatcher.VkRCtrl, LWin = HotkeyMatcher.VkLWin, LShift = HotkeyMatcher.VkLShift, LAlt = HotkeyMatcher.VkLAlt;
    const int Esc = 0x1B, Space = 0x20, KeyA = 0x41;

    sealed class Rig
    {
        public long Now;
        public readonly List<HotkeyOutput> Out = [];
        public readonly HotkeyMatcher M;
        public Rig() { M = new HotkeyMatcher(Out.Add, () => Now); }
        public KeyResult Down(int vk) => M.Process(vk, true);
        public KeyResult Up(int vk) => M.Process(vk, false);
        public int Count(HotkeyOutputKind k) => Out.Count(o => o.Kind == k);
    }

    static HotkeyBinding Mod(HotkeyModifiers m, HotkeyMode mode = HotkeyMode.HoldToRecord) => new(Guid.NewGuid(), null, m, "x", mode);
    static HotkeyBinding Keyed(int vk, HotkeyModifiers m, HotkeyMode mode = HotkeyMode.HoldToRecord) => new(Guid.NewGuid(), vk, m, "x", mode);

    [Fact]
    public void ModifierOnly_Hold_PressesAndReleases_NeverSwallowed()
    {
        var r = new Rig();
        var b = Mod(HotkeyModifiers.Ctrl | HotkeyModifiers.Win);
        r.M.Configure([b], null, true);
        Assert.False(r.Down(LCtrl).Swallow);
        Assert.Empty(r.Out);
        Assert.False(r.Down(LWin).Swallow);
        Assert.Single(r.Out);
        Assert.Equal(HotkeyOutputKind.Pressed, r.Out[0].Kind);
        r.Up(LWin);
        Assert.Equal(HotkeyOutputKind.Released, r.Out[^1].Kind);
    }

    [Fact]
    public void ModifierOnly_IsExact_ExtraModifierDeactivates()
    {
        var r = new Rig();
        r.M.Configure([Mod(HotkeyModifiers.Ctrl | HotkeyModifiers.Win)], null, true);
        r.Down(LCtrl); r.Down(LWin);
        r.Down(LShift);                      // extra modifier -> inactive -> release
        Assert.Equal(1, r.Count(HotkeyOutputKind.Released));
        r.Up(LShift);                        // exact again -> presses again
        Assert.Equal(2, r.Count(HotkeyOutputKind.Pressed));
    }

    [Fact]
    public void ModifierOnly_NotActiveWhenExtraAlreadyHeld()
    {
        var r = new Rig();
        r.M.Configure([Mod(HotkeyModifiers.Ctrl)], null, true);
        r.Down(LShift); r.Down(LCtrl);
        Assert.Empty(r.Out);
    }

    [Fact]
    public void ModifierOnly_SideSpecific()
    {
        var r = new Rig();
        r.M.Configure([Mod(HotkeyModifiers.RightCtrl)], null, true);
        r.Down(LCtrl);
        Assert.Empty(r.Out);
        r.Up(LCtrl);
        r.Down(RCtrl);
        Assert.Equal(1, r.Count(HotkeyOutputKind.Pressed));
    }

    [Fact]
    public void ModifierOnly_GenericAcceptsEitherSide()
    {
        var r = new Rig();
        r.M.Configure([Mod(HotkeyModifiers.Ctrl)], null, true);
        r.Down(RCtrl);
        Assert.Equal(1, r.Count(HotkeyOutputKind.Pressed));
    }

    [Fact]
    public void ModifierOnly_Toggle_ReleaseEmitsNothing()
    {
        var r = new Rig();
        r.M.Configure([Mod(HotkeyModifiers.Ctrl, HotkeyMode.HandsFreeToggle)], null, true);
        r.Down(LCtrl); r.Up(LCtrl);
        Assert.Equal(1, r.Count(HotkeyOutputKind.Pressed));
        Assert.Equal(0, r.Count(HotkeyOutputKind.Released));
    }

    [Fact]
    public void WinModifierOnly_RequestsStartMenuSuppression_OnWinKeyUp()
    {
        var r = new Rig();
        r.M.Configure([Mod(HotkeyModifiers.Ctrl | HotkeyModifiers.Win)], null, true);
        r.Down(LCtrl); r.Down(LWin);
        var up = r.Up(LWin);
        Assert.True(up.SuppressStartMenu);
        Assert.False(up.Swallow);
        r.Up(LCtrl);
        r.Down(LWin);
        Assert.False(r.Up(LWin).SuppressStartMenu);   // plain Win tap is left alone
    }

    [Fact]
    public void Keyed_SwallowsDownAndUp_NoRetriggerOnRepeat_ExtraModifiersAllowed()
    {
        var r = new Rig();
        r.M.Configure([Keyed(Space, HotkeyModifiers.Ctrl)], null, true);
        r.Down(LCtrl);
        r.Down(LShift);                                   // extra modifier is fine for keyed
        Assert.True(r.Down(Space).Swallow);
        Assert.True(r.Down(Space).Swallow);               // auto-repeat swallowed
        Assert.True(r.Down(Space).Swallow);
        Assert.Equal(1, r.Count(HotkeyOutputKind.Pressed));
        r.Up(LCtrl);                                      // modifiers released first
        Assert.True(r.Up(Space).Swallow);
        Assert.Equal(1, r.Count(HotkeyOutputKind.Released));
    }

    [Fact]
    public void Keyed_DoesNotMatchWithoutRequiredModifier()
    {
        var r = new Rig();
        r.M.Configure([Keyed(Space, HotkeyModifiers.Ctrl)], null, true);
        Assert.False(r.Down(Space).Swallow);
        Assert.Empty(r.Out);
    }

    [Fact]
    public void Keyed_LeftSideRequired()
    {
        var r = new Rig();
        r.M.Configure([Keyed(KeyA, HotkeyModifiers.LeftCtrl)], null, true);
        r.Down(RCtrl);
        Assert.False(r.Down(KeyA).Swallow);
    }

    [Fact]
    public void HoldToCancel_PlainEsc_NotSwallowed_ProgressTicksThenFiresOnce()
    {
        var r = new Rig();
        var esc = Keyed(Esc, HotkeyModifiers.None);
        r.M.Configure([], esc, true);
        r.M.SetCancellationEnabled(true);
        Assert.False(r.Down(Esc).Swallow);
        Assert.Equal(0.0, r.Out[0].Progress);
        for (int i = 0; i < 50; i++) { r.Now += 25; r.M.Tick(); }
        Assert.Equal(1, r.Count(HotkeyOutputKind.CancelRequested));
        Assert.Null(r.Out[^1].Progress);                  // progress cleared after firing
        Assert.False(r.Down(Esc).Swallow);                // repeat does not rearm
        Assert.False(r.Up(Esc).Swallow);
        Assert.Equal(1, r.Count(HotkeyOutputKind.CancelRequested));
    }

    [Fact]
    public void HoldToCancel_EarlyRelease_ResetsWithoutFiring()
    {
        var r = new Rig();
        r.M.Configure([], Keyed(Esc, HotkeyModifiers.None), true);
        r.M.SetCancellationEnabled(true);
        r.Down(Esc);
        r.Now += 400; r.M.Tick();
        r.Up(Esc);
        Assert.Equal(0, r.Count(HotkeyOutputKind.CancelRequested));
        Assert.Null(r.Out[^1].Progress);
        Assert.Equal(HotkeyOutputKind.CancelProgress, r.Out[^1].Kind);
        r.Now += 2000; r.M.Tick();
        Assert.Equal(0, r.Count(HotkeyOutputKind.CancelRequested));
    }

    [Fact]
    public void ImmediateEsc_FiresOnDown_SwallowsDownRepeatAndUp()
    {
        var r = new Rig();
        r.M.Configure([], Keyed(Esc, HotkeyModifiers.None), false);
        r.M.SetCancellationEnabled(true);
        Assert.True(r.Down(Esc).Swallow);
        Assert.Equal(1, r.Count(HotkeyOutputKind.CancelRequested));
        Assert.True(r.Down(Esc).Swallow);
        Assert.True(r.Up(Esc).Swallow);
        Assert.Equal(1, r.Count(HotkeyOutputKind.CancelRequested));
        Assert.True(r.Down(Esc).Swallow);                 // fresh press re-arms
        Assert.Equal(2, r.Count(HotkeyOutputKind.CancelRequested));
    }

    [Fact]
    public void Cancel_Disabled_DoesNothing()
    {
        var r = new Rig();
        r.M.Configure([], Keyed(Esc, HotkeyModifiers.None), false);
        Assert.False(r.Down(Esc).Swallow);
        Assert.Empty(r.Out);
    }

    [Fact]
    public void OtherCancelCombo_SwallowedEvenInHoldMode_AndIgnoresActiveBindingModifiers()
    {
        var r = new Rig();
        var rec = Mod(HotkeyModifiers.Ctrl | HotkeyModifiers.Win);
        var cancel = Keyed(Esc, HotkeyModifiers.Shift);
        r.M.Configure([rec], cancel, true);
        r.M.SetCancellationEnabled(true);
        r.Down(LCtrl); r.Down(LWin);                      // recording binding active
        // Shift is extra for the modifier-only binding, so it deactivates: use plain Esc cancel instead below.
        r.Up(LWin); r.Up(LCtrl);
        r.Down(LShift);
        Assert.True(r.Down(Esc).Swallow);
        Assert.True(r.Up(Esc).Swallow);
    }

    [Fact]
    public void Cancel_IgnoresModifiersOwnedByActiveRecordingBinding()
    {
        var r = new Rig();
        var rec = Mod(HotkeyModifiers.Ctrl | HotkeyModifiers.Win);
        r.M.Configure([rec], Keyed(Esc, HotkeyModifiers.None), true);
        r.M.SetCancellationEnabled(true);
        r.Down(LCtrl); r.Down(LWin);                      // push-to-talk held
        r.Down(Esc);
        Assert.Equal(0.0, r.Out.Last(o => o.Kind == HotkeyOutputKind.CancelProgress).Progress);
        r.Up(Esc);
    }

    [Fact]
    public void Cancel_ModifierLoss_ResetsGesture()
    {
        var r = new Rig();
        r.M.Configure([], Keyed(KeyA, HotkeyModifiers.Ctrl), true);
        r.M.SetCancellationEnabled(true);
        r.Down(LCtrl); r.Down(KeyA);
        r.Now += 500; r.M.Tick();
        r.Up(LCtrl);
        Assert.Null(r.Out[^1].Progress);
        r.Now += 1000; r.M.Tick();
        Assert.Equal(0, r.Count(HotkeyOutputKind.CancelRequested));
    }

    [Fact]
    public void Generation_BumpsOnConfigureResetAndNeutral()
    {
        var r = new Rig();
        int g0 = r.M.Generation;
        r.M.Configure([], null, true);
        Assert.True(r.M.Generation > g0);
        int g1 = r.M.Generation;
        r.M.Reset();
        Assert.True(r.M.Generation > g1);
    }

    [Fact]
    public void RequireNeutral_BlocksUntilAllModifiersReleased()
    {
        var r = new Rig();
        r.M.Configure([Mod(HotkeyModifiers.Ctrl)], null, true);
        r.Down(LCtrl);
        r.Out.Clear();
        r.M.RequireNeutralModifiers();                    // like resume while Ctrl is still held
        r.Down(LShift); r.Up(LShift);
        Assert.Empty(r.Out);
        r.Up(LCtrl);                                      // neutral
        r.Down(LCtrl);
        Assert.Equal(1, r.Count(HotkeyOutputKind.Pressed));
    }

    [Fact]
    public void Recorder_KeyedCapture_UsesHeldModifiers()
    {
        var r = new Rig();
        HotkeyBinding? got = null; bool done = false;
        r.M.BeginRecording(false, b => { got = b; done = true; });
        r.Down(LCtrl); r.Down(LAlt); r.Down(Space);
        Assert.True(done);
        Assert.Equal(Space, got!.VirtualKey);
        Assert.Equal(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, got.Modifiers);
        Assert.Equal("Ctrl+Alt+Space", got.DisplayName);
    }

    [Fact]
    public void Recorder_ModifierOnly_CapturesLargestSuperset_OnFullRelease()
    {
        var r = new Rig();
        HotkeyBinding? got = null; bool done = false;
        r.M.BeginRecording(false, b => { got = b; done = true; });
        r.Down(LCtrl); r.Down(LWin);
        r.Up(LWin);                                       // not all released yet
        Assert.False(done);
        r.Up(LCtrl);
        Assert.True(done);
        Assert.Null(got!.VirtualKey);
        Assert.Equal(HotkeyModifiers.Ctrl | HotkeyModifiers.Win, got.Modifiers);
        Assert.Equal("Ctrl+Win", got.DisplayName);
    }

    [Fact]
    public void Recorder_RightSideIsPreserved()
    {
        var r = new Rig();
        HotkeyBinding? got = null;
        r.M.BeginRecording(false, b => got = b);
        r.Down(RCtrl); r.Up(RCtrl);
        Assert.Equal(HotkeyModifiers.RightCtrl, got!.Modifiers);
        Assert.Equal("RCtrl", got.DisplayName);
    }

    [Fact]
    public void Recorder_Esc_AbortsUnlessAllowed()
    {
        var r = new Rig();
        bool done = false; HotkeyBinding? got = new(Guid.Empty, 1, 0, "", 0);
        r.M.BeginRecording(false, b => { got = b; done = true; });
        r.Down(Esc);
        Assert.True(done); Assert.Null(got);

        var r2 = new Rig();
        HotkeyBinding? got2 = null;
        r2.M.BeginRecording(true, b => got2 = b);
        r2.Down(Esc);
        Assert.Equal(Esc, got2!.VirtualKey);
        Assert.Equal("Esc", got2.DisplayName);
    }

    [Fact]
    public void Recorder_IgnoresCapsLock_AndSuspendsMonitoring()
    {
        var r = new Rig();
        r.M.Configure([Keyed(Space, HotkeyModifiers.Ctrl)], null, true);
        bool done = false;
        r.M.BeginRecording(false, _ => done = true);
        r.Down(0x14); r.Up(0x14);
        Assert.False(done);
        r.Down(LCtrl);
        Assert.False(r.Down(Space).Swallow);              // old shortcut cannot fire while recording
        Assert.Empty(r.Out);
        Assert.True(done);
    }

    [Fact]
    public void Reconcile_SynthesizesLostModifierKeyUp()
    {
        var r = new Rig();
        r.M.Configure([Mod(HotkeyModifiers.Ctrl)], null, true);
        r.Down(LCtrl);
        r.M.ReconcileModifiers(HotkeyModifiers.None);     // OS says released but we never saw the key-up
        Assert.Equal(1, r.Count(HotkeyOutputKind.Released));
    }

    [Fact]
    public void TransformShortcutFiresOnceOnPress()
    {
        var r = new Rig();
        var b = Keyed(KeyA, HotkeyModifiers.Ctrl, HotkeyMode.HoldToRecord);   // hold mode is matched as press-once
        r.M.Configure([], null, true);
        r.M.ConfigureTransforms([b]);
        r.Down(LCtrl);
        Assert.True(r.Down(KeyA).Swallow);
        Assert.Single(r.Out);
        Assert.Equal(HotkeyOutputKind.TransformPressed, r.Out[0].Kind);
        Assert.Equal(b.Id, r.Out[0].Binding!.Id);
        Assert.True(r.Down(KeyA).Swallow);        // auto-repeat: swallowed, no fire
        Assert.Single(r.Out);
        Assert.True(r.Up(KeyA).Swallow);          // key-up swallowed, no release event
        Assert.Single(r.Out);
        Assert.Equal(0, r.Count(HotkeyOutputKind.Pressed) + r.Count(HotkeyOutputKind.Released));
        r.Down(KeyA);                              // a fresh press fires again
        Assert.Equal(2, r.Count(HotkeyOutputKind.TransformPressed));
    }
}
