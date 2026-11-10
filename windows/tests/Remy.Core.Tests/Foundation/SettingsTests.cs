using System.Text.Json.Nodes;
using Remy.Core.Hotkeys;
using Remy.Core.Settings;
using Xunit;

namespace Remy.Core.Tests.Foundation;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "remy-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class SettingsTests
{
    [Fact]
    public void Defaults_match_spec()
    {
        var s = new RemySettings();
        Assert.False(s.LaunchAtLogin);
        Assert.Equal(AppAppearanceMode.MenuBarOnly, s.AppAppearanceMode);
        Assert.True(s.PrewarmEnginesAtStartup);
        Assert.Equal(ThemeMode.System, s.ThemeMode);
        Assert.Equal(RemyDictationDestination.OriginalField, s.RemyDictationDestination);
        Assert.Null(s.SelectedMicrophoneUID);
        Assert.True(s.BoostMicrophoneVolumeEnabled);
        Assert.False(s.MuteSystemAudioDuringRecordingEnabled);
        Assert.True(s.SoundEffectsEnabled);
        Assert.Equal(0.3f, s.SoundEffectsVolume);
        Assert.Equal("builtin:Speech On", s.RemyDictationStartSound);
        Assert.Equal("builtin:Speech Off", s.RemyDictationCompletionSound);
        Assert.True(s.HoldToCancel);
        Assert.True(s.PreserveCancelledSessions);
        Assert.True(s.ShowTextPreview);
        Assert.Equal(OverlaySize.Comfortable, s.OverlaySize);
        Assert.Equal(TranscriptPostProcessingMode.None, s.TranscriptPostProcessingMode);
        Assert.Equal("http://127.0.0.1:11434", s.OllamaBaseURL);
        Assert.Equal("http://127.0.0.1:1234/v1", s.OpenAICompatibleBaseURL);
        Assert.True(s.OllamaReasoningEnabled);
        Assert.True(s.OpenRouterReasoningEnabled);
        Assert.False(s.IsFillerWordRemovalEnabled);
        Assert.Equal(new[] { "um", "uh", "erm", "er", "hmm", "嗯", "呃" }, s.FillerWordsToRemove);
        Assert.True(s.DictationContextAwarenessEnabled);
        Assert.False(s.ShareDictationContextWithRemoteProviders);
        Assert.Equal(Remy.Core.Context.DictationWritingStyleSelection.FromBuiltIn(DictationWritingStyle.Formal), s.EmailDictationWritingStyle);
        Assert.Equal(Remy.Core.Context.DictationWritingStyleSelection.FromBuiltIn(DictationWritingStyle.Casual), s.PersonalMessagingDictationWritingStyle);
        Assert.Equal(S1MiniStyling.SemiFormal, s.S1MiniStyling);
        Assert.Equal(S1MiniStructure.Prose, s.S1MiniStructure);
        Assert.Equal(S1MiniContextSetting.Automatic, s.S1MiniContextSetting);
        Assert.True(s.RemyHistoryEnabled);
        var b = Assert.Single(s.HotkeyBindings);
        Assert.Equal("Ctrl+Win", b.DisplayName);
        Assert.True(b.IsModifierOnly);
        Assert.Equal(HotkeyMode.HoldToRecord, b.Mode);
        Assert.Equal(0x1B, s.CancelShortcut.VirtualKey);
        Assert.Equal("Esc", s.CancelShortcut.DisplayName);
    }

    [Fact]
    public void Default_prompt_is_verbatim()
    {
        var p = CleanupPrompts.RecommendedTranscriptCleanupPrompt;
        Assert.StartsWith("Never use em dashes. Replace them with commas, periods, colons, semicolons, or parentheses when needed.\n\n", p);
        Assert.EndsWith("prefer the wording that best matches the surrounding context.", p);
        Assert.Equal(7, p.Split("\n\n").Length);
        Assert.Contains("\"thirteen point five percent\" -> \"13.5%\"", p);
        Assert.DoesNotContain("\r", p);
    }

