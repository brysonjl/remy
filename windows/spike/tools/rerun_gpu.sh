#!/usr/bin/env bash
# GPU configs only, for rerunning when the GPU is idle.
set -u
cd "$(dirname "$0")/.."
export DOTNET_ROOT="${DOTNET_ROOT:-$LOCALAPPDATA/dotnet-sdk}"
FP16=.cache/models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-fp16; F=.cache/fixtures/all; R=.cache/runs
out/speech-cuda/Speech.exe bench $FP16 cuda 1 3 $F cuda-fp16             > $R/cuda-fp16.jsonl
out/speech-cuda/Speech.exe bench $FP16 cuda 1 3 $F cuda-fp16-chunk20 20  > $R/cuda-fp16-chunk20.jsonl
out/speech-cuda/Speech.exe bench $FP16 cuda 1 1 .cache/fixtures/test-clean full-cuda-fp16 > $R/full-cuda-fp16.jsonl
echo gpu-done
