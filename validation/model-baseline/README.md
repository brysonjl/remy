# A00 Parakeet v2 provenance

This is a baseline-only downloader and offline integrity checker, not A03 model management. No model weights were downloaded or inference performed while preparing these files. The 22-file bundle is **464,414,912 bytes** (464.4 MB / 442.9 MiB), including its upstream README. This differs from older model-card size figures and excludes optional VAD and acoustic vocabulary assets.

Selected conversion: [FluidInference/parakeet-tdt-0.6b-v2-coreml](https://huggingface.co/FluidInference/parakeet-tdt-0.6b-v2-coreml/tree/ee09c569f73759e6d44c9bd16766f477b2b36d39), immutable revision `ee09c569f73759e6d44c9bd16766f477b2b36d39`, metadata inspected 2026-10-19. License declared by conversion card: **CC-BY-4.0**; original model [NVIDIA Parakeet TDT 0.6B v2](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2). Preserve attribution to NVIDIA and FluidInference; Apache-2.0 FluidAudio licensing does not replace model licensing. This metadata establishes the conversion revision, not the exact original NVIDIA checkpoint used by the converter (not specified by the conversion card).

The manifest records immutable download URLs, byte counts, upstream Git blob SHA1s and SHA256 for every selected file. LFS entries use upstream LFS SHA256. For ordinary Git files the small payload was fetched at the pinned revision, checked against the upstream Git blob SHA1, then SHA256 calculated locally. Git/LFS identities provide reproducibility and integrity, not a publisher signature.

## Actual SDK compatibility

FluidAudio **0.17.5**, commit `0b1f46289fe27d95b5e66ad8be46e64f5ee02ae7`, explicitly selects `Encoder.mlmodelc`, `Decoder.mlmodelc`, `JointDecision.mlmodelc`, `Preprocessor.mlmodelc`, and `parakeet_vocab.json` for `.v2`. Each compiled model is a directory whose complete committed file set is included. Do not download similarly named legacy `ParakeetEncoder*`, `Melspectogram*`, `RNNTJoint*` variants from the same repository.

Authority: pinned [AsrModels.swift](https://github.com/FluidInference/FluidAudio/blob/0b1f46289fe27d95b5e66ad8be46e64f5ee02ae7/Sources/FluidAudio/ASR/Parakeet/SlidingWindow/TDT/AsrModels.swift) (`getModelFileNames`, `loadLocal`) and [ModelNames.swift](https://github.com/FluidInference/FluidAudio/blob/0b1f46289fe27d95b5e66ad8be46e64f5ee02ae7/Sources/FluidAudio/ModelNames.swift). These were retrieved from the pinned GitHub commit while Xcode dependency resolution was still pending; the integrator should compare its resolved checkout.

## Reproduce only when model download is authorized

The helper requires Python 3 and macOS. It reads no credentials, never chooses a mutable revision, refuses an existing download destination, stages in the selected parent, verifies every file, and atomically renames with macOS `RENAME_EXCL`. Failures remove its temporary directory; cancellation via a hard process kill may leave a `.parakeet-a00-*` staging directory. There is no resumption or automatic app activation. Use a parent without symlink ancestors.

```sh
mkdir -p validation/model-baseline/bundles
python3 validation/model-baseline/model_baseline.py download \
  "$PWD/validation/model-baseline/bundles/parakeet-v2-ee09c569"
python3 validation/model-baseline/model_baseline.py verify \
  "$PWD/validation/model-baseline/bundles/parakeet-v2-ee09c569"
```

`verify` performs no network operations and rejects missing/extra files and symlinks. Do not commit downloaded bundles. The helper intentionally lacks a promotion/removal command. Once signing and runtime ownership are verified, the integrator can explicitly place a verified bundle at the isolated development path below, without replacing an existing model directory, and verify it there.

## App path and offline inference

Actual application `Settings.swift` maps `.englishOnly` to `parakeet-tdt-0.6b-v2-coreml`; `RemyStoragePaths` scopes it to the current bundle identity. Expected development layout:

```text
~/Library/Application Support/io.github.brysonjl.remy.dev/Models/
  parakeet-tdt-0.6b-v2-coreml/
    Encoder.mlmodelc/{coremldata.bin,metadata.json,model.mil,analytics/...,weights/...}
    Decoder.mlmodelc/{coremldata.bin,metadata.json,model.mil,analytics/...,weights/...}
    JointDecision.mlmodelc/{coremldata.bin,metadata.json,model.mil,analytics/...,weights/...}
    Preprocessor.mlmodelc/{coremldata.bin,metadata.json,model.mil,analytics/...,weights/...}
    parakeet_vocab.json
    README.md
```

Release storage instead uses `io.github.brysonjl.remy`; never use or modify `~/Library/Application Support/FluidAudio/Models`. The app's `loadCachedModels` calls `AsrModels.loadLocal(from:version:)` on this exact directory, with `.v2`, away from the main actor. This path does not download. Optional cached VAD can be absent: `loadSpeechDetection(download: false)` returns no detector. Do not click the existing install/download action to establish pinned provenance: it uses SDK download helpers whose URLs are mutable and can install optional assets.

For the offline smoke check after app launch/signing is established: select English Only, disable acoustic vocabulary/cleanup, prepare from the installed cache, block network, then repeat cold and warm launches and capture/stop short harmless speech. Record transcript, tail retention, cancellation, timings and peak memory separately. Existing code `AsrManager.loadModels(models)` + `transcribe(_:decoderState:)` also accepts 16 kHz mono Float32 fixtures and can verify inference without microphone; `AsrCoordinator.initialize` already runs a one-second silent warmup. Successful checksum verification or silent warmup alone does not establish speech accuracy, end-tail behavior or A00 completion.
