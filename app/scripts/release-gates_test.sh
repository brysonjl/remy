#!/usr/bin/env bash
# Stub only external notarization tools; never signs, launches, or submits anything.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/release-gates.sh"
TEST_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEST_ROOT"' EXIT
mkdir -p "$TEST_ROOT/bin"
cat > "$TEST_ROOT/bin/xcrun" <<'STUB'
#!/bin/bash
printf '%s\n' "$*" >> "$CALLS"
case "$1 $2" in
  'notarytool submit')
    if [[ "$CASE" == transport ]]; then exit 1; fi
    if [[ "$CASE" == rejected ]]; then printf '{"status":"Invalid"}\n';
    elif [[ "$CASE" == malformed ]]; then printf 'not-json\n';
    else printf '{"status":"Accepted","id":"synthetic-test-only"}\n'; fi ;;
  'stapler staple') [[ "$CASE" != staple ]] ;;
  'stapler validate') [[ "$CASE" != validate ]] ;;
  *) exit 99 ;;
esac
STUB
chmod +x "$TEST_ROOT/bin/xcrun"
export PATH="$TEST_ROOT/bin:$PATH" NOTARY_PROFILE='synthetic-test-only' CALLS="$TEST_ROOT/calls"
for CASE in transport rejected malformed staple validate success; do
  export CASE
  : > "$CALLS"
  # A subshell models production set -e; function conditional additionally exercises
  # explicit status propagation when callers use it as a condition.
  if (notarize_and_validate 'fixture with spaces.zip' 'fixture with spaces.app' "$TEST_ROOT/report.json") > "$TEST_ROOT/output" 2>&1; then
    [[ "$CASE" == success ]] || { echo "Incorrectly accepted: $CASE"; exit 1; }
  else
    [[ "$CASE" != success ]] || { cat "$TEST_ROOT/output"; exit 1; }
  fi
  if [[ "$CASE" == transport || "$CASE" == rejected || "$CASE" == malformed ]]; then
    ! grep -q '^stapler ' "$CALLS"
  fi
  if [[ "$CASE" == staple ]]; then ! grep -q '^stapler validate' "$CALLS"; fi
  if [[ "$CASE" == success ]]; then grep -q '^stapler validate fixture with spaces.app$' "$CALLS"; fi
done
printf 'Six notarization/stapling gate scenarios passed (stub tools only).\n'
