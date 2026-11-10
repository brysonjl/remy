using System.Text;
using Remy.Core.Cleanup;
using Remy.Core.Contracts;
using Remy.Core.History;
using Remy.Core.Session;
using Remy.Core.Settings;
using Remy.Core.Text;
using Xunit;

namespace Remy.Core.Tests.Session;

public class TextRulesSessionTests
{
    static void LocalCleanup(Harness h)
    {
        h.Settings.TranscriptPostProcessingMode = TranscriptPostProcessingMode.Ollama;
        h.Settings.OllamaModel = "m";
    }

    [Fact]
    public async Task CorrectionsReachCleanupButSnippetExpansionsStayLocal()
    {
        using var h = new Harness(x =>
        {
            LocalCleanup(x);
            x.Settings.SetVocabularyCorrection("Wispr", ["whisper"]);
            x.Settings.DictationSnippets = [new DictationSnippet(Guid.NewGuid(), "my address", "1 Private Lane")];
            x.Recognizer.FinalText = () => "send whisper notes to my address";
            x.Cleanup.Handler = (r, ct) => Task.FromResult(new CleanupResult(CleanupOutcome.Cleaned, "Send Wispr notes to My Address.", null, "Fake", "m", TimeSpan.Zero));
        });
        await h.DictateHoldAsync();

        Assert.Equal("send Wispr notes to my address", h.Cleanup.Last!.Input);
        Assert.Equal(["Send Wispr notes to 1 Private Lane."], h.Clipboard.Copies);
        Assert.Equal("Send Wispr notes to 1 Private Lane.", Assert.Single(h.History.Attempts).Output);
        Assert.Equal([(SelectionKind.Attempt, "Send Wispr notes to 1 Private Lane.")], h.History.Selections);
        Assert.Equal("Send Wispr notes to 1 Private Lane.", h.Coordinator.LastTranscript);
    }

    [Fact]
    public async Task UseRawStillAppliesLocalRules()
    {
        using var h = new Harness(x =>
        {
            LocalCleanup(x);
            x.Settings.SetVocabularyCorrection("Wispr", ["whisper"]);
            x.Settings.DictationSnippets = [new DictationSnippet(Guid.NewGuid(), "sig", "Best,\nAlex")];
            x.Recognizer.FinalText = () => "thanks whisper team sig";
            x.Cleanup.Handler = async (r, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; };
        });
        h.Hotkeys.Press(FakeHotkeys.Hold);
        await Harness.WaitFor(() => h.Coordinator.Status == DictationStatus.Recording);
        h.Audio.FeedSpeech(1.2);
        h.Hotkeys.Release(FakeHotkeys.Hold);
        await Harness.WaitFor(() => h.Coordinator.CanUseRawNow);
        h.Coordinator.UseRawNow();
        await Harness.WaitFor(() => !h.Coordinator.IsBusy);
        Assert.Equal(["thanks Wispr team Best,\nAlex"], h.Clipboard.Copies);
    }

    [Fact]
    public async Task OffModeStillAppliesRules()
    {
        using var h = new Harness(x =>
        {
            x.Settings.DictationSnippets = [new DictationSnippet(Guid.NewGuid(), "sig", "Best")];
            x.Recognizer.FinalText = () => "ok sig";
        });
        await h.DictateHoldAsync();
        Assert.Equal(["ok Best"], h.Clipboard.Copies);
        Assert.Equal("ok sig", h.History.Selections.Count == 1 ? "ok sig" : "");   // raw selection records the delivered text
    }

    static Harness NamesHarness(Func<CancellationToken, Task<ScreenTextWalkResult?>>? walk, string process = "outlook.exe", Action<Harness>? more = null) =>
        new(x =>
        {
            LocalCleanup(x);
            x.Settings.SpellNamesFromActiveWindowEnabled = true;
            x.Settings.DictationContextAwarenessEnabled = true;
            x.Destinations.Target = FakeDestinations.Notepad(process: process);
            x.Destinations.ContextFactory = () => new CapturedContext(process, "Inbox", null, null, null, null, "", "", null);
            x.Destinations.ScreenWalk = walk;
            more?.Invoke(x);
        });

