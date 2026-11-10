using System.Text.Json.Nodes;
using Remy.Core.Hotkeys;
using Remy.Core.Settings;

namespace Remy.Core.Transforms;

/// <summary>Operations over the persisted transform list (v0.2-features.md §8.2-8.3). Stateless over <see cref="RemySettings.TextTransforms"/>:
/// every mutation writes the normalized list back, so several store instances over one settings object agree.</summary>
public sealed class TransformStore(RemySettings settings)
{
    public const int MaximumCustomTransforms = 8;

    public IReadOnlyList<TextTransform> Transforms => settings.TextTransforms;

    public TextTransform? Transform(Guid id) => Transforms.FirstOrDefault(t => t.Id == id);

    public int CustomCount => Transforms.Count(t => t.BuiltIn is null);

    public bool CanAddCustom => CustomCount < MaximumCustomTransforms;

    /// <summary>Shortcuts of enabled transforms, in list order.</summary>
    public IReadOnlyList<HotkeyBinding> ActiveShortcutBindings =>
        Transforms.Where(t => t.IsEnabled && t.Shortcut is { HasBinding: true }).Select(t => t.Shortcut!).ToList();

    public TextTransform? AddCustom()
    {
        if (!CanAddCustom) return null;
        var t = new TextTransform(Guid.NewGuid(), "New transform", "", null, true, null);
        Write(Transforms.Append(t));
        return Transform(t.Id);
    }

    public void RemoveCustom(Guid id)
    {
        if (Transform(id) is not { BuiltIn: null }) return;
        Write(Transforms.Where(t => t.Id != id));
    }

    public void SetName(string name, Guid id) => Update(id, t => t.BuiltIn is null ? t with { Name = name } : t);

    public void SetPrompt(string prompt, Guid id) => Update(id, t => t with { Prompt = prompt });

    public void SetEnabled(bool enabled, Guid id) => Update(id, t => t with { IsEnabled = enabled });

    public void ResetPrompt(Guid id) => Update(id, t => t.BuiltIn is { } b ? t with { Prompt = b.DefaultPrompt() } : t);

    public void SetShortcut(Guid id, int? virtualKey, HotkeyModifiers modifiers) =>
        Update(id, t => t with { Shortcut = HotkeyRules.Canonicalize(new HotkeyBinding(id, virtualKey, modifiers, "", HotkeyMode.HandsFreeToggle)) });

    public void ClearShortcut(Guid id) => Update(id, t => t with { Shortcut = null });

    void Update(Guid id, Func<TextTransform, TextTransform> change) =>
        Write(Transforms.Select(t => t.Id == id ? change(t) : t));

    void Write(IEnumerable<TextTransform> list) => settings.TextTransforms = list.ToList();

    // ------------------------------------------------------------------ normalization and JSON

    /// <summary>Built-ins first (Polish, Prompt Engineer), then up to 8 customs; duplicate ids dropped (first wins);
    /// every shortcut re-keyed to its transform with toggle mode (§8.2).</summary>
    public static IReadOnlyList<TextTransform> Normalize(IEnumerable<TextTransform>? stored)
    {
        var unique = new List<TextTransform>();
        var seen = new HashSet<Guid>();
        foreach (var t in stored ?? [])
            if (seen.Add(t.Id)) unique.Add(t);

        var result = new List<TextTransform>();
        foreach (var b in BuiltInTransforms.All)
        {
            var found = unique.FirstOrDefault(t => t.BuiltIn == b);
            var t = found ?? b.Default();
            t = t with { Name = b.DisplayName(), BuiltInId = b.BuiltInId() };
            if (string.IsNullOrWhiteSpace(t.Prompt)) t = t with { Prompt = b.DefaultPrompt() };
            result.Add(t);
        }
        result.AddRange(unique.Where(t => t.BuiltIn is null && !BuiltInTransforms.IsBuiltInId(t.Id)).Take(MaximumCustomTransforms));

        for (int i = 0; i < result.Count; i++)
        {
            var t = result[i];
            result[i] = t with
            {
                Shortcut = t.Shortcut is { HasBinding: true } s
                    ? HotkeyRules.Canonicalize(s with { Id = t.Id, Mode = HotkeyMode.HandsFreeToggle })
                    : null,
            };
        }
        return result;
    }

    public static JsonNode ToJson(IEnumerable<TextTransform> transforms)
    {
        var arr = new JsonArray();
        foreach (var t in transforms)
        {
            var o = new JsonObject
            {
                ["id"] = t.Id.ToString("D").ToUpperInvariant(),
                ["name"] = t.Name,
                ["prompt"] = t.Prompt,
            };
            if (t.Shortcut is { } s) o["shortcut"] = HotkeyBindingJson.ToJson(s);
            o["isEnabled"] = t.IsEnabled;
            if (t.BuiltInId is { } b) o["builtInID"] = b;
            arr.Add(o);
        }
        return arr;
    }

    /// <summary>Lenient decode (§8.2). Anything that is not an array yields an empty list; bad entries are dropped.</summary>
    public static List<TextTransform> FromJson(JsonNode? node)
    {
        var list = new List<TextTransform>();
        if (node is not JsonArray arr) return list;
        foreach (var item in arr)
        {
            if (item is not JsonObject o) continue;
            if (!Str(o["id"], out var ids) || !Guid.TryParse(ids, out var id)) continue;
            if (!Str(o["name"], out var name) || !Str(o["prompt"], out var prompt)) continue;
            HotkeyBinding? shortcut = null;
            try { shortcut = HotkeyBindingJson.FromJson(o["shortcut"]); } catch { }
            bool enabled = o["isEnabled"] is JsonValue ev && ev.TryGetValue<bool>(out var eb) ? eb : true;
            string? builtIn = Str(o["builtInID"], out var bid) ? bid : null;
            list.Add(new TextTransform(id, name, prompt, shortcut, enabled, builtIn));
        }
        return list;
    }

    static bool Str(JsonNode? n, out string s)
    {
        s = "";
        if (n is JsonValue v && v.TryGetValue<string>(out var str) && str is not null) { s = str; return true; }
        return false;
    }
}
