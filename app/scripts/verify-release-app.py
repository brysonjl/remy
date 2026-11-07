#!/usr/bin/env python3
"""Validate the arm64 manual-update bundle without launching it."""
import os
import plistlib
from pathlib import Path
import subprocess
import sys


def require(condition, message):
    if not condition:
        raise ValueError(message)


def verify(app, version=None, build=None, team=None):
    app = Path(app)
    with (app / 'Contents/Info.plist').open('rb') as stream:
        info = plistlib.load(stream)
    require(info.get('CFBundleIdentifier') == 'io.github.brysonjl.remy', 'Wrong release bundle identity')
    require(not any(info.get(key) for key in ('SUFeedURL', 'SUPublicEDKey', 'SUPublicDSAKey')), 'Updater trust must remain unconfigured')
    require(info.get('SUEnableAutomaticChecks') is False and info.get('SUAutomaticallyUpdate') is False, 'Automatic updates must remain disabled')
    if version is not None:
        require(info.get('CFBundleShortVersionString') == version and str(info.get('CFBundleVersion')) == build, 'Packaged version/build differs from candidate')
    name = info['CFBundleExecutable']
    require(isinstance(name, str) and name not in ('', '.', '..') and '/' not in name and '\\' not in name,
            'Main executable must be a bundle-local filename')
    executable = app / 'Contents/MacOS' / name
    require(executable.is_file() and not executable.is_symlink(), 'Missing or linked main executable')
    require(os.access(executable, os.X_OK), 'Main executable has no executable permission')
    count = 0
    for path in app.rglob('*'):
        if path.is_symlink() or not path.is_file():
            continue
        description = subprocess.check_output(['/usr/bin/file', '-b', str(path)], text=True)
        if 'Mach-O' not in description:
            require(path != executable, 'Main executable is not Mach-O')
            continue
        subprocess.run(['xcrun', 'lipo', str(path), '-verify_arch', 'arm64'], check=True)
        count += 1
    require(count, 'No executable code found')
    if team is not None:
        details = subprocess.check_output(['codesign', '-d', '--verbose=4', str(app)], stderr=subprocess.STDOUT, text=True)
        require(f'TeamIdentifier={team}\n' in details and 'Authority=Developer ID Application:' in details,
                'Wrong distribution signing identity/team')
        require('(runtime)' in details, 'Hardened runtime missing')
        entitlements = plistlib.loads(subprocess.check_output(['codesign', '-d', '--entitlements', ':-', str(app)], stderr=subprocess.DEVNULL))
        require(not entitlements.get('com.apple.security.get-task-allow'), 'Debug entitlement in distributed app')
        require(entitlements.get('com.apple.security.automation.apple-events') is True, 'Missing automation entitlement')
    return count


if __name__ == '__main__':
    try:
        count = verify(*sys.argv[1:])
        print(f'Verified release identity/version, disabled updater, and arm64 in {count} bundle binaries.')
    except (ValueError, OSError, KeyError, subprocess.CalledProcessError) as error:
        sys.exit(f'Release bundle rejected: {error}')
