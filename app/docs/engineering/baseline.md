# Remy A00 baseline

Updated 2026-10-19. **Status: pinned Debug/Release builds, ordinary tests and three signed synthetic runtime/provider tests pass; signed Remy Dev launches. Accessibility is now recognized after the user removed/re-added the stable app; two live TextEdit toggle insertions and repaired quiet cancellation are confirmed; the A00 foundation gate is closed with RETAIN / REPAIR.** A01’s local implementation gate is complete with 776-test ordinary-suite, signed Debug, Release and UI checks passing. A02 is in progress; A02–A11 are not complete. Daily-driver/release acceptance remains open. The user additionally requested a friendly cartoon cyberpunk robot-waiter visual redesign; the UI/icon/recording-panel visual pass is implemented with the evidence below.

## Preserved source and identity

Upstream: `hoomanaskari/mac-dictate-anywhere@e420448de6e2099d33e050a4b419722032fd7519`. The 271 verified imported build-input files and original MIT notice were preserved. This remains a source import, not a clone with upstream Git history. [Import manifest](../../../research/upstream/mac-dictate-anywhere/IMPORT.json), [attribution](../../../ATTRIBUTION.md).

A recoverable pre-continuation checkpoint is `.local/checkpoints/pre-a00-20261019.tar.gz`, SHA256 `bd2e0a368971778c61d98cd94009c7ead65391b42118a5d1f4accfbddd814d31`. It excludes generated build caches and preserves validation symlinks. No repository/release was published and no upstream app was replaced.

Development identity is `io.github.brysonjl.remy.dev` / Remy Dev; Release is `io.github.brysonjl.remy` / Remy. Project/target/module remain Dictate Anywhere / `Dictate_Anywhere`. Data, model, recovery and Keychain service names are bundle-scoped. The stable signed development bundle is `.local/run/Remy Dev.app`, separate from the unsigned XCTest host and from installed dictation apps. [Local development workflow](local-development.md).

## Measured build and test results

Environment: Apple M4 Pro, 48 GB RAM, macOS 27.0.1 (`26A434`), Xcode 27.1 (`27A9275`), Apple Swift 6.4 (`swiftlang-6.4.0.34.1`), macOS SDK 27.0.

- Network resolution now works. All package checkouts match their existing locked commits: FluidAudio **0.17.5**, llama.swift **2.10549.0**, Sparkle **2.9.6**. Independently downloaded all three declared binary ZIPs and verified their SHA256 values. [Verification evidence](../../../validation/dependency-baseline/verification.json).
- `Package.resolved` remains unchanged, SHA256 `8f72a9d408f6be83aa17962e0671f0322160407f83eacf990e8b464664cf52e1`.
- `remy-build.sh resolve`, `list`, `build` (Debug), and `build Release`: **passed**. Discovered scheme: `Dictate Anywhere`; destination `platform=macOS,arch=arm64`.
- Earlier full ordinary Xcode suite: **744 executed, 57 skipped, zero failures** (before the later Escape repair; not a post-repair full-suite run). Opt-in model/hardware/provider tests were not enabled. The skipped cases do not establish runtime coverage.
- Offline harness: **33 XCTest cases, zero failures**, using normal `validation/run-tests.sh`; `--disable-sandbox` was unnecessary in this environment.
- Stable Apple Development signed Debug preparation and signature verification: **passed**. First GUI launch: **passed**, displaying Remy Dev and selected Parakeet v2. No updater feed/public key exists in the built plist; automatic-check/update flags are false, and no Sparkle controller is instantiated. No network sockets were listed in a single first-launch sample; this is not a sustained traffic audit.
- Signed preparation after the UI-copy changes and the Release UI-copy build both **passed**. [Signed build log](../../../validation/results/2026-10-19-a00/signed-final-prepare.log), [Release log](../../../validation/results/2026-10-19-a00/release-ui-copy.log).
- Xcode still warns that installed CoreDevice/CoreSimulator components differ from Xcode's expected versions. Those warnings did **not** prevent the macOS builds/tests. No Xcode reinstall or machine network change was performed.

Local command logs and metadata: [A00 results directory](../../../validation/results/2026-10-19-a00/). Xcode result bundles are under ignored `app/.build/`.

### Failures found and repaired

The first full test build found two assignments to a derived, read-only vocabulary property in the new opt-in replay harness. The harness now selects the underlying cleanup mode and restores settings. Production code was not altered for that compile error.

The first executed full suite ran 743 tests and found ten failed assertions in four startup-context tests. They assumed acoustic vocabulary support for Parakeet v3, intentionally disabled by the existing Remy storage-isolation gate. Fixtures now use supported Compact English for vocabulary lifecycle checks while preserving all prior assertions and the multilingual language checks. A new regression verifies that v2/v3/Ultra cannot invoke vocabulary preparation even when forced. The focused rerun passed **43 tests, one skipped**; the subsequent full rerun passed **744 tests, 57 skipped**.

## Reproducible commands

From the workspace root:

```sh
app/scripts/remy-build.sh resolve
app/scripts/remy-build.sh list
app/scripts/remy-build.sh build
app/scripts/remy-build.sh build Release
app/scripts/remy-build.sh test -parallel-testing-enabled NO
validation/run-tests.sh
python3 validation/dependency-baseline/verify.py
app/scripts/remy-dev.sh prepare
app/scripts/remy-dev.sh launch
```

