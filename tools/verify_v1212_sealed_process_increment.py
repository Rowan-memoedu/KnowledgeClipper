from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator

import verify_v1212_external_phase_b as common


PROJECT_ROOT = Path(__file__).resolve().parents[1]
CANDIDATE_ROOT = PROJECT_ROOT / "releases" / "kc-integrated-settings-v1212-20260830T183500Z" / "win-x64"
FIXTURE_ROOT = PROJECT_ROOT / "tests" / "Quality.Gates" / "fixtures" / "v1212-sealed-process-increment"
FIXTURE_SCHEMA = FIXTURE_ROOT / "v1212-sealed-process-increment.schema.json"
DEFAULT_FIXTURE = FIXTURE_ROOT / "valid" / "sealed-process.json"
AUTHORIZED_DECK = "零碎知识剪藏::Canary::PhaseB-20260828T081109Z"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise common.GateFailure(message)


def upper(value: Any) -> str:
    return str(value).upper()


def normal_path(path: Path) -> Path:
    raw = str(path)
    return Path(raw[4:]) if raw.startswith("\\\\?\\") else path


def walk_files(root: Path) -> list[Path]:
    return [normal_path(path) for path in common.io_path(root).rglob("*") if common.io_path(normal_path(path)).is_file()]


def validate_fixtures() -> dict[str, Any]:
    schema = common.load_json(FIXTURE_SCHEMA)
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema)
    valid = sorted((FIXTURE_ROOT / "valid").glob("*.json"))
    invalid = sorted((FIXTURE_ROOT / "invalid").glob("*.json"))
    require(valid and invalid, "sealed process fixture set incomplete")
    for path in valid:
        require(not list(validator.iter_errors(common.load_json(path))), f"valid fixture rejected: {path.name}")
    for path in invalid:
        require(bool(list(validator.iter_errors(common.load_json(path)))), f"invalid fixture accepted: {path.name}")
    return {"status": "PASS", "validAccepted": len(valid), "invalidRejected": len(invalid), "schema": common.evidence(FIXTURE_SCHEMA)}


def inventory(root: Path) -> dict[str, Any]:
    files = sorted(walk_files(root), key=lambda path: path.relative_to(root).as_posix())
    entries: list[dict[str, Any]] = []
    lines: list[str] = []
    for path in files:
        relative = path.relative_to(root).as_posix()
        size = common.io_path(path).stat().st_size
        digest = common.sha256(path)
        entries.append({"path": relative, "bytes": size, "sha256": digest})
        lines.append(f"{relative}\t{size}\t{digest.lower()}")
    tree = hashlib.sha256("\n".join(lines).encode("utf-8")).hexdigest().upper()
    return {"fileCount": len(entries), "totalBytes": sum(item["bytes"] for item in entries), "treeSha256": tree, "files": entries}


