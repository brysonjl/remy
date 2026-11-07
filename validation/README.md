# Offline utility validation

This is a separate, dependency-free SwiftPM harness for a deliberately selected
subset of the imported macOS app. It is **not an application build or a run of
the full Xcode test target**. The application still requires its original pinned
FluidAudio, llama.swift, and Sparkle dependencies and binary artifacts; see
[build prerequisites](../docs/research/build-prerequisites.md).

Run from the repository root with the installed Xcode toolchain:

```sh
validation/run-tests.sh
```

In the earlier restricted session SwiftPM's nested manifest sandbox failed with
`sandbox-exec: sandbox_apply: Operation not permitted`. The run here used:

```sh
validation/run-tests.sh --disable-sandbox
```

That flag disables SwiftPM's subprocess sandbox; it does not change the outer
workspace restrictions. This package has no dependencies, plugins, build scripts,
model downloads, provider calls, or GUI launch. All build, module, configuration,
and SwiftPM cache paths are under ignored `validation/.build/`. Netrc and Keychain
lookups are disabled. The continuation run on this Mac passed all 33 cases using the first command
without `--disable-sandbox`; use that normal command by default. An optional filter can be appended, for example
`--filter TranscriptCleanupPlanTests`.

## Current release-preparation result (2026-10-23)

`validation/run-tests.sh --disable-sandbox` passes **45 XCTest cases, zero
failures**: the original 33 utility/storage cases, eight cleanup-attempt cases,
and four stored-history deletion cases. The normal command encountered the
nested SwiftPM `sandbox_apply` denial in the current restricted environment;
the fallback flag changed no source or test assertions. The historical results
below describe earlier snapshots and do not replace this current count.

The cleanup harness links the production `RemyCleanupFailure.swift` enum and
Foundation-only classification alongside the deadline primitive. Provider and
FoundationModels classification lives in the production
`RemyCleanupFailureClassification.swift` extension, with its unchanged
provider-specific tests in `RemyCleanupFailureClassificationTests.swift` in
the full Xcode target. AppState classifies runtime errors at the operation
boundary before handing them to the dependency-free primitive. No provider
stubs or conditional substitute implementations are used by this harness.

Stored-history deletion tests use temporary directories to check artifact
removal, safe path boundaries, and failure behavior. They do not access user
history, exercise the live Clear All UI, or replace full AppState integration
tests. No hardware/model/provider or public release validation is implied by
these 45 offline tests.

## Source provenance and coverage

Every `.swift` file under `Sources/Dictate_Anywhere` is a relative symlink to the
real production file under `app/Dictate Anywhere`. Every test under
`Tests/OfflineValidationTests` likewise links directly to
`app/Dictate AnywhereTests`. There are no copied implementations or substitute
app types. The target module keeps the inherited `Dictate_Anywhere` name so the
original `@testable import` statements remain unchanged. Source edits are
immediately reflected on the next run; retain symlinks when checking out or
archiving this harness. The import's upstream revision is
`e420448de6e2099d33e050a4b419722032fd7519`.

The production target uses Swift 5 language mode, macOS 14 minimum, and default
MainActor isolation, matching the relevant application target settings. SwiftPM
provides its own XCTest discovery and test runner; this is not a reproduction of
every Xcode build setting. Apple Foundation, Accelerate, and `os` are supplied by
the local macOS SDK. No external framework is stubbed.

| Inherited suite | Cases | What it checks |
| --- | ---: | --- |
| AudioSampleBufferTests | 1 | FIFO consumption/compaction retains all 160,100 synthetic samples in order |
| AudioVolumeGateTests | 4 | Incremental windows and tail match the recording scan; quiet ending retains speech; one hour of synthetic silence has bounded storage |
| CJKTextTests | 7 | Han detection and punctuation/bracket handling |
| TimedRequestCacheTests | 4 | Expiry/eviction, consumer cancellation, failed refresh, invalidation of late old work |
| TranscriptCleanupIntegrityTests | 1 | Numeric literal preservation, repetition, width folding and formatting |
| TranscriptCleanupPlanTests | 11 | Lossless chunk/paragraph boundaries, cancellation, bounded tokenizer work, nonmonotonic fit checks, response completion validation |

The new `RemyStoragePathsTests` suite adds 5 cases for development/release/shared
cache separation, malformed bundle identities, root/sibling/traversal rejection,
real model deletion with parent pruning and sentinel preservation, and symlink
escapes including replacement of the model root or its application-directory
ancestor before the model root exists. Its fixtures are temporary
directories, not the user's actual Application Support or shared model caches.

The full engine-bound merge, audio pipeline, and history persistence suites are
excluded because they require full app types and/or external frameworks. No
microphone recording, speech recognition, model loading, permission behavior,
real paste, cleanup-provider request, signing, or release packaging is tested by
this harness. The volume gate cases process synthetic PCM only.

