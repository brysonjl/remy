using System.Diagnostics;
using Remy.Core.Cleanup;
using Remy.Core.Contracts;
using Remy.Core.Diagnostics;
using Remy.Core.Session;
using Remy.Core.Settings;

namespace Remy.Core.Transforms;

public enum TransformPhase { Idle, Reading, Processing, Delivering }

/// <summary>What was selected and where. The text is never logged or stored.</summary>
public sealed record TransformSelection(string Text, DestinationTarget Target);

/// <summary>Result of reading the selection: the selection, or the notice explaining why it was not read.</summary>
public sealed record TransformReadResult(TransformSelection? Selection, TransformNotice? Rejected)
{
    public static TransformReadResult Ok(TransformSelection selection) => new(selection, null);
    public static TransformReadResult Reject(TransformNotice notice) => new(null, notice);
}

public enum TransformDeliveryResult { Success, CopyOnly, Failed, Cancelled }

public enum TransformOutcomeKind { Inserted, Copied, NotStarted, Failed, Cancelled }

/// <summary>How a run ended. <see cref="Notice"/> is set for Copied (focusChanged only), NotStarted and Failed.</summary>
public sealed record TransformOutcome(TransformOutcomeKind Kind, TransformNotice? Notice = null)
{
    public static TransformOutcome Inserted { get; } = new(TransformOutcomeKind.Inserted);
    public static TransformOutcome Cancelled { get; } = new(TransformOutcomeKind.Cancelled);
    public static TransformOutcome Copied(TransformNotice? notice) => new(TransformOutcomeKind.Copied, notice);
    public static TransformOutcome NotStarted(TransformNotice notice) => new(TransformOutcomeKind.NotStarted, notice);
    public static TransformOutcome Failed(TransformNotice notice) => new(TransformOutcomeKind.Failed, notice);
}

/// <summary>Run lifecycle events for the overlay (v0.2-features.md §8.8.1).</summary>
public abstract record TransformEvent
{
    public sealed record Processing(string Name, DestinationTarget? Target) : TransformEvent;
    public sealed record Finished(TransformOutcome Outcome) : TransformEvent;
}

/// <summary>Everything the runner needs from the outside. All injectable for tests.</summary>
public sealed class TransformRunnerDependencies
{
    public required Func<TransformRouteResult> Route { get; init; }
    public required Func<CancellationToken, Task<TransformReadResult>> ReadSelection { get; init; }
    /// <summary>Re-reads the selection and compares it with the original (true only when equal).</summary>
    public required Func<TransformSelection, CancellationToken, Task<bool>> SelectionStillMatches { get; init; }
    public required Func<TransformSelection, bool> IsFocusUnchanged { get; init; }
    public required Func<string, TransformSelection, bool, DeliveryLease, Task<TransformDeliveryResult>> Deliver { get; init; }
    public Action<TransformEvent>? OnEvent { get; init; }
    public Action? OnPhaseChange { get; init; }
}

/// <summary>
/// Runs one transform at a time: read the selection, send it with the transform's prompt to the provider, replace the selection
/// (v0.2-features.md §8.7). Mutual exclusion with dictation is by the caller passing <c>dictationBusy</c>. The paste is claimed once
/// through a <see cref="DeliveryLease"/> and never retried. Never logs or stores selected text or provider bodies.
/// </summary>
public sealed class TransformRunner(TransformRunnerDependencies d)
{
    readonly object _gate = new();
    TransformPhase _phase = TransformPhase.Idle;
    long _generation;
    DeliveryLease? _lease;
    CancellationTokenSource? _cts;
    string? _activeName;
    string? _providerName;
    int _words;
    long _startedAt;

    public event EventHandler? PhaseChanged;

    public TransformPhase Phase { get { lock (_gate) return _phase; } }
    public bool IsBusy => Phase != TransformPhase.Idle;
    public string? ActiveName { get { lock (_gate) return _activeName; } }

    public bool CanCancel
    {
        get { lock (_gate) return _phase != TransformPhase.Idle && !(_lease?.PasteClaimed ?? false); }
    }

