# Source release verification

Verified on 2026-11-05 against base commit `0ff81c9` plus the source-release
preparation. The supported local environment is Apple silicon, Xcode 27.2
(`27B52`), Swift 6.4.2 and macOS SDK 27.2. Earlier Xcode 27.1 evidence remains
separate; its exact hosted image is still selected by CI.

## Current tree results

| Check | Result |
| --- | --- |
| Full isolated app suite | 1,128 tests, 60 intentional opt-in skips, zero failures |
| Offline production-source harness | 45 tests, zero failures |
| Unsigned Release build | Passed; no installation or interactive app launch |
| Publication scanner/exporter | 11 synthetic Git/filesystem tests passed |
| Bundle-policy tests | 12 portable tests passed |
| Notarization gates | Six stub-tool success/failure scenarios passed |
| Independent clone helper | Two real temporary-Git isolation tests passed |
| Toolchain/package preflight | Exact 27.2/27B52/SDK27.2 and original locked package revisions verified |
| Publication review | No tested credential patterns found; only retained internal HANDOFF has working-tree machine paths and is excluded from exports |
| Media review | 15 generated PNGs reviewed; detected EXIF metadata is color-space only; two font binaries matched to immutable upstream files with companion terms |
| Hosted source checks | Passed on private preparation commit `03c82b171bd1455186b02ee7cec83709038d896e` |
| Hosted Xcode 27.1 | Debug and Release builds, 1,128 app tests with 61 skips and zero failures, and 45 offline tests passed |
| Independent public-source clone | Debug and Release builds, 1,128 app tests with 60 skips and zero failures, and 45 offline tests passed; lock unchanged and worktree clean |

Ordinary app tests use the isolated test-host identity and a sanitized
environment. Speech-model, microphone, provider and destination tests remain
explicit opt-ins; skipped checks do not establish live compatibility. The four
new external-fixture tests verify missing-input skips and invalid-input failures
without recordings or downloaded models.

The six imported WAVs are no longer source inputs or test-bundle resources.
They survive only in private historical commits and ignored local storage.
Optional ASR tests require `ALFRED_SPEECH_FIXTURES_DIR`; no fallback silently
loads the removed recordings. The new public repository will start from the
reviewed committed-tree export, preserving the development repository privately.

Historical evidence files have normalized home/workspace paths, with raw local
identifiers removed from the old performance capture. Originals remain ignored
and private. The source exporter additionally rejects audio, unexpected binaries,
credential patterns, unsafe links and remaining machine-specific paths, and
records per-file SHA256 values without Git history or author metadata.

## Committed source and external checks

Preparation commit `03c82b171bd1455186b02ee7cec83709038d896e` was pushed only to
the existing private repository's `release/source-preparation-20261105` branch.
The hosted validation run (in that private repository; its logs are not public)
completed successfully for both portable source checks and full Xcode validation.
The additional hosted skip is
`AppleSpeechPreparationLifecycleTests.testOlderInvalidationCannotCancelNewerPreparedSession`,
whose Apple Speech availability guard did not pass on that runner. It passed
locally; no claim of live hosted speech recognition is made.

The reviewed export was checked against every manifest file hash and initialized
as a new, local one-commit repository using the account's public no-reply identity.
It contains no old Git history, personal author email, handoff notes or speech
audio. Its independent verification clone used source commit
`28b33707fcc53a8566333db00f91dbf35133130a`; no ignored development build/model/signing
files were copied, no Git object alternates were used, and the full foundation
sequence passed. HOME and OS/toolchain caches remain shared on the local Mac;
the hosted runner supplies separate-machine evidence.

Final publication metadata edits and omission of the old bounded diagnostic log
follow these runs. They do not change application, test, build or dependency inputs;
the final exported code inputs must match the verified snapshot by hash.

Local test logs
are `/tmp/alfred-source-release-tests-20261105.log`,
`/tmp/alfred-source-offline-20261105.log` and
`/tmp/alfred-source-release-build-20261105.log`. Independent-clone results are in
the ignored clone's `.local/fresh-clone.json` and `fresh-clone-validation.log`.
The complete hosted job log was retrieved directly to
`/tmp/alfred-hosted-xcode-raw-20261105.log`; the CLI-rendered job log was incomplete,
so test totals were verified against the complete direct response. Raw logs are
not published. The final committed-tree snapshot, checksum and integrity manifest
are separate local artifacts.

GitHub metadata confirms the existing development repository is private and the
owner has administrator permission. Private vulnerability reporting is a public
repository feature; configure it on the new repository at launch, rather than
changing the existing repository's visibility.

## Distribution limits

The checks above validate source preparation and an unsigned app, not a public
DMG. No Developer ID Application signing identity was visible in the local
environment during this pass, and no notarization or signed-distribution
rehearsal ran. Native-resource and optional-model terms remain separately scoped
by the [notice inventory](../../../THIRD_PARTY_NOTICES.md). Live destination,
quality, fault, migration and soak gates remain in the
[release checklist](../../../docs/release-readiness.md).
