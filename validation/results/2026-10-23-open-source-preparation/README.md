# Open source preparation verification

Verified on 2026-10-23 against base commit `f016287` plus the uncommitted open
source preparation changes. A final committed release candidate still needs its
own source identifier and distribution evidence. No app was installed, replaced,
published or submitted for notarization during this pass.

Environment: Apple silicon, Xcode 27.1 (27A9275), Swift 6.4, macOS SDK 27.0.
App tests used the helper's isolated `app.alfred.dictation.testhost` identity,
signing disabled, with model/provider/hardware opt-in environment flags excluded.

## Results

| Check | Result and scope |
| --- | --- |
| Full ordinary Xcode suite | 1,117 tests, 60 intentional opt-in skips, zero failures after the cleanup-classification split |
| Final history integration suite | 17 tests, zero failures after the final partial-deletion refresh and delayed-cleanup regression |
| Offline SwiftPM harness | 45 tests, zero failures; actual production-source symlinks, no runtime/provider stubs |
| Release build | Unsigned Release build succeeded; no installation or launch |
| Compiled bundle inspection | Release bundle identity, disabled updater and arm64 support in seven binaries verified; distribution signing was not checked on this unsigned build |
| Publication scanner tests | Five synthetic Git/filesystem cases passed, including removed historical credentials, duplicate-blob sensitive filenames and no-follow behavior |
| Publication scan | Six local reachable commits, 560 historical blobs; no tested credential-pattern/sensitive-filename findings; media, symlinks and machine-path items remain for manual review |
| Release bundle-policy tests | Ten portable tests passed using synthetic bundles and tool mocks |
| Notarization failure gates | Six stub-tool scenarios passed; rejected/malformed/failed submissions and stapling/validation failures stop the pipeline |
| Foundation preflight | Exact toolchain, unchanged lockfile/pins, selected-source hygiene and updater isolation passed |
| Documentation | New/updated entry-point local links, workflow/issue YAML, shell syntax and whitespace checked |

The full-suite run preceded one final AppState partial-deletion refresh and its
new delayed-cleanup test. The 17-case history run covers those final changes;
1,117 is not a claim that the entire suite was rerun after them. The subsequent
Release build includes the final application code.

The first sandboxed Xcode invocation stopped during package manifest diagnostics
before compilation because Xcode needed its user cache path. The successful runs
used approved cache access with the same isolated helper and environment. The
offline harness required its documented `--disable-sandbox` fallback for SwiftPM's
nested sandbox in this execution environment. These are local results, not hosted
CI evidence or a clean-machine installation test.

The offline harness initially exposed an existing dependency gap: cleanup-attempt
source referenced runtime-specific error classification. The repair keeps a
Foundation-only category/common mapping in the primitive and maps application
errors at AppState's operation boundary. Provider classification tests remain in
the full-app suite; no assertions were removed to obtain an offline pass.

The deletion tests cover recognized rollback snapshots, legacy copies and orphan
recovery files; unknown files/models/other app identities survive. Links, hard
links and special objects are rejected or preserved. Integration tests verify
partial failure/retry, reopening empty history, and cancellation of delayed cleanup
without late delivery or recreation of deleted history. They use synthetic files
and overrides rather than live user data.

## Evidence and remaining gates

Local logs are in `/tmp/alfred-open-source-tests-final.log`,
`/tmp/alfred-open-source-history-final.log`,
`/tmp/alfred-open-source-release-build.log`, and
`/tmp/alfred-release-offline-integration.log`. The compiled-bundle inspection is in
`/tmp/alfred-open-source-bundle-policy.log`. Detailed publication scanner reports
remain local and are not committed or uploaded. Raw test logs are not publication
artifacts; review their contents before sharing.

See [release readiness](../../../docs/release-readiness.md) for owner decisions,
audio/artwork provenance, native transitive notices, clean-clone/hosted execution,
live quality/destination/fault/soak checks and actual Developer ID distribution
requirements. Stub success does not establish notarization, signing, a working
downloaded DMG, or compatibility on untested OS/hardware.
