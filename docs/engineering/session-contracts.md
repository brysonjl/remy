# A02 session contracts

These are the current seams around the inherited adapters, not a replacement
ASR/storage/provider stack. Shared source review found no remaining delivery
cancellation race in the reviewed integration. The second full ordinary run passed 831 tests with 59 skips and zero failures.
Build/live validation remains pending; no A02 completion is claimed.

## Authority and configuration

[`RemySessionCoordinator`](../../app/Dictate%20Anywhere/RemySessionCoordinator.swift)
is MainActor-owned. `begin(configuration:id:)` returns a `Token` containing UUID
and monotonically increasing generation, and refuses a second active session.
`isCurrent` rejects terminal/older tokens. The sequence counts accepted state
transitions and the write-once raw event; duplicate events/permit consumption do
not advance it. The foreground route is:

```text
preparing → recording → finalizing → cleaning → ready → delivering → completed
                           └────────────→ ready
any active phase → cancelled / failed
```

`recordFinalRaw` accepts one final raw result while finalizing. `markReady` grants
no clipboard authority. `authorizeDelivery` reserves one `DeliveryPermit` and
moves to delivering. `complete`/`fail` revoke remaining output authority; typed
failure fields identify stage, category and retryability. They are an in-memory
contract, not a durable attempt log.

[`AppState`](../../app/Dictate%20Anywhere/AppState.swift) uses a preparation-request
UUID before a session exists. Permission/model preparation checks its current
request after suspension; cancellation invalidates the request so late completion
cannot start capture. An initial microphone permission gesture never becomes a
recording gesture. The request UUID is passed to coordinator `begin` and used in
performance metadata, correlating preparation and recording. Trace clearing is
conditional on that ID. A failed/obsolete preparation cannot clear another
session's metadata.

[`RemyDictationConfiguration`](../../app/Dictate%20Anywhere/RemyDictationConfiguration.swift)
freezes capture/model/language/microphone/vocabulary/filler/audio/recovery choices
and cleanup mode/endpoint/model/prompt/reasoning at recording start. Its settings
and instruction versions are explicit. `credentialReference` is an ephemeral UUID,
not a persistent Keychain account or provider-profile ID. The corresponding
`RemyCleanupRuntime` privately closes over the credential value captured then;
cleanup does not reread a changed key or environment reference. Do not encode/log
this configuration: prompts, vocabulary and endpoint settings are private.

[`RecognitionSessionConfiguration`](../../app/Dictate%20Anywhere/RecognitionSessionConfiguration.swift)
is installed through `setSessionConfiguration` before capture and cleared during
teardown. Recognition adapters retain model/language/vocabulary/preview choices
through finalization. Selection preparation is checked against the expected
recognition snapshot before capture; changes during preparation abort that
request rather than mixing an old engine and new configuration. Context awareness,
remote-sharing consent, and per-category styles are frozen for each recording.
Bounded context is captured from the original destination alongside recording and
settled before cleanup. Cleanup receives app name/category/style when enabled;
remote document titles, page URLs and nearby text require the separate sharing
opt-in. Local processing may use the bounded snapshot. Off and vocabulary-only
cleanup do not collect destination context.

## Delivery linearization and cancellation

[`RemyDeliveryAuthorization`](../../app/Dictate%20Anywhere/RemyDeliveryAuthorization.swift)
is the thread-safe lease shared by coordinator and actual output workers:

| API | Meaning |
| --- | --- |
| `consumeClipboard()` | Claim initial clipboard ownership once; does not claim a successful copy |
| `performClipboardMutation` | Under one lock, recheck eligibility, perform a bounded synchronous clipboard mutation and record success |
| `consumePaste()` | After successful copy, authorize at most one potentially dispatched paste |
| `revoke()` | Atomically revoke and return `Snapshot(copied:pasteClaimed:)` |

The lock-bounded clipboard closure must not await or reenter the lease. Clipboard
retries and owned list rewrites recheck revocation; initial clipboard ownership is
not consumed twice. Cancellation before copy prevents mutation. Cancellation
after copy preserves the copied text but prevents paste. A paste claim is an
irreversible/possibly-dispatched boundary, not proof of visible insertion.
Cancellation cannot retract it or authorize another fallback.

[`TextInserter`](../../app/Dictate%20Anywhere/TextInserter.swift) accepts optional
`authorization:`. Foreground sessions must pass the permit's shared lease;
manual/history explicit insertion retains the default permissive behavior.
Actual AX workers claim immediately before AXPress; the script worker compiles
first and claims before script execution; keyboard fallback claims before the
balanced keyDown/keyUp pair. No fallback runs after a claimed/ambiguous attempt.
List selection and late clipboard restoration also check eligibility. A claimed
key pair must finish its release even if cancellation arrives after keyDown.

