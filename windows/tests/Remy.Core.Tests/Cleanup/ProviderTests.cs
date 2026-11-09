using System.Net;
using System.Text;
using System.Text.Json;
using Remy.Core.Cleanup;
using Remy.Core.Context;
using Remy.Core.Contracts;
using Remy.Core.Settings;
using Xunit;

namespace Remy.Core.Tests.Cleanup;

public sealed record SeenRequest(HttpMethod Method, string Url, string Body, IReadOnlyDictionary<string, string> Headers);

public sealed class FakeHandler(Func<SeenRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<SeenRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        var seen = new SeenRequest(request.Method, request.RequestUri!.AbsoluteUri, body, headers);
        lock (Requests) Requests.Add(seen);
        return respond(seen);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class ProviderTests
{
    private static ContextPolicy Policy(bool share = false) => new(true, share,
        new Dictionary<DictationContextCategory, DictationWritingStyle>(), S1MiniAppStyling.Recommended, []);

    private static CleanupRuntime Rt(TranscriptPostProcessingMode mode, string? baseUrl, string model = "m", string? key = null,
        bool reasoning = true, bool structured = true, bool discovery = true, bool thinking = true,
        CleanupProfileAdapter? adapter = null, bool share = false) =>
        new(mode, "Be brief.", baseUrl, model, reasoning, key, TimeSpan.FromSeconds(30), adapter, null, null,
            structured, discovery, thinking, S1MiniStructure.Prose, S1MiniContextSetting.Automatic, Policy(share));

    private static CleanupRequest Req(CleanupRuntime rt, string input, DictationContextSnapshot? ctx = null, IReadOnlyList<string>? vocab = null, int seconds = 8) =>
        new(Guid.NewGuid(), Guid.NewGuid(), input, rt, ctx, vocab ?? [], DateTimeOffset.UtcNow.AddSeconds(seconds));

    private static string OllamaAnswer(string text) =>
        JsonSerializer.Serialize(new { done = true, done_reason = "stop", response = JsonSerializer.Serialize(new { action = "pasteCleanedText", text }) });

    private static string ChatAnswer(string text) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = JsonSerializer.Serialize(new { action = "pasteCleanedText", text }) }, finish_reason = "stop" } } });

    [Fact]
    public async Task OllamaRequestShapeAndCleaning()
    {
        var h = new FakeHandler(r => r.Url.EndsWith("/api/show")
            ? FakeHandler.Json("{\"capabilities\":[\"completion\"],\"model_info\":{\"llama.context_length\":4096}}")
            : FakeHandler.Json(OllamaAnswer("Hello there.")));
        using var router = new CleanupRouter(handler: h);
        var res = await router.CleanAsync(Req(Rt(TranscriptPostProcessingMode.Ollama, "http://127.0.0.1:11434", "gemma", reasoning: false), "hello there", vocab: ["Remy"]), default);

        Assert.Equal(CleanupOutcome.Cleaned, res.Outcome);
        Assert.Equal("Hello there.", res.Output);
        var gen = h.Requests.Single(r => r.Url.EndsWith("/api/generate"));
        Assert.Equal("http://127.0.0.1:11434/api/generate", gen.Url);
        using var doc = JsonDocument.Parse(gen.Body);
        var root = doc.RootElement;
        Assert.Equal("gemma", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("10m", root.GetProperty("keep_alive").GetString());
        Assert.Equal(4096, root.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal(CleanupChunker.OutputReserve(11), root.GetProperty("options").GetProperty("num_predict").GetInt32()); // known non-thinking
        Assert.Equal("object", root.GetProperty("format").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("think", out _)); // no thinking block, capability unsupported
        Assert.Equal(CleanupPromptBuilder.RemoteUserMessage("hello there", ["Remy"], null), root.GetProperty("prompt").GetString());
        Assert.Equal(CleanupPromptBuilder.RemoteSystemInstructions("Be brief.", ["Remy"], null), root.GetProperty("system").GetString());
    }

    [Fact]
    public async Task OllamaThinkFalseWhenReasoningOffAndToggleCapable()
    {
        var h = new FakeHandler(r => r.Url.EndsWith("/api/show")
            ? FakeHandler.Json("{\"thinking\":{\"values\":[true,false]}}")
            : FakeHandler.Json(OllamaAnswer("Hi.")));
        using var router = new CleanupRouter(handler: h);
        await router.CleanAsync(Req(Rt(TranscriptPostProcessingMode.Ollama, "http://localhost:11434", reasoning: false), "hi"), default);
        using var doc = JsonDocument.Parse(h.Requests.Single(r => r.Url.EndsWith("/generate")).Body);
        Assert.False(doc.RootElement.GetProperty("think").GetBoolean());
        Assert.Equal(-1, doc.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
    }

    [Fact]
    public async Task OllamaDiscoveryFailureFailsOpenToDefaults()
    {
        var h = new FakeHandler(r => r.Url.EndsWith("/api/show") ? new HttpResponseMessage(HttpStatusCode.NotFound) : FakeHandler.Json(OllamaAnswer("Hi.")));
        using var router = new CleanupRouter(handler: h);
        var res = await router.CleanAsync(Req(Rt(TranscriptPostProcessingMode.Ollama, "http://127.0.0.1:11434"), "hi"), default);
        Assert.Equal("Hi.", res.Output);
        using var doc = JsonDocument.Parse(h.Requests.Single(r => r.Url.EndsWith("/generate")).Body);
        Assert.Equal(8192, doc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task OpenAICompatibleShapeAndHeaders()
    {
        var h = new FakeHandler(_ => FakeHandler.Json(ChatAnswer("Hello.")));
        using var router = new CleanupRouter(handler: h);
        var res = await router.CleanAsync(Req(Rt(TranscriptPostProcessingMode.OpenAICompatible, "https://example.com/v1", "gpt", "sk-test"), "hello"), default);
        Assert.Equal("Hello.", res.Output);
        var r = Assert.Single(h.Requests);
        Assert.Equal("https://example.com/v1/chat/completions", r.Url);
        Assert.Equal("Bearer sk-test", r.Headers["Authorization"]);
        using var doc = JsonDocument.Parse(r.Body);
        var rf = doc.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", rf.GetProperty("type").GetString());
        Assert.Equal("dictate_anywhere_cleanup", rf.GetProperty("json_schema").GetProperty("name").GetString());
        Assert.True(rf.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task OpenAICompatibleOmitsResponseFormatWhenStructuredOff()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("{\"choices\":[{\"message\":{\"content\":\"Hello.\"},\"finish_reason\":\"stop\"}]}"));
        using var router = new CleanupRouter(handler: h);
        var res = await router.CleanAsync(Req(Rt(TranscriptPostProcessingMode.OpenAICompatible, "https://example.com/v1", structured: false), "hello"), default);
        Assert.Equal("Hello.", res.Output);
        Assert.DoesNotContain("response_format", h.Requests[0].Body);
    }

    [Fact]
    public async Task ProfileOllamaAdapterWithoutDiscoverySkipsShow()
    {
        var h = new FakeHandler(_ => FakeHandler.Json(OllamaAnswer("Hi.")));
        using var router = new CleanupRouter(handler: h);
        var rt = Rt(TranscriptPostProcessingMode.Profile, "https://ollama.example.com", discovery: false, structured: false, thinking: false,
            adapter: CleanupProfileAdapter.Ollama, key: "k");
        var res = await router.CleanAsync(Req(rt, "hi"), default);
        Assert.Equal("Hi.", res.Output);
        var r = Assert.Single(h.Requests);
        Assert.Contains("/api/generate", r.Url);
        Assert.DoesNotContain("\"format\"", r.Body);
        Assert.DoesNotContain("\"think\"", r.Body);
    }

    [Fact]
    public async Task OpenRouterUsesCatalogAndHeaders()
    {
        const string catalog = "{\"data\":[{\"id\":\"vendor/model\",\"context_length\":4096,\"supported_parameters\":[\"structured_outputs\"]," +
            "\"reasoning\":{\"supported_efforts\":[\"none\",\"low\"]}}]}";
        var h = new FakeHandler(r => r.Url.EndsWith("/models") ? FakeHandler.Json(catalog) : FakeHandler.Json(ChatAnswer("Done.")));
        using var router = new CleanupRouter(handler: h);
        var res = await router.CleanAsync(Req(Rt(TranscriptPostProcessingMode.OpenRouter, null, "vendor/model", "sk-or-1", reasoning: false), "done"), default);
        Assert.Equal("Done.", res.Output);
        var chat = h.Requests.Single(r => r.Url.EndsWith("/chat/completions"));
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", chat.Url);
        Assert.Equal("Remy", chat.Headers["X-Title"]);
        Assert.Equal("https://github.com/brysonjl/remy", chat.Headers["HTTP-Referer"]);
        using var doc = JsonDocument.Parse(chat.Body);
        Assert.True(doc.RootElement.GetProperty("provider").GetProperty("require_parameters").GetBoolean());
        Assert.Equal("none", doc.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(doc.RootElement.TryGetProperty("stream", out _));
    }

    [Fact]
    public async Task OpenRouterWithoutKeyFailsAuthentication()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("{\"data\":[]}"));
        using var router = new CleanupRouter(handler: h);
        var res = await router.CleanAsync(Req(Rt(TranscriptPostProcessingMode.OpenRouter, null, "m", null), "x y"), default);
        Assert.Equal(CleanupOutcome.FellBackToRaw, res.Outcome);
        Assert.Equal("authentication", res.Failure!.Category);
        Assert.Equal("x y", res.Output);
    }

    [Theory]
    [InlineData(401, "authentication")]
    [InlineData(429, "rateLimited")]
    [InlineData(500, "serverUnavailable")]
    [InlineData(400, "requestRejected")]
    [InlineData(302, "invalidConfiguration")]
    public async Task HttpStatusesClassify(int status, string category)
    {
        var h = new FakeHandler(_ => new HttpResponseMessage((HttpStatusCode)status));
        using var router = new CleanupRouter(handler: h);
        var res = await router.CleanAsync(Req(Rt(TranscriptPostProcessingMode.OpenAICompatible, "https://example.com/v1"), "raw text"), default);
        Assert.Equal(CleanupOutcome.FellBackToRaw, res.Outcome);
        Assert.Equal(category, res.Failure!.Category);
        Assert.Equal("raw text", res.Output);
    }

    [Fact]
    public async Task OversizedResponseIsRejected()
    {
        var h = new FakeHandler(_ => FakeHandler.Json(new string('x', CleanupHttpTransport.MaxResponseBytes + 10)));
        using var router = new CleanupRouter(handler: h);
        var res = await router.CleanAsync(Req(Rt(TranscriptPostProcessingMode.OpenAICompatible, "https://example.com/v1"), "raw"), default);
        Assert.Equal("responseTooLarge", res.Failure!.Category);
    }

    [Fact]
    public async Task ConnectionErrorIsTransport()
    {
        var h = new FakeHandler(_ => throw new HttpRequestException("down"));
        using var router = new CleanupRouter(handler: h);
        var res = await router.CleanAsync(Req(Rt(TranscriptPostProcessingMode.OpenAICompatible, "https://example.com/v1"), "raw"), default);
        Assert.Equal("transport", res.Failure!.Category);
    }

    [Fact]
    public async Task DeadlineProducesTimeoutFallback()
    {
        var h = new FakeHandler(_ => { Thread.Sleep(1500); return FakeHandler.Json(ChatAnswer("late")); });
        using var router = new CleanupRouter(handler: h);
        var req = new CleanupRequest(Guid.NewGuid(), Guid.NewGuid(), "raw", Rt(TranscriptPostProcessingMode.OpenAICompatible, "https://example.com/v1"),
            null, [], DateTimeOffset.UtcNow.AddMilliseconds(200));
        var res = await router.CleanAsync(req, default);
        Assert.Equal(CleanupOutcome.FellBackToRaw, res.Outcome);
        Assert.Equal("timeout", res.Failure!.Category);
    }

    [Fact]
    public async Task CallerCancellationThrows()
    {
        var h = new FakeHandler(_ => FakeHandler.Json(ChatAnswer("x")));
        using var router = new CleanupRouter(handler: h);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            router.CleanAsync(Req(Rt(TranscriptPostProcessingMode.OpenAICompatible, "https://example.com/v1"), "raw"), cts.Token));
    }

    [Fact]
    public async Task SkipRules()
    {
        var h = new FakeHandler(_ => throw new InvalidOperationException("must not call"));
        using var router = new CleanupRouter(handler: h);
        var off = await router.CleanAsync(Req(CleanupRuntime.Off(Policy()), "hello"), default);
        Assert.Equal(CleanupOutcome.Skipped, off.Outcome);
        Assert.Equal("hello", off.Output);
        var blank = await router.CleanAsync(Req(Rt(TranscriptPostProcessingMode.Ollama, "http://127.0.0.1:11434"), "   "), default);
        Assert.Equal(CleanupOutcome.Skipped, blank.Outcome);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task S1ModeDelegatesOrReportsModelUnavailable()
    {
        using var none = new CleanupRouter();
        var rt = Rt(TranscriptPostProcessingMode.S1Mini, null);
        var r1 = await none.CleanAsync(Req(rt, "hello"), default);
        Assert.Equal("modelUnavailable", r1.Failure!.Category);

        var s1 = new FakeS1();
        using var withS1 = new CleanupRouter(s1);
        var r2 = await withS1.CleanAsync(Req(rt, "hello"), default);
        Assert.Equal("S1 says hi", r2.Output);
        Assert.True(s1.Called);
    }

    private sealed class FakeS1 : IS1MiniEngine
    {
        public bool Called;
        public bool IsLoaded => true;
        public Task LoadAsync(string ggufPath, CancellationToken ct) => Task.CompletedTask;
        public void Unload() { }
        public void Dispose() { }
        public Task PrewarmAsync(CleanupRuntime runtime, CancellationToken ct) => Task.CompletedTask;
        public Task<CleanupResult> CleanAsync(CleanupRequest request, CancellationToken ct)
        {
            Called = true;
            return Task.FromResult(new CleanupResult(CleanupOutcome.Cleaned, "S1 says hi", null, null, null, TimeSpan.Zero));
        }
    }

    [Fact]
    public async Task LongTranscriptIsChunkedInOrderAndFailsOpenPerChunk()
    {
        var calls = 0;
        var h = new FakeHandler(r =>
        {
            var n = Interlocked.Increment(ref calls);
            using var d = JsonDocument.Parse(r.Body);
            var user = d.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            var inner = user["<transcript>".Length..^"</transcript>".Length];
            // the 2nd request returns an unusable envelope -> that chunk stays raw; others upper-cased
            return FakeHandler.Json(n == 2 ? ChatAnswer("") : ChatAnswer(inner.ToUpperInvariant()));
        });
        using var router = new CleanupRouter(handler: h);
        var text = string.Concat(Enumerable.Repeat("This is a sentence about nothing at all. ", 300)).Trim();
        var res = await router.CleanAsync(Req(Rt(TranscriptPostProcessingMode.OpenAICompatible, "https://example.com/v1"), text, seconds: 30), default);
        Assert.Null(res.Failure);
        Assert.True(h.Requests.Count > 2);
        Assert.Equal(text.Length, res.Output.Length);
        Assert.Contains("THIS IS A SENTENCE", res.Output);
        Assert.Contains("This is a sentence about nothing at all.", res.Output); // the fail-open chunk
    }

    [Fact]
    public async Task InstructionsOverBudgetFailBeforeNetwork()
    {
        var h = new FakeHandler(_ => throw new InvalidOperationException("must not call"));
        using var router = new CleanupRouter(handler: h);
        var rt = Rt(TranscriptPostProcessingMode.OpenAICompatible, "https://example.com/v1") with { Prompt = new string('p', 9000) };
        var res = await router.CleanAsync(Req(rt, "hello"), default);
        Assert.Equal("requestBudgetExceeded", res.Failure!.Category);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task CredentialNotSentOverRemoteHttp()
    {
        var h = new FakeHandler(_ => FakeHandler.Json(ChatAnswer("x")));
        using var router = new CleanupRouter(handler: h);
        var res = await router.CleanAsync(Req(Rt(TranscriptPostProcessingMode.OpenAICompatible, "http://192.168.1.5:1234/v1", key: "secret"), "raw"), default);
        Assert.Equal("invalidConfiguration", res.Failure!.Category);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task RemoteContextIsCategoryOnlyWithoutOptIn()
    {
        var h = new FakeHandler(_ => FakeHandler.Json(ChatAnswer("Hi.")));
        using var router = new CleanupRouter(handler: h);
        var captured = new CapturedContext("slack.exe", "Secret title", "https://app.slack.com/x", "app.slack.com", "label", null, "before text", "after text", null);
        var snap = new DictationContextSnapshot(DictationContextCategory.WorkMessaging, DictationWritingStyle.Formal, "Slack", "slack.exe",
            "app.slack.com", captured, [], DictationCursorPlacement.MidSentence, true);
        await router.CleanAsync(Req(Rt(TranscriptPostProcessingMode.OpenAICompatible, "https://example.com/v1"), "hi", snap), default);
        var body = h.Requests[0].Body;
        Assert.Contains("Work messaging", body);
        Assert.Contains("<application>Slack</application>", body);
        Assert.DoesNotContain("Secret title", body);
        Assert.DoesNotContain("before text", body);

        h.Requests.Clear();
        await router.CleanAsync(Req(Rt(TranscriptPostProcessingMode.OpenAICompatible, "https://example.com/v1", share: true), "hi", snap), default);
        Assert.Contains("before text", h.Requests[0].Body);
        Assert.Contains("Secret title", h.Requests[0].Body);

        h.Requests.Clear();   // loopback endpoint sends full context without the opt-in
        await router.CleanAsync(Req(Rt(TranscriptPostProcessingMode.OpenAICompatible, "http://127.0.0.1:1234/v1"), "hi", snap), default);
        Assert.Contains("before text", h.Requests[0].Body);
    }

    [Fact]
    public async Task DiscoveryHelpers()
    {
        var h = new FakeHandler(r =>
        {
            if (r.Url.EndsWith("/api/tags")) return FakeHandler.Json("{\"models\":[{\"name\":\"gemma:2b\"},{\"name\":\" gemma:2b \"},{\"name\":\"llama3:latest\"}]}");
            if (r.Url.EndsWith("/v1/models")) return FakeHandler.Json("{\"data\":[{\"id\":\"b-model\"},{\"id\":\"A-model\"},{\"id\":\"b-model\"}]}");
            if (r.Url.EndsWith("/api/show")) return FakeHandler.Json("{\"thinking\":{\"values\":[true,false]},\"model_info\":{\"x.context_length\":32768}}");
            return FakeHandler.Json(ChatAnswer("Remy connection check. Keep the final blue word."));
        });
        using var http = new CleanupHttpTransport(h);
        var d = new CleanupDiscovery(http);
        var tags = await d.ListOllamaModelsAsync("http://127.0.0.1:11434", null, default);
        Assert.Equal(["gemma:2b", "llama3:latest"], tags.Models);
        Assert.Equal("llama3:latest", CleanupDiscovery.MatchingInstalledModel("llama3", tags.Models));
        Assert.Null(CleanupDiscovery.MatchingInstalledModel("gemma", ["gemma2:2b"]));
        var models = await d.ListOpenAICompatibleModelsAsync("https://example.com", "k", default);
        Assert.Equal(["A-model", "b-model"], models.Models);
        var (info, _) = await d.GetOllamaReasoningAsync("http://127.0.0.1:11434", "gemma", null, default);
        Assert.Equal(OllamaReasoningCapability.Toggle, info!.Capability);
        Assert.Equal(8192, info.ContextLength);

        using var router = new CleanupRouter(handler: h);
        var ok = await CleanupDiscovery.TestConnectionAsync(router, Rt(TranscriptPostProcessingMode.OpenAICompatible, "https://example.com/v1"), default);
        Assert.True(ok.Passed);
        var failing = new CleanupRouter(handler: new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var bad = await CleanupDiscovery.TestConnectionAsync(failing, Rt(TranscriptPostProcessingMode.OpenAICompatible, "https://example.com/v1"), default);
        Assert.False(bad.Passed);
        Assert.Equal("authentication", bad.Failure!.Category);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434", "generate", "http://127.0.0.1:11434/api/generate")]
    [InlineData("127.0.0.1:11434/", "tags", "http://127.0.0.1:11434/api/tags")]
    [InlineData("https://h.com/api", "show", "https://h.com/api/show")]
    [InlineData("https://h.com/api/generate", "generate", "https://h.com/api/generate")]
    [InlineData("https://h.com/proxy", "generate", "https://h.com/proxy/api/generate")]
    public void OllamaUrls(string b, string kind, string expected) => Assert.Equal(expected, CleanupEndpoints.OllamaUrl(b, kind));

    [Theory]
    [InlineData("https://ollama.com", "chat/completions", "https://ollama.com/v1/chat/completions")]
    [InlineData("http://127.0.0.1:1234/v1", "models", "http://127.0.0.1:1234/v1/models")]
    [InlineData("https://h.com/v1/chat/completions", "chat/completions", "https://h.com/v1/chat/completions")]
    [InlineData("https://h.com/V1/", "models", "https://h.com/V1/models")]
    [InlineData("https://h.com/openai", "models", "https://h.com/openai/v1/models")]
    public void OpenAIUrls(string b, string path, string expected) => Assert.Equal(expected, CleanupEndpoints.OpenAIUrl(b, path));

    [Fact]
    public void OllamaReasoningCapabilities()
    {
        OllamaModelDetails P(string json) => OllamaModelDetails.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal(OllamaReasoningCapability.Toggle, P("{\"thinking\":{\"values\":[true,false]}}").Reasoning);
        Assert.Equal(OllamaReasoningCapability.Level, P("{\"thinking\":{\"values\":[\"low\",\"high\"]}}").Reasoning);
        Assert.Equal(OllamaReasoningCapability.Required, P("{\"thinking\":{\"values\":[true]}}").Reasoning);
        Assert.Equal(OllamaReasoningCapability.Unsupported, P("{\"capabilities\":[\"completion\"]}").Reasoning);
        Assert.Equal(OllamaReasoningCapability.Toggle, P("{\"capabilities\":[\"completion\",\"thinking\"]}").Reasoning);
        Assert.Equal(OllamaReasoningCapability.Level, P("{\"capabilities\":[\"thinking\"],\"details\":{\"family\":\"gptoss\"}}").Reasoning);
        Assert.True(P("{\"thinking\":{\"values\":[false]}}").KnownNonThinking);
        Assert.Null(P("{\"capabilities\":[\"thinking\"],\"details\":{\"family\":\"gptoss\"}}").ReasoningOffOverride(false));
        Assert.Equal(2048, P("{\"model_info\":{\"a.context_length\":4096,\"b.context_length\":2048}}").CleanupContextLength);
    }
}
