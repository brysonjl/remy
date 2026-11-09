using Remy.Core.Hotkeys;

namespace Remy.Platform.Hotkeys;

public enum HotkeyOutputKind { Pressed, Released, CancelProgress, CancelRequested }

public readonly record struct HotkeyOutput(HotkeyOutputKind Kind, HotkeyBinding? Binding = null, double? Progress = null);

/// <summary>What the hook must do with one key event.</summary>
public readonly record struct KeyResult(bool Swallow, bool SuppressStartMenu);

/// <summary>
/// Pure hotkey state machine (pipeline.md 1.3, 1.5, 1.6). Fed with already-decoded, non-injected key events;
/// it tracks left/right modifier state itself and has no OS dependency, so it is unit-testable.
/// All public members are thread-safe (one lock); <see cref="Process"/> is cheap enough for the hook callback.
/// </summary>
public sealed class HotkeyMatcher
{
    public const int VkEscape = 0x1B, VkCapsLock = 0x14;
    public const int VkLShift = 0xA0, VkRShift = 0xA1, VkLCtrl = 0xA2, VkRCtrl = 0xA3, VkLAlt = 0xA4, VkRAlt = 0xA5, VkLWin = 0x5B, VkRWin = 0x5C;
    public static readonly TimeSpan CancelHoldDuration = TimeSpan.FromSeconds(1);

    static readonly (HotkeyModifiers Gen, HotkeyModifiers L, HotkeyModifiers R)[] Groups =
    [
        (HotkeyModifiers.Ctrl, HotkeyModifiers.LeftCtrl, HotkeyModifiers.RightCtrl),
        (HotkeyModifiers.Alt, HotkeyModifiers.LeftAlt, HotkeyModifiers.RightAlt),
        (HotkeyModifiers.Shift, HotkeyModifiers.LeftShift, HotkeyModifiers.RightShift),
        (HotkeyModifiers.Win, HotkeyModifiers.LeftWin, HotkeyModifiers.RightWin),
    ];

    const HotkeyModifiers WinBits = HotkeyModifiers.Win | HotkeyModifiers.LeftWin | HotkeyModifiers.RightWin;

    readonly object _gate = new();
    readonly Action<HotkeyOutput> _sink;
    readonly Func<long> _nowMs;

    IReadOnlyList<HotkeyBinding> _bindings = [];
    HotkeyBinding? _cancel;
    bool _holdToCancel = true;
    bool _cancelEnabled;

    HotkeyModifiers _held;                       // side bits only
    readonly HashSet<int> _downKeys = [];
    readonly HashSet<Guid> _active = [];
    bool _neutralRequired;
    bool _winSuppress;

    bool _cancelPressed, _cancelFired, _cancelOwnsUp;
    long _cancelStartMs;

    Recorder? _recorder;
    int _generation;

