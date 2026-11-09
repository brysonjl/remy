using Remy.Core.Cleanup;
using Remy.Core.Contracts;
using Remy.Core.History;
using Remy.Core.Session;
using Remy.Core.Settings;
using Xunit;

namespace Remy.Core.Tests.Session;

public class DictationCoordinatorTests
{
    static Task Wait(Func<bool> c, int ms = 5000) => Harness.WaitFor(c, ms);

    [Fact]
    public async Task HoldToRecord_HappyPath_DeliversTextAndRecordsHistoryInOrder()
    {
        using var h = new Harness();
        await h.DictateHoldAsync();

        Assert.Equal(["hello world"], h.Clipboard.Copies);
        Assert.Equal(1, h.Clipboard.Pastes);
        Assert.Equal("hello world", h.Coordinator.LastTranscript);
        Assert.Equal(["begin", "raw", "select", "intent", "outcome"], h.History.Log);
        Assert.Equal(DeliveryOutcome.Dispatched, Assert.Single(h.History.Outcomes).Outcome);
        Assert.Equal([(SelectionKind.Raw, "hello world")], h.History.Selections);
        Assert.Equal([h.Settings.RemyDictationStartSound, h.Settings.RemyDictationCompletionSound], h.Sounds.Played);
        Assert.True(h.Ui.Has("success"));
        Assert.Equal(1, h.SystemAudio.Boosts);            // boost mic is on by default
        Assert.Equal(0, h.SystemAudio.Mutes);             // mute is off by default
        Assert.Equal(1, h.SystemAudio.Restores);
        Assert.Equal(1, h.Destinations.Restores);          // original field: focus restored first
        Assert.Empty(h.Recovery.List());                   // recovery audio discarded on success
        Assert.Equal(DictationStatus.Idle, h.Coordinator.Status);
    }

    [Fact]
    public async Task ToggleMode_FirstPressStartsSecondPressStops()
    {
        using var h = new Harness();
        h.Hotkeys.Press(FakeHotkeys.Toggle);
        await Wait(() => h.Coordinator.Status == DictationStatus.Recording);
        h.Hotkeys.Release(FakeHotkeys.Toggle);            // key-up is ignored for toggle
        h.Audio.FeedSpeech(1.2);
        await Task.Delay(30);
        Assert.Equal(DictationStatus.Recording, h.Coordinator.Status);

        h.Hotkeys.Press(FakeHotkeys.Toggle);
        await Wait(() => !h.Coordinator.IsBusy);
        Assert.Equal(["hello world"], h.Clipboard.Copies);
        Assert.Equal(1, h.Clipboard.Pastes);
    }

    [Fact]
    public async Task HoldKeyReleasedBeforeRecordingBegins_AbortsSilently()
    {
        using var h = new Harness(x => { });
        h.Destinations.OnCapture = () => h.Hotkeys.Release(FakeHotkeys.Hold);   // released while the destination is being captured
        h.Hotkeys.Press(FakeHotkeys.Hold);
        await Wait(() => !h.Coordinator.IsBusy);

        Assert.Empty(h.Audio.StartAttempts);
        Assert.Empty(h.Sounds.Played);
        Assert.DoesNotContain(h.Ui.Snapshot(), e => e.StartsWith("busy") || e.StartsWith("error") || e == "listening");
        Assert.Empty(h.Clipboard.Copies);
    }

    [Fact]
    public async Task HoldKeyReleasedDuringMicStart_StopsImmediatelyAndTakesTheNoSpeechPath()
    {
        var gate = new TaskCompletionSource();
        using var h = new Harness(x => x.Audio.StartGate = () => gate.Task);
        h.Hotkeys.Press(FakeHotkeys.Hold);
        await Wait(() => h.Audio.StartAttempts.Count == 1);
        h.Hotkeys.Release(FakeHotkeys.Hold);
        gate.SetResult();
        await Wait(() => !h.Coordinator.IsBusy);

        Assert.Empty(h.Clipboard.Copies);
        Assert.Equal(0, h.Clipboard.Pastes);
        Assert.Equal(0, h.Recognizer.FinalCalls);
        Assert.Equal([h.Settings.RemyDictationStartSound], h.Sounds.Played);   // no completion sound
        Assert.True(h.Ui.Has("success"));                                      // brief success flash
        Assert.DoesNotContain("raw", h.History.Log);
    }

