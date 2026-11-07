# Configurable dictation destination — 2026-10-19

User direction supersedes unconditional focus-change copy-only behavior:
General → Text destination defaults to **Where dictation started**, with
**Where I’m focused when it finishes** as the alternative. The preference is
captured at the start gesture, before permission/model/AX suspension, and stored
in the immutable session configuration and optional history profile snapshot.
Older history snapshots remain decodable without a schema migration.

After selected text and delivery intent are acknowledged, the original mode
validates retained process launch/window/field membership and editability before
activating its app, raising its window if necessary, and setting AXFocused on
that same field. No field text/selection writes, coordinate click or browser-tab
switch is used for restoration. Current mode captures one fresh target after
cleanup. Neither mode substitutes a different target after a failed resolution.
Each focus mutation checks the revocable delivery lease; cancellation remains
available before actual paste. Same-field restoration is a no-op. Target,
clipboard ownership and terminal newline checks still run at dispatch.

Native restoration has an 800ms overall deadline, bounded worker phases and
25ms AX messaging timeouts. An already-issued OS focus request cannot be recalled;
lease serialization bounds dispatch relative to cancellation, not external OS
state. Retained AX fields can become unavailable after browser navigation/tab
switch; those cases stay copy-only. Restoration retains the field's own current
caret, not a text offset captured earlier. Neither mode sends Return.

Initial focused run: 55 tests, zero failures (`focused-r1.log`). Subsequent final
source adds stricter native restoration deadline/editability checks and a start-
gesture preference-freeze regression; full ordinary suite passes **1,048 tests, 60 opt-in skips, zero failures**
(`full-r1.log`).
Exact pins/toolchain and narrow source hygiene pass (`foundation.log`,156 inputs).
No credentials/preferences/private history/audio were read/exported by shell.
No publishing or Git initialization.

Signed Debug preparation and Release pass. Stable app is
`.local/run/Alfred Dev.app`; previous app retained at
`.local/run/.alfred-dev-backup.UOhm6t/Alfred Dev.app`. Native UI verified both
options render without clipping; finish-time choice survives quit/reopen, then
original-field default was restored. Installed v2 explicitly prepared, Ready to
dictate with no Accessibility warning. Saved Profile/Ollama Cloud/gemma4:31b is
preserved. Latest user changes are sound masterOn, Purr/Pop at30%, microphone
boostOn; do not restore earlier off/Tink snapshots.

Pending live original-field trial: Chrome agent tab375977976, local fixture8765,
allfields/counters cleared, field1focused. User asked to start infield1, say
“The waiter returns to the original blue field,” clickfield3 duringrecording,
thenstop. Expectedonepasteinfield1,field3empty. Do not replace app or steal focus
while awaiting reply. Afterward test finish-time choice, then restore original.
Native app cross-app restoration and broader A07 matrix remain unpassed.

Live follow-up: user confirmed default returns tofield1 and leavesfield3empty;
UI read exact “The waiter returns to the original blue field.” However counter2
paste/3input cannot establishonedispatch; user recallsoneattemptbutisuncertain.
Freshsingle-attempt trial remainsrequired. Finish-time optionpassed onecontrolled
trial: field3 “The wader follows the final green field.”, paste1/input1, otherfields
empty. Waiter→wader is a transcriptionerror, separatefromrouting. Originalmode
restored beforeDevquitfor migrationtests. Cross-apprestore remainsopen.

2026-10-20 retry-qualified original-field check: user confirmed the expected
routing and then confirmed retrying/editing. Field 1 contained one visible
“The water returns to the blue field.” with two paste/three input events; other
fields were empty with zero counts. Native History showed two completed synthetic
dictations at 12:04 AM, consistent with that retry. Routing passes; this is not
an isolated single-attempt event-count measurement. Do not interpret the counter
alone as evidence of a duplicate dispatch from one session.
