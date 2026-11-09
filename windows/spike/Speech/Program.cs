// Throwaway spike: how fast and how accurate is Parakeet v2 (sherpa-onnx) on this Windows machine?
//
//   Speech tts-fixtures <outDir> <passages.json>
//       Writes 10s / 60s / 300s Windows TTS clips (16 kHz mono WAV) plus reference .txt files.
//   Speech bench <modelDir> <cpu|cuda> <threads> <runs> <fixtureDir> [label]
//       Loads the model once, warms up, then decodes every *.wav in fixtureDir <runs> times.
//       Prints one JSON line per decode and a summary line. Run each config in its own process
//       so peak memory belongs to that config. chunkSeconds > 0 splits audio at the quietest
//       100 ms near every chunkSeconds and decodes the pieces in order (what a real app would do
//       for long dictation); 0 decodes the whole file in one pass.

using System.Diagnostics;
using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SherpaOnnx;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = Encoding.UTF8;

return args.FirstOrDefault() switch
{
    "tts-fixtures" => TtsFixtures(args[1], args[2]),
    "bench" => Bench(args[1], args[2], int.Parse(args[3]), int.Parse(args[4]), args[5], args.Length > 6 ? args[6] : "",
                       args.Length > 7 ? double.Parse(args[7]) : 0),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: Speech tts-fixtures <outDir> <passages.json> | bench <modelDir> <cpu|cuda> <threads> <runs> <fixtureDir> [label] [chunkSeconds]");
    return 2;
}

// ---------------------------------------------------------------- fixtures

static int TtsFixtures(string outDir, string passagesPath)
{
    Directory.CreateDirectory(outDir);
    using var doc = JsonDocument.Parse(File.ReadAllText(passagesPath));
    string passage = doc.RootElement.GetProperty("passage").GetString()!;
    var markers = doc.RootElement.GetProperty("markers").EnumerateArray().Select(m => m.GetString()!).ToArray();
    string tail = doc.RootElement.GetProperty("tail").GetString()!;
    var sentences = Regex.Split(passage, @"(?<=[.!?])\s+").Where(s => s.Length > 0).ToArray();
    var words = passage.Split(' ');

    using var synth = new SpeechSynthesizer();
    var voice = synth.GetInstalledVoices().Select(v => v.VoiceInfo)
        .FirstOrDefault(v => v.Culture.Name == "en-US") ?? synth.Voice;
    synth.SelectVoice(voice.Name);
    var format = new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono);

    foreach (var target in new[] { 10, 60, 300 })
    {
        // Grow the script one sentence at a time until the rendered audio reaches the target length.
        var script = new StringBuilder();
        int s = 0, section = 0;
        string wav = Path.Combine(outDir, $"tts-{target:D3}s.wav");
        double seconds = 0;
        while (true)
        {
            // The 10 s clip grows word by word; longer clips grow by sentence with checkpoint phrases.
            if (target <= 10) script.Append(words[s++]).Append(' ');
            else
            {
                if (s % sentences.Length == 0)
                    script.Append($"Section {++section}, checkpoint {markers[(section - 1) % markers.Length]}. ");
                script.Append(sentences[s++ % sentences.Length]).Append(' ');
            }
            string candidate = target > 10 ? script + tail : script.ToString().TrimEnd(' ', ',') + ".";
            synth.SetOutputToWaveFile(wav, format);
            synth.Speak(candidate);
            synth.SetOutputToNull();
            seconds = ReadWav(wav).Length / 16000.0;
            if (seconds >= target)
            {
                File.WriteAllText(Path.ChangeExtension(wav, ".txt"), candidate.Trim());
                break;
            }
        }
        Console.WriteLine($"{Path.GetFileName(wav)}  {seconds:F1}s  voice={voice.Name}");
    }
    return 0;
}

// ---------------------------------------------------------------- benchmark

