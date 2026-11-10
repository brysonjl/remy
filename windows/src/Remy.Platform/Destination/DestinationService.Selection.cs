using FlaUI.Core.AutomationElements;
using Remy.Core.Context;
using Remy.Core.Contracts;

namespace Remy.Platform.Destination;

/// <summary>Reading the selected text for Transforms (v0.2-features.md §8.9). WINDOWS DECISION: UIA TextPattern only, no synthetic Ctrl+C.</summary>
public sealed partial class DestinationService
{
    public static readonly TimeSpan SelectionBudget = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan SelectionRecheckBudget = TimeSpan.FromMilliseconds(500);

    const int SelectionReadChars = 250_000;

    public async Task<SelectionReadResult> ReadSelectionAsync(DestinationTarget target, IReadOnlyList<DictationAppRule> rules, CancellationToken ct)
    {
        try
        {
            if (target.IsRemy) return new(SelectionReadKind.Unreadable, null);
            if (target.IsSecure || DictationContextClassifier.IsSensitiveApplication(target.ProcessName)
                || IsSensitiveApplication(target.ProcessName, target.WindowTitle)) return new(SelectionReadKind.Protected, null);
            var (_, contextEnabled) = DictationContextClassifier.Classify(target.ProcessName, target.WindowTitle, null, null, rules);
            if (!contextEnabled) return new(SelectionReadKind.Protected, null);
            if (target.IsElevated) return new(SelectionReadKind.Unreadable, null);

            var r = await RunSelectionBounded(() => ReadSelectionCore(target.ProcessId), SelectionBudget, ct).ConfigureAwait(false);
            return r ?? new(SelectionReadKind.Unreadable, null);
        }
        catch (OperationCanceledException) { throw; }
        catch { return new(SelectionReadKind.Unreadable, null); }
    }

    public async Task<string?> ReadCurrentSelectionAsync(DestinationTarget target, CancellationToken ct)
    {
        try
        {
            if (target.IsRemy || target.IsElevated || target.IsSecure) return null;
            var r = await RunSelectionBounded(() => ReadSelectionCore(target.ProcessId), SelectionRecheckBudget, ct).ConfigureAwait(false);
            return r is { Kind: SelectionReadKind.Text } ? r.Text : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    static async Task<SelectionReadResult?> RunSelectionBounded(Func<SelectionReadResult> work, TimeSpan budget, CancellationToken ct)
    {
        var task = Task.Run(work, CancellationToken.None);
        var finished = await Task.WhenAny(task, Task.Delay(budget, ct)).ConfigureAwait(false);
        if (finished != task)
        {
            _ = task.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
            ct.ThrowIfCancellationRequested();
            return null;   // timed out
        }
        return await task.ConfigureAwait(false);
    }

    /// <summary>Focused element of <paramref name="expectedPid"/> must expose TextPattern with exactly one non-empty selection range.</summary>
    static SelectionReadResult ReadSelectionCore(int expectedPid)
    {
        try
        {
            AutomationElement? el = Uia.Value.FocusedElement();
            if (el == null || el.Properties.ProcessId.ValueOrDefault != expectedPid) return new(SelectionReadKind.Unreadable, null);
            if (el.Properties.IsPassword.ValueOrDefault) return new(SelectionReadKind.Protected, null);
            var text = el.Patterns.Text.PatternOrDefault;
            if (text == null) return new(SelectionReadKind.Unreadable, null);
            var ranges = text.GetSelection();
            if (ranges is not { Length: > 0 }) return new(SelectionReadKind.Empty, null);
            string? found = null;
            foreach (var range in ranges)
            {
                var t = range.GetText(SelectionReadChars);
                if (string.IsNullOrEmpty(t)) continue;
                if (found != null) return new(SelectionReadKind.Unreadable, null);   // more than one non-empty range
                found = t;
            }
            return found is null ? new(SelectionReadKind.Empty, null) : new(SelectionReadKind.Text, found);
        }
        catch { return new(SelectionReadKind.Unreadable, null); }
    }
}
