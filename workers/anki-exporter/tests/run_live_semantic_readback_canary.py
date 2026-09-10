#!/usr/bin/env python3
"""One append-only real Anki Canary for semantic readback evidence.

The script only targets the existing isolated Canary deck. It never creates or
deletes a deck and never cleans earlier evidence.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker


WORKER_ROOT = Path(__file__).resolve().parents[1]
PROJECT_ROOT = WORKER_ROOT.parents[1]
sys.path.insert(0, str(WORKER_ROOT))
sys.path.insert(0, str(WORKER_ROOT / "tests"))

import anki_exporter as exporter
from build_formal_release import tree
from test_anki_exporter import build_job, dump_json, mutate_job_bundle, mutate_job_config


CANARY_DECK = "零碎知识剪藏::Canary"
CANARY_CARD_ID = "kc-readback-evidence-v141-20260903t120500z"
CANARY_FRONT = "KnowledgeClipper evidence canary：2 + 3 = ?"
CANARY_BACK = "5"


def write_json_once(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8", newline="\n") as handle:
        json.dump(value, handle, ensure_ascii=False, sort_keys=True, indent=2)
        handle.write("\n")


def run_worker(exe: Path, job: dict) -> subprocess.CompletedProcess[str]:
    request = (job["root"] / job["request"]).resolve()
    return subprocess.run([str(exe), "run", "--request", str(request)], cwd=job["root"], capture_output=True, text=True, encoding="utf-8", check=False)


def configure_job(job: dict, endpoint: str, profile: str) -> None:
    def bundle_update(bundle: dict) -> None:
        bundle["media"] = []
        card = bundle["cards"][0]
        card.update({
            "card_id": CANARY_CARD_ID,
            "revision": 1,
            "fields": {"question": CANARY_FRONT, "answer": CANARY_BACK, "source": "synthetic-non-private"},
            "tags": ["knowledge_clipper_canary", "readback_evidence_v1"],
            "media_refs": [],
        })

    mutate_job_bundle(job, bundle_update)

    def config_update(config: dict) -> None:
        config.update({
            "endpoint": endpoint,
            "expected_profile": profile,
            "provision_managed": False,
            "deck_routes": {"canary": CANARY_DECK},
            "note_types": {
                "image_qa": {
                    "model_name": "RQC Basic v1",
                    "field_map": {"question": "Front", "answer": "Back", "source": "Source"},
                    "required_fields": ["question", "answer"],
                }
            },
        })

    mutate_job_config(job, config_update)


def evidence(job: dict, schema: dict) -> dict:
    request = json.loads((job["root"] / job["request"]).read_text(encoding="utf-8"))
    result_path = job["root"] / request["resultPath"]
    outputs = job["root"] / request["outputPath"]
    result = json.loads(result_path.read_text(encoding="utf-8"))
    if result["status"] != "completed" or result["worker"] != {"id": "export.anki", "version": exporter.EXPORTER_VERSION}:
        raise RuntimeError(f"Real Canary worker result is not completed 1.4.1: {result!r}")
    manifest_path = job["root"] / result["outputs"][0]["manifestPath"]
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    readback_path = job["root"] / manifest["metadata"]["readback"]["path"]
    readback = json.loads(readback_path.read_text(encoding="utf-8"))
    Draft202012Validator(schema, format_checker=FormatChecker()).validate(readback)
    if manifest["metadata"]["readback"]["sha256"] != exporter.sha256_file(readback_path):
        raise RuntimeError("Real readback manifest SHA binding failed")
    row = readback["cards"][0]
    semantic = {"notesInfo": row["notesInfo"], "cardsInfo": row["cardsInfo"]}
    if row["semanticBodySha256"] != exporter.sha256_bytes(exporter.json_bytes(semantic)):
        raise RuntimeError("Real semantic body hash cannot be reproduced")
    return {"result": result, "resultPath": result_path, "outputs": outputs, "manifestPath": manifest_path, "readbackPath": readback_path, "readback": readback, "row": row}


def actions(outputs: Path) -> list[str]:
    path = outputs / "operations.ndjson"
    return [json.loads(line)["action"] for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--release-root", required=True)
    parser.add_argument("--output-root", required=True)
    parser.add_argument("--config-source", required=True)
    args = parser.parse_args()
    release_root = Path(args.release_root).resolve()
    output_root = Path(args.output_root).resolve()
    if output_root.exists():
        raise RuntimeError(f"Append-only real Canary root already exists: {output_root}")
    output_root.mkdir(parents=True, exist_ok=False)
    config_source = json.loads(Path(args.config_source).read_text(encoding="utf-8"))
    endpoint = config_source["endpoint"]
    profile = config_source["expected_profile"]
    if not endpoint.startswith("http://127.0.0.1:") or not profile:
        raise RuntimeError("Local config source is not an exact loopback/profile binding")
    exe = release_root / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
    release_manifest = release_root / "workers" / "anki" / "release-manifest.v1.json"
    schema_path = release_root / "workers" / "anki" / "contracts" / "anki-export-readback.v1.schema.json"
    schema = json.loads(schema_path.read_text(encoding="utf-8"))

    first_job = build_job(output_root / "jobs" / "real-first", endpoint, profile=profile)
    configure_job(first_job, endpoint, profile)
    first_process = run_worker(exe, first_job)
    if first_process.returncode != 0:
        raise RuntimeError(f"Real first export failed before verified evidence: exit={first_process.returncode}; stderr={first_process.stderr}")
    first = evidence(first_job, schema)
    first_row = first["row"]
    first_actions = actions(first["outputs"])
    if first_row["outcome"] != "CREATED" or first_actions.count("addNote") != 1:
        raise RuntimeError(f"Real first export was not exactly one new note: {first_row['outcome']!r}, actions={first_actions!r}")
    if any(action in {"createModel", "createDeck"} for action in first_actions):
        raise RuntimeError("Real Canary attempted to provision a model or deck")

    replay_job = build_job(output_root / "jobs" / "real-replay", endpoint, profile=profile)
    configure_job(replay_job, endpoint, profile)
    replay_process = run_worker(exe, replay_job)
    if replay_process.returncode != 0:
        raise RuntimeError(f"Real replay failed: exit={replay_process.returncode}; stderr={replay_process.stderr}")
    replay = evidence(replay_job, schema)
    replay_row = replay["row"]
    replay_actions = actions(replay["outputs"])
    if replay_row["outcome"] != "ALREADY_CURRENT" or replay_row["notesInfo"]["noteId"] != first_row["notesInfo"]["noteId"] or replay_row["notesInfo"]["cardIds"] != first_row["notesInfo"]["cardIds"]:
        raise RuntimeError("Real idempotent replay did not preserve note/card identity")
    if set(replay_actions) & exporter.MUTATING_ANKI_ACTIONS:
        raise RuntimeError(f"Real replay issued mutating actions: {replay_actions!r}")
    note = replay_row["notesInfo"]
    cards = replay_row["cardsInfo"]
    if note["fields"]["Front"] != CANARY_FRONT or note["fields"]["Back"] != CANARY_BACK or any(card["deckName"] != CANARY_DECK for card in cards):
        raise RuntimeError("Real Front/Back/Deck readback mismatch")

    readable_path = output_root / "readable-real-input-to-anki.md"
    readable_path.write_text(f"""# export.anki {exporter.EXPORTER_VERSION} 真实语义读回 Canary

