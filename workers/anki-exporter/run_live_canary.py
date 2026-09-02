#!/usr/bin/env python3
"""Run the fixed Anki Exporter Canary through the stable worker CLI."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import urllib.request
import uuid
from pathlib import Path
from typing import Any


WORKER_ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(WORKER_ROOT))
sys.path.insert(0, str(WORKER_ROOT / "tests"))

import anki_exporter as exporter
from test_anki_exporter import build_job, validate_attempt_outputs, validate_common


CANARY_CARD_ID = "rqc-canary-fixed"
CANARY_DECK = "零碎知识剪藏::Canary"


def invoke(endpoint: str, action: str, params: dict[str, Any] | None = None) -> Any:
    body = exporter.json_bytes({"action": action, "version": exporter.API_VERSION, "params": params or {}})
    request = urllib.request.Request(endpoint, data=body, headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(request, timeout=5) as response:
        payload = json.loads(response.read().decode("utf-8"))
    if not isinstance(payload, dict) or payload.get("error") is not None or "result" not in payload:
        raise RuntimeError(f"AnkiConnect {action} failed: {payload!r}")
    return payload["result"]


def current_note(endpoint: str) -> tuple[int | None, int]:
    ids = invoke(endpoint, "findNotes", {"query": f'RQC_ID:"{CANARY_CARD_ID}"'})
    if not isinstance(ids, list) or len(ids) > 1:
        raise RuntimeError(f"Fixed Canary identity must resolve to at most one note, got {ids!r}")
    if not ids:
        return None, 0
    notes = invoke(endpoint, "notesInfo", {"notes": ids})
    revision = int(notes[0]["fields"]["RQC_Revision"]["value"])
    return int(ids[0]), revision


def inspect_state(endpoint: str) -> dict[str, Any]:
    note_id, revision = current_note(endpoint)
    if note_id is None:
        return {"notePresent": False, "revision": 0, "decks": [], "mediaPresent": False}
    note = invoke(endpoint, "notesInfo", {"notes": [note_id]})[0]
    cards = invoke(endpoint, "cardsInfo", {"cards": note.get("cards", [])})
    front = note.get("fields", {}).get("Front", {}).get("value", "")
    match = re.search(r'<img src="([^"]+)">', front)
    media_present = match is not None and invoke(endpoint, "retrieveMediaFile", {"filename": match.group(1)}) is not False
    return {
        "notePresent": True,
        "noteId": note_id,
        "revision": revision,
        "decks": sorted({card.get("deckName") for card in cards if isinstance(card.get("deckName"), str)}),
        "mediaPresent": media_present,
    }


def publish_unknown_result(request_path: Path) -> dict[str, Any]:
    job_root, _, request_relative = exporter.resolve_standard_request(request_path.resolve())
    request = exporter.load_json(job_root / request_relative)
    result_path = job_root / request["resultPath"]
    if exporter.path_exists(result_path):
        result = exporter.load_json(result_path)
        if result.get("status") != "unknown_external_state":
            raise RuntimeError(f"Prior attempt already has non-unknown terminal result: {result!r}")
    else:
        receipt_path = job_root / request["outputPath"] / "receipt.json"
        started = exporter.utc_now()
        if exporter.path_exists(receipt_path):
            started = exporter.load_json(receipt_path).get("started_at", started)
        details_path = job_root / "worker.stderr.log"
        if not exporter.path_exists(details_path):
            details_path = job_root / request["outputPath"] / "receipt.json"
        result = {
            "schemaVersion": "1.0.0",
            "requestId": request["requestId"], "operationId": request["operationId"], "taskId": request["taskId"],
            "runId": request["runId"], "stageId": request["stageId"], "attemptId": request["attemptId"],
            "worker": {"id": exporter.WORKER_ID, "version": exporter.EXPORTER_VERSION},
            "contractVersion": "1.0.0", "idempotencyKey": request["idempotencyKey"],
            "status": "unknown_external_state", "outputs": [],
            "error": {"code": "effect.result_publication_failed", "category": "effect", "retryable": False,
                      "detailsRef": details_path.resolve().relative_to(job_root.resolve()).as_posix(),
                      "message": "Anki may have committed before the original result publication failed.", "blame": "worker"},
            "startedAt": started, "finishedAt": exporter.utc_now(),
            "warnings": ["Explicit reconciliation is required; automatic retry is prohibited."],
            "sideEffects": [{"kind": "anki.export", "idempotencyKey": request["idempotencyKey"], "state": "unknown"}],
            "payload": {"moduleStatus": "UNKNOWN_EXTERNAL_STATE", "reconciliationRequired": True},
        }
        exporter.write_json_atomic_once(result_path, result)
    validate_common(result_path, "worker-result.schema.json")
    return {
        "attemptId": request["attemptId"],
        "packagePath": job_root.resolve().relative_to(WORKER_ROOT.resolve()).as_posix(),
        "resultPath": result_path.resolve().relative_to(job_root.resolve()).as_posix(),
        "resultSha256": exporter.sha256_file(result_path),
        "status": result["status"],
    }


def run_phase(root: Path, endpoint: str, profile: str, revision: int, answer: str, *, payload_extra: dict[str, Any] | None = None) -> tuple[dict[str, Any], dict[str, Any]]:
    job = build_job(root, endpoint, revision=revision, answer=answer, profile=profile, payload_extra=payload_extra)
    request_path = (job["root"] / job["request"]).resolve()
    process = subprocess.run(
        [sys.executable, str(WORKER_ROOT / "anki_exporter.py"), "run", "--request", str(request_path)],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    (root / "worker.stdout.ndjson").write_text(process.stdout, encoding="utf-8", newline="\n")
    (root / "worker.stderr.log").write_text(process.stderr, encoding="utf-8", newline="\n")
    result_path = root / job["request"].replace("request.json", "result.json")
    if not result_path.is_file():
        raise RuntimeError(f"Worker did not publish result.json; exit={process.returncode}; stderr={process.stderr}")
    result = json.loads(result_path.read_text(encoding="utf-8"))
    if process.returncode != 0 or result.get("status") != "completed":
        raise RuntimeError(f"Canary phase failed; exit={process.returncode}; result={result!r}")
    run = exporter.Exporter.__new__(exporter.Exporter)
    run.writer = type("WriterView", (), {
        "output_root": result_path.parent / "outputs",
        "result_path": result_path,
    })()
    validate_common(root / "task.manifest.v1.json", "task-manifest.schema.json")
    validate_common(request_path, "worker-request.schema.json")
    validate_attempt_outputs(run)
    receipt = json.loads((result_path.parent / "outputs" / "receipt.json").read_text(encoding="utf-8"))
    return result, receipt


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--endpoint", default="http://127.0.0.1:8765")
    parser.add_argument("--profile", required=True)
    parser.add_argument("--evidence-root", type=Path)
    parser.add_argument("--reconcile-request", type=Path, help="Absolute prior request.json whose effect is unknown")
    parser.add_argument("--noop-readback", action="store_true", help="Run one same-revision identity/no-op Canary")
    return parser.parse_args()


def run_noop_canary(args: argparse.Namespace, active_profile: str) -> int:
    if args.reconcile_request:
        raise RuntimeError("--noop-readback cannot be combined with --reconcile-request")
    note_id, revision = current_note(args.endpoint)
    if note_id is None:
        raise RuntimeError("The fixed Canary note does not exist")
    before = invoke(args.endpoint, "notesInfo", {"notes": [note_id]})[0]
    before_fields = {name: value["value"] for name, value in before["fields"].items()}
    before_cards = invoke(args.endpoint, "cardsInfo", {"cards": before.get("cards", [])})
    run_id = "identity-noop-" + exporter.utc_now().replace("-", "").replace(":", "").replace(".", "").replace("Z", "") + "-" + uuid.uuid4().hex[:8]
    evidence_root = (args.evidence_root or (WORKER_ROOT / "canary" / run_id)).resolve()
    evidence_root.mkdir(parents=True, exist_ok=False)
    result, module_receipt = run_phase(
        evidence_root / "01-noop",
        args.endpoint,
        active_profile,
        revision,
        before_fields["Back"],
        payload_extra={"canaryMode": "same-revision-no-op", "expectedWorkerId": exporter.WORKER_ID},
    )
    after_ids = invoke(args.endpoint, "findNotes", {"query": f'RQC_ID:"{CANARY_CARD_ID}"'})
    after = invoke(args.endpoint, "notesInfo", {"notes": after_ids})[0]
    after_fields = {name: value["value"] for name, value in after["fields"].items()}
    after_cards = invoke(args.endpoint, "cardsInfo", {"cards": after.get("cards", [])})
    managed_unchanged = all(before_fields.get(name) == after_fields.get(name) for name in exporter.MANAGED_FIELDS)
    operations_path = evidence_root / "01-noop" / "runs" / result["runId"] / "stages" / result["stageId"] / "attempts" / result["attemptId"] / "outputs" / "operations.ndjson"
    operations = [json.loads(line) for line in operations_path.read_text(encoding="utf-8").splitlines() if line.strip()]
    effect_actions = {"createModel", "createDeck", "storeMediaFile", "addNote", "updateNoteFields", "addTags", "changeDeck"}
    checks = {
        "workerIdentity": result["worker"]["id"] == exporter.WORKER_ID == "export.anki",
        "commonCompleted": result["status"] == "completed",
        "alreadyCurrent": module_receipt["counts"]["already_current"] == 1 and module_receipt["counts"]["succeeded"] == 0,
        "oneNote": after_ids == [note_id],
        "oneCard": len(after_cards) == len(before_cards) == 1,
        "managedFieldsUnchanged": managed_unchanged,
        "userNotesUnchanged": before_fields.get("UserNotes") == after_fields.get("UserNotes"),
        "tagsUnchanged": before.get("tags") == after.get("tags"),
        "deckUnchanged": before_cards == after_cards and after_cards[0].get("deckName") == CANARY_DECK,
        "noMutatingAnkiAction": not any(item.get("action") in effect_actions for item in operations),
    }
    if not all(checks.values()):
        raise RuntimeError(f"Identity no-op Canary failed: {checks!r}")
    result_path = evidence_root / "01-noop" / result["outputs"][0]["manifestPath"]
    common_result_path = evidence_root / "01-noop" / "runs" / result["runId"] / "stages" / result["stageId"] / "attempts" / result["attemptId"] / "result.json"
    receipt = {
        "schemaVersion": "1.0.0", "canaryId": run_id, "status": "passed",
        "checkpointId": "contracts-v1-20260825T115938Z",
        "worker": result["worker"], "fixedCardId": CANARY_CARD_ID, "noteId": note_id,
        "revision": revision, "checks": checks,
        "commonResultPath": common_result_path.resolve().relative_to(evidence_root).as_posix(),
        "commonResultSha256": exporter.sha256_file(common_result_path),
        "outputArtifactPath": result_path.resolve().relative_to(evidence_root).as_posix(),
        "finishedAt": exporter.utc_now(),
    }
    receipt_path = evidence_root / "identity-noop-result.json"
    exporter.write_json_atomic_once(receipt_path, receipt)
    print(json.dumps({"status": "passed", "canaryId": run_id, "receipt": str(receipt_path), "sha256": exporter.sha256_file(receipt_path)}, ensure_ascii=False))
    return 0


def main() -> int:
    args = parse_args()
    active_profile = invoke(args.endpoint, "getActiveProfile")
    if active_profile != args.profile:
        raise RuntimeError(f"Active Anki profile mismatch: expected {args.profile!r}, got {active_profile!r}")
    if args.noop_readback:
        return run_noop_canary(args, active_profile)
    reconciliation = publish_unknown_result(args.reconcile_request) if args.reconcile_request else None
    observed_before = inspect_state(args.endpoint)
    note_id, current_revision = current_note(args.endpoint)
    base_revision = current_revision + 1
    run_id = "r" + exporter.utc_now().replace("-", "").replace(":", "").replace(".", "").replace("Z", "") + "-" + uuid.uuid4().hex[:8]
    evidence_root = (args.evidence_root or (WORKER_ROOT / "canary" / run_id)).resolve()
    evidence_root.mkdir(parents=True, exist_ok=False)

    base_answer = f"4 | canary base {run_id}"
    update_answer = f"4 | canary update {run_id}"
    reconcile_payload = {"reconcileFrom": reconciliation, "observedBefore": observed_before} if reconciliation else None
    base_result, base_receipt = run_phase(evidence_root / "01-base", args.endpoint, args.profile, base_revision, base_answer, payload_extra=reconcile_payload)
    note_id, observed_revision = current_note(args.endpoint)
    if note_id is None or observed_revision != base_revision:
        raise RuntimeError("Base phase did not create/update the fixed Canary note")

    user_marker = f"preserve {run_id}"
    invoke(args.endpoint, "updateNoteFields", {"note": {"id": note_id, "fields": {"UserNotes": user_marker}}})
    rerun_result, rerun_receipt = run_phase(evidence_root / "02-rerun", args.endpoint, args.profile, base_revision, base_answer)
    update_result, update_receipt = run_phase(evidence_root / "03-update", args.endpoint, args.profile, base_revision + 1, update_answer)

    notes = invoke(args.endpoint, "notesInfo", {"notes": [note_id]})
    if len(notes) != 1:
        raise RuntimeError("Final notesInfo did not return exactly one Canary note")
    note = notes[0]
    fields = note["fields"]
    if fields["RQC_ID"]["value"] != CANARY_CARD_ID:
        raise RuntimeError("Final Canary RQC_ID mismatch")
    if fields["RQC_Revision"]["value"] != str(base_revision + 1):
        raise RuntimeError("Final Canary revision mismatch")
    if fields["Back"]["value"] != update_answer:
        raise RuntimeError("Final Canary managed field mismatch")
    if fields["UserNotes"]["value"] != user_marker:
        raise RuntimeError("UserNotes was not preserved")
    cards = invoke(args.endpoint, "cardsInfo", {"cards": note.get("cards", [])})
    if not cards or any(card.get("deckName") != CANARY_DECK for card in cards):
        raise RuntimeError("Final Canary deck readback mismatch")
    media_match = re.search(r'<img src="([^"]+)">', fields["Front"]["value"])
    if media_match is None or invoke(args.endpoint, "retrieveMediaFile", {"filename": media_match.group(1)}) is False:
        raise RuntimeError("Final Canary media readback failed")
    duplicate_ids = invoke(args.endpoint, "findNotes", {"query": f'RQC_ID:"{CANARY_CARD_ID}"'})
    if duplicate_ids != [note_id]:
        raise RuntimeError(f"Final Canary identity is not unique: {duplicate_ids!r}")

    receipt = {
        "schemaVersion": "1.0.0",
        "canaryId": run_id,
        "status": "passed",
        "checkpointId": "contracts-v1-20260825T115938Z",
        "worker": {"id": exporter.WORKER_ID, "version": exporter.EXPORTER_VERSION},
        "endpoint": args.endpoint,
        "activeProfileSha256": exporter.sha256_bytes(args.profile.encode("utf-8")),
        "fixedCardId": CANARY_CARD_ID,
        "noteId": note_id,
        "deck": CANARY_DECK,
        "baseRevision": base_revision,
        "finalRevision": base_revision + 1,
        "checks": {
            "standardRun": True,
            "commonSchemas": True,
            "identityUnique": True,
            "rerunAlreadyCurrent": rerun_receipt["counts"]["already_current"] == 1,
            "managedUpdate": update_receipt["counts"]["succeeded"] == 1,
            "userNotesPreserved": True,
            "deckReadback": True,
            "mediaReadback": True,
        },
        "reconciliation": reconciliation,
        "observedBefore": observed_before,
        "phases": [
            {"name": "base", "status": base_result["status"], "moduleStatus": base_receipt["status"]},
            {"name": "rerun", "status": rerun_result["status"], "moduleStatus": rerun_receipt["status"]},
            {"name": "update", "status": update_result["status"], "moduleStatus": update_receipt["status"]},
        ],
        "finishedAt": exporter.utc_now(),
    }
    if not all(receipt["checks"].values()):
        raise RuntimeError(f"Canary checks did not all pass: {receipt['checks']!r}")
    receipt_path = evidence_root / "canary-result.json"
    exporter.write_json_atomic_once(receipt_path, receipt)
    print(json.dumps({"status": "passed", "canaryId": run_id, "receipt": str(receipt_path), "sha256": exporter.sha256_file(receipt_path)}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
