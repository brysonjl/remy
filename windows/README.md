# Remy for Windows (preview)

A Windows port of Remy with the Mac app's main features: hold a shortcut, speak,
let go, and the text lands in the field you were typing in. Speech recognition
and the optional S1-mini cleanup run on your PC.

It is a preview, tested on one Windows 11 machine.

## Download

[Remy for Windows 0.2.0 (preview)](https://github.com/brysonjl/remy/releases/tag/windows-v0.2.0): download
`Remy-Windows-0.2.0-x64.zip`, unzip it anywhere and run `Remy.exe`. There is no
installer; keep the folder together. Remy isn't code-signed yet, so Windows may
show "Windows protected your PC": click **More info**, then **Run anyway**.
Licenses are in the `Notices` folder. Please [report what you find](https://github.com/brysonjl/remy/issues).

## Use it

- **Dictate:** hold **Ctrl+Win**, speak, release. Hold **Esc** for one second to cancel.
  Both shortcuts can be changed on the Shortcuts page.
- **First run:** the Speech Model page downloads Parakeet v2 (about 660 MB).
  Transcript Cleanup can download S1-mini (about 480 MB) or use Ollama,
  OpenRouter or any OpenAI-compatible server.
- **Transforms:** select text in any app and press a transform's shortcut to rewrite it
  with your Transcript Cleanup provider (Polish and Prompt Engineer built in, up to 8 of
  your own). Set them up on the Transforms page.
- **Text rules:** on the Transcript Cleanup page, give vocabulary terms "heard as"
  spellings, add snippets (say a trigger, get saved text), import vocabulary from CSV,
  and edit or add writing styles. "Spell names from the active window" is off by default.
- **Data:** settings in `%APPDATA%\Remy`, models, history, recovery audio and
  logs in `%LOCALAPPDATA%\Remy`.

Not ported from the Mac: Apple Speech, AssemblyAI, Apple Intelligence cleanup and
transforms, custom acoustic vocabulary and input-source switching. On-screen names
help cleanup only (the Windows speech engine takes no hints).

## Build

To make the release zip (runs the tests first):
`powershell -ExecutionPolicy Bypass -File windows/scripts/package-release.ps1`.
It writes `windows/out/release/Remy-Windows-<version>-x64.zip` and a `.sha256` file.

To build and run by hand:

You need Windows 11 on x64 (Windows 10 is untested) and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
(`global.json` pins 10.0.403 or newer). From the repository root:

```powershell
dotnet build windows/Remy.Windows.slnx -c Release
dotnet test windows/Remy.Windows.slnx -c Release
dotnet publish windows/src/Remy.App/Remy.App.csproj -c Release --self-contained -o windows/out/Remy
windows/out/Remy/Remy.exe --show
```

Tests that need the real models skip themselves unless the models are in
`windows/spike/.cache/` (see [spike/RESULTS.md](spike/RESULTS.md)).

## Layout

| Folder | Contents |
| --- | --- |
| `src/Remy.Core` | Settings, hotkey model, session coordinator, cleanup providers and prompts, history, recovery, model store |
| `src/Remy.Speech` | Parakeet v2 on sherpa-onnx |
| `src/Remy.Cleanup.Local` | S1-mini on LLamaSharp |
| `src/Remy.Platform` | Keyboard hook, microphone, clipboard paste, focus and context capture, Credential Manager, sounds |
| `src/Remy.App` | WPF app: main window, settings pages, recorder overlay, tray |
| `tests/` | xUnit tests for each project |
| `tools/PlatformProbe` | Manual hardware checks for the platform layer |
| `docs/` | [Architecture and Windows decisions](docs/ARCHITECTURE.md) and the Mac behavior specs the port follows (`docs/parity/`) |
| `spike/RESULTS.md` | The feasibility measurements (speed, accuracy, paste reliability) |

## Developer switches

| Switch | Effect |
| --- | --- |
| `--show` | Open the main window at launch |
| `REMY_DATA_ROOT=<folder>` | Keep all settings and data under that folder |
| `REMY_FAKE_AUDIO=<wav>` | Replace the microphone with a WAV file |
| `--dev-dictate-seconds N` / `--dev-dictate-delay S` | Start a dictation after S seconds and stop it after N |
| `--demo`, `--snap <folder>` | Fill the UI with sample data; save screenshots of every page |

Automated paste tests must type only into a window the test itself created.
See the testing rules in [ARCHITECTURE.md](docs/ARCHITECTURE.md).

Third-party components: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
