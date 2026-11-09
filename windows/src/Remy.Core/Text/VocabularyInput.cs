using Remy.Core.Cleanup;
using Remy.Core.Context;
using Remy.Core.Settings;

namespace Remy.Core.Text;

/// <summary>Custom vocabulary parsing, limits and prompt formatting (cleanup.md §6).</summary>
public static class VocabularyInput
{
    public const int MaxItems = 10_000;
    public const int MaxItemBytes = 4096;

    /// <summary>Splits on commas (, U+FF0C, U+3001) and newlines; trims; drops empty and exact duplicates
    /// (of <paramref name="existing"/> or earlier terms); keeps order.</summary>
    public static IReadOnlyList<string> Terms(string input, IEnumerable<string>? existing = null)
    {
        var seen = new HashSet<string>(existing ?? [], StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var piece in input.Split([',', '，', '、', '\n', '\r'], StringSplitOptions.None))
        {
            var t = piece.Trim();
            if (t.Length == 0 || !seen.Add(t)) continue;
            result.Add(t);
        }
        return result;
    }

    /// <summary>"- term" lines for the system prompt, or the "No known terms" sentence.</summary>
    public static string KnownTermsSection(IReadOnlyList<string> vocabulary) =>
        vocabulary.Count == 0 ? CleanupPromptBuilder.NoKnownTerms : string.Join("\n", vocabulary.Select(t => "- " + t));

    /// <summary>Footer under the vocabulary list for a mode, or null when the mode takes no vocabulary.</summary>
    public static string? FooterFor(TranscriptPostProcessingMode mode) => mode switch
    {
        TranscriptPostProcessingMode.Ollama => "These terms are sent to Ollama to preserve product names, names, and domain-specific wording during post-processing.",
        TranscriptPostProcessingMode.OpenRouter => "These terms are sent to OpenRouter to preserve product names, names, and domain-specific wording during post-processing.",
        TranscriptPostProcessingMode.OpenAICompatible => "These terms are sent to the OpenAI-compatible server to preserve product names, names, and domain-specific wording during post-processing.",
        TranscriptPostProcessingMode.Profile => "These hints are sent to the selected profile's server with your transcript.",
        _ => null,
    };

    public static bool ModeSupportsVocabulary(TranscriptPostProcessingMode mode) => FooterFor(mode) is not null;

    public const string QuickPanelCaption = "Add names and phrases for supported transcription and cleanup methods.";
    public const string AddPlaceholder = "Add word or phrase…";
    public const string EmptyState = "No words added yet";
    public static string CountLabel(int n) => n == 1 ? "1 word" : $"{n} words";
}
