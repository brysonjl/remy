# Remy UI and Visual Design: Parity Spec for the Windows Port

Source of truth: `app/Dictate Anywhere/` (Swift/SwiftUI/AppKit). All paths below are relative to that folder unless noted. Line numbers are approximate anchors (files change); symbol names are authoritative. Screenshots: `docs/images/remy-dictating.png`, `remy-cleanup.png`, `remy-context.png`, `remy-history.png` (all dark appearance, 1120x780 window).

Conventions:
- **MAC-ONLY** = depends on a macOS facility. A one-line Windows suggestion follows, or "drop".
- **DEAD/HIDDEN** = code exists but the UI is unreachable in the shipped Mac build (see `RemyFeaturePolicy.swift`: cloud speech (AssemblyAI) is disabled, `allowsCloudSpeechRecognition = false`). Do not build these on Windows unless a cloud engine is added.
- Strings are verbatim. Curly apostrophes are used in some strings (`’`), and `…` is a single ellipsis character U+2026. Copy exactly.
- Mac points (pt) map 1:1 to WPF device-independent pixels (DIP). Use DIPs.
- "Windows engine caveat": the speech-engine catalog (FluidAudio/Parakeet/Apple Speech) and cleanup methods (Apple Intelligence, S1-mini via Metal/llama.cpp) are Mac specifics. This spec documents the UI shape; the Windows team substitutes available engines but should keep the layout (a Settings row that picks an engine, a model row, a status row with Download/Prepare action, delete action).

---------------------------------------------------------------------------

## 1. Design system

### 1.1 Visual identity

"Service-station" palette: cream surfaces, navy structure, teal signals. Brand mascot is a robot waiter (RemyWaiter.png). Tagline "At your word." Sidebar section kicker "SERVICE CONSOLE". Footer card "SERVICE STATUS". Page titles use rounded display lettering with a teal vertical bar.

Appearance modes: Light, Dark, System. Every color token has light and dark variants (resolved at runtime). The theme override is app-wide (Settings > General > Display > Theme). The floating overlay has its own independent theme (Section 4).

### 1.2 Color tokens (`DesignSystem/DesignTokens.swift`, enum `DS.Colors`, lines ~9-37)

Format: `#RRGGBB`. "Fixed" means identical in both modes.

| Token | Light | Dark | Used for |
|---|---|---|---|
| accent (asset `AccentColor`) | #087F83 | #61DCCF | Toggle-on fill, slider fill, selected nav icon, progress/tint, status dot while recording/processing. See `Assets.xcassets/AccentColor.colorset/Contents.json` (components are written in hex: light R08 G7F B83; dark R61 GDC BCF) |
| accentDeep | #08777B | #61DCCF | Primary button fill and border, links, selected-nav bar, focus rings, hint icons, selected chip fill, warning icon |
| onAccent | #FFFFFF | #102A35 | Text on primary button / selected chip |
| accentSoft | #DCEFEB | #153F46 | Selected nav item background, attention-banner background |
| bgCard | #FFFDF7 | #1A2D3B | Cards, secondary buttons, text field fill, search field fill |
| bgInset | #EAEDE6 | #142532 | Inset controls: dropdown fill, chip fill, keycap fill, compact button fill, status pill (neutral) fill |
| bgSidebar | #E7EBE8 | #0B1722 | Sidebar background |
| bgWindow | #F3F1E9 | #101E2A | Window/page background |
| border | #CDD9D5 | #35505F | 1pt control and card borders, sidebar right edge |
| borderSoft | #E0E7DF | #263F4D | Hairline dividers inside cards, nav hover fill |
| ink | #142D3B | #F5F2E9 | Primary text |
| textSecondary | #526571 | #AFBFC8 | Secondary text, captions, inactive icons |
| success | #237C65 | #71DAB4 | Success dot/icon |
| successSoft | #DEEEE3 | #193E36 | Ready pill fill, "Selected" badge fill |
| successText | #22654E | #90E3BE | Ready pill text |
| panelText | #155D64 | #A0E7DE | Attention-banner text |
| destructive | #B34238 | #FF9E8E | Destructive button label, error text/icon, trash tint |
| toggleOff | #B7C6C5 | #4A626D | Switch track when off |
| sliderTrackRest | #D2DDD7 | #324D5B | Slider track |
| addButtonFill | #DFEBE4 | #1C3943 | "Add ..." full-width button fill |
| overlayPreviewFill | #E7EEE7 | #18303C | Backdrop behind overlay sample in settings |
| keycapShadow | #B9C9C4 | #000000 | Keycap 1.5pt bottom shadow |
| footerCardFill | white @ 50% alpha | white @ 8% alpha | Defined, currently unused by DSFooterCard (which uses serviceInk) |
| signalWarm | #AD563B | #F1B184 | Warm signal (fallback warning in overlay uses OverlayPalette.warmSignal) |
| serviceInk | #102838 (fixed) | same | Footer card fill (always dark), shadow base color |
| serviceCream | #F5F2E9 (fixed) | same | Footer card main text |
| serviceLight | #75E5D3 (fixed) | same | Footer card border (at 22% alpha) |
| serviceMuted | #B1C7CF (fixed) | same | Footer card captions |

Other literal colors: white `#FFFFFF` for switch knob and slider knob; black at 15%/13% alpha for knob shadows; white at 35% alpha ring around footer status dot.

### 1.3 Typography

There are two font families in code, both **system fonts on Mac** (SF Pro / SF Pro Rounded). `DS.Fonts.display` = `Font.system(size, weight, design: .rounded)` (SF Rounded), default weight semibold. `DS.Fonts.ui` = `Font.system(size, weight)` (SF Pro), default weight regular. Monospaced overlines use `design: .monospaced` (SF Mono).

The bundled fonts in `Fonts/` are NOT referenced by any Swift code (Info.plist `ATSApplicationFontsPath` = `.` merely registers them). They are shipped for the open-source font inventory only:
- `Fonts/Inter-VariableFont.ttf`, license `Fonts/inter-OFL.txt` (SIL OFL 1.1, Copyright 2020 The Inter Project Authors, github.com/rsms/inter)
- `Fonts/Fraunces-VariableFont.ttf`, license `Fonts/fraunces-OFL.txt` (SIL OFL 1.1, Copyright 2018 The Fraunces Project Authors, github.com/undercasetype/Fraunces)

Windows suggestion: UI text = Segoe UI Variable (or bundled Inter, closest to SF Pro; OFL permits redistribution, ship `inter-OFL.txt`). Display/rounded headings = Segoe UI Variable Display Semibold/Bold, or Inter Bold (no SF Rounded equivalent bundled). Monospaced overlines = Cascadia Mono / Consolas. Fraunces needn't be used (unused on Mac) unless desired for marketing.

Type scale (size / weight), all in DIP:

| Role | Spec |
|---|---|
| Page title (DSSectionHeader) | display 27 bold |
| About hero app name | display 30 semibold (default) |
| Sidebar brand name | display 23 bold |
| Sidebar tagline | ui 11.5 medium |
| Page subtitle | ui 13.5 regular, textSecondary |
| Overline (DSOverline) | mono 10.5 bold, tracking 1.1, UPPERCASED, textSecondary |
| Sidebar "SERVICE CONSOLE" kicker | mono 9 bold, tracking 1.3, textSecondary |
| Footer "SERVICE STATUS" | mono 9 bold tracking 1.1, serviceMuted, with a `waveform` icon, 5 gap |
| Footer status text | ui 12 semibold serviceCream |
| Footer version | mono 10 regular serviceMuted |
| Nav item | ui 13.5; semibold if selected, medium otherwise |
| Row label | ui 13.5 medium ink |
| Row value (DSInfoRow value) | ui 13.5 regular textSecondary |
| Caption / hint / field message | ui 12.5 regular textSecondary; multi-line caption line spacing = 12.5*0.5 - 3 = 3.25 extra |
| Primary button | ui 13 semibold |
| Secondary button | ui 13 medium |
| Inset button (Copy) | ui 12 medium, icon 11 medium |
| Add button | ui 13 semibold, icon 12.5 semibold |
| Dropdown label | ui 13.5 medium; chevron icon 10 medium |
| Text field / search field | ui 13 |
| Chip text | ui 12 medium |
| Status pill | ui 12.5 semibold |
| Keycap | ui 12.5 semibold |
| Empty state title | ui 14 semibold; message 12.5; icon 26 |
| History timestamp | ui 11.5 semibold, tracking 0.2 |
| History body text | ui 13.5, line spacing 13.5*0.55-4 = 3.425 |
| Banner title / message | ui 12.5 semibold / 12 |

### 1.4 Spacing and layout metrics

`DS.Spacing`: section gap 24 (History page uses 20); overline-to-card 10; row padding vertical 13, horizontal 16; row control gap 24 (label to trailing control); label-to-caption 4; page content padding top 34, horizontal 44, bottom 36. `DS.Metrics`: readable content width max 820 (page content column is left-aligned in a column centered in the pane); sidebar width 264; default window 1120x780.

Page scaffold (`DSPage`, `DesignSystem/Molecules/DSCard.swift`): vertically scrolling pane, background bgWindow; content column max width 820 with padding (top 34, h 44, bottom 36), column centered horizontally in the pane.

### 1.5 Radii

`DS.Radius`: card 16; control 11 (buttons, text fields, nav item); small 8 (keycap, compact buttons, icon button); panel 10 (search field); window 14 (defined, window rounding is OS); capsule 999 (chips, pills). Switch radius 14 (pill). Footer card radius 15. Multiline text area radius 12 (continuous). "Selected" badge radius 4. Accent bar in header radius 2.

### 1.6 Shadows and borders

- Card: shadow serviceInk @ 7% alpha, blur radius 6, y offset 3; 1pt border `border`.
- Tactile controls (`DSControlSurface`): shadow serviceInk @ 7%, radius 1, y 2 (y 0 and alpha 0 while pressed); 1pt inside border (`border` unless overridden; increased-contrast mode uses textSecondary). Hover overlay = ink @ 5% alpha on the fill; pressed = ink @ 10%; disabled control opacity 0.45 (hover/press feedback off).
- Keycap: no blur, y offset 1.5 shadow keycapShadow (a "bottom lip").
- Switch knob shadow: black 15%, radius 2, y 1. Slider knob: black 13%, radius 3, y 1; knob has 1pt `border` ring.
- Selected nav item: shadow black @ 8%, radius 3, y 1.
- Focus ring (text fields, slider): 2pt accentDeep stroke (slider ring drawn at padding -4, radius 8).
- Accessibility: honor high-contrast (stronger 1pt border color = textSecondary, selected nav gets 1pt ink outline) and reduce-motion (disable animations).

### 1.7 Atoms (`DesignSystem/Atoms/`)

- **DSPrimaryButtonStyle** (`DSButtons.swift`): label ui 13 semibold, onAccent, single line (no wrap), padding 7 vertical x 14 horizontal, fill accentDeep with accentDeep border, radius 11, DSControlSurface feedback. Used for "Change", "Record Shortcut", "Save profile", "Open Settings" etc.
- **DSSecondaryButtonStyle**: label ui 13 medium in ink (or `destructive` tint for `.dsDestructive`), padding 7x14, fill bgCard, border `border`, radius 11.
- **DSCompactButtonStyle**: wraps arbitrary label in DSControlSurface; default fill bgInset, radius 8.
- **DSIconButton**: 28x28, SF symbol size 11.5 medium, tint textSecondary (trash in app-rules uses destructive), compact style. Tooltip and accessible name = label.
- **DSInsetButton**: bgInset compact button, optional leading icon (11 medium) + title (12 medium), 5 gap, padding 6x10. ("Copy", "Copy raw", "Refresh details", "Replay sample".)
- **DSAddButton**: full width, centered `plus` icon (12.5 semibold) + title (13 semibold) in accentDeep, 8 gap, padding 13 vertical, fill addButtonFill, radius 16 (card radius).
- **DSChip** (`DSChip.swift`): capsule, text ui 12 medium, max width 240 (tail truncation), padding 4 vertical, 10 leading, trailing 10 (or 5 if removable). Default: bgInset fill, 1pt `border`. `isSelected`: accentDeep fill, onAccent text, no border. Optional remove "x" button: xmark icon size 8 bold, hit area 24x24 plus 4 trailing padding; color onAccent when selected else textSecondary; accessible "Remove <text>". Optional select tap ("Select <text>"). Disabled opacity 0.5. Tooltip = full text.
- **DSStatusPill**: capsule, 7pt dot + text (12.5 semibold), 6 gap, padding 4x10. Default success colors: dot `success`, text `successText`, fill `successSoft`. Neutral variant: dot/text `textSecondary`, fill `bgInset`.
- **DSKeycap**: text ui 12.5 semibold ink, padding 6x10, fill bgInset, radius 8, 1pt border, 1.5 bottom lip.
- **DSToggleStyle / DSSwitch**: 46x28 pill (radius 14), on = `accent`, off = `toggleOff`; white 22pt knob with 3pt inset, slides leading/trailing with spring(0.2s) animation (none if reduce-motion). No visible label (label is a separate row text). Accessible value "On"/"Off".
- **DSSlider**: height 16 hit area; 5pt track (radius 3) sliderTrackRest; filled portion accent from left to knob center (min 5pt); 16pt white knob with 1pt border ring. Drag sets value across [range]; keyboard arrows step by (range/10); accessible value "N percent".
- **DSDropdown / DSDropdownLabel**: label ui 13.5 medium ink + `chevron.up.chevron.down` icon (10 medium, textSecondary), 8 gap, padding 7x12, DSControlSurface fill bgInset (radius 11). Opens a native menu; the selected item shows a checkmark. Disabled -> 0.45 opacity.
- **DSSearchField**: leading magnifier (12.5 medium), text field 13, clear (x-circle) button appears when non-empty (tooltip "Clear search"), Esc clears; padding 8x12, fill bgCard, radius 10, 1pt border, 2pt accentDeep when focused.
- **DSTextField**: padding 7x12, ui 13, fill bgCard, radius 11; same focus border (2pt accentDeep). `isSecure` variant masks input.
- **DSOverline / DSDivider / DSHint / DSLoadingMessage / DSFieldMessage** (`DSText.swift`):
  - Divider: 1pt line `borderSoft`.
  - Hint: top-aligned icon (default `lightbulb`, 12 medium, accentDeep, top pad 2) + text (12.5, textSecondary), 8 gap.
  - Loading message: small spinner + text (12.5 textSecondary), 8 gap.
  - Field message: icon 12 medium + text 12.5, 8 gap. Tones: error = xmark.circle.fill, `destructive` icon and text; warning = exclamationmark.triangle.fill, accentDeep icon, secondary text; success = checkmark.circle.fill, `success` icon; info = info.circle.fill, textSecondary icon. Accessible text "Error: ...", "Warning: ...", "Success: ...", "Information: ...".
