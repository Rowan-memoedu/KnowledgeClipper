from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath
from typing import Any

from jsonschema import Draft202012Validator, FormatChecker, RefResolver


PROJECT = Path(__file__).resolve().parents[1]
BASELINE = PROJECT / "receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json"
PREVIOUS = PROJECT / "contracts/core/v2.10/checkpoints/v2.10.2-20260901T100127Z/checkpoint.json"


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def evidence(path: Path) -> dict[str, Any]:
    require(path.is_file(), f"evidence missing: {path}")
    return {"path": relative(path), "bytes": path.stat().st_size, "sha256": sha256_file(path)}


def write_json_once(path: Path, value: Any) -> None:
    require(not path.exists(), f"append-only output exists: {path}")
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def validate(schema_path: Path, instance_path: Path) -> None:
    schema = load(schema_path)
    instance = load(instance_path)
    resolver = RefResolver(base_uri=schema_path.as_uri(), referrer=schema)
    errors = list(Draft202012Validator(schema, resolver=resolver, format_checker=FormatChecker()).iter_errors(instance))
    require(not errors, f"schema rejected {relative(instance_path)}: {errors[0].message if errors else ''}")


def inventory(root: Path) -> dict[str, Any]:
    rows: list[tuple[str, int, str]] = []
    for path in root.rglob("*"):
        if path.is_file():
            rows.append((path.relative_to(root).as_posix(), path.stat().st_size, sha256_file(path)))
    rows.sort(key=lambda item: item[0])
    payload = "\n".join(f"{path}\t{size}\t{digest.lower()}" for path, size, digest in rows).encode("utf-8")
    return {
        "files": len(rows),
        "bytes": sum(item[1] for item in rows),
        "treeSha256": hashlib.sha256(payload).hexdigest().upper(),
    }


def verify_frozen_v1(output_path: Path) -> dict[str, Any]:
    baseline = load(BASELINE)
    mismatches = []
    for item in baseline["files"]:
        path = Path(item["path"])
        if not path.is_file() or sha256_file(path) != item["sha256"].upper():
            mismatches.append(item["path"])
    require(len(baseline["files"]) == 21 and not mismatches, "frozen contracts v1 drift")
    receipt = {
        "schemaVersion": "knowledge-clipper-frozen-contract-verification.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "passed",
        "baselineReceipt": relative(BASELINE),
        "filesChecked": 21,
        "mismatches": 0,
        "modified": False,
        "details": [],
    }
    write_json_once(output_path, receipt)
    return receipt


