# Remy for Windows: architecture and build rules

Remy for Windows is a port of the macOS app in `app/`. The goal is feature parity
with the shipped Mac build. The parity specs in `docs/parity/` are the source of
truth for behavior, defaults and every user-facing string:

| Spec | Covers |
| --- | --- |
| `parity/ui.md` | Design tokens, main window, every settings page, overlay, tray |
| `parity/pipeline.md` | Hotkeys, session state machine, audio, transcription, delivery, history, recovery, sounds |
| `parity/cleanup.md` | Cleanup providers, prompts (byte-exact), safety checks, profiles, context awareness, vocabulary, filler removal |
| `parity/settings-models.md` | Every persisted setting, model store, startup, storage paths, diagnostics, tests to port |

Do not read or edit `app/` (Swift). If a spec is ambiguous, pick the simplest
behavior that matches the Mac and note it in `docs/DECISIONS.md` (append only).

## Stack

- .NET 10, C#, WPF for UI. x64 only. Build: `$env:DOTNET_ROOT="$env:LOCALAPPDATA\dotnet-sdk"; & "$env:DOTNET_ROOT\dotnet.exe" build windows/Remy.Windows.slnx`
  (bash: `export DOTNET_ROOT="$LOCALAPPDATA/dotnet-sdk"; "$DOTNET_ROOT/dotnet.exe" build ...`).
- Speech: sherpa-onnx 2.0.1, Parakeet TDT 0.6B v2 int8, CPU. Validated in `windows/spike/RESULTS.md`.
- Local cleanup: S1-mini GGUF via LLamaSharp 0.30.0 (CPU backend). Validated: ~0.5 s per cleanup.
- Remote cleanup: plain `HttpClient` (Ollama, OpenRouter, OpenAI-compatible, saved profiles).
- History: SQLite via Microsoft.Data.Sqlite.
- Delivery: clipboard + `SendInput` Ctrl+V (the spike showed UI Automation insertion is unreliable).

## Projects and ownership

| Project | Contents | Depends on |
| --- | --- | --- |
| `src/Remy.Core` | Settings, hotkey model, contracts (`Contracts/`), session coordinator, cleanup (remote providers, prompts, safety, profiles), context classification, filler removal, history repository, recovery store, model store, diagnostics | nothing platform-specific |
| `src/Remy.Speech` | `ISpeechRecognizer` on sherpa-onnx | Core |
| `src/Remy.Cleanup.Local` | `S1MiniCleanupEngine` on LLamaSharp | Core |
| `src/Remy.Platform` | Win32 adapters: keyboard hook, WASAPI capture, volume/mute, clipboard + SendInput, focus/UIA destination capture, context capture, Credential Manager, sounds, autostart, power events | Core |
| `src/Remy.App` | WPF: design system, main window + pages, overlay, tray, composition root | all |
| `tests/Remy.Core.Tests` | xUnit tests for Core | Core |

Each agent works only in the folders it is assigned. `Remy.Core/Contracts/` is
shared and owned by the lead; if you need a contract change, make the smallest
additive change and list it in your final report.

## Windows decisions

