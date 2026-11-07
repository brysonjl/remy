# A03–A05 first integrated slice — 2026-10-19

These phases remain **in progress**, not acceptance-gate completions.

## Automated evidence

- `first-slices-r1.log`: compiled; 45 selected tests, six model-store failures
  caused by relative path calculation. Other selected components passed.
- `model-store-r2.log`: fixed path-components calculation; 10 model tests passed.
- `integration-r1.log`: 70 tests passed after model/transport/controller wiring.
- `integration-r2.log`: compiled, interrupted after a test deadlocked by awaiting
  cancellation before releasing its deliberately noncooperative cleanup fixture.
  This is not a passing run.
- `integration-r3.log`: 114 tests passed, including fixed deletion sequencing and
  cancellation while awaiting history generation, under isolated test identity.
- `ordinary-r1.log`: 930 tests, 59 opt-in skips, zero failures.
- `offline.log`: 33 tests passed.
- `foundation.log`: exact pins/toolchain and narrow hygiene passed, 147 inputs.
- `final-history.log`: 31 tests passed; final selected check covers raw continuation provenance,
  ambiguous dispatch vs copy-only records and the isolated test identity.
  Subsequent signed builds/live checks are recorded below.

## Data-loss incident and isolation repair

The inherited XCTest host shared `app.alfred.dictation.dev` preferences. During
the interrupted r2 fixture, legacy history was assigned an empty array before
teardown could restore it. The stable app's History page was independently
observed empty afterward. No verified pre-test history backup was available;
**history was not recovered**. The user was informed directly. Parakeet v2,
OpenAI-compatible `https://ollama.com/v1` / `gemma4:31b`, Ixway and BEGL were
restored through native UI. Credentials were not inspected/exported. Some
nonessential sound/prewarming/audio options were left off by the fixture; their
exact earlier values were not established.

Ordinary tests now build a separate `app.alfred.dictation.testhost` app with its
own preference/storage/Keychain namespaces and display name. Startup rejects
XCTest under the interactive app's identity before Settings construction. The
isolation test and built Info.plist both verified the independent identifier.
Source/app bundle checkpoints do not constitute a backup of user preferences.

## Implemented boundaries

A03: exact bundled v2 manifest (22 files, 464,414,912 bytes, revision
`ee09c569f73759e6d44c9bd16766f477b2b36d39`), staged verified download or explicit
owned-legacy copy/adoption, atomic activation metadata, rollback and leases.
Ordinary v2 prepare acquires a fully verified installed lease; it does not adopt
or download. Original files remain. Apple Speech asset installation is explicit;
optional vocabulary preparation defaults to installed-only. Shutdown joins work
before releasing the model lease. Leases are store-instance scoped.

A04: one SQLite actor/connection, DELETE journal/FULL sync, schema 1, immutable
raw chunks, separately retained continuation prefixes, append-only cleanup
attempts, selected raw/fallback/recovered output and delivery intent/results.
Startup captures private digest-named legacy backups before Settings, then imports
original IDs/times/fields transactionally and verifies an idempotent receipt.
Failed import/open uses a visible memory-only NotSaved state, never history-off.
Raw/final/intent acknowledgement precedes cleanup/delivery. Failed writes retain
memory and block automatic paste. Recovery source audio is removed only after
acknowledged recovery text; optional active recovery audio is retained on a
history failure when possible. History-off is captured per session and suppresses
new transcript/attempt/audio records. Delete/clear revoke work and tombstone late
writes. Restart marks pending deliveries uncertain and does not replay them.
The selected/intent string reaches the inserter verbatim, with formatting bypassed.

A05: immutable profile/credential-scope/migration contracts and strict-local
evidence policy; bounded complete-JSON HTTP for compatible/Ollama requests with
cancellation, body/time limits, redirect rejection and explicit completion markers.
Only answer text is accepted; typed reasoning/tools are excluded. Multipart
complete answers retain separators. SSE/NDJSON are parser-tested only. Cloned
URLSession configuration does not preserve custom delegates/pinning or pooling.

