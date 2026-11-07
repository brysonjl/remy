# Remy release preparation

The initial binary release target is **Apple silicon (arm64), manual updates,
DMG installation**. The first prerelease, 0.1.0, was published on 2026-11-07
([release record](../validation/results/2026-11-07-release-0.1.0/README.md)). Intel support and
Sparkle update feeds are outside this first release scope. Existing development
versions (`2.12.16`, build `58`) are inherited metadata, not Remy's release history.

## Candidate prerequisites

Before a public beta, complete the root release-readiness ledger and the applicable
[acceptance gates](../docs/evaluation/acceptance.md). Record current source SHA,
ordinary-suite results, opt-in skips, live compatibility and fault coverage,
privacy checks, exact bundled third-party notice review, and tested hardware/OS.
A macOS 14 deployment target does not establish macOS 14 compatibility.

A maintainer must choose Remy's independent public version and build number.
Pass numeric `RELEASE_VERSION` (major.minor.patch) and `RELEASE_BUILD` to packaging;
mark the eventual GitHub release **prerelease** for a beta. These explicit overrides
avoid accidentally publishing the inherited version and do not change development
project metadata. Record the chosen version in the candidate notes and tag.

Use one verified arm64 pair: Xcode **27.1 (27A9275) / SDK 27.0**, or
**27.2 (27B52) / SDK 27.2**. The latter passed the local full ordinary suite
and unsigned Release build on 2026-11-05. Full hosted validation remains on
GitHub's `xcode-27` preview image with the exact 27.1 directory; a green hosted
run is still required. The image's listed 27.2 candidate 27B48 is not the measured
local 27B52 build and is not accepted by preflight. See [foundation checks](../validation/foundation-checks/README.md).

## Local packaging setup

Run these commands from `app/`. Install `create-dmg`, full Xcode, your own
**Developer ID Application** signing identity, and a Keychain notarization profile.
Apple Development signing used for local installs is not the distribution identity.
The template never creates/imports credentials or publishes an artifact.

```sh
brew install create-dmg
xcrun notarytool store-credentials "remy-notary"
cp scripts/release-macos.template.sh scripts/release-macos.sh
chmod +x scripts/release-macos.sh
```

`release-macos.sh` is ignored by Git. Compare any existing local copy with the
committed template before each candidate. Set the following in your shell or
ignored local script, substituting values you own:

```sh
export NOTARY_PROFILE="remy-notary"
export TEAM_ID="YOURTEAMID"
export DEVELOPER_ID_APP="Developer ID Application: Your Name (YOURTEAMID)"
# Choose and review these; example values are not an assigned release version.
export RELEASE_VERSION="0.1.0"
export RELEASE_BUILD="1"
./scripts/release-macos.sh
```

The script requires a clean committed working tree and refuses to replace an
existing candidate output. It resolves/verifies locked package revisions and
binary-archive hashes, archives arm64 with distribution tracing disabled, packages
LICENSE/ATTRIBUTION/third-party notices inside the app, verifies the bundle identity
and disabled updater, signs, notarizes/staples/validates the app and DMG, then mounts
the DMG and checks its app signature, architecture and Gatekeeper assessment.
Both notary submissions must return JSON status **Accepted**, even when the tool
itself exits zero. Any failed hard gate aborts without promoting a candidate.

Completed outputs are under `dist/Remy-<version>-<build>-arm64/`:

- `Remy-<version>-arm64.dmg`: primary manual-install artifact.
- `SHA256SUMS`: digest of that exact DMG.
- `release.json`: source SHA, compiler/SDK, version/build, architecture, lock/model
  manifest digests, and DMG hash/size.
- `dependencies.json` and the two notarization response JSON files: verification
  evidence for maintainer review. Review identifiers before sharing these reports.

A temporary ZIP exists only for app notarization. No update feed, Sparkle signing
key, public ZIP or PKG is generated. The inherited Sparkle framework may remain
linked with its updater disabled. Enabling updates later requires independently
owned trust, update/migration testing, and review of foundation policy.

## Verification and publication

The packaging implementation has stubbed failure tests and completed its first
real run for 0.1.0. Passing source CI or stub tests does not establish valid
signing, notarization, installation or user compatibility.

### First-run prompts on a new machine

- **Keychain:** the first signing step asks whether `codesign` may use the
  Developer ID key. Choose **Always Allow** promptly. Signing waits on the dialog,
  and a long wait fails the archive with "timestamps differ"; rerun after allowing.
- **Finder automation:** `create-dmg` scripts Finder to lay out the DMG window.
  Allow the terminal app to control Finder (System Settings → Privacy & Security →
  Automation), or the step times out with AppleEvent error -1712.
- **First notarization:** a new developer team's first submission can take 30
  minutes or more; later submissions usually finish in a few minutes.

A failed run deletes its partial candidate, so rerun the whole script. Reuse the
same version with a new build number when replacing an unpublished candidate.

Before publication, test the exact candidate as a fresh browser download on a clean
account/Mac: Gatekeeper/offline ticket behavior, first-launch permissions, model
installation, dictation, offline local operation, manual upgrade, history/schema
migration, rollback restrictions, permission continuity, uninstall and retained-data
instructions. Check every advertised OS/hardware combination. Keep a previous
working app/data backup for migration tests. Review complete native binary notices;
the inventory's unresolved transitive items are not legal clearance.

Publish only after these checks and owner release decisions are complete. Link the
exact source commit/tag and prerelease notes, attach the verified DMG and checksum,
and verify downloaded bytes match `SHA256SUMS`. Never expose an upstream appcast.
GitHub's `releases/latest` link skips prereleases, so link to the releases page
while only prereleases exist.
The script does not commit, tag, upload or publish.

Per [AGENTS.md](AGENTS.md), a DMG must be notarized, stapled and validated before
publication. If any check fails, stop release publication. A PKG would need its own
separate signing, notarization and verification process.

## Safe local regression checks

From the repository root:

```sh
python3 validation/foundation-checks/check.py
bash -n app/scripts/release-macos.template.sh app/scripts/release-gates.sh
bash app/scripts/release-gates_test.sh
python3 -m unittest discover -s app/scripts -p 'test_*.py'
```

These tests use stub `xcrun` calls and never submit software or access credentials.
The old universal verifier is retained for historical/deferred Intel work; the
current release uses `verify-release-app.py` to require arm64 in all embedded code.
