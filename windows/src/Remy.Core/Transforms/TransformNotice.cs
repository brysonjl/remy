using Remy.Core.Cleanup;

namespace Remy.Core.Transforms;

public enum TransformNoticeKind
{
    CleanupOff, ProviderUnsupported, ProviderUnavailable, InsecureEndpoint, MissingCredential, InvalidProfile,
    AccessibilityRequired, NoSelection, ProtectedField, SelectionUnreadable, SelectionTooLong, EmptyPrompt,
    DictationBusy, AlreadyRunning, ProviderFailed, EmptyResult, Refusal, FocusChanged, DeliveryFailed,
}

/// <summary>Every user-visible Transform result (v0.2-features.md §8.8). Never carries provider bodies, endpoints or selected text.</summary>
public sealed record TransformNotice(TransformNoticeKind Kind, string? Name = null, int Words = 0, string? FailureCategory = null)
{
    const string ChooseProvider = "Choose a provider in Transcript Cleanup.";
    const string NotChanged = "Your text was not changed.";
    const string TryLater = "Try again when it finishes.";

    public static TransformNotice CleanupOff { get; } = new(TransformNoticeKind.CleanupOff);
    public static TransformNotice ProviderUnavailable { get; } = new(TransformNoticeKind.ProviderUnavailable);
    public static TransformNotice InsecureEndpoint { get; } = new(TransformNoticeKind.InsecureEndpoint);
    public static TransformNotice MissingCredential { get; } = new(TransformNoticeKind.MissingCredential);
    public static TransformNotice InvalidProfile { get; } = new(TransformNoticeKind.InvalidProfile);
    public static TransformNotice NoSelection { get; } = new(TransformNoticeKind.NoSelection);
    public static TransformNotice ProtectedField { get; } = new(TransformNoticeKind.ProtectedField);
    public static TransformNotice SelectionUnreadable { get; } = new(TransformNoticeKind.SelectionUnreadable);
    public static TransformNotice EmptyPrompt { get; } = new(TransformNoticeKind.EmptyPrompt);
    public static TransformNotice DictationBusy { get; } = new(TransformNoticeKind.DictationBusy);
    public static TransformNotice AlreadyRunning { get; } = new(TransformNoticeKind.AlreadyRunning);
    public static TransformNotice EmptyResult { get; } = new(TransformNoticeKind.EmptyResult);
    public static TransformNotice Refusal { get; } = new(TransformNoticeKind.Refusal);
    public static TransformNotice FocusChanged { get; } = new(TransformNoticeKind.FocusChanged);
    public static TransformNotice DeliveryFailed { get; } = new(TransformNoticeKind.DeliveryFailed);

    public static TransformNotice ProviderUnsupported(string name) => new(TransformNoticeKind.ProviderUnsupported, name);
    public static TransformNotice SelectionTooLong(int words) => new(TransformNoticeKind.SelectionTooLong, Words: words);
    public static TransformNotice ProviderFailed(string failureCategory) => new(TransformNoticeKind.ProviderFailed, FailureCategory: failureCategory);

    public string Title => Kind switch
    {
        TransformNoticeKind.CleanupOff => "Transforms need an AI provider",
        TransformNoticeKind.ProviderUnsupported => $"{Name} can't run transforms",
        TransformNoticeKind.ProviderUnavailable => "AI provider unavailable",
        TransformNoticeKind.InsecureEndpoint => "Transforms need HTTPS",
        TransformNoticeKind.MissingCredential => "Provider key missing",
        TransformNoticeKind.InvalidProfile => "Saved profile unavailable",
        TransformNoticeKind.AccessibilityRequired => "Accessibility access needed",
        TransformNoticeKind.NoSelection => "Select text to transform",
        TransformNoticeKind.ProtectedField => "Transforms are off here",
        TransformNoticeKind.SelectionUnreadable => "Couldn't read the selection",
        TransformNoticeKind.SelectionTooLong => $"Selection too long ({TransformLimits.Group(Words)} words)",
        TransformNoticeKind.EmptyPrompt => "Transform has no instructions",
        TransformNoticeKind.DictationBusy => "Finish dictating first",
        TransformNoticeKind.AlreadyRunning => "Already transforming",
        TransformNoticeKind.ProviderFailed => FailureCategory switch
        {
            RemyCleanupFailure.Timeout => "Transform timed out",
            RemyCleanupFailure.InputTooLong or RemyCleanupFailure.RequestBudgetExceeded => "Too long for this model",
            RemyCleanupFailure.Authentication => "Provider rejected the key",
            RemyCleanupFailure.RateLimited => "Provider is rate limiting",
            RemyCleanupFailure.ModelUnavailable => "Model unavailable",
            _ => "Transform failed",
        },
        TransformNoticeKind.EmptyResult => "Transform returned nothing",
        TransformNoticeKind.Refusal => "The model declined",
        TransformNoticeKind.FocusChanged => "Focus changed",
        TransformNoticeKind.DeliveryFailed => "Couldn't paste the result",
        _ => "",
    };

    public string Detail => Kind switch
    {
        TransformNoticeKind.CleanupOff or TransformNoticeKind.ProviderUnsupported or TransformNoticeKind.ProviderUnavailable
            or TransformNoticeKind.InvalidProfile => ChooseProvider,
        TransformNoticeKind.InsecureEndpoint => "Use https:// or a local endpoint in Transcript Cleanup.",
        TransformNoticeKind.MissingCredential => "Add the API key in Transcript Cleanup.",
        TransformNoticeKind.AccessibilityRequired => "Allow Remy in Privacy & Security.",
        TransformNoticeKind.NoSelection => "Highlight 1–3,000 words, then try again.",
        TransformNoticeKind.ProtectedField => "Password and excluded apps aren't read.",
        TransformNoticeKind.SelectionUnreadable => "This app doesn't share its selection.",
        TransformNoticeKind.SelectionTooLong => "Transforms accept up to 3,000 words.",
        TransformNoticeKind.EmptyPrompt => "Add instructions on the Transforms page.",
        TransformNoticeKind.DictationBusy or TransformNoticeKind.AlreadyRunning => TryLater,
        TransformNoticeKind.ProviderFailed or TransformNoticeKind.EmptyResult or TransformNoticeKind.Refusal
            or TransformNoticeKind.DeliveryFailed => NotChanged,
        TransformNoticeKind.FocusChanged => "Result copied · Press Ctrl+V",
        _ => "",
    };
}

/// <summary>Carrier for a notice raised inside the transform pipeline (route, completion, output guard).</summary>
public sealed class TransformException(TransformNotice notice) : Exception(notice.Title)
{
    public TransformNotice Notice { get; } = notice;
}
