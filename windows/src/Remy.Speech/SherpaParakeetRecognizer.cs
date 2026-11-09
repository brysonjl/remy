using System.Diagnostics;
using Remy.Core.Contracts;
using SherpaOnnx;

namespace Remy.Speech;

/// <summary>Parakeet TDT 0.6B v2 int8 on sherpa-onnx, CPU, greedy search. One decode at a time.</summary>
public sealed class SherpaParakeetRecognizer : ISpeechRecognizer
{
    private const int SampleRate = 16000;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineRecognizer? _recognizer;
    private bool _disposed;

    public bool IsLoaded => _recognizer is not null;

    /// <summary>min(8, physical cores); falls back to logical processors / 2.</summary>
    public static int DefaultThreads()
    {
        int physical = PhysicalCoreCount() ?? Math.Max(1, Environment.ProcessorCount / 2);
        return Math.Max(1, Math.Min(8, physical));
    }

    private static int? PhysicalCoreCount()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return null;
            int total = 0;
            using var searcher = new System.Management.ManagementObjectSearcher("select NumberOfCores from Win32_Processor");
            foreach (var item in searcher.Get())
                total += Convert.ToInt32(item["NumberOfCores"]);
            return total > 0 ? total : null;
        }
        catch { return null; }
    }

    public async Task LoadAsync(string modelDirectory, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_recognizer is not null) return;
            _recognizer = await Task.Run(() => CreateAndWarm(modelDirectory), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is not SpeechRecognitionException)
        {
            throw new SpeechRecognitionException(SpeechRecognitionException.ModelLoadFailed, e);
        }
        finally { _gate.Release(); }
    }

    private static OfflineRecognizer CreateAndWarm(string dir)
    {
        string Req(string name)
        {
            string p = Path.Combine(dir, name);
            if (!File.Exists(p)) throw new FileNotFoundException("Missing model file", p);
            return p;
        }
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = Req("encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Req("decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Req("joiner.int8.onnx");
        config.ModelConfig.Tokens = Req("tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = DefaultThreads();
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        var r = new OfflineRecognizer(config);
        try
        {
            var rng = new Random(1);
            var noise = new float[SampleRate];
            for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rng.NextDouble() - 0.5) * 0.001f;
            DecodeCore(r, noise, 0);
            return r;
        }
        catch { r.Dispose(); throw; }
    }

    public void Unload()
    {
        _gate.Wait();
        try { _recognizer?.Dispose(); _recognizer = null; }
        finally { _gate.Release(); }
    }

    public Task<RecognitionResult> DecodeWindowAsync(ReadOnlyMemory<float> samples16k, CancellationToken ct) =>
        RunAsync(samples16k, ct, final: false);

    public Task<RecognitionResult> DecodeFinalAsync(ReadOnlyMemory<float> samples16k, CancellationToken ct) =>
        RunAsync(samples16k, ct, final: true);

    private async Task<RecognitionResult> RunAsync(ReadOnlyMemory<float> samples, CancellationToken ct, bool final)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var recognizer = _recognizer ?? throw new SpeechRecognitionException(SpeechRecognitionException.ModelNotLoaded);
            return await Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var (text, words) = final ? DecodeChunked(recognizer, samples.Span, ct) : DecodeCore(recognizer, samples.Span, 0);
                    return new RecognitionResult(text, words, sw.Elapsed);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) when (e is not SpeechRecognitionException)
                {
                    throw new SpeechRecognitionException(SpeechRecognitionException.DidNotFinish, e);
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static (string, List<RecognizedWord>) DecodeChunked(OfflineRecognizer r, ReadOnlySpan<float> samples, CancellationToken ct)
    {
        var plan = ChunkPlanner.Plan(samples);
        var texts = new List<string>();
        var words = new List<RecognizedWord>();
        foreach (var (start, end) in plan)
        {
            ct.ThrowIfCancellationRequested();
            var (t, w) = DecodeCore(r, samples[start..end], start / (double)SampleRate);
            if (t.Length > 0) texts.Add(t);
            words.AddRange(w);
        }
        return (string.Join(' ', texts), words);
    }

    private static (string, List<RecognizedWord>) DecodeCore(OfflineRecognizer r, ReadOnlySpan<float> samples, double offsetSeconds)
    {
        using var stream = r.CreateStream();
        stream.AcceptWaveform(SampleRate, samples.ToArray());
        r.Decode(stream);
        var result = stream.Result;
        string text = (result.Text ?? "").Trim();
        var words = WordBuilder.Build(result.Tokens, result.Timestamps, offsetSeconds, samples.Length / (double)SampleRate);
        return (text, words);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Wait();
        try { _recognizer?.Dispose(); _recognizer = null; }
        finally { _gate.Release(); }
    }
}
