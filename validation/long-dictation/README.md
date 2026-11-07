# Synthetic long dictation replay

This opt-in check uses original synthetic speech, an installed macOS voice, and the pinned installed Parakeet v2 bundle. It runs file recovery decoding, with cleanup disabled and FluidAudio offline mode enabled. It does **not** exercise live microphone capture, the hotkey, overlay, clipboard, target insertion, or a cloud cleanup provider. A successful replay does not close the 100-real-capture A03 gate.

A synthetic fixture was generated at `.local/long-dictation/synthetic-22m-20261019` using the installed voice `Samantha (English (US))`: measured duration **22m25.4s**, audio **43,058,082 bytes**. The opt-in replay subsequently passed **1 test, 0 failures** on 2026-10-19; measured evidence follows below. Review the source and use a new output directory for every generation. No existing fixture is overwritten, and failures retain the reference, metadata, and any audio already written.

```sh
python3 validation/long-dictation/generate.py --plan-only \
  --output "$PWD/.local/long-dictation/plan-review"

# After reviewing the plan: 20+ minutes of speech to FILE, never loudspeakers.
python3 validation/long-dictation/generate.py \
  --voice 'Samantha (English (US))' --minutes 22 --rate 145 \
  --output "$PWD/.local/long-dictation/synthetic-20m"
```

The generator checks the explicit voice against `say -v '?'`; it never requests voice installation. Use the exact installed voice name, including language suffixes; if that name is absent, choose another already-installed English voice explicitly. `say` writes mono16kHz PCM AIFF. `afinfo` measures duration, and the fixture is marked ready only if it is at least1200seconds. Speech timing varies by voice. Expected audio size is roughly40–55MB. The JSON records actual duration/size/SHA256, reference/marker hashes, voice/rate, exact arguments, source/generator hashes, and generation status. It contains section checkpoint phrases and a distinct final tail marker.

The root integrator runs tests serially with the existing isolated build script, explicitly forwarding these variables into the test host (Xcode command-line environments typically require the `TEST_RUNNER_` prefix):

| Variable inside test host | Required value |
| --- | --- |
| `RUN_REMY_LONG_DICTATION` | `1` |
| `REMY_LONG_FIXTURE_DIR` | Absolute directory containing ready fixture.json/audio.aiff/reference.txt/section-markers.json |
| `REMY_LONG_RESULTS_DIR` | Existing absolute result directory, e.g. `.local/long-dictation/results` |
| `REMY_LONG_MODEL_BUNDLE` | Explicit approved verified22-file v2 bundle directory; never a shared FluidAudio cache discovered automatically |
| `REMY_LONG_MAX_WER` | Optional word-error-rate limit; default0.20 |

Select `RemyLongDictationTests/testSyntheticTwentyMinuteInstalledParakeetRecoveryReplay`. The test refuses any bundle identity other than `io.github.brysonjl.remy.testhost` before accessing app state. The approved model source is hash-checked and copied using FileManager into the testhost-owned legacy model path only if absent. Existing legacy files are never replaced/deleted. The model store verifies/adopts/activates the testhost copy; source assets are never mutated. This fixture setup can require about900MB additional disk space without APFS cloning, plus model runtime memory. Testhost model copies remain installed for explicit reruns. ModelHub offline mode and testhost speech/cleanup settings are restored afterward. The inherited Settings singleton may initialize testhost-scoped credential entries; this test never accesses credential properties, production preferences/history, or user audio.

Each run creates a unique retained result directory. `events.jsonl` is flushed after lifecycle/resource events. `raw-transcript.txt` is written atomically immediately when decoding returns, before checking accuracy. `metrics.json` records elapsed inference/real-time factor, process high-water RSS, raw hash, WER, checkpoint coverage, and final-tail retention. RSS is the entire test process high-water mark (including model preparation), not incremental model-only memory. Resource events do not expose partial transcripts; the current decoder returns a final string only.

Default checks require the final tail, at least90% checkpoint phrase coverage, nonempty output, and WER<=0.20. Numeral formatting counts as an error in this deliberately simple WER. Inspect raw/reference and missing markers rather than treating the score alone as semantic proof. On any decode/quality failure, preserve the same audio/reference and result directory; rerun this **raw, no-cleanup** test against the same hashes before changing models, cleanup prompts, or source audio. Never substitute previews for a failed final result. A thrown decoder error may provide no recoverable partial transcript; retain the file and journal for rerun.

`testRecoveryFixturePreservesEveryQueuedChunkAndFinalTail` is an ordinary hardware-free recovery-store test: it writes bounded synthetic PCM chunks, preserves/reopens the file, and checks every sample including the final partial chunk. It does not wait for20minutes or require installed models.

## Measured replay — 2026-10-19

The retained fixture at `.local/long-dictation/synthetic-22m-20261019` was decoded offline through the production v2 file-recovery route, using model revision `ee09c569f73759e6d44c9bd16766f477b2b36d39`. The isolated opt-in test passed 1 test with 0 failures.

| Measurement | Observed |
| --- | --- |
| Audio duration | 1345.4370625 seconds (22m25.4s) |
| Model preparation | 14161.623 milliseconds |
| File decoding | 3649.94625 milliseconds |
| Real-time factor, excluding preparation | 0.002712833 |
| Whole-process peak RSS | 1,259,257,856 bytes (about 1.17 GiB) |
| Reference/output words | 3461 / 3445 |
| Simple word error rate | 0.0338053 (3.38%) |
| Exact checkpoint phrases | 23/25 |
| Final tail marker | Present |
| Raw transcript SHA256 | `a31f042b4654d680882a28459c2ea7268258f79268cc7fb565ae6931ee684da2` |

Evidence is retained in `.local/long-dictation/results/long-replay-2E8F743D-0242-485C-B027-0D830D16745C/`: `metrics.json`, `events.jsonl` (including preparation/resource events), and `raw-transcript.txt`. Fixture hashes, exact voice, and generation arguments remain in the fixture's `fixture.json`.

The root reviewer inspected both missing exact phrases: section 20 appeared as “plumb bicycle” rather than “plum bicycle”; section 22 appeared as “perl notebook” rather than “pearl notebook”. Both sections were present. Found checkpoint phrases appeared in order, and the final tail was intact. These are recognition spelling errors, not evidence of those sections being dropped. This was one synthetic voice/recording/run; repeated passages and coarse WER/checkpoint thresholds do not establish preservation of every word or semantic correctness on natural speech.

The pinned SDK still has a **150-token per-window decoder limit** that can terminate a dense/runaway window without a typed truncation error. This test does not remove or exhaustively validate that limit. Disk-backed processing also uses a duration-scaled temporary Float32 file/mapping and accumulated token results; the RSS figure is a test-process high-water mark, not a guarantee of constant total memory. Preparation and conversion/model caches can affect these timings. No conclusion about 22 minutes of live capture, ingestion/backpressure, device routes, cancellation, cleanup, or insertion follows from this file replay. Those gates, including 100 real capture cycles, remain separate.
