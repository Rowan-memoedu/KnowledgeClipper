#!/usr/bin/env python3
"""Run an authority-v1.2.2-bound Content -> Core -> Anki idempotent Canary."""

from __future__ import annotations

import argparse
import collections
import json
import os
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
import run_content_live_destination_canary as live_helpers


CANARY_DECK = "零碎知识剪藏 Canary"
AUTHORITY_VERSION = "1.2.2"
AUTHORITY_SLUG = "v122"
CONTENT_WORKER_VERSION = "0.5.2"
ANKI_WORKER_VERSION = "1.1.4"
AUTHORITY_ROOT = PROJECT_ROOT / "contracts" / "authority" / "worker-registry" / "v1.2.2-20260828T012500Z"
CHECKPOINT_PATH = AUTHORITY_ROOT / "checkpoint.json"
REGISTRY_PATH = AUTHORITY_ROOT / "worker-registry.v1.json"
DEPENDENCY_PATH = AUTHORITY_ROOT / "dependency-manifest.v1.json"
RELEASE_ROOT = PROJECT_ROOT / "releases" / "knowledge-clipper-authority-v1.2.2-20260828T012500Z" / "win-x64"
ORCHESTRATOR = RELEASE_ROOT / "orchestrator" / "KnowledgeClipper.Orchestrator.exe"
CONTENT_EXE = RELEASE_ROOT / "workers" / "content" / "KnowledgeClipper.Content.Worker.exe"
ANKI_EXE = RELEASE_ROOT / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
PROFILE_PATH = RELEASE_ROOT / "contracts" / "core" / "v2.1" / "profile-resolver.v2.1.json"

SOURCE_ROOT = PROJECT_ROOT / "content-worker" / "canary" / "jobs" / "20260826T054942440Z-offline-language-practice-v2-live-78587002"
SOURCE_RECEIPT = SOURCE_ROOT / "canary-receipt.json"
DESTINATION_RECEIPT = WORKER_ROOT / "canary" / "live-destination" / "20260826T060023Z-fb5bfb91" / "receipt.json"

