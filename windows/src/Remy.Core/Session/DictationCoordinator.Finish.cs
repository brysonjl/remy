using System.Diagnostics;
using System.Text;
using Remy.Core.Cleanup;
using Remy.Core.Contracts;
using Remy.Core.Context;
using Remy.Core.Diagnostics;
using Remy.Core.History;
using Remy.Core.Recovery;
using Remy.Core.Settings;
using Remy.Core.Speech;
using Remy.Core.Text;

namespace Remy.Core.Session;

public sealed partial class DictationCoordinator
{
    enum DeliveryKind { Pasted, CopyOnly, PasteBlocked, PasteUncertain, CopyFailed, Cancelled }

    sealed record DeliveryResult(DeliveryKind Kind, CopyOnlyReason Reason = CopyOnlyReason.None, PasteDispatchResult? Paste = null);

    // ------------------------------------------------------------------ stop -> delivery (pipeline.md 5)

    async Task FinishGuardAsync(DictationSession s)
    {
        try { await FinishAsync(s).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RemyLog.Error("Session", "finish failed", ex);
            if (Current(s)) await FailPipelineAsync(s, "Dictation failed unexpectedly.", history: false).ConfigureAwait(false);
        }
        finally
        {
            if (!s.Cancelling) EndSession(s);
            s.FinishDone?.TrySetResult();
        }
    }

