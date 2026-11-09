using Remy.Core.Contracts;
using Remy.Core.Settings;

namespace Remy.Core.Context;

/// <summary>Per-app override (cleanup.md §5.5). The Core cleanup/context agent owns the exact semantics.</summary>
public sealed record DictationAppRule(string ProcessName, DictationContextCategory? Category, bool Excluded);

/// <summary>What cleanup knows about the destination (cleanup.md §5). Category is always set; captured text only
/// when the context policy allows it for the active provider.
/// The trailing optional members are an additive extension (cursor/field facts derived once by
/// <see cref="DictationContextSnapshotBuilder"/>; they carry no captured text).</summary>
public sealed record DictationContextSnapshot(
    DictationContextCategory Category,
    DictationWritingStyle Style,
    string? AppName,
    string? ProcessName,
    string? BrowserHost,
    CapturedContext? Captured,
    IReadOnlyList<string> LexicalHints,
    DictationCursorPlacement CursorPlacement = DictationCursorPlacement.EmptyField,
    bool ContinuesExistingSentence = false,
    DictationFieldPurpose FieldPurpose = DictationFieldPurpose.Unknown,
    bool IsSecureField = false,
    bool IsExcluded = false);
