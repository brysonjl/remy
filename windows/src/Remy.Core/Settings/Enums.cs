// Shared enums. Names mirror the Mac raw values (settings-models.md §1.3-1.4); JSON stores the raw value
// returned by ToRaw()/parsed by the matching TryParse helpers in RemyEnumNames. Owned by the lead.

namespace Remy.Core.Settings;

public enum AppAppearanceMode { MenuBarOnly, DockAndMenuBar }            // "menuBarOnly", "dockAndMenuBar"
public enum ThemeMode { System, Light, Dark }                             // "system", "light", "dark"
public enum RemyDictationDestination { OriginalField, CurrentField }      // "originalField", "currentField"
public enum OverlaySize { Compact, Comfortable, Large }                   // "compact", "comfortable", "large"

/// <summary>Cleanup method. Windows supports None, S1Mini, Ollama, OpenRouter, OpenAICompatible, Profile.
/// FluidAudioVocabulary and AppleIntelligence are Mac-only: if loaded from a settings file they coerce to None.</summary>
public enum TranscriptPostProcessingMode
{
    None,                 // "none"                  "Off"
    FluidAudioVocabulary, // "fluidAudioVocabulary"  Mac-only
    AppleIntelligence,    // "appleIntelligence"     Mac-only
    S1Mini,               // "s1Mini"                "S1-mini by Superwhisper"
    Ollama,               // "ollama"                "Ollama"
    OpenRouter,           // "openRouter"            "OpenRouter"
    OpenAICompatible,     // "openAICompatible"      "OpenAI Compatible"
    Profile,              // "profile"               "Saved Profile"
}

public enum DictationWritingStyle { Formal, Neutral, Casual, VeryCasual, Excited, Original } // "formal" ... "veryCasual" ... "original"

public enum DictationContextCategory
{
    Email,             // "email"             "Email"
    WorkMessaging,     // "workMessaging"     "Work messaging"
    PersonalMessaging, // "personalMessaging" "Personal messaging"
    Terminal,          // "terminal"          "Terminal"
    TerminalAgent,     // "terminalAgent"     "Terminal coding agent"
    Browser,           // "browser"           "Browser"
    CodingAgent,       // "codingAgent"       "Coding agent"
    AiChat,            // "aiChat"            "AI chat"
    Other,             // "other"             "Other"
}

public enum S1MiniStyling { Casual, SemiCasual, SemiFormal, Formal }     // "casual", "semi-casual", "semi-formal", "formal"
public enum S1MiniStructure { Prose, Lists }                              // "prose", "lists"
public enum S1MiniContextSetting { Automatic, General, Email }            // "automatic", "general", "email"

public enum CleanupProfileAdapter { Ollama, OpenAICompatible }            // "ollama", "openAICompatible"