    public async Task<TransformOutcome> RunAsync(TransformInstruction instruction, bool dictationBusy)
    {
        long gen;
        CancellationToken ct;
        TransformNotice? early = null;
        lock (_gate)
        {
            if (_phase != TransformPhase.Idle) early = TransformNotice.AlreadyRunning;   // state untouched
            else if (dictationBusy) early = TransformNotice.DictationBusy;
            else if (string.IsNullOrWhiteSpace(instruction.Prompt)) early = TransformNotice.EmptyPrompt;
            if (early is not null) { gen = 0; ct = default; }
            else {
            gen = ++_generation;
            _phase = TransformPhase.Reading;
            _lease = null;
            _cts = new CancellationTokenSource();
            ct = _cts.Token;
            _activeName = instruction.Name;
            _providerName = null;
            _words = 0;
            _startedAt = Stopwatch.GetTimestamp();
            }
        }
        if (early is not null) return Emit(TransformOutcome.NotStarted(early));
        PhaseChangedNotify();

        TransformRouteResult routed;
        try { routed = d.Route(); }
        catch (Exception) { routed = TransformRouteResult.Unavailable(TransformNotice.ProviderUnavailable); }
        if (routed.Route is not { } route) return Finish(TransformOutcome.NotStarted(routed.Notice ?? TransformNotice.ProviderUnavailable), gen);
        lock (_gate) { if (gen == _generation) _providerName = route.ProviderName; }

        TransformReadResult read;
        try { read = await d.ReadSelection(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Stale(gen) ? TransformOutcome.Cancelled : Finish(TransformOutcome.NotStarted(TransformNotice.SelectionUnreadable), gen); }
        catch (Exception) { read = TransformReadResult.Reject(TransformNotice.SelectionUnreadable); }
        if (Stale(gen)) return TransformOutcome.Cancelled;
        if (read.Selection is not { } sel) return Finish(TransformOutcome.NotStarted(read.Rejected ?? TransformNotice.SelectionUnreadable), gen);

        if (TransformLimits.Validate(sel.Text) is { } invalid) return Finish(TransformOutcome.NotStarted(invalid), gen);

        DeliveryLease lease;
        lock (_gate)
        {
            if (gen != _generation) return TransformOutcome.Cancelled;
            _phase = TransformPhase.Processing;
            _lease = lease = new DeliveryLease();
            _words = TransformLimits.WordCount(sel.Text);
        }
        PhaseChangedNotify();
        EmitEvent(new TransformEvent.Processing(instruction.Name, sel.Target));

        var request = new TransformRequest(TransformPrompt.System(instruction), TransformPrompt.User(sel.Text));
        string output;
        try
        {
            var pending = route.Complete(request, ct);
            _ = pending.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);   // a provider that ignores cancellation is discarded
            var response = await pending.WaitAsync(ct).ConfigureAwait(false);
            if (Stale(gen)) return TransformOutcome.Cancelled;
            output = TransformPrompt.FinalizedOutput(response, sel.Text);
        }
        catch (TransformException ex)
        {
            return Stale(gen) ? TransformOutcome.Cancelled : Finish(TransformOutcome.Failed(ex.Notice), gen);
        }
        catch (Exception ex)
        {
            if (Stale(gen)) return TransformOutcome.Cancelled;
            var failure = RemyCleanupFailure.Classify(ex);
            return Finish(TransformOutcome.Failed(TransformNotice.ProviderFailed(failure.Category)), gen);
        }

        lock (_gate)
        {
            if (gen != _generation) return TransformOutcome.Cancelled;
            _phase = TransformPhase.Delivering;
        }
        PhaseChangedNotify();

        bool focusUnchanged;
        try { focusUnchanged = d.IsFocusUnchanged(sel) && await d.SelectionStillMatches(sel, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return TransformOutcome.Cancelled; }
        catch (Exception) { focusUnchanged = false; }
        if (Stale(gen) || lease.IsRevoked) return TransformOutcome.Cancelled;

        TransformDeliveryResult delivered;
        try { delivered = await d.Deliver(output, sel, focusUnchanged, lease).ConfigureAwait(false); }
        catch (Exception ex)
        {
            RemyLog.Error("Transform", "delivery threw", ex);
            delivered = TransformDeliveryResult.Failed;
        }
        if (Stale(gen) || delivered == TransformDeliveryResult.Cancelled) return TransformOutcome.Cancelled;

        return delivered switch
        {
            TransformDeliveryResult.Success => Finish(TransformOutcome.Inserted, gen),
            TransformDeliveryResult.CopyOnly => Finish(TransformOutcome.Copied(focusUnchanged ? null : TransformNotice.FocusChanged), gen),
            _ => Finish(TransformOutcome.Failed(TransformNotice.DeliveryFailed), gen),
        };
    }

    /// <summary>Cancel shortcut, sleep or shutdown. Only while <see cref="CanCancel"/>: revokes the lease, cancels the provider request
    /// and finishes as cancelled. A paste that was already dispatched cannot be retracted.</summary>
    public bool Cancel()
    {
        long gen;
        lock (_gate)
        {
            if (_phase == TransformPhase.Idle) return false;
            if (_lease is { } l && !l.TryRevoke()) return false;   // paste already claimed
            gen = _generation;
        }
        Finish(TransformOutcome.Cancelled, gen);
        return true;
    }

    bool Stale(long gen) { lock (_gate) return gen != _generation || _phase == TransformPhase.Idle; }

    /// <summary>Ends the run when it still belongs to the current generation, and always emits the finished event.</summary>
    TransformOutcome Finish(TransformOutcome outcome, long gen)
    {
        bool current = false;
        CancellationTokenSource? cts = null;
        string? provider = null;
        int words = 0;
        long started = 0;
        lock (_gate)
        {
            if (gen == _generation && _phase != TransformPhase.Idle)
            {
                current = true;
                provider = _providerName; words = _words; started = _startedAt;
                _generation++;
                _phase = TransformPhase.Idle;
                _lease = null;
                _activeName = null;
                cts = _cts; _cts = null;
            }
        }
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
        }
        if (current)
        {
            LogOutcome(outcome, provider, words, started);
            PhaseChangedNotify();
        }
        return Emit(outcome);
    }

