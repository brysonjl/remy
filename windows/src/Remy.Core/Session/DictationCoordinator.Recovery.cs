using Remy.Core.Diagnostics;
using Remy.Core.History;
using Remy.Core.Recovery;
using Remy.Core.Speech;
using Remy.Core.Text;

namespace Remy.Core.Session;

public sealed partial class DictationCoordinator
{
    sealed record RecoveredText(string? Text, string? Raw, string? Error);

    /// <summary>Text for a recovery entry (pipeline.md 6.4): the completed transcript, else the decoded audio joined to the prefix, else the preview.</summary>
    async Task<RecoveredText> ResolveRecoveryTextAsync(RecoverySession e, string emptyMessage)
    {
        var prefix = e.TranscriptPrefix;
        string? text, raw = null;
        if (!string.IsNullOrWhiteSpace(e.CompletedTranscript))
        {
            text = e.CompletedTranscript;
        }
        else if (e.HasAudio)
        {
            await PrepareSpeechModelAsync().ConfigureAwait(false);
            if (!_d.Recognizer.IsLoaded) return new RecoveredText(null, null, SessionMessages.NotReady(_d.Models.SpeechDisplayName));
            float[] samples;
            try { samples = _d.Recovery.LoadSamples(e.Id); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return new RecoveredText(null, null, RecoveryMessages.RecoveryFailed(ex.Message));
            }
            string decoded = "";
            if (VolumeGate.ContainsSignificantAudio(samples))
            {
                var r = await _d.Recognizer.DecodeFinalAsync(samples, _life.Token).ConfigureAwait(false);
                decoded = r.Text.Trim();
            }
            if (decoded.Length == 0 && !string.IsNullOrWhiteSpace(prefix))
                return new RecoveredText(null, null, RecoveryMessages.NoSpeechInLatestAudio);
            text = Join(prefix, decoded);
            if (string.IsNullOrWhiteSpace(prefix)) raw = decoded;
        }
        else
        {
            var preview = e.Preview == RecoveryMessages.InterruptedPreview ? null : e.Preview;
            text = string.IsNullOrWhiteSpace(preview) ? prefix : preview;
        }

        if (_settings.IsFillerWordRemovalEnabled) text = FillerWordRemover.Remove(text ?? "", _settings.FillerWordsToRemove);
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? new RecoveredText(null, null, emptyMessage) : new RecoveredText(text, raw, null);
    }

    /// <summary>Returns null on success (text copied to the clipboard and saved to history) or the user-facing failure message.</summary>
    public async Task<string?> RecoverTextAsync(Guid recoverySessionId)
    {
        if (_session != null || _shutdown || _recoveryBusy || TransformBusy) return SessionMessages.Busy;
        if (!_settings.RemyHistoryEnabled) return RecoveryMessages.RecoverNeedsHistory;
        var entry = _d.Recovery.List().FirstOrDefault(x => x.Id == recoverySessionId);
        if (entry == null) return RecoveryMessages.RecoveryFailed("The saved session was not found.");
        _recoveryBusy = true;
        _d.Recovery.MarkInUse(recoverySessionId);
        try
        {
            using var perf = PerfTrace.Begin("recovery.transcribe");
            var r = await ResolveRecoveryTextAsync(entry, RecoveryMessages.NoSpeechRecovered).ConfigureAwait(false);
            if (r.Text == null) return r.Error;

            // Recover Text never runs cleanup but applies both local passes with the current settings (v0.2-features.md 5.3).
            var rules = new DictationTextRules(_settings.VocabularyCorrections, _settings.DictationSnippets);
            var recovered = rules.ExpandSnippets(rules.ApplyCorrections(r.Text)).Trim();
            if (recovered.Length == 0) return RecoveryMessages.NoSpeechRecovered;
            r = r with { Text = recovered };

            var meta = new HistorySessionMetadata(Guid.NewGuid(), DateTimeOffset.UtcNow, "parakeet", _d.Models.SpeechDisplayName, _settings.SelectedLanguage, 1, 1);
            var ack = await _d.History().RecordRecoveredAsync(meta, r.Text, r.Raw, CancellationToken.None).ConfigureAwait(false);
            if (!ack.IsDeliveryPermitted) return RecoveryMessages.RecoveredNotSaved;

            _lastTranscript = r.Text;
            try { await _d.Clipboard.CopyAsync(r.Text).ConfigureAwait(false); } catch { }
            _d.Recovery.Release(recoverySessionId);   // the history commit happens before the audio is removed
            _d.Recovery.Delete(recoverySessionId);
            RaiseStateChanged();
            return null;
        }
        catch (OperationCanceledException) { return RecoveryMessages.RecoveryFailed("Cancelled."); }
        catch (Exception ex)
        {
            RemyLog.Error("Recovery", "recover failed", ex);
            return RecoveryMessages.RecoveryFailed(ex.Message);
        }
        finally
        {
            _d.Recovery.Release(recoverySessionId);
            _recoveryBusy = false;
        }
    }

    public async Task ContinueRecoverySessionAsync(Guid recoverySessionId)
    {
        if (TransformBusy) { _ui.ShowBusy(SessionMessages.TransformBusy); return; }
        if (_session != null || _sleeping || _shutdown || _recoveryBusy) { _ui.ShowBusy(SessionMessages.Busy); return; }
        var entry = _d.Recovery.List().FirstOrDefault(x => x.Id == recoverySessionId);
        if (entry == null) return;
        _recoveryBusy = true;
        _d.Recovery.MarkInUse(recoverySessionId);
        bool handedOver = false;
        try
        {
            var r = await ResolveRecoveryTextAsync(entry, RecoveryMessages.NoSpeechRestored).ConfigureAwait(false);
            if (r.Text == null) { SetRecoveryError(r.Error!); return; }
            _recoveryBusy = false;
            var cont = new SessionContinuation(r.Text, recoverySessionId, entry.DurationSeconds, entry.TargetExecutable);
            await StartDictationAsync(DictationMode.Toggle, cont).ConfigureAwait(false);
            handedOver = _session?.Continuation?.SourceRecoveryId == recoverySessionId;
            if (!handedOver && _recordingStartError != null)
                SetRecoveryError(RecoveryMessages.ContinueMicFailed(_recordingStartError.Replace("Could not start the microphone. ", "")));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RemyLog.Error("Recovery", "continue failed", ex);
            SetRecoveryError(RecoveryMessages.ContinueFailed(ex.Message));
        }
        finally
        {
            _recoveryBusy = false;
            if (!handedOver) _d.Recovery.Release(recoverySessionId);
        }
    }
}
