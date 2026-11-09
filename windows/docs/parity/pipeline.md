# Parity spec: dictation session pipeline (hotkey to delivered text), history and recovery

Source of truth: `app/Dictate Anywhere/` (Swift, macOS). All paths below are relative to that folder unless noted. Line numbers refer to the checked-out `windows-port` branch (HEAD 051eaa1). Design docs consulted: `docs/engineering/session-contracts.md`, `docs/engineering/long-dictation-recovery-audit.md`, `docs/privacy.md`, `docs/architecture.md`, `windows/spike/RESULTS.md`.

Audience: Windows implementers who will not read Swift. Everything a feature must do is stated here. Anything marked **MAC-ONLY** is a macOS mechanism with a one-line suggested Windows equivalent. Anything marked **WINDOWS DECISION** is a place where the Mac behavior cannot or should not be copied literally and the implementer/lead must choose.

Scope reminders (what the shipping Mac build actually does, which differs from what the code can do):

- Cloud speech (AssemblyAI) is **disabled** by policy: `RemyFeaturePolicy.allowsCloudSpeechRecognition = false`, `supportedEngineChoices = [.parakeet, .appleSpeech]`, a stored `assemblyAI` choice is coerced to Parakeet (`RemyFeaturePolicy.swift:1-16`). Do not port AssemblyAI. Apple Speech is also Mac-only. The Windows engine is "Parakeet TDT 0.6B v2" only (Mac name `englishOnly`, `Settings.swift:258`).
- Mac Parakeet v2 is **English only** and uses the "batch" decode path (growing-window previews, whole-recording final). That is the path specified below. Other model families (v3, 110M, EOU, Nemotron, SenseVoice) exist on Mac and are out of scope except where noted.
- The foreground paste path always runs with `verbatimPayload: true`, which **disables all cursor-context spacing, capitalization and list formatting** (section 5.5). Most of `TextInserter`'s formatting code is dormant. Do not port the dormant code.

---

## 0. Glossary and end-to-end flow

| Term | Meaning |
| --- | --- |
| Session | One dictation from hotkey-down to completion. At most one foreground session exists. `RemySessionCoordinator` owns its token. |
| Token | `(UUID id, UInt64 generation)`. Generation increments at each `begin`. A late async result must be dropped if its token is not current. |
| Preview | Disposable live text shown in the overlay while recording. |
| Final raw | The authoritative recognition result after Stop. Immutable once recorded. |
| Cleanup | Optional LLM or local post-processing of the final text (separate spec; interface only here). |
| Delivery | Clipboard copy plus synthetic paste into the destination app. |
| Delivery permit / lease | A thread-safe object (`RemyDeliveryAuthorization`) that cancel can revoke; every side effect claims it first. |
| Recovery capture | A temporary 16 kHz mono audio file written during recording so failures can be recovered. |

Happy-path order in `AppState.finishDictation` (`AppState.swift:2146-2515`):

1. Hotkey down -> `startDictation` (capture destination proof, permission checks, model ready check) -> `beginRecording` (freeze configuration, begin session, open history session, start recovery capture, start microphone, play start sound, show overlay).
2. While recording: level poll every 33 ms, transcript poll every ~200 ms, live preview decode driven by captured audio time.
3. Hotkey up (hold) or second tap (toggle) -> `stopDictation` -> state `processing`, overlay `processing`.
4. Stop capture, flush converter tail, close recovery file; final decode of the whole recording.
5. Empty-text guard; filler removal; save raw to history (must be acknowledged); cleanup (8 s deadline); save selected text; save delivery intent (must be acknowledged); authorize delivery permit.
6. Resolve destination (original or current field); copy to clipboard; guarded paste.
7. Record delivery outcome in history; restore mic volume and system mute; play completion sound; overlay result; discard recovery audio; session complete; status idle.

---

## 1. Activation: hotkeys, cancel shortcut, recorder

### 1.1 What exists

There are exactly these activation inputs. There is **no** global "quick vocabulary" hotkey and no other global shortcut.

| Input | Where | Notes |
| --- | --- | --- |
| Up to 5 recording shortcuts ("bindings") | `Settings.hotkeyBindings`, UI limit `maxBindings = 5` (`ShortcutsView.swift:24`) | At least 1 binding row always exists; it may be empty ("Not set"). |
| 1 cancel shortcut | `Settings.cancelShortcut`, plus `holdToCancel` | Active only while a session can be cancelled. |
| Menu bar items | `AppDelegate.swift:111-165` | "Open Remy", "Copy Last Transcript", "Stop Dictation", "Use Raw Now", "Cancel Dictation", "Add Custom Vocabulary", "Microphone" submenu, "Quit". |
| Quick vocabulary | Menu item "Add Custom Vocabulary" -> `QuickVocabularyPanel.shared.toggle()` (`AppDelegate.swift:300-302`) | A small floating always-on-top panel (408x336, title "Custom Vocabulary"). No hotkey. Menu item is hidden when the cleanup mode does not support custom vocabulary (`AppDelegate.swift:336-342`). |

Menu item visibility (`AppDelegate.swift:329-343`): "Copy Last Transcript" enabled only if the last transcript is non-empty; "Stop Dictation" shown only when `canStopDictation` (status == recording and not transitioning); "Cancel Dictation" shown when `canCancelDictation`; "Use Raw Now" shown when a cleanup attempt is running and undecided.

### 1.2 Data model (`Settings.swift:778-850`)

`HotkeyBinding { id: UUID, keyCode: UInt16?, modifiersRawValue: UInt64, displayName: String, mode }`.

- `mode` is `holdToRecord` ("Hold to Record") or `handsFreeToggle` ("Tap to Toggle"). Raw values `"holdToRecord"`, `"handsFreeToggle"`.
- `hasBinding` = has a key code OR a non-empty modifier set.
- A binding with `keyCode == nil` and modifiers is a **modifier-only** shortcut.
- Modifiers have a generic bit (command/control/option/shift) plus optional left/right bits. A generic bit is auto-set whenever a left or right bit is set (`normalizedHotkeyModifiers`, `Settings.swift:2368-2383`). Also `function` (Fn/Globe). Caps Lock is **not supported** as a shortcut: any binding containing the Caps Lock key (code 57) or flag is blanked on load (`canonicalizedHotkeyBinding`, `Settings.swift:2429-2457`).
- Fn/Globe key codes `63` and `179` are canonicalized: the binding becomes modifier-only with the `function` modifier (`Settings.swift:2438-2445`, `HotkeyService.swift:341-348`).
- **Defaults** (`Settings.swift:838-849`, `1782`): recording binding = modifier-only Control+Option+Command (display "⌃⌥⌘"), mode `holdToRecord`. Cancel binding = key code 53 (Escape) with no modifiers, display "Esc", `holdToCancel = true`, `preserveCancelledSessions = true` (`Settings.swift:1741-1745`). On first run the bindings list is `[defaultBinding]`.
- **WINDOWS DECISION:** Control+Option+Command has no sane Windows analog (Win key reserved by shell; Ctrl+Alt+Win is unusual but possible). Pick a default (for example Right Ctrl hold, or Ctrl+Alt+Space) and keep "modifier-only hold" as a supported shape. Store the same fields; key codes become Windows virtual-key codes and left/right modifier flags map to VK_LCONTROL/VK_RCONTROL etc.

### 1.3 Hold-to-record vs tap-to-toggle: exact semantics

There is **no timing logic at all** that distinguishes a "tap" from a "hold". No long-press threshold, no double-tap, no minimum hold duration, no debounce interval. The distinction is purely the binding's `mode`, and the handlers are (`AppState.swift:413-461`):

`onKeyDown(binding)`:
- Ignored if the system is sleeping or the activation generation changed since the event was queued.
- `holdToRecord`: set `isHoldToRecordKeyDown = true`, then `startDictation(mode: holdToRecord)`.
- `handsFreeToggle`: if `status == recording` then `stopDictation()`, else `startDictation(mode: handsFreeToggle)`.

`onKeyUp(binding)`:
- Ignored for sleeping / stale generation, and ignored entirely when `binding.mode != holdToRecord`.
- For hold: set `isHoldToRecordKeyDown = false`; if `status == recording && !isTransitioning` -> `stopDictation()`; else if `isTransitioning` -> set `pendingHoldRelease = true` (start is still running; it will stop right after recording begins, `AppState.swift:2099-2112`).

Consequences implementers must reproduce:

1. A very short hold still starts and stops a session. If the key is released before recording has actually begun, `startDictation` checks `isHoldToRecordKeyDown` at two points (`AppState.swift:1800,1804`) and silently aborts with no recording, no sound, no message. If release happens after `isTransitioning` was set, recording starts and is stopped immediately after the mic is live (`pendingHoldRelease`). A near-silent/empty result then follows the "no text" path (section 5.2): overlay flashes success for 0.5 s, nothing is pasted, nothing is copied, no completion sound.
2. Pressing a hold-binding while a toggle-started session is recording: `startDictation` refuses (status is not idle) and shows nothing (busy overlay only shows if status != recording, `AppState.swift:1771-1773`). Its later key-up WILL stop the toggle session (`isHoldToRecordKeyDown` was set true; key-up sees status == recording). Preserve or deliberately fix; it is an unintentional edge.
3. Tap-to-toggle key-up is ignored. Auto-repeat is ignored (below).
4. A pressed binding never retriggers until it fully releases (`activeBindingIDs` set, `HotkeyService.swift:372-388`).
5. If a session is busy (anything other than idle, including processing/cleanup), a new key-down shows the overlay notice "Already working on your dictation" (no trailing period in code, `OverlayContent.swift:68`; docs write it with a period) and does nothing else. The notice has no timer; it clears on the next state update or hide (`OverlayWindow.swift:111-121`). It is shown by `overlay.showBusy()` from `startDictation` when: cleanup is draining (`AppState.swift:1766`), or status != idle / transitioning / a start request in flight and status != recording and not cancelling (`1771-1773`).
6. `startDictation` is also refused silently when: shutting down, system sleeping, Apple Speech install in progress, history preparing, or stored-history deletion in progress (`AppState.swift:1765`).

Modifier-only matching (`HotkeyService.swift:392-419`), evaluated on every modifier-change event:
- "Active" means the event's modifier set matches the target **exactly** in the modifier groups (`modifierOnlyModifiersMatch`, `Settings.swift:2418-2427`): every target modifier is held, and **no additional** command/control/option/shift/fn is held. If a target specifies a side (left/right) only that side counts; if the target is generic either side counts but exactly-matching still forbids extras of other groups.
- hold mode: becoming active -> key-down callback; becoming inactive -> key-up callback.
- toggle mode: becoming active -> key-down callback once; becoming inactive -> just clears active state (no callback).
- Modifier-only events are **never swallowed**; the OS and apps still see the modifiers. Adding an extra modifier while holding the combo (for example Shift) makes it inactive and therefore stops a hold session.

Keyed shortcut matching (`HotkeyService.swift:350-390`):
- Target key code must match; modifiers use **non-exact** matching (`keyedModifiersMatch`): all target modifiers present (sides respected), extra modifiers allowed.
- key-down: only if not auto-repeat and not already active; marks active and fires key-down callback. Returns "consume" so the key never reaches other apps. key-up: if active, fires key-up callback and consumes; allowed even if modifiers were released first (`type == keyUp && isActive` bypasses the modifier check).
- Both key-down and key-up of a matched keyed shortcut are swallowed (not delivered to the focused app). If the binding has no modifiers it swallows that bare key globally while monitoring is on.

Event queueing: callbacks are dispatched asynchronously to the main thread, tagged with a `gestureGeneration`; stale generations are dropped (`HotkeyService.swift:140-148`). The generation changes on `configureBindings`, `stopMonitoring`, `resumeAfterSystemWake`.

### 1.4 Hotkey service lifecycle

- **MAC-ONLY mechanism:** a global CGEvent tap (`cghidEventTap`, head insert, default tap so it can swallow events) listening for keyDown, keyUp, flagsChanged (`HotkeyService.swift:181-208`). Needs Accessibility permission. **Windows equivalent:** a low-level keyboard hook (`SetWindowsHookEx(WH_KEYBOARD_LL)`) on a dedicated thread with its own message pump, tracking modifier state itself; return non-zero to swallow keyed events. Keep the hook callback tiny (Windows removes slow hooks; LowLevelHooksTimeout) and hand off to the UI thread via a queue tagged with a generation id. Re-install the hook if it is silently dropped (see tap-disabled handling below). RegisterHotKey cannot do modifier-only or hold/release, so it is not sufficient.
- Monitoring starts only if at least one recording binding or the cancel shortcut has a binding (`HotkeyService.swift:168-173`), and only after the OS permission is granted. On Mac: if Accessibility is not granted, monitoring is stopped and permission polling runs every 2.5 s (`AppState.swift:1169-1183`, `Permissions.swift:171-181`). **Windows:** no permission needed for the hook; skip this.
- Tap creation failure -> retry with exponential backoff `min(0.5 * 2^(attempt-1), 5.0)` seconds (0.5, 1, 2, 4, 5, 5...) until it succeeds (`HotkeyService.swift:210-231`).
- Tap disabled by timeout/user input (system disabled it) -> reset the cancel gesture and re-enable the tap immediately (unless sleeping) (`HotkeyService.swift:434-441`).
- Settings edits call `restartMonitoring()` (stop, then start), which rebuilds the snapshot of bindings the callback thread reads. Recording a new shortcut stops monitoring while the recorder is open and restarts it afterward (`ShortcutsView.swift:57-110,191-200`).
- **Sleep/wake** (`AppState.swift:633-661`, `HotkeyService.swift:96-133`): on `willSleep`: set sleeping, change the activation generation, suspend the hotkey service (disables cancel, stops monitoring, remembers whether to resume), clear `isHoldToRecordKeyDown` and `pendingHoldRelease`, and cancel any session with reason `inputUnavailable` (preserving the session for recovery if that setting is on). On `didWake`: clear sleeping, `resumeAfterSystemWake`, which resumes monitoring but **requires all held modifiers (except Caps Lock) to return to neutral** before any shortcut can fire, so a pre-sleep hold cannot start/stop a recording. **Windows equivalent:** `WM_POWERBROADCAST` (PBT_APMSUSPEND / PBT_APMRESUMEAUTOMATIC) and `WM_WTSSESSION_CHANGE` (lock/unlock) should do the same; also consider session lock.

