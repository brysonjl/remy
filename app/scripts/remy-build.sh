#!/bin/bash
# Build the pinned Remy fork without installing or packaging it.
# The test action uses an isolated app identity, with production startup suppressed.
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: app/scripts/remy-build.sh <build|build-for-testing|test|resolve|list> [Debug|Release] [xcodebuild arguments...]

Defaults to Debug. Build and test disable signing for a local baseline.
Examples:
  app/scripts/remy-build.sh list
  app/scripts/remy-build.sh resolve
  app/scripts/remy-build.sh build
  app/scripts/remy-build.sh test -only-testing:'Dictate AnywhereTests/ModelReadinessTests'
  app/scripts/remy-build.sh build Release

Build, package, and module-cache outputs use app/.build. Versions must match the committed
Package.resolved. Resolve may download pinned sources and binary frameworks.
Build/test/list do not update pinned versions, but Xcode may fetch missing
dependencies even with automatic resolution disabled. Populate local packages
with resolve in a network-enabled environment before an offline build attempt.
Additional arguments pass directly to xcodebuild. No app is installed.
The test action launches Remy Test Host with isolated storage and credentials;
production startup is skipped. build-for-testing compiles without launching it.
USAGE
}

if [[ $# -eq 0 ]]; then
  usage >&2
  exit 64
fi

action="$1"
shift
case "$action" in
  -h|--help|help) usage; exit 0 ;;
  build|build-for-testing|test|resolve|list) ;;
  *) usage >&2; exit 64 ;;
esac

configuration=Debug
if [[ ${1:-} == Debug || ${1:-} == Release ]]; then
  configuration="$1"
  shift
fi

app_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$app_root/Dictate Anywhere.xcodeproj"
scheme='Dictate Anywhere'
lockfile="$project/project.xcworkspace/xcshareddata/swiftpm/Package.resolved"
build_root="$app_root/.build"

if [[ ! -f "$lockfile" ]]; then
  echo "Missing pinned package lockfile: $lockfile" >&2
  exit 66
fi

mkdir -p "$build_root/DerivedData" "$build_root/SourcePackages" \
  "$build_root/cache/packages" "$build_root/cache/clang" \
  "$build_root/cache/swift" "$build_root/tmp"

# Leave HOME unchanged and route package/module outputs to this workspace.
export TMPDIR="$build_root/tmp/"
export XDG_CACHE_HOME="$build_root/cache"
export CLANG_MODULE_CACHE_PATH="$build_root/cache/clang"
export SWIFTPM_MODULECACHE_OVERRIDE="$build_root/cache/swift"

arguments=(
  -project "$project"
  -scheme "$scheme"
  -configuration "$configuration"
  -derivedDataPath "$build_root/DerivedData"
  -clonedSourcePackagesDirPath "$build_root/SourcePackages"
  -packageCachePath "$build_root/cache/packages"
  -disablePackageRepositoryCache
  -onlyUsePackageVersionsFromResolvedFile
)

case "$action" in
  resolve)
    arguments+=(-resolvePackageDependencies)
    ;;
  list)
    arguments+=(-disableAutomaticPackageResolution -skipPackageUpdates -list -json)
    ;;
  build|build-for-testing|test)
    arguments+=(
      -disableAutomaticPackageResolution
      -skipPackageUpdates
      -destination 'platform=macOS,arch=arm64'
      CODE_SIGNING_ALLOWED=NO
      "CLANG_MODULE_CACHE_PATH=$build_root/cache/clang"
      "SWIFT_MODULE_CACHE_PATH=$build_root/cache/swift"
      "$action"
    )
    ;;
esac

if [[ "$action" == test || "$action" == build-for-testing ]]; then
  # XCTest fixtures mutate UserDefaults and may terminate before tearDown.
  # A separate bundle identity also isolates Application Support and Keychain
  # service names, without reading or copying development preferences/secrets.
  arguments+=(REMY_APPLICATION_BUNDLE_IDENTIFIER=io.github.brysonjl.remy.testhost)
  arguments+=("INFOPLIST_KEY_CFBundleDisplayName=Remy Test Host")
fi

exec xcodebuild "${arguments[@]}" "$@"
