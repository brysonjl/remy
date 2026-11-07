# Third-party notices and release inventory

Reviewed 2026-11-05. Remy's application changes are distributed under the root
[MIT license](LICENSE); the original [Dictate Anywhere MIT notice](app/LICENSE)
is preserved. Third-party software, models, and reference files keep their own
terms. These notices do not relicense model weights or grant rights to another
project's name or branding.

Verbatim notices and their source revisions/SHA256 values are recorded in
[docs/third-party/inventory.json](docs/third-party/inventory.json). The
[native review](docs/third-party/native-review.md) now covers the pinned NeMo
lockfile notice superset, embedded Rust runtime copyright bundle, llama.cpp vendor
notices, and exact byte-matched font notices. Remaining binary gates concern the
final app/resource scope, optional models and actual packaging; this inventory
does not claim a reproducible upstream binary build or legal clearance.

## Linked packages and native artifacts

| Component | Exact version/revision | License and retained material |
| --- | --- | --- |
| FluidAudio | 0.17.5 / `0b1f46289fe27d95b5e66ad8be46e64f5ee02ae7` | [Apache-2.0](docs/third-party/FluidAudio/LICENSE); all five supplied [third-party notice files](docs/third-party/FluidAudio/ThirdPartyLicenses/) retained |
| llama.swift | 2.10549.0 / `716419d4d7aa542fce301e809cde7234c68ddbc6` | [MIT, Copyright 2025 Mattt](docs/third-party/llama.swift/LICENSE.md) |
| llama.cpp native framework | b10549 | [MIT, Copyright 2023–2026 The ggml authors](docs/third-party/llama.cpp/LICENSE); complete supplied vendor notice set and embedded image/audio/hash/subprocess license comments retained |
| Sparkle | 2.9.6 / `ac2def288cbff5cfc7df3ffef6abdf45b72bcb0a` | [Complete upstream LICENSE](docs/third-party/Sparkle/LICENSE), including MIT and external BSD-style/zlib notices; retained even though updating is disabled |
| NemoTextProcessing / text-processing-rs | Native artifact v0.3.1 | [Apache-2.0](docs/third-party/NemoTextProcessing/LICENSE), [NOTICE](docs/third-party/NemoTextProcessing/NOTICE), and [third-party summary](docs/third-party/NemoTextProcessing/THIRD-PARTY-LICENSES.md) |

The FluidAudio notices include fastcluster (BSD-style), VBx (Apache-2.0), Japanese
and Spanish/French text frontends with their component terms, and the NeMo engine.
Retaining the package-supplied notices conservatively does not assert that every
optional FluidAudio feature or model ships in Remy. Review actual resources and
linked code to determine precise scope.

FluidAudio's supplied NeMo summary mentions v0.3.0, while the exact pinned package
manifest downloads v0.3.1. Both the unmodified package summary and additional
v0.3.1 source notices are retained; do not silently edit upstream notice text.
The v0.3.1 summary is supplemented by complete notices from all 67 checksum-verified
registry crates in its release lockfile, plus the embedded Rust standard library's
complete generated copyright bundle. See the native review for inclusion scope
and build-provenance limits.

## Bundled fonts

Inter and Fraunces are SIL Open Font License 1.1 fonts. Their committed binaries
byte-match the immutable upstream Google Fonts revisions recorded in the inventory.
Full notices are retained next to the app's font files and as
[Inter OFL](docs/third-party/fonts/inter-OFL.txt) and
[Fraunces OFL](docs/third-party/fonts/fraunces-OFL.txt). Preserve those notices when
redistributing the font files; the application MIT license does not replace them.

## Separately downloaded models

No weights are distributed in this repository. The application license does not
replace the model terms, even when download is optional.

- **Parakeet TDT 0.6B v2, CoreML conversion by FluidInference**, based on
  **NVIDIA Parakeet TDT 0.6B v2**: conversion revision
  `ee09c569f73759e6d44c9bd16766f477b2b36d39`, declared **CC-BY-4.0**.
  Retained [model card](docs/third-party/models/Parakeet-v2-MODEL_CARD.md),
  [original conversion](https://huggingface.co/FluidInference/parakeet-tdt-0.6b-v2-coreml/tree/ee09c569f73759e6d44c9bd16766f477b2b36d39),
  [base model](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2), and
  [license terms](https://creativecommons.org/licenses/by/4.0/legalcode.en).
  Remy pins the conversion and downloads it separately; this notice does not
  claim a Remy-authored conversion or endorse upstream performance claims.
- **S1-mini by Superwhisper**, GGUF revision
  `ee2c0f56e56345f475749a44ff2893e21c3cb292`,
  `s1-mini-q4_k_m.gguf`: [full pinned license](docs/third-party/models/S1-mini-LICENSE).
  This is Apache-2.0 **with an additional naming term** requiring the original
  S1-mini/Superwhisper naming. Do not describe it as unmodified Apache-2.0.
  The app downloads the license alongside the model. The model derives from
  Qwen3-0.6B; preserve the base-model notices in its supplied license.
- Other available speech and cleanup models retain their own terms. The
  [model release policy](docs/engineering/model-release-policy.md) records the
  reviewed routes, additional model-card snapshots, conflicting declarations and
  exact remaining steps. The SDK license does not establish model licensing.

## Source reference material and assets

The selected `research/upstream/` snapshots retain their original licenses and
revision manifests: Dictate Anywhere and OpenWhispr (MIT), FluidAudio (Apache-2.0).
They are research references, not the linked Swift package checkout.

See [asset provenance](docs/engineering/asset-provenance.md) for the generated
mascot/icon and optional local speech fixtures. Six imported WAV recordings are
omitted from the publishable working tree because audio-specific redistribution
provenance was unresolved. Opt-in ASR tests load only explicitly supplied local
recordings. Earlier Git history still contains the originals; a public
history-bearing repository needs a separate publication decision, or use a
reviewed current-tree source snapshot without Git history and local data. No
personal recording or regenerated voice output is cleared for publication by
this document.

## Before binary publication

Reconcile the final app's frameworks, static libraries and resource bundles with
this inventory; complete the open gates in inventory.json. Ship the root LICENSE,
app/LICENSE, ATTRIBUTION.md, this file and applicable complete notice files with
the app/DMG, with an accessible in-app route to notices. Confirm licenses and
attribution accompany separately downloaded models. A checked-in notice folder
alone does not establish that an Xcode app bundle contains those notices.
