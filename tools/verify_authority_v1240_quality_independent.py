from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import verify_authority_v1240_quality as common


CORE_REFERENCE_SHA = "EB0D956BA2D4F5F4F6A4CA2602B07BB032576B4177B1D8F71D3CE838566AA694"


class GateFailure(RuntimeError):
    pass


def require(value: bool, message: str) -> None:
    if not value:
        raise GateFailure(message)


def load(path: Path) -> dict[str, Any]:
    require(path.is_file(), f"missing input: {path}")
    return common.load(path)


def info(path: Path, project: Path) -> dict[str, Any]:
    return common.file_info(path, project)


def compare_inventory(actual: dict[str, Any], files: int, size: int, tree: str, label: str) -> None:
    require(actual["files"] == files, f"{label} file count drift")
    require(actual["bytes"] == size, f"{label} byte count drift")
    require(actual["treeSha256"] == tree, f"{label} tree drift")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project-root", required=True, type=Path)
    parser.add_argument("--quality-root", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    args = parser.parse_args()

    project = args.project_root.resolve()
    quality_root = args.quality_root.resolve()
    output = args.output_dir.resolve()
    require(not output.exists(), f"append-only output exists: {output}")

    release = project / "releases" / common.RELEASE_ID / "win-x64"
    authority = project / "contracts/authority/worker-registry/v1.2.40-20260902T012900Z"
    checkpoint_path = project / "contracts/core/v2.10/checkpoints/v2.10.5-20260902T012600Z/checkpoint.json"
    packaged_checkpoint = release / checkpoint_path.relative_to(project)
    formal_path = project / "receipts/authority-assembly/20260902T012900Z" / f"{common.RELEASE_ID}.receipt.json"
    closure_manifest_path = project / "contracts/authority/assembler-inputs/v3/v1.2.40-20260902T012900Z/schema-closure-manifest.v1.json"
    reference_path = project / "receipts/authority-admission/20260902T013500Z-v1.2.40-final-process/post-assembly-independent-authority-admission.receipt.json"
    process_path = quality_root / "core-process-named-pipe.receipt.json"
    validation_path = quality_root / "validate-registry.json"
    drift_path = quality_root / "dependency-drift-negative.retry1.receipt.json"
    failed_fixture_path = quality_root / "dependency-drift-negative.receipt.json"
    evidence_root = quality_root / "evidence"

    checkpoint = load(checkpoint_path)
    formal = load(formal_path)
    closure_manifest = load(closure_manifest_path)
    registry_path = authority / "worker-registry.v1.json"
    dependency_path = authority / "dependency-manifest.v1.json"
    runtime_manifest_path = authority / "orchestrator-runtime-manifest.v1.json"
    assembly_path = authority / "assembly-receipt.v1.json"
    path_gate_path = authority / "assembly-path-gate.v1.json"
    registry = load(registry_path)
    dependency = load(dependency_path)
    runtime_manifest = load(runtime_manifest_path)
    assembly = load(assembly_path)
    path_gate = load(path_gate_path)

    release_inventory = common.inventory(release)
    authority_inventory = common.inventory(authority, include_files=True)
    runtime_root = release / "orchestrator"
    runtime_inventory = common.inventory(runtime_root)
    compare_inventory(release_inventory, common.RELEASE_FILES, common.RELEASE_BYTES, common.RELEASE_TREE, "release")
    compare_inventory(runtime_inventory, common.RUNTIME_FILES, common.RUNTIME_BYTES, common.RUNTIME_TREE, "runtime")
    require(authority_inventory["files"] == 5 and authority_inventory["treeSha256"] == common.AUTHORITY_TREE, "authority tree drift")
    require((dependency["files"], dependency["bytes"], dependency["treeSha256"].upper()) == (common.RELEASE_FILES, common.RELEASE_BYTES, common.RELEASE_TREE), "dependency manifest drift")
    require((runtime_manifest["fileCount"], runtime_manifest["totalBytes"], runtime_manifest["treeSha256"].upper()) == (common.RUNTIME_FILES, common.RUNTIME_BYTES, common.RUNTIME_TREE), "runtime manifest aggregate drift")
    runtime_map = {item["path"]: item for item in runtime_manifest["files"]}
    require(len(runtime_map) == common.RUNTIME_FILES, "runtime manifest cardinality drift")
    for path in runtime_root.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(runtime_root).as_posix()
        require(relative in runtime_map, f"runtime manifest missing {relative}")
        require(path.stat().st_size == int(runtime_map[relative]["bytes"]), f"runtime bytes drift: {relative}")
        require(common.sha256(path) == runtime_map[relative]["sha256"].upper(), f"runtime SHA drift: {relative}")

    require(common.sha256(checkpoint_path) == common.CHECKPOINT_SHA, "checkpoint SHA drift")
    require(common.sha256(packaged_checkpoint) == common.CHECKPOINT_SHA, "packaged checkpoint SHA drift")
    require(common.sha256(formal_path) == common.FORMAL_SHA, "formal assembly SHA drift")
    require(common.sha256(reference_path) == CORE_REFERENCE_SHA, "Core reference SHA drift")
    require(formal["status"] == "candidate-built", "formal assembly status drift")
    require(formal["result"]["authorityTargetsCreated"] == 1 and formal["result"]["candidateTargetsCreated"] == 0, "formal authority scope drift")
    inner = formal["result"]["innerReceipt"]
    require(inner["releaseId"] == common.RELEASE_ID and inner["coreContracts"]["checkpointSha256"] == common.CHECKPOINT_SHA, "formal identity drift")

    binding_rows: list[dict[str, str]] = []
    packaged_bindings = 0
    for binding in checkpoint["fileBindings"]:
        source = project / binding["path"]
        require(source.is_file() and common.sha256(source) == binding["sha256"].upper(), f"source binding drift: {binding['path']}")
        packaged_state = "not-packaged-by-design"
        if binding["path"].startswith("contracts/core/v2.10/"):
            packaged = release / binding["path"]
            require(packaged.is_file() and common.sha256(packaged) == binding["sha256"].upper(), f"packaged binding drift: {binding['path']}")
            packaged_state = "matched"
            packaged_bindings += 1
        binding_rows.append({"path": binding["path"], "source": "matched", "packaged": packaged_state})
    require(len(binding_rows) == 18 and packaged_bindings == 5, "fileBinding cardinality drift")

    schema_roots = [project / item["path"] for item in checkpoint["fileBindings"] if item["path"].endswith(".schema.json")]
    closure = common.recursive_schema_closure(project, schema_roots)
    declared = closure_manifest["dependencies"]
    dependency_schema = project / common.SCHEMA_DEPENDENCY
    require(not closure["failures"] and closure["externalDependencies"] == [common.SCHEMA_DEPENDENCY], "recursive schema closure drift")
    require(len(declared) == 1 and declared[0]["path"] == common.SCHEMA_DEPENDENCY, "schema dependency manifest drift")
    require(common.sha256(dependency_schema) == common.SCHEMA_DEPENDENCY_SHA == declared[0]["sha256"].upper(), "schema dependency SHA drift")

    require(registry["registryVersion"] == "1.2.40" and len(registry["workers"]) == 5, "authority registry drift")
    require(assembly["status"] == "candidate-built-offline" and assembly["registryValidation"]["workers"] == 5, "assembly receipt drift")
    require(path_gate["status"] == "passed" and path_gate["pathCount"] == 5, "path gate status drift")
    require(path_gate["allExist"] and path_gate["allWithinFinalRoots"] and path_gate["allBytesAndHashesMatch"], "path gate completeness drift")
    require(common.sha256(assembly_path) == path_gate["assemblyReceipt"]["sha256"].upper(), "assembly path receipt SHA drift")
    workers: list[dict[str, Any]] = []
    for worker in registry["workers"]:
        entrypoint = release / worker["entrypoint"]["path"]
        expected = assembly["workers"][worker["workerId"]]
        require(entrypoint.stat().st_size == int(expected["bytes"]), f"worker bytes drift: {worker['workerId']}")
        require(common.sha256(entrypoint) == expected["sha256"].upper(), f"worker SHA drift: {worker['workerId']}")
        workers.append({"workerId": worker["workerId"], **info(entrypoint, project)})

    stable_path = project / "receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json"
    stable = load(stable_path)
    frozen_rows: list[dict[str, str]] = []
    for item in stable["files"]:
        path = Path(item["path"])
        actual = common.sha256(path) if path.is_file() else "MISSING"
        frozen_rows.append({"path": str(path), "expected": item["sha256"].upper(), "actual": actual, "status": "matched" if actual == item["sha256"].upper() else "failed"})
    require(len(frozen_rows) == 21 and all(item["status"] == "matched" for item in frozen_rows), "frozen v1 drift")

    validation = load(validation_path)
    require(validation == {"schemaVersion": "1.0.0", "status": "valid", "registryVersion": "1.2.40", "workers": 5}, "independent validate-registry drift")
    drift = load(drift_path)
    require(drift["status"] == "passed-rejected" and drift["rejected"] is True and "Dependency manifest drift" in drift["message"], "dependency drift negative failed")
    failed_fixture = load(failed_fixture_path)
    require(failed_fixture["status"] == "failed" and failed_fixture["rejected"] is False, "failed fixture history drift")

    process = load(process_path)
    final_exe = runtime_root / "KnowledgeClipper.Orchestrator.exe"
    require(common.sha256(final_exe) == common.EXE_SHA, "final EXE SHA drift")
    require(process["status"] == "passed" and process["processBoundary"] == "published KnowledgeClipper.Orchestrator.exe serve", "process boundary drift")
    require(Path(process["orchestratorRuntime"]["path"]).resolve() == final_exe.resolve(), "process did not run final EXE")
    require(process["orchestratorRuntime"]["sha256"].upper() == common.EXE_SHA, "process EXE hash drift")
    matrix = process["finalProcessProtocolMatrix"]
    require([item["protocolVersion"] for item in matrix] == ["1.5", "1.6", "1.7"], "IPC version matrix drift")
    require(all(item["startup"] == "passed" and item["typedAction"] == "passed" for item in matrix), "IPC process matrix failed")
    require(process["probe"]["executionStatus"] == "completed" and process["probe"]["readiness"] == "ready" and process["probe"]["externalWrites"] == 0, "Anki probe semantic drift")
    require(process["contentSettings"]["operations"] == ["content.settings.describe", "content.settings.copy", "content.settings.validate", "content.settings.diff"], "Content settings operation drift")
    require(process["contentSettings"]["authorizationDriftRejected"] and process["contentSettings"]["externalWrites"] == 0, "Content settings boundary drift")
    typed = process["typedPublicSettings"]
    require(typed["remnote"]["allowedOperations"] == ["status", "search", "list", "read"] and typed["remnote"]["externalWrites"] == 0, "RemNote read-only boundary drift")
    require(typed["speechVoices"]["settingsMutations"] == 0 and typed["speechVoices"]["physicalSpeakerPlayback"] is False, "speech discovery boundary drift")
    require(typed["speechPreview"]["authorizationMode"] == "explicit-user-action" and typed["speechPreview"]["authorizationDriftRejected"], "speech preview authorization drift")
    require(typed["speechPreview"]["fakeOffline"] and typed["speechPreview"]["physicalSpeakerInvocationsDuringCanary"] == 0, "speech preview was not offline")
    require(typed["speechPreview"]["automaticRetryAllowed"] is False, "speech preview retry drift")

    actions = common.verify_action_receipts(evidence_root)
    require(actions["receiptsChecked"] == 8 and not actions["failures"] and actions["externalWrites"] == 0, "typed receipt/outputRef verification failed")
    fake_registry = load(evidence_root / "registry.json")
    expected_fake_ids = {"content.deepseek", "speech.edge", "export.remnote", "export.anki"}
    require({item["workerId"] for item in fake_registry["workers"]} == expected_fake_ids, "Fake action registry worker set drift")
    require(all("tests/FakeWorker/" in item["entrypoint"]["path"].replace("\\", "/") for item in fake_registry["workers"]), "typed action registry was not FakeWorker-only")
    preview_result = load(evidence_root / "jobs/module-actions/typed-speech-preview-process-canary/result.json")
    require(preview_result["voiceSnapshot"]["source"] == "fake-offline" and preview_result["explicitAuthorizationConsumed"] is True, "preview result route drift")
    remnote_result = load(evidence_root / "jobs/module-actions/typed-remnote-probe-process-canary/result.json")
    require(remnote_result["externalWriteCount"] == 0 and remnote_result["admission"] == "ready", "Fake RemNote result drift")
    voices_result = load(evidence_root / "jobs/module-actions/typed-speech-voices-process-canary/result.json")
    require(voices_result["settingsMutationCount"] == 0 and voices_result["physicalSpeakerPlayback"] is False, "Fake voices result drift")
    task_truth_path = quality_root / process["taskTruth"]["resultRef"]
    require(common.sha256(task_truth_path) == process["taskTruth"]["resultSha256"].upper(), "task.get evidence hash drift")

    old_release = project / "releases/knowledge-clipper-authority-v1.2.39-20260901T130618Z/win-x64"
    old_no_go = project / "receipts/quality-authority-admission/20260902T010057Z-v1.2.39-no-go/post-assembly-independent-authority-admission.receipt.json"
    old_invalidation = project / "receipts/authority-invalidation/20260902T012100Z/knowledge-clipper-authority-v1.2.39.runtime-no-go.json"
    old_inventory = common.inventory(old_release)
    compare_inventory(old_inventory, 596, 268681365, common.V1239_TREE, "v1.2.39 immutable release")
    require(common.sha256(old_no_go) == common.V1239_NO_GO_SHA and load(old_no_go)["status"] == "failed-no-go", "v1.2.39 NO-GO drift")
    invalidation = load(old_invalidation)
    require(common.sha256(old_invalidation) == common.V1239_INVALIDATION_SHA, "v1.2.39 invalidation SHA drift")
    require(invalidation["status"] == "invalidated-no-go" and invalidation["immutableAuthorityPreserved"] and invalidation["immutableReleasePreserved"], "v1.2.39 invalidation drift")

    receipt = {
        "schemaVersion": "knowledge-clipper-quality-authority-v1.2.40-independent-admission.v1",
        "generatedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "passed-authority-offline",
        "releaseId": common.RELEASE_ID,
        "scope": "Independent re-hash plus three new final-EXE process runs. Core self-reported PASS is reference-only.",
        "decision": {"authority": "PASS-OFFLINE", "integratedCandidate": "NOT_RUN", "candidateHash": "NOT_CREATED", "externalE2E": "NOT_RUN", "userHandTestReady": False},
        "inventories": {"release": release_inventory, "authority": authority_inventory, "runtime": runtime_inventory},
        "files": {
            "checkpoint": info(checkpoint_path, project),
            "packagedCheckpoint": info(packaged_checkpoint, project),
            "registry": info(registry_path, project),
            "dependencyManifest": info(dependency_path, project),
            "runtimeManifest": info(runtime_manifest_path, project),
            "assemblyReceipt": info(assembly_path, project),
            "assemblyPathGate": info(path_gate_path, project),
            "formalAssembly": info(formal_path, project),
            "coreReference": {**info(reference_path, project), "role": "reference-only-not-decision-input"},
        },
        "workers": workers,
        "fileBindings": {"checked": len(binding_rows), "matched": len(binding_rows), "packagedMatched": packaged_bindings, "items": binding_rows},
        "schemaClosure": closure,
        "frozenV1": {"checked": len(frozen_rows), "mismatches": 0, "items": frozen_rows},
        "runtimeEvidence": {
            "process": info(process_path, project),
            "evidenceInventory": common.inventory(evidence_root),
            "validateRegistry": info(validation_path, project),
            "dependencyDriftNegative": info(drift_path, project),
            "failedFixtureAttemptRetained": info(failed_fixture_path, project),
            "typedOutputs": actions,
            "protocolMatrix": matrix,
            "taskTruth": info(task_truth_path, project),
        },
        "effectBoundary": {
            "authorityWorkerRunInvocations": 0,
            "authorityWorkerDescribeInvocations": 5,
            "typedActionProvider": "checkpoint-bound FakeWorker only",
            "actualExternalServiceCalls": 0,
            "actualDestinationWrites": 0,
            "actualMineruCalls": 0,
            "actualPhysicalSpeakerInvocations": 0,
            "simulatedRemNoteReadCallsInFakePayload": 4,
        },
        "v1239": {"status": "immutable-no-go", "release": old_inventory, "qualityNoGo": info(old_no_go, project), "invalidation": info(old_invalidation, project)},
        "remainingBoundaries": [
            "Integrated Desktop candidate was not assembled or tested.",
            "Real RemNote, Anki, MinerU, DeepSeek, Edge TTS, destination writes, and physical speaker were not invoked.",
            "Release manifest candidateHash, signing, installer, and real external E2E were not created.",
        ],
    }

    receipt_path = output / "post-assembly-independent-authority-admission.receipt.json"
    readable_path = output / "READABLE.md"
    output.mkdir(parents=True)
    receipt_path.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    readable_path.write_text(
        "# v1.2.40 independent post-assembly admission\n\n"
        "- Authority result: `PASS-OFFLINE`; integrated candidate and external E2E were not run.\n"
        f"- Release: {release_inventory['files']} files / {release_inventory['bytes']} bytes / `{release_inventory['treeSha256']}`.\n"
        f"- Runtime: {runtime_inventory['files']} files / {runtime_inventory['bytes']} bytes / `{runtime_inventory['treeSha256']}`.\n"
        f"- Final EXE: `{common.EXE_SHA}`. IPC 1.5, 1.6, and 1.7 each started a separate final release process and passed typed actions.\n"
        "- Checkpoint fileBindings: 18/18 source; packaged Core contracts: 5/5. Frozen v1: 21/21, 0 mismatch.\n"
        "- Authority registry: 5/5 valid. Dependency-drift negative: rejected. Authorization-drift actions: no receipt/result.\n"
        "- Actual external services, destination writes, MinerU, and physical speaker: 0. Typed actions used FakeWorker only.\n"
        "- Core self-reported receipt is reference-only. v1.2.39 remains immutable NO-GO.\n",
        encoding="utf-8",
    )
    print(json.dumps({"status": receipt["status"], "receipt": info(receipt_path, project), "readable": info(readable_path, project)}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
