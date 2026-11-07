# A02 local session validation

Environment and package pins are unchanged from A01. These ordinary runs use the
existing development signing identity, production startup suppressed, no parallel
test workers, and no opt-in live provider/model/hardware suite.

- `session-tests-r1.log`: compile failed because the new cleanup runtime lacked
  its FoundationModels import; repaired.
- `session-tests-r2.log`: initial focused run passed 114 tests, one skipped.
- `ordinary-suite.log`: 820 tests, 59 skipped, 16 failed assertions. Older fixtures
  bypassed the new coordinator by assigning recording status directly. The run
  also found a real cancellation-recovery checkpoint regression and split trace
  identifiers. Fixtures now enter through fake-engine `startDictation`; completed
  decoder text can checkpoint the same session's optional recovery after cancel
  without gaining clipboard/history authority; request and session share an ID.
- `ordinary-suite-r2.log`: 831 tests, 59 skipped, zero failures. Covers new session
  snapshots, delayed recognition/cleanup, duplicate activation/finalization,
  pending permission cancellation, worker delivery revocation, recovery,
  sleep/wake behavior, busy feedback and conditional trace cleanup.
- `offline-tests.log`: all 33 offline tests passed.
- `foundation-checks.log`: exact package pins/toolchain and narrow source hygiene
  passed. The selected input count is a point-in-time scan, not a secret audit.

- `hotkeys-final.log`: all 41 focused hotkey/cancellation tests passed after the
  final Caps Lock wake-rearming repair (the lock latch is not a held modifier).
- `signed-prepare.log` and `release.log`: signed Debug staging and Release passed.
  Prior stable A01 app retained at `.local/run/.alfred-dev-backup.Rmsuru/`.
- `foundation-checks-final.log`: exact lock/toolchain and 134 selected source
  hygiene inputs passed on the final source.
- Native UI inspection: Ready to dictate, no Accessibility warning; user settings
  retained (Right Option toggle, immediate Escape, cancelled-session retention
  off, Custom/OpenAI-compatible Ollama endpoint and gemma4:31b, Ixway/BEGL, fillers
  off). Credentials were neither inspected nor exported.

Cloud and raw TextEdit smoke and immediate quiet cancellation passed; exact final
text was independently read, with visible overlay/cancellation/single-insertion
observations confirmed by the user. Cloud cleanup was restored and verified.
[Live record](live-checks.json). **A02 local gate is complete.** This does not claim physical sleep, Fn/chord,
production cleanup deadlines, durable SQLite history or A07 field-aware delivery.
The prior independently observed Chrome field-change failure remains open.

See [session contracts](../../../docs/engineering/session-contracts.md) for the
reviewed handoff seam and explicit A03–A07 ownership limits.
