# How this repository was published

This repository started from a reviewed source snapshot of Remy's development
repository. The development history stays private: its historical commits
contain imported audio, internal continuation notes and personal author
metadata that were left out of the public snapshot. The initial public commit
is `f04a7a2`, exported from development commit `ed9a350`.

## Review scope

The public source omits the six imported speech recordings. Optional ASR checks
accept explicitly configured local fixtures; ordinary tests and source builds
do not require those recordings. See [asset provenance](asset-provenance.md).
Internal continuation notes were excluded from the export. The old raw Arc
diagnostic capture is also not published; its curated validation report
remains in the source tree.

Publication copies of the historical prerequisite and validation records normalize
personal home/workspace paths to `<HOME>` and `<WORKSPACE>`. The old live-performance
capture also omits process/user/boot identifiers and stack metadata; measured
trace events remain. This normalization does not change historical measurements
or make them evidence of current release compatibility.

Reviewed media are the generated mascot/icon, generated icon sizes and menu-bar
mark, plus the exact licensed font files. The image masters show only the project
robot; generated icon metadata contains display/color properties rather than
captured documents or personal images. Font origins and complete terms are in
the [notice inventory](../../THIRD_PARTY_NOTICES.md). The source exporter pins the
reviewed binary paths and hashes, rejecting changed or unexpected binaries until
they are reviewed again.

## How the snapshot was made

The [source exporter](../../validation/publication-hygiene/README.md) read the
selected committed tree and wrote a deterministic archive with a per-file
integrity manifest. The archive contained no Git history or author metadata,
ignored local files, signing overrides, build caches or old recording blobs.
It was extracted into a new empty directory, its manifest was verified, and it
was built and tested there before a new Git repository was initialized with a
public no-reply author identity. The upstream copyright notices, source import
manifest and independent-project attribution are kept; a new Git history does
not remove attribution obligations.

`SOURCE_MANIFEST.json` at the repository root is that export's manifest. It
describes the initial snapshot only and is not updated by later changes; use Git
history for anything after `f04a7a2`.

## After launch

GitHub private vulnerability reporting is enabled; see [SECURITY.md](../../SECURITY.md).
Changes now land through pull requests on this repository and its hosted checks.

An open source preview can state its tested scope and unfinished runtime gates.
A downloadable application has separate [binary release requirements](../../app/RELEASE.md),
including complete shipped notices and actual Developer ID/notarization evidence.
