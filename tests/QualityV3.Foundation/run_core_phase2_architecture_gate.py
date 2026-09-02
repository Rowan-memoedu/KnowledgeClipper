from __future__ import annotations

import argparse
import hashlib
import json
import os
import stat
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath


PINNED_DOTNET = Path(r"D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe")


class GateError(RuntimeError):
    pass


def require(condition: bool, code: str) -> None:
    if not condition:
        raise GateError(code)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def file_ref(project: Path, path: Path) -> dict:
    return {"path": path.relative_to(project).as_posix(), "bytes": path.stat().st_size, "sha256": sha256_file(path)}


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def safe_relative(value: str) -> bool:
    path = PurePosixPath(value)
    return not path.is_absolute() and ".." not in path.parts and "\\" not in value


def inventory_tree(entries: list[dict]) -> str:
    rows = [f"{item['path']}\t{item['sha256'].upper()}\t{item['bytes']}\n" for item in sorted(entries, key=lambda item: item["path"])]
    return hashlib.sha256("".join(rows).encode("utf-8")).hexdigest().upper()


def run_streamed(command: list[str], cwd: Path, log_path: Path) -> int:
    log_path.parent.mkdir(parents=True, exist_ok=True)
    with log_path.open("w", encoding="utf-8", newline="\n") as log:
        process = subprocess.Popen(
            command,
            cwd=cwd,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        assert process.stdout is not None
        for line in process.stdout:
            sys.stdout.write(line)
            sys.stdout.flush()
            log.write(line)
            log.flush()
        return process.wait()


def verify_release(project: Path, release: Path, expected: dict[str, str]) -> dict:
    require(release.is_relative_to((project / "core-v3/releases").resolve()), "phase2.release-root-location")
    packet_path = release / "typed-sdk-slice-packet.json"
    checkpoint_path = release / "checkpoint.json"
    inventory_path = release / "inventory.json"
    snapshot = release / "snapshot"
    for path in (packet_path, checkpoint_path, inventory_path):
        require(path.is_file(), "phase2.release-control-file-missing:" + path.name)
    require(snapshot.is_dir(), "phase2.snapshot-missing")
    require(sha256_file(checkpoint_path) == expected["checkpoint"], "phase2.checkpoint-external-binding")
    require(sha256_file(packet_path) == expected["packet"], "phase2.packet-external-binding")
    require(sha256_file(inventory_path) == expected["inventory"], "phase2.inventory-external-binding")
    packet = load_json(packet_path)
    checkpoint = load_json(checkpoint_path)
    inventory = load_json(inventory_path)
    require(packet.get("status") == "PASS-not-promoted" and checkpoint.get("status") == "PASS-not-promoted", "phase2.release-declared-status")
    require(packet.get("boundary") == "phase2-question-anki-test-only-slice", "phase2.release-boundary")
    require(packet.get("immutableReleaseRoot") == release.relative_to(project).as_posix(), "phase2.packet-root-binding")
    require(checkpoint.get("immutableReleaseRoot") == release.relative_to(project).as_posix(), "phase2.checkpoint-root-binding")
    require(checkpoint.get("authorityPromoted") is False and checkpoint.get("productionEnabled") is False and checkpoint.get("candidateAssembled") is False, "phase2.not-promoted-boundary")
    require(checkpoint.get("inventory", {}).get("sha256", "").upper() == expected["inventory"], "phase2.checkpoint-inventory-binding")
    require(checkpoint.get("typedSdkSlicePacket", {}).get("sha256", "").upper() == expected["packet"], "phase2.checkpoint-packet-binding")
    require(checkpoint.get("inventoryTreeSha256", "").upper() == expected["inventory_tree"], "phase2.checkpoint-inventory-tree-binding")
    require(checkpoint.get("sourceAndContractSnapshotTreeSha256", "").upper() == expected["snapshot_tree"], "phase2.checkpoint-snapshot-tree-binding")
    require(packet.get("sourceAndContractSnapshotTreeSha256", "").upper() == expected["snapshot_tree"], "phase2.packet-snapshot-tree-binding")
    audited = []
    for inventory_entry in inventory.get("entries", []):
        relative = inventory_entry["path"]
        require(safe_relative(relative), "phase2.inventory-unsafe-path")
        path = release / relative
        require(path.is_file(), "phase2.inventory-missing:" + relative)
        actual = {"path": relative, "bytes": path.stat().st_size, "sha256": sha256_file(path)}
        require(actual["bytes"] == inventory_entry["bytes"] and actual["sha256"] == inventory_entry["sha256"].upper(), "phase2.inventory-drift:" + relative)
        audited.append(actual)
    require(len(audited) > 0 and inventory_tree(audited) == expected["inventory_tree"], "phase2.inventory-tree")
    require(inventory.get("treeSha256", "").upper() == expected["inventory_tree"], "phase2.inventory-declared-tree")
    snapshot_entries = [item for item in audited if item["path"].startswith("snapshot/")]
    require(len(snapshot_entries) > 0 and inventory_tree(snapshot_entries) == expected["snapshot_tree"], "phase2.snapshot-tree")
    actual_files = sorted(path.relative_to(release).as_posix() for path in release.rglob("*") if path.is_file())
    expected_files = sorted([item["path"] for item in audited] + ["inventory.json", "checkpoint.json"])
    require(actual_files == expected_files, "phase2.uninventoried-files")
    require(not any(os.path.islink(release / relative) for relative in actual_files), "phase2.reparse-point")
    if os.name == "nt":
        require(all(os.stat(release / relative).st_file_attributes & stat.FILE_ATTRIBUTE_READONLY for relative in actual_files), "phase2.not-readonly")
    by_path = {item["path"]: item for item in audited}
    packet_refs = [packet["publicAssembly"], *packet.get("runtimeDependencies", []), *packet.get("evidence", [])]
    for reference in packet_refs:
        relative = reference["path"]
        require(relative in by_path, "phase2.packet-reference-not-in-inventory:" + relative)
        require(reference["bytes"] == by_path[relative]["bytes"] and reference["sha256"].upper() == by_path[relative]["sha256"], "phase2.packet-reference-drift:" + relative)
    for relative in (packet["publicProject"], packet["capabilityBinding"], packet["capabilityCatalog"]):
        require(relative in by_path, "phase2.packet-path-not-in-inventory:" + relative)

    binding_path = release / packet["capabilityBinding"]
    require(binding_path.is_file(), "phase2.capability-binding-missing")
    binding = load_json(binding_path)
    require(safe_relative(binding["catalogPath"]), "phase2.capability-catalog-unsafe-path")
    catalog_path = snapshot / binding["catalogPath"]
    require(catalog_path.is_file() and sha256_file(catalog_path) == binding["catalogSha256"].upper(), "phase2.capability-catalog-binding")
    catalog = load_json(catalog_path)
    by_operation = {item["operationId"]: item for item in catalog["operations"]}
    required_operations = ["anki.probe", "question.initial", "question.follow-up", "question.make-cards"]
    require(all(by_operation.get(operation, {}).get("available") is True for operation in required_operations), "phase2.vertical-slice-not-admitted")
    host_reference = next((item for item in packet["runtimeDependencies"] if item["path"].endswith("/KnowledgeClipper.Core.V3.Host.dll")), None)
    require(host_reference is not None, "phase2.host-assembly-not-packaged")
    return {
        "root": release.relative_to(project).as_posix(),
        "packet": file_ref(project, packet_path),
        "checkpoint": file_ref(project, checkpoint_path),
        "inventory": file_ref(project, inventory_path),
        "inventoryEntryCount": len(audited),
        "inventoryTreeSha256": expected["inventory_tree"],
        "snapshotTreeSha256": expected["snapshot_tree"],
        "allFilesReadOnly": True,
        "reparsePointCount": 0,
        "availableOperations": required_operations,
        "capabilityBinding": file_ref(project, binding_path),
        "capabilityCatalog": file_ref(project, catalog_path),
        "hostAssemblyPath": host_reference["path"],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Run independent Core Phase 2 architecture gates against an immutable packet")
    parser.add_argument("--project-root", type=Path, required=True)
    parser.add_argument("--release-root", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    parser.add_argument("--expected-checkpoint-sha256", required=True)
    parser.add_argument("--expected-packet-sha256", required=True)
    parser.add_argument("--expected-inventory-sha256", required=True)
    parser.add_argument("--expected-inventory-tree-sha256", required=True)
    parser.add_argument("--expected-snapshot-tree-sha256", required=True)
    args = parser.parse_args()
    project = args.project_root.resolve()
    release = args.release_root.resolve()
    output = args.output_root.resolve()
    allowed = (project / "canary/quality-v3-core-phase2-architecture").resolve()
    require(output.is_relative_to(allowed) and not output.exists(), "phase2.output-not-append-only")
    output.mkdir(parents=True)
    formal_receipt = output / "quality-core-phase2-architecture-gate.receipt.json"
    started = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    try:
        require(PINNED_DOTNET.is_file(), "phase2.pinned-sdk-missing")
        expected = {
            "checkpoint": args.expected_checkpoint_sha256.upper(),
            "packet": args.expected_packet_sha256.upper(),
            "inventory": args.expected_inventory_sha256.upper(),
            "inventory_tree": args.expected_inventory_tree_sha256.upper(),
            "snapshot_tree": args.expected_snapshot_tree_sha256.upper(),
        }
        require(all(len(value) == 64 and all(character in "0123456789ABCDEF" for character in value) for value in expected.values()), "phase2.expected-hash-format")
        release_evidence = verify_release(project, release, expected)
        snapshot = release / "snapshot"
        test_project = project / "tests/QualityV3.Foundation/CorePhase2Architecture.Tests/QualityV3.CorePhase2Architecture.Tests.csproj"
        test_source = project / "tests/QualityV3.Foundation/CorePhase2Architecture.Tests/Program.cs"
        test_readme = project / "tests/QualityV3.Foundation/CorePhase2Architecture.Tests/README.md"
        role_consumer_project = project / "tests/QualityV3.Foundation/CorePhase2OutputRoles.Consumer/QualityV3.CorePhase2OutputRoles.Consumer.csproj"
        role_consumer_source = project / "tests/QualityV3.Foundation/CorePhase2OutputRoles.Consumer/Program.cs"
        forbidden_consumer_project = project / "tests/QualityV3.Foundation/CorePhase2OutputRoles.ForbiddenConsumer/QualityV3.CorePhase2OutputRoles.ForbiddenConsumer.csproj"
        forbidden_consumer_source = project / "tests/QualityV3.Foundation/CorePhase2OutputRoles.ForbiddenConsumer/Program.cs"
        runner = project / "tests/QualityV3.Foundation/run_core_phase2_architecture_gate.py"
        artifacts = output / "artifacts"
        build_log = output / "build.log"
        build_command = [
            str(PINNED_DOTNET), "build", str(test_project), "--configuration", "Release",
            "--artifacts-path", str(artifacts), f"-p:Phase2SnapshotRoot={snapshot}",
        ]
        require(run_streamed(build_command, project, build_log) == 0, "phase2.quality-test-build")
        role_consumer_log = output / "output-role-consumer-build.log"
        role_consumer_command = [
            str(PINNED_DOTNET), "build", str(role_consumer_project), "--configuration", "Release",
            "--artifacts-path", str(output / "output-role-consumer-artifacts"), f"-p:Phase2ReleaseRoot={release}",
        ]
        require(run_streamed(role_consumer_command, project, role_consumer_log) == 0, "phase2.output-role-consumer-build")
        forbidden_consumer_log = output / "output-role-forbidden-consumer-build.log"
        forbidden_consumer_command = [
            str(PINNED_DOTNET), "build", str(forbidden_consumer_project), "--configuration", "Release",
            "--artifacts-path", str(output / "output-role-forbidden-consumer-artifacts"), f"-p:Phase2ReleaseRoot={release}",
        ]
        forbidden_exit = run_streamed(forbidden_consumer_command, project, forbidden_consumer_log)
        forbidden_text = forbidden_consumer_log.read_text(encoding="utf-8")
        require(forbidden_exit != 0 and all(token in forbidden_text for token in ("Reference", "Kind", "Path")), "phase2.raw-artifact-surface-not-closed")
        test_dll = artifacts / "bin/QualityV3.CorePhase2Architecture.Tests/release/KnowledgeClipper.Core.V3.Phase2Tests.dll"
        require(test_dll.is_file(), "phase2.build-output-identity")
        host_dll = release / release_evidence["hostAssemblyPath"]
        require(host_dll.is_file(), "phase2.packaged-host-missing")
        test_receipt = output / "architecture-tests.receipt.json"
        test_log = output / "architecture-tests.log"
        work_root = output / "work"
        test_command = [
            str(PINNED_DOTNET), str(test_dll),
            "--snapshot-root", str(snapshot), "--host-dll", str(host_dll),
            "--work-root", str(work_root), "--receipt", str(test_receipt),
        ]
        test_exit = run_streamed(test_command, project, test_log)
        require(test_receipt.is_file(), "phase2.test-receipt-missing")
        test_result = load_json(test_receipt)
        require(test_exit == 0 and test_result["status"] == "PASS", "phase2.architecture-tests-failed")
        tests = test_result["tests"]
        expected_names = {
            "production-host-excludes-authority-bound-fake",
            "acceptance-is-decoupled-from-slow-worker-and-reconnect-observes",
            "artifact-identity-and-replay-side-effects-are-stable",
            "crash-after-session-claim-recovers-terminal-and-session",
            "public-output-roles-and-exactly-one-kind-cardinality",
        }
        require(len(tests) == 5 and {item["name"] for item in tests} == expected_names and all(item["status"] == "PASS" for item in tests), "phase2.architecture-test-set")
        result = {
            "schemaVersion": "quality-v3-core-phase2-architecture-gate.v1",
            "createdAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            "startedAtUtc": started,
            "status": "PASS-PHASE2-ARCHITECTURE",
            "release": release_evidence,
            "qualityTests": {
                "count": 5,
                "receipt": file_ref(project, test_receipt),
                "buildLog": file_ref(project, build_log),
                "runLog": file_ref(project, test_log),
                "files": [file_ref(project, path) for path in (
                    test_project, test_source, test_readme,
                    role_consumer_project, role_consumer_source,
                    forbidden_consumer_project, forbidden_consumer_source,
                    runner,
                )],
            },
            "externalConsumerGates": {
                "typedOutputRoles": {"status": "PASS", "buildLog": file_ref(project, role_consumer_log)},
                "rawArtifactSurface": {"status": "EXPECTED-COMPILE-FAIL", "buildLog": file_ref(project, forbidden_consumer_log)},
            },
            "gates": {item["name"]: {"status": item["status"], "evidence": item["evidence"]} for item in tests},
            "ownerReceiptTrusted": False,
            "externalServicesInvoked": False,
            "candidatePromotion": "NO-GO",
        }
    except Exception as exception:
        failure_candidates = [
            release / "typed-sdk-slice-packet.json", release / "checkpoint.json", release / "inventory.json",
            project / "tests/QualityV3.Foundation/CorePhase2Architecture.Tests/QualityV3.CorePhase2Architecture.Tests.csproj",
            project / "tests/QualityV3.Foundation/CorePhase2Architecture.Tests/Program.cs",
            project / "tests/QualityV3.Foundation/CorePhase2Architecture.Tests/README.md",
            project / "tests/QualityV3.Foundation/CorePhase2OutputRoles.Consumer/QualityV3.CorePhase2OutputRoles.Consumer.csproj",
            project / "tests/QualityV3.Foundation/CorePhase2OutputRoles.Consumer/Program.cs",
            project / "tests/QualityV3.Foundation/CorePhase2OutputRoles.ForbiddenConsumer/QualityV3.CorePhase2OutputRoles.ForbiddenConsumer.csproj",
            project / "tests/QualityV3.Foundation/CorePhase2OutputRoles.ForbiddenConsumer/Program.cs",
            project / "tests/QualityV3.Foundation/run_core_phase2_architecture_gate.py",
            output / "build.log", output / "output-role-consumer-build.log", output / "output-role-forbidden-consumer-build.log",
            output / "architecture-tests.log", output / "architecture-tests.receipt.json",
        ]
        failure_evidence = [file_ref(project, path) for path in failure_candidates if path.is_file()]
        result = {
            "schemaVersion": "quality-v3-core-phase2-architecture-gate.v1",
            "createdAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            "startedAtUtc": started,
            "status": "FAIL-PHASE2-ARCHITECTURE",
            "releaseRoot": release.relative_to(project).as_posix() if release.is_relative_to(project) else str(release),
            "error": f"{type(exception).__name__}: {exception}",
            "evidence": failure_evidence,
            "architectureTests": load_json(output / "architecture-tests.receipt.json") if (output / "architecture-tests.receipt.json").is_file() else None,
            "ownerReceiptTrusted": False,
            "externalServicesInvoked": False,
            "candidatePromotion": "NO-GO",
        }
        formal_receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"status": result["status"], "receipt": formal_receipt.relative_to(project).as_posix(), "receiptSha256": sha256_file(formal_receipt), "error": result["error"]}, indent=2))
        return 1
    formal_receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": result["status"], "receipt": formal_receipt.relative_to(project).as_posix(), "receiptSha256": sha256_file(formal_receipt), "tests": 5, "candidatePromotion": "NO-GO"}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