Build/test disables signing by default. `test` launches Remy Dev as an XCTest host with production startup suppressed. The separate dev helper explicitly enables local development signing, verifies the bundle, and stages it at a stable path; launch is an explicit second action. No global installation/login item or provisioning-account change is performed. Never use the inherited `dev.sh` or release scripts unchanged as Remy workflows.

Test environment opt-ins use `TEST_RUNNER_` prefixes with this helper. Ordinary test runs must not inherit model/download/live-provider opt-ins. The legacy S1 tests can also opt in if `/private/tmp/dictate-anywhere-s1-mini-q4_k_m.gguf` exists; it was absent for this run.

## Pinned speech assets and runtime scope

Parakeet v2 conversion: `FluidInference/parakeet-tdt-0.6b-v2-coreml`, immutable revision `ee09c569f73759e6d44c9bd16766f477b2b36d39`. Explicitly downloaded and verified **22 files / 464,414,912 bytes**, including the upstream model card. [Manifest and installer](../../../validation/model-baseline/README.md). The declared conversion license is CC-BY-4.0; preserve NVIDIA/FluidInference attribution.

After the initial signed launch, installed the verified bundle exclusively at `~/Library/Application Support/app.alfred.dictation.dev/Models/parakeet-tdt-0.6b-v2-coreml` and reverified every hash. The shared FluidAudio cache was not used or modified. No optional VAD or acoustic vocabulary assets were installed. Model installation and checksum success are not inference results.

The [synthetic runtime replay](../../../validation/runtime-baseline/README.md) generates twenty public TTS fixtures and uses the actual app's installed-model recovery path, measuring preparation and two replay passes. The separately opt-in [provider harness](../../../validation/provider-baseline/README.md) uses ten public policy fixtures through the real service adapters. Neither harness substitutes for microphone/hotkey/cancel/destination checks or establishes semantic quality by merely passing.

The user selected **Chrome, Arc, Ghostty, Codex, and TextEdit** for actual insertion validation. Microphone permission was enabled. Accessibility initially remained denied in the app despite an enabled System Settings switch. The user clarified that the earlier intervention had only toggled the switch; after manually removing and re-adding Remy Dev, they reported it fixed. GUI inspection independently confirmed the permission banner disappeared and service status reads **Ready to dictate**. This narrow permission-grant refresh resolved the observed blocker; an underlying stale-grant cause is not independently proven.

Show in Finder confirmed the correct stable `.local/run/Remy Dev.app` bundle. Before quitting PID 40783, a dynamic Security API check returned success (`0`) for `guestLookup`, `runningValidity`, `diskLookup`, `requirementLookup`, and `runningMatchesStableRequirement`. The running process was valid and matched the stable bundle's designated requirement at that check. No blanket TCC reset or security workaround was applied. Both Dock and menu-bar presence are enabled. Subsequent live TextEdit microphone/insertion and cancellation observations are recorded below; Subsequent Chrome, Arc, Ghostty, Codex and focus-change observations are recorded below with their verification limits.

The user entered the cloud credential through the UI into app Keychain storage and explicitly selected `gemma4:31b` at `https://ollama.com/v1` before the signed provider test. That listed ID succeeded. **The originally saved `gemma4:31b-cloud` alias was not trialed**; discovery alone does not establish whether an unlisted alias is accepted. No automatic ID mapping is claimed. [Cloud review](../../../validation/results/2026-10-19-a00/cloud-review.json).

After initially deferring Qwen, the user freed storage and authorized installation. Ollama 0.35.1 installed `qwen3.5:4b` (4.2B, Q4_K_M), manifest digest `d8b0f5e9760cd1682034f292d7ef72ec46f432149be0df7574bf2d6e92e38c04`; all six blob hashes verified. The documented tag pull was checked against matching before/after remote digests, not an immutable pull operation. Approximately 51 GiB was available before installation. [Installation evidence](../../../validation/results/2026-10-19-a00/qwen-install.json). The earlier Gemma3 smoke remains a separate historical comparison.

### Runtime evidence collected

