from __future__ import annotations

import argparse
import copy
import hashlib
import json
import os
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator, FormatChecker, RefResolver


PROJECT = Path(__file__).resolve().parents[1]
CHECKPOINT = PROJECT / "contracts/core/v2.10/checkpoints/v2.10.0-20260901T091243Z/checkpoint.json"
PROCESS_RECEIPT = PROJECT / "receipts/core-phase1/20260901T091243Z/core-process-named-pipe.receipt.json"
EVIDENCE_ROOT = PROCESS_RECEIPT.parent / "evidence"
EXPECTED_CHECKPOINT_SHA256 = "62BE194F7A5EBD91B05A4CD1407631FB1BDC9029D77AA01D60D594BCA0939F3A"
EXPECTED_RECEIPT_SHA256 = "2B47F4E339C7E252E37AE229B25241F4EC7988A0BDD8AC575671FF6D321081FE"


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def io_path(path: Path) -> Path:
    resolved = str(path.resolve())
    if os.name == "nt" and not resolved.startswith("\\\\?\\"):
        if resolved.startswith("\\\\"):
            resolved = "\\\\?\\UNC\\" + resolved[2:]
        else:
            resolved = "\\\\?\\" + resolved
    return Path(resolved)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with io_path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load(path: Path) -> dict[str, Any]:
    return json.loads(io_path(path).read_text(encoding="utf-8-sig"))


def evidence(path: Path) -> dict[str, Any]:
    return {
        "path": path.resolve().relative_to(PROJECT.resolve()).as_posix(),
        "bytes": io_path(path).stat().st_size,
        "sha256": sha256_file(path),
    }


def inventory(root: Path) -> list[dict[str, Any]]:
    rows = []
    for path in root.rglob("*"):
        if io_path(path).is_file():
            rows.append({
                "path": path.relative_to(root).as_posix(),
                "bytes": io_path(path).stat().st_size,
                "sha256": sha256_file(path),
            })
    return sorted(rows, key=lambda row: row["path"])


def tree_sha256(rows: list[dict[str, Any]]) -> str:
    material = "\n".join(f"{row['path']}\t{row['bytes']}\t{row['sha256'].lower()}" for row in rows)
    return hashlib.sha256(material.encode("utf-8")).hexdigest().upper()


def validate(schema_path: Path, instance: dict[str, Any]) -> None:
    schema = load(schema_path)
    resolver = RefResolver(base_uri=schema_path.as_uri(), referrer=schema)
    errors = sorted(
        Draft202012Validator(schema, resolver=resolver, format_checker=FormatChecker()).iter_errors(instance),
        key=lambda error: list(error.absolute_path),
    )
    require(not errors, f"schema rejected {schema_path.name}: {errors[0].message if errors else ''}")


def verify_file_bindings(checkpoint: dict[str, Any]) -> dict[str, Any]:
    rows = []
    for binding in checkpoint["fileBindings"]:
        path = PROJECT / binding["path"]
        require(path.is_file(), f"checkpoint binding missing: {binding['path']}")
        actual = sha256_file(path)
        rows.append({
            **evidence(path),
            "checkpointSha256": binding["sha256"].upper(),
            "matchesCurrentSource": actual == binding["sha256"].upper(),
        })
    contract_rows = [row for row in rows if row["path"].startswith("contracts/")]
    require(all(row["matchesCurrentSource"] for row in contract_rows), "frozen Core v2.10 contract binding drift")
    return {
        "checked": len(rows),
        "matched": sum(row["matchesCurrentSource"] for row in rows),
        "mismatched": sum(not row["matchesCurrentSource"] for row in rows),
        "rows": rows,
    }


def verify_runtime_semantics(module_receipt: dict[str, Any], task_result: dict[str, Any]) -> None:
    require(module_receipt["operation"] == "anki.probe", "module action operation drift")
    require(module_receipt["accepted"] is True, "module action not accepted")
    require(module_receipt["executionStatus"] == "completed", "execution status drift")
    require(module_receipt["status"] == "ready", "business readiness drift")
    require(module_receipt["externalWriteCount"] == 0, "read-only probe reported writes")
    require(module_receipt["automaticRetryAllowed"] is False, "probe incorrectly allows automatic retry")
    require(len(module_receipt["outputRefs"]) == 1, "typed output reference count drift")
    require(module_receipt["outputRefs"][0]["kind"] == "anki-connection-status", "typed output kind drift")
    require(task_result["type"] == "task.get", "task truth used recent query")
    require(len(task_result["items"]) == 1, "task.get result cardinality drift")
    item = task_result["items"][0]
    require(item["status"] == "waiting_input" and item["attentionRequired"] is True, "task truth attention drift")
    require(item["activeQuestionSession"]["answeredTurnCount"] == 2, "question turn count drift")


