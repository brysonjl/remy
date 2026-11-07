# Proposal: Remy dictation

Status: proposed for implementation; user scope confirmed 2026-10-19. No application has been built yet.

## Product outcome

Press a configurable global shortcut, speak, press again, and receive cleaned text in the field you were using and on the clipboard. A small nonactivating overlay shows recording state and provisional words. Every completed transcription has a local history record containing raw recognition, corrections, transformation attempts, selected final text, and delivery outcome. Local ASR is the default and only first-release speech route; cleanup can be off, local, or explicitly remote.

Primary target: this user's Apple M4 Pro/48 GB Mac, macOS 27.0.1, English. Native SwiftUI/AppKit application distributed directly. Retain the upstream minimum OS initially where practical, but claim tested support only for versions in the release matrix; do not promise Intel or older-OS parity without testing. Browser editors, terminal prompts, and coding agents are first-class test destinations.

## Foundation decision

**Use a controlled Dictate Anywhere fork first.** At the inspected commit it already offers native capture/hotkeys, NVIDIA Parakeet through FluidAudio, live previews, configurable compatible cleanup, guarded insertion and raw/cleaned history. Its MIT source and regression tests are useful starting assets. [Source audit and pinned links](research/existing-apps.md).

| Approach | Benefit | Cost/risk | Decision |
| --- | --- | --- | --- |
| Dictate Anywhere fork | Closest existing native behavior; retain integration code/tests | Large coordinator files; weak history persistence; inherited update/config paths | Recommended, with baseline gate and incremental changes |
| New Swift app, reused components | Clean boundaries and smaller initial surface | Rebuild audio, permissions, hotkeys, target focus and paste edge cases | Contingency if bounded baseline repair is uneconomic |
| OpenWhispr fork | Cross-platform design and broader existing features | Electron/native helper stack plus unrelated product services | Reference source; reconsider only if platform scope changes |

Run a timeboxed baseline first: suggested 2–3 focused engineering days, an estimate rather than a promise. If build/quality/OS integration failures cannot be repaired in a similarly bounded increment, document them and compare a minimal new Swift shell using the same retained low-level components. A large source file by itself is not grounds for a rewrite. Avoid extracting all modules before producing a working daily path.

Independent identity is mandatory: application/bundle name, Keychain service, data directory, login item, updater feed and signing identity. Disable inherited update checks before the first branded release run. Keep upstream attribution and model notices. “Remy” is the working project name; choose final distributed app identity before public release to avoid confusion with existing installed software.

## Initial experience

1. Onboarding checks microphone and Accessibility capabilities independently, explains the exact missing capability, and installs a user-selected pinned ASR bundle. Cloud cleanup is opt-in; API keys are entered in the app and held in Keychain.
2. Default interaction is toggle-to-record; support hold-to-talk as an alternate binding. Use the inherited configurable hotkey machinery, validate right Option/Fn on this OS, and offer a normal chord fallback. Do not reuse Spokenly's active binding during side-by-side testing.
3. Capture the destination before showing the overlay. Start recording immediately when ready; show explicit preparation state otherwise. Escape/cancel stops and discards delivery, with a clear recovery policy.
4. Show revisable live text locally. At stop, flush captured audio, finalize ASR, apply enabled vocabulary corrections, then run the selected cleanup profile.
5. Persist the result, copy the final text, revalidate destination, and attempt paste once. Retain the final clipboard text. If focus changed or insertion cannot be judged safe, show “Copied; paste when ready.” Do not silently refocus a different document.
6. History supports search, raw/final comparison, copy either version, retry transformation with a new profile, export, individual deletion and clear-all. Retrying from History never automatically pastes.

Terminal-specific policy: never synthesize Return. Single-line text can paste normally. Multiline text or a trailing newline in a terminal is copy-only by default, because pasting itself can execute shell input; an explicit per-app opt-in can permit it. Terminal-hosted coding agents inherit this conservative default until a destination-specific capability is established. Browser chat input is not automatically submitted.

## Models and providers

