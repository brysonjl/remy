using System.Text.Json.Nodes;
using Remy.Core.Settings;

namespace Remy.Core.Hotkeys;

/// <summary>Default bindings and JSON (de)serialization: fields id, virtualKey, modifiers, displayName, mode.</summary>
public static class HotkeyDefaults
{
    public const int MaxRecordingBindings = 5;

    /// <summary>Modifier-only Ctrl+Win, hold to record (ARCHITECTURE.md).</summary>
    public static HotkeyBinding DefaultRecording() =>
        HotkeyRules.Canonicalize(new HotkeyBinding(Guid.NewGuid(), null, HotkeyModifiers.Ctrl | HotkeyModifiers.Win, "", HotkeyMode.HoldToRecord));

    /// <summary>Esc, no modifiers, immediate-or-hold per holdToCancel.</summary>
    public static HotkeyBinding DefaultCancel() =>
        new(Guid.NewGuid(), HotkeyKeyNames.VkEscape, HotkeyModifiers.None, "Esc", HotkeyMode.HandsFreeToggle);
}

public static class HotkeyBindingJson
{
    public static JsonObject ToJson(HotkeyBinding b)
    {
        var o = new JsonObject { ["id"] = b.Id.ToString() };
        if (b.VirtualKey is int vk) o["virtualKey"] = vk;
        o["modifiers"] = (int)b.Modifiers;
        o["displayName"] = b.DisplayName;
        o["mode"] = RemyEnumNames.HotkeyModes.ToRaw(b.Mode);
        return o;
    }

    public static JsonArray ToJson(IEnumerable<HotkeyBinding> bindings)
    {
        var a = new JsonArray();
        foreach (var b in bindings) a.Add(ToJson(b));
        return a;
    }

    /// <summary>Tolerant parse; returns null if the node is not an object. Result is canonicalized.</summary>
    public static HotkeyBinding? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        Guid id = Guid.NewGuid();
        if (TryString(o["id"], out var ids) && Guid.TryParse(ids, out var g)) id = g;
        int? vk = null;
        if (o["virtualKey"] is JsonValue vv && vv.TryGetValue<int>(out var k) && k is > 0 and < 256) vk = k;
        var mods = HotkeyModifiers.None;
        if (o["modifiers"] is JsonValue mv && mv.TryGetValue<int>(out var m) && m >= 0 && m < (1 << 12)) mods = (HotkeyModifiers)m;
        var mode = HotkeyMode.HoldToRecord;
        if (TryString(o["mode"], out var ms)) mode = RemyEnumNames.HotkeyModes.ParseOrDefault(ms, HotkeyMode.HoldToRecord);
        var name = TryString(o["displayName"], out var dn) ? dn : "";
        return HotkeyRules.Canonicalize(new HotkeyBinding(id, vk, mods, name, mode));
    }

    public static List<HotkeyBinding>? ListFromJson(JsonNode? node)
    {
        if (node is not JsonArray a) return null;
        var list = new List<HotkeyBinding>();
        foreach (var n in a)
            if (FromJson(n) is { } b) list.Add(b);
        return list;
    }

    private static bool TryString(JsonNode? n, out string s)
    {
        s = "";
        if (n is JsonValue v && v.TryGetValue<string>(out var str) && str is not null) { s = str; return true; }
        return false;
    }
}