- Fixed an actual model-inventory bug: FluidAudio 0.17.5 `modelsExist` rewrites Remy's exact `-coreml` directory to a shortened sibling, although `loadLocal` consumes the exact path. `RemyParakeetAssets` now checks the owned exact directory, required graph directories and regular vocabulary file. All four regression tests passed, including symlink confinement and v3/fused graph differences. The inherited explicit SDK downloader's path behavior remains an A03 repair item; manual pinned installation was used here.
- All **40 file replays** (20 known Samantha synthetic utterances, two passes) completed. First preparation took **75.990 seconds**, including internal warmup and uncontrolled OS/Core ML cache state. Warm short replay p50/p95: **373/469 ms**; warm long: **1,107/1,161 ms**. These are file-recovery durations, not microphone stop-to-final latency. [Full per-fixture review](../../../validation/results/2026-10-19-a00/synthetic-review.json).
- Agent review found repeated filler/correction-cue, entity and URL defects in four distinct fixtures; one additional fixture adds currency notation requiring review. No negation reversal, changed number magnitude or lost final words was observed in this small corpus. No human/held-out quality pass is claimed.
- Core ML logged Neural Engine compilation failures caused by **“No space left on device.”** The first replay recovered; a subsequent independent model load stalled during compilation but eventually completed. The entire selected run passed **seven tests**, including cached v2 with SDK offline mode and missing optional VAD (197.767 seconds), file recovery (15.194 seconds), and the four inventory regressions. SDK offline mode does not prove system-wide blocked network egress. Preserved system error log and process stack are in the results directory. Disk availability fluctuated sharply; no compute-unit substitution or runtime upgrade was made to hide this failure.
- The test host's one-second RSS samples peaked at **2,677,520 KiB (2.55 GiB)**. This includes app/XCTest overhead and repeated model preparations, excludes Core ML helpers/Ollama, and is not an isolated ASR-memory acceptance measurement. [Resource samples](../../../validation/results/2026-10-19-a00/replay-r2-resources.json).
- Local `gemma3:4b` (digest `a2af6cc3eb7fa8be8504abaf9b04e88f17a119ec3f04a3addf55f92841195f5a`) completed ten production-adapter cleanup calls. Agent review accepted eight within scope, flagged one code-formatting policy question, and rejected one deletion of quoted “wait.” First call **9.328 seconds**; subsequent-nine p50 **1.360 seconds**. Transport success is not quality acceptance. [Provider review](../../../validation/results/2026-10-19-a00/provider-review.json).
- An automatic XCTest activation callback started global hotkey monitoring after Accessibility was granted. Automatic lifecycle callbacks now skip production activation/reopen behavior in the test host; explicit permission tests retain a direct refresh seam. Login-item mutation also skips XCTest. The focused lifecycle/permission/inventory regression run passed **15 tests, zero failures**.

### Latest signed synthetic/provider run

The selected signed suite passed **three tests, zero failures, no skips**: cloud cleanup, local Qwen cleanup, and installed-v2 replay (40 samples). Test execution totaled 32.257 seconds; the ASR test alone took 4.356 seconds. [Signed test log](../../../validation/results/2026-10-19-a00/signed-providers-replay.log). This supplements, rather than replaces, the earlier low-disk evidence above.

Before those tests started, the signed XCTest host spent approximately 145 seconds in a dyld file-open wait and then recovered. The captured stack does not establish a cause; no process was killed or security setting changed to recover it. [Startup stack](../../../validation/results/2026-10-19-a00/signed-test-host-sample.txt). One-second host-only RSS sampling peaked at 213,472 KiB (208.5 MiB); it excludes Core ML helpers/Ollama and is not an isolated ASR memory result. [Samples](../../../validation/results/2026-10-19-a00/signed-providers-replay-resources.json).

- Signed v2 preparation: **363 ms**. Initial short replay p50/p95 **44.687/53.134 ms**, warm short **43.792/47.681 ms**; initial long **145.110/175.383 ms**, warm long **145.174/168.765 ms**. Each group has only ten samples. Timed replay sum was 3.852 seconds. These are file-recovery measurements, not live stop-to-final latency. After storage was freed, preparation and replay were substantially faster than the earlier run with compilation/disk errors. Signing, caches, available storage and workload differ; no single cause or source optimization is established. [Signed ASR review](../../../validation/results/2026-10-19-a00/synthetic-signed-review.json).
- All 20 ASR outputs repeated identically across the two signed passes. The same filler/correction-cue, Xway/entity and malformed spoken-URL defects persist; added currency notation still needs review. No negation reversal, changed numeric magnitude or lost tail was observed in this limited corpus. Agent review is not user acceptance or held-out human-speech evaluation.
- Qwen cleanup: **8/10 acceptable within scope**, one rejected because it retains abandoned Thursday instead of resolving to Friday, and one unresolved `foo underscore bar` → `foo_bar` policy question. It preserves quoted “wait,” unlike the earlier Gemma3 result. All-call p50/p95 **1.604/5.605 seconds**; subsequent-nine **1.577/1.740 seconds**. Reasoning was disabled when supported. [Qwen review](../../../validation/results/2026-10-19-a00/qwen-review.json).
- Cloud `gemma4:31b`: **9/10 acceptable within scope**, with the same code-dictation policy question. Friday resolves correctly and quoted “oh wait” survives. All-call p50/p95 **484/2,406 ms**. Its portable API exposes no immutable model digest and the service has no explicit reasoning toggle. [Cloud review](../../../validation/results/2026-10-19-a00/cloud-review.json).

Provider timing includes real service metadata/adaptation/generation work and possible loading; subsequent calls are not a controlled residency benchmark. Ten known public seeds cannot establish the held-out quality target or select a model. Local loopback and declared routing checks do not prove blocked external egress. No live capture, delivery, deadline fallback or A00 closure follows from these passing tests.

### Live capture, TextEdit insertion and Escape repair

[Live-check evidence](../../../validation/results/2026-10-19-a00/live-checks.json) records the actual MacBook Pro microphone route and user-confirmed Right Option **Tap to Toggle** gesture. Two controlled TextEdit 1.21 trials succeeded, with automatic text appearance reported by the user and complete destination text independently inspected:

- Plain-text raw ASR inserted “Remy check one two three do not delete the final blue folder” once as observed, retaining negation and final words; punctuation was omitted.
- Rich-text cloud cleanup (`https://ollama.com/v1`, `gemma4:31b`) inserted “Book Friday at 3. Do not change the $15,000 budget.” The Thursday-to-Friday correction, negation and numeric magnitude survived.

