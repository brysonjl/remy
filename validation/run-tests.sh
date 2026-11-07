#!/bin/sh
set -eu

validation_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
mkdir -p "$validation_root/.build/module-cache"
export CLANG_MODULE_CACHE_PATH="$validation_root/.build/module-cache"
export SWIFT_MODULECACHE_PATH="$validation_root/.build/module-cache"

exec swift test \
  --package-path "$validation_root" \
  --cache-path "$validation_root/.build/swiftpm-cache" \
  --config-path "$validation_root/.build/swiftpm-config" \
  --security-path "$validation_root/.build/swiftpm-security" \
  --scratch-path "$validation_root/.build" \
  --manifest-cache local \
  --disable-netrc \
  --disable-keychain \
  "$@"
