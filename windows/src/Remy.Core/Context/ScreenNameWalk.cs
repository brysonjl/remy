using System.Globalization;
using Remy.Core.Cleanup;
using Remy.Core.Contracts;
using Remy.Core.Settings;

namespace Remy.Core.Context;

/// <summary>One readable node of an accessibility tree (a fake tree in tests, UI Automation in Remy.Platform).</summary>
public interface IScreenNode
{
    /// <summary>One batched read of the node. Null when it cannot be read within <paramref name="timeout"/>.</summary>
    ScreenNodeSnapshot? Read(TimeSpan timeout);
}

public sealed record ScreenNodeSnapshot(bool IsSecure, IReadOnlyList<string> Texts, IReadOnlyList<IScreenNode> Children);

/// <summary>Bounded breadth-first walk of an accessibility tree (v0.2-features.md §7.3). Texts are never logged or stored.</summary>
public static class ScreenNameWalk
{
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(75);
    public static readonly TimeSpan MessagingTimeout = TimeSpan.FromMilliseconds(50);
    public const int MaximumNodes = 400;
    public const int MaximumDepth = 40;
    public const int MaximumValueCharacters = 400;

    /// <param name="root">Top-level window node.</param>
    /// <param name="now">Monotonic clock (a fake clock in tests).</param>
    /// <param name="isCancelled">Polled between nodes.</param>
    public static ScreenTextWalkResult Run(IScreenNode root, Func<TimeSpan> now, Func<bool>? isCancelled = null)
    {
        var texts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nodes = 0;
        var budgetHit = false;
        var start = now();
        var deadline = start + Budget;
        var queue = new List<(IScreenNode Node, int Depth)> { (root, 0) };
        var head = 0;

        while (head < queue.Count && nodes < MaximumNodes && isCancelled?.Invoke() != true)
        {
            var remaining = deadline - now();
            if (remaining <= TimeSpan.Zero) { budgetHit = true; break; }
            var (node, depth) = queue[head++];
            nodes++;
            var snapshot = node.Read(remaining < MessagingTimeout ? remaining : MessagingTimeout);
            if (snapshot is null) continue;
            if (snapshot.IsSecure) continue;     // never read a secure field or anything inside it
            foreach (var raw in snapshot.Texts)
            {
                var t = (raw ?? "").Trim();
                if (t.Length == 0) continue;
                var bounded = Bound(t);
                if (seen.Add(bounded)) texts.Add(bounded);
            }
            if (depth < MaximumDepth)
                foreach (var child in snapshot.Children) queue.Add((child, depth + 1));
        }

        var elapsed = now() - start;
        var outcome = budgetHit ? "budget" : nodes >= MaximumNodes ? "nodeCap" : "completed";
        return new ScreenTextWalkResult(texts, nodes, budgetHit, elapsed, outcome);
    }

    static string Bound(string t)
    {
        if (t.Length <= MaximumValueCharacters) return t;       // fast path: at most 400 text elements
        var e = StringInfo.GetTextElementEnumerator(t);
        var count = 0;
        var end = 0;
        while (e.MoveNext() && count < MaximumValueCharacters)
        {
            count++;
            end = e.ElementIndex + ((string)e.Current).Length;
        }
        return t[..end];
    }
}

/// <summary>When screen names may be captured and given to cleanup (v0.2-features.md §7.2).</summary>
public static class ScreenNamePolicy
{
    static readonly HashSet<DictationContextCategory> Allowed =
    [
        DictationContextCategory.Email, DictationContextCategory.WorkMessaging, DictationContextCategory.PersonalMessaging,
        DictationContextCategory.CodingAgent, DictationContextCategory.AiChat,
    ];

    /// <summary>WINDOWS DECISION: the only consumer is cleanup that may carry captured text (S1-mini and Off have none).</summary>
    public static bool HasConsumer(CleanupRuntime runtime) =>
        runtime.Mode is TranscriptPostProcessingMode.Ollama or TranscriptPostProcessingMode.OpenRouter
            or TranscriptPostProcessingMode.OpenAICompatible or TranscriptPostProcessingMode.Profile
        && ContextSharing.IncludesCapturedText(runtime);

    public static bool ShouldCapture(bool spellNamesEnabled, CleanupRuntime runtime) =>
        spellNamesEnabled && runtime.ContextPolicy.IsEnabled && HasConsumer(runtime);

    public static bool AllowsWalk(DictationContextSnapshot snapshot) =>
        !snapshot.IsSecureField && !snapshot.IsExcluded
        && !DictationContextClassifier.IsSensitiveApplication(snapshot.ProcessName)
        && Allowed.Contains(snapshot.Category);
}
