using System.Text.Json;
using Remy.Core.Cleanup;
using Remy.Core.Context;
using Remy.Core.Contracts;
using Remy.Core.Settings;
using Remy.Core.Tests.Cleanup;
using Xunit;

namespace Remy.Core.Tests.Context;

public class ScreenNameTermsTests
{
    [Fact]
    public void People()
    {
        var t = ScreenNameTerms.Extract(["Siobhan Delacroix-Ng <siobhan@example.com>", "Thanks Priya, see you Monday.", "Meeting notes from Joaquín Ortega", "Ludwig van Beethoven"]);
        foreach (var x in new[] { "Siobhan Delacroix-Ng", "Priya", "Joaquín Ortega", "Ludwig van Beethoven" }) Assert.Contains(x, t);
        foreach (var x in new[] { "Monday", "Thanks", "Meeting", "siobhan@example.com", "Siobhan" }) Assert.DoesNotContain(x, t);
        Assert.DoesNotContain(t, x => x.Contains('@'));
    }

    [Fact]
    public void Identifiers() =>
        Assert.Equal(new HashSet<string> { "fetchUserProfile", "account_id", "viewModel", "refreshToken" },
            ScreenNameTerms.Extract(["let profile = fetchUserProfile(for: account_id)", "self.viewModel.refreshToken()", "https://example.com/fooBar"]).ToHashSet());

    [Fact]
    public void Stoplist()
    {
        Assert.Empty(ScreenNameTerms.Extract(["Inbox", "Reply All", "Settings", "Monday March 3", "Archive", "Looking forward to it.", "Sounds good."]));
        Assert.Equal(["Anneliese"], ScreenNameTerms.Extract(["Looking forward. Talk to Anneliese tomorrow"]));
    }

    [Fact]
    public void RepeatedLabelsRankAfterFullNames() =>
        Assert.Equal(["Okonkwo Adeyemi", "Priya"], ScreenNameTerms.Extract(["Priya", "Hello there", "Priya", "Okonkwo Adeyemi"]));

    [Fact]
    public void CapsAndDedupes()
    {
        Assert.Equal(25, ScreenNameTerms.Extract(Enumerable.Range(0, 60).Select(i => $"valueNumber{i}x")).Count);
        Assert.Equal(["fetchUser"], ScreenNameTerms.Extract(["fetchUser", "FETCHUSER fetchuser fetchUser"]));
    }

