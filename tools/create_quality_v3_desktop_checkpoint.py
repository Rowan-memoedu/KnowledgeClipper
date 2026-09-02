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


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify Desktop v3 conformance references and create an append-only checkpoint")
    parser.add_argument("--project-root", type=Path, required=True)
    parser.add_argument("--conformance-receipt", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()

    project = args.project_root.resolve()
    receipt_path = args.conformance_receipt.resolve()
    output = args.output_root.resolve()
    allowed = (project / "receipts/quality-v3-desktop-conformance").resolve()
    if not output.is_relative_to(allowed) or output.exists():
        raise RuntimeError("checkpoint.output-not-append-only")
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    if receipt["status"] != "PASS-PHASE-1-CONFORMANCE":
        raise RuntimeError("checkpoint.conformance-status")
    if receipt["checks"]["gatewayStatus"] != "AWAITING_INPUT" or receipt["candidatePromotion"]["status"] != "NO-GO":
        raise RuntimeError("checkpoint.boundary-status")

    references = []
    references.extend(receipt["sourceInput"]["files"])
    references.extend(receipt["tests"].values())
    references.extend(receipt["qualityVerifier"]["files"])
    references.append({key: receipt["windowsUiEvidence"][key] for key in ("path", "bytes", "sha256")})
    references.extend(receipt["windowsUiEvidence"]["referencedLogs"])
    checked = []
    for reference in references:
        path = project / reference["path"]
        if not path.is_file():
            raise RuntimeError("checkpoint.reference-missing:" + reference["path"])
        actual_bytes = path.stat().st_size
        actual_sha = sha256_file(path)
        if actual_bytes != reference["bytes"] or actual_sha != reference["sha256"].upper():
            raise RuntimeError("checkpoint.reference-drift:" + reference["path"])
        checked.append({"path": reference["path"], "bytes": actual_bytes, "sha256": actual_sha})

    generator = project / "tools/create_quality_v3_desktop_checkpoint.py"
    checkpoint = {
        "schemaVersion": "quality-v3-desktop-conformance-checkpoint.v1",
        "createdAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-PHASE-1-CONFORMANCE",
        "conformanceReceipt": {
            "path": receipt_path.relative_to(project).as_posix(),
            "bytes": receipt_path.stat().st_size,
            "sha256": sha256_file(receipt_path),
        },
        "referenceAudit": {"status": "PASS", "checkedCount": len(checked), "references": checked},
        "checkpointGenerator": {
            "path": generator.relative_to(project).as_posix(),
            "bytes": generator.stat().st_size,
            "sha256": sha256_file(generator),
        },
        "gatewayStatus": "AWAITING_INPUT",
        "candidatePromotion": "NO-GO",
        "externalServicesInvoked": False,
    }
    output.mkdir(parents=True)
    checkpoint_path = output / "quality-desktop-conformance-checkpoint.json"
    checkpoint_path.write_text(json.dumps(checkpoint, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "status": checkpoint["status"],
        "checkpoint": checkpoint_path.relative_to(project).as_posix(),
        "checkpointSha256": sha256_file(checkpoint_path),
        "referencesChecked": len(checked),
        "candidatePromotion": "NO-GO",
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
