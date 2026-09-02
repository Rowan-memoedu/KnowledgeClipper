from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import quality_v3_gate as gate


SNAPSHOT = Path(__file__).resolve().parent


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def verify_snapshot() -> dict[str, object]:
    manifest_path = SNAPSHOT / "snapshot-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    observed = []
    for item in manifest["files"]:
        path = SNAPSHOT / item["path"]
        gate.require(path.is_file(), "snapshot.file-missing", item["path"])
        actual = {"path": item["path"], "bytes": path.stat().st_size, "sha256": sha256(path)}
        gate.require(actual == item, "snapshot.file-drift", item["path"])
        observed.append(actual)
    present = sorted(path.relative_to(SNAPSHOT).as_posix() for path in SNAPSHOT.rglob("*") if path.is_file() and path.name != "snapshot-manifest.json" and "__pycache__" not in path.parts)
    gate.require(present == [item["path"] for item in manifest["files"]], "snapshot.file-set-drift")
    rows = [f"{item['path']}\t{item['bytes']}\t{item['sha256'].lower()}" for item in observed]
    tree = hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper()
    gate.require(tree == manifest["treeSha256"], "snapshot.tree-drift")
    return {
        "status": "PASS-PRIVATE-RUNNER-SNAPSHOT",
        "files": len(observed),
        "treeSha256": tree,
        "manifestSha256": sha256(manifest_path),
    }


def verify_plan_binding(plan: dict[str, object], snapshot: dict[str, object]) -> None:
    project = SNAPSHOT.parents[2]
    declared = plan.get("runnerSnapshot") or {}
    declared_root = (project / str(declared.get("root", ""))).resolve()
    gate.require(declared_root == SNAPSHOT.resolve(), "runner.snapshot-root-mismatch")
    gate.require(declared.get("treeSha256") == snapshot["treeSha256"], "runner.snapshot-tree-mismatch")
    gate.require(declared.get("manifestSha256") == snapshot["manifestSha256"], "runner.snapshot-manifest-mismatch")


def main() -> int:
    parser = argparse.ArgumentParser(description="Quality v3 private immutable runner snapshot")
    parser.add_argument("--self-check", action="store_true")
    parser.add_argument("--plan", type=Path)
    args = parser.parse_args()
    snapshot = verify_snapshot()
    result: dict[str, object] = {"snapshot": snapshot}
    if args.plan:
        plan = gate.load_json(args.plan.resolve())
        result["plan"] = gate.validate_runner_plan(plan)
        verify_plan_binding(plan, snapshot)
        result["binding"] = {"status": "PASS-PLAN-BOUND-TO-EXECUTING-SNAPSHOT"}
    gate.require(args.self_check or args.plan is not None, "runner.no-operation")
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
