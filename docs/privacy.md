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

**Spell names from the active window** is off by default and requires Context
Awareness. When it is on, and the session can use the result (Apple Speech, or a
cleanup method allowed to receive captured text), Remy reads the accessibility
text of the front window when you start dictating: labels, titles, values and
descriptions, up to 400 nodes, 400 characters per value and 75 ms. It never takes
screenshots, runs OCR, reads secure fields or their contents, or turns on
Chromium/Electron accessibility for this purpose. It runs only for email, work or
personal messaging, coding-agent and AI chat destinations; terminals, generic
browser pages, other apps, password managers and apps whose context is disabled
by an app rule are never read. Up to 25 names and identifiers are picked out on the
Mac. They go to Apple Speech as contextual strings, and to cleanup as a separate,
lower-trust request section: Apple Intelligence and loopback endpoints receive
them automatically; OpenRouter, remote Ollama/OpenAI-compatible endpoints and
remote saved profiles receive them only with the remote surrounding-text opt-in.
They never ride along with custom vocabulary. The read text and terms stay in
memory for that dictation only. They are not written to history (including attempt
prompts and profile snapshots), recovery files, settings or logs; the log records
only walk time, node count, term count and whether the time budget was hit.

Spelling corrections and snippets run on the Mac. Corrections are applied before
cleanup, so a provider receives the corrected transcript but not the heard-as
spellings. Snippets expand after cleanup: a provider may receive a spoken trigger
phrase, but never the expansion text. Vocabulary, heard-as spellings and snippets
are ordinary preferences. Saved history records the delivered text, including
expanded snippets.

**Transforms** read text only when you press a transform shortcut. Remy reads the
current selection of the focused field in the frontmost app (up to 3,000 words)
and sends it with that transform's instructions to your Transcript Cleanup
provider: Apple Intelligence on this Mac, or the configured Ollama,
OpenRouter, OpenAI-compatible or saved-profile endpoint. Sending the selection is
the purpose of the action you triggered, so it does not depend on the remote
context-sharing opt-in; no app name, surrounding text, document title/URL or
vocabulary is added. Remote endpoints must use HTTPS (a saved profile's explicit
insecure-HTTP choice is honored); loopback HTTP is allowed. S1-mini, Off and
vocabulary-only cleanup cannot run transforms. Secure fields, password managers
and apps whose context is disabled by an app rule are never read. The result
replaces the selection through the clipboard and stays on the clipboard, like a
dictation. Transforms are not saved to History and write no files.

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

