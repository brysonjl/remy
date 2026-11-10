# Changelog

Remy's version numbers start at 0.1.0. Historical Dictate Anywhere release notes
describe the upstream application.

## 0.2.0 — 2026-11-09 (early beta prerelease)

- **Spelling corrections:** give a vocabulary term the spellings recognition
  produces for it ("heard as"), and Remy replaces them as whole words before
  cleanup, even with cleanup off.
- **Snippets:** say a trigger phrase to insert saved text such as a link,
  address or sign-off. Expansions are added after cleanup and never sent to a
  cleanup provider.
- **Vocabulary import:** add terms and heard-as spellings from a CSV file.
- **Custom writing styles:** edit the built-in styles' instructions and add your
  own, then choose them per kind of app.
- **Transforms:** select text in any app and press a shortcut to rewrite it with
  a saved prompt. Polish and Prompt Engineer are built in, and you can add up to
  eight of your own.
- **Spell names from the active window** (off by default): Remy reads the
  focused window's accessibility text to spell names you say the way they appear
  on screen. No screenshots, no new permission, and the names are never saved.
- Cleanup no longer inserts a vocabulary term or on-screen name in place of
  unrelated words; Remy restores what you said.

See the [0.2.0 release record](validation/results/2026-11-09-release-0.2.0/README.md)
for the published artifact and checks.

## 0.1.0 — 2026-11-07 (early beta prerelease)

First downloadable build: a notarized Apple silicon DMG with manual updates.

- Local Parakeet v2 speech recognition with revision-pinned, verified downloads.
- Optional local or remote cleanup, named profiles, vocabulary corrections and
  consent-controlled application context.
- Target-aware delivery, clipboard checks, manual recovery and visible cleanup
  fallback reasons.
- Isolated development/release storage, transcript history, interrupted-session
  recovery and explicit Dev-to-Release migration.
- Clear All includes recognized history rollback snapshots and recovery files,
  with an incomplete-deletion warning and explicit retry.
- Source contribution/security guidance, versioned third-party notices, privacy
  documentation and repeatable publication-hygiene checks.
- Apple silicon manual-update DMG preparation with fail-closed notarization,
  stapling, validation, bundle-policy and dependency-pin checks.
- Recording starts with the Purr sound and completes with Pop by default.

See the [0.1.0 release record](validation/results/2026-11-07-release-0.1.0/README.md)
for what was verified, and [release readiness](docs/release-readiness.md) for the
remaining beta gates.
