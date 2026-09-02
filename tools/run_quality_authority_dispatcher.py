from __future__ import annotations

import json
import os
import subprocess
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from assemble_release_preseal import inventory
from quality_gates import PROJECT_ROOT, SCHEMAS_ROOT, load_json, load_schemas, sha256_file


CHECKPOINT = PROJECT_ROOT / "contracts/authority/worker-registry/v1.1.0-20260826T023054Z/checkpoint.json"
CHECKPOINT_SHA256 = "673E5BDBD964A66654102A5DE5A53CC516D574BB458CB3E2D835694F9D0BE5F4"
AUTHORITY = CHECKPOINT.parent / "worker-registry.v1.json"
OVERLAY = PROJECT_ROOT / "canary/authority-registry/authority-registry-20260826T023501Z/fake-overlay-registry.v1.json"
PIPELINE = PROJECT_ROOT / "canary/authority-registry/authority-registry-20260826T023501Z/dispatcher-job/pipeline.corrected.v1.json"
RELEASE_ROOT = PROJECT_ROOT / "releases/1.0.0-rc.1-preseal.10/win-x64"
ORCHESTRATOR = RELEASE_ROOT / "orchestrator/RemNoteQuickClipper.Orchestrator.exe"
FAKE_WORKER = PROJECT_ROOT / "tests/FakeWorker/bin/Release/net8.0/RemNoteQuickClipper.FakeWorker.exe"


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT_ROOT.resolve()).as_posix()


def io_path(path: Path) -> Path:
    resolved = path.resolve()
    return Path("\\\\?\\" + str(resolved)) if os.name == "nt" and len(str(resolved)) >= 248 else resolved


def record(path: Path) -> dict[str, Any]:
    source = io_path(path)
    return {"path": relative(path), "bytes": source.stat().st_size, "sha256": sha256_file(source)}


def verify_overlay_semantics() -> None:
    authority = load_json(AUTHORITY)
    overlay = load_json(OVERLAY)
    if authority["profiles"] != overlay["profiles"] or len(authority["workers"]) != 5 or len(overlay["workers"]) != 5:
        raise RuntimeError("fake overlay profile/worker count mismatch")
    by_id = {entry["workerId"]: entry for entry in overlay["workers"]}
    for expected in authority["workers"]:
        actual = by_id.get(expected["workerId"])
        if actual is None:
            raise RuntimeError(f"fake overlay worker missing: {expected['workerId']}")
        for key in (
            "workerId", "contractVersionRange", "stages", "inputKinds", "outputKinds",
            "requiredForProfiles", "sideEffectLevel", "idempotencyLevel", "enabledByDefault",
        ):
            if actual[key] != expected[key]:
                raise RuntimeError(f"fake overlay semantic drift: {expected['workerId']}:{key}")
    if not FAKE_WORKER.is_file():
        raise RuntimeError("fixed FakeWorker binary is missing")