def verify_candidate(fixture: dict[str, Any]) -> dict[str, Any]:
    base_path = common.resolve_ref(fixture["baseCandidateReceipt"], "base candidate receipt")
    base = common.load_json(base_path)
    candidate = fixture["candidate"]
    authority = fixture["authority"]
    require(base["candidateBound"] is True and upper(base["candidateHash"]) == candidate["manifestSha256"], "base candidate hash mismatch")
    require(upper(base["desktop"]["fullInventory"]["treeSha256"]) == candidate["sealedTreeSha256"], "base sealed tree mismatch")
    manifest = CANDIDATE_ROOT / "candidate-file-manifest.v1.json"
    registry = CANDIDATE_ROOT / "authority" / "metadata" / "worker-registry.v1.json"
    checkpoint = CANDIDATE_ROOT / "authority" / "metadata" / "checkpoint.json"
    require(common.sha256(manifest) == candidate["manifestSha256"], "candidate manifest drifted")
    require(common.sha256(registry) == authority["registrySha256"], "candidate registry drifted")
    require(common.sha256(checkpoint) == authority["checkpointSha256"], "candidate checkpoint drifted")
    manifest_json = common.load_json(manifest)
    require(manifest_json["candidateId"] == candidate["candidateId"] and manifest_json["authority"]["registryVersion"] == authority["registryVersion"], "candidate identity mismatch")
    registry_json = common.load_json(registry)
    workers = {item["workerId"]: item for item in registry_json["workers"]}
    bound: dict[str, Any] = {}
    for worker_id, version in (("content.deepseek", "0.7.0"), ("export.anki", "1.2.1")):
        entry = workers[worker_id]
        path = (CANDIDATE_ROOT / "authority" / "win-x64" / entry["entrypoint"]["path"]).resolve()
        require(path.is_relative_to(CANDIDATE_ROOT) and common.file_exists(path), f"{worker_id}: candidate entrypoint missing")
        bound[worker_id] = {"versionObservedInResults": version, "entrypoint": common.evidence(path)}
    orchestrator = CANDIDATE_ROOT / "KnowledgeClipper.Orchestrator.dll"
    return {
        "status": "PASS-EXACT-T183500Z-AUTHORITY-V1.2.12",
        "baseReceipt": common.evidence(base_path),
        "manifest": common.evidence(manifest),
        "registry": common.evidence(registry),
        "checkpoint": common.evidence(checkpoint),
        "orchestratorAssembly": common.evidence(orchestrator),
        "workers": bound,
    }


def load_ndjson(path: Path) -> list[dict[str, Any]]:
    return [json.loads(line) for line in common.io_path(path).read_text(encoding="utf-8-sig").splitlines() if line.strip()]


def verify_artifact(job_root: Path, ref: dict[str, Any], label: str) -> dict[str, Any]:
    path = (job_root / ref["manifestPath"]).resolve()
    require(path.is_relative_to(job_root) and common.file_exists(path), f"{label}: artifact manifest missing")
    require(common.sha256(path) == upper(ref["expectedSha256"]), f"{label}: artifact hash mismatch")
    return common.evidence(path)


def verify_worker_result(job_root: Path, result_path: Path, worker_id: str, version: str) -> dict[str, Any]:
    result = common.load_json(result_path)
    request_path = result_path.parent / "request.json"
    request = common.load_json(request_path)
    require(result["status"] == "completed", f"{worker_id}: terminal result mismatch")
    require(result["worker"] == {"id": worker_id, "version": version}, f"{worker_id}: result worker mismatch")
    require(request["workerId"] == worker_id, f"{worker_id}: request worker mismatch")
    require(request["attemptId"] == result["attemptId"] and request["operationId"] == result["operationId"], f"{worker_id}: request/result identity mismatch")
    require(request["stageId"] == result["stageId"], f"{worker_id}: stage mismatch")
    inputs = [verify_artifact(job_root, ref, f"{worker_id} input") for ref in request.get("inputRefs", [])]
    outputs = [verify_artifact(job_root, ref, f"{worker_id} output") for ref in result.get("outputs", [])]
    return {"request": common.evidence(request_path), "result": common.evidence(result_path), "inputs": inputs, "outputs": outputs, "attemptId": result["attemptId"], "workerId": worker_id, "workerVersion": version}


def verify_events(run_root: Path, expected_final: str) -> dict[str, Any]:
    paths = sorted((run_root / "events").glob("*.json"))
    values = [common.load_json(path) for path in paths]
    require([item["sequence"] for item in values] == list(range(1, len(values) + 1)), "event sequence is not contiguous")
    require(values[0]["type"] == "command.accepted" and values[-1]["type"] == "command.execution_completed", "event boundary mismatch")
    require(values[-1]["payload"]["finalState"] == expected_final, "event terminal state mismatch")
    return {"count": len(values), "first": common.evidence(paths[0]), "last": common.evidence(paths[-1]), "terminalState": expected_final}


def find_one(root: Path, name: str) -> Path:
    paths = [path for path in walk_files(root) if path.name == name]
    require(len(paths) == 1, f"expected one {name}, found {len(paths)} under {root}")
    return paths[0]


