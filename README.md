# Remy

Native macOS dictation with local speech recognition, optional transcript cleanup,
and a robot waiter at your service. Dictate into a text field using a global
shortcut, then keep writing.

Remy is an independent adaptation of [Dictate Anywhere](https://github.com/hoomanaskari/mac-dictate-anywhere).
See [attribution](ATTRIBUTION.md) and the [MIT license](LICENSE).

**Status:** local development builds are working. There is no packaged,
notarized Remy release yet. The setup below builds and signs the app for your
Mac; broader reliability and release validation remain in progress.
See the [release readiness checklist](docs/release-readiness.md) for the current
source-preview and binary-release gates. The product name is Remy.

## What it does

- **Local speech recognition** with pinned, verified Parakeet v2 model assets.
- **Optional transcript cleanup** through local models or a configured remote
  provider, with named profiles and custom vocabulary.
- **App-aware writing styles** for browsers, terminals, coding agents, chat and
  other destinations.
- **Flexible delivery:** return to the field where dictation started or use the
  field focused when cleanup finishes. Unavailable targets fall back to manual paste.
- **History and recovery** for reviewing results and continuing saved cancelled
  sessions, plus configurable recording sounds and a floating preview.

## Build from source

To compile or contribute without a signing certificate, use the verified Apple
silicon toolchain described below and run:

```sh
git clone https://github.com/brysonjl/remy.git
cd remy
app/scripts/remy-build.sh resolve
app/scripts/remy-build.sh build
app/scripts/remy-build.sh test -parallel-testing-enabled NO
```

These commands do not install an app. Dependency resolution requires internet
access on first use. See [contributing](CONTRIBUTING.md) for test scope and
[Development](#development) for additional commands. Running an interactive app
uses the signed local-install workflow below.

## Local installation

### 1. Get the source and configure signing

The supported local build workflow targets **Apple silicon**. It has been
verified with **Xcode 27.1 (27A9275), Swift 6.4 and the macOS 27.0 SDK**, and
**Xcode 27.2 (27B52), Swift 6.4.2 and the macOS 27.2 SDK**. Install
full Xcode and select it as your developer directory. The app's deployment target
is macOS 14; this is separate from the macOS version required to run Xcode.

You also need an existing **Apple Development** signing certificate and its team
configured in Xcode. The scripts use that identity without changing your account
or provisioning settings. Internet access is needed for the first dependency
resolution and speech-model download.

Clone the repository, or use your existing checkout:

```sh
git clone https://github.com/brysonjl/remy.git
cd remy
app/scripts/remy-build.sh resolve
```

Create `app/Config/Signing.local.xcconfig` with your ten-character team ID:

```xcconfig
DEVELOPMENT_TEAM = YOURTEAMID
```

Replace `YOURTEAMID` with your actual ID. This local file is ignored by Git.

### 2. Build and install

Finish any dictation and quit both **Remy** and **Remy Dev**, then run these
commands from the repository root:

```sh
app/scripts/remy-local.sh prepare
app/scripts/remy-local.sh install
open "$HOME/Applications/Remy.app"
```

`prepare` builds and verifies a signed Release app. `install` places it in
`~/Applications/Remy.app` and retains the previous installed copy for rollback.
For a development-only app or unsigned build checks, see [Development](#development).

If you already use Remy Dev, follow the first-launch import prompt and the
[local installation and migration guide](docs/engineering/local-installation.md).
Dev and Release keep separate data and permissions; provider keys must be entered
again in the new app.

### 3. Set up dictation

1. Grant **Microphone** and **Accessibility** access to the installed Remy app
   when prompted.
2. In **Speech Model**, choose **FluidAudio**, set **Expected language** to
   **English**, and select **Parakeet v2**. Click **Download Model**, or
   **Verify Existing Model** if offered, then
   **Prepare Model** when available.
3. In **Shortcuts**, choose your dictation key and **Hold to Record** or
   **Tap to Toggle**. Review the cancel shortcut and its hold setting too.
4. In **Transcript Cleanup**, choose **Off** for the first trial. You can add a
   cleanup model or provider profile afterward; enter API keys through the app.
5. Focus an empty TextEdit document or browser text field. Start dictation, speak
   a short sentence, then release the key or tap again to stop. Text should appear
   automatically. If Remy offers manual recovery, press **Command+V** to paste.

In **General → Text destination**, the default **Where dictation started** returns
to the original field. Choose **Where I’m focused when it finishes** if you want
the destination to follow your focus after cleanup.

## Privacy and delivery

Speech recognition runs on your Mac. Remote cleanup
providers receive transcript text and configured vocabulary; app identity and
writing style may accompany it when Context Awareness is enabled. Sharing nearby
text and document metadata with remote providers requires a separate opt-in.

Automatic paste checks the destination and clipboard before delivery. Terminal
payloads containing line breaks use manual recovery. History and temporary
recovery audio are stored locally, with controls in the app. See the
[privacy and data lifecycle guide](docs/privacy.md) for storage, retention,
deletion and remote-sharing details. Personal recordings, model weights,
credentials and generated build output are excluded from this repository;
speech test recordings are omitted. Optional ASR tests accept explicitly supplied
local fixtures; see [asset provenance](docs/engineering/asset-provenance.md).

## Development

Run commands from the repository root. The helper preserves the committed package
pins and keeps build caches under `app/.build/`.

```sh
# Compile without signing or installing.
app/scripts/remy-build.sh build
app/scripts/remy-build.sh build Release

# Run isolated app tests, or the dependency-free utility subset.
app/scripts/remy-build.sh test -parallel-testing-enabled NO
validation/run-tests.sh
```

For a signed interactive Debug app, configure signing as above, quit Remy and
Remy Dev, then run:

```sh
app/scripts/remy-dev.sh prepare
app/scripts/remy-dev.sh launch
```

This stages **Remy Dev** at `.local/run/Remy Dev.app`. The app test helper uses
an isolated test-host identity. For certificate-backed test runs and environment
details, see the [source guide](app/README.md) and
[local development guide](app/docs/engineering/local-development.md).

## Documentation

- [Release readiness and supported scope](docs/release-readiness.md)
- [Open source preparation verification](validation/results/2026-10-23-open-source-preparation/README.md)
- [Changelog](CHANGELOG.md)
- [Contributing](CONTRIBUTING.md) and [security reporting](SECURITY.md)
- [Privacy and data lifecycle](docs/privacy.md)
- [How this repository was published](docs/engineering/publication.md)
- [Third-party notices](THIRD_PARTY_NOTICES.md)
- [Local installation, migration and rollback](docs/engineering/local-installation.md)
- [Architecture and session contracts](docs/architecture.md)
- [Cleanup policy](docs/cleanup-policy.md)
- [Engineering baseline and validation evidence](app/docs/engineering/baseline.md)
- [Context-aware cleanup verification](validation/results/2026-10-20-context-awareness/README.md)
- [Arc paste-guard repair and live confirmation](validation/results/2026-10-20-arc-paste-guard/README.md)
- [Implementation plan](docs/implementation-plan.md) and [acceptance criteria](docs/evaluation/acceptance.md)

The application source lives in `app/`. Files under `research/upstream/` are
reference snapshots with their own notices, not executable build dependencies.
Dependency and model licenses apply separately from Remy's MIT license.
