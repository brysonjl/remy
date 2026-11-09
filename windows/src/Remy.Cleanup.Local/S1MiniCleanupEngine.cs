using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Remy.Core.Cleanup;
using Remy.Core.Contracts;
using Remy.Core.Settings;

namespace Remy.Cleanup.Local;

/// <summary>S1-mini local cleanup on llama.cpp (LLamaSharp, CPU). Greedy decoding with prompt-lookup speculative
/// drafting from the transcript (output-identical to plain greedy). One generation at a time.</summary>
public sealed class S1MiniCleanupEngine : IS1MiniEngine
{
    public const string ProviderName = "S1-mini by Superwhisper";
    public const string ModelName = "S1-mini";
    public const int MaxTranscriptTokens = 1000;
    public const int ContextSize = 4096;

    private static Action<string>? s_logger;
    private static bool s_logConfigured;
    private static readonly object s_logLock = new();

    private readonly Func<string?>? _modelPathResolver;
    private readonly SemaphoreSlim _gate = new(1, 1);   // serializes load/unload/generate
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private string? _loadedPath;
    private volatile bool _loaded;
    private bool _disposed;

    /// <summary>Disable to fall back to plain token-by-token greedy decoding (used by tests/benchmarks).</summary>
    public bool UseSpeculativeDecoding { get; set; } = true;

    /// <summary>Statistics of the most recent generation (diagnostics only; no text).</summary>
    public S1MiniGenerationStats LastStats { get; private set; }

    /// <param name="logger">Receives llama.cpp error-level messages only.</param>
    /// <param name="modelPathResolver">Optional: path of the gguf used by PrewarmAsync/CleanAsync when the model is not yet loaded.</param>
    public S1MiniCleanupEngine(Action<string>? logger = null, Func<string?>? modelPathResolver = null)
    {
        _modelPathResolver = modelPathResolver;
        ConfigureNativeLogging(logger);
    }

    private static void ConfigureNativeLogging(Action<string>? logger)
    {
        lock (s_logLock)
        {
            if (logger != null) s_logger = logger;
            if (s_logConfigured) return;
            try
            {
                NativeLibraryConfig.All.WithLogCallback((level, msg) =>
                {
                    if (level < LLamaLogLevel.Error) return;
                    var m = msg.Trim();
                    if (m.Length == 0 || m.Trim('.').Length == 0) return;   // llama.cpp load-progress dots
                    s_logger?.Invoke(m);
                });
                s_logConfigured = true;
            }
            catch { /* native library already loaded; logging stays as it was */ }
        }
    }

    public bool IsLoaded => _loaded;

    // ------------------------------------------------------------------ load / unload

