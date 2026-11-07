# Local speech runtime research

Research date: 2026-10-19. Recommendation is provisional until the benchmark spike runs. No speech models were downloaded or executed during this investigation; all performance numbers below are upstream reports, not Remy measurements. The coordinator observed an Apple M4 Pro MacBook Pro with 48 GB RAM and macOS 27.0.1; use that as the initial benchmark machine, subject to user confirmation.

## Recommendation

Use Swift + FluidAudio + Core ML for the first macOS implementation, with **Parakeet TDT 0.6B v2 as the default final-transcript engine**. This preserves the user's current English model family while eliminating Python/runtime packaging from the everyday application. “NVIDIA model” describes the trained model; on this Mac the execution backend is Apple's Neural Engine/CPU through Core ML, not CUDA.

First ship reliable start/stop dictation and final text. Add revisable live preview as a separate capability. Benchmark **Nemotron streaming 0.6B at 560 ms and 1120 ms** as the candidate for genuine incremental recognition. Do not make streaming into arbitrary applications the first release: transcription hypotheses and cleanup output both change. Show live text in Remy's overlay; commit the completed transformation to the target once.

This recommendation is an engineering judgment based on native integration and the existing baseline, not a claim that this engine beats every alternative. Keep the recognizer behind a Remy-owned interface so selection can change after measurements.

## Evidence and model choices

| Model | Relevant verified properties | Proposed role |
| --- | --- | --- |
| NVIDIA Parakeet TDT 0.6B v2 | English; full-attention FastConformer-TDT; punctuation, capitalization and timestamps; 16 kHz mono input; upstream model license CC-BY-4.0. | Default reference and first final ASR implementation. |
| NVIDIA Parakeet TDT 0.6B v3 | Multilingual successor covering 25 European languages; CC-BY-4.0. | Optional if multilingual needs emerge; compare English rather than assuming a newer version wins. |
| NVIDIA Parakeet Realtime EOU 120M v1 | Cache-aware RNNT streaming with end-of-utterance token; English; no punctuation/capitalization; NVIDIA Open Model License. | Low-resource/turn-detection experiment, especially for future wake-word conversations. |
| NVIDIA Nemotron Speech Streaming English 0.6B | Cache-aware FastConformer-RNNT; March 2026 checkpoint supports punctuation/capitalization and upstream 80/160/560/1120 ms configurations; NVIDIA Open Model License. | Main native-streaming candidate, using only tiers supported by the pinned Swift runtime. |

Sources: NVIDIA's [v2 card](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2), [v3 card](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3), [EOU card](https://huggingface.co/nvidia/parakeet_realtime_eou_120m-v1), and [Nemotron card](https://huggingface.co/nvidia/nemotron-speech-streaming-en-0.6b). These are separate model licenses; FluidAudio's Apache-2.0 code license does not replace them. Preserve model attribution and conversion provenance in a model manifest and third-party notices; verify exact weight licenses before redistribution.

FluidAudio now also has Ultra/Redux/Phonon-2 derivatives and a Nemotron multilingual integration. They should remain follow-up candidates rather than silently displacing the user's requested baseline. The [v0.17.3 and v0.17.5 release notes](https://github.com/FluidInference/FluidAudio/releases) show both accuracy and size/speed tradeoffs; these products must have their own model manifest and evaluation if selected.

### Version and source audit