Escape cancellation initially kept the document empty but produced the macOS alert sound, first while held and then on a single tap after the user disabled hold-to-cancel. The plain Escape event was reaching the foreground app. A narrow `HotkeyService` repair now consumes matched immediate Escape and its repeats/release, while preserving idle Escape and hold-mode passthrough; `CancellationTests` cover that behavior. **36 focused signed CancellationTests/HotkeyTests passed, zero failures**, followed by successful signed Debug preparation and Release build. [Focused tests](../../../validation/results/2026-10-19-a00/escape-cancel-tests.log), [signed prepare](../../../validation/results/2026-10-19-a00/escape-cancel-signed-prepare.log), [Release](../../../validation/results/2026-10-19-a00/escape-cancel-release.log). The earlier 744-case full suite predates this repair; no new whole-suite pass is claimed.

After rebuilding/relaunching, the user confirmed an actual repeat trial cancelled **immediately and quietly** with a single Escape tap, and independent destination inspection confirmed the TextEdit document stayed empty. Accessibility remained recognized through the signed tests/rebuild/relaunch. Preferred settings are Right Option Tap to Toggle, immediate Escape, cloud cleanup, vocabulary `Ixway`/`BEGL`, context capture off and local filler removal off.

Chrome raw **hold-to-record** capture/insertion subsequently worked in both selected fixture fields. Observed primary-field output was “Chromefield One keeps the final green word”; contenteditable output was “Chrome Field 2 does not submit anything.” The user confirmed dictating into each selected field and retrying, with no manual paste/edit. This supports actual hold capture and browser insertion, including the observed negative sentence; it does not prove verbatim fidelity without an exact spoken reference. Primary counters recorded paste=2/input=3 and editable paste=1/input=1. Because retries occurred, these totals cannot establish one dispatch per attempt or duplicate-dispatch absence.

The user reported the **whole floating recording panel was missing** during Chrome capture. Insertion/hold capture worked, but the recording overlay remains unresolved and under investigation. Runtime settings were then restored and accessibility-inspected: Right Option **Tap to Toggle**, Custom/OpenAI-compatible cleanup at `https://ollama.com/v1` with `gemma4:31b`, immediate Escape with **Hold to cancel off**. The temporary hold/raw configuration is no longer the active preference.

A narrow historical Performance query recovered three app-reported successful Chrome raw delivery intervals: `dictation.stopToInsertion` **93/81/87 ms**, paired with `dictation.requestToRecording` **175/98/152 ms**. A fourth session reported `noText` after 18 ms (request-to-recording 147 ms). The stop interval runs from `finishDictation` entry through the awaited `TextInserter` result; it is not independently timed visual acknowledgement. Sessions are aggregate and cannot be mapped individually to the fixture fields. No controlled distribution, cold/utterance-duration benchmark or acceptance claim follows. [Timing scope and samples](../../../validation/results/2026-10-19-a00/chrome-live-timings.json).

The user also reproduced the missing panel with Right Option **Tap to Toggle** in windowed Chrome. Remy displayed **Listening**, confirming the recording state, while the whole floating panel remained absent. The user cancelled with Escape and **Ready to dictate** was independently verified afterward. A stable signed Debug diagnostic preparation and Release build both passed ([prepare](../../../validation/results/2026-10-19-a00/overlay-diagnostic-prepare.log), [Release](../../../validation/results/2026-10-19-a00/overlay-diagnostic-release.log)). This adds only bounded numeric `overlay.presentation` instrumentation; original presentation behavior is unchanged. The subsequent diagnostic trials are recorded below; this diagnostic-only build did not itself change presentation behavior.

The user subsequently reported the panel appeared only while Remy itself was active. [Native presentation diagnostics](../../../validation/results/2026-10-19-a00/overlay-presentation-before.log) captured four Chrome trials with `nativeVisible=1`, `alpha=1000` and frames inside the chosen Mac display, but `on_active_space=0` and `occlusion_visible=0`. A trial with Remy active on the external display reported both latter fields as `1`. These observations point to native presentation/Space visibility rather than merely translucent styling; they do not independently prove every window-server cause.

The focused repair replaces the overlay window with `DictationOverlayPanel: NSPanel`, adds `.nonactivatingPanel`, keeps it unable to become key/main, sets `hidesOnDeactivate=false` and `canHide=false`, and adds `canJoinAllApplications` alongside the existing Spaces flags. AppDelegate's main-settings-window lookup now excludes panels, floating and borderless windows so it cannot adopt the overlay as the main window. At the user's request, five visual source files also give the panel opaque navy/cream surfaces, a teal waveform/status, a small robot-head MenuBarIcon with Listening header, a warm status dot and rounded status font; size calculations account for the header.

**43 focused tests passed, zero failures**, covering AppDelegate, appearance, overlay content, screen-session and native-window session behavior. Mounted real SwiftUI pixel-coverage tests exercise light/dark, preview on/off and hide/show reuse; listening proofs in `.local/design/overlay-proof/` were visually reviewed in both appearances. [Focused results](../../../validation/results/2026-10-19-a00/overlay-service-tests-r2.log). The first test compile rejected an assignment to read-only `accessibilityReduceMotion` environment state; the test now uses a disabled-animation transaction, and the second run passed. This is focused verification, not a new full-suite result.

