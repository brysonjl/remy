using Remy.Core.Hotkeys;
using Remy.Platform.Audio;
using Remy.Platform.Delivery;
using Remy.Platform.Hotkeys;
using Remy.Platform.Services;
using Xunit;

namespace Remy.Platform.Tests;

/// <summary>Live-OS smoke tests that need no input devices or user interaction.</summary>
public class SmokeTests
{
    [Fact]
    public async Task Listener_StartsStopsAndAbortsRecorder()
    {
        using var l = new LowLevelKeyboardHotkeyListener();
        l.Configure([new HotkeyBinding(Guid.NewGuid(), null, HotkeyModifiers.Ctrl | HotkeyModifiers.Win, "Ctrl+Win", HotkeyMode.HoldToRecord)], null, true);
        l.Start();
        await Task.Delay(200);
        using var cts = new CancellationTokenSource(200);
        var recorded = await l.RecordShortcutAsync(false, cts.Token);
        Assert.Null(recorded);
        l.Stop();
        l.Start();
        l.Stop();
    }

    [Fact]
    public void Devices_CanBeEnumerated_WithoutThrowing()
    {
        using var cap = new WasapiAudioCapture();
        var devices = cap.GetInputDevices();
        Assert.True(devices.Count(d => d.IsDefault) <= 1);
    }

    [Fact]
    public async Task Clipboard_RoundTrip_AndSequenceChanges()
    {
        using var c = new ClipboardDelivery();
        var before = c.ClipboardSequenceNumber;
        var seq = await c.CopyAsync("remy clipboard test é中\nline2");
        Assert.NotNull(seq);
        Assert.NotEqual(before, c.ClipboardSequenceNumber);
        Assert.Equal("remy clipboard test é中\nline2", await c.ReadTextAsync());
    }

    [Fact]
    public void PowerEvents_CreateAndDispose()
    {
        using var p = new PowerEvents();
    }
}
