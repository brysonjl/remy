#!/usr/bin/env bash
# Runs every speech configuration in its own process. Raw JSON lines go to .cache/runs/.
set -u
cd "$(dirname "$0")/.."
export DOTNET_ROOT="${DOTNET_ROOT:-$LOCALAPPDATA/dotnet-sdk}"
M=.cache/models; F=.cache/fixtures/all; R=.cache/runs; mkdir -p $R
INT8=$M/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8
FP16=$M/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-fp16
run() { echo ">> $*"; "$@"; }
# label                 build              model  provider threads runs fixtures label chunk
run out/speech-cpu/Speech.exe  bench $INT8 cpu  8 3 $F cpu-int8-t8         > $R/cpu-int8-t8.jsonl
run out/speech-cpu/Speech.exe  bench $INT8 cpu  8 3 $F cpu-int8-t8-chunk20 20 > $R/cpu-int8-t8-chunk20.jsonl
run out/speech-cpu/Speech.exe  bench $INT8 cpu  4 3 $F cpu-int8-t4         > $R/cpu-int8-t4.jsonl
run out/speech-cuda/Speech.exe bench $INT8 cuda 1 3 $F cuda-int8           > $R/cuda-int8.jsonl
run out/speech-cuda/Speech.exe bench $FP16 cuda 1 3 $F cuda-fp16           > $R/cuda-fp16.jsonl
run out/speech-cuda/Speech.exe bench $FP16 cuda 1 3 $F cuda-fp16-chunk20 20 > $R/cuda-fp16-chunk20.jsonl
# Corpus-level accuracy on all of test-clean (one pass each).
run out/speech-cuda/Speech.exe bench $FP16 cuda 1 1 .cache/fixtures/test-clean full-cuda-fp16 > $R/full-cuda-fp16.jsonl
run out/speech-cpu/Speech.exe  bench $INT8 cpu  8 1 .cache/fixtures/test-clean full-cpu-int8  > $R/full-cpu-int8.jsonl
echo done