Signed Debug preparation and Release validation for this panel repair both **passed** ([prepare](../../../validation/results/2026-10-19-a00/overlay-service-prepare.log), [Release](../../../validation/results/2026-10-19-a00/overlay-service-release.log)). Settings were visually inspected in Compact, Comfortable and Large modes without clipping, then restored to Comfortable.

The user confirmed the repaired panel was visible over Chrome and the inserted text was correct. Independent inspection of the third fixture field read “The teal waiter keeps the final blue word”; its counters incremented paste 1→2 and input 2→3 during the observed trial. Three newest presentation rows from PID 67384 reported `on_active_space=1` and `occlusion_visible=1` on the same primary display, frame x=710/y=6/width=380/height=177. The earlier height was 154; the added header intentionally increases it. [After-repair presentation evidence](../../../validation/results/2026-10-19-a00/overlay-presentation-after.log). This verifies the repair in one actual Chrome use case, not the full Spaces/display/application matrix.

The user subsequently confirmed insertion and visible overlay in both Arc textarea and contenteditable cases. Exact Arc text remains unverified: native computer-use capture failed after an initial no-window result. In Ghostty the user reported visible overlay and one unexecuted cloud-cleaned input for the deliberate sample “Terminal check one two three”; in Codex they reported visible overlay and an unsent draft for “Please keep this as an unsent draft.” Native control of those two apps was blocked by tool safety; no bypass was attempted. These are user-reported observations, not agent-read exact destination values.

A fresh Chrome focus-change trial exposed a delivery-contract failure. Recording began with Right Option toggle in field 1; the user clicked field 3 while recording and stopped. Independent inspection found field 1 empty with zero paste events, and field 3 containing “Keep the final violet word.” with one paste and one input event. **Fail against the planned A07 same-field/copy-only contract:** output followed the new field rather than falling back to copy-only. This is a concrete repair requirement, not a compatible focus-change pass.

The user confirmed the final Ghostty raw trial produced one unexecuted line, then restored cloud cleanup. Exact terminal text remains unread by the agent, as with the earlier Ghostty cloud and Codex draft observations. Independent settings inspection confirmed **Ready to dictate**, Custom/OpenAI-compatible `https://ollama.com/v1` / `gemma4:31b`, with context capture and local filler removal off. The owned local fixture server (PID 80279) was stopped and two agent-created Chrome fixture tabs were closed. The broader destination matrix and A07/A10 reliability remain unpassed.

### Visual redesign

Implemented the requested original robot-waiter mascot and shared cream/navy/teal design system, rounded system headings, tactile cards/controls and service-status sidebar. [Design and artwork provenance](design-direction.md). Both redesigned Debug and Release builds passed; design/render/appearance tests passed in a 48-test selected run (three unrelated ASR skips). The updated signed app launched and dark appearance was visually inspected: mascot, navigation, section/card hierarchy and status/permission banners render without visible clipping. The light General page was subsequently inspected without visible clipping, then System theme was restored. The final Release build after inventory/lifecycle repairs and the later Release UI-copy build also passed; the latest signed final preparation passed. Dock and menu-bar presence are enabled. The subsequent app-icon request is implemented: a matching robot-waiter close-up icon and original monochrome robot/bowtie menu template, with reproducible asset generation. All icon sizes were visually reviewed on light/dark surfaces. Signed Debug preparation and Release passed (`icon-signed-prepare.log`, `icon-release.log`); the updated stable app reopened and Finder displayed the new icon. The later permission-grant refresh removed the Accessibility warning. This is a visual pass alongside A00, not completion of A08's history/profile/session requirements.

## A00 foundation decision and repair scope

**Written decision: RETAIN / REPAIR the imported foundation.** Pinned builds, isolated signing/storage, 20 distinct synthetic short/long utterances (40 real model replays), actual toggle/hold/cancel use, five destination-app observations with differing evidence strength, and real selected cloud/local cleanup support continuing from this implementation. They do not justify a replacement architecture. Preserve the working ASR/provider adapters and repaired hotkey/overlay paths while implementing the explicit contracts below.

**A00 foundation gate: closed with RETAIN / REPAIR. A01 and A02 local implementation gates: complete.** Raw and cloud Ghostty insertion are user-confirmed as unexecuted input; exact text was not independently read. Arc insertion/overlay and Codex unsent-draft/overlay evidence likewise remain user-reported with exact text unread. The independently observed Chrome field-change failure is an explicit retained A07 repair item, not a waived daily-driver safety pass. The original saved cloud alias remains untested; the current user-selected `gemma4:31b` was tested verbatim.

The estimates below are preliminary focused engineering person-days including relevant regression checks, not delivery promises or completed work. They may overlap after A02 and need revision when implementation exposes dependencies.

| Work item | Concrete retained repair scope | Estimate |
| --- | --- | --- |
| A01 | Disable unsupported inherited routes; maintain independent configuration/identity; audit transcript/secret logging and add pinned build/check CI | 2–3 days |
| A02 | Session snapshots/state transitions, cancellation/late-event ownership and explicit finalization/delivery contracts | 2–4 days |
| A03 | Explicit versioned model installation, integrity/owned-path lifecycle, including inherited SDK path behavior and optional assets | 3–5 days |
| A04 | Durable raw/final/attempt/delivery history, migrations and failure-visible commits | 3–5 days |
| A06 | Provider deadline/fallback/output policy; Qwen Thursday correction failure, quoted-word preservation and explicit code-dictation policy | 3–5 days |
| A07 | Focused-field identity/copy-only fallback demonstrated necessary by Chrome; secure-field and terminal/multiline/no-Return guards with per-attempt evidence | 3–6 days |
| A09 | Monotonic stage metrics, controlled cold/warm runs, representative/held-out quality and isolated resource measurements | 2–4 days |
| A10 | Fault injection, cancellation/restart races, 1,000 lifecycle cycles and eight-hour mixed-use validation with real destination checks | 4–7 days plus soak elapsed time |

