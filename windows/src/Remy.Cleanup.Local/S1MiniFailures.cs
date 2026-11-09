using Remy.Core.Cleanup;

namespace Remy.Cleanup.Local;

/// <summary>Failure kinds this engine produces. The factory below is the only place that builds RemyCleanupFailure.</summary>
public enum S1MiniFailureKind { ModelUnavailable, InputTooLong, InvalidResponse, ProviderFailure, Timeout }

public static class S1MiniFailures
{
    public const string NotDownloaded = "Download S1-mini before using local transcript cleanup.";
    public const string LoadFailed = "S1-mini could not be loaded. Delete and download the model again.";
    public const string ContextFailed = "S1-mini could not allocate its inference context.";
    public const string TokenizeFailed = "S1-mini could not tokenize the transcript.";
    public static string TooLong(int max, int actual) => $"S1-mini supports up to {max} transcript tokens; this transcript has {actual}.";
    public static string EvalFailed(int code) => $"S1-mini could not evaluate the transcript (code {code}).";
    public static string GenerateFailed(int code) => $"S1-mini stopped while generating cleaned text (code {code}).";
    public const string OutputLimit = "S1-mini reached its output limit before finishing cleanup.";
    public const string InvalidText = "S1-mini returned invalid text.";

    public static RemyCleanupFailure Create(S1MiniFailureKind kind, string detail) => kind switch
    {
        S1MiniFailureKind.ModelUnavailable => new RemyCleanupFailure("modelUnavailable", detail),
        S1MiniFailureKind.InputTooLong => new RemyCleanupFailure("inputTooLong", detail),
        S1MiniFailureKind.InvalidResponse => new RemyCleanupFailure("invalidResponse", detail),
        S1MiniFailureKind.Timeout => new RemyCleanupFailure("timeout", detail),
        _ => new RemyCleanupFailure("providerFailure", detail),
    };
}
