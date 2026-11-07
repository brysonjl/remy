---
license: other
license_name: nvidia-open-model-license
license_link: https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-open-model-license/
library_name: coreml
base_model: nvidia/nemotron-speech-streaming-en-0.6b
tags:
  - speech-recognition
  - automatic-speech-recognition
  - streaming-asr
  - coreml
  - apple
  - ios
  - macos
  - FastConformer
  - RNNT
  - Parakeet
  - ASR
pipeline_tag: automatic-speech-recognition
---
# Nemotron Speech Streaming 0.6B - CoreML

CoreML conversion of NVIDIA's `nvidia/nemotron-speech-streaming-en-0.6b` for real-time streaming ASR on Apple devices.

## Model Variants

Four chunk-size variants optimized for different latency/accuracy trade-offs:

| Variant | Chunk Duration | Latency | Use Case |
|---------|---------------|---------|----------|
| `nemotron_coreml_1120ms` | 1.12s | High | Best accuracy |
| `nemotron_coreml_560ms` | 0.56s | Medium | Balanced |
| `nemotron_coreml_160ms` | 0.16s | Low | Real-time feedback |
| `nemotron_coreml_80ms` | 0.08s | Ultra-low | Experimental |

All variants include:
- **Int8 quantized encoder** (~564MB, 4x smaller than float32)
- **Compiled .mlmodelc format** (ready for deployment)

## Benchmark Results (LibriSpeech test-clean)

Tested on Apple M2 with [FluidAudio](https://github.com/FluidInference/FluidAudio):

| Chunk Size | WER | RTFx | Files |
|------------|-----|------|-------|
| 1120ms | 1.99% | 9.6x | 100 |
| 560ms | 2.12% | 8.5x | 100 |
| 160ms | ~10% | 3.5x | 20 |
| 80ms | ~60% | 1.9x | 20 |

160ms and 80ms were only tested on 20 files.

## Model Overview

| Property | Value |
|----------|-------|
| Source Model | `nvidia/nemotron-speech-streaming-en-0.6b` |
| Architecture | FastConformer RNNT (Streaming) |
| Parameters | 0.6B |
| Sample Rate | 16kHz |
| Mel Features | 128 bins |
| Quantization | Int8 (encoder) |

## CoreML Models (per variant)

| Model | Size | Function |
|-------|------|----------|
| `preprocessor.mlmodelc` | ~1MB | audio → 128-dim mel spectrogram |
| `encoder/encoder_int8.mlmodelc` | ~564MB | mel + cache → encoded + new_cache |
| `decoder.mlmodelc` | ~28MB | token + LSTM state → decoder_out + new_state |
| `joint.mlmodelc` | ~7MB | encoder + decoder → logits |

Plus:
- `metadata.json` - Model configuration (chunk size, mel frames, etc.)
- `tokenizer.json` - Vocabulary (1024 tokens)

## Directory Structure

```
nemotron-speech-streaming-en-0.6b-coreml/
├── nemotron_coreml_1120ms/      # 1.12s chunks (best accuracy)
│   ├── encoder/
│   │   └── encoder_int8.mlmodelc
│   ├── preprocessor.mlmodelc
│   ├── decoder.mlmodelc
│   ├── joint.mlmodelc
│   ├── metadata.json
│   └── tokenizer.json
├── nemotron_coreml_560ms/       # 0.56s chunks (balanced)
│   └── ...
├── nemotron_coreml_160ms/       # 0.16s chunks (low latency)
│   └── ...
└── nemotron_coreml_80ms/        # 0.08s chunks (experimental)
    └── ...
```

## Chunk Configuration

Each variant has different mel frame counts:

| Variant | chunk_mel_frames | pre_encode_cache | total_mel_frames |
|---------|------------------|------------------|------------------|
| 1120ms | 112 | 9 | 121 |
| 560ms | 56 | 9 | 65 |
| 160ms | 16 | 9 | 25 |
| 80ms | 8 | 9 | 17 |

**Formula:** `chunk_ms = chunk_mel_frames × 10ms`

## Cache Shapes

| Cache | Shape | Description |
|-------|-------|-------------|
| cache_channel | [1, 24, 70, 1024] | Attention context cache |
| cache_time | [1, 24, 1024, 8] | Convolution time cache |
| cache_len | [1] | Cache fill level |

## Usage with FluidAudio

```swift
import FluidAudio

// Load with specific chunk size
let manager = NemotronStreamingAsrManager()
let modelDir = // path to nemotron_coreml_560ms
try await manager.loadModels(modelDir: modelDir)

// Process audio
let result = try await manager.process(audioBuffer: buffer)
let transcript = try await manager.finish()
```

### CLI Benchmark

```bash
# Install FluidAudio CLI
git clone https://github.com/FluidInference/FluidAudio
cd FluidAudio

# Run benchmark with specific chunk size
swift run -c release fluidaudiocli nemotron-benchmark --chunk 560 --max-files 100
```

## Inference Pipeline

```
┌─────────────────────────────────────────────────────────────────┐
│                     STREAMING RNNT PIPELINE                      │
└─────────────────────────────────────────────────────────────────┘

1. PREPROCESSOR (per audio chunk)
   audio [1, samples] → mel [1, 128, chunk_mel_frames]

2. ENCODER (with cache)
   mel [1, 128, total_mel_frames] + cache → encoded [1, 1024, T] + new_cache
   (total_mel_frames = pre_encode_cache + chunk_mel_frames)

3. DECODER + JOINT (greedy loop per encoder frame)
   For each encoder frame:
     token → DECODER → decoder_out
     encoder_step + decoder_out → JOINT → logits
     argmax → predicted token
     if token == BLANK: next encoder frame
     else: emit token, update decoder state
```

## Quantization Details

The encoder is quantized to int8 using CoreMLTools:

| Metric | Float32 | Int8 |
|--------|---------|------|
| Size | ~2.2GB | ~564MB |
| Compression | 1x | **3.9x** |
| WER Impact | Baseline | Negligible |

Other models (preprocessor, decoder, joint) remain in float32 as they are already small.

## Notes

- The encoder is the largest model with 24 Conformer layers
- Model uses 128 mel bins (not the typical 80)
- RNNT blank token index is 1024 (vocab_size)
- Decoder uses 2-layer LSTM with 640 hidden units
- Pre-encode cache (9 frames = 90ms) bridges chunk boundaries

## License

Apache 2.0 (following NVIDIA's original license)