The foundation gate answers whether to retain this codebase with a bounded repair plan; it is separate from daily-driver/release acceptance. Larger held-out/critical sets, ten cold launches, 100 warm samples, externally blocked-egress checks, complete destination/fault matrices and 1,000-cycle/soak evidence have **not passed**. They remain required at the relevant A06/A07/A09/A10 gates rather than being inferred from this small baseline. Preserve all raw results and failed scenarios for regression/review. No replacement of the user's installed dictation tool or distribution approval follows from RETAIN / REPAIR. Model-specific quality defects, uncontrolled OS/Core ML cache effects, limited cold-launch sampling, and host-only/nonisolated hardware-resource measurements remain as recorded above.

Known baseline limits remain: separate-model acoustic vocabulary for Parakeet v2/v3/Ultra is gated because FluidAudio 0.17.5 internally uses shared CTC storage. Compact English optional vocabulary preparation can still download scoped assets; full explicit model lifecycle is A03. Durable SQLite history is not implemented; inherited history behavior remains. A07 terminal/secure-field/output invariants, A10 soak/reliability and distribution validation have not passed.

The earlier restricted session's DNS failures and 33-test offline-only result are historical, superseded by the concrete build results above. Its original logs remain under `validation/results/2026-10-19-*` and the checkpoint. No earlier source-review claim has been converted into an unmeasured runtime pass.


## A01 local implementation gate complete

A01 now enforces local Parakeet/Apple speech selection in settings, UI and runtime;
AssemblyAI recognition is unavailable even through direct engine calls. Explicit
local/cloud cleanup remains supported. The original A01 gate disabled production
dictation-context gathering/sharing independently of retained preferences. The
user requested restoring app-aware cleanup on 2026-10-20; that policy is now
superseded by enabled context capabilities with separate remote-text opt-in and
frozen per-recording consent. See [restoration evidence](../../../validation/results/2026-10-20-context-awareness/README.md).
Direct navigation to the removed cloud prompt page falls back to models.

A configuration **version 1 marker only** is checked before SwiftUI constructs
AppDelegate/Settings. Unsupported or malformed versions stop startup before
legacy migrations, credential reads or login work. The one-key marker migration
is not durable history migration, provider profiles or a transactional settings
store. Startup observes login registration; only the explicit Launch at Login
setting setter registers/unregisters the current build, and XCTest suppresses
that mutation.

Diagnostics use Remy-owned subsystem naming and bounded error/status labels
instead of transcript, provider error bodies and arbitrary paths. This reduces
known source-log exposure, not a comprehensive system/framework traffic or secret
scan. Root push/PR/manual CI uses read-only permissions, pinned checkout/packages,
ordinary tests, offline checks and narrow distribution-input hygiene. Inherited
release publication is disabled. Hosted Xcode 27.1/SDK 27.0 availability and a
hosted end-to-end CI run remain **unverified**; incompatible images fail preflight
and a compatible local fallback is documented. Source/storage/credential namespace
isolation is checked; actual simultaneous operation with the upstream app has
not been tested.

Latest A01 ordinary suite: **776 tests, 59 skipped, zero failures**
([log](../../../validation/results/2026-10-19-a01/ordinary-suite.log)). Opt-in live,
model and provider skips do not establish coverage. The offline harness passed
**33 cases**, and local read-only pin/toolchain/hygiene checks passed across **130
selected text inputs**. Existing package pins remain unchanged.

Two earlier attempts are retained: the first focused run aborted after a new
`@Observable` didSet assigned its own property even when unchanged, producing
recursion; assignment is now conditional. The second ran 150 tests with two skips
and failed one outdated cloud-routing expectation; the fixture now uses cleanup
`.none`, matching the supported local route being tested. The subsequent full
ordinary suite passed. [First log](../../../validation/results/2026-10-19-a01/foundation-tests-r1.log),
[second log](../../../validation/results/2026-10-19-a01/foundation-tests-r2.log).

A clean-checkout hazard was found in inherited `app/.gitignore`: broad workspace,
`Package.resolved` and `*.xcconfig` ignores could omit the package lock and required
shared configuration. Those rules now retain required workspace/lock/shared configuration and ignore
user workspace data/local signing overrides. No Git initialization or publication is
part of this repair.

**A01 local implementation gate: complete.** Signed Debug preparation and Release
both passed ([signed preparation](../../../validation/results/2026-10-19-a01/signed-prepare.log),
[Release](../../../validation/results/2026-10-19-a01/release.log)); the prior stable
copy remains at `.local/run/.remy-dev-backup.JAKg1T/`. Native accessibility UI
inspection confirmed **Ready to dictate**, no Accessibility banner, a local-only
FluidAudio/Apple Speech menu, selected Parakeet v2/English, and absent context
controls. User settings survived: Custom/OpenAI-compatible `https://ollama.com/v1`
/ `gemma4:31b`, filler removal off, vocabulary `Ixway`/`BEGL`, Right Option Tap to
Toggle and immediate Escape (Hold to cancel off). [UI observations](../../../validation/results/2026-10-19-a01/ui-checks.json).
No new dictation, credential read/export or live delivery pass is claimed from
this inspection.

