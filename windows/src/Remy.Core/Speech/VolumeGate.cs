namespace Remy.Core.Speech;

/// <summary>Volume-based speech presence test (pipeline.md 3.6). Pure logic over 16 kHz mono float samples.</summary>
public static class VolumeGate
{
    public const int WindowSamples = 8000;   // 0.5 s
    public const int HopSamples = 4000;      // 0.25 s
    public const double RmsThreshold = 0.005;
    public const float PeakThreshold = 0.02f;
    public const double LoudFractionThreshold = 0.015;

    public static bool Qualifies(ReadOnlySpan<float> window)
    {
        if (window.IsEmpty) return false;
        double sumSq = 0; float peak = 0; int loud = 0;
        foreach (float s in window)
        {
            float a = Math.Abs(s);
            sumSq += (double)s * s;
            if (a > peak) peak = a;
            if (a >= PeakThreshold) loud++;
        }
        if (Math.Sqrt(sumSq / window.Length) > RmsThreshold) return true;
        return peak >= PeakThreshold && (double)loud / window.Length >= LoudFractionThreshold;
    }

    /// <summary>Preview test: only the most recent 0.5 s is examined.</summary>
    public static bool HasSignificantRecentAudio(ReadOnlySpan<float> samples) =>
        Qualifies(samples.Length > WindowSamples ? samples[^WindowSamples..] : samples);

    /// <summary>Whole-recording test: any 0.5 s window (0.25 s hop) qualifies, plus the unaligned tail.</summary>
    public static bool ContainsSignificantAudio(ReadOnlySpan<float> samples)
    {
        int n = samples.Length;
        if (n == 0) return false;
        if (n < WindowSamples) return Qualifies(samples);
        int lastEnd = 0;
        for (int start = 0; start + WindowSamples <= n; start += HopSamples)
        {
            if (Qualifies(samples.Slice(start, WindowSamples))) return true;
            lastEnd = start + WindowSamples;
        }
        return lastEnd != n && Qualifies(samples[^WindowSamples..]);
    }
}