### 1.5 Cancel shortcut (`CancellationShortcut.swift`, `HotkeyService.swift:255-336`)

- Cancellation is enabled only while `canCancelDictation` is true (`AppState.swift:134-143`): `(status == recording || status == processing || a start request is in flight) && !isCancelling && !isDeliveringTranscript && !(paste already claimed) && no recovery in progress`. The hotkey service gets `isCancellationEnabled` updated on every status change. When disabled the cancel gesture is reset.
- **Hold-to-cancel is the default** (`holdToCancel = true`). With it on, the user must hold the full cancel shortcut for **1.0 second** (`progress = min(1, max(0, now - startedAt))`, `CancellationShortcut.swift:37`). A hold task polls every **25 ms**, reports progress 0..1 to the overlay, and fires `onCancel` once when progress reaches 1 (`HotkeyService.swift:296-308`). The overlay shows "Hold to cancel" with a progress bar (`OverlayContent.swift:187-199`) while progress is non-nil; progress is cleared (nil) on release, on fire, and on any gesture reset.
- Releasing early, losing a required modifier, or the key-up of the bound key resets the gesture; key auto-repeat never arms a gesture; a gesture only begins on key-down of the exact bound key (or, for a modifier-only cancel shortcut, on the modifier change) with all required modifiers matching **exactly** (`CancellationGesture.update`).
- While holding the recording hotkey (hold mode), modifiers belonging to that active recording binding are ignored when matching the cancel shortcut, unless they are part of the cancel shortcut itself (`cancellationModifiers`, `HotkeyService.swift:318-335`), so a user can hold push-to-talk and tap/hold Esc.
- **Event swallowing for the cancel shortcut:**
  - Plain Esc with hold-to-cancel ON: Esc is **not** swallowed; the foreground app still receives it during the hold.
  - Plain Esc with hold-to-cancel OFF ("immediate"): the matching key-down press is swallowed (so the destination app does not beep) and cancellation fires on that press; the matching key-up and any auto-repeats of that same physical press are also swallowed. A fresh key-down after a missed key-up re-arms normally (`ownsImmediateEscapePress`, `HotkeyService.swift:262-275`).
  - Any other cancel key combination (not plain Esc): its key-down/key-up are swallowed whenever a gesture is pressed or becomes pressed (reserved only while cancellation is enabled).
- Cancel conflicts (`ConflictDetector`, `Settings.swift:889-977`): a recording binding conflicts with the cancel shortcut if same key and cancel's modifiers satisfy the recording binding's (keyed vs keyed), or a modifier-only recording prefix of a keyed cancel, or exact modifier containment either way for two modifier-only shortcuts. Error strings (verbatim): recording side: "Choose a different combination from the cancel shortcut."; cancel side: "The cancel shortcut overlaps a start/stop shortcut. Choose another combination." The cancel recorder is created with `allowsEscape = true` (Esc is a legal cancel key; for recording shortcuts Esc aborts the recorder instead).
- System-conflict warnings (macOS-specific list, `Settings.swift:919-937`): Cmd+Space "Conflicts with macOS Spotlight", Cmd+Option+Space "... Finder Search", Control+Command (modifier-only) "... Dictation", Cmd+Option+Q and Cmd+Option+Esc "... Force Quit", Cmd+Shift+3/4/5 screenshots. These show as warnings under recording bindings (non-blocking) but **block** a cancel shortcut. Internal duplicate warning: "Duplicate of another shortcut". **WINDOWS DECISION:** supply a Windows list (Win+Space input switch, Alt+Tab, Ctrl+Alt+Del, Win+Shift+S, Win+V, etc.).
- UI text (verbatim, `ShortcutsView.swift`): section "Cancellation"; row "Cancel shortcut" caption "Choose a shortcut you won't use in other apps. Clear it to cancel only from the menu bar."; "Hold to cancel" caption "Hold the full cancel shortcut for one second. Releasing early keeps dictation running."; "Preserve cancelled sessions" caption "Keep cancelled sessions locally for 24 hours. Continue, recover text, or delete them in History. Changes apply to new dictations."; footer hint "Press any key combo, or press only modifiers (like ⌃⌥⌘) and release. Left and right modifiers are supported — for example, R⌘ uses only the right Command key."; binding captions "Tap once to start, tap again to stop" (toggle, title "Toggle dictation") and "Hold the keys down while you speak" (hold, title "Hold to record"); dropdown "Activation mode"; add button "Add another shortcut"; recorder prompts "Press any key combo…", "Not set", buttons "Record Shortcut", "Change", "Clear", "Cancel".
- Cancel gesture side effects: `stopDictation` calls `hotkeyService.resetCancellationGesture()` so a gesture in progress does not survive into processing.

### 1.6 Shortcut recorder logic (`ShortcutRecorderView.swift:100-286`)

- **MAC-ONLY mechanism:** a listen-only CGEvent tap for keyDown + flagsChanged; falls back to an NSEvent local monitor if the tap cannot be created. **Windows equivalent:** while the recorder is open, use the same low-level hook (or a window-level `PreviewKeyDown`/raw input) to observe keys without breaking normal typing.
- Rules:
  - While recording, **hotkey monitoring is stopped** (so the old shortcut cannot fire) and resumed after.
  - Esc cancels recording (no change) unless `allowsEscape` (cancel-shortcut recorder).
  - Caps Lock (code 57) is ignored (cannot be recorded).
  - Fn/Globe (63/179) keyDown is recorded as modifier-only `function` plus any held modifiers.
  - First non-modifier key-down is captured with the modifiers held at that moment as a keyed shortcut.
  - Modifier-only capture: track the **largest superset** of modifiers pressed since the first modifier went down (`pendingModifierFlags`; replaced only if the new set is a superset of the pending one, or pending is empty). When all modifiers are released and pending is non-empty, capture a modifier-only shortcut equal to that pending set.
  - Left/right sides are captured and preserved (side-aware bits, `L⌃`, `R⌘` display forms).
- Display name builder (`Settings.displayName`, `2290`): order is `fn`, Control `⌃`, Option `⌥`, Shift `⇧`, Command `⌘`, then the key name; side-aware prefixes "L" and "R". Windows should define its own symbols/names (Ctrl, Alt, Shift, Win) but keep the same ordering/side-awareness concepts.
- Persistence: JSON-encoded array under key `hotkeyBindings`; cancel shortcut under `cancelShortcut`; bool `holdToCancel`; bool `preserveCancelledSessions`. Legacy single-hotkey keys (`hotkeyKeyCode`, `hotkeyModifiers`, `hotkeyDisplayName`, `hotkeyMode`) migrate into the array once (`Settings.swift:1748-1783`) - not needed on Windows.

---

## 2. Session state machine

### 2.1 Two layers

1. `RemySessionCoordinator` (`RemySessionCoordinator.swift`) - the authoritative per-session state machine.
2. `AppState.status` (`AppState.swift:19-36`) - the UI-facing status: `idle`, `recording`, `processing`, `error(String)`. `error` is transient: it is set and immediately replaced by `idle` in most paths; startDictation resets it to idle before starting.

Coordinator states: `idle, preparing, recording, finalizing, cleaning, ready, delivering, completed, cancelled, failed`. Active = preparing...delivering.

Transitions (all return false and do nothing if the token is not current):

| Call | Allowed from | To | Notes |
| --- | --- | --- | --- |
| `begin(configuration, id)` | no active session | preparing | Returns nil if a session is already active; generation += 1; clears raw, failure, permit. |
| `markRecording` | preparing | recording | Called only after the microphone is confirmed live. |
| `requestStop` | recording | finalizing | `stopDictation` guard. |
| `recordFinalRaw(text)` | finalizing | (same) | Write-once; second call returns false. |
| `beginCleanup` | finalizing and raw recorded | cleaning | |
| `markReady` | finalizing or cleaning, raw recorded | ready | Grants **no** clipboard authority. |
| `authorizeDelivery` | ready, no permit yet | delivering | Creates one `DeliveryPermit` with a fresh `RemyDeliveryAuthorization` lease. |
| `complete` | ready or delivering | completed | Revokes lease. Ready->completed is the "no text" path. |
| `cancel(reason)` | any active | cancelled | If the lease reports `pasteClaimed` the result is `alreadyDispatched` and the cancel is **refused**; if `copied` then `clipboardAlreadyWritten`. |
| `fail(failure)` | any active | failed | Carries stage (`session, capture, recognition, cleanup, history, delivery`), category (`timeout, unavailable, invalidInput, persistence, provider, unknown`), `retryable`. |

`CancellationReason`: `user, superseded, shutdown, inputUnavailable`. Event sequence counter increments on accepted transitions and the write-once raw.

**Rule that matters for Windows:** every `await` in the pipeline is followed by a check `sessions.isCurrent(token) && !Task.isCancelled` and the code returns silently if it fails. Port this as a generation/token check after every await/thread hop. Cancellation never throws up to the user.

### 2.2 Delivery lease (`RemyDeliveryAuthorization.swift`)

A lock-protected object with flags `revoked, clipboardClaimed, copied, pasteClaimed`.

- `consumeClipboard()` -> claims initial ownership once (false if revoked or already claimed).
- `performClipboardMutation { ... }` -> under the lock, re-checks not revoked and clipboard claimed, runs a short synchronous clipboard write, records `copied = true` on success.
- `consumePaste()` -> only if not revoked, `copied == true`, and not already claimed; sets `pasteClaimed = true`. This is the irreversible boundary: after it, cancel is refused and **no second paste attempt/fallback may occur even if the OS reports an error**.
- `performFocusMutation { ... }` -> serializes a short focus change with revoke; does not claim clipboard/paste.
- `revoke()` -> sets revoked and returns `(copied, pasteClaimed)`.
- Cancel semantics: before copy: nothing happens to the clipboard. After copy: the copied text stays on the clipboard but paste never occurs. After paste claimed: cancel is ignored.

**Windows:** reproduce with a mutex-protected struct or `Interlocked` flags; claim paste immediately before calling `SendInput` and never retry after claiming. Release a half-sent Ctrl+V pair: if cancellation arrives after the key-down, still send the key-up (the Mac code treats the pair as indivisible).

### 2.3 Configuration snapshotting (`RemyDictationConfiguration.swift`)

At `beginRecording` (`AppState.swift:1908-1950`) a frozen, never-reread snapshot is built (`RemyCleanupRuntime.capture`, `RemyDictationConfiguration.swift:226-292`). Changing settings during a dictation never affects it. Frozen fields:

Capture: `engine`, `model`, `language`, `microphoneUID`, `vocabulary[]` (custom vocabulary list), `fillerRules {isEnabled, words, bareMatchCJKFillers}`, `muteSystemAudio`, `boostMicrophoneVolume`, `preserveCancelledSessions`, `autoStopAfterSpeechEnds`, `historyEnabled`, `destination` (`originalField` | `currentField`).
Cleanup: `mode`, `prompt`, `baseURL`, `model`, `reasoningEnabled`, S1-mini styling fields, `profile`, `contextPolicy { isEnabled, shareCapturedTextRemotely, writingStyles per category, s1Styles, appRules }`.
Also `settingsVersion = 1`, `instructionVersion = 1`, and an ephemeral `credentialReference` UUID (the cleanup runtime closes over the credential value captured at that moment; it never rereads a changed key).

`RecognitionSessionConfiguration` (`RecognitionSessionConfiguration.swift`) is installed on the engine before capture and cleared at teardown: `modelChoice, expectedLanguage, nativeLanguageCode, appleSpeechLanguage, acousticVocabularyEnabled, vocabularyTerms, showTextPreview, microphoneUID`. `activeVocabularyTerms` = terms only when `acousticVocabularyEnabled` (that is, cleanup mode `fluidAudioVocabulary`). Before capture, `startDictation` compares the snapshot taken at the request with a fresh one and aborts if models/language/preview settings changed during preparation (`AppState.swift:1862-1866`).

Configuration is not logged or serialized. A privacy-reduced subset goes into history metadata (`AppState.historyConfiguration`, `AppState.swift:2516-2528`: engine, model, language, settings/instruction versions, provider, cleanup model, prompt, profile snapshot).

### 2.4 The start sequence in detail (`startDictation`, `AppState.swift:1764-1906`)

