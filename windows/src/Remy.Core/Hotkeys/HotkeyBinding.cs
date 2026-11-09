namespace Remy.Core.Hotkeys;

public enum HotkeyMode { HoldToRecord, HandsFreeToggle }

/// <summary>Modifier set. A side-specific flag always implies its generic flag after <see cref="HotkeyRules.Normalize"/>.
/// A binding with only a generic flag accepts either side; with a side flag it requires that side.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1 << 0, Alt = 1 << 1, Shift = 1 << 2, Win = 1 << 3,
    LeftCtrl = 1 << 4, RightCtrl = 1 << 5, LeftAlt = 1 << 6, RightAlt = 1 << 7,
    LeftShift = 1 << 8, RightShift = 1 << 9, LeftWin = 1 << 10, RightWin = 1 << 11,
}

/// <summary>A recording or cancel shortcut. VirtualKey null = modifier-only. Windows virtual-key codes.</summary>
public sealed record HotkeyBinding(Guid Id, int? VirtualKey, HotkeyModifiers Modifiers, string DisplayName, HotkeyMode Mode)
{
    /// <summary>Has a key or a non-empty modifier set (pipeline.md 1.2).</summary>
    public bool HasBinding => VirtualKey is not null || Modifiers != HotkeyModifiers.None;

    /// <summary>Modifiers only, no key.</summary>
    public bool IsModifierOnly => VirtualKey is null && Modifiers != HotkeyModifiers.None;

    /// <summary>An empty ("Not set") binding.</summary>
    public static HotkeyBinding Empty(Guid? id = null, HotkeyMode mode = HotkeyMode.HoldToRecord) =>
        new(id ?? Guid.NewGuid(), null, HotkeyModifiers.None, "", mode);

    /// <summary>Canonical form (Caps Lock cleared, side flags imply generic, display name regenerated).</summary>
    public HotkeyBinding Canonical() => HotkeyRules.Canonicalize(this);
}
