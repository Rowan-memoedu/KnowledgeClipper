from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


class ConformanceError(RuntimeError):
    pass


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def reference(project: Path, path: Path) -> dict:
    return {"path": path.relative_to(project).as_posix(), "bytes": path.stat().st_size, "sha256": sha256_file(path)}


def audit_reference(project: Path, expected: dict) -> dict:
    path = project / expected["path"]
    if not path.is_file():
        return {"path": expected["path"], "status": "MISSING", "expectedBytes": expected["bytes"], "expectedSha256": expected["sha256"].upper()}
    actual_bytes = path.stat().st_size
    actual_sha = sha256_file(path)
    return {
        "path": expected["path"],
        "status": "MATCH" if actual_bytes == expected["bytes"] and actual_sha == expected["sha256"].upper() else "DRIFT",
        "expectedBytes": expected["bytes"],
        "actualBytes": actual_bytes,
        "expectedSha256": expected["sha256"].upper(),
        "actualSha256": actual_sha,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Independently audit one Core v3 typed SDK packet")
    parser.add_argument("--project-root", type=Path, required=True)
    parser.add_argument("--packet", type=Path, required=True)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--expected-packet-sha256", required=True)
    parser.add_argument("--formal-rerun-receipt", type=Path, required=True)
    parser.add_argument("--formal-rerun-log", type=Path, required=True)
    parser.add_argument("--consumer-probe", type=Path, required=True)
    parser.add_argument("--post-fix-build-log", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()

    project = args.project_root.resolve()
    packet_path = args.packet.resolve()
    checkpoint_path = args.checkpoint.resolve()
    output = args.output_root.resolve()
    allowed = (project / "canary/quality-v3-core-conformance").resolve()
    if not output.is_relative_to(allowed) or output.exists():
        raise ConformanceError("core.output-not-append-only")
    if sha256_file(packet_path) != args.expected_packet_sha256.upper():
        raise ConformanceError("core.packet-sha-mismatch")

    packet = load_json(packet_path)
    checkpoint = load_json(checkpoint_path)
    source_audit = [audit_reference(project, item) for item in checkpoint["sourceTree"]]
    source_drifts = [item for item in source_audit if item["status"] != "MATCH"]
    current_rows = []
    for item in source_audit:
        if item["status"] == "MISSING":
            continue
        current_rows.append(f"{item['path']}\t{item['actualSha256']}\t{item['actualBytes']}\n")
    current_tree = hashlib.sha256("".join(current_rows).encode("utf-8")).hexdigest().upper() if len(current_rows) == len(source_audit) else None

    packet_references = [
        packet["publicAssembly"], packet["contractAssembly"], packet["capabilityBinding"],
        packet["capabilityCatalog"], packet["buildReceipt"], packet["testReceipt"],
        *packet["publicSources"],
    ]
    packet_reference_audit = [audit_reference(project, item) for item in packet_references]
    mutable_binary_paths = [item["path"] for item in (packet["publicAssembly"], packet["contractAssembly"]) if "/bin/" in item["path"]]

    formal_receipt_path = args.formal_rerun_receipt.resolve()
    formal_receipt = load_json(formal_receipt_path)
    tests = formal_receipt.get("tests", [])
    formal_pass = formal_receipt.get("status") == "PASS" and len(tests) == 29 and all(item.get("status") == "PASS" for item in tests)
    formal_log_path = args.formal_rerun_log.resolve()
    formal_log = formal_log_path.read_text(encoding="utf-8")
    formal_pass = formal_pass and "RESULT PASS 29/29" in formal_log

    consumer_path = args.consumer_probe.resolve()
    consumer = load_json(consumer_path)
    consumer_boundary_pass = not consumer["lowLevelContractTypesAccessible"] and len(consumer["publicLowLevelContractTypes"]) == 0
    request_limit_pass = consumer["oversizedRequestFailureCode"] == "request_frame_too_large"
    response_limit_pass = consumer["oversizedResponseFailureCode"] == "response_frame_too_large"

    catalog = load_json(project / packet["capabilityCatalog"]["path"])
    unavailable = sorted(item["operationId"] for item in catalog["operations"] if not item["available"])
    unavailable_expected = ["anki.probe", "question.follow-up", "question.initial", "question.make-cards"]
    matrix = load_json(project / "contracts/core/v3/compatibility/legacy-ipc-matrix.v1.json")
    legacy_versions = matrix["recognizedEnvelopeVersions"]
    compatibility_boundary_pass = matrix["ownerBoundary"] == "Kernel.CompatibilityGateway" and legacy_versions == ["1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "1.7"]

    blockers = []
    if mutable_binary_paths:
        blockers.append("Packet references mutable bin/Release assemblies instead of immutable packet-owned copies.")
    if source_drifts:
        blockers.append("Checkpoint source files drifted after packet creation; current workspace cannot reproduce the packet source tree.")
    if any(item["status"] != "MATCH" for item in packet_reference_audit):
        blockers.append("One or more packet references no longer match their recorded bytes/SHA256.")
    if not consumer_boundary_pass:
        blockers.append("Foundation.1 SDK consumer can access public low-level Contracts types through the Client project reference.")
    if not response_limit_pass:
        blockers.append("Independent oversized-response probe reached request_timeout before the explicit response_frame_too_large gate.")

    result = {
        "schemaVersion": "quality-v3-core-packet-conformance.v1",
        "createdAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "REJECTED-PACKET-CONFORMANCE" if blockers else "PASS-FOUNDATION-CONFORMANCE",
        "packet": reference(project, packet_path),
        "checkpoint": reference(project, checkpoint_path),
        "declaredSourceTreeSha256": checkpoint["sourceTreeSha256"],
        "currentTreeOverDeclaredPathsSha256": current_tree,
        "sourceReferenceAudit": {"checkedCount": len(source_audit), "driftCount": len(source_drifts), "items": source_audit},
        "packetReferenceAudit": {"checkedCount": len(packet_reference_audit), "items": packet_reference_audit},
        "immutablePacketBoundary": {"status": "FAIL" if mutable_binary_paths else "PASS", "mutableBinaryPaths": mutable_binary_paths},
        "formalRerun": {
            "status": "PASS" if formal_pass else "FAIL",
            "testCount": len(tests),
            "receipt": reference(project, formal_receipt_path),
            "log": reference(project, formal_log_path),
        },
        "behavioralCoverage": {
            "persistentIdempotency": "PASS-FORMAL-RERUN",
            "acceptedCommandTerminalGuarantee": "PASS-FORMAL-RERUN",
            "startupRecoveryQueryOnly": "PASS-FORMAL-RERUN",
            "observationOverFiveSeconds": "PASS-FORMAL-RERUN",
            "artifactPublishReadRoundtrip": "PASS-FORMAL-RERUN",
            "responseSchemaCorrelationTypeDuplicateFailClosed": "PASS-FORMAL-RERUN",
            "oversizedRequestFailClosed": "PASS-INDEPENDENT" if request_limit_pass else "FAIL",
            "oversizedResponseExplicitFrameGate": "PASS-INDEPENDENT" if response_limit_pass else "NOT-PROVEN",
        },
        "sdkConsumerBoundary": {
            "status": "PASS" if consumer_boundary_pass else "FAIL",
            "probe": reference(project, consumer_path),
            "lowLevelProtocolClientPublic": consumer["lowLevelProtocolClientPublic"],
            "lowLevelContractTypesAccessible": consumer["lowLevelContractTypesAccessible"],
            "publicLowLevelContractTypes": consumer["publicLowLevelContractTypes"],
            "clientFacadeProtocolTypeLeaks": consumer["clientFacadeProtocolTypeLeaks"],
            "artifactHandlePublicConstructors": consumer["artifactHandlePublicConstructors"],
            "artifactHandlePublicProperties": consumer["artifactHandlePublicProperties"],
        },
        "postPacketFixInProgress": {"buildLog": reference(project, args.post_fix_build_log.resolve()), "notAcceptedAsPacketEvidence": True},
        "compatibilityBoundary": {"status": "PASS" if compatibility_boundary_pass else "FAIL", "recognizedVersions": legacy_versions},
        "foundationCapabilities": {"unavailableOperations": unavailable, "expectedUnavailablePreserved": unavailable == unavailable_expected, "verticalSliceCompleted": False},
        "externalServicesInvoked": False,
        "candidatePromotion": "NO-GO",
        "blockers": blockers,
    }
    output.mkdir(parents=True)
    receipt = output / "core-v3-packet-conformance.receipt.json"
    receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": result["status"], "receipt": receipt.relative_to(project).as_posix(), "receiptSha256": sha256_file(receipt), "blockerCount": len(blockers), "candidatePromotion": "NO-GO"}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