## Recorded result

On 2026-10-19, Xcode 27.1 (27A9275), Apple Swift 6.4
(`swiftlang-6.4.0.34.1`), arm64 macOS SDK 27.0: the selected production sources
compiled and **33 XCTest cases passed, 0 failures**: 28 inherited utility cases
and 5 new storage-path cases. The command, all test names, individual results,
and exit status are in [the final run log](results/2026-10-19-offline-tests.log).
The additional Swift Testing runner reports 0 tests because these tests use
XCTest; that line does not replace the XCTest count.

The first integrated storage run found one failure in
`testDevelopmentReleaseAndUpstreamDataAreIndependent`: a legitimate model path
was rejected before its directory existed because URL resolution changed the
root's trailing slash. The storage implementation was corrected to compare path
components, and the same test passed alongside the symlink rejection tests. The
[initial failure log](results/2026-10-19-storage-initial-failure.log) is retained
to document the finding. No assertions were removed or weakened.

A later assertion for a symlink replacing the application-directory ancestor
before `Models` exists also found that Foundation could leave an unresolved
ancestor in a nonexistent path. The final implementation checks path components
for symlinks through the injected Application Support boundary, including
dangling links and missing tails. The bounded scan accepts the operating
system's `/var` alias above that trusted boundary. Final tests include ancestor
replacement, dangling links, and symlink-plus-parent traversal assertions and
all 33 cases pass. The [ancestor failure](results/2026-10-19-storage-ancestor-symlink-failure.log)
and [intermediate unbounded-scan regression](results/2026-10-19-storage-ancestor-guard-regression.log)
are retained alongside the final successful run. These findings changed the
path guard; no failing assertions were removed or weakened.

## Full-app and supplementary runtime evidence

The separate pinned Xcode app now builds in Debug and Release; the ordinary suite
passed 744 tests with 57 intentional skips, and the continuation offline run
passed 33 cases without `--disable-sandbox`. Latest signed final preparation and
Release UI-copy builds passed. These results do not change this utility harness's
coverage. See the [engineering baseline](../app/docs/engineering/baseline.md) and
[A00 logs/reviews](results/2026-10-19-a00/).

The explicitly opt-in [runtime replay](runtime-baseline/README.md) and
[provider smoke](provider-baseline/README.md) use actual app services and public
synthetic fixtures. The latest signed suite passed three tests with no failures
or skips: 40 installed-v2 file replays plus ten Qwen and ten cloud cleanup calls.
The ASR test took 4.356 seconds, including 363 ms preparation; warm short/long
file-replay p50 values were 43.792/145.174 ms. Earlier low-disk Core ML compilation
errors and much slower preparation/replay remain preserved. Storage, cache,
signing and workload differences confound causal comparison; neither result is
live microphone stop latency. [Signed replay review](results/2026-10-19-a00/synthetic-signed-review.json).

`qwen3.5:4b` was installed after explicit authorization and available-space checks;
its recorded manifest digest and all six blobs verified. The tag pull was checked
before/after rather than immutable. The ten-seed agent review found eight
acceptable outputs, one unresolved code policy transformation, and one rejected
failure to resolve Thursday to Friday. Quoted “wait” was preserved, improving on
the prior Gemma3 deletion. [Install evidence](results/2026-10-19-a00/qwen-install.json),
[Qwen review](results/2026-10-19-a00/qwen-review.json).

The user selected cloud `gemma4:31b` at `https://ollama.com/v1` before testing;
that listed model completed all calls with nine acceptable agent ratings and one
code-policy question. The original saved `gemma4:31b-cloud` alias was **not
trialed**. No alias rejection, silent ID mapping, immutable cloud digest or user
semantic acceptance is claimed. [Cloud review](results/2026-10-19-a00/cloud-review.json).

The app still reports Accessibility denied despite an enabled System Settings
switch; toggling/relaunching did not resolve it. Show in Finder confirmed the
correct stable signed app path. Dock and menu-bar presence are enabled. Light
visual QA, actual microphone/hotkey/cancel and insertion into Chrome, Arc, Ghostty,
Codex and TextEdit remain pending. Passing small known synthetic tests cannot
select a cleanup model or close A00; persistent ASR entity/correction/URL defects
and provider quality questions remain recorded for follow-up.

## Cleanup deadline primitive extension

The harness now also links the real `RemyCleanupAttempt.swift` and its seven
deterministic tests. They exercise one-shot decisions, the eight-second boundary,
noncooperative worker draining, late completion, cancellation and provider failure.
The latest run passed **40 tests, zero failures** without a GUI host or provider
network request: [deadline offline run](results/2026-10-19-a03-a05/deadline-offline-r2.log).
This does not execute AppState integration or real paste/provider tests.
