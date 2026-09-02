from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


RELEASE_ID = "knowledge-clipper-authority-v1.2.40-20260902T012900Z"
RELEASE_FILES = 595
RELEASE_BYTES = 268725358
RELEASE_TREE = "2892F056A612B4555F4E54C080BD6008411CDAA7E5A86FB0A2513FFF3306DF39"
AUTHORITY_TREE = "36DD7AF3289B6137E8DB2DB31FB77336A59C80512651A197EF98A350053DFBF5"
RUNTIME_FILES = 187
RUNTIME_BYTES = 75050245
RUNTIME_TREE = "074939D2987ABD727545EB7D30A9660A7F3FB5BE616AE37C8996E69C6AB90E7E"
EXE_SHA = "DD84BEDDCE083D8F62284189408A97C0549B7332390DFF69B7E2B4586D75C82C"
CHECKPOINT_SHA = "D313242ED851AD93AEFBE64BDDB7C6ADA767630936AA63ACE0ECB65718693FCD"
FORMAL_SHA = "422C0C78B5D528D739CDEFA3ED6EEDC8232B65F18E7ABB94CE97929776C7EABB"
SCHEMA_DEPENDENCY = "contracts/core/v2.3/playback-control/schemas/stage-control-request.v1.schema.json"
SCHEMA_DEPENDENCY_SHA = "B717994985275623719C09C93D04A4F05A5B23BAE6AB240191E00898908182E8"
V1239_TREE = "5F6F87792C67B30FC1BE321359E9E114252D06B48C3542E7628BE5C6752937EF"
V1239_NO_GO_SHA = "2A9F1301BB18214CFB86EAE7DD02F845571A3E4BEE73E9A46ABC5FAD595EA0FB"
V1239_INVALIDATION_SHA = "0E28F31C0E856E557EACD028426BA9479A9C17C849402C71583CD14C132192E1"


