# Native dependency notice review

Reviewed 2026-11-05. This records concrete observations of pinned dependency
inputs, rather than a claim of reproducible upstream binary builds. The
[inventory](inventory.json) records source revisions, retained-file hashes,
native input hashes and arm64-slice hashes. Verify again when package pins change.

## NeMo engine and Rust runtime

The exact FluidAudio package pins NemoTextProcessing v0.3.1. Its source tag
resolves to `46ebddcea5e1ff673c2cb5256a1b6b0e6c4ff527`; its Cargo package version
still says 0.3.0. The release build script enables `ffi,fst-engine` for arm64 and
other Apple targets. Its unmodified Cargo.lock, manifest and build script are
retained under `NemoTextProcessing/source-review/`.

All **67 registry crates** from that lockfile were downloaded from crates.io and
each compressed crate SHA256 verified against the lock. Full included license,
notice, copyright and author files are retained under `NemoTextProcessing/crates/`;
[crates.json](NemoTextProcessing/crates.json) records metadata and paths. The set
conservatively includes development, wasm and other-platform dependencies; it is
not an assertion that every locked crate is linked on macOS arm64. RustFST's
published crate omits its license files, so its exact crate VCS revision
`8e1391df1ef3dfb85e309dd4ee8af45251d28c9f` supplied the retained MIT/Apache texts.
Unicode-3.0 and other additional supplied notices are retained, not flattened to
one generic MIT/Apache label.

The inspected archive contains registry version paths for 15 crates; all match
this release lockfile. These positive matches support version provenance but do
not establish a complete dependency graph from strings alone. The supplier
script does not use `--locked`; this review does not claim to reproduce its build.

The archive also identifies **rustc 1.98.1**, source revision
`48a229ceaefd4985c50990b14116b6d856af0985`. The retained
[runtime copyright bundle](NemoTextProcessing/rust-runtime/COPYRIGHT-library.html)
is the complete generated standard-library notice set extracted from the
SHA256-verified official Rust compiler release archive. It covers library
components separately from compiler-only material. The exact Rust MIT/Apache
texts, copyright statement and compiler-builtins license are also retained.

The NeMo v0.3.1 LICENSE, NOTICE and third-party summary preserve the NVIDIA NeMo
FST grammar provenance and required notices. No grammar weights or Rust source
packages are bundled here; this folder contains notice texts and small build-input
records, not downloaded model weights.

## llama.cpp framework

The b10549 tag resolves to `b2e5e9b28b2484fbf94b543432ece638996a8b97`.
Its retained Apple build script enables Metal, Apple BLAS and mtmd; common tools,
examples, server, OpenMP and OpenSSL are disabled. Inspection of the actual arm64
framework finds mtmd, stb image, miniaudio, XXHash and subprocess symbol families.
Its dynamic dependencies are Apple system frameworks/libraries, not an additional
bundled third-party dylib.

The notice set now includes the top-level MIT license, JSON library MIT notice,
vendored hash licenses, the SHA1 public-domain declaration, stb's complete dual
license comment, miniaudio's complete dual license comment, and subprocess's
complete public-domain dedication. Additional supplied vendor notices are retained
conservatively, including cpp-httplib even though common is disabled. Embedded
header comments are copied verbatim with source-file SHA256 and byte ranges in
the inventory. The source build script and mtmd/vendor CMake inputs are retained
for scope review. No framework binary was modified by this review.

## Fonts and SDK resources

The committed Inter and Fraunces font bytes match the recorded immutable Google
Fonts files exactly; full companion OFL 1.1 texts are retained both adjacent to
the fonts and in this notice folder. The original font copyright metadata and
companion notices remain unmodified.

The inspected local Release app also contains `FluidAudio_FluidAudio.bundle` with
`luxtts_en_us_lexicon.tsv.zz` and `luxtts_en_us_g2p_aux.json`. In the exact SDK,
`Bundle.module` is referenced only by `TTS/LuxTts/G2p/LuxTtsG2p.swift`, and its
manifest processes that resource directory. The SDK's `Documentation/TTS/LuxTts.md`
describes offline espeak probing and clause-rule semantics; the supplied
third-party notice set does not settle those specific resources/rules.

Remy does not expose TTS, but unused resources are still distributed if present
in the app. Binary publication therefore needs a concrete scope decision: obtain
applicable provenance/terms, or remove unused TTS resources and verify that the
final linked app neither depends on them nor retains any separately unresolved
code provenance. Removing data alone does not establish clearance of compiled
rules. This is a documented provenance question, not a finding of infringement.

## Repeatable checks

With Python 3.11 or newer, run `python3 validation/check-third-party-notices.py`
from the repository root to
verify retained-file hashes, package pins, font hashes and crate inventory links.
With the same resolved local native inputs, add `--native-artifacts` to verify
both original and arm64 slice hashes. A changed dependency requires a new review,
not just replacing the expected hash. Packaging must copy the entire applicable
notice tree and expose it from the app; source-folder checks do not verify that
final packaging step.
