from __future__ import annotations

import argparse
import json
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator

import verify_v1212_external_phase_b as common
import verify_v1212_sealed_process_increment as sealed


PROJECT_ROOT = Path(__file__).resolve().parents[1]
FIXTURE_ROOT = PROJECT_ROOT / "tests" / "Quality.Gates" / "fixtures" / "v1214-question-anki-qualification"
FIXTURE_SCHEMA = FIXTURE_ROOT / "v1214-question-anki-qualification.schema.json"
DEFAULT_FIXTURE = FIXTURE_ROOT / "valid" / "v1214-sealed.json"
AUTHORITY_ROOT = PROJECT_ROOT / "contracts" / "authority" / "worker-registry" / "v1.2.14-20260830T184700Z"
AUTHORITY_RELEASE_ROOT = PROJECT_ROOT / "releases" / "knowledge-clipper-authority-v1.2.14-20260830T184700Z" / "win-x64"
AUTHORITY_RECEIPT = PROJECT_ROOT / "receipts" / "knowledge-clipper-authority-1.2.14-20260830T184700Z.json"
NOTE_ID = 1788087084878
CARD_ID = 1788087084878
DECK = "零碎知识剪藏::Canary::PhaseB-20260828T081109Z"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise common.GateFailure(message)


def upper(value: Any) -> str:
    return str(value).upper()


def validate_fixtures() -> dict[str, Any]:
    schema = common.load_json(FIXTURE_SCHEMA)
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema)
    valid = sorted((FIXTURE_ROOT / "valid").glob("*.json"))
    invalid = sorted((FIXTURE_ROOT / "invalid").glob("*.json"))
    require(valid and invalid, "v1.2.14 fixture set incomplete")
    for path in valid:
        require(not list(validator.iter_errors(common.load_json(path))), f"valid fixture rejected: {path.name}")
    for path in invalid:
        require(bool(list(validator.iter_errors(common.load_json(path)))), f"invalid fixture accepted: {path.name}")
    return {"status": "PASS", "validAccepted": len(valid), "invalidRejected": len(invalid), "schema": common.evidence(FIXTURE_SCHEMA)}


def verify_ref(ref: dict[str, Any], label: str) -> tuple[Path, dict[str, Any]]:
    path = common.resolve_ref(ref, label)
    require(common.sha256(path) == upper(ref["sha256"]), f"{label}: SHA-256 drift")
    return path, common.evidence(path)


def verify_authority(fixture: dict[str, Any]) -> dict[str, Any]:
    expected = fixture["authority"]
    checkpoint_path = AUTHORITY_ROOT / "checkpoint.json"
    registry_path = AUTHORITY_ROOT / "worker-registry.v1.json"
    checkpoint = common.load_json(checkpoint_path)
    registry = common.load_json(registry_path)
    require(common.sha256(checkpoint_path) == upper(expected["checkpointSha256"]), "authority checkpoint drift")
    require(common.sha256(registry_path) == upper(expected["registrySha256"]), "authority registry drift")
    require(common.sha256(AUTHORITY_RECEIPT) == upper(expected["receiptSha256"]), "authority receipt drift")
    require(checkpoint["admissionStatus"] == "authority-admitted" and registry["registryVersion"] == expected["version"], "authority not admitted")
    require(checkpoint["frozenV1"] == {"receipt": checkpoint["frozenV1"]["receipt"], "files": 21, "mismatches": 0, "sha256": "1CB738FE133F6645B9757E246E2FBC210785086342F70BAD32B5015BD19E1B1E"}, "frozen v1 drift")
    require(len(registry["workers"]) == 5, "authority worker count mismatch")
    require(checkpoint["workers"]["content.deepseek"]["workerVersion"] == "0.8.0", "Content 0.8.0 not authority-bound")
    require(checkpoint["workers"]["export.anki"]["workerVersion"] == "1.2.2", "Anki 1.2.2 not authority-bound")
    inventory = sealed.inventory(AUTHORITY_RELEASE_ROOT)
    require(inventory["fileCount"] == checkpoint["release"]["files"] == 296, "authority release file count drift")
    require(inventory["totalBytes"] == checkpoint["release"]["bytes"] == 260669250, "authority release byte count drift")
    require(inventory["treeSha256"] == upper(expected["releaseTreeSha256"]) == checkpoint["release"]["treeSha256"], "authority release tree drift")
    for worker_id, entry in ((item["workerId"], item) for item in registry["workers"]):
        executable = AUTHORITY_RELEASE_ROOT / entry["entrypoint"]["path"]
        require(common.file_exists(executable), f"authority entrypoint missing: {worker_id}")
        require(common.sha256(executable) == checkpoint["workers"][worker_id]["executableSha256"], f"authority entrypoint drift: {worker_id}")
    return {
        "status": "PASS-AUTHORITY-V1.2.14-EXACT",
        "checkpoint": common.evidence(checkpoint_path),
        "registry": common.evidence(registry_path),
        "receipt": common.evidence(AUTHORITY_RECEIPT),
        "release": {k: inventory[k] for k in ("fileCount", "totalBytes", "treeSha256")},
        "workers": {key: checkpoint["workers"][key] for key in ("content.deepseek", "export.anki")},
        "frozenV1": checkpoint["frozenV1"],
    }