    async Task FinishAsync(DictationSession s)
    {
        var cfg = s.Config!;
        var ct = s.Cts.Token;
        using var perf = PerfTrace.Begin("dictation.stopToInsertion");

        await StopLoopsAsync(s).ConfigureAwait(false);
        _ui.ShowProcessing();
        await StopCaptureAsync(s).ConfigureAwait(false);
        if (!Current(s)) return;

        // The recovery file is closed before deciding the session succeeded (pipeline.md 3.7).
        if (s.Recovery != null)
        {
            var rw = await s.Recovery.CompleteAsync().ConfigureAwait(false);
            if (!Current(s)) return;
            var failure = rw.FailureMessage ?? s.RecordingSafetyFailure;
            if (failure != null) { await FailPipelineAsync(s, failure, history: false).ConfigureAwait(false); return; }
        }

        // ---- recognition
        var samples = s.Samples.Snapshot();
        RemyLog.Info("Session", $"stopped samples={samples.Length}");
        string newText = "";
        if (VolumeGate.ContainsSignificantAudio(samples))
        {
            try
            {
                using var _ = PerfTrace.Begin("stt.finalize");
                var r = await _d.Recognizer.DecodeFinalAsync(samples, ct).ConfigureAwait(false);
                newText = r.Text.Trim();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                RemyLog.Error("Speech", "final decode failed", ex);
                if (Current(s)) await FailPipelineAsync(s, SessionMessages.DecodeFailed, history: false).ConfigureAwait(false);
                return;
            }
            if (!Current(s)) return;
            if (newText.Length == 0) newText = s.Timeline.Text.Trim();   // live preview as fallback
        }

        var transcript = Join(s.Prefix, newText);
        var corrected = cfg.FillerEnabled ? FillerWordRemover.Remove(transcript, cfg.FillerWords).Trim() : transcript.Trim();
        RemyLog.Info("Session", $"recognized chars={corrected.Length}");
        if (corrected.Length == 0) { await NoTextAsync(s).ConfigureAwait(false); return; }

        _lastTranscript = corrected;
        RaiseStateChanged();

        // ---- history: raw (must be acknowledged before anything else)
        var rawAck = await s.History.SaveRawAsync(s.Id, newText.Length > 0 ? newText : corrected, ct).ConfigureAwait(false);
        if (!Current(s)) return;
        if (!rawAck.IsDeliveryPermitted) { await HistoryFailureAsync(s).ConfigureAwait(false); return; }
        s.RawSaved = true;
        s.CompletedRaw = corrected;

        // ---- cleanup
        string finalText = corrected;
        RemyCleanupFailure? fallback = null;
        var mode = cfg.Cleanup.Mode;
        bool usesCleanup = cfg.CleanupBuildFailure != null || mode is TranscriptPostProcessingMode.S1Mini or TranscriptPostProcessingMode.Ollama
            or TranscriptPostProcessingMode.OpenRouter or TranscriptPostProcessingMode.OpenAICompatible or TranscriptPostProcessingMode.Profile;
        HistoryAck selectAck;
        if (!usesCleanup)
        {
            selectAck = await s.History.SelectOutputAsync(s.Id, SelectionKind.Raw, corrected, null, ct).ConfigureAwait(false);
        }
        else
        {
            s.Stage = SessionStage.Cleaning;
            RaiseStateChanged();
            _ui.ShowCleaning();
            var attemptId = Guid.NewGuid();
            var started = DateTimeOffset.UtcNow;
            var sw = Stopwatch.StartNew();
            CleanupResult result;
            if (cfg.CleanupBuildFailure is { } bf)
            {
                result = new CleanupResult(CleanupOutcome.FellBackToRaw, corrected, bf, null, null, TimeSpan.Zero);
            }
            else
            {
                using var cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                s.CleanupCts = cleanupCts;
                DictationContextSnapshot? snapshot = null;
                if (cfg.Destination == RemyDictationDestination.OriginalField)
                {
                    CapturedContext? captured = null;
                    if (s.ContextTask != null) { try { captured = await s.ContextTask.ConfigureAwait(false); } catch { } }
                    snapshot = DictationContextSnapshotBuilder.Build(s.Origin, captured, cfg.Cleanup);
                }
                var request = new CleanupRequest(s.Id, attemptId, corrected, cfg.Cleanup, snapshot, cfg.Vocabulary, DateTimeOffset.UtcNow + _opt.CleanupDeadline);
                try
                {
                    using var _ = PerfTrace.Begin("cleanup.run");
                    result = await _d.Cleanup.CleanAsync(request, cleanupCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (s.UseRawRequested && !ct.IsCancellationRequested)
                {
                    result = new CleanupResult(CleanupOutcome.FellBackToRaw, corrected, RemyCleanupFailure.Of(RemyCleanupFailure.UseRaw), null, null, sw.Elapsed);
                }
                catch (OperationCanceledException)
                {
                    // Session cancelled during cleanup: keep a cancelled attempt in history (pipeline.md 6.2).
                    await s.History.AppendCleanupAttemptAsync(MakeAttempt(s, attemptId, started, corrected, null, null, null,
                        RemyCleanupFailure.Cancelled, AttemptStatus.Cancelled, sw.Elapsed), CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex)
                {
                    result = new CleanupResult(CleanupOutcome.FellBackToRaw, corrected, RemyCleanupFailure.Classify(ex), null, null, sw.Elapsed);
                }
                s.CleanupCts = null;
                if (s.UseRawRequested && result.Outcome is CleanupOutcome.Cleaned or CleanupOutcome.Unchanged)
                    result = new CleanupResult(CleanupOutcome.FellBackToRaw, corrected, RemyCleanupFailure.Of(RemyCleanupFailure.UseRaw), result.ProviderName, result.ModelName, result.Elapsed);
            }
            s.CleanupDecided = true;
            if (!Current(s)) return;

            switch (result.Outcome)
            {
                case CleanupOutcome.Cleaned:
                case CleanupOutcome.Unchanged:
                {
                    var attempt = MakeAttempt(s, attemptId, started, corrected, result.Output, result.ProviderName, result.ModelName, null, AttemptStatus.Succeeded, result.Elapsed);
                    var a = await s.History.AppendCleanupAttemptAsync(attempt, ct).ConfigureAwait(false);
                    if (!Current(s)) return;
                    if (!a.IsDeliveryPermitted) { await HistoryFailureAsync(s).ConfigureAwait(false); return; }
                    selectAck = await s.History.SelectOutputAsync(s.Id, SelectionKind.Attempt, result.Output, attemptId, ct).ConfigureAwait(false);
                    finalText = result.Output;
                    break;
                }
                case CleanupOutcome.Skipped:
                    selectAck = await s.History.SelectOutputAsync(s.Id, SelectionKind.Raw, corrected, null, ct).ConfigureAwait(false);
                    break;
                default:
                {
                    fallback = result.Failure ?? RemyCleanupFailure.Of(RemyCleanupFailure.ProviderFailure);
                    var attempt = MakeAttempt(s, attemptId, started, corrected, null, result.ProviderName, result.ModelName, fallback.Category,
                        fallback.Category == RemyCleanupFailure.Cancelled ? AttemptStatus.Cancelled : AttemptStatus.Failed, result.Elapsed);
                    var a = await s.History.AppendCleanupAttemptAsync(attempt, ct).ConfigureAwait(false);
                    if (!Current(s)) return;
                    if (!a.IsDeliveryPermitted) { await HistoryFailureAsync(s).ConfigureAwait(false); return; }
                    selectAck = await s.History.SelectOutputAsync(s.Id, SelectionKind.Fallback, corrected, null, ct).ConfigureAwait(false);
                    break;
                }
            }
        }
        if (!Current(s)) return;
        if (!selectAck.IsDeliveryPermitted) { await HistoryFailureAsync(s).ConfigureAwait(false); return; }
        s.OutputSelected = true;

        // ---- delivery intent: acknowledged before any clipboard effect
        var deliveryId = Guid.NewGuid();
        var intentAck = await s.History.RecordDeliveryIntentAsync(deliveryId, s.Id, finalText, ct).ConfigureAwait(false);
        if (!Current(s)) return;
        if (!intentAck.IsDeliveryPermitted) { await HistoryFailureAsync(s).ConfigureAwait(false); return; }
        s.DeliveryId = deliveryId;

        s.Stage = SessionStage.Delivering;
        RaiseStateChanged();
        var delivery = await DeliverAsync(s, finalText, ct).ConfigureAwait(false);
        if (delivery.Kind == DeliveryKind.Cancelled) return;

        // ---- outcome (never retried, even if this write fails)
        var (outcome, reason) = delivery.Kind switch
        {
            DeliveryKind.Pasted => (DeliveryOutcome.Dispatched, (string?)null),
            DeliveryKind.CopyOnly => (DeliveryOutcome.CopiedOnly, delivery.Reason.ToString()),
            DeliveryKind.PasteBlocked => (DeliveryOutcome.Blocked, delivery.Paste?.ToString()),
            DeliveryKind.PasteUncertain => (DeliveryOutcome.Uncertain, "PasteFailed"),
            _ => (DeliveryOutcome.Failed, "ClipboardWriteFailed"),
        };
        RemyLog.Info("Session", $"delivery={delivery.Kind} reason={delivery.Reason}");
        var outAck = await s.History.RecordDeliveryOutcomeAsync(deliveryId, outcome, reason, CancellationToken.None).ConfigureAwait(false);
        s.OutcomeRecorded = true;
        if (!outAck.IsDeliveryPermitted) RemyLog.Warn("History", "delivery outcome not saved");

        await RestoreAudioAsync(s).ConfigureAwait(false);

        bool failed = delivery.Kind == DeliveryKind.CopyFailed;
        if (!failed && cfg.SoundsEnabled) SafePlay(cfg.CompletionSound, cfg.SoundVolume);

        if (fallback != null)
        {
            _ui.ShowError(RemyCleanupFailure.OverlayTitleFor(fallback.Category),
                failed ? SessionMessages.RawInRemy : delivery.Kind == DeliveryKind.Pasted ? SessionMessages.RawUsed : SessionMessages.RawCopied);
            _ui.Hide(_opt.FallbackHide);
        }
        else if (failed)
        {
            _ui.ShowError(SessionMessages.CouldNotCopy, SessionMessages.CouldNotCopyDetail);
            _ui.Hide(_opt.SuccessHide);
        }
        else if (delivery.Kind == DeliveryKind.Pasted)
        {
            _ui.ShowSuccess();
            _ui.Hide(_opt.SuccessHide);
        }
        else
        {
            _ui.ShowCopyOnly(_opt.CopyHint);
            _ui.Hide(_opt.SuccessHide);
        }

        // The temporary recovery audio is no longer needed once history holds the text.
        _d.Recovery.DiscardCompleted(s.Id);
        if (s.Continuation is { } c) { _d.Recovery.Release(c.SourceRecoveryId); _d.Recovery.Delete(c.SourceRecoveryId); }
    }

    CleanupAttemptRecord MakeAttempt(DictationSession s, Guid id, DateTimeOffset started, string input, string? output, string? provider,
        string? model, string? failure, AttemptStatus status, TimeSpan elapsed)
    {
        var cfg = s.Config!;
        var prompt = cfg.Cleanup.Prompt;
        if (prompt.Length > 20_000) prompt = prompt[..20_000];
        var snapshot = new CleanupProfileSnapshot(cfg.Cleanup.ProfileId?.ToString(), cfg.Cleanup.Mode.ToRaw(), cfg.Cleanup.ReasoningEnabled,
            cfg.Cleanup.ContextPolicy.IsEnabled, null, RemyEnumNames.S1Structure.ToRaw(cfg.Cleanup.S1Structure), null, cfg.Destination.ToRaw());
        return new CleanupAttemptRecord(id, s.Id, started, input, output, Clip(provider, 200), Clip(model, 200), 1, failure,
            string.IsNullOrEmpty(prompt) ? null : prompt, status, snapshot, started, (int)Math.Min(int.MaxValue, elapsed.TotalMilliseconds));
    }

    static string? Clip(string? v, int maxBytes)
    {
        if (v == null) return null;
        while (Encoding.UTF8.GetByteCount(v) > maxBytes) v = v[..^1];
        return v;
    }

    /// <summary>"No text" path (pipeline.md 5.2): nothing pasted or copied, no history entry, no sound.</summary>
    async Task NoTextAsync(DictationSession s)
    {
        await RestoreAudioAsync(s).ConfigureAwait(false);
        _d.Recovery.DiscardCompleted(s.Id);
        if (s.Continuation is { } c) _d.Recovery.Release(c.SourceRecoveryId);
        _ui.ShowSuccess();
        _ui.Hide(_opt.NoTextHide);
    }

    // ------------------------------------------------------------------ delivery (pipeline.md 5.4, 5.5)

    async Task<DeliveryResult> DeliverAsync(DictationSession s, string text, CancellationToken ct)
    {
        var cfg = s.Config!;
        DestinationTarget? target;
        CopyOnlyReason reason = CopyOnlyReason.None;
        if (cfg.Destination == RemyDictationDestination.OriginalField)
        {
            target = s.Origin;
            reason = PasteGate.Evaluate(target, text);
            if (reason == CopyOnlyReason.None)
            {
                using var _ = PerfTrace.Begin("insertion.restoreFocus");
                var restored = await _d.Destinations.RestoreFocusAsync(target!, ct).ConfigureAwait(false);
                if (!restored) reason = CopyOnlyReason.FocusNotRestored;
            }
        }
        else
        {
            var now = _d.Destinations.CaptureForeground();
            target = now is { IsRemy: false } ? now : null;
            reason = PasteGate.Evaluate(target, text);
        }
        if (!Current(s) || s.Lease.IsRevoked) return new DeliveryResult(DeliveryKind.Cancelled);

        // Copy first, always. The prior clipboard is not restored (pipeline.md 5.5 step 6).
        if (!s.Lease.TryClaimClipboard()) return new DeliveryResult(DeliveryKind.Cancelled);
        uint? seq = null;
        for (int attempt = 0; attempt < 3 && seq == null; attempt++)
        {
            if (s.Lease.IsRevoked) return new DeliveryResult(DeliveryKind.Cancelled);
            if (attempt > 0) await Task.Delay(25, CancellationToken.None).ConfigureAwait(false);
            using var _ = PerfTrace.Begin("insertion.clipboard");
            seq = await _d.Clipboard.CopyAsync(text).ConfigureAwait(false);
        }
        if (seq == null) return new DeliveryResult(DeliveryKind.CopyFailed);
        if (!s.Lease.MarkCopied()) return new DeliveryResult(DeliveryKind.Cancelled);   // revoked after the copy: text stays, no paste

        if (reason == CopyOnlyReason.None)
        {
            if (!_d.Destinations.IsStillFocused(target!)) reason = CopyOnlyReason.FocusChanged;
            else if (_d.Clipboard.ClipboardSequenceNumber != seq.Value) reason = CopyOnlyReason.ClipboardChanged;   // never paste someone else's content
        }
        if (reason != CopyOnlyReason.None) return new DeliveryResult(DeliveryKind.CopyOnly, reason);

        // The irreversible boundary: claim immediately before SendInput; never retry.
        if (!s.Lease.TryClaimPaste()) return new DeliveryResult(DeliveryKind.Cancelled);
        PasteDispatchResult pr;
        try
        {
            using var _ = PerfTrace.Begin("insertion.pasteEvent");
            pr = await _d.Clipboard.SendPasteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RemyLog.Error("Delivery", "paste threw", ex);
            return new DeliveryResult(DeliveryKind.PasteUncertain);
        }
        return pr switch
        {
            PasteDispatchResult.Dispatched => new DeliveryResult(DeliveryKind.Pasted, Paste: pr),
            PasteDispatchResult.Failed => new DeliveryResult(DeliveryKind.PasteUncertain, Paste: pr),
            _ => new DeliveryResult(DeliveryKind.PasteBlocked, pr == PasteDispatchResult.BlockedElevated ? CopyOnlyReason.Elevated : CopyOnlyReason.None, pr),
        };
    }

    // ------------------------------------------------------------------ cancel / failure / teardown (pipeline.md 2.6)

    public Task CancelAsync() => CancelAsync(CancellationReason.User);

    Task CancelAsync(CancellationReason reason)
    {
        var s = _session;
        return s == null ? Task.CompletedTask : CancelCoreAsync(s, reason);
    }

    async Task CancelCoreAsync(DictationSession s, CancellationReason reason)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_session, s) || s.Cancelling) return;
            if (!s.Lease.TryRevoke()) return;   // paste already claimed: cancel refused
            s.Cancelling = true;
        }
        _cancelProgress = null;
        _ui.Hide(TimeSpan.Zero);
        RaiseStateChanged();
        try { s.CleanupCts?.Cancel(); } catch (ObjectDisposedException) { }

