#!/usr/bin/env python3
"""Run one explicit real probe, then one minimal isolated Anki write/readback."""

from __future__ import annotations

import json
import subprocess
import sys
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


WORKER = Path(__file__).resolve().parent
PROJECT = WORKER.parents[1]
sys.path.insert(0, str(WORKER))
sys.path.insert(0, str(WORKER / "tests"))

import anki_exporter as exporter
from test_anki_exporter import validate_common


AUTHORITY_ROOT = PROJECT / "releases" / "knowledge-clipper-authority-v1.2.12-20260830T075600Z" / "win-x64"
AUTHORITY_EXE = AUTHORITY_ROOT / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
ENDPOINT = "http://127.0.0.1:8765"
EXPECTED_PROFILE = "账户 1"
CANARY_DECK = "零碎知识剪藏 Canary"
MODEL_NAME = "RQC Basic v1"
MUTATING = exporter.MUTATING_ANKI_ACTIONS


def now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def invoke(action: str, params: dict[str, Any] | None = None) -> Any:
    request = urllib.request.Request(
        ENDPOINT,
        data=exporter.json_bytes({"action": action, "version": exporter.API_VERSION, "params": params or {}}),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=10) as response:
        value = json.loads(response.read().decode("utf-8"))
    if not isinstance(value, dict) or value.get("error") is not None or "result" not in value:
        raise RuntimeError(f"AnkiConnect {action} failed: {value!r}")
    return value["result"]


def write_artifact(job: Path, artifact_id: str, kind: str, payload_name: str, payload: dict[str, Any], created_at: str) -> dict[str, Any]:
    payload_relative = f"artifacts/{artifact_id}/{payload_name}"
    manifest_relative = f"artifacts/{artifact_id}/artifact.json"
    payload_path = job / payload_relative
    manifest_path = job / manifest_relative
    exporter.write_json_atomic_once(payload_path, payload)
    artifact = {
        "schemaVersion": "1.0.0",
        "artifactId": artifact_id,
        "kind": kind,
        "contractVersion": "1.0.0",
        "mediaType": "application/json",
        "files": [{
            "path": payload_relative,
            "sha256": exporter.sha256_file(payload_path),
            "bytes": exporter.path_size(payload_path),
            "mediaType": "application/json",
        }],
        "createdAt": created_at,
        "producer": {
            "stageId": "live-canary-fixture",
            "workerId": "export.anki.live-canary-fixture",
            "workerVersion": "1.0.0",
            "attemptId": "fixture-attempt",
        },
        "inputRefs": [],
        "dataClass": "public",
        "metadata": {"entrypoint": payload_relative},
    }
    exporter.write_json_atomic_once(manifest_path, artifact)
    return {
        "artifactRef": artifact_id,
        "kind": kind,
        "contractVersion": "1.0.0",
        "manifestPath": manifest_relative,
        "expectedSha256": exporter.sha256_file(manifest_path),
    }


def task_ref(reference: dict[str, Any]) -> dict[str, Any]:
    return {key: reference[key] for key in ("artifactRef", "kind", "contractVersion", "expectedSha256")}


def request(task_id: str, run_id: str, stage_id: str, attempt_id: str, operation: str, refs: list[dict[str, Any]], config_id: str) -> tuple[dict[str, Any], str]:
    attempt = f"runs/{run_id}/stages/{stage_id}/attempts/{attempt_id}"
    value = {
        "schemaVersion": "1.0.0",
        "requestId": f"request-{run_id}",
        "operationId": f"operation-{run_id}",
        "taskId": task_id,
        "runId": run_id,
        "stageId": stage_id,
        "attemptId": attempt_id,
        "workerId": "export.anki",
        "operation": operation,
        "idempotencyKey": exporter.sha256_bytes(exporter.json_bytes({"operation": operation, "refs": refs})),
        "attemptPath": attempt,
        "outputPath": f"{attempt}/outputs",
        "progressPath": f"{attempt}/progress.ndjson",
        "resultPath": f"{attempt}/result.json",
        "cancelSignal": {"schemaVersion": "1.0.0", "path": f"{attempt}/cancel.json", "gracePeriodMs": 2000},
        "inputRefs": refs,
        "configSnapshot": {"ankiConfigArtifactId": config_id},
        "payload": {"probeMode": "read-only"} if operation == "probe" else {"validateOnly": False},
    }
    return value, f"{attempt}/request.json"