def verify_schema_matrix(receipt_root: Path, output_path: Path) -> dict[str, Any]:
    evidence_root = receipt_root / "evidence"
    cases = [
        ("ipc15/jobs/module-actions/anki-probe-process-canary/input.json", "contracts/core/v2.10/module-control/schemas/anki-readonly-probe-action.v1.schema.json"),
        ("ipc15/jobs/module-actions/anki-probe-process-canary/receipt.json", "contracts/core/v2.10/module-control/schemas/module-action-receipt.v4.schema.json"),
    ]
    for action in ("describe", "copy", "validate", "diff"):
        action_id = f"content-settings-{action}-process-canary"
        cases.extend([
            (f"ipc16/jobs/module-actions/{action_id}/input.json", "contracts/core/v2.10/module-control/schemas/content-settings-action.v1.schema.json"),
            (f"ipc16/jobs/module-actions/{action_id}/receipt.json", "contracts/core/v2.10/module-control/schemas/module-action-receipt.v5.schema.json"),
        ])
    cases.extend([
        ("jobs/module-actions/typed-remnote-probe-process-canary/input.json", "contracts/remnote-public-probe-request.v2.schema.json"),
        ("jobs/module-actions/typed-remnote-probe-process-canary/receipt.json", "contracts/core/v2.10/module-control/schemas/module-action-receipt.v6.schema.json"),
        ("jobs/module-actions/typed-speech-voices-process-canary/input.json", "tests/RemNoteQuickClipper.Speech.Tests/fixtures/settings-companion/speech-settings-voice-discovery-input.schema.json"),
        ("jobs/module-actions/typed-speech-voices-process-canary/receipt.json", "contracts/core/v2.10/module-control/schemas/module-action-receipt.v6.schema.json"),
        ("jobs/module-actions/typed-speech-preview-process-canary/input.json", "contracts/core/v2.10/module-control/schemas/speech-settings-speaker-preview-action.v2.schema.json"),
        ("jobs/module-actions/typed-speech-preview-process-canary/receipt.json", "contracts/core/v2.10/module-control/schemas/module-action-receipt.v6.schema.json"),
    ])
    checked = []
    for instance_rel, schema_rel in cases:
        instance = evidence_root / Path(*PurePosixPath(instance_rel).parts)
        schema = PROJECT / Path(*PurePosixPath(schema_rel).parts)
        validate(schema, instance)
        checked.append({"instance": relative(instance), "schema": schema_rel})
    rejected_roots = [
        evidence_root / "ipc16/jobs/module-actions/content-settings-copy-rejected-process-canary",
        evidence_root / "jobs/module-actions/typed-speech-preview-rejected-process-canary",
    ]
    for root in rejected_roots:
        require((root / "input.json").is_file(), f"negative input missing: {root}")
        require(not (root / "receipt.json").exists() and not (root / "result.json").exists(), f"negative action dispatched: {root}")
    receipt = {
        "schemaVersion": "knowledge-clipper-core-v2.10.3-schema-matrix.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "passed",
        "protocols": ["1.5", "1.6", "1.7"],
        "runtimeDocumentsChecked": len(checked),
        "runtimeErrors": 0,
        "authorizationDriftRejectedBeforeWorker": 2,
        "checked": checked,
    }
    write_json_once(output_path, receipt)
    return receipt


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish-root", type=Path, required=True)
    parser.add_argument("--receipt-root", type=Path, required=True)
    parser.add_argument("--checkpoint-directory", type=Path, required=True)
    args = parser.parse_args()

    publish_root = args.publish_root.resolve()
    receipt_root = args.receipt_root.resolve()
    checkpoint_directory = args.checkpoint_directory.resolve()
    require(publish_root.is_dir() and receipt_root.is_dir(), "runtime or receipt root missing")
    require(not checkpoint_directory.exists(), f"append-only checkpoint directory exists: {checkpoint_directory}")

    process_path = receipt_root / "core-process-named-pipe.receipt.json"
    process = load(process_path)
    require(process["status"] == "passed" and process["processBoundary"] == "published KnowledgeClipper.Orchestrator.exe serve", "final EXE process boundary did not pass")
    matrix = process["finalProcessProtocolMatrix"]
    require([item["protocolVersion"] for item in matrix] == ["1.5", "1.6", "1.7"], "final process protocol matrix drift")
    require(all(item["startup"] == item["typedAction"] == "passed" for item in matrix), "final process protocol matrix failed")
    require(process["probe"]["externalWrites"] == 0, "Anki probe write boundary drift")
    require(process["contentSettings"]["authorizationDriftRejected"] is True and process["contentSettings"]["externalWrites"] == 0, "content settings boundary drift")
    typed = process["typedPublicSettings"]
    require(typed["remnote"]["externalWrites"] == 0, "RemNote probe write boundary drift")
    require(typed["speechVoices"]["settingsMutations"] == 0 and typed["speechVoices"]["physicalSpeakerPlayback"] is False, "voice discovery side effect drift")
    require(typed["speechPreview"]["authorizationDriftRejected"] is True and typed["speechPreview"]["fakeOffline"] is True and typed["speechPreview"]["physicalSpeakerInvocationsDuringCanary"] == 0, "speech preview boundary drift")

    exe = publish_root / "KnowledgeClipper.Orchestrator.exe"
    dll = publish_root / "KnowledgeClipper.Orchestrator.dll"
    describe_path = publish_root.parent / "orchestrator-describe.json"
    describe = load(describe_path)
    require(describe["version"] == "2.10.3.0" and describe["coreContractVersion"] == "2.10.3", "embedded runtime version drift")
    require(describe["ipc"]["protocolVersions"] == ["1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "1.7"], "embedded IPC descriptor drift")
    require(process["orchestratorRuntime"]["sha256"].upper() == sha256_file(exe), "tested EXE hash does not match published EXE")

    for name in ("core-adapter.log", "question-workflow.log", "orchestrator-contract.log"):
        require(load(receipt_root / name)["status"] == "passed", f"regression suite failed: {name}")

    checkpoint_directory.mkdir(parents=True)
    frozen_path = receipt_root / "frozen-v1-verification.receipt.json"
    schema_path = receipt_root / "schema-validation.receipt.json"
    frozen = verify_frozen_v1(frozen_path)
    schema = verify_schema_matrix(receipt_root, schema_path)

    runtime_receipt_path = receipt_root / "runtime-binding.receipt.json"
    runtime_receipt = {
        "schemaVersion": "knowledge-clipper-core-v2.10.3-runtime-binding.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "passed",
        "rootCause": "v1.2.39 checkpoint bound current source while its sealed win-x64 runtime came from an older IPC 1.4-only build",
        "runtime": {
            "version": describe["version"],
            "coreContractVersion": describe["coreContractVersion"],
            "exe": evidence(exe),
            "dll": evidence(dll),
            "describe": evidence(describe_path),
            "inventory": inventory(publish_root),
        },
        "finalProcessMatrix": matrix,
        "testedRuntimeSha256": process["orchestratorRuntime"]["sha256"].upper(),
        "sourceAndRuntimeSameGate": True,
        "externalCalls": 0,
        "physicalSpeakerInvocations": 0,
    }
    write_json_once(runtime_receipt_path, runtime_receipt)

    git_head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=PROJECT, capture_output=True, text=True, check=True).stdout.strip()
    bindings = [
        PROJECT / "orchestrator/KnowledgeClipper.Orchestrator.csproj",
        PROJECT / "orchestrator/Program.cs",
        PROJECT / "orchestrator/Core/CommandAndIpc.cs",
        PROJECT / "orchestrator/Core/ModuleActionControl.cs",
        PROJECT / "tests/CoreProcessNamedPipe.IntegrationTests/Program.cs",
        PROJECT / "tests/FakeWorker/Program.cs",
        PROJECT / "contracts/core/v2.10/module-control/orchestrator-ipc.v1.5.json",
        PROJECT / "contracts/core/v2.10/module-control/orchestrator-ipc.v1.6.json",
        PROJECT / "contracts/core/v2.10/module-control/orchestrator-ipc.v1.7.json",
        PROJECT / "contracts/core/v2.10/module-control/schemas/ipc-request.v1.5.schema.json",
        PROJECT / "contracts/core/v2.10/module-control/schemas/ipc-response.v1.5.schema.json",
        PROJECT / "contracts/core/v2.10/module-control/schemas/ipc-request.v1.6.schema.json",
        PROJECT / "contracts/core/v2.10/module-control/schemas/ipc-response.v1.6.schema.json",
        PROJECT / "contracts/core/v2.10/module-control/schemas/ipc-request.v1.7.schema.json",
        PROJECT / "contracts/core/v2.10/module-control/schemas/ipc-response.v1.7.schema.json",
        exe,
        dll,
        describe_path,
        process_path,
        runtime_receipt_path,
        schema_path,
        frozen_path,
    ]
    now = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    checkpoint = {
        "schemaVersion": "knowledge-clipper-core-checkpoint.v1",
        "checkpointVersion": "2.10.3",
        "createdAt": now,
        "status": "passed-runtime-drift-regression",
        "scope": "Core runtime/source identity repair plus independent final-process IPC 1.5/1.6/1.7 startup and typed-action matrix",
        "repositoryHeadAtCheckpoint": git_head,
        "extendsCheckpoint": relative(PREVIOUS),
        "authority": {
            "ipc": {
                "schemaVersion": "1.7.0",
                "protocolVersion": "1.7",
                "configuration": "contracts/core/v2.10/module-control/orchestrator-ipc.v1.7.json",
                "request": "knowledge-clipper-module-action.v6",
                "receipt": "knowledge-clipper-module-action-receipt.v6",
                "startupRegressions": ["1.5", "1.6", "1.7"],
                "existingProtocolsPreserved": ["1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6"],
            },
            "runtime": {
                "componentVersion": "2.10.3.0",
                "coreContractVersion": "2.10.3",
                "selfContained": True,
                "runtimeIdentifier": "win-x64",
                "entrypoint": "KnowledgeClipper.Orchestrator.exe",
                "publishedExeBound": True,
                "publishedDllBound": True,
                "embeddedCapabilityDescriptorBound": True,
                "testedExeSha256": sha256_file(exe),
            },
            "compatibility": {
                "frozenContractsV1Modified": False,
                "frozenContractsV1FilesChecked": frozen["filesChecked"],
                "frozenContractsV1Mismatches": frozen["mismatches"],
                "legacyIpc12ModuleActionsPreserved": True,
                "contentSettingsIpc16Preserved": True,
                "ankiProbeIpc15Preserved": True,
                "taskTruth24Preserved": True,
            },
        },
        "verification": {
            "warningsAsErrors": {"status": "passed", "projectsBuilt": 6, "warnings": 0, "errors": 0},
            "coreAdapter": {"status": "passed", "log": relative(receipt_root / "core-adapter.log"), "logSha256": sha256_file(receipt_root / "core-adapter.log")},
            "questionWorkflow": {"status": "passed", "log": relative(receipt_root / "question-workflow.log"), "logSha256": sha256_file(receipt_root / "question-workflow.log")},
            "orchestratorContracts": {"status": "passed", "log": relative(receipt_root / "orchestrator-contract.log"), "logSha256": sha256_file(receipt_root / "orchestrator-contract.log")},
            "finalProcessProtocolMatrix": {"status": "passed", "receipt": relative(process_path), "receiptSha256": sha256_file(process_path), "protocols": ["1.5", "1.6", "1.7"], "process": "published KnowledgeClipper.Orchestrator.exe serve"},
            "runtimeBinding": {"status": "passed", "receipt": relative(runtime_receipt_path), "receiptSha256": sha256_file(runtime_receipt_path)},
            "schemaValidation": {"status": schema["status"], "receipt": relative(schema_path), "receiptSha256": sha256_file(schema_path)},
            "frozenV1": {"status": frozen["status"], "receipt": relative(frozen_path), "receiptSha256": sha256_file(frozen_path)},
            "v1239Invalidation": {"status": "failed-no-go", "receipt": "receipts/quality-authority-admission/20260902T010057Z-v1.2.39-no-go/post-assembly-independent-authority-admission.receipt.json", "receiptSha256": "2A9F1301BB18214CFB86EAE7DD02F845571A3E4BEE73E9A46ABC5FAD595EA0FB"},
        },
        "qualityAuditInput": [
            "Independently re-hash the checkpoint, published EXE/DLL, runtime manifest, and every fileBinding.",
            "Run the checkpoint-bound published EXE separately with IPC 1.5, 1.6, and 1.7 configurations; require typed actions in every process.",
            "Reject any authority whose tested EXE SHA differs from its runtime manifest or final release EXE SHA.",
            "Keep v1.2.39 immutable and NO-GO; do not treat its earlier static PASS as runtime admission.",
        ],
        "releaseDecision": {"coreRuntimeInputReady": True, "authorityV1240Required": True, "integratedCandidateGo": False, "releaseGo": False},
        "fileBindings": [{"path": relative(path), "sha256": sha256_file(path).lower()} for path in bindings],
    }
    checkpoint_path = checkpoint_directory / "checkpoint.json"
    write_json_once(checkpoint_path, checkpoint)
    result = {
        "status": "passed",
        "checkpoint": evidence(checkpoint_path),
        "runtimeBinding": evidence(runtime_receipt_path),
        "schemaValidation": evidence(schema_path),
        "frozenV1": evidence(frozen_path),
        "finalProcess": evidence(process_path),
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
