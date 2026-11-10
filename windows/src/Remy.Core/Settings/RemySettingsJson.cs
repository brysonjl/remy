using System.Text.Json.Nodes;
using Remy.Core.Cleanup;
using Remy.Core.Context;
using Remy.Core.Hotkeys;

namespace Remy.Core.Settings;

/// <summary>Tolerant JSON mapping: camelCase Mac key names, enums as raw strings. A bad/missing/wrong-typed value
/// leaves that one property at its default; nothing here throws for bad data.</summary>
public sealed partial class RemySettings
{
    public const string SchemaVersionKey = "remy.configurationSchemaVersion";
    public const int CurrentSchemaVersion = 1;

    private sealed record Entry(string Key, Action<JsonNode?> Read, Func<JsonNode?> Write);

    private List<Entry>? _entries;
    private List<Entry> Entries => _entries ??= BuildEntries();

    /// <summary>All persisted keys (excluding the schema marker).</summary>
    public static IReadOnlyList<string> PersistedKeys => new RemySettings().Entries.Select(e => e.Key).ToArray();

    /// <summary>Adds typed entries. Feature partial files receive one through the hooks at the end of BuildEntries.</summary>
    private sealed class EntryBuilder(List<Entry> l)
    {
        public void Bool(string key, Func<bool> get, Action<bool> set) => l.Add(new(key,
            n => { if (n is JsonValue v && v.TryGetValue<bool>(out var b)) set(b); }, () => get()));

        public void Str(string key, Func<string?> get, Action<string> set, bool omitWhenNull = false) => l.Add(new(key,
            n => { if (n is JsonValue v && v.TryGetValue<string>(out var s) && s is not null) set(s); },
            () => get() is { } s ? s : (omitWhenNull ? null : "")));

        public void Float(string key, Func<float> get, Action<float> set) => l.Add(new(key,
            n => { if (n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d)) set((float)d); }, () => get()));

        public void Enum<T>(string key, EnumNameMap<T> map, Func<T> get, Action<T> set) where T : struct, System.Enum => l.Add(new(key,
            n => { if (n is JsonValue v && v.TryGetValue<string>(out var s) && map.TryParse(s, out var e)) set(e); },
            () => map.ToRaw(get())));

        public void StrList(string key, Func<IReadOnlyList<string>> get, Action<IReadOnlyList<string>> set) => l.Add(new(key,
            n =>
            {
                if (n is not JsonArray a) return;
                var list = new List<string>();
                foreach (var item in a)
                    if (item is JsonValue v && v.TryGetValue<string>(out var s) && s is not null) list.Add(s);
                set(list);
            },
            () => { var a = new JsonArray(); foreach (var s in get()) a.Add(s); return a; }));

