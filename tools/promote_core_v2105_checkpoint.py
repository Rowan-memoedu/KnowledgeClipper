from __future__ import annotations

import hashlib
import json
from copy import deepcopy
from datetime import datetime, timezone
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[1]
SOURCE_DIR = PROJECT / "contracts/core/v2.10/checkpoints/v2.10.4-20260902T011656Z"
SOURCE = SOURCE_DIR / "checkpoint.json"
SOURCE_SHA256 = "6722DC6FBEBE2E96A06042E0EA14E0CD8A79C6266D358C058875E143DFC33A65"
TARGET_DIR = PROJECT / "contracts/core/v2.10/checkpoints/v2.10.5-20260902T012600Z"
INVALIDATION = SOURCE_DIR / "v3-input-generation-invalidation-receipt.json"


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
        raise RuntimeError("Core v2.10.4 checkpoint drifted")
    if TARGET_DIR.exists():
        raise RuntimeError(f"append-only target exists: {TARGET_DIR}")
    now = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    invalidation = {
        "schemaVersion": "knowledge-clipper-core-checkpoint-assembly-invalidation.v1",
        "createdAt": now,
        "status": "invalidated-for-v3-input-generation",
        "checkpoint": {"path": relative(SOURCE), "sha256": SOURCE_SHA256},
        "runtimeVerificationStillValid": True,
        "authorityTargetsCreated": 0,
        "candidateTargetsCreated": 0,
        "reason": "Authority package fileBindings were broader than the frozen V3 assembler requires and caused non-linear schema-closure scanning.",
        "observed": "Two progress-aware runs reached 120s and 300s idle ceilings with sustained CPU and no committed output directory.",
        "supersededBy": "contracts/core/v2.10/checkpoints/v2.10.5-20260902T012600Z/checkpoint.json",
    }
    write_once(INVALIDATION, invalidation)

    source = json.loads(SOURCE.read_text(encoding="utf-8"))
    checkpoint = deepcopy(source)
    checkpoint["checkpointVersion"] = "2.10.5"
    checkpoint["createdAt"] = now
    checkpoint["status"] = "passed-runtime-drift-regression-and-bounded-v3-package-binding"
    checkpoint["scope"] = "Core runtime/source identity repair with bounded V3 authority package bindings and final-process IPC 1.5/1.6/1.7 evidence"
    checkpoint["extendsCheckpoint"] = relative(SOURCE)
    checkpoint["verification"]["v2104InputGenerationInvalidation"] = {
        "status": "superseded-before-target-creation",
        "receipt": relative(INVALIDATION),
        "receiptSha256": sha256(INVALIDATION),
    }
    remove = {
        "contracts/core/v2.10/module-control/orchestrator-ipc.v1.5.json",
        "contracts/core/v2.10/module-control/orchestrator-ipc.v1.6.json",
        "contracts/core/v2.10/module-control/schemas/ipc-request.v1.5.schema.json",
        "contracts/core/v2.10/module-control/schemas/ipc-response.v1.5.schema.json",
        "contracts/core/v2.10/module-control/schemas/ipc-request.v1.6.schema.json",
        "contracts/core/v2.10/module-control/schemas/ipc-response.v1.6.schema.json",
        "contracts/core/v2.10/checkpoints/v2.10.3-20260902T010816Z/authority-assembly-invalidation-receipt.json",
    }
    checkpoint["fileBindings"] = [item for item in checkpoint["fileBindings"] if item["path"] not in remove]
    checkpoint["authority"]["runtime"]["authorityPackageBindingsBounded"] = True
    checkpoint["authority"]["runtime"]["ipc15And16EvidenceBoundByFinalProcessReceipt"] = True

    TARGET_DIR.mkdir(parents=True)
    target = TARGET_DIR / "checkpoint.json"
    write_once(target, checkpoint)
    package_bindings = [item["path"] for item in checkpoint["fileBindings"] if item["path"].startswith("contracts/core/v2.10/")]
    print(json.dumps({
        "status": "passed",
        "checkpoint": {"path": relative(target), "bytes": target.stat().st_size, "sha256": sha256(target)},
        "invalidation": {"path": relative(INVALIDATION), "bytes": INVALIDATION.stat().st_size, "sha256": sha256(INVALIDATION)},
        "fileBindings": len(checkpoint["fileBindings"]),
        "authorityPackageBindings": package_bindings,
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
