#!/usr/bin/env bash
# Shared fail-closed distribution checks. Sourcing this file has no side effects.
notarize_and_validate() {
  local artifact="$1" ticket="$2" report="$3"
  xcrun notarytool submit "$artifact" --keychain-profile "$NOTARY_PROFILE" \
    --wait --output-format json > "$report" || return 1
  python3 - "$report" <<'PY'
import json, sys
with open(sys.argv[1]) as stream:
    result = json.load(stream)
if result.get('status') != 'Accepted':
    sys.exit('Notarization was not Accepted; do not distribute this candidate.')
PY
  # notarytool can exit successfully for a rejected submission: check JSON above.
  # Explicit return also keeps this gate safe when called from a shell conditional.
  if [[ $? -ne 0 ]]; then return 1; fi
  xcrun stapler staple "$ticket" || return 1
  xcrun stapler validate "$ticket" || return 1
}