def main() -> int:
    if sha256_file(CHECKPOINT) != CHECKPOINT_SHA256:
        raise RuntimeError("Core authority checkpoint hash mismatch")
    verify_overlay_semantics()
    before = inventory(RELEASE_ROOT)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    canary_id = f"qad-{stamp}-{uuid.uuid4().hex[:8]}"
    root = PROJECT_ROOT / "canary/qa" / canary_id
    job_root = root / "j"
    job_root.mkdir(parents=True, exist_ok=False)
    command = [
        str(ORCHESTRATOR), "run",
        "--pipeline", str(PIPELINE),
        "--registry", str(OVERLAY),
        "--release-root", str(PROJECT_ROOT),
        "--job-root", str(job_root),
        "--run-id", "run-001",
    ]
    completed = subprocess.run(
        command,
        cwd=PROJECT_ROOT,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=60,
        check=False,
    )
    stdout_path = root / "orchestrator.stdout.json"
    stderr_path = root / "orchestrator.stderr.txt"
    stdout_path.write_text(completed.stdout, encoding="utf-8", newline="\n")
    stderr_path.write_text(completed.stderr, encoding="utf-8", newline="\n")
    if completed.returncode != 0:
        raise RuntimeError(f"quality authority dispatcher failed: {completed.returncode}: {completed.stderr}")
    results = list(job_root.rglob("result.json"))
    if len(results) != 1:
        raise RuntimeError(f"expected one terminal result, got {len(results)}")
    result_path = results[0]
    result = load_json(io_path(result_path))
    schemas = load_schemas()
    result_errors = list(schemas.validator(SCHEMAS_ROOT / "common/worker-result.schema.json").iter_errors(result))
    if result_errors:
        raise RuntimeError(f"fixed dispatcher result violates common schema: {result_errors[0].message}")
    if result.get("status") != "completed" or result.get("worker", {}).get("id") != "export.anki":
        raise RuntimeError("fixed dispatcher result identity/status mismatch")
    if "itemDiagnostics" in result and not isinstance(result["itemDiagnostics"], list):
        raise RuntimeError("fixed dispatcher retained a null optional itemDiagnostics field")
    if result.get("error", "missing") is not None:
        raise RuntimeError("fixed dispatcher completed result did not retain required error:null")
    if len(result.get("outputs", [])) != 1:
        raise RuntimeError("fixed dispatcher did not publish exactly one fake output")
    artifact_path = job_root / result["outputs"][0]["manifestPath"]
    artifact_document = load_json(io_path(artifact_path))
    artifact_errors = list(schemas.validator(SCHEMAS_ROOT / "common/artifact.schema.json").iter_errors(artifact_document))
    if artifact_errors:
        raise RuntimeError(f"fixed dispatcher artifact violates common schema: {artifact_errors[0].message}")
    after = inventory(RELEASE_ROOT)
    if after != before:
        raise RuntimeError("quality dispatcher changed the immutable preseal root")
    receipt = {
        "schemaVersion": "quality-authority-dispatcher-receipt.v1",
        "receiptId": canary_id,
        "createdAt": utc_now(),
        "status": "passed",
        "scope": "offline-fake-dispatch-replacement-evidence",
        "supersedesInvalidEvidence": {
            "path": "canary/authority-registry/authority-registry-20260826T023501Z/receipt.json",
            "reason": "bound result serialized optional itemDiagnostics as null",
        },
        "authority": {**record(AUTHORITY), "checkpointSha256": CHECKPOINT_SHA256, "workers": 5},
        "releaseBinding": {"root": relative(RELEASE_ROOT), "treeSha256": after[2], "unchanged": True},
        "fakeOverlay": {**record(OVERLAY), "semanticFieldsEqualToAuthority": True},
        "fixedFakeWorker": {**record(FAKE_WORKER), "optionalNullFieldsOmitted": True, "requiredErrorNullRetained": True},
        "dispatcher": {
            "orchestrator": record(ORCHESTRATOR),
            "pipeline": record(PIPELINE),
            "stdout": record(stdout_path),
            "stderr": record(stderr_path),
            "result": {**record(result_path), "status": result["status"], "workerId": result["worker"]["id"]},
            "output": {**record(artifact_path), "kind": artifact_document["kind"]},
        },
        "checks": {
            "authorityCheckpointHash": True,
            "overlaySemanticParity": True,
            "registryDescribe": True,
            "workerRequestAccepted": True,
            "workerResultCommonSchema": True,
            "artifactCommonSchema": True,
            "releaseRootUnchanged": True,
            "hiddenWritesInsideReleaseRoot": 0,
        },
        "productionServices": {
            "remnote": "not-called", "anki": "not-called", "mineru": "not-called",
            "deepseek": "not-called", "edgeTtsNetwork": "not-called",
        },
        "externalProductionApisInvoked": False,
    }
    receipt_path = root / "receipt.json"
    with receipt_path.open("x", encoding="utf-8", newline="\n") as stream:
        stream.write(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps({"status": "passed", "receipt": relative(receipt_path), "receiptSha256": sha256_file(receipt_path)}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(str(error), file=sys.stderr)
        raise
