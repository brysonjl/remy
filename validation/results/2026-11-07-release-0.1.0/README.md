# Release 0.1.0 (early beta prerelease)

Published on 2026-11-07 as GitHub prerelease
[`v0.1.0`](https://github.com/brysonjl/remy/releases/tag/v0.1.0). This was the
first real run of the release script with Developer ID credentials.

## Published artifact

| Field | Value |
| --- | --- |
| Source commit | `6febfa22e605c2e94a7dc66a0db1540512bf911f` (tag `v0.1.0`) |
| Version / build | 0.1.0 / 2 |
| Architecture / updates | arm64 / manual |
| Toolchain | Xcode 27.2 (27B52), macOS SDK 27.2 |
| Artifact | `Remy-0.1.0-arm64.dmg`, 22,201,229 bytes |
| SHA256 | `5a36f918f72c1af749388d62d7bc853e4bc48710166c415d8338b1dd2e2e19bb` |
| Lockfile SHA256 | `8f72a9d408f6be83aa17962e0671f0322160407f83eacf990e8b464664cf52e1` |
| Model manifest SHA256 | `cda0167b02368f3f2c54e4be8e850c6e8f85f40da3a57403f088d2425976002d` |

## Release script gates

All hard gates in `release-macos.sh` passed for build 2:

- Clean committed tree, toolchain preflight and locked package/artifact verification.
- Developer ID signing; bundle identity, version, arm64 and disabled-updater checks.
- App notarization **Accepted**, stapled and validated; Gatekeeper accepted the app.
- DMG notarization **Accepted**, stapled and validated.
- Mounted DMG: app signature, architecture and Gatekeeper assessment passed.

After publication, the DMG downloaded from the release URL matched the SHA256
above, and the tag resolved to the source commit. The DMG container itself is
not code-signed; it is notarized and stapled, and the app inside is signed.

## Manual install check

Build 1 (commit `57ed496`) differed from build 2 only in the default
recording-start sound. It was downloaded through a browser from the draft
release, dragged to Applications and opened on the maintainer's Mac. macOS
showed the standard first-launch prompt stating that Apple checked the app for
malicious software. The app reported 0.1.0, reused the existing Remy data and
completed a dictation with cleanup. Build 2 passed the same script gates and
download-hash check but was not separately browser-installed.

## Not covered

This is a narrower early-beta claim than the full
[binary beta gates](../../../docs/release-readiness.md#binary-beta-gates).
Not yet exercised: a clean account or second Mac, macOS versions other than the
build machine's, upgrade from an earlier release, uninstall, the destination
matrix, fault and soak testing.