The recorded [foundation preflight](../../../validation/results/2026-10-19-a01/foundation-preflight.log)
passed **130 selected text inputs**, exact pins/toolchain and source updater/ID
checks. CI is authored, not remotely executed; hosted exact-toolchain availability
and actual simultaneous upstream operation remain unverified. No Git repository
or release was published. This local gate does not close A07’s Chrome field-change
failure or subsequent model/history/provider/performance/reliability/distribution
gates. **A02 is now in progress**; no completed A02 behavior is claimed here.

## A02 local implementation gate complete

The inherited app now runs foreground dictation through a Remy-owned
session/generation coordinator. Each recording captures immutable recognition,
cleanup, vocabulary/filler and audio/recovery settings. Credentials remain inside
a private runtime closure; the configuration contains only an ephemeral reference.
Duplicate activation/finalization and late recognition/cleanup results cannot
acquire a new delivery permit. A worker-safe revocable lease checks actual
clipboard/paste effects; cancellation after copy blocks paste, while an already
claimed paste is truthfully treated as possibly dispatched.

Sleep invalidates held and queued gestures, cancels eligible foreground work and
requires a fresh activation after wake. Caps Lock's latched state does not block
rearming. Busy starts display a short notice in the existing overlay. Apple
Speech generation checks and Parakeet post-inference cancellation guards reject
stale transcript publication. The reviewed shared interfaces and A03–A07 limits
are documented in [session contracts](../../../docs/engineering/session-contracts.md).

Full ordinary run r2 passed **831 tests, 59 skipped, zero failures**; final focused
hotkey/cancellation run passed **41 tests**, and the offline harness passed **33**.
Signed Debug preparation and Release passed. Exact pins/toolchain and narrow
hygiene passed across **134 selected text inputs**. [Evidence and failed-run
explanations](../../../validation/results/2026-10-19-a02/README.md). The initial
full run caught legacy fixtures bypassing the coordinator, split trace IDs, and a
real cancellation-recovery regression. The repair retains a completed decoder
checkpoint for its same cancelled session's optional recovery without authorizing
ordinary history or delivery. The next session cannot reuse the engine until that
work has joined. All original checkpoint assertions now pass.

The stable signed app is open at `.local/run/Remy Dev.app`, prior A01 bundle
retained at `.local/run/.remy-dev-backup.Rmsuru/`. Native inspection confirmed
Ready to dictate with no Accessibility warning and preserved Right Option toggle,
immediate Escape, cancellation retention off, Custom/OpenAI-compatible
`https://ollama.com/v1` / `gemma4:31b`, Ixway/BEGL and filler removal off. No credential
read/export occurred. The cloud TextEdit smoke passed with independently observed
`Move the meeting from Thursday to Friday. Do not change the $15,000 budget`.
The subsequent raw check produced only `Raw session check seven eight nine`; the
user confirmed immediate quiet Escape cancellation first left the document empty.
Cloud cleanup was restored and independently verified. [Live evidence](../../../validation/results/2026-10-19-a02/live-checks.json).

**A02 local implementation gate is complete; A03–A05 may proceed against the
reviewed interfaces.** Sleep and Fn/chord behavior
have deterministic coverage; physical sleep/wake and the complete hardware hotkey
matrix are not claimed. A coordinator timeout test proves revoked authority
rejection, not an implemented provider deadline (A06). Current raw/history state
is not durable SQLite/transactional acknowledgement (A04), the credential reference
is not a persistent named profile (A05), and A07's observed field-change failure
remains open. No later quality/reliability/distribution gate or publication follows
from these checks.

## A03–A05 first integrated slice

The manifest-backed v2 store, durable history path and bounded HTTP cleanup are
implemented. Full ordinary **930 tests, 59 skipped, zero failures**, offline **33**,
final history/delivery **31** and exact pin/toolchain/hygiene **147 inputs** passed.
Signed Debug and Release passed. Stable app verified/adopted the existing v2 assets
without download, preserving originals, and reached Ready to dictate with no
Accessibility warning. New History storage controls rendered without clipping.
Cloud settings/vocabulary and Right Option/immediate Escape are preserved.

An interrupted inherited shared-identity test cleared the legacy history list;
no verified backup was available, and history was not recovered. User was informed.
Tests now require their own app/preferences/storage/credential identity before
Settings constructs. Confirmed model/provider/vocabulary settings were restored.
See [incident, runs, implementation and open gates](../../../validation/results/2026-10-19-a03-a05/README.md).
No A03–A05 acceptance completion or publication is claimed. The first-slice live
cloud/history smoke passed: TextEdit, History final and raw all showed `Schedule
Friday at 3. Do not change the $15,000 budget`. History survived clean restart;
TextEdit still contained one copy. Named-profile integration is now being validated;
see the same evidence index for the later suites and remaining limits.


### Named cleanup profiles integrated

