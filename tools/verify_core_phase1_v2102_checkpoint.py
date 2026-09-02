from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path
from pathlib import PurePosixPath
from typing import Any

from jsonschema import Draft202012Validator

import verify_core_phase1_checkpoint as common
import verify_core_phase1_v2101_checkpoint as previous


PROJECT = common.PROJECT
CHECKPOINT = PROJECT / "contracts/core/v2.10/checkpoints/v2.10.2-20260901T100127Z/checkpoint.json"
PROCESS_RECEIPT = PROJECT / "receipts/core-phase1/20260901T100127Z/core-process-named-pipe.receipt.json"
PROCESS_LOG = PROCESS_RECEIPT.parent / "core-process-named-pipe.log"
EVIDENCE_ROOT = PROCESS_RECEIPT.parent / "evidence"
EXPECTED_CHECKPOINT_SHA256 = "954F1D28DA9F64E15099FD55527FC158A0C3E5E34668A3F2385A09A8266F1039"
EXPECTED_RECEIPT_SHA256 = "65445D97E6850E1BB1CDB211B872CF6E46182FA63685F939E8AA5BE50F5E71CD"


def require(condition: bool, message: str) -> None:
    common.require(condition, message)


def verify_output_reference(receipt: dict[str, Any], jobs_root: Path) -> Path:
    require(len(receipt.get("outputRefs") or []) == 1, "typed receipt output reference cardinality drift")
    reference = receipt["outputRefs"][0]
    require(reference["rootRef"] == "jobs-root", "typed output rootRef is not jobs-root")
    pure = PurePosixPath(reference["path"])
    require(not pure.is_absolute() and ".." not in pure.parts and ":" not in reference["path"], "typed output path escaped jobs-root")
    result_path = jobs_root / Path(*pure.parts)
    require(common.io_path(result_path).is_file(), "typed output file missing")
    require(reference["expectedSha256"].upper() == common.sha256_file(result_path), "typed output reference SHA drift")
    require(receipt["outputRef"] == reference["path"] and receipt["outputSha256"].upper() == reference["expectedSha256"].upper(), "receipt/outputRefs identity drift")
    return result_path


def verify_terminal_failure(receipt: dict[str, Any]) -> None:
    require(receipt.get("accepted") is False, "failure receipt is accepted")
    require(receipt.get("status") in {"failed", "unknown_external_state"}, "failure receipt status drift")
    require(receipt.get("executionStatus") in {"failed", "unknown_external_state"}, "failure executionStatus drift")
    require(receipt.get("automaticRetryAllowed") is False, "failure receipt allows automatic retry")
    require(receipt.get("outputRef") is None and receipt.get("outputSha256") is None and receipt.get("outputRefs") == [], "failure receipt published output")
    require(isinstance(receipt.get("error"), dict), "failure receipt lost error")


def verify_runtime_action(name: str, operation: str, kind: str, input_schema: Path, result_schema: Path) -> dict[str, Any]:
    root = EVIDENCE_ROOT / "jobs/module-actions" / name
    input_path = root / "input.json"
    receipt_path = root / "receipt.json"
    result_path = root / "result.json"
    action = common.load(input_path)
    receipt = common.load(receipt_path)
    result = common.load(result_path)
    common.validate(input_schema, action)
    common.validate(PROJECT / "contracts/core/v2.10/module-control/schemas/module-action-receipt.v6.schema.json", receipt)
    common.validate(result_schema, result)
    require(receipt["operation"] == operation and receipt["accepted"] is True and receipt["executionStatus"] == "completed", f"typed runtime action failed: {name}")
    require(receipt["inputSha256"].upper() == common.sha256_file(input_path), f"typed input SHA drift: {name}")
    require(receipt["outputSha256"].upper() == common.sha256_file(result_path), f"typed result SHA drift: {name}")
    require(receipt["externalWriteCount"] == 0 and receipt["automaticRetryAllowed"] is False, f"typed action write/retry drift: {name}")
    require(len(receipt["outputRefs"]) == 1 and receipt["outputRefs"][0]["kind"] == kind, f"typed result kind drift: {name}")
    require(verify_output_reference(receipt, EVIDENCE_ROOT / "jobs").resolve() == result_path.resolve(), f"typed output path drift: {name}")
    return {"input": common.evidence(input_path), "receipt": common.evidence(receipt_path), "result": common.evidence(result_path), "document": result}