- **HotkeyKeycaps** (`HotkeyKeycaps.swift`): `HotkeyKeycapTokenizer.tokens(displayName)` splits a stored hotkey display string into keycaps: leading `fn`; side-aware modifier = `L`/`R` followed by a modifier symbol becomes ONE keycap "L ⌃" (letter, space, symbol); lone modifier symbols (⌃ ⌥ ⇧ ⌘) are one keycap each; remainder is the key name ("Space", "F5", "A"). Legacy "⎋" is rendered "Esc". `DSKeycapGroup` = HStack, 5 gap. **Windows:** use the modifier names "Ctrl", "Alt", "Shift", "Win" (and "L Ctrl"/"R Ctrl" for side-specific) in keycaps instead of Mac glyphs.
- **WindowDragArea** (`WindowDragArea.swift`): the sidebar background is a window-drag handle (the window has no native title bar). Windows: set the sidebar region as a caption drag area (`WindowChrome` caption height or `DragMove()` on mouse-down of non-interactive areas).

### 1.8 Molecules (`DesignSystem/Molecules/`)

- **DSCard**: VStack(0) filling width, fill bgCard, radius 16, 1pt border, card shadow (above).
- **DSSection(overline:)**: VStack(spacing 10): DSOverline above a DSCard.
- **DSSectionHeader(title, subtitle)**: HStack: 5x22 accentDeep bar (radius 2) + title (display 27 bold, ink), 9 gap; below it (5 gap) subtitle (ui 13.5 textSecondary, wraps).
- **DSInfoRow(label, trailing)**: HStack center-aligned, 24 gap, label left (13.5 medium, optional color/weight override), spacer, trailing control right-aligned; padding 13 v / 16 h. Value convenience: trailing = text 13.5 regular textSecondary.
- **DSDetailRow(label, caption, trailing)**: VStack(4): top row = label + trailing control (center aligned, 24 gap); below = caption (12.5 secondary, wraps); padding 14 v / 16 h.
- **DSStackedRow(label, caption, isOn)**: DetailRow with a DSSwitch as trailing.
- **DSCardCaption(text)**: caption spanning card width, padding 12 v / 16 h.
- **DSEmptyState(systemImage, title, message?)**: centered column, 8 gap, 26pt icon textSecondary, title 14 semibold, optional message 12.5 centered; padding 24 vertical.
- **DSBrandMark(size)**: image `RemyWaiter` (`Assets.xcassets/RemyWaiter.imageset/remy-waiter.png`, 1145x1374 PNG) aspect-fit into a size x size square. Sidebar 66, About hero 76.
- **DSBrand** (sidebar header): HStack(10): brand mark 66 + VStack(3): app name (display 23 bold ink; `RemyIdentity.displayName` = "Remy") and "At your word." (ui 11.5 medium secondary).
- **DSFooterCard(statusText, statusColor, versionText)**: always-dark card: fill serviceInk, radius 15, 1pt border serviceLight @22%, padding 14, VStack(10) left-aligned: [waveform icon + "SERVICE STATUS"], [8pt status dot (1pt white@35% ring) + status text], version text "Version X.Y.Z" in mono 10.
- **DSNavItem(title, systemImage, isSelected)**: HStack(10): icon (13.5 medium, 17pt wide frame; accent when selected else textSecondary) + title; padding 9 v / 10 h; radius 11 background: accentSoft (selected, with shadow) / borderSoft (hover) / clear. Selected shows a 3x18 accentDeep capsule at the leading edge (2pt inset). Whole row clickable.
- **DSModelReadinessStatus / DSModelReadinessRow** (`DSModelReadiness.swift`, `ModelReadiness.swift`): a standard "Status" row used for every model. Layout: DSInfoRow(label "Status" by default) with trailing HStack(10): status element + optional secondary action button. If `readiness.isWorking` the status element is a spinner + title, otherwise a status pill: Ready = green pill (dot success, text successText, fill successSoft); any other non-working state = neutral pill. Under the row: if downloading, a linear progress bar (0...1, accessible "<label> download") inset 16 h with 10 bottom; if the state carries a detail (failed/unavailable reason), a DSFieldMessage error line inset the same way.
  ModelReadiness titles (verbatim): notDownloaded "Not downloaded"; downloaded "Downloaded"; available "Available"; configured "Configured"; needsSetup "Not set up"; downloading "Downloading…"; verifying "Verifying…"; preparing "Preparing for first use…"; deleting "Deleting…"; ready "Ready"; unavailable "Unavailable"; failed "Needs attention". Working states: downloading, verifying, preparing, deleting. The secondary action button is disabled while working.

### 1.9 Image assets (reuse the PNGs)

All in `app/Dictate Anywhere/Assets.xcassets/`:
- `RemyWaiter.imageset/remy-waiter.png` (1145x1374): brand mascot. Used in sidebar (66pt), About (76pt), README.
- `MenuBarIcon.imageset/18x18.png`, `36x36.png`, `54x54.png` (1x/2x/3x): monochrome waiter-head glyph, flagged as a **template image** (tinted by the system). Used for the menu-bar icon AND as the small "service mark" in the overlay (tinted with the overlay accent). On Windows: use as the tray icon (convert to .ico; provide a light/dark variant since Windows has no template tinting) and tint it in WPF for the overlay mark (e.g., an OpacityMask with a solid brush).
- `AppIcon.appiconset/` 16, 32, 64, 128, 256, 512, 1024 PNG (+ "256 1", "32 1", "512 1" duplicates for @2x). Build a multi-size .ico from these.
- `AccentColor.colorset`: accent color (see table).
- No other bitmaps are used. All other icons are SF Symbols (listed per-screen below): Windows should substitute Segoe Fluent Icons or a bundled icon set; a mapping table follows.

SF Symbol -> Segoe Fluent Icons suggestion: `cpu` (E950 Processing / Chip), `slider.horizontal.3` (Equalizer E9E9), `command` (Keyboard E765), `textformat` (Font E8D2), `wand.and.stars` (Wand / Sparkle), `text.bubble` (Message E8BD), `clock.arrow.circlepath` (History E81C), `info.circle` (Info E946), `trash` (Delete E74D), `plus` (Add E710), `waveform` (Mic/Audio), `lightbulb`, `keyboard`, `doc.on.doc` (Copy E8C8), `magnifyingglass` (Search E721), `xmark` (Cancel E711), `xmark.circle.fill`, `exclamationmark.triangle.fill` (Warning E7BA), `checkmark.circle.fill`, `info.circle.fill`, `chevron.left/right`, `chevron.up.chevron.down` (ChevronUpDown), `arrow.clockwise` (Refresh E72C), `play.fill`, `arrow.down.circle.fill` (Download), `doc.on.clipboard.fill` (Paste/Clipboard), `shippingbox`, `checkmark.shield`, `arrow.counterclockwise`, `clock.badge.xmark`, `clock`, `text.book.closed` (Library/Dictionary), `speaker.fill` / `speaker.wave.3.fill` (Volume), `heart.fill`, `arrow.up.right.square` (Link).

---------------------------------------------------------------------------

## 2. Main window and navigation

Files: `MainWindow.swift`, `MainWindowSizing.swift`, `SidebarView.swift`, `App.swift`, `AppDelegate.swift` (`setupWindow` ~line 214).

