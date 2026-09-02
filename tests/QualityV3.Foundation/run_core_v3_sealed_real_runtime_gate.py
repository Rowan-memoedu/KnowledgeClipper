from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import stat
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath


PINNED_DOTNET = Path(r"D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe")


class NotReady(RuntimeError):
    pass


class GateFailure(RuntimeError):
    pass


def require(condition: bool, code: str) -> None:
    if not condition:
        raise GateFailure(code)


def native(path: Path) -> str:
    value = os.path.abspath(str(path))
    if os.name == "nt" and not value.startswith("\\\\?\\"):
        return "\\\\?\\" + value
    return value


def is_file(path: Path) -> bool:
    return os.path.isfile(native(path))


def is_dir(path: Path) -> bool:
    return os.path.isdir(native(path))


def path_stat(path: Path):
    return os.stat(native(path))


def read_text(path: Path) -> str:
    with open(native(path), "r", encoding="utf-8") as stream:
        return stream.read()


def read_bytes(path: Path) -> bytes:
    with open(native(path), "rb") as stream:
        return stream.read()


def walk_files(root: Path):
    prefix = "\\\\?\\" if os.name == "nt" else ""
    for directory, _, files in os.walk(native(root)):
        for name in files:
            value = os.path.join(directory, name)
            yield Path(value[len(prefix):] if prefix and value.startswith(prefix) else value)