def verify_completed_task(evidence_root: Path, task_name: str, note_id: int, expected_question: str, expected_answer: str, *, ocr: bool) -> dict[str, Any]:
    job_root = evidence_root / "jobs" / task_name
    manifest_path = job_root / "task.manifest.v1.json"
    manifest = common.load_json(manifest_path)
    require(manifest["taskId"] == task_name and manifest["intent"] == "clip.card" and manifest["executionProfile"] == "cards-anki", f"{task_name}: manifest mismatch")
    require(manifest["metadata"]["authorizedAnkiDeck"] == AUTHORIZED_DECK, f"{task_name}: deck escaped isolation")
    for ref in manifest["inputRefs"]:
        artifact = job_root / "artifacts" / ref["artifactRef"] / "artifact.json"
        require(common.file_exists(artifact) and common.sha256(artifact) == upper(ref["expectedSha256"]), f"{task_name}: manifest input hash mismatch")
    run_root = next((job_root / "runs").iterdir())
    plan_path = job_root / "plans" / "resolved" / run_root.name / "pipeline.v2.json"
    plan = common.load_json(plan_path)
    require([(item["workerId"], item["stageId"]) for item in plan["stages"]] == [("content.deepseek", "content.understand-and-card"), ("export.anki", "anki-export")], f"{task_name}: resolved stage set mismatch")
    require(all(item["workerId"] != "image.ocr.mineru" for item in plan["stages"]), f"{task_name}: OCR provider was rerun")
    content_result = find_one(run_root / "stages" / "content.understand-and-card", "result.json")
    anki_result = find_one(run_root / "stages" / "anki-export", "result.json")
    attempts = [verify_worker_result(job_root, content_result, "content.deepseek", "0.7.0"), verify_worker_result(job_root, anki_result, "export.anki", "1.2.1")]
    adapter = find_one(run_root / "adapters" / "anki-export", "card-bundle.v1.json")
    card_bundle = common.load_json(adapter)
    require(len(card_bundle["cards"]) == 1, f"{task_name}: expected one card")
    card = card_bundle["cards"][0]
    require(card["fields"] == {"question": expected_question, "answer": expected_answer}, f"{task_name}: card content mismatch")
    operations_path = find_one(run_root / "stages" / "anki-export", "operations.ndjson")
    operations = load_ndjson(operations_path)
    require(all(item["status"] == "SUCCEEDED" for item in operations), f"{task_name}: Anki operation failed")
    actions = [item["action"] for item in operations]
    require(actions.count("addNote") == actions.count("notesInfo") == actions.count("cardsInfo") == 1, f"{task_name}: Anki write/readback action count mismatch")
    items_path = find_one(run_root / "stages" / "anki-export", "items.ndjson")
    items = load_ndjson(items_path)
    require(len(items) == 1 and items[0]["note_id"] == note_id and items[0]["card_id"] == card["card_id"] and items[0]["status"] == "CREATED", f"{task_name}: note/card identity mismatch")
    receipt_path = find_one(run_root / "stages" / "anki-export", "receipt.json")
    receipt = common.load_json(receipt_path)
    require(receipt["status"] == "SUCCEEDED" and receipt["counts"] == {"already_current": 0, "blocked_upstream": 0, "failed": 0, "succeeded": 1}, f"{task_name}: Anki receipt mismatch")
    events = verify_events(run_root, "succeeded")
    ocr_gap = None
    if ocr:
        handoff_path = job_root / "artifacts" / "capture-bundle" / "ocr-consumer-handoff.json"
        handoff = common.load_json(handoff_path)
        material_path = job_root / "artifacts" / "capture-bundle" / "ocr-content.md"
        material = common.io_path(material_path).read_text(encoding="utf-8-sig").strip()
        adapted_material = common.io_path(find_one(run_root / "adapters" / "content.understand-and-card", "material.md")).read_text(encoding="utf-8-sig").strip()
        require(handoff["status"] == "ready-for-content-consumer" and handoff["worker"] == {"id": "image.ocr.mineru", "version": "1.2.2"}, "OCR handoff identity mismatch")
        require(material == adapted_material == handoff["markdown"]["text"], "OCR handoff/adapted material drift")
        require("SYNTHETIC OCR CANAR " in material and "SYNTHETIC OCR CANARY " not in material, "OCR correction gap was hidden or changed")
        ocr_gap = {
            "status": "PRODUCT-GAP-OCR-CORRECTION-UNMET",
            "observed": "KNOWLEDGECLIPPER SYNTHETIC OCR CANAR",
            "expected": "KNOWLEDGECLIPPER SYNTHETIC OCR CANARY",
            "handoff": common.evidence(handoff_path),
            "material": common.evidence(material_path),
            "providerRerun": False,
        }
    return {
        "status": "PROCESS-COMPLETED",
        "taskId": task_name,
        "manifest": common.evidence(manifest_path),
        "plan": common.evidence(plan_path),
        "events": events,
        "attempts": attempts,
        "card": {"cardId": card["card_id"], "noteId": note_id, "question": expected_question, "answer": expected_answer, "bundle": common.evidence(adapter)},
        "ankiEvidence": {"operations": common.evidence(operations_path), "items": common.evidence(items_path), "receipt": common.evidence(receipt_path), "notesInfo": "SUCCEEDED", "cardsInfo": "SUCCEEDED"},
        "ocrCorrection": ocr_gap,
    }