- 原始合成输入：`{CANARY_FRONT}` → `{CANARY_BACK}`
- RQC_ID：`{CANARY_CARD_ID}`；revision `1`
- 目标：既有隔离 deck `{CANARY_DECK}`；未创建/删除 deck。
- 最终 Front：`{note['fields']['Front']}`
- 最终 Back：`{note['fields']['Back']}`
- 最终 tags：`{', '.join(note['tags'])}`
- noteId：`{note['noteId']}`；cardIds：`{note['cardIds']}`
- cardsInfo deck：`{', '.join(card['deckName'] for card in cards)}`
- 首次：`CREATED`，addNote `1`；幂等重放：`ALREADY_CURRENT`，mutating actions `0`。
- semanticBodySha256：`{replay_row['semanticBodySha256'].upper()}`
- readback.json SHA-256：`{exporter.sha256_file(replay['readbackPath']).upper()}`
- 未输出 profile、endpoint、modelName、UserNotes 或秘密。
""", encoding="utf-8", newline="\n")

    receipt_path = output_root / "receipt.json"
    release_tree = tree(release_root)
    receipt = {
        "schemaVersion": "export.anki.real-semantic-readback-canary.v1",
        "status": "passed",
        "worker": {"workerId": "export.anki", "workerVersion": exporter.EXPORTER_VERSION},
        "release": {"root": str(release_root), "treeSha256": release_tree["treeSha256"], "exeSha256": exporter.sha256_file(exe).upper(), "manifestSha256": exporter.sha256_file(release_manifest).upper()},
        "source": {"kind": "synthetic-non-private", "cardId": CANARY_CARD_ID, "front": CANARY_FRONT, "back": CANARY_BACK, "tags": ["knowledge_clipper_canary", "readback_evidence_v1"]},
        "destination": {"deck": CANARY_DECK, "deckCreated": False, "deckDeleted": False},
        "first": {"outcome": first_row["outcome"], "noteId": first_row["notesInfo"]["noteId"], "cardIds": first_row["notesInfo"]["cardIds"], "addNoteCalls": first_actions.count("addNote"), "result": str(first["resultPath"]), "resultSha256": exporter.sha256_file(first["resultPath"]).upper(), "readback": str(first["readbackPath"]), "readbackSha256": exporter.sha256_file(first["readbackPath"]).upper()},
        "replay": {"outcome": replay_row["outcome"], "noteId": note["noteId"], "cardIds": note["cardIds"], "mutatingActions": 0, "result": str(replay["resultPath"]), "resultSha256": exporter.sha256_file(replay["resultPath"]).upper(), "readback": str(replay["readbackPath"]), "readbackSha256": exporter.sha256_file(replay["readbackPath"]).upper(), "semanticBodySha256": replay_row["semanticBodySha256"].upper()},
        "readable": str(readable_path), "readableSha256": exporter.sha256_file(readable_path).upper(),
        "secretsIncluded": False,
        "createdAt": exporter.utc_now(),
    }
    write_json_once(receipt_path, receipt)
    print(json.dumps({"status": "passed", "receipt": str(receipt_path), "receiptSha256": exporter.sha256_file(receipt_path).upper(), "noteId": note["noteId"], "cardIds": note["cardIds"], "firstOutcome": first_row["outcome"], "replayOutcome": replay_row["outcome"]}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
