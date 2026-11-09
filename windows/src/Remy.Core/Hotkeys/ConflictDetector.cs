namespace Remy.Core.Hotkeys;

/// <summary>A conflict message. Blocking conflicts must be refused; non-blocking ones are shown as warnings.</summary>
public sealed record HotkeyConflict(string Message, bool Blocking);

/// <summary>Duplicate, recording-vs-cancel and Windows-reserved checks (pipeline.md 1.5, ARCHITECTURE.md).</summary>
public static class ConflictDetector
{
    public const string DuplicateMessage = "Duplicate of another shortcut";
    public const string RecordingVsCancelMessage = "Choose a different combination from the cancel shortcut.";
    public const string CancelVsRecordingMessage = "The cancel shortcut overlaps a start/stop shortcut. Choose another combination.";

    private sealed record SystemShortcut(int? VirtualKey, HotkeyModifiers Modifiers, string Name);

    private static readonly SystemShortcut[] SystemShortcuts =
    {
        new('H', HotkeyModifiers.Win, "voice typing"),
        new(HotkeyKeyNames.VkSpace, HotkeyModifiers.Win, "input switching"),
        new('V', HotkeyModifiers.Win, "clipboard history"),
        new('S', HotkeyModifiers.Win | HotkeyModifiers.Shift, "Snipping Tool"),
        new(HotkeyKeyNames.VkTab, HotkeyModifiers.Alt, "app switching"),
        new('L', HotkeyModifiers.Win, "lock"),
        new(HotkeyKeyNames.VkEscape, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, "Task Manager"),
        new(0x2E, HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, "security screen"),
    };

    /// <summary>"Conflicts with Windows &lt;name&gt;" when the binding is exactly a reserved Windows shortcut, else null.</summary>
    public static string? SystemConflictMessage(HotkeyBinding binding)
    {
        if (binding.VirtualKey is null) return null;
        var groups = HotkeyRules.GroupSet(binding.Modifiers);
        foreach (var s in SystemShortcuts)
            if (s.VirtualKey == binding.VirtualKey && s.Modifiers == groups)
                return $"Conflicts with Windows {s.Name}";
        return null;
    }

    /// <summary>True when the recording shortcut and the cancel shortcut would fire from the same gesture
    /// (same key with satisfied modifiers; a modifier-only recording prefix of a keyed cancel;
    /// two modifier-only shortcuts where one contains the other).</summary>
    public static bool RecordingOverlapsCancel(HotkeyBinding recording, HotkeyBinding cancel)
    {
        if (!recording.HasBinding || !cancel.HasBinding) return false;
        var rm = HotkeyRules.Normalize(recording.Modifiers);
        var cm = HotkeyRules.Normalize(cancel.Modifiers);
        if (recording.VirtualKey is not null && cancel.VirtualKey is not null)
            return recording.VirtualKey == cancel.VirtualKey && HotkeyRules.KeyedModifiersMatch(rm, cm);
        if (recording.VirtualKey is null && cancel.VirtualKey is not null)
            return HotkeyRules.KeyedModifiersMatch(rm, cm);
        if (recording.VirtualKey is null && cancel.VirtualKey is null)
            return HotkeyRules.KeyedModifiersMatch(rm, cm) || HotkeyRules.KeyedModifiersMatch(cm, rm);
        return false;
    }

    /// <summary>Check a recording-shortcut candidate. <paramref name="others"/> are the other recording bindings
    /// (excluding the candidate). Duplicate and cancel overlap block; a Windows reserved combination is a warning.</summary>
    public static HotkeyConflict? CheckRecording(HotkeyBinding candidate, IEnumerable<HotkeyBinding> others, HotkeyBinding? cancel)
    {
        if (!candidate.HasBinding) return null;
        foreach (var o in others)
            if (o.Id != candidate.Id && o.HasBinding && HotkeyRules.SameShortcut(candidate, o))
                return new HotkeyConflict(DuplicateMessage, Blocking: true);
        if (cancel is not null && RecordingOverlapsCancel(candidate, cancel))
            return new HotkeyConflict(RecordingVsCancelMessage, Blocking: true);
        var sys = SystemConflictMessage(candidate);
        return sys is null ? null : new HotkeyConflict(sys, Blocking: false);
    }

    /// <summary>Check a cancel-shortcut candidate. Overlap with any recording shortcut and Windows reserved combos block.</summary>
    public static HotkeyConflict? CheckCancel(HotkeyBinding candidate, IEnumerable<HotkeyBinding> recordings)
    {
        if (!candidate.HasBinding) return null;
        foreach (var r in recordings)
            if (RecordingOverlapsCancel(r, candidate))
                return new HotkeyConflict(CancelVsRecordingMessage, Blocking: true);
        var sys = SystemConflictMessage(candidate);
        return sys is null ? null : new HotkeyConflict(sys, Blocking: true);
    }
}
