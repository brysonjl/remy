using Remy.Core.Contracts;
using Remy.Core.Speech;
using Remy.Speech;
using Xunit;

namespace Remy.Speech.Tests;

public class PreviewLogicTests
{
    private static float[] Tone(int n, float amp) => Enumerable.Range(0, n).Select(i => amp * MathF.Sin(i * 0.1f)).ToArray();

    [Fact]
    public void Cadence_first_preview_after_half_second_then_1s_then_2s()
    {
        var s = new LivePreviewScheduler();
        Assert.False(s.IsDue(7_999, false));
        Assert.True(s.IsDue(8_000, false));
        s.MarkPreviewed(8_000);
        Assert.False(s.IsDue(23_999, true));
        Assert.True(s.IsDue(24_000, true));
        s.MarkPreviewed(100_000);
        Assert.False(s.IsDue(115_999, true));   // total < 8 s: 1 s interval
        Assert.True(s.IsDue(116_000, true));
        Assert.False(s.IsDue(129_000, true));   // total >= 8 s: needs 2 s since 100 000
        Assert.True(s.IsDue(132_000, true));
    }

    [Fact]
    public void After_8s_total_interval_is_2s()
    {
        var s = new LivePreviewScheduler();
        s.MarkPreviewed(128_000);
        Assert.False(s.IsDue(128_000 + 31_999, true));
        Assert.True(s.IsDue(128_000 + 32_000, true));
    }

    [Fact]
    public void No_visible_text_keeps_half_second_cadence_even_late()
    {
        var s = new LivePreviewScheduler();
        s.MarkPreviewed(200_000);
        Assert.True(s.IsDue(208_000, false));
    }

    [Fact]
    public void Failed_volume_gate_does_not_advance_cadence()
    {
        var s = new LivePreviewScheduler();
        Assert.False(s.ShouldPreview(8_000, false, new float[8000]));
        Assert.True(s.ShouldPreview(8_000, false, Tone(8000, 0.3f)));
        Assert.True(s.ShouldPreview(8_000, false, new float[8000], vadRecentSpeech: true));
    }

    [Fact]
    public void Window_is_last_15_seconds()
    {
        Assert.Equal((0L, 100_000), LivePreviewScheduler.Window(100_000));
        Assert.Equal((60_000L, 240_000), LivePreviewScheduler.Window(300_000));
        Assert.Equal(3.75, LivePreviewScheduler.WindowStartSeconds(300_000));
    }

    [Fact]
    public void VolumeGate_rules()
    {
        Assert.False(VolumeGate.Qualifies(new float[8000]));
        Assert.True(VolumeGate.Qualifies(Tone(8000, 0.05f)));                  // RMS ~0.035 > 0.005
        var quietClick = new float[8000];
        for (int i = 0; i < 200; i++) quietClick[i * 40] = 0.02f;              // 2.5% samples at 0.02, RMS 0.0032
        Assert.True(VolumeGate.Qualifies(quietClick));
        var tooFew = new float[8000];
        for (int i = 0; i < 50; i++) tooFew[i * 100] = 0.03f;                  // 0.6% < 1.5%, RMS 0.0024
        Assert.False(VolumeGate.Qualifies(tooFew));
    }

    [Fact]
    public void VolumeGate_whole_recording_finds_speech_in_middle_and_in_unaligned_tail()
    {
        var a = new float[48_000];
        Tone(8000, 0.2f).CopyTo(a, 20_000);
        Assert.True(VolumeGate.ContainsSignificantAudio(a));

        var b = new float[41_000];                    // last full window ends at 40 000, tail 33 000..41 000
        Tone(8000, 0.2f).CopyTo(b, 33_000);
        Assert.True(VolumeGate.ContainsSignificantAudio(b));

        Assert.False(VolumeGate.ContainsSignificantAudio(new float[48_000]));
        Assert.False(VolumeGate.ContainsSignificantAudio(Array.Empty<float>()));
    }

    private static RecognitionResult R(string text, params (string w, double s, double e)[] words) =>
        new(text, words.Select(x => new RecognizedWord(x.w, x.s, x.e)).ToList(), TimeSpan.Zero);

    [Fact]
    public void Timeline_replaces_words_inside_window_and_freezes_earlier_ones()
    {
        var t = new PreviewTimeline();
        t.Apply(0, R("hello wor", ("hello", 0.0, 0.4), ("wor", 0.5, 0.7)));
        Assert.Equal("hello wor", t.Text);
        t.Apply(0, R("hello world again", ("hello", 0.0, 0.4), ("world", 0.5, 0.9), ("again", 1.0, 1.3)));
        Assert.Equal("hello world again", t.Text);
        // Window now starts at 1.0 s: "hello world" (ends before 1.0) stays frozen, "again" is replaced.
        t.Apply(1.0, R("again today", ("again", 0.0, 0.3), ("today", 0.4, 0.8)));
        Assert.Equal("hello world again today", t.Text);
    }

    [Fact]
    public void Timeline_empty_result_does_not_clear_and_untimed_result_is_fallback()
    {
        var t = new PreviewTimeline();
        t.Apply(0, R("raw text only"));
        Assert.Equal("raw text only", t.Text);
        t.Apply(0, R(""));
        Assert.Equal("raw text only", t.Text);
        t.Apply(0, R("hi", ("hi", 0, 0.2)));
        Assert.Equal("hi", t.Text);
    }

    [Fact]
    public void WordBuilder_groups_sentencepiece_tokens()
    {
        var words = WordBuilder.Build(new[] { "▁hel", "lo", "▁world", "." }, new[] { 0.1f, 0.3f, 0.6f, 0.9f }, 2.0, 1.2);
        Assert.Equal(new[] { "hello", "world." }, words.Select(w => w.Text));
        Assert.Equal(2.1, words[0].StartSeconds, 3);
        Assert.Equal(2.6, words[1].StartSeconds, 3);
        Assert.True(words[0].EndSeconds <= words[1].StartSeconds + 1e-9);
    }

    [Fact]
    public void ChunkPlanner_covers_all_audio_and_cuts_at_quiet_point()
    {
        var samples = new float[16000 * 50];
        for (int i = 0; i < samples.Length; i++) samples[i] = 0.1f * MathF.Sin(i * 0.05f);
        int quiet = 16000 * 21;
        Array.Clear(samples, quiet, 1600);
        var plan = ChunkPlanner.Plan(samples);
        Assert.Equal(0, plan[0].Start);
        Assert.Equal(samples.Length, plan[^1].End);
        for (int i = 1; i < plan.Count; i++) Assert.Equal(plan[i - 1].End, plan[i].Start);
        Assert.InRange(plan[0].End, quiet, quiet + 1600);
        Assert.All(plan, p => Assert.True(p.End - p.Start <= 16000 * 25));
        Assert.Single(ChunkPlanner.Plan(new float[16000 * 24]));
    }
}