def verify_core(fixture: dict[str, Any]) -> dict[str, Any]:
    checkpoint_path, checkpoint_ref = verify_ref(fixture["core"], "Core 2.5.3 checkpoint")
    checkpoint = common.load_json(checkpoint_path)
    require(checkpoint["checkpointVersion"] == "2.5.3" and checkpoint["status"] == "passed-offline-authority-admission-ready", "Core checkpoint status mismatch")
    validation = checkpoint["validation"]
    checked: dict[str, Any] = {}
    for key in ("settings", "coreAdapter", "orchestrator", "questionWorkflow"):
        path = PROJECT_ROOT / validation[key]["path"]
        require(common.sha256(path) == upper(validation[key]["sha256"]), f"Core {key} receipt drift")
        receipt = common.load_json(path)
        require(receipt["status"] == "passed", f"Core {key} receipt did not pass")
        checked[key] = common.evidence(path)
    contracts_path = PROJECT_ROOT / "canary" / "core-v2.5.3-offline" / "20260830T184300Z" / "public-contracts.receipt.json"
    contracts = common.load_json(contracts_path)
    require((contracts["schemasChecked"], contracts["validFixturesAccepted"], contracts["invalidFixturesRejected"]) == (64, 67, 55), "public contract counts drift")
    require(contracts["frozenV1Modified"] is False, "public contracts changed frozen v1")
    groups = common.load_json(PROJECT_ROOT / validation["coreAdapter"]["path"])["results"]
    unresolved = next(item for item in groups if item["name"] == "ocr-unresolved-attention-required")
    require(unresolved["taskStatus"] == "waiting_input" and unresolved["downstreamStarted"] is False and unresolved["automaticRetry"] is False, "OCR unresolved projection drift")
    question = common.load_json(PROJECT_ROOT / validation["questionWorkflow"]["path"])["observed"]
    require(question["destinationRouting"]["initialWrites"] == question["destinationRouting"]["followUpWrites"] == question["destinationRouting"]["replayWrites"] == 0, "offline question writes drift")
    require(question["destinationRouting"]["makeCardsWrites"] == 1, "offline make-cards write count drift")
    require("interactive-command-root" in question["failClosed"] and "interactive-command-owner-drift" in question["failClosed"], "interactive command negative coverage missing")
    require(validation["productionRetention"]["status"] == "disabled-fail-closed" and validation["productionRetention"]["legacyStartupInvocations"] == 0, "retention safety drift")
    return {
        "status": "PASS-CORE-2.5.3-OFFLINE",
        "checkpoint": checkpoint_ref,
        "receipts": checked | {"publicContracts": common.evidence(contracts_path)},
        "counts": {"adapterGroups": len(groups), "orchestratorTests": len(common.load_json(PROJECT_ROOT / validation["orchestrator"]["path"])["tests"]), "schemas": 64, "validFixtures": 67, "invalidFixtures": 55},
        "ocrUnresolved": {"taskStatus": "waiting_input", "exportBlocked": True, "automaticRetry": False},
        "retention": validation["productionRetention"],
    }


