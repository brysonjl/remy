# Open source release readiness

Remy is being prepared for a public source preview followed by a downloadable
macOS beta. The public name is Remy, with bundle identifier
`io.github.brysonjl.remy` (Dev: `.dev`). The first binary scope is Apple silicon
with manual updates; Intel support and an automatic update feed require separate
validation.

This checklist tracks the current working tree. A completed checklist is not
authorization to publish the repository or a release.

The owner selected a new public repository from a reviewed source snapshot.
The existing development repository and its history stay private. Use the
[publication workflow](engineering/publication.md), which exports a committed
tree without old audio, internal handoff notes or Git author metadata.

## Source preview gates

| Gate | Current state | Completion evidence |
| --- | --- | --- |
| Public name | Owner decision pending; preference is a person's name | Chosen name, repository URLs, app branding and migration plan agree |
| Publication review | Source export verified; current evidence paths normalized; media reviewed; raw diagnostics/internal notes/history omitted | Keep development history private; regenerate the reviewed snapshot after the public rename |
| License and provenance | Imported audio omitted; exact font provenance, 67 locked crate notice sets, native notices and model-card reviews retained and checked | Final signed binary/resource scope remains separate; use only explicitly supplied local speech fixtures for optional ASR checks |
| Contributor entry points | Contribution, security, conduct, issue and PR guidance added | Update repository URLs and enable private reporting on the new public repository at launch |
| Clean clone | Passed from an independent clone of the one-commit public snapshot: Debug, Release, app/offline tests; no signing overrides or development caches copied | Source commit, unchanged lock and clean worktree recorded in the November 5 report |
| CI | Hosted portable and full Xcode 27.1 jobs passed on preparation commit `03c82b1` | Re-run on the renamed public repository; final launch/reporting setup remains pending |
| Current evidence | Preparation committed and privately pushed; current local and hosted results recorded | Name and public repository destination remain owner decisions; binary runtime/distribution gates stay separate |

Run [publication hygiene](../validation/publication-hygiene/README.md) locally
before publication. The checker covers locally reachable refs and publishable
working files, reports categories without matching contents, and flags media,
large files and symlinks for review. It does not inspect remote-only refs,
unreachable objects, media metadata, author emails, arbitrary secret formats or
runtime traffic. An exit status of zero does not clear those review gates.

Removing a sensitive file from the working tree does not remove historical
copies. Review any proposed history rewrite separately; preserve upstream
copyright notices and provenance. No rewrite is part of this preparation pass.

The [asset provenance record](engineering/asset-provenance.md) documents omission
of the six imported WAV fixtures. They remain only in private historical commits
and ignored local storage. The public snapshot contains no speech recordings;
optional ASR checks require an explicit external fixture directory. The
[notice inventory](../THIRD_PARTY_NOTICES.md) records model and native artifact
scope separately from source publication.

## Binary beta gates

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