static int Bench(string modelDir, string provider, int threads, int runs, string fixtureDir, string label, double chunkSeconds)
{
    string Find(string prefix) => Directory.GetFiles(modelDir, prefix + "*.onnx").OrderBy(f => f.Length).First();

    var config = new OfflineRecognizerConfig();
    config.FeatConfig.SampleRate = 16000;
    config.FeatConfig.FeatureDim = 80; // NeMo models override this from model metadata.
    config.ModelConfig.Transducer.Encoder = Find("encoder");
    config.ModelConfig.Transducer.Decoder = Find("decoder");
    config.ModelConfig.Transducer.Joiner = Find("joiner");
    config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
    config.ModelConfig.ModelType = "nemo_transducer";
    config.ModelConfig.NumThreads = threads;
    config.ModelConfig.Provider = provider;
    config.ModelConfig.Debug = 0;
    config.DecodingMethod = "greedy_search";

    var load = Stopwatch.StartNew();
    using var recognizer = new OfflineRecognizer(config);
    load.Stop();

    // Warm-up: one second of low noise, then one second of the first fixture. GPU kernels and
    // ORT arena allocations happen here, the way a warm app would have done them before dictation.
    var warm = Stopwatch.StartNew();
    var rng = new Random(1);
    Decode(recognizer, Enumerable.Range(0, 16000).Select(_ => (float)(rng.NextDouble() - 0.5) * 0.001f).ToArray());
    warm.Stop();

    var fixtures = Directory.GetFiles(fixtureDir, "*.wav").OrderBy(f => f).ToArray();
    var common = new { label, provider, threads, model = Path.GetFileName(Path.GetFullPath(modelDir).TrimEnd('\\')), encoder = Path.GetFileName(config.ModelConfig.Transducer.Encoder) };
    Emit(new { kind = "load", common.label, common.provider, common.threads, common.model, common.encoder, load_ms = load.Elapsed.TotalMilliseconds, warmup_ms = warm.Elapsed.TotalMilliseconds });

    foreach (var wav in fixtures)
    {
        float[] samples = ReadWav(wav);
        double audioSec = samples.Length / 16000.0;
        string reference = File.ReadAllText(Path.ChangeExtension(wav, ".txt"));
        for (int r = 0; r < runs; r++)
        {
            var sw = Stopwatch.StartNew();
            string text;
            try { text = chunkSeconds > 0 ? DecodeChunked(recognizer, samples, chunkSeconds) : Decode(recognizer, samples); }
            catch (Exception e) { Emit(new { kind = "error", common.label, fixture = Path.GetFileName(wav), error = e.Message }); break; }
            sw.Stop();
            var (errors, refWords) = WordErrors(reference, text);
            Emit(new
            {
                kind = "decode", common.label, chunk_s = chunkSeconds, common.provider, common.threads, common.model,
                fixture = Path.GetFileName(wav), run = r, audio_s = audioSec,
                decode_ms = sw.Elapsed.TotalMilliseconds, rtf = sw.Elapsed.TotalSeconds / audioSec,
                wer = (double)errors / refWords, word_errors = errors, ref_words = refWords,
                hyp_words = Normalize(text).Length,
                tail = text.Length > 80 ? text[^80..] : text,
            });
            if (r == 0) File.WriteAllText(Path.Combine(fixtureDir, $"{Path.GetFileNameWithoutExtension(wav)}.{label}.hyp.txt"), text);
        }
    }

    using var self = Process.GetCurrentProcess();
    Emit(new { kind = "summary", common.label, peak_working_set_mb = self.PeakWorkingSet64 / 1048576.0, peak_paged_mb = self.PeakPagedMemorySize64 / 1048576.0 });
    return 0;
}

static string Decode(OfflineRecognizer recognizer, float[] samples)
{
    using var stream = recognizer.CreateStream();
    stream.AcceptWaveform(16000, samples);
    recognizer.Decode(stream);
    return stream.Result.Text;
}

static string DecodeChunked(OfflineRecognizer recognizer, float[] samples, double chunkSeconds)
{
    // Cut near every chunkSeconds at the quietest 100 ms window within +/-25% of the target.
    int target = (int)(chunkSeconds * 16000), win = 1600, start = 0;
    var parts = new List<string>();
    while (start < samples.Length)
    {
        int end = samples.Length;
        if (samples.Length - start > target * 1.25)
        {
            double best = double.MaxValue;
            for (int c = start + (int)(target * 0.75); c + win < start + (int)(target * 1.25); c += 160)
            {
                double e = 0;
                for (int i = c; i < c + win; i++) e += samples[i] * samples[i];
                if (e < best) { best = e; end = c + win / 2; }
            }
        }
        parts.Add(Decode(recognizer, samples[start..end]).Trim());
        start = end;
    }
    return string.Join(' ', parts.Where(p => p.Length > 0));
}

static void Emit(object o) => Console.WriteLine(JsonSerializer.Serialize(o));

// ---------------------------------------------------------------- helpers

static float[] ReadWav(string path)
{
    // Minimal RIFF reader: 16-bit PCM, mono, 16 kHz only (the fixtures are written that way).
    using var br = new BinaryReader(File.OpenRead(path));
    if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException(path);
    br.ReadInt32();
    if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException(path);
    short channels = 0, bits = 0; int rate = 0;
    while (br.BaseStream.Position < br.BaseStream.Length)
    {
        string id = new(br.ReadChars(4));
        int size = br.ReadInt32();
        if (id == "fmt ")
        {
            br.ReadInt16(); channels = br.ReadInt16(); rate = br.ReadInt32(); br.ReadInt32(); br.ReadInt16(); bits = br.ReadInt16();
            br.BaseStream.Seek(size - 16, SeekOrigin.Current);
        }
        else if (id == "data")
        {
            if (channels != 1 || rate != 16000 || bits != 16) throw new InvalidDataException($"{path}: need 16k mono s16, got {rate}/{channels}/{bits}");
            int n = size / 2;
            var data = new float[n];
            for (int i = 0; i < n; i++) data[i] = br.ReadInt16() / 32768f;
            return data;
        }
        else br.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
    }
    throw new InvalidDataException($"{path}: no data chunk");
}

// Simple WER: lowercase, keep letters/digits/apostrophes, hyphens split words.
// Digits vs spelled-out numbers count as errors (same rule as the macOS long-dictation check).
static string[] Normalize(string s) =>
    Regex.Replace(s.ToLowerInvariant().Replace('’', '\''), @"[^a-z0-9']+", " ")
        .Split(' ', StringSplitOptions.RemoveEmptyEntries);

static (int errors, int refWords) WordErrors(string reference, string hypothesis)
{
    var r = Normalize(reference); var h = Normalize(hypothesis);
    var prev = new int[h.Length + 1]; var cur = new int[h.Length + 1];
    for (int j = 0; j <= h.Length; j++) prev[j] = j;
    for (int i = 1; i <= r.Length; i++)
    {
        cur[0] = i;
        for (int j = 1; j <= h.Length; j++)
            cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1));
        (prev, cur) = (cur, prev);
    }
    return (prev[h.Length], Math.Max(r.Length, 1));
}