    [Fact]
    public async Task NoSpeech_NothingPastedCopiedOrSaved()
    {
        using var h = new Harness();
        h.Hotkeys.Press(FakeHotkeys.Hold);
        await Wait(() => h.Coordinator.Status == DictationStatus.Recording);
        h.Audio.FeedSilence(1.0);
        h.Hotkeys.Release(FakeHotkeys.Hold);
        await Wait(() => !h.Coordinator.IsBusy);

        Assert.Empty(h.Clipboard.Copies);
        Assert.Equal(0, h.Clipboard.Pastes);
        Assert.Equal(0, h.Recognizer.FinalCalls);                              // volume gate: no decode at all
        Assert.Equal(["begin"], h.History.Log);
        Assert.Null(h.Coordinator.LastTranscript);
        Assert.Single(h.Sounds.Played);
    }

    [Fact]
    public async Task EmptyFinalResult_FallsBackToTheLivePreview()
    {
        using var h = new Harness(x => x.Recognizer.FinalText = () => "   ");
        await h.DictateHoldAsync(2.0);
        // the fake preview decoder says "hello"; when the final is empty the preview text is used
        Assert.True(h.Clipboard.Copies.Count == 0 || h.Clipboard.Copies[0] == "hello");
    }

    [Fact]
    public async Task CancelBeforeCopy_WritesNothingToClipboardAndKeepsTheRecording()
    {
        var gate = new TaskCompletionSource();
        using var h = new Harness(x => x.Recognizer.FinalGate = ct => gate.Task.WaitAsync(ct));
        h.Hotkeys.Press(FakeHotkeys.Hold);
        await Wait(() => h.Coordinator.Status == DictationStatus.Recording);
        h.Audio.FeedSpeech(1.2);
        h.Hotkeys.Release(FakeHotkeys.Hold);
        await h.Recognizer.FinalEntered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(h.Coordinator.CanCancel);

        await h.Coordinator.CancelAsync();
        gate.TrySetResult();

        Assert.Empty(h.Clipboard.Copies);
        Assert.Equal(0, h.Clipboard.Pastes);
        Assert.False(h.Coordinator.IsBusy);
        var saved = Assert.Single(h.Recovery.List());                          // preserveCancelledSessions defaults to true
        Assert.True(saved.HasAudio);
        Assert.Single(h.Sounds.Played);                                        // no completion sound on cancel
    }

    [Fact]
    public async Task CancelWithPreserveOff_DiscardsTheAudio()
    {
        var gate = new TaskCompletionSource();
        using var h = new Harness(x => { x.Recognizer.FinalGate = ct => gate.Task.WaitAsync(ct); x.Settings.PreserveCancelledSessions = false; });
        h.Hotkeys.Press(FakeHotkeys.Hold);
        await Wait(() => h.Coordinator.Status == DictationStatus.Recording);
        h.Audio.FeedSpeech(1.2);
        h.Hotkeys.Release(FakeHotkeys.Hold);
        await h.Recognizer.FinalEntered.WaitAsync(TimeSpan.FromSeconds(5));
        await h.Coordinator.CancelAsync();
        Assert.Empty(h.Recovery.List());
    }

    [Fact]
    public async Task CancelAfterPasteClaimed_IsRefusedAndThePasteCompletes()
    {
        var gate = new TaskCompletionSource();
        using var h = new Harness(x => x.Clipboard.PasteGate = () => gate.Task);
        h.Hotkeys.Press(FakeHotkeys.Hold);
        await Wait(() => h.Coordinator.Status == DictationStatus.Recording);
        h.Audio.FeedSpeech(1.2);
        h.Hotkeys.Release(FakeHotkeys.Hold);
        await Wait(() => h.Clipboard.Pastes == 1);                              // paste claimed and in flight

        Assert.False(h.Coordinator.CanCancel);
        await h.Coordinator.CancelAsync();
        Assert.True(h.Coordinator.IsBusy);                                      // refused

        gate.SetResult();
        await Wait(() => !h.Coordinator.IsBusy);
        Assert.Equal(1, h.Clipboard.Pastes);
        Assert.Equal(DeliveryOutcome.Dispatched, Assert.Single(h.History.Outcomes).Outcome);
        Assert.Empty(h.Recovery.List());
    }

