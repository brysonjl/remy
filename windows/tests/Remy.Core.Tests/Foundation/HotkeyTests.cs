using System.Text.Json.Nodes;
using Remy.Core.Diagnostics;
using Remy.Core.Hotkeys;
using Remy.Core.Storage;
using Xunit;

namespace Remy.Core.Tests.Foundation;

public class HotkeyTests
{
    private const HotkeyModifiers C = HotkeyModifiers.Ctrl, A = HotkeyModifiers.Alt, S = HotkeyModifiers.Shift, W = HotkeyModifiers.Win;

    private static HotkeyBinding B(int? vk, HotkeyModifiers m, HotkeyMode mode = HotkeyMode.HoldToRecord) =>
        new(Guid.NewGuid(), vk, m, "", mode);

    [Fact]
    public void Normalize_side_flag_implies_generic()
    {
        Assert.Equal(HotkeyModifiers.LeftCtrl | C, HotkeyRules.Normalize(HotkeyModifiers.LeftCtrl));
        Assert.Equal(HotkeyModifiers.RightWin | W | A, HotkeyRules.Normalize(HotkeyModifiers.RightWin | A));
        Assert.Equal(C | W, HotkeyRules.GenericOnly(HotkeyModifiers.LeftCtrl | HotkeyModifiers.RightWin));
    }

    [Fact]
    public void Caps_lock_binding_is_cleared()
    {
        var b = HotkeyRules.Canonicalize(B(HotkeyKeyNames.VkCapital, C));
        Assert.False(b.HasBinding);
        Assert.Equal("", b.DisplayName);
        Assert.Null(b.VirtualKey);
        Assert.Equal(HotkeyModifiers.None, b.Modifiers);
    }

    [Fact]
    public void Modifier_virtual_key_becomes_modifier_only()
    {
        var b = HotkeyRules.Canonicalize(B(HotkeyKeyNames.VkRControl, HotkeyModifiers.None));
        Assert.True(b.IsModifierOnly);
        Assert.Equal("RCtrl", b.DisplayName);
        Assert.Equal(HotkeyModifiers.RightCtrl | C, b.Modifiers);
    }

    [Fact]
    public void Empty_binding_has_empty_display_name()
    {
        var b = HotkeyRules.Canonicalize(new HotkeyBinding(Guid.NewGuid(), null, HotkeyModifiers.None, "stale", HotkeyMode.HoldToRecord));
        Assert.Equal("", b.DisplayName);
        Assert.False(b.HasBinding);
    }

    [Theory]
    [InlineData(0x20, C | A, "Ctrl+Alt+Space")]
    [InlineData(null, C | W, "Ctrl+Win")]
    [InlineData(0x41, C | S | A | W, "Ctrl+Alt+Shift+Win+A")]
    [InlineData(null, HotkeyModifiers.RightCtrl, "RCtrl")]
    [InlineData(0x70, HotkeyModifiers.LeftAlt | HotkeyModifiers.RightShift, "LAlt+RShift+F1")]
    [InlineData(null, HotkeyModifiers.LeftCtrl | HotkeyModifiers.RightCtrl, "Ctrl")]
    [InlineData(0x1B, HotkeyModifiers.None, "Esc")]
    [InlineData(0xDB, C, "Ctrl+[")]
    [InlineData(0x60, HotkeyModifiers.None, "Num0")]
    [InlineData(0x5, HotkeyModifiers.None, "Key 0x05")]
    public void Display_names(int? vk, HotkeyModifiers mods, string expected) =>
        Assert.Equal(expected, HotkeyRules.BuildDisplayName(vk, mods));

    [Fact]
    public void Keyed_matching_allows_extra_modifiers()
    {
        Assert.True(HotkeyRules.KeyedModifiersMatch(C, C | S));
        Assert.True(HotkeyRules.KeyedModifiersMatch(C | A, C | A | W));
        Assert.False(HotkeyRules.KeyedModifiersMatch(C | A, C));
        Assert.True(HotkeyRules.KeyedModifiersMatch(HotkeyModifiers.None, S));
    }

    [Fact]
    public void Keyed_matching_is_side_aware()
    {
        var heldLeft = HotkeyModifiers.LeftCtrl | C;
        var heldRight = HotkeyModifiers.RightCtrl | C;
        Assert.True(HotkeyRules.KeyedModifiersMatch(C, heldLeft));
        Assert.True(HotkeyRules.KeyedModifiersMatch(C, heldRight));
        Assert.True(HotkeyRules.KeyedModifiersMatch(HotkeyModifiers.LeftCtrl, heldLeft));
        Assert.False(HotkeyRules.KeyedModifiersMatch(HotkeyModifiers.LeftCtrl, heldRight));
        Assert.True(HotkeyRules.KeyedModifiersMatch(HotkeyModifiers.RightCtrl | HotkeyModifiers.LeftCtrl, heldLeft | HotkeyModifiers.RightCtrl));
    }

