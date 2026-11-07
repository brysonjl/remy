# Cleanup input budget regression

The installed app recorded an OpenAI-compatible cleanup failure with category
`requestBudgetExceeded`, 1,539 input bytes, a 1,176-byte saved base prompt, and
zero elapsed milliseconds. Only metadata was inspected; the transcript was not
sent to a provider for this investigation. The runtime rejected the request
before provider work, using an 8,192-byte conservative estimate that also counted
generated instructions, context, framing, and reserved output.

The runtime now selects bounded chunking. It validates a lossless partition
before provider operations, then adapters plan against their effective context
and completion limits. Each request still obeys the conservative cap. This policy
does not enable automatic retries. Failed or cancelled chunks cannot return
partial cleanup, and the existing whole-attempt deadline/raw fallback remains.

## Verification

The isolated Xcode test host passed 69 distinct tests across these suites:

- `RemoteCleanupInputPolicyTests`
- `RemoteCleanupRetryPolicyTests`
- `RemoteCleanupConcurrencyTests`
- `AlfredCleanupAttemptTests`
- `AlfredCleanupProfileIntegrationTests`
- `CleanupProviderContractTests`
- `CleanupProfileRuntimeTests`
- `TranscriptCleanupPlanTests`

The main run passed 67 tests; a follow-up concurrency run passed six, including
two newly added bounded-policy failure/cancellation cases (four overlap).
The realistic synthetic short-dictation fixture uses production instructions and
app context, reproduces the former single-request rejection, and now returns the
complete text through multiple individually checked requests. Additional cases
cover all frozen remote modes, short-input single requests, smaller provider
limits, oversized fixed instructions, indivisible identifiers, separator/order
preservation, cancellation, partial failure, and no automatic retries.

These are deterministic tests with synthetic generation/HTTP responses. They do
not measure live provider latency or cleanup quality across chunk boundaries.
The total foreground deadline remains eight seconds, so slow provider responses
still fall back to the complete corrected raw transcript.

## Local installation

The signed Release build succeeded and passed `codesign --verify --deep --strict`.
After the user approved restarting Alfred, `alfred-local.sh prepare` and
`alfred-local.sh install` staged and installed the update, preserving the previous
owned app in the install helper's rollback directory. The installed
`~/Applications/Alfred.app` was reopened and its Speech Model screen verified:
FluidAudio, Parakeet v2 downloaded, service status “Loads on first dictation.”
No new recording or live cleanup request was performed during verification.

## Commit verification, 2026-10-28

An isolated snapshot containing only this cleanup fix on top of `f016287`
passed all 72 tests in the eight suites listed above. Unrelated release and
history working-tree changes were excluded. The different total reflects the
snapshot's baseline tests and the explicit single-request compatibility case.
