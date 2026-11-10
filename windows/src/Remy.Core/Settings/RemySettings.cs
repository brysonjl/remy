using System.ComponentModel;
using System.Runtime.CompilerServices;
using Remy.Core.Cleanup;
using Remy.Core.Context;
using Remy.Core.Hotkeys;

namespace Remy.Core.Settings;

/// <summary>Every persisted setting that applies to Windows (settings-models.md 1.3). Property name = Mac key in
/// PascalCase. Defaults per the spec plus the Windows decisions in ARCHITECTURE.md. Not thread-safe: mutate on the
/// UI/app thread; <see cref="SettingsStore"/> serializes saves. JSON mapping lives in RemySettingsJson.cs.</summary>
public sealed partial class RemySettings : INotifyPropertyChanged
{
    public const string DefaultOllamaBaseUrl = "http://127.0.0.1:11434";
    public const string DefaultOpenAICompatibleBaseUrl = "http://127.0.0.1:1234/v1";
    public const string DefaultStartSound = "builtin:Speech On";
    public const string DefaultCompletionSound = "builtin:Speech Off";
    public const float DefaultSoundEffectsVolume = 0.3f;

    public static readonly string[] DefaultFillerWords = { "um", "uh", "erm", "er", "hmm", "嗯", "呃" };

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>While true, property changes are not raised (used during load).</summary>
    internal bool SuppressNotifications { get; set; }

