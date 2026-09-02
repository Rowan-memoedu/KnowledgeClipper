from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator, FormatChecker, RefResolver

import verify_core_phase1_checkpoint as common


PROJECT = common.PROJECT
CHECKPOINT = PROJECT / "contracts/core/v2.10/checkpoints/v2.10.1-20260901T094233Z/checkpoint.json"
PROCESS_RECEIPT = PROJECT / "receipts/core-phase1/20260901T094233Z/core-process-named-pipe.receipt.json"
PROCESS_LOG = PROCESS_RECEIPT.parent / "core-process-named-pipe.log"
EVIDENCE_ROOT = PROCESS_RECEIPT.parent / "evidence"
EXPECTED_CHECKPOINT_SHA256 = "0AF3A5552A84453A670979CCBF432D8AFB9A0B07CD5EB18C4103F3E04357A7D8"
EXPECTED_RECEIPT_SHA256 = "896D67FC22D6DD24D2A7AB410583FC85CF39FC5F59DF3013FB92DD37B4CF8BA2"


def require(condition: bool, message: str) -> None:
    common.require(condition, message)


def validate(schema_path: Path, instance: dict[str, Any]) -> None:
    schema = common.load(schema_path)
    resolver = RefResolver(base_uri=schema_path.as_uri(), referrer=schema)
    errors = list(Draft202012Validator(schema, resolver=resolver, format_checker=FormatChecker()).iter_errors(instance))
    require(not errors, f"schema rejected {schema_path.name}: {errors[0].message if errors else ''}")


def verify_frozen_v1() -> dict[str, Any]:
    baseline_path = PROJECT / "receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json"
    baseline = common.load(baseline_path)
    mismatches = []
    for item in baseline["files"]:
        path = Path(item["path"])
        if not path.is_file() or common.sha256_file(path) != item["sha256"].upper():
            mismatches.append(item["path"])
    require(len(baseline["files"]) == 21 and not mismatches, "frozen contracts v1 drift")
    return {"status": "passed", "filesChecked": 21, "mismatches": 0, "baseline": common.evidence(baseline_path)}


def verify_runtime_action(name: str, expected_kind: str) -> dict[str, Any]:
    root = EVIDENCE_ROOT / "jobs/module-actions" / name
    input_path = root / "input.json"
    receipt_path = root / "receipt.json"
    result_path = root / "result.json"
    action = common.load(input_path)
    receipt = common.load(receipt_path)
    validate(PROJECT / "contracts/core/v2.10/module-control/schemas/content-settings-action.v1.schema.json", action)
    validate(PROJECT / "contracts/core/v2.10/module-control/schemas/module-action-receipt.v5.schema.json", receipt)
    require(receipt["operation"] == action["action"], f"action/receipt operation drift: {name}")
    require(receipt["accepted"] is True and receipt["executionStatus"] == "completed" and receipt["status"] == "completed", f"runtime action did not complete: {name}")
    require(receipt["externalWriteCount"] == 0 and receipt["physicalAudio"] is False and receipt["automaticRetryAllowed"] is False, f"runtime side-effect boundary drift: {name}")
    require(receipt["inputSha256"].upper() == common.sha256_file(input_path), f"runtime input SHA drift: {name}")
    require(receipt["outputSha256"].upper() == common.sha256_file(result_path), f"runtime output SHA drift: {name}")
    require(len(receipt["outputRefs"]) == 1 and receipt["outputRefs"][0]["kind"] == expected_kind, f"typed output drift: {name}")
    require(receipt["outputRefs"][0]["expectedSha256"].upper() == common.sha256_file(result_path), f"typed output reference SHA drift: {name}")
    if action["action"] == "content.settings.copy":
        require(action["authorization"]["targetRoot"] == action["targetRoot"], "copy exact-target authorization drift")
        require(action["authorization"]["mode"] == "explicit-user-action" and action["authorization"]["createOnly"] is True, "copy create-once authorization drift")
    else:
        require(action["effectClass"] == "local-filesystem-read-only" and action["externalMutation"] is False, f"read-only action boundary drift: {name}")
    return {"input": common.evidence(input_path), "receipt": common.evidence(receipt_path), "result": common.evidence(result_path)}


