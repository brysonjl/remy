using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Remy.Core.Contracts;

namespace Remy.Platform.Audio;

/// <summary>
/// WASAPI shared-mode capture -> mono -> 16 kHz float through one persistent resampler.
/// <c>deviceId == null</c> means the Windows default capture endpoint (eConsole), never "device 0".
/// <see cref="DevicesChanged"/> is raised on a COM notification thread.
/// </summary>
public sealed class WasapiAudioCapture : IAudioCapture, IDisposable
{
    public static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan OverallStartTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RestartGap = TimeSpan.FromMilliseconds(120);

    const string TimeoutMessage = "Audio capture did not start in time. Check the selected microphone and try again.";
    const string SetupMessage = "Failed to set up audio capture.";
    const string SelectMessage = "Failed to select the specified microphone.";

    readonly MMDeviceEnumerator _enumerator = new();
    readonly NotificationClient _notifications;
    readonly object _gate = new();          // serialises DataAvailable, tail flush and teardown
    readonly SemaphoreSlim _startStop = new(1, 1);

    WasapiCapture? _capture;
    StreamingResampler? _resampler;
    Action<ReadOnlyMemory<float>>? _onSamples;
    WaveFormat? _format;
    TaskCompletionSource? _firstBuffer;
    TaskCompletionSource? _stopped;
    long _lastStopTicks;
    float _levelRaw, _levelSmoothed;
    readonly float[] _levelWindow = new float[800];
    int _levelPos;