    [Fact]
    public void Modifier_only_matching_is_exact()
    {
        Assert.True(HotkeyRules.ModifierOnlyModifiersMatch(C | W, C | W));
        Assert.True(HotkeyRules.ModifierOnlyModifiersMatch(C | W, HotkeyModifiers.LeftCtrl | HotkeyModifiers.RightWin));
        Assert.False(HotkeyRules.ModifierOnlyModifiersMatch(C | W, C | W | S));
        Assert.False(HotkeyRules.ModifierOnlyModifiersMatch(C | W, C));
        Assert.False(HotkeyRules.ModifierOnlyModifiersMatch(C, A));
        Assert.True(HotkeyRules.ModifierOnlyModifiersMatch(HotkeyModifiers.RightCtrl, HotkeyModifiers.RightCtrl));
        Assert.False(HotkeyRules.ModifierOnlyModifiersMatch(HotkeyModifiers.RightCtrl, HotkeyModifiers.LeftCtrl));
        Assert.False(HotkeyRules.ModifierOnlyModifiersMatch(HotkeyModifiers.RightCtrl, HotkeyModifiers.RightCtrl | S));
    }

    [Fact]
    public void Binding_level_matching()
    {
        var keyed = HotkeyRules.Canonicalize(B(0x20, C | A));
        Assert.True(HotkeyRules.MatchesKeyed(keyed, 0x20, C | A | S));
        Assert.False(HotkeyRules.MatchesKeyed(keyed, 0x21, C | A));
        var only = HotkeyDefaults.DefaultRecording();
        Assert.True(HotkeyRules.MatchesModifierOnly(only, C | W));
        Assert.False(HotkeyRules.MatchesModifierOnly(only, C | W | A));
        Assert.False(HotkeyRules.MatchesModifierOnly(keyed, C | A));
    }

    [Fact]
    public void Defaults()
    {
        var r = HotkeyDefaults.DefaultRecording();
        Assert.Equal("Ctrl+Win", r.DisplayName);
        Assert.Equal(HotkeyMode.HoldToRecord, r.Mode);
        var c = HotkeyDefaults.DefaultCancel();
        Assert.Equal(0x1B, c.VirtualKey);
        Assert.Equal(HotkeyModifiers.None, c.Modifiers);
        Assert.Equal("Esc", c.DisplayName);
    }

    [Fact]
    public void Json_roundtrip_and_field_names()
    {
        var b = HotkeyRules.Canonicalize(B(0x20, C | HotkeyModifiers.LeftAlt, HotkeyMode.HandsFreeToggle));
        var j = HotkeyBindingJson.ToJson(b);
        Assert.Equal("handsFreeToggle", j["mode"]!.GetValue<string>());
        Assert.Equal(0x20, j["virtualKey"]!.GetValue<int>());
        Assert.Equal("Ctrl+LAlt+Space", j["displayName"]!.GetValue<string>());
        Assert.Equal(b.Id.ToString(), j["id"]!.GetValue<string>());
        Assert.Equal(b, HotkeyBindingJson.FromJson(JsonNode.Parse(j.ToJsonString())));

        var modOnly = HotkeyDefaults.DefaultRecording();
        Assert.False(HotkeyBindingJson.ToJson(modOnly).ContainsKey("virtualKey"));
        Assert.Equal(modOnly, HotkeyBindingJson.FromJson(HotkeyBindingJson.ToJson(modOnly)));
        Assert.Null(HotkeyBindingJson.FromJson(JsonNode.Parse("5")));
    }

    [Fact]
    public void Json_load_canonicalizes_and_tolerates_bad_fields()
    {
        var b = HotkeyBindingJson.FromJson(JsonNode.Parse("""{"id":"bad","virtualKey":20,"modifiers":1,"displayName":"x","mode":"weird"}"""))!;
        Assert.False(b.HasBinding); // 20 == 0x14 caps lock
        var b2 = HotkeyBindingJson.FromJson(JsonNode.Parse("""{"virtualKey":"a","modifiers":"b","mode":"handsFreeToggle"}"""))!;
        Assert.False(b2.HasBinding);
        Assert.Equal(HotkeyMode.HandsFreeToggle, b2.Mode);
    }

    // ---- conflicts