    static async Task RunAsync(Harness h, Func<Task>? beforeRelease = null)
    {
        h.Hotkeys.Press(FakeHotkeys.Hold);
        await Harness.WaitFor(() => h.Coordinator.Status == DictationStatus.Recording);
        if (beforeRelease != null) await beforeRelease();
        h.Audio.FeedSpeech(1.2);
        h.Hotkeys.Release(FakeHotkeys.Hold);
        await Harness.WaitFor(() => !h.Coordinator.IsBusy);
    }

    static Task<ScreenTextWalkResult?> Walk(params string[] texts) =>
        Task.FromResult<ScreenTextWalkResult?>(new ScreenTextWalkResult(texts, 1, false, TimeSpan.Zero, "completed"));

    [Fact]
    public async Task ScreenNamesReachCleanupRequest()
    {
        using var h = NamesHarness(_ => Walk("Okonkwo Adeyemi"));
        await RunAsync(h, async () => { await Harness.WaitFor(() => h.Destinations.Walks == 1); await Task.Delay(100); });
        Assert.Equal(1, h.Destinations.Walks);
        Assert.Contains("Okonkwo Adeyemi", h.Cleanup.Last!.ScreenNames!);
    }

    [Theory]
    [InlineData("notepad.exe", false)]            // category other
    [InlineData("windowsterminal.exe", false)]
    [InlineData("chrome.exe", false)]
    public async Task NoWalkForDisallowedDestinations(string process, bool _)
    {
        using var h = NamesHarness(_ => Walk("Okonkwo Adeyemi"), process);
        await h.DictateHoldAsync();
        Assert.Equal(0, h.Destinations.Walks);
    }

    [Fact]
    public async Task NoWalkWhenOffOrWithoutConsumer()
    {
        using var off = NamesHarness(_ => Walk("Okonkwo Adeyemi"), more: x => x.Settings.SpellNamesFromActiveWindowEnabled = false);
        await off.DictateHoldAsync();
        Assert.Equal(0, off.Destinations.Walks);

        using var none = NamesHarness(_ => Walk("Okonkwo Adeyemi"), more: x => x.Settings.TranscriptPostProcessingMode = TranscriptPostProcessingMode.None);
        await none.DictateHoldAsync();
        Assert.Equal(0, none.Destinations.Walks);
    }

    [Fact]
    public async Task StopNeverWaitsForAnUnfinishedWalk()
    {
        var gate = new TaskCompletionSource<ScreenTextWalkResult?>();
        using var h = NamesHarness(_ => gate.Task, more: x => x.Recognizer.FinalText = () => "Recorded words.");
        await RunAsync(h, async () => await Harness.WaitFor(() => h.Destinations.Walks == 1));
        Assert.Equal(["Recorded words.!"], h.Clipboard.Copies);
        Assert.True(h.Cleanup.Last!.ScreenNames is null or { Count: 0 });
        gate.SetResult(new ScreenTextWalkResult(["Late Person"], 1, false, TimeSpan.Zero, "completed"));
        await Task.Delay(50);
        Assert.Single(h.Clipboard.Copies);
    }

    [Fact]
    public async Task ScreenNamesAreNeverWrittenToHistoryOrFiles()
    {
        const string marker = "Zyxwvutsrq Qponmlkj";
        using var h = NamesHarness(_ => Walk(marker), more: x =>
        {
            x.Recognizer.FinalText = () => "Recorded words.";
            x.Cleanup.Handler = (r, ct) => Task.FromResult(new CleanupResult(CleanupOutcome.Cleaned, r.Input + " Done.", null, "Fake", "m", TimeSpan.Zero));
        });
        await RunAsync(h, async () => { await Harness.WaitFor(() => h.Destinations.Walks == 1); await Task.Delay(100); });
        Assert.Contains(marker, h.Cleanup.Last!.ScreenNames!);
        var attempt = Assert.Single(h.History.Attempts);
        Assert.DoesNotContain("Zyxwvutsrq", attempt.Prompt ?? "");
        Assert.Equal("Recorded words. Done.", attempt.Output);

        var root = Environment.GetEnvironmentVariable("REMY_DATA_ROOT")!;
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); } catch { continue; }
            foreach (var enc in new Encoding[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode })
                Assert.DoesNotContain(enc.GetString(bytes), "Zyxwvutsrq", StringComparison.Ordinal);
        }
    }
}