    public WasapiAudioCapture()
    {
        _notifications = new NotificationClient(() => DevicesChanged?.Invoke(this, EventArgs.Empty));
        try { _enumerator.RegisterEndpointNotificationCallback(_notifications); } catch { /* best effort */ }
    }

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<AudioInputDevice> GetInputDevices()
    {
        var list = new List<AudioInputDevice>();
        try
        {
            string? defaultId = null;
            try { using var d = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); defaultId = d.ID; } catch { }
            foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (d)
                {
                    string name;
                    try { name = d.FriendlyName; } catch { continue; }
                    if (name.Contains("Stereo Mix", StringComparison.OrdinalIgnoreCase)) continue;   // loopback-style
                    list.Add(new AudioInputDevice(d.ID, name, d.ID == defaultId));
                }
            }
        }
        catch { /* no devices */ }
        return list;
    }

    public async Task StartAsync(string? deviceId, Action<ReadOnlyMemory<float>> onSamples, CancellationToken ct)
    {
        await _startStop.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_capture != null) await StopCoreAsync().ConfigureAwait(false);

            // 120 ms minimum between the last stop and the next start.
            var since = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastStopTicks));
            if (since < RestartGap) await Task.Delay(RestartGap - since, ct).ConfigureAwait(false);

            _firstBuffer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var build = Task.Run(() => Build(deviceId, onSamples), CancellationToken.None);
            var finished = await Task.WhenAny(build, Task.Delay(OverallStartTimeout, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (finished != build)
            {
                // A late-arriving capture must not leave an orphaned mic session.
                _ = build.ContinueWith(t => { if (t.IsCompletedSuccessfully) TearDownOrphan(t.Result); }, TaskScheduler.Default);
                throw new AudioCaptureException(TimeoutMessage);
            }
            var capture = await build.ConfigureAwait(false);   // throws AudioCaptureException on setup failure

            var ready = await Task.WhenAny(_firstBuffer.Task, Task.Delay(ReadinessTimeout, ct)).ConfigureAwait(false);
            if (ct.IsCancellationRequested || ready != _firstBuffer.Task)
            {
                await StopCoreAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                throw new AudioCaptureException(TimeoutMessage);
            }
        }
        finally { _startStop.Release(); }
    }

    WasapiCapture Build(string? deviceId, Action<ReadOnlyMemory<float>> onSamples)
    {
        MMDevice? device = null;
        WasapiCapture? capture = null;
        try
        {
            try
            {
                device = deviceId == null
                    ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                    : _enumerator.GetDevice(deviceId);
            }
            catch (Exception e) { throw new AudioCaptureException(deviceId == null ? SetupMessage : SelectMessage, e); }
            if (device.State != DeviceState.Active) throw new AudioCaptureException(SelectMessage);

            try { capture = new WasapiCapture(device, true, 50); }
            catch (Exception e) { throw new AudioCaptureException(SetupMessage, e); }

            var format = capture.WaveFormat;
            var resampler = new StreamingResampler(format.SampleRate);
            lock (_gate)
            {
                _format = format;
                _resampler = resampler;
                _onSamples = onSamples;
                _levelPos = 0;
                Array.Clear(_levelWindow);
                _levelRaw = _levelSmoothed = 0;
            }
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopped = _stopped;
            capture.DataAvailable += OnData;
            capture.RecordingStopped += (_, _) => stopped.TrySetResult();
            lock (_gate) { _capture = capture; }
            try { capture.StartRecording(); }
            catch (Exception e) { lock (_gate) { _capture = null; } throw new AudioCaptureException(SetupMessage, e); }
            return capture;
        }
        catch
        {
            try { capture?.Dispose(); } catch { }
            device?.Dispose();
            throw;
        }
    }

    void TearDownOrphan(WasapiCapture capture)
    {
        // Build() assigned _capture; only clean it up if nobody else took over since.
        lock (_gate) { if (!ReferenceEquals(_capture, capture)) return; }
        _ = StopAsync();
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            if (_capture == null || _format == null || _resampler == null || _onSamples == null) return;
            _firstBuffer?.TrySetResult();
            if (e.BytesRecorded <= 0) return;
            var mono = ToMono(e.Buffer.AsSpan(0, e.BytesRecorded), _format);
            var output = _resampler.Process(mono);
            if (output.Length > 0)
            {
                UpdateLevel(output);
                _onSamples(output);
            }
        }
    }

    public async Task StopAsync()
    {
        await _startStop.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _startStop.Release(); }
    }

    async Task StopCoreAsync()
    {
        WasapiCapture? capture;
        TaskCompletionSource? stopped;
        lock (_gate) { capture = _capture; stopped = _stopped; }
        if (capture == null) return;
        try { capture.StopRecording(); } catch { }
        if (stopped != null) await Task.WhenAny(stopped.Task, Task.Delay(1000)).ConfigureAwait(false);
        lock (_gate)
        {
            // In-flight DataAvailable calls hold this lock, so the tail is always ordered after the last buffer.
            try
            {
                var tail = _resampler?.Flush();
                if (tail is { Length: > 0 }) _onSamples?.Invoke(tail);
            }
            catch { }
            _capture = null;
            _resampler = null;
            _onSamples = null;
            _levelRaw = 0;
        }
        try { capture.Dispose(); } catch { }
        Interlocked.Exchange(ref _lastStopTicks, Environment.TickCount64);
    }

    /// <summary>Recent input level 0..1: Mac formula (RMS over the last 800 samples, x6.6, ^0.85, attack 0.08 / release 0.65), smoothed per call (~30 Hz polling).</summary>
    public float CurrentLevel
    {
        get
        {
            lock (_gate)
            {
                float target = _levelRaw;
                float s = target > _levelSmoothed ? 0.08f : 0.65f;
                _levelSmoothed = _levelSmoothed * s + target * (1 - s);
                return Math.Clamp(_levelSmoothed, 0f, 1f);
            }
        }
    }

    void UpdateLevel(float[] samples)
    {
        foreach (var f in samples) { _levelWindow[_levelPos] = f; _levelPos = (_levelPos + 1) % _levelWindow.Length; }
        double sum = 0;
        foreach (var f in _levelWindow) sum += (double)f * f;
        double rms = Math.Sqrt(sum / _levelWindow.Length);
        double scaled = Math.Min(1, rms * 6.6);
        _levelRaw = (float)Math.Pow(scaled, 0.85);
    }

    /// <summary>Decode interleaved PCM/float bytes to mono float by averaging channels.</summary>
    internal static float[] ToMono(ReadOnlySpan<byte> bytes, WaveFormat format)
    {
        int channels = Math.Max(1, format.Channels);
        int bytesPerSample = format.BitsPerSample / 8;
        int frames = bytes.Length / (bytesPerSample * channels);
        var mono = new float[frames];
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        for (int i = 0; i < frames; i++)
        {
            float acc = 0;
            for (int c = 0; c < channels; c++)
            {
                int o = (i * channels + c) * bytesPerSample;
                acc += bytesPerSample switch
                {
                    4 when isFloat => BitConverter.ToSingle(bytes.Slice(o, 4)),
                    4 => BitConverter.ToInt32(bytes.Slice(o, 4)) / 2147483648f,
                    3 => ((bytes[o] << 8) | (bytes[o + 1] << 16) | (bytes[o + 2] << 24)) / 2147483648f,
                    2 => BitConverter.ToInt16(bytes.Slice(o, 2)) / 32768f,
                    1 => (bytes[o] - 128) / 128f,
                    _ => 0f,
                };
            }
            mono[i] = acc / channels;
        }
        return mono;
    }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(_notifications); } catch { }
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        _enumerator.Dispose();
    }

    sealed class NotificationClient(Action changed) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => changed();
        public void OnDeviceAdded(string pwstrDeviceId) => changed();
        public void OnDeviceRemoved(string deviceId) => changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { if (flow == DataFlow.Capture) changed(); }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