EXPECTED = {
    "checkpointSha256": "7B2EFE4D6A144494B0C18F0B84150906BB0F229374DD82F6BB83C6136A2DBFA5",
    "releaseFiles": 79,
    "releaseBytes": 248977927,
    "releaseTreeSha256": "F15F21E5489B4002086B0664144606A7E2EF5BD40E1AA02AB90F34EC277C2503",
    "registrySha256": "6E0486D733103F7EF9581743C139D65E65F9C4F56397F571402590C982F6A8F6",
    "dependencySha256": "414B9B9A0AC18EB48E8412B1EF4AC11D435FBE8D750C2944DB03347ABC91C842",
    "orchestratorSha256": "FAE5F351719234B03C0F4FD58555AF4BA800E4E83FFCEFAB46B15C69923BDA9D",
    "contentSha256": "F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8",
    "ankiSha256": "850BACAB0F3C86D84AE3B3A415AE780C079C5F98475E76D9C9F3E2D25DDBA0DB",
    "sourceCardBundleSha256": "E90E094EB52AF27D4C8BFA33ACF1F2BACC16A1917ADC6C674950E95539DAD0DF",
    "sourceReceiptSha256": "978EC4A0AC7E517EB6B71675C434BA3F63D7CE0FF4752AC17DC64DA12EA44851",
    "destinationReceiptSha256": "8D12EDE16B730BDEEEA1E8F094D179E18FA362ABB2BA834D8D0DF32D19C0C31C",
}
EXPECTED_TEMPLATES = ["language_context", "language_writing", "language_context", "language_writing"]
MUTATING_ACTIONS = {
    "createModel", "createDeck", "storeMediaFile", "addNote", "updateNoteFields", "addTags", "changeDeck",
    "deleteDecks", "deleteNotes", "deleteMediaFile",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--endpoint", default="http://127.0.0.1:8765")
    parser.add_argument("--evidence-root", type=Path)
    parser.add_argument("--reconcile-attempt", type=Path)
    return parser.parse_args()


def read_json(path: Path) -> dict[str, Any]:
    with open(exporter.native_fs_path(path), "r", encoding="utf-8") as stream:
        value = json.load(stream)
    if not isinstance(value, dict):
        raise RuntimeError(f"Expected JSON object: {path}")
    return value


def read_ndjson(path: Path) -> list[dict[str, Any]]:
    output: list[dict[str, Any]] = []
    with open(exporter.native_fs_path(path), "r", encoding="utf-8") as stream:
        for line in stream:
            if line.strip():
                value = json.loads(line)
                if isinstance(value, dict):
                    output.append(value)
    return output


def rel(path: Path, root: Path = PROJECT_ROOT) -> str:
    return path.resolve().relative_to(root.resolve()).as_posix()


def copy_once(source: Path, target: Path) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    with open(exporter.native_fs_path(source), "rb") as input_stream:
        payload = input_stream.read()
    with open(exporter.native_fs_path(target), "xb") as output_stream:
        output_stream.write(payload)


def write_text_once(path: Path, value: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(exporter.native_fs_path(path), "x", encoding="utf-8", newline="\n") as stream:
        stream.write(value)


def sha(path: Path) -> str:
    return exporter.sha256_file(path).upper()


def native_to_path(value: str) -> Path:
    if value.startswith("\\\\?\\UNC\\"):
        return Path("\\\\" + value[8:])
    if value.startswith("\\\\?\\"):
        return Path(value[4:])
    return Path(value)


def release_tree() -> dict[str, Any]:
    native_root = exporter.native_fs_path(RELEASE_ROOT)
    rows: list[tuple[str, int, str]] = []
    for current, directories, files in os.walk(native_root):
        directories.sort()
        for name in sorted(files):
            native_path = os.path.join(current, name)
            normal_path = native_to_path(native_path)
            relative = normal_path.relative_to(RELEASE_ROOT).as_posix()
            rows.append((relative, os.path.getsize(native_path), exporter.sha256_file(normal_path).upper()))
    rows.sort(key=lambda item: item[0])
    digest = exporter.sha256_bytes("\n".join(f"{path}\t{size}\t{hash_value.lower()}" for path, size, hash_value in rows).encode("utf-8")).upper()
    return {"files": len(rows), "bytes": sum(item[1] for item in rows), "treeSha256": digest}


def run_process(command: list[str], cwd: Path, stdout_path: Path, stderr_path: Path) -> dict[str, Any]:
    process = subprocess.run(command, cwd=cwd, capture_output=True, text=True, encoding="utf-8", check=False)
    write_text_once(stdout_path, process.stdout)
    write_text_once(stderr_path, process.stderr)
    return {
        "exitCode": process.returncode,
        "stdoutPath": rel(stdout_path),
        "stdoutSha256": sha(stdout_path),
        "stderrPath": rel(stderr_path),
        "stderrSha256": sha(stderr_path),
    }


def describe(executable: Path, worker_id: str, version: str, evidence_root: Path) -> dict[str, Any]:
    process = subprocess.run([str(executable), "describe", "--json"], cwd=executable.parent, capture_output=True, text=True, encoding="utf-8", check=False)
    if process.returncode != 0:
        raise RuntimeError(f"describe failed for {executable}: {process.stderr}")
    value = json.loads(process.stdout)
    if value.get("workerId") != worker_id or value.get("workerVersion") != version:
        raise RuntimeError(f"candidate identity mismatch for {worker_id}: {value!r}")
    path = evidence_root / "authority" / f"{worker_id}.describe.json"
    exporter.write_json_atomic_once(path, value)
    return {"path": rel(path), "sha256": sha(path), "identity": {"workerId": worker_id, "workerVersion": version}}


def verify_authority(evidence_root: Path) -> dict[str, Any]:
    checkpoint = read_json(CHECKPOINT_PATH)
    if checkpoint.get("admissionStatus") != "authority-admitted":
        raise RuntimeError("checkpoint is not authority-admitted")
    checks = {
        "checkpoint": sha(CHECKPOINT_PATH) == EXPECTED["checkpointSha256"],
        "registry": sha(REGISTRY_PATH) == EXPECTED["registrySha256"],
        "dependency": sha(DEPENDENCY_PATH) == EXPECTED["dependencySha256"],
        "orchestrator": sha(ORCHESTRATOR) == EXPECTED["orchestratorSha256"],
        "content": sha(CONTENT_EXE) == EXPECTED["contentSha256"],
        "anki": sha(ANKI_EXE) == EXPECTED["ankiSha256"],
    }
    tree = release_tree()
    checks["releaseTree"] = tree == {"files": EXPECTED["releaseFiles"], "bytes": EXPECTED["releaseBytes"], "treeSha256": EXPECTED["releaseTreeSha256"]}
    if not all(checks.values()):
        raise RuntimeError(f"authority candidate hash mismatch: {checks!r}; tree={tree!r}")
    validation = run_process(
        [str(ORCHESTRATOR), "validate-registry", "--registry", str(REGISTRY_PATH), "--release-root", str(RELEASE_ROOT)],
        ORCHESTRATOR.parent,
        evidence_root / "authority" / "validate-registry.stdout.json",
        evidence_root / "authority" / "validate-registry.stderr.log",
    )
    if validation["exitCode"] != 0:
        raise RuntimeError("exact candidate validate-registry failed")
    return {
        "checkpoint": {"path": rel(CHECKPOINT_PATH), "sha256": EXPECTED["checkpointSha256"], "checkpointId": checkpoint.get("checkpointId")},
        "registry": {"path": rel(REGISTRY_PATH), "sha256": sha(REGISTRY_PATH), "version": AUTHORITY_VERSION},
        "dependencyManifest": {"path": rel(DEPENDENCY_PATH), "sha256": sha(DEPENDENCY_PATH)},
        "release": {"root": rel(RELEASE_ROOT), **tree},
        "validation": validation,
        "executables": {
            "orchestrator": {"path": rel(ORCHESTRATOR), "sha256": sha(ORCHESTRATOR)},
            "content": {"path": rel(CONTENT_EXE), "sha256": sha(CONTENT_EXE), **describe(CONTENT_EXE, "content.deepseek", CONTENT_WORKER_VERSION, evidence_root)},
            "anki": {"path": rel(ANKI_EXE), "sha256": sha(ANKI_EXE), **describe(ANKI_EXE, "export.anki", ANKI_WORKER_VERSION, evidence_root)},
        },
        "verifierCaveat": f"The supplemental verify_authority_registry_checkpoint.py expects a non-authoritative registry-candidate copy absent from the admitted {EXPECTED['releaseFiles']}-file tree; exact candidate validate-registry is the production admission check used here.",
    }


def source_artifacts() -> dict[str, Any]:
    if sha(SOURCE_RECEIPT) != EXPECTED["sourceReceiptSha256"]:
        raise RuntimeError("frozen live source receipt changed")
    if sha(DESTINATION_RECEIPT) != EXPECTED["destinationReceiptSha256"]:
        raise RuntimeError("frozen destination receipt changed")
    source_receipt = read_json(SOURCE_RECEIPT)
    payload_paths: dict[str, Path] = {}
    for kind in ("card-bundle", "learning-bundle"):
        outputs = [item for item in source_receipt.get("outputs", []) if item.get("kind") == kind]
        if len(outputs) != 1:
            raise RuntimeError(f"frozen source receipt must identify one {kind}")
        manifest_path = SOURCE_ROOT / outputs[0]["manifestPath"]
        if exporter.sha256_file(manifest_path) != str(outputs[0]["manifestSha256"]).lower():
            raise RuntimeError(f"frozen {kind} manifest changed")
        manifest = read_json(manifest_path)
        if len(manifest.get("files", [])) != 1:
            raise RuntimeError(f"frozen {kind} manifest must contain one payload")
        payload_path = SOURCE_ROOT / manifest["files"][0]["path"]
        if exporter.sha256_file(payload_path) != str(manifest["files"][0]["sha256"]).lower():
            raise RuntimeError(f"frozen {kind} payload changed")
        payload_paths[kind] = payload_path
    card_path = payload_paths["card-bundle"]
    learning_path = payload_paths["learning-bundle"]
    if sha(card_path) != EXPECTED["sourceCardBundleSha256"]:
        raise RuntimeError("frozen live CardBundle changed")
    bundle = read_json(card_path)
    learning = read_json(learning_path)
    destination = read_json(DESTINATION_RECEIPT)
    cards = bundle.get("cards", [])
    if [item.get("template") for item in cards] != EXPECTED_TEMPLATES:
        raise RuntimeError("frozen source no longer contains the ordered both pairs")
    if [item.get("card_id") for item in cards] != destination.get("cardSemantics", {}).get("orderedCardIds"):
        raise RuntimeError("source and prior destination identities differ")
    responses = {str(item["call_key"]): item["response"] for item in learning.get("ai_trace", [])}
    expected_keys = {
        "cleaning:item-language-zh", "classification:item-language-zh", "card_generation:item-language-zh:unit-zh-yidou",
        "language_writing:item-language-zh:unit-zh-yidou", "cleaning:item-language-en", "classification:item-language-en",
        "card_generation:item-language-en:unit-1", "language_writing:item-language-en:unit-1",
    }
    if set(responses) != expected_keys:
        raise RuntimeError("frozen live provider trace cannot be replayed exactly")
    return {
        "cardPath": card_path,
        "learningPath": learning_path,
        "bundle": bundle,
        "learning": learning,
        "destination": destination,
        "replay": {"schema_version": "provider-replay/1.0.0", "responses": responses},
    }


class AnkiConnect:
    def __init__(self, endpoint: str) -> None:
        self.endpoint = endpoint
        self.counts: collections.Counter[str] = collections.Counter()

    def invoke(self, action: str, params: dict[str, Any] | None = None) -> Any:
        self.counts[action] += 1
        request = urllib.request.Request(
            self.endpoint,
            data=exporter.json_bytes({"action": action, "version": exporter.API_VERSION, "params": params or {}}),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(request, timeout=15) as response:
            payload = json.loads(response.read().decode("utf-8"))
        if not isinstance(payload, dict) or payload.get("error") is not None or "result" not in payload:
            raise RuntimeError(f"AnkiConnect {action} failed")
        return payload["result"]


def exact_readback(client: AnkiConnect, source: dict[str, Any], expected_rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    expected_by_card = {row["cardId"]: row for row in expected_rows}
    output: list[dict[str, Any]] = []
    for card in source["cards"]:
        card_id = str(card["card_id"])
        note_ids = client.invoke("findNotes", {"query": f'RQC_ID:"{card_id}"'})
        if not isinstance(note_ids, list) or len(note_ids) != 1:
            raise RuntimeError(f"expected exactly one existing Canary note: {card_id}")
        note = client.invoke("notesInfo", {"notes": note_ids})[0]
        fields = {key: value["value"] for key, value in note.get("fields", {}).items()}
        cards = client.invoke("cardsInfo", {"cards": note.get("cards", [])})
        if len(cards) != 1 or cards[0].get("deckName") != CANARY_DECK:
            raise RuntimeError(f"existing identity is not isolated to {CANARY_DECK}: {card_id}")
        expected = expected_by_card[card_id]
        if fields.get("Front") != expected["ankiFront"] or fields.get("Back") != expected["ankiBack"]:
            raise RuntimeError(f"existing Canary fields changed: {card_id}")
        output.append({
            "cardId": card_id,
            "template": card["template"],
            "noteId": int(note_ids[0]),
            "cardIds": [int(cards[0]["cardId"])],
            "deck": cards[0]["deckName"],
            "ankiFront": fields["Front"],
            "ankiBack": fields["Back"],
        })
    if len({row["noteId"] for row in output}) != 4 or len({row["cardIds"][0] for row in output}) != 4:
        raise RuntimeError("existing Canary does not contain four unique notes and cards")
    return output


def deck_inventory(client: AnkiConnect) -> dict[str, Any]:
    names = client.invoke("deckNames")
    if not isinstance(names, list) or not all(isinstance(name, str) for name in names):
        raise RuntimeError("Anki deckNames returned an invalid inventory")
    ordered = sorted(names)
    stats = client.invoke("getDeckStats", {"decks": ordered})
    if not isinstance(stats, dict):
        raise RuntimeError("Anki getDeckStats returned an invalid inventory")
    snapshot = {"deckNames": ordered, "stats": stats}
    return {"snapshot": snapshot, "sha256": exporter.sha256_bytes(exporter.json_bytes(snapshot)).upper(), "deckCount": len(ordered)}


def file_entry(job_root: Path, path: Path, media_type: str) -> dict[str, Any]:
    return {"path": path.relative_to(job_root).as_posix(), "sha256": exporter.sha256_file(path), "bytes": path.stat().st_size, "mediaType": media_type}


def build_job(evidence_root: Path, endpoint: str, profile: str, source: dict[str, Any]) -> tuple[Path, dict[str, Any]]:
    job_root = evidence_root / "job"
    content_dir = job_root / "inputs" / "content"
    content_artifact_id = f"content-input-authority-{AUTHORITY_SLUG}"
    config_artifact_id = f"anki-config-authority-{AUTHORITY_SLUG}"
    config_dir = job_root / "artifacts" / config_artifact_id
    content_dir.mkdir(parents=True)
    config_dir.mkdir(parents=True)
    for name in ("standard-input.v2.json",):
        copy_once(SOURCE_ROOT / "inputs" / "content" / name, content_dir / name)
    for name in ("zh.md", "en.md"):
        copy_once(SOURCE_ROOT / "inputs" / "content" / "materials" / name, content_dir / "materials" / name)
    replay_path = content_dir / "provider-replay.v1.json"
    exporter.write_json_atomic_once(replay_path, source["replay"])
    config_path = config_dir / "anki-export.v1.json"
    config = live_helpers.write_private_config(config_path, endpoint, profile)
    created = exporter.utc_now()
    producer = {"stageId": "authority-canary.fixture", "workerId": "authority-canary.fixture", "workerVersion": "1.0.0", "attemptId": "fixture-build"}
    content_manifest = {
        "schemaVersion": "1.0.0", "artifactId": content_artifact_id, "kind": "content-input", "contractVersion": "1.0.0",
        "mediaType": "application/json", "files": [
            file_entry(job_root, content_dir / "standard-input.v2.json", "application/json"),
            file_entry(job_root, content_dir / "materials" / "zh.md", "text/markdown; charset=utf-8"),
            file_entry(job_root, content_dir / "materials" / "en.md", "text/markdown; charset=utf-8"),
            file_entry(job_root, replay_path, "application/json"),
        ], "createdAt": created, "producer": producer, "inputRefs": [], "dataClass": "personal",
        "metadata": {"entrypoint": "inputs/content/standard-input.v2.json", "providerReplay": "inputs/content/provider-replay.v1.json", "sourceAuthorityReceiptSha256": EXPECTED["sourceReceiptSha256"]},
    }
    content_manifest_path = job_root / "artifacts" / content_artifact_id / "artifact.json"
    exporter.write_json_atomic_once(content_manifest_path, content_manifest)
    config_manifest = {
        "schemaVersion": "1.0.0", "artifactId": config_artifact_id, "kind": "anki-export-config", "contractVersion": "1.0.0",
        "mediaType": "application/json", "files": [file_entry(job_root, config_path, "application/json")], "createdAt": created,
        "producer": producer, "inputRefs": [], "dataClass": "personal", "metadata": {"entrypoint": f"artifacts/{config_artifact_id}/anki-export.v1.json"},
    }
    config_manifest_path = config_dir / "artifact.json"
    exporter.write_json_atomic_once(config_manifest_path, config_manifest)
    refs = {
        "content": {"artifactRef": content_artifact_id, "kind": "content-input", "contractVersion": "1.0.0", "manifestPath": f"artifacts/{content_artifact_id}/artifact.json", "expectedSha256": exporter.sha256_file(content_manifest_path)},
        "config": {"artifactRef": config_artifact_id, "kind": "anki-export-config", "contractVersion": "1.0.0", "manifestPath": f"artifacts/{config_artifact_id}/artifact.json", "expectedSha256": exporter.sha256_file(config_manifest_path)},
    }
    task_manifest = {
        "schemaVersion": "2.0.0", "taskId": f"authority-{AUTHORITY_SLUG}-anki-idempotent", "createdAt": created,
        "createdBy": {"application": "KnowledgeClipper.Anki.AuthorityCanary", "version": "1.0.0"}, "intent": "clip.card",
        "executionProfile": "cards", "destinations": ["anki"],
        "inputRefs": [{key: value for key, value in item.items() if key != "manifestPath"} for item in refs.values()],
        "requestedOutputs": ["card-bundle", "anki-export-evidence"], "initialPlanRef": "plans/full-authority-anki.v2.json", "retentionClass": "diagnostic",
    }
    exporter.write_json_atomic_once(job_root / "task.manifest.v2.json", task_manifest)
    legacy_manifest = {
        "schemaVersion": "1.0.0", "taskId": task_manifest["taskId"], "createdAt": created,
        "createdBy": task_manifest["createdBy"], "intent": "clip.card", "executionProfile": "cards-anki",
        "inputRefs": task_manifest["inputRefs"], "requestedOutputs": ["anki-export-evidence"],
        "initialPlanRef": "plans/full-authority-anki.v2.json", "retentionClass": "evidence",
        "metadata": {"compatibilityAliasFor": "task.manifest.v2.json", "consumer": f"export.anki/{ANKI_WORKER_VERSION}"},
    }
    exporter.write_json_atomic_once(job_root / "task.manifest.v1.json", legacy_manifest)
    profile = read_json(PROFILE_PATH)["destinationConfigurations"]["anki"]["projection"]
    content_stage = {
        "stageId": "content.understand-and-card", "workerId": "content.deepseek", "operation": "understand-and-card",
        "dependsOn": [], "inputRefs": [refs["content"]], "outputKinds": ["learning-bundle", "card-bundle"], "timeoutMs": 300000,
        "retryPolicy": {"maxAttempts": 1, "backoffMs": 0}, "configSnapshot": {"providerMode": "replay-from-live-authority"},
        "payload": {"schemaVersion": "content-run/1.0.0", "contentInputPath": "inputs/content/standard-input.v2.json", "providerMode": "replay", "replayPath": "inputs/content/provider-replay.v1.json"},
        "inputBindings": [], "inputAdapter": None,
    }
    anki_stage = {
        "stageId": "anki-export", "workerId": "export.anki", "operation": "export", "dependsOn": ["content.understand-and-card"],
        "inputRefs": [refs["config"]], "outputKinds": ["anki-export-evidence"], "timeoutMs": 180000,
        "retryPolicy": {"maxAttempts": 1, "backoffMs": 0}, "configSnapshot": {},
        "payload": {"validateOnly": False, "canaryMode": f"authority-v{AUTHORITY_VERSION}-idempotent-replay"},
        "inputBindings": [
            {"fromStageId": "content.understand-and-card", "kind": "card-bundle", "required": True},
            {"fromStageId": "content.understand-and-card", "kind": "learning-bundle", "required": True},
        ],
        "inputAdapter": {"type": "content-to-anki.v1", "targetKind": "card-bundle", "mappingSnapshot": profile},
    }
    common = {"schemaVersion": "2.0.0", "planVersion": "2.0.0", "maxConcurrency": 1}
    preflight = {**common, "taskId": f"authority-{AUTHORITY_SLUG}-content-preflight", "stages": [content_stage], "routing": {"processingProfile": "cards", "destinations": [], "source": "task-manifest.v2"}}
    full = {**common, "taskId": task_manifest["taskId"], "stages": [content_stage, anki_stage], "routing": {"processingProfile": "cards", "destinations": ["anki"], "source": "task-manifest.v2"}}
    exporter.write_json_atomic_once(job_root / "plans" / "content-preflight.v2.json", preflight)
    exporter.write_json_atomic_once(job_root / "plans" / "full-authority-anki.v2.json", full)
    return job_root, {"config": config, "configPath": config_path, "replayPath": replay_path, "refs": refs}


def verify_reconcile_attempt(path: Path | None) -> dict[str, Any] | None:
    if path is None:
        return None
    attempt_root = path.resolve()
    result_path = attempt_root / "result.json"
    stderr_path = attempt_root / "stderr.log"
    result = read_json(result_path)
    if result.get("status") != "unknown_external_state" or result.get("error", {}).get("retryable") is not False:
        raise RuntimeError("reconcile source is not a preserved non-retryable unknown_external_state attempt")
    if "task.manifest.v1.json" not in open(exporter.native_fs_path(stderr_path), "r", encoding="utf-8").read():
        raise RuntimeError("reconcile source does not prove the known pre-API compatibility failure")
    return {
        "attemptPath": rel(attempt_root), "resultPath": rel(result_path), "resultSha256": sha(result_path),
        "stderrPath": rel(stderr_path), "stderrSha256": sha(stderr_path), "status": "unknown_external_state",
        "retryable": False, "reconciliationRequired": True,
    }


def run_pipeline(job_root: Path, evidence_root: Path, plan_name: str, run_id: str) -> dict[str, Any]:
    command = [
        str(ORCHESTRATOR), "run", "--pipeline", str(job_root / "plans" / plan_name), "--registry", str(REGISTRY_PATH),
        "--release-root", str(RELEASE_ROOT), "--job-root", str(job_root), "--run-id", run_id,
    ]
    record = run_process(command, ORCHESTRATOR.parent, evidence_root / "logs" / f"{run_id}.stdout.json", evidence_root / "logs" / f"{run_id}.stderr.log")
    record["command"] = [rel(ORCHESTRATOR), "run", "--pipeline", rel(job_root / "plans" / plan_name), "--registry", rel(REGISTRY_PATH), "--release-root", rel(RELEASE_ROOT), "--job-root", rel(job_root), "--run-id", run_id]
    if record["exitCode"] != 0:
        raise RuntimeError(f"exact candidate pipeline failed: {run_id}")
    return record


def stage_result(job_root: Path, run_id: str, stage_id: str, worker_id: str, version: str) -> tuple[Path, dict[str, Any]]:
    attempts_root = job_root / "runs" / run_id / "stages" / stage_id / "attempts"
    names = [name for name in os.listdir(exporter.native_fs_path(attempts_root)) if name.startswith("attempt-")]
    paths = [attempts_root / name / "result.json" for name in names if os.path.isfile(exporter.native_fs_path(attempts_root / name / "result.json"))]
    if len(paths) != 1:
        raise RuntimeError(f"expected one attempt result for {run_id}/{stage_id}")
    value = read_json(paths[0])
    if value.get("status") != "completed" or value.get("worker") != {"id": worker_id, "version": version}:
        raise RuntimeError(f"candidate stage identity/status mismatch: {value.get('worker')!r}/{value.get('status')!r}")
    return paths[0], value


def output_payload(job_root: Path, result: dict[str, Any], kind: str) -> tuple[Path, dict[str, Any]]:
    refs = [item for item in result.get("outputs", []) if item.get("kind") == kind]
    if len(refs) != 1:
        raise RuntimeError(f"expected one {kind} output")
    manifest_path = job_root / refs[0]["manifestPath"]
    if exporter.sha256_file(manifest_path) != str(refs[0]["expectedSha256"]).lower():
        raise RuntimeError(f"{kind} output manifest SHA mismatch")
    manifest = read_json(manifest_path)
    files = manifest.get("files", [])
    if len(files) != 1:
        raise RuntimeError(f"{kind} output manifest must contain one payload")
    payload_path = job_root / files[0]["path"]
    if exporter.sha256_file(payload_path) != str(files[0]["sha256"]).lower():
        raise RuntimeError(f"{kind} output payload SHA mismatch")
    return payload_path, read_json(payload_path)


def semantic_cards(bundle: dict[str, Any]) -> list[dict[str, Any]]:
    return [{
        "cardId": card["card_id"], "template": card["template"], "itemId": card["item_id"], "unitId": card["unit_id"],
        "variantGroupId": card.get("variant_group_id"), "categoryId": card["category_id"], "subtype": card["subtype"],
        "front": card["front_blocks"][0]["text"], "back": card["back_blocks"][0]["text"],
    } for card in bundle["cards"]]


def anki_attempt(job_root: Path, result_path: Path, result: dict[str, Any]) -> dict[str, Any]:
    attempt_root = result_path.parent
    operations_path = attempt_root / "outputs" / "operations.ndjson"
    receipt_path = attempt_root / "outputs" / "receipt.json"
    operations = read_ndjson(operations_path)
    module_receipt = read_json(receipt_path)
    summary = live_helpers.operation_summary(operations)
    counts = module_receipt.get("counts", {})
    if counts.get("succeeded") != 0 or counts.get("already_current") != 4 or counts.get("failed") != 0:
        raise RuntimeError(f"idempotent replay did not converge as 0-new/4-current: {counts!r}")
    if summary["mutatingActions"]:
        raise RuntimeError(f"idempotent replay mutated Anki: {summary['mutatingActions']!r}")
    return {
        "resultPath": rel(result_path), "resultSha256": sha(result_path), "moduleReceiptPath": rel(receipt_path),
        "moduleReceiptSha256": sha(receipt_path), "counts": counts, "operations": summary,
    }


def projection_payload(job_root: Path, result: dict[str, Any]) -> tuple[Path, dict[str, Any]]:
    refs = [item for item in result.get("inputRefs", []) if item.get("kind") == "card-bundle" and "adapters/" in str(item.get("manifestPath", ""))]
    if len(refs) != 1:
        raise RuntimeError("Anki request/result did not bind one Core-derived card-bundle")
    manifest_path = job_root / refs[0]["manifestPath"]
    manifest = read_json(manifest_path)
    if manifest.get("producer", {}).get("workerId") != "orchestrator.core-adapter":
        raise RuntimeError("Anki projection is not produced by Core adapter")
    payload_path = job_root / manifest["metadata"]["entrypoint"]
    return payload_path, read_json(payload_path)


def write_readable(path: Path, source: dict[str, Any], semantics: list[dict[str, Any]], projected: dict[str, Any], readback: list[dict[str, Any]]) -> None:
    items = {item["item_id"]: item for item in source["learning"]["items"]}
    projected_by_id = {item["card_id"]: item for item in projected["cards"]}
    readback_by_id = {item["cardId"]: item for item in readback}
    lines = [
        f"# authority v{AUTHORITY_VERSION} · Core → content.deepseek → export.anki 幂等 Canary", "",
        f"- 目标 deck：`{CANARY_DECK}`（既有隔离牌组）", f"- 候选运行：Orchestrator → Content {CONTENT_WORKER_VERSION} replay → Core content-to-anki.v1 → Anki {ANKI_WORKER_VERSION}", "- 结果：0 新增，4 existing/current；删除/清理 0", "",
    ]
    for index, card in enumerate(semantics, 1):
        item = items[card["itemId"]]
        unit = next(value for value in item["units"] if value["unit_id"] == card["unitId"])
        projection = projected_by_id[card["cardId"]]
        row = readback_by_id[card["cardId"]]
        lines.extend([
            f"## {index}. `{card['template']}` · `{card['cardId']}`", "",
            f"- 原始文本：{json.dumps(item['source_segments'][0]['text'], ensure_ascii=False)}",
            f"- 清洗后：{json.dumps(item['cleaning']['cleaned_text'], ensure_ascii=False)}",
            f"- 分类：`{unit['category_id']}` / `{unit['subtype']}`；target=`{unit.get('target_term', '')}`",
            f"- 最终 Front：{json.dumps(card['front'], ensure_ascii=False)}",
            f"- 最终 Back：{json.dumps(card['back'], ensure_ascii=False)}",
            f"- Core 投影：route=`{projection.get('deck_route_key', projection.get('deck_route'))}`；noteType=`{projection['note_type_key']}`；fields=`{json.dumps(projection['fields'], ensure_ascii=False, sort_keys=True)}`",
            f"- Anki outcome：`already_current`；note ID=`{row['noteId']}`；card ID=`{row['cardIds'][0]}`；deck=`{row['deck']}`", "",
        ])
    write_text_once(path, "\n".join(lines))


def main() -> int:
    args = parse_args()
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    evidence_root = (args.evidence_root or (WORKER_ROOT / "canary" / f"authority-v{AUTHORITY_VERSION}" / stamp)).resolve()
    evidence_root.mkdir(parents=True, exist_ok=False)
    authority = verify_authority(evidence_root)
    source = source_artifacts()
    reconciles = verify_reconcile_attempt(args.reconcile_attempt)
    client = AnkiConnect(args.endpoint)
    if client.invoke("version") != 6:
        raise RuntimeError("AnkiConnect v6 is required")
    profiles = client.invoke("getProfiles")
    active_profile = client.invoke("getActiveProfile")
    if active_profile not in profiles:
        raise RuntimeError("Anki active profile is unavailable")
    inventory_before = deck_inventory(client)
    before = exact_readback(client, source["bundle"], source["destination"]["readback"])
    before_counts = dict(client.counts)
    job_root, staged = build_job(evidence_root, args.endpoint, active_profile, source)

    preflight_run = run_pipeline(job_root, evidence_root, "content-preflight.v2.json", "run-content-preflight")
    preflight_result_path, preflight_result = stage_result(job_root, "run-content-preflight", "content.understand-and-card", "content.deepseek", CONTENT_WORKER_VERSION)
    _, preflight_bundle = output_payload(job_root, preflight_result, "card-bundle")
    frozen_semantics = semantic_cards(source["bundle"])
    if semantic_cards(preflight_bundle) != frozen_semantics:
        raise RuntimeError("candidate Content 0.5.2 changed the frozen four-card semantics; Anki was not invoked")

    full_run = run_pipeline(job_root, evidence_root, "full-authority-anki.v2.json", "run-authority-anki")
    full_content_result_path, full_content_result = stage_result(job_root, "run-authority-anki", "content.understand-and-card", "content.deepseek", CONTENT_WORKER_VERSION)
    _, full_bundle = output_payload(job_root, full_content_result, "card-bundle")
    if semantic_cards(full_bundle) != frozen_semantics:
        raise RuntimeError("full candidate run changed the frozen card semantics")
    anki_result_path, anki_result = stage_result(job_root, "run-authority-anki", "anki-export", "export.anki", ANKI_WORKER_VERSION)
    attempt = anki_attempt(job_root, anki_result_path, anki_result)
    anki_request = read_json(anki_result_path.parent / "request.json")
    projected_path, projected = projection_payload(job_root, anki_request)
    if [item["card_id"] for item in projected["cards"]] != [item["cardId"] for item in frozen_semantics]:
        raise RuntimeError("Core adapter changed ordered card identities")
    after = exact_readback(client, full_bundle, before)
    inventory_after = deck_inventory(client)
    if after != before:
        raise RuntimeError("same-revision candidate replay changed Anki readback")
    if inventory_after["snapshot"] != inventory_before["snapshot"]:
        raise RuntimeError("Anki deck inventory changed outside the idempotent target readback")
    readable_path = evidence_root / f"readable-authority-{AUTHORITY_SLUG}-idempotent.md"
    write_readable(readable_path, source, frozen_semantics, projected, after)

    module_operations = attempt["operations"]
    receipt = {
        "schemaVersion": f"authority-v{AUTHORITY_VERSION}-anki-idempotent-canary/1.0.0", "status": "passed", "canaryId": stamp,
        "route": f"exact-candidate orchestrator -> content.deepseek {CONTENT_WORKER_VERSION} -> Core content-to-anki.v1 -> export.anki {ANKI_WORKER_VERSION}",
        "authority": authority,
        "source": {
            "providerMode": "replay-from-frozen-live-deepseek-authority", "businessInputByteIdentical": True,
            "receiptPath": rel(SOURCE_RECEIPT), "receiptSha256": sha(SOURCE_RECEIPT),
            "cardBundlePath": rel(source["cardPath"]), "cardBundleSha256": sha(source["cardPath"]),
            "learningBundlePath": rel(source["learningPath"]), "learningBundleSha256": sha(source["learningPath"]),
            "priorDestinationReceiptPath": rel(DESTINATION_RECEIPT), "priorDestinationReceiptSha256": sha(DESTINATION_RECEIPT),
            "replayPath": rel(staged["replayPath"]), "replaySha256": sha(staged["replayPath"]), "providerExternalCalls": 0,
        },
        "reconciliation": {"explicit": reconciles is not None, "reconciles": reconciles, "strategy": "read existing fixed RQC_ID state before dispatch, then converge same revision through a new immutable task/run"},
        "runs": {
            "contentPreflight": {**preflight_run, "resultPath": rel(preflight_result_path), "resultSha256": sha(preflight_result_path), "semanticMatch": True},
            "fullAuthorityAnki": {**full_run, "contentResultPath": rel(full_content_result_path), "contentResultSha256": sha(full_content_result_path), "anki": attempt},
        },
        "coreProjection": {"adapter": "content-to-anki.v1", "profilePath": rel(PROFILE_PATH), "profileSha256": sha(PROFILE_PATH), "payloadPath": rel(projected_path), "payloadSha256": sha(projected_path), "orderPreserved": True},
        "destination": {
            "deck": CANARY_DECK, "configurationSha256": sha(staged["configPath"]),
            "activeProfileSha256": exporter.sha256_bytes(active_profile.encode("utf-8")).upper(), "sensitiveValuesOmitted": True,
            "before": before, "after": after, "newNotes": 0, "existingCurrent": 4, "deleted": 0,
            "allDeckInventory": {"beforeSha256": inventory_before["sha256"], "afterSha256": inventory_after["sha256"], "deckCountBefore": inventory_before["deckCount"], "deckCountAfter": inventory_after["deckCount"], "changedDecks": []},
        },
        "cardSemantics": {"orderedTemplates": EXPECTED_TEMPLATES, "meaningReading": 2, "writing": 2, "bothGroups": 2, "cards": frozen_semantics},
        "apiCalls": {
            "contentProvider": 0, "ankiConnectDirect": sum(client.counts.values()), "ankiConnectWorker": module_operations["calls"],
            "ankiConnectTotal": sum(client.counts.values()) + module_operations["calls"], "directBefore": before_counts,
            "workerByAction": module_operations["byAction"], "mutatingActions": module_operations["mutatingActions"],
        },
        "checks": {
            "authorityBound": True, "exactCandidateProcesses": True, "sourceInputByteIdentical": True, "contentSemanticMatch": True,
            "coreOrderAndFieldsVerified": True, "isolatedDeckOnly": all(item["deck"] == CANARY_DECK for item in after),
            "zeroNewFourCurrent": attempt["counts"].get("succeeded") == 0 and attempt["counts"].get("already_current") == 4,
            "readbackUnchanged": before == after, "noMutatingAction": not module_operations["mutatingActions"], "noDeleteOrCleanup": True,
        },
        "readableEvidence": {"path": rel(readable_path), "sha256": sha(readable_path)},
        "external_api_calls": {"deepseek": 0, "ankiConnect": sum(client.counts.values()) + module_operations["calls"]},
        "finishedAt": exporter.utc_now(),
    }
    if not all(receipt["checks"].values()):
        raise RuntimeError(f"authority Canary checks failed: {receipt['checks']!r}")
    receipt_path = evidence_root / "receipt.json"
    exporter.write_json_atomic_once(receipt_path, receipt)
    print(json.dumps({"status": "passed", "receipt": str(receipt_path), "receiptSha256": sha(receipt_path), "newNotes": 0, "existingCurrent": 4, "noteIds": [item["noteId"] for item in after], "cardIds": [item["cardIds"][0] for item in after]}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