Research baseline: **FluidAudio v0.17.7**, commit **503b4bd1bbf7220882de39fe8ae6716aae4132da**, published 2026-10-08. This is an inspected release, not an assertion that `main` is identical. [Pinned commit](https://github.com/FluidInference/FluidAudio/commit/503b4bd1bbf7220882de39fe8ae6716aae4132da).

Selected upstream source, manifests, documentation and license are stored under [research/upstream/fluid-audio](../../research/upstream/fluid-audio/). [SNAPSHOT.json](../../research/upstream/fluid-audio/SNAPSHOT.json) records the URLs and commit. This is a review snapshot, not a buildable vendored dependency. No model weights are included. Shell Git network access failed because DNS was unavailable; the authenticated GitHub connector provided the pinned files.

The pinned manifests require macOS 14+ and Swift tools 6.0, with a separate Swift 6.2 manifest. Under 6.2, disabling the `NemoTextProcessing` trait avoids its prebuilt Rust framework when ASR-only operation does not need that normalization. On older toolchains it is linked. Review the actual binary dependency and resources when building a reproducible release; “Swift package” does not mean all transitive bits are compiled from source. [Pinned Swift 6.2 manifest](https://github.com/FluidInference/FluidAudio/blob/503b4bd1bbf7220882de39fe8ae6716aae4132da/Package@swift-6.2.swift).

**Critical documentation discrepancy:** the pinned `NemotronChunkSize` enum has exactly **560, 1120, and 2240 ms** cases. Some docs and the Hugging Face card still describe 80/160 ms variants. Do not promise those tiers in Remy. The pinned manager uses a native Swift mel extractor and optionally fused decoder/joint; the older model card describes a Core ML preprocessor. The enum and loading code are the integration authority, and an actual compatible model bundle must be verified in the spike. [Pinned chunk enum](https://github.com/FluidInference/FluidAudio/blob/503b4bd1bbf7220882de39fe8ae6716aae4132da/Sources/FluidAudio/ASR/Parakeet/Streaming/Nemotron/NemotronChunkSize.swift), [pinned manager](https://github.com/FluidInference/FluidAudio/blob/503b4bd1bbf7220882de39fe8ae6716aae4132da/Sources/FluidAudio/ASR/Parakeet/Streaming/Nemotron/StreamingNemotronAsrManager.swift), [conversion card](https://huggingface.co/FluidInference/nemotron-speech-streaming-en-0.6b-coreml).

The pinned Nemotron default is 2240 ms, chosen for throughput. Explicitly select 560 ms for the initial responsive-preview trial and compare 1120 ms for quality. Upstream source requests CPU+ANE and rejects Intel Macs for the int8 model; do not advertise Intel support for that adapter. A compatible Intel fallback would be a separate release decision.

### What streaming actually means

1. **Capture streaming:** microphone PCM arrives continuously; all implementations need this.
2. **Sliding-window ASR:** rerun an offline encoder on overlapping buffered audio, merge hypotheses, revise text. Parakeet TDT v2/v3 can provide this experience but their encoder is not a native cache-aware streaming encoder.
3. **Native streaming ASR:** retain encoder/decoder state and process new chunks. EOU and Nemotron implement this.
4. **Cleanup streaming:** generate transformation tokens after receiving source text. This lowers perceived waiting in Remy but does not make arbitrary partial transformations safe to paste.

FluidAudio's own [StreamingAsrManager protocol](https://github.com/FluidInference/FluidAudio/blob/503b4bd1bbf7220882de39fe8ae6716aae4132da/Sources/FluidAudio/ASR/Parakeet/Streaming/StreamingAsrManager.swift) explicitly excludes the sliding-window TDT manager from the native-streaming protocol. Its API includes finish/reset/cleanup and partial callbacks. Remy should mirror those lifecycle distinctions without exporting upstream types throughout the application.

Do not use the convenience `SlidingWindowAsrSession.loadModels()` blindly: the inspected implementation calls default `downloadAndLoad()` without selecting v2, and convenience engine creation may download automatically. Remy must explicitly choose the model and use its controlled model store, including offline load paths. [Pinned session](https://github.com/FluidInference/FluidAudio/blob/503b4bd1bbf7220882de39fe8ae6716aae4132da/Sources/FluidAudio/ASR/Parakeet/SlidingWindow/SlidingWindowAsrSession.swift), [manual loading guide](https://github.com/FluidInference/FluidAudio/blob/503b4bd1bbf7220882de39fe8ae6716aae4132da/Documentation/ASR/ManualModelLoading.md).

## Alternatives considered

| Runtime | Why it is useful | Why it is not the first default |
| --- | --- | --- |
| parakeet-mlx | Apple Silicon implementation; Python API/CLI; TDT beam decoding and streaming context with draft/final tokens. Apache-2.0 code. | Requires packaging/supporting Python plus dependencies; GPU use must be evaluated alongside local cleanup. Its streaming context can alter attention behavior, so final accuracy parity cannot be assumed. Excellent independent comparison harness. |
| whisper.cpp | C/C++ runtime; CPU, Metal and Core ML paths; Mac Intel/Arm support and quantized Whisper weights. | Different model family from user's baseline; C bridging/build artifacts add work. Strong fallback if broader hardware or language coverage becomes required. |
| WhisperKit / Argmax OSS Swift | Native Swift alternative for Whisper-family inference; current repository has moved to `argmax-oss-swift`. | Evaluate if FluidAudio fails quality/reliability gates; changing recognizer should follow evidence rather than familiarity. |

Primary sources: [parakeet-mlx README](https://raw.githubusercontent.com/senstella/parakeet-mlx/master/README.md), [whisper.cpp](https://github.com/ggml-org/whisper.cpp), [Argmax OSS Swift](https://github.com/argmaxinc/argmax-oss-swift). No comparative latency was measured here.

## Performance evidence and limits

FluidInference's [v2 conversion card](https://huggingface.co/FluidInference/parakeet-tdt-0.6b-v2-coreml) reports approximately 110× realtime on M4 Pro and approximately 800 MB peak memory. Treat this as an upstream indication of feasibility. It does not specify Remy's cold-start, microphone pipeline, cleanup, overlay or paste costs. The screenshot's **496 MB** model size is not a runtime memory budget.

The [pinned Nemotron documentation](https://github.com/FluidInference/FluidAudio/blob/503b4bd1bbf7220882de39fe8ae6716aae4132da/Documentation/ASR/Nemotron.md) reports an M5 Pro, 100-file LibriSpeech test-clean run: 560 ms at 42.1× realtime/2.28% WER, 1120 ms at 65.0×/2.28%, and 2240 ms at 93.6×/2.46%. This small cross-hardware benchmark is not a user-dictation accuracy estimate; its 2240 ms prose says “no accuracy cost” despite different tabulated WER. Use the table as reported and do not repeat that stronger claim. The documented encoder is roughly 564 MB and total variant roughly 600 MB on disk; runtime memory still needs measurement.

EOU's NVIDIA card reports 9.30% average WER in its 160 ms streaming benchmark and 160 ms median endpoint latency on synthetic dialogue with appended silence. Neither quantity predicts hotkey-to-paste latency on a Mac. Similarly, v2's NVIDIA batch-throughput benchmark uses NVIDIA hardware/large batches; it is unsuitable as an interactive-Mac latency promise.

## Architecture implications

- The audio tap performs bounded copying into a ring buffer and returns promptly. Resampling, inference, logging, database writes and networking run outside the realtime callback.
- Capture once and assign monotonic sample offsets. Use 16 kHz mono Float32 at the recognition boundary, with explicit route/sample-rate conversion. Keep enough original audio to finalize or replay within the session policy.
- Own one warm inference service per selected engine. On start, allocate a new session generation and fresh decode state. Model switching occurs while idle, never mid-recording.
- Emit `partial(revision, text, sampleRange)`, `final(text, tokens?)`, and `failure(stage, retryability)`; every event carries session ID. Partial text is replaceable. Consumers cannot infer stability merely because text appeared twice.
- User hotkey stop means drain captured audio, finish the tail, then finalize exactly once. VAD/EOU may identify boundaries, but must not discard intended pauses or substitute for an explicit stop policy.
- Bound backlog. If realtime recognition falls behind, reduce preview work and preserve final-capture audio; never silently drop audio. Expose a recoverable failure if storage/memory bounds are exceeded.
- If the speech helper crashes, keep UI/history alive and recover the pending session from the configured transient spool. If audio persistence is disabled, describe the narrower recovery guarantee accurately. No automatic re-paste after a helper restart.
- Own model downloads: explicit user-visible install, immutable repository revision, per-file checksum, complete bundle validation, temporary staging and atomic activation, previous known-good version retained until the replacement passes a local smoke test. No automatic dependency helper downloads during ordinary dictation.
- Log timings, sizes and error categories by default, not transcript content. Verify upstream log configuration suppresses recognized text before release.
- Keep custom-word corrections as an independently recorded step. Start with whole-token/phrase mappings such as `Xway → Ixway`, case rules and tests. Do not mutate the archived raw transcript. Later contextual biasing is a distinct engine capability.

FluidAudio already documents acoustic CTC vocabulary rescoring, including a separate CTC encoder for the 0.6B TDT models. It is a useful future experiment, not a free string-replacement feature; it adds inference and needs false-positive evaluation. [Custom vocabulary documentation](https://github.com/FluidInference/FluidAudio/blob/503b4bd1bbf7220882de39fe8ae6716aae4132da/Documentation/ASR/CustomVocabulary.md).

Future wake word should be its own small, locally running detector with explicit armed state and a bounded pre-roll buffer. A wake event starts an ordinary dictation session; it does not bypass lifecycle/state rules. Future command mode can consume timestamped hypotheses for speculative intent, but executable actions must use a separate policy/authorization boundary and stable intent. Do not couple transcription callbacks to action execution.

## Implementation spike and acceptance gates

These are **proposed product targets**, not achieved measurements. Resolve them before committing to the final runtime. Store exact machine/OS/build/SDK/model revisions and individual timing samples with the report.

1. Build a minimal signed macOS harness using the pinned SDK. Prove v2 explicit loading, network-disabled repeat launches, microphone capture, final tail flush, session reset, cancellation and 100 repeated utterances. Verify ASR-only package traits and log privacy.
2. Create a locally stored evaluation set: at least 100 user-representative utterances totaling 30–60 minutes, plus an independent held-out set of at least 50 utterances. Include 1–5 second replies, 30–120 second paragraphs, a ten-minute monologue, self-corrections, “do not,” negation, numbers/dates, URLs, code identifiers, Ixway, background noise, silence and low microphone levels. Record verbatim and intended-clean reference separately. Never score filler removal as an ASR success.
3. Compare v2 final, v2 sliding preview + final, Nemotron 560/1120 ms, and optional EOU 320 ms. Use a small MLX or Whisper baseline only if the primary candidate fails a gate or to diagnose conversion regressions. Replay identical PCM; do not compare separate spoken takes as if controlled.
4. Measure cold install/download, first Core ML compilation, warm model load, hotkey-to-recording, first useful partial, partial revision rate, stop-to-final ASR, cleanup first/final token, history commit, and final-text-to-paste separately. Report p50/p95/p99 plus failures, not just average realtime factor. Run both with cloud cleanup and with a local cleanup model resident.
5. Provisional warm targets on the observed M4 Pro: recording acknowledgement ≤100 ms p95; first useful preview ≤1.5 s p95; stop-to-final ASR ≤700 ms p95 for clips up to 30 seconds; raw paste complete ≤1 s p95. Target completed cleaned paste ≤2 s p50/≤4 s p95 on short utterances using a locally benchmarked cleanup model. Remote-provider targets are observations, not app-controlled guarantees. If a target fails, document measured tradeoffs and revise selection/requirements explicitly.
6. Quality gate: final held-out WER no more than 1 absolute percentage point worse than the captured Spokenly baseline, alongside entity/number accuracy and a manual correction-effort comparison. Require no truncation or invented speech on the deterministic silence/tail fixtures. A low aggregate WER cannot waive changed negations, names or numbers.
7. Resource gate: record resident/peak memory, thermal state, CPU/GPU/ANE utilization where observable, idle power and concurrent local-cleanup contention. Initial ASR process target ≤2 GB peak for ordinary ≤2-minute clips, no unbounded growth across 1,000 start/stop cycles, and no sustained memory-pressure warning. Revisit the budget using actual measurements; ANE placement alone is not an energy measurement.
8. Reliability gate: 1,000 start/stop cycles plus one eight-hour day of mixed use; zero app crashes, no duplicated or lost finalization, no cross-session text leakage. Test sleep/wake, microphone unplug/replug, Bluetooth route changes, permission revocation, model corruption, disk-full, inference timeout, helper termination and cancellation during finish. Failure must leave a visible recoverable state and retained transcript when one exists.
9. Before enabling streaming by default, specifically test final partial chunks below the engine chunk size, cuts just before/after chunk boundaries, long pauses and overlapping-window duplicate words. Native streaming does not eliminate tail-flush bugs.

Deliverable from this spike: a measured decision record naming one default ASR engine/model/bundle revision, optional preview engine, warm-up policy, memory cap, error behavior and the exact reasons other candidates were rejected or deferred. No application agent should fill missing benchmark cells with upstream marketing numbers.
