# Optional local speech fixtures

The six imported WAV files have been omitted from the publishable working tree
because audio-specific redistribution provenance was unresolved. Earlier Git
history still contains them; a working-tree deletion does not remove old blobs.
See [asset provenance](../../../docs/engineering/asset-provenance.md) for release
policy and instructions for locally supplied fixtures.

Tests load recordings only from the absolute directory explicitly supplied in
`REMY_SPEECH_FIXTURES_DIR`; they do not search this folder or the test bundle.
Recordings and reference transcripts placed here are ignored as a precaution.