    public async Task LoadAsync(string ggufPath, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_weights != null && string.Equals(_loadedPath, ggufPath, StringComparison.OrdinalIgnoreCase)) return;
            DisposeModel();
            if (!File.Exists(ggufPath))
                throw new FileNotFoundException(S1MiniFailures.NotDownloaded, ggufPath);
            var path = ggufPath;
            _weights = await Task.Run(() => LLamaWeights.LoadFromFile(MakeParams(path)), ct).ConfigureAwait(false);
            _loadedPath = ggufPath;
            _loaded = true;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            DisposeModel();
            throw;
        }
        finally { _gate.Release(); }
    }

    public void Unload()
    {
        _gate.Wait();   // waits for a running generation to drain
        try { DisposeModel(); }
        finally { _gate.Release(); }
    }

    private void DisposeModel()
    {
        _loaded = false;
        try { _context?.Dispose(); } catch { }
        _context = null;
        try { _weights?.Dispose(); } catch { }
        _weights = null;
        _loadedPath = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _gate.Wait();
        try { _disposed = true; DisposeModel(); }
        finally { _gate.Release(); }
    }

    private static ModelParams MakeParams(string path)
    {
        var threads = CpuTopology.S1ThreadCount();
        return new ModelParams(path)
        {
            ContextSize = ContextSize,
            BatchSize = 2048,
            UBatchSize = 512,
            GpuLayerCount = 0,
            Threads = threads,
            BatchThreads = threads,
            TypeK = GGMLType.GGML_TYPE_Q8_0,
            TypeV = GGMLType.GGML_TYPE_Q8_0,
            FlashAttention = true,   // llama.cpp requires flash attention for a quantized V cache
        };
    }

    private async Task<bool> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return true;
        var path = _modelPathResolver?.Invoke();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        try { await LoadAsync(path, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
        return _loaded;
    }

    // ------------------------------------------------------------------ prewarm

    public async Task PrewarmAsync(CleanupRuntime runtime, CancellationToken ct)
    {
        if (!await EnsureLoadedAsync(ct).ConfigureAwait(false)) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_weights == null) return;
            await Task.Run(() =>
            {
                try
                {
                    var ctx = EnsureContext();
                    Generate(ctx, "Hello.", S1MiniStyling.SemiFormal, S1MiniStructure.Prose, false, DateTimeOffset.MaxValue, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (S1MiniException) { ResetContext(); }
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------------ cleanup

    public async Task<CleanupResult> CleanAsync(CleanupRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        CleanupResult Done(CleanupOutcome o, string text, RemyCleanupFailure? f) =>
            new(o, text, f, ProviderName, ModelName, sw.Elapsed);
        CleanupResult Fail(S1MiniFailureKind k, string msg) =>
            Done(CleanupOutcome.FellBackToRaw, request.Input, S1MiniFailures.Create(k, msg));

        ct.ThrowIfCancellationRequested();
        var input = request.Input ?? "";
        if (string.IsNullOrWhiteSpace(input)) return Done(CleanupOutcome.Unchanged, input, null);
        // Dictated control markers: leave unchanged without loading the model.
        if (input.Contains("<|", StringComparison.Ordinal) && input.Contains("|>", StringComparison.Ordinal))
            return Done(CleanupOutcome.Unchanged, input, null);

        if (!await EnsureLoadedAsync(ct).ConfigureAwait(false))
            return Fail(S1MiniFailureKind.ModelUnavailable, S1MiniFailures.NotDownloaded);

        var category = request.Context?.Category;
        var styling = ResolveStyling(request.Runtime, category, request.Context != null);
        var structure = request.Runtime.S1Structure;
        bool email = S1MiniPromptBuilder.ResolveEmail(request.Runtime.S1Context, category);
        var deadline = request.Deadline;

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { return Fail(S1MiniFailureKind.ModelUnavailable, S1MiniFailures.NotDownloaded); }
        try
        {
            if (_weights == null) return Fail(S1MiniFailureKind.ModelUnavailable, S1MiniFailures.NotDownloaded);
            return await Task.Run(() =>
            {
                try
                {
                    var text = ProcessText(input, styling, structure, email, deadline, ct);
                    var outcome = string.Equals(text, input, StringComparison.Ordinal) ? CleanupOutcome.Unchanged : CleanupOutcome.Cleaned;
                    if (outcome == CleanupOutcome.Cleaned && string.IsNullOrWhiteSpace(text))
                        return Fail(S1MiniFailureKind.InvalidResponse, S1MiniFailures.InvalidText);
                    return Done(outcome, text, null);
                }
                catch (S1MiniException ex)
                {
                    if (ex.ResetsContext) ResetContext();
                    return Fail(ex.Kind, ex.Message);
                }
                catch (OperationCanceledException)
                {
                    ResetContext();
                    throw;
                }
                catch (Exception)
                {
                    ResetContext();
                    return Fail(S1MiniFailureKind.ProviderFailure, "The cleanup provider failed without a recognized error.");
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Per-category styling (cleanup.md 1.5): extended styles, then the four stored values, else Other.
    /// Context awareness off or no destination: the global style (Other).</summary>
    public static S1MiniStyling ResolveStyling(CleanupRuntime runtime, DictationContextCategory? category, bool hasContext)
    {
        var s = runtime.ContextPolicy.S1Styles;
        if (!runtime.ContextPolicy.IsEnabled || !hasContext || category == null) return s.Other;
        if (s.ExtendedStyles != null && s.ExtendedStyles.TryGetValue(category.Value, out var ext)) return ext;
        return category.Value switch
        {
            DictationContextCategory.Email => s.Email,
            DictationContextCategory.WorkMessaging => s.WorkMessaging,
            DictationContextCategory.PersonalMessaging => s.PersonalMessaging,
            _ => s.Other,
        };
    }

    // ------------------------------------------------------------------ text processing (chunking + safety)

    private string ProcessText(string input, S1MiniStyling styling, S1MiniStructure structure, bool email,
        DateTimeOffset deadline, CancellationToken ct)
    {
        var ctx = EnsureContext();
        var chunks = SplitIntoChunks(input);
        var sb = new StringBuilder(input.Length + 16);
        foreach (var (prefix, text, suffix) in chunks)
        {
            if (text.Length == 0) { sb.Append(prefix).Append(suffix); continue; }
            string raw;
            try { raw = Generate(ctx, text, styling, structure, email, deadline, ct); }
            catch (S1MiniException ex) when (ex.Kind == S1MiniFailureKind.ProviderFailure && ex.OutputLimit)
            {
                ResetContext();
                return input;   // outputLimitReached: return the whole original text
            }
            if (S1MiniOutputSafety.ContainsControlLeak(raw))
                throw new S1MiniException(S1MiniFailureKind.InvalidResponse, S1MiniFailures.InvalidText);
            sb.Append(prefix).Append(S1MiniOutputSafety.Accept(text, raw)).Append(suffix);
        }
        return sb.ToString();
    }

    private List<(string Prefix, string Text, string Suffix)> SplitIntoChunks(string input)
    {
        var list = new List<(string, string, string)>();
        int CountTokens(string s) => TokenizeTranscript(s).Length;
        int pos = 0;
        while (pos < input.Length)
        {
            var remaining = input.AsSpan(pos);
            int start = 0;
            while (start < remaining.Length && char.IsWhiteSpace(remaining[start])) start++;
            if (start == remaining.Length) { list.Add((input.Substring(pos), "", "")); break; }
            string rest = input.Substring(pos + start);
            string whole = rest.TrimEnd();
            int wholeTokens = CountTokens(whole);
            string chunkText; int consumed;
            if (wholeTokens <= MaxTranscriptTokens)
            {
                chunkText = whole; consumed = rest.Length;
            }
            else
            {
                // Largest prefix with <= 1000 tokens (binary search), then back up to a sentence/whitespace boundary.
                int lo = 1, hi = whole.Length, best = 0;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    if (CountTokens(whole.Substring(0, mid)) <= MaxTranscriptTokens) { best = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                int end = FindBoundary(whole, best);
                if (end <= 0) throw new S1MiniException(S1MiniFailureKind.InputTooLong, S1MiniFailures.TooLong(MaxTranscriptTokens, wholeTokens));
                chunkText = whole.Substring(0, end).TrimEnd();
                consumed = end;
            }
            string prefix = input.Substring(pos, start);
            int textEndAbs = pos + start + chunkText.Length;
            int consumedAbs = pos + start + consumed;
            if (consumedAbs >= input.Length || consumed == rest.Length) consumedAbs = Math.Max(consumedAbs, textEndAbs);
            string suffix = input.Substring(textEndAbs, consumedAbs - textEndAbs);
            list.Add((prefix, chunkText, suffix));
            if (consumedAbs <= pos) break;
            pos = consumedAbs;
            if (wholeTokens <= MaxTranscriptTokens)
            {
                // trailing whitespace belongs to this chunk's suffix
                if (pos < input.Length) { list[^1] = (list[^1].Item1, list[^1].Item2, list[^1].Item3 + input.Substring(pos)); pos = input.Length; }
            }
        }
        return list;
    }

    private static int FindBoundary(string text, int limit)
    {
        // Prefer a sentence boundary at or after limit/2, else any whitespace boundary.
        int minSentence = limit / 2;
        for (int i = limit; i >= Math.Max(1, minSentence); i--)
        {
            if (i < text.Length && !char.IsWhiteSpace(text[i]) ) continue;
            if (i >= text.Length) { if (IsSentenceEnd(text, i)) return i; continue; }
            if (IsSentenceEnd(text, i)) return i;
        }
        for (int i = limit; i >= 1; i--)
            if (i < text.Length && char.IsWhiteSpace(text[i])) return i;
        return 0;
    }

    private static bool IsSentenceEnd(string text, int i)
    {
        char prev = text[i - 1];
        return prev == '\n' || prev == '.' || prev == '!' || prev == '?' || prev == '。' || prev == '！' || prev == '？';
    }

    // ------------------------------------------------------------------ context + generation

    private LLamaContext EnsureContext()
    {
        if (_context != null) return _context;
        var w = _weights ?? throw new S1MiniException(S1MiniFailureKind.ModelUnavailable, S1MiniFailures.NotDownloaded);
        try { _context = w.CreateContext(MakeParams(_loadedPath!)); }
        catch (Exception) { throw new S1MiniException(S1MiniFailureKind.ModelUnavailable, S1MiniFailures.ContextFailed); }
        return _context;
    }

    private void ResetContext()
    {
        try { _context?.Dispose(); } catch { }
        _context = null;
    }

    private int[] TokenizeTranscript(string text)
    {
        var w = _weights ?? throw new S1MiniException(S1MiniFailureKind.ModelUnavailable, S1MiniFailures.NotDownloaded);
        var t = w.NativeHandle.Tokenize(text, false, false, Encoding.UTF8);
        return Array.ConvertAll(t, x => (int)x);
    }

    /// <summary>Greedy generation for one chunk. Raises S1MiniException for engine failures.</summary>
    private string Generate(LLamaContext ctx, string transcript, S1MiniStyling styling, S1MiniStructure structure,
        bool email, DateTimeOffset deadline, CancellationToken ct)
    {
        var w = _weights!;
        var sw = Stopwatch.StartNew();
        var transcriptTokens = TokenizeTranscript(transcript);
        if (transcriptTokens.Length == 0) throw new S1MiniException(S1MiniFailureKind.ProviderFailure, S1MiniFailures.TokenizeFailed);
        if (transcriptTokens.Length > MaxTranscriptTokens)
            throw new S1MiniException(S1MiniFailureKind.InputTooLong, S1MiniFailures.TooLong(MaxTranscriptTokens, transcriptTokens.Length));
        int maxOutput = Math.Max(32, (int)Math.Ceiling(transcriptTokens.Length * 1.3) + 32);

        var prompt = S1MiniPromptBuilder.Build(transcript, styling, structure, email);
        var promptTokens = w.NativeHandle.Tokenize(prompt, false, true, Encoding.UTF8);
        if (promptTokens.Length == 0) throw new S1MiniException(S1MiniFailureKind.ProviderFailure, S1MiniFailures.TokenizeFailed);
        if (promptTokens.Length + maxOutput + S1MiniInputDraft.MaxProposals + 1 > ContextSize)
            throw new S1MiniException(S1MiniFailureKind.InputTooLong, S1MiniFailures.TooLong(MaxTranscriptTokens, transcriptTokens.Length));

        var handle = ctx.NativeHandle;
        var vocab = w.NativeHandle.Vocab;
        handle.MemoryClear(true);
        using var sampler = new GreedySamplingPipeline();
        var batch = new LLamaBatch();
        var bytes = new List<byte>(256);
        var emitted = new List<int>(maxOutput);
        S1MiniInputDraft? draft = null;
        if (UseSpeculativeDecoding)
        {
            var src = styling is S1MiniStyling.SemiFormal or S1MiniStyling.Formal
                ? S1MiniInputDraft.SentenceCase(transcript) : transcript;
            draft = new S1MiniInputDraft(TokenizeTranscript(src));
        }

        void CheckStop()
        {
            ct.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
                throw new S1MiniException(S1MiniFailureKind.Timeout, "Cleanup did not finish before its time limit.") { ResetsContext = true };
        }

        // Prompt evaluation in n_batch-sized slices; logits only for the last token.
        int pos = 0, forwardPasses = 0, accepted = 0, proposed = 0;
        while (pos < promptTokens.Length)
        {
            CheckStop();
            int n = Math.Min(2048, promptTokens.Length - pos);
            batch.Clear();
            for (int i = 0; i < n; i++)
                batch.Add(promptTokens[pos + i], pos + i, LLamaSeqId.Zero, pos + i == promptTokens.Length - 1);
            var rc = handle.Decode(batch);
            if (rc != DecodeResult.Ok) throw new S1MiniException(S1MiniFailureKind.ProviderFailure, S1MiniFailures.EvalFailed((int)rc)) { ResetsContext = true };
            pos += n;
        }
        int promptMs = (int)sw.ElapsedMilliseconds;

        LLamaToken pending = sampler.Sample(handle, batch.TokenCount - 1);
        forwardPasses++;
        bool done = false;
        while (!done)
        {
            if (pending.IsEndOfGeneration(vocab)) break;
            if (!Emit(pending)) throw new S1MiniException(S1MiniFailureKind.ProviderFailure, S1MiniFailures.OutputLimit) { OutputLimit = true, ResetsContext = true };
            CheckStop();

            var props = draft?.Propose(emitted) ?? Array.Empty<int>();
            batch.Clear();
            batch.Add(pending, pos, LLamaSeqId.Zero, true);
            for (int i = 0; i < props.Count; i++) batch.Add(props[i], pos + 1 + i, LLamaSeqId.Zero, true);
            var rc = handle.Decode(batch);
            forwardPasses++;
            if (rc != DecodeResult.Ok) throw new S1MiniException(S1MiniFailureKind.ProviderFailure, S1MiniFailures.GenerateFailed((int)rc)) { ResetsContext = true };
            proposed += props.Count;

            int j = 0;
            while (true)
            {
                LLamaToken s = sampler.Sample(handle, j);
                if (j < props.Count && (int)s == props[j])
                {
                    accepted++;
                    if (s.IsEndOfGeneration(vocab)) { done = true; j++; break; }
                    if (!Emit(s)) throw new S1MiniException(S1MiniFailureKind.ProviderFailure, S1MiniFailures.OutputLimit) { OutputLimit = true, ResetsContext = true };
                    j++;
                    continue;
                }
                pending = s;
                break;
            }
            pos += 1 + j;
            if (j < props.Count || done) handle.MemorySequenceRemove(LLamaSeqId.Zero, pos, -1);
        }

        handle.MemoryClear(true);
        LastStats = new S1MiniGenerationStats(promptTokens.Length, emitted.Count, forwardPasses, proposed, accepted, promptMs, (int)sw.ElapsedMilliseconds);
        return Encoding.UTF8.GetString(bytes.ToArray());

        bool Emit(LLamaToken t)
        {
            if (emitted.Count >= maxOutput) return false;
            emitted.Add((int)t);
            var big = new byte[128];
            int n = (int)w.NativeHandle.TokenToSpan(t, big, 0, false);
            if (n > big.Length)
            {
                big = new byte[n];
                n = (int)w.NativeHandle.TokenToSpan(t, big, 0, false);
            }
            for (int k = 0; k < n; k++) bytes.Add(big[k]);
            return true;
        }
    }

    private sealed class S1MiniException : Exception
    {
        public S1MiniFailureKind Kind { get; }
        public bool ResetsContext { get; init; }
        public bool OutputLimit { get; init; }
        public S1MiniException(S1MiniFailureKind kind, string message) : base(message) => Kind = kind;
    }
}

public readonly record struct S1MiniGenerationStats(
    int PromptTokens, int OutputTokens, int ForwardPasses, int ProposedTokens, int AcceptedTokens, int PromptMs, int TotalMs);
