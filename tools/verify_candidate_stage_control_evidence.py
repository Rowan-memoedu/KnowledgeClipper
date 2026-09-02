from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


class VerificationError(RuntimeError):
    pass


def native(path: Path) -> Path:
    resolved = str(path.resolve())
    if resolved.startswith("\\\\?\\"):
        return Path(resolved)
    if resolved.startswith("\\\\"):
        return Path("\\\\?\\UNC\\" + resolved[2:])
    return Path("\\\\?\\" + resolved)


def load(path: Path) -> dict[str, Any]:
    return json.loads(native(path).read_text(encoding="utf-8"))


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with native(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise VerificationError(message)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--authority-runtime-gate", type=Path, required=True)
    parser.add_argument("--authority-runtime-gate-sha", required=True)
    args = parser.parse_args()

    root = args.evidence.resolve()
    output = args.output.resolve()
    require(root.is_dir(), "direct IPC evidence root is missing")
    require(not output.exists(), "append-only output already exists")
    require(
        args.authority_runtime_gate.is_file()
        and sha256(args.authority_runtime_gate) == args.authority_runtime_gate_sha.upper(),
        "authority runtime gate binding mismatch",
    )

    validation = load(root / "validate-registry.json")
    query = load(root / "task-query-response.v1.json")
    command = load(root / "task-command-response.v1.json")
    response_path = root / "stage-control-response.v1.1.json"
    response = load(response_path)
    payload = response.get("payload", {})
    resolved = payload.get("resolved", {})
    require(
        validation.get("status") == "valid"
        and validation.get("registryVersion") == "1.2.4"
        and validation.get("workers") == 5,
        "candidate embedded registry gate failed",
    )
    require(
        query.get("ok") is True
        and query.get("protocolVersion") == "1.0"
        and query.get("messageType") == "task-query-result",
        "protocol 1.0 task-query regression failed",
    )
    require(
        command.get("ok") is True
        and command.get("protocolVersion") == "1.0"
        and command.get("messageType") == "task-command-receipt"
        and command.get("payload", {}).get("accepted") is False
        and command.get("payload", {}).get("error", {}).get("code") == "contract.expected_state_mismatch",
        "protocol 1.0 task-command regression failed",
    )
    require(
        response.get("ok") is True
        and response.get("protocolVersion") == "1.1"
        and response.get("messageType") == "stage-control-receipt"
        and payload.get("schemaVersion") == "2.3.0"
        and payload.get("accepted") is True
        and payload.get("taskId") == "task-authority-stage-control-probe"
        and payload.get("operation") == "speech.playback"
        and payload.get("selector") == "current-active"
        and payload.get("reason") == "user_stop"
        and payload.get("status") == "requested"
        and resolved == {
            "runId": "run-probe",
            "stageId": "speech-playback",
            "attemptId": "attempt-probe",
            "operationId": "operation-probe",
            "cancelSignalRef": "runs/run-probe/stages/speech-playback/attempts/attempt-probe/cancel.json",
        },
        "protocol 1.1 stage-control receipt mismatch",
    )

    job = root / "jobs/task-authority-stage-control-probe"
    cancel_path = job / resolved["cancelSignalRef"]
    result_path = job / "runs/run-probe/stages/speech-playback/attempts/attempt-probe/result.json"
    run_cancel_path = job / "runs/run-probe/cancel-requested.json"
    event_path = job / payload["durableEventRef"]
    receipt_path = job / payload["receiptRef"]
    cancel = load(cancel_path)
    event = load(event_path)
    durable_receipt = load(receipt_path)
    require(
        cancel.get("schemaVersion") == "1.0.0"
        and cancel.get("attemptId") == "attempt-probe"
        and cancel.get("operationId") == "operation-probe"
        and cancel.get("reason") == "user_stop",
        "attempt-scoped worker cancel signal mismatch",
    )
    require(event.get("type") == "stage.control_requested", "durable stage control event is missing")
    require(durable_receipt.get("controlId") == payload.get("controlId"), "durable receipt identity mismatch")
    require(not native(result_path).exists(), "worker result unexpectedly exists")
    require(not native(run_cancel_path).exists(), "whole-run cancel-requested.json unexpectedly exists")
    require(native(root / "orchestrator.stderr.log").read_bytes() == b"", "candidate orchestrator emitted stderr")

    result = {
        "schemaVersion": "knowledge-clipper-desktop-candidate-stage-control-gate.v1",
        "generatedAt": datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z"),
        "status": "PASS",
        "candidateAuthorityRegistry": {"version": "1.2.4", "workers": 5},
        "protocol10": {"taskQuery": "PASS", "taskCommand": "PASS"},
        "protocol11": {
            "messageType": "stage-control-receipt",
            "receiptSchemaVersion": "2.3.0",
            "accepted": True,
            "stateClaim": "stop-requested",
            "resolved": resolved,
        },
        "durableEffects": {
            "attemptCancelSignal": cancel_path.relative_to(root).as_posix(),
            "stageControlEvent": event_path.relative_to(root).as_posix(),
            "stageControlReceipt": receipt_path.relative_to(root).as_posix(),
            "runCancelRequestedWritten": False,
            "workerResultWritten": False,
        },
        "stageControlResponse": {
            "path": response_path.relative_to(root).as_posix(),
            "sha256": sha256(response_path),
        },
        "authorityRuntimeGate": {
            "path": str(args.authority_runtime_gate.resolve()),
            "sha256": args.authority_runtime_gate_sha.upper(),
        },
        "workerRunInvocations": 0,
        "physicalSpeakerInvocations": 0,
        "externalWrites": 0,
        "postProbeDependencyDriftCheck": "not-claimed; authoritative v1.2.4 runtime gate is hash-bound above",
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": "PASS", "output": str(output), "sha256": sha256(output)}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (VerificationError, OSError, ValueError, KeyError, json.JSONDecodeError) as exc:
        print(json.dumps({"status": "FAIL", "error": str(exc)}, ensure_ascii=False))
        raise SystemExit(1)