## Still open

No full A03–A05 gate is claimed. A03 still needs the real capture/lifecycle and
blocked-egress matrix, complete remaining model-route manifests and long-session
buffer limits. A04 still needs correction records, FTS/paginated UI, retention,
export, comprehensive audio deletion/restart fault coverage and saved-projection
memory bounds. Current UI loads up to 1,000 stored rows plus current-session memory.
Retry saving a failed raw write is not yet exposed; explicit manual copy is.
A05 profile store/Keychain migration/UI are now wired as described below; request
timeouts are not A06's whole-cleanup deadline. Semantic cleanup validation, A07 focused-field
guard (the observed Chrome failure), performance/quality series, 1,000 cycles and
eight-hour soak remain open. No publication or release acceptance occurred.

## Signed build and first launch

`signed-prepare.log` and `release.log` passed. Stable signed app is
`.local/run/Alfred Dev.app`; previous A02 bundle is retained at
`.local/run/.alfred-dev-backup.7o3cOX/Alfred Dev.app`.

Native UI initially offered Verify Existing Model. Explicit activation completed
and reached Ready to dictate, with no Accessibility warning. Original model
assets remain. The new History storage card was inspected visually with no
clipping; history is on and initially empty. Cleanup UI confirmed the selected
Ollama Cloud URL/model, Ixway/BEGL and filler removal off without reading keys.
The live cloud/history/restart smoke passed. Native UI read `Schedule Friday at 3.
Do not change the $15,000 budget` in TextEdit Untitled 3, History final and raw.
Those history values survived clean restart, and TextEdit still contained one copy.
The row remained after the later isolated tests. See `live-checks.json`.


## Named profiles continuation

`profiles-compile-r1.log` failed on an actor-isolated default argument; fixed.
`profiles-integrated-r1.log` passed41 profile/isolation tests. Full ordinary
`profiles-ordinary-r1.log` passed965 tests,59 skipped,0 failures. The final app
integration suite passed32 tests, including3 new profile capture/migration/history
checks. Offline33 and foundation/pin/hygiene151-input checks passed.

File repository: bounded versioned private JSON, nofollow600/700/single-link
checks, cross-instance lock/CAS, verified staging bytes AND pathname identity,
atomic replacement with directory sync. Profile controller stages fresh credentials
before commits, never mutates selected keys during failed edits, and preserves
legacy sources. Uncertain publication reports an unconfirmed save and preserves the previous
observable selection; readable bytes alone do not prove durability. The focused
`profiles-durability-final.log` run passed13 tests.

Runtime/editor are wired through Saved Profile. Conversion is explicit, not startup.
Profile snapshots and credentials freeze at session start, per-request timeouts and
conservative capabilities are respected, and errors expose only closed categories.
Synthetic connection check has no history/clipboard authority. Stream protocols
remain parser-only; unknown strict-local routes are rejected. Removed/replaced keys
remain for later GC. OpenRouter environment-key migration, stream transport,
whole-cleanup deadline, verified local execution and full A05 gates remain open.
Final Keychain/controller/runtime/app integration checks passed27 tests
(`profiles-keychain-final.log`). Signed Debug preparation and Release passed
(`profiles-ui-final-prepare.log`, `profiles-ui-final-release.log`).

