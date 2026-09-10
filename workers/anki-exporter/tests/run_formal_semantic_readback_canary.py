#!/usr/bin/env python3
"""Run export.anki semantic-readback from an exact formal release against Fake Anki.

The Fake provider is populated by a separate setup attempt.  The measured
semantic-readback attempt is provider-read-only; the receipt reports setup and
measured actions separately so it cannot imply that an earlier real scenario
performed zero writes.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path


WORKER_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(WORKER_ROOT))
sys.path.insert(0, str(WORKER_ROOT / "tests"))

import anki_exporter as exporter
from test_anki_exporter import (
    FakeAnkiServer,
    build_language_both_job,
    dump_json,
    make_exporter,
    mutate_job_config,
    prepare_semantic_readback_job,
    seed_job_managed_resources,
)


MUTATING = {
    "createModel", "createDeck", "storeMediaFile", "addNote",
    "updateNoteFields", "addTags", "changeDeck",
}
CANARY_DECK = "零碎知识剪藏::Canary::SemanticReadback"


def write_json_once(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8", newline="\n") as handle:
        json.dump(value, handle, ensure_ascii=False, sort_keys=True, indent=2)
        handle.write("\n")


def request_path(job: dict) -> Path:
    return (job["root"] / job["request"]).resolve()


def configure(job: dict, endpoint: str, profile: str) -> None:
    def update(config: dict) -> None:
        config.update({
            "endpoint": endpoint,
            "expected_profile": profile,
            "provision_managed": False,
        })
        config["deck_routes"] = {route: CANARY_DECK for route in config["deck_routes"]}

    mutate_job_config(job, update)


def read_result(job: dict) -> tuple[dict, Path]:
    request = json.loads(request_path(job).read_text(encoding="utf-8"))
    path = job["root"] / request["resultPath"]
    return json.loads(path.read_text(encoding="utf-8")), path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--release-root", required=True, type=Path)
    parser.add_argument("--output-root", required=True, type=Path)
    args = parser.parse_args()
    release_root = args.release_root.resolve()
    output_root = args.output_root.resolve()
    if output_root.exists():
        raise RuntimeError(f"Append-only Canary root already exists: {output_root}")
    output_root.mkdir(parents=True, exist_ok=False)
    exe = release_root / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
    release_manifest = release_root / "workers" / "anki" / "release-manifest.v1.json"
    descriptor_path = release_root / "workers" / "anki" / "export.anki.describe.json"
    if not exe.is_file() or not release_manifest.is_file() or not descriptor_path.is_file():
        raise RuntimeError("Formal release is incomplete")
    descriptor = json.loads(descriptor_path.read_text(encoding="utf-8"))
    if descriptor.get("workerId") != "export.anki" or descriptor.get("workerVersion") != "1.5.0":
        raise RuntimeError("Formal release identity is not export.anki@1.5.0")
    if "anki-semantic-readback" not in descriptor.get("outputKinds", []):
        raise RuntimeError("Formal release descriptor omitted anki-semantic-readback")

    with FakeAnkiServer() as fake:
        setup_job = build_language_both_job(output_root / "jobs" / "fake-setup")
        configure(setup_job, fake.endpoint, fake.state.profile)
        setup_request_path = request_path(setup_job)
        setup_request = json.loads(setup_request_path.read_text(encoding="utf-8"))
        setup_request["operation"] = "export"
        setup_request["payload"] = {"validateOnly": False}
        dump_json(setup_request_path, setup_request)
        seed_job_managed_resources(fake.state, setup_job)
        setup_start = len(fake.state.calls)
        setup_run = make_exporter(setup_job)
        if setup_run.run() != "SUCCEEDED":
            raise RuntimeError("Fake setup export failed")
        setup_actions = [row["action"] for row in fake.state.calls[setup_start:]]
        setup_readback = json.loads((setup_run.writer.output_root / "readback.json").read_text(encoding="utf-8"))

        semantic_job = build_language_both_job(output_root / "jobs" / "semantic-readback")
        configure(semantic_job, fake.endpoint, fake.state.profile)
        semantic_job = prepare_semantic_readback_job(semantic_job)
        semantic_request_path = request_path(semantic_job)
        semantic_request = json.loads(semantic_request_path.read_text(encoding="utf-8"))
        for expected, observed in zip(semantic_request["payload"]["expectedCards"], setup_readback["cards"], strict=True):
            expected["noteId"] = observed["notesInfo"]["noteId"]
            expected["ankiCardId"] = observed["notesInfo"]["cardIds"][0]
        semantic_request["idempotencyKey"] = exporter.sha256_bytes(exporter.json_bytes(semantic_request["payload"]))
        dump_json(semantic_request_path, semantic_request)

        measured_start = len(fake.state.calls)
        process = subprocess.run(
            [str(exe), "run", "--request", str(semantic_request_path)],
            cwd=semantic_job["root"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
        )
        measured_actions = [row["action"] for row in fake.state.calls[measured_start:]]
        if process.returncode != 0:
            raise RuntimeError(f"Formal semantic-readback failed: exit={process.returncode}; stderr={process.stderr}")
        if set(measured_actions) & MUTATING:
            raise RuntimeError(f"Semantic readback mutated Fake Anki: {measured_actions!r}")
        result, result_path = read_result(semantic_job)
        if result.get("status") != "completed" or result.get("sideEffects") != []:
            raise RuntimeError("Semantic readback result is not completed/sideEffects=[]")
        output_ref = result["outputs"][0]
        if output_ref.get("kind") != "anki-semantic-readback":
            raise RuntimeError("Semantic readback output kind is wrong")
        artifact_path = semantic_job["root"] / output_ref["manifestPath"]
        if exporter.sha256_file(artifact_path) != output_ref["expectedSha256"]:
            raise RuntimeError("Semantic readback artifact manifest SHA is wrong")
        artifact = json.loads(artifact_path.read_text(encoding="utf-8"))
        readback_path = semantic_job["root"] / artifact["metadata"]["readback"]["path"]
        readback = json.loads(readback_path.read_text(encoding="utf-8"))
        if readback.get("cardCount") != 2:
            raise RuntimeError("Semantic readback did not return two cards")

        cards = []
        for ordinal, (expected, observed) in enumerate(zip(semantic_request["payload"]["expectedCards"], readback["cards"], strict=True)):
            note = observed["notesInfo"]
            card = observed["cardsInfo"][0]
            if (
                observed["ordinal"] != ordinal
                or observed["sourceCardId"] != expected["cardId"]
                or note["fields"]["Front"] != expected["front"]
                or note["fields"]["Back"] != expected["back"]
                or note["noteId"] != expected["noteId"]
                or card["cardId"] != expected["ankiCardId"]
                or card["deckName"] != semantic_request["payload"]["expectedDeck"]
            ):
                raise RuntimeError(f"Semantic mismatch at ordinal {ordinal}")
            cards.append({
                "ordinal": ordinal,
                "stableCardId": expected["cardId"],
                "front": expected["front"],
                "back": expected["back"],
                "deck": card["deckName"],
                "noteId": note["noteId"],
                "cardId": card["cardId"],
                "outcome": observed["outcome"],
            })

        receipt = {
            "schemaVersion": "export.anki.formal-semantic-readback-canary.v1",
            "status": "passed",
            "worker": {"workerId": "export.anki", "workerVersion": "1.5.0"},
            "release": {
                "root": str(release_root),
                "manifest": str(release_manifest),
                "manifestSha256": exporter.sha256_file(release_manifest),
                "exe": str(exe),
                "exeSha256": exporter.sha256_file(exe),
                "descriptor": str(descriptor_path),
                "descriptorSha256": exporter.sha256_file(descriptor_path),
            },
            "provider": {"kind": "fake-ankiconnect", "realAnkiConnectCalls": 0},
            "setup": {
                "scope": "fake-provider-population-only",
                "actions": setup_actions,
                "mutatingActions": [action for action in setup_actions if action in MUTATING],
            },
            "semanticReadback": {
                "scope": "measured-provider-read-only-attempt",
                "actions": measured_actions,
                "mutatingActions": [],
                "externalMutation": False,
                "priorScenarioWritesInferred": False,
                "workerRequest": str(semantic_request_path),
                "workerRequestSha256": exporter.sha256_file(semantic_request_path),
                "workerResult": str(result_path),
                "workerResultSha256": exporter.sha256_file(result_path),
                "artifactManifest": str(artifact_path),
                "artifactManifestSha256": exporter.sha256_file(artifact_path),
                "readback": str(readback_path),
                "readbackSha256": exporter.sha256_file(readback_path),
            },
            "assertions": {
                "exactCardinality": True,
                "exactStableIdentity": True,
                "exactOrder": True,
                "exactFrontBack": True,
                "exactDeck": True,
                "exactNoteCardOwnership": True,
                "semanticAttemptHasNoMutatingActions": True,
            },
            "cards": cards,
        }
        receipt_path = output_root / "receipt.json"
        write_json_once(receipt_path, receipt)
        print(json.dumps({
            "status": "passed",
            "receipt": str(receipt_path),
            "receiptSha256": exporter.sha256_file(receipt_path).upper(),
            "workerResult": str(result_path),
            "workerResultSha256": exporter.sha256_file(result_path).upper(),
            "readback": str(readback_path),
            "readbackSha256": exporter.sha256_file(readback_path).upper(),
            "measuredActions": measured_actions,
            "cards": cards,
        }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