    [Fact]
    public void Duplicate_shortcut()
    {
        var a = HotkeyRules.Canonicalize(B(0x20, C | A));
        var dup = HotkeyRules.Canonicalize(B(0x20, C | A));
        var r = ConflictDetector.CheckRecording(dup, new[] { a }, null);
        Assert.Equal("Duplicate of another shortcut", r!.Message);
        Assert.True(r.Blocking);
        Assert.Null(ConflictDetector.CheckRecording(HotkeyRules.Canonicalize(B(0x21, C | A)), new[] { a }, null));
        Assert.Null(ConflictDetector.CheckRecording(a, new[] { a }, null)); // itself is not a duplicate
    }

    [Fact]
    public void Recording_vs_cancel_rules()
    {
        var esc = HotkeyDefaults.DefaultCancel();
        // keyed vs keyed: same key and cancel modifiers satisfy the recording's.
        Assert.True(ConflictDetector.RecordingOverlapsCancel(HotkeyRules.Canonicalize(B(0x1B, HotkeyModifiers.None)), esc));
        Assert.True(ConflictDetector.RecordingOverlapsCancel(HotkeyRules.Canonicalize(B(0x1B, C)), HotkeyRules.Canonicalize(B(0x1B, C | S))));
        Assert.False(ConflictDetector.RecordingOverlapsCancel(HotkeyRules.Canonicalize(B(0x1B, C | S)), HotkeyRules.Canonicalize(B(0x1B, C))));
        // modifier-only recording prefix of a keyed cancel.
        var ctrlWin = HotkeyDefaults.DefaultRecording();
        Assert.False(ConflictDetector.RecordingOverlapsCancel(ctrlWin, esc));
        Assert.True(ConflictDetector.RecordingOverlapsCancel(ctrlWin, HotkeyRules.Canonicalize(B(0x1B, C | W))));
        Assert.True(ConflictDetector.RecordingOverlapsCancel(ctrlWin, HotkeyRules.Canonicalize(B(0x1B, C | W | S))));
        // two modifier-only: containment either way.
        var ctrlOnly = HotkeyRules.Canonicalize(B(null, C));
        Assert.True(ConflictDetector.RecordingOverlapsCancel(ctrlWin, ctrlOnly));
        Assert.True(ConflictDetector.RecordingOverlapsCancel(ctrlOnly, ctrlWin));
        Assert.False(ConflictDetector.RecordingOverlapsCancel(ctrlWin, HotkeyRules.Canonicalize(B(null, A | S))));
        // empty bindings never conflict.
        Assert.False(ConflictDetector.RecordingOverlapsCancel(HotkeyBinding.Empty(), esc));
    }

    [Fact]
    public void Conflict_messages_are_verbatim()
    {
        var rec = HotkeyRules.Canonicalize(B(0x1B, HotkeyModifiers.None));
        var esc = HotkeyDefaults.DefaultCancel();
        Assert.Equal("Choose a different combination from the cancel shortcut.", ConflictDetector.CheckRecording(rec, Array.Empty<HotkeyBinding>(), esc)!.Message);
        Assert.Equal("The cancel shortcut overlaps a start/stop shortcut. Choose another combination.", ConflictDetector.CheckCancel(esc, new[] { rec })!.Message);
        Assert.Null(ConflictDetector.CheckCancel(esc, new[] { HotkeyDefaults.DefaultRecording() }));
    }

    [Theory]
    [InlineData('H', W, "Conflicts with Windows voice typing")]
    [InlineData(0x20, W, "Conflicts with Windows input switching")]
    [InlineData('V', W, "Conflicts with Windows clipboard history")]
    [InlineData('S', W | S, "Conflicts with Windows Snipping Tool")]
    [InlineData(0x09, A, "Conflicts with Windows app switching")]
    [InlineData('L', W, "Conflicts with Windows lock")]
    [InlineData(0x1B, C | S, "Conflicts with Windows Task Manager")]
    public void Windows_reserved_shortcuts(int vk, HotkeyModifiers m, string message)
    {
        var b = HotkeyRules.Canonicalize(B(vk, m));
        Assert.Equal(message, ConflictDetector.SystemConflictMessage(b));
        var rec = ConflictDetector.CheckRecording(b, Array.Empty<HotkeyBinding>(), null)!;
        Assert.False(rec.Blocking);   // warning for recording shortcuts
        Assert.True(ConflictDetector.CheckCancel(b, Array.Empty<HotkeyBinding>())!.Blocking); // blocks cancel
    }

    [Fact]
    public void Non_reserved_shortcut_has_no_system_conflict()
    {
        Assert.Null(ConflictDetector.SystemConflictMessage(HotkeyRules.Canonicalize(B('H', C))));
        Assert.Null(ConflictDetector.SystemConflictMessage(HotkeyDefaults.DefaultRecording()));
        // extra modifiers make it a different combination
        Assert.Null(ConflictDetector.SystemConflictMessage(HotkeyRules.Canonicalize(B('H', W | S))));
    }
}

