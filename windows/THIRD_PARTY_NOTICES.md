# Windows preview: third-party components

This list covers the Windows build in `windows/` only. The macOS app's reviewed
inventory is the root [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md).

The licenses below are the ones each NuGet package declares. Their license
texts are kept in [`licenses/`](licenses/) (copied from the NuGet package, or
from the upstream repository at the matching tag when the package has none) and
ship in the release zip's `Notices` folder with the .NET runtime's license and
notices. This has not had the hash-pinned review the macOS release received.

## NuGet packages

| Package | Version | Declared license | Used by |
| --- | --- | --- | --- |
| org.k2fsa.sherpa.onnx (+ runtime.win-x64, includes ONNX Runtime) | 2.0.1 | Apache-2.0 | Speech recognition |
| LLamaSharp | 0.30.0 | MIT | S1-mini local cleanup |
| LLamaSharp.Backend.Cpu (includes llama.cpp native builds) | 0.30.0 | MIT | S1-mini local cleanup |
| Microsoft.Data.Sqlite (+ .Core) | 10.0.14 | MIT | History |
| SQLitePCLRaw (bundle_e_sqlite3, core, lib, provider) | 2.1.13 | Apache-2.0 (SQLite itself is public domain) | History |
| NAudio | 2.2.1 | MIT (file license, Copyright 2020 Mark Heath) | Microphone capture, volume |
| FlaUI.UIA3 / FlaUI.Core | 5.0.0 | MIT (file license) | Focused-field and context capture |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | UI view models |
| H.NotifyIcon.Wpf / H.NotifyIcon | 2.4.1 | MIT | Tray icon |
| System.Management | 10.0.0 | MIT | Hardware detection for the speech engine |

Test-only: xunit 2.9.3 (Apache-2.0), xunit.runner.visualstudio 3.1.4 (Apache-2.0),
Microsoft.NET.Test.Sdk 17.14.1 (MIT). The .NET runtime is included in
self-contained builds (MIT).

## Separately downloaded models

No model weights are in this repository. The app downloads them on demand from
pinned revisions with SHA-256 checks (`src/Remy.Core/Models/ModelCatalog.cs`).

- **Parakeet TDT 0.6B v2, sherpa-onnx int8 export** by csukuangfj, revision
  `84b478d541cdbf0c296a7ab5f1e20a6f8f998114`
  ([files](https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8/tree/84b478d541cdbf0c296a7ab5f1e20a6f8f998114)),
  based on [NVIDIA Parakeet TDT 0.6B v2](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2),
  CC-BY-4.0. This is a different conversion from the CoreML one the macOS app uses.
- **S1-mini by Superwhisper**, the same GGUF revision and license as the macOS
  app (Apache-2.0 with an additional naming term). See the root notices file.

## App artwork

`src/Remy.App/Assets/` reuses the Remy icon and mascot artwork from `app/`.
`remy.ico` is generated at build time from `Assets/AppIcon/*.png`.