def evaluate() -> dict[str, Any]:
    require(common.sha256_file(CHECKPOINT) == EXPECTED_CHECKPOINT_SHA256, "Core v2.10.1 checkpoint SHA drift")
    require(common.sha256_file(PROCESS_RECEIPT) == EXPECTED_RECEIPT_SHA256, "Core v2.10.1 process receipt SHA drift")
    checkpoint = common.load(CHECKPOINT)
    process_receipt = common.load(PROCESS_RECEIPT)
    require(checkpoint["status"] == "passed" and checkpoint["checkpointVersion"] == "2.10.1", "checkpoint identity/status drift")
    bindings = common.verify_file_bindings(checkpoint)
    require(bindings["matched"] == bindings["checked"] == 11, "Core v2.10.1 fileBinding drift")
    require(checkpoint["authority"]["ipc"]["schemaVersion"] == "1.6.0", "IPC 1.6 binding drift")
    require(checkpoint["authority"]["moduleAction"]["request"] == "knowledge-clipper-module-action.v5", "module action v5 binding drift")

    referenced = []
    for section in ("coreAdapter", "questionWorkflow", "orchestratorContracts", "formalProcessNamedPipe", "schemaValidation", "frozenV1", "desktopAudit", "preservedFailureEvidence"):
        item = checkpoint["verification"][section]
        for key in ("log", "receipt"):
            if item.get(key):
                path = PROJECT / item[key]
                require(path.is_file(), f"checkpoint evidence missing: {item[key]}")
                require(common.sha256_file(path) == item[f"{key}Sha256"].upper(), f"checkpoint evidence SHA drift: {item[key]}")
                referenced.append(common.evidence(path))

    ipc = common.load(EVIDENCE_ROOT / "ipc.json")
    registry = common.load(EVIDENCE_ROOT / "registry.json")
    require(ipc["schemaVersion"] == "1.6.0" and ipc["serverOwnership"] == "orchestrator-exclusive", "runtime IPC evidence drift")
    require({item["workerId"] for item in registry["workers"]} == {"content.deepseek", "export.anki"}, "runtime fake registry identity drift")
    require(all(item["entrypoint"]["path"].endswith("RemNoteQuickClipper.FakeWorker.exe") for item in registry["workers"]), "runtime process evidence used a non-fake worker")
    require(process_receipt["processBoundary"] == "KnowledgeClipper.Orchestrator.dll serve" and process_receipt["displayProjectionReturnedByFake"] is False, "formal process/fake projection boundary drift")
    require(process_receipt["contentSettings"]["authorizationDriftRejected"] is True and process_receipt["contentSettings"]["externalWrites"] == 0, "content settings authorization/side-effect drift")
    require(process_receipt["taskTruth"]["queryType"] == "task.get" and process_receipt["taskTruth"]["recentQueryUsed"] is False, "task.get regression")

    action_evidence = {
        "describe": verify_runtime_action("content-settings-describe-process-canary", "content-settings-description"),
        "copy": verify_runtime_action("content-settings-copy-process-canary", "content-settings-copy-result"),
        "validate": verify_runtime_action("content-settings-validate-process-canary", "content-settings-validation"),
        "diff": verify_runtime_action("content-settings-diff-process-canary", "content-settings-diff"),
    }
    rejected_input = common.load(EVIDENCE_ROOT / "jobs/module-actions/content-settings-copy-rejected-process-canary/input.json")
    require(rejected_input["authorization"]["targetRoot"] != rejected_input["targetRoot"], "authorization drift fixture no longer drifts")
    rejected_schema = common.load(PROJECT / "contracts/core/v2.10/module-control/schemas/content-settings-action.v1.schema.json")
    require(not list(Draft202012Validator(rejected_schema).iter_errors(rejected_input)), "authorization drift is schema-valid by design and must be rejected by runtime exact-target policy")

    schema_receipt = common.load(PROJECT / "receipts/core-phase1/20260901T094233Z/schema-validation.receipt.json")
    require(schema_receipt["result"] == {"schemaErrors": 0, "fixtureErrors": 0, "runtimeReceiptErrors": 0}, "schema validation receipt reports errors")
    frozen = verify_frozen_v1()
    rows = common.inventory(EVIDENCE_ROOT)
    return {
        "schemaVersion": "knowledge-clipper-quality-core-v2.10.1-admission.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-CORE-V2.10.1-PREREQUISITE-DESKTOP-MIGRATION-PENDING",
        "candidateBound": False,
        "checkpoint": common.evidence(CHECKPOINT),
        "processReceipt": common.evidence(PROCESS_RECEIPT),
        "processLog": common.evidence(PROCESS_LOG),
        "fileBindings": bindings,
        "referencedEvidence": referenced,
        "runtimeEvidence": {"files": len(rows), "bytes": sum(row["bytes"] for row in rows), "treeSha256": common.tree_sha256(rows), "actions": action_evidence},
        "frozenV1": frozen,
        "semantics": {
            "ipc": "1.6/orchestrator-exclusive",
            "moduleAction": "v5/content.settings.describe-copy-validate-diff",
            "copy": "explicit-user-action/exact-target/create-once/no-auto-retry",
            "authorizationDrift": "fail-closed-before-worker",
            "taskTruth": "task.get/v2.4/no-query-recent",
        },
        "desktopBoundary": {"status": "PENDING", "migrationInput": checkpoint["desktopMigrationInput"]},
        "releaseDecision": checkpoint["releaseDecision"],
        "sideEffects": {"productionWrites": 0, "candidateRuns": 0, "externalCalls": 0},
    }


def write(output_root: Path) -> dict[str, Any]:
    require(not output_root.exists(), f"append-only output exists: {output_root}")
    output_root.mkdir(parents=True)
    result = evaluate()
    receipt = output_root / "core-v2.10.1-quality-receipt.json"
    receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return {"status": result["status"], "receipt": common.evidence(receipt)}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-root", type=Path)
    args = parser.parse_args()
    result = write(args.output_root.resolve()) if args.output_root else evaluate()
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
