# Publishing the reviewed source snapshot

The public project will start in a new repository from a reviewed source
snapshot. Keep the existing development repository private: its historical
commits contain imported audio, internal continuation notes and personal author
metadata that are outside the public snapshot. Existing history is preserved,
without a force push or rewrite.

## Review scope

The public source omits the six imported speech recordings. Optional ASR checks
accept explicitly configured local fixtures; ordinary tests and source builds
do not require those recordings. See [asset provenance](asset-provenance.md).
The internal `HANDOFF.md` is excluded from source exports.
The old raw Arc diagnostic capture is retained privately rather than published;
its curated validation report remains in the source tree.

Publication copies of the historical prerequisite and validation records normalize
personal home/workspace paths to `<HOME>` and `<WORKSPACE>`. The old live-performance
capture also omits process/user/boot identifiers and stack metadata; measured
trace events remain. Originals are retained only in ignored local storage. This
normalization does not change historical measurements or make them evidence of
current release compatibility.

Reviewed media are the generated mascot/icon, generated icon sizes and menu-bar
mark, plus the exact licensed font files. The image masters show only the project
robot; generated icon metadata contains display/color properties rather than
captured documents or personal images. Font origins and complete terms are in
the [notice inventory](../../THIRD_PARTY_NOTICES.md). The source exporter pins the
reviewed binary paths and hashes, rejecting changed or unexpected binaries until
they are reviewed again.

## Prepare a snapshot

Commit reviewed changes first, run the current checks, then invoke the source
exporter described in [publication hygiene](../../validation/publication-hygiene/README.md).
It reads the selected committed tree, excludes internal continuation notes, and
writes a deterministic archive with a per-file integrity manifest. It includes
neither Git history nor author metadata, ignored local files, signing overrides,
build caches or old recording blobs. Do not substitute `git clone` or make the
development repository public as the publication step.

Extract the archive into a new empty directory. Verify its file manifest, build
and run tests from that directory without borrowing ignored development files,
and initialize a new Git repository. Use a public no-reply author identity for
the initial commit if personal email addresses should remain private. Keep the
upstream copyright notices, source import manifest and independent-project
attribution; a new Git history does not remove attribution obligations.

## Launch decisions

Choose the public name, update app/repository/support URLs together, and commit
the reviewed public tree. The public name is Remy. Create
the new remote only after its destination/name is selected. Push this new history
to that remote, obtain green hosted checks for that exact public commit, and
verify the README's build and test instructions.

GitHub private vulnerability reporting is available for public repositories.
Enable it on the new public repository and test the reporting route; update
SECURITY.md and the issue template's contact link. A successful metadata check
against the existing private repository does not enable this feature there.

An open source preview can state its tested scope and unfinished runtime gates.
A downloadable application has separate [binary release requirements](../../app/RELEASE.md),
including complete shipped notices and actual Developer ID/notarization evidence.
