#!/usr/bin/env python3
"""Export one committed source tree as a deterministic, reviewed-policy tar snapshot."""
import argparse
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path, PurePosixPath
import posixpath
import subprocess
import sys
import tarfile
import tempfile

_spec = importlib.util.spec_from_file_location('publication_patterns', Path(__file__).with_name('check.py'))
_patterns = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_patterns)
POLICY_PATH = 'validation/publication-hygiene/binary-allowlist.json'
MANIFEST_PATH = 'SOURCE_MANIFEST.json'
BINARY_SUFFIXES = {'.zip', '.dmg', '.pkg', '.pdf', '.jpg', '.jpeg', '.webp', '.gif', '.bin', '.gguf', '.dylib', '.so', '.a', '.o', '.exe'}
AUDIO_SUFFIXES = {'.wav', '.mp3', '.m4a', '.aac', '.aiff', '.aif', '.caf', '.ogg', '.flac', '.opus', '.pcm', '.mp4', '.mov'}
EXCLUDED_PATHS = {'HANDOFF.md'}


def git(root, *args):
    return subprocess.check_output(['git', '-C', str(root), *args], stderr=subprocess.PIPE)


def safe_name(name):
    path = PurePosixPath(name)
    return (bool(name) and not path.is_absolute() and '\\' not in name
            and all(part not in ('', '.', '..', '.git', '.local') for part in name.split('/'))
            and not any(ord(character) < 32 or ord(character) == 127 for character in name))


def collect(root, revision):
    # SHA resolution is explicit; no worktree files supply exported bytes or policy.
    if len(revision) not in (40, 64) or any(character not in '0123456789abcdef' for character in revision):
        raise ValueError('Supply a full lowercase commit object ID; symbolic revisions are not accepted.')
    commit = git(root, 'rev-parse', '--verify', '--end-of-options', f'{revision}^{{commit}}').decode().strip()
    entries = {}
    for raw in git(root, 'ls-tree', '-rz', '--full-tree', commit).split(b'\0'):
        if not raw:
            continue
        metadata, encoded = raw.split(b'\t', 1)
        name = encoded.decode('utf-8', 'strict')
        if not safe_name(name):
            raise ValueError('Unsafe source path; no archive written.')
        if name in EXCLUDED_PATHS:
            continue
        mode, kind, oid = metadata.decode('ascii').split()
        if mode not in ('100644', '100755', '120000') or kind != 'blob':
            raise ValueError(f'Unsupported object or submodule: {name}')
        if name == MANIFEST_PATH:
            raise ValueError('Source tree already reserves the generated manifest path.')
        private_components = {'.aws', '.ssh', '.gnupg', '.netrc', '.npmrc', 'id_rsa', 'id_ed25519', 'id_ecdsa'}
        if (_patterns.private_name(name) or private_components.intersection(PurePosixPath(name).parts)
                or PurePosixPath(name).suffix.lower() in AUDIO_SUFFIXES):
            raise ValueError(f'Private filename or audio/media excluded from publication: {name}')
        data = git(root, 'cat-file', 'blob', oid)
        if any(pattern.search(data) for pattern in _patterns.PATTERNS.values()):
            raise ValueError(f'Credential pattern in committed input: {name}')
        entries[name] = dict(mode=mode, data=data)
    policy = json.loads(entries[POLICY_PATH]['data']) if POLICY_PATH in entries else {}
    if not isinstance(policy, dict) or any(not isinstance(name, str) or not isinstance(value, str)
                                           or len(value) != 64 or any(c not in '0123456789abcdef' for c in value)
                                           for name, value in policy.items()):
        raise ValueError('Invalid committed binary allowlist.')
    for name, entry in entries.items():
        data = entry['data']
        binary = (b'\0' in data or PurePosixPath(name).suffix.lower() in BINARY_SUFFIXES
                  or any(byte < 32 and byte not in (9, 10, 13) for byte in data))
        try:
            data.decode('utf-8', 'strict')
        except UnicodeDecodeError:
            binary = True
        extension = PurePosixPath(name).suffix.lower()
        if binary or extension in ('.png', '.ttf'):
            if extension not in ('.png', '.ttf') or policy.get(name) != hashlib.sha256(data).hexdigest():
                raise ValueError(f'Binary requires exact reviewed PNG/font allowlist entry: {name}')
            if extension == '.png' and not data.startswith(b'\x89PNG\r\n\x1a\n'):
                raise ValueError(f'Invalid PNG signature: {name}')
            if extension == '.ttf' and not data.startswith((b'\x00\x01\x00\x00', b'OTTO', b'ttcf')):
                raise ValueError(f'Invalid font signature: {name}')
        elif _patterns.MACHINE_PATH.search(data):
            raise ValueError(f'Machine-specific path in committed text: {name}')
        if entry['mode'] == '120000':
            target = data.decode('utf-8', 'strict')
            if not name.startswith('validation/') or not target or target.startswith('/') or '\\' in target:
                raise ValueError(f'Only confined relative validation links are supported: {name}')
            current, seen = name, set()
            while entries.get(current, {}).get('mode') == '120000':
                if current in seen:
                    raise ValueError(f'Symlink cycle: {name}')
                seen.add(current)
                link = entries[current]['data'].decode('utf-8', 'strict')
                if link.startswith('/') or '\\' in link:
                    raise ValueError(f'Unsafe symlink target: {name}')
                current = posixpath.normpath(posixpath.join(posixpath.dirname(current), link))
                if not safe_name(current):
                    raise ValueError(f'Escaping symlink: {name}')
            if current not in entries or entries[current]['mode'] not in ('100644', '100755'):
                raise ValueError(f'Broken or directory symlink: {name}')
            entry['target'] = target
    return commit, entries