def verify_artifact(job_root: Path, ref: dict[str, Any], label: str) -> dict[str, Any]:
    path = (job_root / ref["manifestPath"]).resolve()
    require(path.is_relative_to(job_root) and common.file_exists(path), f"{label}: artifact missing")
    require(common.sha256(path) == upper(ref["expectedSha256"]), f"{label}: artifact hash drift")
    return common.evidence(path)


def find_one(root: Path, name: str) -> Path:
    paths = [path for path in sealed.walk_files(root) if path.name == name]
    require(len(paths) == 1, f"expected one {name}, found {len(paths)} under {root}")
    return paths[0]


def verify_sealed(fixture: dict[str, Any]) -> dict[str, Any]:
    root = (PROJECT_ROOT / fixture["sealed"]["root"]).resolve()
    execution_path = root / "execution-receipt.json"
    readable_path = root / "READABLE.md"
    require(common.sha256(execution_path) == upper(fixture["sealed"]["receiptSha256"]), "sealed execution receipt drift")
    require(common.sha256(readable_path) == upper(fixture["sealed"]["readableSha256"]), "sealed readable drift")
    execution = common.load_json(execution_path)
    require(execution["status"] == "passed" and execution["boundary"] == "sealed-actual-orchestrator-process" and execution["backendHarness"] is False, "sealed process boundary mismatch")
    require(execution["authority"]["version"] == "1.2.14", "sealed authority mismatch")
    require(common.sha256(Path(execution["authority"]["orchestrator"]["path"])) == upper(execution["authority"]["orchestrator"]["sha256"]), "packaged Orchestrator drift")
    job_root = Path(execution["task"]["jobRoot"])
    manifest_v1 = job_root / "task.manifest.v1.json"
    manifest_v2 = job_root / "task.manifest.v2.json"
    task_v1 = common.load_json(manifest_v1)
    task_v2 = common.load_json(manifest_v2)
    require(task_v1["metadata"]["authoritativeManifest"] == "task.manifest.v2.json" and task_v2["destinations"] == ["anki"], "dual manifest boundary mismatch")
    require(task_v1["metadata"]["authorizedAnkiDeck"] == task_v2["metadata"]["authorizedAnkiDeck"] == DECK, "Anki deck isolation mismatch")

    result_paths = sorted(path for path in sealed.walk_files(job_root / "runs") if path.name == "result.json")
    require(len(result_paths) == 4, f"expected four worker results, found {len(result_paths)}")
    results = [common.load_json(path) for path in result_paths]
    content = [item for item in results if item["worker"]["id"] == "content.deepseek"]
    anki = [item for item in results if item["worker"]["id"] == "export.anki"]
    require(len(content) == 3 and all(item["worker"]["version"] == "0.8.0" and item["status"] == "completed" for item in content), "Content attempt identity/status mismatch")
    require({item["stageId"] for item in content} == {"question-answer", "question-follow-up", "question-make-cards"}, "Content stage set mismatch")
    require(len(anki) == 1 and anki[0]["worker"]["version"] == "1.2.2" and anki[0]["status"] == "completed", "Anki attempt identity/status mismatch")
    for path, result in zip(result_paths, results, strict=True):
        request = common.load_json(path.parent / "request.json")
        require(request["attemptId"] == result["attemptId"] and request["operationId"] == result["operationId"], f"request/result identity drift: {result['stageId']}")
        for ref in request.get("inputRefs", []):
            verify_artifact(job_root, ref, f"{result['stageId']} input")
        for ref in result.get("outputs", []):
            verify_artifact(job_root, ref, f"{result['stageId']} output")

    plans = {common.load_json(path)["stages"][0]["stageId"]: (path, common.load_json(path)) for path in sorted((job_root / "plans" / "resolved").rglob("pipeline.v2.json"))}
    require(set(plans) == {"question-answer", "question-follow-up", "question-make-cards"}, "resolved plan set mismatch")
    follow_plan = plans["question-follow-up"][1]
    command_root = follow_plan["stages"][0]["inputAdapter"]["mappingSnapshot"]["interactiveCommandRoot"]
    require(command_root["payloadRef"].startswith("commands/") and command_root["payloadRef"] != task_v2["initialPlanRef"], "desktop artifact was used as interactive command root")
    payload_path = job_root / command_root["payloadRef"]
    require(common.sha256(payload_path) == upper(command_root["payloadSha256"]), "interactive command payload drift")
    require(common.file_exists(job_root / command_root["acceptedEventRef"]) and common.file_exists(job_root / command_root["resolutionRef"]), "durable command event/resolution missing")
    require(common.load_json(payload_path)["action"] == "question.follow-up", "follow-up command action mismatch")
    make_resolution = common.load_json(find_one(job_root / "runs" / plans["question-follow-up"][0].parent.name / "stages" / "question-follow-up", "question-resolution.json"))
    make_command = job_root / "commands" / make_resolution["commandId"] / "question-action.v2.json"
    require(common.load_json(make_command)["action"] == "question.make-cards", "make-cards durable command mismatch")

    initial_run = next(path for path in (job_root / "runs").iterdir() if path.name == plans["question-answer"][0].parent.name)
    follow_run = next(path for path in (job_root / "runs").iterdir() if path.name == plans["question-follow-up"][0].parent.name)
    make_run = next(path for path in (job_root / "runs").iterdir() if path.name == plans["question-make-cards"][0].parent.name)
    require(not (initial_run / "stages" / "anki-export").exists() and not (follow_run / "stages" / "anki-export").exists(), "initial/follow-up started Anki")
    require((make_run / "stages" / "anki-export").exists(), "make-cards did not start Anki")

    card_bundle_path = find_one(make_run / "adapters" / "anki-export", "card-bundle.v1.json")
    card_bundle = common.load_json(card_bundle_path)
    require(len(card_bundle["cards"]) == 1 and card_bundle["cards"][0]["status"] == "ready", "card bundle mismatch")
    card = card_bundle["cards"][0]
    attempt_root = find_one(make_run / "stages" / "anki-export", "result.json").parent
    operations_path = attempt_root / "outputs" / "operations.ndjson"
    items_path = attempt_root / "outputs" / "items.ndjson"
    module_receipt_path = attempt_root / "outputs" / "receipt.json"
    operations = sealed.load_ndjson(operations_path)
    items = sealed.load_ndjson(items_path)
    module_receipt = common.load_json(module_receipt_path)
    require(all(item["status"] == "SUCCEEDED" for item in operations), "Anki operation failure")
    actions = [item["action"] for item in operations]
    require(actions.count("addNote") == actions.count("notesInfo") == actions.count("cardsInfo") == 1, "Anki write/readback count mismatch")
    require(len(items) == 1 and items[0]["note_id"] == NOTE_ID and items[0]["status"] == "CREATED", "Anki item identity mismatch")
    require(module_receipt["counts"] == {"already_current": 0, "blocked_upstream": 0, "failed": 0, "succeeded": 1}, "Anki module receipt counts mismatch")
    require(module_receipt["inputs"]["task_manifest_sha256"] == common.sha256(manifest_v1).lower(), "Anki did not consume v1 compatibility manifest")
    observed = execution["anki"]["exactReadback"][0]
    require(observed["frontExact"] and observed["backExact"] and observed["deckExact"], "sealed exact readback failed")
    require(observed["front"] == card["fields"]["question"] and observed["back"] == card["fields"]["answer"] and observed["deck"] == DECK, "card/readback content drift")

    first_replay = common.load_json(root / "ipc" / "0013-task-command.request.json")["payload"]
    second_replay = common.load_json(root / "ipc" / "0020-task-command.request.json")["payload"]
    require({key: first_replay[key] for key in ("commandId", "operationId", "idempotencyKey", "payloadRef")} == {key: second_replay[key] for key in ("commandId", "operationId", "idempotencyKey", "payloadRef")}, "replay command identity drift")
    require(execution["replay"]["passed"] and execution["replay"]["newContentAttempts"] == execution["replay"]["newAnkiAttempts"] == execution["replay"]["newNotes"] == 0, "replay created new effects")
    require(execution["sideEffects"]["remnoteCalls"] == execution["sideEffects"]["mineruCalls"] == execution["sideEffects"]["physicalSpeakerInvocations"] == 0, "out-of-scope side effect observed")
    return {
        "status": "PASS-SEALED-ACTUAL-ORCHESTRATOR-QUESTION-ANKI",
        "executionReceipt": common.evidence(execution_path),
        "readable": common.evidence(readable_path),
        "inventory": {k: sealed.inventory(root)[k] for k in ("fileCount", "totalBytes", "treeSha256")},
        "orchestrator": execution["authority"]["orchestrator"],
        "task": {"taskId": task_v2["taskId"], "manifestV1": common.evidence(manifest_v1), "manifestV2": common.evidence(manifest_v2), "states": execution["states"]},
        "workers": {"content.deepseek@0.8.0": 3, "export.anki@1.2.2": 1},
        "interactiveCommandRoot": {"status": "PASS-DURABLE-COMMAND-ROOT", "payload": common.evidence(payload_path), "acceptedEventRef": command_root["acceptedEventRef"], "resolutionRef": command_root["resolutionRef"], "desktopArtifactUsedAsRoot": False},
        "content": {"initialWrites": 0, "followUpWrites": 0, "makeCards": {"front": card["fields"]["question"], "back": card["fields"]["answer"], "bundle": common.evidence(card_bundle_path)}},
        "anki": {"writeCount": 1, "noteId": NOTE_ID, "cardId": CARD_ID, "deck": DECK, "operations": common.evidence(operations_path), "items": common.evidence(items_path), "receipt": common.evidence(module_receipt_path), "exactReadbackPassed": True},
        "replay": {"newContentAttempts": 0, "newAnkiAttempts": 0, "newNotes": 0, "commandIdentityReused": True},
        "outOfScopeCalls": {"remnote": 0, "mineru": 0, "speaker": 0},
    }