    [Fact]
    public void Writing_style_defaults_and_sanitizing()
    {
        var s = new RemySettings();
        Assert.Equal(DictationWritingStyle.Neutral, s.GetWritingStyle(DictationContextCategory.Browser));
        foreach (var c in new[] { DictationContextCategory.Terminal, DictationContextCategory.TerminalAgent, DictationContextCategory.CodingAgent, DictationContextCategory.AiChat })
            Assert.Equal(DictationWritingStyle.Original, s.GetWritingStyle(c));
        s.SetWritingStyle(DictationContextCategory.Terminal, DictationWritingStyle.Excited);
        Assert.Equal(DictationWritingStyle.Excited, s.GetWritingStyle(DictationContextCategory.Terminal));
        // Excited is not offered for personal messaging -> casual.
        s.SetWritingStyle(DictationContextCategory.PersonalMessaging, DictationWritingStyle.Excited);
        Assert.Equal(DictationWritingStyle.Casual, s.GetWritingStyle(DictationContextCategory.PersonalMessaging));
        s.SetWritingStyle(DictationContextCategory.PersonalMessaging, DictationWritingStyle.VeryCasual);
        Assert.Equal(DictationWritingStyle.VeryCasual, s.GetWritingStyle(DictationContextCategory.PersonalMessaging));
        Assert.Equal(9, s.WritingStylesSnapshot().Count);
    }

    [Fact]
    public void Mac_only_modes_coerce_to_none()
    {
        var s = new RemySettings { TranscriptPostProcessingMode = TranscriptPostProcessingMode.AppleIntelligence };
        Assert.Equal(TranscriptPostProcessingMode.None, s.TranscriptPostProcessingMode);
        s.TranscriptPostProcessingMode = TranscriptPostProcessingMode.FluidAudioVocabulary;
        Assert.Equal(TranscriptPostProcessingMode.None, s.TranscriptPostProcessingMode);
        s.TranscriptPostProcessingMode = TranscriptPostProcessingMode.Ollama;
        Assert.Equal(TranscriptPostProcessingMode.Ollama, s.TranscriptPostProcessingMode);
    }

    [Fact]
    public void Blank_prompt_resets_to_default()
    {
        var s = new RemySettings { OllamaPostProcessingPrompt = "custom" };
        Assert.Equal("custom", s.OllamaPostProcessingPrompt);
        s.OllamaPostProcessingPrompt = "   ";
        Assert.Equal(CleanupPrompts.RecommendedTranscriptCleanupPrompt, s.OllamaPostProcessingPrompt);
    }

