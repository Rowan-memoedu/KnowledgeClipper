from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def file_ref(project: Path, path: Path) -> dict:
    return {"path": path.relative_to(project).as_posix(), "bytes": path.stat().st_size, "sha256": sha256_file(path)}


def main() -> int:
    parser = argparse.ArgumentParser(description="Create an append-only checkpoint for Core v3 foundation.2 conformance")
    parser.add_argument("--project-root", type=Path, required=True)
    parser.add_argument("--conformance-receipt", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()
    project = args.project_root.resolve()
    receipt_path = args.conformance_receipt.resolve()
    output = args.output_root.resolve()
    allowed = (project / "receipts/quality-v3-core-foundation2-conformance").resolve()
    if not output.is_relative_to(allowed) or output.exists():
        raise RuntimeError("foundation2-checkpoint.output-not-append-only")
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    if receipt["status"] != "PASS-FOUNDATION2-CONFORMANCE" or receipt["candidatePromotion"] != "NO-GO":
        raise RuntimeError("foundation2-checkpoint.status-boundary")

    refs = [
        receipt["release"]["packet"], receipt["release"]["checkpoint"], receipt["release"]["inventory"],
        receipt["release"]["publicAssembly"], receipt["release"]["contractsAssembly"],
        receipt["tests"]["ownerEvidence"], receipt["tests"]["isolatedFrozenSnapshotReceipt"], receipt["tests"]["isolatedFrozenSnapshotLog"],
        receipt["consumerGates"]["positiveBuild"], receipt["consumerGates"]["positiveProbe"],
        receipt["consumerGates"]["forbiddenBuild"], receipt["consumerGates"]["forbiddenVerdict"], receipt["consumerGates"]["assemblyProbe"],
        receipt["sdk"], *receipt["qualityVerifier"]["files"],
    ]
    checked = []
    for expected in refs:
        path = project / expected["path"]
        if not path.is_file() or path.stat().st_size != expected["bytes"] or sha256_file(path) != expected["sha256"].upper():
            raise RuntimeError("foundation2-checkpoint.reference-drift:" + expected["path"])
        checked.append(file_ref(project, path))
    generator = project / "tools/create_quality_v3_core_foundation2_checkpoint.py"
    checkpoint = {
        "schemaVersion": "quality-v3-core-foundation2-conformance-checkpoint.v1",
        "createdAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": receipt["status"], "conformanceReceipt": file_ref(project, receipt_path),
        "referenceAudit": {"status": "PASS", "checkedCount": len(checked), "files": checked},
        "checkpointGenerator": file_ref(project, generator),
        "desktopGatewayInputStatus": receipt["foundationBoundary"]["desktopGatewayInputStatus"],
        "candidatePromotion": "NO-GO",
    }
    output.mkdir(parents=True)
    path = output / "quality-core-foundation2-conformance-checkpoint.json"
    path.write_text(json.dumps(checkpoint, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": checkpoint["status"], "checkpoint": path.relative_to(project).as_posix(), "checkpointSha256": sha256_file(path), "referencesChecked": len(checked), "desktopGatewayInputStatus": checkpoint["desktopGatewayInputStatus"], "candidatePromotion": "NO-GO"}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
