# Acceptance and evaluation

All numeric values below are **proposed acceptance targets**, not measurements. No model or app was executed during planning. This document is the canonical release-gate policy if exploratory research notes differ.

## Environment and corpus

Initial target: Apple M4 Pro, 48 GB RAM, macOS 27.0.1. Record exact hardware/OS/Xcode/app/SDK/model/quantization/digest, microphone and route, power mode, thermal state, network and cleanup profile for every benchmark. Do not combine runs from different model revisions under one result.

Collect at least 100 representative utterances (30–60 minutes total) for tuning and 50 separate held-out utterances. Keep a verbatim reference for ASR and an intended-clean reference for transformation; raw WER must not reward deleted fillers. Include short messages, paragraphs, code identifiers, Ixway/product names, URLs, dates/amounts, negation, uncertainty, corrections, quotations, noise, quiet speech, long pauses, silence, and clipped endpoints. Add one ten-minute monologue and one maximum-duration session. Store personal audio locally and separately from committed synthetic fixtures.

Compare the same captured audio where replay/import permits it. If Spokenly cannot consume identical audio, label its real-microphone comparison uncontrolled and use correction effort/user preference as supporting evidence rather than claiming an exact WER comparison. Keep Spokenly settings/model/prompt fixed during baseline collection.

## Quality gates

| Dimension | Proposed gate | Evidence |
| --- | --- | --- |
| ASR accuracy | Held-out WER no more than 1 absolute percentage point worse than comparable baseline; no systematic entity/negation regression | Per-clip raw outputs, references, WER calculation and error categories |
| Meaning preservation | Zero changed negations/critical numbers/invented facts in a designated 50-case critical set | Human-reviewed pass/fail with reasons; zero observed is not proof of zero future risk |
| Cleanup usefulness | At least 95% of held-out ordinary cases accepted without meaning edits; compare correction effort with existing prompt | Blinded/manual ratings and edit counts, not just string equality |
| Silence/tail | No invented transcript on deterministic silence fixtures; no lost final words or duplicated overlap | Synthetic tail/chunk-boundary tests and real recordings |
| Vocabulary | Correct configured whole words/phrases; no unintended substring or cascade changes | Unicode, case, punctuation, phrase-overlap and scope fixtures |
| Long input | No silent truncation, sentence loss or reordered chunks | End-to-end sample accounting and manual reference review |

Formatting differences may be acceptable; unsupported actions, answers to dictated questions, invented content and changed meaning are not. Programmatic output checks supplement human review, not replace it.

## Latency and resources

Instrument `hotkey_received`, `capture_started`, `first_audio`, `first_partial`, `stop_received`, `capture_drained`, `raw_final`, `cleanup_requested`, `first_answer_token`, `cleanup_complete`, `history_committed`, `clipboard_written`, `paste_dispatched`, `paste_verified_if_available`. Use a monotonic clock for durations. Count failed/timed-out samples explicitly; do not hide them from percentiles.

For warm short dictation (up to 30 seconds), use at least 100 samples per shipping pipeline configuration. Report cold first-run download/compilation/load separately and include 10 cold launches plus cache-warm restarts. A small p99 sample is directional, not statistically precise.

| Stage | Initial target on this Mac |
| --- | --- |
| Hotkey to recording acknowledgement | p95 ≤100 ms when engine is ready |
| First useful preview | p95 ≤1.5 s from speech onset |
| Stop to final raw ASR | p95 ≤700 ms for ≤30-second clips |
| Stop to raw copy/paste dispatch | p95 ≤1 second |
| Stop to completed cleaned copy/paste dispatch | p50 ≤2 s; p95 ≤4 s for short clips with the chosen warm cleanup profile |
| Cleanup timeout recovery | Default 8-second deadline produces visible raw fallback without later replacement |
| ASR memory | Initial target ≤2 GB peak for ordinary ≤2-minute clips, measured separately from cleanup/server memory |
| Long-run resources | No unbounded post-idle RSS growth, sustained memory pressure or runaway preview backlog across 1,000 cycles |

Report remote latency separately because network/provider time is not app-controlled. Measure local ASR with local cleanup resident and under simultaneous development workload. Download size and upstream realtime factor do not establish end-to-end latency or RAM. When targets fail, record tradeoffs and explicitly revise the chosen configuration or scope; do not relabel an unmeasured target as passed.

## Destination matrix

