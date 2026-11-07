#!/usr/bin/env python3
"""Verify the retained license inventory without network or user-data access."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native-artifacts", action="store_true",
                        help="also verify resolved dependency-input and arm64 slice hashes")
    args = parser.parse_args()
    inventory = json.loads((ROOT / "docs/third-party/inventory.json").read_text())
    pins = json.loads((ROOT / "app/Dictate Anywhere.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved").read_text())["pins"]
    require(inventory["package_pins"] == pins, "Package pins changed; repeat the notice review.")
    paths = set()
    for item in inventory["notice_files"]:
        path = ROOT / item["path"]
        require(path.is_file(), f"Missing retained file: {item['path']}")
        require(item["path"] not in paths, f"Duplicate inventory path: {item['path']}")
        require(digest(path) == item["sha256"], f"Retained file changed: {item['path']}")
        require(item.get("source") and item.get("revision"), f"Missing provenance: {item['path']}")
        paths.add(item["path"])
    for font in inventory["font_assets"]:
        require(digest(ROOT / font["path"]) == font["sha256"], f"Font changed: {font['component']}")
        require(font["notice_path"] in paths, f"Font notice not inventoried: {font['component']}")
        adjacent = ROOT / "app/Dictate Anywhere/Fonts" / (font["component"] + "-OFL.txt")
        require(adjacent.read_bytes() == (ROOT / font["notice_path"]).read_bytes(),
                f"Adjacent font notice differs: {font['component']}")
    import tomllib
    lock = tomllib.loads((ROOT / "docs/third-party/NemoTextProcessing/source-review/Cargo.lock").read_text())
    locked = {(p["name"], p["version"]): p["checksum"] for p in lock["package"] if p.get("source", "").startswith("registry+")}
    crates = json.loads((ROOT / "docs/third-party/NemoTextProcessing/crates.json").read_text())
    require(len(crates) == len(locked), "Crate inventory does not cover the release lockfile.")
    for crate in crates:
        require(locked.get((crate["name"], crate["version"])) == crate["crate_sha256"],
                f"Crate metadata differs from lockfile: {crate['name']}")
        require(crate["retained_notice_paths"] and all(p in paths for p in crate["retained_notice_paths"]),
                f"Crate notice missing: {crate['name']}")
    reviews = json.loads((ROOT / "docs/third-party/models/reviewed-options.json").read_text())
    for model in reviews["repositories"]:
        for item in model["files"]:
            require(item["retained_path"] in paths, f"Model review not inventoried: {model['repository']}")
            require(digest(ROOT / item["retained_path"]) == item["sha256"],
                    f"Model review changed: {model['repository']}")
    if args.native_artifacts:
        for artifact in inventory["native_artifacts"]:
            path = ROOT / artifact["path"]
            require(path.is_file(), f"Resolve dependency first: {artifact['component']}")
            require(digest(path) == artifact["sha256"], f"Native input changed: {artifact['component']}")
            with tempfile.TemporaryDirectory(prefix="remy-notice-slice-") as temporary:
                thin = Path(temporary) / "arm64"
                subprocess.run(["xcrun", "lipo", str(path), "-thin", "arm64", "-output", str(thin)], check=True, capture_output=True)
                require(digest(thin) == artifact["arm64_sha256"], f"Native arm64 slice changed: {artifact['component']}")
    print(f"Verified {len(paths)} retained files, {len(crates)} locked crate notice sets, "
          f"{len(inventory['font_assets'])} fonts and {len(reviews['repositories'])} model-card reviews.")
    if args.native_artifacts:
        print(f"Verified {len(inventory['native_artifacts'])} resolved native inputs and their arm64 slices.")
    print("This check verifies recorded inputs; final binary/resource scope and model gates remain in inventory.json.")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, KeyError, subprocess.CalledProcessError) as error:
        raise SystemExit(f"Notice verification failed: {error}")