def verify_ipc(root: Path, tasks: dict[str, Any]) -> dict[str, Any]:
    ipc_root = root / "ipc"
    requests = sorted(ipc_root.glob("*.request.json"))
    responses = sorted(ipc_root.glob("*.response.json"))
    require(len(requests) == len(responses) == 26, "IPC request/response count mismatch")
    pairs: list[dict[str, Any]] = []
    for request_path, response_path in zip(requests, responses, strict=True):
        require(request_path.name.split(".")[0] == response_path.name.split(".")[0], "IPC pair number mismatch")
        request = common.load_json(request_path)
        response = common.load_json(response_path)
        require(response["ok"] is True and request["correlationId"] == response["correlationId"], "IPC correlation/ok mismatch")
        pairs.append({"request": common.evidence(request_path), "response": common.evidence(response_path), "messageType": request["messageType"]})
    commands = {number: (common.load_json(ipc_root / f"{number}-task-command.request.json"), common.load_json(ipc_root / f"{number}-task-command.response.json")) for number in ("0002", "0012", "0021")}
    for request, response in commands.values():
        require(request["payload"]["type"] == "task.submit" and response["payload"]["accepted"] is True, "IPC task command not accepted")
        task_id = response["payload"]["taskId"]
        event_path = root / "jobs" / task_id / response["payload"]["durableEventRef"]
        require(common.file_exists(event_path), "IPC durable event missing")
    finals = {"process-text": "0011", "process-ocr-handoff": "0020", "process-question": "0026"}
    states: dict[str, str] = {}
    for name, number in finals.items():
        response = common.load_json(ipc_root / f"{number}-task-query.response.json")
        item = response["payload"]["items"][0]
        states[name] = item["status"]
    require(states == {"process-text": "succeeded", "process-ocr-handoff": "succeeded", "process-question": "waiting_input"}, "IPC final task states mismatch")
    return {"status": "PASS", "requestCount": 26, "responseCount": 26, "pairs": pairs, "finalStates": states}


