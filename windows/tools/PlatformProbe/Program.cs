using Remy.Core.Contracts;
using Remy.Platform.Audio;
using Remy.Platform.Delivery;
using Remy.Platform.Destination;

// Usage: PlatformProbe [mics|record|dest|paste|all]   (default: all)
var what = args.Length > 0 ? args[0] : "all";
bool Want(string s) => what == "all" || what == s;

if (Want("mics"))
{
    using var cap = new WasapiAudioCapture();
    Console.WriteLine("Input devices:");
    using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    foreach (var d in cap.GetInputDevices())
    {
        string vol = "";
        try { var v = en.GetDevice(d.Id).AudioEndpointVolume; vol = $" volume={v.MasterVolumeLevelScalar:F2} muted={v.Mute}"; } catch { vol = " (no volume control)"; }
        Console.WriteLine($"  {(d.IsDefault ? "*" : " ")} {d.Name}{vol}");
    }
}

if (Want("record"))
{
    using var cap = new WasapiAudioCapture();
    long samples = 0; float peak = 0;
    Console.WriteLine("Recording 3 s from the default microphone...");
    try
    {
        var micIndex = Environment.GetEnvironmentVariable("PROBE_MIC_INDEX");
        string? micId = micIndex != null ? cap.GetInputDevices()[int.Parse(micIndex)].Id : null;   // optional: pick a device by list index
        await cap.StartAsync(micId, s => { samples += s.Length; foreach (var f in s.Span) peak = Math.Max(peak, Math.Abs(f)); }, CancellationToken.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 6; i++) { await Task.Delay(500); Console.WriteLine($"  t={sw.ElapsedMilliseconds}ms samples={samples} level={cap.CurrentLevel:F2}"); }
        await cap.StopAsync();
        Console.WriteLine($"Captured {samples} samples ({samples / 16000.0:F2} s at 16 kHz), peak={peak:F3}");
    }
    catch (AudioCaptureException e) { Console.WriteLine("Capture failed: " + e.Message); }
}

var dest = new DestinationService();
if (Want("dest"))
{
    Console.WriteLine("Focus the window to inspect; capturing in 3 s...");
    await Task.Delay(3000);
    var t = dest.CaptureForeground();
    Console.WriteLine(t == null ? "No foreground window" : t.ToString());
    if (t != null)
    {
        var ctx = await dest.CaptureContextAsync(t, new ContextCaptureLimits(), CancellationToken.None);
        Console.WriteLine(ctx == null ? "Context: null" : ctx.ToString());
    }
}

if (Want("paste"))
{
    Console.WriteLine("Focus a text field (e.g. a fresh Notepad); pasting in 5 s...");
    await Task.Delay(5000);
    var target = dest.CaptureForeground();
    using var clip = new ClipboardDelivery();
    var seq = await clip.CopyAsync("Remy platform probe: hello from the clipboard.");
    Console.WriteLine($"Copied, sequence={seq}, read-back='{await clip.ReadTextAsync()}'");
    var result = await clip.SendPasteAsync(CancellationToken.None);
    Console.WriteLine($"Paste result: {result} (target: {target?.ProcessName})");
}

if (what == "raw")
{
    using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var dev = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Console);
    using var wc = new NAudio.CoreAudioApi.WasapiCapture(dev, true, 50);
    Console.WriteLine(wc.WaveFormat + " " + wc.WaveFormat.GetType().Name + " bits=" + wc.WaveFormat.BitsPerSample);
    int nonzero = 0, total = 0;
    wc.DataAvailable += (_, e) => { total += e.BytesRecorded; for (int i = 0; i < e.BytesRecorded; i++) if (e.Buffer[i] != 0) nonzero++; };
    wc.StartRecording(); await Task.Delay(2000); wc.StopRecording();
    Console.WriteLine($"raw bytes={total} nonzero={nonzero}");
}
