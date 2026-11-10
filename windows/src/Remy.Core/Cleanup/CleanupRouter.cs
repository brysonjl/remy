using System.Diagnostics;
using Remy.Core.Context;
using Remy.Core.Contracts;
using Remy.Core.Settings;

namespace Remy.Core.Cleanup;

/// <summary>Dispatches a cleanup request on the frozen mode (cleanup.md §3). Remote modes run here; S1-mini is delegated.</summary>
public sealed class CleanupRouter : ICleanupEngine, IDisposable
{
    private readonly IS1MiniEngine? _s1;
    private readonly CleanupHttpTransport _http;
    private readonly CleanupProviderCaches _caches;

    /// <param name="s1Mini">Local engine, or null when it is not available (S1 mode then falls back with modelUnavailable).</param>
    /// <param name="handler">Test hook: HTTP handler used for all remote calls.</param>
    public CleanupRouter(IS1MiniEngine? s1Mini = null, HttpMessageHandler? handler = null, CleanupProviderCaches? caches = null)
    {
        _s1 = s1Mini;
        _http = new CleanupHttpTransport(handler);
        _caches = caches ?? new CleanupProviderCaches();
    }

    public CleanupHttpTransport Transport => _http;
    public CleanupProviderCaches Caches => _caches;

    public static bool IsRemoteMode(TranscriptPostProcessingMode m) => m is
        TranscriptPostProcessingMode.Ollama or TranscriptPostProcessingMode.OpenRouter or
        TranscriptPostProcessingMode.OpenAICompatible or TranscriptPostProcessingMode.Profile;

    public async Task<CleanupResult> CleanAsync(CleanupRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var rt = request.Runtime;
        var input = request.Input;

        CleanupResult Result(CleanupOutcome o, string output, RemyCleanupFailure? f, string? provider = null) =>
            new(o, output, f, provider, rt.Model, sw.Elapsed);

        // §3.2 skip rules
        if (rt.Mode is TranscriptPostProcessingMode.None or TranscriptPostProcessingMode.FluidAudioVocabulary or
            TranscriptPostProcessingMode.AppleIntelligence || string.IsNullOrWhiteSpace(input))
            return Result(CleanupOutcome.Skipped, input, null);

        ct.ThrowIfCancellationRequested();
        var remaining = request.Deadline - DateTimeOffset.UtcNow;
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);

        string? providerName = null;
        try
        {
            string output;
            if (rt.Mode == TranscriptPostProcessingMode.S1Mini)
            {
                if (_s1 is null) return Result(CleanupOutcome.FellBackToRaw, input, RemyCleanupFailure.Of(RemyCleanupFailure.ModelUnavailable), "S1-mini");
                providerName = "S1-mini";
                var inner = await _s1.CleanAsync(request, deadlineCts.Token).ConfigureAwait(false);
                return inner with { ProviderName = inner.ProviderName ?? providerName, Elapsed = sw.Elapsed };
            }

            var provider = CreateProvider(rt);
            providerName = provider.Name;
            var context = DictationContextSnapshotBuilder.ToPostProcessingContext(request.Context, rt);
            // Screen names are lower-trust and only travel where captured text may (v0.2-features.md 7.7).
            var allowNames = ContextSharing.IncludesCapturedText(rt) && request.Context is { IsSecureField: false, IsExcluded: false };
            output = await ProcessRemoteAsync(provider, input, rt.Prompt, request.Vocabulary, context, deadlineCts.Token,
                allowNames ? request.ScreenNames : null).ConfigureAwait(false);

            output = CleanupAnswer.Normalize(output);
            if (string.IsNullOrWhiteSpace(output))
                return Result(CleanupOutcome.FellBackToRaw, input, RemyCleanupFailure.Of(RemyCleanupFailure.InvalidResponse), providerName);
            return Result(string.Equals(output, input, StringComparison.Ordinal) ? CleanupOutcome.Unchanged : CleanupOutcome.Cleaned, output, null, providerName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var late = deadlineCts.IsCancellationRequested || DateTimeOffset.UtcNow >= request.Deadline;
            var failure = RemyCleanupFailure.Classify(ex, deadlineReached: late && ex is OperationCanceledException);
            if (late && failure.Category is RemyCleanupFailure.Cancelled) failure = RemyCleanupFailure.Of(RemyCleanupFailure.Timeout);
            return Result(CleanupOutcome.FellBackToRaw, input, failure, providerName);
        }
    }