| Topic | Decision |
| --- | --- |
| Data folders | Roaming config `%APPDATA%\Remy\` (settings.json, CleanupProfiles\profiles.json). Local data `%LOCALAPPDATA%\Remy\` (Models\, History\history.sqlite, Recovery\, Sounds\, Logs\). Override root with env var `REMY_DATA_ROOT` (used by tests and dev runs: both trees go under it). Paths live in `Remy.Core.Storage.RemyPaths`. |
| Settings | `settings.json`, keys identical to the Mac key names (camelCase), enums as Mac raw-value strings. Tolerant load (bad value -> default for that key only). Atomic write (temp + replace). `remy.configurationSchemaVersion: 1` gate as on Mac. |
| Engines | One engine: Parakeet v2 via sherpa-onnx. English only. Apple Speech, AssemblyAI, Apple Intelligence, FluidAudio vocabulary mode, input-source switching: not ported (hidden). |
| Speech model | sherpa-onnx `sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8` bundle (encoder/decoder/joiner int8 + tokens.txt), downloaded on demand into `Models\parakeet-tdt-0.6b-v2-int8\` with pinned URL + SHA-256 (see `Remy.Core.Models.ModelCatalog`). Display name "Parakeet v2". |
| Long audio | Final decode splits at the quietest 100 ms near every 20 s (spike `DecodeChunked`). Whole-file decode of 5 min used 7.9 GB, so chunking is mandatory. |
| Preview | Mac v2 cadence: rolling last-15 s window, first decode after 0.5 s of audio, then every 1 s, every 2 s once total audio > 8 s. |
| Default shortcut | Modifier-only **Ctrl+Win**, hold to record (same shape as the Mac's modifier-only Ctrl+Option+Cmd). Display "Ctrl+Win". When a modifier-only binding that includes Win fires, the hook injects a no-op key (VK 0xFF) before Win is released so the Start menu does not open. Cancel: Esc, hold-to-cancel 1 s. |
| Hotkey display | `Ctrl`, `Alt`, `Shift`, `Win`, side-aware `LCtrl`/`RCtrl` etc., joined with `+`, key name last (e.g. `Ctrl+Alt+Space`). Order: Ctrl, Alt, Shift, Win, key. |
| System shortcut conflicts | Windows list: Win+H "voice typing", Win+Space "input switching", Win+V "clipboard history", Win+Shift+S "Snipping Tool", Alt+Tab "app switching", Win+L "lock", Ctrl+Shift+Esc "Task Manager". Message `Conflicts with Windows <name>`. |
| Sounds | No macOS sounds. Built-ins are WAVs from `%WINDIR%\Media` that exist on the machine (id `builtin:<file name without .wav>`), plus `none` and `custom:<uuid>.<ext>` (wav/mp3 only). Defaults: start `builtin:Speech On`, completion `builtin:Speech Off` (if a file is missing, play nothing). Platform owns the list. |
| Credentials | Windows Credential Manager (`CredWrite`/`CredRead`, generic credential, target `Remy/<service>/<account>`), same service/account names as the Mac Keychain items. |
| Paste | Clipboard set to the final text and left there (Mac never restores it). Then `SendInput` Ctrl+V after physical modifiers are released (wait up to 1 s). Never retry once the paste is claimed. |
| Secure fields | UIA `IsPassword` on the focused element -> copy-only. Elevated target (UIPI, `SendInput` returns 0 or target integrity level higher) -> copy-only with "Press Ctrl+V" hint. |
| Terminals | Windows Terminal (`WindowsTerminal.exe`), `conhost.exe`/`cmd.exe`/`powershell.exe`/`pwsh.exe` consoles, `wezterm-gui.exe`, `alacritty.exe`, `mintty.exe`: multiline text is copy-only (Mac rule). |
| Overlay | WPF borderless topmost window, `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT` (click-through), shown without activation on the monitor with the foreground window (fallback: cursor). |
| Tray | H.NotifyIcon. "Menu Bar Only" -> "Tray only" (no taskbar button when the main window is closed); "Dock and Menu Bar" -> "Taskbar and tray". |
| Theme | Light/Dark/System from `AppsUseLightTheme` registry value. |
| Single instance | Named mutex `Local\io.github.brysonjl.remy`; a second launch signals the first to show its main window and exits. |
| Launch at login | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `Remy`. |
| Logs | `%LOCALAPPDATA%\Remy\Logs\remy-YYYYMMDD.log`, metadata only (never transcript text), 7 days kept. |

## Testing on the user's machine (hard rules)

- Never start, focus, type into or kill applications you did not create (Notepad, browsers, editors...). On Windows 11, Notepad runs every window and tab in one process; killing it closes the user's tabs.
- For paste/UIA tests, launch your own test window (e.g. a WinForms Form with a TextBox titled `RemyE2ETarget`), record its PID, and close only that PID.
- Never `Stop-Process -Name`; kill only PIDs you launched.

## Threading rules

- UI thread only touches WPF. The session coordinator runs on its own `SemaphoreSlim`-serialized async context; it raises events the UI marshals with `Dispatcher`.
- Every await in the pipeline is followed by a session-token check; stale results are dropped silently (`pipeline.md` §2.1).
- The keyboard hook callback does no work beyond matching and enqueueing.
- Clipboard calls happen on an STA thread (the spike hit `ThreadStateException` otherwise). `Remy.Platform` owns a dedicated STA worker for clipboard and SendInput.
