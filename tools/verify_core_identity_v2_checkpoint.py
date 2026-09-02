from __future__ import annotations

import hashlib
import json
import subprocess
import sys
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[1]
CHECKPOINT = PROJECT_ROOT / "contracts" / "authority" / "core-identity" / "v2.0.0-20260826T031615Z" / "checkpoint.json"
FROZEN_RECEIPT = PROJECT_ROOT / "receipts" / "contracts-v1-stable-checkpoint-20260825T115938Z.json"
FROZEN_RECEIPT_SHA256 = "1CB738FE133F6645B9757E246E2FBC210785086342F70BAD32B5015BD19E1B1E"
OLD_EMPTY_REGISTRY = PROJECT_ROOT / "contracts" / "v1" / "registry" / "worker-registry.v1.json"
OLD_EMPTY_REGISTRY_SHA256 = "ACB4BA01ADBC0A410AAE7050FEC04F527C47EE6D0A98F30B73E7AC2B0E35F6E1"


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def fail(message: str) -> None:
    raise RuntimeError(message)


def main() -> int:
    checkpoint = json.loads(CHECKPOINT.read_text(encoding="utf-8"))
    if checkpoint.get("schemaVersion") != "knowledge-clipper-core-identity-checkpoint.v2":
        fail("unexpected checkpoint schemaVersion")
    if checkpoint.get("status") != "passed":
        fail("checkpoint is not passed")

    mismatches: list[str] = []
    for item in checkpoint["files"]:
        path = PROJECT_ROOT / item["path"]
        if not path.is_file():
            mismatches.append(f"missing:{item['path']}")
            continue
        if path.stat().st_size != item["bytes"]:
            mismatches.append(f"bytes:{item['path']}")
        if digest(path) != item["sha256"]:
            mismatches.append(f"sha256:{item['path']}")
    if mismatches:
        fail("checkpoint file mismatches: " + ", ".join(mismatches))

    if digest(FROZEN_RECEIPT) != FROZEN_RECEIPT_SHA256:
        fail("frozen v1 checkpoint receipt changed")
    frozen = json.loads(FROZEN_RECEIPT.read_text(encoding="utf-8"))
    frozen_mismatches = []
    for item in frozen["files"]:
        path = Path(item["path"])
        if not path.is_absolute():
            path = PROJECT_ROOT / path
        if not path.is_file() or digest(path) != item["sha256"]:
            frozen_mismatches.append(item["path"])
    if frozen_mismatches:
        fail("frozen v1 files changed: " + ", ".join(frozen_mismatches))
    if digest(OLD_EMPTY_REGISTRY) != OLD_EMPTY_REGISTRY_SHA256:
        fail("old empty authority registry changed")

    identity = json.loads((PROJECT_ROOT / "contracts/core/v2/product-identity.v2.json").read_text(encoding="utf-8"))
    expected_identity = {
        "productId": "knowledge-clipper",
        "productName": "零碎知识剪藏",
        "assemblyRoot": "KnowledgeClipper",
        "namespaceRoot": "KnowledgeClipper",
        "desktopEntrypoint": "KnowledgeClipper.exe",
        "orchestratorComponentId": "knowledge-clipper.orchestrator",
        "architecture": "destination-neutral",
        "destinations": ["remnote", "anki"],
    }
    for key, expected in expected_identity.items():
        if identity.get(key) != expected:
            fail(f"identity mismatch: {key}")

    source_files = list((PROJECT_ROOT / "orchestrator").glob("*.cs")) + list((PROJECT_ROOT / "orchestrator/Core").glob("*.cs"))
    namespace_violations = []
    for path in source_files:
        for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            if "namespace RemNoteQuickClipper" in line or "using RemNoteQuickClipper" in line:
                namespace_violations.append(f"{path.relative_to(PROJECT_ROOT)}:{number}")
    if namespace_violations:
        fail("legacy namespace remains in canonical source: " + ", ".join(namespace_violations))

    executable = PROJECT_ROOT / "dist/orchestrator/win-x64/v2.0.0/KnowledgeClipper.Orchestrator.exe"
    completed = subprocess.run([str(executable), "describe"], cwd=PROJECT_ROOT, capture_output=True, text=True, encoding="utf-8", check=True)
    described = json.loads(completed.stdout)
    if described.get("productId") != "knowledge-clipper" or described.get("componentId") != "knowledge-clipper.orchestrator":
        fail("published executable describe identity mismatch")
    if described.get("architecture") != "destination-neutral" or described.get("destinations") != ["remnote", "anki"]:
        fail("published executable destination model mismatch")

    result_paths = list((PROJECT_ROOT / "canary/core-identity/knowledge-clipper-core-v2-20260826T031231Z/job/runs/run-002/stages/core-noop-canary/attempts").glob("*/result.json"))
    if len(result_paths) != 1:
        fail("corrected dispatcher canary must contain exactly one result")
    result = json.loads(result_paths[0].read_text(encoding="utf-8"))
    if result.get("status") != "completed" or result.get("worker", {}).get("id") != "test.fake":
        fail("corrected dispatcher canary result is not completed")

    print(json.dumps({
        "schemaVersion": "knowledge-clipper-core-identity-verification.v2",
        "status": "passed",
        "checkpointFiles": len(checkpoint["files"]),
        "frozenV1Files": len(frozen["files"]),
        "frozenV1Mismatches": 0,
        "oldEmptyRegistryModified": False,
        "publishedIdentity": {
            "productId": described["productId"],
            "componentId": described["componentId"],
            "architecture": described["architecture"],
            "destinations": described["destinations"],
        },
        "dispatcherCanary": "succeeded",
        "externalProductionApisInvoked": False,
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(json.dumps({"status": "failed", "error": str(exc)}, ensure_ascii=False), file=sys.stderr)
        raise SystemExit(1)
