from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import stat
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath


class ConformanceError(RuntimeError):
    pass


def require(condition: bool, code: str) -> None:
    if not condition:
        raise ConformanceError(code)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def file_ref(project: Path, path: Path) -> dict:
    return {"path": path.relative_to(project).as_posix(), "bytes": path.stat().st_size, "sha256": sha256_file(path)}


def safe_relative(value: str) -> bool:
    path = PurePosixPath(value)
    return not path.is_absolute() and ".." not in path.parts and "\\" not in value


def tree_sha(entries: list[dict]) -> str:
    rows = [f"{item['path']}\t{item['sha256'].upper()}\t{item['bytes']}\n" for item in sorted(entries, key=lambda item: item["path"])]
    return hashlib.sha256("".join(rows).encode("utf-8")).hexdigest().upper()


def main() -> int:
    parser = argparse.ArgumentParser(description="Independent Core v3 foundation.2 immutable release conformance")
    parser.add_argument("--project-root", type=Path, required=True)
    parser.add_argument("--release-root", type=Path, required=True)
    parser.add_argument("--expected-packet-sha256", required=True)
    parser.add_argument("--expected-checkpoint-sha256", required=True)
    parser.add_argument("--expected-inventory-sha256", required=True)
    parser.add_argument("--expected-inventory-tree-sha256", required=True)
    parser.add_argument("--expected-tests-evidence-sha256", required=True)
    parser.add_argument("--isolated-test-receipt", type=Path, required=True)
    parser.add_argument("--isolated-test-log", type=Path, required=True)
    parser.add_argument("--positive-build-log", type=Path, required=True)
    parser.add_argument("--positive-probe", type=Path, required=True)
    parser.add_argument("--forbidden-build-log", type=Path, required=True)
    parser.add_argument("--forbidden-verdict-log", type=Path, required=True)
    parser.add_argument("--assembly-probe", type=Path, required=True)
    parser.add_argument("--sdk-info-log", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()

    project = args.project_root.resolve()
    release = args.release_root.resolve()
    output = args.output_root.resolve()
    allowed = (project / "canary/quality-v3-core-foundation2-conformance").resolve()
    require(output.is_relative_to(allowed) and not output.exists(), "foundation2.output-not-append-only")
    require(release.is_relative_to((project / "core-v3/releases").resolve()), "foundation2.release-root-location")

    packet_path = release / "typed-sdk-packet.json"
    checkpoint_path = release / "checkpoint.json"
    inventory_path = release / "inventory.json"
    require(sha256_file(packet_path) == args.expected_packet_sha256.upper(), "foundation2.packet-sha")
    require(sha256_file(checkpoint_path) == args.expected_checkpoint_sha256.upper(), "foundation2.checkpoint-sha")
    require(sha256_file(inventory_path) == args.expected_inventory_sha256.upper(), "foundation2.inventory-sha")
    packet = load_json(packet_path)
    checkpoint = load_json(checkpoint_path)
    inventory = load_json(inventory_path)
    require(packet["status"] == "PASS" and checkpoint["status"] == "PASS", "foundation2.declared-status")
    require(packet["boundary"] == "foundation-only", "foundation2.boundary")
    require(packet["immutableReleaseRoot"] == release.relative_to(project).as_posix(), "foundation2.root-binding")
    require(checkpoint["typedSdkPacket"]["sha256"].upper() == args.expected_packet_sha256.upper(), "foundation2.checkpoint-packet-binding")
    require(checkpoint["inventory"]["sha256"].upper() == args.expected_inventory_sha256.upper(), "foundation2.checkpoint-inventory-binding")

    audited_entries = []
    for expected in inventory["entries"]:
        require(safe_relative(expected["path"]), "foundation2.inventory-unsafe-path")
        path = release / expected["path"]
        require(path.is_file(), "foundation2.inventory-missing:" + expected["path"])
        actual = {"path": expected["path"], "bytes": path.stat().st_size, "sha256": sha256_file(path)}
        require(actual["bytes"] == expected["bytes"] and actual["sha256"] == expected["sha256"].upper(), "foundation2.inventory-drift:" + expected["path"])
        audited_entries.append(actual)
    calculated_tree = tree_sha(audited_entries)
    require(calculated_tree == inventory["treeSha256"].upper() == args.expected_inventory_tree_sha256.upper(), "foundation2.inventory-tree")
    actual_files = sorted(path.relative_to(release).as_posix() for path in release.rglob("*") if path.is_file())
    expected_files = sorted([item["path"] for item in audited_entries] + ["inventory.json", "checkpoint.json"])
    require(actual_files == expected_files, "foundation2.uninventoried-files")
    reparse = [value for value in actual_files if os.path.islink(release / value)]
    non_readonly = [value for value in actual_files if not (os.stat(release / value).st_file_attributes & stat.FILE_ATTRIBUTE_READONLY)]
    require(not reparse, "foundation2.reparse-point")
    require(not non_readonly, "foundation2.not-readonly")

    snapshot_entries = [item for item in audited_entries if item["path"].startswith("snapshot/")]
    snapshot_tree = tree_sha(snapshot_entries)
    require(snapshot_tree == packet["sourceAndContractSnapshotTreeSha256"].upper() == checkpoint["sourceAndContractSnapshotTreeSha256"].upper(), "foundation2.snapshot-tree")
    inventory_paths = {item["path"] for item in audited_entries}
    packet_refs = [packet["publicAssembly"], *packet["runtimeDependencies"], *packet["evidence"]]
    for item in packet_refs:
        require(safe_relative(item["path"]) and item["path"] in inventory_paths, "foundation2.packet-reference-boundary")
        actual = next(entry for entry in audited_entries if entry["path"] == item["path"])
        require(actual["bytes"] == item["bytes"] and actual["sha256"] == item["sha256"].upper(), "foundation2.packet-reference-drift")
    for relative in (packet["publicProject"], packet["contractProject"], packet["externalConsumerGate"]["positive"], packet["externalConsumerGate"]["negative"]):
        require(safe_relative(relative) and relative in inventory_paths, "foundation2.packet-source-reference")

    public_assembly = release / packet["publicAssembly"]["path"]
    contracts_entry = next(item for item in packet["runtimeDependencies"] if item["path"].endswith("Contracts.dll"))
    contracts_assembly = release / contracts_entry["path"]
    require(sha256_file(public_assembly) == "7C191017324F6464D0A8328CF5506F1A6960BC0148B25FAC4F5BD1D3B20F5D8B", "foundation2.client-assembly-identity")
    require(sha256_file(contracts_assembly) == "831DBE54E8202A513F94E8ECCB419D8EB5847E587F981DB30DE585FA74E78DFC", "foundation2.contracts-assembly-identity")

    owner_tests_path = release / "evidence/foundation2-tests.receipt.json"
    require(sha256_file(owner_tests_path) == args.expected_tests_evidence_sha256.upper(), "foundation2.owner-tests-sha")
    owner_tests = load_json(owner_tests_path)
    require(owner_tests["status"] == "PASS" and len(owner_tests["tests"]) == 30 and all(item["status"] == "PASS" for item in owner_tests["tests"]), "foundation2.owner-tests")

    isolated_receipt_path = args.isolated_test_receipt.resolve()
    isolated_receipt = load_json(isolated_receipt_path)
    isolated_log_path = args.isolated_test_log.resolve()
    isolated_log = isolated_log_path.read_text(encoding="utf-8")
    require(isolated_receipt["status"] == "PASS" and len(isolated_receipt["tests"]) == 30 and all(item["status"] == "PASS" for item in isolated_receipt["tests"]), "foundation2.isolated-tests")
    require("RESULT PASS 30/30" in isolated_log, "foundation2.isolated-test-log")
    test_names = {item["name"] for item in isolated_receipt["tests"]}
    required_tests = {
        "command-index-concurrent-same-key", "command-index-same-key-different-body", "command-index-restart-replay",
        "startup-recovery-query-without-resubmit", "operation-handler-cancellation-terminal", "operation-timeout-late-completion-terminal",
        "artifact-v2-publish-and-open", "client-response-schema-fail-closed", "client-response-correlation-fail-closed",
        "client-response-type-fail-closed", "client-response-duplicate-property-fail-closed", "process-current-and-legacy-equivalence",
    }
    require(required_tests.issubset(test_names), "foundation2.behavioral-test-set")
    test_source = (release / "snapshot/core-v3/tests/KnowledgeClipper.Core.V3.FoundationTests/Program.cs").read_text(encoding="utf-8")
    for anchor in (
        "Exactly one concurrent writer must win", "Same idempotency key with a different canonical body was accepted",
        "Restart replay executed the handler again", "Startup sweep left the projection Running",
        "produced multiple terminal events", "Typed artifact publish/read roundtrip changed content",
        "Long-running observation did not remain active for more than five seconds",
        "A healthy >5s running projection was degraded because its cursor was unchanged",
    ):
        require(anchor in test_source, "foundation2.behavior-anchor")

    positive_path = args.positive_probe.resolve()
    positive = load_json(positive_path)
    require(positive["lowLevelProtocolClientPublic"] is False and positive["clientFacadeProtocolTypeLeaks"] == [], "foundation2.positive-public-boundary")
    require(positive["artifactHandlePublicConstructors"] == 0 and positive["artifactHandlePublicProperties"] == ["Token"], "foundation2.opaque-handle")
    require(positive["oversizedRequestFailureCode"] == "request_frame_too_large" and positive["oversizedResponseFailureCode"] == "response_frame_too_large", "foundation2.frame-limit")
    positive_build_path = args.positive_build_log.resolve()
    positive_build = positive_build_path.read_text(encoding="utf-8")
    require("0 个错误" in positive_build or "0 Error(s)" in positive_build, "foundation2.positive-build")

    forbidden_build_path = args.forbidden_build_log.resolve()
    forbidden_build = forbidden_build_path.read_text(encoding="utf-8")
    forbidden_verdict_path = args.forbidden_verdict_log.resolve()
    forbidden_verdict = forbidden_verdict_path.read_text(encoding="utf-8")
    require("error CS0234" in forbidden_build and "EXPECTED_FORBIDDEN_CONSUMER_BUILD_FAILURE exitCode=1" in forbidden_verdict, "foundation2.forbidden-consumer")

    assembly_path = args.assembly_probe.resolve()
    assembly = load_json(assembly_path)
    require(assembly["contractsExportedTypeCount"] == 0 and assembly["contractsExportedTypes"] == [], "foundation2.contracts-export-surface")
    require(assembly["clientPublicSignatureContractLeaks"] == [], "foundation2.client-signature-surface")
    require("KnowledgeClipper.Core.V3.Client.CoreV3ProtocolClient" not in assembly["clientExportedTypes"], "foundation2.protocol-client-export")

    catalog = load_json(release / "snapshot/contracts/core/v3/capabilities/capability-catalog.v1.json")
    unavailable = sorted(item["operationId"] for item in catalog["operations"] if not item["available"])
    expected_unavailable = ["anki.probe", "question.follow-up", "question.initial", "question.make-cards"]
    require(unavailable == expected_unavailable, "foundation2.capability-boundary")
    matrix = load_json(release / "snapshot/contracts/core/v3/compatibility/legacy-ipc-matrix.v1.json")
    require(matrix["ownerBoundary"] == "Kernel.CompatibilityGateway", "foundation2.compatibility-owner")
    require(matrix["recognizedEnvelopeVersions"] == ["1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "1.7"], "foundation2.compatibility-versions")

    sdk_info_path = args.sdk_info_log.resolve()
    sdk_info = sdk_info_path.read_text(encoding="utf-8")
    require("8.0.424" in sdk_info and "RemNoteQuickClipperRuntime" in sdk_info, "foundation2.sdk-identity")
    quality_files = [
        project / "tests/QualityV3.Foundation/core_v3_foundation2_conformance.py",
        project / "tests/QualityV3.Foundation/CoreSdkPublicConsumer.Tests/QualityV3.CoreSdkPublicConsumer.Tests.csproj",
        project / "tests/QualityV3.Foundation/CoreSdkPublicConsumer.Tests/Program.cs",
        project / "tests/QualityV3.Foundation/CoreSdkForbiddenConsumer.CompileProbe/QualityV3.CoreSdkForbiddenConsumer.CompileProbe.csproj",
        project / "tests/QualityV3.Foundation/CoreSdkForbiddenConsumer.CompileProbe/Program.cs",
        project / "tests/QualityV3.Foundation/CoreAssemblySurfaceProbe/QualityV3.CoreAssemblySurfaceProbe.csproj",
        project / "tests/QualityV3.Foundation/CoreAssemblySurfaceProbe/Program.cs",
    ]
    result = {
        "schemaVersion": "quality-v3-core-foundation2-conformance.v1",
        "createdAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-FOUNDATION2-CONFORMANCE",
        "release": {
            "root": release.relative_to(project).as_posix(), "fileCount": len(actual_files), "inventoryEntryCount": len(audited_entries),
            "allInventoryReferencesMatch": True, "allFilesReadOnly": True, "reparsePointCount": 0,
            "inventoryTreeSha256": calculated_tree, "snapshotTreeSha256": snapshot_tree,
            "packet": file_ref(project, packet_path), "checkpoint": file_ref(project, checkpoint_path), "inventory": file_ref(project, inventory_path),
            "publicAssembly": file_ref(project, public_assembly), "contractsAssembly": file_ref(project, contracts_assembly),
        },
        "tests": {
            "ownerEvidence": file_ref(project, owner_tests_path), "ownerEvidenceCount": 30,
            "isolatedFrozenSnapshotReceipt": file_ref(project, isolated_receipt_path), "isolatedFrozenSnapshotLog": file_ref(project, isolated_log_path),
            "isolatedFrozenSnapshotCount": 30,
        },
        "consumerGates": {
            "positive": "PASS", "positiveBuild": file_ref(project, positive_build_path), "positiveProbe": file_ref(project, positive_path),
            "forbiddenProtocolConsumer": "EXPECTED-COMPILE-FAIL", "forbiddenBuild": file_ref(project, forbidden_build_path), "forbiddenVerdict": file_ref(project, forbidden_verdict_path),
            "assemblySurface": "PASS", "assemblyProbe": file_ref(project, assembly_path), "contractsExportedTypeCount": 0,
            "clientPublicSignatureContractLeaks": 0, "opaqueArtifactHandle": "PASS", "requestFrameLimit": "PASS", "responseFrameLimit": "PASS",
        },
        "behavioralConformance": {
            "responseSchemaCorrelationTypeFailClosed": "PASS", "artifactPublishReadRoundtrip": "PASS",
            "persistentIdempotencyRetryConflictConcurrencyRestart": "PASS", "acceptedCommandTerminalGuarantee": "PASS",
            "startupRecoveryQueryOnly": "PASS", "observationOverFiveSecondsWithStableCursor": "PASS",
            "legacyCompatibilityVersions": matrix["recognizedEnvelopeVersions"], "legacyOwner": matrix["ownerBoundary"],
        },
        "foundationBoundary": {"unavailableOperations": unavailable, "verticalSliceCompleted": False, "desktopGatewayInputStatus": "READY"},
        "sdk": file_ref(project, sdk_info_path),
        "qualityVerifier": {"files": [file_ref(project, path) for path in quality_files]},
        "externalServicesInvoked": False,
        "candidatePromotion": "NO-GO",
    }
    output.mkdir(parents=True)
    receipt = output / "core-v3-foundation2-conformance.receipt.json"
    receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": result["status"], "receipt": receipt.relative_to(project).as_posix(), "receiptSha256": sha256_file(receipt), "desktopGatewayInputStatus": "READY", "candidatePromotion": "NO-GO"}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
