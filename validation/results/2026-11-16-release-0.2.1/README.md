# Release 0.2.1 (early beta prerelease)

Published on 2026-11-16 as GitHub prerelease
[`v0.2.1`](https://github.com/brysonjl/remy/releases/tag/v0.2.1). It stops
cleanup from rewriting dictation to fit ChatGPT's composer placeholder and
withholds surrounding text from cloud providers for mid-sentence insertions
(PR #10).

## Published artifact

| Field | Value |
| --- | --- |
| Source commit | `6e87b934558568f026822e13fae10444dc8bce03` (tag `v0.2.1`) |
| Version / build | 0.2.1 / 4 |
| Architecture / updates | arm64 / manual |
| Toolchain | Xcode 27.2 (27B52), macOS SDK 27.2 |
| Artifact | `Remy-0.2.1-arm64.dmg`, 22,413,207 bytes |
| SHA256 | `4d9966904fa10cd2f0363059b87cd9a2d5ef3c9b54fd20620626ecb05fc26d39` |
| Lockfile SHA256 | `8f72a9d408f6be83aa17962e0671f0322160407f83eacf990e8b464664cf52e1` |
| Model manifest SHA256 | `cda0167b02368f3f2c54e4be8e850c6e8f85f40da3a57403f088d2425976002d` |

## Release script gates

All hard gates in `release-macos.sh` passed for build 4:

- Clean committed tree, toolchain preflight and locked package/artifact verification.
- Developer ID signing; bundle identity, version, arm64 and disabled-updater checks.
- App notarization **Accepted**, stapled and validated; Gatekeeper accepted the app.
- DMG notarization **Accepted**, stapled and validated.
- Mounted DMG: app signature, architecture and Gatekeeper assessment passed
  (`source=Notarized Developer ID`).

After publication, the DMG downloaded from the release URL matched the SHA256
above, and the tag resolved to the source commit.

Source checks for the merged change: hosted CI on PR #10 passed portable checks,
full Xcode 27.1 validation and the Windows preview build. Locally, 1,243 app
tests passed with 60 skips, and the offline validation suite passed 49 tests.

## Manual checks

- Before the fix, cleanup replays against the maintainer's OpenAI-compatible
  model reproduced the reported output (lowercase start, missing question mark,
  invented lead-in) from ChatGPT's placeholder context. After it, the same
  dictation came back as an ordinary question.
- A signed Remy Dev build of the change, dictating into an empty ChatGPT
  composer in a new chat and in an existing conversation, recorded
  `empty_field` with no placeholder text.
- The published DMG was installed over the maintainer's 0.2.0 copy. On first
  launch, history migrated to schema 3 and wrote its pre-migration backup.

## Not covered

This is a narrower early-beta claim than the full
[binary beta gates](../../../docs/release-readiness.md#binary-beta-gates).
Placeholder detection depends on an editor exposing its placeholder as a field
label, as ChatGPT does; other web editors were not surveyed. Not yet exercised:
a clean account or second Mac, macOS versions other than the build machine's,
upgrade testing beyond the maintainer's own data, uninstall, the destination
matrix, and fault and soak testing.
