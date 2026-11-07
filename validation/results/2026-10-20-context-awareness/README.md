# Restored application context for cleanup

User-requested restoration of inherited Context Awareness, expanded to terminals,
terminal coding agents, browsers, coding apps and AI chat destinations.

## Implemented

- Restored settings and runtime collection/sharing capability gates, including
  saved cleanup profiles and request-budget accounting for actual context.
- Frozen awareness, remote-sharing consent, app rules and category styles before
  asynchronous startup, so settings edits cannot broaden an in-flight session.
- App name, category and style accompany enabled cleanup. Remote title/URL/nearby
  text requires separate opt-in. Password/excluded fields suppress captured text
  and app identity. Off and vocabulary-only cleanup do not collect context.
- Strict loopback/local model handling agrees between runtime and UI; cloud model
  suffixes are normalized before checking. Unknown/LAN/wildcard endpoints require
  the remote-sharing choice for full context.
- Known native ChatGPT/Claude/Codex/Cursor and AI websites are classified, with
  website categories preceding generic browser classification. Known app overrides
  retain priority. Installed ChatGPT currently shares `com.openai.codex`; exact
  app-name disambiguation preserves ChatGPT's AI chat classification.
- Terminal coding agents require explicit bounded title evidence (Claude Code,
  Codex, Gemini CLI, OpenCode or Aider). Generic terminals remain Terminal.
  Metadata-only fallback works without an accessible focused text field.
- Technical/prompt instructions preserve identifiers, paths, flags and code,
  and keep natural-language prompts as prompts. S1-mini retains its trained
  style and email/general interface. New preferences decode older S1 style data.
- Existing awareness opt-outs and category styles survive Dev-to-Alfred migration;
  remote surrounding-text sharing is not imported. Historical explicit off
  preferences remain off; new installations default to awareness on.
- Startup cancellation immediately revokes context capture, including while
  history initialization is suspended; late context cannot enter another session.
  Stored history configuration records the frozen awareness setting.

## Verification

- Final signed isolated full XCTest run, including the startup-cancellation
  regression: **1,091 tests, 60 opt-in skips, zero failures**.
- Expanded settings rendered and visually inspected at 480 and 676 point widths;
  all nine category/style rows and sharing controls fit without clipping.
- HTTP fixtures verify actual provider request payloads for both profile adapters,
  loopback routes, remote metadata only, and explicit full-context opt-in.
- Signed Release build succeeded. `codesign --verify --deep --strict` passed
  using macOS trust services; bundle identity is `app.alfred.dictation`.
  Updated artifact: `app/.build/DerivedData/Build/Products/Release/Alfred.app`.

Local logs: `/tmp/alfred-context-full-tests.log`,
`/tmp/alfred-context-final-full-tests.log`, `/tmp/alfred-context-release.log`.
Rendered settings images are in the task's OS temporary directory as
`alfred-context-settings-480.png` and `alfred-context-settings-676.png`.

## Limits

No live microphone/provider quality matrix or live browser/terminal AX matrix is
claimed. Agent identification uses window-title evidence, not subprocess proof;
custom/hidden titles fall back to Terminal and can be overridden per application.
Website detection requires an exposed page/window URL, otherwise Browser applies.
Loopback locality assumes the selected server/model actually processes locally.
These are code/test/build results, not an installed or published release.