    [Fact]
    public void Roundtrip_every_changed_value()
    {
        using var t = new TempDir();
        var path = t.File("settings.json");
        using (var store = new SettingsStore(path))
        {
            var s = store.Load().Settings;
            s.LaunchAtLogin = true;
            s.AppAppearanceMode = AppAppearanceMode.DockAndMenuBar;
            s.ThemeMode = ThemeMode.Dark;
            s.OverlayThemeMode = ThemeMode.Light;
            s.RemyDictationDestination = RemyDictationDestination.CurrentField;
            s.SelectedMicrophoneUID = "{0.0.1.00000000}.{abc}";
            s.SoundEffectsVolume = 0.75f;
            s.RemyDictationStartSound = "none";
            s.OverlaySize = OverlaySize.Large;
            s.TranscriptPostProcessingMode = TranscriptPostProcessingMode.OpenRouter;
            s.OpenRouterModel = "openai/gpt-4o-mini";
            s.OpenRouterPostProcessingPrompt = "my prompt\nline 2";
            s.OllamaReasoningEnabled = false;
            s.CustomVocabulary = new[] { "Remy", "Parakeet" };
            s.IsFillerWordRemovalEnabled = true;
            s.SetWritingStyle(DictationContextCategory.Terminal, DictationWritingStyle.Neutral);
            s.SetWritingStyle(DictationContextCategory.Email, DictationWritingStyle.Casual);
            s.DictationAppRules = new[] { new Remy.Core.Context.DictationAppRule("slack.exe", DictationContextCategory.WorkMessaging, false), new("keepass.exe", null, true) };
            s.S1MiniAppStyling = new Remy.Core.Cleanup.S1MiniAppStyling(S1MiniStyling.Casual, S1MiniStyling.Casual, S1MiniStyling.Formal, S1MiniStyling.SemiCasual,
                new Dictionary<DictationContextCategory, S1MiniStyling> { [DictationContextCategory.Browser] = S1MiniStyling.Formal });
            s.S1MiniStructure = S1MiniStructure.Lists;
            s.HotkeyBindings = new[]
            {
                new HotkeyBinding(Guid.NewGuid(), 0x20, HotkeyModifiers.Ctrl | HotkeyModifiers.LeftAlt, "", HotkeyMode.HandsFreeToggle),
                HotkeyDefaults.DefaultRecording(),
            };
            s.CancelShortcut = HotkeyBinding.Empty(mode: HotkeyMode.HandsFreeToggle);
            s.HoldToCancel = false;
            s.RemyHistoryEnabled = false;
            store.Flush();
        }

        using var store2 = new SettingsStore(path);
        var r = store2.Load();
        var l = r.Settings;
        Assert.Equal(ConfigurationVersionKind.Current, r.Gate.Kind);
        Assert.True(l.LaunchAtLogin);
        Assert.Equal(AppAppearanceMode.DockAndMenuBar, l.AppAppearanceMode);
        Assert.Equal(ThemeMode.Dark, l.ThemeMode);
        Assert.Equal(ThemeMode.Light, l.OverlayThemeMode);
        Assert.Equal(RemyDictationDestination.CurrentField, l.RemyDictationDestination);
        Assert.Equal("{0.0.1.00000000}.{abc}", l.SelectedMicrophoneUID);
        Assert.Equal(0.75f, l.SoundEffectsVolume);
        Assert.Equal("none", l.RemyDictationStartSound);
        Assert.Equal(OverlaySize.Large, l.OverlaySize);
        Assert.Equal(TranscriptPostProcessingMode.OpenRouter, l.TranscriptPostProcessingMode);
        Assert.Equal("openai/gpt-4o-mini", l.OpenRouterModel);
        Assert.Equal("my prompt\nline 2", l.OpenRouterPostProcessingPrompt);
        Assert.False(l.OllamaReasoningEnabled);
        Assert.Equal(new[] { "Remy", "Parakeet" }, l.CustomVocabulary);
        Assert.True(l.IsFillerWordRemovalEnabled);
        Assert.Equal(DictationWritingStyle.Neutral, l.GetWritingStyle(DictationContextCategory.Terminal));
        Assert.Equal(DictationWritingStyle.Casual, l.GetWritingStyle(DictationContextCategory.Email));
        Assert.Equal(2, l.DictationAppRules.Count);
        Assert.Equal(new Remy.Core.Context.DictationAppRule("keepass.exe", null, true), l.DictationAppRules[1]);
        Assert.Equal(S1MiniStyling.Formal, l.S1MiniAppStyling.PersonalMessaging);
        Assert.Equal(S1MiniStyling.Formal, l.S1MiniAppStyling.ExtendedStyles![DictationContextCategory.Browser]);
        Assert.Equal(S1MiniStructure.Lists, l.S1MiniStructure);
        Assert.Equal(2, l.HotkeyBindings.Count);
        Assert.Equal("Ctrl+LAlt+Space", l.HotkeyBindings[0].DisplayName);
        Assert.Equal(HotkeyMode.HandsFreeToggle, l.HotkeyBindings[0].Mode);
        Assert.False(l.CancelShortcut.HasBinding);
        Assert.False(l.HoldToCancel);
        Assert.False(l.RemyHistoryEnabled);
    }

    [Fact]
    public void Json_uses_mac_key_names_and_raw_enum_strings()
    {
        using var t = new TempDir();
        var path = t.File("settings.json");
        using var store = new SettingsStore(path);
        var s = store.Load().Settings;
        s.TranscriptPostProcessingMode = TranscriptPostProcessingMode.OpenAICompatible;
        s.S1MiniStyling = S1MiniStyling.SemiCasual;
        store.Flush();
        var o = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(1, o["remy.configurationSchemaVersion"]!.GetValue<int>());
        Assert.Equal("openAICompatible", o["transcriptPostProcessingMode"]!.GetValue<string>());
        Assert.Equal("semi-casual", o["s1MiniStyling"]!.GetValue<string>());
        Assert.Equal("originalField", o["remyDictationDestination"]!.GetValue<string>());
        Assert.Equal("holdToRecord", o["hotkeyBindings"]![0]!["mode"]!.GetValue<string>());
        Assert.False(o.ContainsKey("selectedMicrophoneUID"));
        foreach (var k in RemySettings.PersistedKeys) Assert.True(o.ContainsKey(k) || k == "selectedMicrophoneUID", k);
    }

