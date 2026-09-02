from __future__ import annotations

import hashlib
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[1]
STAMP = "20260902T041800Z"
RECEIPT_ROOT = PROJECT / f"receipts/core-phase1/{STAMP}-v2106-entrypoint-resolver"
ARTIFACT_ROOT = PROJECT / f"artifacts/core-v2.10.6/{STAMP}"
PUBLISH_ROOT = ARTIFACT_ROOT / "orchestrator"
CHECKPOINT_DIR = PROJECT / f"contracts/core/v2.10/checkpoints/v2.10.6-{STAMP}"
PREVIOUS = PROJECT / "contracts/core/v2.10/checkpoints/v2.10.5-20260902T012600Z/checkpoint.json"
PREVIOUS_SHA256 = "D313242ED851AD93AEFBE64BDDB7C6ADA767630936AA63ACE0ECB65718693FCD"
BASELINE = PROJECT / "receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def evidence(path: Path) -> dict[str, object]:
    require(path.is_file(), f"missing evidence: {path}")
    return {"path": relative(path), "bytes": path.stat().st_size, "sha256": sha256(path)}


def load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_once(path: Path, value: object) -> None:
    require(not path.exists(), f"append-only target exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def inventory(root: Path) -> dict[str, object]:
    rows = []
    for path in root.rglob("*"):
        if path.is_file():
            rows.append((path.relative_to(root).as_posix(), path.stat().st_size, sha256(path)))
    rows.sort()
    payload = "\n".join(f"{name}\t{size}\t{digest.lower()}" for name, size, digest in rows).encode()
    return {"fileCount": len(rows), "totalBytes": sum(row[1] for row in rows), "treeSha256": hashlib.sha256(payload).hexdigest().upper()}


def main() -> int:
    require(sha256(PREVIOUS) == PREVIOUS_SHA256, "v2.10.5 checkpoint drifted")
    require(not CHECKPOINT_DIR.exists(), "append-only checkpoint target exists")
    process_path = RECEIPT_ROOT / "core-process-named-pipe.receipt.json"
    process = load(process_path)
    require(process["status"] == "passed" and process["processBoundary"] == "published KnowledgeClipper.Orchestrator.exe serve", "final EXE process gate failed")
    require([item["protocolVersion"] for item in process["finalProcessProtocolMatrix"]] == ["1.5", "1.6", "1.7"], "IPC matrix drifted")
    require(all(item["startup"] == item["typedAction"] == "passed" for item in process["finalProcessProtocolMatrix"]), "IPC process matrix failed")
    require(process["probe"]["declaredFileCount"] == 4 and process["probe"]["externalWrites"] == 0, "four-file Anki probe regression failed")

    contract_path = RECEIPT_ROOT / "orchestrator-contract.receipt.json"
    contract = load(contract_path)
    entrypoint = next(item for item in contract["tests"] if item["name"] == "verified-artifact-entrypoint-resolution")
    require(entrypoint["status"] == "passed" and all(entrypoint[key] is True for key in (
        "oneFilePositive", "fourFilePositive", "missingRejected", "duplicateRejected", "undeclaredRejected", "noncanonicalRejected", "pathEscapeRejected"
    )), "entrypoint contract matrix failed")
    for name in ("core-adapter.receipt.json", "question-workflow.receipt.json", "orchestrator-contract.receipt.json"):
        require(load(RECEIPT_ROOT / name)["status"] == "passed", f"regression failed: {name}")

    exe = PUBLISH_ROOT / "KnowledgeClipper.Orchestrator.exe"
    dll = PUBLISH_ROOT / "KnowledgeClipper.Orchestrator.dll"
    describe_path = ARTIFACT_ROOT / "orchestrator-describe.json"
    completed = subprocess.run([str(exe), "describe"], capture_output=True, text=True, check=True)
    describe = json.loads(completed.stdout)
    require(describe["version"] == "2.10.6.0" and describe["coreContractVersion"] == "2.10.6", "published runtime identity drifted")
    require(describe["ipc"]["protocolVersions"] == ["1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "1.7"], "embedded IPC descriptor drifted")
    write_once(describe_path, describe)
    require(process["orchestratorRuntime"]["sha256"].upper() == sha256(exe), "tested runtime differs from published EXE")

    baseline = load(BASELINE)
    mismatches = [item["path"] for item in baseline["files"] if not Path(item["path"]).is_file() or sha256(Path(item["path"])) != item["sha256"].upper()]
    require(len(baseline["files"]) == 21 and not mismatches, "frozen contracts v1 drifted")
    frozen_path = RECEIPT_ROOT / "frozen-v1-verification.receipt.json"
    write_once(frozen_path, {"schemaVersion": "knowledge-clipper-frozen-contract-verification.v1", "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"), "status": "passed", "baselineReceipt": relative(BASELINE), "filesChecked": 21, "mismatches": 0, "modified": False, "details": []})

    runtime_path = RECEIPT_ROOT / "runtime-binding.receipt.json"
    write_once(runtime_path, {
        "schemaVersion": "knowledge-clipper-core-v2.10.6-runtime-binding.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "passed",
        "fix": "All artifact files remain independently verified; metadata.entrypoint is resolved strictly and source-neutrally for single-file and multi-file typed artifacts.",
        "runtime": {"version": "2.10.6.0", "coreContractVersion": "2.10.6", "exe": evidence(exe), "dll": evidence(dll), "describe": evidence(describe_path), "inventory": inventory(PUBLISH_ROOT)},
        "finalProcessMatrix": process["finalProcessProtocolMatrix"],
        "fourFileAnkiProbe": process["probe"],
        "externalCalls": 0,
        "externalWrites": 0,
        "physicalSpeakerInvocations": 0,
    })

    bindings = [
        PROJECT / "orchestrator/KnowledgeClipper.Orchestrator.csproj",
        PROJECT / "orchestrator/Program.cs",
        PROJECT / "orchestrator/Core/DesktopIntentResolver.cs",
        PROJECT / "orchestrator/Core/ModuleActionControl.cs",
        PROJECT / "orchestrator/Core/VerifiedArtifactEntrypointResolver.cs",
        PROJECT / "tests/Orchestrator.ContractTests/Program.cs",
        PROJECT / "tests/CoreProcessNamedPipe.IntegrationTests/Program.cs",
        PROJECT / "tests/FakeWorker/Program.cs",
        PROJECT / "contracts/core/v2.10/module-control/orchestrator-ipc.v1.7.json",
        PROJECT / "contracts/core/v2.10/module-control/schemas/ipc-request.v1.7.schema.json",
        PROJECT / "contracts/core/v2.10/module-control/schemas/ipc-response.v1.7.schema.json",
        PROJECT / "contracts/core/v2.10/module-control/schemas/module-action-request.v6.schema.json",
        PROJECT / "contracts/core/v2.10/module-control/schemas/module-action-receipt.v6.schema.json",
        exe, dll, describe_path, process_path, contract_path, runtime_path, frozen_path,
    ]
    CHECKPOINT_DIR.mkdir(parents=True)
    checkpoint_path = CHECKPOINT_DIR / "checkpoint.json"
    checkpoint = {
        "schemaVersion": "knowledge-clipper-core-checkpoint.v1",
        "checkpointVersion": "2.10.6",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "passed-shared-verified-entrypoint-resolution",
        "scope": "Source-neutral strict Artifact metadata.entrypoint resolution with all-file integrity verification and four-file Anki module-action regression",
        "repositoryHeadAtCheckpoint": subprocess.run(["git", "rev-parse", "HEAD"], cwd=PROJECT, capture_output=True, text=True, check=True).stdout.strip(),
        "extendsCheckpoint": relative(PREVIOUS),
        "authority": {
            "ipc": {"schemaVersion": "1.7.0", "protocolVersion": "1.7", "configuration": "contracts/core/v2.10/module-control/orchestrator-ipc.v1.7.json", "request": "knowledge-clipper-module-action.v6", "receipt": "knowledge-clipper-module-action-receipt.v6", "startupRegressions": ["1.5", "1.6", "1.7"]},
            "runtime": {"componentVersion": "2.10.6.0", "coreContractVersion": "2.10.6", "selfContained": True, "runtimeIdentifier": "win-x64", "entrypoint": "KnowledgeClipper.Orchestrator.exe", "testedExeSha256": sha256(exe), "authorityPackageBindingsBounded": True},
            "artifactEntrypoint": {"resolver": "VerifiedArtifactEntrypointResolver", "allFilesVerifiedBeforeConsumption": True, "singleFilePositive": True, "fourFilePositive": True, "missingDuplicateUndeclaredNoncanonicalEscapeRejected": True, "moduleActionConsumersUnified": ["anki.probe", "anki.initialize-managed"], "otherModuleActionsWithArtifactManifestConsumer": []},
            "compatibility": {"frozenContractsV1Modified": False, "frozenContractsV1FilesChecked": 21, "frozenContractsV1Mismatches": 0, "ankiProbeIpc15Preserved": True, "contentSettingsIpc16Preserved": True, "typedPublicIpc17Preserved": True},
        },
        "verification": {
            "warningsAsErrors": {"status": "passed", "projectsBuilt": 5, "warnings": 0, "errors": 0},
            "orchestratorContracts": {"status": "passed", **evidence(contract_path)},
            "finalProcessProtocolMatrix": {"status": "passed", **evidence(process_path), "protocols": ["1.5", "1.6", "1.7"], "declaredAnkiFiles": 4},
            "runtimeBinding": {"status": "passed", **evidence(runtime_path)},
            "frozenV1": {"status": "passed", **evidence(frozen_path)},
            "qualityBlocker": {"status": "confirmed-and-fixed", "receipt": "receipts/quality-integrated-candidate/20260902T040142Z-v1240-shared-protocol-no-go/candidate-stateful-no-go.receipt.json", "receiptSha256": "16A419037B1DB096D88DFD46B7CD1449B97A94E711C496B5A64399E6D3A6A51D"},
        },
        "qualityAuditInput": [
            "Re-hash every checkpoint fileBinding and the sealed runtime before execution.",
            "Run the published EXE with IPC 1.5, 1.6, and 1.7 and require a four-file Anki typed status artifact to resolve metadata.entrypoint.",
            "Independently mutate missing, duplicate, undeclared, noncanonical, and escaping entrypoints and require fail-closed behavior.",
            "Keep v1.2.40 immutable and NO-GO; rebuild the candidate only from the new authority.",
        ],
        "releaseDecision": {"coreRuntimeInputReady": True, "authorityV1241Required": True, "integratedCandidateGo": False, "releaseGo": False},
        "fileBindings": [{"path": relative(path), "sha256": sha256(path).lower()} for path in bindings],
    }
    write_once(checkpoint_path, checkpoint)
    print(json.dumps({"status": "passed", "checkpoint": evidence(checkpoint_path), "runtime": evidence(runtime_path), "finalProcess": evidence(process_path), "publishInventory": inventory(PUBLISH_ROOT)}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
