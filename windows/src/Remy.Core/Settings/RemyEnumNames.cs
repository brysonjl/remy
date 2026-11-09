using Remy.Core.Hotkeys;

namespace Remy.Core.Settings;

/// <summary>Bidirectional raw-value and display-name maps for a closed enum.</summary>
public sealed class EnumNameMap<T> where T : struct, Enum
{
    private readonly Dictionary<T, (string Raw, string Display)> _byValue = new();
    private readonly Dictionary<string, T> _byRaw = new(StringComparer.Ordinal);

    public EnumNameMap(params (T Value, string Raw, string Display)[] entries)
    {
        foreach (var (v, raw, display) in entries)
        {
            _byValue[v] = (raw, display);
            _byRaw[raw] = v;
        }
    }

    public string ToRaw(T value) => _byValue.TryGetValue(value, out var e) ? e.Raw : value.ToString();
    public string ToDisplay(T value) => _byValue.TryGetValue(value, out var e) ? e.Display : value.ToString();

    public bool TryParse(string? raw, out T value)
    {
        if (raw is not null && _byRaw.TryGetValue(raw, out value)) return true;
        value = default;
        return false;
    }

    public T ParseOrDefault(string? raw, T fallback) => TryParse(raw, out var v) ? v : fallback;
    public IEnumerable<T> Values => _byValue.Keys;
}

/// <summary>Raw (persisted) and display names for every shared enum (settings-models.md §1.3, §1.4).</summary>
public static class RemyEnumNames
{
    public static readonly EnumNameMap<AppAppearanceMode> AppAppearance = new(
        (AppAppearanceMode.MenuBarOnly, "menuBarOnly", "Tray only"),
        (AppAppearanceMode.DockAndMenuBar, "dockAndMenuBar", "Taskbar and tray"));

    public static readonly EnumNameMap<ThemeMode> Theme = new(
        (ThemeMode.System, "system", "System"),
        (ThemeMode.Light, "light", "Light"),
        (ThemeMode.Dark, "dark", "Dark"));

    public static readonly EnumNameMap<RemyDictationDestination> Destination = new(
        (RemyDictationDestination.OriginalField, "originalField", "Where dictation started"),
        (RemyDictationDestination.CurrentField, "currentField", "Where I’m focused when it finishes"));

    public static readonly EnumNameMap<OverlaySize> Overlay = new(
        (OverlaySize.Compact, "compact", "Compact"),
        (OverlaySize.Comfortable, "comfortable", "Comfortable"),
        (OverlaySize.Large, "large", "Large"));

    public static readonly EnumNameMap<TranscriptPostProcessingMode> PostProcessing = new(
        (TranscriptPostProcessingMode.None, "none", "Off"),
        (TranscriptPostProcessingMode.FluidAudioVocabulary, "fluidAudioVocabulary", "Vocabulary correction only"),
        (TranscriptPostProcessingMode.AppleIntelligence, "appleIntelligence", "Apple Intelligence"),
        (TranscriptPostProcessingMode.S1Mini, "s1Mini", "S1-mini by Superwhisper"),
        (TranscriptPostProcessingMode.Ollama, "ollama", "Ollama"),
        (TranscriptPostProcessingMode.OpenRouter, "openRouter", "OpenRouter"),
        (TranscriptPostProcessingMode.OpenAICompatible, "openAICompatible", "OpenAI Compatible"),
        (TranscriptPostProcessingMode.Profile, "profile", "Saved Profile"));

    public static readonly EnumNameMap<DictationWritingStyle> WritingStyle = new(
        (DictationWritingStyle.Formal, "formal", "Formal"),
        (DictationWritingStyle.Neutral, "neutral", "Neutral"),
        (DictationWritingStyle.Casual, "casual", "Casual"),
        (DictationWritingStyle.VeryCasual, "veryCasual", "Very Casual"),
        (DictationWritingStyle.Excited, "excited", "Excited"),
        (DictationWritingStyle.Original, "original", "Original tone"));

    public static readonly EnumNameMap<DictationContextCategory> ContextCategory = new(
        (DictationContextCategory.Email, "email", "Email"),
        (DictationContextCategory.WorkMessaging, "workMessaging", "Work messaging"),
        (DictationContextCategory.PersonalMessaging, "personalMessaging", "Personal messaging"),
        (DictationContextCategory.Terminal, "terminal", "Terminal"),
        (DictationContextCategory.TerminalAgent, "terminalAgent", "Terminal coding agent"),
        (DictationContextCategory.Browser, "browser", "Browser"),
        (DictationContextCategory.CodingAgent, "codingAgent", "Coding agent"),
        (DictationContextCategory.AiChat, "aiChat", "AI chat"),
        (DictationContextCategory.Other, "other", "Other"));