    [Fact]
    public void Tolerant_load_of_partial_and_wrong_typed_file()
    {
        using var t = new TempDir();
        var path = t.File("settings.json");
        File.WriteAllText(path, """
        {
          "remy.configurationSchemaVersion": 1,
          "launchAtLogin": "yes",
          "themeMode": "purple",
          "overlaySize": "large",
          "soundEffectsVolume": "loud",
          "customVocabulary": ["a", 5, "b"],
          "transcriptPostProcessingMode": "appleIntelligence",
          "ollamaPostProcessingPrompt": "   ",
          "hotkeyBindings": "nope",
          "cancelShortcut": 7,
          "emailDictationWritingStyle": "excited",
          "futureKey": {"x": 1}
        }
        """);
        using var store = new SettingsStore(path);
        var s = store.Load().Settings;
        Assert.False(s.LaunchAtLogin);
        Assert.Equal(ThemeMode.System, s.ThemeMode);
        Assert.Equal(OverlaySize.Large, s.OverlaySize);
        Assert.Equal(0.3f, s.SoundEffectsVolume);
        Assert.Equal(new[] { "a", "b" }, s.CustomVocabulary);
        Assert.Equal(TranscriptPostProcessingMode.None, s.TranscriptPostProcessingMode);
        Assert.Equal(CleanupPrompts.RecommendedTranscriptCleanupPrompt, s.OllamaPostProcessingPrompt);
        Assert.Equal("Ctrl+Win", Assert.Single(s.HotkeyBindings).DisplayName);
        Assert.Equal("Esc", s.CancelShortcut.DisplayName);
        Assert.Equal(Remy.Core.Context.DictationWritingStyleSelection.FromBuiltIn(DictationWritingStyle.Excited), s.EmailDictationWritingStyle);
        // blank prompt and coerced mode are written back; unknown keys survive.
        var o = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(CleanupPrompts.RecommendedTranscriptCleanupPrompt, o["ollamaPostProcessingPrompt"]!.GetValue<string>());
        Assert.Equal("none", o["transcriptPostProcessingMode"]!.GetValue<string>());
        Assert.Equal(1, o["futureKey"]!["x"]!.GetValue<int>());
    }

