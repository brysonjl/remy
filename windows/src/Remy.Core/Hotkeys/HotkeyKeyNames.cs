namespace Remy.Core.Hotkeys;

/// <summary>Windows virtual-key codes used by hotkeys and a name table for display.</summary>
public static class HotkeyKeyNames
{
    public const int VkBack = 0x08, VkTab = 0x09, VkReturn = 0x0D, VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12,
        VkPause = 0x13, VkCapital = 0x14, VkEscape = 0x1B, VkSpace = 0x20, VkLWin = 0x5B, VkRWin = 0x5C,
        VkLShift = 0xA0, VkRShift = 0xA1, VkLControl = 0xA2, VkRControl = 0xA3, VkLMenu = 0xA4, VkRMenu = 0xA5;

    private static readonly Dictionary<int, string> Names = Build();

    private static Dictionary<int, string> Build()
    {
        var d = new Dictionary<int, string>
        {
            [0x08] = "Backspace", [0x09] = "Tab", [0x0C] = "Clear", [0x0D] = "Enter", [0x13] = "Pause",
            [0x1B] = "Esc", [0x20] = "Space", [0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home",
            [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down", [0x2C] = "PrintScreen",
            [0x2D] = "Insert", [0x2E] = "Delete", [0x5D] = "Menu", [0x90] = "NumLock", [0x91] = "ScrollLock",
            [0x6A] = "Num*", [0x6B] = "Num+", [0x6C] = "NumSeparator", [0x6D] = "Num-", [0x6E] = "Num.", [0x6F] = "Num/",
            [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".", [0xBF] = "/", [0xC0] = "`",
            [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'",
            [0xA6] = "BrowserBack", [0xA7] = "BrowserForward", [0xA8] = "BrowserRefresh", [0xAD] = "VolumeMute",
            [0xAE] = "VolumeDown", [0xAF] = "VolumeUp", [0xB0] = "NextTrack", [0xB1] = "PrevTrack", [0xB2] = "Stop",
            [0xB3] = "PlayPause",
        };
        for (int c = 'A'; c <= 'Z'; c++) d[c] = ((char)c).ToString();
        for (int c = '0'; c <= '9'; c++) d[c] = ((char)c).ToString();
        for (int i = 0; i <= 9; i++) d[0x60 + i] = $"Num{i}";
        for (int i = 1; i <= 24; i++) d[0x6F + i] = $"F{i}";
        return d;
    }

    /// <summary>Display name for a (non-modifier) virtual key; unknown keys render as "Key 0xNN".</summary>
    public static string Name(int virtualKey) =>
        Names.TryGetValue(virtualKey, out var n) ? n : $"Key 0x{virtualKey:X2}";

    public static bool IsKnown(int virtualKey) => Names.ContainsKey(virtualKey);

    /// <summary>The modifier flags a modifier virtual key represents (side-specific + generic), or None.</summary>
    public static HotkeyModifiers ModifierForKey(int vk) => vk switch
    {
        VkControl => HotkeyModifiers.Ctrl,
        VkLControl => HotkeyModifiers.LeftCtrl | HotkeyModifiers.Ctrl,
        VkRControl => HotkeyModifiers.RightCtrl | HotkeyModifiers.Ctrl,
        VkMenu => HotkeyModifiers.Alt,
        VkLMenu => HotkeyModifiers.LeftAlt | HotkeyModifiers.Alt,
        VkRMenu => HotkeyModifiers.RightAlt | HotkeyModifiers.Alt,
        VkShift => HotkeyModifiers.Shift,
        VkLShift => HotkeyModifiers.LeftShift | HotkeyModifiers.Shift,
        VkRShift => HotkeyModifiers.RightShift | HotkeyModifiers.Shift,
        VkLWin => HotkeyModifiers.LeftWin | HotkeyModifiers.Win,
        VkRWin => HotkeyModifiers.RightWin | HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    public static bool IsModifierKey(int vk) => ModifierForKey(vk) != HotkeyModifiers.None;
}
