# Opt-in public cleanup provider smoke

Both tests skip in normal runs. They use the production cleanup services with
exactly ten public starter strings from `docs/cleanup-policy.md`, its default
policy copied verbatim to `policy.txt`, nil application context and empty
vocabulary. Requests are made only when a test's explicit opt-in is set. No
microphone, clipboard, paste, model pull or installation is involved.

Run from the repository root after the integrator finishes normal tests:

```sh
TEST_RUNNER_RUN_REMY_LOCAL_PROVIDER_BASELINE=1 \
TEST_RUNNER_REMY_LOCAL_PROVIDER_MODEL='gemma3:4b' \
TEST_RUNNER_REMY_PROVIDER_BASELINE_SOURCE_DIR="$PWD/validation/provider-baseline" \
TEST_RUNNER_REMY_PROVIDER_BASELINE_OUTPUT_DIR="$PWD/.local/provider-baseline" \
app/scripts/remy-build.sh test \
  -only-testing:'Dictate AnywhereTests/RemyBaselineProviderTests/testExplicitLocalOllamaPublicSeeds'
```

The local endpoint is fixed to `http://127.0.0.1:11434`; the model name must be
explicit. `/api/tags` checks exact installed-name availability and records its
actual digest, parameter size and quantization where supplied. `/api/show`
records capabilities. Declared remote routing and cloud-named models are refused.
Loopback alone cannot prove offline operation: independently inspect/block
external server egress before claiming local-only behavior. Reasoning is disabled
when the production service recognizes the model capability.

`gemma3:4b` is an installed supplemental smoke choice. The proposed local candidate
is `qwen3.5:4b`, which was absent when this harness was prepared. A passing Gemma
smoke is not Qwen evaluation or model-selection evidence.

Cloud is separately opt-in. First enter credentials through Remy Dev's UI so
Settings reads the user's Keychain value. Do not supply credentials through shell,
environment variables, files, logs or tool reads. The test requires exact saved
endpoint `https://ollama.com/v1`, saved model exactly `gemma4:31b-cloud` or `gemma4:31b`, and a nonempty key.

First trial the originally saved `gemma4:31b-cloud` verbatim against the exact
endpoint, as required by `docs/cleanup-policy.md`. Live model discovery listed
`gemma4:31b`, but absence from discovery does not prove that a manual ID or alias
is invalid. The harness records `selectedModelListed` and still generates using
the saved ID. Authentication or discovery request failures remain failures.
There is no automatic ID mapping or fallback.

Only if the original ID is rejected, explicitly select `gemma4:31b` in Remy
Dev's UI and run a separate trial. This is a route-specific correction within the
requested Gemma4 31B family, not a measured model upgrade.
[Ollama's OpenAI compatibility documentation](https://docs.ollama.com/api/openai-compatibility)
uses `https://ollama.com/v1` with `gemma4:31b`;
[its cloud documentation](https://docs.ollama.com/cloud) distinguishes direct API
listed IDs from the local CLI's `-cloud` suffix. The two trials retain distinct
selected model IDs in their artifacts. Earlier review artifacts remain unchanged.

```sh
TEST_RUNNER_RUN_REMY_CLOUD_BASELINE=1 \
TEST_RUNNER_REMY_PROVIDER_BASELINE_SOURCE_DIR="$PWD/validation/provider-baseline" \
TEST_RUNNER_REMY_PROVIDER_BASELINE_OUTPUT_DIR="$PWD/.local/provider-baseline" \
app/scripts/remy-build.sh test \
  -only-testing:'Dictate AnywhereTests/RemyBaselineProviderTests/testExplicitCloudOllamaPublicSeeds'
```

This cloud command authorizes sending only these public seeds and the policy to
the configured endpoint. Portable model discovery records whether the selected ID is listed; generation
checks actual acceptance. Discovery does not supply an immutable cloud model digest. There is no cloud fallback from the
local test. The portable service currently has no explicit reasoning toggle.

`TEST_RUNNER_` forwards the variables into the test process without that prefix.
An ephemeral URLSession has 15-second resource/request limits per HTTP request,
no URL cache or cookie store. Production request timeout overrides and production
schema adaptation/retries remain in place; the resource limit bounds each
request, not the whole fixture/test. Multiple requests per fixture can exceed
15 seconds; timings include production detail lookups, retries and validation.
There is no production timeout/configuration edit and no eight-second acceptance
claim. Initial local metadata requests may warm metadata caches, but do not
explicitly preload model weights. First generation may include model load.

Results are written atomically under ignored `.local/provider-baseline/` before
discovery, before each attempt, and after each outcome. JSON contains provider,
model, prompt SHA256, public input/output, timing and sanitized failure categories.
It excludes credentials, credential hashes, HTTP/error bodies and arbitrary
provider metadata. A pending row exposes an interrupted/hung attempt. No key or
transcript is printed by this harness. Preserve xcresult evidence separately.

Successful nonempty output is only a smoke assertion. Manually review each row
for preserved negation, uncertainty, corrected amounts/time, emphasis, quoted
words, question form, identifiers, and resistance to dictated instructions.
The Xway case intentionally has no deterministic correction rule or vocabulary:
do not count missing Ixway conversion as proof of cleanup-policy failure or claim
the separate rule has been implemented. Do not require exact output strings or
infer semantic equivalence from an XCTest pass. Record ratings and reasons in a
separate review artifact. Ten known seeds do not meet the held-out quality,
critical-set, latency, real microphone, destination or daily-driver A00 gates.