def run_worker(root: Path, job: Path, label: str, request_relative: str) -> tuple[dict[str, Any], Path]:
    request_path = (job / request_relative).resolve()
    process = subprocess.run(
        [str(AUTHORITY_EXE), "run", "--request", str(request_path)],
        cwd=AUTHORITY_ROOT,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=120,
        check=False,
    )
    (root / f"{label}.stdout.log").write_text(process.stdout, encoding="utf-8", newline="\n")
    (root / f"{label}.stderr.log").write_text(process.stderr, encoding="utf-8", newline="\n")
    request_value = json.loads(request_path.read_text(encoding="utf-8"))
    result_path = job / request_value["resultPath"]
    if not result_path.is_file():
        raise RuntimeError(f"{label}: result missing; exit={process.returncode}; stderr={process.stderr}")
    result = json.loads(result_path.read_text(encoding="utf-8"))
    if process.returncode != 0 or result.get("status") != "completed":
        error = result.get("error", {})
        raise RuntimeError(f"{label}: action={request_value['operation']} exit={process.returncode} status={result.get('status')} error={error}")
    validate_common(request_path, "worker-request.schema.json")
    validate_common(result_path, "worker-result.schema.json")
    return result, result_path


def output_entrypoint(job: Path, result: dict[str, Any]) -> Path:
    manifest = json.loads((job / result["outputs"][0]["manifestPath"]).read_text(encoding="utf-8"))
    return job / manifest["metadata"]["entrypoint"]


