# Parity spec: settings, models, startup/lifecycle, storage, diagnostics, tests

Source: `app/Dictate Anywhere/` (Swift). Paths below are relative to that folder unless stated. `File:line` references are to the Swift source at the time of extraction. Items marked **MAC-ONLY** have a one-line Windows suggestion. Windows implementers do not need to read Swift: every constant, default and user-facing string is reproduced here.

Conventions: "Page" = the sidebar page that exposes the setting (sidebar titles: Speech Model, General, Shortcuts, Text & Overlay, Transcript Cleanup, Internal Prompts (hidden in supported build), History, About; `MainWindow.swift:10-33`). Default page on launch is Speech Model (`AppState.swift:47`).

---------------------------------------------------------------------------

## 0. Product identity constants

| Constant | Value | Ref |
|---|---|---|
| Release bundle id | `io.github.brysonjl.remy` | `RemyStoragePaths.swift:8`, `RemyMigrationBootstrap.swift:8` |
| Dev bundle id | `io.github.brysonjl.remy.dev` | `RemyMigrationBootstrap.swift:7` |
| Test-host bundle id | `io.github.brysonjl.remy.testhost` | `App.swift:43` |
| Display name | `Remy` (from `CFBundleDisplayName`, fallback "Remy") | `App.swift:66-70` |
| Supported engines (feature policy) | Local Parakeet + Apple Speech only. Cloud speech (AssemblyAI) is disabled: `allowsCloudSpeechRecognition = false`; any stored `assemblyAI` engine is coerced to `parakeet` | `RemyFeaturePolicy.swift:1-17` |

Windows: one engine only (sherpa-onnx Parakeet). Drop `engineChoice` choices Apple Speech/AssemblyAI entirely (see section 1.4 for how to treat the key). Suggested Windows app id: `io.github.brysonjl.remy` for the folder name (or `Remy`), Dev builds use a `.dev` suffix so they stay isolated.

---------------------------------------------------------------------------

## 1. Settings model (`Settings.swift`, 2,570 lines)

### 1.1 Storage location and format

- Storage is macOS `UserDefaults.standard` = the app's preferences plist (`~/Library/Preferences/<bundle id>.plist`). Every property writes through immediately in its `didSet` (`Settings.swift:1057-1712`); there is no explicit Save and no batching. Load is a one-time read in `private init()` (`Settings.swift:1738-2016`), singleton `Settings.shared` (`:949`).
- Complex values (arrays/dicts/structs) are JSON-encoded (`JSONEncoder`) into a `Data` blob under their key. Primitive values (Bool, String, Float, `[String]`) are stored natively.
- Secrets are NOT in preferences. They live in macOS Keychain (generic password; service = `<bundle id>.<suffix>`, account strings below). Keychain items: `<bid>.openrouter`/`openrouter-api-key`, `<bid>.openai-compatible`/`openai-compatible-api-key`, `<bid>.assemblyai`/`assemblyai-api-key` (`Settings.swift:971-973, 2505-2560`). Writing an empty/whitespace value deletes the item; values are trimmed. On Windows use Windows Credential Manager (or DPAPI `ProtectedData`) with the same service/account names; error string on failure: `Could not save the API key to Keychain. Try entering it again.` (`:974`; on Windows reword to "Credential Manager").
- Reading rule used everywhere in `init`: a missing key OR a key with the wrong type OR an unknown enum raw value falls back to the default (`defaults.object(forKey:) as? Bool ?? default`, `Enum(rawValue:) ?? default`). Implement as tolerant deserialization: never throw, never reset the whole file because one value is bad.
- Windows suggestion: `%APPDATA%\Remy\settings.json` (one JSON object, camelCase keys identical to the Mac key names below, enums stored as the raw-value strings listed). Write atomically (temp file + replace) on every change, debounce is acceptable. Keep the Mac key names so a future settings import/export is trivial.

### 1.2 Versioning and validation

| Mechanism | Behavior | Ref |
|---|---|---|
| `RemyConfigurationVersion` | One key `remy.configurationSchemaVersion` (Int). `currentVersion = 1`. Preflight: key absent or `0` -> `legacy`; `== 1` -> `current`; `> 1` -> `unsupported(version)`; present but not an integer (bool, string, negative) -> `malformed`. `migrateIfNeeded`: legacy -> writes `1` and returns `migrated(0->1)`; current -> no-op; unsupported/malformed -> change nothing. Re-checks the stored marker at commit time so a stale preflight can never downgrade a future version. Version 1 only adds the marker; no other data migration. | `RemyConfigurationVersion.swift:1-58` |
| `RemyStartup.configurationIssue` | Runs before any other settings/credential/login work. `current`/`migrated` -> OK (nil). `unsupported(v)` -> blocking message: `These settings use configuration version <v>. This build supports version 1. Open a compatible Remy build or restore a Remy settings backup. No Remy settings were changed.` `malformed` -> `The Remy configuration version could not be read. Restore a Remy settings backup before reopening the app. No Remy settings were changed.` | `RemyStartup.swift:4-15` |
| Blocking dialog | `NSAlert` title `Remy cannot open these settings`, body = issue text, single button `Quit`; app then exits without starting. | `App.swift:48-60` |

Windows: keep `remy.configurationSchemaVersion: 1` in `settings.json`; the same three-way gate (absent/0 = legacy then write 1; 1 = ok; >1 or non-integer = blocking message box with "Quit"). Port the `RemyConfigurationVersionTests` cases (section 9).