public class StorageAndDiagnosticsTests
{
    [Fact]
    public void Paths_honor_data_root_override_and_create_on_demand()
    {
        using var t = new TempDir();
        var old = RemyPaths.OverrideRoot;
        try
        {
            RemyPaths.OverrideRoot = t.Path;
            Assert.Equal(Path.Combine(t.Path, "Roaming", "settings.json"), RemyPaths.SettingsFile);
            Assert.Equal(Path.Combine(t.Path, "Roaming", "CleanupProfiles", "profiles.json"), RemyPaths.CleanupProfilesFile);
            Assert.Equal(Path.Combine(t.Path, "Local", "History", "history.sqlite"), RemyPaths.HistoryDatabaseFile);
            Assert.StartsWith(Path.Combine(t.Path, "Local"), RemyPaths.ModelsDirectory);
            Assert.False(Directory.Exists(RemyPaths.RecoveryDirectory));
            RemyPaths.EnsureRecoveryDirectory();
            Assert.True(Directory.Exists(RemyPaths.RecoveryDirectory));
            Assert.True(RemyPaths.IsConfinedTo(RemyPaths.ModelsDirectory, Path.Combine(RemyPaths.ModelsDirectory, "a", "b.bin")));
            Assert.False(RemyPaths.IsConfinedTo(RemyPaths.ModelsDirectory, Path.Combine(RemyPaths.ModelsDirectory, "..", "x")));
            Assert.False(RemyPaths.IsConfinedTo(RemyPaths.ModelsDirectory, RemyPaths.ModelsDirectory));
        }
        finally { RemyPaths.OverrideRoot = old; }
    }

    [Fact]
    public void Error_category_vocabulary()
    {
        Assert.Equal("cancelled", RemyDiagnostics.ErrorCategory(new OperationCanceledException()));
        Assert.Equal("timeout", RemyDiagnostics.ErrorCategory(new TimeoutException()));
        Assert.Equal("timeout", RemyDiagnostics.ErrorCategory(new TaskCanceledException("t", new TimeoutException())));
        Assert.Equal("transport", RemyDiagnostics.ErrorCategory(new HttpRequestException("secret body")));
        Assert.Equal("other", RemyDiagnostics.ErrorCategory(new InvalidOperationException("x")));
    }

    [Fact]
    public void Log_writes_daily_file_never_includes_exception_text_and_prunes()
    {
        using var t = new TempDir();
        var old = RemyPaths.OverrideRoot;
        var oldNow = RemyLog.Now;
        try
        {
            RemyPaths.OverrideRoot = t.Path;
            var now = new DateTime(2026, 11, 9, 12, 0, 0);
            RemyLog.Now = () => now;
            Directory.CreateDirectory(RemyPaths.LogsDirectory);
            File.WriteAllText(Path.Combine(RemyPaths.LogsDirectory, "remy-20261101.log"), "old");
            File.WriteAllText(Path.Combine(RemyPaths.LogsDirectory, "remy-20261105.log"), "recent");
            RemyLog.Error("test", "failed", new InvalidOperationException("SENTINEL-TRANSCRIPT"));
            var file = RemyLog.LogFileFor(now);
            Assert.EndsWith("remy-20261109.log", file);
            var text = File.ReadAllText(file);
            Assert.Contains("category=other", text);
            Assert.DoesNotContain("SENTINEL", text);
            RemyLog.Prune(now);
            Assert.False(File.Exists(Path.Combine(RemyPaths.LogsDirectory, "remy-20261101.log")));
            Assert.True(File.Exists(Path.Combine(RemyPaths.LogsDirectory, "remy-20261105.log")));
        }
        finally { RemyPaths.OverrideRoot = old; RemyLog.Now = oldNow; }
    }

    [Fact]
    public void PerfTrace_is_silent_when_disabled()
    {
        var lines = new List<string>();
        var oldSink = RemyLog.Sink; var oldEnabled = PerfTrace.Enabled;
        try
        {
            RemyLog.Sink = lines.Add;
            PerfTrace.Enabled = false;
            using (PerfTrace.Begin("stt.finalize")) { }
            Assert.Empty(lines);
            PerfTrace.Enabled = true;
            using (PerfTrace.Begin("stt.finalize")) { }
            Assert.Contains(lines, l => l.Contains("stt.finalize duration_ms="));
        }
        finally { RemyLog.Sink = oldSink; PerfTrace.Enabled = oldEnabled; }
    }
}
