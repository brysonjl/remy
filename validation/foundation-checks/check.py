#!/usr/bin/env python3
"""Read-only package-pin checks; optionally require the measured A00 toolchain."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import plistlib
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
LOCK = ROOT / 'app/Dictate Anywhere.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved'
EXPECTED_LOCK_SHA256 = '8f72a9d408f6be83aa17962e0671f0322160407f83eacf990e8b464664cf52e1'
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--toolchain', action='store_true')
a = p.parse_args()

def require(condition, message):
    if not condition:
        sys.exit(message)

require(hashlib.sha256(LOCK.read_bytes()).hexdigest() == EXPECTED_LOCK_SHA256,
        'Package.resolved changed from the A00 baseline; deliberate pin review required.')
manifest = json.loads((ROOT / 'research/upstream/dependency-artifacts.json').read_text())
pins = {item['identity']: item for item in json.loads(LOCK.read_text())['pins']}
require(set(pins) == {item['identity'] for item in manifest['packages']}, 'Package inventory differs from artifact manifest.')
for package in manifest['packages']:
    pin = pins[package['identity']]
    require(pin['state']['version'] == package['version'] and pin['state']['revision'] == package['revision'],
            'Package version/revision differs from pinned artifact manifest.')
    require(pin['location'] == package['repository'], 'Package repository differs from artifact manifest.')
print('Pinned lockfile SHA256 and all package identities/repositories/versions/revisions match.')

# Narrow distribution-input hygiene, never user preferences/cache contents.
# A source import has no tracked inventory; its explicit fallback is intentionally
# smaller than a repository-wide secret scan.
private_suffixes = {'.p12', '.pfx', '.p8', '.key', '.mobileprovision', '.provisionprofile'}
def private_name(path):
    return (path.suffix.lower() in private_suffixes or path.name == 'Signing.local.xcconfig'
            or path.name == '.env' or (path.name.startswith('.env.') and path.name != '.env.example'))

if (ROOT / '.git').exists():
    tracked = subprocess.check_output(['git', '-C', str(ROOT), 'ls-files', '-z'], text=True).split('\0')
    inventory = [Path(name) for name in tracked if name]
    require(not any(private_name(path) for path in inventory),
            'Tracked private signing/key/environment material detected; values and paths omitted.')
    inventory_scope = 'tracked inventory plus selected source contents'
else:
    inventory = None
    inventory_scope = 'explicit source-import fallback (no Git inventory)'

selected_roots = [ROOT / 'app/Dictate Anywhere', ROOT / 'app/Config', ROOT / 'app/scripts', ROOT / '.github/workflows']
selected = []
for directory in selected_roots:
    if not directory.exists():
        continue
    for path in directory.rglob('*'):
        if path.is_symlink():
            # Do not follow source links into a user's unrelated filesystem.
            continue
        if not path.is_file():
            continue
        if path.name == 'Signing.local.xcconfig' and inventory is None:
            # The explicitly ignored local override is not a distribution input.
            # Never open it, or imply it was inspected.
            continue
        if inventory is not None and path.relative_to(ROOT) not in inventory:
            continue
        require(not private_name(path), 'Private material selected as distribution input; values and paths omitted.')
        if path.suffix.lower() in {'.swift', '.plist', '.xcconfig', '.sh', '.py', '.yml', '.yaml', '.json', '.entitlements'}:
            selected.append(path)

secret_patterns = [
    re.compile(rb'-----BEGIN (?:RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----'),
    re.compile(rb'(?<![A-Za-z0-9])sk-(?:proj-)?[A-Za-z0-9_-]{24,}'),
    re.compile(rb'Bearer[ \t]+[A-Za-z0-9_-]{24,}(?:\.[A-Za-z0-9_-]+)*'),
]
for path in selected:
    require(path.stat().st_size <= 2_000_000, 'Selected text input exceeds bounded hygiene scan size.')
    contents = path.read_bytes()
    require(not any(pattern.search(contents) for pattern in secret_patterns),
            'Credential/private-key marker detected in selected source; content and path omitted.')

info = plistlib.loads((ROOT / 'app/Dictate Anywhere/Info.plist').read_bytes())
require(not info.get('SUFeedURL') and not info.get('SUPublicEDKey') and not info.get('SUPublicDSAKey'),
        'Source plist must not configure an updater feed or signing key.')
require(info.get('SUEnableAutomaticChecks') is False and info.get('SUAutomaticallyUpdate') is False,
        'Source plist automatic updater flags must be explicitly false.')
project = (ROOT / 'app/Dictate Anywhere.xcodeproj/project.pbxproj').read_text()
require('REMY_APPLICATION_BUNDLE_IDENTIFIER = "io.github.brysonjl.remy.dev";' in project
        and 'REMY_APPLICATION_BUNDLE_IDENTIFIER = "io.github.brysonjl.remy";' in project
        and project.count('PRODUCT_BUNDLE_IDENTIFIER = "$(REMY_APPLICATION_BUNDLE_IDENTIFIER)";') == 2,
        'Distinct Remy development/release bundle IDs missing from project.')
build_script = (ROOT / 'app/scripts/remy-build.sh').read_text()
require('REMY_APPLICATION_BUNDLE_IDENTIFIER=io.github.brysonjl.remy.testhost' in build_script,
        'Ordinary tests must isolate their preference/storage/credential identity.')
print(f'Narrow hygiene passed: {inventory_scope}; {len(selected)} selected text inputs; updater disabled and distinct source bundle IDs present.')
if a.toolchain:
    require(platform.system() == 'Darwin' and platform.machine() == 'arm64', 'Requires an Apple Silicon Mac; no architecture substitution.')
    version = subprocess.check_output(['xcodebuild', '-version'], text=True).strip()
    supported = {
        'Xcode 27.1\nBuild version 27A9275': '27.0',
        'Xcode 27.2\nBuild version 27B52': '27.2',
    }
    require(version in supported,
            'Requires measured Xcode 27.1 (27A9275) or 27.2 (27B52); select the documented DEVELOPER_DIR.')
    sdk = subprocess.check_output(['xcrun', '--sdk', 'macosx', '--show-sdk-version'], text=True).strip()
    require(sdk == supported[version], 'SDK must match the measured compiler pair: 27.1/SDK 27.0 or 27.2/SDK 27.2.')
    print(f'Measured {version.replace(chr(10), " / ")}, macOS SDK {sdk} and arm64 host verified.')
