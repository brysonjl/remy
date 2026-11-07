# Privacy and stored-data lifecycle

The supported speech-recognition route processes audio on the Mac. Model downloads
and configured remote transcript cleanup still require network access. A loopback
endpoint is treated as local by Remy; that does not prove the server itself
avoids forwarding data elsewhere. Offline behavior and traffic need validation for
the actual runtime/provider configuration; this document does not certify zero egress.

Remote cleanup receives the dictated text, configured vocabulary and cleanup
instructions. When Context Awareness is enabled, app identity, destination
category and writing style may accompany the request. Nearby text and document
metadata require the separate remote-sharing opt-in. Awareness defaults on for
new installations; remote surrounding-text sharing defaults off. Settings are
frozen at recording start, so changing consent does not broaden a session already
in flight. Off and vocabulary-only cleanup do not collect cleanup context.

Remy's application data is scoped to its bundle identity under
`~/Library/Application Support/<bundle identifier>/`. Release uses
`io.github.brysonjl.remy`; Dev uses `io.github.brysonjl.remy.dev`. Deleting data in one
does not clear the other. Provider secrets are held separately in Keychain;
ordinary preferences are held in the app's UserDefaults domain.

| Data | Location and lifetime |
| --- | --- |
| Transcript history | `History/history.sqlite`; saved raw/selected text, attempt input/output, base instructions, provider/model metadata and delivery records remain until deleted. There is no automatic age-based transcript retention control. The full generated context prompt is not stored as a history attempt. |
| Rollback snapshots | `History/history-before-v2-<UUID>.sqlite` and recognized JSON files in `History/Legacy Backups/`; retained for manual rollback until explicit Clear All or manual removal. Per-entry deletion does not edit these snapshots. |
| Recovery audio and metadata | UUID-named `.caf`/`.json` files in `Recovery/`; history-enabled sessions may spool audio to protect interruptions/failures. Successful sessions discard their spool. Recovery entries expire after 24 hours; cleanup occurs while Remy runs or reloads the store, not while the app is closed. An entry actively being used is retained until released. Recovery storage is marked excluded from system backup. |
| Models | `Models/`; retained until removed through model controls or manual uninstall cleanup. |
| Explicit exports | User-selected destinations; these intentionally contain selected transcript data and require separate deletion. |

**Save new dictations** defaults on. Turning it off applies to subsequent
recordings: their transcripts remain in memory for delivery and no recovery audio
is created. Existing saved history, recovery files and rollback snapshots remain.
This setting is distinct from deletion.

History → **Clear All** stops active dictation and deletes active history records,
the legacy history preference, recognized history rollback/legacy snapshots and
recognized recovery audio/metadata, including orphaned recovery files. The
confirmation explains that history rollback is lost. It remains available when
the visible history is empty, so backup-only stores can be cleared. New recordings
and recovery actions are blocked during the operation. Unknown files, links and
special filesystem objects are preserved rather than traversed. A suspicious link
or a filesystem/database failure produces an incomplete-deletion warning; repair
the problem and explicitly retry Clear All. Failed disk deletions may reappear
after restart. No automatic destructive retry runs at startup.

Clear All keeps models, settings and Keychain credentials. It does not clear the
clipboard, another app identity's data, manually exported copies, or copies
already captured by system/third-party backups. History and its rollback snapshots
are not excluded from system backups by Remy. Files and SQLite rows are removed
using ordinary filesystem/database operations; Remy does not promise forensic
erasure from storage media or backups. Default diagnostics are metadata only;
see [the diagnostics policy](engineering/diagnostics.md) for third-party logging
limits and review logs before sharing them.

For uninstall, quit Remy and remove its app bundle. Application deletion alone
does not remove its Application Support folder, preferences or Keychain items.
Use Clear All first if desired; remove remaining model/application data only for
the intended bundle identity, and remove provider credentials through the app's
credential controls before uninstalling. User-owned exports, clipboard contents
and external backups require their own cleanup.