    TransformOutcome Emit(TransformOutcome outcome)
    {
        EmitEvent(new TransformEvent.Finished(outcome));
        return outcome;
    }

    void EmitEvent(TransformEvent e)
    {
        try { d.OnEvent?.Invoke(e); } catch (Exception ex) { RemyLog.Error("Transform", "event handler failed", ex); }
    }

    void PhaseChangedNotify()
    {
        try { d.OnPhaseChange?.Invoke(); } catch (Exception ex) { RemyLog.Error("Transform", "phase handler failed", ex); }
        try { PhaseChanged?.Invoke(this, EventArgs.Empty); } catch (Exception ex) { RemyLog.Error("Transform", "phase listener failed", ex); }
    }

    /// <summary>Metadata only: outcome, provider name, elapsed ms, word count bucket. Never text.</summary>
    static void LogOutcome(TransformOutcome outcome, string? provider, int words, long started)
    {
        try
        {
            var elapsed = started == 0 ? 0 : (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var bucket = words == 0 ? "none" : words <= 100 ? "1-100" : words <= 500 ? "101-500" : words <= 1500 ? "501-1500" : "1501+";
            var detail = outcome.Notice is { } n ? n.Kind.ToString() : "-";
            RemyLog.Info("Transform", $"outcome={outcome.Kind} notice={detail} provider={provider ?? "-"} elapsedMs={elapsed} words={bucket}");
        }
        catch { /* logging never affects the run */ }
    }
}

/// <summary>The guarded delivery path shared with dictation (pipeline.md §5.5), verbatim text (v0.2-features.md §8.10).</summary>
public static class TransformDelivery
{
    public static async Task<TransformDeliveryResult> DeliverAsync(IClipboardDelivery clipboard, IDestinationService destinations,
        string output, TransformSelection selection, bool pasteAutomatically, DeliveryLease lease)
    {
        if (!lease.TryClaimClipboard()) return TransformDeliveryResult.Cancelled;
        uint? seq = null;
        for (int attempt = 0; attempt < 3 && seq == null; attempt++)
        {
            if (lease.IsRevoked) return TransformDeliveryResult.Cancelled;
            if (attempt > 0) await Task.Delay(25, CancellationToken.None).ConfigureAwait(false);
            seq = await clipboard.CopyAsync(output).ConfigureAwait(false);
        }
        if (seq == null) return TransformDeliveryResult.Failed;
        if (!lease.MarkCopied()) return TransformDeliveryResult.Cancelled;   // revoked after the copy: the text stays on the clipboard

        if (!pasteAutomatically) return TransformDeliveryResult.CopyOnly;
        if (PasteGate.Evaluate(selection.Target, output) != CopyOnlyReason.None) return TransformDeliveryResult.CopyOnly;
        if (!destinations.IsStillFocused(selection.Target)) return TransformDeliveryResult.CopyOnly;
        if (clipboard.ClipboardSequenceNumber != seq.Value) return TransformDeliveryResult.CopyOnly;   // never paste someone else's content

        // The irreversible boundary: claim immediately before SendInput; never retry.
        if (!lease.TryClaimPaste()) return TransformDeliveryResult.Cancelled;
        try
        {
            var result = await clipboard.SendPasteAsync(CancellationToken.None).ConfigureAwait(false);
            return result == PasteDispatchResult.Dispatched ? TransformDeliveryResult.Success : TransformDeliveryResult.CopyOnly;
        }
        catch (Exception ex)
        {
            RemyLog.Error("Transform", "paste threw", ex);
            return TransformDeliveryResult.CopyOnly;
        }
    }
}

/// <summary>Reads the selection through <see cref="IDestinationService"/> (v0.2-features.md §8.9) and maps the result to notices.</summary>
public static class TransformSelectionReader
{
    public static async Task<TransformReadResult> ReadAsync(IDestinationService destinations, RemySettings settings, CancellationToken ct)
    {
        var target = destinations.CaptureForeground();
        if (target is null || target.IsRemy) return TransformReadResult.Reject(TransformNotice.SelectionUnreadable);
        var r = await destinations.ReadSelectionAsync(target, settings.DictationAppRules, ct).ConfigureAwait(false);
        return r.Kind switch
        {
            SelectionReadKind.Text when !string.IsNullOrEmpty(r.Text) => TransformReadResult.Ok(new TransformSelection(r.Text, target)),
            SelectionReadKind.Text or SelectionReadKind.Empty => TransformReadResult.Reject(TransformNotice.NoSelection),
            SelectionReadKind.Protected => TransformReadResult.Reject(TransformNotice.ProtectedField),
            _ => TransformReadResult.Reject(TransformNotice.SelectionUnreadable),
        };
    }

