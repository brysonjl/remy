# Ordinary foundation validation

The root workflow runs on push, pull request and manual dispatch with read-only
repository permissions, a commit-pinned checkout action and no persisted checkout
credential. It does not sign, launch the interactive app, access user credentials,
install speech/cleanup models, upload artifacts, create releases or publish.
The Xcode test action does run the unsigned XCTest host with production startup
suppressed. No deployment/signing secrets are configured or referenced.

The always-on `source-checks` job runs package-pin/hygiene checks, publication
scanner regression tests, shell syntax and stubbed notarization failure tests on
`ubuntu-24.04`. A pass establishes source checks only, not a successful app build.

Full validation requires Apple Silicon and one exact measured pair:

- **Xcode 27.1 (27A9275), macOS SDK 27.0**: original validated baseline and hosted CI selection.
- **Xcode 27.2 (27B52), macOS SDK 27.2**: local full suite (1,128 tests, 60 opt-in skips, zero failures) and unsigned Release build passed on 2026-11-05.

Other builds, including the hosted 27.2 release candidate 27B48, are not accepted.
The check validates the compiler/build and SDK together; no arbitrary version
range or automatic fallback is permitted. Fresh-clone results are recorded
separately from these existing-workspace measurements.
The `full-xcode` job now selects GitHub's **public-preview `xcode-27`** arm64 image
and explicitly sets `DEVELOPER_DIR=/Applications/Xcode_27.1.app/Contents/Developer`.
The [official runner inventory](https://github.com/actions/runner-images/blob/main/images/macos/xcode-27-arm64-Readme.md)
was rechecked on **2026-11-05** (image `20261026.0314.1`): it still lists
27.1 / 27A9275 at that directory with SDK 27.0, now as its default compiler.
It also lists 27.2 **release candidate 27B48**, which is a different build from the
local 27.2 / 27B52 toolchain. This is not evidence that the newer local build is
on hosted runners. The [runner reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)
lists `xcode-27` for both public and private repositories.
Preview image availability/content can change. Exact preflight remains mandatory;
a matching inventory is not a successful hosted run. Obtain green full hosted
validation before claiming CI builds the app. No untrusted PR runs on a personal
or self-hosted Mac. No older-toolchain compatibility is inferred.

Manual fallback on a compatible local Mac, from the repository root:

```sh
python3 validation/foundation-checks/check.py --toolchain
bash validation/foundation-checks/run.sh
```

If necessary, select the already-installed required Xcode with `DEVELOPER_DIR`.
The wrapper clears inherited environment except HOME, PATH, locale and selected
Xcode, preventing TEST_RUNNER/model/provider/hardware/benchmark/signing opt-ins.
A legacy `/private/tmp` S1 model fixture makes it refuse the run rather than
accidentally enable inference. Normal opt-in tests remain skipped; their absence
is not runtime coverage. Use a clean ephemeral runner for credential-free CI;
clearing environment is not a claim to sandbox an existing user's HOME/Keychain.
This wrapper is not a signed/live-provider test workflow.

Sequence: exact toolchain and lock checks; pinned package resolve; existing
independent dependency revision/ZIP verifier; unsigned Debug and Release builds;
ordinary XCTest without parallel execution; offline SwiftPM harness; final lock
check. Explicit dependency verification may download the three pinned binary
archives into ignored `.local/dependency-archives/`; this is distinct from speech
model downloading. Its generated report goes to ignored
`.local/foundation-checks/dependency-verification.json`. Existing notices are
retained. No transcript/audio/credential diagnostics are uploaded.

`check.py` compares the exact A00 lock SHA256 and all manifest package identities,
repositories, versions and revisions. The workflow also checks tracked lockfile
changes even after failure. Intentional future dependency updates require review
of the manifest, lock and expected digest together. This does not attest a full
supply chain or replace independent artifact hash checks.

The same preflight performs narrow distribution-input hygiene. With Git metadata,
it rejects tracked private signing/key/environment filenames. A source archive
without Git metadata receives an explicit smaller fallback scope instead. It scans
bounded selected text under app sources, Config, scripts and root workflows for
private-key PEM, recognizable `sk-` credentials and literal long Bearer markers.
It does not read the ignored `Signing.local.xcconfig` fallback override, follow
symlink files, inspect `.local`/`.build`, user preferences or Keychain, or dump
matched content/paths. It also requires disabled source-plist updater fields and
presence of distinct development/release project IDs. These checks are not a
comprehensive secret scanner, compiled-bundle audit, signing proof or guarantee
that arbitrary credential formats are absent. Manual notice/credential review
still applies to actual distribution inputs. Namespace isolation has been
measured; simultaneous upstream/Remy operation is not claimed.

The wrapper forwards the user's actual `HOME` unchanged only to preserve normal
OS/toolchain behavior; it never repurposes that variable as a task directory.

The inherited nested `app/.github/workflows/release.yml` now has only a manual
informational job with no permissions, tag trigger, release action or write path.
GitHub discovers workflows only at the repository root; this nested file is also
safe if the app folder is later made a separate repository. Release remains
disabled pending independent distribution work and app/AGENTS.md requirements.

## Verify a committed snapshot in a fresh clone

After committing reviewed changes, run from the repository root:

```sh
# Prepare only; creates an independent clone and records the exact source SHA.
python3 validation/foundation-checks/fresh-clone.py --revision HEAD

# Prepare another fresh clone and run the full serial Xcode foundation checks.
python3 validation/foundation-checks/fresh-clone.py --revision HEAD --run
```

The helper defaults to ignored `.local/clean-clones/remy-*/`. An optional
`--destination /absolute/new/path` must not exist; existing files are never
replaced. It uses Git transport without local hardlinks or alternate object
storage, checks out the resolved commit detached, and copies no ignored caches,
models, signing overrides, or uncommitted changes. A dirty source is reported
explicitly because its edits are excluded. Only the `--run` form starts Xcode;
coordinate it with other builds rather than running multiple Xcode jobs locally.

The result and full-validation log remain inside the clone's ignored `.local/`.
The final report checks the lockfile and working tree stayed unchanged. A prepared
clone does not count as a passed build. HOME, Keychain, OS caches, and the selected
Xcode installation remain those of the current user; this verifies committed-source
and repository-cache independence, not a clean user account or a sandboxed machine.
A hosted run or separate clean account provides additional independent evidence.