def verify_question(root: Path) -> dict[str, Any]:
    job_root = root / "jobs" / "process-question-20260830092626858"
    run_root = next((job_root / "runs").iterdir())
    events = verify_events(run_root, "waiting_input")
    result_path = find_one(run_root / "stages" / "question-answer", "result.json")
    attempt = verify_worker_result(job_root, result_path, "content.deepseek", "0.7.0")
    answer_path = find_one(run_root / "stages" / "question-answer", "question-answer.schema-v1.json")
    answer = common.load_json(answer_path)
    require(answer["phase"] == "answer" and len(answer["turns"]) == 1, "question initial turn mismatch")
    require(not list(run_root.rglob("*make-cards*")), "question make-cards ran prematurely")
    return {
        "status": "PENDING-WAITING_INPUT",
        "initialAnswer": "COMPLETED",
        "followUp": "PENDING",
        "makeCards": "PENDING",
        "turnCount": 1,
        "answer": common.evidence(answer_path),
        "attempt": attempt,
        "events": events,
        "fullChainPassed": False,
    }


def current_process_snapshot() -> dict[str, Any]:
    command = "@(Get-Process -ErrorAction SilentlyContinue | Where-Object {$_.ProcessName -like 'KnowledgeClipper*'} | Select-Object Id,ProcessName,StartTime,Path) | ConvertTo-Json -Compress"
    completed = subprocess.run(["powershell", "-NoProfile", "-Command", command], capture_output=True, text=True, timeout=15, check=True)
    raw = completed.stdout.strip()
    values = [] if raw in ("", "null") else json.loads(raw)
    if isinstance(values, dict):
        values = [values]
    normalized = [{"id": item["Id"], "processName": item["ProcessName"], "startTime": str(item["StartTime"]), "path": item["Path"]} for item in values]
    target = [item for item in normalized if item["path"] and Path(item["path"]).resolve().is_relative_to(CANDIDATE_ROOT.resolve())]
    require(not target, "T183500Z candidate process remains during quality snapshot")
    return {
        "observedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "targetCandidateProcessCount": 0,
        "otherKnowledgeClipperProcesses": normalized,
        "otherProcessCount": len(normalized),
        "classification": "no T183500Z residual; other processes are not attributed to this evidence run",
        "action": "read-only; no process terminated",
    }


def evaluate(fixture: dict[str, Any], *, observe_processes: bool = False) -> dict[str, Any]:
    require(fixture["policy"] == {"externalRerun": False, "ocrCorrection": "product-gap", "questionWorkflow": "waiting_input"}, "sealed process policy weakened")
    root = common.inside_project(fixture["evidenceRoot"], "sealed process evidence root", directory=True)
    raw_inventory = inventory(root)
    candidate = verify_candidate(fixture)
    stderr = root / "orchestrator.stderr.log"
    stdout = root / "orchestrator.stdout.log"
    require(common.io_path(stderr).stat().st_size == 0, "orchestrator stderr is not empty")
    runtime = common.load_json(root / "orchestrator-ipc.runtime.v1.1.json")
    require(runtime["schemaVersion"] == "1.1.0" and runtime["transport"] == "windows-named-pipe" and runtime["serverOwnership"] == "orchestrator-exclusive", "IPC runtime contract mismatch")
    preflight = common.load_json(root / "preflight.json")
    require(preflight["status"] == "passed" and preflight["deepSeekCredential"]["secretPersisted"] is False, "sealed process preflight mismatch")
    text = verify_completed_task(
        root,
        "process-text-20260830092626858",
        fixture["expectedNotes"]["process-text"],
        "在可审计系统中，重试操作时旧证据会怎样处理？",
        "在可审计系统中，每次处理都从不可变输入建立新的运行记录；重试不会覆盖旧证据，而是新增 attempt 并保留全部哈希与状态。",
        ocr=False,
    )
    ocr = verify_completed_task(
        root,
        "process-ocr-handoff-20260830092626858",
        fixture["expectedNotes"]["process-ocr-handoff"],
        "What is 19 + 23?",
        "42",
        ocr=True,
    )
    question = verify_question(root)
    ipc = verify_ipc(root, {"text": text, "ocr": ocr, "question": question})
    process_snapshot = current_process_snapshot() if observe_processes else {"status": "not-observed-during-unit-evaluation"}
    return {
        "schemaVersion": "knowledge-clipper-v1212-sealed-process-external-increment-quality.v1",
        "generatedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "CONDITIONAL-SEALED-PROCESS-PASS-WITH-OCR-CORRECTION-GAP-QUESTION-PENDING",
        "candidateBound": True,
        "candidateHash": fixture["candidate"]["manifestSha256"],
        "candidate": fixture["candidate"],
        "authority": fixture["authority"],
        "candidateVerification": candidate,
        "evidenceRoot": {"path": root.relative_to(PROJECT_ROOT).as_posix(), **raw_inventory},
        "process": {"stdout": common.evidence(stdout), "stderr": common.evidence(stderr), "ipcRuntime": common.evidence(root / "orchestrator-ipc.runtime.v1.1.json"), "preflight": common.evidence(root / "preflight.json"), "postRunSnapshot": process_snapshot},
        "ipc": ipc,
        "tasks": {"process-text": text, "process-ocr-handoff": ocr, "process-question": question},
        "productGaps": [ocr["ocrCorrection"]],
        "classification": {
            "sealedCandidateOrchestratorProcess": True,
            "namedPipe": True,
            "externalServicesRerunByQuality": False,
            "ocrProviderRerunInEvidence": False,
            "completedDoesNotImplyOcrCorrectionQuality": True,
        },
        "existingPassedTextAndManualUi": {"status": "UNCHANGED-NON-BLOCKED"},
        "noAdditionalGateIntroduced": True,
    }


