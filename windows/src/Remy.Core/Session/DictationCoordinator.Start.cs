using Remy.Core.Contracts;
using Remy.Core.Context;
using Remy.Core.Diagnostics;
using Remy.Core.History;
using Remy.Core.Recovery;
using Remy.Core.Settings;
using Remy.Core.Speech;

namespace Remy.Core.Session;

public sealed partial class DictationCoordinator
{
    // ------------------------------------------------------------------ start (pipeline.md 2.4)

    /// <summary>Hotkey / menu entry. Captures the destination before anything else, then readies the model and starts recording.</summary>
    async Task StartDictationAsync(DictationMode mode, SessionContinuation? continuation = null)
    {
        DictationSession s;
        bool showBusy = false;
        lock (_gate)
        {
            if (_shutdown || _sleeping) return;
            if (_session != null || _recoveryBusy)
            {
                showBusy = _session == null || (_session.Stage != SessionStage.Recording && !_session.Cancelling);
                s = null!;
            }
            else
            {
                _recordingStartError = null;
                s = _session = new DictationSession(Guid.NewGuid(), mode, ++_generation);
            }
        }
        if (s == null)
        {
            if (showBusy) _ui.ShowBusy(SessionMessages.Busy);
            return;
        }

        try
        {
            using var perf = PerfTrace.Begin("dictation.start");
            // Destination first: before overlay or any prompt can take focus.
            var target = _d.Destinations.CaptureForeground();
            s.Origin = target is { IsRemy: false } ? target : null;
            if (Environment.GetEnvironmentVariable("REMY_FAKE_AUDIO") != null && target != null)   // dev runs only
                RemyLog.Info("Dev", $"target process={target.ProcessName} elevated={target.IsElevated} secure={target.IsSecure} editable={target.IsEditable}");
            RemyLog.Info("Session", $"start mode={mode} destination={(target == null ? "none" : target.IsRemy ? "remy" : "captured")}");
            s.Continuation = continuation;
            RaiseStateChanged();

            if (mode == DictationMode.Hold && !_holdKeyDown) { AbortStart(s); return; }
            if (!await EnsureSpeechReadyAsync(s).ConfigureAwait(false)) return;
            if (!Current(s)) return;
            if (mode == DictationMode.Hold && !_holdKeyDown) { AbortStart(s); return; }

            await BeginRecordingAsync(s, continuation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RemyLog.Error("Session", "start failed", ex);
            if (Current(s))
            {
                _ui.ShowError("Could not start dictation", "");
                _ui.Hide(_opt.StartFailureHide);
                await TeardownAsync(s, preserve: false, null, fromPipeline: true).ConfigureAwait(false);
            }
        }
        finally { s.StartDone.TrySetResult(); }
    }

    void AbortStart(DictationSession s)
    {
        lock (_gate) { if (ReferenceEquals(_session, s)) _session = null; }
        s.Cts.Cancel();
        _ui.Hide(TimeSpan.Zero);
        RaiseStateChanged();
    }

    /// <summary>Model check at dictation start (pipeline.md 2.4 step 8). Loads lazily when installed but not loaded.</summary>
    async Task<bool> EnsureSpeechReadyAsync(DictationSession s)
    {
        var name = _d.Models.SpeechDisplayName;
        if (!_d.Models.IsSpeechInstalled)
        {
            var msg = _d.Models.IsSpeechDownloading ? SessionMessages.StillDownloading(name) : SessionMessages.NotReady(name);
            SpeechStartFailure(s, msg);
            return false;
        }
        if (!_d.Recognizer.IsLoaded)
        {
            _ui.ShowPreparing(name);
            await PrepareSpeechModelAsync().ConfigureAwait(false);
            if (!Current(s)) return false;
            if (!_d.Recognizer.IsLoaded)
            {
                SpeechStartFailure(s, SessionMessages.NotReady(name));
                return false;
            }
        }
        return true;
    }

    void SpeechStartFailure(DictationSession s, string message)
    {
        _ui.ShowError(message, "");
        _ui.Hide(_opt.StartFailureHide);
        lock (_gate) { if (ReferenceEquals(_session, s)) _session = null; }
        s.Cts.Cancel();
        if (!_presentedSpeechSetup) { _presentedSpeechSetup = true; _ui.ShowMainWindow(); }
        RaiseStateChanged();
    }

    SessionConfig Freeze()
    {
        var (runtime, failure) = _runtimeFactory.Build();
        return new SessionConfig(
            _settings.SelectedMicrophoneUID,
            _settings.CustomVocabulary.ToArray(),
            _settings.IsFillerWordRemovalEnabled,
            _settings.FillerWordsToRemove.ToArray(),
            _settings.MuteSystemAudioDuringRecordingEnabled,
            _settings.BoostMicrophoneVolumeEnabled,
            _settings.PreserveCancelledSessions,
            _settings.RemyHistoryEnabled,
            _settings.RemyDictationDestination,
            _settings.ShowTextPreview,
            _settings.SelectedLanguage,
            _settings.RemyDictationStartSound,
            _settings.RemyDictationCompletionSound,
            _settings.SoundEffectsEnabled,
            _settings.SoundEffectsVolume,
            runtime, failure);
    }

    List<string?> Candidates(string? selectedId)
    {
        IReadOnlyList<AudioInputDevice> devices;
        try { devices = _d.Audio.GetInputDevices(); } catch { devices = []; }
        var list = new List<string?>();
        if (selectedId != null && devices.Any(d => d.Id == selectedId)) list.Add(selectedId);
        var def = devices.FirstOrDefault(d => d.IsDefault);
        if (def != null && !list.Contains(def.Id)) list.Add(def.Id);
        foreach (var d in devices) if (!list.Contains(d.Id)) list.Add(d.Id);
        if (list.Count == 0) list.Add(null);
        return list;
    }

    async Task BeginRecordingAsync(DictationSession s, SessionContinuation? cont)
    {
        var cfg = Freeze();
        s.Config = cfg;
        s.History = _d.History();
        _ui.BeginSession();

        var meta = new HistorySessionMetadata(s.Id, s.Created, "parakeet", _d.Models.SpeechDisplayName, cfg.Language, 1, 1, cont?.Prefix);
        var ack = await s.History.BeginSessionAsync(meta, s.Cts.Token).ConfigureAwait(false);
        if (!Current(s)) return;
        if (!ack.IsDeliveryPermitted)
        {
            // Never silently "history off": the database is unavailable. Recording would only produce text that cannot be delivered.
            _ui.Hide(TimeSpan.Zero);
            await TeardownAsync(s, preserve: false, null, fromPipeline: true).ConfigureAwait(false);
            SetRecoveryError(HistoryMessages.NotSavedWarning);
            _ui.NavigateToHistory();
            _ui.ShowMainWindow();
            return;
        }

        if (cfg.Cleanup.ContextPolicy.IsEnabled && ContextSharing.UsesDictationContext(cfg.Cleanup.Mode)
            && s.Origin != null && cfg.Destination == RemyDictationDestination.OriginalField)
        {
            var origin = s.Origin;
            s.ContextTask = SafeContextAsync(origin, s.Cts.Token);
        }

        if (cfg.HistoryEnabled)
        {
            try
            {
                var w = _d.Recovery.CreateWriter(s.Id);
                s.Recovery = w;
                w.Failed += msg => OnRecoveryWriterFailed(s, msg);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var msg = RecoveryMessages.UnavailablePrefix + ex.Message;
                _ui.ShowError("Audio recovery is unavailable", ex.Message);
                _ui.Hide(_opt.StartFailureHide);
                await TeardownAsync(s, preserve: false, null, fromPipeline: true).ConfigureAwait(false);
                SetRecoveryError(msg);
                return;
            }
        }

        var candidates = Candidates(cfg.MicrophoneId);
        try
        {
            if (cfg.BoostMicrophone) s.BoostToken = _d.SystemAudio.BoostInput(candidates[0], 0.8f);
            if (cfg.MuteSystemAudio) s.MuteToken = _d.SystemAudio.MuteOutputForRecording();
        }
        catch (Exception ex) { RemyLog.Error("Audio", "mute/boost failed", ex); }

        Exception? last = null;
        bool started = false;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (i > 0)
            {
                await Task.Delay(_opt.CandidateGap, s.Cts.Token).ConfigureAwait(false);
                if (!Current(s)) return;
            }
            try
            {
                using var perf = PerfTrace.Begin("audio.startup");
                await _d.Audio.StartAsync(candidates[i], m => OnSamples(s, m), s.Cts.Token).ConfigureAwait(false);
                started = true;
                break;
            }
            catch (OperationCanceledException) when (s.Cts.IsCancellationRequested) { return; }
            catch (Exception ex) { last = ex; RemyLog.Warn("Audio", "start attempt failed category=" + RemyDiagnostics.ErrorCategory(ex)); }
        }
        if (!Current(s)) return;

        if (!started)
        {
            var message = string.IsNullOrWhiteSpace(last?.Message) ? SessionMessages.UnknownAudioError : last!.Message;
            _recordingStartError = SessionMessages.RecordingStartError(message);
            _ui.ShowError("Could not start the microphone", message);
            _ui.Hide(_opt.StartFailureHide);
            await TeardownAsync(s, preserve: false, null, fromPipeline: true).ConfigureAwait(false);
            _ui.ShowMainWindow();
            RaiseStateChanged();
            return;
        }

        bool stopNow;
        lock (_gate)
        {
            if (!Current(s)) return;
            s.Stage = SessionStage.Recording;
            stopNow = s.Mode == DictationMode.Hold && !_holdKeyDown;
        }
        RemyLog.Info("Session", "recording started");
        if (cfg.SoundsEnabled) SafePlay(cfg.StartSound, cfg.SoundVolume);
        _ui.ShowListening(0, s.Prefix);
        s.PreviewTask = Task.Run(() => PreviewLoopAsync(s));
        s.LevelTask = Task.Run(() => LevelLoopAsync(s));
        if (cfg.Cleanup.Mode != TranscriptPostProcessingMode.None)
            _ = Guard(() => _d.Cleanup.PrewarmAsync(cfg.Cleanup, s.Cts.Token), "prewarm");
        RaiseStateChanged();
        if (stopNow) _ = StopAsync();
    }

