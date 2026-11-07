# Local development and identity

Use the pinned build helper from the workspace root:

```sh
app/scripts/remy-build.sh resolve
app/scripts/remy-build.sh build
app/scripts/remy-build.sh build Release
app/scripts/remy-build.sh test -parallel-testing-enabled NO
validation/run-tests.sh
```

Required measured toolchain: Apple Silicon, Xcode 27.1 (27A9275), macOS SDK 27.0.
The helper keeps build/package caches under ignored `app/.build`, preserves package
pins and disables signing for ordinary build/test. Test runs the XCTest host with
production startup suppressed and an isolated `io.github.brysonjl.remy.testhost`
identity (display name **Remy Test Host**). Its preferences, Application Support
and Keychain service names differ from Remy Dev. The entry point refuses an
XCTest launch under a development/release identity before constructing Settings.
Use this helper for tests; a direct Xcode Test action needs the same
`REMY_APPLICATION_BUNDLE_IDENTIFIER=io.github.brysonjl.remy.testhost` build override.
Tests do not establish live permissions/insertion.
Keep live model/provider/hardware opt-ins out of ordinary test environments.
The [foundation wrapper](../../../validation/foundation-checks/README.md) supplies
an environment-filtered validation sequence, read-only pin checks and narrowly
scoped distribution-input hygiene (not comprehensive secret scanning). Root CI
uses a hosted macOS image but fails preflight if this exact toolchain is absent;
local execution is the documented fallback, not a claim of hosted support.

## Signed interactive development

Debug is **Remy Dev**, bundle `io.github.brysonjl.remy.dev`; Release is **Remy**,
`io.github.brysonjl.remy`. The inherited project/target remains Dictate Anywhere and
the Swift module `Dictate_Anywhere`. Models, recovery, data and Keychain services
are bundle-scoped; do not reuse upstream credentials or write to shared FluidAudio
storage. Preserve the MIT notice, [attribution](../../../ATTRIBUTION.md) and
separate dependency/model terms. Source namespace isolation is measured;
simultaneous operation with the upstream app has not been independently tested.

The isolation rule was added after an interrupted A04 fixture cleared Remy
Dev's legacy history preference and changed several settings. The legacy list
was confirmed empty and no verified backup was available. Confirmed model/cloud/
vocabulary settings were restored; history was not reconstructed. Tests now
assert their independent identity. Do not rely on fixture teardown to protect
interactive app data.

To use an existing local Apple Development identity, create ignored
`app/Config/Signing.local.xcconfig`:

```xcconfig
DEVELOPMENT_TEAM = YOURTEAMID
```

After ordinary checks finish:

```sh
app/scripts/remy-dev.sh prepare
app/scripts/remy-dev.sh launch
```

`prepare` explicitly signs Debug, verifies identity/signature and stages the
verified bundle at `.local/run/Remy Dev.app`; it does not launch. Previous valid
copies are retained in uniquely named `.remy-dev-backup.*` directories and a
failed move restores the prior copy. `launch` verifies and opens only that stable
copy. Both actions refuse while Remy Dev or its test host runs; their lock is
`.local/run/.remy-dev.lock`. Inspect an interrupted action before removing its
lock. Symlink run directories/app replacements are rejected. Ordinary unsigned
builds do not overwrite this stable permission-bearing copy.

The helper does not install into `/Applications`, configure login items or Xcode
accounts, distribute, notarize or publish. Launch at login is a separate explicit
user setting; tests suppress login-item mutation. In General, Dock and Menu Bar
keeps both entry points available; closing settings does not quit the app. Use
the stable Finder path and same signing identity for interactive permission tests.
The user grants Microphone/Accessibility and handles any signing-key dialog.
Provider credentials belong in app UI/Keychain, never shell commands, environment,
local signing config, logs or exported diagnostics.

On the baseline Mac, Accessibility remained denied despite an enabled switch;
the user's manual remove/re-add of the exact stable entry resolved it. Signature
checks had matched the running process to the stable bundle. This does not prove
a stale-grant mechanism or justify a blanket TCC reset. Verify app identity and
current state before a narrow user-mediated grant refresh. Actual live results
and remaining focus/terminal/resource limits are in the [baseline](baseline.md).

## Updater and release boundary

Inherited Sparkle startup/feed activation is disabled for Remy. Local build and
dev helpers do not activate an updater or publish. The inherited release workflow
is informational only, with no tag publication path or write permission. Do not
use inherited `dev.sh`/release automation unchanged as Remy workflows. Any later
release must satisfy [app/AGENTS.md](../../AGENTS.md), independent updater identity,
license/notice checks and the distribution gate; local development signing is
not Developer ID distribution signing, notarization or release approval.
