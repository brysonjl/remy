# Windows feasibility spike: results

Date: 2026-11-07. Throwaway measurement code, not a port. The macOS app was not touched.

Machine: Windows 11 Pro 23H2 (build 22631), AMD Ryzen 7 5800X (8 cores / 16 threads), 128 GB RAM,
NVIDIA RTX 3090 24 GB (driver 610.60). Tests ran in a normal (non-elevated) user session with UAC on.

## Short answer

| Question | Answer |
| --- | --- |
| Is Parakeet v2 fast enough on Windows CPU? | **Yes.** A 10–13 s dictation decodes in **0.5–0.6 s** on CPU (int8, 8 threads), about 20× faster than real time. With 20 s chunking, 5 minutes decode in about 16 s total, and most of that can happen while the user is still speaking. |
| Is it accurate? | **Yes, comparable to the published model.** 1.98% WER over all of LibriSpeech test-clean (2,620 utterances, 5.4 h) with a strict scorer, against NVIDIA's published 1.69%. |
| Does the GPU help? | Barely for dictation-length audio, and it adds risk. Warm fp16 on the RTX 3090 decoded 12.8 s in **66–70 ms** (versus 600 ms on CPU), but it needs about 2 GB of CUDA/cuDNN DLLs, and the fp16 + CUDA combination **crashes the process on some inputs every time** (an access violation that can't be caught). CPU int8 should be the default. |
| Can a background process insert text? | **Yes, with clipboard + Ctrl+V.** It inserted exactly once in every app reached: Notepad, a Chrome textarea, a Chrome contenteditable, VS Code and Windows Terminal. UI Automation alone is not a usable insertion method (details below). |
| Untested | Slack (the installed copy refuses to start until updated), Word (not installed), and an app running as administrator (the UAC prompt wasn't approved). See [Not tested](#not-tested). |

Windows looks feasible. Speech recognition carries over directly through sherpa-onnx. Insertion should copy the macOS
design: clipboard plus a synthetic paste, guarded by a foreground check, with copy-only fallback.

## 1. Speech: Parakeet v2 through sherpa-onnx

### Setup

- sherpa-onnx **2.0.1** through the `org.k2fsa.sherpa.onnx` NuGet package (CPU) from a .NET 10 console app, `OfflineRecognizer`,
  `nemo_transducer`, greedy search.
- GPU: the same app, with the native DLLs replaced by the upstream build
  `sherpa-onnx-v2.0.1-cuda-12.x-cudnn-9.x-onnxruntime1.28.2-win-x64-cuda` and NVIDIA's CUDA 12.9 / cuDNN 9.28
  redistributable DLLs. No system CUDA install was needed.
- Models (from the sherpa-onnx `asr-models` release):
  - `sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8.tar.bz2`, 482 MB, SHA256 `6fc4426b…f915f714`.
    **The upstream asset was re-uploaded on 2026-11-07, so it is a mutable artifact.** A port must pin it by hash.
  - `sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-fp16.tar.bz2`, 1.12 GB, SHA256 `37f67a1a…026054cf0` (full hashes in `.cache/models/SHA256SUMS`).
- Fixtures, each 16 kHz mono with a reference transcript:
  - **libri**: real read speech from LibriSpeech test-clean. Consecutive utterances from one speaker, joined with 0.25 s of silence, at 12.8 s / 65.2 s / 319.4 s. Each length uses a different speaker.
  - **tts**: Windows SAPI voice "Microsoft David" reading the repo's `validation/long-dictation/fixtures/passages.json`
    at 10.5 s / 60.0 s / 307.9 s. This mirrors the macOS long-dictation method.
  - **test-clean**: all 2,620 LibriSpeech test-clean utterances, for a corpus-level WER.
- Timing covers decode only: the model is loaded and warmed up first, with 3 runs per fixture. "Warm" is the median of runs 2–3.
  RTF = decode time ÷ audio length (lower is better; 0.05 means 20× faster than real time).
- WER uses lowercase words with punctuation removed. Digits vs spelled-out numbers ("9 30" vs "nine thirty") and
  British/American spellings count as errors, as in the macOS long-dictation check.

### CPU results (int8 model)

| Audio | 8 threads, whole file | 4 threads, whole file | 8 threads, 20 s chunks | WER |
| --- | --- | --- | --- | --- |
| libri 12.8 s | **0.60 s** (RTF 0.047) | 0.65 s (0.050) | 0.64 s (0.050) | 0.0% |
| tts 10.5 s | **0.50 s** (0.047) | 0.54 s (0.051) | 0.53 s (0.050) | 0.0% |
| libri 65.2 s | 3.80 s (0.058) | 4.75 s (0.073) | **3.09 s** (0.047) | 2.8% |
| tts 60.0 s | 3.51 s (0.059) | 4.05 s (0.067) | **3.03 s** (0.051) | 2.9% whole / 0.0% chunked |
| libri 319.4 s | 41.0 s (0.128) | 48.6 s (0.152) | **16.2 s** (0.051) | 1.5% |
| tts 307.9 s | 40.5 s (0.132) | 45.2 s (0.147) | **15.7 s** (0.051) | 3.5% whole / 1.8% chunked |
| Peak process RAM | 7.9 GB | 7.9 GB | **1.3 GB** | |

- Model load: 2.0–2.4 s. Warm-up: 0.2 s.
- **Whole-file decoding scales worse than linearly.** RTF more than doubles from 1 minute to 5 minutes, and RAM reaches
  about 8 GB, because the encoder attends over the whole recording. Chunking at pauses keeps cost linear (RTF about 0.05 at every
  length) and RAM at about 1.3 GB, with the same or better accuracy. A Windows build should decode in chunks of 20 s or less,
  in the background during recording. Then stop-to-text is roughly the time to decode the last chunk (under 1 s on
  this CPU), whatever the recording length.
- 4 threads costs only about 10–20% compared with 8. That's a rough guide for weaker machines, but it wasn't tested on a laptop.

### Accuracy

| Run | Utterances | Audio | Decode time | RTF | Corpus WER |
| --- | --- | --- | --- | --- | --- |
| CPU int8, 8 threads, all of test-clean | 2,620 | 5.40 h | 17.4 min | 0.054 | **1.98%** |

NVIDIA reports 1.69% on test-clean for the original model, using the Open ASR Leaderboard text normalizer. This
scorer is stricter, so 1.98% suggests the int8 ONNX conversion is close to the original. Errors in the long fixtures
were mainly proper names (Rodolfo → "Rudolpho", Leocadia → "Laocadia"), British spellings ("theatre", "coloured")
and digits ("nine thirty" → "9 30"). One finding relevant to Remy's cleanup policy: the robotic TTS voice's
**"oh wait" was often recognized as "owe" or "await"**, so cleanup fixtures for "oh wait" corrections should be checked with real speech.

### GPU results (RTX 3090)

Measured with the GPU otherwise idle (under 1 GB VRAM used, 4% utilization before the run).

| Config | Audio | First decode | Warm | WER |
| --- | --- | --- | --- | --- |
| fp16, CUDA, whole file | libri 12.8 s | 0.20 s | **0.07 s** (RTF 0.005) | 0.0% |
| fp16, CUDA, 20 s chunks | libri 65.2 s | 0.89 s | **0.88 s** (RTF 0.013) | 3.4% |
| fp16, CUDA, test-clean (until it crashed) | first 87 utterances, 0.21 h | | RTF 0.022 | 1.72% |
| int8, CUDA | libri 12.8 s / 65.2 s / 319.4 s | 5.8 s / 17.6 s / 434 s | 4.8 s / 26.1 s / 320 s | **slower than CPU** (this run may have overlapped the llama-bench job below) |

- Load: 2.2 s. Warm-up: 0.7 s.
- **fp16 + CUDA crashes the process on certain inputs, every time.** Cutting the 65 s file to 22 s or 30 s
  crashes on every run with an access violation (0xC0000005) inside `OfflineRecognizer.Decode`. Cuts of 15, 18, 20, 21, 23 and 25 s
  decode fine. The whole 65 s file, one chunk of the 5-minute file, and test-clean utterance `1188-133604-0023` (23 s) also
  crash every time, so the full GPU corpus pass and most of the GPU matrix couldn't finish. The same 22 s clip
  decodes correctly with **fp16 on CPU** and with **int8 on CUDA**, so the problem is the fp16 model on the CUDA
  provider in sherpa-onnx 2.0.1 / onnxruntime 1.28.2. The root cause wasn't investigated. The crash bypasses .NET's
  `try/catch`, so it would take down a whole app.
- int8 on CUDA is slow because its quantized operators fall back to CPU (single-threaded in this config). A recheck on
  the idle GPU gave 2.8 s for the 22 s clip, worse than CPU.
- Early runs overlapped a separate `llama-bench` (llama.cpp) job using 22–24 GB of VRAM. Those runs were 20–50× slower,
  were discarded (kept in `.cache/runs/contended/`) and rerun after it finished. A Remy user running local LLM cleanup
  (Ollama, LM Studio) will often have the GPU in that state.
- Takeaways: for dictation, the GPU saves about 0.5 s on a 10 s clip. It costs about 2 GB of DLLs, a dependency on the NVIDIA
  driver and CUDA version, competition for VRAM with local LLMs, and a crash bug in the fast (fp16) configuration. **Ship
  CPU int8.** If a GPU option is ever added, run speech in a separate worker process so a crash loses one
  recognition, not the app. That matches the out-of-process speech worker `architecture.md` already plans for macOS.

## 2. Paste: inserting text from a background process

### Method

`Paste/` is a console process that is never in the foreground (`harness_is_foreground: false` on every trial). For
each app it opens a scratch window, brings it to the front and focuses the text field (standing in for the user
clicking there), clears the field, and then:

- **uia**: UI Automation `ValuePattern.SetValue` on the focused element. UIA has no "insert at caret", so this
  *replaces the whole field*.
- **ctrlv**: puts text on the clipboard, then `SendInput` Ctrl+V.
- **unicode** (extra): `SendInput` with `KEYEVENTF_UNICODE`, typing one character at a time. Many Windows
  dictation tools use this as a fallback.

The test string `Remy <method> check N café naïve — done` includes accented letters and an em dash. Readback uses UIA
`ValuePattern` or `TextPattern` on the focused element. Where UIA shows nothing, it falls back to Ctrl+A, Ctrl+C with a
sentinel on the clipboard first, so a failed copy can't look like a pass. The harness checks the foreground window before every
keystroke, never sends Enter, and restores the clipboard text afterward.

### Results

| App | UIA `SetValue` | Clipboard + Ctrl+V | Unicode typing (30 ms/char) | Readback that worked |
| --- | --- | --- | --- | --- |
| Notepad (Win 11 Notepad, RichEditD2DPT) | ✅ inserted | ✅ inserted | ⚠️ altered: autocorrect turned "unicode" into "Unicode". At 5 ms/char, characters were **dropped and repeated** ("hheck", "———————"). | UIA |
| Chrome textarea | ✅ inserted (1 `input` event) | ✅ inserted (1 `paste` + `input`) | ✅ inserted | UIA |
| Chrome contenteditable | ⚠️ **text shown, but 0 `input` events** | ✅ inserted | ✅ inserted | UIA |
| VS Code 1.101.2 editor (Electron) | ❌ **`SetValue` returned success and nothing changed** | ✅ inserted | ✅ inserted | Ctrl+A/C only: UIA shows an empty field unless screen-reader mode is on |
| Windows Terminal + PowerShell | ❌ no `ValuePattern` | ✅ inserted at the prompt, not run | ✅ inserted, not run | UIA `TextPattern` (whole buffer) |
| Slack | not run | not run | not run | The installed Slack shows "This version of the app is no longer supported" and won't open without an update. |
| Word | not run | not run | not run | Not installed |
| Notepad as administrator | not run | not run | not run | The UAC prompt was cancelled or timed out, so the elevated window never opened |

Insertion took about 45 ms from Ctrl+V dispatch. Typing at 30 ms/char took 1.3 s for 38 characters, so a paragraph would take
tens of seconds.

### What this means for a port

- **Use clipboard + Ctrl+V as the main path**, as on macOS. It was the only method that inserted exactly once,
  unaltered, in every app reached, and it fires the paste/input events that web apps listen for.
- **Don't use UIA `SetValue` to insert.** It replaces the whole field rather than inserting at the cursor, loses undo, and
  either did nothing in VS Code while reporting success, or skipped the `input` event in a contenteditable, so React-style
  apps (Slack, ChatGPT, Notion…) would ignore or later overwrite the text. That's the "broad whole-field value
  replacement" `architecture.md` already warns against.
- **Treat Unicode typing as a last resort.** It's slow, it goes through autocorrect, and it drops characters when sent fast.
- **UIA is useful for checking the destination, not for inserting.** It identified the focused control type in every app
  (Document / Edit / TermControl), which is what the "where dictation started" guard and terminal detection
  (`TermControl` means a terminal, so multiline should be copy-only) need. Readback is uneven: it worked in Notepad, Chrome and Terminal, and
  showed nothing in VS Code. So, as `architecture.md` already says for macOS, report "Paste sent" unless a specific destination can be verified.
- `SendInput` doesn't report whether keystrokes were delivered or blocked: it returned 4/4 every time. The elevated-app
  case is where that matters (below).

## Not tested

- **Admin/elevated apps.** Not measured, because the UAC prompt wasn't approved. Windows' documented behaviour
  (User Interface Privilege Isolation) is that `SendInput` from a normal process to an elevated window is **dropped
  silently** while still returning success, and UIA access is denied. A normal-integrity dictation app could
  therefore only copy, not paste, into elevated windows. It should detect that case (compare the target process's integrity level) and fall back
  to copy-only instead of reporting a paste. To measure it, run the command below and approve the UAC prompt.
- **Slack**: update Slack and rerun `Paste.exe slack`. The harness types into whatever conversation is open, so open your own DM first.
  Others in a channel would see a "typing…" indicator, although nothing is sent and the draft is cleared afterwards.
- **Word**: not installed.
- Not covered: focus changing mid-paste, multi-line text, non-US keyboard layouts (the Ctrl+V virtual key is
  layout-independent, but this wasn't checked), secure/password fields, a real microphone, hotkeys, and battery or laptop CPUs.

## Reproduce

Everything downloaded or generated (SDK installer, models, CUDA DLLs, LibriSpeech, fixtures, logs, screenshots)
is in `windows/spike/.cache/` (git-ignored, about 9 GB). .NET 10 SDK was installed user-locally to `%LOCALAPPDATA%\dotnet-sdk`.

```sh
export DOTNET_ROOT="$LOCALAPPDATA/dotnet-sdk"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build Speech -c Release -o out/speech-cpu    # copy to out/speech-cuda and add the CUDA DLLs for GPU runs
dotnet build Paste  -c Release -o out/paste
python -I tools/make_librispeech_fixtures.py .cache/librispeech/LibriSpeech/test-clean .cache/fixtures/libri
python -I tools/make_librispeech_full.py     .cache/librispeech/LibriSpeech/test-clean .cache/fixtures/test-clean
out/speech-cpu/Speech.exe tts-fixtures .cache/fixtures/tts ../../validation/long-dictation/fixtures/passages.json
bash tools/run_speech_matrix.sh        # all speech configs → .cache/runs/*.jsonl
bash tools/rerun_gpu.sh                # GPU configs only; run with the GPU idle
python -I tools/summarize.py .cache/runs
REMY_UNICODE_DELAY_MS=30 out/paste/Paste.exe notepad chrome-textarea chrome-editable vscode terminal
out/paste/Paste.exe notepad-admin      # needs UAC approval
```

The paste harness takes focus and types into windows it opens. Don't use the PC while it runs.
