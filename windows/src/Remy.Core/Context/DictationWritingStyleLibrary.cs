using System.Globalization;
using Remy.Core.Settings;

namespace Remy.Core.Context;

/// <summary>A writing style choice: a built-in (stable raw id) or a user-defined custom style (v0.2-features.md §6.2).</summary>
public readonly record struct DictationWritingStyleSelection
{
    private DictationWritingStyleSelection(DictationWritingStyle? builtIn, Guid? custom) { BuiltInStyle = builtIn; CustomId = custom; }

    public DictationWritingStyle? BuiltInStyle { get; }
    public Guid? CustomId { get; }

    public bool IsCustom => CustomId is not null;

    public static DictationWritingStyleSelection FromBuiltIn(DictationWritingStyle style) => new(style, null);
    public static DictationWritingStyleSelection FromCustom(Guid id) => new(null, id);

    /// <summary>Built-in: the raw value ("formal"). Custom: "custom:" + UUID (uppercase D).</summary>
    public string Id => CustomId is { } g
        ? "custom:" + g.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant()
        : (BuiltInStyle ?? DictationWritingStyle.Original).ToRaw();

    /// <summary>Raw value, or "custom:" + valid UUID; anything else is null.</summary>
    public static DictationWritingStyleSelection? Parse(string? id)
    {
        if (id is null) return null;
        if (DictationText.TryParseStyle(id, out var s)) return FromBuiltIn(s);
        const string prefix = "custom:";
        if (id.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(id[prefix.Length..], "D", out var g))
            return FromCustom(g);
        return null;
    }

    /// <summary>Built-in outside the category's options becomes casual; custom is unchanged (the library decides).</summary>
    public DictationWritingStyleSelection BuiltInSanitized(DictationContextCategory category) =>
        BuiltInStyle is { } s ? FromBuiltIn(s.Sanitized(category)) : this;
}

public sealed record DictationCustomWritingStyle(Guid Id, string Name, string Instruction);

/// <summary>The style frozen into a session: name and instruction, so later edits never change an in-flight dictation.</summary>
public sealed record DictationResolvedWritingStyle(DictationWritingStyleSelection Selection, string DisplayName, string Instruction)
{
    public static DictationResolvedWritingStyle BuiltIn(DictationWritingStyle style) =>
        new(DictationWritingStyleSelection.FromBuiltIn(style), style.DisplayName(), style.CleanupInstruction());

    /// <summary>True when the instruction is not the shipped text of a built-in (custom styles: always).</summary>
    public bool IsUserAuthored =>
        Instruction != (Selection.BuiltInStyle is { } b ? b.CleanupInstruction() : null);

    public DictationResolvedWritingStyle Sanitized(DictationContextCategory category)
    {
        if (Selection.BuiltInStyle is not { } s) return this;
        return DictationText.StyleOptions(category).Contains(s) ? this : BuiltIn(s.Sanitized(category));
    }
}

public enum DictationWritingStyleError { EmptyName, NameTooLong, DuplicateName, EmptyInstruction, InstructionTooLong }

public sealed class DictationWritingStyleValidationException(DictationWritingStyleError error)
    : Exception(MessageFor(error))
{
    public DictationWritingStyleError Error { get; } = error;

    public static string MessageFor(DictationWritingStyleError e) => e switch
    {
        DictationWritingStyleError.EmptyName => "Enter a style name.",
        DictationWritingStyleError.NameTooLong => "Use a name of 40 characters or fewer.",
        DictationWritingStyleError.DuplicateName => "A style with this name already exists.",
        DictationWritingStyleError.EmptyInstruction => "Enter instructions for this style.",
        _ => "Use instructions of 1000 characters or fewer.",
    };
}

/// <summary>Edited built-in instructions and custom styles (v0.2-features.md §6.2). Mutable; use <see cref="Copy"/>
/// to edit a draft, then assign it to the settings.</summary>
public sealed class DictationWritingStyleLibrary : IEquatable<DictationWritingStyleLibrary>
{
    public const int MaximumNameLength = 40;
    public const int MaximumInstructionLength = 1000;

    private readonly List<DictationCustomWritingStyle> _custom = [];
    private readonly Dictionary<string, string> _overrides = new(StringComparer.Ordinal);

    public IReadOnlyList<DictationCustomWritingStyle> CustomStyles => _custom;
    public IReadOnlyDictionary<string, string> InstructionOverrides => _overrides;

    public static readonly DictationWritingStyle[] BuiltInStyles = Enum.GetValues<DictationWritingStyle>();

    public DictationWritingStyleLibrary Copy()
    {
        var c = new DictationWritingStyleLibrary();
        c._custom.AddRange(_custom);
        foreach (var kv in _overrides) c._overrides[kv.Key] = kv.Value;
        return c;
    }