def physical(path: Path) -> Path:
    resolved = path.resolve()
    if str(resolved).startswith("\\\\?\\"):
        return resolved
    return Path("\\\\?\\" + str(resolved))


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with physical(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def file_info(path: Path, root: Path | None = None) -> dict[str, Any]:
    return {
        "path": path.relative_to(root).as_posix() if root else str(path),
        "bytes": physical(path).stat().st_size,
        "sha256": sha256(path),
    }


def inventory(root: Path, include_files: bool = False) -> dict[str, Any]:
    paths = sorted((p for p in root.rglob("*") if p.is_file()), key=lambda p: p.relative_to(root).as_posix())
    rows: list[dict[str, Any]] = []
    lines: list[str] = []
    total = 0
    for path in paths:
        rel = path.relative_to(root).as_posix()
        size = path.stat().st_size
        digest = sha256(path)
        total += size
        lines.append(f"{rel}\t{size}\t{digest.lower()}")
        if include_files:
            rows.append({"path": rel, "bytes": size, "sha256": digest})
    result: dict[str, Any] = {
        "root": str(root),
        "files": len(paths),
        "bytes": total,
        "treeSha256": hashlib.sha256("\n".join(lines).encode("utf-8")).hexdigest().upper(),
        "algorithm": "sort relative paths ordinal; path TAB bytes TAB lowercase SHA-256; join with LF and no trailing LF; SHA-256 UTF-8",
    }
    if include_files:
        result["fileInventory"] = rows
    return result


def collect_refs(node: Any) -> list[str]:
    found: list[str] = []
    if isinstance(node, dict):
        for key, value in node.items():
            if key == "$ref" and isinstance(value, str):
                found.append(value)
            else:
                found.extend(collect_refs(value))
    elif isinstance(node, list):
        for value in node:
            found.extend(collect_refs(value))
    return found


def recursive_schema_closure(project: Path, roots: list[Path]) -> dict[str, Any]:
    pending = list(roots)
    visited: set[Path] = set()
    refs: list[dict[str, str]] = []
    failures: list[str] = []
    project_resolved = project.resolve()
    while pending:
        current = pending.pop()
        current = current.resolve()
        if current in visited:
            continue
        if not current.is_file():
            failures.append(f"missing schema: {current}")
            continue
        try:
            current.relative_to(project_resolved)
        except ValueError:
            failures.append(f"schema escaped project: {current}")
            continue
        visited.add(current)
        for ref in collect_refs(load(current)):
            file_part = ref.split("#", 1)[0]
            if not file_part or "://" in file_part:
                continue
            target = (current.parent / file_part).resolve()
            try:
                target_rel = target.relative_to(project_resolved).as_posix()
            except ValueError:
                failures.append(f"ref escaped project: {current} -> {ref}")
                continue
            refs.append({"from": current.relative_to(project_resolved).as_posix(), "ref": ref, "target": target_rel})
            if not target.is_file():
                failures.append(f"missing ref target: {target_rel}")
            elif target not in visited:
                pending.append(target)
    external = sorted(
        p.relative_to(project_resolved).as_posix()
        for p in visited
        if not p.relative_to(project_resolved).as_posix().startswith("contracts/core/v2.10/")
    )
    return {
        "schemasChecked": len(visited),
        "localRefsChecked": len(refs),
        "externalDependencies": external,
        "refs": sorted(refs, key=lambda item: (item["from"], item["ref"])),
        "failures": failures,
    }


def resolve_output_ref(evidence_root: Path, receipt: dict[str, Any], reference: dict[str, Any]) -> Path:
    root_ref = reference["rootRef"]
    if root_ref == "jobs-root":
        base = evidence_root / "jobs"
    else:
        base = evidence_root / "jobs" / root_ref
    return base / Path(reference["path"])


def verify_action_receipts(evidence_root: Path) -> dict[str, Any]:
    action_root = evidence_root / "jobs" / "module-actions"
    receipts = sorted(action_root.glob("*/receipt.json"))
    checked: list[dict[str, Any]] = []
    failures: list[str] = []
    external_writes = 0
    for path in receipts:
        receipt = load(path)
        external_writes += int(receipt.get("externalWriteCount", 0))
        if receipt.get("accepted") is not True or receipt.get("executionStatus") != "completed":
            failures.append(f"accepted receipt status drift: {path.parent.name}")
        output_ref = receipt.get("outputRef")
        output_sha = receipt.get("outputSha256")
        if not output_ref or not output_sha:
            failures.append(f"missing primary output: {path.parent.name}")
        else:
            output_path = evidence_root / "jobs" / Path(output_ref)
            if not physical(output_path).is_file() or sha256(output_path) != str(output_sha).upper():
                failures.append(f"primary output drift: {path.parent.name}")
        refs = receipt.get("outputRefs") or []
        if len(refs) != 1:
            failures.append(f"typed output cardinality drift: {path.parent.name}")
        for reference in refs:
            target = resolve_output_ref(evidence_root, receipt, reference)
            if not physical(target).is_file() or sha256(target) != str(reference["expectedSha256"]).upper():
                failures.append(f"typed output drift: {path.parent.name}")
        checked.append({"actionId": receipt.get("actionId"), "operation": receipt.get("operation"), "receipt": file_info(path, evidence_root)})
    rejected = [
        action_root / "content-settings-copy-rejected-process-canary",
        action_root / "typed-speech-preview-rejected-process-canary",
    ]
    for directory in rejected:
        if (directory / "receipt.json").exists() or (directory / "result.json").exists():
            failures.append(f"negative action published output: {directory.name}")
    expected_operations = {
        "anki.probe", "content.settings.describe", "content.settings.copy", "content.settings.validate",
        "content.settings.diff", "remnote.probe", "speech.voices", "speech.preview",
    }
    actual_operations = {item["operation"] for item in checked}
    if actual_operations != expected_operations:
        failures.append(f"typed operation set drift: {sorted(actual_operations)}")
    return {
        "receiptsChecked": len(checked),
        "operations": sorted(actual_operations),
        "items": checked,
        "negativeActionsWithoutReceiptOrResult": len(rejected),
        "externalWrites": external_writes,
        "failures": failures,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project-root", required=True, type=Path)
    parser.add_argument("--runtime-evidence-root", required=True, type=Path)
    parser.add_argument("--process-evidence-root", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    args = parser.parse_args()

    project = args.project_root.resolve()
    output = args.output_dir.resolve()
    if output.exists():
        raise FileExistsError(f"append-only output directory exists: {output}")
    output.mkdir(parents=True)

    release = project / "releases" / RELEASE_ID / "win-x64"
    authority = project / "contracts/authority/worker-registry/v1.2.40-20260902T012900Z"
    checkpoint_path = project / "contracts/core/v2.10/checkpoints/v2.10.5-20260902T012600Z/checkpoint.json"
    packaged_checkpoint = release / checkpoint_path.relative_to(project)
    formal_path = project / "receipts/authority-assembly/20260902T012900Z" / f"{RELEASE_ID}.receipt.json"
    closure_manifest_path = project / "contracts/authority/assembler-inputs/v3/v1.2.40-20260902T012900Z/schema-closure-manifest.v1.json"
    runtime_gate_path = args.runtime_evidence_root.resolve() / "runtime-gates.json"
    process_path = args.process_evidence_root.resolve() / "core-process.receipt.json"
    old_release = project / "releases/knowledge-clipper-authority-v1.2.39-20260901T130618Z/win-x64"
    old_no_go = project / "receipts/quality-authority-admission/20260902T010057Z-v1.2.39-no-go/post-assembly-independent-authority-admission.receipt.json"
    old_invalidation = project / "receipts/authority-invalidation/20260902T012100Z/knowledge-clipper-authority-v1.2.39.runtime-no-go.json"

    checkpoint = load(checkpoint_path)
    formal = load(formal_path)
    closure_manifest = load(closure_manifest_path)
    registry = load(authority / "worker-registry.v1.json")
    dependency = load(authority / "dependency-manifest.v1.json")
    runtime_manifest = load(authority / "orchestrator-runtime-manifest.v1.json")
    assembly = load(authority / "assembly-receipt.v1.json")
    path_gate = load(authority / "assembly-path-gate.v1.json")
    runtime_gate = load(runtime_gate_path)
    process = load(process_path)

    checks: list[dict[str, str]] = []
    def check(condition: bool, name: str) -> None:
        checks.append({"status": "passed" if condition else "failed", "check": name})

    release_inventory = inventory(release)
    authority_inventory = inventory(authority, include_files=True)
    runtime_root = release / "orchestrator"
    runtime_inventory = inventory(runtime_root)
    check((release_inventory["files"], release_inventory["bytes"], release_inventory["treeSha256"]) == (RELEASE_FILES, RELEASE_BYTES, RELEASE_TREE), "release exact inventory")
    check((dependency["files"], dependency["bytes"], dependency["treeSha256"].upper()) == (RELEASE_FILES, RELEASE_BYTES, RELEASE_TREE), "dependency manifest exact release inventory")
    check(authority_inventory["files"] == 5 and authority_inventory["treeSha256"] == AUTHORITY_TREE, "authority exact five-file tree")
    check((runtime_inventory["files"], runtime_inventory["bytes"], runtime_inventory["treeSha256"]) == (RUNTIME_FILES, RUNTIME_BYTES, RUNTIME_TREE), "runtime exact inventory")
    check((runtime_manifest["fileCount"], runtime_manifest["totalBytes"], runtime_manifest["treeSha256"].upper()) == (RUNTIME_FILES, RUNTIME_BYTES, RUNTIME_TREE), "runtime manifest aggregate")

    manifest_map = {item["path"]: item for item in runtime_manifest["files"]}
    runtime_file_failures: list[str] = []
    for path in sorted((p for p in runtime_root.rglob("*") if p.is_file()), key=lambda p: p.relative_to(runtime_root).as_posix()):
        rel = path.relative_to(runtime_root).as_posix()
        item = manifest_map.get(rel)
        if item is None or int(item["bytes"]) != path.stat().st_size or str(item["sha256"]).upper() != sha256(path):
            runtime_file_failures.append(rel)
    check(len(manifest_map) == RUNTIME_FILES and not runtime_file_failures, "all 187 runtime manifest entries rehashed")

    check(sha256(checkpoint_path) == CHECKPOINT_SHA and sha256(packaged_checkpoint) == CHECKPOINT_SHA, "source and packaged checkpoint exact SHA")
    check(sha256(formal_path) == FORMAL_SHA, "formal assembly receipt exact SHA")
    check(formal["status"] == "candidate-built" and formal["result"]["authorityTargetsCreated"] == 1 and formal["result"]["candidateTargetsCreated"] == 0, "formal receipt authority-only result")
    check(formal["v3Assembler"]["sha256Before"] == formal["v3Assembler"]["sha256After"], "frozen V3 assembler unchanged")
    inner = formal["result"]["innerReceipt"]
    check(inner["releaseId"] == RELEASE_ID and inner["coreContracts"]["checkpointSha256"] == CHECKPOINT_SHA, "formal identity binding")

    binding_rows: list[dict[str, str]] = []
    binding_failures: list[str] = []
    for binding in checkpoint["fileBindings"]:
        source = project / binding["path"]
        expected = binding["sha256"].upper()
        source_ok = source.is_file() and sha256(source) == expected
        packaged_state = "not-packaged-by-design"
        if binding["path"].startswith("contracts/core/v2.10/"):
            packaged = release / binding["path"]
            packaged_ok = packaged.is_file() and sha256(packaged) == expected
            packaged_state = "matched" if packaged_ok else "failed"
            if not packaged_ok:
                binding_failures.append(f"packaged:{binding['path']}")
        if not source_ok:
            binding_failures.append(f"source:{binding['path']}")
        binding_rows.append({"path": binding["path"], "source": "matched" if source_ok else "failed", "packaged": packaged_state})
    check(len(binding_rows) == 18 and not binding_failures, "all 18 checkpoint fileBindings rehashed")

    schema_roots = [project / item["path"] for item in checkpoint["fileBindings"] if item["path"].endswith(".schema.json")]
    closure = recursive_schema_closure(project, schema_roots)
    declared = closure_manifest["dependencies"]
    dependency_path = project / SCHEMA_DEPENDENCY
    declared_ok = (
        len(declared) == 1 and declared[0]["path"] == SCHEMA_DEPENDENCY and int(declared[0]["bytes"]) == dependency_path.stat().st_size
        and str(declared[0]["sha256"]).upper() == sha256(dependency_path) == SCHEMA_DEPENDENCY_SHA
        and declared[0]["referencedBy"] == "contracts/core/v2.10/module-control/schemas/ipc-request.v1.7.schema.json"
    )
    check(closure_manifest["checkpoint"]["sha256"] == CHECKPOINT_SHA and declared_ok, "schema closure manifest exact dependency")
    check(not closure["failures"] and closure["externalDependencies"] == [SCHEMA_DEPENDENCY], "recursive local schema ref closure")

    check(registry["registryVersion"] == "1.2.40" and len(registry["workers"]) == 5, "registry v1.2.40 has five workers")
    check(assembly["status"] == "candidate-built-offline" and assembly["registryValidation"]["workers"] == 5, "assembly receipt offline 5/5")
    check(path_gate["status"] == "passed" and path_gate["pathCount"] == 5 and path_gate["allExist"] and path_gate["allWithinFinalRoots"] and path_gate["allBytesAndHashesMatch"], "assembly path gate 5/5")
    worker_rows: list[dict[str, Any]] = []
    worker_failures: list[str] = []
    path_gate_map = {item["workerId"]: item for item in path_gate["paths"]}
    for worker in registry["workers"]:
        worker_id = worker["workerId"]
        target = release / worker["entrypoint"]["path"]
        info = file_info(target, release)
        expected = assembly["workers"][worker_id]
        gated = path_gate_map[worker_id]
        if not (info["bytes"] == int(expected["bytes"]) == int(gated["bytes"]) and info["sha256"] == expected["sha256"].upper() == gated["sha256"].upper()):
            worker_failures.append(worker_id)
        worker_rows.append({"workerId": worker_id, **info})
    check(not worker_failures and len(worker_rows) == 5, "five worker final paths bytes and hashes")

    frozen_baseline = load(project / "receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json")
    frozen_rows: list[dict[str, str]] = []
    for item in frozen_baseline["files"]:
        path = Path(item["path"])
        actual = sha256(path) if path.is_file() else "MISSING"
        frozen_rows.append({"path": str(path), "expectedSha256": item["sha256"], "actualSha256": actual, "status": "matched" if actual == item["sha256"].upper() else "failed"})
    check(len(frozen_rows) == 21 and all(item["status"] == "matched" for item in frozen_rows), "frozen v1 independently rehashed 21/21")

    old_inventory = inventory(old_release)
    old_no_go_json = load(old_no_go)
    old_invalidation_json = load(old_invalidation)
    check((old_inventory["files"], old_inventory["bytes"], old_inventory["treeSha256"]) == (596, 268681365, V1239_TREE), "v1.2.39 immutable release preserved")
    check(sha256(old_no_go) == V1239_NO_GO_SHA and old_no_go_json["status"] == "failed-no-go", "v1.2.39 Quality NO-GO preserved")
    check(sha256(old_invalidation) == V1239_INVALIDATION_SHA and old_invalidation_json["status"] == "invalidated-no-go" and old_invalidation_json["immutableAuthorityPreserved"] and old_invalidation_json["immutableReleasePreserved"], "v1.2.39 invalidation preserved")

    check(runtime_gate["status"] == "passed" and runtime_gate["validateRegistry"] == {"status": "valid", "workers": 5}, "independent final registry 5/5")
    check(runtime_gate["protocol10"]["taskQuery"]["status"] == "passed" and runtime_gate["protocol10"]["taskCommand"]["rejectionCode"] == "contract.expected_state_mismatch", "independent IPC 1.0 positive and negative")
    check(runtime_gate["protocol11"]["stageControl"]["status"] == "passed" and runtime_gate["dependencyDriftNegative"]["rejected"] is True, "independent IPC 1.1 and dependency drift negative")
    check(runtime_gate["workerRunInvocations"] == runtime_gate["physicalSpeakerInvocations"] == runtime_gate["externalWrites"] == 0, "runtime gate effect counts zero")

    final_exe = runtime_root / "KnowledgeClipper.Orchestrator.exe"
    matrix = process["finalProcessProtocolMatrix"]
    check(process["status"] == "passed" and process["processBoundary"] == "published KnowledgeClipper.Orchestrator.exe serve", "independent final EXE process boundary")
    check(Path(process["orchestratorRuntime"]["path"]).resolve() == final_exe.resolve() and process["orchestratorRuntime"]["sha256"].upper() == sha256(final_exe) == EXE_SHA, "independent process tested exact final EXE")
    check([item["protocolVersion"] for item in matrix] == ["1.5", "1.6", "1.7"] and all(item["startup"] == item["typedAction"] == "passed" for item in matrix), "IPC 1.5/1.6/1.7 startup and typed actions")
    typed = process["typedPublicSettings"]
    check(process["probe"]["executionStatus"] == "completed" and process["probe"]["readiness"] == "ready" and process["probe"]["externalWrites"] == 0, "IPC 1.5 typed Anki readiness")
    check(process["contentSettings"]["authorizationDriftRejected"] and process["contentSettings"]["externalWrites"] == 0, "IPC 1.6 operations and authorization negative")
    check(typed["remnote"]["allowedOperations"] == ["status", "search", "list", "read"] and typed["remnote"]["externalWrites"] == 0, "IPC 1.7 RemNote read-only boundary")
    check(typed["speechVoices"]["settingsMutations"] == 0 and typed["speechVoices"]["physicalSpeakerPlayback"] is False, "IPC 1.7 speech voices read-only boundary")
    preview = typed["speechPreview"]
    check(preview["authorizationMode"] == "explicit-user-action" and preview["authorizationDriftRejected"] and preview["fakeOffline"] and preview["physicalSpeakerInvocationsDuringCanary"] == 0 and preview["automaticRetryAllowed"] is False, "IPC 1.7 preview explicit offline negative boundary")
    check(process["taskTruth"]["status"] == "passed" and process["taskTruth"]["queryType"] == "task.get" and process["taskTruth"]["recentQueryUsed"] is False, "task.get durable truth")

    action_receipts = verify_action_receipts(args.process_evidence_root.resolve() / "evidence")
    check(action_receipts["receiptsChecked"] == 8 and not action_receipts["failures"] and action_receipts["externalWrites"] == 0, "all typed receipt outputRefs and negative non-publication")
    fake_registry = load(args.process_evidence_root.resolve() / "evidence/registry.json")
    fake_paths = [worker["entrypoint"]["path"].replace("\\", "/") for worker in fake_registry["workers"]]
    check(len(fake_paths) == 4 and all("tests/FakeWorker/" in path for path in fake_paths), "runtime evidence used FakeWorker-only registry")

    failures = [item["check"] for item in checks if item["status"] == "failed"]
    status = "passed-authority-offline" if not failures else "failed-no-go"
    report = {
        "schemaVersion": "knowledge-clipper-quality-authority-v1.2.40-admission.v1",
        "generatedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": status,
        "releaseId": RELEASE_ID,
        "scope": "Independent Quality re-hash, schema closure, final-process IPC matrix, typed-output and immutable-baseline admission. Core self-reported PASS was not used as a decision input.",
        "decision": {"authority": "PASS-OFFLINE" if not failures else "NO-GO", "integratedCandidate": "NOT_RUN", "userHandTestReady": False},
        "inventories": {"release": release_inventory, "authority": authority_inventory, "runtime": runtime_inventory},
        "files": {"checkpoint": file_info(checkpoint_path, project), "packagedCheckpoint": file_info(packaged_checkpoint, release), "formalAssembly": file_info(formal_path, project), "finalExe": file_info(final_exe, release)},
        "fileBindings": {"checked": len(binding_rows), "failures": binding_failures, "items": binding_rows},
        "schemaClosure": closure,
        "runtimeManifestFailures": runtime_file_failures,
        "workers": worker_rows,
        "frozenV1": {"checked": len(frozen_rows), "mismatches": sum(item["status"] != "matched" for item in frozen_rows), "items": frozen_rows},
        "v1239": {"release": old_inventory, "qualityNoGo": file_info(old_no_go, project), "invalidation": file_info(old_invalidation, project)},
        "runtimeEvidence": {"authorityGates": file_info(runtime_gate_path, project), "finalProcess": file_info(process_path, project), "typedOutputs": action_receipts},
        "effectBoundary": {"provider": "Fake/offline only", "externalApiCalls": 0, "externalWrites": 0, "remnoteWrites": 0, "ankiWrites": 0, "mineruCalls": 0, "physicalSpeakerInvocations": 0},
        "checks": checks,
        "failedChecks": failures,
    }
    receipt = output / "post-assembly-independent-authority-admission.receipt.json"
    receipt.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": status, "receipt": str(receipt), "bytes": receipt.stat().st_size, "sha256": sha256(receipt), "failedChecks": failures}, ensure_ascii=False, indent=2))
    return 0 if not failures else 1


if __name__ == "__main__":
    raise SystemExit(main())
