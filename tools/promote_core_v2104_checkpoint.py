from __future__ import annotations

import hashlib
import json
from copy import deepcopy
from datetime import datetime, timezone
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[1]
SOURCE_DIR = PROJECT / "contracts/core/v2.10/checkpoints/v2.10.3-20260902T010816Z"
SOURCE = SOURCE_DIR / "checkpoint.json"
SOURCE_SHA256 = "3D943E1BCCC1570816EA2753E37277D1A3FC68B799D2F8DC7E64F90E485EFCDC"
TARGET_DIR = PROJECT / "contracts/core/v2.10/checkpoints/v2.10.4-20260902T011656Z"
INVALIDATION = SOURCE_DIR / "authority-assembly-invalidation-receipt.json"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def write_once(path: Path, value: object) -> None:
    if path.exists():
        raise RuntimeError(f"append-only output exists: {path}")
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main() -> int:
    if sha256(SOURCE) != SOURCE_SHA256:
        raise RuntimeError("Core v2.10.3 checkpoint drifted")
    if TARGET_DIR.exists():
        raise RuntimeError(f"append-only target exists: {TARGET_DIR}")
    now = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    invalidation = {
        "schemaVersion": "knowledge-clipper-core-checkpoint-assembly-invalidation.v1",
        "createdAt": now,
        "status": "invalidated-for-authority-assembly-only",
        "checkpoint": {"path": relative(SOURCE), "sha256": SOURCE_SHA256},
        "runtimeVerificationStillValid": True,
        "authorityAssemblyReady": False,
        "reason": "Frozen V3 base assembler requires direct checkpoint fileBindings for the declared request and receipt contract schemas; v2.10.3 included them only through schema closure.",
        "failure": "Checkpoint-bound request contract schema was not found: knowledge-clipper-module-action.v6",
        "authorityTargetsCreated": 0,
        "candidateTargetsCreated": 0,
        "supersededBy": "contracts/core/v2.10/checkpoints/v2.10.4-20260902T011656Z/checkpoint.json",
    }
    write_once(INVALIDATION, invalidation)

    source = json.loads(SOURCE.read_text(encoding="utf-8"))
    checkpoint = deepcopy(source)
    checkpoint["checkpointVersion"] = "2.10.4"
    checkpoint["createdAt"] = now
    checkpoint["status"] = "passed-runtime-drift-regression-and-v3-assembly-binding"
    checkpoint["scope"] = "Core v2.10.3 runtime/source identity repair plus direct V3 request/receipt schema authority bindings"
    checkpoint["extendsCheckpoint"] = relative(SOURCE)
    checkpoint["verification"]["v2103AssemblyInvalidation"] = {
        "status": "superseded-before-target-creation",
        "receipt": relative(INVALIDATION),
        "receiptSha256": sha256(INVALIDATION),
    }
    checkpoint["releaseDecision"]["coreRuntimeInputReady"] = True
    checkpoint["releaseDecision"]["authorityV1240Required"] = True
    additions = [
        PROJECT / "contracts/core/v2.10/module-control/schemas/module-action-request.v6.schema.json",
        PROJECT / "contracts/core/v2.10/module-control/schemas/module-action-receipt.v6.schema.json",
        INVALIDATION,
    ]
    known = {item["path"] for item in checkpoint["fileBindings"]}
    for path in additions:
        rel = relative(path)
        if rel not in known:
            checkpoint["fileBindings"].append({"path": rel, "sha256": sha256(path).lower()})
            known.add(rel)

    TARGET_DIR.mkdir(parents=True)
    target = TARGET_DIR / "checkpoint.json"
    write_once(target, checkpoint)
    print(json.dumps({
        "status": "passed",
        "checkpoint": {"path": relative(target), "bytes": target.stat().st_size, "sha256": sha256(target)},
        "invalidation": {"path": relative(INVALIDATION), "bytes": INVALIDATION.stat().st_size, "sha256": sha256(INVALIDATION)},
        "fileBindings": len(checkpoint["fileBindings"]),
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