    /// <summary>True only when the selection is still exactly the original (ordinal).</summary>
    public static async Task<bool> StillMatchesAsync(IDestinationService destinations, TransformSelection selection, CancellationToken ct)
    {
        var now = await destinations.ReadCurrentSelectionAsync(selection.Target, ct).ConfigureAwait(false);
        return now is not null && string.Equals(now, selection.Text, StringComparison.Ordinal);
    }
}

/// <summary>Overlay sink for transform runs (implemented in Remy.App).</summary>
public interface ITransformPresenter
{
    /// <summary>Begin an overlay session on the target's monitor in the Transforming state.</summary>
    void Processing(string name, DestinationTarget? target);

    /// <summary>Show the Transforming state again on the current session (after a hold-to-cancel gesture was aborted).</summary>
    void ResumeProcessing(string name);

    void Finished(TransformOutcome outcome);
}

public static class TransformPresenterExtensions
{
    /// <summary>Adapter for <see cref="TransformRunnerDependencies.OnEvent"/>.</summary>
    public static Action<TransformEvent> AsEventSink(this ITransformPresenter p) => e =>
    {
        switch (e)
        {
            case TransformEvent.Processing pr: p.Processing(pr.Name, pr.Target); break;
            case TransformEvent.Finished f: p.Finished(f.Outcome); break;
        }
    };
}