Per-setting validation on load (all in `init`):
- Enums: unknown raw value -> default.
- `engineChoice`: passed through `RemyFeaturePolicy.effectiveEngineChoice` (assemblyAI -> parakeet) both on load and every set (`:1089-1103`).
- `parakeetModelChoice`: if not runnable on this hardware -> `availableFallback(for: selectedLanguage)` applied in memory only, the stored value is not rewritten (`:1995-2006`). (Windows: n/a, single model.)
- `transcriptPostProcessingMode == fluidAudioVocabulary` while engine is Parakeet and the model does not support FluidAudio vocabulary -> coerced to `none` (`:2007-2011`, also in didSets `:1097-1115`). (Windows: vocabulary correction mode depends on the implementer's engine; if unsupported, hide the mode and coerce to `none`.)
- Cleanup prompts: empty/whitespace stored prompt is replaced AND persisted with the recommended prompt (`loadCleanupPrompt`, `:1717-1724`).
- Hotkey bindings are canonicalized on load (section 1.5).
- Input-source mappings are sanitized (section 1.6).
- Sound identifiers: validated by `RemySoundEffects.isValidIdentifier` at use time.
- Soft limits: AssemblyAI prompt override text truncated to 4,000 chars (`:1194`); history capped at 50 entries (`:2208`, legacy path only; production history lives in SQLite, see section 7).

### 1.3 Complete settings table

Key = persisted key name. "Type" gives Mac storage; "Win" is a recommended JSON type. Defaults are the values used when the key is missing. All refs are `Settings.swift` lines unless noted (declaration/default).

**General page (`SettingsView.swift`)**

| Key | Type | Default | Allowed values | Controls | Ref |
|---|---|---|---|---|---|
| `launchAtLogin` | Bool | `false` | true/false | Registers the app as a login item (UI "Launch at login"). Startup only *observes* registration state; only a user toggle may register/unregister. Error strings in section 5.3 | `:1014, 1681, 1987, 2213-2252`; UI `SettingsView.swift:25` |
| `appAppearanceMode` | String enum `AppAppearanceMode` | `menuBarOnly` | `menuBarOnly` = "Menu Bar Only"; `dockAndMenuBar` = "Dock and Menu Bar" | Dock icon vs tray-only. UI label "App appears in" | `:61-78, 1689, 1989`; UI `SettingsView.swift:34` |
| `prewarmEnginesAtStartup` | Bool | `true` | | "Prepare models ahead of time": load selected speech + cleanup models at launch and after cleanup/preference changes; off = load on first dictation. Caption: `Warm selected models at launch and when cleanup is enabled. Uses more memory to avoid first-use loading.` | `:1015, 1708, 1988`; UI `SettingsView.swift:43-48` |
| `themeMode` | String enum `ThemeMode` | `system` | `system`="System", `light`="Light", `dark`="Dark" | App-wide light/dark override. Caption: `System follows your Mac's appearance setting.` (Windows: "your PC's") | `:82-103, 1698, 1991`; UI `SettingsView.swift:52-62` |
| `remyDictationDestination` | String enum `RemyDictationDestination` | `originalField` | `originalField`="Where dictation started"; `currentField`="Where I’m focused when it finishes" (curly apostrophe) | Where the final text is delivered. Frozen at the start of each dictation. UI label "Send dictated text to"; section "Text destination". Captions: original -> `Returns to the app and field where dictation started. If that field cannot be restored, copy the result and paste it manually.`; current -> `Chooses your focused app and field after cleanup finishes. If that field is unavailable, copy the result and paste it manually.` UI appends ` Changes apply to your next dictation.` | `RemyDictationDestination.swift:1-24`; `Settings.swift:982, 1631, 1973` |
| `selectedLanguage` | String enum `SupportedLanguage` | `en` | see section 3 | Expected language for the local (Parakeet) engine. Shown on General ("Expected language") and on Speech Model page | `:994, 1139, 1816` |
| `appleSpeechLanguage` | `SupportedLanguage` | `en` | same | Separate language for Apple Speech so switching engines does not overwrite `selectedLanguage`. **MAC-ONLY** (Apple Speech engine). Windows: omit | `:995, 1147, 1818` |
| `selectedMicrophoneUID` | String? | absent (= follow system default) | device unique id | Microphone choice. Removed from store when set to nil. UI "Microphone" dropdown; first option text = `appState.automaticMicrophoneTitle`. If the saved device is not present the UI shows Automatic and the tray menu shows Automatic checked | `:1018, 1601-1609, 1958`; `SettingsView.swift:134-154`, `AppDelegate.swift:397-419`. Windows: store the MMDevice endpoint id |
| `boostMicrophoneVolumeEnabled` | Bool | `true` | | "Boost microphone volume during recording" | `:1003, 1613, 1961` |
| `muteSystemAudioDuringRecordingEnabled` | Bool | `false` | | "Mute system audio during recording". Migration: if absent, copies legacy `autoVolumeEnabled` (default false) and persists it | `:1004, 1619, 1963-1970` |
| `autoVolumeEnabled` | Bool | legacy, read-only | | Legacy name of the mute setting (migration source only) | `:1006` |
| `soundEffectsEnabled` | Bool | `true` | | "Sound effects" master switch | `:1007, 1637, 1976` |
| `soundEffectsVolume` | Float | `0.3` | 0.0-1.0 (migration validator enforces finite in [0,1]) | Slider "Sound effects volume" (speaker icons either side) | `:1008, 1643, 1977`; `RemyMigrationPreferences.swift:65, 99-102` |
| `remyDictationStartSound` | String id | `builtin:Purr` | `none`, `builtin:<name>` where name in `Tink, Pop, Purr, Ping, Glass, Hero, Submarine, Funk, Basso, Bottle, Blow, Frog, Morse, Sosumi`, or `custom:<uuid>.<ext>` (ext in `wav aiff aif caf mp3 m4a`, id <= 64 UTF-8 bytes) | Sound at recording start. Labels: `none`->"None", builtin->its name, custom->"Custom audio", invalid->"Unavailable sound" | `RemySoundEffects.swift:9-30`; `Settings.swift:1009, 1649, 1978` |
| `remyDictationCompletionSound` | String id | `builtin:Pop` | same as above | Sound when dictation completes | same, `:1010, 1652, 1979` |

Sound details: custom sounds are imported into `<app dir>/Sounds/<UUID>.<ext>` (max 10 MiB, max 10 s duration; `RemySoundEffects.swift:33, 100, 174, 195`). **MAC-ONLY**: the 14 built-ins are macOS system sounds (`NSSound`); Windows should ship 14 equivalently named bundled WAVs or map to a smaller set, keep the `none`/`builtin:`/`custom:` id scheme. Sound playback is skipped under test (`:2022`).

**Speech Model page (`ModelsView.swift`)**

| Key | Type | Default | Allowed | Controls | Ref |
|---|---|---|---|---|---|
| `engineChoice` | String enum `TranscriptionEngineChoice` | `parakeet` | `parakeet` ("FluidAudio"), `appleSpeech` ("Apple Speech"), `assemblyAI` ("AssemblyAI"); effective set = parakeet, appleSpeech | Which recognizer. Details: parakeet "Downloadable on-device speech models powered by FluidAudio."; appleSpeech "Apple's latest on-device speech-to-text model, managed by macOS."; assemblyAI "Cloud dictation with transcription and cleanup in one request." Dropdown disabled while status != idle, preparing, installing, downloading or deleting | `:105-145, 1089, 1786-1811`; `ModelsView.swift:27-51` |
| `parakeetModelChoice` | String enum `ParakeetModelChoice` | `multilingual` | see section 2.1 | Selected on-device model | `:255-633, 1106, 1791` |
| `userHasChosenEngine` | Bool | `false` | | Whether user picked an engine explicitly. If false and a Parakeet model exists on disk the engine is forced to Parakeet. Also reset to false when a discontinued Apple Speech selection is replaced | `:1019, 1120, 1786-1810` |
| `legacyAppleSpeechMigrationPending` | Bool | derived | | True when stored engine was `appleSpeech` on an OS where it is unsupported and no Parakeet model is on disk. Drives a "set up speech" prompt. **MAC-ONLY**; Windows omit | `:1020, 1128, 1811-1813` |
| `autoStopAfterSpeechEndsEnabled` | Bool | `false` | | "Stop hands-free dictation after speech ends"; only shown for models with end-of-utterance support (Parakeet EOU 120M) | `:1005, 1625, 1971`; `ModelsView.swift:137-142` |
| `assemblyAIRegion` / `assemblyAILanguage` / `assemblyAIOutputMode` / `assemblyAIInstruction` / `assemblyAIPromptOverrides` | String / enum / enum / String / JSON `[String:String]` | `global` / `en` / `polished` / `""` / `{}` | region: `global`="Global (Lowest Latency)", `us`="United States", `eu`="European Union"; output: `polished`="Polished", `verbatim`="Verbatim"; language: 32 codes (`:181-253`) | **Disabled cloud route; not reachable in supported build.** Windows: omit. Documented for completeness; the Internal Prompts page is hidden | `:147-253, 1161-1216, 1823-1840` |

**Shortcuts page (`ShortcutsView.swift`)** (details owned by another spec; the persisted contract is here)

| Key | Type | Default | Allowed | Controls | Ref |
|---|---|---|---|---|---|
| `hotkeyBindings` | JSON `[HotkeyBinding]` | one binding: modifier-only Control+Option+Command (`\u{2303}\u{2325}\u{2318}`), `holdToRecord`, new UUID | `HotkeyBinding {id: UUID, keyCode: UInt16?, modifiersRawValue: UInt64, displayName: String, mode}`; `mode` = `holdToRecord` ("Hold to Record") or `handsFreeToggle` ("Tap to Toggle") | Recording shortcuts (multiple allowed) | `:780-850, 1074, 1748-1783` |
| `cancelShortcut` | JSON `HotkeyBinding` | keyCode 53 (Esc), no modifiers, displayName "Esc", mode `handsFreeToggle` | | Cancel gesture | `:846-849, 1057, 1741-1743` |
| `holdToCancel` | Bool | `true` | | Cancel needs a hold (vs tap) | `:985, 1065, 1744` |
| `preserveCancelledSessions` | Bool | `true` | | Keep audio of cancelled recordings in recovery | `:986, 1069, 1745` |
| legacy `hotkeyKeyCode` (Int), `hotkeyModifiers` (UInt64), `hotkeyDisplayName`, `hotkeyMode` | | | | Pre-multi-binding single hotkey. If `hotkeyBindings` is absent and a legacy key exists: build one binding (mode default holdToRecord, displayName computed if empty), save as `hotkeyBindings`, delete the four legacy keys (`:1755-1779`). **MAC-ONLY migration**; Windows omit | |

Hotkey modifier bit values are macOS CGEvent masks (`HotkeyModifiers`, `:794-819`): Windows must define its own modifier model (Ctrl, Alt, Shift, Win, optional left/right side). Behavioral rules to port: Caps Lock (keyCode 57 or the caps modifier) is not allowed as a hotkey -> binding is cleared (`:2429-2438`); fn is not available on most Windows keyboards. Conflict checks (`ConflictDetector`, `:887-943`): duplicate binding -> `Duplicate of another shortcut`; system conflicts -> `Conflicts with macOS <name>` (Spotlight, Finder Search, Dictation, Force Quit x2, Screenshot area/selection/options). Windows: replace the system list with Windows-reserved combos (e.g. Win+H "Voice typing", Win+Space, Ctrl+Alt+Del, Win+Shift+S) using the message `Conflicts with Windows <name>`. Recording-vs-cancel conflict logic (prefix matching so a modifier-only recording shortcut that would fire before a keyed cancel combo is finished) is pure logic: port with tests (`HotkeyTests`).

**Text & Overlay page (`TextOverlayView.swift`)**

| Key | Type | Default | Allowed | Controls | Ref |
|---|---|---|---|---|---|
| `showTextPreview` | Bool | `true` | | Show live text in the overlay pill | `:1011, 1658, 1982` |
| `overlaySize` | String enum `OverlaySize` | `comfortable` | `compact`="Compact" (width 169), `comfortable`="Comfortable" (340), `large`="Large" (460) | Overlay pill size. `shadowInset = 10` | `OverlaySize.swift:5-26`; `Settings.swift:1013, 1665, 1983` |
| `overlayThemeMode` | `ThemeMode` | `system` | System/Light/Dark | Overlay theme independent of app theme | `:1012, 1672, 1984` |

Changing any of these posts `overlayPreferencesChanged` so a live overlay refreshes (`:1661, 1668, 1675`).

**Transcript Cleanup page (`AIPostProcessingView.swift`)**

| Key | Type | Default | Allowed | Controls | Ref |
|---|---|---|---|---|---|
| `transcriptPostProcessingMode` | String enum `TranscriptPostProcessingMode` | `none` (or `appleIntelligence` when legacy `aiPostProcessingEnabled == true`; migrated value is persisted) | `none`="Off", `fluidAudioVocabulary`="Vocabulary correction only", `appleIntelligence`="Apple Intelligence", `s1Mini`="S1-mini by Superwhisper", `ollama`="Ollama", `openRouter`="OpenRouter", `openAICompatible`="OpenAI Compatible", `profile`="Saved Profile" | Cleanup method. Supported-feature matrix at `:697-731` (none: filler removal only; vocabulary: custom vocabulary + filler; apple: prompt, vocabulary, context, writing styles, app categories, filler; s1Mini: context, per-category styling, app categories, trained controls, filler; ollama/openRouter/openAICompatible/profile: prompt, vocabulary, context, writing styles, app categories, remote context sharing, filler) | `:670-765, 1412, 1896-1904` |
| `aiPostProcessingEnabled` | Bool | legacy | | Migration source only | `:1021` |
| `aiPostProcessingPrompt` | String | recommended Apple Intelligence prompt (`:951-954`) | non-empty | Apple Intelligence cleanup instructions. **MAC-ONLY** (Apple on-device model) | `:1467, 1905` |
| `ollamaPostProcessingPrompt`, `openRouterPostProcessingPrompt`, `openAICompatiblePostProcessingPrompt` | String | recommended transcript cleanup prompt (`:955-969`, 8 paragraphs: no em dashes; keep latest intended meaning on self-correction; auto structure; numerals; remove erroneous repetition; faithful tone; vocabulary is a hint) | non-empty | Per-provider cleanup instructions; empty is reset to default and persisted | `:1491, 1509, 1565, 1932-1955` |
| `ollamaBaseURL` | String | `http://127.0.0.1:11434` | URL | Ollama server | `OllamaPostProcessingService.swift:7`; `Settings.swift:1473, 1929` |
| `ollamaModel`, `openRouterModel`, `openAICompatibleModel` | String | `""` | model id | Model id per provider; empty -> readiness `Not set up` | `:1479, 1497, 1549` |
| `ollamaReasoningEnabled` | Bool | `true` | | Reasoning toggle. Migration: legacy string `ollamaReasoningSetting` (`"disabled"` -> false, anything else true) is converted once, then the legacy key is deleted | `:1485, 1726-1736` |
| `openRouterReasoningEnabled` | Bool | `true` | | Reasoning toggle | `:1503, 1943` |
| `openAICompatibleBaseURL` | String | `http://127.0.0.1:1234/v1` | URL | OpenAI-compatible server | `OpenAICompatiblePostProcessingService.swift:6`; `Settings.swift:1543, 1948` |
| `openRouterAPIKeyEnvironmentVariable` | String | from `OpenRouterCredentialPreferences.migrate` | env var name | Name of an env var holding the OpenRouter key; if the user pastes a key literal into this field it is moved to the credential store and replaced by the env var name | `:1046, 1527-1541, 1936-1941` |
| (secret) OpenRouter / OpenAI-compatible / AssemblyAI API keys | credential store | `""` | | See 1.1 | |
| `customVocabulary` | `[String]` | `[]` | up to 10,000 terms, 4,096 bytes each, 4 MiB total (migration limits) | Names/terms used by cleanup | `:1023, 1318, 1856` |
| `isFillerWordRemovalEnabled` | Bool | `false` | | Local filler-word removal (runs before any cleanup method) | `:1001, 1286, 1852` |
| `fillerWordsToRemove` | `[String]` | `["um","uh","erm","er","hmm","嗯","呃"]` | up to 1,000 words, 256 bytes each | Words to strip. `唔` deliberately absent (Cantonese negator). `bareMatchCJKFillers = {"嗯","呃"}` are removed anywhere inside CJK text; other Han fillers only when standing alone | `:1305, 1314, 1853` |
| `dictationContextAwarenessEnabled` | Bool | `true` | | "Adapt to the current app and text field": classify destination app/field for writing style | `:1024, 1324, 1860-1862` |
| `shareDictationContextWithRemoteProviders` | Bool | `false` | | Opt-in: send app details/surrounding text to remote providers. Never migrated; always starts off after import | `:1025, 1333, 1863` |
| `emailDictationWritingStyle` | enum `DictationWritingStyle` | `formal` | see below | Style for Email | `:1026, 1342, 1866` |
| `workMessagingDictationWritingStyle` | | `formal` | | Work messaging | `:1027, 1348, 1869` |
| `personalMessagingDictationWritingStyle` | | `casual` | | Personal messaging | `:1028, 1357, 1872` |
| `otherDictationWritingStyle` | | `formal` | | Other | `:1029, 1366, 1875` |
| `extendedDictationWritingStyles` | JSON `[String: DictationWritingStyle]` keyed by category raw value | `{}` | | Styles for `terminal`, `terminalAgent`, `browser`, `codingAgent`, `aiChat`. Missing key -> `browser`: `neutral`; `terminal`, `terminalAgent`, `codingAgent`, `aiChat`: `original`. Every read/write goes through `sanitized(for:)` | `:1030, 1372, 1386-1410, 1878` |
| `dictationAppRules` | JSON `[DictationAppRule]` | `[]` | | Per-app overrides/exclusions (structure in the app-context spec) | `:1031, 1379, 1880-1885` |
| `s1MiniStyling` | enum `S1MiniStyling` | `semi-formal` | `casual`="Casual", `semi-casual`="Semi-Casual", `semi-formal`="Semi-Formal", `formal`="Formal" | Single S1-mini style | `S1MiniPostProcessingService.swift:12-28`; `Settings.swift:1034, 1418, 1909` |
| `s1MiniAppStyling` | JSON `S1MiniAppStyling {email, workMessaging, personalMessaging, other, extendedStyles?}` | `recommended` = email `formal`, workMessaging `semi-formal`, personalMessaging `semi-casual`, other `semi-formal`; if only legacy `s1MiniStyling` exists, all categories = that value | | Per-category S1-mini style | `S1MiniPostProcessingService.swift:30-52`; `Settings.swift:1913-1922` |
| `s1MiniStructure` | enum | `prose` | `prose`="Prose", `lists`="Lists" | S1-mini structure | `:79-85, 1923` |
| `s1MiniContextSetting` | enum | `automatic` | `automatic`="Automatic", `general`="General", `email`="Email" | S1-mini context (automatic -> `email` if destination category is email else `general`) | `:87-110, 1926` |
| `inputSourceMappings` | JSON `[InputSourceMapping]` | `[]` | | Keyboard layout/IME -> engine/model/language map (General page section "input source") | `:855-864, 1227, 1843-1847` |
| `inputSourceAutoSwitchEnabled` | Bool | `false` | | Enable auto switching | `:1051, 1234, 1848` |
| `pendingVocabularyModeRestore` | Bool | `false` | | Internal: auto-switch stripped vocabulary mode; restore later | `:1052, 1245, 1849` |

`DictationWritingStyle` (`DictationContext.swift:40-90`): raw -> display: `formal` "Formal", `neutral` "Neutral", `casual` "Casual", `veryCasual` "Very Casual", `excited` "Excited", `original` "Original tone". Options per category: `personalMessaging` -> [formal, neutral, casual, veryCasual, original]; all others -> [formal, neutral, casual, excited, original]. Out-of-set value -> `casual`. Cleanup instructions (verbatim, `:61-76`):
- formal: `Use polished, complete sentences with conventional capitalization and punctuation.`
- neutral: `Use a clear, balanced tone without making the text notably formal, casual, or enthusiastic. Preserve the speaker's meaning.`
- casual: `Use natural conversational wording. Keep punctuation light and do not add a final period to a short message.`
- veryCasual: `Use concise chat-style wording. Keep punctuation minimal and do not add a final period to a short message.`
- excited: `Use an upbeat, energetic tone while preserving the speaker's meaning. Do not invent claims or emojis.`
- original: `Preserve the transcript's original tone, register, emotional intensity, and level of formality. Only clean grammar, punctuation, structure, and obvious speech artifacts without restyling the wording.`

`DictationContextCategory` (`DictationContext.swift:12-38`): `email` "Email", `workMessaging` "Work messaging", `personalMessaging` "Personal messaging", `terminal` "Terminal", `terminalAgent` "Terminal coding agent", `browser` "Browser", `codingAgent` "Coding agent", `aiChat` "AI chat", `other` "Other".

Context support matrix (`dictationContextSupport`, `:751-764`): none/vocabulary -> none; appleIntelligence -> fullOnDevice; s1Mini -> categoryOnlyOnDevice; ollama/openAICompatible/profile -> fullLocalServer if configured server is local else remoteCategoryWithOptionalText; openRouter -> remoteCategoryWithOptionalText. Captured text is included only for the two "full" cases, or for the remote case when `shareDictationContextWithRemoteProviders` is true.

Recommended prompt texts to port verbatim: `recommendedAppleIntelligenceCleanupPrompt` (`:951-954`), `recommendedTranscriptCleanupPrompt` (`:955-969`).

**History page (`TranscriptHistoryView.swift`)**

| Key | Type | Default | Controls | Ref |
|---|---|---|---|---|
| `remyHistoryEnabled` | Bool | `true` | Master switch for saving transcript history (no key constant; literal string) | `:1456-1458, 1887` |
| `transcriptHistory` | JSON `[TranscriptHistoryEntry {id, text, createdAt, rawText?}]` | `[]` | LEGACY in-defaults history, now only read once by `RemyHistoryBootstrap.captureLegacy` and imported into SQLite; only written in test mode | `:877-883, 1448-1454, 1888-1893` |

**Other / internal keys**

| Key | Purpose | Ref |
|---|---|---|
| `remy.configurationSchemaVersion` | Schema marker (1.2) | `RemyConfigurationVersion.swift:8` |
| `remy.migrationCredentialReviewDismissed` | Bool; Dev->Release import prompts the user to re-enter credentials until dismissed | `RemyMigrationBootstrap.swift:11-14` |
| `NSWindow Frame *`, `NSNavLastRootDirectory`, `NSNavPanelExpandedStateForSaveMode` | AppKit bookkeeping; ignored by migration. **MAC-ONLY**; Windows: persist main window bounds separately if desired | `RemyMigrationBootstrap.swift:137-143` |

### 1.4 Enums reference (all cases, verbatim display names)

Already inline above for AppAppearanceMode, ThemeMode, TranscriptionEngineChoice, AssemblyAI*, HotkeyMode, TranscriptPostProcessingMode, DictationWritingStyle, DictationContextCategory, S1Mini*, OverlaySize, RemyDictationDestination. Remaining:

`OllamaReasoningCapability` (`:767-776`): `unsupported`, `toggle`, `level`, `required` (supportsReasoning = not unsupported).

`TranscriptCleanupFeature` (`:635-645`): customPrompt, customVocabulary, dictationContext, categoryWritingStyles, s1MiniCategoryStyling, appCategories, remoteContextSharing, s1MiniTrainedControls, localFillerWordRemoval.

`DictationContextSupport` (`:647-668`): none, categoryOnlyOnDevice, fullOnDevice, fullLocalServer, remoteCategoryWithOptionalText.

`ParakeetModelChoice` and `SupportedLanguage`: sections 2.1 and 3.

### 1.5 Hotkey canonicalization (pure logic, port with tests)

`canonicalizedHotkeyBinding` (`:2429-2455`): (a) Caps Lock key/modifier -> clear binding (keyCode nil, modifiers 0, displayName ""); (b) fn key codes 63/179 as keyCode -> move to the `function` modifier and clear keyCode; (c) normalize modifiers (a side-specific flag implies its generic flag) and regenerate `displayName`; empty binding -> displayName "". Display name format (`:2290-2334`): `fn`, then Control, Option, Shift, Command; side-aware as `L<sym>`/`R<sym>`; symbols `⌃ ⌥ ⇧ ⌘`; then key name. Key-name table `:2263-2287` (macOS virtual key codes; Windows needs its own VK table, display e.g. "Ctrl", "Alt", "Shift", "Win"). Matching semantics (`keyedModifiersMatch`, `modifierOnlyModifiersMatch`, `groupMatch`, `:2407-2503`): for keyed shortcuts extra modifiers are allowed; for modifier-only shortcuts match must be exact; left/right-specific targets require that side.

### 1.6 Input-source mapping sanitization

`sanitizedMappings` (`:1257-1282`): decode leniently; drop entries whose engine/language/model raw value no longer exists, drop `assemblyAI` entries, drop `parakeet` entries without a model; if model does not support the language, language -> `en`. `add/update` helpers (`:2085-2155`) enforce: unique `inputSourceID`; apple engine clears model; parakeet fills model from current and coerces language; assemblyAI -> parakeet. **MAC-ONLY concept** (macOS input sources). Windows equivalent is keyboard layout (HKL/LCID) -> language profile; low priority; since there is one engine and the model's language set is fixed, the mapping may reduce to "layout -> expected language". If skipped, still reserve the keys.

### 1.7 Notifications emitted on setting change

`overlayPreferencesChanged` (showTextPreview, overlaySize, overlayThemeMode) and `appAppearanceModeChanged` (appAppearanceMode) (`:2565-2570`). Windows equivalent: an in-process event/observable the overlay and tray subscribe to.

---------------------------------------------------------------------------

## 2. Model management

### 2.1 Speech model catalog (Mac) - `ParakeetModelChoice` (`Settings.swift:255-633`)

Stored raw values (ten): `multilingual, multilingualUltra, englishOnly, compactEnglish, parakeetEou320, nemotron560, nemotron1120, nemotron2240, senseVoice, nemotronMultilingual`. The three English Nemotron raws are presets of one catalog model (`catalogChoice` = `nemotron560`). Menu order (`catalogChoices`, `:313-316`): multilingual, englishOnly, compactEnglish, parakeetEou320, senseVoice, nemotronMultilingual, multilingualUltra, nemotron560.

| Raw | Display name | Underlying model name | Folder under Models/ | Download size text | Languages | Streaming |
|---|---|---|---|---|---|---|
| `multilingual` | Parakeet v3 | NVIDIA Parakeet TDT 0.6B v3 | `parakeet-tdt-0.6b-v3-coreml` | ~500 MB | 25 European | no |
| `multilingualUltra` | Parakeet Ultra | moondream Parakeet Ultra | `parakeet-ultra-coreml` | ~630 MB | 25 European | no |
| `englishOnly` | **Parakeet v2** | **NVIDIA Parakeet TDT 0.6B v2** | `parakeet-tdt-0.6b-v2-coreml` (legacy) / `Installed/parakeet-tdt-0.6b-v2-coreml/revisions/<sha>` (current) | ~500 MB | English only | no |
| `compactEnglish` | Parakeet 110M | NVIDIA Parakeet TDT-CTC 110M | `parakeet-tdt-ctc-110m` | ~220 MB | English | no |
| `parakeetEou320` | Parakeet EOU 120M | NVIDIA Parakeet Realtime EOU 120M | `parakeet-eou-streaming/320ms` | ~200 MB | English | yes, supports end-of-utterance |
| `nemotron560/1120/2240` | Nemotron EN 0.6B | NVIDIA Nemotron Speech Streaming EN 0.6B | `nemotron-streaming/{560,1120,2240}ms` | ~1 GB | English | yes. Preset titles: "Fast preview (560 ms)", "Balanced (1120 ms)", "Higher throughput (2240 ms)" |
| `senseVoice` | SenseVoice Small | FunAudioLLM SenseVoiceSmall | `sensevoice-small` | ~225 MB with Neural Engine else ~900 MB | Chinese + English | no |
| `nemotronMultilingual` | Nemotron 3.5 0.6B | NVIDIA Nemotron 3.5 ASR Streaming 0.6B | `nemotron-multilingual/multilingual/1120ms` | ~650 MB | 26 presets | yes; requires Apple Neural Engine |

Detail/caption strings (`Settings.swift:280-299`), recommendation titles (`:372-383`), language summaries (`:485-493`), per-model language footers (`:515-527`) and model footer (`:529-531`: `Download size is not a memory estimate. The language and model you select are kept until you change them.`) are all in the Swift file; the ones that matter for Windows (Parakeet v2):
- detail: `Accuracy-focused English dictation. Uses Parakeet's English-only v2 checkpoint.`
- recommendation title: `English accuracy`; language summary: `English only`; fixed language label: `English`; footer: `This model transcribes English. Choose another model for other languages.`

Windows scope: single model, so the picker, streaming presets, EOU and language support notice logic can be dropped; if a model picker is kept, show the single row `Parakeet v2 — English accuracy · <size>`.

Language-support notice strings (reusable if multi-model returns, `:422-430`): not supported -> `<Model> does not support <Language>. Choose another model or expected language. Apple Speech may offer additional languages on this Mac.` ; limited coverage (Nemotron multilingual for el, lt, lv, sl) -> `<Language> has limited coverage in Nemotron and may require model adaptation. Parakeet v3 is the recommended choice for everyday dictation in this language.`

Recommendation logic (`:324-339`): language en -> [englishOnly, compactEnglish, parakeetEou320, multilingual]; zh -> [senseVoice, nemotronMultilingual]; no -> [nemotronMultilingual]; else [multilingual, nemotronMultilingual]; each filtered by hardware availability, language support, and not limited coverage. Menu sections `Recommended for <Language>` and `Alternatives`; item text `<name> — <purpose> · <size>`; limited coverage purpose = `Limited coverage for <Language>` (`SpeechModelMenu.swift:100-140`).

### 2.2 The pinned Parakeet v2 bundle (Mac artifact; reference only)

Manifest `ModelManifests/parakeet-v2.json` (bundled resource; `RemyModelManifest.loadBundledV2`, `RemyModelManifest.swift:44-58`):
- `schema_version` 1; repo `FluidInference/parakeet-tdt-0.6b-v2-coreml`; revision (git SHA) `ee09c569f73759e6d44c9bd16766f477b2b36d39`; license `CC-BY-4.0`; base model `nvidia/parakeet-tdt-0.6b-v2`; FluidAudio version `0.17.5`, revision `0b1f46289fe27d95b5e66ad8be46e64f5ee02ae7`; total bytes `464,414,912`; 22 files; app-relative dir `Models/parakeet-tdt-0.6b-v2-coreml`.
- URL for each file is forced to be exactly `https://huggingface.co/<repo>/resolve/<revision>/<path>`.
- Files (path, bytes, SHA-256): the four Core ML packages Decoder/Encoder/JointDecision/Preprocessor `.mlmodelc/` each with `analytics/coremldata.bin`, `coremldata.bin`, `metadata.json`, `model.mil`, `weights/weight.bin`, plus `README.md` and `parakeet_vocab.json`:
  - `Encoder.mlmodelc/weights/weight.bin` 445,187,200 `4adc7ad44f9d05e1bffeb2b06d3bb02861a5c7602dff63a6b494aed3bf8a6c3e`
  - `Decoder.mlmodelc/weights/weight.bin` 14,429,952 `27d26890221d82322c1092fd99d7b40578e435d5cf4b83c887c42603caf97aba`
  - `JointDecision.mlmodelc/weights/weight.bin` 3,453,388 `ca22a65903a05e64137677da608077578a8606090a598abf4875fa6199aaa19d`
  - `Preprocessor.mlmodelc/weights/weight.bin` 298,880 `a5f7df6c7f47147ae9486fe18cc7792f9a44d093ec3c6a11e91ef2dc363c48dc`
  - `Encoder.mlmodelc/model.mil` 959,769 `ed7b19156ca29fa7dfd6891deb9fda4b0e8893f68597c985d135736546a43808`
  - `parakeet_vocab.json` 18,762 `57019fe3c745772ca83a1b048a4bb951cd51329504ea33d4d83316b96e279a97`
  - `README.md` 1,665 `491eac9a160dc7b0f7ce1210dea4fff6df0222124e93ea83ae2cd72827b4ce02`
  - (remaining 15 small files are in the JSON file; read `ModelManifests/parakeet-v2.json` directly for the complete table)
- Documented licensing (`docs/engineering/model-release-policy.md`): NVIDIA Parakeet TDT 0.6B v2, Core ML conversion by FluidInference, CC-BY-4.0; preserve model card + license URL; the About page names the model and creators (`AboutView.swift:75`: "NVIDIA Parakeet TDT 0.6B v2, converted to CoreML by FluidInference. Remy downloads the pinned conversion separately; the complete notices include its model card and license link.").
- Other speech models and helpers (v3, Ultra, 110M, EOU, Nemotron, SenseVoice, Silero VAD) are NOT pinned: FluidAudio downloads `main` of its registry repos (`model-release-policy.md`). Not for Windows.

**Windows target (decided by caller): sherpa-onnx Parakeet TDT 0.6B v2 int8 ONNX bundle.** Windows must author its own manifest with the same *shape*: `schema_version`, repo/URL base, immutable revision (git SHA, not `main`), license `CC-BY-4.0`, base model `nvidia/parakeet-tdt-0.6b-v2`, per-file `path`, `size`, `sha256`, `url`, and `total_bytes` equal to the sum of file sizes. Expected files for the sherpa-onnx transducer int8 export: `encoder.int8.onnx`, `decoder.int8.onnx`, `joiner.int8.onnx`, `tokens.txt` (verify against the actual pinned release; compute sizes and SHA-256 yourself from the pinned revision and commit them). Preserve the attribution text above and the CC-BY-4.0 notice in the About page / THIRD_PARTY notices.

### 2.3 Manifest validation rules (port as-is) - `RemyModelManifest.validate` (`:70-100`)

Reject unless: `schemaVersion == 1`; license non-empty; at least one file; revision is 40 lowercase hex; repo is exactly `owner/name` with each part from `[A-Za-z0-9._-]` and not `.`/`..`; every `path` is a `/`-separated list of safe components (same character set, no empty, `.`, `..`), unique, `size >= 0`, `sha256` is 64 lowercase hex, `url` equals exactly `https://huggingface.co/<repo>/resolve/<revision>/<path>`; sum of sizes (overflow checked) equals `totalBytes`; no file path is also an ancestor directory of another. Manifest JSON max 2 MiB (`:37`). Manifest `digest` = SHA-256 of the canonical (sorted-keys) JSON re-encoding of the validated struct (`:25-27, 62-66`); the activation record stores this digest.

### 2.4 Store layout, staging, verification, activation - `RemyModelStore` (`RemyModelStore.swift`)

Layout under `<app dir>/Models/`:
- `Installed/<modelID>/revisions/<revision>/` - the verified bundle (modelID = last repo path component, e.g. `parakeet-tdt-0.6b-v2-coreml`).
- `Installed/<modelID>/revisions/<revision>.json` - registered manifest copy (written atomically after publish).
- `Installed/<modelID>/active.json` - JSON `{current: {modelID, revision, manifestDigest}?, previous: {...}?}`, written atomically.
- `.staging/<operation UUID>/` - in-flight download area; deleted on completion or failure.
- Legacy (pre-store) bundle location: `Models/<modelID>/` directly (`parakeet-tdt-0.6b-v2-coreml`). `RemyParakeetAssets.legacyV2IsPresent` checks for it (`RemyParakeetAssets.swift`).

Flow (`install`, `:89-127`):
1. Reject if another operation is running (`busy`). Validate manifest.
2. If the revision directory already exists: verify full inventory, (re)register, return (no download).
3. Create `Models/`. Check free space: needs `totalBytes + 64 MiB` free on the volume else `insufficientSpace`.
4. Create staging dir. For each file in manifest order: create parent dirs, download to `staging/<path>`, verify size + SHA-256, add to completed bytes.
5. Verify the whole staging directory: the set of files and directories must equal exactly the manifest (no extras, no symlinks, only regular files/dirs), then each file's size and SHA-256 (streamed in 1 MiB chunks; any cancellation check between chunks; a file larger than declared aborts).
6. Publish: exclusive atomic rename `staging -> revisions/<revision>` (fails if destination exists), then write `<revision>.json`. Install NEVER activates.
7. Activate (`:175-187`): `verify` (offline full re-hash) then write `active.json` with `previous = old current`, `current = new`. Refused while any lease is held (`leasedRevision`).
8. Inference acquires a *lease* (`acquireInstalled`, `:191-199`): requires `current == this manifest`, re-verifies the full bundle offline, returns a lease; the lease blocks activate/rollback/deactivate/remove until released (on engine unload/shutdown, not on cancel).
9. Other operations: `adoptLegacy` (copy a verified legacy dir into the store; never moves/deletes the original), `rollback` (swap current/previous after verifying), `deactivate` (current -> nil, previous = old current), `remove` (refuses active revision and leased; clears `previous` if it points at it; deletes revision directory and its `.json`; recovers orphaned/corrupt inactive revisions).
- Download transport (`RemyModelDownloadTransport.swift`): HTTPS only (scheme https, host present, no embedded credentials); follow redirects only to HTTPS and strip `Authorization`/`Cookie` on redirect; ephemeral session (no cache/cookies/credentials); request timeout 60 s, whole-resource timeout 3,600 s; stream to a freshly created file (exclusive create, mode 0600, no symlink follow); HTTP status must be 2xx; `Content-Length` must not exceed the manifest size; bytes written must not exceed manifest size and must equal it at completion else `incomplete`; cancellation closes the file and throws cancelled. Windows: `HttpClient` with `ResponseHeadersRead`, same constraints, `FileMode.CreateNew`.
- Path confinement (`RemyStoragePaths.requireOwnedModelPath`, `RemyStoragePaths.swift:84-103`): any path used for model IO must be inside `Models/`, contain no `.`/`..` components and no symlinks/reparse points in any ancestor; outside -> error `The model path is outside Remy's model store or uses a symbolic link. Choose a Remy-owned model directory.` Model directory names: relative, `/`-separated, no empty/`.`/`..`/backslash components (precondition).

Errors and exact user-facing text (`RemyModelManifest.swift:117-139`):
- notActive: `This model has not been activated. Verify an existing bundle or explicitly install it in Speech Model.`
- notInstalled: `Verified model assets are not installed.`
- busy: `Another model operation is running.`
- leasedRevision: `Unload the model before switching or removing its assets.`
- activeRevision: `Deactivate the model before removing its assets.`
- noRollback: `No previous verified model revision is available.`
- invalidInventory / integrityMismatch: `Model assets failed integrity verification. Use the explicit model installation controls to repair them.`
- invalidActivation: `The model activation record is invalid. Restore or reinstall a verified revision.`
- invalidLegacyLocation: `Only Remy-owned legacy model bundles can be verified.`
- invalidManifest: `The model manifest is invalid or is not pinned to the expected revision.`
- insufficientSpace: `There is not enough free space to stage and verify this model.`

Cheap offline "is installed" check (`activeDirectory(for:)`, `:54-74`): checks `active.json` and the registered manifest digest, not file hashes. Hashes are verified only at lease acquisition (prepare). UI "downloaded" state therefore means "active and registered", not "hash-verified".

Windows implementation notes: replace `renamex_np(RENAME_EXCL)` with `Directory.Move` after checking the destination does not exist (same volume as the staging dir, so staging must be under `Models\.staging`); replace symlink checks with reparse-point checks (`FileAttributes.ReparsePoint`); replace the free-space API with `DriveInfo.AvailableFreeSpace`.

### 2.5 Download progress and UI state (Speech Model page)

`ModelReadiness` (`ModelReadiness.swift`) - the shared state set used for speech, cleanup and optional components. Title strings are verbatim (including the ellipsis character `…`):

| State | Title | Detail | Working? | Symbol (SF) |
|---|---|---|---|---|
| `notDownloaded` | Not downloaded | | no | internaldrive |
| `downloaded` | Downloaded | | no | internaldrive |
| `available` | Available | | no | internaldrive |
| `configured` | Configured | | no | internaldrive |
| `needsSetup` | Not set up | | no | internaldrive |
| `downloading(Double)` | Downloading… | | yes; progress clamped 0..1, non-finite -> none | arrow.down.circle |
| `verifying` | Verifying… | | yes | checkmark.shield |
| `preparing` | Preparing for first use… | | yes | hourglass |
| `deleting` | Deleting… | | yes | trash.circle |
| `ready` | Ready | | no (only state with `isReady`) | checkmark.circle.fill |
| `unavailable(reason)` | Unavailable | reason | no | exclamationmark.circle |
| `failed(reason)` | Needs attention | reason | no | exclamationmark.circle |

Rendering (`DSModelReadiness.swift`): while `isWorking` show a loading message with the title; else a status pill (green with success colors only for `ready`, neutral grey otherwise). Row layout: label `Status` + pill + optional secondary button (disabled while working); below it a linear progress bar when `progress != nil` (accessibility label `<label> download`); below that an error-toned message with `detail`.

Speech-model readiness derivation (`ModelsView.swift:486-492`), in priority order: deleting -> `deleting`; engine downloading -> `downloading(downloadProgress)`; preparing -> `preparing`; engine ready -> `ready`; model on disk -> `downloaded`; else `notDownloaded`. Note "Downloaded" and "Ready" are separate facts: installed vs successfully loaded.

Action button (`:494-512`): `notDownloaded` -> `Verify Existing Model` if a legacy (pre-store) bundle exists and is not yet active, else `Download Model`; `downloaded` -> **`Prepare Model`** (loads the model into memory; calls `prepareActiveEngine`); other states -> no button. Whole row disabled unless app status is idle and not deleting.
- Clicking with the model on disk -> prepare (load). Clicking otherwise -> `downloadModel()`; on success, re-apply selection (prepare).
- Errors from either shown below the row in an error-toned message (`modelActionError ?? enginePreparationError`).

Download progress details: for the Parakeet v2 store path, progress = `completedBytes / totalBytes` (real byte progress through the callback, updated per chunk and when each file verifies; `RemyModelStore.swift:115-121`; `TranscriptionEngine.swift:1207-1215`). For other (SDK) models progress is simulated: +1% every 600 ms up to 90% (`TranscriptionEngine.swift:848-857`). After the files install the model is loaded; at completion progress = 1.0 and the engine is ready. On failure/cancel progress resets to 0.0 and `isReady = false`. Concurrent downloads are rejected (guard on `isDownloading`).

Windows: use real byte progress (the store already provides it); do not simulate.

"Prepare Model" step: `prepare()` (`TranscriptionEngine.swift:1008+`) never downloads. If the model is not on disk it sets not-ready and returns. For v2: acquires a verified lease (full SHA-256 re-verification of all files), loads the model into the recognizer, sets `isReady`. `AppState.performPrepareActiveEngine` (`AppState.swift:1329-1380`): skip when recording/processing; set `isPreparingEngine = true` and clear `enginePreparationError`; call prepare; on failure wait 1 second then retry once; on second failure set `enginePreparationError = error text`. Prepare is skipped entirely when `prewarmModel` is false (startup toggle off) and happens lazily on first dictation.

Speech detection helper (VAD) row (`ModelsView.swift:514-535`): only for non-streaming models when the model is downloaded. Label `Speech detection`; installed caption `Helps recognize quiet speech and split recordings at pauses.`; not installed caption `Optional download for quiet speech and pause detection. Dictation works without it.`; installed shows `Installed`; otherwise button `Download Speech Detection` (disabled while busy). It is the Silero VAD model stored at `Models/<vad repo folder>/<silero vad file>` (FluidAudio `Repo.vad`, `BatchSpeechDetection.swift:8-11`). Windows: if the Windows pipeline uses Silero VAD via sherpa-onnx, ship/pin it as a second manifest and mirror this row; if VAD is bundled, drop the row.

Deletion (`ModelsView.swift:158-224, 241-260`; `TranscriptionEngine.swift:947-985`):
- Button shown only when downloaded. Label `Remove Verified Copy…` for Parakeet v2, `Delete Preset…` for English Nemotron, otherwise `Delete Model…`. Caption beside it: v2: `Remove the verified copy. Original model files are kept.`; others `Remove the downloaded model files from this Mac.` Button is destructive style; disabled while busy.
- Confirmation alert title `Delete <display name>?`; buttons `Delete` (destructive) and `Cancel`. Message for v2: `This removes Remy’s verified model copy. Your original downloaded files stay on this Mac and can be verified again.`; others: `This will remove the <name> speech model (<size>). You can download it again later.` For a Windows single-model build use: `This will remove the Parakeet v2 speech model (<size>). You can download it again later.`
- Guard: if the app is not idle, preparing, downloading, or the selection changed -> `The selected model is busy or has changed. Review it and try deleting again.`
- Procedure (v2): unload engine resources (release lease, cleanup recognizer, `isReady=false`), `deactivate` (clear active pointer), `remove` revision dir + registered manifest; mark not downloaded. Deletion is refused while a lease is held and after cancelling recording. Status shows `Deleting…` while running.
- Page text: subtitle `Speech recognition runs on your Mac. Transcript cleanup uses the method you choose separately.`; section overline `On-Device Speech`; info rows `Type` (`On-device speech-to-text`, or `On-device true streaming speech-to-text` for streaming), `Underlying model`, `Languages`, `Download size`, `Also installed` (another installed model's name, if any; excludes unavailable-on-this-Mac models). Hint shown when a legacy bundle needs adoption: `Verify and copy your existing model into Remy’s versioned store. The original files are kept, and no model download is needed.` Windows: legacy adoption does not apply; omit.

Sidebar footer status (`SidebarView.swift:67-80`), reusable for a Windows status strip: idle -> `Finishing cancelled cleanup…` (cleanup draining), else `Setup needed` (any non-optional attention issue), else `Preparing speech model…` (preparing), else `Ready to dictate` (engine ready) or `Loads on first dictation`; recording -> `Listening…`; processing -> `Transcribing…`; error -> `Something went wrong`. Version line `Version <CFBundleShortVersionString>`.

### 2.6 Cleanup (post-processing) models

Only one downloadable cleanup model exists: **S1-mini by Superwhisper** (`S1MiniPostProcessingService.swift:112-128`). The Apple Intelligence model is OS-managed (MAC-ONLY); Ollama/OpenRouter/OpenAI-compatible/Saved Profile are external endpoints (no download, user supplies model ids).

S1-mini spec: display name `S1-mini by Superwhisper`; repo `superwhisper/s1-mini-GGUF`; revision `ee2c0f56e56345f475749a44ff2893e21c3cb292`; file `s1-mini-q4_k_m.gguf`; size `484,219,808` bytes; SHA-256 `3b41ebe2502cbd03e811d5d16b022f5ab551eda58d62597d152f89535003c634`; model URL `https://huggingface.co/superwhisper/s1-mini-GGUF/resolve/ee2c0f56e56345f475749a44ff2893e21c3cb292/s1-mini-q4_k_m.gguf?download=true`; LICENSE URL `https://huggingface.co/superwhisper/s1-mini-GGUF/resolve/ee2c0f56e56345f475749a44ff2893e21c3cb292/LICENSE`; Apache-2.0 plus the Superwhisper naming term (preserve S1-mini / Superwhisper capitalization and Qwen attribution; `docs/engineering/model-release-policy.md`). Max transcript tokens 1,000. Supports English cleanup only. Installed at `Models/S1-mini/s1-mini-q4_k_m.gguf` plus `Models/S1-mini/LICENSE` (`S1MiniModelManager.swift:252-254, 244-245`).

Flow (`S1MiniModelManager.swift`): stage `.<id>.gguf.part` and `.<id>.license.part` in the model dir; download model with URLSession download task; progress fraction = bytesWritten / pinned byte count (pinned size authoritative because the redirected server may report unknown length); progress during transfer maps to 0..0.98 (`installationFraction = 0.98`) and the last 2% is the verification phase (`S1MiniDownloadProgress`, `:75-97`); validate size then SHA-256 (1 MiB chunks); download LICENSE (must be non-empty); move both into place (replacing existing LICENSE); installed means model file has the exact expected size AND LICENSE is non-empty (`hasCompleteInstallation`). Delete removes both. Fully verified (hash) before first use (`validatedModelURL`).

UI text (`AIPostProcessingView.swift:933-963`): while downloading the readiness is `downloading(min(1, progress / 0.98))`; caption under the bar while transferring: `<pct>% · <downloadedMiB> of <totalMiB> MB` (totalMiB = ceil(484,219,808 / 1,048,576) = 462); when progress >= 0.98: `Verifying model integrity…`. Error strings: `An S1-mini model operation is already in progress.`, `The S1-mini download server returned an invalid response.`, `The S1-mini download had an unexpected size (<actual> of <expected> bytes).`, `The S1-mini download failed its integrity check.`, `The S1-mini license could not be downloaded.`, `The S1-mini model changed while its integrity was being checked.` Caption on the page: `S1-mini uses only its fixed, trained controls—not an arbitrary prompt or custom vocabulary. Local filler-word removal still runs first.` Runtime uses llama.cpp via Metal (**MAC-ONLY** runtime; CPU thread count = max(1, min(8, physical cores, active processors)), `S1MiniCPUConfiguration.swift`). Windows suggestion: either drop S1-mini or run the same GGUF via LLamaSharp/llama.cpp CPU; reuse the pinned URL/size/SHA above.

Cleanup readiness state machine (`AppState.cleanupReadiness`, `AppState.swift:883-929`) with exact strings:
- `profile`: no selected profile -> profile load error as `failed(text)` else `needsSetup`; profile route policy `strictLocalOnly` -> `unavailable("This profile's execution location has not been verified.")`; else `configured`.
- `s1Mini`: language != en -> `unavailable("S1-mini supports English cleanup.")`; then downloading / verifying / deleting states from the manager.
- `fluidAudioVocabulary`: engine != parakeet -> `unavailable("Requires a supported Parakeet speech model.")`; else the engine's vocabulary readiness.
- `appleIntelligence` (**MAC-ONLY**): < macOS 26 -> `unavailable("Requires macOS 26 or later.")`; device not eligible `Apple Intelligence is unavailable on this Mac.`; not enabled `Enable Apple Intelligence in System Settings.`; model not ready -> `preparing`; other `Apple Intelligence is temporarily unavailable.`; available -> `available`, or `ready` once prepared.
- `openRouter` with no key from field or env var -> `needsSetup`.
- Any mode: if preparing -> `preparing`; if prepared and mode not in {ollama, openRouter, openAICompatible} -> `ready`; matching prior failure -> `failed(message)`.
- Fallthrough: s1Mini -> `downloaded` or `notDownloaded`; ollama/openRouter/openAICompatible -> `needsSetup` when model id is blank else `configured`; everything else `available`.
- Cleanup "Prepare" is possible only when readiness is not in {notDownloaded, needsSetup, unavailable, ready, downloading, verifying, preparing, deleting}, status is idle, and mode != profile (`canPrepareCleanupEngine`, `:942-951`).
Cleanup prep is retriggered (task keyed by `cleanupPreparationKey`, `AppState.swift:850-880`) whenever any of engine, model, mode, language, prewarm flag, install state, prompt, vocabulary, provider URL/model, credentials revision, or context changes.

---------------------------------------------------------------------------

## 3. Languages (`SupportedLanguage.swift`)

Enum `SupportedLanguage` (stored as the ISO-style raw code). 27 languages, in this exact order, with `displayName` (English) / `nativeName` / flag:

| Raw | Display name | Native name |
|---|---|---|
| en | English | English |
| de | German | Deutsch |
| nl | Dutch | Nederlands |
| sv | Swedish | Svenska |
| da | Danish | Dansk |
| no | Norwegian | Norsk |
| es | Spanish | Español |
| fr | French | Français |
| it | Italian | Italiano |
| pt | Portuguese | Português |
| ro | Romanian | Română |
| pl | Polish | Polski |
| cs | Czech | Čeština |
| sk | Slovak | Slovenčina |
| sl | Slovenian | Slovenščina |
| hr | Croatian | Hrvatski |
| bg | Bulgarian | Български |
| uk | Ukrainian | Українська |
| ru | Russian | Русский |
| lv | Latvian | Latviešu |
| lt | Lithuanian | Lietuvių |
| et | Estonian | Eesti |
| hu | Hungarian | Magyar |
| fi | Finnish | Suomi |
| el | Greek | Ελληνικά |
| mt | Maltese | Malti |
| zh | **Chinese (Simplified)** | 简体中文 |

`displayWithFlag` = `"<flag emoji> <displayName>"` used in dropdowns (English -> GB flag, others -> country flag; Chinese -> CN; Greek GR; etc. at `SupportedLanguage.swift:110-138`). Windows note: WPF/Segoe UI does not render flag emoji on Windows; show plain `displayName` (optionally with ISO code).

How language affects engines:
- Parakeet path: `selectedLanguage` is an *expected language hint*, not a hard filter. Per-model supported subsets (`transcriptionLanguages`, `Settings.swift:395-415`): v3 and Ultra = en, de, nl, sv, da, es, fr, it, pt, ro, pl, cs, sk, sl, hr, bg, uk, ru, lv, lt, et, hu, fi, el, mt (25; no Norwegian, no Chinese). Nemotron multilingual = en, de, nl, sv, da, no, es, fr, it, pt, ro, pl, cs, sk, sl, hr, bg, uk, ru, lv, lt, et, hu, fi, el, zh (26). SenseVoice = en, zh. All English-only models = en. The picker offers only languages with at least one compatible model on the current hardware (`catalogLanguages`); a stored language not offered is kept visible (`catalogLanguageOptions(preserving:)`) with a notice rather than silently rewritten. Model change keeps the expected language; recording start validates the pair (`testUnsupportedStoredLanguageFailsBeforeModelOrMicrophoneStartup`).
- Picker visibility: `selectableLanguages` is non-nil only for v3/Ultra/Nemotron-multilingual (the model's `transcriptionLanguages`); English-only and SenseVoice hide the picker and show `fixedLanguageLabel` (`English` or `Chinese + English (auto-detected)`).
- Nemotron language hint code: `zh` -> `zh-CN`, `en` -> `en-US`, others raw code (`nemotronLanguageCode`, `:151-158`).
- Chinese output uses Simplified characters (SenseVoice and Apple Speech Chinese locale mapped to Simplified).
- Cleanup: S1-mini works only when the active language is English (`S1-mini supports English cleanup.`); CJK handling in filler removal and text insertion keys off the text itself (see CJK tests).
- Apple Speech uses `appleSpeechLanguage` against OS-reported supported/installed locales; **MAC-ONLY**.

Windows (Parakeet v2 = English only): ship the full `SupportedLanguage` enum in the settings schema for forward compatibility, but the picker is hidden and shows fixed label `English`, `selectedLanguage` is forced/treated as `en`. Keep `languageSupportNotice` logic so that if a stored language is not `en` the page shows: `Parakeet v2 does not support <Language>. Choose another model or expected language.` (drop the Apple Speech sentence on Windows).

---------------------------------------------------------------------------

## 4. Hardware checks (`Hardware.swift`)

Exactly one hardware check exists:
- `Hardware.isArm64Process` = compile-time `arch(arm64)` of the running slice. `Hardware.canUseAppleNeuralEngine` = same value (`Hardware.swift:16-41`). Deliberately compile-time: an x86_64 slice under Rosetta on Apple Silicon is "false" because FluidAudio refuses to load ANE models there.
- Gates: (1) `ParakeetModelChoice.requiresAppleNeuralEngine` is true only for `nemotronMultilingual`; unavailable models are hidden from the model menu and from language lists, and a stored unavailable choice is coerced in memory (not persisted) to `availableFallback(for: language)` (first runnable model supporting the language, else first runnable, else `multilingual`) (`Settings.swift:585-633, 1995-2006`). (2) SenseVoice size text and encoder precision: with ANE `~225 MB` (int8) else `~900 MB` (fp32) (`:507-509`). (3) UI uses the value in model/language pickers (`SpeechModelMenu.swift`, `SettingsView.swift:107-109`).
- Other OS/hardware gates are not in `Hardware.swift`: Apple Speech requires macOS 26 (title `Apple Speech (Requires macOS 26)`, `ModelsView.swift:262-267`), Apple Intelligence requires macOS 26 + eligible device, release binary scope is Apple silicon (docs). Window sizing constants live in `MainWindowSizing`.
- `PerfTrace` records `hw.model` string as a metadata label (`PerfTrace.swift:99-110`).

Windows: no ANE gate. Candidate analogs: (a) 64-bit only (`Environment.Is64BitProcess`), (b) AVX2 / CPU feature check if the onnxruntime build requires it, (c) minimum free disk before download (already in the store: model bytes + 64 MiB), (d) optional DirectML/GPU availability if used. Surface failure as readiness `unavailable("<reason>")`.

---------------------------------------------------------------------------

## 5. Startup and lifecycle

### 5.1 Launch sequence (in order)

1. `RemyEntryPoint.main` (`App.swift:38-63`), before any UI framework object exists:
   a. If running under the unit-test host: require bundle id `io.github.brysonjl.remy.testhost` else crash with a message telling to run via `scripts/remy-build.sh test`.
   b. `RemyMigrationBootstrap.permitsExclusiveLaunch()` - if the *other* identity (Dev vs Release) is running, show alert `Quit <Remy Dev|Remy> first` / `Run one Remy app at a time so the dictation shortcut and microphone have a single owner. Your data stays separate.` button `Quit`, then exit (`RemyMigrationBootstrap.swift:26-39`).
   c. `RemyMigrationBootstrap.runIfNeeded()` - Dev->Release data import (5.5). Returns false to abort launch.
   d. `RemyStartup.configurationIssue` then `RemyHistoryBootstrap.captureLegacy` (legacy `transcriptHistory` capture from defaults); any non-nil issue -> blocking alert and exit (1.2).
   e. Start the UI app (`RemyApp.main()`).
2. `AppDelegate.init` creates `AppState` (and thereby `Settings.shared`, which runs all legacy migrations and, last, observes login-item status). If the import happened and credentials still need review, `selectedPage` is set to Transcript Cleanup (`AppDelegate.swift:94-101`).
3. `applicationDidFinishLaunching` (`:117-137`): configure speech SDK logging (warning level, no console mirror, unless Debug + env `REMY_KEEP_FLUID_DEBUG_LOGS=1`); skip everything under tests; **enforce single instance** (5.2); start perf trace `app.launch`; disable relaunch-on-login restoration; create the tray (menu bar) item; configure the main window after 0.1 s; register notification observers; apply appearance mode (activation policy); `appState.start()` (skipped in Debug screenshot mode via `--screenshot-mode` or `REMY_SCREENSHOT_MODE=1`).
4. `AppState.start()` (`AppState.swift:619-631`): idempotent (`hasStarted`); observe system sleep/wake; reload the recovery store (error -> shown as recovery error); refresh S1-mini install state; spawn `runStartupSequence`.
5. `runStartupSequence` (`AppState.swift:775-860`), each step guarded by "not shutting down", traced as `app.startup` with sub-traces:
   1. Prepare history store (SQLite open/migrate, import legacy history). On failure fall back to an in-memory history and suspend auto-paste (a failed open must never authorize later DB writes). Set `isPreparingHistory = false`.
   2. Pre-compile the paste script (`prewarmPasteScript`) - must finish before the dictation hotkey is enabled; failure is only a warning (retries on first use). **MAC-ONLY** (AppleScript); Windows: prewarm the SendInput/clipboard path if any.
   3. Permissions refresh (`app.permissionCheck`), then accessibility integration (prompt if needed). Windows analog: microphone privacy setting check; no Accessibility prompt.
   4. Refresh Apple Speech asset state (`app.appleSpeechAssetRefresh`). **MAC-ONLY**.
   5. If input-source auto-switch is on and an input source is known: apply its profile *without* prewarming (`app.inputSourceApply`).
   6. `prepareSelectedEngines(prewarmModel: settings.prewarmEnginesAtStartup)` - resolves the active engine config (rechecks models on disk; auto-default to Parakeet if the user never chose an engine and a model exists; clears the Apple migration flag), then if prewarm is on loads the speech model, then the cleanup model.
   7. Start the input-source monitor; reconcile once if the source changed during model load.

Warm model loading specifics: prewarm applies to both speech model and cleanup model; speech load failure retries once after 1 s (2.5); the cleanup engine is also prewarmed at the *start of each recording* (`prewarmCleanupForRecording`, `AppState.swift:2097, 2116, 3093`) and after any relevant setting change (2.6). With prewarm off, nothing loads at launch and the sidebar shows `Loads on first dictation`.

### 5.2 Single instance, windowing, menu-bar-only vs Dock

- Single instance (`AppDelegate.swift:245-262`): among running processes with the same bundle id, the lowest PID is the keeper; any other instance activates the keeper and terminates itself. Plus the cross-identity (Dev/Release) exclusion in 5.1. Windows: named `Mutex` (`Local\Remy` or per-identity); second instance signals the first (named pipe/EventWaitHandle or `SetForegroundWindow`) to show its main window, then exits. Keep Dev and Release mutually exclusive with the same alert text (replace "Remy Dev" naming as appropriate).
- Appearance mode (`applyAppearanceMode`, `:309-316`): preferred policy from `appAppearanceMode` (`menuBarOnly` -> accessory/no Dock; `dockAndMenuBar` -> regular). The app is *forced regular* while the main window is visible or being shown (so it can take focus), and returns to accessory ~0.1 s after the main window closes (`windowWillClose`, `:426-431`) and whenever the app resigns active with the window hidden. Windows mapping: tray icon always present; `menuBarOnly` = no taskbar button when the main window is closed/hidden (`ShowInTaskbar=false` + hide window instead of closing); `dockAndMenuBar` = taskbar button always shown. Closing the main window never quits (`applicationShouldTerminateAfterLastWindowClosed -> false`, `:139-141`).
- Reopen (`:160-164`): activating the app with no visible window shows the main window. Windows: launching/clicking the tray icon or a second-instance signal shows it.
- Main window (`App.swift:10-34`, `AppDelegate.swift:278-305`): hidden-title-bar window group, default size `MainWindowSizing.defaultWidth x defaultHeight`, resizable between `MainWindowSizing` min/max, centered at first show, not movable by background (sidebar has an explicit drag area), menu command `Open Remy` and `Settings…` (Cmd+,) which selects the General page. Floating panels (overlay, quick vocabulary) are never treated as the main window. `showMainWindow` un-minimizes, activates and brings to front. A blocking attention issue (microphone/accessibility/speech setup) posts `requestShowMainWindow` once per issue (`presentBlockingAttentionIssueOnce`, `AppState.swift:569-573`).
- Tray menu (`AppDelegate.swift:187-241, 382-420`), items in order: `Open Remy`; `Copy Last Transcript` (disabled when no transcript); `Stop Dictation` (hidden unless `canStopDictation`); `Use Raw Now` (hidden unless `canUseRawCleanup`); `Cancel Dictation` (hidden unless `canCancelDictation`); `Add Custom Vocabulary` (hidden unless engine is AssemblyAI or the cleanup mode supports custom vocabulary); separator; `Microphone` submenu (first item = automatic title, checked when no explicit or missing device; separator; one item per input device, checked when selected; refreshed each time the menu opens); separator; `Quit` (Cmd+Q). Tooltip = `Remy`. Icon asset `MenuBarIcon`. Choosing a mic writes `selectedMicrophoneUID` (nil for automatic). Menus are dismissed when text is pasted (`dismissMenusForPaste`).
- Activation (`AppDelegate.swift:166-183`): on becoming active, refresh login-item status and re-check permissions (re-arms resolved blocking issues); on resigning active with window hidden, re-apply appearance mode. Windows: on `Activated`, re-check mic permission and login-item registration.

### 5.3 Launch at login

- Mac: `SMAppService.mainApp` register/unregister (`Settings.swift:2215-2252`). Toggle -> if enabled and status != enabled: register; if disabled and status is enabled or requiresApproval: unregister. Never during tests. Startup only *reads* status (`refreshLoginItemStatus`), never registers (so an existing choice is not silently rewritten).
- Error strings: register failure `Could not update Launch at Login. <system error>`; status requiresApproval `Allow Remy to launch at login in System Settings.`; any other non-enabled status while the setting is on `Launch at Login is not active. Turn it off and on to retry.`; cleared when disabled or enabled. Shown under the switch in error tone.
- Windows: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `Remy` = quoted exe path (+ ` --minimized` so launch goes to tray only, i.e. start with the main window hidden). Refresh by reading the key; error text `Could not update Launch at Login. <message>` on registry failure, and `Launch at Login is not active. Turn it off and on to retry.` if the stored value is missing/mismatched while the setting is on. For MSIX packaging use `StartupTask` instead (requires-approval maps to the "Allow..." message with "Settings > Apps > Startup").

### 5.4 Sleep / wake

(`AppState.swift:633-661`) Observes `willSleep`/`didWake` workspace notifications.
- Will sleep: set `isSystemSleeping`; invalidate `activationGeneration` (new UUID, so in-flight activations are discarded); cancel any Apple Speech install task; suspend the hotkey service (`suspendForSystemSleep`); reset hold-to-record key-down/pending-release flags; `cancelDictation(reason: .inputUnavailable)` (an active recording is cancelled and its audio is preserved per `preserveCancelledSessions`).
- Did wake: clear `isSystemSleeping`; resume hotkey service (`resumeAfterSystemWake`).
- Models are NOT unloaded on sleep. Tests: `RemyHotkeySleepTests`.
- Windows: `SystemEvents.PowerModeChanged` (Suspend/Resume) or `PowerSettingChanged`; also treat session lock/`SessionSwitch` as optional; on Suspend do the same sequence; on Resume re-register the low-level keyboard hook (hooks can die across sleep) and refresh the microphone device list.

### 5.5 Dev -> Release data import (MAC-ONLY, summarize)

`RemyMigrationBootstrap` (`RemyMigrationBootstrap.swift`), `RemyMigrationPreferences.swift`, `RemyLocalDataMigration.swift`: Release (bundle `io.github.brysonjl.remy`) offers a one-time import from Dev (`...remy.dev`) when the Release app-support directory does not exist and the Release preferences domain is pristine (only AppKit window keys allowed) and the Dev directory exists, Dev not running. Prompt: title `Bring your setup over from Remy Dev?`; body `Copy your settings, history, recovery recordings, downloaded models, and custom sounds into Remy.` + `Remy Dev stays intact as your fallback. API keys and macOS permissions do not transfer. You’ll enter your key in Remy and approve its microphone and Accessibility access. Launch at login starts off.`; buttons `Import from Remy Dev` / `Not Now` (Not Now exits). Progress alert `Bringing your setup to Remy` / `Copying and verifying your local data. Keep Remy Dev closed. Large downloaded models may take a few minutes.` Failure alert `Your Remy Dev setup is still safe` / `Remy could not finish the import. Your original data and keys have not been removed or replaced. Quit Remy Dev, check available disk space, then reopen Remy to retry or resume.` + `Existing Remy data is never overwritten. If this continues, keep using Remy Dev while the import is reviewed.`; Dev-running alert `Quit Remy Dev before importing` / `The import needs a stable copy of your data. Quit Remy Dev, then reopen Remy. Your setup stays in place.` button `Quit Remy`.
- Copies only allowlisted dirs `History, Recovery, CleanupProfiles, Models, Sounds` (limits: 200,000 files, 100 GiB), verifies a SQLite snapshot of history, writes `Migration/receipt.json` and a pending preferences snapshot; preferences are then applied atomically from an allowlist with per-key type/size validation (`RemyMigrationPreferences`: bool keys, string keys <= 1,024 B (models 512 B, URLs 2,048 B, prompts 65,536 B, mic uid 4,096 B), `soundEffectsVolume` in 0..1, `hotkeyKeyCode` <= 65,535, `customVocabulary` <= 10,000 x 4 KiB / 4 MiB, `fillerWordsToRemove` <= 1,000 x 256 B / 256 KiB, JSON blobs <= 256 KiB (history 64 MiB; app rules / input maps 1 MiB), whole plist <= 80 MiB). Not migrated: `launchAtLogin`, `shareDictationContextWithRemoteProviders`, AssemblyAI keys/settings, credentials, `legacyAppleSpeechMigrationPending`, `pendingVocabularyModeRestore`.
- Windows: not needed for v1 (no Dev/Release predecessor with data). If a Dev/Release split is kept, simplest equivalent is separate folders and no importer. Keep the receipt-style "resume if interrupted" idea only if an importer is built.

### 5.6 Quit / shutdown

`applicationShouldTerminate` (`AppDelegate.swift:143-158`): returns `terminateLater`, runs `appState.shutdown()` then unloads the S1-mini runtime (Metal buffers must be released before AppKit teardown) in a Task, then replies `terminate`. Re-entrancy guarded. `Quit` (tray, Cmd+Q) -> terminate. `AppState.shutdown` (`AppState.swift:663-770`) order: mark `isShuttingDown` (idempotent; traced `app.shutdown`); remove sleep/wake observers; revoke the active session and delivery authorization; invalidate context capture; cancel startup task and Apple install tasks; cancel input-source apply, recording start, processing and every cleanup attempt; stop audio-level polling, input-source monitor, hotkey monitoring, permission polling; join processing + cleanup attempts; cancel and *join* any in-flight cleanup/speech model prewarm (a prewarm is a blocking native call that ignores cancellation, wait for it); discard prepared Apple session; cancel the active engine(s); **flush recovery capture** (drain accepted recovery writes, close the CAF, record metadata; on failure message `Recovery details could not be saved before quitting. Existing audio was kept.`); clear session state; unload speech model resources (release lease); `flushTranscriptHistory`; restore microphone volume and system mute state; hide the overlay. Windows: handle `Application.Exit`/`SessionEnding` with the same ordering; restore mic boost/mute even on crash paths if possible (consider a startup "restore stale mute/volume" check).

---------------------------------------------------------------------------

## 6. Software updater (`SoftwareUpdater.swift`)

- Reality: **updates are disabled in this fork.** `SoftwareUpdater.isEnabled = false`; `unavailableReason = "In-app update checks are disabled. Install development builds manually."` No Sparkle controller is instantiated; Info.plist has `SUEnableAutomaticChecks=false`, `SUAutomaticallyUpdate=false` and no feed URL (`Info.plist:11-14`). The Sparkle package is retained only to reproduce the inherited build (`SoftwareUpdater.swift:8-9`). The 0.1.0 DMG is "manual updates" (`CHANGELOG.md`, `docs/release-readiness.md`).
- UI surface: the About page shows `Version <short> (<build>)` (`AboutView.swift:28`), a card caption with `SoftwareUpdater.unavailableReason` (`:45`), links to third-party notices (bundled `OpenSourceNotices` folder and `https://github.com/brysonjl/remy/blob/main/THIRD_PARTY_NOTICES.md`), publisher link `https://pixelforty.com`, and the Sparkle credit: `Inherited macOS update framework dependency. Update checks are disabled in Remy.` There is no "Check for Updates" button, no preference, no tray item.
- **MAC-ONLY** (Sparkle, appcast, EdDSA, DMG/zip as Sparkle artifact per `app/CLAUDE.md`). Windows suggestion: v1 = match the Mac and drop updating (About page shows version and the text `Install new builds manually.`, with a link to `https://github.com/brysonjl/remy/releases`). Optional later: a `Check for Updates` button on About that calls `GET https://api.github.com/repos/brysonjl/remy/releases/latest`, compares `tag_name` (strip leading `v`) with the assembly version, and opens the release page in the browser (no silent download/install; no automatic checks by default, consistent with the privacy posture of no unsolicited network calls).

---------------------------------------------------------------------------

## 7. Storage paths (`RemyStoragePaths.swift` and writers)

Mac root: `~/Library/Application Support/<bundle id>/` (`RemyStoragePaths.swift:11-17, 54-64`). A malformed bundle id (empty, `.`, `..`, contains `/` or `\`) falls back to `io.github.brysonjl.remy`. The app never uses the shared FluidAudio default cache (`:5-6`). The application support root is resolved through symlinks once and is the trust boundary.

| Mac path (under root) | Contents | Written by | Suggested Windows location |
|---|---|---|---|
| (preferences plist, `~/Library/Preferences/<bid>.plist`) | All settings in section 1 | UserDefaults | `%APPDATA%\Remy\settings.json` (roaming is fine; no machine-specific secrets inside) |
| Keychain items (3 services above) | API keys | Settings | Windows Credential Manager entries `Remy/openrouter-api-key`, etc. |
| `Models/` | Model store root | `RemyStoragePaths.modelsDirectory` (`:18`) | `%LOCALAPPDATA%\Remy\Models\` (large, non-roaming) |
| `Models/Installed/<modelID>/{active.json, revisions/<sha>/..., revisions/<sha>.json}` | Verified pinned bundles | `RemyModelStore` | same under Models |
| `Models/.staging/<uuid>/` | In-flight downloads | `RemyModelStore` | same |
| `Models/<legacy folder per model>` (e.g. `parakeet-tdt-0.6b-v3-coreml`) | SDK-managed models | FluidAudio | n/a on Windows |
| `Models/<vad folder>/<silero vad file>` | Optional VAD | FluidAudio | `Models\silero-vad\` if used |
| `Models/S1-mini/s1-mini-q4_k_m.gguf`, `LICENSE`, transient `.<id>.gguf.part`, `.<id>.license.part` | Cleanup model | `S1MiniModelManager` | `Models\S1-mini\` if ported |
| `Recovery/` (`RemyStoragePaths.swift:19`) | `<UUID>.caf` audio + `<UUID>.json` metadata for interrupted/failed/cancelled sessions; 24 h retention (cleanup runs while the app runs or when the store reloads); marked excluded from backup. Audio is 16 kHz (duration = frames/16000) | Recovery store | `%LOCALAPPDATA%\Remy\Recovery\` (use WAV/FLAC instead of CAF) |
| `History/history.sqlite` (+ `-journal`, `-wal`, `-shm`) | Transcript history (SQLite, schema v2) | `RemyHistoryRepository` | `%LOCALAPPDATA%\Remy\History\history.sqlite` (use Microsoft.Data.Sqlite; keep WAL) |
| `History/history-before-v2-<UUID>.sqlite`, `History/Legacy Backups/*` | Rollback snapshots of legacy history | history bootstrap | optional; not needed on Windows (no legacy history) |
| `CleanupProfiles/profiles.json` | Named cleanup provider profiles (non-secret; keys in Keychain; atomic swap writes) | `CleanupProfileFileRepository` (`:29`) | `%APPDATA%\Remy\CleanupProfiles\profiles.json` |
| `Sounds/<UUID>.<ext>` | Imported custom start/complete sounds (<=10 MiB, <=10 s) | `RemySoundEffects.Store` (`:42-44`) | `%APPDATA%\Remy\Sounds\` |
| `Migration/receipt.json` | Dev->Release import receipt | `RemyLocalDataMigration` | not needed |
| Logs | Mac uses unified logging (`os.Logger`), nothing written by the app itself | | `%LOCALAPPDATA%\Remy\Logs\remy-<date>.log` (new; see section 8) |

Safety rules to port (they have tests): history directory is created with mode 0700 via directory descriptors, refusing symlinks and files not owned by the user (`prepareHistoryDirectory`, `:30-59`; error `Remy could not open its private history folder. Your existing history has not been changed.`); SQLite side files must be regular, single-link, owned by user; model deletion/creation only inside `Models` and never through links (error text in 2.4); `removeEmptyModelParents` prunes now-empty model directories upward until `Models` root. Windows: create dirs under the user profile (default ACL is already user-private), reject reparse points in the app tree, never follow junctions for model/history IO.

Deletion semantics ("Clear All" on History, `RemyStoredHistoryDeletion.swift`): removes recognized history rollback snapshots (`History/history-before-v2-*.sqlite`, `History/Legacy Backups/*` recognized names) and recovery files (`Recovery/*.caf|.json` recognized names); leaves unknown files, links and special files untouched; leaves the open database, models, preferences, credentials; if anything fails it reports incomplete deletion and can be retried. Uninstall does not remove the app data folder, preferences or credentials (`docs/privacy.md`): Windows installer should offer the same and document manual cleanup of `%LOCALAPPDATA%\Remy`, `%APPDATA%\Remy` and Credential Manager entries.

---------------------------------------------------------------------------

## 8. Diagnostics (`RemyDiagnostics.swift`, `PerfTrace.swift`, `docs/engineering/diagnostics.md`)

Policy (hard requirements for Windows):
- Default diagnostics describe operations and bounded results only. Never log: audio, transcripts, selected-field text, prompts, provider response/error bodies, API keys or credential hashes, file paths, document/window titles, target process ids, microphone unique ids, arbitrary error descriptions. "Marking private" is not enough; do not log them at all.
- `RemyDiagnostics.subsystem` = bundle id (fallback `io.github.brysonjl.remy`; Dev differs so logs are separable).
- `RemyDiagnostics.errorCategory(error)` returns only: `cancelled` (cancellation / URL cancelled), `timeout` (URL timed out), `transport` (any other URL error), `other` (everything else) (`RemyDiagnostics.swift:20-29`). Windows map: `OperationCanceledException`/`TaskCanceledException` not timeout -> `cancelled`; timeout (`HttpClient` timeout, `TimeoutException`) -> `timeout`; `HttpRequestException`/`IOException` network -> `transport`; else `other`. Never log exception message/type/stack for these. Port `RemyDiagnosticsTests` (3 tests).
- Hotkey and insertion logs describe state/format decisions without keys pressed or text delivered. Provider requests never log bodies, auth headers, HTTP error bodies or raw messages.

PerfTrace (`PerfTrace.swift`): span/event timing helper `PerfTrace.begin("<area>.<phase>")` ... `.end()`, `PerfTrace.measure(name){...}`. Names are static strings `<area>.<phase>`; known ones: `app.launch`, `app.startup`, `app.permissionCheck`, `app.appleSpeechAssetRefresh`, `app.inputSourceApply`, `app.shutdown`, `app.terminate`, `stt.prepare`, `stt.enginePrepare`, `stt.modelDownload`, `stt.modelLoad`, `stt.finalize`, `cleanup.generate`. Each interval is an OSSignpost span plus a `notice` log line with duration (category `Performance`). Metadata allowed: session UUID, engine/mode/state labels from a closed set, counts, durations, numeric system status, OS version, `hw.model`, build configuration (`Debug`/`Release`), numeric window-presentation geometry. Kill switch env `DICTATE_ANYWHERE_PERF_TRACE=0|1`; defaults: Debug and Release traces on, distribution build (`DISTRIBUTION_BUILD`) off. Disk use is bounded by the OS log store; the app has no log file and no in-app export.
- Third-party/native stderr is not filtered automatically; SDK debug mirroring is off by default (warning level; Debug + `REMY_KEEP_FLUID_DEBUG_LOGS=1` for local only). Policy: review captured logs before sharing; do not upload raw logs or attach to CI.
- There is **no in-app diagnostics export and no log viewer** in the Mac app. Transcript history and explicit exports are product data, not diagnostics.

Windows suggestion: `Microsoft.Extensions.Logging` + a rolling file sink at `%LOCALAPPDATA%\Remy\Logs\remy-yyyyMMdd.log` (cap e.g. 7 files x 5 MB), default level Warning in release; `PerfTrace` as an `ActivitySource`/`Stopwatch` wrapper logging `name duration_ms` at Information gated by env `REMY_PERF_TRACE=0|1` (keep the old env name as an alias). Use a structured logger API that takes only enums/ints/known labels, with unit tests mirroring `PerfTraceTests` and `PrivacyRegressionTests` (sentinel strings must never appear in captured logs). Provide no upload; an optional "Open logs folder" button on About is fine.

---------------------------------------------------------------------------

## 9. Tests

### 9.1 `app/Dictate AnywhereTests/` (XCTest, count of `func test*` in parentheses)

Legend: **[PORT]** = pure logic we should port to xUnit so Windows behavior matches (cases/fixtures reusable); [adapt] = contains portable logic but needs platform seams; (mac) = macOS/AppKit/FluidAudio/Apple Speech/Keychain specific, do not port directly; (ui/perf) = rendering or benchmark.

Settings, startup, models, storage, diagnostics:
- SettingsLogicTests (33) **[PORT]**: setting defaults/round-trips (prewarm), Ollama reasoning migration, filler-word removal (case-insensitive, word boundaries, punctuation/spacing cleanup, disabled/empty no-op, defaults list), hotkey binding add/update/clear, post-processing mode flags, S1-mini styling fallback, vocabulary-mode coercion/restore (auto-switch pending-restore logic), extended writing styles, history raw-text decode, theme mode names, Assembly prompt reset (cloud route; skip).
- SettingsInteractionTests (5) [adapt]: settings interaction wiring.
- ModelAndModeTests (45) [adapt]: enum raw values/metadata, language sets per model, catalog recommendations, hardware gating, post-processing feature matrix, context-support matrix, sidebar page order/titles, `SupportedLanguage` uniqueness, Nemotron language codes. Port the enum/matrix/recommendation cases that remain applicable (languages, post-processing modes, sidebar titles, appearance modes); drop ANE cases.
- RemyConfigurationVersionTests (5) **[PORT]**: legacy preflight read-only, migrate once/idempotent, future version never downgrades, malformed markers rejected, domains independent.
- RemyStartupTests (2) **[PORT]**: configuration gate allows legacy/current, blocks future/malformed without mutation.
- RemyMigrationPreferencesTests (9), RemyMigrationBootstrapTests (5), RemyLocalDataMigrationTests (11) (mac): Dev->Release import (allowlist, limits, receipts). Port only if an importer is built.
- RemyHistoryBootstrapTests (5), RemyLegacyHistorySnapshotTests (6) (mac): legacy defaults-history capture. Skip.
- RemyModelManifestTests (2) **[PORT]**: pinned bundle completeness, rejection of traversal/mutable URLs/inventory conflicts. Rewrite with the Windows manifest.
- RemyModelStoreTests (10) **[PORT]**: install never activates; corrupt/incomplete payloads never activate; extra files/symlinks rejected; cancel retains previous bundle; leases block switch/rollback/removal; offline integrity detects post-install corruption; legacy adoption; orphan removal; active-identity protection; activation write failure keeps pointer. Fully portable with an injected transport and temp dirs.
- RemyParakeetAssetsTests (4) [adapt]: legacy/active directory inventory.
- RemyStoragePathsTests (5) **[PORT]**: Dev/Release/upstream independence, malformed identity cannot escape root, deletion rejects root/siblings/traversal, nested removal preserves others, symlink (use reparse point) cannot redirect.
- RemyStoredHistoryDeletionTests (4) **[PORT]**: Clear All removes recognized artifacts only, safe boundaries, failures reported.
- RemyDiagnosticsTests (3) **[PORT]**: subsystem fallback and `errorCategory` vocabulary.
- PerfTraceTests (8) [adapt]: trace naming/metadata boundaries/kill switch.
- PrivacyRegressionTests (10) **[PORT]**: sentinels never appear in logs/diagnostics.
- ModelReadinessTests (2) **[PORT]**: installed != ready; failure keeps actionable reason (also verify titles from section 2.5).
- ReleaseReadinessTests (9) (mac/doc): release metadata/doc consistency; replace with Windows release gate checks.
- AppDelegateTests (4), AppearanceTests (2), PermissionsTests (2), PermissionLifecycleTests (6), RemyHotkeySleepTests (5) (mac): lifecycle/permissions/sleep (port the sleep/wake state-machine intent only).
- RemyTestIsolationTests (1), RemyFeaturePolicyTests (3), RemyDisabledCloudRouteTests (5): isolation and the policy that cloud ASR is off (port policy test: stored `assemblyAI` -> local).

Text processing and cleanup integrity (highest value to port):
- FillerWordCJKTests (8) **[PORT]**; CJKTextTests (7) **[PORT]**; TextInserterCJKTests (3) [adapt]; MandarinFixtureTests (6) [adapt].
- MergeTranscriptsTests (17) **[PORT]**: merging continued/recovered transcripts.
- PostProcessingNormalizationTests (11) **[PORT]**: normalization of cleanup output.
- TranscriptCleanupIntegrityTests (1) **[PORT]** and TranscriptCleanupPlanTests (11) **[PORT]**: semantic-preservation guard and cleanup plan decisions (the validation harness also runs both).
- CleanupAnswerParserTests (12) **[PORT]**: provider answer parsing; CleanupProviderContractTests (21), CleanupBoundedHTTPTransportTests (11), CleanupTransportPolicyTests (2), RemoteCleanupInputPolicyTests (10), RemoteCleanupRetryPolicyTests (3), RemoteCleanupConcurrencyTests (6) [adapt]: provider contracts/limits/retries (portable if Windows keeps remote cleanup).
- RemyCleanupAttemptTests (8) **[PORT]**, RemyCleanupFailureClassificationTests (2), RemyCleanupProfileIntegrationTests (3): cleanup attempt deadline/fallback state and failure categories.
- CleanupProfileTests (7), CleanupProfileControllerTests (9), CleanupProfileFileRepositoryTests (13), CleanupProfileRuntimeTests (8) [adapt]; CleanupProfileKeychainStoreTests (10) (mac, rewrite for Credential Manager); CleanupPromptSettingsTests (4) **[PORT]** (default prompt loading/reset).
- S1MiniPostProcessingTests (32) [adapt]: S1-mini prompt/control-line building and output safety (**[PORT]** if S1-mini is ported); S1RuntimePolicyTests (5).
- AppleCleanupInstructionsTests (2) (mac).
- VocabularyParserTests (9) **[PORT]**, VocabularyHeadLoaderTests (2) (mac/FluidAudio), TimedRequestCacheTests (4) **[PORT]**.
- DictationContextTests (78) **[PORT]** (largest pure suite: writing styles, categories, context building/prompt text), DictationStartupContextTests (44) [adapt], DictationContinuationTests (24) **[PORT]**, DictationFocusResolverTests (10), DictationRecoveryTests (17), ChromiumContextCaptureTests (14), AccessibilityMessagingTimeoutTests (5), RemyInsertionTargetTests (17), RemyDeliveryAuthorizationTests (8), RemyDictationConfigurationTests (16), RemyDictationDestinationTests (8) **[PORT]** (destination enum decode/default, router eligibility), TextInsertionFinalizationTests (6) (all mostly mac AX/paste; port the pure decision logic).
- InputSourceMappingTests (15), InputSourceMappingAvailabilityTests (6), InputSourceMonitorTests (5), InputSourceProfileResolverTests (10) [adapt]: mapping sanitization (pure) vs monitor (mac).
- HotkeyTests (20) **[PORT]** (modifier normalization, matching, conflict rules, display names; re-key to Windows VKs).

Audio/session/history:
- AudioSampleBufferTests (1), AudioVolumeGateTests (4), AudioPipelineBufferTests (4), PCMStreamConverterTests (7), BatchTranscriptionPolicyTests (13), RecognitionSessionConfigurationTests (3), OfflineBatchPreparationTests (2), AudioMonitorTests (7), AudioCaptureStartupTests (8), AudioCaptureTeardownTests (8), VolumeControllerTests (6) [adapt]: sample buffering, gating, conversion and batching are portable logic; capture/teardown/volume are AVFoundation/CoreAudio.
- CancellationTests (16), CancellationScreenTests (2), RemySessionCoordinatorTests (13), RemySessionIntegrationTests (20), RemyLongCaptureFailureTests (9), RemyLongDictationTests (2) [adapt]: session state machine/cancellation; port the state-machine cases.
- RemyHistoryControllerTests (20), RemyHistoryRepositoryTests (14), RemyHistoryIntegrationTests (17), TranscriptHistoryPersistenceTests (2) [adapt]: SQLite history semantics (portable with Microsoft.Data.Sqlite).
- AttentionIssueTests (11) **[PORT]** (issue ordering/optionality), RemySoundEffectsTests (11) [adapt] (identifier validation is pure **[PORT]**; decoding is mac).
- AssemblyAIEngineTests (28), AssemblyAILiveInsertionTests (1) (cloud route disabled; skip).
- Overlay: OverlayContentTests (14) [adapt], OverlayScreenPickerTests (11), OverlayScreenSessionTests (16), OverlayWindowScreenSessionTests (12), ScreenSnapshotTests (8) (mac window/screen).
- AppleSpeechExplicitInstallationTests (2), AppleSpeechPreparationLifecycleTests (2), NemotronMultilingualSmokeTests (1), RecoveryASRSmokeTests (9), ExternalSpeechFixtureTests (4)/ExternalSpeechFixtures, ModelSwitchBenchmarkTests (1), OverlayPerformanceBenchmarkTests (1), PipelinePerformanceBenchmarkTests (5), PipelineWorkloadBenchmarkTests (6), BenchmarkStatistics(+Tests 2), RemyBaselineProviderTests (2), RemyBaselineRuntimeTests (1) (mac/ASR/benchmark; reference for what a Windows ASR smoke test should assert).
- ComponentRenderTests (19), DesignSystemTests (23) (ui).
- `Fixtures/` folder: synthetic test data (speech fixtures omitted from the public repo).

### 9.2 `validation/` (separate dependency-free SwiftPM harness + scripts)

`validation/Package.swift`, `run-tests.sh`; `validation/Sources/Dictate_Anywhere` = symlinks to production files; `validation/Tests/OfflineValidationTests/` = symlinks to 9 app tests: AudioSampleBufferTests, AudioVolumeGateTests, CJKTextTests, RemyCleanupAttemptTests, RemyStoragePathsTests, RemyStoredHistoryDeletionTests, TimedRequestCacheTests, TranscriptCleanupIntegrityTests, TranscriptCleanupPlanTests (45 XCTest cases, no network/GUI/models). This list is the recommended first batch of xUnit ports because it was deliberately chosen as the portable core.
Other validation folders: `check-third-party-notices.py` (notice inventory check), `dependency-baseline/` (dependency pin verification), `destination-baseline/` (destination-app check page), `foundation-checks/` (clean-clone/foundation scripts), `long-dictation/` (long-dictation fixture generator), `model-baseline/` (`manifest.json`, `model_baseline.py`: pinned model baseline), `provider-baseline/` (cleanup provider fixtures and Qwen install plan), `publication-hygiene/` (public-repo secret/media hygiene checker + tests), `runtime-baseline/` (runtime monitor/fixtures), `results/` (dated evidence records, e.g. `2026-11-07-release-0.1.0`). Windows should add an equivalent offline `dotnet test` project of the [PORT] suites plus a model-baseline check that re-verifies the Windows manifest hashes.

### 9.3 Porting priority for parity

1. Settings/versioning: SettingsLogicTests (defaults list, filler removal, mode coercion), RemyConfigurationVersionTests, RemyStartupTests, ModelReadinessTests, RemyDictationDestinationTests, CleanupPromptSettingsTests.
2. Model store: RemyModelManifestTests, RemyModelStoreTests, RemyStoragePathsTests (with fakes for HTTP).
3. Text processing/cleanup integrity: FillerWordCJKTests, CJKTextTests, MergeTranscriptsTests, PostProcessingNormalizationTests, TranscriptCleanupIntegrityTests, TranscriptCleanupPlanTests, CleanupAnswerParserTests, RemyCleanupAttemptTests, VocabularyParserTests, TimedRequestCacheTests, DictationContextTests.
4. Diagnostics/privacy: RemyDiagnosticsTests, PrivacyRegressionTests, PerfTraceTests.
5. Hotkey logic: HotkeyTests (re-keyed).

---------------------------------------------------------------------------

## 10. Quick checklist of MAC-ONLY items and the Windows suggestion

| Item | Suggestion |
|---|---|
| UserDefaults plist | JSON file in `%APPDATA%\Remy` |
| Keychain | Credential Manager / DPAPI |
| SMAppService login item | HKCU Run key (or MSIX StartupTask) |
| NSStatusItem / activation policy / Dock | Tray icon (NotifyIcon/H.NotifyIcon) + `ShowInTaskbar` toggle |
| Single instance by bundle id | Named mutex + activate-first-instance signal |
| NSWorkspace sleep/wake | `SystemEvents.PowerModeChanged`, re-hook keyboard on resume |
| Dev/Release importer | Skip for v1 |
| Sparkle | None; About shows version, optional GitHub Releases "Check for updates" |
| Apple Speech, Apple Intelligence, `appleSpeechLanguage`, `legacyAppleSpeechMigrationPending` | Remove |
| Neural Engine gating, Core ML bundle, FluidAudio | Replace with sherpa-onnx int8 bundle + own manifest |
| AppleScript paste prewarm | Prewarm clipboard/SendInput path |
| macOS system sound names | Bundle WAV equivalents; keep id scheme |
| macOS-specific shortcut conflicts, fn key, Caps Lock rule | Windows-reserved list; keep Caps Lock exclusion |
| Unified logging / signposts | File logger + Stopwatch spans, same privacy rules |
| AppKit window frame defaults | Persist window bounds in `settings.json` (new keys, not in Mac schema) |
