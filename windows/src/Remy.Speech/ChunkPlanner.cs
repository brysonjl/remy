namespace Remy.Speech;

/// <summary>Splits long audio at the quietest 100 ms window within +/-25% of each 20 s target (spike DecodeChunked).</summary>
public static class ChunkPlanner
{
    public const int SampleRate = 16000;

    /// <summary>Returns [start, end) sample ranges covering the whole input in order.</summary>
    public static IReadOnlyList<(int Start, int End)> Plan(ReadOnlySpan<float> samples, double chunkSeconds = 20)
    {
        var result = new List<(int, int)>();
        int target = (int)(chunkSeconds * SampleRate), win = SampleRate / 10, start = 0;
        while (start < samples.Length)
        {
            int end = samples.Length;
            if (samples.Length - start > target * 1.25)
            {
                double best = double.MaxValue;
                for (int c = start + (int)(target * 0.75); c + win < start + (int)(target * 1.25); c += 160)
                {
                    double e = 0;
                    for (int i = c; i < c + win; i++) e += (double)samples[i] * samples[i];
                    if (e < best) { best = e; end = c + win / 2; }
                }
            }
            result.Add((start, end));
            start = end;
        }
        return result;
    }
}