def evaluate() -> dict[str, Any]:
    require(common.sha256_file(CHECKPOINT) == EXPECTED_CHECKPOINT_SHA256, "Core v2.10.2 checkpoint SHA drift")
    require(common.sha256_file(PROCESS_RECEIPT) == EXPECTED_RECEIPT_SHA256, "Core v2.10.2 process receipt SHA drift")
    checkpoint = common.load(CHECKPOINT)
    process_receipt = common.load(PROCESS_RECEIPT)
    require(checkpoint["status"] == "passed" and checkpoint["checkpointVersion"] == "2.10.2", "checkpoint identity/status drift")
    bindings = common.verify_file_bindings(checkpoint)
    require(bindings["checked"] == bindings["matched"] == 11, "Core v2.10.2 fileBinding drift")
    require(checkpoint["authority"]["ipc"]["schemaVersion"] == "1.7.0", "IPC 1.7 binding drift")
    require(checkpoint["authority"]["ipc"]["request"] == "knowledge-clipper-module-action.v6", "module-action v6 binding drift")

    referenced = []
    for section in ("coreAdapter", "questionWorkflow", "orchestratorContracts", "formalProcessNamedPipe", "schemaValidation", "frozenV1"):
        item = checkpoint["verification"][section]
        for key in ("log", "receipt"):
            if item.get(key):
                path = PROJECT / item[key]
                require(path.is_file(), f"checkpoint evidence missing: {item[key]}")
                require(common.sha256_file(path) == item[f"{key}Sha256"].upper(), f"checkpoint evidence SHA drift: {item[key]}")
                referenced.append(common.evidence(path))

    ipc = common.load(EVIDENCE_ROOT / "ipc.json")
    registry = common.load(EVIDENCE_ROOT / "registry.json")
    require(ipc["schemaVersion"] == "1.7.0" and ipc["serverOwnership"] == "orchestrator-exclusive", "runtime IPC evidence drift")
    require({item["workerId"] for item in registry["workers"]} == {"content.deepseek", "export.anki", "export.remnote", "speech.edge"}, "runtime fake registry identity drift")
    require(all(item["entrypoint"]["path"].endswith("RemNoteQuickClipper.FakeWorker.exe") for item in registry["workers"]), "runtime process evidence used a non-fake worker")
    require(process_receipt["processBoundary"] == "KnowledgeClipper.Orchestrator.dll serve" and process_receipt["displayProjectionReturnedByFake"] is False, "formal process/fake projection boundary drift")

    actions = {
        "remnote": verify_runtime_action(
            "typed-remnote-probe-process-canary", "remnote.probe", "remnote-public-probe-result",
            PROJECT / "contracts/remnote-public-probe-request.v2.schema.json",
            PROJECT / "contracts/remnote-public-probe-result.v2.schema.json",
        ),
        "speechVoices": verify_runtime_action(
            "typed-speech-voices-process-canary", "speech.voices", "speech-voice-discovery-result",
            PROJECT / "tests/RemNoteQuickClipper.Speech.Tests/fixtures/settings-companion/speech-settings-voice-discovery-input.schema.json",
            PROJECT / "tests/RemNoteQuickClipper.Speech.Tests/fixtures/settings-companion/speech-settings-voice-discovery-output.schema.json",
        ),
        "speechPreview": verify_runtime_action(
            "typed-speech-preview-process-canary", "speech.preview", "speech-speaker-preview-result",
            PROJECT / "contracts/core/v2.10/module-control/schemas/speech-settings-speaker-preview-action.v2.schema.json",
            PROJECT / "tests/RemNoteQuickClipper.Speech.Tests/fixtures/settings-companion/speech-settings-speaker-preview-output.schema.json",
        ),
    }
    remnote = actions["remnote"]["document"]
    require([item["operation"] for item in remnote["toolChecks"]] == ["status", "search", "list", "read"], "RemNote operation allow-list drift")
    require(remnote["externalWriteCount"] == 0 and remnote["sanitized"] is True, "RemNote probe write/sanitization drift")
    voices = actions["speechVoices"]["document"]
    require(voices["settingsMutationCount"] == 0 and voices["physicalSpeakerPlayback"] is False and voices["sideEffectsPerformed"] == [], "Speech discovery side-effect drift")
    preview = actions["speechPreview"]["document"]
    preview_input = common.load(EVIDENCE_ROOT / "jobs/module-actions/typed-speech-preview-process-canary/input.json")
    require(preview_input["authorization"]["actionId"] == "typed-speech-preview-process-canary", "preview actionId authorization drift")
    require(preview_input["authorization"]["previewLanguage"] == preview_input["workerInput"]["previewLanguage"], "preview language authorization drift")
    require(preview_input["authorization"]["mode"] == "explicit-user-action" and preview_input["automaticRetryAllowed"] is False, "preview explicit/no-retry boundary drift")
    require(preview["previewAttemptCount"] == 1 and preview["audioPlayerInvocationCount"] == 1 and preview["sideEffectsPerformed"] == ["fake-offline-synthesis", "fake-offline-playback-contract"], "preview Fake/offline cardinality drift")
    require(process_receipt["typedPublicSettings"]["speechPreview"]["physicalSpeakerInvocationsDuringCanary"] == 0, "physical speaker was invoked during Core canary")
    rejected_root = EVIDENCE_ROOT / "jobs/module-actions/typed-speech-preview-rejected-process-canary"
    rejected_input = common.load(rejected_root / "input.json")
    require(rejected_input["authorization"]["actionId"] != "typed-speech-preview-rejected-process-canary", "preview rejection fixture no longer drifts")
    require(not (rejected_root / "receipt.json").exists() and not (rejected_root / "result.json").exists(), "authorization drift produced receipt/result")

    schema_receipt = common.load(PROJECT / "receipts/core-phase1/20260901T100127Z/schema-validation.receipt.json")
    require(schema_receipt["schemaErrors"] == schema_receipt["fixtureErrors"] == schema_receipt["runtimeErrors"] == 0, "schema validation receipt reports errors")
    frozen = previous.verify_frozen_v1()
    rows = common.inventory(EVIDENCE_ROOT)
    return {
        "schemaVersion": "knowledge-clipper-quality-core-v2.10.2-admission.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-CORE-V2.10.2-PREREQUISITE-DESKTOP-MIGRATION-PENDING",
        "candidateBound": False,
        "checkpoint": common.evidence(CHECKPOINT),
        "processReceipt": common.evidence(PROCESS_RECEIPT),
        "processLog": common.evidence(PROCESS_LOG),
        "fileBindings": bindings,
        "referencedEvidence": referenced,
        "runtimeEvidence": {"files": len(rows), "bytes": sum(row["bytes"] for row in rows), "treeSha256": common.tree_sha256(rows), "actions": {key: {k: v for k, v in value.items() if k != "document"} for key, value in actions.items()}},
        "frozenV1": frozen,
        "semantics": {
            "ipc": "1.7/orchestrator-exclusive",
            "remnoteProbe": "status-search-list-read/0-writes/sanitized/no-auto-retry",
            "speechVoices": "0-settings-mutations/0-speaker",
            "speechPreview": "explicit-actionId-language-time-bound/one-fake-offline-attempt/0-physical-speaker/no-auto-retry",
            "authorizationDrift": "fail-closed-before-worker/no-receipt/no-result",
        },
        "desktopBoundary": {"status": "PENDING", "migrationInput": checkpoint["desktopMigrationInput"]},
        "releaseDecision": checkpoint["releaseDecision"],
        "sideEffects": {"productionWrites": 0, "candidateRuns": 0, "externalCalls": 0, "physicalSpeakerInvocations": 0},
    }


def write(output_root: Path) -> dict[str, Any]:
    require(not output_root.exists(), f"append-only output exists: {output_root}")
    output_root.mkdir(parents=True)
    result = evaluate()
    receipt = output_root / "core-v2.10.2-quality-receipt.json"
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