def export(root, revision, output):
    output = Path(output)
    if output.exists() or output.is_symlink():
        raise ValueError('Output already exists; refusing to replace it.')
    commit, entries = collect(Path(root), revision)
    manifest = dict(schema_version=1, source_commit=commit,
                    excluded_paths=sorted(EXCLUDED_PATHS),
                    scope='Committed source only; no Git history or author metadata. File hashes cover payload bytes; symlink hashes cover target text. This generated manifest does not hash itself.',
                    files=[dict(path=name, mode=entry['mode'], size=len(entry['data']),
                                sha256=hashlib.sha256(entry['data']).hexdigest(),
                                **({'symlink_target': entry['target']} if entry['mode'] == '120000' else {}))
                           for name, entry in sorted(entries.items())])
    payload = json.dumps(manifest, indent=2, sort_keys=True).encode() + b'\n'
    output.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix='.source-export-', dir=output.parent)
    try:
        with os.fdopen(descriptor, 'wb') as stream, tarfile.open(fileobj=stream, mode='w', format=tarfile.PAX_FORMAT) as archive:
            for name, entry in sorted({**entries, MANIFEST_PATH: dict(mode='100644', data=payload)}.items()):
                info = tarfile.TarInfo(name)
                info.uid = info.gid = info.mtime = 0
                info.uname = info.gname = ''
                info.mode = 0o755 if entry['mode'] == '100755' else 0o644
                if entry['mode'] == '120000':
                    info.type = tarfile.SYMTYPE
                    info.linkname = entry['target']
                    info.mode = 0o777
                    archive.addfile(info)
                else:
                    info.size = len(entry['data'])
                    archive.addfile(info, io.BytesIO(entry['data']))
        # An exclusive hard-link promotion is atomic and cannot overwrite a racing output.
        os.link(temporary, output)
    finally:
        os.unlink(temporary)
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument('--revision', required=True, help='Exact committed revision; resolved SHA is recorded in the manifest')
    parser.add_argument('--output', type=Path, required=True, help='New .tar path; never overwritten')
    args = parser.parse_args()
    try:
        manifest = export(args.repo, args.revision, args.output)
    except (OSError, ValueError, KeyError, UnicodeError, subprocess.SubprocessError):
        print('Source export rejected; no contents or underlying command errors printed. Review committed paths, policy, and publication checks privately.', file=sys.stderr)
        return 1
    print(json.dumps(dict(source_commit=manifest['source_commit'], files=len(manifest['files']),
                          archive_sha256=hashlib.sha256(args.output.read_bytes()).hexdigest(),
                          output=str(args.output))))
    return 0


if __name__ == '__main__':
    sys.exit(main())