    async Task<CapturedContext?> SafeContextAsync(DestinationTarget target, CancellationToken ct)
    {
        try { return await _d.Destinations.CaptureContextAsync(target, new ContextCaptureLimits(), ct).ConfigureAwait(false); }
        catch { return null; }
    }

    void SafePlay(string id, float volume)
    {
        try { _d.Sounds.Play(id, volume); } catch (Exception ex) { RemyLog.Error("Sound", "play failed", ex); }
    }

    void OnRecoveryWriterFailed(DictationSession s, string message)
    {
        s.RecordingSafetyFailure = message;
        // Stop the dictation (pipeline.md 2.5); the stop path sees the failure and preserves what was written.
        _ = Guard(async () => { if (s.Stage == SessionStage.Recording) await StopAsync().ConfigureAwait(false); }, "recovery-failed");
    }

    // ------------------------------------------------------------------ capture callback, preview, level

    void OnSamples(DictationSession s, ReadOnlyMemory<float> m)
    {
        if (s.Closed || m.IsEmpty) return;
        s.Samples.Append(m.Span);
        s.Recovery?.TryEnqueue(m);
        long block = s.Samples.Count / 4000;
        if (block > s.LastSignalBlock)
        {
            s.LastSignalBlock = block;
            s.PreviewSignal.Writer.TryWrite(true);
        }
    }

