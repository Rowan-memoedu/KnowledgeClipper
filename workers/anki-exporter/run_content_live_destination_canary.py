#!/usr/bin/env python3
"""Project a real Content 0.5 live Canary through Core and write an isolated Anki deck."""

from __future__ import annotations

import argparse
import collections
import json
import shutil
import subprocess
import sys
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


WORKER_ROOT = Path(__file__).resolve().parent
PROJECT_ROOT = WORKER_ROOT.parent.parent
sys.path.insert(0, str(WORKER_ROOT))

import anki_exporter as exporter


CANARY_DECK = "零碎知识剪藏 Canary"
CONTENT_WORKER_ID = "content.deepseek"
CONTENT_WORKER_VERSION = "0.5.0"
ANKI_WORKER_ID = "export.anki"
ANKI_WORKER_VERSION = "1.1.5"
EXPECTED_TEMPLATES = ["language_context", "language_writing", "language_context", "language_writing"]
MUTATING_ACTIONS = {
    "createModel", "createDeck", "storeMediaFile", "addNote", "updateNoteFields", "addTags", "changeDeck",
    "deleteDecks", "deleteNotes",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--content-canary-root", type=Path)
    parser.add_argument("--endpoint", default="http://127.0.0.1:8765")
    parser.add_argument("--evidence-root", type=Path)
    return parser.parse_args()


def sha256_file(path: Path) -> str:
    return exporter.sha256_file(path)


def contained(root: Path, value: str) -> Path:
    candidate = Path(value)
    resolved = candidate.resolve() if candidate.is_absolute() else (root / candidate).resolve()
    if not resolved.is_relative_to(root.resolve()):
        raise RuntimeError(f"Evidence path escapes its immutable root: {value!r}")
    return resolved


def read_json(path: Path) -> dict[str, Any]:
    with open(exporter.native_fs_path(path), "r", encoding="utf-8") as stream:
        value = json.load(stream)
    if not isinstance(value, dict):
        raise RuntimeError(f"Expected JSON object: {path}")
    return value


def discover_content_canary(explicit: Path | None) -> Path:
    if explicit is not None:
        return explicit.resolve()
    jobs = PROJECT_ROOT / "content-worker" / "canary" / "jobs"
    for root in sorted((path for path in jobs.iterdir() if path.is_dir()), key=lambda path: path.name, reverse=True):
        receipt_path = root / "canary-receipt.json"
        if not receipt_path.is_file():
            continue
        try:
            receipt = read_json(receipt_path)
            result_path = contained(root, str(receipt["resultPath"]))
            result = read_json(result_path)
        except Exception:
            continue
        evidence = receipt.get("providerEvidence")
        if (
            receipt.get("status") == "completed"
            and receipt.get("mode") == "live"
            and result.get("worker") == {"id": CONTENT_WORKER_ID, "version": CONTENT_WORKER_VERSION}
            and isinstance(evidence, list)
            and evidence
            and all(item.get("mode") == "live" and item.get("provider") == "deepseek" for item in evidence)
        ):
            return root.resolve()
    raise RuntimeError("No completed Content 0.5 live DeepSeek Canary is available")


def verify_content_canary(root: Path) -> dict[str, Any]:
    receipt_path = root / "canary-receipt.json"
    receipt = read_json(receipt_path)
    if receipt.get("status") != "completed" or receipt.get("mode") != "live":
        raise RuntimeError("Content Canary is not a completed live run")
    provider_evidence = receipt.get("providerEvidence")
    if not isinstance(provider_evidence, list) or not provider_evidence or not all(
        item.get("mode") == "live" and item.get("provider") == "deepseek" for item in provider_evidence
    ):
        raise RuntimeError("Content Canary is not backed exclusively by live DeepSeek provider evidence")
    result_path = contained(root, str(receipt["resultPath"]))
    result = read_json(result_path)
    if result.get("worker") != {"id": CONTENT_WORKER_ID, "version": CONTENT_WORKER_VERSION}:
        raise RuntimeError(f"Content worker identity mismatch: {result.get('worker')!r}")

    artifacts: dict[str, dict[str, Any]] = {}
    for kind in ("learning-bundle", "card-bundle"):
        outputs = [item for item in receipt.get("outputs", []) if item.get("kind") == kind]
        if len(outputs) != 1:
            raise RuntimeError(f"Content receipt must expose exactly one {kind}")
        output = outputs[0]
        manifest_path = contained(root, str(output["manifestPath"]))
        if sha256_file(manifest_path) != str(output["manifestSha256"]).lower():
            raise RuntimeError(f"Content {kind} manifest SHA-256 mismatch")
        manifest = read_json(manifest_path)
        if manifest.get("kind") != kind or manifest.get("producer", {}).get("workerId") != CONTENT_WORKER_ID:
            raise RuntimeError(f"Content {kind} manifest identity mismatch")
        files = manifest.get("files")
        if not isinstance(files, list) or len(files) != 1:
            raise RuntimeError(f"Content {kind} must contain exactly one payload")
        payload_path = contained(root, str(files[0]["path"]))
        payload_sha = sha256_file(payload_path)
        if payload_sha != str(files[0]["sha256"]).lower():
            raise RuntimeError(f"Content {kind} payload SHA-256 mismatch")
        artifacts[kind] = {
            "manifestPath": manifest_path,
            "manifestSha256": sha256_file(manifest_path),
            "payloadPath": payload_path,
            "payloadSha256": payload_sha,
        }

    learning = read_json(artifacts["learning-bundle"]["payloadPath"])
    bundle = read_json(artifacts["card-bundle"]["payloadPath"])
    if bundle.get("learning_bundle_sha256") != artifacts["learning-bundle"]["payloadSha256"]:
        raise RuntimeError("Content CardBundle does not pin the verified LearningBundle")
    cards = bundle.get("cards")
    if not isinstance(cards, list) or [card.get("template") for card in cards] != EXPECTED_TEMPLATES:
        raise RuntimeError("Content live CardBundle does not contain the required ordered both pairs")
    if bundle.get("media", []) != []:
        raise RuntimeError("Content live destination Canary must be text-only")
    for offset in (0, 2):
        pair = cards[offset : offset + 2]
        if pair[0].get("variant_group_id") != pair[1].get("variant_group_id"):
            raise RuntimeError("Content both pair has inconsistent variant_group_id")
        if pair[0].get("card_id") == pair[1].get("card_id"):
            raise RuntimeError("Content both pair must contain independent card identities")
    return {
        "root": root,
        "receipt": receipt,
        "receiptPath": receipt_path,
        "receiptSha256": sha256_file(receipt_path),
        "resultPath": result_path,
        "providerCalls": len(provider_evidence),
        "learning": learning,
        "bundle": bundle,
        "artifacts": artifacts,
    }


class AnkiConnect:
    def __init__(self, endpoint: str) -> None:
        self.endpoint = endpoint
        self.counts: collections.Counter[str] = collections.Counter()

    def invoke(self, action: str, params: dict[str, Any] | None = None) -> Any:
        self.counts[action] += 1
        body = exporter.json_bytes({"action": action, "version": exporter.API_VERSION, "params": params or {}})
        request = urllib.request.Request(self.endpoint, data=body, headers={"Content-Type": "application/json"}, method="POST")
        with urllib.request.urlopen(request, timeout=15) as response:
            payload = json.loads(response.read().decode("utf-8"))
        if not isinstance(payload, dict) or payload.get("error") is not None or "result" not in payload:
            raise RuntimeError(f"AnkiConnect {action} failed without a valid result")
        return payload["result"]


def locate_dotnet() -> str:
    candidates = [
        Path(r"D:\Applications\KnowledgeClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe"),
        Path(r"D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe"),
    ]
    for candidate in candidates:
        if candidate.is_file():
            return str(candidate)
    found = shutil.which("dotnet")
    if found:
        return found
    raise RuntimeError(".NET 8 runtime is unavailable")


def write_private_config(path: Path, endpoint: str, profile: str) -> dict[str, Any]:
    config = read_json(WORKER_ROOT / "config" / "anki-export.v1.example.json")
    config["endpoint"] = endpoint
    config["expected_profile"] = profile
    config["provision_managed"] = False
    for route in list(config["deck_routes"]):
        config["deck_routes"][route] = CANARY_DECK
    exporter.write_json_atomic_once(path, config)
    return config


def run_core_projection(content: dict[str, Any], config_path: Path, evidence_root: Path) -> tuple[Path, dict[str, Any]]:
    job_root = evidence_root / "job"
    project = PROJECT_ROOT / "tests" / "RemNoteQuickClipper.AnkiExporter.Tests" / "ContentAdapterCompatibility" / "ContentAdapterCompatibility.csproj"
    command = [
        locate_dotnet(), "run", "--project", str(project), "--configuration", "Release", "--no-build", "--",
        str(PROJECT_ROOT), "--project-only",
        str(content["artifacts"]["card-bundle"]["payloadPath"]),
        str(content["artifacts"]["learning-bundle"]["payloadPath"]),
        str(config_path), str(job_root),
    ]
    process = subprocess.run(command, cwd=PROJECT_ROOT, capture_output=True, text=True, encoding="utf-8", check=False)
    (evidence_root / "core-projection.stdout.ndjson").write_text(process.stdout, encoding="utf-8", newline="\n")
    (evidence_root / "core-projection.stderr.log").write_text(process.stderr, encoding="utf-8", newline="\n")
    if process.returncode != 0:
        raise RuntimeError(f"Core content-to-anki.v1 projection failed with exit {process.returncode}")
    record = read_json(job_root / "projection-stage.json")
    if record.get("status") != "projected" or record.get("cards") != 4 or record.get("externalApiCalls") != 0:
        raise RuntimeError("Core projection record failed its destination-neutral boundary")
    return job_root, record


def write_worker_request(job_root: Path, projection: dict[str, Any], attempt_id: str) -> Path:
    run_id = str(projection["runId"])
    attempt_relative = f"runs/{run_id}/stages/anki-export/attempts/{attempt_id}"
    request_path = job_root / attempt_relative / "request.json"
    request = {
        "schemaVersion": "1.0.0",
        "requestId": f"request-{attempt_id}",
        "operationId": f"operation-{attempt_id}",
        "taskId": projection["taskId"],
        "runId": run_id,
        "stageId": "anki-export",
        "attemptId": attempt_id,
        "workerId": ANKI_WORKER_ID,
        "operation": "export",
        "idempotencyKey": exporter.sha256_bytes(exporter.json_bytes({"projected": projection["projected"], "config": projection["config"]})),
        "attemptPath": attempt_relative,
        "outputPath": f"{attempt_relative}/outputs",
        "progressPath": f"{attempt_relative}/progress.ndjson",
        "resultPath": f"{attempt_relative}/result.json",
        "cancelSignal": {"schemaVersion": "1.0.0", "path": f"{attempt_relative}/cancel.json", "gracePeriodMs": 2000},
        "inputRefs": [projection["projected"], projection["config"]],
        "configSnapshot": {"ankiConfigArtifactId": projection["config"]["artifactRef"]},
        "payload": {"validateOnly": False, "canaryMode": "content-0.5-live-destination"},
    }
    exporter.write_json_atomic_once(request_path, request)
    return request_path


def run_worker(job_root: Path, projection: dict[str, Any], attempt_id: str) -> dict[str, Any]:
    request_path = write_worker_request(job_root, projection, attempt_id)
    executable = PROJECT_ROOT / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
    process = subprocess.run(
        [str(executable), "run", "--request", str(request_path)],
        cwd=executable.parent,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )
    attempt_root = request_path.parent
    (attempt_root / "worker.stdout.ndjson").write_text(process.stdout, encoding="utf-8", newline="\n")
    (attempt_root / "worker.stderr.log").write_text(process.stderr, encoding="utf-8", newline="\n")
    result_path = attempt_root / "result.json"
    if not result_path.is_file():
        raise RuntimeError(f"Anki worker published no result after possible external effects; evidence preserved at {attempt_root}")
    result = read_json(result_path)
    if process.returncode != 0 or result.get("status") != "completed" or result.get("worker") != {"id": ANKI_WORKER_ID, "version": ANKI_WORKER_VERSION}:
        raise RuntimeError(f"Anki worker attempt {attempt_id} did not complete; evidence preserved at {attempt_root}")
    module_receipt_path = attempt_root / "outputs" / "receipt.json"
    operations_path = attempt_root / "outputs" / "operations.ndjson"
    module_receipt = read_json(module_receipt_path)
    operations = [json.loads(line) for line in operations_path.read_text(encoding="utf-8").splitlines() if line.strip()]
    return {
        "attemptId": attempt_id,
        "resultPath": result_path,
        "resultSha256": sha256_file(result_path),
        "receiptPath": module_receipt_path,
        "receiptSha256": sha256_file(module_receipt_path),
        "receipt": module_receipt,
        "operationsPath": operations_path,
        "operations": operations,
    }


def projected_payload(job_root: Path, projection: dict[str, Any]) -> dict[str, Any]:
    manifest = read_json(contained(job_root, projection["projected"]["manifestPath"]))
    entrypoint = str(manifest["metadata"]["entrypoint"])
    payload_path = contained(job_root, entrypoint)
    if sha256_file(payload_path) != str(manifest["files"][0]["sha256"]).lower():
        raise RuntimeError("Core projected CardBundle payload SHA-256 mismatch")
    return read_json(payload_path)


def preflight_isolation(client: AnkiConnect, source_cards: list[dict[str, Any]]) -> tuple[str, dict[str, list[int]]]:
    if client.invoke("version") != exporter.API_VERSION:
        raise RuntimeError("AnkiConnect API version mismatch")
    active_profile = client.invoke("getActiveProfile")
    if not isinstance(active_profile, str) or not active_profile:
        raise RuntimeError("AnkiConnect did not return an active profile")
    model_names = client.invoke("modelNames")
    if not isinstance(model_names, list) or "RQC Basic v1" not in model_names:
        raise RuntimeError("Existing managed Canary note model RQC Basic v1 is unavailable; refusing to provision outside the deck")
    deck_names = client.invoke("deckNames")
    if not isinstance(deck_names, list):
        raise RuntimeError("AnkiConnect deckNames response is invalid")
    if CANARY_DECK not in deck_names:
        client.invoke("createDeck", {"deck": CANARY_DECK})

    existing: dict[str, list[int]] = {}
    for card in source_cards:
        card_id = str(card["card_id"])
        ids = client.invoke("findNotes", {"query": f'RQC_ID:"{card_id}"'})
        if not isinstance(ids, list) or len(ids) > 1:
            raise RuntimeError(f"Canary identity is already duplicated: {card_id}")
        existing[card_id] = [int(value) for value in ids]
        if ids:
            note = client.invoke("notesInfo", {"notes": ids})[0]
            cards = client.invoke("cardsInfo", {"cards": note.get("cards", [])})
            if not cards or any(item.get("deckName") != CANARY_DECK for item in cards):
                raise RuntimeError(f"Canary identity exists outside the isolated deck: {card_id}")
    return active_profile, existing


def readback_cards(
    client: AnkiConnect,
    projected: dict[str, Any],
    source: dict[str, Any],
    config: dict[str, Any],
) -> list[dict[str, Any]]:
    source_by_id = {str(card["card_id"]): card for card in source["cards"]}
    output: list[dict[str, Any]] = []
    for card in projected["cards"]:
        card_id = str(card["card_id"])
        ids = client.invoke("findNotes", {"query": f'RQC_ID:"{card_id}"'})
        if not isinstance(ids, list) or len(ids) != 1:
            raise RuntimeError(f"Readback requires exactly one note for {card_id}")
        note = client.invoke("notesInfo", {"notes": ids})[0]
        fields = {name: value["value"] for name, value in note.get("fields", {}).items()}
        note_type = config["note_types"][card["note_type_key"]]
        expected_anki_fields = {note_type["field_map"][name]: value for name, value in card["fields"].items()}
        for name, value in expected_anki_fields.items():
            if fields.get(name) != value:
                raise RuntimeError(f"Anki field readback mismatch for {card_id}:{name}")
        card_rows = client.invoke("cardsInfo", {"cards": note.get("cards", [])})
        if not card_rows or any(row.get("deckName") != CANARY_DECK for row in card_rows):
            raise RuntimeError(f"Anki deck readback mismatch for {card_id}")
        output.append({
            "cardId": card_id,
            "template": source_by_id[card_id]["template"],
            "variantGroupId": source_by_id[card_id]["variant_group_id"],
            "sourceFront": source_by_id[card_id]["front_blocks"][0]["text"],
            "sourceBack": source_by_id[card_id]["back_blocks"][0]["text"],
            "projectedFields": card["fields"],
            "ankiFront": fields.get("Front", ""),
            "ankiBack": fields.get("Back", ""),
            "noteId": int(ids[0]),
            "cardIds": [int(row["cardId"]) for row in card_rows],
            "deck": CANARY_DECK,
        })
    return output


def operation_summary(operations: list[dict[str, Any]]) -> dict[str, Any]:
    counts = collections.Counter(str(item.get("action")) for item in operations)
    return {"calls": len(operations), "byAction": dict(sorted(counts.items())), "mutatingActions": [item.get("action") for item in operations if item.get("action") in MUTATING_ACTIONS]}


def write_readable(path: Path, source: dict[str, Any], readback: list[dict[str, Any]], source_sha: str) -> None:
    lines = [
        "# Content 0.5 live DeepSeek → Core → Anki 真实目的端 Canary",
        "",
        f"- 独立 deck：`{CANARY_DECK}`",
        f"- Content CardBundle SHA-256：`{source_sha}`",
        "- 执行：首次真实写入 + 同 revision 幂等重跑 + AnkiConnect 逐卡回读",
        "- 删除/清理：0",
        "",
    ]
    for index, item in enumerate(readback, 1):
        lines.extend([
            f"## {index}. `{item['template']}` / `{item['cardId']}`",
            "",
            f"- 原始 Front：{json.dumps(item['sourceFront'], ensure_ascii=False)}",
            f"- 原始 Back：{json.dumps(item['sourceBack'], ensure_ascii=False)}",
            f"- Core 投影字段：`{json.dumps(item['projectedFields'], ensure_ascii=False, sort_keys=True)}`",
            f"- Anki Front 回读：{json.dumps(item['ankiFront'], ensure_ascii=False)}",
            f"- Anki Back 回读：{json.dumps(item['ankiBack'], ensure_ascii=False)}",
            f"- note ID：`{item['noteId']}`；card ID：`{', '.join(str(value) for value in item['cardIds'])}`",
            f"- deck 回读：`{CANARY_DECK}`",
            "",
        ])
    path.write_text("\n".join(lines), encoding="utf-8", newline="\n")


def main() -> int:
    args = parse_args()
    content_root = discover_content_canary(args.content_canary_root)
    content = verify_content_canary(content_root)
    source_cards = content["bundle"]["cards"]

    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    evidence_root = (args.evidence_root or (WORKER_ROOT / "canary" / "live-destination" / stamp)).resolve()
    evidence_root.mkdir(parents=True, exist_ok=False)
    client = AnkiConnect(args.endpoint)
    active_profile, existing = preflight_isolation(client, source_cards)
    if any(existing.values()):
        raise RuntimeError("This immutable live Content Canary was already exported; refusing to treat a read-only rerun as a fresh destination write")

    private_root = evidence_root / "private"
    private_root.mkdir()
    config_path = private_root / "anki-export.v1.json"
    config = write_private_config(config_path, args.endpoint, active_profile)
    job_root, projection = run_core_projection(content, config_path, evidence_root)
    projected = projected_payload(job_root, projection)
    if [card["card_id"] for card in projected["cards"]] != [card["card_id"] for card in source_cards]:
        raise RuntimeError("Core projection changed the Content card order")

    first = run_worker(job_root, projection, "attempt-write-1")
    second = run_worker(job_root, projection, "attempt-write-2")
    first_counts = first["receipt"]["counts"]
    second_counts = second["receipt"]["counts"]
    if first_counts.get("succeeded") != 4 or first_counts.get("already_current") != 0:
        raise RuntimeError(f"Fresh live destination write did not create all four notes: {first_counts!r}")
    if second_counts.get("already_current") != 4 or second_counts.get("succeeded") != 0:
        raise RuntimeError(f"Same-revision rerun did not converge as a four-note no-op: {second_counts!r}")
    second_operations = operation_summary(second["operations"])
    if second_operations["mutatingActions"]:
        raise RuntimeError(f"Same-revision rerun performed mutating Anki actions: {second_operations['mutatingActions']!r}")

    readback = readback_cards(client, projected, content["bundle"], config)
    if len({item["noteId"] for item in readback}) != 4 or sum(len(item["cardIds"]) for item in readback) != 4:
        raise RuntimeError("Anki readback did not yield four unique notes and four cards")
    for card in source_cards:
        ids = client.invoke("findNotes", {"query": f'RQC_ID:"{card["card_id"]}"'})
        if len(ids) != 1:
            raise RuntimeError("Post-rerun duplicate identity detected")

    readable_path = evidence_root / "readable-input-to-anki.md"
    write_readable(readable_path, content["bundle"], readback, content["artifacts"]["card-bundle"]["payloadSha256"])
    first_operations = operation_summary(first["operations"])
    combined_actions = collections.Counter(client.counts)
    for attempt in (first["operations"], second["operations"]):
        combined_actions.update(str(item.get("action")) for item in attempt)
    anki_calls = len(first["operations"]) + len(second["operations"]) + sum(client.counts.values())
    templates = [card["template"] for card in source_cards]
    receipt = {
        "schemaVersion": "anki-live-destination-canary/1.0.0",
        "status": "passed",
        "canaryId": stamp,
        "deck": CANARY_DECK,
        "source": {
            "contentCanaryId": content["receipt"].get("canaryId"),
            "contentWorker": {"id": CONTENT_WORKER_ID, "version": CONTENT_WORKER_VERSION},
            "mode": "live",
            "provider": "deepseek",
            "providerCallCount": content["providerCalls"],
            "receiptPath": content["receiptPath"].relative_to(PROJECT_ROOT).as_posix(),
            "receiptSha256": content["receiptSha256"],
            "learningBundleSha256": content["artifacts"]["learning-bundle"]["payloadSha256"],
            "cardBundleSha256": content["artifacts"]["card-bundle"]["payloadSha256"],
        },
        "core": {
            "adapter": "content-to-anki.v1",
            "profileSha256": projection["core"]["profileSha256"],
            "projectionRecord": (job_root / "projection-stage.json").relative_to(evidence_root).as_posix(),
            "projectionRecordSha256": sha256_file(job_root / "projection-stage.json"),
            "orderPreserved": True,
        },
        "worker": {
            "id": ANKI_WORKER_ID,
            "version": ANKI_WORKER_VERSION,
            "executable": "workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe",
            "executableSha256": sha256_file(PROJECT_ROOT / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"),
        },
        "configuration": {
            "sha256": sha256_file(config_path),
            "activeProfileSha256": exporter.sha256_bytes(active_profile.encode("utf-8")),
            "sensitiveValuesOmitted": True,
            "allDeckRoutesPinnedToCanaryDeck": True,
            "provisionManaged": False,
        },
        "cardSemantics": {
            "total": 4,
            "meaningReading": templates.count("language_context"),
            "writing": templates.count("language_writing"),
            "bothGroups": len({card["variant_group_id"] for card in source_cards}),
            "orderedTemplates": templates,
            "orderedCardIds": [card["card_id"] for card in source_cards],
        },
        "attempts": {
            "firstWrite": {
                "result": first["resultPath"].relative_to(evidence_root).as_posix(),
                "resultSha256": first["resultSha256"],
                "moduleReceiptSha256": first["receiptSha256"],
                "counts": first_counts,
                "ankiConnect": first_operations,
            },
            "sameRevisionRerun": {
                "result": second["resultPath"].relative_to(evidence_root).as_posix(),
                "resultSha256": second["resultSha256"],
                "moduleReceiptSha256": second["receiptSha256"],
                "counts": second_counts,
                "ankiConnect": second_operations,
                "duplicateNotesCreated": 0,
            },
        },
        "readback": readback,
        "apiCalls": {
            "contentProvider": content["providerCalls"],
            "ankiConnectDirect": sum(client.counts.values()),
            "ankiConnectFirstWrite": len(first["operations"]),
            "ankiConnectSameRevisionRerun": len(second["operations"]),
            "ankiConnectTotal": anki_calls,
            "ankiConnectByAction": dict(sorted(combined_actions.items())),
        },
        "checks": {
            "isolatedDeckOnly": all(item["deck"] == CANARY_DECK for item in readback),
            "fourUniqueNotesAndCards": True,
            "frontBackExactReadback": True,
            "meaningReadingWritingBothOrder": True,
            "sameRevisionNoDuplicate": True,
            "sameRevisionNoMutation": True,
            "noDeleteOrCleanup": True,
        },
        "readableEvidence": {
            "path": readable_path.relative_to(PROJECT_ROOT).as_posix(),
            "sha256": sha256_file(readable_path),
        },
        "external_api_calls": {"deepseek": content["providerCalls"], "ankiConnect": anki_calls},
        "finishedAt": exporter.utc_now(),
    }
    if not all(receipt["checks"].values()):
        raise RuntimeError(f"Live destination Canary checks failed: {receipt['checks']!r}")
    receipt_path = evidence_root / "receipt.json"
    exporter.write_json_atomic_once(receipt_path, receipt)
    print(json.dumps({
        "status": "passed",
        "receipt": str(receipt_path),
        "receiptSha256": sha256_file(receipt_path),
        "deck": CANARY_DECK,
        "notes": [item["noteId"] for item in readback],
        "cards": [value for item in readback for value in item["cardIds"]],
        "ankiConnectCalls": anki_calls,
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