    // ---- built-in instructions

    public string Instruction(DictationWritingStyle style) =>
        _overrides.TryGetValue(style.ToRaw(), out var t) ? t : style.CleanupInstruction();

    public bool IsCustomized(DictationWritingStyle style) => _overrides.ContainsKey(style.ToRaw());

    public void SetInstruction(string text, DictationWritingStyle style)
    {
        var v = ValidatedInstruction(text);
        if (v == style.CleanupInstruction()) _overrides.Remove(style.ToRaw());
        else _overrides[style.ToRaw()] = v;
    }

    public void ResetInstruction(DictationWritingStyle style) => _overrides.Remove(style.ToRaw());

    // ---- custom styles

    public DictationCustomWritingStyle AddCustomStyle(string name, string instruction) =>
        AddCustomStyle(Guid.NewGuid(), name, instruction);

    /// <summary>Adds with a known id (loading saved settings).</summary>
    public DictationCustomWritingStyle AddCustomStyle(Guid id, string name, string instruction)
    {
        var s = new DictationCustomWritingStyle(id, ValidatedName(name, null), ValidatedInstruction(instruction));
        _custom.Add(s);
        return s;
    }

    public void UpdateCustomStyle(Guid id, string name, string instruction)
    {
        var i = _custom.FindIndex(s => s.Id == id);
        if (i < 0) return;
        _custom[i] = new DictationCustomWritingStyle(id, ValidatedName(name, id), ValidatedInstruction(instruction));
    }

    public void RemoveCustomStyle(Guid id) => _custom.RemoveAll(s => s.Id == id);

    public DictationCustomWritingStyle? Custom(Guid id) => _custom.FirstOrDefault(s => s.Id == id);

    // ---- selections

    public string DisplayName(DictationWritingStyleSelection selection)
    {
        if (selection.BuiltInStyle is { } b) return b.DisplayName();
        return Custom(selection.CustomId!.Value)?.Name ?? "Deleted style";
    }

    public DictationWritingStyleSelection Sanitized(DictationWritingStyleSelection selection, DictationContextCategory category)
    {
        if (selection.BuiltInStyle is { } b) return DictationWritingStyleSelection.FromBuiltIn(b.Sanitized(category));
        return Custom(selection.CustomId!.Value) is null
            ? DictationWritingStyleSelection.FromBuiltIn(DictationText.DefaultStyle(category))
            : selection;
    }

    public DictationResolvedWritingStyle Resolved(DictationWritingStyleSelection selection, DictationContextCategory category)
    {
        var s = Sanitized(selection, category);
        if (s.BuiltInStyle is { } b) return new(s, b.DisplayName(), Instruction(b));
        var c = Custom(s.CustomId!.Value)!;
        return new(s, c.Name, c.Instruction);
    }

    // ---- validation

    /// <summary>Whitespace runs (including newlines) collapse to one space. Throws on empty, too long or duplicate.</summary>
    public string ValidatedName(string name, Guid? excluding)
    {
        var collapsed = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0) throw new DictationWritingStyleValidationException(DictationWritingStyleError.EmptyName);
        if (Length(collapsed) > MaximumNameLength) throw new DictationWritingStyleValidationException(DictationWritingStyleError.NameTooLong);
        var duplicate = BuiltInStyles.Any(b => string.Equals(b.DisplayName(), collapsed, StringComparison.OrdinalIgnoreCase))
            || _custom.Any(c => c.Id != excluding && string.Equals(c.Name, collapsed, StringComparison.OrdinalIgnoreCase));
        if (duplicate) throw new DictationWritingStyleValidationException(DictationWritingStyleError.DuplicateName);
        return collapsed;
    }

    public static string ValidatedInstruction(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) throw new DictationWritingStyleValidationException(DictationWritingStyleError.EmptyInstruction);
        if (Length(t) > MaximumInstructionLength) throw new DictationWritingStyleValidationException(DictationWritingStyleError.InstructionTooLong);
        return t;
    }

    /// <summary>Characters as extended grapheme clusters (Swift String.count).</summary>
    public static int Length(string s) => new StringInfo(s).LengthInTextElements;

    // ---- equality (round trip)

    public bool Equals(DictationWritingStyleLibrary? other) =>
        other is not null && _custom.SequenceEqual(other._custom)
        && _overrides.Count == other._overrides.Count
        && _overrides.All(kv => other._overrides.TryGetValue(kv.Key, out var v) && v == kv.Value);

    public override bool Equals(object? obj) => Equals(obj as DictationWritingStyleLibrary);
    public override int GetHashCode() => HashCode.Combine(_custom.Count, _overrides.Count);
}
