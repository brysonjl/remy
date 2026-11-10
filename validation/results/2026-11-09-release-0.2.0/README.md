# Release 0.2.0 (early beta prerelease)

Published on 2026-11-09 as GitHub prerelease
[`v0.2.0`](https://github.com/brysonjl/remy/releases/tag/v0.2.0). It adds
spelling corrections, snippets, vocabulary CSV import, custom writing styles,
Transforms and opt-in on-screen name spelling (PR #8).

## Published artifact

| Field | Value |
| --- | --- |
| Source commit | `46eee1c8405a55b24a7f324b1e7f06a86d640283` (tag `v0.2.0`) |
| Version / build | 0.2.0 / 3 |
| Architecture / updates | arm64 / manual |
| Toolchain | Xcode 27.2 (27B52), macOS SDK 27.2 |
| Artifact | `Remy-0.2.0-arm64.dmg`, 22,410,737 bytes |
| SHA256 | `d670d081d9e0e4aae06e5f51369c2a342472a011c81b335ad73ab1c73035dead` |
| Lockfile SHA256 | `8f72a9d408f6be83aa17962e0671f0322160407f83eacf990e8b464664cf52e1` |
| Model manifest SHA256 | `cda0167b02368f3f2c54e4be8e850c6e8f85f40da3a57403f088d2425976002d` |

## Release script gates

All hard gates in `release-macos.sh` passed for build 3:

- Clean committed tree, toolchain preflight and locked package/artifact verification.
- Developer ID signing; bundle identity, version, arm64 and disabled-updater checks.
- App notarization **Accepted**, stapled and validated; Gatekeeper accepted the app.
- DMG notarization **Accepted**, stapled and validated.
- Mounted DMG: app signature, architecture and Gatekeeper assessment passed
  (`source=Notarized Developer ID`).

After publication, the DMG downloaded from the release URL matched the SHA256
above, and the tag resolved to the source commit.

Source checks for the merged change: hosted CI on PR #8 passed portable checks,
full Xcode 27.1 validation and the Windows preview build. Locally, 1,236 app
tests passed with 60 skips.

## Manual install check

The features were exercised by the maintainer in Dev and locally signed Release
builds of the same changes before release. Build 3 itself has not yet been
browser-installed.

## Not covered

This is a narrower early-beta claim than the full
[binary beta gates](../../../docs/release-readiness.md#binary-beta-gates).
Not yet exercised: a clean account or second Mac, macOS versions other than the
build machine's, upgrade testing beyond the maintainer's own data, uninstall, the
destination matrix, fault and soak testing, and how much accessibility text
Chromium and Electron apps expose for on-screen name spelling.