    async Task PreviewLoopAsync(DictationSession s)
    {
        var ct = s.LoopCts.Token;
        try
        {
            while (await s.PreviewSignal.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                s.PreviewSignal.Reader.TryRead(out _);
                if (s.Config is not { ShowTextPreview: true } || !_d.Recognizer.IsLoaded) continue;
                long total = s.Samples.Count;
                if (!s.Scheduler.ShouldPreview(total, s.Timeline.HasText, s.Samples.CopyTail(VolumeGate.WindowSamples))) continue;
                var (start, len) = LivePreviewScheduler.Window(total);
                var window = s.Samples.CopyRange(start, len);
                RecognitionResult r;
                try { r = await _d.Recognizer.DecodeWindowAsync(window, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { RemyLog.Error("Speech", "preview decode failed", ex); continue; }
                if (ct.IsCancellationRequested) break;
                s.Timeline.Apply(start / (double)LivePreviewScheduler.SampleRate, r);
                s.Scheduler.MarkPreviewed(total);
                s.Transcript = Join(s.Prefix, s.Timeline.Text);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { RemyLog.Error("Speech", "preview loop failed", ex); }
    }

    async Task LevelLoopAsync(DictationSession s)
    {
        var ct = s.LoopCts.Token;
        double lastLevel = -1; string shown = s.Prefix; int tick = 0;
        try
        {
            using var timer = new PeriodicTimer(_opt.LevelPollInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (s.Stage != SessionStage.Recording) break;
                if (_cancelProgress != null) continue;
                double level = _d.Audio.CurrentLevel;
                string text = shown;
                if (++tick % 6 == 0) text = s.Transcript;
                if (Math.Abs(level - lastLevel) >= 0.01 || text != shown)
                {
                    lastLevel = level; shown = text;
                    _ui.ShowListening(level, text);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { RemyLog.Error("Session", "level loop failed", ex); }
    }

    async Task StopLoopsAsync(DictationSession s)
    {
        try { s.LoopCts.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var t in new[] { s.PreviewTask, s.LevelTask })
        {
            if (t == null) continue;
            try { await t.ConfigureAwait(false); } catch { }
        }
    }

    Task StopCaptureAsync(DictationSession s)
    {
        lock (s) { return s.StopCaptureTask ??= StopCaptureCoreAsync(s); }
    }

    async Task StopCaptureCoreAsync(DictationSession s)
    {
        try { await _d.Audio.StopAsync().ConfigureAwait(false); }
        catch (Exception ex) { RemyLog.Error("Audio", "stop failed", ex); }
        s.CaptureStoppedAt = DateTime.UtcNow;
        s.Closed = true;
    }

    async Task RestoreAudioAsync(DictationSession s)
    {
        if (Interlocked.Exchange(ref s.AudioRestored, 1) == 1) return;
        if (s.MuteToken == null && s.BoostToken == null) return;
        var remaining = s.CaptureStoppedAt == DateTime.MinValue
            ? TimeSpan.Zero
            : _opt.RouteSettle - (DateTime.UtcNow - s.CaptureStoppedAt);
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        try { await _d.SystemAudio.RestoreAsync(s.MuteToken, s.BoostToken, remaining).ConfigureAwait(false); }
        catch (Exception ex) { RemyLog.Error("Audio", "restore failed", ex); }
    }
}
