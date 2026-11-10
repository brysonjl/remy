using Remy.Core.Hotkeys;

namespace Remy.Core.Transforms;

/// <summary>Transform shortcut overlap and conflict rules (v0.2-features.md §8.6). ConflictDetector stays untouched.</summary>
public static class TransformShortcutRules
{
    public const string OverlapsDictationMessage = "Overlaps a dictation shortcut. Choose another combination.";
    public const string OverlapsCancelMessage = "Overlaps the cancel shortcut. Choose another combination.";
    public const string TypesCharacterWarning = "This combination normally types a character. Remy will intercept it in every app.";

    /// <summary>Pressing one can also trigger the other; both must have a binding.</summary>
    public static bool ShortcutsOverlap(HotkeyBinding a, HotkeyBinding b)
    {
        if (!a.HasBinding || !b.HasBinding) return false;
        bool aInB = HotkeyRules.KeyedModifiersMatch(a.Modifiers, b.Modifiers);
        bool bInA = HotkeyRules.KeyedModifiersMatch(b.Modifiers, a.Modifiers);
        if (a.VirtualKey is not null && b.VirtualKey is not null) return a.VirtualKey == b.VirtualKey && (aInB || bInA);
        if (a.VirtualKey is null && b.VirtualKey is not null) return aInB;
        if (a.VirtualKey is not null && b.VirtualKey is null) return bInA;
        return aInB || bInA;
    }

    public static string UsedByTransformMessage(string displayName) => $"Already used by the “{displayName}” transform.";

    public static string CancelOverlapsTransformMessage(string displayName) => $"The cancel shortcut overlaps the “{displayName}” transform shortcut.";

    /// <summary>Blocking conflict for a transform shortcut candidate, or null. Checked in order: dictation, cancel, other transforms.</summary>
    public static string? TransformShortcutConflict(HotkeyBinding candidate, Guid transformId, IEnumerable<TextTransform> transforms,
        IEnumerable<HotkeyBinding> dictation, HotkeyBinding? cancel)
    {
        if (!candidate.HasBinding) return null;
        foreach (var d in dictation)
            if (ShortcutsOverlap(candidate, d)) return OverlapsDictationMessage;
        if (cancel is not null && ShortcutsOverlap(candidate, cancel)) return OverlapsCancelMessage;
        foreach (var t in transforms)
            if (t.Id != transformId && t.Shortcut is { } s && ShortcutsOverlap(candidate, s)) return UsedByTransformMessage(t.DisplayName);
        return null;
    }

    /// <summary>The transform (enabled or not) whose shortcut overlaps <paramref name="candidate"/>, or null. Used when recording dictation or cancel shortcuts.</summary>
    public static TextTransform? TransformUsingShortcut(HotkeyBinding candidate, IEnumerable<TextTransform> transforms)
    {
        if (!candidate.HasBinding) return null;
        foreach (var t in transforms)
            if (t.Shortcut is { } s && ShortcutsOverlap(candidate, s)) return t;
        return null;
    }

    /// <summary>Non-blocking warning: a key that is not F1-F24 whose generic modifiers are a subset of {Shift}.
    /// WINDOWS DECISION: Mac uses {Option, Shift}; Alt does not type characters on Windows.</summary>
    public static string? TypingCharacterWarning(HotkeyBinding binding)
    {
        if (binding.VirtualKey is not int vk) return null;
        if (vk is >= 0x70 and <= 0x87) return null;
        var generic = HotkeyRules.GenericOnly(binding.Modifiers);
        return (generic & ~HotkeyModifiers.Shift) == HotkeyModifiers.None ? TypesCharacterWarning : null;
    }
}
