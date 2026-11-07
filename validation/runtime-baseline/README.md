# Controlled synthetic runtime replay (supplementary A00 evidence)

This opt-in XCTest uses the app's real `ParakeetEngine.prepare()` and
`transcribeRecording(at:)` recovery path with already-installed Parakeet v2
(`englishOnly`). It never records a microphone, downloads models, runs cleanup,
or invokes clipboard/paste. Normal test runs skip it.

Generate 20 deliberately public English fixtures (10 short cleanup-policy seeds
and 10 longer extensions) with an installed macOS voice:

```sh
python3 validation/runtime-baseline/generate.py --voice Samantha
```

Use `say -v '?'` to select an already installed English voice if necessary. Do
not install additional voices. Audio and generated metadata/results stay in
ignored `.local/runtime-baseline/`; the committed references contain no personal
audio or secrets. Generator records voice, rate, OS, references and SHA256 per
WAV. The test validates WAV hashes and records the fixture and model manifest
SHA256. Verify actual installed model hashes using the model-baseline procedure
separately; hashing the manifest alone is not proof of installed model integrity.

After the integrator's build/test work and explicit pinned model installation:

```sh
TEST_RUNNER_RUN_REMY_RUNTIME_BASELINE=1 \
TEST_RUNNER_REMY_RUNTIME_BASELINE_DIR="$PWD/.local/runtime-baseline" \
TEST_RUNNER_REMY_RUNTIME_MODEL_MANIFEST="$PWD/validation/model-baseline/manifest.json" \
app/scripts/remy-build.sh test \
  -only-testing:'Dictate AnywhereTests/RemyBaselineRuntimeTests/testInstalledV2PublicSyntheticReplay'
```

`TEST_RUNNER_` forwards variables to the Xcode test runner, where Swift reads
names without that prefix. Settings touched for this run are restored with
`defer`; the runner should use the isolated Remy Dev storage environment.
Results are atomically saved to a fresh `result-<UUID>.json` after every replay,
including failures and deliberately public reference/transcript pairs. No
transcripts are printed by this test. It asserts nonempty successful output;
quality/negation/numbers/tail review requires inspection of every JSON row.
A completed XCTest is not an ASR quality pass.

The first prepare timing is cold only within this test process, includes engine
internal warmup, and leaves OS/CoreML caches uncontrolled. The first replay is
labeled separately, followed by the initial corpus pass and a second warm corpus
pass (40 samples total). Times use `ContinuousClock`; audio duration is measured
from each file. These are file replay durations, not hotkey, speech-onset,
streaming-preview, or stop-to-final timings. No enforced per-call timeout exists;
a hung runtime requires terminating the test. A pending row is saved before each
replay so the unfinished attempt remains visible in the JSON. Preserve the xcresult/termination evidence in that case.

This corpus is synthetic, small, known, clean and spoken by one TTS voice; it is
neither held-out human speech nor the 100/50-utterance acceptance corpus. No
baseline comparison, WER acceptance, memory/thermal/power measurement, silence
regression, ten cold launches, 100 warm short samples, real tail draining,
microphone/permission/hotkey/cancel, destination insertion, cleanup-provider,
fault/soak or daily-driver gate is established here. Record those separately per
`docs/evaluation/acceptance.md`. This replay cannot close A00.