The first signed migration attempt failed generically and kept the legacy method.
Inspection found no app-ID/access-group entitlements on the unprovisioned dev
signature; no specific Keychain OSStatus was captured. The adapter now explicitly
uses macOS file-based Keychain with `SecAccessCreate`'s creator-only ACL, unchanged
endpoint-scoped accounts, and no data-protection fallback or entitlement mutation.
Updates preserve ACLs. This is not a ThisDeviceOnly guarantee. Apple's
[TN3137](https://developer.apple.com/documentation/technotes/tn3137-on-mac-keychains)
and [SecAccessCreate](https://developer.apple.com/documentation/security/secaccesscreate(_:_:_:))
describe the distinct implementation/access-control mechanisms. A provisioned
release Keychain strategy remains a later distribution decision.

The repaired explicit UI migration succeeded: Saved Profile / Ollama Cloud,
exact URL/model/prompt preserved, key shown only as configured, synthetic connection
check passed. No new key entry, agent credential read/export or permission grant.
Profile controls visually inspected; final polish adds visible labels and cancels
connection checks when leaving/changing selection, so an old result cannot certify
an edited draft. Final live dictation check remains pending.

## Source-ahead checkpoint: history deletion, cleanup deadline and paste guards

The running stable app is still the **final signed A05 profile build**. The
following changes are in source and compile, but have **not** been staged over
`.local/run/Alfred Dev.app` or live-validated. Do not describe them as shipped.

- A04 delete/clear immediately revoke memory/context authority even with no or
  read-only database. Pending disk deletion has a themed warning and explicit
  retry. A retry of an older Clear All preserves sessions created afterward and
  never repeats clear-all. Pending intents are process-local; unsuccessful disk
  deletion can reappear after restart. No schema change or durable retry queue.
- A07 captures process launch identity and AX field/window before preparation or
  overlay, then revalidates after bounded editability/ancestor checks and again
  inside paste workers. Unknown/secure/read-only/changed targets are copy-only.
  Known terminals reject any newline, including trailing Unicode separators.
  Normal delivery no longer reactivates the original app. Clipboard generation
  and exact UTF-8 bytes must still match the successful own-copy before dispatch;
  external clipboard changes are preserved. No guarantee of an atomic external
  focus/clipboard transaction. Continue now rejects sleep/wake generation changes.
- A06 foreground cleanup has an eight-second decision deadline including remaining
  preparation, and a menu-bar **Use Raw Now** action. Timeout/use-raw selects exact
  corrected raw once; late provider results cannot replace it. Worker ownership
  remains until explicit drain; new capture and shared cleanup runtime reuse are
  blocked while draining. Cancel revokes immediately, then joins before reuse.
  Heavy synchronous work must remain off the main actor for the timer to run.
- Frozen remote runtime rejects over-budget input/prompt/vocabulary before provider
  work using an explicit conservative byte estimate capped at 8192. Smaller known
  context/completion budgets tighten it. One generation request; no automatic
  schema-adaptation retry or chunk/reassembly. Direct legacy adapter callers keep
  old behavior. Output reserve is planning, not a universally enforced wire cap.
  Semantic validation/versioned corrections and broader A06 acceptance remain open.

Validation so far: `delivery-history-compile-r1.log`, `deadline-delivery-compile-r2.log`
and `deadline-delivery-final-compile.log` are successful signed **build-for-testing**
compilations, not executed Xcode suites. Initial new test isolation/trailing-closure
warnings were repaired; final compile contains only existing toolchain metadata
warnings. Dependency-free offline run `deadline-offline-r2.log` passes **40 tests**,
including seven deterministic real-source cleanup attempt tests (new relative
symlinks). `deadline-foundation.log` passes exact pins/toolchain and 153 selected
source hygiene inputs. No credentials or user storage were read/exported.

Pending: execute focused then ordinary isolated Xcode suites, signed development
staging and real destination/copy-only checks. The existing live profile question
is still pending in **TextEdit Untitled 4**. The latest user reply referenced the
older Untitled 3 first-slice question, whose success is already recorded; native
UI inspection found Untitled 4 empty, so it was not counted as a new profile pass.
The user was asked again for the synthetic Thursday-to-Friday/$15,000 phrase.
Do not launch the test host, replace the running app, or steal focus during that
check. A00/A01/A02 statuses stay complete at their documented scope; A03 onward
acceptance gates stay open. All three GPT-6.1 Sol agents are complete/frozen.

Final unsigned Release build also passed (`deadline-delivery-release.log`). Latest
source checkpoint: `.local/checkpoints/a04-a06-a07-compiled-20261019.tar.gz`. This
is source/build evidence, not user-data backup or a validated/staged runtime.
