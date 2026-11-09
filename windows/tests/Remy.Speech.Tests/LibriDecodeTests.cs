using System.Text.RegularExpressions;
using Remy.Speech;
using Xunit;

namespace Remy.Speech.Tests;

internal static class Assets
{
    public static string? Find(params string[] relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(new[] { dir.FullName, "spike", ".cache" }.Concat(relative).ToArray());
            if (Directory.Exists(candidate) || File.Exists(candidate)) return candidate;
            candidate = Path.Combine(new[] { dir.FullName, "windows", "spike", ".cache" }.Concat(relative).ToArray());
            if (Directory.Exists(candidate) || File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static string? ModelDir => Find("models", "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8");
    public static string? Fixture(string name) => Find("fixtures", "libri", name);
}

/// <summary>[Fact] that skips with a clear message when the local model or fixtures are missing.</summary>
public sealed class RequiresModelFactAttribute : FactAttribute
{
    public RequiresModelFactAttribute()
    {
        if (Assets.ModelDir is null || Assets.Fixture("libri-010s.wav") is null)
            Skip = "Local Parakeet model or libri fixtures not found under windows/spike/.cache (see spike/RESULTS.md to fetch them).";
    }
}

public class LibriDecodeTests
{
    [RequiresModelFact]
    public async Task Decodes_10s_and_60s_fixtures_under_5_percent_wer()
    {
        using var recognizer = new SherpaParakeetRecognizer();
        await recognizer.LoadAsync(Assets.ModelDir!, CancellationToken.None);
        Assert.True(recognizer.IsLoaded);

        foreach (var name in new[] { "libri-010s", "libri-060s" })
        {
            float[] samples = ReadWav(Assets.Fixture(name + ".wav")!);
            string reference = File.ReadAllText(Assets.Fixture(name + ".txt")!);
            var result = await recognizer.DecodeFinalAsync(samples, CancellationToken.None);
            double wer = Wer(reference, result.Text);
            Assert.True(wer < 0.05, $"{name}: WER {wer:P1} text: {result.Text}");
            Assert.NotEmpty(result.Words);
            Assert.All(result.Words, w => Assert.True(w.EndSeconds >= w.StartSeconds));
            Assert.True(result.Words[^1].EndSeconds <= samples.Length / 16000.0 + 0.5);
        }
    }

    [RequiresModelFact]
    public async Task Window_decode_returns_text_with_word_timings()
    {
        using var recognizer = new SherpaParakeetRecognizer();
        await recognizer.LoadAsync(Assets.ModelDir!, CancellationToken.None);
        float[] samples = ReadWav(Assets.Fixture("libri-010s.wav")!);
        var r = await recognizer.DecodeWindowAsync(samples.AsMemory(0, 16000 * 5), CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(r.Text));
        Assert.Equal(r.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length, r.Words.Count);
        Assert.True(r.Words[0].StartSeconds < 2);
    }

    [Fact]
    public async Task Decode_before_load_reports_user_facing_message()
    {
        using var recognizer = new SherpaParakeetRecognizer();
        var e = await Assert.ThrowsAsync<SpeechRecognitionException>(() =>
            recognizer.DecodeWindowAsync(new float[1600], CancellationToken.None));
        Assert.Equal(SpeechRecognitionException.ModelNotLoaded, e.Message);
    }

    [Fact]
    public async Task Load_with_missing_files_reports_user_facing_message()
    {
        using var recognizer = new SherpaParakeetRecognizer();
        var e = await Assert.ThrowsAsync<SpeechRecognitionException>(() =>
            recognizer.LoadAsync(Path.Combine(Path.GetTempPath(), "remy-no-model-" + Guid.NewGuid()), CancellationToken.None));
        Assert.Equal(SpeechRecognitionException.ModelLoadFailed, e.Message);
        Assert.False(recognizer.IsLoaded);
    }

    internal static float[] ReadWav(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        int pos = 12;
        while (pos + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int size = BitConverter.ToInt32(b, pos + 4);
            if (id == "data")
            {
                var data = new float[size / 2];
                for (int i = 0; i < data.Length; i++) data[i] = BitConverter.ToInt16(b, pos + 8 + 2 * i) / 32768f;
                return data;
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException(path);
    }

    private static string[] Normalize(string s) =>
        Regex.Replace(s.ToLowerInvariant().Replace('’', '\''), @"[^a-z0-9']+", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    internal static double Wer(string reference, string hypothesis)
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
        return (double)prev[h.Length] / Math.Max(r.Length, 1);
    }
}