In order:
1. Guards (shutdown, sleeping, installs, history preparation/deletion, cleanup draining -> busy notice). Clear `recordingStartError`; if status is `error`, set idle.
2. Require `status == idle && !isTransitioning && startingRequestID == nil`, else busy notice (see 1.3).
3. Create `requestID` (UUID); set `startingRequestID`; clear delivery lease. The request id is later the session id.
4. **Capture the destination BEFORE anything else** (before permission prompts/overlay can steal focus): frontmost application (excluded if it is Remy itself) and, via accessibility, a "target proof" of the focused window and field (section 5.3). Result may be "unavailable". In tests this is skipped.
5. Re-check state; if mode is hold and the key is no longer down, return. Refresh permissions; re-check; again hold-key check.
6. If microphone permission is missing: request it; on grant mark granted and **return without recording** ("The permission gesture must never become a recording gesture"); on denial present the blocking microphone attention issue once. (**Windows:** microphone privacy toggle check via `Windows.Media.Capture`/registry `ConsentStore\microphone` or just a failed capture start.)
7. Begin perf session metadata; if input-source auto-switch is on (and engine not AssemblyAI) apply the profile for the current keyboard input source (section 3.5).
8. Check the engine is ready. For Parakeet: if the model is still downloading set status error text "{model display name} is still downloading. Try again when it finishes." then idle; if not on disk, attempt to prepare; if still not ready set "{model} is not ready. Download it from Speech Model settings." then idle and present the blocking "speechSetup" issue. (Display name for v2 is "Parakeet v2".)
9. `beginRecording`.

