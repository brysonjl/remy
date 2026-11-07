#!/bin/bash
# Local signed Release preparation/install only. No launch, migration or publication.
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: app/scripts/remy-local.sh <prepare|install|help>
  prepare  Build signed Release using existing Signing.local.xcconfig and Apple
           Development certificate; verify and stage .local/release/Remy.app.
  install  Verify that prepared app and install ONLY to ~/Applications/Remy.app.
           Refuse running apps, symlinks and unrelated existing bundles. Retain
           previous owned app in a printed rollback directory; never launch.

Prepare and install are separate explicit actions. This is a local Mac install,
not notarized distribution. No sudo, account/provisioning changes, updater feed,
release packaging, upload, credential copying or preferences migration occurs.
Quit Remy and Remy Dev before either action. The existing pinned build
helper controls toolchain/packages; prepare may fetch missing pinned dependencies.
USAGE
}
fail() { echo "remy-local: $*" >&2; exit 1; }
case "${1:-}" in
  help|-h|--help) usage; exit 0 ;;
  prepare|install) action="$1" ;;
  *) usage >&2; exit 64 ;;
esac
[[ $# -eq 1 ]] || { usage >&2; exit 64; }
app_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
workspace="$(cd "$app_root/.." && pwd -P)"
release_root="$workspace/.local/release"
owner="$(/usr/bin/id -u)"

owned_directory() {
  local directory="$1"
  [[ ! -L "$directory" ]] || fail "Refusing symlink directory: $directory"
  [[ ! -e "$directory" || -d "$directory" ]] || fail "Not a directory: $directory"
  if [[ ! -e "$directory" ]]; then /bin/mkdir -m 700 "$directory"; fi
  [[ "$(cd "$directory" && pwd -P)" == "$directory" ]] || fail 'Directory has a symlink ancestor'
  [[ "$(/usr/bin/stat -f %u "$directory")" == "$owner" ]] || fail 'Directory is not owned by this user'
}
owned_directory "$workspace/.local"
owned_directory "$release_root"
lock="$release_root/.remy-local.lock"
/bin/mkdir "$lock" 2>/dev/null || fail 'Another local action is running, or a stale release lock needs inspection'
install_lock=''
stage=''
cleanup() {
  # Remove only empty stages; failed nonempty stages remain for inspection.
  if [[ -n "$stage" ]]; then /bin/rmdir "$stage" 2>/dev/null || true; fi
  if [[ -n "$install_lock" ]]; then /bin/rmdir "$install_lock" 2>/dev/null || true; fi
  /bin/rmdir "$lock" 2>/dev/null || true
}
trap cleanup EXIT

refuse_running() {
  if /usr/bin/pgrep -x 'Remy' >/dev/null || /usr/bin/pgrep -x 'Remy Dev' >/dev/null; then
    fail 'Quit Remy and Remy Dev before preparing or installing; no app will be stopped automatically'
  fi
}
# Every failure is local and safe to print; never read signing private keys.
verify_bundle() {
  local candidate="$1" details identity executable checks team
  [[ -d "$candidate" && ! -L "$candidate" ]] || fail 'Expected a real app bundle directory'
  [[ "$(cd "$candidate" && pwd -P)" == "$candidate" ]] || fail 'Bundle has a symlink ancestor'
  [[ ! -L "$candidate/Contents" && ! -L "$candidate/Contents/Info.plist" && ! -L "$candidate/Contents/MacOS" ]] || fail 'Bundle metadata/executable directory must not be symlinked'
  [[ "$(/usr/bin/stat -f %u "$candidate")" == "$owner" ]] || fail 'App bundle is not owned by this user'
  identity=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$candidate/Contents/Info.plist")
  executable=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$candidate/Contents/Info.plist")
  [[ "$identity" == 'io.github.brysonjl.remy' && "$executable" == 'Remy' ]] || fail 'Refusing unrelated app identity/executable'
  [[ -f "$candidate/Contents/MacOS/Remy" && ! -L "$candidate/Contents/MacOS/Remy" ]] || fail 'Missing regular Remy executable'
  checks=$(/usr/libexec/PlistBuddy -c 'Print :SUEnableAutomaticChecks' "$candidate/Contents/Info.plist")
  [[ "$checks" == false ]] || fail 'Automatic updater checks must be disabled'
  if /usr/libexec/PlistBuddy -c 'Print :SUFeedURL' "$candidate/Contents/Info.plist" >/dev/null 2>&1; then
    fail 'Refusing app with an updater feed'
  fi
  /usr/bin/codesign --verify --deep --strict "$candidate"
  details=$(/usr/bin/codesign -d --verbose=4 "$candidate" 2>&1)
  [[ "$details" == *'Authority=Apple Development:'* && "$details" == *'Identifier=io.github.brysonjl.remy'* ]] || fail 'Expected Apple Development signed Remy Release'
  team=$(echo "$details" | /usr/bin/sed -n 's/^TeamIdentifier=//p')
  [[ "$team" =~ ^[A-Z0-9]{10}$ ]] || fail 'Missing valid signing team'
  if [[ -n "${expected_team:-}" ]]; then [[ "$team" == "$expected_team" ]] || fail 'Refusing app signed by a different team'; fi
}
team_for() {
  /usr/bin/codesign -d --verbose=4 "$1" 2>&1 | /usr/bin/sed -n 's/^TeamIdentifier=//p'
}

# Same-filesystem staging and backup preserve the prior app for manual rollback.
replace_owned_bundle() {
  local source="$1" destination="$2" parent="$3" backup
  stage=$(/usr/bin/mktemp -d "$parent/.remy-local-stage.XXXXXX")
  /usr/bin/ditto "$source" "$stage/Remy.app"
  verify_bundle "$stage/Remy.app"
  refuse_running
  [[ ! -L "$destination" ]] || fail 'Refusing symlink app destination'
  backup=''
  if [[ -e "$destination" ]]; then
    verify_bundle "$destination"
    backup=$(/usr/bin/mktemp -d "$parent/.remy-local-backup.XXXXXX")
    /bin/mv "$destination" "$backup/Remy.app"
  fi
  # Address the containing directory so a newly appeared Remy.app is
  # skipped, never treated as a directory in which to nest another app.
  if ! /bin/mv -n "$stage/Remy.app" "$parent/" || [[ -e "$stage/Remy.app" ]]; then
    if [[ -n "$backup" && ! -e "$destination" && ! -L "$destination" ]]; then
      /bin/mv -n "$backup/Remy.app" "$parent/"
    fi
    fail "Placement refused; previous bundle, if any, retained/restored at: $backup"
  fi
  if ! (verify_bundle "$destination"); then
    /bin/mv "$destination" "$stage/Remy.app"
    if [[ -n "$backup" ]]; then /bin/mv "$backup/Remy.app" "$destination"; fi
    fail 'Installed verification failed; previous app restored when present, failed stage retained'
  fi
  /bin/rmdir "$stage"
  stage=''
  if [[ -n "$backup" ]]; then echo "Previous owned app retained for rollback: $backup/Remy.app"; fi
  echo "Ready (not launched): $destination"
}

refuse_running
prepared="$release_root/Remy.app"
if [[ "$action" == prepare ]]; then
  signing_config="$app_root/Config/Signing.local.xcconfig"
  [[ -f "$signing_config" && ! -L "$signing_config" ]] || fail 'Create regular app/Config/Signing.local.xcconfig with DEVELOPMENT_TEAM first'
  /usr/bin/grep -Eq '^[[:space:]]*DEVELOPMENT_TEAM[[:space:]]*=[[:space:]]*[A-Z0-9]{10}[[:space:]]*;?[[:space:]]*$' "$signing_config" \
    || fail 'Signing.local.xcconfig must specify a literal ten-character DEVELOPMENT_TEAM'
  "$app_root/scripts/remy-build.sh" build Release \
    -xcconfig "$signing_config" \
    REMY_APPLICATION_BUNDLE_IDENTIFIER=io.github.brysonjl.remy \
    CODE_SIGNING_ALLOWED=YES CODE_SIGNING_REQUIRED=YES \
    CODE_SIGN_STYLE=Automatic 'CODE_SIGN_IDENTITY=Apple Development'
  source_bundle="$app_root/.build/DerivedData/Build/Products/Release/Remy.app"
  verify_bundle "$source_bundle"
  expected_team="$(team_for "$source_bundle")"
  replace_owned_bundle "$source_bundle" "$prepared" "$release_root"
else
  verify_bundle "$prepared"
  expected_team="$(team_for "$prepared")"
  [[ -n "${HOME:-}" && "$HOME" == /* ]] || fail 'A real user home directory is required'
  [[ "$(cd "$HOME" && pwd -P)" == "$HOME" ]] || fail 'Refusing home directory with symlink ancestors'
  applications="$HOME/Applications"
  owned_directory "$applications"
  requested_lock="$applications/.remy-local-install.lock"
  /bin/mkdir "$requested_lock" 2>/dev/null || fail 'Another install is running, or a stale Applications lock needs inspection'
  install_lock="$requested_lock"
  replace_owned_bundle "$prepared" "$applications/Remy.app" "$applications"
fi
