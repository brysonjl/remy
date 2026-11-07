#!/usr/bin/env python3
"""Scan local reachable Git blobs and publishable working files without printing content."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import sys


PATTERNS = {
    "private_key": re.compile(rb"-----BEGIN (?:RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----"),
    "provider_token": re.compile(rb"(?<![A-Za-z0-9])sk-(?:proj-|ant-)?[A-Za-z0-9_-]{24,}"),
    "github_token": re.compile(rb"(?<![A-Za-z0-9])(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})"),
    "aws_access_key": re.compile(rb"(?<![A-Z0-9])(?:AKIA|ASIA)[A-Z0-9]{16}(?![A-Z0-9])"),
    "literal_bearer": re.compile(rb"Bearer[ \t]+[A-Za-z0-9_-]{24,}(?:\.[A-Za-z0-9_-]+)*"),
}
MACHINE_PATH = re.compile(rb"/(?:Users|home)/[^/\s\"'<>]+/")
PRIVATE_SUFFIXES = {".p12", ".pfx", ".p8", ".pem", ".key", ".cer", ".mobileprovision", ".provisionprofile"}


def private_name(name):
    path = Path(name)
    return (path.suffix.lower() in PRIVATE_SUFFIXES or path.name == "Signing.local.xcconfig"
            or path.name == ".env" or (path.name.startswith(".env.") and path.name != ".env.example"))


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args], stderr=subprocess.PIPE)


def scan(root, max_bytes=5_000_000):
    root = Path(git(root, "rev-parse", "--show-toplevel").decode().strip())
    findings, machine_paths, review, examined = [], [], [], 0

    def inspect(data, scope, identity):
        nonlocal examined
        if b"\0" in data:
            review.append({"scope": scope, "file": identity, "reason": "binary_content"})
            return
        examined += 1
        for category, pattern in PATTERNS.items():
            if pattern.search(data):
                findings.append({"scope": scope, "file": identity, "category": category})
        if MACHINE_PATH.search(data):
            machine_paths.append({"scope": scope, "file": identity})

    commits = git(root, "rev-list", "--all").decode().splitlines()
    # rev-list --objects gives one representative path per blob. Inspect trees
    # too: identical harmless contents may have lived at a sensitive filename.
    historical_private_paths = set()
    for commit in commits:
        for entry in git(root, "ls-tree", "-rz", "--full-tree", commit).split(b"\0"):
            if not entry:
                continue
            metadata, name = entry.split(b"\t", 1)
            path_name = name.decode("utf-8", "replace")
            if private_name(path_name):
                historical_private_paths.add((path_name, metadata.split()[2].decode()))
    for name, oid in sorted(historical_private_paths):
        findings.append({"scope": "history", "file": {"path": name, "blob": oid},
                         "category": "sensitive_filename"})

    objects = git(root, "rev-list", "--objects", "--all").decode("utf-8", "replace").splitlines()
    history_blobs = 0
    # Read objects one at a time rather than materializing historical content in output.
    with subprocess.Popen(["git", "-C", str(root), "cat-file", "--batch"],
                          stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE) as proc:
        for entry in objects:
            oid, _, name = entry.partition(" ")
            proc.stdin.write((oid + "\n").encode())
            proc.stdin.flush()
            header = proc.stdout.readline().split()
            if len(header) != 3:
                raise RuntimeError("Cannot read a reachable Git object")
            kind, size = header[1], int(header[2])
            # Drain oversized/non-blob objects without retaining or printing them.
            if kind != b"blob" or size > max_bytes:
                remaining = size
                while remaining:
                    chunk = proc.stdout.read(min(remaining, 65536))
                    if not chunk:
                        raise RuntimeError("Truncated Git object")
                    remaining -= len(chunk)
                data = None
            else:
                data = proc.stdout.read(size)
                if len(data) != size:
                    raise RuntimeError("Truncated Git blob")
            if proc.stdout.read(1) != b"\n":
                raise RuntimeError("Invalid Git object framing")
            if kind != b"blob":
                continue
            history_blobs += 1
            identity = {"path": name, "blob": oid}
            if private_name(name) and (name, oid) not in historical_private_paths:
                findings.append({"scope": "history", "file": identity, "category": "sensitive_filename"})
            if data is None:
                review.append({"scope": "history", "file": identity, "reason": "oversized_blob"})
            else:
                inspect(data, "history", identity)
        proc.stdin.close()
        if proc.wait() != 0:
            raise RuntimeError("Git object scan failed")

    names = git(root, "ls-files", "--cached", "--others", "--exclude-standard", "-z").split(b"\0")
    working_files = 0
    for encoded in sorted(set(names)):
        if not encoded:
            continue
        name = encoded.decode("utf-8", "replace")
        path = root / name
        identity = {"path": name}
        if private_name(name):
            findings.append({"scope": "working_tree", "file": identity, "category": "sensitive_filename"})
        if path.is_symlink() or any(parent.is_symlink() for parent in path.parents if parent != root and root in parent.parents):
            review.append({"scope": "working_tree", "file": identity, "reason": "symlink_not_followed"})
            continue
        if not path.exists():
            continue  # A staged deletion still remains in the historical scan.
        if not path.is_file():
            review.append({"scope": "working_tree", "file": identity, "reason": "non_regular_file"})
            continue
        working_files += 1
        if path.stat().st_size > max_bytes:
            review.append({"scope": "working_tree", "file": identity, "reason": "oversized_file"})
            continue
        inspect(path.read_bytes(), "working_tree", identity)

    return {
        "scope": "all locally reachable refs plus tracked and nonignored untracked working files",
        "commits": len(commits),
        "history_blobs": history_blobs,
        "working_files": working_files,
        "text_inputs_scanned": examined,
        "findings": findings,
        "machine_path_review": machine_paths,
        "manual_review": review,
        "limits": ["Pattern matches are not comprehensive secret detection.",
                   "Binary/media content, metadata and oversized content require manual review.",
                   "No remote refs fetched, unreachable objects inspected, or runtime traffic observed.",
                   "Author metadata and content/privacy/licensing review remain owner responsibilities."],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args()
    try:
        report = scan(args.repo)
    except (OSError, subprocess.SubprocessError, RuntimeError, ValueError):
        print("Publication scan failed; no contents or command errors printed.", file=sys.stderr)
        return 2
    print(json.dumps(report, indent=2))
    return 1 if report["findings"] else 0


if __name__ == "__main__":
    sys.exit(main())
