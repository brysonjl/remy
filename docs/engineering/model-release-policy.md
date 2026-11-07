# Model release policy

Reviewed 2026-11-05 against the current app source and pinned FluidAudio 0.17.5.
Model weights are downloaded separately and are not part of the source snapshot.
Optional download does not replace the model's license or attribution duties.

## Reviewed, app-pinned paths

| Model | App integrity identity | Attribution and terms |
| --- | --- | --- |
| Parakeet v2 CoreML | `FluidInference/parakeet-tdt-0.6b-v2-coreml@ee09c569f73759e6d44c9bd16766f477b2b36d39`; per-file sizes and SHA256 in the app manifest | NVIDIA Parakeet TDT 0.6B v2; CoreML conversion by FluidInference; CC-BY-4.0; preserve the model card and license URL |
| S1-mini by Superwhisper, Q4_K_M | `superwhisper/s1-mini-GGUF@ee2c0f56e56345f475749a44ff2893e21c3cb292`; 484,219,808 bytes; SHA256 `3b41ebe2502cbd03e811d5d16b022f5ab551eda58d62597d152f89535003c634` | Full supplied Apache-2.0 license plus the additional naming term; preserve S1-mini and Superwhisper capitalization and underlying Qwen attribution |

The application About page identifies Parakeet v2 and its creators. The pinned
S1-mini installer also downloads its license. Complete model notices are retained
under [third-party models](../third-party/models/); this policy does not attest
that a user's previously cached files are the reviewed files.

## Other available routes

The speech menu still contains the alternatives below. Reviewing their public
cards does **not** change the downloader's revision or certify their quality.
The pinned SDK's `Repo.revision` returns `main` for these speech/helper repos;
registry/repository/revision overrides can also redirect SDK routes. The app's
Parakeet v2 downloader is a separate pinned path.

[Reviewed option metadata](../third-party/models/reviewed-options.json) records
immutable review revisions and hashes for the source cards, with the original
texts retained. These are documentary snapshots, not new runtime model pins.

| Existing choice or helper | Review result and release action |
| --- | --- |
| Parakeet v3; Parakeet Ultra; hybrid compact TDT/CTC 110M | Conversion cards declare CC-BY-4.0. Preserve NVIDIA/FluidInference and, for Ultra, moondream provenance. Pin the actual conversion and review the linked base-model terms before advertising binary support. |
| CTC 110M vocabulary helper | Conversion card metadata declares CC-BY-4.0 while its prose says Apache-2.0. Resolve the discrepancy against the exact base-model/conversion terms before including this helper in a supported release configuration. |
| Parakeet EOU 320 ms | Conversion card identifies NVIDIA Open Model License, not generic Apache-2.0. Review and retain the governing exact base-model terms and conversion provenance; pin the selected subdirectory/files. |
| Nemotron English 560/1120/2240 ms | Card metadata identifies NVIDIA Open Model License but prose says Apache-2.0. Resolve this conflict; pin each supported tier and retain applicable exact model terms. |
| Nemotron multilingual | Card identifies OpenMDW-1.1 and NVIDIA base weights. Review the governing terms and pin the selected multilingual/tier variant; do not relabel as Apache-2.0. |
| SenseVoiceSmall | Conversion card defers to upstream SenseVoice terms. Retain and review the exact upstream weight/conversion terms, not merely the Swift SDK license. |
| Silero VAD helper | Conversion card declares MIT. Obtain the complete upstream copyright/license notice and pin the selected model file before treating this helper as a reviewed redistribution route. |
| Apple Speech and Apple Intelligence | OS-provided frameworks/assets are not Remy-distributed model weights. Document platform availability and actual execution/privacy behavior separately. |
| Ollama and remote cleanup profiles | Users select external model IDs/endpoints. Provider access does not establish model redistribution rights or local execution. Do not bundle or mirror third-party model weights by default. |

This pass retains existing development capabilities. A public binary must either
complete these checks for its exposed supported routes or narrow the supported
release configuration explicitly. Labeling a choice experimental does not resolve
licensing or integrity requirements.

## Adding or updating a model

Record repository, immutable revision, exact file set/variant, sizes/hashes,
conversion and base-model provenance, complete governing terms, required display
names/attribution, and any use/distribution conditions. Verify staged files before
activation and keep a model update separate from an app update. Update notices
and review request payload/locality claims before changing the advertised route.
Never infer a model license from FluidAudio, llama.cpp, or Remy's source license.

For a binary candidate, verify the selected route from a clean cache and confirm
its license/attribution survives download, offline restart, migration and deletion.
Also test corrupt/partial downloads and unavailable metadata without silently
substituting a different model. Model cards and SDK benchmark claims are not
Remy accuracy, latency, or compatibility results.
