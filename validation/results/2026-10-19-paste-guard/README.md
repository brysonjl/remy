# Paste guard runtime investigation — 2026-10-19

The user reports a “press Command+V” copy-only result in Terminal and TextEdit.
Six recent production traces show `insertion.deliver` returning copiedOnly in
0–1ms before any paste attempt. No field text or clipboard was inspected.
This is a reported normal-use regression to investigate, not a passing A07 gate.

`before-diagnostics.log` retains only bounded Performance delivery events.
`diagnostic-prepare.log` passes signed Debug build/staging. Stable app now has
diagnostic-only changes in AlfredInsertionTarget and TextInserter; paste policy
is unchanged. Logs contain fixed reasons/AX attribute names, allowlisted role
classes, capability flags and numeric status/depth. No dictated text, field text,
window titles, URLs or clipboard contents. A rejected proof may probe whether
AXSelectedText is settable, without reading that value or permitting paste.

Prior app retained at `.local/run/.alfred-dev-backup.3Re2tn/Alfred Dev.app`.
No Release/test rerun is claimed for this diagnostic-only build yet.

A fresh empty TextEdit “Untitled 4” was created for reproduction. Existing
“Book Friday 2” document was left untouched; only its window identity was
observed. User-confirmed earlier Friday smoke remains passed. The Chrome focus
change gate remains unpassed; resolve this unchanged-target regression first.

## Confirmed cause and repair

`textedit-diagnostic.log` records three reproductions. Each captured AXTextArea
reported writable AXValue and writable AXSelectedText, while optional AXEnabled,
AXEditable, AXReadOnly and AXProtectedContent were absent. The initial policy
required enabled==true, rejecting the otherwise writable field. Delivery logged
automatic=true, targetProof=false and targetActive=true, then returned copy-only.

The repair treats absent AXEnabled as unknown rather than disabled. Explicit
enabled=false still rejects, as do read-only/protected/secure/unsupported-role
facts, AX IPC errors/malformed values, changed field/window/process, clipboard
replacement and terminal newlines. Nonterminal controls still need affirmative
writing capability. The existing narrow known-terminal text-surface exception
remains; AXSelectedText does not acquire any new authorization role.

`repair-tests-r1.log`: 27 tests, zero failures (nine target-policy tests and 18
session integration tests). Added the observed writable-native-field/missing-flag
regression and negative assertions. An independent read-only agent review agreed
with the narrow policy correction. Signed repaired staging and live results will
be recorded below; no live repair pass is yet claimed.

`repair-prepare.log` passed signed Debug build/staging. Stable app is the repaired
build, previous diagnostic bundle retained at
`.local/run/.alfred-dev-backup.xeFzIY/Alfred Dev.app`. `foundation.log` passes exact
pins/toolchain/hygiene155 inputs. Native UI shows installed v2 prepared and Ready
to dictate without an Accessibility banner. Existing empty Untitled4 was focused;
user was asked to dictate “The waiter keeps the blue folder” without changing
focus and report automatic insertion. Latest user microphone-boost change is kept.
`repair-release.log` subsequently passed. The user confirmed repaired automatic
TextEdit insertion. Native UI independently read one copy of “The waiter keeps
the blue folder” in the now-renamed “Blue Folder” document. `repair-live.log`
shows captureSuccess, ancestry depth2, repeated matchSuccess and an AX paste
dispatch followed by delivery success. One user-confirmed real case, not a broad
matrix. Terminal automatic/unexecuted-line retest subsequently passed by user
report. `repair-live-terminal.log` shows a following depth3 capture/recheck and
AX paste/delivery success. Terminal contents were not independently read. The
two reported app regressions are resolved in these live trials; Chrome focus
change remains unverified.

Current pending Chrome trial: existing fixture tab375977976 had all three fields
empty and zero paste/input counters, then field1 was focused. User was asked for
one unchanged-field phrase (“The waiter keeps the final blue word”), then to start
“Keep this as a manual recovery check” in field1, switch to field3 during capture,
and stop. Expected first single insertion, second copy-only without manual paste.

Source/evidence checkpoint `.local/checkpoints/paste-guard-repaired-20261019.tar.gz`,
SHA256 `c6538164572abcbdc297ddb327d27fc26bbb4873b997130507d0be68bbef0477`.

Chrome follow-up passed by user report and bounded fixture inspection: primary
contains “The waiter keeps the final blue word.”, one paste/two input events;
fields2/3 remain empty with zero paste/input events. The second recovery phrase
is absent from all fields. This closes this particular old-policy trial, not the
full A07 matrix. The user then requested a configurable destination with original
field restoration as default and finish-time field as alternative; that newer
product direction supersedes unconditional focus-change copy-only behavior.
