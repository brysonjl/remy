#!/usr/bin/env python3
"""Clone committed local source without shared objects/caches; optionally validate it."""
import argparse
import datetime
import json
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def git(source, *arguments):
    return subprocess.check_output(['git', '-C', str(source), *arguments], text=True).strip()


def prepare(source, revision='HEAD', destination=None):
    source = Path(source).resolve()
    commit = git(source, 'rev-parse', '--verify', '--end-of-options', f'{revision}^{{commit}}')
    if destination is None:
        parent = source / '.local/clean-clones'
        parent.mkdir(parents=True, exist_ok=True)
        destination = Path(tempfile.mkdtemp(prefix='remy-', dir=parent))
    else:
        destination = Path(destination).resolve()
        if destination.exists():
            raise ValueError('Destination must not already exist; existing files are never replaced.')
        destination.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(['git', 'clone', '--quiet', '--no-local', '--no-hardlinks', '--no-checkout',
                    '--', str(source), str(destination)], check=True)
    subprocess.run(['git', '-C', str(destination), 'checkout', '--quiet', '--detach', commit], check=True)
    if git(destination, 'status', '--porcelain'):
        raise ValueError('Fresh clone unexpectedly has tracked/untracked changes.')
    if (destination / '.git/objects/info/alternates').exists():
        raise ValueError('Fresh clone must not share source object storage.')
    metadata = dict(schema_version=1, source_commit=commit,
                    source_worktree_dirty=bool(git(source, 'status', '--porcelain')),
                    created_at=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                    scope='Committed source snapshot; no ignored build/model/signing files copied. HOME and OS caches remain shared.',
                    full_validation='not_run')
    return destination, metadata


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=ROOT)
    parser.add_argument('--revision', default='HEAD')
    parser.add_argument('--destination', type=Path)
    parser.add_argument('--run', action='store_true', help='Run serial full foundation validation in the new clone (uses Xcode)')
    arguments = parser.parse_args()
    destination, metadata = prepare(arguments.source, arguments.revision, arguments.destination)
    output = destination / '.local'
    output.mkdir()
    report = output / 'fresh-clone.json'
    report.write_text(json.dumps(metadata, indent=2) + '\n')
    print(f'Prepared committed source {metadata["source_commit"]} at {destination}', flush=True)
    if metadata['source_worktree_dirty']:
        print('Source has uncommitted changes; those changes are excluded from this clone.', flush=True)
    if not arguments.run:
        print(f'Full validation has not run. Review {report}; use --run with a new destination to validate.', flush=True)
        return 0
    log = output / 'fresh-clone-validation.log'
    with log.open('w') as stream:
        completed = subprocess.run(['bash', 'validation/foundation-checks/run.sh'], cwd=destination,
                                   stdout=stream, stderr=subprocess.STDOUT)
    metadata['full_validation'] = 'passed' if completed.returncode == 0 else 'failed'
    metadata['exit_code'] = completed.returncode
    metadata['lockfile_unchanged'] = not bool(git(destination, 'diff', 'HEAD', '--',
        'app/Dictate Anywhere.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved'))
    metadata['worktree_clean_after_validation'] = not bool(git(destination, 'status', '--porcelain'))
    if not metadata['lockfile_unchanged'] or not metadata['worktree_clean_after_validation']:
        metadata['full_validation'] = 'failed'
    report.write_text(json.dumps(metadata, indent=2) + '\n')
    print(f'Full validation {metadata["full_validation"]}; local log: {log}', flush=True)
    return 0 if metadata['full_validation'] == 'passed' else 1


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        sys.exit(f'Fresh-clone verification stopped: {error}')