    public HotkeyMatcher(Action<HotkeyOutput> sink, Func<long>? nowMs = null)
    {
        _sink = sink;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>Bumped on Configure, Stop (Reset), RequireNeutralModifiers and recorder start. Queued events from older generations are stale.</summary>
    public int Generation { get { lock (_gate) return _generation; } }
    public HotkeyModifiers HeldModifiers { get { lock (_gate) return _held; } }
    public bool NeutralRequired { get { lock (_gate) return _neutralRequired; } }
    /// <summary>True while the consumer should call <see cref="Tick"/> (hold-to-cancel gesture running) or reconcile modifiers.</summary>
    public bool NeedsTick { get { lock (_gate) return (_cancelPressed && _holdToCancel && !_cancelFired) || _active.Count > 0 || _held != HotkeyModifiers.None && _cancelPressed; } }
    public bool IsRecording { get { lock (_gate) return _recorder != null; } }

    public void Configure(IReadOnlyList<HotkeyBinding> bindings, HotkeyBinding? cancel, bool holdToCancel)
    {
        lock (_gate)
        {
            _bindings = bindings.ToArray();
            _cancel = cancel is { } c && HasBinding(c) ? c : null;
            _holdToCancel = holdToCancel;
            _active.Clear();
            _cancelOwnsUp = false;
            ResetGestureLocked();
            _generation++;
        }
    }

    /// <summary>Stop monitoring state: clears active bindings and gesture. Held-modifier tracking is kept.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _active.Clear();
            _downKeys.Clear();
            _winSuppress = false;
            ResetGestureLocked();
            _generation++;
        }
    }

    public void SetCancellationEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_cancelEnabled == enabled) return;
            _cancelEnabled = enabled;
            if (!enabled) ResetGestureLocked();
        }
    }

    public void RequireNeutralModifiers()
    {
        lock (_gate)
        {
            _neutralRequired = _held != HotkeyModifiers.None;
            _active.Clear();
            _downKeys.Clear();
            ResetGestureLocked();
            _generation++;
        }
    }

    public void ResetCancellationGesture() { lock (_gate) ResetGestureLocked(); }

    /// <summary>Replace the tracked modifier state with the physical state (start, hook reinstall, resume).</summary>
    public void SyncModifiers(HotkeyModifiers physical)
    {
        lock (_gate)
        {
            _held = physical & SideMask;
            if (_neutralRequired && _held == HotkeyModifiers.None) _neutralRequired = false;
        }
    }

    /// <summary>Synthesizes key-ups for modifiers the matcher thinks are held but the OS says are released (lost key-ups).</summary>
    public void ReconcileModifiers(HotkeyModifiers physical)
    {
        lock (_gate)
        {
            var stale = _held & ~(physical & SideMask);
            if (stale == HotkeyModifiers.None) return;
            foreach (var (vk, bit) in ModifierVks)
                if (stale.HasFlag(bit)) ProcessLocked(vk, false, out _);
        }
    }

    const HotkeyModifiers SideMask = HotkeyModifiers.LeftCtrl | HotkeyModifiers.RightCtrl | HotkeyModifiers.LeftAlt | HotkeyModifiers.RightAlt
        | HotkeyModifiers.LeftShift | HotkeyModifiers.RightShift | HotkeyModifiers.LeftWin | HotkeyModifiers.RightWin;

    static readonly (int Vk, HotkeyModifiers Bit)[] ModifierVks =
    [
        (VkLCtrl, HotkeyModifiers.LeftCtrl), (VkRCtrl, HotkeyModifiers.RightCtrl),
        (VkLAlt, HotkeyModifiers.LeftAlt), (VkRAlt, HotkeyModifiers.RightAlt),
        (VkLShift, HotkeyModifiers.LeftShift), (VkRShift, HotkeyModifiers.RightShift),
        (VkLWin, HotkeyModifiers.LeftWin), (VkRWin, HotkeyModifiers.RightWin),
    ];

    public static bool TryGetModifierBit(int vk, out HotkeyModifiers bit)
    {
        foreach (var (v, b) in ModifierVks) if (v == vk) { bit = b; return true; }
        bit = HotkeyModifiers.None;
        return false;
    }

    public static (int Vk, HotkeyModifiers Bit)[] AllModifierKeys => ModifierVks;

    /// <summary>Advance the hold-to-cancel gesture (call every 25 ms while <see cref="NeedsTick"/>).</summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (!_cancelPressed || !_holdToCancel || _cancelFired || _cancel == null) return;
            double progress = Math.Clamp((_nowMs() - _cancelStartMs) / CancelHoldDuration.TotalMilliseconds, 0, 1);
            _sink(new HotkeyOutput(HotkeyOutputKind.CancelProgress, Progress: progress));
            if (progress >= 1)
            {
                _cancelFired = true;
                _sink(new HotkeyOutput(HotkeyOutputKind.CancelRequested));
                _sink(new HotkeyOutput(HotkeyOutputKind.CancelProgress, Progress: null));
            }
        }
    }

    // ------------------------------------------------------------------ recorder

    sealed class Recorder(bool allowEscape, Action<HotkeyBinding?> done)
    {
        public bool AllowEscape = allowEscape;
        public Action<HotkeyBinding?> Done = done;
        public HotkeyModifiers Pending;
    }

    /// <summary>Enter recorder mode: monitoring is suspended, events are observed only. <paramref name="done"/> is invoked once.</summary>
    public void BeginRecording(bool allowEscape, Action<HotkeyBinding?> done)
    {
        lock (_gate)
        {
            _recorder = new Recorder(allowEscape, done);
            _active.Clear();
            ResetGestureLocked();
            _generation++;
        }
    }

    public void EndRecording(bool abort)
    {
        Recorder? r;
        lock (_gate) { r = _recorder; _recorder = null; _generation++; }
        if (abort) r?.Done(null);
    }

    // ------------------------------------------------------------------ event processing

    /// <summary>Process one non-injected key event. <paramref name="vk"/> is the left/right-specific VK for modifiers.</summary>
    public KeyResult Process(int vk, bool isDown)
    {
        lock (_gate)
        {
            var swallow = ProcessLocked(vk, isDown, out var suppress);
            return new KeyResult(swallow, suppress);
        }
    }

    bool ProcessLocked(int vk, bool isDown, out bool suppressStartMenu)
    {
        suppressStartMenu = false;

        if (TryGetModifierBit(vk, out var bit))
        {
            var before = _held;
            _held = isDown ? _held | bit : _held & ~bit;
            if (_recorder != null) { RecorderModifier(isDown); return false; }
            if (!isDown && (bit & WinBits) != 0 && _winSuppress)
            {
                suppressStartMenu = true;
                if ((_held & WinBits) == 0) _winSuppress = false;
            }
            if (_neutralRequired)
            {
                if (_held == HotkeyModifiers.None) _neutralRequired = false;
                return false;
            }
            if (before != _held) ModifierChanged();
            return false;
        }

        if (vk == VkCapsLock) return false;

        bool repeat = false;
        if (isDown) repeat = !_downKeys.Add(vk); else _downKeys.Remove(vk);

        if (_recorder != null)
        {
            if (isDown && !repeat) RecorderKey(vk);
            return false;
        }
        if (_neutralRequired) return false;
        return isDown ? KeyDown(vk, repeat) : KeyUp(vk);
    }

    void ModifierChanged()
    {
        foreach (var b in _bindings)
        {
            if (b.VirtualKey != null || b.Modifiers == HotkeyModifiers.None) continue;
            bool active = GroupsMatch(_held, b.Modifiers, exact: true);
            bool was = _active.Contains(b.Id);
            if (active && !was)
            {
                _active.Add(b.Id);
                if ((b.Modifiers & WinBits) != 0) _winSuppress = true;
                _sink(new HotkeyOutput(HotkeyOutputKind.Pressed, b));
            }
            else if (!active && was)
            {
                _active.Remove(b.Id);
                if (b.Mode == HotkeyMode.HoldToRecord) _sink(new HotkeyOutput(HotkeyOutputKind.Released, b));
            }
        }

        if (_cancel != null && _cancelEnabled)
        {
            bool matches = GroupsMatch(CancelEffectiveModifiers(), _cancel.Modifiers, exact: true);
            if (_cancel.VirtualKey == null)
            {
                if (matches && !_cancelPressed) ArmGesture();
                else if (!matches && _cancelPressed) ResetGestureLocked();
            }
            else if (_cancelPressed && !matches) ResetGestureLocked();
        }
    }

    bool KeyDown(int vk, bool repeat)
    {
        foreach (var b in _bindings)
        {
            if (b.VirtualKey != vk) continue;
            if (_active.Contains(b.Id)) return true;   // auto-repeat (or missed key-up): swallow, never retrigger
            if (!repeat && GroupsMatch(_held, b.Modifiers, exact: false))
            {
                _active.Add(b.Id);
                _sink(new HotkeyOutput(HotkeyOutputKind.Pressed, b));
                return true;
            }
        }

        if (_cancel is { VirtualKey: { } cvk } cancel && cvk == vk && _cancelEnabled)
        {
            bool owns = !(_holdToCancel && IsPlainEscape(cancel));
            if (_cancelPressed || _cancelOwnsUp) return _cancelOwnsUp;      // repeat of an armed press
            if (repeat) return false;                                       // repeat never arms
            if (GroupsMatch(CancelEffectiveModifiers(), cancel.Modifiers, exact: true))
            {
                ArmGesture();
                _cancelOwnsUp = owns;
                return owns;
            }
        }
        return false;
    }

    bool KeyUp(int vk)
    {
        bool swallow = false;
        foreach (var b in _bindings)
        {
            if (b.VirtualKey == vk && _active.Remove(b.Id))
            {
                _sink(new HotkeyOutput(HotkeyOutputKind.Released, b));
                swallow = true;   // allowed even if modifiers were released first
            }
        }
        if (_cancel is { VirtualKey: { } cvk } && cvk == vk && (_cancelPressed || _cancelOwnsUp))
        {
            swallow |= _cancelOwnsUp;
            _cancelOwnsUp = false;
            if (_cancelPressed) ResetGestureLocked();
        }
        return swallow;
    }

    void ArmGesture()
    {
        _cancelPressed = true;
        _cancelFired = false;
        _cancelStartMs = _nowMs();
        if (_holdToCancel)
            _sink(new HotkeyOutput(HotkeyOutputKind.CancelProgress, Progress: 0));
        else
        {
            _cancelFired = true;
            _sink(new HotkeyOutput(HotkeyOutputKind.CancelRequested));
        }
    }

    void ResetGestureLocked()
    {
        bool hadProgress = _cancelPressed && _holdToCancel && !_cancelFired;
        _cancelPressed = false;
        _cancelFired = false;
        if (hadProgress) _sink(new HotkeyOutput(HotkeyOutputKind.CancelProgress, Progress: null));
        // _cancelOwnsUp survives so the matching key-up of a swallowed key-down is still swallowed.
    }

    static bool IsPlainEscape(HotkeyBinding b) => b.VirtualKey == VkEscape && b.Modifiers == HotkeyModifiers.None;

    /// <summary>Held modifiers minus those owned by an active recording binding (unless part of the cancel shortcut).</summary>
    HotkeyModifiers CancelEffectiveModifiers()
    {
        var effective = _held;
        if (_cancel == null || _active.Count == 0) return effective;
        foreach (var b in _bindings)
        {
            if (!_active.Contains(b.Id)) continue;
            foreach (var g in Groups)
            {
                bool inBinding = (b.Modifiers & (g.Gen | g.L | g.R)) != 0;
                bool inCancel = (_cancel.Modifiers & (g.Gen | g.L | g.R)) != 0;
                if (inBinding && !inCancel) effective &= ~(g.L | g.R);
            }
        }
        return effective;
    }

    // ------------------------------------------------------------------ recorder logic

    void RecorderModifier(bool isDown)
    {
        var r = _recorder!;
        if (isDown)
        {
            if (r.Pending == HotkeyModifiers.None || (_held & r.Pending) == r.Pending) r.Pending = _held;
        }
        else if (_held == HotkeyModifiers.None && r.Pending != HotkeyModifiers.None)
        {
            var mods = ToRecordedModifiers(r.Pending);
            Finish(new HotkeyBinding(Guid.NewGuid(), null, mods, HotkeyNames.Display(mods, null), HotkeyMode.HoldToRecord));
        }
    }

    void RecorderKey(int vk)
    {
        var r = _recorder!;
        if (vk == VkEscape && !r.AllowEscape) { Finish(null); return; }
        var mods = ToRecordedModifiers(_held);
        Finish(new HotkeyBinding(Guid.NewGuid(), vk, mods, HotkeyNames.Display(mods, vk), HotkeyMode.HoldToRecord));
    }

    void Finish(HotkeyBinding? result)
    {
        var r = _recorder;
        _recorder = null;
        _generation++;
        r?.Done(result);
    }

    /// <summary>Recorded form: a group pressed on the right side only is recorded side-specific; otherwise the generic bit (either side).</summary>
    public static HotkeyModifiers ToRecordedModifiers(HotkeyModifiers held)
    {
        var result = HotkeyModifiers.None;
        foreach (var g in Groups)
        {
            bool l = held.HasFlag(g.L), r = held.HasFlag(g.R);
            if (l && r) result |= g.Gen;
            else if (r) result |= g.R;
            else if (l) result |= g.Gen;
        }
        return result;
    }

    // ------------------------------------------------------------------ matching helpers

    public static bool HasBinding(HotkeyBinding b) => b.VirtualKey != null || b.Modifiers != HotkeyModifiers.None;

    /// <summary>Compare held side-bits with a target. Generic target bits accept either side; side bits require that side.
    /// exact = no modifier group beyond the target (and no extra side of a side-specific group) may be held.</summary>
    public static bool GroupsMatch(HotkeyModifiers held, HotkeyModifiers target, bool exact)
    {
        foreach (var g in Groups)
        {
            bool heldL = held.HasFlag(g.L), heldR = held.HasFlag(g.R);
            bool tL = target.HasFlag(g.L), tR = target.HasFlag(g.R), tG = target.HasFlag(g.Gen);
            if (!tL && !tR && !tG)
            {
                if (exact && (heldL || heldR)) return false;
                continue;
            }
            if (tL || tR)
            {
                if (tL && !heldL) return false;
                if (tR && !heldR) return false;
                if (exact && ((heldL && !tL) || (heldR && !tR))) return false;
            }
            else if (!heldL && !heldR) return false;
        }
        return true;
    }
}
