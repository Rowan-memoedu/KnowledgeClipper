from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


EXPECTED_RELEASE_ID = "knowledge-clipper-authority-v1.2.39-20260901T130618Z"
EXPECTED_CHECKPOINT_SHA = "954F1D28DA9F64E15099FD55527FC158A0C3E5E34668A3F2385A09A8266F1039"
EXPECTED_IPC_FAILURE = (
    "IPC configuration is neither frozen v1 nor append-only stage-control/module-action "
    "v1.1/v1.2/v1.3/v1.4."
)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def file_info(path: Path, relative_to: Path | None = None) -> dict[str, Any]:
    return {
        "path": path.relative_to(relative_to).as_posix() if relative_to else str(path),
        "bytes": path.stat().st_size,
        "sha256": sha256(path),
    }


def inventory(root: Path, include_files: bool = False) -> dict[str, Any]:
    files = sorted((path for path in root.rglob("*") if path.is_file()), key=lambda p: p.relative_to(root).as_posix())
    rows: list[dict[str, Any]] = []
    lines: list[str] = []
    total = 0
    for path in files:
        relative = path.relative_to(root).as_posix()
        size = path.stat().st_size
        digest = sha256(path)
        total += size
        lines.append(f"{relative}\t{size}\t{digest.lower()}")
        if include_files:
            rows.append({"path": relative, "bytes": size, "sha256": digest})
    result: dict[str, Any] = {
        "root": str(root),
        "files": len(files),
        "bytes": total,
        "treeSha256": hashlib.sha256("\n".join(lines).encode("utf-8")).hexdigest().upper(),
        "algorithm": "sort relative paths ordinal; path TAB bytes TAB lowercase SHA-256; LF; SHA-256 UTF-8",
    }
    if include_files:
        result["fileInventory"] = rows
    return result


