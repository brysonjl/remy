#!/usr/bin/env python3
"""Verify pinned checkouts and release ZIP bytes without changing SwiftPM caches."""
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import subprocess
import sys
from urllib.parse import urlparse

ROOT = Path(__file__).resolve().parents[2]
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--download-missing', action='store_true', help='Fetch exact manifest URLs into .local/dependency-archives')
p.add_argument('--output', type=Path, default=Path(__file__).with_name('verification.json'))
a = p.parse_args()
manifest_path = ROOT / 'research/upstream/dependency-artifacts.json'
lock_path = ROOT / 'app/Dictate Anywhere.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved'
manifest = json.loads(manifest_path.read_text())
pins = {x['identity']: x for x in json.loads(lock_path.read_text())['pins']}
cache_roots = [ROOT / 'app/.build', Path.home() / 'Library/Caches/org.swift.swiftpm', ROOT / '.local/dependency-archives']

def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()

def git(path, *args):
    return subprocess.check_output(['git', '-C', str(path), *args], text=True).strip()

out = {'schema_version': 1, 'verified_at': datetime.datetime.now(datetime.timezone.utc).isoformat(),
       'lockfile_sha256': digest(lock_path), 'artifact_manifest_sha256': digest(manifest_path),
       'scope': 'Independent hashes of release ZIP bytes; does not attest extracted build-cache contents or constitute an app/runtime test.',
       'packages': []}
for package in manifest['packages']:
    identity = package['identity']
    checkout = ROOT / 'app/.build/SourcePackages/checkouts' / {'fluidaudio': 'FluidAudio', 'sparkle': 'Sparkle'}.get(identity, identity)
    pin = pins[identity]
    revision = git(checkout, 'rev-parse', 'HEAD')
    dirty = git(checkout, 'status', '--porcelain', '--untracked-files=no')
    row = {'identity': identity, 'version': pin['state']['version'], 'expected_revision': pin['state']['revision'],
           'checkout_revision': revision, 'checkout_tracked_changes': dirty,
           'revision_matches': revision == pin['state']['revision'] == package['revision'],
           'version_matches_manifest': pin['state']['version'] == package['version'], 'artifacts': []}
    for artifact in package['artifacts']:
        filename = Path(urlparse(artifact['url']).path).name
        candidates = [path for root in cache_roots if root.exists() for path in root.rglob(filename) if path.is_file()]
        downloaded = False
        if not candidates and a.download_missing:
            destination = ROOT / '.local/dependency-archives' / identity / filename
            destination.parent.mkdir(parents=True, exist_ok=True)
            partial = destination.with_suffix(destination.suffix + '.partial')
            subprocess.run(['curl', '--fail', '--location', '--silent', '--show-error', '--retry', '2', '--output', str(partial), artifact['url']], check=True)
            partial.replace(destination)
            candidates = [destination]
            downloaded = True
        archive = candidates[0] if candidates else None
        actual = digest(archive) if archive else None
        row['artifacts'].append({'target': artifact['target'], 'url': artifact['url'],
                                 'expected_sha256': artifact['sha256'], 'actual_sha256': actual,
                                 'archive_path': str(archive.relative_to(ROOT)) if archive and archive.is_relative_to(ROOT) else str(archive) if archive else None,
                                 'size_bytes': archive.stat().st_size if archive else None,
                                 'downloaded_by_verifier': downloaded, 'sha256_matches': actual == artifact['sha256']})
    out['packages'].append(row)
out['passed'] = all(x['revision_matches'] and x['version_matches_manifest'] and not x['checkout_tracked_changes'] and all(y['sha256_matches'] for y in x['artifacts']) for x in out['packages'])
a.output.write_text(json.dumps(out, indent=2) + '\n')
print(json.dumps({'passed': out['passed'], 'packages': len(out['packages']), 'output': str(a.output)}))
sys.exit(0 if out['passed'] else 1)
