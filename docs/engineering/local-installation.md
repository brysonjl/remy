# Local Remy installation

This guide covers signing and installing Remy at `~/Applications/Remy.app`,
optionally importing an existing Remy Dev setup, and rolling back. Start with
the [quick start](../../README.md#quick-start) for prerequisites and first use.
The Release app uses `io.github.brysonjl.remy`; Remy Dev retains
`io.github.brysonjl.remy.dev`. These Apple Development signed builds are for local
use, not notarized distribution. For recorded checks and their limits, see the
[migration validation record](../../validation/results/2026-10-19-local-migration/README.md)
and [latest local update](../../validation/results/2026-10-20-arc-paste-guard/README.md).

## Prepare and install separately

Keep the existing Remy Dev bundle and data as the fallback. Finish any recording and quit both Remy apps before either command. From the workspace root:

```sh
app/scripts/remy-local.sh prepare
# Review the prepared candidate, then explicitly install:
app/scripts/remy-local.sh install
```

[The helper](../../app/scripts/remy-local.sh) uses the existing local signing configuration and certificate to build Release, verifies its identity/signature and disabled updater, and prepares `.local/release/Remy.app`. Installation targets only `~/Applications/Remy.app`. It refuses symlinks, running Remy apps, unrelated bundles and a different signing team. An existing owned app is retained in a printed `.remy-local-backup…/Remy.app` directory. Neither command launches an app, migrates preferences/credentials, notarizes or uploads anything. No `sudo` is needed; `/Applications` is not supported by this helper.

Open the installed `~/Applications/Remy.app` explicitly after installation. Keep Dev closed. Native startup prevents Dev and Release from owning the shortcut/microphone together.

## Bring the Dev setup across

On first eligible Release launch, choose **Import from Remy Dev**. Import is offered only when the Release Application Support directory is absent and its managed preferences are pristine. Existing Release setup is never merged with or overwritten by Dev. **Not Now** exits before ordinary startup; it does not initialize a fresh setup behind the prompt.

The native bootstrap copies an allowlisted preference snapshot and these directories:

- `History` (including a verified SQLite snapshot), `Recovery` recordings and `CleanupProfiles`;
- downloaded `Models` and imported `Sounds`.

Source data remains under `~/Library/Application Support/io.github.brysonjl.remy.dev`; the copy belongs to `~/Library/Application Support/io.github.brysonjl.remy`. Dev preferences and keys stay in place. Copying models requires room for the copied data plus database work and a safety reserve; allow the progress window to finish. Unsupported or unsafe source data causes import to stop rather than overwrite a destination.

Credentials do **not** transfer. Enter the required API key again through Release's settings/SecureField; never extract it from Dev, Keychain tools, shell or environment variables. Copied profile references do not grant access to Dev's credential namespace. Launch at login starts off. The app-awareness preference (including an opt-out) and category styles are imported; remote surrounding-text sharing is not imported and starts off.

If interrupted after files were published, Release resumes the verified pending preferences receipt on reopening, with Dev stopped. A conflicting destination preference snapshot blocks resumption. Do not delete or edit `Migration/receipt.json`, manually merge folders, or repeatedly erase Release data to force import. Keep using Dev while a failed import is reviewed; its original setup is retained.

## Verify before changing the login app

Grant microphone and Accessibility access separately to the installed Release app when requested. Dev's grants do not transfer. Confirm the exact installed app in System Settings and reopen it if needed; do not use a blanket permission reset.

Before enabling Release at login, check:

- Ready status, the intended hotkey/cancel gesture and destination setting, and no permission banner;
- selected speech model and local model readiness without an unintended download;
- exact cleanup profile endpoint/model/prompt, followed by explicit key entry and a synthetic connection check;
- representative history/recovery entries and custom start/completion sound previews;
- real raw and cloud dictation into a disposable field, original/current destination behavior as selected, and quiet cancellation. For a terminal, keep the test line unexecuted.

Only after verification, explicitly turn off Dev's login item and enable Release's if desired. Use the respective app settings or macOS Login Items; the import does not register Release automatically. Avoid leaving both login items enabled. Keep the Dev bundle and source data until Release has proved reliable in daily use.

## Roll back without destroying data

Quit Release before returning to Dev. Disable Release's login item if enabled, then open the retained Remy Dev bundle and restore its login item only if wanted. Release-only history and later setting changes are not automatically copied back to Dev.

For a previous installed Release bundle, quit both apps, move the current `~/Applications/Remy.app` aside and restore the exact retained backup bundle to that path. Keep the displaced bundle for inspection. A bundle rollback does not roll back Release data or guarantee an older build can read a newer data schema; use Dev as the independent fallback if compatibility is uncertain. Do not overwrite either app's data directory with the other's.

Implementation: [native bootstrap](../../app/Dictate%20Anywhere/RemyMigrationBootstrap.swift), [allowlisted preferences](../../app/Dictate%20Anywhere/RemyMigrationPreferences.swift), [file/database migration](../../app/Dictate%20Anywhere/RemyLocalDataMigration.swift).