### 2.1 Window rules
- Title: "Remy" (`RemyIdentity.displayName`, `CFBundleDisplayName` fallback "Remy"). Window title text is hidden; no native title bar chrome: style `.hiddenTitleBar` (content extends under the title bar; only the traffic-light buttons float at top-left over the sidebar). Windows: borderless or `WindowChrome` with hidden caption but keep caption buttons (min/max/close) at the top-right or draw custom ones; sidebar is the drag area.
- Default size 1120 x 780 (`MainWindowSizing`). Minimum 940 x 640. Maximum 2240 x 1560 (2x default). Resizable. Centered on first show (`window.center()` after a 0.1 s delay). Windows: store/restore position optional; minimum/maximum as above.
- `isMovableByWindowBackground = false` (drag only via sidebar drag area, so sliders and text-area grips aren't hijacked).
- Window background bgWindow; app tint = `accent`.
- Single window. Closing the window does NOT quit the app (`applicationShouldTerminateAfterLastWindowClosed` = false); the app keeps running in the menu bar. Reopen via tray item, Dock icon click (reopen with no visible windows shows the window), or relaunch (single instance: a second launch activates the first and exits).
- Single-instance enforcement: if another process with the same bundle id exists, activate it and quit.
- App menu (MAC-ONLY menu bar; Windows: no app menu): "Open Remy" (File>New replacement) and "Settings…" (Cmd+,) which selects General and shows the window.

### 2.2 Layout
`HStack(spacing 0)`: [Sidebar 264 wide] | [VStack: AttentionBanner (if any issue) above the detail page]. Detail pane fills the rest.

### 2.3 Sidebar (`SidebarView.swift`)
Width 264, full height, background bgSidebar, 1pt `border` line on the trailing edge, padding 16 vertical / 14 horizontal, items VStack spacing 4, whole sidebar is a window-drag handle.
Order top to bottom:
1. `DSBrand` (padding h 6, bottom 16).
2. Kicker text "SERVICE CONSOLE" (mono 9 bold, tracking 1.3, textSecondary, padding h 10, bottom 7; hidden from accessibility).
3. Nav items in this order (title / icon). Items are filtered by `isVisible`:

| # | Page enum | Title | SF Symbol | Visibility |
|---|---|---|---|---|
| 1 | models | Speech Model | cpu | always |
| 2 | settings | General | slider.horizontal.3 | always |
| 3 | shortcuts | Shortcuts | command | always |
| 4 | textOverlay | Text & Overlay | textformat | always |
| 5 | aiPostProcessing | Transcript Cleanup | wand.and.stars | hidden when effective engine is AssemblyAI (never, in shipped build) |
| 6 | internalPrompts | Internal Prompts | text.bubble | **DEAD/HIDDEN**: only when the engine supports internal-prompt customization (AssemblyAI only). Never shown in the shipped build. Also its detail route renders ModelsView. Do not implement. |
| 7 | history | History | clock.arrow.circlepath | always |
| 8 | about | About | info.circle | always |

So the visible sidebar is: Speech Model, General, Shortcuts, Text & Overlay, Transcript Cleanup, History, About (matches screenshots). Default selected page on launch: `.models` (the `AppState.selectedPage` default; except when `RemyMigrationBootstrap.needsCredentialReview` it opens `.aiPostProcessing`). Selection persists only in memory.
4. Flexible spacer.
5. `DSFooterCard` with dynamic status (see below) and "Version <CFBundleShortVersionString>" (fallback "—").

Footer status text and dot color (state machine, `SidebarView.statusText/statusColor`):
- idle:
  - cleanup draining -> "Finishing cancelled cleanup…" (dot textSecondary)
  - any non-optional attention issue -> "Setup needed" (dot accentDeep)
  - preparing speech engine -> "Preparing speech model…" (dot textSecondary, since engine not ready)
  - active engine ready -> "Ready to dictate" (dot success)
  - else -> "Loads on first dictation" (dot textSecondary)
- recording -> "Listening…" (dot accent)
- processing -> "Transcribing…" (dot accent)
- error -> "Something went wrong" (dot destructive)

### 2.4 Attention banner (`MainWindow.swift` `AttentionBanner`, `AttentionIssue.swift`)
Shown above the detail pane when `attentionIssues` is non-empty. Only ONE issue is displayed at a time, with a pager when there are several.
- Layout: HStack(12), padding 16 h / 8 v, min height 50, background accentSoft, 1pt `border` line along the bottom.
- Icon: `exclamationmark.triangle.fill` for blocking issues, `info.circle.fill` for optional issues; 13 medium accentDeep.
- Text: title (12.5 semibold) over message (12), both `panelText`; fills width.
- Action button (`dsSecondary`, label = issue.actionTitle).
- If more than one issue: a pager HStack(4): chevron.left icon button (accessible "Previous issue"), text "N of M" (11, monospaced digits, accessible "Issue N of M"), chevron.right icon button ("Next issue"); wraps around. Pager text color panelText.
- Selected issue: `selectedAttentionIssueID` (can be forced by the app, e.g., recording error), otherwise index 0. On a recovery error at appear it selects Microphone if mic not granted else Recovery.
- Accessibility focus moves to the issue text when paging.
- Auto-present: certain blocking issues (microphone, accessibility, speech setup) request the main window to be shown once per occurrence (`presentBlockingAttentionIssueOnce` -> `.requestShowMainWindow`) when a dictation attempt hits them.

Issue list, in display order (blocking first). Each: title / message / action / optional?

1. **microphone** (blocking): shown when permissions checked and mic not granted. "Microphone access needed" / "Allow microphone access to start dictating." / action "Allow Microphone" if the OS can still prompt else "Open Settings". Action: request mic access (system prompt), or open OS mic privacy settings. **Windows:** map to Settings > Privacy & security > Microphone (`ms-settings:privacy-microphone`); show action "Open Settings" (Windows apps generally can't prompt) - keep the title/message verbatim.
2. **accessibility** (blocking) **MAC-ONLY**: "Accessibility access needed" / "Allow Accessibility for shortcuts and pasting." / "Open Settings". Windows: drop (no equivalent permission); if the paste/hotkey path requires elevated rights to type into elevated apps, you may add a similarly worded issue only when needed.
3. **speechSetup** (blocking): when active engine not ready, not preparing, and (preparation error OR model not downloaded / Apple language not installed). Title "Dictation setup needed", action "Set Up" (navigates to Speech Model page). Message by engine:
   - Apple Speech: prep failed "Apple Speech could not be prepared. Review Speech Model settings." else "Set up Apple Speech to start dictating."
   - AssemblyAI: "Add an AssemblyAI API key to start dictating." (DEAD)
   - FluidAudio/Parakeet: prep failed "The speech model could not be prepared. Review Speech Model settings."; legacy-migration "Apple Speech is unavailable here. Download a FluidAudio model to keep dictating."; else "Download a speech model to start dictating."
   Windows: use the "FluidAudio/Parakeet" wording generically ("Download a speech model to start dictating." / "The speech model could not be prepared. Review Speech Model settings.").
4. **recovery** (blocking): title "Dictation recovery needs attention", message = the recovery error text, action "Dismiss" (clears the error).
5. **recordingFailed** (blocking; only if mic granted): "Recording could not start" / error text / "Dismiss". Typical error text: "Could not start the microphone. <system message>".
6. **appleSpeechUnsupported** (optional) **MAC-ONLY**: "Apple Speech is unavailable" / "Apple Speech requires macOS 26 or later. Choose another speech engine." or "Apple Speech is unavailable on this Mac. Choose another speech engine." / "Dismiss". Windows: drop.
7. **cleanup(problem)** (optional), title always "Transcript cleanup needs attention"; action "View Setup" (navigates to Transcript Cleanup) except noted:
   - fluidAudioVocabularyUnavailable: "Vocabulary correction needs Parakeet v2, v3, 110M, Ultra, or Nemotron 3.5. Choose a compatible model on the Speech Model page." action "Open Speech Model" (note: message lists models that differ from the actual compatible set; treat as engine-specific text)
   - appleIntelligenceRequiresMacOS26: "Apple Intelligence cleanup requires macOS 26 or later. Choose another method." (MAC-ONLY)
   - appleIntelligenceDeviceIneligible: "Apple Intelligence is unavailable on this Mac. Choose another cleanup method." (MAC-ONLY)
   - appleIntelligenceNotEnabled: "Enable Apple Intelligence in System Settings to use it for cleanup." action "Open Settings" (opens Apple Intelligence pane) (MAC-ONLY)
   - appleIntelligenceUnavailable: "Apple Intelligence is unavailable right now. Try again or choose another method." (MAC-ONLY)
   - s1MiniLanguageUnsupported: "S1-mini cleans up English only. Other languages are pasted without S1-mini cleanup."
   - s1MiniNotDownloaded: "Download S1-mini to use local transcript cleanup."
   - ollamaModelMissing: "Choose an Ollama model to use transcript cleanup."
   - openRouterKeyMissing: "Add an OpenRouter API key to use transcript cleanup."
   - openRouterModelMissing: "Choose an OpenRouter model to use transcript cleanup."
   - openAICompatibleModelMissing: "Choose a model on your OpenAI-compatible server to use transcript cleanup."
   - savedProfileMissing: "Create or select a saved cleanup profile. Your speech transcript is kept if cleanup is unavailable."
8. **automation** (optional) **MAC-ONLY**: "System Events access is off" / "Allow System Events for AppleScript paste. Keyboard paste still works." / "Open Settings". Windows: drop.

Which cleanup problems are raised (`AppState.cleanupAttentionProblems`, AppState.swift ~490+): mode none -> none; vocabulary -> if engine/model doesn't support vocabulary; appleIntelligence -> by availability; s1Mini -> language not English (language = Apple Speech language or selected language) and/or model not downloaded; ollama -> model empty; openRouter -> key missing / model empty; openAICompatible -> model empty; profile -> no saved profile selected.

### 2.5 Startup failure dialog
If settings cannot be read (`RemyStartup.configurationIssue` / history capture issue), before any window: modal alert, title "Remy cannot open these settings", body = issue text, single button "Quit". (`App.swift`.) Windows: equivalent MessageBox.

---------------------------------------------------------------------------

## 3. Settings pages (detail panes)

All pages use `DSPage` (scroll, content column 820) with a `DSSectionHeader` first. Controls show the setting name in code in `code`. Defaults are from `Settings.init` (Settings.swift ~1740-2000). Binding names refer to `Settings` properties.

### 3.1 General (`SettingsView.swift`)
Header: title "General"; subtitle "How Remy starts, looks, sounds, and listens."

**Section "Startup"** (card):
- InfoRow "Launch at login" - switch -> `launchAtLogin` (default off). If `launchAtLoginError` is set, a field message (error) appears under the row (padding h 16, bottom 10). Error strings: "Could not update Launch at Login. <system error>" / "Allow Remy to launch at login in System Settings." (Windows: use registry Run key; message "Could not update Launch at Login. <error>").
- Divider
- InfoRow "App appears in" - dropdown -> `appAppearanceMode`; options "Menu Bar Only" (default) / "Dock and Menu Bar". **MAC-ONLY** (Dock). Windows equivalent: "Tray Only" vs "Taskbar and Tray" (show/hide taskbar button when the window is closed; with Tray Only the process has no taskbar entry while the window is closed, but shows one while the window is open).
- Divider
- DetailRow "Prepare models ahead of time", caption "Warm selected models at launch and when cleanup is enabled. Uses more memory to avoid first-use loading." - switch -> `prewarmEnginesAtStartup` (default on). Turning on triggers preparing the active engine.

**Section "Display"**:
- DetailRow "Theme", caption "System follows your Mac's appearance setting." (Windows: "System follows your Windows appearance setting.") - dropdown -> `themeMode`: "System" (default), "Light", "Dark". Applies app-wide immediately.

**Section "Text destination"**:
- InfoRow "Send dictated text to" - dropdown -> `dictationDestination`: "Where dictation started" (default; stored `originalField`) / "Where I’m focused when it finishes" (`currentField`; apostrophe is U+2019).
- Hint (default lightbulb icon, padding h 16, bottom 14): `<destination caption> + " Changes apply to your next dictation."` where captions are:
  - originalField: "Returns to the app and field where dictation started. If that field cannot be restored, copy the result and paste it manually."
  - currentField: "Chooses your focused app and field after cleanup finishes. If that field is unavailable, copy the result and paste it manually."

**Section "Language"** - shown only when the engine != AssemblyAI (always in shipped build):
- If engine == Apple Speech (**MAC-ONLY**; Windows: use for Windows' own on-device speech if added, else drop): DetailRow "Transcription language", caption "Apple Speech downloads and uses the matching on-device language model." dropdown of Apple-supported languages (`displayWithFlag`, i.e. "<flag emoji> <English name>"; Windows can't render flag emoji, so show the name only; chinese is "Chinese (Simplified)").
- If engine == FluidAudio/Parakeet: DetailRow "Expected language", caption "Choose the expected language, then a compatible model on the Speech Model page. " + model-specific footer (see 3.2 model data). Dropdown -> `selectedLanguage` (default English "en"); options = languages that have at least one compatible catalog model on this machine; if the stored language is not in the list it is prepended so it stays visible. Disabled while recording, preparing, or downloading.
  - If the selected model has a language notice: field message under it: notice + " Review your selection on the Speech Model page." Tone warning if model supports the language (limited coverage) else error.

**Section "Input Source Switching"** (InputSourceSettingsSection; shown only when engine != AssemblyAI) - **MAC-ONLY** concept (macOS keyboard input sources / TIS). Windows equivalent: keyboard layout / input language (HKL); implement with the same UI if wanted, else drop. See 3.9.

**Section "Audio"**:
- InfoRow "Microphone" - dropdown -> `selectedMicrophoneUID` (nil = automatic). Options: first "Automatic" (or "Automatic (<device name>)" when the selected device is absent/none and the effective device name is known; plain "Automatic" if a specific available device is selected), then every input device by name. If the saved device is not currently connected, the control shows Automatic.
- Divider; InfoRow "Boost microphone volume during recording" - switch -> `boostMicrophoneVolumeEnabled` (default on).
- Divider; InfoRow "Mute system audio during recording" - switch -> `muteSystemAudioDuringRecordingEnabled` (default off).
- Divider; InfoRow "Sound effects" - switch -> `soundEffectsEnabled` (default on).
- If sound effects are on: Divider, then the **SoundEffectsSettingsView** block (3.8), Divider, then a volume row: `speaker.fill` icon (12, textSecondary) + DSSlider (0...1, label "Sound effects volume") -> `soundEffectsVolume` (Float, default 0.3) + `speaker.wave.3.fill` icon; padding 14 v / 16 h; 12 gap.

Below the cards: DSHint "Boosting raises low mic input, and muting keeps system audio out of your dictation."

Side effects: `.task(id: cleanupPreparationKey)` prepares the cleanup engine if needed.

### 3.2 Speech Model (`ModelsView.swift`, `SpeechModelMenu.swift`, `ModelReadiness.swift`)
Header: title "Speech Model"; subtitle "Speech recognition runs on your Mac. Transcript cleanup uses the method you choose separately." (Windows: "...on your PC...").

**Section "Active Engine"**: DetailRow label "Engine", caption = engine.detail, dropdown (accessible "Speech engine") -> `engineChoice`. Options (shipped): "FluidAudio" (caption "Downloadable on-device speech models powered by FluidAudio.") and "Apple Speech" (caption "Apple's latest on-device speech-to-text model, managed by macOS."; title becomes "Apple Speech (Requires macOS 26)" when the OS is too old, and selecting it when unsupported raises the appleSpeechUnsupported attention issue instead). Disabled while recording, preparing, installing Apple Speech, downloading, or deleting. **Windows:** replace options with the Windows engines; keep the DetailRow and the engine.detail caption pattern. Apple Speech is MAC-ONLY (drop).

**If engine == FluidAudio (Parakeet)**: Section "On-Device Speech":
1. DetailRow "Expected language", caption "The model menu shows choices for this language and this Mac. Multilingual models can still recognize mixed speech." dropdown -> `selectedLanguage`.
2. Divider; DetailRow "Speech model", caption = model.detail, **SpeechModelMenu** (accessible label "Speech model"): a menu with Section "Recommended for <Language>" (models recommended for that language and this hardware, excluding limited-coverage ones) and Section "Alternatives" (remaining compatible models). Each item title: "<Model name> — <purpose> · <size>" (em dash), purpose = recommendationTitle or "Limited coverage for <Language>"; the current selection has a checkmark. Button label = selected model displayName. Disabled while busy or when both lists are empty. Choosing a model rechecks disk, and triggers model-selection handling. English Nemotron presets collapse to one catalog item and keep the chosen preset.
3. Under it if `languageSupportNotice`: field message (error if unsupported language else warning), bottom padding 14.
4. Divider; InfoRow "Type" value: "On-device true streaming speech-to-text" (true-streaming models) or "On-device speech-to-text".
5. Divider; InfoRow "Underlying model" value = model.modelName.
6. If English Nemotron: DetailRow "Streaming preset", caption "Fast preview uses a smaller audio buffer. Larger buffers delay the preview and improve processing throughput. Each preset has its own download." dropdown (accessible "English Nemotron streaming preset") options: "Fast preview (560 ms)", "Balanced (1120 ms)", "Higher throughput (2240 ms)". Followed by Divider.
7. InfoRow "Languages" value languageSummary; Divider; InfoRow "Download size" value sizeSummary; optional Divider + InfoRow "Also installed" value = other installed catalog model name.
8. If the model supports end-of-utterance (Parakeet EOU only): Divider + InfoRow "Stop hands-free dictation after speech ends" switch -> `autoStopAfterSpeechEndsEnabled` (default off).
9. Divider + **Status row** (DSModelReadinessRow "Status"): readiness = deleting / downloading(progress) / preparing / ready / downloaded / notDownloaded. Action button: when notDownloaded: "Verify Existing Model" if an existing model needs activation else "Download Model"; when downloaded (not ready): "Prepare Model"; otherwise none. Disabled unless app idle and not deleting. Action: download then apply selection / prepare engine. Errors shown as error field message in the row.
10. If a model is on disk: if the model is not true-streaming: Divider + **Speech detection** DetailRow (label "Speech detection"): if installed caption "Helps recognize quiet speech and split recordings at pauses." trailing text "Installed"; else caption "Optional download for quiet speech and pause detection. Dictation works without it." trailing button "Download Speech Detection". Then Divider + InfoRow (label color textSecondary, regular weight) with text "Remove the selected preset files from this Mac." (Nemotron) / "Remove the verified copy. Original model files are kept." (Parakeet v2) / "Remove the downloaded model files from this Mac." and trailing destructive button "Delete Preset…" / "Remove Verified Copy…" / "Delete Model…" (disabled while busy). Click -> confirmation alert (below).
11. If the model needs activation: DSHint (icon `checkmark.shield`, padding 16) "Verify and copy your existing model into Remy’s versioned store. The original files are kept, and no model download is needed."
12. If an error (modelActionError or enginePreparationError): Divider + error field message (padding 10 v / 16 h).
Below the section: DSHint = "Download size is not a memory estimate. The language and model you select are kept until you change them."

Confirmation alert title "Delete <Model displayName>?" with buttons "Delete" (destructive) and "Cancel". Message: Nemotron: "This will remove the <preset title> download for <name> (<size>). Other preset downloads are kept. You can download it again later."; Parakeet v2: "This removes Remy’s verified model copy. Your original downloaded files stay on this Mac and can be verified again."; others: "This will remove the <name> speech model (<size>). You can download it again later." Busy-race error: "The selected model is busy or has changed. Review it and try deleting again."

**If engine == Apple Speech** (MAC-ONLY): Section "Apple Speech": InfoRows "Model" = "Apple Speech — managed by macOS"; "Type" = "On-device speech-to-text"; "Language" = language name; "Model storage" = "Downloaded and managed by macOS"; Status row (actions "Download Language" if not installed else "Prepare Apple Speech", shown when needsSetup); error line. Hint: "Download the selected language once, then dictate on this Mac. Starting a dictation never downloads speech assets."

**DEAD/HIDDEN (AssemblyAI)**: `assemblyAIContent` (ModelsView.swift ~234-400) is never rendered. Includes sections AssemblyAI, Dictation, Internal Prompts, Custom Vocabulary, Context Awareness, Writing Style and strings such as "Paste AssemblyAI API key". Not needed. (If a cloud engine is added later, mimic its layout.)

**Model catalog data** (for the Mac engine; Windows engines will differ; table kept for string reuse where the same models run via ONNX/other):

| Model (displayName) | Detail caption | Download size | Languages |
|---|---|---|---|
| Parakeet v3 | Balanced dictation across 25 European languages, with automatic language detection. | ~500 MB | 25 European languages |
| Parakeet Ultra | An alternative Parakeet checkpoint for 25 European languages. Results vary by language and recording; the download is larger than v3. | ~630 MB | 25 European languages |
| Parakeet v2 | Accuracy-focused English dictation. Uses Parakeet's English-only v2 checkpoint. | ~500 MB | English only |
| Parakeet 110M | A smaller English model when download size and memory use matter. | ~220 MB | English only |
| Parakeet EOU 120M | Compact live English preview, with optional automatic stop after speech ends. Trades some accuracy for size and responsiveness. | ~200 MB | English only |
| Nemotron EN 0.6B (presets 560/1120/2240 ms) | A larger English streaming alternative. The performance preset balances preview delay and processing throughput. | ~1 GB | English only |
| SenseVoice Small | Mandarin dictation with mixed English and native punctuation. Chinese output uses Simplified characters. | ~225 MB (~900 MB without Neural Engine) | Mandarin + English (automatic detection) |
| Nemotron 3.5 0.6B | Live multilingual preview on Apple Silicon. Language coverage and accuracy vary; SenseVoice is recommended for Mandarin final accuracy. | ~650 MB | 26 language presets; coverage varies |

Recommendation titles (shown in menu): Parakeet v3 "European languages"; Ultra "Multilingual alternative"; v2 "English accuracy"; 110M "Compact English"; EOU "Live English + auto-stop"; Nemotron EN "English streaming alternative"; SenseVoice "Mandarin + English"; Nemotron 3.5 "Multilingual live preview". Language-footer strings (appended to "Expected language" caption on General): multilingual/ultra: "Auto-detects among 25 supported European languages. The expected language helps guide recognition without forcing translation."; Nemotron multilingual: "Uses the expected language as a hint when the export supports it. Some language presets have limited coverage."; SenseVoice: "Automatically recognizes Mandarin and English, including mixed speech. Chinese output uses Simplified characters."; English-only models: "This model transcribes English. Choose another model for other languages." Language notice strings: unsupported "<Model> does not support <Language>. Choose another model or expected language. Apple Speech may offer additional languages on this Mac." and limited "<Language> has limited coverage in Nemotron and may require model adaptation. Parakeet v3 is the recommended choice for everyday dictation in this language."

Supported languages enum (English display names; native names exist but UI uses English with flag): English, German, Dutch, Swedish, Danish, Norwegian, Spanish, French, Italian, Portuguese, Romanian, Polish, Czech, Slovak, Slovenian, Croatian, Bulgarian, Ukrainian, Russian, Latvian, Lithuanian, Estonian, Hungarian, Finnish, Greek, Maltese, Chinese (Simplified). Codes: en de nl sv da no es fr it pt ro pl cs sk sl hr bg uk ru lv lt et hu fi el mt zh (`SupportedLanguage.swift`).

### 3.3 Shortcuts (`ShortcutsView.swift`, `ShortcutRecorderView.swift`)
Header: title "Shortcuts"; subtitle "Start and stop dictation from anywhere with a quick key press."

**Section "Keyboard Shortcuts"**: one block per binding (max 5; Divider between blocks). Each block (`HotkeyBindingRow`):
- Top row (padding 16, HStack 14): left VStack(3): name + caption by mode — Tap-to-toggle: "Toggle dictation" / "Tap once to start, tap again to stop"; Hold: "Hold to record" / "Hold the keys down while you speak". Right: shortcut recorder (below), and a trash icon button (accessible/tooltip "Remove shortcut N") only when more than one binding exists (disabled while recording).
- Error message (error tone) if validation failed: "Choose a different combination from the cancel shortcut."
- Warning messages (warning tone), zero or more: "Duplicate of another shortcut" (same key + modifiers as another binding); "Conflicts with macOS <name>" for system shortcuts: Spotlight (Cmd+Space), Finder Search (Cmd+Opt+Space), Dictation (Ctrl+Cmd modifier-only), Force Quit (Cmd+Opt+Q and Cmd+Opt+Esc), Screenshot area/selection/options (Cmd+Shift+3/4/5). **Windows:** substitute Windows-reserved combos with the string "Conflicts with Windows <name>" (e.g., Win+Space "Input switcher", Win+V "Clipboard history", Win+H "Voice typing", Win+Shift+S "Snipping", Alt+Tab, Ctrl+Alt+Del) and keep the Duplicate text.
- Divider; InfoRow "Activation mode" - dropdown (accessible "Activation mode for shortcut N") options "Hold to Record" (`holdToRecord`) / "Tap to Toggle" (`handsFreeToggle`); disabled while that row is recording. Changing restarts hotkey monitoring.
- Default bindings: one binding, modifier-only Ctrl+Opt+Cmd (display "⌃⌥⌘"), mode Hold to Record. **Windows default:** e.g., Ctrl+Alt+Win modifier-only? Choose a non-conflicting default (suggest Ctrl+Alt hold or Ctrl+Win) and show it as keycaps.
- A new binding added via "Add another shortcut": empty (keycode nil, no modifiers), mode Hold to Record.

Below the card: if fewer than 5 bindings, `DSAddButton` "Add another shortcut" (disabled while any recorder is active).

**ShortcutRecorderView** (shared widget): HStack(14), shows one of:
- Recording: text "Press any key combo…" (13 medium, accentDeep) and a secondary button "Cancel" (accessible "Cancel recording <name>").
- Empty: text "Not set" (13 medium textSecondary) and primary button "Record Shortcut" (accessible "Record <name>").
- Set: keycap group of the display name, primary button "Change" (accessible "Change <name>") and secondary "Clear" (accessible "Clear <name>").
Behavior: while one recorder is active, all other rows/buttons on the page are disabled and hotkey monitoring is paused (restored afterward). Recording captures either a key + modifiers (on keydown) or a modifier-only combination (on release of all modifiers: the largest simultaneously-held set is captured). Esc cancels recording unless `allowsEscape` (true only for the Cancel-shortcut recorder where Esc is a valid choice). Caps Lock ignored. fn/Globe normalized to a modifier-only "fn". Left/right modifier distinction is preserved (stored e.g. "R⌘"). Also stops if the view disappears. Display strings: modifier order fn, ⌃, ⌥, ⇧, ⌘ then key; key names: letters, digits, symbols, "Space", "Esc", "F1"-"F15", arrows ← → ↑ ↓, ↩ ⇥ ⌫ ⌦, "Help"; unknown codes "Key<code>". **Windows:** capture via low-level keyboard hook/RawInput; display names "Ctrl", "Alt", "Shift", "Win", with "L"/"R" prefixes for side-specific modifiers; omit fn.

**Section "Cancellation"**:
- DetailRow "Cancel shortcut", caption "Choose a shortcut you won't use in other apps. Clear it to cancel only from the menu bar." (Windows: "...from the tray menu.") with ShortcutRecorderView (accessible "Cancel shortcut", Esc allowed). Default Esc (keyCode 53, no modifiers, shown as keycap "Esc"). Validation errors (error tone under the row): "The cancel shortcut overlaps a start/stop shortcut. Choose another combination."; or the system-conflict warning text as an error.
- Divider; StackedRow "Hold to cancel", caption "Hold the full cancel shortcut for one second. Releasing early keeps dictation running." switch -> `holdToCancel` (default on). Changing restarts hotkey monitoring. Disabled while recording a shortcut.
- Divider; StackedRow "Preserve cancelled sessions", caption "Keep cancelled sessions locally for 24 hours. Continue, recover text, or delete them in History. Changes apply to new dictations." switch -> `preserveCancelledSessions` (default on).

Bottom hint (keyboard icon): "Press any key combo, or press only modifiers (like ⌃⌥⌘) and release. Left and right modifiers are supported — for example, R⌘ uses only the right Command key." (Windows: adapt glyphs, e.g. "(like Ctrl+Alt+Win)" and "R Ctrl uses only the right Ctrl key".)

### 3.4 Text & Overlay (`TextOverlayView.swift`, `OverlaySettingsPreview.swift`, `OverlaySizeControl.swift`)
Header: title "Text & Overlay"; subtitle "The floating overlay follows you while you dictate."

**Section "Overlay"** (one card, top to bottom):
1. **OverlaySettingsPreview** (fills the card width, no inner border): backdrop overlayPreviewFill, padding 24 v/h, only the top corners rounded (radius 16 top-leading/top-trailing, 0 bottom). Contains the real overlay pill rendered live (frame height = Large size's pill height so the layout doesn't jump), colored with the preview-theme setting, then a row (16 gap, 12pt font): static text "Sample preview" (textSecondary) + DSInsetButton "Replay sample" with `play.fill` icon (becomes "Playing…" and disabled while playing; tooltip "Replay a short example of dictation, processing, and completion"). Accessible label "Example dictation preview".
   Sample text: "A little more room for your train of thought, so a quick glance shows exactly what was heard." Resting state: listening with that text and level 0.
   Replay animation: state listening; words appended one every 4 ticks (tick = 70 ms) so (#words*4) ticks; level per tick = 0.12 + 0.55*|sin(tick*0.7)|; then processing state for 450 ms; then success state for 700 ms; then returns to resting listening with sample text (level 0).
2. **OverlaySizeControl** (DetailRow): label "Preview size", caption "More room and larger text for easier reading." trailing: 220-wide VStack(4): a stepped slider (0...2, step 1, tint accent; tooltip "Compact, Comfortable, or Large"; accessible value = size name) plus a row of three labels under it: "Compact" (left aligned), "Comfortable" (center), "Large" (right); selected label 11.5 semibold ink, others regular textSecondary. -> `overlaySize` (default Comfortable). Slider value <0.5 compact, <1.5 comfortable, else large.
3. Divider; DetailRow "Preview theme", caption "System follows your Mac, independently of the app theme." (Windows: "...your Windows setting...") dropdown -> `overlayThemeMode` (System default / Light / Dark). Affects both this preview and the real overlay.
4. Divider; StackedRow "Show text preview in overlay", caption "When enabled, live transcription text appears above the waveform." switch -> `showTextPreview` (default on).
Bottom hint: "Keep text preview off for a calmer, distraction-free overlay."

### 3.5 Transcript Cleanup (`AIPostProcessingView.swift`, `CleanupMethodMenu.swift`, `CleanupProfiles/CleanupProfilesView.swift`, `SettingsMultilineTextArea.swift`)
Header: title "Transcript Cleanup"; subtitle "Choose how your transcript is polished before it's pasted."

Page order top to bottom:
0. **Migration notice** (only when `RemyMigrationBootstrap.hasImportedSetup` and not dismissed): warning field message "Your Remy Dev setup was copied. If your selected cleanup setup uses an API key, enter it here, save, and test the connection. Keys stay separate between the two apps." plus bordered button "I’ve reviewed my cleanup setup" (sets dismissed). Dev-fork-specific; Windows: drop.
1. **Section "Cleanup Method"**:
   - DetailRow "Method", caption "Choose a built-in model, vocabulary correction only, or a custom model provider. Local filler-word removal below runs before the selected method." trailing **CleanupMethodMenu** (accessible "Transcript processing method"): menu with sections:
     - "On this Mac": "Off" (`none`, default for new installs), "S1-mini — English", "Apple Intelligence" (MAC-ONLY)
     - "Recognition correction" (only offered when engine isn't FluidAudio or the model supports vocabulary): "Vocabulary correction only"
     - "Custom model": "Saved Profile", "Custom: Ollama", "Custom: OpenRouter", "Custom: OpenAI-compatible"
     Button label = selected item's title; selected shows a checkmark. Stored value: `transcriptPostProcessingMode` (none/fluidAudioVocabulary/appleIntelligence/s1Mini/ollama/openRouter/openAICompatible/profile). If the stored mode is fluidAudioVocabulary but the model no longer supports it, it falls back to Off.
   - If a model identity exists: Divider + InfoRow "Model" value: Apple Intelligence "Apple Foundation Models — managed by macOS"; S1-mini "Superwhisper S1-mini — Qwen3 0.6B fine-tune"; Ollama/OpenRouter/OpenAI-compatible/Profile = trimmed model id, or "Choose a model ID below" if empty. None/vocabulary: no row.
2. **Method-specific content** (directly under the picker; see below).
3. **"Saved profiles" section** (only when the current custom setup can be saved as a profile): caption "Keep this setup as a named profile. Its endpoint, model, instructions and stored key are preserved." and secondary button "Save current setup as a profile" (padding 12). Plus profile error caption if `cleanupProfileError` (shown when not in profile mode or no selected profile).
4. **Section "Preparation"** (shown when mode is not none, S1-mini or profile): readiness row labelled "Vocabulary recognition" (vocabulary mode) or "Cleanup model", action "Prepare" (or "Retry" when there is a detail/error) when preparation is possible; Divider; caption "Ready means the local model has been prepared. Preparing downloads missing local support files before first use. Remote services show Configured; preparation checks their connection and model information." Disabled while not idle.
5. **Context Awareness** sections (3.5.6) when the method supports dictation context.
6. **Section "Local Filler Word Removal"** (3.5.7) when supported (all methods support it).

**3.5.1 Method content: Off** - Section "No Cleanup": caption "Your transcript is pasted exactly as the speech model produced it, after the local filler-word removal below. Pick another method to turn on AI cleanup."

**3.5.2 Vocabulary correction only** - If the engine/model doesn't support it: Section "Vocabulary Correction" InfoRow "Status" value "Requires Parakeet TDT or multilingual Nemotron". Else the Custom Vocabulary section (3.5.8) with footer: Nemotron multilingual: "These terms guide multilingual Nemotron while it recognizes speech, including live previews. Keep the list short and domain-specific for best precision." else "These terms are applied by FluidAudio's vocabulary rescoring on Parakeet TDT final transcripts only. Keep the list short and domain-specific for best precision." (engine-specific; Windows: adapt or drop)

**3.5.3 Apple Intelligence** (MAC-ONLY; Windows: drop, or substitute Windows AI/Phi Silica/Copilot+ local model if available). OS < 26: Section "Apple Intelligence" InfoRow "Status" value "Requires macOS 26". Else by availability: available -> Section "Prompt" (3.5.5; label "Apple Intelligence prompt", default = recommended Apple prompt) + Custom Vocabulary (footer "These terms are applied only by Apple Intelligence post-processing to preserve product names, names, and domain-specific wording."); device ineligible -> InfoRow Status "Unavailable on this Mac"; not enabled -> Status row with neutral pill "Not enabled" + primary button "Open Settings"; model not ready -> loading message "Apple Intelligence model is downloading…" and caption "The on-device model is being prepared. This may take a few minutes."; other -> Status "Temporarily unavailable".

**3.5.4 S1-mini** (local model; llama.cpp/Metal on Mac; the Windows team decides runtime, UI stays): Section "S1-mini by Superwhisper":
- InfoRow "Language" = "English cleanup"; Divider; InfoRow "Model" = "462 MB · English · Local transcript normalizer"; Divider; readiness row (actions "Download Model" when not downloaded and idle; "Prepare Model" / "Retry" otherwise when preparable; disabled when busy or app not idle). States include downloading (progress), verifying, deleting.
- Caption "Downloaded means the files are installed. Ready means the model has been loaded and prepared for cleanup."
- While downloading, caption: "<N>% · <downloaded> of <total> MB" (progress is scaled so 100% of the visible bar = install fraction), switching to "Verifying model integrity…" when download finishes.
- Error field message (S1 action error or manager error).
- If downloaded: Divider + InfoRow (label color textSecondary): "Remove the downloaded model files from this Mac." + destructive button "Delete Model…" (shows "Deleting…" while deleting). Confirmation alert: title "Delete S1-mini by Superwhisper?", message "This removes the local model and its downloaded license file. You can download them again later.", buttons "Delete"/"Cancel".
- Divider; padded block: caption "Downloads a pinned, integrity-checked copy directly from Hugging Face. Once installed, transcript cleanup runs entirely on this Mac and does not send transcript text to a server. Apple Silicon uses Metal acceleration; Intel uses CPU inference and will be slower." + link (12.5 medium accentDeep) "View the model and its license on Hugging Face" -> https://huggingface.co/superwhisper/s1-mini.
- Section "Cleanup Controls" (S1-mini trained controls): 
  - If context awareness is off: DetailRow "Styling", caption "Controls how casual or formal the normalized transcript should sound." dropdown (accessible "S1-mini styling") -> `s1MiniStyling` options Casual, Semi-Casual, Semi-Formal (default), Formal; then Divider.
  - DetailRow "Structure", caption "Choose prose paragraphs or a list-oriented result." options "Prose" (default), "Lists" -> `s1MiniStructure`.
  - Divider; DetailRow "Context", caption "Automatic uses email formatting for email destinations and general formatting elsewhere." options "Automatic" (default), "General", "Email" -> `s1MiniContextSetting`.
  - Divider; caption "S1-mini uses only its fixed, trained controls—not an arbitrary prompt or custom vocabulary. Local filler-word removal still runs first."

**3.5.5 Prompt section** (shared by Ollama/OpenRouter/OpenAI-compatible/Apple): Section "Prompt": padded block with header row: label "Cleanup instructions" (13.5 medium) left, secondary button "Reset to Default" right (disabled when text equals default; accessible "Reset <label> to default"); beneath it **SettingsMultilineTextArea** (placeholder "Enter cleanup instructions."). Defaults: Apple prompt `Settings.recommendedAppleIntelligenceCleanupPrompt`; others `Settings.recommendedTranscriptCleanupPrompt` (full text in Settings.swift lines ~950-975; owned by the cleanup spec). Bindings: `aiPostProcessingPrompt`, `ollamaPostProcessingPrompt`, `openRouterPostProcessingPrompt`, `openAICompatiblePostProcessingPrompt`.

**SettingsMultilineTextArea** (reusable control): rounded rect radius 12 (continuous), background vertical gradient from system text-background to control-background (Windows: use bgCard flat or a very subtle gradient), plain text editing (no rich text, undo supported), vertical scrollbar autohiding, insets 8h/6v + 4/7 text inset, font = system 13. Default height (min) 80, max 240 (prompt editors for Internal Prompts used 110-150; dead). Bottom-right resize grip (drag changes height within [min,max]; keyboard focusable grip: up/down arrows +/-20pt; accessible "<N> points"). Placeholder (tertiary color) when empty at inset 14/11. Border: 1pt, focused = accent @80% alpha 1.5pt, unfocused = primary @12% (light) / 24% (dark); inner 0.5pt white highlight (42% light / 8% dark); shadow black 6%/22% radius 4 (focused 8) y 1 (2). Animations 0.16 s ease-out on focus/height change.

**3.5.6 Context Awareness** (shown when `RemyFeaturePolicy.allowsDictationContextGathering` (true) and the method supports `dictationContext`: Apple Intelligence, S1-mini, Ollama, OpenRouter, OpenAI-compatible, Profile; not Off or Vocabulary):
- Section "Context Awareness": StackedRow "Adapt to the current app and text field", caption by support level, switch -> `dictationContextAwarenessEnabled` (default ON):
  - categoryOnlyOnDevice (S1-mini): "Detects the app category and cursor position locally. S1-mini receives only its trained email/general context value; surrounding text stays outside the model. Password fields and excluded apps are never read."
  - fullOnDevice (Apple Intelligence): "Reads a bounded snapshot around the cursor, classifies the destination, and applies its writing style entirely on this Mac. Password fields and excluded apps are never read."
  - fullLocalServer (Ollama/OpenAI-compatible/profile pointing at localhost): "Reads a bounded snapshot around the cursor and supplies it to the configured local server. Password fields and excluded apps are never read."
  - remoteCategoryWithOptionalText (OpenRouter, remote Ollama/OpenAI-compatible/profile): "Sends the app name, destination category and writing style with your transcript. Window titles, page URLs and surrounding text are shared only when enabled below. Password fields and excluded apps are never read."
  When enabled: if remote-capable: Divider + StackedRow "Share surrounding text with <mode display name>" (e.g., "OpenRouter", "Ollama", "OpenAI Compatible", "Saved Profile"), caption "Off by default. App name, category and writing style are sent while context awareness is on. Enable this to also send window titles, page URLs and the text around your cursor to the selected provider." -> `shareDictationContextWithRemoteProviders` (default off). Else Divider + DSCardCaption describing delivery: S1-mini "S1-mini by Superwhisper receives only the destination category, so its Automatic context control can choose email or general formatting. The text around your cursor is never passed to the model, and nothing leaves this Mac."; Apple "Apple Intelligence runs on this Mac, so anything read around your cursor stays on this device."; local server "The configured <Mode> endpoint is on this Mac, so requests include surrounding text automatically. This assumes the server processes the selected model locally. Remote endpoints use the separate sharing toggle."
  Mode display names: "S1-mini by Superwhisper", "Ollama", "OpenRouter", "OpenAI Compatible", "Saved Profile".
- When context awareness is on:
  - Section "Writing Style" (modes with category writing styles: Apple Intelligence, Ollama, OpenRouter, OpenAI-compatible, Profile): one DetailRow per category (then Divider each), then a caption: "<Mode name> receives the app name, destination category and selected style. Terminal and AI destinations preserve technical terms and prompt wording." Each row: label = category name; caption = category caption; dropdown of styles (accessible "Writing style for <category>").
    Categories (order) with caption and default style:
    - Email: "Used in mail apps and webmail." default Formal
    - Work messaging: "Used in Slack, Teams, Discord, and similar work chat." default Formal
    - Personal messaging: "Used in Messages, WhatsApp, Telegram, and similar personal chat." default Casual
    - Terminal: "Shell input; preserves commands, paths, flags and literal punctuation." default Original tone
    - Terminal coding agent: "Claude Code, Codex and similar terminal agents; preserves natural-language requests and technical terms." default Original tone
    - Browser: "General browser fields; known email, messaging and AI sites use their own categories." default Neutral
    - Coding agent: "Codex, Cursor and similar coding apps; preserves code references and requests." default Original tone
    - AI chat: "ChatGPT, Claude, Gemini and similar AI chat apps and sites." default Original tone
    - Other: "Used when no specific app or website category matches." default Formal
    Style options: Personal messaging: Formal, Neutral, Casual, Very Casual, Original tone. All other categories: Formal, Neutral, Casual, Excited, Original tone. (An out-of-range stored style is coerced to Casual.)
  - Section "S1-mini App Styling" (S1-mini only): same category rows (label, same captions) each with dropdown of S1-mini styles (Casual, Semi-Casual, Semi-Formal, Formal; accessible "S1-mini style for <category>"), Divider each; then DetailRow "Fallback", caption "Used only when no destination app can be captured." (accessible "S1-mini fallback style") -> `s1MiniStyling`; Divider; caption "Every option shown here is one of S1-mini's trained Styling values. Remy selects the value locally from the destination category without changing the model prompt format."
  - Section "App Categories" (all context-capable methods): caption — S1-mini: "Common email and messaging apps and sites are categorized automatically. Categories select only S1-mini's trained styling values, and its Automatic context maps email to Email and every other category to General. Add an override to recategorize an app or stop reading its field context."; others: "Email, messaging, browsers, terminals, coding agents and AI chat apps such as Claude and ChatGPT are categorized automatically. Claude Code or Codex in a terminal needs a recognizable window title; otherwise the Terminal category applies. Add an app override to choose a category or exclude its context." Then per-app override rows (Divider between): VStack(10): [app name (13.5 medium), bundle identifier (11.5 textSecondary, single line)], then HStack(12): category dropdown (accessible "Category for <app>") ... spacer ... text "Read field context" (13.5 medium) + switch (accessible "Read field context for <app>") + destructive-tinted trash icon button (accessible "Remove <app> override"). Last: padded block with a menu-button (secondary style) "Add Running App" with `plus` icon: lists running regular GUI apps (excluding Remy itself and apps already overridden) sorted by name; empty list shows disabled "No other running apps". Choosing an app appends an override with the auto-detected category and `contextEnabled = true`. **Windows:** use process name / executable path (and optionally AppUserModelID) in place of bundle identifier; list top-level windowed processes.

**3.5.7 Local Filler Word Removal**: Section "Local Filler Word Removal": StackedRow "Remove selected filler words" -> `isFillerWordRemovalEnabled` (default OFF); caption: for true-streaming models "Runs locally on this Mac before any AI processing — no AI involved. Helpful for streaming models that transcribe filler words literally." else "Runs locally on this Mac before any AI processing — no AI involved. It removes only the editable words listed here." When on: Divider, padded block: row with "Words:" (12.5 medium textSecondary) then a flow layout (6 gap) of removable chips for `fillerWordsToRemove` (default list: um, uh, erm, er, hmm, 嗯, 呃); below: HStack(8): text field "Add word…" (width 220; Enter submits; accessible "Add filler word"), secondary "Add" (disabled unless trimmed lowercase non-empty and not already present), spacer, secondary "Reset to Defaults" (disabled when equal to defaults; accessible "Reset filler words to defaults"). New words are trimmed and lowercased.

**3.5.8 Custom Vocabulary section** (`VocabularyInput.swift`; also used elsewhere): Section "Custom Vocabulary": padded block (padding 12 v/16 h, 10 spacing): if terms exist, a flow layout of removable chips (remove animates 0.2 s ease-out unless reduce motion); HStack(8): DSTextField "Add word or phrase…" (width 260, Enter submits, accessible "Add custom vocabulary word or phrase"), secondary button "Add" (disabled when parse yields nothing; accessible "Add custom vocabulary"). Divider; footer caption (varies by method; see strings in 3.5.2-3.5.9). Parsing: input split on `,` `，` `、` and newlines, each trimmed, empties and duplicates (exact match, including already-present terms) dropped; multiple terms can be added in one entry. Terms stored in `customVocabulary` (default empty; shown order = insertion). The README says up to 100 terms are sent for cloud; no hard cap in the UI.
Footers: Ollama "These terms are sent to Ollama to preserve product names, names, and domain-specific wording during post-processing."; OpenRouter "These terms are sent to OpenRouter to preserve product names, names, and domain-specific wording during post-processing."; OpenAI-compatible "These terms are sent to the OpenAI-compatible server to preserve product names, names, and domain-specific wording during post-processing."; Profile "These hints are sent to the selected profile's server with your transcript."

**3.5.9 Ollama** - Section "Ollama":
- If the `ollama` CLI isn't found: first an install block (padding 12/16): HStack(12): `shippingbox` icon (accentDeep), VStack(3): "Not installed" (13.5 semibold) / "Local models · Free · Runs on this Mac" (12.5 secondary); spacer; primary button with `arrow.down.circle.fill` "Download Ollama" -> https://ollama.com/download. Below: caption "Install Ollama to run your own cleanup model. Choose an installed model by its exact ID. You can also leave it uninstalled and point the server URL below at a remote Ollama server." Divider.
- `fieldRow` (label left 13.5 medium; right field width 320, padding 10 v/16 h): "Server URL" (placeholder `http://127.0.0.1:11434`, accessible "Ollama server URL") -> `ollamaBaseURL`; Divider; "Model ID" (placeholder "Enter an installed model name", accessible "Ollama model") -> `ollamaModel` (default empty).
- Divider; padded block: HStack(12): status message on the left; right secondary button "Refresh Models" (text becomes "Checking…" and disabled while checking). Status message (see table below). If models are installed: label "Installed Models" (12 semibold secondary) and a flow layout (6 gap) of selectable chips (tap selects -> sets Model ID; selected chip = filled accentDeep); each chip has a remove "x" (only when the CLI exists and no deletion in progress) which opens confirmation "Delete Ollama Model?" / "This will remove <model> from the configured Ollama server." with "Delete" (destructive) / "Cancel". A chip being deleted is disabled.
- If `ollamaModelActionError`: Divider + error message.
- Divider; caption "Ollama is the provider; the Model ID above selects the cleanup model. Use an installed model trained to preserve names, numbers and meaning. The server URL determines where processing runs."
- Optional Section "Cleanup Reasoning" (only when the selected model supports reasoning): DetailRow "Allow reasoning" with switch (accessible "Allow Ollama cleanup reasoning") -> `ollamaReasoningEnabled`; if the model can't turn reasoning off the switch is forced on and disabled with caption "This model cannot turn reasoning off. Its model/server recommendation is preserved."; else caption "On preserves the model/server recommendation. Off can reduce wait time, but may affect corrections."
- Prompt section (3.5.5) + Custom Vocabulary.
Ollama status messages (field message; tone): checking -> loading "Checking Ollama…"; error -> error tone with the thrown error text; connected and zero models -> warning "Connected, but no Ollama models are installed yet."; connected, no selected model -> success "Connected. Choose an installed model below or enter one manually."; selected model installed -> success "Connected. <resolved model> is available."; selected not installed -> warning "Connected, but <model> is not installed on this Ollama server."; not checked -> info "Enter your Ollama server URL to check connectivity." Availability refresh runs automatically (350 ms debounce) when mode/URL/model/credentials change.

**3.5.10 OpenRouter** - Section "OpenRouter":
- fieldRow "API key" (secure field, placeholder "Paste OpenRouter API key", accessible "OpenRouter API key") -> `openRouterAPIKey` (stored in OS keychain; **Windows:** Windows Credential Manager / DPAPI). Error under it if `openRouterAPIKeyError`.
- Divider; fieldRow "API key variable (optional)" (placeholder `OPENROUTER_API_KEY`) -> `openRouterAPIKeyEnvironmentVariable`.
- Divider; fieldRow "Model ID" (placeholder "openai/gpt-5-mini") -> `openRouterModel` (default empty).
- Divider; padded block: left = status message; right = VStack(8, trailing): secondary "Refresh Models" ("Checking…"), secondary "Browse Models" (opens https://openrouter.ai/models), destructive "Clear Stored Key" (only when a key is entered).
- Divider; caption "Runs transcript cleanup through OpenRouter's cloud API. Paste a key directly to store it in Keychain, or leave the API key field blank and use the optional environment variable setting instead." (Windows: "Credential Manager").
- When a model catalog was fetched: Section "Model Search": up to 12 matches for the typed Model ID (sorted: exact, prefix, structured-output support, alphabetical), as plain-button rows (Divider between): model id (13) with sub-lines "Prompt-only fallback" (12 secondary, when no structured outputs) and "Audio input available" (when supported); trailing "Selected" (12 semibold accentDeep) on the current one. Empty: caption "Type part of a model id to filter the fetched OpenRouter catalog, or use Browse Models to pick one from openrouter.ai." or, if a query exists, "No fetched OpenRouter models matched <query>. Try a broader search term or browse the full model directory." Divider + caption "Fetched <N> OpenRouter models. <S> currently advertise structured output support and <A> advertise audio input support."
- Optional "Cleanup Reasoning" section: appears when the model can disable reasoning or has mandatory reasoning: DetailRow "Allow reasoning", caption mandatory: "This model requires reasoning, so it cannot be turned off." else "On preserves the provider's recommended reasoning setting and effort. Off can reduce wait time, but may affect corrections." switch (accessible "Allow OpenRouter cleanup reasoning") -> `openRouterReasoningEnabled` (default on); forced on/disabled when mandatory.
- Prompt + Custom Vocabulary.
Status messages: checking -> "Checking OpenRouter…"; fetch error -> error text; no key -> warning "No OpenRouter API key is configured yet. Paste one above or set <ENV_NAME> in the app environment."; matched model -> "<credential source sentence> <model reference>, which advertises both structured output and audio input support." / "... advertises structured output support." / "... advertises audio input support, but it does not advertise structured outputs. Remy will fall back to prompt-based JSON parsing if needed." / "..., but it does not advertise structured outputs. Remy will fall back to prompt-based JSON parsing if needed." (success when structured outputs supported else warning), where the model reference is "<id> is available on OpenRouter" or "<selected> is valid on OpenRouter and uses the <id> catalog entry" (dynamic variant suffix); model typed but not in catalog -> warning "<credential sentence> <model> was not found in the latest OpenRouter model refresh."; catalog loaded, no model -> success "<credential sentence> Enter a model id above or search the fetched catalog below."; else info "Refresh models to validate your OpenRouter setup and search the available catalog." Credential sentences: "OpenRouter API key is stored securely in Keychain." / "OpenRouter API key was pasted directly into the environment variable field." / "OpenRouter API key was loaded from <ENV_NAME>." / "No OpenRouter API key is configured."

**3.5.11 OpenAI Compatible** - Section "OpenAI Compatible":
- fieldRow "Server URL" (placeholder `http://127.0.0.1:1234/v1`, accessible "OpenAI-compatible server URL") -> `openAICompatibleBaseURL`; Divider; fieldRow "API key (optional)" (secure, placeholder "Leave blank for local servers without auth") -> `openAICompatibleAPIKey` + error line; Divider; fieldRow "Model ID" (placeholder "local-model") -> `openAICompatibleModel`.
- Divider; padded block: status message left; right VStack: secondary "Refresh Models" ("Checking…"), destructive "Clear Stored Key" (if key present). If models reported: label "Available Models" + selectable chips (case-insensitive match highlights selection; no remove).
- Divider; caption "Runs transcript cleanup through your selected OpenAI-compatible server. Remote servers receive the transcript and vocabulary hints. Use the base URL and model name reported by that server."
- Prompt + Custom Vocabulary.
Status: checking "Checking server…"; error text; zero models warning "Connected, but the server did not report any models."; no model "Connected. Choose a model below or enter one manually." (success); model available "Connected. <model> is available." (success); not listed warning "Connected, but <model> was not listed by this server."; idle info "Enter a server URL and refresh models to check connectivity."

**3.5.12 Saved Profile** (`CleanupProfilesView`): own header inside the page ("Cleanup profiles" title using the same DSSectionHeader; subtitle "Give each service its own endpoint, model and instructions."), then a card (padding 18, VStack 14):
- Row: native Picker "Saved profile" (first item "Unsaved profile", then each profile name) + secondary buttons "New" and "Reload". Caption (12) "Switching profiles, New and Reload discard unsaved changes."
- Labeled fields (label 13 semibold above a DSTextField): "Profile name" (default "New profile"), Picker "Protocol": "Ollama" / "OpenAI-compatible" (default OpenAI-compatible), "Base URL" (default `https://ollama.com/v1`; tooltip "Enter the exact service URL. Remy does not add /v1. Remote endpoints require HTTPS."), "Model ID" (tooltip "Use the exact model identifier accepted by your service.").
- Caption "API key: configured" / "API key: not configured"; secure field "New API key (optional)" (tooltip "Stored keys are never displayed. Leave this empty to preserve the current endpoint's key reference."); if the saved profile has a key: toggle "Remove key from this profile on Save"; if base URL changed: caption "Changing the endpoint clears its saved key reference. Enter a new key if required."
- "Cleanup instructions" (13 semibold) + a plain multi-line editor (min height 140, 1pt border radius 5) defaulting to the recommended prompt.
- Row "Request timeout" + stepper "<N> seconds" (1-120, step 1, default 30).
- Route caption: localhost/127.0.0.1/::1 -> "Local execution has not been verified. A server on this Mac may still use a cloud model."; else "This server receives your transcript and vocabulary for cleanup."
- Buttons: primary "Save profile"; secondary "Test connection" (label "Checking…" while running; disabled unless draft unchanged from saved, tooltip "Checks only the saved profile using a synthetic input. Save changes first."); spacer; destructive "Delete…" (if a saved profile is loaded). Delete confirmation dialog: title "Delete this cleanup profile?", buttons "Delete profile" (destructive) / "Cancel", message "The profile will be removed. Its stored key is not deleted."
- Result/status messages (12 secondary): "Profile saved."; "Connection check passed."; "Connection check failed. Check the saved endpoint, model and key."; "The saved profile check finished. Save your changes before testing this draft."; "Profiles could not be reloaded. Your draft has been kept."; "Profiles could not be loaded. No changes were saved."; "The save could not be confirmed. Reload profiles before making another change."; "The profile could not be saved. Check its fields, or reload if it changed elsewhere."; "The profile could not be deleted. Reload profiles and try again."; "The profile could not be selected. Reload profiles and try again."
- Then Custom Vocabulary section (footer for Profile above). If profiles are unavailable: Section "Saved profiles" caption "Profiles are unavailable. Check that Remy's Application Support folder is writable, then reopen Remy." (Windows: "...the app data folder (%APPDATA%\Remy)...")

### 3.6 History (`TranscriptHistoryView.swift`)
Page spacing 20. Header: title "History"; subtitle "Your recent dictations, kept on this Mac." (Windows: "...on this PC.")

**Section "History storage"** (card; each item padded 16):
- Hint (icon `waveform`): "With history on, a temporary audio copy protects interrupted or failed dictations. Successful recordings are removed; recovery audio expires after 24 hours."
- DetailRow "Save new dictations", caption "Changes apply to your next recording. Turning this off keeps existing history and disables recovery audio for new sessions." switch -> `historyEnabled` (default on).
- If off: Hint (icon `clock.badge.xmark`) "History off. New transcripts stay in memory for delivery and cannot be recovered after Remy quits."
- If `historyNotSaved`: warning message "Some history changes were not saved. Copy any unsaved text you need before quitting Remy. A paste that was already sent is never automatically retried."
- If `storedHistoryDeletionError`: warning with that text.
- If a clear/deletion is pending: warning "Deleted history is hidden for this session, but its deletion from disk is not confirmed. It may return after Remy restarts." + secondary button "Retry deletion from disk" ("Retrying deletion…" while running; tooltip "Retries the earlier deletion. Dictations saved after Clear All are kept.").
- If preparing: Hint (icon `clock`) "Preparing your history…"

**Search/clear row**: HStack(10): DSSearchField (placeholder "Search your dictations", accessible "Search transcript history"; filters entries whose text OR raw text contains the query, case-insensitive; query trimmed) + destructive "Clear All…" (disabled while preparing/deleting history or while recovering/continuing a session). Click -> alert "Clear all stored history?" buttons "Clear All" (destructive)/"Cancel", message "Delete this app's saved transcripts, recovery recordings and history rollback backups. This removes history rollback options. Models, settings and API keys are kept. Copies you exported, the clipboard and system backups are not cleared. This cannot be undone."

**Section "Recovery sessions"** (shown only if cancelled sessions exist, filtered by search against preview or the phrase "Cancelled dictation"): top Hint (icon `arrow.counterclockwise`, padding 12 v/16 h) "Continue restores your words and resumes recording. Stop finishes the combined dictation; if the original app is unavailable, the text is copied. Recover text saves it here without pasting. Sessions expire after 24 hours." Then rows (Divider before each), padding 16, HStack(16): left VStack(5): "Saved for recovery · <timestamp>" (12 semibold ink), preview text (13 secondary, max 3 lines; empty -> "Audio saved for recovery"), "<N> sec · Expires in <relative time>" (11.5 secondary), optional warning "Partial recovery copy — some audio may be unavailable." Right VStack(8): primary button [Continue | Preparing… | Stop | Finishing…] and secondary [Recover text | Recovering…], then trash icon button (accessible "Delete cancelled session from <timestamp>"; disabled during recover/continue). Continue becomes "Stop" while its continued dictation is recording (and acts as stop). Buttons disabled unless app idle (except the Stop).

**Entries card**: newest first. Empty state (inside card, padding 20): icon `clock.arrow.circlepath`; title "No transcripts" with message "Completed dictations will appear here."; with a query: title "No matches", message "No dictations match “<query>”." (curly quotes). If both entries and recovery are empty, show this; if only recovery sessions exist, no empty card.
Each row (padding 14 v/16 h, HStack 16):
- Left VStack(5): timestamp (abbreviated date + short time in the user's locale, e.g. "Nov 7, 2026 at 11:59 AM"); optional warning field message (from processing session: "AI cleanup wasn't applied. Raw transcript used." if fallback; else "Processing was interrupted. Review the details below." if interrupted); the transcript text (13.5, selectable, wraps); optional disclosure "Raw transcript" (12 medium) which expands to raw text (13 secondary, selectable) plus inset button "Copy raw" (icon doc.on.doc; becomes "Copied raw" for 1.5 s); disclosure "Processing details" (lazy-loads).
- Right: inset button "Copy" (becomes "Copied" for 1.5 s; copies the final text to the clipboard) and trash icon button (tooltip "Delete transcript"; accessible "Delete transcript from <timestamp>"). Disclosure triangles use the platform default expander.
**Processing details** (expanded): metadata lines "Label: value" (label medium weight; 12 secondary, selectable): Status [Transcription saved; processing not completed | Processing completed | Processing interrupted]; Selected text [Raw transcript selected | AI pass output selected | Raw transcript selected instead of AI cleanup | Imported transcript; original processing details unavailable | Recovered text selected]; Transcription engine; Transcription model; Language; Finished (abbreviated date + time with seconds). Loading: spinner + "Loading processing details…"; failure: warning "Processing details couldn't be loaded. Retry to read this session's log." + secondary "Retry loading details". Loaded: for each AI attempt: Divider; heading "AI pass <n> · <Succeeded|Failed|Cancelled|Skipped by you>" (12 semibold) plus a small "Selected" badge (11 semibold successText on successSoft, radius 4, padding 6x2) if it is the chosen output; failed attempts show a warning with the failure message; metadata: Provider, Model, Started, Recorded, Duration (ms under 1 s, else "x.xx s"; "Not recorded" if nil), then if profile data: Cleanup mode, Reasoning (On/Off), App context setting (On/Off), and for S1-mini: S1 style, S1 structure. Text disclosures: "AI input"; "AI output" (or text "No AI output recorded."); "Saved cleanup instructions" with caption "Generated formatting instructions and app context aren't fully captured in this log." (or text "AI instructions not recorded."). If no attempts: "No AI pass recorded. Older records may not include processing details." Then Divider, heading "Delivery" (12 semibold); with no deliveries: "No delivery result recorded. Older records may not include delivery details."; each delivery: outcome [Delivery intent recorded; no result recorded | Copied to clipboard | Paste sent; insertion not verified | Insertion verified | Paste may have been sent; insertion uncertain | Delivery failed], "Started: <time>", disclosure "Delivery text". Empty text shows "(Empty)". Footer inset button "Refresh details" (icon arrow.clockwise; tooltip "Reload processing and delivery results for this dictation.").
See `docs/images/remy-history.png` for the layout.

### 3.7 About (`AboutView.swift`)
No section header. Layout: hero (centered, padding 28 v/24 h, 14 gap): brand mark 76; app name "Remy" (display 30); "Version <short> (<build>)" (13 secondary; fallback "–"); line "Based on Dictate Anywhere by" (13 secondary) + link "Pixel Forty Inc." (13 semibold accentDeep) -> https://pixelforty.com. Then caption "In-app update checks are disabled. Install development builds manually." (Windows: decide on an update story; if none, same sentence). Then a link: if bundled notices exist "Open bundled license notices" (13) -> `OpenSourceNotices` resource; else "View complete third-party notices" -> https://github.com/brysonjl/remy/blob/main/THIRD_PARTY_NOTICES.md.
Section "Open Source Acknowledgements" (card; LibraryRow separated by dividers; each row padding 14 v/16 h: left VStack(4): name 13.5 semibold; description 12.5 secondary; link with `arrow.up.right.square` icon (10.5) + URL without "https://" (12.5 medium accentDeep; accessible "<name> project website"); right: license chip):
1. "Dictate Anywhere" - https://github.com/hoomanaskari/mac-dictate-anywhere - "MIT License" - "Original application by Hooman Askari. Copyright (c) 2026 Hooman Askari. Remy retains the upstream MIT license and notices."
2. "FluidAudio" - https://github.com/FluidInference/FluidAudio - "Apache License 2.0" - "On-device speech recognition powered by FluidAudio models."
3. "Parakeet v2 by NVIDIA / FluidInference" - https://huggingface.co/FluidInference/parakeet-tdt-0.6b-v2-coreml/tree/ee09c569f73759e6d44c9bd16766f477b2b36d39 - "CC-BY-4.0" - "NVIDIA Parakeet TDT 0.6B v2, converted to CoreML by FluidInference. Remy downloads the pinned conversion separately; the complete notices include its model card and license link."
4. "S1-mini by Superwhisper" - https://huggingface.co/superwhisper/s1-mini - "Apache 2.0 + naming term" - "Optional on-device model for English transcript normalization. Model files and license are downloaded separately at the user's request."
5. "llama.cpp / llama.swift" - https://github.com/mattt/llama.swift - "MIT License" - "Local inference runtime used by S1-mini by Superwhisper."
6. "Ollama" - https://github.com/ollama/ollama - "MIT License" - "Local LLM runtime used for optional transcript post-processing."
7. "Sparkle" - https://github.com/sparkle-project/Sparkle - "MIT License" - "Inherited macOS update framework dependency. Update checks are disabled in Remy." (MAC-ONLY; drop on Windows, and add Windows-side dependencies + fonts (Inter OFL) as rows as appropriate; the Windows build should add its own engine/runtime attributions)
Footer line (centered): `heart.fill` icon (12, accentDeep) + "Built with the help of the open-source community. Thank you to all contributors." (12.5 secondary).

### 3.8 Sound effects block (`SoundEffectsSettingsView.swift`, `RemySoundEffects.swift`) - embedded in General > Audio when Sound effects is on
Two rows: "Recording started" (event start; default sound "Purr", identifier `builtin:Purr`) and "Dictation completed" (event completion; default "Pop", `builtin:Pop`). Divider between. Each row: InfoRow with a native Picker (max width 190, accessible "<title> sound"): options "None" (`none`), then the built-in names, then (if the current value is a custom sound) one extra item with label "Custom audio" (or "Unavailable sound" when the custom file is missing/invalid). Under it (padding h 16, bottom 14) HStack(10): secondary "Import audio…" (accessible "Import <title lowercased> sound"; disabled while importing), secondary "Preview" (accessible "Preview <title lowercased> sound"; disabled while importing or when "None"; plays at the current volume; if the sound is unavailable shows message "That sound is unavailable. Remy used the default sound instead."), small spinner when importing (accessible "Importing audio").
Import opens a file chooser titled "Choose a short sound effect" for audio files (single file); on success sets the picker to the imported id and shows "Sound imported. Use Preview to listen."; on failure "Audio could not be imported. Choose a supported local clip up to 10 seconds and 10 MiB." Footer caption (always): "Import a local WAV, AIFF, CAF, MP3 or M4A clip, up to 10 seconds and 10 MiB. Remy keeps its own copy; your original stays unchanged." (Windows: say "WAV, MP3 or M4A" if AIFF/CAF are not supported; keep the limits 10 s / 10 MiB.) The message caption (if any) shows above the footer caption.
Built-in sound names (macOS system sounds in /System/Library/Sounds, **MAC-ONLY assets**): Tink, Pop, Purr, Ping, Glass, Hero, Submarine, Funk, Basso, Bottle, Blow, Frog, Morse, Sosumi. Windows: not available; ship a small set of royalty-free clips with these names (or Windows media sounds), default start "Purr"-like soft cue and completion "Pop"-like. Stored identifiers: `none`, `builtin:<Name>`, `custom:<uuid>.<ext>` (ext in wav aiff aif caf mp3 m4a; identifier length <= 64 bytes). Volume default 0.3.

### 3.9 Input Source Switching (`InputSourceSettingsSection.swift`) - MAC-ONLY
Section "Input Source Switching": StackedRow "Match speech model to keyboard input source", caption "When you change keyboard input sources, apply the mapped engine, model, and language automatically." -> `inputSourceAutoSwitchEnabled` (default off). When on: one mapping block per mapping (Divider before each): InfoRow "Input source" (dropdown of enabled input sources not taken by other mappings; a no-longer-enabled source shows "<cached name> (not enabled)") + trash icon button (accessible "Delete mapping for <name>"); InfoRow "Engine" (options FluidAudio, plus Apple Speech when supported); if FluidAudio: InfoRow "Model" (catalog models available on this machine; titles get suffix " (not downloaded)" or " (not available on this Mac)"); if English Nemotron: InfoRow "Streaming preset"; InfoRow "Language" (flag+name dropdown, or static text for fixed-language models: "English" / "Chinese + English (auto-detected)"); optional warning (inactive reason). Outside the card: if any source remains unmapped, `DSAddButton` "Add mapping" (adds the first unmapped source with a derived language). Enabling the switch immediately applies the mapping for the current input source. Windows suggestion: same UI keyed by keyboard layout/language (GetKeyboardLayout), or drop for v1.

### 3.10 Quick Vocabulary panel (`QuickVocabularyPanel.swift`)
Opened from tray item "Add Custom Vocabulary" (toggle: opens, or closes if already visible). A small floating utility window: title "Custom Vocabulary" (transparent title bar), size 408 x 336 (min content height 240), always on top (floating level), centered on screen when shown, activates the app, draggable by its background, not in window list, stays open when app deactivates, closes on Esc or the close button, non-activating-panel. Background bgWindow.
Content (top to bottom): padding block (h 16, top 16, bottom 14): caption "Add names and phrases for supported transcription and cleanup methods." (12.5 secondary) then HStack(8): text field "Add word or phrase…" (auto-focused on appear; Enter adds; accessible "Add custom vocabulary word or phrase") + secondary "Add" (disabled when parse yields no new terms). Divider. Body: if no terms: centered empty state with icon `text.book.closed` and title "No words added yet" (no message); else a scrolling flow of removable chips (padding 16, 6 gap; add animates with spring(0.35, 0.8), remove with 0.2 s ease-out; no animation if reduce motion), Divider, and a footer line (padding 8 v/16 h) "<N> word(s)" (11.5 secondary, "1 word"/"N words"). Edits the same `customVocabulary` list as the Transcript Cleanup page.
Menu visibility: the tray item "Add Custom Vocabulary" shows only if the engine is AssemblyAI or the current cleanup mode supports custom vocabulary (Vocabulary correction only, Apple Intelligence, Ollama, OpenRouter, OpenAI-compatible, Profile).

### 3.11 Internal Prompts (`InternalPromptsView.swift`) - DEAD/HIDDEN
Never reachable (AssemblyAI-only). Skip. (If implemented later: header "Customize Internal Prompts"; sections Cleanup/Destination Type/Writing Style/Field Context/Recognition Context with per-prompt multiline editors, "Reset to Default", "Additional instructions" 4,000-char counter.)

---------------------------------------------------------------------------

## 4. Floating recorder overlay

Files: `OverlayWindow.swift`, `OverlayPill.swift`, `OverlayContent.swift`, `OverlayTranscriptContent.swift`, `OverlayPalette.swift`, `OverlaySize.swift`, `OverlayScreenPicker.swift`, `OverlayScreenResolver.swift`, `OverlayScreenSession.swift`, `OverlaySystemAppearance.swift`, `ThemeMode+Overlay.swift`, `Waveform*.swift`, `AudioMonitor.swift`.
Reference image: `docs/images/remy-dictating.png` (Comfortable, dark: mark + "LISTENING" header, transcript text, teal waveform ribbon at the bottom, small warm dot at top-right).

### 4.1 Window behavior
- Borderless, transparent, shadow-less (the pill draws its own shadow), always-on-top ("floating" level), visible on all virtual desktops/Spaces and over full-screen apps, never takes focus or becomes key/main window (a non-activating tool window), mouse events ignored (click-through), excluded from window menus / Alt-Tab, does not hide when the app deactivates. Windows: WPF `WindowStyle=None`, `AllowsTransparency=True`, `Topmost=True`, `ShowActivated=False`, `ShowInTaskbar=False`; add `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED` so clicks pass through; show with `ShowWindow(SW_SHOWNOACTIVATE)`. Identifier "remy.recording-overlay".
- Canvas (window) size = (pillMaxWidth + 40) x max(130, pillMaxHeight + 24 + 10) per size preset (below). The window is bottom-anchored content: the pill sits at the bottom center of the canvas with 10pt of shadow room beneath. Pill content bottom-aligned (`Spacer` above), 
- Position: horizontally centered on the chosen display's *visible frame* (work area, excluding taskbar/dock); window bottom = visibleFrame.bottom + bottomMargin where bottomMargin = `size(24) - 10` = 16 - 10 = 6, so the pill's bottom edge sits 16 DIP above the work-area bottom. Repositioned whenever settings change or on each show/update if the frame changed (cheap numeric compare). Works for displays at negative coordinates.
- Appear/disappear: pill fades+scales: visible -> opacity 1, scale 1; hidden -> opacity 0, scale 0.95 anchored at bottom; spring(response 0.35, damping 0.8) (none if reduce motion). On hide the window is ordered out 0.3 s after the fade starts unless re-shown in that interval.
- Session semantics: `beginSession(targetProcess)` is called at dictation start BEFORE audio starts: cancels any pending hide, hides the prior visuals immediately, and pins the target app's process id for display choice. `show(state)` cancels pending hide. `hide(afterDelay)` default 0.5 s. A new show while a hide is pending ends the previous screen session (new display chosen).
- Appearance: the window's appearance (light/dark) is set from the overlay theme (4.4), not the app theme.

### 4.2 Multi-monitor rules
Choose the display once per dictation session (cached by display id; geometry of that display is re-read every update because the work area can change), via `OverlayScreenPicker.pickScreenIndex`:
1. Single monitor -> that monitor.
2. Else the monitor containing the focused text control's position of the TARGET app (the app the transcript will be pasted into, captured before audio startup; if nil, the frontmost app). Uses Accessibility focused-element position, falling back to the app's focused-window position (needed for Chromium/Electron/Firefox apps whose accessibility tree is off). Each lookup has a 0.25 s timeout so an unresponsive app can't stall the UI. The system-wide focused element is deliberately not used (it can return stale elements of other apps).
3. Else the monitor containing the mouse pointer.
4. Else the primary monitor.
If resolution fails it is retried on the next update (not cached). If the chosen display disappears the choice is dropped and re-resolved. **Windows:** use `GetGUIThreadInfo` (caret/focus rect) for the foreground window of the target process, else window rect via `GetWindowRect` of the foreground window; then `MonitorFromPoint`; else cursor position; else primary. Use `MONITORINFO.rcWork` as the visible frame. Convert physical px to DIPs per-monitor DPI.

### 4.3 States and presentation (`OverlayPill.swift`, `OverlayContent.swift`)
`OverlayState`: listening, processing, success, copiedOnly, cleanupFallback(reason, delivery), preparingModel(name). Plus transient overrides: cancellationProgress (non-nil) and busyMessage (non-nil) take precedence over state.

Scale helpers: `size(v) = round(v*0.65)`, `type(v) = round(v*0.78*10)/10`.

Pill dimensions (width x height, DIP), `S` = selected `OverlaySize`:

| Mode | Width | Height | Shape |
|---|---|---|---|
| Listening with text preview ON | S.width (169 / 340 / 460) | grows with measured content, from S.minHeight to S.height | rounded rect, radius S.cornerRadius (14/16/18) (continuous corners) |
| Listening, text preview OFF (waveform only) | S.waveformOnlyWidth (85 / 163.2 / 220.8) | S.waveformOnlyHeight (29 / 38 / 42) | rounded rect radius size(22)=14 |
| Processing, Success | 31 (= size(48)) | 31 | circle (animated morph via spring(0.42, 0.82)); pill gets +1 bottom inset |
| copiedOnly | 144 | 38 | rounded rect radius 14 |
| cleanupFallback | min(S.width+20, 300) (189 / 300 / 300) | 54 | rounded rect radius 14 |
| preparingModel | min(S.width, 240) (169 / 240 / 240) | 38 | rounded rect radius 14 |
| busyMessage | min(S.width, 260) (169 / 260 / 260) | 44 | rounded rect radius 14 |
| cancellationProgress | 169 (= size(260)) | 42 (= size(64)) | rounded rect radius 14 |

Size presets (`OverlaySize.swift`): Compact / Comfortable (default) / Large:
- width: 169 / 340 / 460; canvas width = width + 40 (209 / 380 / 500)
- fontSize: 10.1 / 13.5 / 15.3; line spacing = round(fontSize*0.28) = 3 / 4 / 4
- maxLines: 2 / 3 / 4 (text beyond this scrolls away, newest at the bottom)
- horizontalPadding: 10 / 16 / 18; verticalPadding: 10 / 12 / 13.5
- cornerRadius: 14 / 16 / 18
- waveformHeight: 16 / 16 / 20
- serviceHeaderHeight: 12 / 16 / 16; serviceHeaderSpacing: 5 / 7 / 7
- textWaveformSpacing = round(fontSize*0.9) = 9 / 12 / 14
- characterLimit (text shown, tail kept with leading "…"): 208 / 400 / 640 (global `OverlayPreviewText.maximumCharacters` default = Int(320*0.65) = 208)
- lineHeight = NSFont default line height of the UI font at fontSize; approx 12 / 16 / 18 (≈ fontSize*1.19, rounded to a pixel). Derived values using those: maxTextHeight = maxLines*lineHeight + (maxLines-1)*lineSpacing = 27 / 56 / 84; overflowPeekHeight = lineSpacing + lineHeight/2 = 9 / 12 / 13; viewportMaxHeight = maxTextHeight + overflowPeek = 36 / 68 / 97; chrome = serviceHeader + headerSpacing + waveformHeight + textWaveformSpacing + 2*verticalPadding = 62 / 75 / 84; minHeight = lineHeight + chrome = 74 / 91 / 102; height (max) = viewportMax + chrome = 98 / 143 / 181; canvas height = max(130, height + 24 + 10) = 132 / 177 / 215.
- On Windows measure the real line height with the chosen font (e.g., `FontFamily.LineSpacing * fontSize`) and use the same formulas.

**Listening, text ON** (`ListeningOverlayContent`): padding h = horizontalPadding, v = verticalPadding. VStack(alignment leading, spacing serviceHeaderSpacing):
 1. Header row (height serviceHeaderHeight, 6 gap): service mark (menu-bar glyph template, tinted accent, size = header height), text "LISTENING" (mono bold 9 pt (8 pt in Compact), tracking 1, color secondaryText), spacer, 4pt circle in warmSignal.
 2. VStack(spacing textWaveformSpacing): transcript block + waveform (height waveformHeight).
 Transcript: shows the tail of the live transcript trimmed to characterLimit ("…" prefix when trimmed). Empty -> placeholder "Listening…" in secondaryText (else main text color). Font = UI font at fontSize, line spacing as above, left aligned, wraps to the pill's inner width. Text viewport is bottom-anchored: its height hugs the text up to viewportMaxHeight; once the text exceeds maxTextHeight+1 the top `overflowPeekHeight` fades from transparent to opaque (gradient mask) so half of the previous line peeks and fades. Pill height animates (spring response 0.3, damping 0.86) as lines are added; pill is anchored bottom (grows upward because window is bottom-anchored). Accessible label "Live transcript" (value = text) or "Listening".
**Listening, text OFF**: HStack(8, centered): service mark (16pt, accent) + waveform (height waveformHeight); padding h horizontalPadding.
**Processing**: circle badge containing the processing spinner (below) tinted accent.
**Success**: circle badge containing the success animation tinted `success`. After success the overlay hides after 0.5 s (hide called with 0.5-1.0 s delay depending on path).
**copiedOnly**: HStack(10): clipboard icon (`doc.on.clipboard.fill`, size type(16)=12.5, accent) + text "Press ⌘V" (display 12 semibold, text color @90%). **Windows text: "Press Ctrl+V"**.
**cleanupFallback**: HStack(7): warning triangle (13, warmSignal) + VStack(3): title (UI 11.5 semibold, text color) and detail (UI 10.5, secondaryText), single-line each; padding h 9. Title by failure reason: timeout "Cleanup timed out"; useRaw "Raw transcript selected"; cancelled "Cleanup cancelled"; modelUnavailable/unsupportedLanguage "Cleanup unavailable"; inputTooLong/requestBudgetExceeded "Cleanup input too long"; unknown/default (and a nil mapping) "Cleanup failed" (fallback for unrecognized reason strings is "AI cleanup failed"). Detail by delivery: inserted "Raw transcript used"; copied "Raw transcript · Press ⌘V" (Windows "Ctrl+V"); failed "Raw text in Remy". Accessible hint: failed "Delivery failed. Open Remy to copy the transcript." else "See History for processing details."
**preparingModel(name)**: HStack(10): spinner + text "Loading <name>…" (display 12 semibold, @90%, one line); padding h 14. (Name e.g. "Apple Speech" or the saved profile name.)
**busyMessage**: HStack(10): spinner + "Already working on your dictation" (display 12 semibold, up to 2 lines); padding h 14. No timer: cleared by the next state update or hide.
**cancellationProgress** (hold-to-cancel feedback): VStack(5): text "Hold to cancel" (system 10.1 medium, text color) + determinate progress bar (value 0...1, tint = text color); padding h 16.

When overlay appears per dictation lifecycle (`AppState`): `beginSession` at start; `showListening(level, transcript)` while recording (~every 33 ms when transcript changed or level changed by >= 0.01); processing after stop; success/copiedOnly/cleanupFallback at completion, then `hide(afterDelay)`: 1.0 s normal, 5.0 s when a cleanup failure is displayed; failed-to-start shows processing then hides after 2.0 s; most cancel/abort paths hide immediately (delay 0).

State transitions crossfade: content view keyed by state category, opacity transition 0.2 s ease-in-out (none for reduce motion). Size change animates 0.18 s ease-in-out.

### 4.4 Colors (`OverlayPalette.swift`) - fixed hex, chosen by the overlay's own light/dark

| Token | Light | Dark |
|---|---|---|
| surface | #FFFDF7 | #102838 |
| text | #142D3B | #F5F2E9 |
| secondaryText | #526571 | #B1C7CF |
| accent | #08777B | #75E5D3 |
| success | #22654E | #90E3BE |
| warmSignal | #AD563B | #F1B184 |
| border | #B6D3CA | #355F6A |
| highlight | white @90% | white @16% |

Overlay scheme = `overlayThemeMode`: Light, Dark, or System (follows the OS app-mode setting independent of the app's own theme override; updated live on OS change). Windows: read `AppsUseLightTheme` registry key and listen for `UserPreferenceChanged`/theme change.

Surface (`OverlaySurfaceModifier`): opaque `surface` fill (no blur/glass), clipped to the shape; two strokes: (1) 1.25pt vertical gradient stroke from `highlight` (top) to `border` (bottom) (2pt in high-contrast); (2) 0.75pt `border` stroke (high contrast: `text` color 2pt). Shadows: black 10%/32% (light/dark) radius 1 y 1, and black 14%/34% radius 8 y 4.

### 4.5 Waveform (`WaveformView`, `WaveformDrawing`, `WaveformGeometry`)
Drawing area = full available width x height `waveformHeight` (16/16/20; 16 or `size.waveformHeight` in waveform-only mode). Tint = overlay accent.
- Input `level` in [0,1] (non-finite -> 0), amplitude = 0.06 + sqrt(level) * 0.88.
- Geometry: 65 samples, i = 0...64, x = i/64, envelope = sin(pi * x). Two layers (0 and 1). phase = (t mod 60) * 2.5 + layer * 1.7 (t = seconds, wall clock). frequency = 1.15 (layer 0) / 1.8 (layer 1). wave = sin(x * 2*pi * frequency + phase). Point = (x * width, height/2 - wave * envelope * amplitude * height/2). Connected as a polyline (lines, no smoothing).
- Per layer: fill region = polyline closed via (width, height/2) back to start (so it fills between the wave and the center line), fill color tint @ 9% (layer 0) / 4% (layer 1); stroke the polyline with tint @ 90% (layer 0) / 38% (layer 1), line width 1.2, round caps.
- Edge taper: the whole waveform is masked by a horizontal gradient: transparent at 0%, opaque at 20%, opaque at 80%, transparent at 100%.
- Animation: redraw at 30 fps while active; paused (frozen frame) when reduce motion is on, the overlay is not visible, or level < 0.015. With reduce motion, time = 0 (static). `level` changes animate with 0.12 s ease-out interpolation.
- Audio level source (`AudioMonitor`): every ~33 ms take the last 800 mono samples, RMS, `scaled = min(1, rms * 6.6)`, `level_raw = scaled^0.85`; smoothed: `smoothed = smoothed*k + raw*(1-k)` where k = 0.08 when raw > smoothed (fast attack) else 0.65 (slow release). Overlay updates when |delta| >= 0.01.

### 4.6 Spinner and success animation
Processing spinner (ring size 16 = size(24)): three layers in the tint color: (a) full circle stroke @12% alpha 1pt; (b) open arc from 16% to 82% of the circle, tint @90%, 1.35pt, round caps, rotating 360 degrees linearly every 1.15 s, repeating forever (static if reduce motion); (c) center dot diameter 3 (size(5)) tint @90%.
Success (ring size 16): (a) full circle @12% 1pt; (b) expanding ring: 1pt stroke @42%, scale 0.72 -> 1.28 and opacity 0.8 -> 0; (c) progress arc: 1.4pt stroke @90% trimming 0.12 -> 1.0 starting at -90 degrees (top); (d) checkmark.circle.fill icon (size type(18)=14) tint, scale 0.72 -> 1 and opacity 0 -> 1. All driven by one spring(response 0.34, damping 0.72) on appear (no animation if reduce motion).

---------------------------------------------------------------------------

## 5. Menu bar (tray) and Dock

`AppDelegate.setupMenuBar` (~line 117), `menuNeedsUpdate` (~312).

Status item: square-length item using the `MenuBarIcon` template image; tooltip "Remy". Windows: `NotifyIcon` with tooltip "Remy"; left-click opens the main window (Mac shows the menu on click; Windows convention: left-click = open window, right-click = this menu, or show the menu on either click to match).

Menu items in order (title; action; visibility/enabled rules; evaluated each time the menu opens):
1. "Open Remy" - show main window (restore if minimized, activate/bring to front, switch to Dock policy while visible).
2. "Copy Last Transcript" - copies the last transcript to the clipboard; **disabled** when there is no last transcript.
3. "Stop Dictation" - stops recording and delivers; **hidden** unless recording and not transitioning.
4. "Use Raw Now" - skips cleanup and uses the raw transcript; **hidden** unless status is processing and a cleanup attempt is running without an outcome.
5. "Cancel Dictation" - cancels; **hidden** unless cancellation is available (recording/processing/starting, not already cancelling, not delivering, paste not claimed, not recovering).
6. "Add Custom Vocabulary" - toggles the Quick Vocabulary panel; **hidden** unless engine is AssemblyAI or the cleanup mode supports custom vocabulary.
7. Separator
8. "Microphone" submenu (rebuilt on open; refreshes device list): first item = automatic title ("Automatic" or "Automatic (<device name>)") with a checkmark when no specific mic is selected or the selected one is unavailable; separator; then one item per input device by name with a checkmark on the selected one. Selecting sets `selectedMicrophoneUID` (automatic -> nil).
9. Separator
10. "Quit" (Cmd+Q on Mac; Windows: no shortcut, or "Exit") - triggers graceful shutdown (engine shutdown and local-model unload, then exit; termination waits for async cleanup).
While a dictation happens, an in-flight paste dismisses an open menu (`dismissMenusForPaste`) so focus returns to the target app.

Dock/taskbar option ("App appears in"): "Menu Bar Only" (default; accessory app: no Dock icon, no app menu bar, UI only via tray) vs "Dock and Menu Bar". Policy rule: whenever the main window is visible the app presents as a regular app (Dock icon) even in "Menu Bar Only"; when the window closes (0.1 s later) it reverts to accessory if the setting is Menu Bar Only. Windows analogue: toggle taskbar presence (`ShowInTaskbar`) accordingly; in Tray Only mode hide the window to tray on close, remove taskbar button; also reopen by launching the app again (single instance). The app never quits when the window closes.

Startup behavior: shows the main window at launch (the WindowGroup opens); with Menu Bar Only the Dock icon disappears once the window is closed. Relaunch-on-login disabled (`disableRelaunchOnLogin`). Debug-only `--screenshot-mode` skips services (ignore).

---------------------------------------------------------------------------

## 6. Permissions and onboarding

There is no onboarding wizard or first-run tutorial. First-run guidance is entirely: (a) the main window opens, (b) the Attention banner (2.4) lists blockers, (c) the sidebar footer shows "Setup needed", (d) the OS permission dialogs.

Permissions (`Permissions.swift`):
- Microphone (blocking): `AVCaptureDevice` authorization. States: notDetermined -> action requests the system prompt; denied/restricted -> opens OS privacy settings (`x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone`). Windows: `ms-settings:privacy-microphone`; detect denial via the MMDevice/`Windows.Media.Capture` access error and the registry consent store (`HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone`).
- Accessibility (blocking) **MAC-ONLY**: `AXIsProcessTrusted`; action opens Privacy_Accessibility settings (and a system prompt via `AXIsProcessTrustedWithOptions` is available). Used for hotkeys and paste. Windows: drop (SendInput and global hooks need no grant; note UIPI blocks sending input to elevated apps - if detected, consider an optional attention issue).
- Automation / System Events (optional) **MAC-ONLY**: tri-state (unknown/granted/denied); a denied state is learned from a failed AppleScript paste or a read-only preflight; unknown shows no warning. Action opens Privacy_Automation. Windows: drop.
- Polling: while permissions are not all granted the app polls every 2.5 s (stops when mic + accessibility are both granted) and re-checks on app activation (`applicationDidBecomeActive`). Windows: re-check on window activation and on a 2.5 s timer while the banner is visible.
- `hasChecked` gates banner display: nothing is shown before the first check completes.
- First-time microphone request happens when the user taps "Allow Microphone" or when starting a dictation without permission (which also surfaces the main window once via `presentBlockingAttentionIssueOnce`).

---------------------------------------------------------------------------

## 7. Cross-cutting behavior and notes for implementers

- **Accessibility / reduce motion / high contrast**: every animation above is skipped when the OS reduce-motion preference is on; borders strengthen in high-contrast; controls expose accessible names exactly as the quoted "accessible ..." strings. Map to WPF `AutomationProperties.Name` and `SystemParameters.ClientAreaAnimation`, `SystemParameters.HighContrast`.
- **Disabled state**: opacity 0.45 for buttons/slider/dropdown; chip 0.5; no hover/press feedback.
- **Hover/press**: ink overlay 5% hover, 10% pressed on button fills; nav hover = borderSoft fill.
- **Tooltips**: use `.help` strings where specified (icon buttons use their accessible label).
- **Alerts**: standard modal alerts with a destructive default action label and "Cancel". Windows: `TaskDialog`/custom dialog with the same titles and messages.
- **Window dragging**: sidebar only.
- **Text selection**: History transcript and processing-detail text are selectable.
- **Date format**: abbreviated date + short time in the user's locale (Mac format "Nov 7, 2026 at 11:59 AM"); with seconds for details. Use .NET `DateTime.ToString("g")`-style localized equivalents.
- **Dark-first look**: screenshots are dark mode; light mode is the same layout with the light column.
- **Unused-in-UI strings**: footerCardFill token, `DSIconButton` tint param, display font for About name use default weight - all trivial.
- **README product claims to keep consistent**: Hold to record / Tap to toggle; floating recorder with live waveform and transcript preview; start and finish sounds with custom sounds; tray with or without Dock icon.
