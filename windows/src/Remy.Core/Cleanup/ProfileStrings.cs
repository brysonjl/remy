namespace Remy.Core.Cleanup;

/// <summary>Verbatim UI strings for cleanup profiles (cleanup.md §4.6).</summary>
public static class ProfileStrings
{
    public const string Header = "Cleanup profiles";
    public const string Subheader = "Give each service its own endpoint, model and instructions.";
    public const string PickerLabel = "Saved profile";
    public const string UnsavedProfileItem = "Unsaved profile";
    public const string New = "New";
    public const string Reload = "Reload";
    public const string SaveProfile = "Save profile";
    public const string TestConnection = "Test connection";
    public const string TestConnectionBusy = "Checking…";
    public const string TestConnectionTooltip = "Checks only the saved profile using a synthetic input. Save changes first.";
    public const string Delete = "Delete…";
    public const string DiscardNote = "Switching profiles, New and Reload discard unsaved changes.";

    public const string ProfileNameLabel = "Profile name";
    public const string DefaultName = "New profile";
    public const string ProtocolLabel = "Protocol";
    public const string ProtocolOllama = "Ollama";
    public const string ProtocolOpenAICompatible = "OpenAI-compatible";
    public const string BaseUrlLabel = "Base URL";
    public const string DefaultBaseUrl = "https://ollama.com/v1";
    public const string BaseUrlHelp = "Enter the exact service URL. Remy does not add /v1. Remote endpoints require HTTPS.";
    public const string ModelIdLabel = "Model ID";
    public const string ModelIdHelp = "Use the exact model identifier accepted by your service.";
    public const string ApiKeyConfigured = "API key: configured";
    public const string ApiKeyNotConfigured = "API key: not configured";
    public const string NewApiKeyLabel = "New API key (optional)";
    public const string NewApiKeyHelp = "Stored keys are never displayed. Leave this empty to preserve the current endpoint's key reference.";
    public const string RemoveKeyToggle = "Remove key from this profile on Save";
    public const string InstructionsLabel = "Cleanup instructions";
    public const string TimeoutLabel = "Request timeout";
    public static string TimeoutValue(int seconds) => $"{seconds} seconds";

    public const string RouteLoopback = "Local execution has not been verified. A server on this Mac may still use a cloud model.";
    public const string RouteLoopbackWindows = "Local execution has not been verified. A server on this PC may still use a cloud model.";
    public const string RouteRemote = "This server receives your transcript and vocabulary for cleanup.";
    public const string RouteUnverified = "This profile's execution location has not been verified.";
    public const string EndpointChangeNote = "Changing the endpoint clears its saved key reference. Enter a new key if required.";

    public const string Saved = "Profile saved.";
    public const string SaveFailed = "The profile could not be saved. Check its fields, or reload if it changed elsewhere.";
    public const string SaveUncertain = "The save could not be confirmed. Reload profiles before making another change.";
    public const string SelectFailed = "The profile could not be selected. Reload profiles and try again.";
    public const string DeleteFailed = "The profile could not be deleted. Reload profiles and try again.";
    public const string LoadFailed = "Profiles could not be loaded. No changes were saved.";
    public const string ReloadFailed = "Profiles could not be reloaded. Your draft has been kept.";
    public const string ConnectionPassed = "Connection check passed.";
    public const string ConnectionFailed = "Connection check failed. Check the saved endpoint, model and key.";
    public const string ConnectionFinishedSaveFirst = "The saved profile check finished. Save your changes before testing this draft.";
    public const string DeleteConfirmTitle = "Delete this cleanup profile?";
    public const string DeleteConfirmButton = "Delete profile";
    public const string DeleteCancelButton = "Cancel";
    public const string DeleteNote = "The profile will be removed. Its stored key is not deleted.";

    public const string ModeCaption = "Keep this setup as a named profile. Its endpoint, model, instructions and stored key are preserved.";
    public const string VocabularyFooter = "These hints are sent to the selected profile's server with your transcript.";
    public const string Unavailable = "Profiles are unavailable. Check that Remy's Application Support folder is writable, then reopen Remy.";
    public const string UnavailableWindows = "Profiles are unavailable. Check that Remy's data folder is writable, then reopen Remy.";
    public const string StartupError = "Cleanup profiles could not be loaded. Existing provider settings have been kept.";

    /// <summary>Message the settings UI should show for a failed save (cleanup.md §4.3).</summary>
    public static string SaveMessage(CleanupProfileError error) =>
        error == CleanupProfileError.StateUnknown ? SaveUncertain : SaveFailed;
}
