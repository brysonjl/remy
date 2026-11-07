#!/usr/bin/env python3
"""Explicit pinned A00 download or offline verification; never activates models."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import tempfile
import urllib.request

MANIFEST = Path(__file__).with_name('manifest.json')


def checked_path(value):
    path = Path(value).expanduser().absolute()
    if '..' in path.parts or any(p.is_symlink() for p in (path, *path.parents)):
        raise ValueError('Destination and ancestors must not contain symlinks or ..')
    return path


def verify(root, manifest):
    expected = {f['path'] for f in manifest['files']}
    actual = set()
    for path in root.rglob('*'):
        if path.is_symlink():
            raise ValueError(f'Symlink rejected: {path}')
        if path.is_file():
            actual.add(path.relative_to(root).as_posix())
    if actual != expected:
        raise ValueError(f'File set mismatch: missing={expected-actual}, extra={actual-expected}')
    for entry in manifest['files']:
        path = root / entry['path']
        digest = hashlib.sha256()
        with path.open('rb') as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b''):
                digest.update(chunk)
        if path.stat().st_size != entry['size'] or digest.hexdigest() != entry['sha256']:
            raise ValueError(f'Integrity mismatch: {entry["path"]}')
    print(f'Verified {len(expected)} files / {manifest["total_bytes"]} bytes at {root}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['download', 'verify'])
    parser.add_argument('directory', help='Explicit new destination for download, existing bundle for verify')
    args = parser.parse_args()
    manifest = json.loads(MANIFEST.read_text())
    root = checked_path(args.directory)
    if args.action == 'verify':
        verify(root, manifest)
        return
    if root.exists():
        raise ValueError('Download refuses an existing destination; verify or choose a new staging path')
    # Parent must already exist: avoids implicitly creating application/cache locations.
    if not root.parent.is_dir():
        raise ValueError('Create the intended parent directory explicitly first')
    stage = Path(tempfile.mkdtemp(prefix='.parakeet-a00-', dir=root.parent))
    try:
        for entry in manifest['files']:
            relative = Path(entry['path'])
            if relative.is_absolute() or '..' in relative.parts:
                raise ValueError('Unsafe manifest path')
            url = entry['url']
            prefix = f'https://huggingface.co/{manifest["repo"]}/resolve/{manifest["revision"]}/'
            if not url.startswith(prefix):
                raise ValueError('Manifest URL is not pinned to the selected revision')
            target = stage / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            print(f'Downloading {relative} ({entry["size"]} bytes)', flush=True)
            # Hugging Face redirects large immutable artifacts to its CDN. No auth is read/sent.
            with urllib.request.urlopen(url, timeout=120) as response, target.open('xb') as output:
                remaining = entry['size']
                while chunk := response.read(min(1024 * 1024, remaining + 1)):
                    remaining -= len(chunk)
                    if remaining < 0:
                        raise ValueError(f'Oversized artifact: {relative}')
                    output.write(chunk)
        verify(stage, manifest)
        checked_path(root)
        if root.exists():
            raise ValueError('Destination appeared during download; refusing replacement')
        # macOS renamex_np with RENAME_EXCL avoids replacing even an empty concurrent directory.
        import ctypes
        library = ctypes.CDLL(None, use_errno=True)
        rename = library.renamex_np
        rename.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_uint]
        if rename(os.fsencode(stage), os.fsencode(root), 4) != 0:
            raise OSError(ctypes.get_errno(), 'Exclusive activation of staging directory failed')
        print(f'Created verified baseline bundle at {root}; app activation was not changed')
    finally:
        if stage.exists():
            shutil.rmtree(stage)


if __name__ == '__main__':
    main()
