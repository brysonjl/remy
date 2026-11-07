# Remy source and attribution

Remy is an independent adaptation of [Dictate Anywhere](https://github.com/hoomanaskari/mac-dictate-anywhere), originally created by Hooman Askari. The initial source import is pinned to commit `e420448de6e2099d33e050a4b419722032fd7519` (2026-10-16). The original MIT copyright and permission notice is retained in [app/LICENSE](app/LICENSE).

Remy's changes begin with independent application identity, storage/model-cache isolation, and disabling inherited automatic updates. The build status and actual modifications are recorded in the engineering baseline report. This project is not presented as an official release of Dictate Anywhere.

The [upstream tree](research/upstream/mac-dictate-anywhere/TREE.json) and import manifest record the provenance of the imported files: the pinned revision, per-file hashes and the original license notices. This repository does not include the upstream Git history.

## Third-party material

The root MIT license does not replace separate licenses on third-party files, dependencies, or model weights. Keep the notices in each imported subtree. In particular:

- `app/` contains the adapted Dictate Anywhere application and its MIT notice.
- `research/upstream/mac-dictate-anywhere/` and `research/upstream/openwhispr/` contain selected MIT-licensed reference files with their original licenses.
- `research/upstream/fluid-audio/` contains selected Apache-2.0 reference files with their original license. These evidence files are not the application's linked package checkout.
- The application's Swift package lock identifies FluidAudio, llama.swift, and Sparkle. Their source and bundled binary notices must accompany an eventual distribution as required by their respective licenses.
- No model weights are included in this repository. Model downloads must retain their own attribution, license and conversion provenance; the application license does not relicense them.

A binary distribution must include complete notices generated from the exact files, frameworks and models it ships. Never publish signing configuration, credentials, user dictation, evaluation recordings or generated build output.

## Current release inventory

See [third-party notices](THIRD_PARTY_NOTICES.md) and the
[revision/hash inventory](docs/third-party/inventory.json) for verbatim notices
retained from exact pinned packages and selected model/native artifact sources.
FluidAudio 0.17.5 is Apache-2.0, including the actual linked pinned checkout; its
license is not MIT. Sparkle's complete notice includes external components in
addition to its top-level MIT-style terms.

The inventory explicitly records incomplete native/transitive artifact review.
It is not a completed SBOM or permission to distribute a binary.
[Asset provenance](docs/engineering/asset-provenance.md) records generated artwork
and why the imported audio fixtures are not redistributed. Preserve original
notices in any redistribution. No name or trademark clearance
is asserted by the application license or these records.