def hardlink_copy(source: str, destination: str) -> str:
    try:
        os.link(native(Path(source)), native(Path(destination)))
    except OSError:
        shutil.copy2(native(Path(source)), native(Path(destination)))
    return destination


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with open(native(path), "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def file_ref(project: Path, path: Path) -> dict:
    return {
        "path": path.relative_to(project).as_posix(),
        "bytes": path_stat(path).st_size,
        "sha256": sha256_file(path),
    }


def safe_relative(value: str) -> bool:
    path = PurePosixPath(value)
    return bool(value) and not path.is_absolute() and ".." not in path.parts and "\\" not in value


def tree_hash(entries: list[dict]) -> str:
    rows = [
        f"{item['path']}\t{item['sha256'].upper()}\t{item['bytes']}\n"
        for item in sorted(entries, key=lambda item: item["path"])
    ]
    return hashlib.sha256("".join(rows).encode("utf-8")).hexdigest().upper()


def load_json(path: Path) -> dict:
    return json.loads(read_text(path))


def run_logged(command: list[str], cwd: Path, log_path: Path) -> int:
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


def expected_hash(value: str | None, label: str) -> str:
    if value is None:
        raise NotReady(f"sealed-packet.external-{label}-binding-missing")
    normalized = value.upper()
    require(len(normalized) == 64 and all(character in "0123456789ABCDEF" for character in normalized), f"sealed-packet.{label}-hash-format")
    return normalized


def preflight_release(project: Path, release: Path, args: argparse.Namespace) -> tuple[dict, dict, dict, Path]:
    controls = {
        "checkpoint": release / "checkpoint.json",
        "inventory": release / "inventory.json",
        "packet": release / "typed-sdk-slice-packet.json",
    }
    missing = [path.name for path in controls.values() if not is_file(path)]
    if missing:
        raise NotReady("sealed-packet.root-controls-missing:" + ",".join(sorted(missing)))

    expected = {
        "checkpoint": expected_hash(args.expected_checkpoint_sha256, "checkpoint"),
        "inventory": expected_hash(args.expected_inventory_sha256, "inventory"),
        "packet": expected_hash(args.expected_packet_sha256, "packet"),
        "inventory_tree": expected_hash(args.expected_inventory_tree_sha256, "inventory-tree"),
        "snapshot_tree": expected_hash(args.expected_snapshot_tree_sha256, "snapshot-tree"),
    }
    for name in ("checkpoint", "inventory", "packet"):
        require(sha256_file(controls[name]) == expected[name], f"sealed-packet.{name}-external-binding")

    checkpoint = load_json(controls["checkpoint"])
    inventory = load_json(controls["inventory"])
    packet = load_json(controls["packet"])
    release_relative = release.relative_to(project).as_posix()
    require(checkpoint.get("immutableReleaseRoot") == release_relative, "sealed-packet.checkpoint-root-binding")
    require(packet.get("immutableReleaseRoot") == release_relative, "sealed-packet.packet-root-binding")
    require(checkpoint.get("status") == "PASS-awaiting-independent-quality", "sealed-packet.checkpoint-status")
    require(packet.get("status") == "PASS-awaiting-independent-quality", "sealed-packet.packet-status")
    require(checkpoint.get("inventory", {}).get("sha256", "").upper() == expected["inventory"], "sealed-packet.checkpoint-inventory-binding")
    require(checkpoint.get("typedSdkSlicePacket", {}).get("sha256", "").upper() == expected["packet"], "sealed-packet.checkpoint-packet-binding")
    require(checkpoint.get("inventoryTreeSha256", "").upper() == expected["inventory_tree"], "sealed-packet.checkpoint-tree-binding")
    require(checkpoint.get("sourceAndContractSnapshotTreeSha256", "").upper() == expected["snapshot_tree"], "sealed-packet.checkpoint-snapshot-binding")
    require(packet.get("sourceAndContractSnapshotTreeSha256", "").upper() == expected["snapshot_tree"], "sealed-packet.packet-snapshot-binding")
    require(checkpoint.get("productionRuntimeImplemented") is True, "sealed-packet.real-runtime-not-declared")
    require(checkpoint.get("productionEnabled") is False and checkpoint.get("candidateAssembled") is False, "sealed-packet.promotion-boundary")

    audited: list[dict] = []
    for item in inventory.get("entries", []):
        relative = item.get("path", "")
        require(safe_relative(relative), "sealed-packet.inventory-path")
        path = release / relative
        require(is_file(path), "sealed-packet.inventory-missing:" + relative)
        actual = {"path": relative, "bytes": path_stat(path).st_size, "sha256": sha256_file(path)}
        require(actual["bytes"] == item.get("bytes") and actual["sha256"] == str(item.get("sha256", "")).upper(), "sealed-packet.inventory-drift:" + relative)
        audited.append(actual)
    require(len(audited) > 0 and tree_hash(audited) == expected["inventory_tree"], "sealed-packet.inventory-tree")
    require(str(inventory.get("treeSha256", "")).upper() == expected["inventory_tree"], "sealed-packet.inventory-declared-tree")
    snapshot_entries = [item for item in audited if item["path"].startswith("snapshot/")]
    require(snapshot_entries and tree_hash(snapshot_entries) == expected["snapshot_tree"], "sealed-packet.snapshot-tree")

    actual_files = sorted(path.relative_to(release).as_posix() for path in walk_files(release))
    expected_files = sorted([item["path"] for item in audited] + ["inventory.json", "checkpoint.json"])
    require(actual_files == expected_files, "sealed-packet.uninventoried-or-missing-files")
    require(not any(os.path.islink(native(release / relative)) for relative in actual_files), "sealed-packet.reparse-point")
    if os.name == "nt":
        require(all(path_stat(release / relative).st_file_attributes & stat.FILE_ATTRIBUTE_READONLY for relative in actual_files), "sealed-packet.not-readonly")

    replay_relative = packet.get("selfContainedReplayRoot", "")
    require(safe_relative(replay_relative), "sealed-packet.self-contained-root")
    replay_root = release / replay_relative
    require(is_dir(replay_root), "sealed-packet.self-contained-root-missing")
    required_paths = [
        "core-v3/tests/KnowledgeClipper.Core.V3.FoundationTests/KnowledgeClipper.Core.V3.FoundationTests.csproj",
        "core-v3/tests/KnowledgeClipper.Core.V3.Phase2Tests/KnowledgeClipper.Core.V3.Phase2Tests.csproj",
        "core-v3/src/KnowledgeClipper.Core.V3.Client/DomainClient.cs",
        "core-v3/src/KnowledgeClipper.Core.V3.Host/Program.cs",
        "core-v3/src/KnowledgeClipper.Core.V3.Kernel/Application/KernelApplication.cs",
        "core-v3/src/KnowledgeClipper.Core.V3.Kernel/WorkerAdapter/WorkerProcessRuntime.cs",
        "contracts/core/v3/capabilities/authority-bound-capability-catalog.phase2.ref.json",
        "contracts/core/v2.10/checkpoints/v2.10.6-20260902T041800Z/checkpoint.json",
    ]
    require(all(is_file(replay_root / relative) for relative in required_paths), "sealed-packet.self-contained-required-file")
    replay_files = [path.as_posix() for path in walk_files(replay_root / "releases")]
    require(any(path.endswith("/workers/content/KnowledgeClipper.Content.Worker.exe") for path in replay_files), "sealed-packet.content-worker-companion-tree")
    require(any(path.endswith("/workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe") for path in replay_files), "sealed-packet.anki-worker-companion-tree")

    candidate_files = packet.get("candidateFiles")
    if not isinstance(candidate_files, list) or not candidate_files:
        candidate_files = [packet["publicAssembly"], *packet.get("runtimeDependencies", [])]
    for item in candidate_files:
        relative = item["path"] if isinstance(item, dict) else str(item)
        require(safe_relative(relative), "sealed-packet.candidate-file-path")
        lowered = relative.lower()
        require("testhost" not in lowered and "authority-bound-fake" not in lowered and "fake-runtime" not in lowered, "sealed-packet.candidate-carries-test-host-or-fake-switch")

    inventory_paths = {item["path"] for item in audited}
    packet_refs = [packet.get("publicAssembly"), *packet.get("runtimeDependencies", []), *packet.get("evidence", [])]
    for reference in packet_refs:
        require(isinstance(reference, dict) and reference.get("path") in inventory_paths, "sealed-packet.packet-reference-not-in-inventory")

    host_reference = next((item for item in packet.get("runtimeDependencies", []) if item.get("path", "").endswith("/KnowledgeClipper.Core.V3.Host.dll")), None)
    require(host_reference is not None, "sealed-packet.host-assembly-not-published")
    return checkpoint, packet, inventory, replay_root


def static_hard_gates(replay_root: Path, packet: dict, release: Path) -> dict:
    source_root = replay_root / "core-v3/src"
    sources = {path.relative_to(source_root).as_posix(): read_text(path) for path in walk_files(source_root) if path.suffix == ".cs"}
    declaration_count = sum(text.count("class WorkerExternalStateUnknownException") for text in sources.values())
    require(declaration_count == 1, "hard-gate.unknown-state-channel-not-canonical")
    runtime = sources["KnowledgeClipper.Core.V3.Kernel/WorkerAdapter/WorkerProcessRuntime.cs"]
    kernel = sources["KnowledgeClipper.Core.V3.Kernel/Application/KernelApplication.cs"]
    client = sources["KnowledgeClipper.Core.V3.Client/DomainClient.cs"]
    host = sources["KnowledgeClipper.Core.V3.Host/Program.cs"]
    require(
        "!string.Equals(result.Status, \"completed\"" in runtime and
        "AnkiUnknownWriteAttempts" in runtime and
        "WorkerExternalStateUnknownException" in runtime and
        "worker_result_noncompleted_after_dispatch" in runtime,
        "hard-gate.worker-unknown-state-mapping",
    )
    typed_catch = kernel.find("catch (WorkerExternalStateUnknownException")
    generic_catch = kernel.find("catch (Exception ex)", typed_catch + 1)
    require(typed_catch >= 0 and generic_catch > typed_catch and "operation.unknown-external-state" in kernel[typed_catch:generic_catch], "hard-gate.kernel-generic-catch-downgrade")
    require("record OperationSubmission" in client and "record QuestionOperationSubmission" in client, "hard-gate.sdk-submission-types")
    submission_region = client[client.find("record OperationSubmission"):client.find("record TaskProjectionSnapshot")]
    require("Artifacts" not in submission_region, "hard-gate.sdk-generic-submission-artifacts")
    for token in ("ReadyForRequestedAction", "OverallState", "NonBlockingIssues", "BlockingIssues"):
        require(token in client, "hard-gate.sdk-scoped-readiness:" + token)
    projection_region = client[client.find("GetOperationProjectionCoreAsync"):client.find("WaitOperationProjectionAsync")]
    require(".Single(" not in projection_region and "SequenceEqual(" not in projection_region, "hard-gate.sdk-order-assumption")
    require("arguments.ContainsKey(\"runtime-mode\")" in host and "WorkerRuntimeProfile.Production" in host and "AuthorityBoundFakeWorkerRuntime" not in host, "hard-gate.production-host-fake-reachability")

    host_reference = next(item for item in packet["runtimeDependencies"] if item["path"].endswith("/KnowledgeClipper.Core.V3.Host.dll"))
    host_bytes = read_bytes(release / host_reference["path"])
    require(b"authority-bound-fake-test-only" not in host_bytes, "hard-gate.published-host-contains-fake-switch")
    return {
        "canonicalUnknownStateDeclarationCount": declaration_count,
        "kernelTypedCatchBeforeGeneric": True,
        "submissionArtifactsAbsent": True,
        "scopedReadinessSurface": True,
        "projectionOrderAssumptionAbsent": True,
        "publishedHostFakeSwitchAbsent": True,
    }


def require_test_receipt(path: Path, expected_count: int, expected_names: set[str] | None = None) -> dict:
    require(is_file(path), "tests.receipt-missing:" + path.name)
    receipt = load_json(path)
    tests = receipt.get("tests", [])
    require(receipt.get("status") == "PASS" and len(tests) == expected_count and all(item.get("status") == "PASS" for item in tests), f"tests.not-{expected_count}-of-{expected_count}")
    if expected_names is not None:
        require({item.get("name") for item in tests} == expected_names, "tests.unexpected-test-set")
    return receipt


def run_formal(project: Path, release: Path, replay_root: Path, packet: dict, output: Path, work_root: Path) -> dict:
    require(PINNED_DOTNET.is_file(), "formal.pinned-sdk-missing")
    require(not work_root.exists(), "formal.work-root-not-append-only")
    work_root.mkdir(parents=True)
    isolated = work_root / "isolated-packet-repository"
    shutil.copytree(native(replay_root), native(isolated), copy_function=hardlink_copy)
    logs = output / "logs"
    receipts = output / "test-receipts"
    phase2_project = isolated / "core-v3/tests/KnowledgeClipper.Core.V3.Phase2Tests/KnowledgeClipper.Core.V3.Phase2Tests.csproj"
    foundation_project = isolated / "core-v3/tests/KnowledgeClipper.Core.V3.FoundationTests/KnowledgeClipper.Core.V3.FoundationTests.csproj"

    for label, test_project in (("phase2", phase2_project), ("foundation", foundation_project)):
        command = [str(PINNED_DOTNET), "build", str(test_project), "-c", "Release", "--nologo", "--ignore-failed-sources"]
        require(run_logged(command, isolated, logs / f"{label}-build.log") == 0, f"formal.{label}-build")

    phase2_dll = isolated / "core-v3/tests/KnowledgeClipper.Core.V3.Phase2Tests/bin/Release/net8.0/KnowledgeClipper.Core.V3.Phase2Tests.dll"
    foundation_dll = isolated / "core-v3/tests/KnowledgeClipper.Core.V3.FoundationTests/bin/Release/net8.0/KnowledgeClipper.Core.V3.FoundationTests.dll"
    require(is_file(phase2_dll) and is_file(foundation_dll), "formal.test-assembly-missing")
    receipts.mkdir(parents=True)
    phase2_receipt = receipts / "phase2.receipt.json"
    foundation_receipt = receipts / "foundation.receipt.json"
    require(run_logged([str(PINNED_DOTNET), str(phase2_dll), "--receipt", str(phase2_receipt)], isolated, logs / "phase2-run.log") == 0, "formal.phase2-run")
    require(run_logged([str(PINNED_DOTNET), str(foundation_dll), "--receipt", str(foundation_receipt)], isolated, logs / "foundation-run.log") == 0, "formal.foundation-run")
    phase2_names = {
        "authority-bound-fake-question-anki-vertical-slice",
        "sdk-projection-uses-named-domain-artifacts",
        "production-host-rejects-fake-runtime-switch",
        "slow-worker-fast-accept-and-reconnect-observation",
        "question-session-crash-recovery",
        "authority-entrypoint-hash-drift-rejected",
        "authority-exact-worker-version-drift-rejected",
        "authority-contract-version-drift-rejected",
        "worker-process-runtime-offline-contract",
        "worker-process-unknown-state-is-distinct",
    }
    require_test_receipt(phase2_receipt, 10, phase2_names)
    require_test_receipt(foundation_receipt, 30)

    client_reference = packet["publicAssembly"]
    client_dll = release / client_reference["path"]
    positive_project = project / "tests/QualityV3.Foundation/CoreRealRuntimeSdk.Consumer/QualityV3.CoreRealRuntimeSdk.Consumer.csproj"
    negative_project = project / "tests/QualityV3.Foundation/CoreRealRuntimeSdk.ForbiddenConsumer/QualityV3.CoreRealRuntimeSdk.ForbiddenConsumer.csproj"
    consumer_artifacts = work_root / "sdk-consumer-artifacts"
    property_arg = f"-p:RealRuntimeClientAssembly={client_dll}"
    require(run_logged([str(PINNED_DOTNET), "build", str(positive_project), "-c", "Release", "--nologo", "--artifacts-path", str(consumer_artifacts), property_arg], project, logs / "sdk-positive-build.log") == 0, "formal.sdk-positive-build")
    consumer_dll = consumer_artifacts / "bin/QualityV3.CoreRealRuntimeSdk.Consumer/release/QualityV3.CoreRealRuntimeSdk.Consumer.dll"
    require(is_file(consumer_dll), "formal.sdk-positive-assembly")
    require(run_logged([str(PINNED_DOTNET), str(consumer_dll)], project, logs / "sdk-positive-run.log") == 0, "formal.sdk-positive-run")
    negative_exit = run_logged([str(PINNED_DOTNET), "build", str(negative_project), "-c", "Release", "--nologo", "--artifacts-path", str(work_root / "sdk-negative-artifacts"), property_arg], project, logs / "sdk-negative-build.log")
    negative_text = read_text(logs / "sdk-negative-build.log")
    require(negative_exit != 0 and all(token in negative_text for token in ("Reference", "Kind", "Path", "Artifacts")), "formal.sdk-negative-boundary")

    host_reference = next(item for item in packet["runtimeDependencies"] if item["path"].endswith("/KnowledgeClipper.Core.V3.Host.dll"))
    host_dll = release / host_reference["path"]
    host_command = [
        str(PINNED_DOTNET), str(host_dll),
        "--repository-root", str(isolated),
        "--binding", str(isolated / "contracts/core/v3/capabilities/authority-bound-capability-catalog.phase2.ref.json"),
        "--data-root", str(output / "production-host-reject-data"),
        "--pipe-name", "KnowledgeClipper.Core.V3.QualityRejectFake",
        "--runtime-mode", "authority-bound-fake-test-only",
    ]
    host_log = logs / "production-host-reject-fake.log"
    host_exit = run_logged(host_command, isolated, host_log)
    host_text = read_text(host_log)
    require(host_exit != 0 and "CORE_V3_READY" not in host_text and "does not accept test worker runtime switches" in host_text, "formal.production-host-accepted-fake")

    return {
        "foundation": {"status": "PASS", "count": 30, "receipt": file_ref(project, foundation_receipt)},
        "phase2": {"status": "PASS", "count": 10, "receipt": file_ref(project, phase2_receipt)},
        "sdkPositiveConsumer": "PASS",
        "sdkForbiddenConsumer": "EXPECTED-COMPILE-FAIL",
        "productionHostRejectsFake": "PASS",
        "realExternalServicesInvoked": False,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Independent gate for a sealed, append-only, self-contained Core v3 real-runtime packet")
    parser.add_argument("--project-root", type=Path, required=True)
    parser.add_argument("--release-root", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    parser.add_argument("--expected-checkpoint-sha256")
    parser.add_argument("--expected-inventory-sha256")
    parser.add_argument("--expected-packet-sha256")
    parser.add_argument("--expected-inventory-tree-sha256")
    parser.add_argument("--expected-snapshot-tree-sha256")
    parser.add_argument("--work-root", type=Path)
    parser.add_argument("--preflight-static-only", action="store_true")
    args = parser.parse_args()
    project = args.project_root.resolve()
    release = args.release_root.resolve()
    output = args.output_root.resolve()
    work_root = args.work_root.resolve() if args.work_root else output / "work"
    allowed = (project / "canary/quality-v3-core-real-runtime-sealed-packet").resolve()
    require(output.is_relative_to(allowed) and not output.exists(), "output.not-append-only")
    output.mkdir(parents=True)
    receipt_path = output / "quality-core-real-runtime-sealed-packet.receipt.json"
    started = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    structural_audit = {
        "qualificationScript": "core-v3/tools/Invoke-Phase2SliceQualification.ps1",
        "rootCause": "The final release directory is created and populated before fallible external replay tests. Packet, inventory, checkpoint, external checkpoint reference, and read-only sealing occur only at the end. Any intervening exception leaves an unsealed half-directory with no explicit INCOMPLETE marker or atomic staging-to-release publication.",
        "halfProductsAreReleases": False,
    }
    try:
        require(release.is_relative_to((project / "core-v3/releases").resolve()), "release.outside-release-root")
        checkpoint, packet, inventory, replay_root = preflight_release(project, release, args)
        static_evidence = static_hard_gates(replay_root, packet, release)
        formal = {"executed": False} if args.preflight_static_only else run_formal(project, release, replay_root, packet, output, work_root)
        result = {
            "schemaVersion": "quality-v3-core-real-runtime-sealed-packet.v1",
            "createdAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            "startedAtUtc": started,
            "status": "PASS-PREFLIGHT-STATIC" if args.preflight_static_only else "PASS-INDEPENDENT-QUALITY",
            "resolvedPaths": {"project": str(project), "release": str(release), "selfContainedRepository": str(replay_root)},
            "qualityRunner": file_ref(project, Path(__file__).resolve()),
            "releaseRoot": release.relative_to(project).as_posix(),
            "controlFiles": {
                "checkpoint": file_ref(project, release / "checkpoint.json"),
                "inventory": file_ref(project, release / "inventory.json"),
                "packet": file_ref(project, release / "typed-sdk-slice-packet.json"),
                "inventoryTreeSha256": inventory["treeSha256"],
            },
            "staticHardGates": static_evidence,
            "formalRuns": formal,
            "structuralAudit": structural_audit,
            "ownerReceiptTrusted": False,
            "candidatePromotion": "NO-GO-UNTIL-SEPARATE-CANDIDATE-GATE",
        }
        exit_code = 0
    except NotReady as exception:
        result = {
            "schemaVersion": "quality-v3-core-real-runtime-sealed-packet.v1",
            "createdAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            "startedAtUtc": started,
            "status": "NOT_READY",
            "releaseRoot": release.relative_to(project).as_posix() if release.is_relative_to(project) else str(release),
            "reason": str(exception),
            "formalExecution": {"executed": False},
            "structuralAudit": structural_audit,
            "ownerReceiptTrusted": False,
            "externalServicesInvoked": False,
            "candidatePromotion": "NO-GO",
        }
        exit_code = 2
    except Exception as exception:
        result = {
            "schemaVersion": "quality-v3-core-real-runtime-sealed-packet.v1",
            "createdAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            "startedAtUtc": started,
            "status": "FAIL-INDEPENDENT-QUALITY",
            "releaseRoot": release.relative_to(project).as_posix() if release.is_relative_to(project) else str(release),
            "reason": f"{type(exception).__name__}: {exception}",
            "structuralAudit": structural_audit,
            "ownerReceiptTrusted": False,
            "externalServicesInvoked": False,
            "candidatePromotion": "NO-GO",
        }
        exit_code = 1
    receipt_path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "status": result["status"],
        "receipt": receipt_path.relative_to(project).as_posix(),
        "receiptSha256": sha256_file(receipt_path),
        "reason": result.get("reason"),
    }, ensure_ascii=False, indent=2))
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