Saved Profile now uses a private versioned file repository, endpoint-scoped
Keychain references, explicit migration/editor, and an immutable runtime snapshot.
Current live selection is **Saved Profile → Ollama Cloud** (`https://ollama.com/v1`,
`gemma4:31b`). Explicit conversion preserved the original prompt/settings/key;
synthetic connection checks passed before and after restart without new key entry.
Key values were never read/exported by the agent. The development backend uses
file-based macOS Keychain with creator-only ACLs because this signed build is
unprovisioned; no entitlement or permission workaround occurred.

Full ordinary965/59skips/0 failures, later integration32/0, durability13/0 and
final Keychain/runtime/controller/integration27/0 checks passed. Offline33 and
pin/hygiene151-input checks passed. Final signed Debug and Release pass. Profile
fields are visibly labeled, errors are bounded, uncertain commits stay unconfirmed,
and stale connection checks cannot certify an edited draft. New live profile
check is pending in TextEdit Untitled4. A03–A05 acceptance and later gates remain
open; details in the same [evidence index](../../../validation/results/2026-10-19-a03-a05/README.md).

### Source-ahead A04/A06/A07 continuation

History deletion warning/retry, foreground cleanup deadline/use-raw/drain ownership,
remote single-request input policy and field/clipboard delivery guards are now in
source. Signed test compilation passes; 40 offline tests pass, including the
new deadline primitive. The isolated Xcode suite and new live destination checks
are pending. Running stable app remains the A05 profile build, with its TextEdit
Untitled 4 live check pending. See the current [evidence and limitations](../../../validation/results/2026-10-19-a03-a05/README.md#source-ahead-checkpoint-history-deletion-cleanup-deadline-and-paste-guards).
No new phase acceptance or distribution is claimed.

### Custom sounds, long recovery and A04/A06/A07 staged continuation

The source-ahead slice is now staged in signed `.local/run/Remy Dev.app`, together
with independent imported start/completion sounds and long-dictation recovery
repairs. Final signed Debug and Release builds pass. Ordinary isolated suite:
**1,033 tests, 60 opt-in skips, zero failures**. Offline40 and exact pin/toolchain/
narrow hygiene155-input checks pass. [Evidence and limits](../../../validation/results/2026-10-19-long-audio-sounds/README.md).

One 22m25.4s synthetic `say` file replay through installed pinned v2 passed with
3.38% simple WER and complete final tail. Two of25 exact checkpoints had homophone
spellings, with both sections present; found checkpoints were ordered. Decode3.650s,
preparation14.162s, process peakRSS1.17GiB. Source audio and raw output are retained
for rerun. This does not establish live microphone/route/pressure endurance.

History-enabled capture now uses temporary recovery audio. Earlier/final ASR errors,
overflow and final writer errors preserve recoverable material instead of accepting
incomplete previews. Metadata-write failure no longer deletes CAF; valid orphan
CAFs are reconciled and normal shutdown drains queued tails. HistoryOff remains
disk-audio-free; intentional cancellation still follows its separate preference.
Audio retention24h; quota, crash, duration and live-capture gates remain open.

Native UI verified custom WAV import/preview/restart; restored original Tink/Pop,
masterOff/volume30%. Cloud saved profile and model/hotkeys remain, connection check
passes, installed v2 prepared/Ready with no Accessibility warning. Live Chrome
unchanged-field and changed-field checks are pending; no new A07 runtime pass yet.
Git connection task added near the end; no repository publication or remote mutation.

Follow-up: the user confirmed “the Book Friday 2 test passed as expected.” The
earlier Friday booking/saved-profile live smoke is now passed by user report;
exact output/counters were not re-inspected. The separate Chrome field-change
copy-only check remains pending.

### Native paste guard correction confirmed in TextEdit and Terminal

User testing found normal TextEdit/Terminal dictation falling back to Command+V.
Three logged TextEdit attempts showed a writable AXTextArea with optional AXEnabled
absent. The initial guard incorrectly demanded enabled==true. The signed repair
accepts absence while retaining explicit disabled/read-only/protected/secure,
identity, clipboard and newline checks. 27 focused tests, signed Debug, Release and
pin/toolchain/hygiene checks pass. Repaired TextEdit automatic paste is confirmed
by user and independently observed as one “The waiter keeps the blue folder”.
Capture/recheck/AX-paste logs agree. Terminal automatic insertion/unexecuted-line
check also passed by user report with bounded dispatch-log corroboration; terminal
contents were not read. Chrome focus-change remains pending.
See [paste investigation](../../../validation/results/2026-10-19-paste-guard/README.md).

### Configurable destination direction

The previous Chrome guard trial is now user-confirmed and independently verified:
one primary paste, other fields empty, recovery phrase absent. User then requested
General → Text destination with original-field restoration as default and finish-
time focus as an alternative. Source now implements per-session preference capture
at the start gesture, bounded revocable original-focus restoration, and one-time
finish target capture. Failed restoration never silently retargets. Target/secure/
terminal/clipboard safeguards remain. Full isolated ordinary suite passes1,048tests,
60skips,0failures; initialfocused55/0 and pins/toolchain/hygiene156 pass. Signed Debug staging and Release pass; native settings layout and restart
persistence pass. Original-field default restored. Both real routing trials remain pending. See
[destination evidence](../../../validation/results/2026-10-19-destination-setting/README.md).
This supersedes unconditional focus-change copy-only as the intended default;
full A07 matrix and broader A03–A11 acceptance remain open.
