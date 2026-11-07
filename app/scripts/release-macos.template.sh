#!/usr/bin/env bash
set -euo pipefail
# Copy to scripts/release-macos.sh (ignored). Run from app/ after reviewing RELEASE.md.
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "$ROOT_DIR/.." && pwd)"
PROJECT_PATH="$ROOT_DIR/Dictate Anywhere.xcodeproj"
SCHEME="Dictate Anywhere"
APP_NAME="Remy.app"
DIST_DIR="${DIST_DIR:-$ROOT_DIR/dist}"
NOTARY_PROFILE="${NOTARY_PROFILE:?Set your own Keychain notary profile}"
TEAM_ID="${TEAM_ID:?Set your own Apple developer team}"
DEVELOPER_ID_APP="${DEVELOPER_ID_APP:?Set your Developer ID Application identity}"
RELEASE_VERSION="${RELEASE_VERSION:?Choose an independent prerelease version (see RELEASE.md)}"
RELEASE_BUILD="${RELEASE_BUILD:?Choose the independent build number}"
[[ "$DEVELOPER_ID_APP" == 'Developer ID Application:'* ]] || { echo 'Use a Developer ID Application certificate name.' >&2; exit 1; }
[[ "$RELEASE_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ && "$RELEASE_BUILD" =~ ^[1-9][0-9]*$ ]] || { echo 'Use numeric major.minor.patch and a positive build number; mark the GitHub release prerelease.' >&2; exit 1; }
for command in xcodebuild xcrun ditto codesign spctl create-dmg hdiutil python3 git security; do
  command -v "$command" >/dev/null || { echo "Missing command: $command" >&2; exit 1; }
done
source "$ROOT_DIR/scripts/release-gates.sh"
# A release must identify committed, reviewed inputs. No dirty-tree override.
[[ -z "$(git -C "$REPO_ROOT" status --porcelain)" ]] || { echo 'Commit reviewed source before packaging a release.' >&2; exit 1; }
SOURCE_COMMIT="$(git -C "$REPO_ROOT" rev-parse HEAD)"
python3 "$REPO_ROOT/validation/foundation-checks/check.py" --toolchain
security find-identity -v -p codesigning | grep -F "$DEVELOPER_ID_APP" >/dev/null
xcrun notarytool history --keychain-profile "$NOTARY_PROFILE" >/dev/null
# Resolve the exact lock using the contributor cache, then independently verify pins/artifacts.
bash "$ROOT_DIR/scripts/remy-build.sh" resolve
mkdir -p "$DIST_DIR"
CANDIDATE_DIR="$(mktemp -d "$DIST_DIR/.candidate.XXXXXX")"
TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/remy-release.XXXXXX")"
MOUNT_POINT=""
cleanup() {
  if [[ -n "$MOUNT_POINT" ]]; then hdiutil detach "$MOUNT_POINT" -quiet || true; fi
  rm -rf "$TEMP_DIR" "$CANDIDATE_DIR"
}
trap cleanup EXIT
python3 "$REPO_ROOT/validation/dependency-baseline/verify.py" --download-missing \
  --output "$CANDIDATE_DIR/dependencies.json"
ARCHIVE_PATH="$TEMP_DIR/Remy.xcarchive"
APP_PATH="$CANDIDATE_DIR/$APP_NAME"
DMG_NAME="Remy-${RELEASE_VERSION}-arm64.dmg"
DMG_ASSET="$CANDIDATE_DIR/$DMG_NAME"
OUTPUT_DIR="$DIST_DIR/Remy-${RELEASE_VERSION}-${RELEASE_BUILD}-arm64"
[[ ! -e "$OUTPUT_DIR" ]] || { echo 'Candidate output already exists; choose a new build or archive it manually.' >&2; exit 1; }
xcodebuild -project "$PROJECT_PATH" -scheme "$SCHEME" -configuration Release \
  -derivedDataPath "$ROOT_DIR/.build/DerivedData" \
  -clonedSourcePackagesDirPath "$ROOT_DIR/.build/SourcePackages" \
  -packageCachePath "$ROOT_DIR/.build/cache/packages" -disablePackageRepositoryCache \
  -onlyUsePackageVersionsFromResolvedFile -disableAutomaticPackageResolution -skipPackageUpdates \
  -destination 'generic/platform=macOS' -archivePath "$ARCHIVE_PATH" \
  ARCHS=arm64 ONLY_ACTIVE_ARCH=NO \
  SWIFT_ACTIVE_COMPILATION_CONDITIONS='$(inherited) DISTRIBUTION_BUILD' \
  MARKETING_VERSION="$RELEASE_VERSION" CURRENT_PROJECT_VERSION="$RELEASE_BUILD" \
  CODE_SIGN_IDENTITY="$DEVELOPER_ID_APP" CODE_SIGN_STYLE=Manual DEVELOPMENT_TEAM="$TEAM_ID" archive
cp -R "$ARCHIVE_PATH/Products/Applications/$APP_NAME" "$APP_PATH"
NOTICES="$APP_PATH/Contents/Resources/OpenSourceNotices"
mkdir -p "$NOTICES/docs/engineering" "$NOTICES/app" "$NOTICES/research/upstream/mac-dictate-anywhere"
for notice in LICENSE ATTRIBUTION.md THIRD_PARTY_NOTICES.md; do
  cp "$REPO_ROOT/$notice" "$NOTICES/"
done
cp "$ROOT_DIR/LICENSE" "$NOTICES/app/LICENSE"
cp -R "$REPO_ROOT/docs/third-party" "$NOTICES/docs/third-party"
cp "$REPO_ROOT/docs/engineering/asset-provenance.md" "$NOTICES/docs/engineering/"
cp "$REPO_ROOT/research/upstream/mac-dictate-anywhere/TREE.json" "$NOTICES/research/upstream/mac-dictate-anywhere/"
# Re-sign inherited Sparkle helpers if present, without configuring or generating a feed.
SPARKLE="$APP_PATH/Contents/Frameworks/Sparkle.framework/Versions/B"
if [[ -d "$SPARKLE" ]]; then
  for relative in Autoupdate XPCServices/Downloader.xpc XPCServices/Installer.xpc Updater.app; do
    codesign --force --sign "$DEVELOPER_ID_APP" --timestamp --options runtime \
      --preserve-metadata=identifier,entitlements,requirements,flags "$SPARKLE/$relative"
  done
  codesign --force --sign "$DEVELOPER_ID_APP" --timestamp --options runtime \
    --preserve-metadata=identifier,entitlements,requirements,flags "$APP_PATH/Contents/Frameworks/Sparkle.framework"
fi
codesign --force --sign "$DEVELOPER_ID_APP" --timestamp --options runtime \
  --preserve-metadata=identifier,entitlements,requirements,flags "$APP_PATH"
python3 "$ROOT_DIR/scripts/verify-release-app.py" "$APP_PATH" "$RELEASE_VERSION" "$RELEASE_BUILD" "$TEAM_ID"
codesign --verify --deep --strict --verbose=2 "$APP_PATH"
ditto -c -k --sequesterRsrc --keepParent "$APP_PATH" "$TEMP_DIR/Remy-notary.zip"
notarize_and_validate "$TEMP_DIR/Remy-notary.zip" "$APP_PATH" "$CANDIDATE_DIR/app-notary.json"
spctl -a -vv "$APP_PATH"
create-dmg --volname Remy --window-pos 200 120 --window-size 600 400 \
  --icon-size 100 --icon "$APP_NAME" 150 185 --app-drop-link 450 185 "$DMG_ASSET" "$APP_PATH"
notarize_and_validate "$DMG_ASSET" "$DMG_ASSET" "$CANDIDATE_DIR/dmg-notary.json"
MOUNT_POINT="$TEMP_DIR/mounted"
mkdir -p "$MOUNT_POINT"
hdiutil attach "$DMG_ASSET" -mountpoint "$MOUNT_POINT" -nobrowse -quiet
python3 "$ROOT_DIR/scripts/verify-release-app.py" "$MOUNT_POINT/$APP_NAME" "$RELEASE_VERSION" "$RELEASE_BUILD" "$TEAM_ID"
codesign --verify --deep --strict --verbose=2 "$MOUNT_POINT/$APP_NAME"
spctl -a -vv "$MOUNT_POINT/$APP_NAME"
hdiutil detach "$MOUNT_POINT" -quiet
MOUNT_POINT=""
python3 "$REPO_ROOT/validation/foundation-checks/check.py"
[[ "$(git -C "$REPO_ROOT" rev-parse HEAD)" == "$SOURCE_COMMIT" && -z "$(git -C "$REPO_ROOT" status --porcelain)" ]] || { echo 'Source changed during packaging.' >&2; exit 1; }
python3 - "$REPO_ROOT" "$CANDIDATE_DIR" "$SOURCE_COMMIT" "$RELEASE_VERSION" "$RELEASE_BUILD" "$DMG_NAME" <<'PY'
import datetime, hashlib, json, pathlib, subprocess, sys
root, output = map(pathlib.Path, sys.argv[1:3])
def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()
artifact = output / sys.argv[6]
metadata = dict(schema_version=1, source_commit=sys.argv[3], version=sys.argv[4], build=sys.argv[5],
    architecture='arm64', update_policy='manual', created_at=datetime.datetime.now(datetime.timezone.utc).isoformat(),
    toolchain=subprocess.check_output(['xcodebuild', '-version'], text=True).strip(),
    sdk=subprocess.check_output(['xcrun', '--sdk', 'macosx', '--show-sdk-version'], text=True).strip(),
    lockfile_sha256=digest(root / 'app/Dictate Anywhere.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved'),
    model_manifest_sha256=digest(root / 'app/Dictate Anywhere/ModelManifests/parakeet-v2.json'),
    artifact=dict(name=artifact.name, sha256=digest(artifact), size_bytes=artifact.stat().st_size))
(output / 'release.json').write_text(json.dumps(metadata, indent=2) + '\n')
(output / 'SHA256SUMS').write_text(f"{metadata['artifact']['sha256']}  {artifact.name}\n")
PY
rm -rf "$APP_PATH"
# Only promote a complete candidate after every hard gate succeeds. This never uploads.
mv "$CANDIDATE_DIR" "$OUTPUT_DIR"
printf 'Validated manual-update candidate: %s\nReview RELEASE.md before publication.\n' "$OUTPUT_DIR"