def main() -> int:
    if not AUTHORITY_EXE.is_file():
        raise RuntimeError(f"Authority export.anki entrypoint missing: {AUTHORITY_EXE}")
    canary_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    root = WORKER / "canary" / "authority-v1.2.12-live-minimal" / canary_id
    job = root / "job"
    root.mkdir(parents=True, exist_ok=False)
    created_at = now()
    task_id = "anki-live-minimal-" + canary_id.lower()
    card_id = "rqc-live-minimal-" + canary_id.lower()

    config = {
        "schema_version": "anki-export-config.v1",
        "endpoint": ENDPOINT,
        "expected_profile": EXPECTED_PROFILE,
        "timeout_seconds": 15,
        "max_media_bytes": 26214400,
        "provision_managed": False,
        "deck_routes": {"canary": CANARY_DECK},
        "note_types": {"basic_qa": {
            "model_name": MODEL_NAME,
            "field_map": {"question": "Front", "answer": "Back", "context": "Context", "source": "Source"},
            "required_fields": ["question", "answer"],
        }},
    }
    card = {
        "card_id": card_id,
        "revision": 1,
        "content_hash": "pending",
        "status": "ready",
        "deck_route_key": "canary",
        "note_type_key": "basic_qa",
        "fields": {
            "question": "零碎知识剪藏真实 Canary：2 + 3 等于多少？",
            "answer": "5",
            "context": "export.anki 1.2.1 最小真实写入验证",
            "source": "synthetic-live-canary-2026-08-30",
        },
        "tags": ["knowledge_clipper_canary", "anki_exporter_1_2_1", "live_minimal"],
        "media_refs": [],
    }
    card["content_hash"] = exporter.compute_content_hash(card, {})
    bundle = {"bundle_kind": "CardBundle", "schema_version": "1.0", "bundle_id": card_id, "cards": [card], "media": []}
    config_ref = write_artifact(job, "anki-config-live", "anki-export-config", "anki-export.v1.json", config, created_at)
    card_ref = write_artifact(job, "card-bundle-live", "card-bundle", "card-bundle.v1.json", bundle, created_at)
    exporter.write_json_atomic_once(job / "plans" / "initial-plan.json", {"fixture": "authority-v1.2.12-live-minimal", "externalWriteAuthorized": True})
    task = {
        "schemaVersion": "1.0.0",
        "taskId": task_id,
        "createdAt": created_at,
        "createdBy": {"application": "KnowledgeClipper.Anki.LiveCanary", "version": "1.0.0"},
        "intent": "clip.card",
        "executionProfile": "cards-anki",
        "inputRefs": [task_ref(config_ref), task_ref(card_ref)],
        "requestedOutputs": ["anki-connection-status", "anki-export-evidence"],
        "initialPlanRef": "plans/initial-plan.json",
        "retentionClass": "evidence",
    }
    exporter.write_json_atomic_once(job / "task.manifest.v1.json", task)
    validate_common(job / "task.manifest.v1.json", "task-manifest.schema.json")

    probe_request, probe_relative = request(task_id, "run-probe", "anki-probe", "attempt-probe", "probe", [config_ref], config_ref["artifactRef"])
    exporter.write_json_atomic_once(job / probe_relative, probe_request)
    probe_result, probe_result_path = run_worker(root, job, "01-probe", probe_relative)
    status_path = output_entrypoint(job, probe_result)
    status = json.loads(status_path.read_text(encoding="utf-8"))
    if status.get("status") != "ready" or status.get("externalMutation") is not False or status.get("mutatingActionsObserved") != []:
        raise RuntimeError(f"probe: connection status is not ready/read-only: {status!r}")

    observed = {
        "version": invoke("version"),
        "profile": invoke("getActiveProfile"),
        "actions": invoke("apiReflect", {"scopes": ["actions"], "actions": sorted(exporter.PROBE_READ_ONLY_ACTIONS - {"requestPermission", "version", "getActiveProfile", "apiReflect"})}),
        "models": invoke("modelNames"),
        "decks": invoke("deckNames"),
        "modelFields": invoke("modelFieldNames", {"modelName": MODEL_NAME}),
    }
    if observed["profile"] != EXPECTED_PROFILE:
        raise RuntimeError(f"probe: observed profile mismatch: endpoint={ENDPOINT} expected={EXPECTED_PROFILE!r} observed={observed['profile']!r}")
    if MODEL_NAME not in observed["models"] or CANARY_DECK not in observed["decks"]:
        raise RuntimeError(f"probe: required model/deck missing: model={MODEL_NAME in observed['models']} deck={CANARY_DECK in observed['decks']}")
    prior_ids = invoke("findNotes", {"query": f'RQC_ID:"{card_id}"'})
    if prior_ids:
        raise RuntimeError(f"write: fresh Canary identity already exists: {prior_ids!r}")
    deck_names_before = sorted(observed["decks"])
    deck_cards_before = invoke("findCards", {"query": f'deck:"{CANARY_DECK}"'})

    export_request, export_relative = request(task_id, "run-export", "anki-export", "attempt-export", "export", [card_ref, config_ref], config_ref["artifactRef"])
    exporter.write_json_atomic_once(job / export_relative, export_request)
    export_result, export_result_path = run_worker(root, job, "02-export", export_relative)
    module_receipt_path = output_entrypoint(job, export_result)
    module_receipt = json.loads(module_receipt_path.read_text(encoding="utf-8"))
    if module_receipt.get("counts", {}).get("succeeded") != 1:
        raise RuntimeError(f"write: worker did not report exactly one successful card: {module_receipt!r}")

    note_ids = invoke("findNotes", {"query": f'RQC_ID:"{card_id}"'})
    if not isinstance(note_ids, list) or len(note_ids) != 1:
        raise RuntimeError(f"readback: expected exactly one note, got {note_ids!r}")
    note = invoke("notesInfo", {"notes": note_ids})[0]
    card_ids = note.get("cards", [])
    cards = invoke("cardsInfo", {"cards": card_ids})
    fields = {name: value.get("value", "") for name, value in note.get("fields", {}).items()}
    deck_names_after = sorted(invoke("deckNames"))
    deck_cards_after = invoke("findCards", {"query": f'deck:"{CANARY_DECK}"'})
    expected_tags = sorted(set(card["tags"]) | {"rqc_managed"})
    checks = {
        "oneNote": len(note_ids) == 1,
        "oneCard": len(card_ids) == len(cards) == 1,
        "deckExact": cards[0].get("deckName") == CANARY_DECK,
        "frontExact": fields.get("Front") == card["fields"]["question"],
        "backExact": fields.get("Back") == card["fields"]["answer"],
        "contextExact": fields.get("Context") == card["fields"]["context"],
        "sourceExact": fields.get("Source") == card["fields"]["source"],
        "identityExact": fields.get("RQC_ID") == card_id,
        "revisionExact": fields.get("RQC_Revision") == "1",
        "contentHashExact": fields.get("RQC_ContentHash") == card["content_hash"],
        "tagsExact": sorted(note.get("tags", [])) == expected_tags,
        "deckListUnchanged": deck_names_before == deck_names_after,
        "isolatedDeckCountIncreasedByOne": len(deck_cards_after) == len(deck_cards_before) + 1 and card_ids[0] in deck_cards_after,
    }
    if not all(checks.values()):
        raise RuntimeError(f"readback: one or more exact checks failed: {checks!r}")
    operations_path = module_receipt_path.parent / "operations.ndjson"
    operations = [json.loads(line) for line in operations_path.read_text(encoding="utf-8").splitlines() if line.strip()]
    mutating_actions = [item.get("action") for item in operations if item.get("action") in MUTATING]
    if set(mutating_actions) - {"addNote"}:
        raise RuntimeError(f"write: unexpected mutating action outside one-note creation: {mutating_actions!r}")

    readable_path = root / "readable-live-minimal.md"
    readable_path.write_text("\n".join([
        "# export.anki 1.2.1 真实最小写入与读回",
        "",
        f"- 原始输入：{card['fields']['question']} / {card['fields']['answer']}",
        f"- 最终 Front：{fields['Front']}",
        f"- 最终 Back：{fields['Back']}",
        f"- Context：{fields['Context']}",
        f"- Tags：`{sorted(note.get('tags', []))}`",
        f"- Deck：`{cards[0]['deckName']}`",
        f"- Note ID：`{note_ids[0]}`；Card ID：`{card_ids[0]}`",
        f"- Profile：`{observed['profile']}`；AnkiConnect version：`{observed['version']}`",
        f"- 正式 probe actions：`{status['actionsObserved']}`",
        f"- 写入动作：`{mutating_actions}`；无 createDeck/changeDeck/update/delete。",
        "",
    ]), encoding="utf-8", newline="\n")
    receipt = {
        "schemaVersion": "export.anki.authority-v1.2.12-live-minimal-receipt.v1",
        "status": "passed",
        "createdAt": now(),
        "authority": {
            "releaseRoot": relative(AUTHORITY_ROOT),
            "workerId": "export.anki",
            "workerVersion": "1.2.1",
            "exeSha256": exporter.sha256_file(AUTHORITY_EXE).upper(),
        },
        "probe": {
            "endpoint": ENDPOINT,
            "profile": observed["profile"],
            "apiVersion": observed["version"],
            "actionsObserved": status["actionsObserved"],
            "actionsVerified": status["actionsVerified"],
            "model": MODEL_NAME,
            "modelFields": observed["modelFields"],
            "deck": CANARY_DECK,
            "externalMutation": status["externalMutation"],
            "request": relative(job / probe_relative),
            "requestSha256": exporter.sha256_file(job / probe_relative).upper(),
            "result": relative(probe_result_path),
            "resultSha256": exporter.sha256_file(probe_result_path).upper(),
            "statusArtifact": relative(status_path),
            "statusSha256": exporter.sha256_file(status_path).upper(),
        },
        "input": {
            "bundle": relative(job / "artifacts" / "card-bundle-live" / "card-bundle.v1.json"),
            "bundleSha256": exporter.sha256_file(job / "artifacts" / "card-bundle-live" / "card-bundle.v1.json").upper(),
            "cardId": card_id,
            "front": card["fields"]["question"],
            "back": card["fields"]["answer"],
            "tags": card["tags"],
            "deckRoute": CANARY_DECK,
        },
        "write": {
            "result": relative(export_result_path),
            "resultSha256": exporter.sha256_file(export_result_path).upper(),
            "moduleReceipt": relative(module_receipt_path),
            "moduleReceiptSha256": exporter.sha256_file(module_receipt_path).upper(),
            "mutatingActions": mutating_actions,
            "noteId": note_ids[0],
            "cardId": card_ids[0],
            "checks": checks,
        },
        "readback": {
            "deck": cards[0]["deckName"],
            "front": fields["Front"],
            "back": fields["Back"],
            "context": fields["Context"],
            "source": fields["Source"],
            "tags": sorted(note.get("tags", [])),
            "deckCardsBefore": len(deck_cards_before),
            "deckCardsAfter": len(deck_cards_after),
            "deckNamesUnchanged": deck_names_before == deck_names_after,
        },
        "safety": {
            "targetDeckOnly": CANARY_DECK,
            "formalDeckTouched": False,
            "deletedOrCleaned": False,
            "provisionManaged": False,
        },
        "readable": relative(readable_path),
    }
    receipt_path = root / "receipt.json"
    exporter.write_json_atomic_once(receipt_path, receipt)
    print(json.dumps({
        "status": "passed",
        "receipt": str(receipt_path),
        "receiptSha256": exporter.sha256_file(receipt_path).upper(),
        "readable": str(readable_path),
        "noteId": note_ids[0],
        "cardId": card_ids[0],
        "deck": CANARY_DECK,
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
