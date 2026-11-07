# Remy application source

This directory contains Remy's native macOS app, adapted from Dictate Anywhere
at `e420448de6e2099d33e050a4b419722032fd7519`. The original
[MIT notice](LICENSE) and [upstream attribution](../ATTRIBUTION.md) are retained.

Start with the root [README and quick start](../README.md) to build, install and
set up dictation. Debug and Release builds, isolated tests and signed local
installation have been verified; see the [engineering baseline](docs/engineering/baseline.md)
and the validation records linked from the root README for scope and results.

## Build and test

All commands below run from the **repository root**, one directory above this
file. The build helper targets Apple silicon. Verified exact toolchain pairs are
Xcode **27.1 (27A9275) / macOS SDK 27.0** and **27.2 (27B52) / macOS SDK 27.2**.
The latter passed the full ordinary suite and unsigned Release build on 2026-11-05.
Hosted CI remains on the exact 27.1 baseline; local measurements do not establish
a green hosted run or a clean-machine installation.

```sh
app/scripts/remy-build.sh resolve
app/scripts/remy-build.sh build
app/scripts/remy-build.sh build Release
app/scripts/remy-build.sh test -parallel-testing-enabled NO
```

Ordinary build/test commands disable signing. Tests use
`io.github.brysonjl.remy.testhost` with production startup suppressed and isolated
storage. If your Mac requires a signed test host, configure the existing Apple
Development identity as described in the root quick start, then run:

```sh
app/scripts/remy-build.sh test Debug \
  -xcconfig app/Config/Signing.local.xcconfig \
  CODE_SIGNING_ALLOWED=YES CODE_SIGNING_REQUIRED=YES \
  CODE_SIGN_STYLE=Automatic 'CODE_SIGN_IDENTITY=Apple Development' \
  -parallel-testing-enabled NO
```

Package versions are locked in
[Package.resolved](Dictate%20Anywhere.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved).
Build/package caches live in ignored `app/.build/`. First resolution downloads
pinned source packages and binary frameworks; speech models are installed
separately through the app.

For a committed-source reproducibility check, use the
[fresh-clone helper](../validation/foundation-checks/README.md#verify-a-committed-snapshot-in-a-fresh-clone).
It prepares an independent clone by default; `--run` performs full serial foundation
validation with the exact supported toolchain. Ignored signing/model/build files
and uncommitted edits are not copied. A clone prepared without `--run` is not a
successful build.

## Local app identities

| Workflow | App | Bundle ID | Location |
| --- | --- | --- | --- |
| `remy-dev.sh prepare`, then `launch` | Remy Dev | `io.github.brysonjl.remy.dev` | `.local/run/Remy Dev.app` |
| `remy-local.sh prepare`, then `install` | Remy | `io.github.brysonjl.remy` | `~/Applications/Remy.app` |

Both use your local Apple Development certificate. Dev and Release keep separate
data, model caches and credentials. Finish dictation and quit both apps before
switching between them. See [local development](docs/engineering/local-development.md)
and [installation, migration and rollback](../docs/engineering/local-installation.md).

The Xcode project and scheme retain the name **Dictate Anywhere**; the Swift
module is `Dictate_Anywhere`. Use the Remy helpers for this fork. The inherited
`dev.sh` is not the supported Remy workflow. Public packaging follows
[RELEASE.md](RELEASE.md): arm64, manual updates, independently chosen version/build,
and fail-closed DMG notarization/stapling/validation. A local signed build does not
constitute a notarized public release; real distribution validation remains open.
