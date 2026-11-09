using System.Net;
using System.Text.Json;

namespace Remy.Core.Cleanup;

/// <summary>Classified cleanup failure (cleanup.md §1.8). Category is the closed raw string stored in history.</summary>
public sealed record RemyCleanupFailure(string Category, string Message)
{
    public const string Timeout = "timeout";
    public const string UseRaw = "useRaw";
    public const string ProviderFailure = "providerFailure";
    public const string Cancelled = "cancelled";
    public const string Transport = "transport";
    public const string InputTooLong = "inputTooLong";
    public const string RequestBudgetExceeded = "requestBudgetExceeded";
    public const string IncompleteResponse = "incompleteResponse";
    public const string InvalidResponse = "invalidResponse";
    public const string Authentication = "authentication";
    public const string RateLimited = "rateLimited";
    public const string ServerUnavailable = "serverUnavailable";
    public const string InvalidConfiguration = "invalidConfiguration";
    public const string ModelUnavailable = "modelUnavailable";
    public const string UnsupportedLanguage = "unsupportedLanguage";
    public const string ResponseTooLarge = "responseTooLarge";
    public const string RequestRejected = "requestRejected";

    public const string UnknownMessage = "Cleanup did not complete. No detailed reason was recorded.";
    public const string UnknownOverlayTitle = "AI cleanup failed";

    private static readonly Dictionary<string, (string Message, string Title)> Table = new()
    {
        [Timeout] = ("Cleanup did not finish before its time limit.", "Cleanup timed out"),
        [UseRaw] = ("You chose to use the original transcript before cleanup finished.", "Raw transcript selected"),
        [ProviderFailure] = ("The cleanup provider failed without a recognized error.", "Cleanup failed"),
        [Cancelled] = ("Cleanup was cancelled before it finished.", "Cleanup cancelled"),
        [Transport] = ("Remy could not connect to the cleanup provider or the connection was interrupted.", "Cleanup failed"),
        [InputTooLong] = ("The transcript and cleanup instructions exceed the model's request limit.", "Cleanup input too long"),
        [RequestBudgetExceeded] = ("The transcript, instructions, and enabled context exceed Remy's cleanup request budget.", "Cleanup input too long"),
        [IncompleteResponse] = ("The cleanup provider returned an incomplete response.", "Cleanup failed"),
        [InvalidResponse] = ("The cleanup provider returned an empty or invalid response.", "Cleanup failed"),
        [Authentication] = ("The cleanup credential is missing, unavailable, or was rejected.", "Cleanup failed"),
        [RateLimited] = ("The cleanup provider is limiting requests. Try again later.", "Cleanup failed"),
        [ServerUnavailable] = ("The cleanup provider is temporarily unavailable.", "Cleanup failed"),
        [InvalidConfiguration] = ("The cleanup configuration or endpoint is invalid or unsupported.", "Cleanup failed"),
        [ModelUnavailable] = ("The selected cleanup model is unavailable or could not be loaded.", "Cleanup unavailable"),
        [UnsupportedLanguage] = ("The selected cleanup model does not support this dictation language.", "Cleanup unavailable"),
        [ResponseTooLarge] = ("The cleanup provider's response exceeded the allowed size.", "Cleanup failed"),
        [RequestRejected] = ("The cleanup provider rejected the request. Check the model and cleanup settings.", "Cleanup failed"),
    };

    public static IReadOnlyCollection<string> AllCategories => Table.Keys;

    public static RemyCleanupFailure Of(string category) =>
        Table.TryGetValue(category, out var e) ? new RemyCleanupFailure(category, e.Message) : new RemyCleanupFailure(category, UnknownMessage);

    /// <summary>User-facing message for a stored category string (unknown/legacy -> generic text).</summary>
    public static string MessageFor(string? category) =>
        category is not null && Table.TryGetValue(category, out var e) ? e.Message : UnknownMessage;

    public static string OverlayTitleFor(string? category) =>
        category is not null && Table.TryGetValue(category, out var e) ? e.Title : UnknownOverlayTitle;

    /// <summary>HTTP status table (cleanup.md §1.8). Returns null for 2xx.</summary>
    public static RemyCleanupFailure? FromHttpStatus(int status)
    {
        if (status >= 200 && status <= 299) return null;
        return Of(status switch
        {
            401 or 403 => Authentication,
            408 or 504 => Timeout,
            413 => InputTooLong,
            429 => RateLimited,
            >= 500 and <= 599 => ServerUnavailable,
            >= 400 and <= 499 => RequestRejected,
            _ => InvalidResponse,
        });
    }

    /// <summary>Maps any exception to a failure. First match wins; unknown -> providerFailure.</summary>
    public static RemyCleanupFailure Classify(Exception ex, bool deadlineReached = false)
    {
        switch (ex)
        {
            case CleanupFailureException cf: return cf.Failure;
            case OperationCanceledException:
                return Of(deadlineReached ? Timeout : Cancelled);
            case TimeoutException: return Of(Timeout);
            case HttpRequestException hre:
                return hre.StatusCode is { } sc && FromHttpStatus((int)sc) is { } f ? f : Of(Transport);
            case JsonException: return Of(InvalidResponse);
            case UriFormatException: return Of(InvalidConfiguration);
            case InvalidOperationException: return Of(ProviderFailure);
            default: return Of(ProviderFailure);
        }
    }
}

/// <summary>Carrier used to throw a classified failure inside the cleanup pipeline.</summary>
public sealed class CleanupFailureException(RemyCleanupFailure failure, Exception? inner = null)
    : Exception(failure.Message, inner)
{
    public RemyCleanupFailure Failure { get; } = failure;
    public CleanupFailureException(string category) : this(RemyCleanupFailure.Of(category)) { }
}