Run at least 20 insertion trials per available target class, with both cleanup on/off and at least five focus-change or cancellation trials. Record actual app versions. Ask the user to name their exact coding-agent/browser tools before the final daily-driver gate; missing apps remain untested, not assumed compatible.

| Destination | Required scenarios |
| --- | --- |
| Safari and Chrome/Chromium | textarea, contenteditable, browser chat composer; field/tab/window changes; no auto-submit |
| Terminal.app and iTerm if installed | zsh/bash prompt, single line, trailing newline, multiline, focus change; no Return; multiline copy-only default |
| Terminal coding agent | prompt entry vs shell context; safe defaults when child process cannot be identified reliably |
| VS Code/Cursor if installed | editor, chat composer, integrated terminal, focus changes within same process |
| Native coding-agent app | actual prompt field, running-task state, follow-up field and window changes |
| TextEdit | plain/rich text control baseline; selection and cursor spacing |
| Blocked destination | secure field, app exited, revoked Accessibility, absent focused element |

Clipboard contains the exact selected final output after normal delivery. An external clipboard modification must not cause Remy to paste unrelated content. A dispatched event is logged as dispatched; only verified destinations may claim verified insertion. There is no universal acknowledgement contract. Test alternative keyboard layouts and held modifier release.

## Fault matrix

| Injected event | Required outcome |
| --- | --- |
| Cancel while preparing/recording/finalizing/cleaning | Stop work and never initiate delivery; ignore all late events |
| Duplicate stop/key repeat | One finalization and at most one dispatch |
| Sleep/wake, unplug/replug, Bluetooth route/sample-rate change | Explicit stopped/recovered state, usable next session, preserved completed text |
| Microphone/Accessibility permission revoked | Accurate capability-specific message; copy/history still usable where possible |
| Provider 401/429/5xx, network loss, malformed/truncated stream | Visible failed attempt; corrected-raw fallback; no late overwrite |
| Model missing/corrupt/incompatible | Actionable preparation error; no silent download/model swap |
| Disk-full/history commit failure | “Not saved,” retained in-memory result, explicit recovery/copy action |
| Terminate ASR worker if used | UI survives; no automatic paste; recovery only within actual spool guarantees |
| Kill app before/after dispatch | Pending delivery is uncertain after restart; no automatic retry |
| Kill during migration/update | Intact previous data/model or recoverable rollback; no mixed-version bundle |
| Config/model change during dictation | Active snapshot unchanged; later sessions use new config |
| Late completion from old session | No history/output mutation in new session |

Run 1,000 automated lifecycle cycles, including injected failures, plus real microphone cycles and one eight-hour mixed-use day. Require zero unexplained app crashes, duplicate dispatches, stale-session text or silent loss of produced final transcripts during the gate. Hardware tests cannot be substituted with mocked audio/UI events. Repeat failed scenarios after fixes and run affected regression tests; do not restart the entire suite without reason.

## Data and release checks

- Local ASR/local cleanup works with external network blocked after initial installation; inspect helper/server routing to detect loopback-to-cloud proxies. Model downloads and remote cleanup are distinct visible routes.
- API keys are Keychain-only. Settings/diagnostic exports and logs contain no secrets or transcript content by default; explicit history exports intentionally include the selected transcripts but never credentials. No telemetry network traffic. Remote cleanup payload contains only the intended transcript/prompt/vocabulary.
- History preserves raw/final/attempt/delivery records through restart; retention, delete, clear-all, FTS cleanup, export and migration are tested, including late callbacks after deletion. Intentional history-off permits delivery while writing no transcript/prompt/attempt/audio files; simulated disk-write failure still surfaces “Not saved.” Confirm audio recovery policy and backup exclusions when enabled.
- Independent updater configuration cannot install the upstream product over Remy. Manual app/model rollback and schema incompatibility behavior are documented. Dependency/model notices and source availability match shipped binaries.

## Result template

```text
Run ID/date:
App/upstream/patch revisions:
Machine/OS/toolchain/microphone:
ASR runtime + model manifest digest:
Cleanup route/model digest/settings/prompt version:
Corpus version and held-out split:
Sample counts, failures/timeouts:
Quality scores and critical failures:
Cold/warm p50/p95/p99 by pipeline stage:
Peak/idle memory, power/thermal observations:
Destination results (dispatched vs verified):
Fault/soak results:
Decision: pass / repair / scoped exception with owner:
Unmeasured areas and next action:
```

Do not replace absent values with upstream benchmark claims. The user can keep Spokenly installed until the measured daily-driver gate passes.
