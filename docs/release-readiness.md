# Open source release readiness

Remy's source is public, and 0.2.0 is available as an early beta prerelease DMG.
The public name is Remy, with bundle identifier `io.github.brysonjl.remy`
(Dev: `.dev`). The first binary scope is Apple silicon with manual updates;
Intel support and an automatic update feed require separate validation.

This checklist tracks the current working tree. A completed checklist is not
authorization to publish a release.

This repository started from a reviewed source snapshot; the development
history stays private. See [how this repository was published](engineering/publication.md).

## Source preview gates

| Gate | Current state | Completion evidence |
| --- | --- | --- |
| Public name | Done: Remy, bundle identifier `io.github.brysonjl.remy` | App branding, repository URLs and bundle identifier agree |
| Publication review | Done: source export verified; evidence paths normalized; media reviewed; raw diagnostics, internal notes and history omitted | Initial public commit `f04a7a2` matches the reviewed export manifest |
| License and provenance | Imported audio omitted; exact font provenance, 67 locked crate notice sets, native notices and model-card reviews retained and checked | Final signed binary/resource scope remains separate; use only explicitly supplied local speech fixtures for optional ASR checks |
| Contributor entry points | Done: contribution, security, conduct, issue and PR guidance; private vulnerability reporting enabled | Repository URLs point at this repository |
| Clean clone | Passed from an independent clone of the one-commit public snapshot: Debug, Release, app/offline tests; no signing overrides or development caches copied | Recorded in the November 5 report |
| CI | Done: hosted portable and full Xcode 27.1 jobs passed on `f04a7a2` (1,128 tests, 61 skips, 0 failures; 45 offline tests) | Hosted checks run on every pull request |

Run [publication hygiene](../validation/publication-hygiene/README.md) locally
before merging changes. The checker covers locally reachable refs and publishable
working files, reports categories without matching contents, and flags media,
large files and symlinks for review. It does not inspect remote-only refs,
unreachable objects, media metadata, author emails, arbitrary secret formats or
runtime traffic. An exit status of zero does not clear those review gates.

Removing a sensitive file from the working tree does not remove it from Git
history. Review any proposed history rewrite separately; preserve upstream
copyright notices and provenance.

The [asset provenance record](engineering/asset-provenance.md) documents omission
of the six imported WAV fixtures. This repository contains no speech recordings;
optional ASR checks require an explicit external fixture directory. The
[notice inventory](../THIRD_PARTY_NOTICES.md) records model and native artifact
scope separately from source publication.

## Binary beta gates

The owner published 0.1.0 as an early beta with a narrower claim than these
gates: the release script's signing, notarization and Gatekeeper gates passed,
and one browser download was installed and used on the maintainer's Mac. The
remaining gates below are open; see the
[0.1.0 release record](../validation/results/2026-11-07-release-0.1.0/README.md).
0.2.0 was published the same way on 2026-11-09; see the
[0.2.0 release record](../validation/results/2026-11-09-release-0.2.0/README.md).

| Gate | Required result |
| --- | --- |
| Public identity and version | Independent version/build, tag, app metadata, DMG and release notes agree |
| Architecture and OS | Package checks match Apple silicon scope; oldest advertised macOS and representative hardware actually tested |
| Dependency and model inventory | Source SHA, locked revisions, toolchain, applicable notices and artifact hashes recorded |
| Privacy and deletion | Synthetic tests and UI verify history-off, retention, migration snapshots, complete deletion and visible partial failures |
| Current regression suite | Ordinary full-app and offline tests pass on the candidate; opt-in skips described |
| Actual destinations | Recording and insertion tested against advertised apps, both destination policies, focus changes and manual recovery |
| Fault and quality tests | Cancellation, clipboard changes, permissions, provider errors, storage/model failures and semantic preservation pass or have explicitly scoped exceptions |
| Reliability | Lifecycle and mixed-use soak evidence meets the [acceptance policy](evaluation/acceptance.md), or the owner records a narrower beta claim |
| Manual update and rollback | Fresh install, upgrade, schema compatibility, permissions, offline restart and uninstall exercised |
| Distribution trust | Developer ID signature, accepted notarization, stapling, DMG validation and mounted-app Gatekeeper assessment pass |

Use the prescribed release-script flow in [app/RELEASE.md](../app/RELEASE.md).
[app/AGENTS.md](../app/AGENTS.md) requires a notarized, stapled, successfully
validated DMG before publication. Local Apple Development signing alone does not
satisfy this gate. Credentials and signing assets stay outside the repository.

## Existing evidence

- The [November 5 source-release report](../validation/results/2026-11-05-source-release/README.md)
  records 1,128 app tests with 60 opt-in skips and zero failures, 45 offline
  tests, and a successful unsigned Release build on Xcode 27.2 (27B52).
- The [October 23 preparation report](../validation/results/2026-10-23-open-source-preparation/README.md)
  records 1,117 full-app tests with 60 opt-in skips and zero failures, followed by
  17 final history integration tests and a successful unsigned Release build.
  The offline harness passed 45 tests. The report identifies the exact change
  sequence and limits; these results do not establish distribution readiness.
- The [October 20 context report](../validation/results/2026-10-20-context-awareness/README.md)
  records 1,091 tests, 60 opt-in skips, zero failures, and a signed Release build.
- The [Arc repair](../validation/results/2026-10-20-arc-paste-guard/README.md)
  records 37 focused tests and one user-confirmed live insertion case. It does
  not establish a complete destination matrix.
- Commit `f016287` records 122 targeted tests for cleanup fallbacks, history
  migration, error sanitization and overlay delivery. The earlier full suite
  cannot be treated as a run of that commit or this preparation work.

Historical implementation plans, baseline reports and continuation notes retain
their original scope. Use this checklist and a candidate report for current
release decisions rather than interpreting old pending-work statements as
current test results.

## Candidate record

Record these values for the final candidate after implementation is integrated:

```text
Source commit and clean/dirty state:
Public version/build and supported scope:
Hardware, macOS, Xcode, SDK and architecture:
Lockfile digest and dependency/model revisions:
Build/test commands, results and intentional skips:
Destination, fault, quality and soak evidence:
Privacy/deletion and install/upgrade/rollback results:
Artifact SHA256, signing and notarization/stapling/validation results:
Known limitations, approved exceptions and owner:
Source-preview decision and binary-release decision:
```
