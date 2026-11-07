# Publication hygiene

Run from the repository root before publishing changes:

```sh
python3 validation/publication-hygiene/check.py
python3 -m unittest discover -s validation/publication-hygiene -p 'test_*.py'
```

The checker reads blobs reachable from all local Git refs, plus tracked and
nonignored untracked working files. It does not fetch refs or read ignored local
configuration, preferences, Keychain, model caches, or installed application data.
Working-tree symlinks are reported without following their targets.

Output includes file paths, historical blob IDs, match categories and counts;
it never includes matched content. Keep the report local until its filenames
have been reviewed. Exit status 1 means a credential-pattern or sensitive-filename
finding, 2 means the scan could not finish, and 0 means no such pattern was found.
Machine paths and unscanned binary/large files are manual-review items even when
the exit status is 0. A match requires review; it is not automatically proof of
a live credential. Never paste a matching value into an issue or test fixture.

This is a bounded pattern scan, not security clearance. Review audio, screenshots,
media metadata, arbitrary secret formats, personal/project references, author
email metadata, and redistribution provenance separately. Remote-only refs and
unreachable objects are outside its scope. A deleted file can remain public in
Git history; removing it from the working tree does not resolve that disclosure.
Any history rewrite requires a separate owner decision and coordination with
existing clones. Rotate an exposed credential rather than relying on deletion.

The portable CI job fetches full available history and runs this checker, keeping
the detailed report in its temporary directory rather than uploading it or
printing matching paths/content. The log shows only aggregate manual-review
counts. CI cannot replace the owner's media, privacy and provenance review.

## Export a reviewed committed source snapshot

This exporter produced the initial public snapshot from the private development
repository (see [how this repository was published](../../docs/engineering/publication.md)).
It reads only committed Git blobs at the supplied full lowercase commit SHA
(symbolic names such as `HEAD` are refused), never working-tree bytes, commit
messages, author identities, ignored configuration, or `.git` internals. It
omits a root `HANDOFF.md` internal-notes file if one exists; no source history
is rewritten.

```sh
python3 validation/publication-hygiene/export-source.py \
  --revision EXACT_COMMIT_SHA --output .local/public-source.tar
```

`--output` must be new; existing files, including links, are never replaced.
The tar is deterministic for that committed source and includes `SOURCE_MANIFEST.json`
with the resolved source SHA and payload SHA256/modes/link targets. File
owner/group names and timestamps are neutral. No public repository is created,
no Git identity is assigned, and nothing is uploaded. Extract into a new empty
review directory, inspect the manifest, run clean-source validation, and only then
initialize a new repository using the owner's chosen public author identity.
The root `SOURCE_MANIFEST.json` in this repository is the manifest of the
initial export and is not updated afterwards.

The exporter fails on recognized credentials/private filenames, audio/media,
actual machine paths in text, unsupported binary content, submodules, unsafe paths,
and escaping/cyclic/broken links. Only relative validation links resolving to an
included regular source file are preserved. PNG and font files require an exact
path **and SHA256** in the committed `binary-allowlist.json`; other binary formats
are refused. That initial list records the existing generated icon/mascot PNGs and
font bytes for explicit asset/license review. Changing an asset requires reviewing
its bytes/provenance and updating its hash; adding an extension-wide exception is
not supported. A matching hash is not a claim of complete license clearance or
that image metadata/content has been independently sanitized.

The same narrow credential patterns as `check.py` apply. Pattern checks cannot
prove arbitrary secrets absent. The exported tree still needs human source,
asset/privacy, licensing, and functional review. Original locally reachable
history remains covered by `check.py`; an export succeeds only for its selected
source tree, not the source repository's whole history. The helper itself does
not certify a release or notarize an application.
