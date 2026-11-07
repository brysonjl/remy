# A00 destination fixture

Open `index.html` directly in Chrome or Arc. If local file navigation is inconvenient, from the repository root run the following explicitly; no server has been started by this task:

```sh
python3 -m http.server 8765 --bind 127.0.0.1 --directory validation/destination-baseline
```

Navigate to `http://127.0.0.1:8765/`; stop the server with Control-C afterward. The fixture has no form, submission handler, network calls, storage or clipboard reads. Counters observe paste/input events without inspecting clipboard data. **Clear fields** does not reset counters or emit synthetic input; **Reset counters** leaves text unchanged. Reload clears everything.

Use synthetic single-line text such as “Remy check one two three.” Verify the exact text, insertion count, target field and absence of unintended Return/submit. Clear fields and reset counters between trials; counter values alone do not prove delivery. In a separate trial change focus from field 1 to field 3 during processing and record the actual behavior. A00 measures existing behavior; this fixture does not implement A07 focus guards.

For TextEdit, use an empty scratch document. For Chrome/Arc, test both textarea and contenteditable. For Ghostty, use only harmless single-line text in a controlled shell; **never send Return or execute the inserted text**. For Codex's TextChatGPT bundle, use an empty composer and never submit. Confirm the actual foreground app identity and selected hotkey configuration before each trial. Do not change system permissions or hotkeys silently. Hold and toggle are separate trials; cancel must leave destination text unchanged. Record clipboard outcomes only through an explicitly authorized manual check; this page does not read the clipboard.

## Evidence template

Record build/bundle ID, OS, date, configured gesture, cleanup mode and model revision alongside this table. Use pass/fail/blocked plus observed evidence; leave unrun rows unrun.

| Destination / surface | Toggle start-stop | Hold press-release | Cancel | Focus change while processing | Exact insertion / duplicate count | Return or submit observed | Evidence / limitations |
| --- | --- | --- | --- | --- | --- | --- | --- |
| TextEdit scratch document | Unrun | Unrun | Unrun | Unrun | Unrun | Unrun | |
| Chrome textarea | Unrun | Unrun | Unrun | Unrun | Unrun | Unrun | |
| Chrome contenteditable | Unrun | Unrun | Unrun | Unrun | Unrun | Unrun | |
| Arc textarea | Unrun | Unrun | Unrun | Unrun | Unrun | Unrun | |
| Arc contenteditable | Unrun | Unrun | Unrun | Unrun | Unrun | Unrun | |
| Ghostty controlled shell | Unrun | Unrun | Unrun | Unrun | Unrun | Unrun | Never execute |
| Codex / TextChatGPT composer | Unrun | Unrun | Unrun | Unrun | Unrun | Unrun | Never submit |

Distinguish automatic insertion, copy-only fallback and manual paste. Screenshot/text evidence should contain only the synthetic fixture. A successful browser fixture trial does not establish terminal or Codex behavior, and these checks alone do not close A00.