    [Fact]
    public void Corrupt_file_yields_defaults_and_is_backed_up()
    {
        using var t = new TempDir();
        var path = t.File("settings.json");
        File.WriteAllText(path, "{ not json");
        using var store = new SettingsStore(path);
        var r = store.Load();
        Assert.True(r.FileWasUnreadable);
        Assert.False(r.Gate.IsBlocking);
        Assert.Equal(AppAppearanceMode.MenuBarOnly, r.Settings.AppAppearanceMode);
        Assert.Single(Directory.GetFiles(t.Path, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Missing_file_loads_defaults_and_creates_file()
    {
        using var t = new TempDir();
        var path = t.File("sub/settings.json");
        using var store = new SettingsStore(path);
        var r = store.Load();
        Assert.False(r.Gate.IsBlocking);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Debounced_save_writes_after_change()
    {
        using var t = new TempDir();
        var path = t.File("settings.json");
        using var store = new SettingsStore(path, TimeSpan.FromMilliseconds(50));
        var s = store.Load().Settings;
        s.ShowTextPreview = false;
        // The store replaces the file atomically, so a read can briefly hit a sharing violation or the old file.
        bool? saved = null;
        for (int i = 0; i < 200 && saved != false; i++)
        {
            saved = ReadShowTextPreview(path);
            if (saved != false) Thread.Sleep(25);
        }
        Assert.False(saved);
    }

    private static bool? ReadShowTextPreview(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path))?["showTextPreview"]?.GetValue<bool>(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    // ---- version gate (RemyConfigurationVersionTests / RemyStartupTests)

    private static ConfigurationGate Gate(string json) =>
        ConfigurationVersion.Preflight(JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void Gate_absent_and_zero_are_legacy()
    {
        Assert.Equal(ConfigurationVersionKind.Legacy, Gate("{}").Kind);
        Assert.Equal(ConfigurationVersionKind.Legacy, Gate("""{"remy.configurationSchemaVersion":0}""").Kind);
        Assert.Null(Gate("{}").BlockingMessage);
    }

    [Fact]
    public void Gate_one_is_current_and_two_is_unsupported()
    {
        Assert.Equal(ConfigurationVersionKind.Current, Gate("""{"remy.configurationSchemaVersion":1}""").Kind);
        var g = Gate("""{"remy.configurationSchemaVersion":2}""");
        Assert.Equal(ConfigurationVersionKind.Unsupported, g.Kind);
        Assert.Equal("These settings use configuration version 2. This build supports version 1. Open a compatible Remy build or restore a Remy settings backup. No Remy settings were changed.", g.BlockingMessage);
    }

    [Theory]
    [InlineData("\"x\"")]
    [InlineData("true")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("null")]
    public void Gate_non_integer_is_malformed(string value)
    {
        var g = Gate("{\"remy.configurationSchemaVersion\":" + value + "}");
        Assert.Equal(ConfigurationVersionKind.Malformed, g.Kind);
        Assert.Equal("The Remy configuration version could not be read. Restore a Remy settings backup before reopening the app. No Remy settings were changed.", g.BlockingMessage);
    }

    [Fact]
    public void Blocked_file_is_never_modified()
    {
        using var t = new TempDir();
        var path = t.File("settings.json");
        const string original = """{"remy.configurationSchemaVersion":2,"launchAtLogin":true}""";
        File.WriteAllText(path, original);
        using var store = new SettingsStore(path, TimeSpan.FromMilliseconds(20));
        var r = store.Load();
        Assert.True(r.Gate.IsBlocking);
        r.Settings.LaunchAtLogin = true;
        store.Flush();
        Assert.False(store.Save(r.Settings));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void Legacy_file_is_migrated_once_to_version_one()
    {
        using var t = new TempDir();
        var path = t.File("settings.json");
        File.WriteAllText(path, """{"launchAtLogin":true}""");
        using var store = new SettingsStore(path);
        var r = store.Load();
        Assert.True(r.MigratedFromLegacy);
        Assert.True(r.Settings.LaunchAtLogin);
        Assert.Equal(1, JsonNode.Parse(File.ReadAllText(path))![ "remy.configurationSchemaVersion"]!.GetValue<int>());
        using var store2 = new SettingsStore(path);
        Assert.False(store2.Load().MigratedFromLegacy);
    }

    [Fact]
    public void Legacy_s1MiniStyling_fans_out_to_all_categories()
    {
        using var t = new TempDir();
        var path = t.File("settings.json");
        File.WriteAllText(path, """{"s1MiniStyling":"casual"}""");
        using var store = new SettingsStore(path);
        var s = store.Load().Settings;
        Assert.Equal(S1MiniStyling.Casual, s.S1MiniAppStyling.Email);
        Assert.Equal(S1MiniStyling.Casual, s.S1MiniAppStyling.Other);
    }

    [Fact]
    public void Enum_names_roundtrip_and_display()
    {
        Assert.Equal("veryCasual", RemyEnumNames.WritingStyle.ToRaw(DictationWritingStyle.VeryCasual));
        Assert.Equal("Original tone", RemyEnumNames.WritingStyle.ToDisplay(DictationWritingStyle.Original));
        Assert.Equal("semi-formal", RemyEnumNames.S1Styling.ToRaw(S1MiniStyling.SemiFormal));
        Assert.Equal("Terminal coding agent", RemyEnumNames.ContextCategory.ToDisplay(DictationContextCategory.TerminalAgent));
        Assert.Equal("Saved Profile", TranscriptPostProcessingMode.Profile.DisplayName());
        Assert.Equal(169, OverlaySize.Compact.Width());
        Assert.Equal("Where I’m focused when it finishes", RemyDictationDestination.CurrentField.DisplayName());
        foreach (var c in Enum.GetValues<DictationContextCategory>())
            Assert.True(RemyEnumNames.ContextCategory.TryParse(RemyEnumNames.ContextCategory.ToRaw(c), out var back) && back == c);
        foreach (var c in Enum.GetValues<TranscriptPostProcessingMode>())
            Assert.True(RemyEnumNames.PostProcessing.TryParse(c.ToRaw(), out var back) && back == c);
        foreach (var c in Enum.GetValues<S1MiniStyling>())
            Assert.True(RemyEnumNames.S1Styling.TryParse(RemyEnumNames.S1Styling.ToRaw(c), out var back) && back == c);
        Assert.False(RemyEnumNames.Theme.TryParse("bogus", out _));
    }
}
