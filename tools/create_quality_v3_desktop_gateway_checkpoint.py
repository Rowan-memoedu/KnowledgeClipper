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


def collect_refs(value: object) -> list[dict]:
    refs: list[dict] = []
    if isinstance(value, dict):
        if set(value.keys()) == {"path", "bytes", "sha256"}:
            refs.append(value)
        else:
            for child in value.values():
                refs.extend(collect_refs(child))
    elif isinstance(value, list):
        for child in value:
            refs.extend(collect_refs(child))
    return refs


def main() -> int:
    parser = argparse.ArgumentParser(description="Create append-only Desktop v3 gateway conformance checkpoint")
    parser.add_argument("--project-root", type=Path, required=True)
    parser.add_argument("--conformance-receipt", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()
    project = args.project_root.resolve()
    receipt_path = args.conformance_receipt.resolve()
    output = args.output_root.resolve()
    allowed = (project / "receipts/quality-v3-desktop-gateway-conformance").resolve()
    if not output.is_relative_to(allowed) or output.exists():
        raise RuntimeError("gateway-checkpoint.output-not-append-only")
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    if (receipt["status"] != "PASS-GATEWAY-CROSS-CONFORMANCE"
            or receipt.get("gateAuthority") != "SUPERSEDING"
            or receipt["candidatePromotion"] != "NO-GO"):
        raise RuntimeError("gateway-checkpoint.status-boundary")
    if receipt["productionBoundary"]["viewProductionWiring"] != "NOT-SWITCHED" or receipt["productionBoundary"]["featureReadiness"] != "NO-GO":
        raise RuntimeError("gateway-checkpoint.production-boundary")

    checked = []
    seen = set()
    for expected in collect_refs(receipt):
        if expected["path"] in seen:
            continue
        seen.add(expected["path"])
        path = project / expected["path"]
        if not path.is_file() or path.stat().st_size != expected["bytes"] or sha256_file(path) != expected["sha256"].upper():
            raise RuntimeError("gateway-checkpoint.reference-drift:" + expected["path"])
        checked.append(file_ref(project, path))

    generator = project / "tools/create_quality_v3_desktop_gateway_checkpoint.py"
    checkpoint = {
        "schemaVersion": "quality-v3-desktop-gateway-cross-conformance-checkpoint.v2",
        "createdAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": receipt["status"],
        "gateAuthority": "SUPERSEDING",
        "conformanceReceipt": file_ref(project, receipt_path),
        "referenceAudit": {"status": "PASS", "checkedCount": len(checked), "files": checked},
        "checkpointGenerator": file_ref(project, generator),
        "coreFoundation2Status": receipt["foundation2"]["status"],
        "sourceSnapshotTreeSha256": receipt["delta"]["treeSha256"],
        "supersededSourceSnapshotTreeSha256": receipt["supersedes"]["declaredTreeSha256"],
        "supersededEvidenceStatus": receipt["supersedes"]["status"],
        "viewProductionWiring": "NOT-SWITCHED",
        "featureReadiness": "NO-GO",
        "candidatePromotion": "NO-GO",
    }
    output.mkdir(parents=True)
    path = output / "quality-desktop-gateway-cross-conformance-checkpoint.json"
    path.write_text(json.dumps(checkpoint, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "status": checkpoint["status"], "checkpoint": path.relative_to(project).as_posix(),
        "checkpointSha256": sha256_file(path), "referencesChecked": len(checked),
        "viewProductionWiring": "NOT-SWITCHED", "featureReadiness": "NO-GO",
        "candidatePromotion": "NO-GO",
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
