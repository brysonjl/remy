using Remy.Cleanup.Local;
using Remy.Core.Cleanup;
using Remy.Core.Contracts;
using Remy.Core.Context;
using Remy.Core.Settings;
using Xunit;
using Xunit.Abstractions;

namespace Remy.Cleanup.Local.Tests;

public class RealModelTests
{
    private readonly ITestOutputHelper _out;
    public RealModelTests(ITestOutputHelper output) => _out = output;

    private static string? FindModel()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir != null; i++, dir = Path.GetDirectoryName(dir))
        {
            var p = Path.Combine(dir, "spike", ".cache", "s1mini", "s1-mini-q4_k_m.gguf");
            if (File.Exists(p)) return p;
            p = Path.Combine(dir, "windows", "spike", ".cache", "s1mini", "s1-mini-q4_k_m.gguf");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static CleanupRequest Req(string input, DictationContextCategory? cat, S1MiniStructure structure = S1MiniStructure.Prose,
        TimeSpan? budget = null)
    {
        var policy = new ContextPolicy(true, false, new Dictionary<DictationContextCategory, DictationWritingStyle>(),
            S1MiniAppStyling.Recommended, Array.Empty<DictationAppRule>());
        var rt = CleanupRuntime.Off(policy) with { Mode = TranscriptPostProcessingMode.S1Mini, S1Structure = structure };
        var ctx = cat == null ? null : new DictationContextSnapshot(cat.Value, DictationWritingStyle.Neutral, null, null, null, null, Array.Empty<string>());
        return new CleanupRequest(Guid.NewGuid(), Guid.NewGuid(), input, rt, ctx, Array.Empty<string>(),
            DateTimeOffset.UtcNow + (budget ?? TimeSpan.FromSeconds(30)));
    }

    private static readonly (string Text, DictationContextCategory? Cat, S1MiniStructure Structure)[] Samples =
    {
        ("um so I think we should uh we should ship the windows version next week no wait the week after", DictationContextCategory.WorkMessaging, S1MiniStructure.Prose),
        ("hey can you send me the uh the report from yesterday thanks", DictationContextCategory.Email, S1MiniStructure.Prose),
        ("okay first buy milk second call mom and third uh finish the deck", null, S1MiniStructure.Lists),
        ("the temperature dropped to minus five degrees overnight and the pipes froze so we called the plumber at nine thirty", DictationContextCategory.PersonalMessaging, S1MiniStructure.Prose),
    };

    [Fact]
    public async Task CleansSamplesWithRealModel()
    {
        var model = FindModel();
        if (model == null) { _out.WriteLine("SKIP: gguf not found"); return; }

        var logs = new List<string>();
        using var engine = new S1MiniCleanupEngine(logs.Add);
        var load = System.Diagnostics.Stopwatch.StartNew();
        await engine.LoadAsync(model, CancellationToken.None);
        _out.WriteLine($"load: {load.ElapsedMilliseconds} ms");
        Assert.True(engine.IsLoaded);
        await engine.LoadAsync(model, CancellationToken.None); // idempotent

        var rt = Req("x", null).Runtime;
        var pw = System.Diagnostics.Stopwatch.StartNew();
        await engine.PrewarmAsync(rt, CancellationToken.None);
        _out.WriteLine($"prewarm: {pw.ElapsedMilliseconds} ms");

        var spec = new Dictionary<string, string>();
        foreach (var useSpec in new[] { true, false })
        {
            engine.UseSpeculativeDecoding = useSpec;
            foreach (var (text, cat, structure) in Samples)
            {
                var r = await engine.CleanAsync(Req(text, cat, structure), CancellationToken.None);
                var st = engine.LastStats;
                _out.WriteLine($"[spec={useSpec}] {r.Elapsed.TotalMilliseconds:F0} ms outcome={r.Outcome} passes={st.ForwardPasses}/{st.OutputTokens} tok accepted={st.AcceptedTokens}/{st.ProposedTokens}\n  IN : {text}\n  OUT: {r.Output}");
                Assert.NotEqual(CleanupOutcome.FellBackToRaw, r.Outcome);
                Assert.False(string.IsNullOrWhiteSpace(r.Output));
                Assert.DoesNotContain("<think>", r.Output);
                Assert.DoesNotContain("<|im_", r.Output);
                if (useSpec) spec[text] = r.Output;
                else Assert.Equal(spec[text], r.Output);
            }
        }

        engine.Unload();
        Assert.False(engine.IsLoaded);
        var gone = await engine.CleanAsync(Req("hello there", null), CancellationToken.None);
        Assert.Equal(CleanupOutcome.FellBackToRaw, gone.Outcome);
        Assert.Equal("hello there", gone.Output);
        Assert.Empty(logs);
    }

    [Fact]
    public async Task ControlMarkersAndCancellation()
    {
        var model = FindModel();
        if (model == null) return;
        using var engine = new S1MiniCleanupEngine();
        await engine.LoadAsync(model, CancellationToken.None);

        var r = await engine.CleanAsync(Req("literal <|im_start|> text", null), CancellationToken.None);
        Assert.Equal(CleanupOutcome.Unchanged, r.Outcome);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.CleanAsync(Req("hello there friend", null), cts.Token));

        var late = await engine.CleanAsync(Req("hello there friend, how are you doing today", null, budget: TimeSpan.FromMilliseconds(-1)), CancellationToken.None);
        Assert.Equal(CleanupOutcome.FellBackToRaw, late.Outcome);
        Assert.Equal("timeout", late.Failure!.Category);

        // Engine still works after a timeout.
        var ok = await engine.CleanAsync(Req("hello there friend", null), CancellationToken.None);
        Assert.NotEqual(CleanupOutcome.FellBackToRaw, ok.Outcome);
    }

    [Fact]
    public async Task OverlongTranscriptIsChunked()
    {
        var model = FindModel();
        if (model == null) return;
        using var engine = new S1MiniCleanupEngine();
        await engine.LoadAsync(model, CancellationToken.None);
        var words = new[] { "budget", "roadmap", "invoice", "launch", "contract", "survey", "backlog", "release", "pricing", "hiring" };
        var text = string.Join(" ", Enumerable.Range(1, 45).Select(i =>
            $"item {i} um we should check the {words[i % 10]} with the {words[(i * 7) % 10]} team before tuesday so"));
        var r = await engine.CleanAsync(Req(text, null, budget: TimeSpan.FromMinutes(3)), CancellationToken.None);
        _out.WriteLine($"{r.Failure?.Category} {engine.LastStats} {r.Outcome} {r.Elapsed.TotalSeconds:F1}s len {text.Length}->{r.Output.Length}");
        Assert.NotEqual(CleanupOutcome.FellBackToRaw, r.Outcome);
    }
}
