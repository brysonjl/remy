using System.Text;
using Remy.Core.Hotkeys;

namespace Remy.Platform.Hotkeys;

/// <summary>Display names for recorded shortcuts: Ctrl, Alt, Shift, Win (side-aware LCtrl/RCtrl...), then the key (ARCHITECTURE.md).</summary>
internal static class HotkeyNames
{
    public static string Display(HotkeyModifiers mods, int? vk)
    {
        var parts = new List<string>();
        Add(parts, mods, HotkeyModifiers.Ctrl, HotkeyModifiers.LeftCtrl, HotkeyModifiers.RightCtrl, "Ctrl");
        Add(parts, mods, HotkeyModifiers.Alt, HotkeyModifiers.LeftAlt, HotkeyModifiers.RightAlt, "Alt");
        Add(parts, mods, HotkeyModifiers.Shift, HotkeyModifiers.LeftShift, HotkeyModifiers.RightShift, "Shift");
        Add(parts, mods, HotkeyModifiers.Win, HotkeyModifiers.LeftWin, HotkeyModifiers.RightWin, "Win");
        if (vk is { } k) parts.Add(KeyName(k));
        return string.Join('+', parts);
    }

    static void Add(List<string> parts, HotkeyModifiers mods, HotkeyModifiers gen, HotkeyModifiers l, HotkeyModifiers r, string name)
    {
        bool hasL = mods.HasFlag(l), hasR = mods.HasFlag(r);
        if (hasL && hasR) parts.Add(name);
        else if (hasL) parts.Add("L" + name);
        else if (hasR) parts.Add("R" + name);
        else if (mods.HasFlag(gen)) parts.Add(name);
    }

    public static string KeyName(int vk)
    {
        if (vk is >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        if (vk is >= 0x60 and <= 0x69) return "Num" + (vk - 0x60);
        return vk switch
        {
            0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x1B => "Esc", 0x20 => "Space",
            0x21 => "PageUp", 0x22 => "PageDown", 0x23 => "End", 0x24 => "Home",
            0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
            0x2C => "PrintScreen", 0x2D => "Insert", 0x2E => "Delete", 0x13 => "Pause",
            0x6A => "Num*", 0x6B => "Num+", 0x6D => "Num-", 0x6E => "Num.", 0x6F => "Num/",
            0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`",
            0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
            0xA6 => "BrowserBack", 0xA7 => "BrowserForward", 0xB3 => "Play/Pause",
            _ => new StringBuilder("Key").Append(vk).ToString(),
        };
    }
}
