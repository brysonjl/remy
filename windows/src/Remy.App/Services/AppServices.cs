using Remy.Core.Cleanup;
using Remy.Core.Contracts;
using Remy.Core.History;
using Remy.Core.Models;
using Remy.Core.Recovery;
using Remy.Core.Settings;

namespace Remy.App.Services;

/// <summary>
/// Every long-lived service, created once by the composition root and handed to pages and view models.
/// Pages read and write <see cref="Settings"/> directly (it saves itself); anything that touches a running
/// dictation goes through <see cref="Dictation"/>.
/// </summary>
public sealed class AppServices
{
    public required RemySettings Settings { get; init; }
    public required SettingsStore SettingsStore { get; init; }
    public required IDictationController Dictation { get; init; }

    public required ModelStore Models { get; init; }
    public required CleanupRouter CleanupRouter { get; init; }
    public required CleanupDiscovery CleanupDiscovery { get; init; }
    /// <summary>Null when profiles could not be loaded (show ProfileStrings unavailable text).</summary>
    public CleanupProfileController? Profiles { get; init; }
    public required ICredentialStore Credentials { get; init; }

    /// <summary>Current history repository. Can switch between SQLite and ephemeral when history is toggled.</summary>
    public required Func<IHistoryRepository> History { get; init; }
    public required RecoveryStore Recovery { get; init; }

    public required IAudioCapture Audio { get; init; }
    public required IHotkeyListener Hotkeys { get; init; }
    public required ISoundPlayer Sounds { get; init; }
    public required IAutostart Autostart { get; init; }
}
