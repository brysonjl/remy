// Contracts for speech recognition and cleanup. Owned by the lead. Additive changes only.

namespace Remy.Core.Contracts;

// ------------------------------------------------------------------ speech

public enum SpeechModelState { NotDownloaded, Downloading, Downloaded, Loading, Ready, Failed }

/// <summary>Parakeet v2 on sherpa-onnx (Remy.Speech). Thread-safe: one decode at a time internally.</summary>
public interface ISpeechRecognizer : IDisposable
{
    bool IsLoaded { get; }

    /// <summary>Load the model from <paramref name="modelDirectory"/> and warm it up. Idempotent.</summary>
    Task LoadAsync(string modelDirectory, CancellationToken ct);

    void Unload();

    /// <summary>Decode a short window (preview). Fresh decoder state each call. Returns words with timings relative to the window start.</summary>
    Task<RecognitionResult> DecodeWindowAsync(ReadOnlyMemory<float> samples16k, CancellationToken ct);

    /// <summary>Authoritative final decode of a whole recording, chunked at quiet points near every 20 s.</summary>
    Task<RecognitionResult> DecodeFinalAsync(ReadOnlyMemory<float> samples16k, CancellationToken ct);
}

public sealed record RecognizedWord(string Text, double StartSeconds, double EndSeconds);

public sealed record RecognitionResult(string Text, IReadOnlyList<RecognizedWord> Words, TimeSpan DecodeTime);

// ------------------------------------------------------------------ cleanup

/// <summary>Input to one cleanup attempt. Built by the coordinator from the frozen configuration.</summary>
public sealed record CleanupRequest(
    Guid SessionId,
    Guid AttemptId,
    string Input,                       // corrected raw transcript (after filler removal)
    Cleanup.CleanupRuntime Runtime,     // frozen mode, prompt, endpoint, model, credential, styles
    Context.DictationContextSnapshot? Context,
    IReadOnlyList<string> Vocabulary,
    DateTimeOffset Deadline);           // hard 8 s budget (cleanup.md §3.5)

public enum CleanupOutcome { Cleaned, Unchanged, FellBackToRaw, Skipped, Cancelled }

public sealed record CleanupResult(
    CleanupOutcome Outcome,
    string Output,                      // always usable: cleaned text or the raw input
    Cleanup.RemyCleanupFailure? Failure,
    string? ProviderName,
    string? ModelName,
    TimeSpan Elapsed);

/// <summary>A cleanup method. Remote providers live in Remy.Core.Cleanup; S1-mini lives in Remy.Cleanup.Local.</summary>
public interface ICleanupEngine
{
    /// <summary>Never throws for provider failures: returns FellBackToRaw with a classified failure. Throws only OperationCanceledException.</summary>
    Task<CleanupResult> CleanAsync(CleanupRequest request, CancellationToken ct);

    /// <summary>Optional warm-up started when recording begins (overlaps with speech).</summary>
    Task PrewarmAsync(Cleanup.CleanupRuntime runtime, CancellationToken ct);
}

/// <summary>Local S1-mini engine surface used by the cleanup router and the settings page.</summary>
public interface IS1MiniEngine : ICleanupEngine, IDisposable
{
    bool IsLoaded { get; }
    Task LoadAsync(string ggufPath, CancellationToken ct);
    void Unload();
}