def evaluate() -> dict[str, Any]:
    require(sha256_file(CHECKPOINT) == EXPECTED_CHECKPOINT_SHA256, "Core checkpoint SHA drift")
    require(sha256_file(PROCESS_RECEIPT) == EXPECTED_RECEIPT_SHA256, "Core process receipt SHA drift")
    checkpoint = load(CHECKPOINT)
    process_receipt = load(PROCESS_RECEIPT)
    require(checkpoint["status"] == "passed", "Core checkpoint is not passed")
    require(checkpoint["authority"]["ipc"]["schemaVersion"] == "1.5.0", "IPC schema version drift")
    require(checkpoint["authority"]["ipc"]["serverOwnership"] == "orchestrator-exclusive", "IPC server ownership drift")
    require(checkpoint["authority"]["taskTruth"]["currentTaskQuery"] == "task.get", "task truth query drift")
    require(checkpoint["authority"]["taskTruth"]["activeSessionUsesRecentQuery"] is False, "active task still uses recent query")
    require(process_receipt["processBoundary"] == "KnowledgeClipper.Orchestrator.dll serve", "formal process boundary drift")
    require(process_receipt["fakeWorkerRole"] == "worker-only-real-artifact-producer", "fake worker role drift")
    require(process_receipt["displayProjectionReturnedByFake"] is False, "fake worker returned display projection")

    bindings = verify_file_bindings(checkpoint)
    ipc_evidence = load(EVIDENCE_ROOT / "ipc.json")
    registry_evidence = load(EVIDENCE_ROOT / "registry.json")
    require(ipc_evidence["schemaVersion"] == "1.5.0", "runtime IPC version drift")
    require(ipc_evidence["serverOwnership"] == "orchestrator-exclusive", "runtime IPC ownership drift")
    require("module-action" in ipc_evidence["allowedMessages"], "runtime IPC lacks module-action")
    require(len(registry_evidence["workers"]) == 1, "process evidence must use one fake worker")
    require(registry_evidence["workers"][0]["entrypoint"]["path"].endswith("RemNoteQuickClipper.FakeWorker.exe"), "process evidence registry is not fake-worker-only")

    action_root = EVIDENCE_ROOT / "jobs/module-actions/anki-probe-process-canary"
    package_root = action_root / "package"
    action_input_path = action_root / "input.json"
    action_receipt_path = action_root / "receipt.json"
    action_input = load(action_input_path)
    module_receipt = load(action_receipt_path)
    task_result_path = EVIDENCE_ROOT / "task-get-result.v2.4.json"
    task_result = load(task_result_path)

    validate(PROJECT / "contracts/core/v2.10/module-control/schemas/anki-readonly-probe-action.v1.schema.json", action_input)
    validate(PROJECT / "contracts/core/v2.10/module-control/schemas/module-action-receipt.v4.schema.json", module_receipt)
    validate(PROJECT / "contracts/core/v2.10/task-truth/schemas/task-query-result.v2.4.schema.json", task_result)
    verify_runtime_semantics(module_receipt, task_result)

    require(module_receipt["inputSha256"].upper() == sha256_file(action_input_path), "module receipt input SHA drift")
    execution_result = EVIDENCE_ROOT / "jobs" / module_receipt["outputRef"]
    require(io_path(execution_result).is_file(), "execution result reference missing")
    require(module_receipt["outputSha256"].upper() == sha256_file(execution_result), "execution result SHA drift")
    output_ref = module_receipt["outputRefs"][0]
    output_manifest = EVIDENCE_ROOT / "jobs" / output_ref["rootRef"] / output_ref["path"]
    require(io_path(output_manifest).is_file(), "typed output manifest missing")
    require(output_ref["expectedSha256"].upper() == sha256_file(output_manifest), "typed output manifest SHA drift")
    manifest = load(output_manifest)
    for file_entry in manifest["files"]:
        payload = package_root / file_entry["path"]
        require(io_path(payload).is_file(), f"typed output payload missing: {file_entry['path']}")
        require(io_path(payload).stat().st_size == int(file_entry["bytes"]), f"typed output payload bytes drift: {file_entry['path']}")
        require(sha256_file(payload) == file_entry["sha256"].upper(), f"typed output payload SHA drift: {file_entry['path']}")

    require(process_receipt["probe"]["executionResultSha256"].upper() == sha256_file(execution_result), "root receipt execution result SHA drift")
    require(process_receipt["probe"]["outputManifestSha256"].upper() == sha256_file(output_manifest), "root receipt manifest SHA drift")
    require(process_receipt["taskTruth"]["resultSha256"].upper() == sha256_file(task_result_path), "root receipt task result SHA drift")
    evidence_rows = inventory(EVIDENCE_ROOT)
    return {
        "schemaVersion": "knowledge-clipper-quality-core-phase1-admission.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-CORE-PROCESS-EVIDENCE-SOURCE-ADVANCED-DESKTOP-MIGRATION-PENDING",
        "candidateBound": False,
        "core": {
            "checkpoint": evidence(CHECKPOINT),
            "processReceipt": evidence(PROCESS_RECEIPT),
            "checkpointVersion": checkpoint["checkpointVersion"],
            "fileBindingsChecked": bindings["checked"],
            "fileBindingsMatchedCurrentSource": bindings["matched"],
            "fileBindingsDriftedAfterCheckpoint": bindings["mismatched"],
            "fileBindings": bindings["rows"],
        },
        "runtimeEvidence": {
            "root": EVIDENCE_ROOT.resolve().relative_to(PROJECT.resolve()).as_posix(),
            "files": len(evidence_rows),
            "bytes": sum(row["bytes"] for row in evidence_rows),
            "treeSha256": tree_sha256(evidence_rows),
            "ipc": evidence(EVIDENCE_ROOT / "ipc.json"),
            "registry": evidence(EVIDENCE_ROOT / "registry.json"),
            "moduleActionInput": evidence(action_input_path),
            "moduleActionReceipt": evidence(action_receipt_path),
            "executionResult": evidence(execution_result),
            "typedOutputManifest": evidence(output_manifest),
            "taskGetResult": evidence(task_result_path),
        },
        "semantics": {
            "namedPipe": "1.5/orchestrator-exclusive",
            "moduleAction": "anki.probe/v4/completed/ready/provider-read-only/no-auto-retry",
            "taskTruth": "task.get/v2.4/waiting_input/attentionRequired/answeredTurnCount=2",
            "fakeWorkerScope": "worker-only-real-artifact-producer",
            "fakeDisplayProjection": False,
        },
        "desktopBoundary": {
            "status": "PENDING",
            "reason": "Core prerequisite evidence does not resolve Desktop source violations or prove Desktop IPC migration",
            "interfacesRemaining": checkpoint["desktopInterfacesRemaining"],
        },
        "sourceDriftBoundary": {
            "status": "CURRENT-SOURCE-ADVANCED-AFTER-CHECKPOINT" if bindings["mismatched"] else "NO-DRIFT",
            "meaning": "The immutable process evidence remains valid; current source must be judged by the live architecture burndown, not by this historical checkpoint binding.",
            "mismatchedPaths": [row["path"] for row in bindings["rows"] if not row["matchesCurrentSource"]],
        },
        "honestBoundary": checkpoint["verification"]["questionWorkflowStandalone"],
        "sideEffects": {"productionWrites": 0, "candidateRuns": 0, "externalCalls": 0},
    }


def write(output_root: Path) -> dict[str, Any]:
    require(not output_root.exists(), f"append-only output exists: {output_root}")
    output_root.mkdir(parents=True)
    result = evaluate()
    receipt = output_root / "core-phase1-quality-receipt.json"
    receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return {"status": result["status"], "receipt": evidence(receipt)}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-root", type=Path)
    args = parser.parse_args()
    result = write(args.output_root.resolve()) if args.output_root else evaluate()
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