        bool preserve = reason == CancellationReason.Shutdown || s.Continuation != null || (s.Config?.PreserveCancelledSessions ?? false);
        using var perf = PerfTrace.Begin("dictation.cancel");
        var res = await TeardownAsync(s, preserve, null, fromPipeline: false).ConfigureAwait(false);

        // History bookkeeping for work that was in flight (writes after cancel use no token: they only describe what already happened).
        try
        {
            if (s.DeliveryId is Guid did && !s.OutcomeRecorded)
                await s.History.RecordDeliveryOutcomeAsync(did, s.Lease.Copied ? DeliveryOutcome.CopiedOnly : DeliveryOutcome.Blocked, "cancelled", CancellationToken.None).ConfigureAwait(false);
            if (s.RawSaved && !s.OutputSelected)
                await s.History.MarkInterruptedAsync(s.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { RemyLog.Error("History", "cancel bookkeeping failed", ex); }

        if (res?.Message != null) SetRecoveryError(reason == CancellationReason.Shutdown ? SessionMessages.QuitRecoveryNotSaved : res.Message);
        else if (reason == CancellationReason.Shutdown) RaiseStateChanged();
    }

    /// <summary>Stops everything for a session, then saves or discards the recovery audio. Always ends the session.</summary>
    async Task<RecoverySaveResult?> TeardownAsync(DictationSession s, bool preserve, string? captureError, bool fromPipeline)
    {
        s.Cancelling = true;
        try { s.Cts.Cancel(); } catch (ObjectDisposedException) { }
        await StopLoopsAsync(s).ConfigureAwait(false);
        await StopCaptureAsync(s).ConfigureAwait(false);
        if (!fromPipeline)
        {
            // Join the old tasks so the engine and the recovery file are never reused while one still touches them.
            await s.StartDone.Task.ConfigureAwait(false);
            if (s.FinishDone != null) await s.FinishDone.Task.ConfigureAwait(false);
        }

        RecoverySaveResult? res = null;
        if (s.Recovery != null)
        {
            RecoveryWriteResult w;
            try { w = await s.Recovery.CompleteAsync().ConfigureAwait(false); }
            catch (Exception) { w = new RecoveryWriteResult(0, RecoveryMessages.WriterFailed); }
            if (preserve)
            {
                var preview = s.Transcript;
                res = _d.Recovery.Save(new RecoverySaveRequest(s.Id, s.Created, w.SamplesWritten, string.IsNullOrWhiteSpace(preview) ? null : preview,
                    s.CompletedRaw, captureError ?? w.FailureMessage, s.Continuation?.Prefix,
                    s.Continuation?.TargetExecutable ?? s.Origin?.ExecutablePath, s.Continuation?.PreviousDurationSeconds ?? 0));
            }
            else _d.Recovery.DiscardCompleted(s.Id);
        }
        if (s.Continuation is { } c) _d.Recovery.Release(c.SourceRecoveryId);

        await RestoreAudioAsync(s).ConfigureAwait(false);
        EndSession(s);
        return res;
    }

    void EndSession(DictationSession s)
    {
        bool ended;
        lock (_gate)
        {
            ended = ReferenceEquals(_session, s);
            if (ended) _session = null;
        }
        if (ended) { _cancelProgress = null; RaiseStateChanged(); }
    }

    /// <summary>A failure after recording: preserve the audio as a recovery session and tell the user (pipeline.md 4.5, 6.2).</summary>
    async Task FailPipelineAsync(DictationSession s, string message, bool history)
    {
        s.Lease.Revoke();
        _ui.Hide(TimeSpan.Zero);
        var save = await TeardownAsync(s, preserve: true, null, fromPipeline: true).ConfigureAwait(false);
        string msg = message;
        if (save?.Message != null) msg = history ? RecoveryMessages.MetadataAndHistoryNotSaved : SessionMessages.RecoveryCopyNotSaved(message, save.Message);
        else if (save?.Entry is { HasAudio: true } && !history) msg += SessionMessages.RecordingAvailableInHistory;
        SetRecoveryError(msg);
        if (history) { _ui.NavigateToHistory(); _ui.ShowMainWindow(); }
        try { if (s.RawSaved && !s.OutputSelected) await s.History.FailAsync(s.Id, history ? "persistence" : "unknown", CancellationToken.None).ConfigureAwait(false); } catch { }
    }

    /// <summary>History write was not acknowledged: nothing is delivered (pipeline.md 6.2).</summary>
    Task HistoryFailureAsync(DictationSession s)
    {
        RemyLog.Warn("History", "write not acknowledged; delivery suspended");
        return FailPipelineAsync(s, HistoryMessages.NotSavedWarning, history: true);
    }
}