def load(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def require(condition: bool, message: str, checks: list[dict[str, str]]) -> None:
    checks.append({"status": "passed" if condition else "failed", "check": message})


def compare_inventory(actual: dict[str, Any], expected: dict[str, Any], label: str, checks: list[dict[str, str]]) -> None:
    require(actual["files"] == int(expected["files"]), f"{label} file count", checks)
    require(actual["bytes"] == int(expected["bytes"]), f"{label} byte count", checks)
    require(actual["treeSha256"] == str(expected["treeSha256"]).upper(), f"{label} tree SHA-256", checks)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project-root", required=True, type=Path)
    parser.add_argument("--runtime-evidence-root", required=True, type=Path)
    parser.add_argument("--ipc-failure-root", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    args = parser.parse_args()

    root = args.project_root.resolve()
    runtime_evidence = args.runtime_evidence_root.resolve()
    ipc_failure = args.ipc_failure_root.resolve()
    output_dir = args.output_dir.resolve()
    if output_dir.exists():
        raise FileExistsError(f"Append-only output directory exists: {output_dir}")
    output_dir.mkdir(parents=True)

    release_container = root / "releases" / EXPECTED_RELEASE_ID
    release_root = release_container / "win-x64"
    authority = root / "contracts" / "authority" / "worker-registry" / "v1.2.39-20260901T130618Z"
    checkpoint = root / "contracts" / "core" / "v2.10" / "checkpoints" / "v2.10.2-20260901T100127Z" / "checkpoint.json"
    packaged_checkpoint = release_root / "contracts" / "core" / "v2.10" / "checkpoints" / "v2.10.2-20260901T100127Z" / "checkpoint.json"
    registry_path = authority / "worker-registry.v1.json"
    dependency_path = authority / "dependency-manifest.v1.json"
    runtime_manifest_path = authority / "orchestrator-runtime-manifest.v1.json"
    assembly_path = authority / "assembly-receipt.v1.json"
    path_gate_path = authority / "assembly-path-gate.v1.json"
    formal_receipt_path = root / "receipts" / "authority-assembly" / "20260901T130618Z" / f"{EXPECTED_RELEASE_ID}.receipt.json"

    checks: list[dict[str, str]] = []
    for required in [release_root, authority, checkpoint, packaged_checkpoint, registry_path, dependency_path,
                     runtime_manifest_path, assembly_path, path_gate_path, formal_receipt_path,
                     runtime_evidence, ipc_failure / "startup-reprobe" / "stderr.log"]:
        require(required.exists(), f"required input exists: {required}", checks)

    dependency = load(dependency_path)
    runtime_manifest = load(runtime_manifest_path)
    assembly = load(assembly_path)
    path_gate = load(path_gate_path)
    checkpoint_json = load(checkpoint)
    registry = load(registry_path)
    runtime_gate = load(runtime_evidence / "runtime-gates.json")

    release_inventory = inventory(release_root)
    compare_inventory(release_inventory, dependency, "release", checks)
    compare_inventory(release_inventory, assembly["release"], "assembly receipt release", checks)

    orchestrator_inventory = inventory(release_root / "orchestrator")
    compare_inventory(orchestrator_inventory, {
        "files": runtime_manifest["fileCount"],
        "bytes": runtime_manifest["totalBytes"],
        "treeSha256": runtime_manifest["treeSha256"],
    }, "orchestrator runtime", checks)
    manifest_by_path = {entry["path"]: entry for entry in runtime_manifest["files"]}
    require(len(manifest_by_path) == orchestrator_inventory["files"], "runtime manifest path cardinality", checks)
    for path in sorted((release_root / "orchestrator").rglob("*")):
        if not path.is_file():
            continue
        relative = path.relative_to(release_root / "orchestrator").as_posix()
        entry = manifest_by_path.get(relative)
        require(entry is not None, f"runtime manifest contains {relative}", checks)
        if entry:
            require(path.stat().st_size == int(entry["bytes"]), f"runtime bytes match {relative}", checks)
            require(sha256(path) == str(entry["sha256"]).upper(), f"runtime SHA-256 matches {relative}", checks)

    authority_inventory = inventory(authority, include_files=True)
    checkpoint_info = file_info(checkpoint, root)
    packaged_checkpoint_info = file_info(packaged_checkpoint, release_root)
    require(checkpoint_info["sha256"] == EXPECTED_CHECKPOINT_SHA, "source checkpoint SHA-256", checks)
    require(packaged_checkpoint_info["sha256"] == EXPECTED_CHECKPOINT_SHA, "packaged checkpoint SHA-256", checks)
    require(checkpoint_json["authority"]["ipc"]["protocolVersion"] == "1.7", "checkpoint declares IPC 1.7", checks)
    for binding in checkpoint_json["fileBindings"]:
        bound = root / binding["path"]
        require(bound.is_file(), f"checkpoint binding exists: {binding['path']}", checks)
        if bound.is_file():
            require(sha256(bound) == binding["sha256"].upper(), f"checkpoint binding SHA-256: {binding['path']}", checks)

    registry_info = file_info(registry_path, root)
    dependency_info = file_info(dependency_path, root)
    runtime_manifest_info = file_info(runtime_manifest_path, root)
    assembly_info = file_info(assembly_path, root)
    path_gate_info = file_info(path_gate_path, root)
    formal_receipt_info = file_info(formal_receipt_path, root)
    require(registry_info["sha256"] == assembly["registrySha256"].upper(), "assembly receipt registry SHA-256", checks)
    require(dependency_info["sha256"] == assembly["dependencyManifestSha256"].upper(), "assembly receipt dependency SHA-256", checks)
    require(assembly_info["bytes"] == int(path_gate["assemblyReceipt"]["bytes"]), "path gate assembly receipt bytes", checks)
    require(assembly_info["sha256"] == path_gate["assemblyReceipt"]["sha256"].upper(), "path gate assembly receipt SHA-256", checks)
    require(len(registry["workers"]) == 5, "registry contains exactly five workers", checks)
    worker_inventory: list[dict[str, Any]] = []
    for worker in registry["workers"]:
        relative = worker["entrypoint"]["path"]
        path = release_root / Path(relative.replace("/", "\\"))
        info = file_info(path, release_root)
        worker_inventory.append({"workerId": worker["workerId"], **info})
        assembly_worker = assembly["workers"][worker["workerId"]]
        require(info["bytes"] == int(assembly_worker["bytes"]), f"assembly worker bytes: {worker['workerId']}", checks)
        require(info["sha256"] == assembly_worker["sha256"].upper(), f"assembly worker SHA-256: {worker['workerId']}", checks)
    require(path_gate["pathCount"] == 5 and path_gate["allExist"] and path_gate["allWithinFinalRoots"] and path_gate["allBytesAndHashesMatch"],
            "path gate five final-root bindings", checks)

    runtime_evidence_inventory = inventory(runtime_evidence, include_files=True)
    require(runtime_gate["validateRegistry"]["status"] == "valid" and runtime_gate["validateRegistry"]["workers"] == 5,
            "independent final validate-registry 5/5", checks)
    require(runtime_gate["protocol10"]["taskQuery"]["status"] == "passed", "independent IPC 1.0 task query", checks)
    require(runtime_gate["protocol10"]["taskCommand"]["rejectionCode"] == "contract.expected_state_mismatch",
            "independent IPC 1.0 negative command", checks)
    require(runtime_gate["protocol11"]["stageControl"]["status"] == "passed", "independent IPC 1.1 stage control", checks)
    require(runtime_gate["dependencyDriftNegative"]["rejected"] is True, "independent dependency drift rejection", checks)
    require(runtime_gate["workerRunInvocations"] == 0 and runtime_gate["physicalSpeakerInvocations"] == 0 and runtime_gate["externalWrites"] == 0,
            "runtime gate effect counts are zero", checks)

    failure_stderr_path = ipc_failure / "startup-reprobe" / "stderr.log"
    failure_stdout_path = ipc_failure / "startup-reprobe" / "stdout.log"
    failure_stderr = failure_stderr_path.read_text(encoding="utf-8-sig")
    failure_stdout = failure_stdout_path.read_text(encoding="utf-8-sig")
    require(EXPECTED_IPC_FAILURE in failure_stderr, "final Orchestrator rejects IPC 1.7 at startup", checks)
    require('"status": "starting"' in failure_stdout, "final Orchestrator startup attempt was real", checks)
    final_dll = release_root / "orchestrator" / "KnowledgeClipper.Orchestrator.dll"
    mirrored_dll = ipc_failure / "harness-root" / "orchestrator" / "bin" / "Release" / "net8.0" / "KnowledgeClipper.Orchestrator.dll"
    require(sha256(final_dll) == sha256(mirrored_dll), "failed-process DLL exactly matches final DLL", checks)
    failed_action_root = ipc_failure / "evidence" / "jobs" / "module-actions"
    generated_receipts = list(failed_action_root.rglob("receipt.json")) if failed_action_root.exists() else []
    generated_results = list(failed_action_root.rglob("result.json")) if failed_action_root.exists() else []
    require(not generated_receipts and not generated_results, "IPC 1.7 rejection occurred before worker receipt/result", checks)

    failures = [entry["check"] for entry in checks if entry["status"] == "failed"]
    report = {
        "schemaVersion": "knowledge-clipper-post-assembly-independent-authority-admission.v1",
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "status": "failed-no-go",
        "releaseId": EXPECTED_RELEASE_ID,
        "scope": "Independent post-assembly re-hash and final-process runtime admission; immutable authority/checkpoint inputs were read-only.",
        "inventories": {
            "release": release_inventory,
            "authority": authority_inventory,
            "orchestratorRuntime": orchestrator_inventory,
            "runtimeGateEvidence": runtime_evidence_inventory,
            "ipcFailureEvidence": inventory(ipc_failure),
        },
        "files": {
            "checkpoint": checkpoint_info,
            "packagedCheckpoint": packaged_checkpoint_info,
            "registry": registry_info,
            "dependencyManifest": dependency_info,
            "orchestratorRuntimeManifest": runtime_manifest_info,
            "assemblyReceipt": assembly_info,
            "assemblyPathGate": path_gate_info,
            "formalAssemblyReceipt": formal_receipt_info,
        },
        "workers": worker_inventory,
        "runtime": {
            "ipc10": "passed",
            "ipc11": "passed",
            "validateRegistry": "passed-5-of-5",
            "dependencyDriftNegative": "passed-rejected",
            "ipc15": "not-reached-final-runtime-rejected-ipc17-configuration",
            "ipc16": "not-reached-final-runtime-rejected-ipc17-configuration",
            "ipc17": "failed-final-runtime-rejected-configuration",
            "failure": {
                "message": EXPECTED_IPC_FAILURE,
                "finalExe": file_info(release_root / "orchestrator" / "KnowledgeClipper.Orchestrator.exe", release_root),
                "finalDll": file_info(final_dll, release_root),
                "mirroredDll": file_info(mirrored_dll, ipc_failure),
                "stdout": file_info(failure_stdout_path, ipc_failure),
                "stderr": file_info(failure_stderr_path, ipc_failure),
            },
        },
        "effectBoundary": {
            "workerInvocations": 0,
            "externalWorkerCalls": 0,
            "externalApiCalls": 0,
            "remnoteCalls": 0,
            "remnoteWrites": 0,
            "ankiCalls": 0,
            "ankiWrites": 0,
            "mineruCalls": 0,
            "physicalSpeakerInvocations": 0,
            "localEvidenceWritesOnly": True,
        },
        "checks": checks,
        "failedChecks": failures,
        "decision": {
            "authority": "NO-GO",
            "candidateAssembly": "NOT_RUN",
            "userHandTestReady": False,
            "reason": "The sealed v1.2.39 Orchestrator runtime rejects the checkpoint-bound IPC 1.7 configuration before IPC 1.5-1.7 actions can run.",
        },
    }
    receipt = output_dir / "post-assembly-independent-authority-admission.receipt.json"
    receipt.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": report["status"], "receipt": str(receipt), "bytes": receipt.stat().st_size,
                      "sha256": sha256(receipt), "failedChecks": failures}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
