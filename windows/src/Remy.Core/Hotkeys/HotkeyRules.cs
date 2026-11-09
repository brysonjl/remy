namespace Remy.Core.Hotkeys;

/// <summary>Pure hotkey logic shared by settings, the shortcut recorder UI and the keyboard hook
/// (pipeline.md 1.2-1.3, settings-models.md 1.5). No Win32 calls.</summary>
public static class HotkeyRules
{
    private readonly record struct Group(HotkeyModifiers Generic, HotkeyModifiers Left, HotkeyModifiers Right, string Name);

    private static readonly Group[] Groups =
    {
        new(HotkeyModifiers.Ctrl, HotkeyModifiers.LeftCtrl, HotkeyModifiers.RightCtrl, "Ctrl"),
        new(HotkeyModifiers.Alt, HotkeyModifiers.LeftAlt, HotkeyModifiers.RightAlt, "Alt"),
        new(HotkeyModifiers.Shift, HotkeyModifiers.LeftShift, HotkeyModifiers.RightShift, "Shift"),
        new(HotkeyModifiers.Win, HotkeyModifiers.LeftWin, HotkeyModifiers.RightWin, "Win"),
    };

    /// <summary>A left/right flag implies its generic flag.</summary>
    public static HotkeyModifiers Normalize(HotkeyModifiers m)
    {
        foreach (var g in Groups)
            if ((m & (g.Left | g.Right)) != 0) m |= g.Generic;
        return m;
    }

    /// <summary>Remove side-specific flags, keeping only the generic ones.</summary>
    public static HotkeyModifiers GenericOnly(HotkeyModifiers m)
    {
        m = Normalize(m);
        foreach (var g in Groups) m &= ~(g.Left | g.Right);
        return m;
    }

    /// <summary>Generic modifier flags only, regardless of sides (used to compare combos).</summary>
    public static HotkeyModifiers GroupSet(HotkeyModifiers m) => GenericOnly(m);

    /// <summary>Canonical form: Caps Lock cleared; a modifier virtual key becomes modifier-only;
    /// side flags imply generic; display name regenerated ("" when empty).</summary>
    public static HotkeyBinding Canonicalize(HotkeyBinding b)
    {
        if (b.VirtualKey == HotkeyKeyNames.VkCapital)
            return b with { VirtualKey = null, Modifiers = HotkeyModifiers.None, DisplayName = "" };

        var vk = b.VirtualKey;
        var mods = Normalize(b.Modifiers);
        if (vk is int k && HotkeyKeyNames.IsModifierKey(k))
        {
            mods = Normalize(mods | HotkeyKeyNames.ModifierForKey(k));
            vk = null;
        }
        if (vk is null && mods == HotkeyModifiers.None)
            return b with { VirtualKey = null, Modifiers = HotkeyModifiers.None, DisplayName = "" };

        return b with { VirtualKey = vk, Modifiers = mods, DisplayName = BuildDisplayName(vk, mods) };
    }

    /// <summary>Order: Ctrl, Alt, Shift, Win, key; side-aware as LCtrl/RCtrl; joined with "+".</summary>
    public static string BuildDisplayName(int? virtualKey, HotkeyModifiers modifiers)
    {
        var m = Normalize(modifiers);
        var parts = new List<string>(5);
        foreach (var g in Groups)
        {
            if ((m & g.Generic) == 0) continue;
            bool l = (m & g.Left) != 0, r = (m & g.Right) != 0;
            parts.Add(l && !r ? "L" + g.Name : r && !l ? "R" + g.Name : g.Name);
        }
        if (virtualKey is int k) parts.Add(HotkeyKeyNames.Name(k));
        return string.Join("+", parts);
    }

    /// <summary>Whether a single modifier group of <paramref name="target"/> is satisfied by <paramref name="held"/>.
    /// Generic target: either side. Side-specific target: that side must be held.</summary>
    private static bool GroupSatisfied(Group g, HotkeyModifiers target, HotkeyModifiers held)
    {
        if ((target & g.Generic) == 0) return true;
        bool needL = (target & g.Left) != 0, needR = (target & g.Right) != 0;
        if (!needL && !needR) return (held & (g.Generic | g.Left | g.Right)) != 0;
        if (needL && (held & g.Left) == 0) return false;
        if (needR && (held & g.Right) == 0) return false;
        return true;
    }

    /// <summary>Keyed shortcuts: every target modifier is held (sides respected); extra modifiers are allowed.</summary>
    public static bool KeyedModifiersMatch(HotkeyModifiers target, HotkeyModifiers held)
    {
        target = Normalize(target); held = Normalize(held);
        foreach (var g in Groups)
            if (!GroupSatisfied(g, target, held)) return false;
        return true;
    }

    /// <summary>Modifier-only shortcuts: every target modifier is held and no modifier group outside the target is held.</summary>
    public static bool ModifierOnlyModifiersMatch(HotkeyModifiers target, HotkeyModifiers held)
    {
        target = Normalize(target); held = Normalize(held);
        foreach (var g in Groups)
        {
            if (!GroupSatisfied(g, target, held)) return false;
            if ((target & g.Generic) == 0 && (held & (g.Generic | g.Left | g.Right)) != 0) return false;
        }
        return true;
    }

    /// <summary>Key-down of <paramref name="virtualKey"/> with <paramref name="held"/> modifiers triggers this keyed binding.</summary>
    public static bool MatchesKeyed(HotkeyBinding binding, int virtualKey, HotkeyModifiers held) =>
        binding.VirtualKey == virtualKey && KeyedModifiersMatch(binding.Modifiers, held);

    /// <summary>The held modifier set activates this modifier-only binding.</summary>
    public static bool MatchesModifierOnly(HotkeyBinding binding, HotkeyModifiers held) =>
        binding.IsModifierOnly && ModifierOnlyModifiersMatch(binding.Modifiers, held);

    /// <summary>Two bindings describe the same shortcut (same key and same modifier flags after normalization).</summary>
    public static bool SameShortcut(HotkeyBinding a, HotkeyBinding b) =>
        a.VirtualKey == b.VirtualKey && Normalize(a.Modifiers) == Normalize(b.Modifiers);
}