def verify_content_081(fixture: dict[str, Any]) -> dict[str, Any]:
    path, ref = verify_ref(fixture["content081Rejection"], "Content 0.8.1 rejection")
    value = common.load_json(path)
    require(value["status"] == "NEVER_PROMOTED" and value["authorityRegistryAllowed"] is False and fixture["policy"]["content081Promotion"] is False, "Content 0.8.1 promotion is not rejected")
    return {"status": "REJECTED-NEVER-PROMOTED", "receipt": ref, "authorityRegistryAllowed": False}


def live_anki_readback(front: str, back: str) -> dict[str, Any]:
    def invoke(action: str, params: dict[str, Any]) -> dict[str, Any]:
        body = json.dumps({"action": action, "version": 6, "params": params}).encode("utf-8")
        request = urllib.request.Request("http://127.0.0.1:8765", data=body, headers={"Content-Type": "application/json"}, method="POST")
        with urllib.request.urlopen(request, timeout=15) as response:
            return json.loads(response.read().decode("utf-8"))
    notes = invoke("notesInfo", {"notes": [NOTE_ID]})
    cards = invoke("cardsInfo", {"cards": [CARD_ID]})
    require(notes.get("error") is None and cards.get("error") is None, "Anki delayed readback returned an error")
    require(len(notes["result"]) == len(cards["result"]) == 1, "Anki delayed readback count mismatch")
    note = notes["result"][0]
    card = cards["result"][0]
    require(note["noteId"] == NOTE_ID and card["cardId"] == CARD_ID and card["note"] == NOTE_ID, "Anki delayed identity mismatch")
    require(note["fields"]["Front"]["value"] == card["fields"]["Front"]["value"] == front, "Anki delayed Front drift")
    require(note["fields"]["Back"]["value"] == card["fields"]["Back"]["value"] == back, "Anki delayed Back drift")
    require(card["deckName"] == DECK and "rqc_managed" in note["tags"], "Anki delayed deck/tag drift")
    return {
        "schemaVersion": "knowledge-clipper-anki-delayed-readback.v1",
        "status": "passed",
        "queriedAt": datetime.now(timezone.utc).isoformat(),
        "endpoint": "loopback-only",
        "actions": ["notesInfo", "cardsInfo"],
        "mutatingActions": [],
        "note": notes,
        "card": cards,
        "exact": {"noteId": True, "cardId": True, "front": True, "back": True, "deck": True, "tag": True},
    }