    public static readonly EnumNameMap<S1MiniStyling> S1Styling = new(
        (S1MiniStyling.Casual, "casual", "Casual"),
        (S1MiniStyling.SemiCasual, "semi-casual", "Semi-Casual"),
        (S1MiniStyling.SemiFormal, "semi-formal", "Semi-Formal"),
        (S1MiniStyling.Formal, "formal", "Formal"));

    public static readonly EnumNameMap<S1MiniStructure> S1Structure = new(
        (S1MiniStructure.Prose, "prose", "Prose"),
        (S1MiniStructure.Lists, "lists", "Lists"));

    public static readonly EnumNameMap<S1MiniContextSetting> S1Context = new(
        (S1MiniContextSetting.Automatic, "automatic", "Automatic"),
        (S1MiniContextSetting.General, "general", "General"),
        (S1MiniContextSetting.Email, "email", "Email"));

    public static readonly EnumNameMap<CleanupProfileAdapter> ProfileAdapter = new(
        (CleanupProfileAdapter.Ollama, "ollama", "Ollama"),
        (CleanupProfileAdapter.OpenAICompatible, "openAICompatible", "OpenAI Compatible"));

    public static readonly EnumNameMap<HotkeyMode> HotkeyModes = new(
        (HotkeyMode.HoldToRecord, "holdToRecord", "Hold to Record"),
        (HotkeyMode.HandsFreeToggle, "handsFreeToggle", "Tap to Toggle"));

    // ToRaw/DisplayName extensions for DictationWritingStyle, DictationContextCategory, S1MiniStyling and S1MiniStructure
    // live in Remy.Core.Context.DictationText (to avoid ambiguity); use the maps above for any enum.
    public static string ToRaw(this AppAppearanceMode v) => AppAppearance.ToRaw(v);
    public static string ToRaw(this ThemeMode v) => Theme.ToRaw(v);
    public static string ToRaw(this RemyDictationDestination v) => Destination.ToRaw(v);
    public static string ToRaw(this OverlaySize v) => Overlay.ToRaw(v);
    public static string ToRaw(this TranscriptPostProcessingMode v) => PostProcessing.ToRaw(v);
    public static string ToRaw(this S1MiniContextSetting v) => S1Context.ToRaw(v);
    public static string ToRaw(this CleanupProfileAdapter v) => ProfileAdapter.ToRaw(v);
    public static string ToRaw(this HotkeyMode v) => HotkeyModes.ToRaw(v);

    public static string DisplayName(this AppAppearanceMode v) => AppAppearance.ToDisplay(v);
    public static string DisplayName(this ThemeMode v) => Theme.ToDisplay(v);
    public static string DisplayName(this RemyDictationDestination v) => Destination.ToDisplay(v);
    public static string DisplayName(this OverlaySize v) => Overlay.ToDisplay(v);
    public static string DisplayName(this TranscriptPostProcessingMode v) => PostProcessing.ToDisplay(v);
    public static string DisplayName(this S1MiniContextSetting v) => S1Context.ToDisplay(v);
    public static string DisplayName(this CleanupProfileAdapter v) => ProfileAdapter.ToDisplay(v);
    public static string DisplayName(this HotkeyMode v) => HotkeyModes.ToDisplay(v);

    /// <summary>Modes this Windows build can run; others coerce to None on load/set.</summary>
    public static bool IsSupportedOnWindows(this TranscriptPostProcessingMode m) =>
        m is not (TranscriptPostProcessingMode.FluidAudioVocabulary or TranscriptPostProcessingMode.AppleIntelligence);

    /// <summary>Overlay pill widths (settings-models.md §1.3).</summary>
    public static int Width(this OverlaySize s) => s switch { OverlaySize.Compact => 169, OverlaySize.Large => 460, _ => 340 };

    /// <summary>Style choices offered per category (settings-models.md §1.3).</summary>
    public static IReadOnlyList<DictationWritingStyle> StyleOptions(this DictationContextCategory c) =>
        c == DictationContextCategory.PersonalMessaging
            ? new[] { DictationWritingStyle.Formal, DictationWritingStyle.Neutral, DictationWritingStyle.Casual, DictationWritingStyle.VeryCasual, DictationWritingStyle.Original }
            : new[] { DictationWritingStyle.Formal, DictationWritingStyle.Neutral, DictationWritingStyle.Casual, DictationWritingStyle.Excited, DictationWritingStyle.Original };
}