def render_readable(result: dict[str, Any]) -> str:
    text = result["tasks"]["process-text"]["card"]
    ocr = result["tasks"]["process-ocr-handoff"]["card"]
    return (
        "# v1.2.12 sealed Orchestrator process increment\n\n"
        f"- status: {result['status']}\n"
        f"- candidateHash: `{result['candidateHash']}`\n"
        f"- evidence: {result['evidenceRoot']['fileCount']} files / {result['evidenceRoot']['totalBytes']} bytes / tree `{result['evidenceRoot']['treeSha256']}`\n"
        "- Quality external rerun: 0\n\n"
        "## process-text\n\n"
        f"- Anki noteId: `{text['noteId']}`\n- front: {text['question']}\n- back: {text['answer']}\n\n"
        "## process-ocr-handoff\n\n"
        f"- Anki noteId: `{ocr['noteId']}`\n- front: {ocr['question']}\n- back: {ocr['answer']}\n"
        "- chain state: completed; OCR provider was not rerun.\n"
        "- product gap: OCR text contains `KNOWLEDGECLIPPER SYNTHETIC OCR CANAR`; Content did not restore `CANARY`. Completed is not OCR-correction PASS.\n\n"
        "## process-question\n\n"
        "- initial answer: completed\n- task: waiting_input\n- follow_up: pending\n- make_cards: pending\n- full-chain PASS: false\n\n"
        "## Boundary\n\n"
        "This increment proves the sealed candidate Orchestrator process, Named Pipe, candidate workers and two Anki readbacks. It does not rerun external writes and does not block earlier passed text/manual UI evidence.\n"
    )


def write_outputs(result: dict[str, Any], output_root: Path) -> tuple[Path, Path]:
    require(not output_root.exists(), f"append-only output already exists: {output_root}")
    output_root.mkdir(parents=True)
    receipt = output_root / "sealed-process-increment-quality-receipt.json"
    readable = output_root / "input-output-index.md"
    receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    readable.write_text(render_readable(result), encoding="utf-8")
    return receipt, readable


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--fixture", type=Path, default=DEFAULT_FIXTURE)
    parser.add_argument("--output-root", type=Path)
    args = parser.parse_args()
    fixture = common.load_json(args.fixture)
    result = evaluate(fixture, observe_processes=bool(args.output_root))
    result["fixtureValidation"] = validate_fixtures()
    if args.output_root:
        receipt, readable = write_outputs(result, args.output_root.resolve())
        print(json.dumps({"status": result["status"], "receipt": common.evidence(receipt), "readable": common.evidence(readable)}, ensure_ascii=False, indent=2))
    else:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