def evaluate(fixture: dict[str, Any]) -> dict[str, Any]:
    superseded_path, superseded = verify_ref(fixture["supersedes"], "v1.2.13 preflight failure quality receipt")
    old = common.load_json(superseded_path)
    require(old["status"] == "CONDITIONAL-ANKI-PREFLIGHT-FAILED-NO-EXTERNAL-WRITE", "historical failure classification drift")
    sealed_result = verify_sealed(fixture)
    return {
        "schemaVersion": "knowledge-clipper-v1214-question-anki-qualification-receipt.v1",
        "status": "PASS-SEALED-QUESTION-ANKI-DESKTOP-CANDIDATE-PENDING",
        "fixtureGate": validate_fixtures(),
        "authority": verify_authority(fixture),
        "core": verify_core(fixture),
        "sealedQuestionAnki": sealed_result,
        "content081": verify_content_081(fixture),
        "supersedesHistoricalFailure": {"receipt": superseded, "oldAttemptRetried": False, "newImmutableTask": sealed_result["task"]["taskId"]},
        "desktopCandidateGate": {"status": "PENDING-FINAL-REBASE", "candidateBound": fixture["policy"]["desktopCandidateBound"], "requiredNext": ["exact authority v1.2.14 subtree", "sealed desktop manifest/tree", "startup/single-instance/IPC", "recursive receipt and secret scan", "no legacy external canaries"]},
        "qualityExecution": {"externalWrites": 0, "externalWorkersStarted": 0, "externalServicesRerun": 0, "liveReadbackAllowedActions": fixture["policy"]["allowedLiveActions"]},
    }