    private void Raise(string? name)
    {
        if (!SuppressNotifications) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private bool SetList(ref IReadOnlyList<string> field, IReadOnlyList<string>? value, [CallerMemberName] string? name = null)
    {
        var v = (value ?? Array.Empty<string>()).ToArray();
        if (field.SequenceEqual(v)) return false;
        field = v;
        Raise(name);
        return true;
    }

    // ---------------------------------------------------------------- General

    private bool _launchAtLogin;
    public bool LaunchAtLogin { get => _launchAtLogin; set => Set(ref _launchAtLogin, value); }

    private AppAppearanceMode _appAppearanceMode = AppAppearanceMode.MenuBarOnly;
    public AppAppearanceMode AppAppearanceMode { get => _appAppearanceMode; set => Set(ref _appAppearanceMode, value); }

    private bool _prewarmEnginesAtStartup = true;
    public bool PrewarmEnginesAtStartup { get => _prewarmEnginesAtStartup; set => Set(ref _prewarmEnginesAtStartup, value); }

    private ThemeMode _themeMode = ThemeMode.System;
    public ThemeMode ThemeMode { get => _themeMode; set => Set(ref _themeMode, value); }

    private RemyDictationDestination _remyDictationDestination = RemyDictationDestination.OriginalField;
    public RemyDictationDestination RemyDictationDestination { get => _remyDictationDestination; set => Set(ref _remyDictationDestination, value); }

    private string _selectedLanguage = "en";
    public string SelectedLanguage { get => _selectedLanguage; set => Set(ref _selectedLanguage, string.IsNullOrWhiteSpace(value) ? "en" : value); }

    private string? _selectedMicrophoneUID;
    /// <summary>MMDevice endpoint id; null = follow the system default.</summary>
    public string? SelectedMicrophoneUID { get => _selectedMicrophoneUID; set => Set(ref _selectedMicrophoneUID, string.IsNullOrWhiteSpace(value) ? null : value); }

    private bool _boostMicrophoneVolumeEnabled = true;
    public bool BoostMicrophoneVolumeEnabled { get => _boostMicrophoneVolumeEnabled; set => Set(ref _boostMicrophoneVolumeEnabled, value); }

    private bool _muteSystemAudioDuringRecordingEnabled;
    public bool MuteSystemAudioDuringRecordingEnabled { get => _muteSystemAudioDuringRecordingEnabled; set => Set(ref _muteSystemAudioDuringRecordingEnabled, value); }

    private bool _soundEffectsEnabled = true;
    public bool SoundEffectsEnabled { get => _soundEffectsEnabled; set => Set(ref _soundEffectsEnabled, value); }

    private float _soundEffectsVolume = DefaultSoundEffectsVolume;
    /// <summary>0..1; non-finite or out-of-range values are clamped / reset.</summary>
    public float SoundEffectsVolume
    {
        get => _soundEffectsVolume;
        set => Set(ref _soundEffectsVolume, float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : DefaultSoundEffectsVolume);
    }

    private string _remyDictationStartSound = DefaultStartSound;
    public string RemyDictationStartSound { get => _remyDictationStartSound; set => Set(ref _remyDictationStartSound, string.IsNullOrWhiteSpace(value) ? DefaultStartSound : value); }

    private string _remyDictationCompletionSound = DefaultCompletionSound;
    public string RemyDictationCompletionSound { get => _remyDictationCompletionSound; set => Set(ref _remyDictationCompletionSound, string.IsNullOrWhiteSpace(value) ? DefaultCompletionSound : value); }

    private bool _autoStopAfterSpeechEndsEnabled;
    public bool AutoStopAfterSpeechEndsEnabled { get => _autoStopAfterSpeechEndsEnabled; set => Set(ref _autoStopAfterSpeechEndsEnabled, value); }

    // ---------------------------------------------------------------- Shortcuts

    private IReadOnlyList<HotkeyBinding> _hotkeyBindings = new[] { HotkeyDefaults.DefaultRecording() };
    /// <summary>1..5 recording bindings (canonicalized). Setting an empty list restores one empty ("Not set") binding.</summary>
    public IReadOnlyList<HotkeyBinding> HotkeyBindings
    {
        get => _hotkeyBindings;
        set
        {
            var v = (value ?? Array.Empty<HotkeyBinding>()).Take(HotkeyDefaults.MaxRecordingBindings).Select(HotkeyRules.Canonicalize).ToList();
            if (v.Count == 0) v.Add(HotkeyBinding.Empty());
            if (_hotkeyBindings.SequenceEqual(v)) return;
            _hotkeyBindings = v;
            Raise(nameof(HotkeyBindings));
        }
    }

    private HotkeyBinding _cancelShortcut = HotkeyDefaults.DefaultCancel();
    /// <summary>Cancel shortcut; a cleared shortcut is an empty binding (HasBinding == false).</summary>
    public HotkeyBinding CancelShortcut
    {
        get => _cancelShortcut;
        set => Set(ref _cancelShortcut, HotkeyRules.Canonicalize(value ?? HotkeyBinding.Empty(mode: HotkeyMode.HandsFreeToggle)));
    }

    private bool _holdToCancel = true;
    public bool HoldToCancel { get => _holdToCancel; set => Set(ref _holdToCancel, value); }

    private bool _preserveCancelledSessions = true;
    public bool PreserveCancelledSessions { get => _preserveCancelledSessions; set => Set(ref _preserveCancelledSessions, value); }

    // ---------------------------------------------------------------- Text & overlay

    private bool _showTextPreview = true;
    public bool ShowTextPreview { get => _showTextPreview; set => Set(ref _showTextPreview, value); }

    private OverlaySize _overlaySize = OverlaySize.Comfortable;
    public OverlaySize OverlaySize { get => _overlaySize; set => Set(ref _overlaySize, value); }

    private ThemeMode _overlayThemeMode = ThemeMode.System;
    public ThemeMode OverlayThemeMode { get => _overlayThemeMode; set => Set(ref _overlayThemeMode, value); }

    // ---------------------------------------------------------------- Transcript cleanup

    private TranscriptPostProcessingMode _transcriptPostProcessingMode = TranscriptPostProcessingMode.None;
    /// <summary>FluidAudioVocabulary and AppleIntelligence are Mac-only and coerce to None.</summary>
    public TranscriptPostProcessingMode TranscriptPostProcessingMode
    {
        get => _transcriptPostProcessingMode;
        set => Set(ref _transcriptPostProcessingMode, value.IsSupportedOnWindows() ? value : TranscriptPostProcessingMode.None);
    }

    private string _ollamaPostProcessingPrompt = CleanupPrompts.RecommendedTranscriptCleanupPrompt;
    public string OllamaPostProcessingPrompt { get => _ollamaPostProcessingPrompt; set => Set(ref _ollamaPostProcessingPrompt, OrDefaultPrompt(value)); }

    private string _openRouterPostProcessingPrompt = CleanupPrompts.RecommendedTranscriptCleanupPrompt;
    public string OpenRouterPostProcessingPrompt { get => _openRouterPostProcessingPrompt; set => Set(ref _openRouterPostProcessingPrompt, OrDefaultPrompt(value)); }

    private string _openAICompatiblePostProcessingPrompt = CleanupPrompts.RecommendedTranscriptCleanupPrompt;
    public string OpenAICompatiblePostProcessingPrompt { get => _openAICompatiblePostProcessingPrompt; set => Set(ref _openAICompatiblePostProcessingPrompt, OrDefaultPrompt(value)); }

    private static string OrDefaultPrompt(string? v) => string.IsNullOrWhiteSpace(v) ? CleanupPrompts.RecommendedTranscriptCleanupPrompt : v;

    private string _ollamaBaseURL = DefaultOllamaBaseUrl;
    public string OllamaBaseURL { get => _ollamaBaseURL; set => Set(ref _ollamaBaseURL, value ?? ""); }

    private string _ollamaModel = "";
    public string OllamaModel { get => _ollamaModel; set => Set(ref _ollamaModel, value ?? ""); }

    private string _openRouterModel = "";
    public string OpenRouterModel { get => _openRouterModel; set => Set(ref _openRouterModel, value ?? ""); }

    private string _openAICompatibleModel = "";
    public string OpenAICompatibleModel { get => _openAICompatibleModel; set => Set(ref _openAICompatibleModel, value ?? ""); }

    private bool _ollamaReasoningEnabled = true;
    public bool OllamaReasoningEnabled { get => _ollamaReasoningEnabled; set => Set(ref _ollamaReasoningEnabled, value); }

    private bool _openRouterReasoningEnabled = true;
    public bool OpenRouterReasoningEnabled { get => _openRouterReasoningEnabled; set => Set(ref _openRouterReasoningEnabled, value); }

    private string _openAICompatibleBaseURL = DefaultOpenAICompatibleBaseUrl;
    public string OpenAICompatibleBaseURL { get => _openAICompatibleBaseURL; set => Set(ref _openAICompatibleBaseURL, value ?? ""); }

    private string _openRouterAPIKeyEnvironmentVariable = "";
    /// <summary>Name of an environment variable holding the OpenRouter key.</summary>
    public string OpenRouterAPIKeyEnvironmentVariable { get => _openRouterAPIKeyEnvironmentVariable; set => Set(ref _openRouterAPIKeyEnvironmentVariable, value ?? ""); }

    private IReadOnlyList<string> _customVocabulary = Array.Empty<string>();
    public IReadOnlyList<string> CustomVocabulary { get => _customVocabulary; set { if (SetList(ref _customVocabulary, value)) OnCustomVocabularyChanged(); } }

    /// <summary>Hook for v0.2 text rules (prunes corrections of removed terms).</summary>
    partial void OnCustomVocabularyChanged();

    private bool _isFillerWordRemovalEnabled;
    public bool IsFillerWordRemovalEnabled { get => _isFillerWordRemovalEnabled; set => Set(ref _isFillerWordRemovalEnabled, value); }

    private IReadOnlyList<string> _fillerWordsToRemove = DefaultFillerWords;
    public IReadOnlyList<string> FillerWordsToRemove { get => _fillerWordsToRemove; set => SetList(ref _fillerWordsToRemove, value); }

    private bool _dictationContextAwarenessEnabled = true;
    public bool DictationContextAwarenessEnabled { get => _dictationContextAwarenessEnabled; set => Set(ref _dictationContextAwarenessEnabled, value); }

    private bool _spellNamesFromActiveWindowEnabled;
    /// <summary>Spell names from the active window (v0.2-features.md §7). Off by default.</summary>
    public bool SpellNamesFromActiveWindowEnabled { get => _spellNamesFromActiveWindowEnabled; set => Set(ref _spellNamesFromActiveWindowEnabled, value); }

    private bool _shareDictationContextWithRemoteProviders;
    public bool ShareDictationContextWithRemoteProviders { get => _shareDictationContextWithRemoteProviders; set => Set(ref _shareDictationContextWithRemoteProviders, value); }

    // ---- writing styles

    private DictationWritingStyle _emailDictationWritingStyle = DictationWritingStyle.Formal;
    public DictationWritingStyle EmailDictationWritingStyle
    {
        get => _emailDictationWritingStyle;
        set => Set(ref _emailDictationWritingStyle, SanitizeStyle(DictationContextCategory.Email, value));
    }

    private DictationWritingStyle _workMessagingDictationWritingStyle = DictationWritingStyle.Formal;
    public DictationWritingStyle WorkMessagingDictationWritingStyle
    {
        get => _workMessagingDictationWritingStyle;
        set => Set(ref _workMessagingDictationWritingStyle, SanitizeStyle(DictationContextCategory.WorkMessaging, value));
    }

    private DictationWritingStyle _personalMessagingDictationWritingStyle = DictationWritingStyle.Casual;
    public DictationWritingStyle PersonalMessagingDictationWritingStyle
    {
        get => _personalMessagingDictationWritingStyle;
        set => Set(ref _personalMessagingDictationWritingStyle, SanitizeStyle(DictationContextCategory.PersonalMessaging, value));
    }

    private DictationWritingStyle _otherDictationWritingStyle = DictationWritingStyle.Formal;
    public DictationWritingStyle OtherDictationWritingStyle
    {
        get => _otherDictationWritingStyle;
        set => Set(ref _otherDictationWritingStyle, SanitizeStyle(DictationContextCategory.Other, value));
    }

    private IReadOnlyDictionary<DictationContextCategory, DictationWritingStyle> _extendedDictationWritingStyles =
        new Dictionary<DictationContextCategory, DictationWritingStyle>();

    /// <summary>Explicitly stored styles for terminal, terminalAgent, browser, codingAgent, aiChat. Missing entries use
    /// <see cref="DefaultStyle"/>. Prefer <see cref="GetWritingStyle"/>/<see cref="SetWritingStyle"/>.</summary>
    public IReadOnlyDictionary<DictationContextCategory, DictationWritingStyle> ExtendedDictationWritingStyles
    {
        get => _extendedDictationWritingStyles;
        set
        {
            var d = new Dictionary<DictationContextCategory, DictationWritingStyle>();
            foreach (var (cat, style) in value ?? new Dictionary<DictationContextCategory, DictationWritingStyle>())
                if (IsExtendedCategory(cat)) d[cat] = SanitizeStyle(cat, style);
            if (_extendedDictationWritingStyles.Count == d.Count && d.All(kv => _extendedDictationWritingStyles.TryGetValue(kv.Key, out var s) && s == kv.Value)) return;
            _extendedDictationWritingStyles = d;
            Raise(nameof(ExtendedDictationWritingStyles));
        }
    }

    public static bool IsExtendedCategory(DictationContextCategory c) =>
        c is DictationContextCategory.Terminal or DictationContextCategory.TerminalAgent or DictationContextCategory.Browser
            or DictationContextCategory.CodingAgent or DictationContextCategory.AiChat;

    /// <summary>Per-category default (settings-models.md 1.3): email/work formal, personal casual, other formal,
    /// browser neutral, terminal/terminalAgent/codingAgent/aiChat original.</summary>
    public static DictationWritingStyle DefaultStyle(DictationContextCategory c) => c switch
    {
        DictationContextCategory.Email => DictationWritingStyle.Formal,
        DictationContextCategory.WorkMessaging => DictationWritingStyle.Formal,
        DictationContextCategory.PersonalMessaging => DictationWritingStyle.Casual,
        DictationContextCategory.Other => DictationWritingStyle.Formal,
        DictationContextCategory.Browser => DictationWritingStyle.Neutral,
        _ => DictationWritingStyle.Original,
    };

    /// <summary>A style outside the category's option set becomes Casual.</summary>
    public static DictationWritingStyle SanitizeStyle(DictationContextCategory c, DictationWritingStyle s) =>
        c.StyleOptions().Contains(s) ? s : DictationWritingStyle.Casual;

    public DictationWritingStyle GetWritingStyle(DictationContextCategory category) => category switch
    {
        DictationContextCategory.Email => EmailDictationWritingStyle,
        DictationContextCategory.WorkMessaging => WorkMessagingDictationWritingStyle,
        DictationContextCategory.PersonalMessaging => PersonalMessagingDictationWritingStyle,
        DictationContextCategory.Other => OtherDictationWritingStyle,
        _ => _extendedDictationWritingStyles.TryGetValue(category, out var s) ? SanitizeStyle(category, s) : DefaultStyle(category),
    };

    public void SetWritingStyle(DictationContextCategory category, DictationWritingStyle style)
    {
        switch (category)
        {
            case DictationContextCategory.Email: EmailDictationWritingStyle = style; break;
            case DictationContextCategory.WorkMessaging: WorkMessagingDictationWritingStyle = style; break;
            case DictationContextCategory.PersonalMessaging: PersonalMessagingDictationWritingStyle = style; break;
            case DictationContextCategory.Other: OtherDictationWritingStyle = style; break;
            default:
                var d = new Dictionary<DictationContextCategory, DictationWritingStyle>(_extendedDictationWritingStyles) { [category] = style };
                ExtendedDictationWritingStyles = d;
                break;
        }
    }

    /// <summary>Effective style for every category (for the frozen context policy).</summary>
    public IReadOnlyDictionary<DictationContextCategory, DictationWritingStyle> WritingStylesSnapshot() =>
        Enum.GetValues<DictationContextCategory>().ToDictionary(c => c, GetWritingStyle);

    private IReadOnlyList<DictationAppRule> _dictationAppRules = Array.Empty<DictationAppRule>();
    public IReadOnlyList<DictationAppRule> DictationAppRules
    {
        get => _dictationAppRules;
        set
        {
            var v = (value ?? Array.Empty<DictationAppRule>()).ToArray();
            if (_dictationAppRules.SequenceEqual(v)) return;
            _dictationAppRules = v;
            Raise(nameof(DictationAppRules));
        }
    }

    // ---- S1-mini

    private S1MiniStyling _s1MiniStyling = S1MiniStyling.SemiFormal;
    public S1MiniStyling S1MiniStyling { get => _s1MiniStyling; set => Set(ref _s1MiniStyling, value); }

    private S1MiniAppStyling _s1MiniAppStyling = S1MiniAppStyling.Recommended;
    public S1MiniAppStyling S1MiniAppStyling
    {
        get => _s1MiniAppStyling;
        set
        {
            value ??= S1MiniAppStyling.Recommended;
            var o = _s1MiniAppStyling;
            if (o.Email == value.Email && o.WorkMessaging == value.WorkMessaging && o.PersonalMessaging == value.PersonalMessaging
                && o.Other == value.Other && SameExtended(o, value)) return;
            _s1MiniAppStyling = value;
            Raise(nameof(S1MiniAppStyling));
        }
    }

    private static bool SameExtended(S1MiniAppStyling a, S1MiniAppStyling b)
    {
        var x = a.ExtendedStyles; var y = b.ExtendedStyles;
        if (x is null || y is null) return x is null && y is null;
        return x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out var s) && s == kv.Value);
    }

    private S1MiniStructure _s1MiniStructure = S1MiniStructure.Prose;
    public S1MiniStructure S1MiniStructure { get => _s1MiniStructure; set => Set(ref _s1MiniStructure, value); }

    private S1MiniContextSetting _s1MiniContextSetting = S1MiniContextSetting.Automatic;
    public S1MiniContextSetting S1MiniContextSetting { get => _s1MiniContextSetting; set => Set(ref _s1MiniContextSetting, value); }

    // ---------------------------------------------------------------- History

    private bool _remyHistoryEnabled = true;
    public bool RemyHistoryEnabled { get => _remyHistoryEnabled; set => Set(ref _remyHistoryEnabled, value); }
}
