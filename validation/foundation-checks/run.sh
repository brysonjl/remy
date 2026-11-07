#!/bin/bash
# Ordinary validation only. Never starts signed GUI, release or model/provider jobs.
set -euo pipefail
foundation_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$foundation_root"
python3 validation/foundation-checks/check.py --toolchain

# An inherited S1 test can opt in through this file even without environment flags.
if [[ -e /private/tmp/dictate-anywhere-s1-mini-q4_k_m.gguf ]]; then
  echo 'Ordinary validation refuses the legacy /private/tmp S1 opt-in fixture. Use a clean runner.' >&2
  exit 65
fi

# Allow only toolchain/OS essentials. Do not inherit TEST_RUNNER_*, provider keys,
# model paths, benchmark flags, signing overrides or live hardware test opt-ins.
foundation_developer_dir="${DEVELOPER_DIR:-$(xcode-select -p)}"
ordinary() {
  env -i HOME="$HOME" PATH="$PATH" LANG=en_US.UTF-8 \
    DEVELOPER_DIR="$foundation_developer_dir" GIT_TERMINAL_PROMPT=0 \
    "$@"
}
# Validate the lock even on a failed subprocess; never rewrite it for CI.
trap 'python3 validation/foundation-checks/check.py' EXIT
mkdir -p .local/foundation-checks
ordinary bash app/scripts/remy-build.sh resolve
ordinary python3 validation/dependency-baseline/verify.py --download-missing \
  --output .local/foundation-checks/dependency-verification.json
ordinary python3 validation/check-third-party-notices.py --native-artifacts
ordinary bash app/scripts/remy-build.sh build
ordinary bash app/scripts/remy-build.sh build Release
ordinary bash app/scripts/remy-build.sh test -parallel-testing-enabled NO
ordinary sh validation/run-tests.sh