**ASR:** keep Parakeet TDT 0.6B v2 as the initial final-transcript model. NVIDIA supplies the model; this Mac runs its converted weights through Core ML, not CUDA. A matching family name does not establish identical Spokenly packaging, quality, or 496 MB size. Preserve the fork's pinned FluidAudio 0.17.5 for the baseline; assess 0.17.7 separately rather than upgrading while rebranding. [Speech evidence](research/speech-runtime.md).

**Streaming:** first provide revisable Parakeet previews and one final paste. Evaluate genuine Nemotron streaming at 560/1120 ms under the inspected newer SDK. Choose it only if its final text and practical latency pass the same corpus. Streaming LLM output appears in Remy; live typing into arbitrary external fields is deferred because revisions and cleanup can change earlier text.

**Cleanup:** preserve the user's Ollama Cloud profile as a migration baseline and evaluate local `qwen3.5:4b` first, then 9B if needed. This is a candidate choice, not a measured quality claim. Expose exact model ID, base URL, provider type, prompt, timeout and supported thinking controls. Off is a fully supported route. [Default prompt and model rationale](cleanup-policy.md).

Initial provider adapters: Ollama native and generic OpenAI-compatible. Native Anthropic/Gemini can follow without changing the session pipeline. Model discovery is helpful but optional; manual model IDs remain supported. No cloud fallback without selecting a remote route.

## First release scope

| User requirement | Planned behavior | Acceptance work |
| --- | --- | --- |
| Global start/stop | Toggle plus hold-to-talk, cancellation, conflict reporting | A02, A10 |
| Accurate local NVIDIA ASR | Explicit Parakeet v2 bundle, offline runtime, tail preservation | A00, A03 |
| Separate model cleanup | Off/local/cloud profiles; custom prompts | A05–A06 |
| Low latency/streaming | Warm engine, bounded preview work, streaming cleanup preview | A03, A06, A09 |
| Open source/data inspection | Retained MIT notices, manifests, visible route, no telemetry by default | A01, A10–A11 |
| Configuration/update control | Versioned settings, secret-free export, manual pinned updates | A01, A08, A11 |
| Daily reliability | Cancellation/recovery, fault handling, soak and actual-app tests | A02, A07, A10 |
| Raw and AI history | SQLite attempts and delivery records; search/export/reprocess | A04, A08 |
| Paste plus clipboard | Persistent final clipboard, focus validation, explicit delivery outcomes | A07 |

A first usable vertical slice ends at A07 with Parakeet, one local and one cloud profile, durable history, and guarded paste. The daily-driver release additionally requires onboarding/configuration, measured latency, fault/soak checks, and controlled packaging. Native extra-provider adapters and advanced streaming selection can follow if the core passes.

## Future-compatible scope

- **Vocabulary:** simple explicit `Xway → Ixway` corrections are cheap enough for the initial release. Acoustic/contextual ASR biasing remains separate because it affects recognition and can introduce false positives.
- **Wake word:** a local detector later emits the same activation event as the hotkey, with an armed indicator, small pre-roll buffer, battery budget and false-trigger testing. It must not require keeping the full ASR engine actively decoding all day.
- **SystemOne/command mode:** reserve versioned intent events and a separate action broker. Dictation never invokes actions. Future partial transcripts may produce speculative intent previews; executable actions require stable intent and explicit command-mode policy. The SystemOne API/schema/deployment is unspecified, so no integration protocol is invented here.

## Defaults and decisions still adjustable

Use text history retained until deletion by default, with optional age/size limits; audio recovery spool is opt-in and short-lived. The user can select a finite retention policy during onboarding. Context awareness can adapt cleanup to the destination application. Keep remote surrounding-text sharing off by default and expose it separately from app/category awareness. Password fields and excluded applications remain outside context capture. Detailed timing diagnostics are local and content-free by default.

Remaining preferences do not block the baseline: exact hotkey, primary microphone, specific browser/terminal/coding-agent apps, desired retention limit, and whether the local 4B cleanup result is good enough. Final branding/public distribution and the meaning/API of SystemOne are later decisions. Record answers in implementation notes rather than silently changing these defaults.

Estimated effort: 3–6 focused engineering weeks for one experienced macOS engineer after a successful baseline, depending heavily on inherited build health and device/paste failures. Parallel agents can reduce independent implementation time; they cannot compress real-device permission checks or daily-use soak time. This is a planning range, not a delivery commitment.