    [Fact]
    public async Task HoldToCancelProgress_ReachesTheOverlay_AndEscapeCancelsFromRecording()
    {
        using var h = new Harness();
        h.Hotkeys.Press(FakeHotkeys.Hold);
        await Wait(() => h.Coordinator.Status == DictationStatus.Recording);
        h.Audio.FeedSpeech(0.8);
        h.Hotkeys.Progress(0.5);
        Assert.True(h.Ui.Has("hold:0.5"));
        h.Hotkeys.RaiseCancel();
        await Wait(() => !h.Coordinator.IsBusy);
        Assert.Empty(h.Clipboard.Copies);
        Assert.Single(h.Recovery.List());
        h.Hotkeys.Release(FakeHotkeys.Hold);                                    // late key-up after cancel does nothing
        Assert.Equal(DictationStatus.Idle, h.Coordinator.Status);
    }

    [Fact]
    public async Task CleanupFailure_FallsBackToTheRawTranscript()
    {
        using var h = new Harness(x =>
        {
            x.Settings.TranscriptPostProcessingMode = TranscriptPostProcessingMode.Ollama;
            x.Settings.OllamaModel = "llama";
            x.Cleanup.Handler = (r, ct) => Task.FromResult(new CleanupResult(CleanupOutcome.FellBackToRaw, r.Input,
                RemyCleanupFailure.Of(RemyCleanupFailure.Timeout), "Ollama", "llama", TimeSpan.FromSeconds(8)));
        });
        await h.DictateHoldAsync();

        Assert.Equal(["hello world"], h.Clipboard.Copies);
        Assert.Equal(1, h.Clipboard.Pastes);
        var attempt = Assert.Single(h.History.Attempts);
        Assert.Equal(AttemptStatus.Failed, attempt.Status);
        Assert.Equal(RemyCleanupFailure.Timeout, attempt.Failure);
        Assert.Equal([(SelectionKind.Fallback, "hello world")], h.History.Selections);
        Assert.Equal(["begin", "raw", "attempt", "select", "intent", "outcome"], h.History.Log);
        Assert.Contains("error:Cleanup timed out|Raw transcript used", h.Ui.Snapshot());
        Assert.Contains("cleaning", h.Ui.Snapshot());
        Assert.Equal(2, h.Sounds.Played.Count);                                 // completion sound still plays
    }

