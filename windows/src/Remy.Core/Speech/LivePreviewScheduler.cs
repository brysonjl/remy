namespace Remy.Core.Speech;

/// <summary>
/// Decides when to run a live preview decode and which audio to decode (pipeline.md 4.2). Pure logic, no threads:
/// the coordinator calls <see cref="ShouldPreview"/> on each audio wakeup, runs the decode only when it returns
/// true and no earlier decode is in flight (skip, never queue), then calls <see cref="MarkPreviewed"/>.
/// </summary>
public sealed class LivePreviewScheduler
{
    public const int SampleRate = 16000;
    public const int FirstPreviewSamples = 8_000;        // 0.5 s while no preview text exists
    public const int PreviewDeltaSamples = 16_000;       // 1 s
    public const int SustainedStartSamples = 128_000;    // 8 s total audio
    public const int SustainedDeltaSamples = 32_000;     // 2 s
    public const int WindowSamples = 240_000;            // rolling last 15 s

    private long _lastPreviewTotal;

    /// <summary>True when enough new audio has arrived for the next preview. Does not apply the volume gate.</summary>
    public bool IsDue(long totalSamples, bool hasVisibleText)
    {
        long interval = !hasVisibleText ? FirstPreviewSamples
            : totalSamples >= SustainedStartSamples ? SustainedDeltaSamples
            : PreviewDeltaSamples;
        return totalSamples - _lastPreviewTotal >= interval;
    }

    /// <summary>
    /// Full decision: due by cadence AND the most recent 0.5 s passes the volume gate (or VAD saw recent speech).
    /// A failed gate does not advance the cadence.
    /// </summary>
    public bool ShouldPreview(long totalSamples, bool hasVisibleText, ReadOnlySpan<float> recentSamples, bool vadRecentSpeech = false)
    {
        if (!IsDue(totalSamples, hasVisibleText)) return false;
        return VolumeGate.HasSignificantRecentAudio(recentSamples) || vadRecentSpeech;
    }

    /// <summary>Record that a preview decode ran at this total (also call for empty results).</summary>
    public void MarkPreviewed(long totalSamples) => _lastPreviewTotal = totalSamples;

    /// <summary>Sample range [start, total) of the rolling window to decode.</summary>
    public static (long Start, int Length) Window(long totalSamples)
    {
        int len = (int)Math.Min(totalSamples, WindowSamples);
        return (totalSamples - len, len);
    }

    public static double WindowStartSeconds(long totalSamples) => Window(totalSamples).Start / (double)SampleRate;

    public void Reset() => _lastPreviewTotal = 0;
}