    [Fact]
    public void MergesHints()
    {
        var hints = DictationContextSnapshotBuilder.LexicalHints("Hi Zephyrine, ");
        var merged = ScreenNameTerms.Merged(hints, ScreenNameTerms.Extract(["zephyrine", "Thanks Zephyrine, from Okonkwo Adeyemi"]));
        Assert.Single(merged, t => t.Equals("zephyrine", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Okonkwo Adeyemi", merged);
        Assert.Equal("Zephyrine", merged[0]);
    }
}

public class ScreenNameWalkTests
{
    sealed class Clock { public TimeSpan Now; }

    sealed class Node(Clock clock, TimeSpan step, ScreenNodeSnapshotBuilder build) : IScreenNode
    {
        public static int Reads;
        public static List<TimeSpan> Timeouts = [];
        public ScreenNodeSnapshot? Read(TimeSpan timeout)
        {
            Reads++; Timeouts.Add(timeout);
            clock.Now += step;
            return build();
        }
    }

    delegate ScreenNodeSnapshot ScreenNodeSnapshotBuilder();

    static (ScreenTextWalkResult Result, int Reads) Walk(Func<Clock, IScreenNode> make, int stepMs = 0, Func<bool>? cancelled = null)
    {
        var clock = new Clock();
        Node.Reads = 0; Node.Timeouts = [];
        var root = make(clock);
        var r = ScreenNameWalk.Run(root, () => clock.Now, cancelled);
        return (r, Node.Reads);
    }

    static Node Leaf(Clock c, int stepMs, bool secure, params string[] texts) =>
        new(c, TimeSpan.FromMilliseconds(stepMs), () => new ScreenNodeSnapshot(secure, texts, []));

    static Node Parent(Clock c, int stepMs, IEnumerable<IScreenNode> kids, params string[] texts) =>
        new(c, TimeSpan.FromMilliseconds(stepMs), () => new ScreenNodeSnapshot(false, texts, kids.ToList()));

    [Fact]
    public void NodeCap()
    {
        var (r, reads) = Walk(c => Parent(c, 0, Enumerable.Range(0, 1000).Select(_ => Leaf(c, 0, false)).ToList()));
        Assert.Equal((400, 400, false), (r.Nodes, reads, r.BudgetHit));
    }

    [Fact]
    public void DepthCap()
    {
        IScreenNode Chain(Clock c, int n) => n == 0 ? Leaf(c, 0, false) : Parent(c, 0, [Chain(c, n - 1)]);
        var (r, _) = Walk(c => Chain(c, 100));
        Assert.Equal(41, r.Nodes);
        Assert.False(r.BudgetHit);
    }

    [Fact]
    public void TimeBudget()
    {
        var (r, _) = Walk(c => Parent(c, 10, Enumerable.Range(0, 50).Select(_ => Leaf(c, 10, false)).ToList()));
        Assert.True(r.BudgetHit);
        Assert.Equal(8, r.Nodes);
        Assert.True(r.Elapsed <= TimeSpan.FromMilliseconds(80));
        Assert.Equal(TimeSpan.FromMilliseconds(50), Node.Timeouts[0]);
        Assert.Equal(5, Node.Timeouts[^1].TotalMilliseconds, 0.5);
        Assert.All(Node.Timeouts, t => Assert.True(t <= TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void SecureFieldsAndSubtreesSkipped()
    {
        var (r, _) = Walk(c => Parent(c, 0, [
            new Node(c, TimeSpan.Zero, () => new ScreenNodeSnapshot(true, ["Hunter Secret"], [Leaf(c, 0, false, "Inner Hidden")])),
            Leaf(c, 0, false, "Visible Person")]));
        Assert.Equal(["Visible Person"], r.Texts);
        Assert.Equal(3, r.Nodes);
    }

    [Fact]
    public void BoundsAndDeduplicates()
    {
        var (r, _) = Walk(c => Parent(c, 0, [Leaf(c, 0, false, "Same")], new string('a', 2000), "  ", "Same"));
        Assert.Equal([400, 4], r.Texts.Select(t => t.Length));
    }

    [Fact]
    public void CancellationStops()
    {
        var (r, _) = Walk(c => Parent(c, 0, Enumerable.Range(0, 20).Select(_ => Leaf(c, 0, false)).ToList()), cancelled: () => Node.Reads >= 3);
        Assert.Equal(3, r.Nodes);
    }
}

public class ScreenNamePolicyAndPromptTests
{
    static ContextPolicy Policy(bool enabled = true, bool share = false) => new(enabled, share,
        new Dictionary<DictationContextCategory, DictationResolvedWritingStyle>(), S1MiniAppStyling.Recommended, []);

    static CleanupRuntime Rt(TranscriptPostProcessingMode mode, string? url, bool enabled = true, bool share = false) =>
        new(mode, "Be brief.", url, "m", false, null, TimeSpan.FromSeconds(30), null, null, null, true, false, false,
            S1MiniStructure.Prose, S1MiniContextSetting.Automatic, Policy(enabled, share));

    [Fact]
    public void CaptureRules()
    {
        var local = Rt(TranscriptPostProcessingMode.Ollama, "http://127.0.0.1:11434");
        Assert.True(ScreenNamePolicy.ShouldCapture(true, local));
        Assert.False(ScreenNamePolicy.ShouldCapture(false, local));
        Assert.False(ScreenNamePolicy.ShouldCapture(true, Rt(TranscriptPostProcessingMode.Ollama, "http://127.0.0.1:11434", enabled: false)));
        Assert.False(ScreenNamePolicy.ShouldCapture(true, Rt(TranscriptPostProcessingMode.None, null)));
        Assert.False(ScreenNamePolicy.ShouldCapture(true, Rt(TranscriptPostProcessingMode.S1Mini, null)));
        Assert.False(ScreenNamePolicy.ShouldCapture(true, Rt(TranscriptPostProcessingMode.Ollama, "https://ollama.com")));
        Assert.False(ScreenNamePolicy.ShouldCapture(true, Rt(TranscriptPostProcessingMode.OpenRouter, null)));
        Assert.True(ScreenNamePolicy.ShouldCapture(true, Rt(TranscriptPostProcessingMode.OpenRouter, null, share: true)));
    }

    static DictationContextSnapshot Snap(DictationContextCategory cat, bool secure = false, bool excluded = false, string process = "outlook.exe") =>
        new(cat, DictationResolvedWritingStyle.BuiltIn(DictationWritingStyle.Casual), null, process, null, null, [], IsSecureField: secure, IsExcluded: excluded);

    [Fact]
    public void WalkAllowed()
    {
        foreach (var cat in Enum.GetValues<DictationContextCategory>())
        {
            var allowed = cat is DictationContextCategory.Email or DictationContextCategory.WorkMessaging or DictationContextCategory.PersonalMessaging
                or DictationContextCategory.CodingAgent or DictationContextCategory.AiChat;
            Assert.Equal(allowed, ScreenNamePolicy.AllowsWalk(Snap(cat)));
        }
        Assert.False(ScreenNamePolicy.AllowsWalk(Snap(DictationContextCategory.Email, secure: true)));
        Assert.False(ScreenNamePolicy.AllowsWalk(Snap(DictationContextCategory.Email, excluded: true)));
        Assert.False(ScreenNamePolicy.AllowsWalk(Snap(DictationContextCategory.Email, process: "1password.exe")));
    }

    [Fact]
    public void SectionIsSeparateAndEscaped()
    {
        string[] names = ["Okonkwo Adeyemi", "Priya", "a<b"];
        var user = CleanupPromptBuilder.RemoteUserMessage("hello", ["priya"], null, names);
        Assert.True(user.IndexOf("<screen_names>", StringComparison.Ordinal) > user.IndexOf("</transcript>", StringComparison.Ordinal));
        Assert.Contains("Okonkwo Adeyemi", user);
        Assert.Contains("a&lt;b", user);
        Assert.Contains("may be unrelated", user);
        Assert.DoesNotContain("\nPriya\n", user);
        var system = CleanupPromptBuilder.RemoteSystemInstructions("x", ["priya"], null);
        Assert.DoesNotContain("Okonkwo", system);
        Assert.DoesNotContain("screen_names", system);
        Assert.DoesNotContain("screen_names", CleanupPromptBuilder.RemoteUserMessage("hello", [], null, []));
    }

    static async Task<string> RouteBody(CleanupRuntime rt, DictationContextSnapshot? ctx, string[] names)
    {
        var answer = JsonSerializer.Serialize(new { done = true, done_reason = "stop", response = JsonSerializer.Serialize(new { action = "pasteCleanedText", text = "Hello." }) });
        var chat = JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = JsonSerializer.Serialize(new { action = "pasteCleanedText", text = "Hello." }) }, finish_reason = "stop" } } });
        var handler = new FakeHandler(r => r.Url.Contains("/chat/completions") ? FakeHandler.Json(chat) : FakeHandler.Json(answer));
        using var router = new CleanupRouter(null, handler);
        var req = new CleanupRequest(Guid.NewGuid(), Guid.NewGuid(), "hello", rt, ctx, [], DateTimeOffset.UtcNow.AddSeconds(8), names);
        await router.CleanAsync(req, default);
        return string.Join("\n", handler.Requests.Select(r => r.Body));
    }

    [Fact]
    public async Task RouterGatesNames()
    {
        string[] names = ["Okonkwo Adeyemi"];
        var snap = Snap(DictationContextCategory.Email);
        Assert.Contains("screen_names", await RouteBody(Rt(TranscriptPostProcessingMode.Ollama, "http://127.0.0.1:11434"), snap, names));
        Assert.DoesNotContain("screen_names", await RouteBody(Rt(TranscriptPostProcessingMode.OpenAICompatible, "https://example.com/v1"), snap, names));
        Assert.Contains("screen_names", await RouteBody(Rt(TranscriptPostProcessingMode.OpenAICompatible, "https://example.com/v1", share: true), snap, names));
        Assert.DoesNotContain("screen_names", await RouteBody(Rt(TranscriptPostProcessingMode.Ollama, "http://127.0.0.1:11434"), Snap(DictationContextCategory.Email, secure: true), names));
        Assert.DoesNotContain("screen_names", await RouteBody(Rt(TranscriptPostProcessingMode.Ollama, "http://127.0.0.1:11434"), null, names));
    }
}
