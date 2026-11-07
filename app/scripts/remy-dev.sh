#!/bin/bash
# Prepare a stable, workspace-local signed development app; launch only explicitly.
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: app/scripts/remy-dev.sh <prepare|launch|help>
  prepare  Build signed Debug with existing local Apple Development identity,
           verify, and stage .local/run/Remy Dev.app; never launches.
  launch   Verify and open the prepared workspace app; never builds.
Requires app/Config/Signing.local.xcconfig with DEVELOPMENT_TEAM and an existing
Apple Development certificate. No provisioning updates or account changes.
EOF
}
fail() { echo "remy-dev: $*" >&2; exit 1; }
case "${1:-}" in
  help|-h|--help) usage; exit 0 ;;
  prepare|launch) action="$1" ;;
  *) usage >&2; exit 64 ;;
esac
[[ $# -eq 1 ]] || { usage >&2; exit 64; }
app_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
workspace="$(cd "$app_root/.." && pwd -P)"
local_root="$workspace/.local"
run_root="$local_root/run"
for directory in "$local_root" "$run_root"; do
  [[ ! -L "$directory" ]] || fail "Refusing symlink directory: $directory"
  [[ ! -e "$directory" || -d "$directory" ]] || fail "Not a directory: $directory"
  mkdir -p "$directory"
done
[[ "$(cd "$run_root" && pwd -P)" == "$run_root" ]] || fail 'Run directory escaped workspace'
lock="$run_root/.remy-dev.lock"
mkdir "$lock" 2>/dev/null || fail 'Another dev action is running, or a stale .remy-dev.lock needs inspection'
trap 'rmdir "$lock"' EXIT
bundle="$run_root/Remy Dev.app"

refuse_running() {
  if /usr/bin/pgrep -x 'Remy Dev' >/dev/null; then
    fail 'Quit Remy Dev (including its XCTest host) before preparing or launching'
  fi
}
verify_bundle() {
  local candidate="$1" identity details
  [[ -d "$candidate" && ! -L "$candidate" ]] || fail "Expected a real app directory: $candidate"
  [[ ! -L "$candidate/Contents" && ! -L "$candidate/Contents/Info.plist" ]] || fail 'Bundle metadata must not be symlinked'
  identity=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$candidate/Contents/Info.plist")
  [[ "$identity" == 'io.github.brysonjl.remy.dev' ]] || fail "Unexpected bundle identity: $identity"
  /usr/bin/codesign --verify --deep --strict "$candidate"
  details=$(/usr/bin/codesign -d --verbose=4 "$candidate" 2>&1)
  [[ "$details" == *'Authority=Apple Development:'* ]] || fail 'App is not signed with an Apple Development certificate'
  [[ "$details" == *'TeamIdentifier='* && "$details" != *'TeamIdentifier=not set'* ]] || fail 'Missing signing team'
}
refuse_running
if [[ "$action" == prepare ]]; then
  signing_config="$app_root/Config/Signing.local.xcconfig"
  [[ -f "$signing_config" && ! -L "$signing_config" ]] || fail 'Create a regular app/Config/Signing.local.xcconfig with DEVELOPMENT_TEAM first'
  /usr/bin/grep -Eq '^[[:space:]]*DEVELOPMENT_TEAM[[:space:]]*=[[:space:]]*[A-Z0-9]{10}[[:space:]]*;?[[:space:]]*$' "$signing_config" \
    || fail 'Local signing config must specify a literal ten-character DEVELOPMENT_TEAM'
  "$app_root/scripts/remy-build.sh" build Debug \
    -xcconfig "$signing_config" \
    CODE_SIGNING_ALLOWED=YES CODE_SIGNING_REQUIRED=YES \
    CODE_SIGN_STYLE=Automatic 'CODE_SIGN_IDENTITY=Apple Development'
  source_bundle="$app_root/.build/DerivedData/Build/Products/Debug/Remy Dev.app"
  verify_bundle "$source_bundle"
  stage=$(/usr/bin/mktemp -d "$run_root/.remy-dev-stage.XXXXXX")
  /usr/bin/ditto "$source_bundle" "$stage/Remy Dev.app"
  verify_bundle "$stage/Remy Dev.app"
  refuse_running
  [[ ! -L "$bundle" ]] || fail 'Refusing to replace a symlink app'
  if [[ -e "$bundle" ]]; then
    verify_bundle "$bundle"
    backup=$(/usr/bin/mktemp -d "$run_root/.remy-dev-backup.XXXXXX")
    /bin/mv "$bundle" "$backup/Remy Dev.app"
    if ! /bin/mv "$stage/Remy Dev.app" "$bundle"; then
      /bin/mv "$backup/Remy Dev.app" "$bundle"
      fail 'Staging failed; previous app restored'
    fi
    echo "Previous app retained at: $backup/Remy Dev.app"
  else
    /bin/mv "$stage/Remy Dev.app" "$bundle"
  fi
  rmdir "$stage"
  verify_bundle "$bundle"
  echo "Prepared: $bundle"
else
  verify_bundle "$bundle"
  /usr/bin/open -n "$bundle"
  echo "Opened: $bundle"
fi
