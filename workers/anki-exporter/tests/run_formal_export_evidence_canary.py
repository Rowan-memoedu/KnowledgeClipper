#!/usr/bin/env python3
"""Formal EXE qualification for SHA-bound semantic Anki export evidence.

Only an ephemeral Fake AnkiConnect server on a random loopback port is used.
The script never connects to the default AnkiConnect endpoint.
"""

from __future__ import annotations

import argparse
import copy
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
from test_anki_exporter import FakeAnkiServer, build_job, seed_job_managed_resources


def write_json_once(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8", newline="\n") as handle:
        json.dump(value, handle, ensure_ascii=False, sort_keys=True, indent=2)
        handle.write("\n")


def relative(path: Path) -> str:
    try:
        return path.resolve().relative_to(PROJECT_ROOT.resolve()).as_posix()
    except ValueError:
        return str(path.resolve())


def run_worker(exe: Path, job: dict) -> subprocess.CompletedProcess[str]:
    request = (job["root"] / job["request"]).resolve()
    return subprocess.run(
        [str(exe), "run", "--request", str(request)],
        cwd=job["root"], capture_output=True, text=True, encoding="utf-8", check=False,
    )


def attempt(job: dict) -> tuple[Path, Path]:
    request = json.loads((job["root"] / job["request"]).read_text(encoding="utf-8"))
    return job["root"] / request["resultPath"], job["root"] / request["outputPath"]


def load_completed_evidence(job: dict, schema: dict) -> dict:
    result_path, outputs = attempt(job)
    result = json.loads(result_path.read_text(encoding="utf-8"))
    if result["status"] != "completed" or result["worker"] != {"id": "export.anki", "version": exporter.EXPORTER_VERSION}:
        raise RuntimeError(f"Formal worker did not complete with expected identity: {result!r}")
    manifest_path = job["root"] / result["outputs"][0]["manifestPath"]
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    readback_ref = manifest["metadata"]["readback"]
    readback_path = job["root"] / readback_ref["path"]
    readback = json.loads(readback_path.read_text(encoding="utf-8"))
    Draft202012Validator(schema, format_checker=FormatChecker()).validate(readback)
    readback_sha = exporter.sha256_file(readback_path)
    if readback_sha != readback_ref["sha256"]:
        raise RuntimeError("Artifact metadata did not bind readback SHA-256")
    receipt = json.loads((outputs / "receipt.json").read_text(encoding="utf-8"))
    if receipt["readback_evidence"]["sha256"] != readback_sha:
        raise RuntimeError("Receipt did not bind readback SHA-256")
    if readback["cardsSha256"] != exporter.sha256_bytes(exporter.json_bytes(readback["cards"])):
        raise RuntimeError("Readback card array hash is not reproducible")
    for row in readback["cards"]:
        semantic = {"notesInfo": row["notesInfo"], "cardsInfo": row["cardsInfo"]}
        if row["semanticBodySha256"] != exporter.sha256_bytes(exporter.json_bytes(semantic)):
            raise RuntimeError("Per-card semantic readback hash is not reproducible")
    return {
        "result": result,
        "resultPath": result_path,
        "manifest": manifest,
        "manifestPath": manifest_path,
        "readback": readback,
        "readbackPath": readback_path,
        "readbackSha256": readback_sha,
        "receipt": receipt,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--release-root", required=True)
    parser.add_argument("--output-root", required=True)
    args = parser.parse_args()
    release_root = Path(args.release_root).resolve()
    output_root = Path(args.output_root).resolve()
    if output_root.exists():
        raise RuntimeError(f"Append-only canary root already exists: {output_root}")
    output_root.mkdir(parents=True, exist_ok=False)
    exe = release_root / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
    release_manifest = release_root / "workers" / "anki" / "release-manifest.v1.json"
    schema_path = release_root / "workers" / "anki" / "contracts" / "anki-export-readback.v1.schema.json"
    if not exe.is_file() or not release_manifest.is_file() or not schema_path.is_file():
        raise RuntimeError("Formal release does not contain the executable, manifest, and readback schema")
    schema = json.loads(schema_path.read_text(encoding="utf-8"))

    described = subprocess.run([str(exe), "describe", "--json"], cwd=release_root, capture_output=True, text=True, encoding="utf-8", check=False)
    descriptor = json.loads(described.stdout) if described.returncode == 0 and not described.stderr else {}
    if descriptor.get("workerId") != "export.anki" or descriptor.get("workerVersion") != exporter.EXPORTER_VERSION:
        raise RuntimeError(f"Formal describe identity mismatch: {descriptor!r}")

    # Existing validate-only behavior remains connection-free and does not
    # fabricate provider readback evidence.
    validate_job = build_job(output_root / "jobs" / "validate-only", "http://127.0.0.1:65534", operation="validate")
    validate_process = run_worker(exe, validate_job)
    validate_result_path, validate_outputs = attempt(validate_job)
    validate_result = json.loads(validate_result_path.read_text(encoding="utf-8"))
    if validate_process.returncode != 0 or validate_result["status"] != "completed" or (validate_outputs / "readback.json").exists() or (validate_outputs / "operations.ndjson").exists():
        raise RuntimeError("Validate-only compatibility regressed or fabricated live evidence")

    with FakeAnkiServer() as fake:
        first_job = build_job(output_root / "jobs" / "fake-first", fake.endpoint)
        seed_job_managed_resources(fake.state, first_job)
        first_process = run_worker(exe, first_job)
        if first_process.returncode != 0:
            raise RuntimeError(f"Formal Fake first export failed: {first_process.stderr}")
        first = load_completed_evidence(first_job, schema)
        first_calls = copy.deepcopy(fake.state.calls)
        row = first["readback"]["cards"][0]
        note = row["notesInfo"]
        cards = row["cardsInfo"]
        if row["sourceCardId"] != "rqc-canary-fixed" or row["outcome"] != "CREATED":
            raise RuntimeError("First evidence source identity/outcome mismatch")
        if note["fields"]["Back"] != "四" or cards[0]["deckName"] != "零碎知识剪藏::Canary":
            raise RuntimeError("First evidence does not expose exact Back/Deck semantics")
        serialized = json.dumps(first["readback"], ensure_ascii=False)
        for forbidden in (fake.state.profile, fake.endpoint, "RQC Basic v1", "UserNotes"):
            if forbidden in serialized:
                raise RuntimeError(f"Readback evidence leaked forbidden value: {forbidden!r}")

        fake.state.notes[note["noteId"]]["fields"]["UserNotes"] = "private fake user note"
        fake.state.calls.clear()
        replay_job = build_job(output_root / "jobs" / "fake-replay", fake.endpoint)
        replay_process = run_worker(exe, replay_job)
        if replay_process.returncode != 0:
            raise RuntimeError(f"Formal Fake replay failed: {replay_process.stderr}")
        replay = load_completed_evidence(replay_job, schema)
        replay_calls = copy.deepcopy(fake.state.calls)
        replay_row = replay["readback"]["cards"][0]
        if replay_row["outcome"] != "ALREADY_CURRENT" or replay_row["notesInfo"]["noteId"] != note["noteId"] or replay_row["notesInfo"]["cardIds"] != note["cardIds"]:
            raise RuntimeError("Idempotent replay evidence did not preserve note/card identity")
        if {item["action"] for item in replay_calls} & exporter.MUTATING_ANKI_ACTIONS:
            raise RuntimeError("Idempotent replay issued a mutating provider action")
        if "private fake user note" in json.dumps(replay["readback"], ensure_ascii=False):
            raise RuntimeError("UserNotes leaked into replay evidence")

    with FakeAnkiServer() as fake:
        malformed_job = build_job(output_root / "jobs" / "fake-malformed-readback", fake.endpoint)
        seed_job_managed_resources(fake.state, malformed_job)
        fake.state.readback_defect = "duplicate_cards_info"
        malformed_process = run_worker(exe, malformed_job)
        malformed_result_path, malformed_outputs = attempt(malformed_job)
        malformed_result = json.loads(malformed_result_path.read_text(encoding="utf-8"))
        if malformed_process.returncode == 0 or malformed_result["status"] != "failed" or malformed_result["outputs"] or (malformed_outputs / "readback.json").exists():
            raise RuntimeError("Malformed provider readback was not failed closed")

    readable_path = output_root / "readable-input-to-evidence.md"
    readable = f"""# export.anki {exporter.EXPORTER_VERSION} semantic readback evidence

- 原始 CardBundle card_id：`rqc-canary-fixed`
- 原始 Front：`2+2={{{{media:question-image}}}}`
- 原始 Back：`四`
- 投影后 Front：`{note['fields']['Front']}`
- Anki notesInfo：noteId `{note['noteId']}`；cardIds `{note['cardIds']}`；Front/Back/Context/Extra/Source 与 RQC identity/revision/content hash 已保存。
- Anki cardsInfo：cardId `{cards[0]['cardId']}`；noteId `{cards[0]['noteId']}`；Deck `{cards[0]['deckName']}`。
- 实际标签：`{', '.join(note['tags'])}`；必需标签：`{', '.join(note['requiredTags'])}`。
- semanticBodySha256：`{row['semanticBodySha256'].upper()}`
- readback.json SHA-256：`{first['readbackSha256'].upper()}`
- 幂等重放：`ALREADY_CURRENT`，note/card IDs 不变，mutating actions `0`。
- 恶意重复 cardsInfo：failed closed，未发布 readback/output Artifact。
- validate-only：外部调用 `0`，不伪造 readback。
- 本验收仅使用随机 loopback Fake AnkiConnect；真实 AnkiConnect 调用 `0`。
"""
    readable_path.write_text(readable, encoding="utf-8", newline="\n")

    release_tree = tree(release_root)
    receipt_path = output_root / "receipt.json"
    receipt = {
        "schemaVersion": "export.anki.formal-semantic-readback-canary.v1",
        "status": "passed",
        "worker": {"workerId": "export.anki", "workerVersion": exporter.EXPORTER_VERSION},
        "release": {
            "root": relative(release_root),
            "treeSha256": release_tree["treeSha256"],
            "exe": relative(exe),
            "exeSha256": exporter.sha256_file(exe).upper(),
            "manifest": relative(release_manifest),
            "manifestSha256": exporter.sha256_file(release_manifest).upper(),
            "describeSha256": exporter.sha256_bytes(described.stdout.encode("utf-8")).upper(),
        },
        "contract": {"path": relative(schema_path), "sha256": exporter.sha256_file(schema_path).upper(), "schemaVersion": "anki-export-readback.v1"},
        "firstExport": {
            "result": relative(first["resultPath"]),
            "resultSha256": exporter.sha256_file(first["resultPath"]).upper(),
            "artifact": relative(first["manifestPath"]),
            "artifactSha256": exporter.sha256_file(first["manifestPath"]).upper(),
            "readback": relative(first["readbackPath"]),
            "readbackSha256": first["readbackSha256"].upper(),
            "noteId": note["noteId"], "cardIds": note["cardIds"],
            "semanticBodySha256": row["semanticBodySha256"].upper(),
            "providerCalls": len(first_calls),
        },
        "idempotentReplay": {
            "result": relative(replay["resultPath"]),
            "resultSha256": exporter.sha256_file(replay["resultPath"]).upper(),
            "readback": relative(replay["readbackPath"]),
            "readbackSha256": replay["readbackSha256"].upper(),
            "outcome": replay_row["outcome"], "noteId": replay_row["notesInfo"]["noteId"], "cardIds": replay_row["notesInfo"]["cardIds"],
            "providerCalls": len(replay_calls), "mutatingActions": 0,
        },
        "negative": {"duplicateCardsInfo": "failed-closed", "outputs": 0, "readbackPublished": False},
        "validateOnly": {"status": validate_result["status"], "providerCalls": 0, "readbackPublished": False},
        "redaction": {"profile": False, "endpoint": False, "modelName": False, "userNotes": False, "secrets": False},
        "realAnkiConnectCalls": 0,
        "readable": relative(readable_path),
        "readableSha256": exporter.sha256_file(readable_path).upper(),
        "createdAt": exporter.utc_now(),
    }
    write_json_once(receipt_path, receipt)
    print(json.dumps({"status": "passed", "receipt": str(receipt_path), "receiptSha256": exporter.sha256_file(receipt_path).upper()}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