    public async Task PrewarmAsync(CleanupRuntime runtime, CancellationToken ct)
    {
        try
        {
            switch (runtime.Mode)
            {
                case TranscriptPostProcessingMode.S1Mini:
                    if (_s1 is not null) await _s1.PrewarmAsync(runtime, ct).ConfigureAwait(false);
                    break;
                case TranscriptPostProcessingMode.Ollama:
                case TranscriptPostProcessingMode.Profile when runtime.ProfileAdapter == CleanupProfileAdapter.Ollama:
                    if (!string.IsNullOrWhiteSpace(runtime.Model) && runtime.Discovery)
                        await OllamaCleanupProvider.FetchDetailsAsync(_http, _caches, runtime.BaseUrl, runtime.Model, runtime.ApiKey, ct).ConfigureAwait(false);
                    break;
                case TranscriptPostProcessingMode.OpenRouter:
                    await OpenRouterCleanupProvider.FetchCatalogAsync(_http, _caches, ct).ConfigureAwait(false);
                    break;
                // OpenAI-compatible: never a billable completion; model list fetch is the settings UI's job.
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { /* prewarm is best effort */ }
    }

    internal RemoteProvider CreateProvider(CleanupRuntime rt)
    {
        var timeout = rt.RequestTimeout > TimeSpan.Zero ? rt.RequestTimeout : TimeSpan.FromSeconds(30);
        var model = rt.Model ?? "";
        switch (rt.Mode)
        {
            case TranscriptPostProcessingMode.Ollama:
                return new OllamaCleanupProvider(_http, _caches, rt.BaseUrl ?? CleanupEndpoints.DefaultOllamaBaseUrl, model, rt.ApiKey,
                    timeout, rt.StructuredOutput, rt.Discovery, rt.ThinkingControl, rt.ReasoningEnabled);
            case TranscriptPostProcessingMode.OpenAICompatible:
                return new OpenAICompatibleCleanupProvider(_http, rt.BaseUrl ?? CleanupEndpoints.DefaultOpenAICompatibleBaseUrl, model, rt.ApiKey, timeout, rt.StructuredOutput);
            case TranscriptPostProcessingMode.OpenRouter:
                return new OpenRouterCleanupProvider(_http, _caches, model, rt.ApiKey, timeout, rt.ReasoningEnabled);
            case TranscriptPostProcessingMode.Profile:
                if (rt.ProfileAdapter == CleanupProfileAdapter.Ollama)
                    return new OllamaCleanupProvider(_http, _caches, rt.BaseUrl, model, rt.ApiKey, timeout, rt.StructuredOutput,
                        rt.Discovery, rt.ThinkingControl, rt.ReasoningEnabled);
                if (rt.ProfileAdapter == CleanupProfileAdapter.OpenAICompatible)
                    return new OpenAICompatibleCleanupProvider(_http, rt.BaseUrl, model, rt.ApiKey, timeout, rt.StructuredOutput);
                break;
        }
        throw new CleanupFailureException(RemyCleanupFailure.InvalidConfiguration);
    }

    /// <summary>Budget check, chunking, per-chunk requests (bounded concurrency) and fail-open safety checks (§1.1).</summary>
    internal static async Task<string> ProcessRemoteAsync(RemoteProvider provider, string text, string prompt,
        IReadOnlyList<string> vocabulary, DictationPostProcessingContext? context, CancellationToken ct,
        IReadOnlyList<string>? screenNames = null)
    {
        var limits = await provider.PrepareAsync(ct).ConfigureAwait(false);
        var system = CleanupPromptBuilder.RemoteSystemInstructions(prompt, vocabulary, context);

        bool Fits(string t) => CleanupChunker.FitsSingleRequest(t, system, CleanupPromptBuilder.RemoteUserMessage(t, vocabulary, context, screenNames),
            limits.Budget, limits.MaxCompletionTokens);

        // Pre-flight with an empty transcript: fixed instructions alone may exceed the budget.
        if (!Fits(""))
            throw new CleanupFailureException(RemyCleanupFailure.RequestBudgetExceeded);

        var chunks = CleanupChunker.Chunks(text, Fits);
        var outputs = new string[chunks.Count];

        async Task RunChunk(int i, CancellationToken token)
        {
            var c = chunks[i];
            if (c.PassesThrough) { outputs[i] = c.Prefix + c.Suffix; return; }
            var user = CleanupPromptBuilder.RemoteUserMessage(c.Text, vocabulary, context, screenNames);
            var answer = await provider.CompleteAsync(system, user, c.Text, token).ConfigureAwait(false);
            outputs[i] = c.Prefix + CleanupAnswer.CleanedResponse(answer, c.Text) + c.Suffix;
        }

        var concurrency = Math.Max(1, Math.Min(provider.MaxConcurrentRequests, chunks.Count));
        if (concurrency == 1)
        {
            for (var i = 0; i < chunks.Count; i++) await RunChunk(i, ct).ConfigureAwait(false);
        }
        else
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var gate = new SemaphoreSlim(concurrency);
            var tasks = Enumerable.Range(0, chunks.Count).Select(async i =>
            {
                await gate.WaitAsync(cts.Token).ConfigureAwait(false);
                try { await RunChunk(i, cts.Token).ConfigureAwait(false); }
                catch { cts.Cancel(); throw; }
                finally { gate.Release(); }
            }).ToList();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch
            {
                // Prefer the first real failure over a sibling's cancellation.
                var real = tasks.Where(t => t.IsFaulted).Select(t => t.Exception!.GetBaseException()).FirstOrDefault();
                if (real is not null && !ct.IsCancellationRequested) throw real;
                throw;
            }
        }
        return string.Concat(outputs);
    }

    public void Dispose() => _http.Dispose();
}
