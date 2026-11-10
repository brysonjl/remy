using Remy.Core.Hotkeys;

namespace Remy.Core.Transforms;

/// <summary>Fixed-identity transforms that ship with Remy (v0.2-features.md §8.1).</summary>
public enum BuiltInTransform { Polish, PromptEngineer }

/// <summary>What a transform sends to the provider: its display name and instructions. A future command mode can supply a spoken one.</summary>
public sealed record TransformInstruction(string Name, string Prompt);

public static class BuiltInTransforms
{
    public static IReadOnlyList<BuiltInTransform> All { get; } = [BuiltInTransform.Polish, BuiltInTransform.PromptEngineer];

    public static BuiltInTransform? Parse(string? builtInId) => builtInId switch
    {
        "polish" => BuiltInTransform.Polish,
        "promptEngineer" => BuiltInTransform.PromptEngineer,
        _ => null,
    };

    public static string BuiltInId(this BuiltInTransform b) => b == BuiltInTransform.Polish ? "polish" : "promptEngineer";

    public static Guid Id(this BuiltInTransform b) => b == BuiltInTransform.Polish
        ? new Guid("6F1C3C52-6B0A-4E0B-9C1D-3A5E1B7D0001")
        : new Guid("6F1C3C52-6B0A-4E0B-9C1D-3A5E1B7D0002");

    public static string DisplayName(this BuiltInTransform b) => b == BuiltInTransform.Polish ? "Polish" : "Prompt Engineer";

    public static string DefaultPrompt(this BuiltInTransform b) => b == BuiltInTransform.Polish ? PolishPrompt : PromptEngineerPrompt;

    public static bool IsBuiltInId(Guid id) => id == BuiltInTransform.Polish.Id() || id == BuiltInTransform.PromptEngineer.Id();

    public static TextTransform Default(this BuiltInTransform b) =>
        new(b.Id(), b.DisplayName(), b.DefaultPrompt(), null, true, b.BuiltInId());

    public const string PolishPrompt =
        "Polish this text so it reads clearly and concisely. Fix grammar, spelling and punctuation. Remove filler, repetition and unnecessary words. Reorder sentences when that makes the point easier to follow, and use paragraphs or a short list only when the content clearly has that structure.\n\n" +
        "Keep the author's voice, tone and level of formality. Keep every fact, name, number, date and commitment. Do not add new content.";

    public const string PromptEngineerPrompt =
        "Turn this rough, spoken-style text into a clear, well-structured prompt for an AI assistant. Work out what the author wants and state it as the task. Include the context, constraints, inputs and desired output format that the author mentioned or clearly implied. Use short sections or bullet points when that helps.\n\n" +
        "Address the assistant directly. Do not answer the prompt yourself, and do not add requirements the author did not express. If something important is ambiguous, keep it in the prompt as an explicit open question.";
}

/// <summary>A saved selection rewrite: a prompt plus an optional press-once shortcut.</summary>
public sealed record TextTransform(Guid Id, string Name, string Prompt, HotkeyBinding? Shortcut = null, bool IsEnabled = true, string? BuiltInId = null)
{
    public const string UntitledName = "Untitled transform";

    /// <summary>Parsed <see cref="BuiltInId"/>; an unknown id makes this an ordinary custom transform.</summary>
    public BuiltInTransform? BuiltIn => BuiltInTransforms.Parse(BuiltInId);

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? UntitledName : Name.Trim();

    public bool HasCustomizedPrompt => BuiltIn is { } b && Prompt != b.DefaultPrompt();

    public TransformInstruction Instruction => new(DisplayName, Prompt);
}