`cancelDictation` revokes coordinator/lease before its first await, then joins
old processing/capture work before engine reuse. The atomic revoke snapshot
resolves races with worker claims. The legacy `transcriptDelivery` fake defines invocation as its dispatch boundary
and claims both effects then; it cannot model every queued-worker or copy-only
failure boundary. The newer `authorizedTranscriptDelivery` fake receives the
shared lease and can model queued work, copy-only outcomes and cancellation; the
13 integration tests use this seam. Leaf tests additionally use fake effects and
isolated pasteboards. Neither kind is live paste evidence.

## Recovery, sleep and presentation

A recovery checkpoint is saved audio/preview/completed-text data, **not** a live
session token or output permit. `recoverCancelledDictation` reprocesses into
history without foreground delivery authority. `continueCancelledDictation`
retains the source checkpoint, restores a prefix, then creates a fresh foreground
session for resumed capture. A polished prefix plus new raw audio must not be
mislabelled as one full raw ASR result. Recovery retention/removal and inherited
history writes still need A04 durable transaction/failure semantics.

AppState observes workspace sleep/wake. Sleep changes activation generation,
suspends hotkey monitoring, invalidates held gestures and cancels pre-dispatch
foreground work with `inputUnavailable`. HotkeyService separately invalidates
queued callback generations. Wake only resumes eligible monitoring and requires
held modifiers to return to neutral; it does not resume recording or retry paste.
After an already-claimed dispatch, sleep cannot retract the OS action.

A rejected nonrecording busy start calls `overlay.showBusy()`: “Already working on
your dictation.” It preserves session state, transcript and cancellation progress,
adds no hide timer, and clears on the next legitimate state update/hide. It
neither starts another session nor changes the pinned display.

## Handoff to A03–A05 and open contracts

- **A03:** consume recognition snapshots and token-owned callbacks; retain the
  installed-model/no-silent-swap boundary. Explicit model revision/integrity,
  optional-asset lifecycle and route recovery still require implementation.
- **A04:** introduce durable repositories for immutable raw/final results,
  attempts, delivery intent/status and recovery checkpoints. Current history
  flush returns no durable commit/failure acknowledgement; do not call it a
  transaction or infer exactly-once delivery after restart.
- **A05:** turn provider/profile and credential references into explicit
  persistent identities while keeping active runtime snapshots immutable.
  Session credential UUIDs are not that storage scheme.
- **A06:** implement real cleanup deadline/fallback/attempt policy. A coordinator
  timeout-then-late-success test proves revoked authority rejection, not a running
  eight-second provider deadline. Preserve the observed Qwen correction/code-policy
  defects for evaluation.
- **A07:** add field identity, secure-field and terminal guards. The recorded
  Chrome field-change trial still fails the planned same-field/copy-only contract;
  the session lease prevents stale-session output, not a same-session focus change.

See [A02 acceptance](../implementation-plan.md#a02--session-lifecycle-and-stable-contracts)
and the [measured baseline](../../app/docs/engineering/baseline.md) for evidence and
remaining live/quality/resource limits. No later gate or daily-driver claim is
established by these interfaces.
# A03–A05 integration additions

`AppState` now optionally accepts an `RemyHistoryController` for isolated tests;
production constructs one owned SQLite repository after pre-Settings legacy backup.
Default inherited fixtures use their separate test-host preference namespace.
History context snapshots carry session UUID/time, deletion generation and
intentional enabled state. Raw is acknowledged before cleanup; selected output
and delivery intent are acknowledged before the output lease is issued. Every
await is followed by session-authority validation. Failed writes keep memory text
and stop automatic paste. Intentional history-off permits delivery and disables
new recovery audio. A failed migration switches to a NotSaved memory controller,
never a successful history-off acknowledgement.

New recognized chunks remain byte-exact in raw fields. A continuation's old prefix
is separate provenance and only participates in initial selected/output text.
Cleanup failure appends a failed attempt with nil output, then selects fallback
separately. Off-mode selection does not fabricate a cleanup attempt. Recovered
legacy/cancelled text keeps unknown raw nullable. Copied/dispatched/uncertain
outcomes are separate; ambiguous output after a paste claim is uncertain, and no
automatic replay follows a crash or failed outcome write.

`TextInserter.insertText(..., verbatimPayload: true)` bypasses contextual spacing,
style/model-plan/list rewriting so clipboard and dispatch use the persisted intent
string. This does **not** close A07's focused-field/secure-field/terminal guards.

Parakeet v2 acquires a manifest-verified installed lease before loading and retains
it warm. Only explicit installation/adoption activates a revision. Shutdown joins
capture/finalization/preparation and clears session configuration before releasing
that lease. Apple Speech and optional vocabulary ordinary preparation are
installed-only. Other model-route manifests/capture bounds remain open.

Compatible/Ollama cleanup use bounded cancellable complete-JSON HTTP and strict
answer-only completion parsing. Profile persistence/credential migration and the
whole-cleanup A06 deadline are still pending. See the [slice evidence](../../validation/results/2026-10-19-a03-a05/README.md).