def write_outputs(result: dict[str, Any], output_root: Path, run_live_readback: bool) -> tuple[Path, Path, Path | None]:
    output_root.mkdir(parents=True, exist_ok=False)
    live_path: Path | None = None
    if run_live_readback:
        card = result["sealedQuestionAnki"]["content"]["makeCards"]
        live = live_anki_readback(card["front"], card["back"])
        live_path = output_root / "anki-delayed-readback.json"
        live_path.write_text(json.dumps(live, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        result["ankiDelayedReadback"] = {"status": "PASS-READ-ONLY-EXACT", "evidence": common.evidence(live_path), "actions": live["actions"], "mutatingActions": []}
    else:
        result["ankiDelayedReadback"] = {"status": "NOT-RUN", "actions": [], "mutatingActions": []}
    result["generatedAt"] = datetime.now(timezone.utc).isoformat()
    receipt = output_root / "v1214-question-anki-quality-receipt.json"
    readable = output_root / "READABLE.md"
    receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    readable.write_text(
        "# authority v1.2.14 question -> Anki 独立质量资格\n\n"
        "- authority：精确树、registry、checkpoint、五入口 PASS；frozen v1 21/21、0 mismatch。\n"
        "- Core 2.5.3：offline settings/adapter/orchestrator/question/public contracts PASS；OCR unresolved 正确投影 waiting_input 并阻断出口。\n"
        "- 真实封装进程：初问、追问、制卡分别启动 Content 0.8.0，共 3 attempts；仅制卡阶段启动 Anki 1.2.2，共 1 attempt。\n"
        "- 真实结果：initial/follow-up 写入 0；make_cards 写入 1；notesInfo/cardsInfo 对 Front、Back、deck、note/card ID 精确读回。\n"
        "- replay：同 commandId/operationId/idempotencyKey；Content attempts +0、Anki attempts +0、notes +0。\n"
        "- durable root：追问来自 commands/ 下 SHA 绑定的 interactiveCommandRoot；未把 desktop-intent artifact 冒充交互输入根。\n"
        "- Content 0.8.1：NEVER_PROMOTED，继续拒绝。\n"
        f"- 延迟 Anki 读回：{result['ankiDelayedReadback']['status']}；变更动作 0。\n"
        "- 当前结论：sealed backend question->Anki PASS；Desktop 最终 rebase 尚未到，candidate-bound 仍 PENDING。\n",
        encoding="utf-8",
    )
    return receipt, readable, live_path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--fixture", type=Path, default=DEFAULT_FIXTURE)
    parser.add_argument("--output-root", type=Path)
    parser.add_argument("--live-anki-readback", action="store_true")
    parser.add_argument("--validate-only", action="store_true")
    args = parser.parse_args()
    fixture = common.load_json(args.fixture)
    result = evaluate(fixture)
    if args.output_root and not args.validate_only:
        receipt, readable, live = write_outputs(result, args.output_root, args.live_anki_readback)
        print(json.dumps({"status": result["status"], "receipt": common.evidence(receipt), "readable": common.evidence(readable), "liveReadback": common.evidence(live) if live else None}, ensure_ascii=False))
    else:
        print(json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