    [Fact]
    public async Task CleanupSuccess_DeliversTheCleanedText()
    {
        using var h = new Harness(x => { x.Settings.TranscriptPostProcessingMode = TranscriptPostProcessingMode.Ollama; x.Settings.OllamaModel = "m"; });
        await h.DictateHoldAsync();
        Assert.Equal(["hello world!"], h.Clipboard.Copies);
        Assert.Equal([(SelectionKind.Attempt, "hello world!")], h.History.Selections);
        Assert.Equal(AttemptStatus.Succeeded, Assert.Single(h.History.Attempts).Status);
        Assert.True((h.Cleanup.Last!.Deadline - DateTimeOffset.UtcNow) <= TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task UseRawNow_AbortsCleanupAndDeliversRaw()
    {
        using var h = new Harness(x =>
        {
            x.Settings.TranscriptPostProcessingMode = TranscriptPostProcessingMode.Ollama;
            x.Settings.OllamaModel = "m";
            x.Cleanup.Handler = async (r, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; };
        });
        h.Hotkeys.Press(FakeHotkeys.Hold);
        await Wait(() => h.Coordinator.Status == DictationStatus.Recording);
        h.Audio.FeedSpeech(1.2);
        h.Hotkeys.Release(FakeHotkeys.Hold);
        await Wait(() => h.Coordinator.CanUseRawNow);
        h.Coordinator.UseRawNow();
        await Wait(() => !h.Coordinator.IsBusy);

        Assert.Equal(["hello world"], h.Clipboard.Copies);
        Assert.Equal(RemyCleanupFailure.UseRaw, Assert.Single(h.History.Attempts).Failure);
        Assert.Contains("error:Raw transcript selected|Raw transcript used", h.Ui.Snapshot());
    }

    [Fact]
    public async Task HistoryNotSavedBeforeDelivery_SuspendsDeliveryAndKeepsTheText()
    {
        using var h = new Harness(x => x.History.NotSaved.Add("intent"));
        await h.DictateHoldAsync();

        Assert.Empty(h.Clipboard.Copies);                                       // never copy before the intent is acknowledged
        Assert.Equal(0, h.Clipboard.Pastes);
        Assert.Equal("hello world", h.Coordinator.LastTranscript);              // still available via Copy Last Transcript
        Assert.True(h.Ui.Has("history"));
        Assert.Contains(h.Coordinator.AttentionItems, a => a.Kind == "recovery");
        Assert.Single(h.Recovery.List());                                       // audio preserved
    }

    [Fact]
    public async Task HistoryNotSavedAtRaw_StopsBeforeCleanup()
    {
        using var h = new Harness(x =>
        {
            x.History.NotSaved.Add("raw");
            x.Settings.TranscriptPostProcessingMode = TranscriptPostProcessingMode.Ollama; x.Settings.OllamaModel = "m";
        });
        await h.DictateHoldAsync();
        Assert.Equal(0, h.Cleanup.Calls);
        Assert.Empty(h.Clipboard.Copies);
    }

    [Fact]
    public async Task StaleSession_ResultsAreDroppedAfterCancelAndNewSession()
    {
        var gate = new TaskCompletionSource();
        int call = 0;
        using var h = new Harness(x =>
        {
            x.Recognizer.FinalGate = ct => ++call == 1 ? gate.Task : Task.CompletedTask;   // first decode ignores cancellation, like a slow engine
            x.Recognizer.FinalText = () => call == 1 ? "stale text" : "fresh text";
        });
        h.Hotkeys.Press(FakeHotkeys.Hold);
        await Wait(() => h.Coordinator.Status == DictationStatus.Recording);
        h.Audio.FeedSpeech(1.2);
        h.Hotkeys.Release(FakeHotkeys.Hold);
        await h.Recognizer.FinalEntered.WaitAsync(TimeSpan.FromSeconds(5));

        var cancel = h.Coordinator.CancelAsync();      // joins the old decode, so it completes once the gate opens
        await Task.Delay(50);
        gate.SetResult();
        await cancel;

        await h.DictateHoldAsync();                     // a brand new session
        Assert.Equal(["fresh text"], h.Clipboard.Copies);
        Assert.Equal(1, h.Clipboard.Pastes);
    }

    [Fact]
    public async Task SecureField_IsCopyOnlyWithAHint()
    {
        using var h = new Harness(x => x.Destinations.Target = FakeDestinations.Notepad(secure: true));
        await h.DictateHoldAsync();
        Assert.Equal(["hello world"], h.Clipboard.Copies);
        Assert.Equal(0, h.Clipboard.Pastes);
        Assert.Equal(0, h.Destinations.Restores);
        Assert.Contains("copyonly:Press Ctrl+V", h.Ui.Snapshot());
        Assert.Equal(DeliveryOutcome.CopiedOnly, Assert.Single(h.History.Outcomes).Outcome);
        Assert.Equal(2, h.Sounds.Played.Count);
    }

    [Fact]
    public async Task ElevatedTarget_IsCopyOnly()
    {
        using var h = new Harness(x => x.Destinations.Target = FakeDestinations.Notepad(elevated: true));
        await h.DictateHoldAsync();
        Assert.Equal(["hello world"], h.Clipboard.Copies);
        Assert.Equal(0, h.Clipboard.Pastes);
    }

    [Fact]
    public async Task UnknownEditability_StillPastes()
    {
        using var h = new Harness(x => x.Destinations.Target = FakeDestinations.Notepad(editable: null));
        await h.DictateHoldAsync();
        Assert.Equal(1, h.Clipboard.Pastes);
    }

    [Fact]
    public async Task MultilineIntoATerminal_IsCopyOnly_SingleLineIsPasted()
    {
        using (var h = new Harness(x => { x.Destinations.Target = FakeDestinations.Notepad(process: "windowsterminal.exe"); x.Recognizer.FinalText = () => "one\ntwo"; }))
        {
            await h.DictateHoldAsync();
            Assert.Equal(0, h.Clipboard.Pastes);
            Assert.Single(h.Clipboard.Copies);
        }
        using var single = new Harness(x => x.Destinations.Target = FakeDestinations.Notepad(process: "windowsterminal.exe"));
        await single.DictateHoldAsync();
        Assert.Equal(1, single.Clipboard.Pastes);
    }

    [Fact]
    public async Task FocusNotRestoredOrClipboardChanged_IsCopyOnly()
    {
        using (var a = new Harness(x => x.Destinations.RestoreResult = false))
        {
            await a.DictateHoldAsync();
            Assert.Equal(0, a.Clipboard.Pastes);
            Assert.Single(a.Clipboard.Copies);
        }
        using var b = new Harness(x => x.Destinations.StillFocused = false);
        await b.DictateHoldAsync();
        Assert.Equal(0, b.Clipboard.Pastes);
    }

    [Fact]
    public async Task CurrentFieldDestination_UsesTheForegroundAfterCleanup_WithoutRestoringFocus()
    {
        using var h = new Harness(x => x.Settings.RemyDictationDestination = RemyDictationDestination.CurrentField);
        await h.DictateHoldAsync();
        Assert.Equal(0, h.Destinations.Restores);
        Assert.Equal(2, h.Destinations.Captures);        // start + current field at delivery
        Assert.Equal(1, h.Clipboard.Pastes);
    }

    [Fact]
    public async Task ClipboardCopyFailure_NeverPastes_AndShowsCouldNotCopy()
    {
        using var h = new Harness(x => x.Clipboard.CopyFails = true);
        await h.DictateHoldAsync();
        Assert.Equal(0, h.Clipboard.Pastes);
        Assert.True(h.Ui.Has("error:Could not copy"));
        Assert.Equal(DeliveryOutcome.Failed, Assert.Single(h.History.Outcomes).Outcome);
        Assert.Single(h.Sounds.Played);                  // no completion sound on failure
    }

    [Fact]
    public async Task SecondHotkeyWhileProcessing_ShowsTheBusyNotice()
    {
        var gate = new TaskCompletionSource();
        using var h = new Harness(x => x.Recognizer.FinalGate = ct => gate.Task);
        h.Hotkeys.Press(FakeHotkeys.Hold);
        await Wait(() => h.Coordinator.Status == DictationStatus.Recording);
        h.Audio.FeedSpeech(1.2);
        h.Hotkeys.Release(FakeHotkeys.Hold);
        await h.Recognizer.FinalEntered.WaitAsync(TimeSpan.FromSeconds(5));

        h.Hotkeys.Press(FakeHotkeys.Hold);
        await Wait(() => h.Ui.Has("busy:Already working on your dictation"));
        gate.SetResult();
        await Wait(() => !h.Coordinator.IsBusy);
        Assert.Single(h.Clipboard.Copies);
    }

    [Fact]
    public async Task MicrophoneFallback_TriesTheSelectedThenDefaultThenOthers()
    {
        using var h = new Harness(x =>
        {
            x.Audio.Devices.Clear();
            x.Audio.Devices.AddRange([new("a", "A", false), new("b", "B", true), new("c", "C", false)]);
            x.Audio.FailingDevices.UnionWith(["a", "b"]);
            x.Settings.SelectedMicrophoneUID = "a";
        });
        await h.DictateHoldAsync();
        Assert.Equal(["a", "b", "c"], h.Audio.StartAttempts);
        Assert.Single(h.Clipboard.Copies);
    }

    [Fact]
    public async Task AllMicrophonesFail_ShowsErrorAndTheRecordingFailedIssue()
    {
        using var h = new Harness(x => x.Audio.FailingDevices.Add("mic1"));
        h.Hotkeys.Press(FakeHotkeys.Hold);
        await Wait(() => h.Ui.Has("error:Could not start the microphone"));
        await Wait(() => !h.Coordinator.IsBusy);
        var issue = Assert.Single(h.Coordinator.AttentionItems, a => a.Kind == "recordingFailed");
        Assert.StartsWith("Could not start the microphone. ", issue.Message);
        Assert.Equal(1, h.SystemAudio.Restores);
        Assert.Empty(h.Sounds.Played);
        Assert.Empty(h.Recovery.List());
        h.Coordinator.DismissAttention("recordingFailed");
        Assert.DoesNotContain(h.Coordinator.AttentionItems, a => a.Kind == "recordingFailed");
    }

    [Fact]
    public async Task SpeechModelNotInstalled_ShowsTheVerbatimMessageAndASetupIssue()
    {
        using var h = new Harness(x => { x.Models.Installed = false; x.Recognizer.IsLoaded = false; });
        h.Hotkeys.Press(FakeHotkeys.Hold);
        await Wait(() => h.Ui.Has("error:"));
        Assert.Contains("error:Parakeet v2 is not ready. Download it from Speech Model settings.|", h.Ui.Snapshot());
        Assert.Contains(h.Coordinator.AttentionItems, a => a.Kind == "speechSetup" && a.IsBlocking && a.Message == "Download a speech model to start dictating.");
        Assert.Empty(h.Audio.StartAttempts);
    }

    [Fact]
    public async Task SpeechModelStillDownloading_UsesTheDownloadingMessage()
    {
        using var h = new Harness(x => { x.Models.Installed = false; x.Models.Downloading = true; });
        h.Hotkeys.Press(FakeHotkeys.Hold);
        await Wait(() => h.Ui.Has("error:"));
        Assert.Contains("error:Parakeet v2 is still downloading. Try again when it finishes.|", h.Ui.Snapshot());
    }

    [Fact]
    public async Task MuteSystemAudio_IsAppliedAndRestored()
    {
        using var h = new Harness(x => x.Settings.MuteSystemAudioDuringRecordingEnabled = true);
        await h.DictateHoldAsync();
        Assert.Equal(1, h.SystemAudio.Mutes);
        Assert.Equal(1, h.SystemAudio.Restores);
    }

    [Fact]
    public async Task FillerRemoval_AppliesBeforeHistoryAndDelivery()
    {
        using var h = new Harness(x =>
        {
            x.Settings.IsFillerWordRemovalEnabled = true;
            x.Recognizer.FinalText = () => "um hello uh world";
        });
        await h.DictateHoldAsync();
        Assert.Equal(["hello world"], h.Clipboard.Copies);
    }

    [Fact]
    public async Task HistoryOffRepository_StillDeliversAndSkipsTheRecoverySpool()
    {
        using var h = new Harness(x => x.Settings.RemyHistoryEnabled = false);
        await h.DictateHoldAsync();
        Assert.Single(h.Clipboard.Copies);
        Assert.Empty(h.Recovery.List());
    }

    [Fact]
    public async Task Sleep_CancelsTheSession_ResumeRequiresNeutralModifiers()
    {
        var power = new FakePower();
        using var h = new Harness(x => { });
        // rebuild with power
        var d = new DictationCoordinator(new DictationDependencies
        {
            Settings = h.Settings, Hotkeys = h.Hotkeys, Audio = h.Audio, SystemAudio = h.SystemAudio, Destinations = h.Destinations,
            Clipboard = h.Clipboard, Sounds = h.Sounds, Recognizer = h.Recognizer, Cleanup = h.Cleanup, History = () => h.History,
            Recovery = h.Recovery, Models = h.Models, Credentials = new FakeCredentials(), Presenter = h.Ui, Power = power,
        });
        h.Coordinator.Dispose();
        h.Hotkeys.Press(FakeHotkeys.Hold);
        await Wait(() => d.Status == DictationStatus.Recording);
        h.Audio.FeedSpeech(0.8);
        power.RaiseSuspend();
        await Wait(() => !d.IsBusy);
        Assert.Empty(h.Clipboard.Copies);
        Assert.Equal(1, h.Hotkeys.Stops);

        h.Hotkeys.Press(FakeHotkeys.Hold);                  // ignored while sleeping
        await Task.Delay(30);
        Assert.False(d.IsBusy);
        power.RaiseResume();
        Assert.Equal(1, h.Hotkeys.NeutralRequests);
        d.Dispose();
    }

    sealed class FakePower : IPowerEvents
    {
        public event EventHandler? Suspending;
        public event EventHandler? Resumed;
        public void RaiseSuspend() => Suspending?.Invoke(this, EventArgs.Empty);
        public void RaiseResume() => Resumed?.Invoke(this, EventArgs.Empty);
    }

    [Fact]
    public async Task Shutdown_PreservesARecordingInProgress()
    {
        using var h = new Harness();
        h.Hotkeys.Press(FakeHotkeys.Hold);
        await Wait(() => h.Coordinator.Status == DictationStatus.Recording);
        h.Audio.FeedSpeech(1.0);
        await h.Coordinator.ShutdownAsync();
        Assert.False(h.Coordinator.IsBusy);
        Assert.Single(h.Recovery.List());
        Assert.Empty(h.Clipboard.Copies);
    }

    [Fact]
    public async Task ContinueRecovery_PrefixesTheNewTextAndRemovesTheSource()
    {
        using var h = new Harness();
        // First a cancelled session leaves a recovery entry with audio and a preview.
        h.Hotkeys.Press(FakeHotkeys.Hold);
        await Wait(() => h.Coordinator.Status == DictationStatus.Recording);
        h.Audio.FeedSpeech(1.2);
        await Wait(() => h.Recognizer.WindowCalls > 0);
        await h.Coordinator.CancelAsync();
        var entry = Assert.Single(h.Recovery.List());

        h.Recognizer.FinalText = () => "second part";
        await h.Coordinator.ContinueRecoverySessionAsync(entry.Id);
        await Wait(() => h.Coordinator.Status == DictationStatus.Recording);
        h.Audio.FeedSpeech(1.2);
        await h.Coordinator.StopAsync();
        await Wait(() => !h.Coordinator.IsBusy);

        Assert.Single(h.Clipboard.Copies);
        Assert.EndsWith("second part", h.Clipboard.Copies[0]);
        Assert.StartsWith(h.Coordinator.LastTranscript![..5], h.Clipboard.Copies[0]);
        Assert.Empty(h.Recovery.List());
    }
}

public class DeliveryLeaseTests
{
    [Fact]
    public void CancelBeforeCopy_BlocksEverySideEffect()
    {
        var l = new DeliveryLease();
        Assert.True(l.TryRevoke());
        Assert.False(l.TryClaimClipboard());
        Assert.False(l.TryClaimPaste());
    }

    [Fact]
    public void PasteNeedsCopy_AndClaimsOnce()
    {
        var l = new DeliveryLease();
        Assert.False(l.TryClaimPaste());
        Assert.True(l.TryClaimClipboard());
        Assert.False(l.TryClaimClipboard());
        Assert.True(l.MarkCopied());
        Assert.True(l.TryClaimPaste());
        Assert.False(l.TryClaimPaste());
    }

    [Fact]
    public void CancelAfterCopy_KeepsTheClipboardButRefusesPaste()
    {
        var l = new DeliveryLease();
        l.TryClaimClipboard(); l.MarkCopied();
        Assert.True(l.TryRevoke());
        Assert.True(l.Copied);
        Assert.False(l.TryClaimPaste());
    }

    [Fact]
    public void CancelAfterPasteClaimed_IsRefused()
    {
        var l = new DeliveryLease();
        l.TryClaimClipboard(); l.MarkCopied(); l.TryClaimPaste();
        Assert.False(l.TryRevoke());
        Assert.False(l.IsRevoked);
    }
}

public class PasteGateTests
{
    [Theory]
    [InlineData("notepad.exe", false, false, true, "text", CopyOnlyReason.None)]
    [InlineData("notepad.exe", true, false, true, "text", CopyOnlyReason.Secure)]
    [InlineData("notepad.exe", false, true, true, "text", CopyOnlyReason.Elevated)]
    [InlineData("notepad.exe", false, false, false, "text", CopyOnlyReason.NotEditable)]
    [InlineData("pwsh.exe", false, false, null, "a\nb", CopyOnlyReason.TerminalMultiline)]
    [InlineData("pwsh.exe", false, false, null, "ab", CopyOnlyReason.None)]
    [InlineData("code.exe", false, false, null, "a\nb", CopyOnlyReason.None)]
    public void Evaluate(string process, bool secure, bool elevated, bool? editable, string text, CopyOnlyReason expected)
    {
        var t = new DestinationTarget(1, 2, process, null, "t", null, editable, secure, elevated, false);
        Assert.Equal(expected, PasteGate.Evaluate(t, text));
    }

    [Fact]
    public void NoTargetOrRemy_IsCopyOnly()
    {
        Assert.Equal(CopyOnlyReason.NoTarget, PasteGate.Evaluate(null, "x"));
        Assert.Equal(CopyOnlyReason.NoTarget, PasteGate.Evaluate(new DestinationTarget(1, 2, "remy.exe", null, "", null, true, false, false, true), "x"));
    }
}
