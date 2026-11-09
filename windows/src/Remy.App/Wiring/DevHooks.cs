using System.IO;
using Remy.Core.Contracts;
using Remy.Core.Session;

namespace Remy.App.Wiring;

/// <summary>Developer-only hooks. Everything here is inert unless its env var or argument is present.
///   REMY_FAKE_AUDIO=&lt;wav&gt;            microphone replaced by a real-time stream of the WAV (16-bit PCM or float).
///   --dev-dictate-seconds N          start a toggle dictation after the delay and stop it N seconds later.
///   --dev-dictate-delay S            delay before starting (default 3).</summary>
public static class DevHooks
{
    public const string FakeAudioEnv = "REMY_FAKE_AUDIO";

    public static IAudioCapture? CreateFakeAudio()
    {
        var path = Environment.GetEnvironmentVariable(FakeAudioEnv);
        return string.IsNullOrWhiteSpace(path) ? null : new WavStreamAudioCapture(path);
    }

    public static void StartDictationTrigger(DictationCoordinator coordinator, string[] args)
    {
        int i = Array.IndexOf(args, "--dev-dictate-seconds");
        if (i < 0 || i + 1 >= args.Length || !double.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var seconds)) return;
        double delay = 3;
        int d = Array.IndexOf(args, "--dev-dictate-delay");
        if (d >= 0 && d + 1 < args.Length) double.TryParse(args[d + 1], System.Globalization.CultureInfo.InvariantCulture, out delay);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(delay));
            await coordinator.StartFromMenuAsync();
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            await coordinator.StopAsync();
        });
    }
}

/// <summary>Streams a WAV file as if it were a microphone, in real time (100 ms blocks).</summary>
sealed class WavStreamAudioCapture : IAudioCapture
{
    readonly float[] _samples;
    CancellationTokenSource? _cts;
    Task? _pump;
    volatile float _level;

    public WavStreamAudioCapture(string path) => _samples = Load(path);
    public event EventHandler? DevicesChanged { add { } remove { } }
    public float CurrentLevel => _level;
    public IReadOnlyList<AudioInputDevice> GetInputDevices() => [new AudioInputDevice("fake-wav", "Fake WAV input", true)];

    public Task StartAsync(string? deviceId, Action<ReadOnlyMemory<float>> onSamples, CancellationToken ct)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        const int block = 1600;
        _pump = Task.Run(async () =>
        {
            var start = Environment.TickCount64;
            for (int off = 0, n = 0; off < _samples.Length && !token.IsCancellationRequested; off += block, n++)
            {
                var b = _samples.AsMemory(off, Math.Min(block, _samples.Length - off));
                double sum = 0; foreach (var f in b.Span) sum += f * f;
                _level = (float)Math.Min(1, Math.Sqrt(sum / b.Length) * 6.6);
                onSamples(b.ToArray());
                var due = start + (n + 1) * 100L - Environment.TickCount64;
                if (due > 0) { try { await Task.Delay((int)due, token); } catch (OperationCanceledException) { break; } }
            }
            _level = 0;
        });
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_pump != null) { try { await _pump; } catch { } }
        _pump = null;
    }

    static float[] Load(string path)
    {
        var b = File.ReadAllBytes(path);
        int pos = 12, rate = 16000, ch = 1, bits = 16, fmt = 1;
        while (pos + 8 <= b.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int size = BitConverter.ToInt32(b, pos + 4);
            if (id == "fmt ") { fmt = BitConverter.ToUInt16(b, pos + 8); ch = BitConverter.ToUInt16(b, pos + 10); rate = BitConverter.ToInt32(b, pos + 12); bits = BitConverter.ToUInt16(b, pos + 22); }
            else if (id == "data")
            {
                int len = Math.Min(size, b.Length - pos - 8), bps = bits / 8, frames = len / (bps * ch);
                var mono = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    double acc = 0;
                    for (int c = 0; c < ch; c++)
                    {
                        int o = pos + 8 + (i * ch + c) * bps;
                        acc += fmt == 3 && bits == 32 ? BitConverter.ToSingle(b, o) : bits == 16 ? BitConverter.ToInt16(b, o) / 32768.0 : 0;
                    }
                    mono[i] = (float)(acc / ch);
                }
                if (rate == 16000) return mono;
                var outLen = (int)((long)mono.Length * 16000 / rate);
                var r = new float[outLen];
                for (int i = 0; i < outLen; i++) { double p = (double)i * rate / 16000; int i0 = (int)p; int i1 = Math.Min(i0 + 1, mono.Length - 1); r[i] = (float)(mono[i0] + (mono[i1] - mono[i0]) * (p - i0)); }
                return r;
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException("No data chunk in " + path);
    }
}
