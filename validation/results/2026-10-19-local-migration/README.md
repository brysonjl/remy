# Local Dev-to-Alfred migration candidate

This is a local installation candidate, not a published or notarized release.
The signed candidate is installed at `~/Applications/Alfred.app`. First launch
reached the import prompt; real user-data import and post-import checks are pending.

## Implementation

- Release offers an explicit first-launch import before Settings or history opens.
  Dev and Release refuse simultaneous launch. Existing Release data is not merged
  or overwritten.
- A typed preference allowlist excludes credentials, login registration and
  unsupported/transient settings. Legacy history bytes are preserved exactly.
- History, Recovery, Models, CleanupProfiles and Sounds are copied to a private
  staging directory. The source is retained. Files are hash-checked; the staged
  SQLite cohort is recovered and backed up using SQLite, with schema, integrity,
  foreign-key and logical-content checks before publication.
- Capacity checks reserve room for the copy and database work. Static symlinks,
  hard-linked files and special files are rejected. The fixed macOS `/var` alias
  is normalized; arbitrary aliases are not resolved.
- A receipt and hashed preference snapshot support resuming publication before
  preference commit. Conflicting destination preferences block the import.
- API keys remain separate. A persistent cleanup-setup reminder asks the user to
  enter the Release key and test its connection. Microphone and Accessibility
  access require separate macOS grants.
- `app/scripts/alfred-local.sh` prepares a signed Release candidate and separately
  installs it to `~/Applications/Alfred.app`, keeping an owned predecessor.

## Automated evidence

| Check | Result |
| --- | --- |
| Initial compilation | Failed on inferred private generic type; explicit `Set<String>` fixed it |
| Build for testing, second run | Passed |
| Focused migration, first run | Failed because Foundation retained `/var` and the ancestor guard rejected it |
| Focused migration/startup/session, second run | 54 tests, zero failures |
| Full isolated ordinary suite | 1,072 tests, 60 opt-in skips, zero failures |
| Signed Debug preparation | Passed; stable Dev updated, predecessor retained |
| Signed Release preparation | Passed; staged `.local/release/Alfred.app` |
| Local installation | Passed; `~/Applications/Alfred.app` |
| First launch | Reached import modal; user interaction pending |
| Exact pins/toolchain/source hygiene | Passed; 160 selected source inputs |

Focused coverage includes source retention, existing-destination refusal,
source-restarted cancellation, static link refusal, low space, growing files,
WAL and hot rollback-journal recovery, altered preferences, pending-resume,
typed preference conflicts and mutual-exclusion identity selection. All fixture
data and preferences use isolated temporary roots/domains. The ordinary XCTest
host uses `app.alfred.dictation.testhost`.

Raw build/test logs and xcresults remain local and are ignored by Git.

## Limits and remaining checks

The filesystem guard assumes a stopped source app and does not claim confinement
against a malicious concurrent same-user writer. Copy verification does not
replace runtime model/profile/sound validation. Disk publication and preferences
are separate stores with idempotent recovery, not one atomic transaction.

Read-only review found no remaining blocker in the stopped-Dev transition.
Recovery audio, model and sound references derive their paths from the current
app's root; copied references cannot authorize deleting Dev's source files.
Profile credential identifiers resolve only in the Release Keychain namespace.

Remaining: first real import,
permission/key setup, model/profile checks, settings/history/sound continuity,
restart, and real TextEdit/terminal dictation. Broader A03–A11 reliability gates
remain open. Destination-mode live evidence is recorded separately in
`../2026-10-19-destination-setting/`.
