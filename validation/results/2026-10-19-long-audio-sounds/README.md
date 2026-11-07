# Custom sounds and long dictation recovery — 2026-10-19

Local development validation only. No publication or completed A03–A11 gate.

## Behavior

- General → Audio has independent recording-start and completed-dictation sound
  choices, None, Preview and local audio import. Existing master switch/volume
  remain. Clips are limited to 10 seconds/10 MiB, decoded before acceptance and
  copied to private Alfred storage. Start follows microphone readiness; completion
  follows delivery/copy outcome, not merely microphone stop. Cancel/failure do not
  play completion. Tests suppress application event playback.
- With history enabled, each recording gets a temporary recovery CAF. Success
  removes it; recognition/persistence failure preserves it. Intentional cancel
  still follows its separate retention preference; History off writes no audio.
- Final or earlier ASR processing failure and pending-queue overflow are explicit
  incomplete outcomes, never silently accepted previews. The recovery writer has
  a 30-second queued-PCM bound. Its errors stop capture and are checked again after
  draining the final writes. Available final text can be retained alongside partial
  audio without granting delivery permission.
- Metadata failure keeps existing CAF and in-process text. Restart discovers
  readable orphan CAFs. Normal shutdown drains the accepted tail. Recovery expires
  after 24 hours; History → Recover text saves text without automatically pasting.
- The source-ahead A04 deletion/retry, A06 deadline/use-raw and A07 focus/clipboard
  guards are included in this validation slice.

## Evidence

`focused-r1.log` failed compilation because the new long test attempted to assign
a computed setting; the fixture now snapshots/restores the mutable vocabulary.
`focused-r2.log` executed 129 tests, one skipped, with two assertions failing in
one new shutdown test. AVAudioFile returned 31,744 then 259 samples, all 32,003
intact; the test incorrectly assumed a single read. `recovery-r3.log` passed all
16 recovery tests after using a read-until-end loop. Later final-write and earlier
ASR-ingestion failure regressions are covered by the ordinary run recorded below.

`offline.log`: 40 tests, zero failures. `foundation.log`: exact dependency pins,
toolchain and narrow hygiene pass across 155 selected source inputs. Unchanged
pins: FluidAudio 0.17.5, llama.swift 2.10549.0, Sparkle 2.9.6.

### Actual long file replay

`long-replay-r1.log`: one opt-in signed test passed under the isolated
`app.alfred.dictation.testhost`. Synthetic `say` input, no microphone, cleanup,
clipboard or insertion; FluidAudio offline mode enabled. The exact 22-file v2
model source was verified and copied/adopted into testhost storage. No credential
or user recording/history inspection occurred.

| Measurement | Observed |
| --- | --- |
| Audio | 1,345.437 seconds (22m25.4s), mono 16 kHz |
| Reference/output words | 3,461 / 3,445 |
| Simple word error rate | 3.3805% |
| Exact checkpoint phrases | 23/25 (92%) |
| Final tail | Present |
| File decode | 3,649.946 ms; RTF 0.002713 |
| Model preparation | 14,161.623 ms |
| Whole-process peak RSS | 1,259,257,856 bytes (~1.17 GiB) |

The two exact-marker misses were homophones: section 20 produced “plumb bicycle”
for “plum bicycle”; section 22 produced “Perl notebook” for “pearl notebook”. Both
sections were present, found markers were ordered, and the full final phrase
“purple compass waits beside the silver lantern” survived. The simple WER includes
number/word formatting. This is useful synthetic evidence, not a semantic corpus.

Retained fixture: `.local/long-dictation/synthetic-22m-20261019/`.
Retained result: `.local/long-dictation/results/long-replay-2E8F743D-0242-485C-B027-0D830D16745C/`
contains `raw-transcript.txt`, `metrics.json` and flushed `events.jsonl`. Audio hash:
`ddabebd9237bb0252216e323d3c2510bbd248dcb3c649e189b4370d2d59e7964`.
See [reproduction instructions](../../long-dictation/README.md).

## Limits

File replay bypasses real-time microphone queues, route changes and hotkeys.
The pinned SDK has a 150-token per-window cap and overlap merging; this run does
not prove lossless recognition for arbitrary speech. Decode timing excludes
preparation; RSS includes preparation and the whole test host. Core ML emitted
shape-inference diagnostics during preparation, then initialization and replay
succeeded; the log is retained.

Total recording/disk quotas, the planned session-duration bound, hard crash/power
loss, actual low-disk pressure, 100 real captures, destination matrix and soak
gates remain open. Unflushed or unreadable crash audio cannot be guaranteed.
Metadata-failure text is memory-only until a durable write succeeds. Recovery
retention is 24 hours, not indefinite backup. No remote Git operation occurred;
the requested Git setup is recorded near the end of the implementation plan.

`ordinary-r1.log`: **1,033 tests, 60 opt-in skips, zero failures**, including all
nine ASR failure regressions and the final-write failure integration test.
`release.log`: Release build passed. The subsequent History section copy changes
“Cancelled sessions” to “Recovery sessions” to accurately include failures/quit.
Signed staging and final native UI results follow below when completed.

Source checkpoint before that copy-only polish:
`.local/checkpoints/sounds-long-recovery-validated-20261019.tar.gz` (source and
evidence, not a user-data backup; excludes generated caches and local signing).
Final staged source/evidence checkpoint:
`.local/checkpoints/sounds-long-recovery-staged-20261019.tar.gz`, SHA256
`b327ba3dee03736c38d75aa6c3aa5833eda4173aa9368182ca6c5dd825681a06`.

## Signed app and native UI

`signed-prepare.log` and `release-final.log` pass, including the recovery-section
wording. Stable app `.local/run/Alfred Dev.app` is this slice; previous bundle is
`.local/run/.alfred-dev-backup.QSrIZk/Alfred Dev.app`. `foundation-final.log` passes.

Native UI import of `.local/long-dictation/sound-smoke.wav` (synthetic 0.2sec WAV)
reported success and selected Custom audio. Preview returned without fallback
warning. After clean quit/reopen, selection and preview still worked. The dark
General → Audio card was visually inspected with no clipping. Restored original
Tink/Pop, masterOff, volume30%. Imported test copy remains harmless unused local
audio; no personal audio was opened. Speaker audibility was not independently
measured, and live event timing still needs the next user capture check.

Saved Profile → Ollama Cloud (`https://ollama.com/v1`, `gemma4:31b`) persisted and
synthetic Test connection passed. Right Option toggle, immediate Escape and cancel
retentionOff verified. Installed v2 explicitly prepared; Ready to dictate with no
Accessibility banner. No key read/export. User was asked for a live Chrome ordinary
insertion and same-window focus-change trial; result remains pending. The user
subsequently confirmed “the Book Friday 2 test passed as expected”; the earlier
Friday booking/saved-profile smoke is now recorded as passed by user report.
Exact output and counters were not independently re-inspected for that confirmation.