`beginRecording` (`AppState.swift:1908-2112`):
1. Build the config snapshot; `sessions.begin` (refuses if a session exists). Set `sessionCleanupRuntime`.
2. Launch destination text/context capture (bounded, async; only when the feature policy allows, context awareness setting is on, and the active cleanup mode uses context). Never blocks recording.
3. Install the recognition configuration on the engine. Open the history session (`history.beginSession`) with `enabled = historyEnabled`.
4. Mark `isTransitioning = true`, clear `pendingHoldRelease`.
5. If history is enabled: create the recovery audio capture; **if it cannot be created recording does not start** (the user gets `recoveryStore.errorMessage = "Audio recovery is unavailable for this recording: {reason}"`). With history off no recovery audio is ever created.
6. Set `status = recording`, `currentTranscript = transcriptPrefix` (empty unless continuing).
7. Refresh device list; compute candidate devices (section 3.4). If boost-mic is on and a preferred device exists, set its input volume (3.2). If mute-system-audio is on, mute output (3.2).
8. For each candidate device in order: (from the 2nd on, wait 220 ms first) try `engine.startRecording(deviceID)`. First success wins.
9. If none succeeded: refresh permissions; if mic not granted present the blocking microphone issue; discard recovery capture; `status = error("Failed to start recording: {message}")` (message is the last error's localized description or "Unknown audio startup error"); set `recordingStartError = "Could not start the microphone. {message}"` (only if mic is granted and not a continuation); post "show main window"; show overlay `processing` and hide it after 2.0 s; restore mic volume and system mute; back to idle. Attention banner "Recording could not start" (see section 8).
10. On success: `sessions.markRecording`, **play the start sound**, show overlay `listening`, start cleanup pre-warm (a background warm-up of the cleanup model that overlaps with speech), start the 33 ms level poll, clear `isTransitioning`, and if `pendingHoldRelease` is set, stop immediately.

### 2.5 Stop/finish, busy rules and generation checks

- `stopDictation` (`AppState.swift:2129-2144`): requires `status == recording && !isTransitioning` and `sessions.requestStop` success. Sets `isTransitioning = true`, `status = processing`, resets the cancel gesture, runs `finishDictation` as a tracked task, and clears transitioning when done (unless cancelling).
- While `status == processing` the only user actions are: Cancel (hotkey or menu) and "Use Raw Now" (menu, while a cleanup attempt is running). New hotkey presses show the busy notice.
- Auto-stop after speech ends (`autoStopAfterSpeechEnds`) exists only for the Parakeet EOU model in toggle mode. Not applicable to v2 (default off).
- The recording-time poll also stops the session if the recovery writer reports a failure (`AppState.swift:3193-3201`): sets `recordingSafetyFailure` and `recoveryStore.errorMessage` to the failure text and calls stop on a fresh task.

### 2.6 Cancellation (`cancelDictation`, `AppState.swift:2673-2784`)

Order of effects (all synchronous up to the first `await`, so revocation wins any race):
1. Guard `canCancelDictation`. If only a start request exists (no engine yet): clear request, invalidate context capture, revoke session with the reason, hide overlay, return (no recovery file exists yet).
2. `sessions.cancel(token, reason)`; if it returns `alreadyDispatched` stop (paste already claimed: cancel refused).
3. Revoke the lease; cancel the cleanup attempt; mark `isCancelling`, invalidate context capture, cancel cleanup pre-warm, cancel processing task, clear `isHoldToRecordKeyDown`, stop level polling, hide overlay immediately (0 s).
4. Stop audio capture, record the capture-stop time for the audio-route settle (3.2), **join** the old processing and cleanup tasks (so the engine/recovery file are never reused while an old task still touches them), cancel the engine.
5. If recovery capture exists and `preserveSessionOnCancellation` (setting `preserveCancelledSessions` captured at start, default **true**, or the session is a continuation): save it as a recovery session (section 6.5). Otherwise discard the temporary audio. Clear all session state.
6. Restore mic volume and system mute (with the settle wait), `currentTranscript = ""`, status idle.
- Cancel reasons used: `user` (hotkey/menu), `inputUnavailable` (sleep), `shutdown`.
- No sound plays on cancel; the overlay is simply hidden. No text is delivered, nothing is copied.
- History: an interrupted session whose raw text was already saved is marked `interrupted` only in specific cleanup-cancel paths (`AppState.swift:2294-2300`).

### 2.7 Shutdown

`AppState.shutdown` (`AppState.swift:663-774`) revokes everything, joins capture/finalization/preparation tasks, **drains and preserves the recovery capture** (so a normal quit never loses the last audio buffer: if saving fails, `recoveryStore.errorMessage = "Recovery details could not be saved before quitting. Existing audio was kept."`), flushes history, restores mic volume/system mute and hides the overlay. A single-instance guard exists (`AppDelegate.swift:152-170`): the lowest-PID running copy of the app keeps going, others activate it and exit. **Windows:** use a named mutex and activate the first instance.

---

## 3. Audio

### 3.1 Capture format and path

- Output of capture is **16 kHz, mono, Float32 PCM** (`BatchTranscriptionPolicy.sampleRate = 16_000`).
- **MAC-ONLY details:** two capture backends exist. (a) `AVCaptureSession` with the output pre-set to 16 kHz mono Float32 (`TranscriptionEngine.swift:113-245`) is used when an `AVCaptureDevice` can be resolved for the chosen device; (b) `AVAudioEngine` with an input tap (buffer size 4096 frames at the device's native rate, converted with `PCMStreamConverter`) is the fallback (`makeRecordingEngine`, `TranscriptionEngine.swift:296-376`). `PCMStreamConverter` (`PCMStreamConverter.swift`) is one stateful sample-rate converter per stream, never reset between blocks, drained once at end (`finish()`) to flush filter tail. **Windows equivalent:** WASAPI shared-mode capture (NAudio `WasapiCapture`/`MMDevice`), request the device mix format, convert to mono and resample to 16 kHz with a single persistent resampler (e.g. `MediaFoundationResampler` / `WdlResampler`), and flush the resampler tail on stop. The tail flush must be ordered after the last in-flight buffer (the Mac code uses a lock so Stop cannot deliver tail samples before an in-flight block).
- **Capture readiness:** the controller counts as started only after the **first PCM buffer is actually delivered** (even silence), with a timeout of **1.5 s** (`AudioCaptureReadiness.wait`, `TranscriptionEngine.swift:106`). The overall capture construction is bounded at **5 s** (`timeout: 5`, `TranscriptionEngine.swift:1408`); a late-arriving controller is stopped immediately so no orphaned mic session remains. Error text: "Audio capture did not start in time. Check the selected microphone and try again." (`audioEngineSetupTimedOut`). Other errors (verbatim, `TranscriptionEngine.swift:460-481`): "Failed to set up audio capture.", "Failed to create audio format.", "Failed to select the specified microphone.", "Transcription engine is not ready.", "{model} does not support {language}. Choose another speech model or expected language in Speech Model settings."
- **Restart gap:** a 120 ms minimum interval between the last capture stop and the next capture start (`AudioCaptureRestartGate`, `AudioCaptureStartup.swift:106-140`); a late-finishing previous startup is rechecked at construction time. Capture start/stop run off the UI thread.
- After stopping a capture the engine object is kept alive for 0.35 s and at most 3 retired engines to avoid late callbacks (`TranscriptionEngine.swift:1960-2000`) - **MAC-ONLY**.

### 3.2 System audio and microphone level control (`VolumeController.swift`)

**Yes, it mutes system audio during recording, but only when the setting is on, and with conditions.**

Settings (`Settings.swift:1961-1971`):
- `muteSystemAudioDuringRecordingEnabled`: **default false** (migrated from legacy `autoVolumeEnabled` if present). Label: "mute system audio".
- `boostMicrophoneVolumeEnabled`: **default true**.

Mute behavior (`adjustForRecording`, `VolumeController.swift:82-93`):
- Mutes the **default output device** (the hardware mute control, not a volume change and not per-app). Tries the main element then each output channel element; if the device is already muted, it records "was muted" and will **not** unmute later; if no mute control is settable it does nothing.
- It is **skipped entirely** when the output device or the default input device is "headphone-like": Bluetooth transport, or the device name contains any of `airpods`, `headphone`, `headset`, `earbud`, `beats` (case-insensitive) (`shouldBypassMuteForCurrentRoute`, lines 301-320). Rationale: muting/changing a Bluetooth route can switch the headset to a low-quality call profile.
- Restore (`restoreAfterRecordingWithSettle`, lines 36-53): unmute only if this session did the muting. If a mute happened, wait until **200 ms after capture stopped** (`recordCaptureStopped()` time + 0.2 s, only the remaining part) so Bluetooth/route changes settle, then unmute. If the unmute on the original element fails, try all elements. A suspended restore is dropped if a newer recording changed the audio state id.
- Called at: stop (after delivery, with settle), cancel, failure, start failure, "no text" path, history failure. Deinit and shutdown restore synchronously.

Mic boost (`boostMicrophoneVolume`, lines 97-110): reads the selected (first candidate) input device's scalar input volume; **if below 0.8 sets it to 0.8**, remembering the previous value; if already >= 0.8 nothing changes. Restore sets the saved value back only if the default input device is still the same device. Restored on every end path.

**Windows equivalents:** output mute via Core Audio `IAudioEndpointVolume.SetMute` on the default render endpoint; mic boost via `IAudioEndpointVolume.SetMasterVolumeLevelScalar` on the capture endpoint (many mics have no controllable volume: do nothing). Keep the headphone bypass (Bluetooth: `PKEY_Device_EnumeratorName` = "BTHENUM"/"BTHLEENUM", form factor headphones/headset via `PKEY_AudioEndpoint_FormFactor`, name tokens). Persist the "I muted it" state so a crash can recover (the Mac code does not persist; a crashed Mac app leaves audio muted - **WINDOWS DECISION:** at startup, if a persisted "muted by Remy" flag is set, unmute).

### 3.3 Audio buffers and level meter

- Capture callbacks append to four places under one lock (`appendCapturedSamples`, `TranscriptionEngine.swift:1889-1908`):
  1. `sampleBuffer` - pending samples awaiting decode, hard cap **120 s** (`16000 * 120`). Overflow drops the oldest samples and increments `droppedPendingSamples`; at finalization any drop produces the error "Speech recognition could not keep up. The transcript may be incomplete; use any available recovery recording to try again."
  2. `levelSampleBuffer` - last **800 samples** (50 ms) for the meter (`AudioMonitor.windowSampleCount`, `AudioMonitor.swift:43`).
  3. `volumeGate` - rolling window analyzer (3.6).
  4. `fullRecordingSamples` - the complete recording, retained only when the model needs it (`needsOriginalAudio`: v2 always, and any TDT model if vocabulary terms exist) (`TranscriptionEngine.swift:1885-1887`).
  5. Also every block is appended to the recovery file writer (3.7).
- Audio-time wakeups: a signal is posted when total captured samples cross each multiple of **4000 samples (0.25 s)** (`batchProcessingSignalSamples = sampleRate/4`, `AudioProcessingSignalThreshold`); signals coalesce into a "newest only" stream; the consumer drains **all** queued samples on each signal, so a slow decode never queues stale work and never drops audio.
- **Level meter** (`AudioMonitor.swift`): RMS over the last 800 samples; `scaled = min(1, rms * 6.6)`, `level = scaled ^ 0.85`; smoothing `smoothed = smoothed * s + level_rms * (1 - s)` where `s = 0.08` when rising (fast attack) and `s = 0.65` when falling (slow release). The UI is updated only if the level changed by >= 0.01 (`displayLevelTolerance`) or the transcript changed. Polled every **33 ms** (~30 Hz).
- Transcript text shown in the overlay is copied from the engine only every **6th poll (~200 ms)** and only if it changed (`AppState.swift:3217-3228`). With the continuation prefix: `displayTranscript = join(prefix, engineTranscript)` where `join` trims both and joins non-empty parts with a single space.

### 3.4 Input device selection (`AppState.swift:3270-3544`)

- Setting `selectedMicrophoneUID` (nil = "Automatic": follow system default). Menu: first item is the automatic title (`automaticMicrophoneTitle`) selected when UID is nil or the saved device is no longer present; then a separator; then each available input by name.
- Enumeration filter (`enumerateInputDevices`): must be a "physical" transport (excludes aggregate, virtual, Continuity Camera mics), have input channels, be alive/connected/not suspended, and **exclude the built-in mic when the laptop lid is closed** (clamshell: `IOPMrootDomain AppleClamshellState`). **MAC-ONLY**; Windows: enumerate active capture endpoints with `MMDeviceEnumerator.EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE)`, skip "Stereo Mix"/loopback-style and virtual devices if desired; no lid logic (WINDOWS DECISION).
- A device-change listener refreshes the list live (**Windows:** `IMMNotificationClient`).
- **Candidate order** for a recording start (`MicrophoneHelper.candidates`, `AppState.swift:3464-3473`): (1) the selected device if currently usable, (2) the system default input if usable and different, (3) every other usable device with built-in devices first. The selected UID is only a preference, never proof it can record. `startRecording` is tried against each in order with a 220 ms pause between attempts.

### 3.5 Input source profiles (`InputSourceMonitor.swift`, `InputSourceProfileResolver.swift`, `AppState.swift:1516-1674`)

An **input source** is the macOS *keyboard layout/input method* (Carbon TIS, e.g. "com.apple.keylayout.US", Pinyin, ABC). An **input source profile** is a user mapping `InputSourceMapping { id, inputSourceID, inputSourceDisplayName, engine, parakeetModel?, language }` that says "when this keyboard layout is selected, use this speech engine/model/language". Feature flag `inputSourceAutoSwitchEnabled` (default **false**). Behavior:
- Observe `kTISNotifySelectedKeyboardInputSourceChanged` (distributed notification), **debounced 500 ms**; only the final selection matters (users cycle with Ctrl+Space).
- The resolver returns `none | noChange | languageOnly(lang) | fullApply | inactive`. Parakeet mappings are inactive if the model is not downloaded; auto-switching never downloads models. Apple Speech mappings need the language installed.
- Applied only while status is idle; applied at startup, on change, and again as a backstop at the start of each dictation (serialized through a queue so overlapping applies run in order). `fullApply` shows overlay state "Loading {name}…" while swapping models.
- Language derivation: `deriveLanguage(fromBCP47:)` takes the primary subtag ("zh-Hans" -> `zh`; "nb" -> "no").
- **Windows relevance:** with a single English v2 model this feature has nothing to switch. **WINDOWS DECISION:** omit for v1. If a multilingual model is added later, the Windows equivalent is the active input language (`GetKeyboardLayout`, `WM_INPUTLANGCHANGE` via a hidden window or TSF `ITfInputProcessorProfileMgr`).

### 3.6 Silence / speech detection

Two independent layers; both are only for **deciding whether there is speech** (they never trim audio sent to the final decode).

**Volume gate** (`AudioVolumeGate.swift`) - the primary test:
- A window is 8000 samples (**0.5 s**), hop 4000 samples (0.25 s) over the entire recording.
- `qualifies(window)`: true if RMS > **0.005**; else, if peak amplitude >= **0.02** and the fraction of samples with |x| >= 0.02 is >= **0.015** (1.5%).
- The recording "contains significant audio" if any 0.5 s window (stepping 0.25 s) qualifies, plus a check of the final unaligned tail (only if the last full window did not end exactly at the end).
- For previews only the **most recent 8000 samples** are tested (`hasSignificantAudio(recent)`).

**Silero VAD** (optional extra model; `BatchSpeechDetection.swift`): a CoreML VAD model installed by explicit setup. If installed, its streaming "triggered" state marks speech; a preview is also allowed when the volume gate fails but VAD saw speech within the last **0.5 s** (`hasRecentSpeech`, `BatchSpeechPresence`), and at Stop, if the volume gate failed, VAD over the whole recording (including flushed tail) can still declare speech. If VAD is not installed, only the volume gate applies. **WINDOWS:** sherpa-onnx ships Silero VAD as ONNX (`silero_vad.onnx`); optional. Recommended to include it because the volume gate rejects very quiet speakers.

**Silence outcome:** if neither layer finds speech at Stop, the final result is the empty string (no decode runs). See the "no text" path in 5.2.

**Max duration:** there is **no maximum recording duration** for the local engine. Limits that exist: pending-decode backlog 120 s (3.3); recovery writer queue 30 s of backlog (3.7); AssemblyAI had a 120 s cap (`AssemblyAIEngine.swift:82`, irrelevant). Full-session memory grows with duration (v2 keeps the whole recording in RAM; about 3.84 MB per minute as Float32 mono 16 kHz). The recovery audit lists "total recovery quota / long-session memory bound" as still open. The Windows spike measured 7.9 GB peak at 5 minutes whole-file decode; **Windows must chunk the final decode** (section 4.4).

### 3.7 Recovery audio file writer (`DictationRecovery.swift:1-60`)

- One file per session: `<recovery dir>/<session UUID>.caf` (16 kHz mono; Mac format CAF/Float32 via `AVAudioFile`). Created with 0600 permissions; directory 0700 and excluded from backup. **Windows:** a WAV (16 kHz mono 16-bit or float32) or raw PCM file in `%LOCALAPPDATA%\Remy\Recovery\<uuid>.wav`, user-only ACL, excluded from OneDrive/backup.
- Audio callbacks only enqueue; a serial writer thread does file I/O. Admission is bounded: at most **30 seconds** (480 000 samples) of queued-not-yet-written audio; overflow or write error sets a sticky failure message and stops admitting audio: "Audio recovery could not keep up. The saved recording may be incomplete." / "Audio recovery could not be written. The saved recording may be incomplete." The recording poller sees the failure and stops the dictation (2.5). Closing drains the queue first.
- At Stop the file is closed (`capture.finish()`) **before** deciding the session succeeded; a writer error at that point is treated as a failure (section 6.4).

---

## 4. Transcription

### 4.1 Engine contract (`TranscriptionEngine` protocol, `TranscriptionEngine.swift:253-292`)

`prepare()`, `startRecording(deviceID)`, `stopAudioCapture()`, `stopRecording() -> final text`, `cancel()`, `transcribeRecording(at:)` (for recovery), `currentTranscript` (live preview), `lastTranscriptionError`, `levelSamples(count)`, `isReady`, `recoveryCapture`, plus session setters for configuration/vocabulary/context. Implement the same shape in C#.

Engine state rules: only one recording at a time; `isTranscribing` flags the decode loop; `stopRecording()` stops capture first (so a long decode cannot keep the mic open), then the loop, then runs the final decode, then resets and publishes `currentTranscript = final`.

### 4.2 Live preview for Parakeet v2 (batch path)

This is the path used by the shipping default English model (`BatchTranscriptionSession` backend `.parakeetBatch`, because v2 or "requires whole-recording final" selects it; `BatchTranscriptionSession.swift:48-58`).

Cadence constants (`BatchTranscriptionPolicy.swift:6-14`, all in **audio time**, 16 kHz):

| Constant | Value | Meaning |
| --- | --- | --- |
| `batchProcessingSignalSamples` | 4 000 (0.25 s) | Wakeup granularity of the decode loop |
| `firstPreviewSamples` | 8 000 (0.5 s) | Preview interval while no preview text exists yet |
| `previewDeltaSamples` | 16 000 (1 s) | Preview interval once text exists and total audio < 8 s |
| `sustainedPreviewStartSamples` | 128 000 (8 s) | After this much total audio... |
| `sustainedPreviewDeltaSamples` | 32 000 (2 s) | ...preview every 2 s instead (non-streaming, non-SenseVoice models) |
| `previewWindowSamples` | 240 000 (15 s) | Rolling retained window for preview decodes |

Algorithm per wakeup (`processBatchAudio`, `TranscriptionEngine.swift:1635-1662`):
1. Drain all pending samples; append them to the session's rolling buffer, which keeps only the **last 15 s** (older samples are dropped from the preview buffer; the full recording is separately kept for the final).
2. `shouldPreview`: previews must be enabled (`showTextPreview` setting, **default true**) and `total - lastPreviewTotal >= interval` using the table above (0.5 s if no visible text yet; 2 s if total >= 8 s; otherwise 1 s).
3. If due: test the most recent 0.5 s with the volume gate; if it fails, require VAD "recent speech" (<= 0.5 s ago); otherwise skip (do not update `lastPreview`).
4. Decode the **entire rolling window (up to 15 s)** with a **fresh decoder state** (no carried context). Set `lastPreviewTotal = total`. Trim the text; if non-empty set `currentTranscript` (empty results do **not** clear the existing preview). The first non-empty result emits a "first partial" trace event.
5. Merge into the displayed timeline (`BatchPreviewTimeline`): convert token timings to word timings offset by the window's start sample; drop previously stored words whose end time is after the new window's first word start; append the new words. The preview text is all stored words joined by spaces. Net effect: text before the 15 s window stays frozen from earlier decodes; text inside the window is replaced by the newest decode. If a decode returned no word timings, the raw result text is used as a fallback.
6. A batch error does not abort recording; it sets a latch (`captureProcessingFailed`) so the **final result is reported as failed** (see 4.5). Cancellation errors while stopping are ignored.

Overlay display: only the tail of the transcript that fits is shown with a leading "…" (`OverlayContent.swift:~180`); placeholder text "Listening…" when empty; label "LISTENING".

**Windows adaptation:** sherpa-onnx `OfflineRecognizer` with the same 15 s rolling window decode and the same cadence is a faithful port. Decode cost measured in the spike (about 0.5-0.6 s per 10-13 s on 8 CPU threads) is well under the 1-2 s cadence, so serialize previews and skip (never queue) when one is still running. Preview must never block capture.

### 4.3 Models and language handling

- v2 (`englishOnly`): language list `[english]`; `selectableLanguages = nil`; no language picker; `scriptLanguage` is nil so the library receives no language hint. Starting a recording with a language the model does not support throws "{model} does not support {language}…" (`startRecording` guard, `TranscriptionEngine.swift:1306-1310`).
- v3/Ultra (multilingual, 25 European languages): the selected language is passed as a **script constraint only** (not a vocabulary or detection override), except Bulgarian, Ukrainian, Russian, Greek, which use automatic decoding because the SDK's Cyrillic/Greek filters reject Latin names (`BatchTranscriptionPolicy.scriptLanguage`, lines 18-27). Default selected language `en` (raw `"en"`, `SupportedLanguage.swift`; raw values are ISO codes; Norwegian is `"no"`, Chinese `"zh"`).
- **CJK handling** (`CJKText.swift`): used when concatenating chunk transcripts and for filler removal. `isCJK` ranges: 0x4E00-0x9FFF, 0x3400-0x4DBF, 0xF900-0xFAFF, 0x20000-0x2EBEF. Chunk joining (`joinChunkTranscripts`/`chunkSeamSeparator`, `TranscriptionEngine.swift:1929-1955`): trim both sides; empty side returns the other; no space if left ends with a space, right starts with `,` or `.`, right starts with CJK terminal/closing punctuation (`。！？，、；：` and closers `」』）】〉》` and `…`), or left ends with CJK and right starts with CJK; a CJK opening bracket on the right gets no space if left ends with CJK else a space; otherwise a single space. `endsWithCJK` skips trailing terminal punctuation, brackets and ASCII closers (`" ' ) ] }` and curly quotes). Repeated words across chunks are **kept** (no string-overlap dedupe). Needed on Windows if v2 chunk results are joined (English: single-space join, but keep the rules for `.`/`,` starts).
- For v2 English the CJK code is irrelevant except the filler default list contains `嗯`, `呃` (section 5.1).

### 4.4 Final pass (v2)

`stopRecording` -> `performFinalTranscription` (`TranscriptionEngine.swift:1759-1792`):
1. Capture is stopped first; converter tail flushed; decode loop cancelled and joined.
2. Drain remaining pending samples into the session; `recorded = fullRecordingSamples`.
3. Speech check: `volumeGate.containsSignificantAudio`; if false, ask VAD (if installed) over the whole recording; if still false -> cancel session and return `""`.
4. If vocabulary terms exist and the model supports acoustic vocabulary (not v2): custom-vocabulary rescoring path (4.6). Otherwise `finishBatch(recordedSamples: recorded)`: **one decode of the entire recording** with a fresh decoder state through FluidAudio's `AsrManager.transcribe`, which internally chunks long audio (the app configures `ASRConfig(streamingEnabled: true, streamingThreshold: 10 s)`; exact internal chunk size/overlap is inside the FluidAudio library and not in this repo). The Mac comments say the batch path "preserves v2 mel context and repairs gaps"; the live overlap path "drops clear speech in shifted-seam goldens", which is why v2 uses a whole-recording final.
5. Result handling: `finalTranscript(final, fallback: livePreview)` = trimmed final text; if that is empty use the trimmed live preview.
6. Incomplete-capture checks run before and after the decode: if any pending samples were dropped, or a live decode step failed, `lastTranscriptionError` is set and the **live preview text is returned as recovery material but treated as a failure** (see 5.1 failure handling). Messages (verbatim): "Speech recognition missed a processing step. The transcript may be incomplete; use any available recovery recording to try again." ; "Speech recognition could not keep up. The transcript may be incomplete; use any available recovery recording to try again." ; on a final decode exception: "Speech recognition did not finish. Your available preview is incomplete; use any available recovery recording to try again."
7. After a stop the engine publishes `currentTranscript = final`, clears all buffers.

**WINDOWS DECISION (chunking):** the spike (`RESULTS.md`) shows whole-file decode RTF doubles past 1 minute and peaks ~8 GB at 5 minutes, while chunking at <= 20 s keeps RTF about 0.05 and RAM ~1.3 GB. The Windows port should decode finalized chunks in the background during recording (split at silence, <= 20 s) and join results with the single-space rule in 4.3, so stop-to-text is roughly the last chunk's decode time. This intentionally differs from the Mac's single whole-recording decode; result text will not be byte-identical on long recordings. For recordings <= ~20 s use a single decode to match Mac exactly.

### 4.5 Recognition errors to user

`engine.lastTranscriptionError` or a recovery-writer failure causes `handleTranscriptionFailure` (`AppState.swift:2624-2670`): the recovery audio (and preview/completed text) is **preserved** as a recovery entry; if audio was saved the shown message gets " The recording is available in History." appended; `recoveryStore.errorMessage` is set (shown as the "Dictation recovery needs attention" banner, section 8); overlay hidden; nothing is pasted or copied; session fails with stage `recognition`, retryable. If saving recovery fails: "{message} The recovery copy could not be saved: {reason}".

### 4.6 Vocabulary boosting: what the Mac actually does and whether sherpa-onnx can replicate it

**Critical finding: acoustic vocabulary boosting does not run for Parakeet v2 on Mac.**
- `supportsFluidAudioVocabulary` is true only for `compactEnglish` (Parakeet TDT-CTC 110M) and `nemotronMultilingual` (`Settings.swift:564-570`). `PreparedTDTVocabulary.prepare` throws for any other TDT model: "Acoustic vocabulary for this model is unavailable in Remy with FluidAudio 0.17.5. Use Compact English for acoustic vocabulary, or choose another cleanup mode and keep this speech model." (`PreparedTDTVocabulary.swift:12-18`).
- Acoustic boosting is activated only by cleanup mode `fluidAudioVocabulary` ("Vocabulary correction only"); `RecognitionSessionConfiguration.activeVocabularyTerms` is empty otherwise. When the model does not support it and the mode is selected, the mode is coerced to `none` on load (`Settings.swift:2000-2004`) and an attention banner appears: "Vocabulary correction needs Parakeet v2, v3, 110M, Ultra, or Nemotron 3.5. Choose a compatible model on the Speech Model page." (`AttentionIssue.swift`; note this text claims v2 works, which contradicts the code; treat it as a Mac copy bug).
- For v2 the user's **custom vocabulary list is therefore used only by LLM cleanup prompts** (modes Apple Intelligence, Ollama, OpenRouter, OpenAI-compatible, saved profile; the term list is passed as vocabulary hints) and by the known-term capitalization guards in the dormant insertion formatter. It does **not** change ASR output.
- Vocabulary input parsing (`VocabularyInput.swift:10-26`, used by the quick panel and settings): split on `,` `，` `、` and newlines; trim; drop empty and duplicates (case-sensitive exact compare against existing + already parsed). Stored as ordered `[String]` under key `customVocabulary`. Quick panel texts (verbatim): title "Custom Vocabulary"; help "Add names and phrases for supported transcription and cleanup methods."; placeholder "Add word or phrase…"; button "Add"; empty state "No words added yet"; count "{n} word(s)" (singular for 1).

**The 110M algorithm** (for reference; parity is only needed if Windows adds a model with an auxiliary CTC head):
1. Prepare (before recording, never at Stop): normalize terms (trim, drop empties, sort unique); tokenize each term with the CTC tokenizer (`tokenizer.json` of the 110M CTC model); build a custom-vocabulary context of `(text, ctcTokenIds)` and a constrained "rescorer" configured by `VocabularyRescoringPolicy.config`: `shortTermCbwTaperPivot: 5`, `shortTermCbwTaperExponent: 2` (the context-biasing weight bonus is scaled down for terms with fewer than 5 CTC tokens so short keywords cannot win on the flat boost alone), `spotterRescueEnabled: false` (the "acoustic rescue" that could insert words absent from the TDT result is disabled; it dropped correct conjunctions and replaced "Craig" with an acronym), spotter rescue min similarity 0.30 / multi-word 0.50 (unused while disabled). Thresholds `cbw` and `minSimilarity` come from the SDK's vocab-size table (`ContextBiasingConstants.rescorerConfig(forVocabSize:)`).
2. At final: run the normal TDT decode on the whole recording first (gives text plus per-token timings). Then compute frame-level CTC log-probabilities for the whole recording from the model's fused encoder plus a CTC auxiliary head ("shared weights" with the TDT model; windows of **240 000 samples (15 s)** with **32 000 samples (2 s)** overlap, stride aligned to the 80 ms encoder frame grid; overlapping frames merged by log-mean-exp (average of probabilities); log-softmax with blank id 1024, 1025 logits).
3. `ctcTokenRescore(transcript, tokenTimings, logProbs, frameDuration, cbw, marginSeconds: 0.5, minSimilarity)`: for each hypothesis word span (with a 0.5 s margin) compare against vocabulary terms by CTC-forced-alignment score and string similarity; replace the span with the vocabulary term when the biased score beats the original by the weighted margin and similarity >= `minSimilarity`. If nothing changed, the original TDT text is returned.
4. Any failure or missing preparation returns the original text unchanged (never blocks delivery); cancellation propagates.
**Can sherpa-onnx replicate it?** Not directly. sherpa-onnx offers (a) transducer **hotwords / contextual biasing** (Aho-Corasick biasing in `modified_beam_search`, with a hotwords file and `hotwords_score`) for transducer models; whether it works with the NeMo TDT (`nemo_transducer`) decoding path in sherpa-onnx 2.0.1 must be tested, since the spike only used greedy search; (b) keyword spotting models (not applicable). The post-hoc CTC rescoring needs a CTC head that the v2 0.6B ONNX export does not have (the Mac only does this on the 110M TDT-CTC hybrid). Since the Mac does not boost v2 either, **parity requires nothing for v2**: just store the vocabulary list and pass it to cleanup. If acoustic boosting is wanted on Windows, treat it as a new feature: test sherpa-onnx hotwords with the TDT model, or use the 110M hybrid via a CTC model in sherpa-onnx.

### 4.7 Recovery/offline file decode

`transcribeRecording(at url)` for v2: `AsrManager.transcribe(url)` on the whole file with a persistent disk-backed decoder (comment: "independently transcribing file chunks can split and lose words"); readers pull bounded chunks (default 160 000 samples = 10 s; the engine uses 20 s blocks) and a short positive read is not EOF. Windows: stream the WAV in blocks into the same chunked decoder as the live path.

---

## 5. Delivery

### 5.1 Post-recognition text rules (before cleanup and delivery)

Order (`AppState.swift:2191-2217`):
1. New text = engine final result; `transcript = join(prefix, newText)` (continuations only).
2. **Filler removal** (not for AssemblyAI): applies `fillerRules.removingFillers` to the whole transcript, then `trimmingCharacters(whitespace)`. Rules (`RemyFillerRules.removingFillers`, `RemyDictationConfiguration.swift:127-158`):
   - Enabled flag `isFillerWordRemovalEnabled` default **false**; word list default `["um","uh","erm","er","hmm","嗯","呃"]` (`Settings.swift:1305`; 唔 is deliberately excluded because it is the Cantonese negator). User list stored under `fillerWordsToRemove`.
   - Latin words: whole-word (`\b word \b`), case-insensitive regex with the term escaped. CJK words: `(?:word)+`; if the word is in `bareMatchCJKFillers = {嗯, 呃}` it matches anywhere inside text, otherwise only when bounded by whitespace/punctuation/string edges (`(?<![^\s\p{P}])...(?![^\s\p{P}])`).
   - After removal: collapse double spaces repeatedly; remove a space before `, . ! ? ， 。 ！ ？`; collapse `，，` to `，`; trim spaces.
3. If the result is empty -> "no text" path (5.2).
4. `currentTranscript = lastTranscript = finalText`; "Copy Last Transcript" menu text updated.
5. History raw record (**uses the pre-filler `newText` as the raw chunk**, plus continuation prefix), must be acknowledged saved or history-off, else the session stops (6.3).
6. Cleanup (separate spec): mode `none` or `fluidAudioVocabulary` -> text unchanged. Other modes run under an **8-second whole-cleanup deadline** (`cleanupTimeout = .seconds(8)`, `AppState.swift:361`, `RemyCleanupAttempt.swift:~45`). Outcomes: cleaned text; or fallback to the filler-corrected raw text with a failure category (`timeout`, `useRaw` = user pressed "Use Raw Now", or a provider failure category); or cancelled. Failure categories and messages are in `RemyCleanupFailure.swift`; overlay titles: timeout "Cleanup timed out"; useRaw "Raw transcript selected"; cancelled "Cleanup cancelled"; modelUnavailable/unsupportedLanguage "Cleanup unavailable"; inputTooLong/requestBudgetExceeded "Cleanup input too long"; everything else "Cleanup failed" (default when unknown: "AI cleanup failed"). A successful cleaned text passes through `normalizePostProcessedTranscript` (em dash replaced with ", ", or a fullwidth comma next to Han; tidy `" ,"`, `".,"`, `"!,"`, `"?,"`; `AIPostProcessingService.swift:87-110`).
7. Selected text saved to history; delivery intent saved; then the permit is authorized.

### 5.2 Empty / silent result ("no text" path, `AppState.swift:2219-2244`)

Triggered when the final text is empty after filler removal (silence, or recognition returned nothing). Behavior: `currentTranscript = ""`; restore mic volume and system mute (with route settle); mark ready, discard the recovery audio as completed, complete the session; overlay shows `success` then hides after **0.5 s**; status idle. **No start/completion sound variant, no clipboard change, no paste, no history entry, no cleanup call.** (The start sound already played at record start.) Clipboard is untouched.

### 5.3 Destination proof (where dictation started) - `RemyInsertionTarget.swift`

**MAC-ONLY mechanism:** Accessibility (AX) element identity (window + focused element) plus process launch date. **Windows equivalent:** at hotkey-down capture `GetForegroundWindow()` (HWND), its process id and creation time (to defend against PID reuse), the focused control via `GetGUIThreadInfo` (`hwndFocus`/`hwndCaret`) and optionally the UI Automation focused element (`AutomationElement.FocusedElement`, `ControlType`, `IsPassword`, `IsReadOnly`/ValuePattern.IsReadOnly, `IsEnabled`, `IsKeyboardFocusable`).

Rules on the Mac:
- Captured **at the very start** of `startDictation`, only if the frontmost app is not Remy itself; bounded by a **200 ms** deadline, each accessibility call limited to **25 ms** (`AXUIElementSetMessagingTimeout(app, 0.025)`).
- Succeeds only if: the app is active and not terminated and its launch date is stable; there is a focused window and a focused element; both belong to that PID; the focused element is **editable** (below); final identity re-check matches. Otherwise the proof is `unavailable` and **paste will never be automatic for that session (copy-only)**.
- Editable test (`supports`, lines 214-222): role must be one of `AXTextField`, `AXTextArea`, `AXComboBox`; unsafe -> reject: role or subrole `AXSecureTextField` (**password field**), `AXReadOnly == true`, `AXProtectedContent == true`, `AXEnabled == false`; `AXEditable == false` rejects; for non-terminals require `AXEditable == true` or AXValue settable; **terminals** (below) accept `AXTextField`/`AXTextArea` without requiring editability flags. Missing optional attributes (unsupported/noValue) are accepted; an IPC error or malformed value rejects. Every ancestor from the field up to the window (max depth **128**, cycle-safe, within the time budget) must also be non-unsafe (no secure/read-only/protected/disabled container).
- **Terminal apps** by bundle id: `com.mitchellh.ghostty`, `com.apple.Terminal`, `com.googlecode.iterm2` (only these three, `RemyInsertionTarget.swift:233-235`). For a terminal target, **if the payload contains any newline the paste is blocked (copy-only)**, reason "terminalNewline" (line 33). Single-line text is pasted.
- At paste time `matches(pid)` re-verifies: same requested PID, same app not terminated and **active (frontmost)**, same launch date, **same focused window and same focused element** (CFEqual), still editable, final identity ok (200 ms budget). Any mismatch -> copy-only.
- Secure fields: never pasted (above). The separate *context* capture (for cleanup prompts) also refuses text capture for secure fields and for "sensitive applications" (password managers: bundle ids `com.1password.*`, `com.agilebits.onepassword7`, `com.apple.keychainaccess`, `com.apple.passwords`, `com.bitwarden.desktop`, `com.callpod.keepermac`, `com.dashlane.dashlane`, `com.lastpass.*`, `com.nordsec.nordpass`, `in.sinew.enpass-desktop`, `me.proton.pass`, `org.keepassxc.keepassxc`, plus any bundle id containing 1password, bitwarden, dashlane, enpass, keepass, keeper, lastpass, nordpass, password, proton.pass; `DictationContext.swift:~455-482`).
- **WINDOWS DECISION (terminals):** the spike found Windows Terminal exposes `TermControl`, which has no ValuePattern; identify terminals by process name (`WindowsTerminal.exe`, `wt.exe`, `cmd.exe`/`conhost.exe`, `powershell.exe`, `pwsh.exe`, `mintty.exe`, `alacritty.exe`, `wezterm-gui.exe`, `ghostty`, `kitty`, VS Code integrated terminal can't be told apart from the editor by process) and apply the same multiline -> copy-only rule. Docs say multiline in terminals should be copy-only by default with a per-app opt-in.
- **WINDOWS DECISION (elevated targets):** per the spike, SendInput to an elevated (higher integrity) window is dropped silently while returning success. Compare the target process's token elevation with Remy's; if the target is higher, treat as copy-only with a message. Also treat UAC/secure desktop and lock screen as copy-only.
- **WINDOWS DECISION (password fields):** UIA `IsPasswordProperty` is the analog of `AXSecureTextField`; Chromium/Electron expose it for `<input type=password>`. Block paste when focused element reports IsPassword. If the focused element cannot be classified at all (many Win32/Electron apps), the Mac's strict "unknown -> copy-only" would break most apps; pick a relaxed rule (for example: allow paste when UIA says nothing is wrong and the foreground window is unchanged) and document it.

### 5.4 Destination policy: "where dictation started" vs "where focused when it finishes" (`RemyDictationDestination.swift`)

Setting `dictationDestination`, raw values `originalField` (**default**) and `currentField`; frozen per session; a change applies to the next dictation. Display names (verbatim): `originalField` -> "Where dictation started"; `currentField` -> "Where I’m focused when it finishes" (curly apostrophe U+2019). Captions: original: "Returns to the app and field where dictation started. If that field cannot be restored, copy the result and paste it manually."; current: "Chooses your focused app and field after cleanup finishes. If that field is unavailable, copy the result and paste it manually." Unknown stored value decodes to `originalField`.

`RemyDestinationRouter.resolve` (executed after the delivery permit is issued, `AppState.swift:2392-2420`):
- If cancelled/revoked/paste claimed -> unavailable (no paste).
- `originalField`: if the original proof allows paste (the target app still exists, not terminated), **restore focus** (below), then paste only if the restore succeeded; the destination stays the originally captured process and proof. If the original app is gone: copy-only.
- `currentField`: capture the *current* frontmost app (excluding Remy), build a **fresh** proof, use that. Never substitute the original proof.

Focus restoration (`Proof.restore`, lines 63-120): total budget **800 ms**. If the exact window+field is already focused, no action. Else verify the original window/field still exist and are editable; activate the app if inactive (then wait up to **120 ms**, polling every 8 ms); raise the original window (AXRaise) and set it as the focused window; set the original field focused; re-verify everything; every focus mutation is serialized with the lease so cancel cannot be overtaken. **Windows equivalent:** `SetForegroundWindow` (subject to foreground-lock rules; the hotkey hook thread usually has the right; use `AllowSetForegroundWindow`/`AttachThreadInput` as needed), then `SetFocus` on the saved control via `AttachThreadInput`, verify `GetForegroundWindow()` and `GetGUIThreadInfo` afterwards, same time budget.

During recovery "Continue" (6.5) the saved app is explicitly re-activated (up to 120 ms wait) and the focus target captured after activation.

### 5.5 Text insertion algorithm (`TextInserter.insertText`, `TextInserter.swift:692-901`)

Foreground call parameters (`AppState.swift:2434-2447`): `text = processedText`, `verbatimPayload: true`, `pasteAutomatically = destination.allowsPaste`, `targetProcessIdentifier = destination.pid`, `targetSnapshot = destination.target`, `authorization = permit lease`, `knownTerms = vocabulary`, `modelInsertionPlan = nil` for Parakeet. `context`/`style` are passed only when destination policy is `originalField` and context awareness captured something, but with `verbatimPayload` they are ignored.

**Exact behavior of the production path:**
1. Empty text -> `failed`.
2. Resolve the target app (explicit pid; it must never fall back to a different frontmost app).
3. `insertionText = text` **exactly as is** (no trimming, no leading/trailing space, no capitalization change, no list rewriting). `resetPendingSeparator()` is called. So whatever cleanup/filler logic produced is what gets pasted.
4. `targetAllowsPaste = targetSnapshot.permitsPaste(pid, payload)` (terminal newline rule + proof match) evaluated on a worker before clipboard work.
5. **Copy to clipboard first, always** (`copyToClipboard`, lines 1355-1401): up to 3 attempts; each: stop if the lease is revoked; claim clipboard once; write plain text (`NSPasteboard.clearContents` + `setString(.string)`); read back and compare to record an "ownership receipt" = (pasteboard change counter, exact UTF-8 payload). If the system pasteboard was written but the read-back/stability check fails, assume another app wrote and **do not retry** (return failure). Between failed attempts wait 25 ms. A failed copy -> result `failed`.
6. **The prior clipboard contents are not saved and not restored.** The dictated text stays on the clipboard after delivery. (Only the dormant list-edit path restores `insertionText` onto the clipboard.) **WINDOWS DECISION:** the spike harness restored clipboard text afterward, but the Mac does not; the Mac relies on the text remaining on the clipboard as the manual fallback (overlay "Press ⌘V"). If Windows restores the old clipboard, "copy-only" outcomes must still leave the text there, and the restore must be skipped if the clipboard sequence number changed. Recommend matching the Mac (leave it) for parity and documenting it.
7. Gate for automatic paste, in order; failing any returns `copiedOnly` (text remains on clipboard) and resets state:
   - `pasteAutomatically` is false (destination router said no),
   - `targetAllowsPaste` false (proof mismatch/secure/terminal newline/unavailable),
   - target app not frontmost after the copy (`targetIsReady`),
   - Accessibility permission not granted (checked with prompt) - **MAC-ONLY**; on Windows replace with "target not elevated / not secure desktop",
   - clipboard ownership no longer matches (another process changed the clipboard between copy and paste: **never overwrite and never paste someone else's content**).
8. Dispatch (`deliverToTarget`, lines 1455-1473): requires lease eligible and target ready. Primary: **MAC-ONLY** click the app's menu item Edit->Paste via Accessibility / System Events AppleScript (matches a menu item whose cmd char is "V" with no extra modifiers and enabled), after verifying focused element and selection are unchanged. If that reports pre-dispatch failure and there is no pending list edit, a second script path may run; if the result is "target unavailable" or the paste claim was already made, stop (copy-only / possibly dispatched). Only when pre-dispatch failure occurred and no paste claim was recorded, fall back to a **synthetic Cmd+V** (CGEvent key 9 with Command flag, posted directly to the target process, balanced key-down/key-up, lease claimed immediately before). After a keyboard-event success wait **100 ms**. **Windows equivalent:** `SendInput` Ctrl down, V down, V up, Ctrl up (use scan codes and `KEYEVENTF_SCANCODE`/virtual-key `VK_V` per spike: virtual key is layout-independent for the V chord), after verifying `GetForegroundWindow()` equals the expected HWND; claim the lease right before; if modifier keys physically held by the user (for example still holding the hotkey modifiers) could corrupt the chord, release/neutralize them first or wait for them to release (**WINDOWS DECISION**; the Mac posts to the pid, so physical modifiers do not leak in; with SendInput they can, notably hold-to-record with Ctrl/Alt held at the moment of key-up). The spike measured about 45 ms from dispatch to inserted.
9. Result mapping: paste outcome success or possiblyDispatched-then-confirmed -> `success`; else `copiedOnly`. (`possiblyDispatched` from the script path is final: never retried, to avoid double insertion; the code returns copiedOnly only for definitely-not-dispatched cases.)

Delivery result types: `TextInsertionResult { success, copiedOnly, failed }`.

History outcome mapping after the call (`AppState.swift:2455-2464`): `success` -> `dispatched`; `copiedOnly` -> `uncertain` if the paste claim was made else `copied`; `failed` -> `uncertain` if claimed else `failed`. A database failure after an OS effect must never trigger a retry.

User-facing result feedback (`AppState.swift:2475-2507`), after restoring mic volume/system mute:
- `success`: play completion sound; overlay `success` (checkmark; accessibility label "Dictation inserted").
- `copiedOnly`: play completion sound; overlay `copiedOnly` showing a clipboard icon and the text "Press ⌘V" (**Windows: "Press Ctrl+V"**); if the cause was missing Accessibility permission (`!permissions.accessibilityGranted`) also present the blocking Accessibility issue.
- `failed` (clipboard copy itself failed): overlay `copiedOnly` is shown as well (no completion sound). Quirk: the message "Press ⌘V" is misleading in that case; Windows should show a distinct "Could not copy" message.
- If cleanup fell back to raw text, the overlay is replaced by the cleanup-fallback pill: warning icon, title = overlay title for the category (5.1), detail by delivery: inserted -> "Raw transcript used"; copied -> "Raw transcript · Press ⌘V"; failed -> "Raw text in Remy"; accessibility hint: failed -> "Delivery failed. Open Remy to copy the transcript.", else "See History for processing details."
- Overlay hide delays: **1.0 s** after delivery (success/copy-only); **5.0 s** when a cleanup fallback is shown; 0.5 s for "no text"; 0 s on cancel/failure; 2.0 s after a failed microphone start.
- Raw `Copy Last Transcript` menu item copies `lastTranscriptForMenuBar` to the clipboard (replace contents; no trimming).

### 5.6 Dormant formatting code (do NOT port; documented for completeness)

All of this is skipped when `verbatimPayload == true` (always, in production). It remains reachable only by the unit tests and potential future callers. Rules, in case a future mode enables them (`TextInserter.swift:905-1300`):
- `contextualizedForInsertion(text, context, style, knownTerms, preserveModelFormatting)`: trims; if the cursor is at a list item start (list detection from literal markers `- + * • ◦ ▪ ‣ ⁃ ● ○ ∙` or `1.`/`1)` numbering via regex `^(\h*)(?:([•◦▪‣⁃●○∙])\h*|([-+*]|[0-9]{1,4}[.)])(?:\h+|$))(.*)$` and neighbors' capitalization/final-period conventions) it splits model output into items and re-applies the list convention (capitalize/lowercase first word, remove or add final period, literal marker continuation with incrementing numbers); in search-query fields it removes a light final period and lowercases the first ordinary word; when continuing a sentence (text after the cursor, last left char not a sentence ender) it strips trailing sentence punctuation, lowercases the first ordinary word (not "I", not known terms, not words with inner capitals) and may add a parallel-list comma; inline-comma boundaries convert newline-separated items into comma-joined text; casual/very casual styles drop the final period of a short single message (<= 20 words, no inner sentence punctuation) when nothing follows the cursor; a single-space separator is inserted between left context and the text unless either side is whitespace/newline, the text starts with attached punctuation (`! ) , . : ; ? ] } …` and CJK terminal punctuation), the left char is an opener `([{"'“`, or CJK is adjacent.
- `pendingSeparator` (a single space inserted before the next dictation into the same app if the previous character is not whitespace) - only for non-verbatim.
- `PlainTextListEdit` (renumbering following ordinals in plain-text lists by replacing a bounded range, <= 100 000 chars, with AX selection edit) and rich-list capture via accessibility text markers (`DictationRichListCapture`, `DictationTextMarkerCapture`; 600-char bounds, 200 ms budgets) - **MAC-ONLY**, no Windows equivalent needed.
- `ModelInsertionPlan` (JSON `{text|items, space_before, space_after}` from the AssemblyAI polish step) - AssemblyAI only; unused.
- `DictationFocusResolver` (breadth-first search <= 128 nodes, depth <= 12, 0.2 s budget, for the focused text input when the app's focus attribute is empty) is used only for *context capture* (text around the cursor for cleanup prompts), not for paste.
- `DictationListInsertion.instructions` strings are cleanup-prompt text, owned by the cleanup spec.

Because the active path inserts verbatim: a Windows port needs **no** surrounding-text reading for paste. It only needs context capture if the cleanup spec wants destination category/surrounding text for prompts.

### 5.7 Continuation text

For a recovery "Continue" session the prefix (previously recognized text) is joined to new text with `CancelledDictation.joining` (trim both, join non-empty with one space) before filler removal and cleanup; raw history provenance keeps the new chunk and the prefix separate (6.2).

---

## 6. History and recovery

### 6.1 Storage layout and paths (`RemyStoragePaths.swift`)

Root: `~/Library/Application Support/<bundle id>/` with release bundle id `io.github.brysonjl.remy` (dev `io.github.brysonjl.remy.dev`; fallback id used if the bundle id is empty, `.`, `..`, or contains `/` `\`). Subfolders: `Models/`, `Recovery/`, `History/` (`history.sqlite`, schema backups `history-before-v2-<UUID>.sqlite`, `Legacy Backups/legacy-history-<sha256>.json`), `CleanupProfiles/`, `Sounds/` (custom sound files). **Windows:** `%LOCALAPPDATA%\Remy\` (or `%APPDATA%`) with the same subfolders. **MAC-ONLY:** descriptor-relative `openat`/`O_NOFOLLOW` hardening and 0700/0600 modes; **Windows equivalent:** create directories under the user profile with an ACL granting only the current user, refuse reparse points/junctions/symlinks when opening/deleting, and keep the database file non-shared.

### 6.2 Schema (SQLite, `History/RemyHistoryRepository.swift`)

`PRAGMA user_version` schema version **2**; `journal_mode=DELETE`, `synchronous=FULL`, `busy_timeout=2000`, `foreign_keys=ON`. Opening a newer version fails (`unsupportedSchema`). Migration v1->v2 backs up the old file first (`history-before-v2-<UUID>.sqlite`) then adds `attempts.started REAL` and `attempts.duration_ms INTEGER`. On every open: `sessions.status='recognized'` rows become `interrupted` (with `finished = now` if null) and `deliveries.outcome in (intent, copied, dispatched)` become `uncertain` (a restart never replays an output).

Tables:
- `sessions(id TEXT PK, created REAL, raw TEXT NULL, selected TEXT NOT NULL, engine, model, language TEXT NULL, settings_version INT NULL, instruction_version INT NULL, status TEXT NOT NULL, finished REAL NULL, selected_attempt TEXT NULL, raw_chunk TEXT NULL, continuation_prefix TEXT NULL, selection_kind TEXT NOT NULL)`, index `(created DESC, id DESC)`. `created`/`finished` are Unix epoch seconds (double). Status values: `recognized, completed, interrupted`. `selection_kind`: `raw, attempt, fallback, legacy, recovered`.
- `attempts(id PK, session_id FK->sessions ON DELETE CASCADE, created, input NOT NULL, output NULL, provider, model, instruction_version, failure, prompt, status, attempt_index, profile BLOB (JSON), started REAL NULL, duration_ms INT NULL)`, unique `(session_id, attempt_index)`. Status `succeeded|failed|cancelled`. Constraints: succeeded iff output present; prompt <= 65 536 bytes; provider/model <= 256; failure category <= 128 bytes; profile snapshot strings <= 128 bytes. Profile JSON fields: `profileID, cleanupMode, reasoningEnabled, contextEnabled, style, structure, profileRevision, destinationMode`.
- `deliveries(id PK, session_id FK cascade, created, payload NOT NULL, outcome)`; outcomes `intent, copied, dispatched, verified, uncertain, failed`; allowed transitions: intent -> any non-intent; copied -> dispatched|verified|uncertain|failed; dispatched -> verified|uncertain|failed; terminal: verified, uncertain, failed.
- `tombstones(id)` - deleted session ids, so a late write cannot resurrect a deleted/cleared row; `repository_state(generation)` - incremented by Clear All so late writes from older sessions are rejected; `legacy_imports(source sha256 BLOB PK, entry_count)`.
- Writes are `BEGIN IMMEDIATE ... COMMIT`; failures return a typed `notSaved(reason)` acknowledgement. No raw/attempt overwrite; selection changes only after the attempt is durable.
- **Windows:** use `Microsoft.Data.Sqlite` with the same schema (portable as-is). Preserve the tombstone/generation semantics.

Session lifecycle writes (`RemyHistoryController`, `AppState.finishDictation`):
1. `beginSession` (in-memory context capturing the repository generation).
2. `recordRaw` after final recognition: row with `raw = rawChunk`, `selected = join(prefix, rawChunk)`, status `recognized`, `selection_kind=raw`, `raw_chunk`, `continuation_prefix`. For continuations `rawText` stays the new chunk only.
3. Cleanup: success -> attempt `succeeded` selected (`selection_kind=attempt`, status `completed`); failure -> attempt `failed` with output NULL then `selectFallback(rawCorrectedText)` (kind `fallback`); mode none/vocabulary -> `completeRawSelection` (kind `raw`); cancelled cleanup -> attempt `cancelled` and session `interrupted`.
4. `recordDeliveryIntent(deliveryID, payload=processedText)` BEFORE any clipboard/paste effect; only after it is acknowledged the permit is issued. After the effect `recordDeliveryResult`.
5. `recordRecovered` for "Recover text": a `completed` row with `selection_kind=recovered`; `raw` only if known (audio-only recovery with no prefix).
- Acknowledgement: `saved`, `historyOff`, or `notSaved(failure)`. Delivery is permitted for `saved` and `historyOff` only.
- **History off** (`remyHistoryEnabled` default **true**): no database and no recovery audio for new sessions; transcripts remain in memory for delivery and are lost at quit; delivery still works. UI text: label "Save new dictations", caption "Changes apply to your next recording. Turning this off keeps existing history and disables recovery audio for new sessions."; hint when off "History off. New transcripts stay in memory for delivery and cannot be recovered after Remy quits."
- **Persistence failure** (`notSaved`): the transcript stays in memory, delivery is **not** performed automatically, the session is stopped with `stopForHistoryFailure` (`AppState.swift:2537-2573`): revoke lease, preserve the recovery audio as a recovery entry (if saving that also fails: "Recovery details could not be saved. Existing audio was kept; copy the unsaved transcript before quitting Remy."), restore audio state, fail the session (stage `history`, `persistence`, not retryable), hide overlay, switch to the History page and bring the main window forward. History page warning: "Some history changes were not saved. Copy any unsaved text you need before quitting Remy. A paste that was already sent is never automatically retried." If the database cannot be opened/migrated at startup the app runs with an in-memory "NotSaved" controller (never silently "history off").

### 6.3 Retention, listing, search, deletion

- **Retention options: none.** There is no age/size retention control for transcripts (`docs/privacy.md`: "no automatic age-based transcript retention control"). History persists until the user deletes. The UI list/projection loads up to **1000** most recent sessions (`page(limit: 1000)`); queries accept `limit` clamped to 1..1000 and cursor paging `(created, id)` descending. (Architecture doc proposes retention age/size as future work.)
- **Search:** case-sensitive literal Unicode substring match on `selected` OR `raw` (`instr(...)>0`, no LIKE/FTS) (`RemyHistoryRepository.page`, lines 189-196). Placeholder "Search your dictations"; empty states: title "No transcripts" with message "Completed dictations will appear here."; title "No matches" with message "No dictations match “{query}”."
- History page copy: title "History", subtitle "Your recent dictations, kept on this Mac." (Windows: "this PC"). Section "History storage". Hint "With history on, a temporary audio copy protects interrupted or failed dictations. Successful recordings are removed; recovery audio expires after 24 hours."; row actions "Copy", "Copied", "Copy raw", "Copied raw", delete icon "Delete transcript"; detail sections "Delivery" ("No delivery result recorded. Older records may not include delivery details."), AI pass rows "AI pass {n} · {status}", "Selected", "No AI output recorded.", "AI instructions not recorded.", "Loading processing details…", "Retry loading details", "Refresh details".
- **Delete one** (`delete(id)`): revoked locally *before any await* (removed from memory/UI immediately), tombstone inserted and row deleted (attempts/deliveries cascade). If the disk delete fails the row stays hidden this process; the UI warns "Deleted history is hidden for this session, but its deletion from disk is not confirmed. It may return after Remy restarts." Deleting the history entry of an in-flight session cancels the dictation first.
- **Clear All** (`clearAllStoredHistory`, `AppState.swift:2591-2621`): confirmation dialog title "Clear All", button "Clear All…" (destructive) and message: "Delete this app's saved transcripts, recovery recordings and history rollback backups. This removes history rollback options. Models, settings and API keys are kept. Copies you exported, the clipboard and system backups are not cleared. This cannot be undone." It cancels any dictation first; blocked while recovery audio is in use ("History could not be cleared while recovery audio is in use. Try Clear All again after recovery finishes."); clears database rows (generation++ and tombstones), the legacy preference blob, schema backups and legacy backups, and recognized recovery `.caf`/`.json` files (UUID-named only; unknown files and links are preserved). Partial failure message: "Some stored history could not be deleted. Saved transcripts, recovery files or rollback backups may remain and return after restart. Repair folder access, then use Clear All again." It never deletes the open database file, models, settings or Keychain credentials, and is not retried automatically.
- Legacy history import (`RemyHistoryBootstrap`, `RemyLegacyHistorySnapshot`, `TranscriptHistoryPersistence`, `RemyLocalDataMigration`): migrates an older `UserDefaults` JSON blob `transcriptHistory` (`[{id,text,createdAt,rawText?}]`) into SQLite (idempotent by SHA-256 of the blob; backed up first as `legacy-history-<sha>.json`). **Not needed on Windows** (no prior store). `TranscriptHistoryPersistence` only serializes saves of that legacy blob on a utility queue with a flush barrier; irrelevant for SQLite.

### 6.4 Recovery audio and sessions (`DictationRecovery.swift`)

Two products exist, do not confuse them:
- **Safety spool**: while history is on, every dictation writes recovery audio (3.7). On success it is deleted (`discardSessionRecovery(completed: true)`). On any failure (recognition error, history failure, writer failure, quit) it is converted to a recovery session.
- **Recovery session** (`CancelledDictation`, JSON metadata `<uuid>.json` beside `<uuid>.caf`): fields `id, createdAt, expiresAt (= created + 24 h), duration (seconds; previousDuration + samples/16000), preview (last live transcript), completedTranscript (a final recognition result captured before cancellation, if any), hasAudio, captureError?, transcriptPrefix?, targetBundleIdentifier?` (JSON-encoded with default Foundation date encoding).
- **Retention: 24 hours** (`static let retention = 24 * 60 * 60`, `DictationRecovery.swift:164`). Cleanup occurs while the app runs: a timer wakes at the earliest expiry and reloads; also at launch/reload. Orphan `.caf` files older than 24 h without metadata, and unreadable metadata files older than 24 h, are deleted. Entries "in use" (being recovered/continued) are exempt until released.
- **Orphan discovery**: a `.caf` without metadata, regular file, 16 kHz mono, length > 0 and younger than 24 h becomes an entry with preview "Interrupted recording" and `captureError = "Recovered after an interruption. Check the transcript for missing audio."`; metadata is written for it.
- **When a session is preserved**: user cancel only if the setting `preserveCancelledSessions` was true at start (default true) or the session is a continuation; recognition/history/writer failure always; normal quit always; sleep-cancel follows the setting. A session with zero samples and no preview/prefix/completed text is deleted instead of saved. Metadata write failure keeps the audio and a volatile in-memory entry (message "Recovery details could not be saved. Existing audio and this session's text have been kept."); `captureError` partial warning: "Only part of the cancelled audio was saved: {error}".
- **UI** (History page "Recovery sessions", verbatim): hint "Continue restores your words and resumes recording. Stop finishes the combined dictation; if the original app is unavailable, the text is copied. Recover text saves it here without pasting. Sessions expire after 24 hours."; row title "Saved for recovery · {timestamp}"; preview or "Audio saved for recovery"; "{n} sec · Expires in {relative}"; warning "Partial recovery copy — some audio may be unavailable."; buttons "Continue" (becomes "Preparing…" then "Stop"/"Finishing…"), "Recover text" (becomes "Recovering…"), trash "Delete cancelled session from {timestamp}".
- **Recover text** (`recoverCancelledDictation`, `AppState.swift:2835-2889`): requires idle and history enabled (else error "Turn on Save new dictations in History before recovering this recording. The saved audio is unchanged."). Uses the *current* engine/model/language and filler rules; cleanup mode is forced to none. Text source priority: `completedTranscript` if present; else if audio exists decode it (prefix joined) - if the new audio decodes to empty while a prefix exists it fails with "No speech was recognized in the latest saved audio. It has been kept so you can try another speech model."; else preview or prefix. Empty after filler removal -> "No speech was recovered. The recording is still available; try a different speech model.". On success: history row (`recovered`; raw known only when audio-only with no prefix/completed text), text becomes the last transcript, then the recovery files are removed (history commit happens **before** audio removal). It never pastes. Errors: "Recovered text was not saved. Copy it from History before quitting. The saved recording has been kept." and "Recovery failed. The saved session is still available. {reason}".
- **Continue** (`continueCancelledDictation`, `AppState.swift:2908-2989`): requires idle, not sleeping, no cleanup draining, mic permission (else "Microphone access is ready. Click Continue to resume your saved session." after a grant, or "Microphone access is required to continue. Your saved session is still available." on denial). Restores the text (as above, must be non-empty: "No speech was restored. Try Recover text with a different speech model."), sets it as `transcriptPrefix`, remembers `previousRecordingDuration`, re-activates the saved target app by bundle id if still running, captures its focus snapshot, then begins a **fresh toggle-mode session** (`mode: handsFreeToggle`) whose raw event is the new audio only. The source entry stays until the continued session succeeds (then removed) or is released. Failure text "Could not continue. Your saved session is still available. {reason}" and, if the mic fails to start, "Could not start the microphone. Your saved session is still available. {message}". **Windows:** `targetBundleIdentifier` becomes the executable path/AppUserModelID; relaunch is not attempted.
- The recovery store never decodes or pastes automatically, never replays a provider or output.

### 6.5 Known limits carried over (do not claim fixed on Windows)

Per the audit doc: no total recovery disk quota or free-space policy; no bound proof on full-session memory; abrupt crash/power loss can lose unflushed samples (orphan recovery is best-effort); CAF/metadata deletion and SQLite transactions are separate resources (no durable deletion queue).

---

## 7. Sounds (`RemySoundEffects.swift`, `Settings.swift:2021-2026`)

Events: only two. `start` plays when the microphone is confirmed live and the overlay shows listening (`AppState.swift:2092`, after `markRecording`). `completion` plays after delivery for `success` and `copiedOnly` (`AppState.swift:2486-2492`), **not** for `failed`, **not** on cancel, **not** on "no text", **not** on errors. No sound at stop time.

Settings: `soundEffectsEnabled` default **true**; `soundEffectsVolume` default **0.3** (Float 0..1; non-finite or out of range clamps to 0..1, invalid -> 0.3); `dictationStartSound` default `builtin:Purr`; `dictationCompletionSound` default `builtin:Pop`. Master switch and volume are applied by the caller (`playDictationSound` returns without playing if disabled or running under tests).

Identifier grammar: `none`; `builtin:<name>` where name is one of `Tink, Pop, Purr, Ping, Glass, Hero, Submarine, Funk, Basso, Bottle, Blow, Frog, Morse, Sosumi` (14 names); `custom:<UUID>.<ext>` with ext in `wav, aiff, aif, caf, mp3, m4a` and total identifier <= 64 bytes. Labels: "None", the built-in name, "Custom audio", "Unavailable sound".

**The built-in sounds are macOS system sounds** (`NSSound(named:)` from `/System/Library/Sounds/*.aiff`); they are **not bundled** in this repo (no audio assets exist under `app/`). **WINDOWS DECISION:** ship 14 original sound files (or fewer) bundled in the app and keep the same identifier scheme and the default start = Purr-like, completion = Pop-like. Do not copy Apple's sounds.

Playback: stops any currently playing sound first (single `activeSound`); `none` plays nothing and counts as success; unknown/missing/corrupt custom sound falls back to the event default (`Purr` for start, `Pop` for completion) and reports "unavailable" (Preview then shows "That sound is unavailable. Remy used the default sound instead."). Playback is fire-and-forget on the main thread.

Custom sounds (`RemySoundEffects.Store`): imported via a file picker ("Choose a short sound effect"); validation: extension allowed; local (not iCloud-pending) file; regular file, no symlink, no hard links; **size <= 10 MiB** (10 * 1024 * 1024) and > 0; decodes as audio with sample rate in (0, 192 000], 1-8 channels, **duration <= 10 s** (and <= 1 920 000 frames); copied into the app's `Sounds/` folder as `<UUID>.<ext>` with private permissions via atomic stage+rename; identifier `custom:<UUID>.<ext>` stored, never the source path. Messages (verbatim): success "Sound imported. Use Preview to listen."; failure "Audio could not be imported. Choose a supported local clip up to 10 seconds and 10 MiB."; caption "Import a local WAV, AIFF, CAF, MP3 or M4A clip, up to 10 seconds and 10 MiB. Remy keeps its own copy; your original stays unchanged."; rows "Recording started" and "Dictation completed" with a picker, "Import audio…", "Preview" (disabled when "None"). **Windows:** `System.Media.SoundPlayer` handles WAV only; use `MediaFoundation`/`NAudio` (`AudioFileReader` + `WaveOutEvent` with volume) for wav/mp3/m4a/aiff as needed; shared output device default; do not let the notification sound mix into the microphone capture in the start-sound case (the start sound plays after capture is live, so on Mac it may be captured by the mic; muting system audio (3.2) is the user-level mitigation). **WINDOWS DECISION:** Mac plays the start sound *after* capture starts, so it can bleed into the first fraction of the recording; keep for parity or delay recognition of the first ~150 ms.

---

## 8. Diagnostics, performance tracing, attention issues, privacy

### 8.1 Logging policy (`RemyDiagnostics.swift`, `docs/engineering/diagnostics.md`)

- Logging uses the unified system logger with subsystem = bundle id (fallback `io.github.brysonjl.remy`) and categories such as `AppState`, `HotkeyService`, `AudioPipeline`, `ParakeetEngine`, `AsrCoordinator`, `TextInsertion`, `InsertionTarget`, `PCMConversion`, `Performance`. **MAC-ONLY**; Windows equivalent: `Microsoft.Extensions.Logging` to a rolling file under `%LOCALAPPDATA%\Remy\Logs\` and/or `EventSource`/ETW, same category names.
- **Never log**: audio, transcripts, selected-field text, prompts, provider bodies/errors, API keys, file paths, window/document titles, target process ids, microphone UIDs/names, arbitrary error descriptions, key presses or delivered text. Errors are logged only through `errorCategory(error)` which yields exactly one of `cancelled`, `timeout`, `transport`, `other`.
- Allowed fields: session UUID, known engine/mode labels, counts, durations, numeric status codes, OS/hardware model; insertion logs describe decisions (e.g. `insertionFormatting: model=... leadingSpace=...`, `pasteGuard: copiedOnly automatic=... targetProof=... targetActive=...`, `target reason=... role=<allow-listed class or "other"> secure=... enabled=... editable=...` with role names restricted to an allow-list). Hotkey logs: "Hotkey monitoring started", "Failed to create CGEvent tap. Will retry until the login session is ready.", retry delay/attempt numbers.
- Third-party SDK logging is set to warning level with console mirroring off (`configureFluidAudioLogging`); debug mirroring only via `REMY_KEEP_FLUID_DEBUG_LOGS=1` in Debug builds.

### 8.2 PerfTrace (`PerfTrace.swift`)

Lightweight span timing: `PerfTrace.begin("<area>.<phase>")` returns an interval; `end(outcome:)`; `measure`; point `event`. Backed by signposts + notice-level log lines `name duration_ms outcome` with session metadata labels. Kill switch env var `DICTATE_ANYWHERE_PERF_TRACE=0` (and `=1` forces on); default **off in distribution builds**, on in local builds. Session labels (`AppState.beginPerformanceSession`/`performanceConfigurationLabels`, lines 1704-1762): `session_id`, `audio_tap_buffer_frames=4096`, `hotkey_mode`, `engine`, `model`, `language`, `eou_enabled`, `cleanup_mode`, `s1_mini_enabled`, `filler_removal_enabled`, `context_awareness_enabled`, `audio_mute_enabled`, `microphone_boost_enabled`, `input_auto_switch_enabled`, `custom_vocabulary_enabled`, plus `context_category`, `context_excluded`, `secure_field`, `text_position_snapshot`, and OS/hardware labels. Span names worth keeping on Windows for comparable measurements: `app.launch`, `app.startup`, `app.shutdown`, `dictation.requestToRecording`, `dictation.start`, `dictation.stopToInsertion`, `dictation.cancel`, `dictation.teardown`, `audio.startup`, `audio.controllerCreate`, `audio.controllerWait`, `audio.settle`, `audio.teardown`, `audio.systemMute`, `audio.microphoneBoost`, `audio.restoreSettle`, `stt.modelLoad`, `stt.modelWarmup`, `stt.batchPreview`, `stt.batchFinish`, `stt.finalize`, `stt.stopToFinal`, `stt.firstPartial` (event), `audio.captureSummary` (event with `captured_samples`, `dropped_pending_samples`), `transcript.normalize`, `transcript.history`, `insertion.orchestration`, `insertion.deliver`, `insertion.clipboard`, `insertion.pasteAX`, `insertion.pasteEvent`, `recovery.captureStart`, `recovery.preserve`, `recovery.discard`, `recovery.reload`, `recovery.transcribe`, `recovery.continue`, `cleanup.*`. Windows: `System.Diagnostics.Activity`/`Stopwatch` with the same names; only numeric facts.

### 8.3 What problems are surfaced to the user (`AttentionIssue.swift`, `AppState.attentionIssues` lines 471-489)

A single banner in the main window shows the first pending issue; order is blockers first. `isOptional` false = blocking. Exact strings:

| ID | Title | Message | Action | Optional |
| --- | --- | --- | --- | --- |
| microphone | "Microphone access needed" | "Allow microphone access to start dictating." | "Allow Microphone" (can prompt) / "Open Settings" | no |
| accessibility | "Accessibility access needed" | "Allow Accessibility for shortcuts and pasting." | "Open Settings" | no |
| speechSetup | "Dictation setup needed" | Parakeet: "Download a speech model to start dictating." / failure: "The speech model could not be prepared. Review Speech Model settings." (other engines have their own) | "Set Up" | no |
| recovery | "Dictation recovery needs attention" | the recovery store's `errorMessage` | "Dismiss" | no |
| recordingFailed | "Recording could not start" | `recordingStartError` (only if mic granted) | "Dismiss" | no |
| appleSpeechUnsupported | "Apple Speech is unavailable" | ... | "Dismiss" | yes |
| cleanup(problem) | "Transcript cleanup needs attention" | per problem (e.g. "Choose an Ollama model to use transcript cleanup.", "Add an OpenRouter API key to use transcript cleanup.", "Choose an OpenRouter model to use transcript cleanup.", "Choose a model on your OpenAI-compatible server to use transcript cleanup.", "Create or select a saved cleanup profile. Your speech transcript is kept if cleanup is unavailable.", Apple Intelligence/S1-mini variants, vocabulary variant above) | "View Setup" / "Open Settings" / "Open Speech Model" | yes |
| automation | "System Events access is off" | "Allow System Events for AppleScript paste. Keyboard paste still works." | "Open Settings" | yes |

Blocking issues also pop the main window once per occurrence (`presentBlockingAttentionIssueOnce`); the "presented" markers re-arm when the permission is granted. Status bar `status = .error(String)` texts listed in 2.4. **Windows mapping:** Accessibility and Automation issues disappear; add equivalents for "microphone privacy setting off" (Settings > Privacy > Microphone, `ms-settings:privacy-microphone`) and optionally "target app is elevated" (informational, when copy-only occurred for that reason).

### 8.4 Overlay states (user feedback surface)

`OverlayState`: `listening` (waveform + optional live text + "LISTENING" label + optional "Hold to cancel" progress), `processing` (spinner; accessibility "Processing dictation"), `success` (check), `copiedOnly` (clipboard icon + "Press ⌘V"), `cleanupFallback(reason, delivery)`, `preparingModel(name)` ("Loading {name}…"). Preview text setting `showTextPreview` default true; overlay size default "comfortable", theme default system. A rejected start shows "Already working on your dictation" without changing the underlying state. (Overlay layout/visuals belong to another spec; only the state/strings contract is listed here.)

---

## 9. Settings defaults relevant to this pipeline (consolidated)

| Setting (key) | Default | Source |
| --- | --- | --- |
| `hotkeyBindings` | `[ Control+Option+Command modifier-only, holdToRecord ]` | `Settings.swift:838`, `1782` |
| `cancelShortcut` | Esc (key 53), no modifiers | `Settings.swift:846` |
| `holdToCancel` | true (1.0 s hold) | `Settings.swift:1744` |
| `preserveCancelledSessions` | true | `Settings.swift:1745` |
| `dictationDestination` | `originalField` | `RemyDictationDestination.decodeStored` |
| `selectedMicrophoneUID` | nil (automatic/system default) | `Settings.swift:1958` |
| `boostMicrophoneVolumeEnabled` | true (target 0.8) | `Settings.swift:1961` |
| `muteSystemAudioDuringRecordingEnabled` | false | `Settings.swift:1964-1968` |
| `autoStopAfterSpeechEndsEnabled` | false (EOU model only) | `Settings.swift:1971` |
| `showTextPreview` | true | `Settings.swift:1982` |
| `soundEffectsEnabled` / `soundEffectsVolume` | true / 0.3 | `Settings.swift:1976-1977` |
| `dictationStartSound` / `dictationCompletionSound` | `builtin:Purr` / `builtin:Pop` | `RemySoundEffects.swift:9-10` |
| `isFillerWordRemovalEnabled` / `fillerWordsToRemove` | false / `["um","uh","erm","er","hmm","嗯","呃"]` | `Settings.swift:1852-1853` |
| `customVocabulary` | `[]` | `Settings.swift:1856` |
| `remyHistoryEnabled` | true | `Settings.swift:1887` |
| `inputSourceAutoSwitchEnabled` | false | `Settings.swift:1848` |
| `dictationContextAwarenessEnabled` | true (frozen per session; remote text sharing default false) | `Settings.swift:1860` |
| `selectedLanguage` | `"en"` | `Settings.swift:1816` |
| cleanup mode | `none` unless stored | `Settings.swift:1896` |
| `prewarmEnginesAtStartup` | true (loads the model at app start) | `Settings.swift:1988` |
| `launchAtLogin` | false | `Settings.swift:1987` |

---

## 10. Windows decisions and divergences summary

1. Hotkey hook: WH_KEYBOARD_LL, own modifier-state tracking, exact-match semantics for modifier-only, non-exact for keyed, swallow rules for keyed and immediate-cancel Esc. No tap/hold timing logic exists; do not add any (parity).
2. Default shortcut: choose a Windows-appropriate modifier-only hold (Mac default Ctrl+Option+Command has no analog).
3. Overlap of physical modifiers with `SendInput` Ctrl+V after a modifier-hold hotkey release: needs explicit handling (Mac posts to the target pid so it was immune).
4. Clipboard: Mac copies and leaves the text; no restore. Keep unless product decides otherwise (spike restored). Use `GetClipboardSequenceNumber` for the ownership check (analog of `changeCount`), plus byte-exact text compare, plus read-back stability.
5. Paste gate: replace AX proof with foreground HWND/PID/creation-time + UIA control facts; add elevation check; classify password fields via UIA; keep "multiline into terminal => copy-only".
6. Chunked final decode for long recordings (<= 20 s chunks, background), not whole-recording decode; identical output only guaranteed for short recordings.
7. Vocabulary: store and use for cleanup prompts only; no acoustic boosting for v2 (matches Mac).
8. Sounds: ship original assets; same identifiers.
9. Remove Accessibility/Automation/AppleScript/System Events concepts; add microphone-privacy and elevation messages.
10. Persist a "Remy muted system audio" flag to survive crashes.

---

## 11. Riskiest items to port (ordered)

1. **Delivery safety model** (lease + proof + clipboard ownership + never-retry-after-claim): subtle races; Windows has no per-process key posting, so focus can change between the check and `SendInput`.
2. **Global hotkey semantics** (modifier-only exact matching, hold/toggle, key-up after modifier release, swallowing, sleep/lock invalidation, hook timeouts).
3. **Destination validation on Windows** (unknown-field policy, password/secure detection, terminals, elevated apps, UWP/Electron/Chromium UIA gaps) - the Mac policy of "unverifiable => copy-only" is too strict for Windows UIA reality; needs a deliberate relaxed rule and testing.
4. **Streaming/preview + final decode with sherpa-onnx** (15 s rolling-window previews, fresh state per decode, merge by time stamps, silence gating, chunked final, memory on long recordings).
5. **Foreground/clipboard focus restoration for "where dictation started"** (SetForegroundWindow restrictions, AttachThreadInput, 800 ms budget).
6. **History acknowledgement gating** (raw must be saved before cleanup; delivery intent before clipboard; `uncertain` on crash; tombstone/generation semantics for delete/clear).
7. **Recovery audio spool + crash recovery** (bounded writer, orphan discovery, 24 h expiry, Continue/Recover flows with prefixes).
8. **System audio mute and mic boost** (Bluetooth bypass, restore on every path, crash-safe restore).
9. **Audio start robustness** (first-buffer readiness, 5 s timeout, candidate-device fallback with 220 ms gaps, 120 ms restart gap, device hot-plug).
10. **Cancel gesture** (1 s hold, 25 ms progress ticks, plain-Esc passthrough vs immediate swallow, active-hotkey modifier masking).
