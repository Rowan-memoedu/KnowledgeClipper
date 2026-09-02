from __future__ import annotations

import argparse
import hashlib
import json
import shutil
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[1]
SOURCE = PROJECT / "tests/QualityV3.Foundation"
SOURCE_FILES = [
    "contracts/canonical-build-manifest.v1.schema.json",
    "contracts/private-runner-plan.v1.schema.json",
    "contracts/side-effect-audit.v1.schema.json",
    "contracts/ui-action-evidence.v1.schema.json",
    "private_runner.py",
    "quality_v3_gate.py",
]


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def main() -> int:
    parser = argparse.ArgumentParser(description="Create one private immutable Quality v3 runner snapshot")
    parser.add_argument("--snapshot-root", type=Path, required=True)
    parser.add_argument("--snapshot-id", required=True)
    args = parser.parse_args()
    root = args.snapshot_root.resolve()
    allowed = (PROJECT / "canary/quality-v3-runner-snapshots").resolve()
    if not root.is_relative_to(allowed) or root.exists():
        raise RuntimeError("snapshot must be a new append-only directory under canary/quality-v3-runner-snapshots")
    root.mkdir(parents=True)
    records = []
    for relative in SOURCE_FILES:
        source = SOURCE / relative
        target = root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
        records.append({"path": relative, "bytes": target.stat().st_size, "sha256": sha256(target)})
    records.sort(key=lambda item: item["path"])
    rows = [f"{item['path']}\t{item['bytes']}\t{item['sha256'].lower()}" for item in records]
    tree = hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper()
    manifest = {
        "schemaVersion": "quality-v3-private-runner-snapshot.v1",
        "snapshotId": args.snapshot_id,
        "immutable": True,
        "files": records,
        "treeAlgorithm": "sha256 utf8 ordinal(path TAB bytes TAB lowercase-sha256) joined with LF, no trailing LF",
        "treeSha256": tree,
    }
    manifest_path = root / "snapshot-manifest.json"
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": "CREATED-PRIVATE-RUNNER-SNAPSHOT", "root": root.relative_to(PROJECT).as_posix(), "manifestSha256": sha256(manifest_path), "treeSha256": tree}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