        /// <summary>Raw JSON entry: read gets the stored node (ignore what you can't parse), write returns the node to store (null omits the key).</summary>
        public void Raw(string key, Action<JsonNode?> read, Func<JsonNode?> write) => l.Add(new(key, read, write));
    }

    // v0.2 feature hooks (v0.2-features.md §12): each lives in its own partial file.
    partial void AddTextRuleEntries(EntryBuilder b);
    partial void AddScreenNameEntries(EntryBuilder b);
    partial void AddTransformEntries(EntryBuilder b);

    private List<Entry> BuildEntries()
    {
        var l = new List<Entry>();
        var b = new EntryBuilder(l);

        void Bool(string key, Func<bool> get, Action<bool> set) => b.Bool(key, get, set);
        void Str(string key, Func<string?> get, Action<string> set, bool omitWhenNull = false) => b.Str(key, get, set, omitWhenNull);
        void Float(string key, Func<float> get, Action<float> set) => b.Float(key, get, set);
        void Enum<T>(string key, EnumNameMap<T> map, Func<T> get, Action<T> set) where T : struct, System.Enum => b.Enum(key, map, get, set);
        void StyleSelection(string key, Func<DictationWritingStyleSelection> get, Action<DictationWritingStyleSelection> set) => b.Raw(key,
            n => { if (n is JsonValue v && v.TryGetValue<string>(out var s) && DictationWritingStyleSelection.Parse(s) is { } sel) set(sel); },
            () => get().Id);
        void StrList(string key, Func<IReadOnlyList<string>> get, Action<IReadOnlyList<string>> set) => b.StrList(key, get, set);

        // General
        Bool("launchAtLogin", () => LaunchAtLogin, v => LaunchAtLogin = v);
        Enum("appAppearanceMode", RemyEnumNames.AppAppearance, () => AppAppearanceMode, v => AppAppearanceMode = v);
        Bool("prewarmEnginesAtStartup", () => PrewarmEnginesAtStartup, v => PrewarmEnginesAtStartup = v);
        Enum("themeMode", RemyEnumNames.Theme, () => ThemeMode, v => ThemeMode = v);
        Enum("remyDictationDestination", RemyEnumNames.Destination, () => RemyDictationDestination, v => RemyDictationDestination = v);
        Str("selectedLanguage", () => SelectedLanguage, v => SelectedLanguage = v);
        Str("selectedMicrophoneUID", () => SelectedMicrophoneUID, v => SelectedMicrophoneUID = v, omitWhenNull: true);
        Bool("boostMicrophoneVolumeEnabled", () => BoostMicrophoneVolumeEnabled, v => BoostMicrophoneVolumeEnabled = v);
        Bool("muteSystemAudioDuringRecordingEnabled", () => MuteSystemAudioDuringRecordingEnabled, v => MuteSystemAudioDuringRecordingEnabled = v);
        Bool("soundEffectsEnabled", () => SoundEffectsEnabled, v => SoundEffectsEnabled = v);
        Float("soundEffectsVolume", () => SoundEffectsVolume, v => SoundEffectsVolume = v);
        Str("remyDictationStartSound", () => RemyDictationStartSound, v => RemyDictationStartSound = v);
        Str("remyDictationCompletionSound", () => RemyDictationCompletionSound, v => RemyDictationCompletionSound = v);
        Bool("autoStopAfterSpeechEndsEnabled", () => AutoStopAfterSpeechEndsEnabled, v => AutoStopAfterSpeechEndsEnabled = v);

        // Shortcuts
        l.Add(new("hotkeyBindings",
            n => { if (HotkeyBindingJson.ListFromJson(n) is { } list) HotkeyBindings = list.Count == 0 ? new[] { HotkeyDefaults.DefaultRecording() } : list; },
            () => HotkeyBindingJson.ToJson(HotkeyBindings)));
        l.Add(new("cancelShortcut",
            n => { if (HotkeyBindingJson.FromJson(n) is { } b) CancelShortcut = b; },
            () => HotkeyBindingJson.ToJson(CancelShortcut)));
        Bool("holdToCancel", () => HoldToCancel, v => HoldToCancel = v);
        Bool("preserveCancelledSessions", () => PreserveCancelledSessions, v => PreserveCancelledSessions = v);

        // Text & overlay
        Bool("showTextPreview", () => ShowTextPreview, v => ShowTextPreview = v);
        Enum("overlaySize", RemyEnumNames.Overlay, () => OverlaySize, v => OverlaySize = v);
        Enum("overlayThemeMode", RemyEnumNames.Theme, () => OverlayThemeMode, v => OverlayThemeMode = v);

        // Cleanup
        Enum("transcriptPostProcessingMode", RemyEnumNames.PostProcessing, () => TranscriptPostProcessingMode, v => TranscriptPostProcessingMode = v);
        Str("ollamaPostProcessingPrompt", () => OllamaPostProcessingPrompt, v => OllamaPostProcessingPrompt = v);
        Str("openRouterPostProcessingPrompt", () => OpenRouterPostProcessingPrompt, v => OpenRouterPostProcessingPrompt = v);
        Str("openAICompatiblePostProcessingPrompt", () => OpenAICompatiblePostProcessingPrompt, v => OpenAICompatiblePostProcessingPrompt = v);
        Str("ollamaBaseURL", () => OllamaBaseURL, v => OllamaBaseURL = v);
        Str("ollamaModel", () => OllamaModel, v => OllamaModel = v);
        Str("openRouterModel", () => OpenRouterModel, v => OpenRouterModel = v);
        Str("openAICompatibleModel", () => OpenAICompatibleModel, v => OpenAICompatibleModel = v);
        Bool("ollamaReasoningEnabled", () => OllamaReasoningEnabled, v => OllamaReasoningEnabled = v);
        Bool("openRouterReasoningEnabled", () => OpenRouterReasoningEnabled, v => OpenRouterReasoningEnabled = v);
        Str("openAICompatibleBaseURL", () => OpenAICompatibleBaseURL, v => OpenAICompatibleBaseURL = v);
        Str("openRouterAPIKeyEnvironmentVariable", () => OpenRouterAPIKeyEnvironmentVariable, v => OpenRouterAPIKeyEnvironmentVariable = v);
        StrList("customVocabulary", () => CustomVocabulary, v => CustomVocabulary = v);
        Bool("isFillerWordRemovalEnabled", () => IsFillerWordRemovalEnabled, v => IsFillerWordRemovalEnabled = v);
        StrList("fillerWordsToRemove", () => FillerWordsToRemove, v => FillerWordsToRemove = v);
        Bool("dictationContextAwarenessEnabled", () => DictationContextAwarenessEnabled, v => DictationContextAwarenessEnabled = v);
        Bool("spellNamesFromActiveWindowEnabled", () => SpellNamesFromActiveWindowEnabled, v => SpellNamesFromActiveWindowEnabled = v);
        Bool("shareDictationContextWithRemoteProviders", () => ShareDictationContextWithRemoteProviders, v => ShareDictationContextWithRemoteProviders = v);
        StyleSelection("emailDictationWritingStyle", () => EmailDictationWritingStyle, v => EmailDictationWritingStyle = v);
        StyleSelection("workMessagingDictationWritingStyle", () => WorkMessagingDictationWritingStyle, v => WorkMessagingDictationWritingStyle = v);
        StyleSelection("personalMessagingDictationWritingStyle", () => PersonalMessagingDictationWritingStyle, v => PersonalMessagingDictationWritingStyle = v);
        StyleSelection("otherDictationWritingStyle", () => OtherDictationWritingStyle, v => OtherDictationWritingStyle = v);
        l.Add(new("extendedDictationWritingStyles",
            n =>
            {
                if (n is not JsonObject o) { ExtendedDictationWritingStyles = new Dictionary<DictationContextCategory, DictationWritingStyleSelection>(); return; }
                var d = new Dictionary<DictationContextCategory, DictationWritingStyleSelection>();
                foreach (var (k, v) in o)
                    if (RemyEnumNames.ContextCategory.TryParse(k, out var cat) && IsExtendedCategory(cat)
                        && v is JsonValue jv && jv.TryGetValue<string>(out var s) && DictationWritingStyleSelection.Parse(s) is { } st)
                        d[cat] = st;
                ExtendedDictationWritingStyles = d;
            },
            () =>
            {
                var o = new JsonObject();
                foreach (var (cat, st) in ExtendedDictationWritingStyles.OrderBy(kv => kv.Key))
                    o[RemyEnumNames.ContextCategory.ToRaw(cat)] = st.Id;
                return o;
            }));
        l.Add(new("dictationWritingStyleLibrary",
            n => WritingStyleLibrary = LibraryFromJson(n),
            () => LibraryToJson(WritingStyleLibrary)));
        l.Add(new("dictationAppRules",
            n =>
            {
                if (n is not JsonArray a) return;
                var rules = new List<DictationAppRule>();
                foreach (var item in a)
                {
                    if (item is not JsonObject o) continue;
                    if (!(o["processName"] is JsonValue pv && pv.TryGetValue<string>(out var proc)) || string.IsNullOrWhiteSpace(proc)) continue;
                    DictationContextCategory? cat = null;
                    if (o["category"] is JsonValue cv && cv.TryGetValue<string>(out var cs) && RemyEnumNames.ContextCategory.TryParse(cs, out var c)) cat = c;
                    bool excluded = o["excluded"] is JsonValue ev && ev.TryGetValue<bool>(out var eb) && eb;
                    rules.Add(new DictationAppRule(proc, cat, excluded));
                }
                DictationAppRules = rules;
            },
            () =>
            {
                var a = new JsonArray();
                foreach (var r in DictationAppRules)
                {
                    var o = new JsonObject { ["processName"] = r.ProcessName };
                    if (r.Category is { } c) o["category"] = RemyEnumNames.ContextCategory.ToRaw(c);
                    o["excluded"] = r.Excluded;
                    a.Add(o);
                }
                return a;
            }));

        // S1-mini
        Enum("s1MiniStyling", RemyEnumNames.S1Styling, () => S1MiniStyling, v => S1MiniStyling = v);
        l.Add(new("s1MiniAppStyling",
            n => { if (S1AppStylingFromJson(n) is { } s) S1MiniAppStyling = s; },
            () => S1AppStylingToJson(S1MiniAppStyling)));
        Enum("s1MiniStructure", RemyEnumNames.S1Structure, () => S1MiniStructure, v => S1MiniStructure = v);
        Enum("s1MiniContextSetting", RemyEnumNames.S1Context, () => S1MiniContextSetting, v => S1MiniContextSetting = v);

        // History
        Bool("remyHistoryEnabled", () => RemyHistoryEnabled, v => RemyHistoryEnabled = v);

        AddTextRuleEntries(b);
        AddScreenNameEntries(b);
        AddTransformEntries(b);
        return l;
    }

    private static DictationWritingStyleLibrary LibraryFromJson(JsonNode? n)
    {
        var lib = new DictationWritingStyleLibrary();
        if (n is not JsonObject o) return lib;
        if (o["instructionOverrides"] is JsonObject ov)
            foreach (var (k, v) in ov)
                if (DictationText.TryParseStyle(k, out var style) && v is JsonValue jv && jv.TryGetValue<string>(out var text))
                    try { lib.SetInstruction(text, style); } catch (DictationWritingStyleValidationException) { }
        if (o["customStyles"] is JsonArray arr)
        {
            var ids = new HashSet<Guid>();
            foreach (var item in arr)
            {
                if (item is not JsonObject c) continue;
                if (!(c["id"] is JsonValue iv && iv.TryGetValue<string>(out var idText) && Guid.TryParse(idText, out var id))) continue;
                if (!(c["name"] is JsonValue nv && nv.TryGetValue<string>(out var name))) continue;
                if (!(c["instruction"] is JsonValue tv && tv.TryGetValue<string>(out var instruction))) continue;
                if (!ids.Add(id)) continue;
                try { lib.AddCustomStyle(id, name, instruction); } catch (DictationWritingStyleValidationException) { ids.Remove(id); }
            }
        }
        return lib;
    }

    private static JsonNode LibraryToJson(DictationWritingStyleLibrary lib)
    {
        var overrides = new JsonObject();
        foreach (var style in DictationWritingStyleLibrary.BuiltInStyles)
            if (lib.InstructionOverrides.TryGetValue(style.ToRaw(), out var text)) overrides[style.ToRaw()] = text;
        var customs = new JsonArray();
        foreach (var c in lib.CustomStyles)
            customs.Add(new JsonObject
            {
                ["id"] = c.Id.ToString("D").ToUpperInvariant(),
                ["name"] = c.Name,
                ["instruction"] = c.Instruction,
            });
        return new JsonObject { ["customStyles"] = customs, ["instructionOverrides"] = overrides };
    }

    private static S1MiniAppStyling? S1AppStylingFromJson(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        var rec = S1MiniAppStyling.Recommended;
        S1MiniStyling Get(string key, S1MiniStyling fallback) =>
            o[key] is JsonValue v && v.TryGetValue<string>(out var s) && RemyEnumNames.S1Styling.TryParse(s, out var e) ? e : fallback;
        Dictionary<DictationContextCategory, S1MiniStyling>? ext = null;
        if (o["extendedStyles"] is JsonObject eo)
        {
            ext = new();
            foreach (var (k, v) in eo)
                if (RemyEnumNames.ContextCategory.TryParse(k, out var cat) && v is JsonValue jv && jv.TryGetValue<string>(out var s)
                    && RemyEnumNames.S1Styling.TryParse(s, out var st)) ext[cat] = st;
        }
        return new S1MiniAppStyling(Get("email", rec.Email), Get("workMessaging", rec.WorkMessaging),
            Get("personalMessaging", rec.PersonalMessaging), Get("other", rec.Other), ext);
    }

    private static JsonObject S1AppStylingToJson(S1MiniAppStyling s)
    {
        var o = new JsonObject
        {
            ["email"] = RemyEnumNames.S1Styling.ToRaw(s.Email),
            ["workMessaging"] = RemyEnumNames.S1Styling.ToRaw(s.WorkMessaging),
            ["personalMessaging"] = RemyEnumNames.S1Styling.ToRaw(s.PersonalMessaging),
            ["other"] = RemyEnumNames.S1Styling.ToRaw(s.Other),
        };
        if (s.ExtendedStyles is { Count: > 0 } ext)
        {
            var e = new JsonObject();
            foreach (var (cat, st) in ext.OrderBy(kv => kv.Key)) e[RemyEnumNames.ContextCategory.ToRaw(cat)] = RemyEnumNames.S1Styling.ToRaw(st);
            o["extendedStyles"] = e;
        }
        return o;
    }

    /// <summary>Apply every key present in <paramref name="root"/>; returns true if something needed rewriting
    /// (blank provider prompt replaced, Mac-only mode coerced, legacy s1MiniStyling fan-out).</summary>
    internal bool LoadFrom(JsonObject root)
    {
        bool rewrite = false;
        var oldSuppress = SuppressNotifications;
        SuppressNotifications = true;
        try
        {
            foreach (var e in Entries)
            {
                if (!root.TryGetPropertyValue(e.Key, out var node) || node is null) continue;
                try { e.Read(node); } catch { /* one bad value never breaks the rest */ }
            }

            foreach (var key in new[] { "ollamaPostProcessingPrompt", "openRouterPostProcessingPrompt", "openAICompatiblePostProcessingPrompt" })
                if (root[key] is JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s)) rewrite = true;

            if (root["transcriptPostProcessingMode"] is JsonValue mv && mv.TryGetValue<string>(out var ms)
                && RemyEnumNames.PostProcessing.TryParse(ms, out var mode) && !mode.IsSupportedOnWindows()) rewrite = true;

            // Legacy: only s1MiniStyling stored -> every category uses it.
            if (!root.ContainsKey("s1MiniAppStyling") && root["s1MiniStyling"] is JsonValue lv && lv.TryGetValue<string>(out var ls)
                && RemyEnumNames.S1Styling.TryParse(ls, out var legacy))
            {
                S1MiniAppStyling = new S1MiniAppStyling(legacy, legacy, legacy, legacy);
                rewrite = true;
            }
        }
        finally { SuppressNotifications = oldSuppress; }
        return rewrite;
    }

    /// <summary>All persisted keys as JSON (schema marker excluded; the store adds it).</summary>
    internal JsonObject ToJson()
    {
        var o = new JsonObject();
        foreach (var e in Entries)
            if (e.Write() is { } n) o[e.Key] = n;
        return o;
    }
}
