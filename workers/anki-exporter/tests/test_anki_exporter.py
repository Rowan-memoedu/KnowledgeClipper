import base64
import copy
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from unittest import mock

from jsonschema import Draft202012Validator, FormatChecker, RefResolver


WORKER = Path(__file__).resolve().parents[1]
COMMON_SCHEMAS = WORKER.parents[1] / "contracts" / "v1" / "schemas" / "common"
sys.path.insert(0, str(WORKER))

import anki_exporter as exporter


MODEL_FIELDS = list(exporter.ALL_MODEL_FIELDS)


class FakeAnkiState:
    def __init__(self):
        self.profile = "Canary Profile"
        self.models = {}
        self.model_templates = {}
        self.model_css = {}
        self.decks = {"Default"}
        self.media = {}
        self.notes = {}
        self.next_note = 1000
        self.next_card = 5000
        self.actions = {
            "requestPermission", "version", "getActiveProfile", "apiReflect",
            "modelNames", "modelFieldNames", "modelTemplates", "modelStyling", "createModel", "deckNames", "createDeck",
            "findNotes", "notesInfo", "cardsInfo", "addNote", "updateNoteFields",
            "addTags", "changeDeck", "retrieveMediaFile", "storeMediaFile",
        }
        self.fail_after_add_once = False
        self.fail_after_store_once = False
        self.fail_after_create_model_once = False
        self.fail_after_create_deck_once = False
        self.readback_defect = None
        self.forced_results = {}
        self.calls = []

    def invoke(self, action, params):
        self.calls.append({"action": action, "params": copy.deepcopy(params)})
        if action in self.forced_results:
            return copy.deepcopy(self.forced_results[action])
        if action == "requestPermission":
            return {"permission": "granted", "requireApiKey": False, "version": 6}
        if action == "version":
            return 6
        if action == "getActiveProfile":
            return self.profile
        if action == "apiReflect":
            requested = params.get("actions") or []
            return {"scopes": ["actions"], "actions": [name for name in requested if name in self.actions]}
        if action == "modelNames":
            return sorted(self.models)
        if action == "createModel":
            self.models[params["modelName"]] = list(params["inOrderFields"])
            self.model_templates[params["modelName"]] = {
                item.get("Name", f"Card {index + 1}"): {"Front": item["Front"], "Back": item["Back"]}
                for index, item in enumerate(params["cardTemplates"])
            }
            self.model_css[params["modelName"]] = params["css"]
            if self.fail_after_create_model_once:
                self.fail_after_create_model_once = False
                raise RuntimeError("simulated model response loss after commit")
            return {"id": 1, "name": params["modelName"]}
        if action == "modelFieldNames":
            return list(self.models[params["modelName"]])
        if action == "modelTemplates":
            return copy.deepcopy(self.model_templates[params["modelName"]])
        if action == "modelStyling":
            return {"css": self.model_css[params["modelName"]]}
        if action == "deckNames":
            return sorted(self.decks)
        if action == "createDeck":
            self.decks.add(params["deck"])
            if self.fail_after_create_deck_once:
                self.fail_after_create_deck_once = False
                raise RuntimeError("simulated deck response loss after commit")
            return len(self.decks)
        if action == "retrieveMediaFile":
            data = self.media.get(params["filename"])
            return False if data is None else base64.b64encode(data).decode("ascii")
        if action == "storeMediaFile":
            name = params["filename"]
            data = base64.b64decode(params["data"])
            if name in self.media and not params.get("deleteExisting", True):
                return name + "_duplicate"
            self.media[name] = data
            if self.fail_after_store_once:
                self.fail_after_store_once = False
                raise RuntimeError("simulated media response loss after commit")
            return name
        if action == "findNotes":
            model_match = re.search(r'note:"((?:\\.|[^"])*)"', params["query"])
            wanted_model = model_match.group(1).replace('\\"', '"').replace('\\\\', '\\') if model_match else None
            match = re.search(r'RQC_ID:"((?:\\.|[^"])*)"', params["query"])
            wanted = match.group(1).replace('\\"', '"').replace('\\\\', '\\') if match else None
            return [
                note_id for note_id, note in self.notes.items()
                if note["fields"]["RQC_ID"] == wanted
                and (wanted_model is None or note["modelName"] == wanted_model)
            ]
        if action == "addNote":
            payload = params["note"]
            identity = payload["fields"]["RQC_ID"]
            if any(note["fields"]["RQC_ID"] == identity for note in self.notes.values()):
                return None
            note_id = self.next_note
            card_id = self.next_card
            self.next_note += 1
            self.next_card += 1
            self.notes[note_id] = {
                "modelName": payload["modelName"],
                "fields": dict(payload["fields"]),
                "tags": list(payload.get("tags", [])),
                "cards": [card_id],
                "card_decks": {card_id: payload["deckName"]},
            }
            if self.fail_after_add_once:
                self.fail_after_add_once = False
                raise RuntimeError("simulated response loss after commit")
            return note_id
        if action == "notesInfo":
            result = []
            for note_id in params["notes"]:
                note = self.notes[note_id]
                value = {
                    "noteId": note_id,
                    "modelName": note["modelName"],
                    "fields": {name: {"value": value, "order": index} for index, (name, value) in enumerate(note["fields"].items())},
                    "tags": list(note["tags"]),
                    "cards": list(note["cards"]),
                    "profile": self.profile,
                }
                if self.readback_defect == "empty_cards":
                    value["cards"] = []
                elif self.readback_defect == "missing_front":
                    value["fields"].pop("Front", None)
                elif self.readback_defect == "missing_required_tag":
                    value["tags"] = []
                result.append(value)
            return result
        if action == "cardsInfo":
            result = []
            for card_id in params["cards"]:
                for note_id, note in self.notes.items():
                    if card_id in note["cards"]:
                        value = {"cardId": card_id, "note": note_id, "deckName": note["card_decks"][card_id]}
                        result.append(value)
                        if self.readback_defect == "duplicate_cards_info":
                            result.append(copy.deepcopy(value))
            return result
        if action == "updateNoteFields":
            note_id = params["note"]["id"]
            self.notes[note_id]["fields"].update(params["note"]["fields"])
            return None
        if action == "addTags":
            additions = params["tags"].split()
            for note_id in params["notes"]:
                self.notes[note_id]["tags"] = sorted(set(self.notes[note_id]["tags"]) | set(additions))
            return None
        if action == "changeDeck":
            for note in self.notes.values():
                for card_id in params["cards"]:
                    if card_id in note["cards"]:
                        note["card_decks"][card_id] = params["deck"]
            return None
        raise RuntimeError("unsupported action: " + action)


class FakeHandler(BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        request = json.loads(self.rfile.read(length).decode("utf-8"))
        try:
            result = self.server.state.invoke(request["action"], request.get("params", {}))
            response = {"result": result, "error": None}
        except Exception as exc:
            response = {"result": None, "error": str(exc)}
        body = json.dumps(response).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_args):
        pass


class FakeAnkiServer:
    def __init__(self):
        self.state = FakeAnkiState()
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), FakeHandler)
        self.server.state = self.state
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)

    @property
    def endpoint(self):
        return f"http://127.0.0.1:{self.server.server_port}"

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *_args):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)


def dump_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2) + "\n", encoding="utf-8")


def base_config(endpoint, profile="Canary Profile"):
    return {
        "schema_version": "anki-export-config.v1",
        "endpoint": endpoint,
        "expected_profile": profile,
        "timeout_seconds": 2,
        "max_media_bytes": 1024 * 1024,
        "provision_managed": True,
        "deck_routes": {"canary": "零碎知识剪藏::Canary"},
        "note_types": {
            "image_qa": {
                "model_name": "RQC Basic v1",
                "field_map": {"question": "Front", "answer": "Back", "source": "Source"},
                "required_fields": ["question", "answer"],
            }
        },
    }


def seed_managed_resources(state, config):
    for mapping in config["note_types"].values():
        model_name = mapping["model_name"]
        state.models[model_name] = list(MODEL_FIELDS)
        state.model_templates[model_name] = {
            "Card 1": {
                "Front": mapping.get("front_template", exporter.DEFAULT_FRONT_TEMPLATE),
                "Back": mapping.get("back_template", exporter.DEFAULT_BACK_TEMPLATE),
            }
        }
        state.model_css[model_name] = mapping.get("css", exporter.DEFAULT_CSS)
    state.decks.update(config["deck_routes"].values())


def seed_job_managed_resources(state, job):
    request = json.loads((job["root"] / job["request"]).read_text(encoding="utf-8"))
    config_ref = next(ref for ref in request["inputRefs"] if ref["kind"] == "anki-export-config")
    manifest = json.loads((job["root"] / config_ref["manifestPath"]).read_text(encoding="utf-8"))
    config = json.loads((job["root"] / manifest["metadata"]["entrypoint"]).read_text(encoding="utf-8"))
    seed_managed_resources(state, config)


def build_job(root, endpoint, *, revision=1, answer="四", unresolved=False, path_escape=False, operation="export", profile="Canary Profile", payload_extra=None, bundle_kind="CardBundle", artifact_kind="card-bundle"):
    root.mkdir(parents=True, exist_ok=True)
    media_bytes = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")
    media_path = root / "artifacts" / "card-input" / "media" / "question.png"
    media_path.parent.mkdir(parents=True, exist_ok=True)
    media_path.write_bytes(media_bytes)
    media = {
        "media_id": "question-image",
        "kind": "image",
        "relative_path": "../outside.png" if path_escape else "artifacts/card-input/media/question.png",
        "sha256": exporter.sha256_bytes(media_bytes),
        "size": len(media_bytes),
        "mime_type": "image/png",
    }
    card = {
        "card_id": "rqc-canary-fixed",
        "revision": revision,
        "content_hash": "pending",
        "status": "unresolved" if unresolved else "ready",
        "deck_route_key": "canary",
        "note_type_key": "image_qa",
        "fields": {"question": "2+2={{media:question-image}}", "answer": answer, "source": "fixture"},
        "tags": ["rqc_canary"],
        "media_refs": [{"media_id": "question-image", "field": "question", "placement": "replace"}],
    }
    card["content_hash"] = exporter.compute_content_hash(card, {"question-image": media})
    bundle = {"bundle_kind": bundle_kind, "schema_version": "1.0", "bundle_id": root.name, "cards": [card], "media": [media]}
    config = base_config(endpoint, profile)
    bundle_path = root / "artifacts" / "card-input" / "card-bundle.v1.json"
    config_path = root / "artifacts" / "anki-config" / "anki-export.v1.json"
    dump_json(bundle_path, bundle)
    dump_json(config_path, config)
    created = "2026-08-25T00:00:00Z"
    producer = {"stageId": "fixture-build", "workerId": "fixture-builder", "workerVersion": "1.0.0", "attemptId": "fixture-attempt"}
    card_artifact_path = root / "artifacts" / "card-input" / "artifact.json"
    config_artifact_path = root / "artifacts" / "anki-config" / "artifact.json"
    card_artifact = {
        "schemaVersion": "1.0.0", "artifactId": "card-input", "kind": artifact_kind, "contractVersion": "1.0.0",
        "mediaType": "application/vnd.rqc.card-bundle+json",
        "files": [
            {"path": "artifacts/card-input/card-bundle.v1.json", "sha256": exporter.sha256_file(bundle_path), "bytes": bundle_path.stat().st_size, "mediaType": "application/vnd.rqc.card-bundle+json"},
            {"path": "artifacts/card-input/media/question.png", "sha256": exporter.sha256_file(media_path), "bytes": media_path.stat().st_size, "mediaType": "image/png"},
        ],
        "createdAt": created, "producer": producer, "inputRefs": [], "dataClass": "personal",
        "metadata": {"entrypoint": "artifacts/card-input/card-bundle.v1.json"},
    }
    config_artifact = {
        "schemaVersion": "1.0.0", "artifactId": "anki-config", "kind": "anki-export-config", "contractVersion": "1.0.0",
        "mediaType": "application/vnd.rqc.anki-export-config+json",
        "files": [{"path": "artifacts/anki-config/anki-export.v1.json", "sha256": exporter.sha256_file(config_path), "bytes": config_path.stat().st_size, "mediaType": "application/vnd.rqc.anki-export-config+json"}],
        "createdAt": created, "producer": producer, "inputRefs": [], "dataClass": "personal",
        "metadata": {"entrypoint": "artifacts/anki-config/anki-export.v1.json"},
    }
    dump_json(card_artifact_path, card_artifact)
    dump_json(config_artifact_path, config_artifact)
    card_ref = {"artifactRef": "card-input", "kind": artifact_kind, "contractVersion": "1.0.0", "manifestPath": "artifacts/card-input/artifact.json", "expectedSha256": exporter.sha256_file(card_artifact_path)}
    config_ref = {"artifactRef": "anki-config", "kind": "anki-export-config", "contractVersion": "1.0.0", "manifestPath": "artifacts/anki-config/artifact.json", "expectedSha256": exporter.sha256_file(config_artifact_path)}
    task_refs = [{key: ref[key] for key in ("artifactRef", "kind", "contractVersion", "expectedSha256")} for ref in (card_ref, config_ref)]
    task = {
        "schemaVersion": "1.0.0", "taskId": "task-" + root.name, "createdAt": created,
        "createdBy": {"application": "anki-exporter-fixture", "version": "1.0.0"},
        "intent": "clip.card", "executionProfile": "cards-anki", "inputRefs": task_refs,
        "requestedOutputs": ["anki-export-evidence"], "initialPlanRef": "plans/initial-plan.json", "retentionClass": "evidence",
    }
    dump_json(root / "plans" / "initial-plan.json", {"fixture": True})
    dump_json(root / "task.manifest.v1.json", task)
    run_id = "run-" + root.name
    stage_id = "anki-semantic-readback" if operation == exporter.SEMANTIC_READBACK_OPERATION else "anki-export"
    attempt_id = "attempt-1"
    attempt = f"runs/{run_id}/stages/{stage_id}/attempts/{attempt_id}"
    request_rel = f"{attempt}/request.json"
    payload = {"validateOnly": operation == "validate"}
    if payload_extra:
        payload.update(payload_extra)
    request = {
        "schemaVersion": "1.0.0", "requestId": "request-" + root.name, "operationId": "operation-" + root.name, "taskId": task["taskId"],
        "runId": run_id, "stageId": stage_id, "attemptId": attempt_id, "workerId": exporter.WORKER_ID,
        "operation": operation, "idempotencyKey": exporter.sha256_bytes(exporter.json_bytes({"bundle": card["content_hash"], "config": config})),
        "attemptPath": attempt, "outputPath": f"{attempt}/outputs", "progressPath": f"{attempt}/progress.ndjson",
        "resultPath": f"{attempt}/result.json",
        "cancelSignal": {"schemaVersion": "1.0.0", "path": f"{attempt}/cancel.json", "gracePeriodMs": 2000},
        "inputRefs": [card_ref, config_ref],
        "configSnapshot": {"ankiConfigArtifactId": "anki-config"}, "payload": payload,
    }
    dump_json(root / request_rel, request)
    return {"root": root, "task": "task.manifest.v1.json", "request": request_rel, "media": media}


def build_original_media_job(root, endpoint, *, operation="export", question_text="题目", answer_text="", include_answer_images=True):
    job = build_job(root, endpoint, operation=operation)
    bundle_path = root / "artifacts" / "card-input" / "card-bundle.v1.1.json"
    config_path = root / "artifacts" / "anki-config" / "anki-export.v2.json"
    card_manifest_path = root / "artifacts" / "card-input" / "artifact.json"
    config_manifest_path = root / "artifacts" / "anki-config" / "artifact.json"
    base_png = (root / "artifacts" / "card-input" / "media" / "question.png").read_bytes()
    specs = [
        ("capture.image.question.0001", "question-0001.png", base_png + b"question-1", "questionImages", 0, "question"),
        ("capture.image.question.0002", "question-0002.png", base_png + b"question-2", "questionImages", 1, "question"),
    ]
    if include_answer_images:
        specs.append(("capture.image.answer.0001", "answer-0001.png", base_png + b"answer-1", "answerImages", 0, "answer"))
    media = []
    media_refs = []
    artifact_files = []
    for media_id, filename, data, role, ordinal, field in specs:
        relative = f"artifacts/card-input/media/{filename}"
        path = root / relative
        path.write_bytes(data)
        item = {
            "media_id": media_id,
            "kind": "image",
            "relative_path": relative,
            "sha256": exporter.sha256_bytes(data),
            "size": len(data),
            "mime_type": "image/png",
        }
        media.append(item)
        media_refs.append({"media_id": media_id, "field": field, "placement": "append", "role": role, "ordinal": ordinal})
        artifact_files.append({"path": relative, "sha256": item["sha256"], "bytes": len(data), "mediaType": "image/png"})
    card = {
        "card_id": "knowledge-clipper-original-media-fixed",
        "revision": 1,
        "content_hash": "pending",
        "status": "ready",
        "deck_route_key": "canary",
        "note_type_key": "original_image_qa",
        "fields": {"question": question_text, "answer": answer_text, "source": "capture-bundle.v1"},
        "tags": ["knowledge_clipper", "original_media"],
        "media_refs": media_refs,
    }
    card["content_hash"] = exporter.compute_content_hash(card, {item["media_id"]: item for item in media})
    bundle = {"bundle_kind": "CardBundle", "schema_version": "1.1", "bundle_id": root.name, "cards": [card], "media": media}
    config = {
        "schema_version": "anki-export-config.v2",
        "endpoint": endpoint,
        "expected_profile": "Canary Profile",
        "timeout_seconds": 2,
        "max_media_bytes": 1024 * 1024,
        "provision_managed": True,
        "deck_routes": {"canary": "零碎知识剪藏::Canary"},
        "note_types": {
            "original_image_qa": {
                "model_name": "KnowledgeClipper Original Media v1",
                "field_map": {"question": "Front", "answer": "Back", "source": "Source"},
                "required_fields": ["question"],
                "media_projection": exporter.ORIGINAL_MEDIA_PROJECTION,
                "css": exporter.ORIGINAL_MEDIA_CSS,
                "front_template": exporter.ORIGINAL_MEDIA_FRONT_TEMPLATE,
                "back_template": exporter.ORIGINAL_MEDIA_BACK_TEMPLATE,
            }
        },
    }
    dump_json(bundle_path, bundle)
    dump_json(config_path, config)
    card_manifest = json.loads(card_manifest_path.read_text(encoding="utf-8"))
    card_manifest["files"] = [{
        "path": bundle_path.relative_to(root).as_posix(),
        "sha256": exporter.sha256_file(bundle_path),
        "bytes": bundle_path.stat().st_size,
        "mediaType": "application/vnd.knowledge-clipper.anki-card-bundle+json",
    }, *artifact_files]
    card_manifest["metadata"]["entrypoint"] = bundle_path.relative_to(root).as_posix()
    dump_json(card_manifest_path, card_manifest)
    config_manifest = json.loads(config_manifest_path.read_text(encoding="utf-8"))
    config_manifest["files"] = [{
        "path": config_path.relative_to(root).as_posix(),
        "sha256": exporter.sha256_file(config_path),
        "bytes": config_path.stat().st_size,
        "mediaType": "application/vnd.knowledge-clipper.anki-export-config+json",
    }]
    config_manifest["metadata"]["entrypoint"] = config_path.relative_to(root).as_posix()
    dump_json(config_manifest_path, config_manifest)
    task_path = root / job["task"]
    request_path = root / job["request"]
    task = json.loads(task_path.read_text(encoding="utf-8"))
    request = json.loads(request_path.read_text(encoding="utf-8"))
    hashes = {"card-input": exporter.sha256_file(card_manifest_path), "anki-config": exporter.sha256_file(config_manifest_path)}
    for ref in task["inputRefs"]:
        ref["expectedSha256"] = hashes[ref["artifactRef"]]
    for ref in request["inputRefs"]:
        ref["expectedSha256"] = hashes[ref["artifactRef"]]
    request["idempotencyKey"] = exporter.sha256_bytes(exporter.json_bytes({"bundle": card["content_hash"], "config": config}))
    dump_json(task_path, task)
    dump_json(request_path, request)
    job.update({"bundle": bundle, "config": config, "media": media})
    return job


def mutate_original_media_job(job, *, bundle_mutation=None, config_mutation=None):
    root = job["root"]
    bundle_path = root / "artifacts" / "card-input" / "card-bundle.v1.1.json"
    config_path = root / "artifacts" / "anki-config" / "anki-export.v2.json"
    card_manifest_path = root / "artifacts" / "card-input" / "artifact.json"
    config_manifest_path = root / "artifacts" / "anki-config" / "artifact.json"
    bundle = json.loads(bundle_path.read_text(encoding="utf-8"))
    config = json.loads(config_path.read_text(encoding="utf-8"))
    if bundle_mutation:
        bundle_mutation(bundle)
    if config_mutation:
        config_mutation(config)
    media_by_id = {item["media_id"]: item for item in bundle["media"]}
    for card in bundle["cards"]:
        card["content_hash"] = exporter.compute_content_hash(card, media_by_id)
    dump_json(bundle_path, bundle)
    dump_json(config_path, config)
    card_manifest = json.loads(card_manifest_path.read_text(encoding="utf-8"))
    card_entry = next(item for item in card_manifest["files"] if item["path"].endswith("card-bundle.v1.1.json"))
    card_entry.update({"sha256": exporter.sha256_file(bundle_path), "bytes": bundle_path.stat().st_size})
    dump_json(card_manifest_path, card_manifest)
    config_manifest = json.loads(config_manifest_path.read_text(encoding="utf-8"))
    config_entry = next(item for item in config_manifest["files"] if item["path"].endswith("anki-export.v2.json"))
    config_entry.update({"sha256": exporter.sha256_file(config_path), "bytes": config_path.stat().st_size})
    dump_json(config_manifest_path, config_manifest)
    hashes = {"card-input": exporter.sha256_file(card_manifest_path), "anki-config": exporter.sha256_file(config_manifest_path)}
    for relative in (job["task"], job["request"]):
        path = root / relative
        value = json.loads(path.read_text(encoding="utf-8"))
        for ref in value["inputRefs"]:
            ref["expectedSha256"] = hashes[ref["artifactRef"]]
        if path.name == "request.json":
            value["idempotencyKey"] = exporter.sha256_bytes(exporter.json_bytes({"bundle": bundle["cards"][0]["content_hash"], "config": config}))
        dump_json(path, value)


def promote_original_media_to_core_derived(job):
    """Replace the initial card input with a canonical Core-adapter derived artifact.

    The immutable initial root is a CaptureBundle-shaped original-media artifact;
    the derived CardBundle owns copied, hash-identical media bytes under its v0001
    directory and points back to the initial capture/config refs.
    """
    root = job["root"]
    task_path = root / job["task"]
    request_path = root / job["request"]
    task = json.loads(task_path.read_text(encoding="utf-8"))
    request = json.loads(request_path.read_text(encoding="utf-8"))
    config_ref = next(ref for ref in request["inputRefs"] if ref["kind"] == "anki-export-config")

    source_root = root / "artifacts" / "capture-original-media"
    source_media_root = source_root / "media"
    source_media_root.mkdir(parents=True, exist_ok=True)
    question_images = []
    answer_images = []
    source_files = []
    for item in job["media"]:
        filename = Path(item["relative_path"]).name
        source_path = source_media_root / filename
        source_path.write_bytes((root / item["relative_path"]).read_bytes())
        source_entry = {
            "mediaId": item["media_id"],
            "path": source_path.relative_to(root).as_posix(),
            "sha256": exporter.sha256_file(source_path),
            "bytes": source_path.stat().st_size,
            "mediaType": item["mime_type"],
        }
        (question_images if ".question." in item["media_id"] else answer_images).append(source_entry)
        source_files.append({
            "path": source_entry["path"], "sha256": source_entry["sha256"],
            "bytes": source_entry["bytes"], "mediaType": source_entry["mediaType"],
        })
    capture_payload = source_root / "capture-bundle.v1.json"
    dump_json(capture_payload, {
        "schemaVersion": "capture-bundle.v1",
        "questionText": job["bundle"]["cards"][0]["fields"]["question"],
        "answerText": job["bundle"]["cards"][0]["fields"]["answer"],
        "questionImages": question_images,
        "answerImages": answer_images,
    })
    source_files.insert(0, {
        "path": capture_payload.relative_to(root).as_posix(),
        "sha256": exporter.sha256_file(capture_payload),
        "bytes": capture_payload.stat().st_size,
        "mediaType": "application/vnd.knowledge-clipper.capture-bundle+json",
    })
    source_manifest_path = source_root / "artifact.json"
    source_manifest = {
        "schemaVersion": "1.0.0", "artifactId": "capture-original-media", "kind": "capture-bundle",
        "contractVersion": "1.0.0", "mediaType": "application/vnd.knowledge-clipper.capture-bundle+json",
        "files": source_files, "createdAt": "2026-08-30T00:00:00Z",
        "producer": {"stageId": "fixture-build", "workerId": "fixture-builder", "workerVersion": "1.0.0", "attemptId": "fixture-capture"},
        "inputRefs": [], "dataClass": "personal",
        "metadata": {"entrypoint": capture_payload.relative_to(root).as_posix()},
    }
    dump_json(source_manifest_path, source_manifest)
    source_ref = {
        "artifactRef": "capture-original-media", "kind": "capture-bundle", "contractVersion": "1.0.0",
        "manifestPath": source_manifest_path.relative_to(root).as_posix(),
        "expectedSha256": exporter.sha256_file(source_manifest_path),
    }

    run_id = request["runId"]
    derived_root = root / "runs" / run_id / "adapters" / "anki-export" / "card-derived" / "v0001"
    derived_media_root = derived_root / "media"
    derived_media_root.mkdir(parents=True, exist_ok=True)
    derived_bundle = json.loads(json.dumps(job["bundle"]))
    derived_files = []
    for item in derived_bundle["media"]:
        source_item = next(entry for entry in question_images + answer_images if entry["mediaId"] == item["media_id"])
        target = derived_media_root / Path(source_item["path"]).name
        target.write_bytes((root / source_item["path"]).read_bytes())
        item["relative_path"] = target.relative_to(root).as_posix()
        derived_files.append({
            "path": item["relative_path"], "sha256": item["sha256"],
            "bytes": item["size"], "mediaType": item["mime_type"],
        })
    derived_payload = derived_root / "card-bundle.v1.1.json"
    dump_json(derived_payload, derived_bundle)
    derived_files.insert(0, {
        "path": derived_payload.relative_to(root).as_posix(),
        "sha256": exporter.sha256_file(derived_payload), "bytes": derived_payload.stat().st_size,
        "mediaType": "application/vnd.knowledge-clipper.anki-card-bundle+json",
    })
    derived_manifest_path = derived_root / "artifact.json"
    derived_manifest = {
        "schemaVersion": "1.0.0", "artifactId": "card-derived", "kind": "card-bundle",
        "contractVersion": "1.0.0", "mediaType": "application/vnd.knowledge-clipper.anki-card-bundle+json",
        "files": derived_files, "createdAt": "2026-08-30T00:00:01Z",
        "producer": {"stageId": "core-adapter", "workerId": "orchestrator.core-adapter", "workerVersion": "1.0.0", "attemptId": "adapter-card-derived"},
        "inputRefs": [source_ref, config_ref], "dataClass": "personal",
        "metadata": {"entrypoint": derived_payload.relative_to(root).as_posix()},
    }
    dump_json(derived_manifest_path, derived_manifest)
    derived_ref = {
        "artifactRef": "card-derived", "kind": "card-bundle", "contractVersion": "1.0.0",
        "manifestPath": derived_manifest_path.relative_to(root).as_posix(),
        "expectedSha256": exporter.sha256_file(derived_manifest_path),
    }
    task["inputRefs"] = [
        {key: source_ref[key] for key in ("artifactRef", "kind", "contractVersion", "expectedSha256")},
        {key: config_ref[key] for key in ("artifactRef", "kind", "contractVersion", "expectedSha256")},
    ]
    request["inputRefs"] = [derived_ref, config_ref]
    dump_json(task_path, task)
    dump_json(request_path, request)
    job.update({
        "bundle": derived_bundle, "derived_ref": derived_ref, "source_ref": source_ref,
        "source_manifest": source_manifest_path.relative_to(root).as_posix(),
    })
    return job


def build_derived_job(root, endpoint, *, defect=None):
    job = build_job(root, endpoint, operation="validate")
    request_path = root / job["request"]
    task_path = root / job["task"]
    request = json.loads(request_path.read_text(encoding="utf-8"))
    task = json.loads(task_path.read_text(encoding="utf-8"))
    config_ref = next(ref for ref in request["inputRefs"] if ref["kind"] == "anki-export-config")

    source_payload = root / "artifacts" / "source-input" / "payload.txt"
    source_payload.parent.mkdir(parents=True, exist_ok=True)
    source_payload.write_text("trusted original source", encoding="utf-8", newline="\n")
    source_manifest_path = source_payload.parent / "artifact.json"
    source_manifest = {
        "schemaVersion": "1.0.0", "artifactId": "source-input", "kind": "source.input", "contractVersion": "1.0.0",
        "mediaType": "text/plain",
        "files": [{"path": "artifacts/source-input/payload.txt", "sha256": exporter.sha256_file(source_payload), "bytes": source_payload.stat().st_size, "mediaType": "text/plain"}],
        "createdAt": "2026-08-25T00:00:00Z",
        "producer": {"stageId": "fixture-build", "workerId": "fixture-builder", "workerVersion": "1.0.0", "attemptId": "fixture-source"},
        "inputRefs": [], "dataClass": "personal", "metadata": {"entrypoint": "artifacts/source-input/payload.txt"},
    }
    dump_json(source_manifest_path, source_manifest)
    source_ref = {"artifactRef": "source-input", "kind": "source.input", "contractVersion": "1.0.0", "manifestPath": "artifacts/source-input/artifact.json", "expectedSha256": exporter.sha256_file(source_manifest_path)}
    if defect == "initial_path_alias":
        source_ref["manifestPath"] = "artifacts/source-input/./artifact.json"

    run_id = request["runId"]
    producer_run_id = f"run-upstream-{root.name}" if defect == "cross_run" else run_id
    outputs_root = root / "runs" / producer_run_id / "stages" / "content.understand-and-card" / "attempts" / "attempt-content" / "outputs"
    flat_v2_defects = {
        "flat_intermediate_v2", "flat_v2_forged_stage", "flat_v2_forged_attempt",
        "flat_v2_encoded_alias", "flat_v2_traversal", "flat_v2_leading_zero",
    }
    intermediate_root = outputs_root if defect in {"flat_intermediate", "flat_bad_extension", *flat_v2_defects} else outputs_root / "card-intermediate" / "v0001"
    intermediate_payload = intermediate_root / "payload.json"
    dump_json(intermediate_payload, {"status": "content-ready"})
    if defect == "flat_bad_extension":
        intermediate_name = "card-bundle.json"
    elif defect == "flat_intermediate":
        intermediate_name = "card-bundle.artifact.v1.json"
    elif defect == "flat_v2_leading_zero":
        intermediate_name = "learning-bundle.artifact.v02.json"
    elif defect in flat_v2_defects:
        intermediate_name = "learning-bundle.artifact.v2.json"
    else:
        intermediate_name = "artifact.json"
    intermediate_manifest_path = intermediate_root / intermediate_name
    intermediate_manifest = {
        "schemaVersion": "1.0.0", "artifactId": "card-intermediate", "kind": "content.intermediate", "contractVersion": "1.0.0",
        "mediaType": "application/json",
        "files": [{"path": intermediate_payload.relative_to(root).as_posix(), "sha256": exporter.sha256_file(intermediate_payload), "bytes": intermediate_payload.stat().st_size, "mediaType": "application/json"}],
        "createdAt": "2026-08-25T00:00:01Z",
        "producer": {"stageId": "content.understand-and-card", "workerId": "content.deepseek", "workerVersion": "1.0.0", "attemptId": "attempt-content"},
        "inputRefs": [] if defect == "broken_ancestry" else [source_ref], "dataClass": "personal",
        "metadata": {"entrypoint": intermediate_payload.relative_to(root).as_posix()},
    }
    if defect in {"initial_file_alias", "initial_file_alias_descriptor_mismatch"}:
        intermediate_manifest["files"] = [{
            "path": source_payload.relative_to(root).as_posix(),
            "sha256": exporter.sha256_file(source_payload),
            "bytes": source_payload.stat().st_size,
            "mediaType": "application/octet-stream" if defect == "initial_file_alias_descriptor_mismatch" else "text/plain",
        }]
        intermediate_manifest["mediaType"] = intermediate_manifest["files"][0]["mediaType"]
        intermediate_manifest["metadata"]["entrypoint"] = source_payload.relative_to(root).as_posix()
    if defect == "initial_file_alias_unbound":
        unbound = source_payload.parent / "not-declared-by-initial.txt"
        unbound.write_text("not part of the initial artifact", encoding="utf-8", newline="\n")
        intermediate_manifest["files"] = [{
            "path": unbound.relative_to(root).as_posix(),
            "sha256": exporter.sha256_file(unbound),
            "bytes": unbound.stat().st_size,
            "mediaType": "text/plain",
        }]
        intermediate_manifest["mediaType"] = "text/plain"
        intermediate_manifest["metadata"]["entrypoint"] = unbound.relative_to(root).as_posix()
    dump_json(intermediate_manifest_path, intermediate_manifest)
    intermediate_ref = {"artifactRef": "card-intermediate", "kind": "content.intermediate", "contractVersion": "1.0.0", "manifestPath": intermediate_manifest_path.relative_to(root).as_posix(), "expectedSha256": exporter.sha256_file(intermediate_manifest_path)}
    if defect == "path_escape":
        intermediate_ref["manifestPath"] = "../outside/artifact.json"
    if defect == "flat_v2_encoded_alias":
        intermediate_ref["manifestPath"] = intermediate_ref["manifestPath"].replace(
            "/outputs/", "/outputs/%2e%2e/outputs/",
        )
    if defect == "flat_v2_traversal":
        intermediate_ref["manifestPath"] = "../outside/learning-bundle.artifact.v2.json"
    if defect in {"intermediate_path_mismatch", "forged_run", "forged_stage", "forged_attempt", "flat_v2_forged_stage", "flat_v2_forged_attempt"}:
        forged_run = "run-forged" if defect == "forged_run" else producer_run_id
        forged_stage = "wrong-stage" if defect in {"intermediate_path_mismatch", "forged_stage", "flat_v2_forged_stage"} else "content.understand-and-card"
        forged_attempt = "wrong-attempt" if defect in {"intermediate_path_mismatch", "forged_attempt", "flat_v2_forged_attempt"} else "attempt-content"
        mismatched_root = root / "runs" / forged_run / "stages" / forged_stage / "attempts" / forged_attempt / "outputs"
        if defect not in {"flat_v2_forged_stage", "flat_v2_forged_attempt"}:
            mismatched_root = mismatched_root / "card-intermediate" / "v0001"
        mismatched_root.mkdir(parents=True, exist_ok=True)
        mismatched_manifest = mismatched_root / ("learning-bundle.artifact.v2.json" if defect in {"flat_v2_forged_stage", "flat_v2_forged_attempt"} else "artifact.json")
        mismatched_manifest.write_bytes(intermediate_manifest_path.read_bytes())
        intermediate_ref["manifestPath"] = mismatched_manifest.relative_to(root).as_posix()

    producer_result = {
        "schemaVersion": "1.0.0", "requestId": "request-content", "operationId": "operation-content",
        "taskId": task["taskId"], "runId": producer_run_id, "stageId": "content.understand-and-card",
        "attemptId": "attempt-content", "worker": {"id": "content.deepseek", "version": "1.0.0"},
        "contractVersion": "1.0.0", "idempotencyKey": "a" * 64,
        "status": "completed", "outputs": [intermediate_ref], "error": None,
        "startedAt": "2026-08-25T00:00:00Z", "finishedAt": "2026-08-25T00:00:01Z", "warnings": [],
    }
    if defect == "producer_result_unknown":
        producer_result.update({
            "status": "unknown_external_state", "outputs": [],
            "error": {"code": "UNKNOWN", "category": "effect", "retryable": False, "detailsRef": "errors/details.json"},
        })
    dump_json(outputs_root.parent / "result.json", producer_result)

    original_bundle = json.loads((root / "artifacts" / "card-input" / "card-bundle.v1.json").read_text(encoding="utf-8"))
    original_media = (root / "artifacts" / "card-input" / "media" / "question.png").read_bytes()
    derived_root = root / "runs" / run_id / "adapters" / "anki-export" / "card-derived" / "v0001"
    derived_media = derived_root / "media" / "question.png"
    derived_media.parent.mkdir(parents=True, exist_ok=True)
    derived_media.write_bytes(original_media)
    original_bundle["media"][0]["relative_path"] = derived_media.relative_to(root).as_posix()
    original_bundle["cards"][0]["content_hash"] = exporter.compute_content_hash(original_bundle["cards"][0], {"question-image": original_bundle["media"][0]})
    derived_payload = derived_root / "card-bundle.v1.json"
    dump_json(derived_payload, original_bundle)
    derived_manifest_path = derived_root / "artifact.json"
    producer = {"stageId": "core-adapter", "workerId": "orchestrator.core-adapter", "workerVersion": "1.0.0", "attemptId": "adapter-card-derived"}
    if defect == "forged_producer":
        producer["workerId"] = "attacker.worker"
    if defect == "producer_identity":
        producer["attemptId"] = "adapter-attacker"
    derived_manifest = {
        "schemaVersion": "1.0.0", "artifactId": "card-derived", "kind": "card-bundle", "contractVersion": "1.0.0",
        "mediaType": "application/vnd.rqc.card-bundle+json",
        "files": [
            {"path": derived_payload.relative_to(root).as_posix(), "sha256": exporter.sha256_file(derived_payload), "bytes": derived_payload.stat().st_size, "mediaType": "application/vnd.rqc.card-bundle+json"},
            {"path": derived_media.relative_to(root).as_posix(), "sha256": exporter.sha256_file(derived_media), "bytes": derived_media.stat().st_size, "mediaType": "image/png"},
        ],
        "createdAt": "2026-08-25T00:00:02Z", "producer": producer, "inputRefs": [intermediate_ref, config_ref], "dataClass": "personal",
        "metadata": {"entrypoint": derived_payload.relative_to(root).as_posix()},
    }
    if defect == "file_bypass":
        bypass_payload = root / "artifacts" / "card-input" / "card-bundle.v1.json"
        derived_manifest["files"].append({
            "path": bypass_payload.relative_to(root).as_posix(),
            "sha256": exporter.sha256_file(bypass_payload),
            "bytes": bypass_payload.stat().st_size,
            "mediaType": "application/json",
        })
    if defect == "file_alias":
        aliased = derived_payload.parent.relative_to(root).as_posix() + "/media/../card-bundle.v1.json"
        derived_manifest["files"][0]["path"] = aliased
        derived_manifest["metadata"]["entrypoint"] = aliased
    dump_json(derived_manifest_path, derived_manifest)
    derived_ref = {"artifactRef": "card-derived", "kind": "card-bundle", "contractVersion": "1.0.0", "manifestPath": derived_manifest_path.relative_to(root).as_posix(), "expectedSha256": exporter.sha256_file(derived_manifest_path)}
    if defect == "manifest_hash":
        derived_ref["expectedSha256"] = "0" * 64
    if defect == "tampered_payload":
        tampered = bytearray(derived_payload.read_bytes())
        tampered[-2] = ord(" ") if tampered[-2] != ord(" ") else ord("\t")
        derived_payload.write_bytes(tampered)

    config_task_ref = {key: config_ref[key] for key in ("artifactRef", "kind", "contractVersion", "expectedSha256")}
    source_task_ref = {key: source_ref[key] for key in ("artifactRef", "kind", "contractVersion", "expectedSha256")}
    task["inputRefs"] = [source_task_ref, config_task_ref]
    request["inputRefs"] = [derived_ref, config_ref]
    dump_json(task_path, task)
    dump_json(request_path, request)
    job["derived_ref"] = derived_ref
    return job


def mutate_job_config(job, mutate):
    root = job["root"]
    config_path = root / "artifacts" / "anki-config" / "anki-export.v1.json"
    artifact_path = root / "artifacts" / "anki-config" / "artifact.json"
    task_path = root / job["task"]
    request_path = root / job["request"]

    config = json.loads(config_path.read_text(encoding="utf-8"))
    mutate(config)
    dump_json(config_path, config)

    artifact = json.loads(artifact_path.read_text(encoding="utf-8"))
    artifact["files"][0]["sha256"] = exporter.sha256_file(config_path)
    artifact["files"][0]["bytes"] = config_path.stat().st_size
    dump_json(artifact_path, artifact)
    artifact_hash = exporter.sha256_file(artifact_path)

    task = json.loads(task_path.read_text(encoding="utf-8"))
    task_ref = next(ref for ref in task["inputRefs"] if ref["artifactRef"] == "anki-config")
    task_ref["expectedSha256"] = artifact_hash
    dump_json(task_path, task)

    request = json.loads(request_path.read_text(encoding="utf-8"))
    request_ref = next(ref for ref in request["inputRefs"] if ref["artifactRef"] == "anki-config")
    request_ref["expectedSha256"] = artifact_hash
    request["idempotencyKey"] = exporter.sha256_bytes(exporter.json_bytes({"inputs": request["inputRefs"], "config": config}))
    dump_json(request_path, request)


def mutate_job_bundle(job, mutate):
    root = job["root"]
    bundle_path = root / "artifacts" / "card-input" / "card-bundle.v1.json"
    artifact_path = root / "artifacts" / "card-input" / "artifact.json"
    task_path = root / job["task"]
    request_path = root / job["request"]

    bundle = json.loads(bundle_path.read_text(encoding="utf-8"))
    mutate(bundle)
    for card in bundle["cards"]:
        card["content_hash"] = exporter.compute_content_hash(card, {item["media_id"]: item for item in bundle["media"]})
    dump_json(bundle_path, bundle)

    artifact = json.loads(artifact_path.read_text(encoding="utf-8"))
    entry = next(item for item in artifact["files"] if item["path"].endswith("card-bundle.v1.json"))
    entry["sha256"] = exporter.sha256_file(bundle_path)
    entry["bytes"] = bundle_path.stat().st_size
    dump_json(artifact_path, artifact)
    artifact_hash = exporter.sha256_file(artifact_path)

    task = json.loads(task_path.read_text(encoding="utf-8"))
    task_ref = next(ref for ref in task["inputRefs"] if ref["artifactRef"] == "card-input")
    task_ref["expectedSha256"] = artifact_hash
    dump_json(task_path, task)

    request = json.loads(request_path.read_text(encoding="utf-8"))
    request_ref = next(ref for ref in request["inputRefs"] if ref["artifactRef"] == "card-input")
    request_ref["expectedSha256"] = artifact_hash
    request["idempotencyKey"] = exporter.sha256_bytes(exporter.json_bytes({"inputs": request["inputRefs"], "bundle": bundle}))
    dump_json(request_path, request)


def build_language_both_job(root):
    job = build_job(root, "http://127.0.0.1:65534", operation="validate")

    def configure(config):
        config["deck_routes"]["language.zh"] = "零碎知识剪藏::语言::中文"
        config["note_types"].update({
            "language_item": {
                "model_name": "RQC Basic v1",
                "field_map": {"context_sentence": "Front", "reading_and_meaning": "Back"},
                "required_fields": ["context_sentence", "reading_and_meaning"],
            },
            "language_writing": {
                "model_name": "RQC Basic v1",
                "field_map": {"front": "Front", "back": "Back"},
                "required_fields": ["front", "back"],
            },
        })

    mutate_job_config(job, configure)

    def replace_cards(bundle):
        bundle["media"] = []
        bundle["cards"] = [
            {
                "card_id": "language-context-card",
                "revision": 1,
                "content_hash": "pending",
                "status": "ready",
                "deck_route_key": "language.zh",
                "note_type_key": "language_item",
                "fields": {"context_sentence": "他心中仍有疑窦。", "reading_and_meaning": "疑窦（yí dòu）：心里的疑问。"},
                "tags": ["language_context"],
                "media_refs": [],
            },
            {
                "card_id": "language-writing-card",
                "revision": 1,
                "content_hash": "pending",
                "status": "ready",
                "deck_route_key": "language.zh",
                "note_type_key": "language_writing",
                "fields": {"front": "他心中仍有（yí dòu；心里的疑问）。", "back": "疑窦"},
                "tags": ["language_writing"],
                "media_refs": [],
            },
        ]

    mutate_job_bundle(job, replace_cards)
    return job


def prepare_semantic_readback_job(job):
    root = job["root"]
    request_path = root / job["request"]
    request = json.loads(request_path.read_text(encoding="utf-8"))
    bundle_ref = next(ref for ref in request["inputRefs"] if ref["kind"] in {"card-bundle", "learning-bundle"})
    bundle_manifest = json.loads((root / bundle_ref["manifestPath"]).read_text(encoding="utf-8"))
    bundle = json.loads((root / bundle_manifest["metadata"]["entrypoint"]).read_text(encoding="utf-8"))
    config_ref = next(ref for ref in request["inputRefs"] if ref["kind"] == "anki-export-config")
    config_manifest = json.loads((root / config_ref["manifestPath"]).read_text(encoding="utf-8"))
    config = json.loads((root / config_manifest["metadata"]["entrypoint"]).read_text(encoding="utf-8"))
    package_view = type("PackageView", (), {
        "config": config,
        "media_by_id": {item["media_id"]: item for item in bundle["media"]},
    })()
    expected = []
    expected_decks = set()
    for card in bundle["cards"]:
        fields, _ = exporter.render_card(card, package_view)
        expected.append({"cardId": card["card_id"], "front": fields["Front"], "back": fields["Back"]})
        expected_decks.add(config["deck_routes"][card["deck_route_key"]])
    if len(expected_decks) != 1:
        raise AssertionError("semantic readback fixture requires one destination deck")
    request["operation"] = exporter.SEMANTIC_READBACK_OPERATION
    request["payload"] = {
        "expectedCardCount": len(expected),
        "expectedCards": expected,
        "expectedDeck": next(iter(expected_decks)),
    }
    request["idempotencyKey"] = exporter.sha256_bytes(exporter.json_bytes({
        "operation": exporter.SEMANTIC_READBACK_OPERATION,
        "expectedCards": expected,
        "expectedDeck": request["payload"]["expectedDeck"],
    }))
    dump_json(request_path, request)
    task_path = root / job["task"]
    task = json.loads(task_path.read_text(encoding="utf-8"))
    task["requestedOutputs"] = ["anki-semantic-readback"]
    dump_json(task_path, task)
    return job


def make_exporter(job):
    return exporter.Exporter(job["root"], job["task"], job["request"])


def validate_common(instance_path, schema_name):
    base_uri = COMMON_SCHEMAS.resolve().as_uri() + "/"
    schemas = {name: json.loads((COMMON_SCHEMAS / name).read_text(encoding="utf-8")) for name in (
        "task-manifest.schema.json", "artifact.schema.json", "worker-request.schema.json", "worker-result.schema.json",
        "worker-describe.schema.json", "worker-registry.schema.json", "worker-cancel-signal.schema.json"
    )}
    store = {base_uri + name: schema for name, schema in schemas.items()}
    resolver = RefResolver(base_uri=base_uri, referrer=schemas[schema_name], store=store)
    instance = json.loads(Path(instance_path).read_text(encoding="utf-8"))
    Draft202012Validator(schemas[schema_name], resolver=resolver, format_checker=FormatChecker()).validate(instance)


def validate_attempt_outputs(run):
    validate_common(run.writer.output_root / "anki-export-evidence.artifact.json", "artifact.schema.json")
    validate_common(run.writer.result_path, "worker-result.schema.json")


def validate_readback_evidence(run):
    path = run.writer.output_root / "readback.json"
    value = json.loads(path.read_text(encoding="utf-8"))
    schema = json.loads((WORKER / "contracts" / "anki-export-readback.v1.schema.json").read_text(encoding="utf-8"))
    Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)
    assert value["cardCount"] == len(value["cards"])
    assert value["cardsSha256"] == exporter.sha256_bytes(exporter.json_bytes(value["cards"]))
    for row in value["cards"]:
        body = {"notesInfo": row["notesInfo"], "cardsInfo": row["cardsInfo"]}
        assert row["semanticBodySha256"] == exporter.sha256_bytes(exporter.json_bytes(body))
    receipt = json.loads((run.writer.output_root / "receipt.json").read_text(encoding="utf-8"))
    manifest_name = (
        "anki-semantic-readback.artifact.json"
        if run.request["operation"] == exporter.SEMANTIC_READBACK_OPERATION
        else "anki-export-evidence.artifact.json"
    )
    manifest = json.loads((run.writer.output_root / manifest_name).read_text(encoding="utf-8"))
    expected_sha = exporter.sha256_file(path)
    assert receipt["readback_evidence"]["sha256"] == expected_sha
    assert manifest["metadata"]["readback"] == {
        "path": run.writer.relative(path),
        "sha256": expected_sha,
        "schemaVersion": "anki-export-readback.v1",
    }
    declared = next(item for item in manifest["files"] if item["path"] == run.writer.relative(path))
    assert declared["sha256"] == expected_sha
    return value


class AnkiExporterTests(unittest.TestCase):
    def test_export_evidence_contains_sha_bound_secret_free_semantic_readback(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            root = Path(temp)
            job = build_job(root / "first", fake.endpoint)
            seed_job_managed_resources(fake.state, job)
            run = make_exporter(job)
            self.assertEqual("SUCCEEDED", run.run())
            evidence = validate_readback_evidence(run)
            row = evidence["cards"][0]
            note = row["notesInfo"]
            cards = row["cardsInfo"]
            self.assertEqual(0, row["ordinal"])
            self.assertEqual("rqc-canary-fixed", row["sourceCardId"])
            self.assertEqual("CREATED", row["outcome"])
            self.assertEqual([note["noteId"]], [item["noteId"] for item in cards])
            self.assertEqual(note["cardIds"], [item["cardId"] for item in cards])
            self.assertEqual("零碎知识剪藏::Canary", cards[0]["deckName"])
            self.assertEqual("四", note["fields"]["Back"])
            self.assertEqual({"Front", "Back"}, set(note["requiredFieldNames"]))
            self.assertEqual({"rqc_canary", "rqc_managed"}, set(note["requiredTags"]))
            serialized = json.dumps(evidence, ensure_ascii=False)
            self.assertNotIn("Canary Profile", serialized)
            self.assertNotIn(fake.endpoint, serialized)
            self.assertNotIn("RQC Basic v1", serialized)
            self.assertNotIn("UserNotes", serialized)

            fake.state.notes[note["noteId"]]["fields"]["UserNotes"] = "private manual note"
            replay_job = build_job(root / "replay", fake.endpoint)
            replay = make_exporter(replay_job)
            self.assertEqual("SUCCEEDED", replay.run())
            replay_evidence = validate_readback_evidence(replay)
            replay_row = replay_evidence["cards"][0]
            self.assertEqual("ALREADY_CURRENT", replay_row["outcome"])
            self.assertEqual(note["noteId"], replay_row["notesInfo"]["noteId"])
            self.assertEqual(note["cardIds"], replay_row["notesInfo"]["cardIds"])
            self.assertNotIn("private manual note", json.dumps(replay_evidence, ensure_ascii=False))

    def test_multicard_readback_preserves_bundle_order(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            job = build_language_both_job(Path(temp) / "language-live")
            mutate_job_config(job, lambda config: config.update({"endpoint": fake.endpoint, "expected_profile": fake.state.profile}))
            request_path = job["root"] / job["request"]
            request = json.loads(request_path.read_text(encoding="utf-8"))
            request["operation"] = "export"
            request["payload"] = {"validateOnly": False}
            dump_json(request_path, request)
            seed_job_managed_resources(fake.state, job)
            run = make_exporter(job)
            self.assertEqual("SUCCEEDED", run.run())
            evidence = validate_readback_evidence(run)
            self.assertEqual(["language-context-card", "language-writing-card"], [row["sourceCardId"] for row in evidence["cards"]])
            self.assertEqual([0, 1], [row["ordinal"] for row in evidence["cards"]])

    def test_semantic_readback_operation_is_exact_and_provider_read_only(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            root = Path(temp)
            first = build_language_both_job(root / "semantic-source")
            mutate_job_config(first, lambda config: config.update({"endpoint": fake.endpoint, "expected_profile": fake.state.profile}))
            first_request_path = first["root"] / first["request"]
            first_request = json.loads(first_request_path.read_text(encoding="utf-8"))
            first_request["operation"] = "export"
            first_request["payload"] = {"validateOnly": False}
            dump_json(first_request_path, first_request)
            seed_job_managed_resources(fake.state, first)
            first_run = make_exporter(first)
            self.assertEqual("SUCCEEDED", first_run.run())
            first_evidence = validate_readback_evidence(first_run)

            readback_job = prepare_semantic_readback_job(build_language_both_job(root / "semantic-readback"))
            mutate_job_config(readback_job, lambda config: config.update({"endpoint": fake.endpoint, "expected_profile": fake.state.profile}))
            readback_request_path = readback_job["root"] / readback_job["request"]
            readback_request = json.loads(readback_request_path.read_text(encoding="utf-8"))
            for expected, observed in zip(readback_request["payload"]["expectedCards"], first_evidence["cards"], strict=True):
                expected["noteId"] = observed["notesInfo"]["noteId"]
                expected["ankiCardId"] = observed["notesInfo"]["cardIds"][0]
            readback_request["idempotencyKey"] = exporter.sha256_bytes(exporter.json_bytes(readback_request["payload"]))
            dump_json(readback_request_path, readback_request)
            calls_before = len(fake.state.calls)
            run = make_exporter(readback_job)
            self.assertEqual("SUCCEEDED", run.run())
            new_actions = [item["action"] for item in fake.state.calls[calls_before:]]
            self.assertFalse(set(new_actions) & exporter.MUTATING_ANKI_ACTIONS)
            evidence = validate_readback_evidence(run)
            self.assertEqual(["language-context-card", "language-writing-card"], [row["sourceCardId"] for row in evidence["cards"]])
            self.assertEqual([0, 1], [row["ordinal"] for row in evidence["cards"]])
            self.assertEqual(["ALREADY_CURRENT", "ALREADY_CURRENT"], [row["outcome"] for row in evidence["cards"]])
            result = json.loads(run.writer.result_path.read_text(encoding="utf-8"))
            self.assertEqual([], result["sideEffects"])
            self.assertTrue(result["payload"]["semanticReadbackOnly"])
            self.assertEqual("anki-semantic-readback", result["outputs"][0]["kind"])
            receipt = json.loads((run.writer.output_root / "receipt.json").read_text(encoding="utf-8"))
            self.assertEqual(False, receipt["operation_effect"]["external_mutation"])
            self.assertEqual("provider-read-only", receipt["operation_effect"]["effect_class"])

    def test_semantic_readback_rejects_content_drift_without_mutation(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            root = Path(temp)
            first = build_language_both_job(root / "semantic-drift-source")
            mutate_job_config(first, lambda config: config.update({"endpoint": fake.endpoint, "expected_profile": fake.state.profile}))
            first_request_path = first["root"] / first["request"]
            first_request = json.loads(first_request_path.read_text(encoding="utf-8"))
            first_request["operation"] = "export"
            first_request["payload"] = {"validateOnly": False}
            dump_json(first_request_path, first_request)
            seed_job_managed_resources(fake.state, first)
            self.assertEqual("SUCCEEDED", make_exporter(first).run())
            first_note = min(fake.state.notes)
            fake.state.notes[first_note]["fields"]["Front"] = "wrong but still non-empty"

            readback_job = prepare_semantic_readback_job(build_language_both_job(root / "semantic-drift-readback"))
            mutate_job_config(readback_job, lambda config: config.update({"endpoint": fake.endpoint, "expected_profile": fake.state.profile}))
            calls_before = len(fake.state.calls)
            run = make_exporter(readback_job)
            self.assertEqual("FAILED", run.run())
            new_actions = [item["action"] for item in fake.state.calls[calls_before:]]
            self.assertFalse(set(new_actions) & exporter.MUTATING_ANKI_ACTIONS)
            result = json.loads(run.writer.result_path.read_text(encoding="utf-8"))
            self.assertEqual("failed", result["status"])
            self.assertEqual([], result["outputs"])
            self.assertEqual([], result["sideEffects"])
            self.assertEqual("READBACK_MISMATCH", result["error"]["code"])

    def test_semantic_readback_rejects_bound_note_card_id_drift_without_mutation(self):
        for field in ("noteId", "ankiCardId"):
            with self.subTest(field=field), tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
                root = Path(temp)
                first = build_language_both_job(root / f"semantic-id-source-{field}")
                mutate_job_config(first, lambda config: config.update({"endpoint": fake.endpoint, "expected_profile": fake.state.profile}))
                first_request_path = first["root"] / first["request"]
                first_request = json.loads(first_request_path.read_text(encoding="utf-8"))
                first_request["operation"] = "export"
                first_request["payload"] = {"validateOnly": False}
                dump_json(first_request_path, first_request)
                seed_job_managed_resources(fake.state, first)
                self.assertEqual("SUCCEEDED", make_exporter(first).run())

                readback_job = prepare_semantic_readback_job(build_language_both_job(root / f"semantic-id-readback-{field}"))
                mutate_job_config(readback_job, lambda config: config.update({"endpoint": fake.endpoint, "expected_profile": fake.state.profile}))
                request_path = readback_job["root"] / readback_job["request"]
                request = json.loads(request_path.read_text(encoding="utf-8"))
                request["payload"]["expectedCards"][0][field] = 999999
                request["idempotencyKey"] = exporter.sha256_bytes(exporter.json_bytes(request["payload"]))
                dump_json(request_path, request)
                calls_before = len(fake.state.calls)
                run = make_exporter(readback_job)
                self.assertEqual("FAILED", run.run())
                self.assertFalse(set(item["action"] for item in fake.state.calls[calls_before:]) & exporter.MUTATING_ANKI_ACTIONS)
                result = json.loads(run.writer.result_path.read_text(encoding="utf-8"))
                self.assertEqual("READBACK_IDENTITY_MISMATCH", result["error"]["code"])
                self.assertEqual([], result["sideEffects"])

    def test_semantic_readback_missing_note_is_known_and_never_writes(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            job = prepare_semantic_readback_job(build_language_both_job(Path(temp) / "semantic-missing"))
            mutate_job_config(job, lambda config: config.update({"endpoint": fake.endpoint, "expected_profile": fake.state.profile}))
            seed_job_managed_resources(fake.state, job)
            run = make_exporter(job)
            self.assertEqual("FAILED", run.run())
            self.assertFalse(set(item["action"] for item in fake.state.calls) & exporter.MUTATING_ANKI_ACTIONS)
            result = json.loads(run.writer.result_path.read_text(encoding="utf-8"))
            self.assertEqual("failed", result["status"])
            self.assertNotEqual("unknown_external_state", result["status"])
            self.assertEqual("READBACK_NOTE_MISSING", result["error"]["code"])
            self.assertFalse(result["error"]["retryable"])
            self.assertEqual([], result["sideEffects"])

    def test_malformed_readback_bodies_fail_closed(self):
        cases = {
            "empty-card-ids": ("empty_cards", "ANKI_RESPONSE_INVALID"),
            "missing-front": ("missing_front", "READBACK_MISMATCH"),
            "missing-required-tag": ("missing_required_tag", "READBACK_MISMATCH"),
            "duplicate-cards-info": ("duplicate_cards_info", "ANKI_RESPONSE_INVALID"),
        }
        for name, (defect, expected_code) in cases.items():
            with self.subTest(case=name), tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
                job = build_job(Path(temp) / name, fake.endpoint)
                seed_job_managed_resources(fake.state, job)
                fake.state.readback_defect = defect
                run = make_exporter(job)
                self.assertEqual("FAILED", run.run())
                result = json.loads(run.writer.result_path.read_text(encoding="utf-8"))
                self.assertEqual("failed", result["status"])
                self.assertEqual([], result["outputs"])
                self.assertFalse((run.writer.output_root / "readback.json").exists())
                self.assertIn(expected_code, (run.writer.output_root / "errors.ndjson").read_text(encoding="utf-8"))

    def test_ordered_original_media_accepts_core_derived_capture_ancestry(self):
        with tempfile.TemporaryDirectory() as temp:
            job = promote_original_media_to_core_derived(build_original_media_job(
                Path(temp) / "core-derived-original-media", "http://127.0.0.1:65534", operation="validate",
            ))
            with mock.patch.object(exporter, "AnkiClient", side_effect=AssertionError("derived validate opened AnkiConnect")):
                run = make_exporter(job)
                self.assertEqual("SUCCEEDED", run.run())
            self.assertEqual("orchestrator.core-adapter", run.package.bundle_artifact["producer"]["workerId"])
            self.assertEqual([job["source_ref"], next(
                ref for ref in run.package.bundle_artifact["inputRefs"] if ref["kind"] == "anki-export-config"
            )], run.package.bundle_artifact["inputRefs"])
            result = json.loads(run.writer.result_path.read_text(encoding="utf-8"))
            self.assertEqual("completed", result["status"])
            self.assertFalse((run.writer.output_root / "operations.ndjson").exists())

    def test_ordered_original_media_validate_only_renders_exact_fields_and_hash_order(self):
        with tempfile.TemporaryDirectory() as temp:
            job = build_original_media_job(Path(temp) / "original-media", "http://127.0.0.1:65534", operation="validate")
            bundle_schema = json.loads((WORKER / "schemas" / "card-bundle.v1.1.schema.json").read_text(encoding="utf-8"))
            config_schema = json.loads((WORKER / "contracts" / "anki-export-config.v2.schema.json").read_text(encoding="utf-8"))
            Draft202012Validator(bundle_schema, format_checker=FormatChecker()).validate(job["bundle"])
            Draft202012Validator(config_schema, format_checker=FormatChecker()).validate(job["config"])
            with mock.patch.object(exporter, "AnkiClient", side_effect=AssertionError("original media validate-only opened AnkiConnect")):
                run = make_exporter(job)
                self.assertEqual("SUCCEEDED", run.run())
            card = run.package.bundle["cards"][0]
            fields, used = exporter.render_card(card, run.package)
            media = run.package.media_by_id
            q1 = exporter.media_filename(media["capture.image.question.0001"])
            q2 = exporter.media_filename(media["capture.image.question.0002"])
            a1 = exporter.media_filename(media["capture.image.answer.0001"])
            self.assertEqual(
                f'题目<br><img src="{q1}"><br><img src="{q2}">',
                fields["Front"],
            )
            self.assertEqual(f'<img src="{a1}">', fields["Back"])
            self.assertEqual(
                ["capture.image.question.0001", "capture.image.question.0002", "capture.image.answer.0001"],
                used,
            )
            reordered = copy.deepcopy(card)
            reordered["media_refs"][:2] = reversed(reordered["media_refs"][:2])
            self.assertNotEqual(card["content_hash"], exporter.compute_content_hash(reordered, media))
            result = json.loads(run.writer.result_path.read_text(encoding="utf-8"))
            self.assertEqual("completed", result["status"])
            self.assertTrue(result["payload"]["validateOnly"])
            self.assertFalse((run.writer.output_root / "operations.ndjson").exists())

    def test_ordered_original_media_allows_empty_answer_and_no_answer_images(self):
        with tempfile.TemporaryDirectory() as temp:
            job = build_original_media_job(
                Path(temp) / "question-only", "http://127.0.0.1:65534",
                operation="validate", question_text="", answer_text="", include_answer_images=False,
            )
            with mock.patch.object(exporter, "AnkiClient", side_effect=AssertionError("question-only validate opened AnkiConnect")):
                run = make_exporter(job)
                self.assertEqual("SUCCEEDED", run.run())
            fields, used = exporter.render_card(run.package.bundle["cards"][0], run.package)
            self.assertEqual(2, len(used))
            self.assertTrue(fields["Front"].startswith('<img src="rqc_'))
            self.assertEqual("", fields["Back"])

    def test_ordered_original_media_unsupported_is_destination_failure_not_success(self):
        with tempfile.TemporaryDirectory() as temp:
            job = build_original_media_job(Path(temp) / "unsupported", "http://127.0.0.1:65534", operation="validate")

            def legacy_config(config):
                config["schema_version"] = "anki-export-config.v1"
                mapping = config["note_types"]["original_image_qa"]
                for key in ("media_projection", "css", "front_template", "back_template"):
                    mapping.pop(key, None)

            mutate_original_media_job(job, config_mutation=legacy_config)
            with mock.patch.object(exporter, "AnkiClient", side_effect=AssertionError("unsupported media opened AnkiConnect")):
                run = make_exporter(job)
                self.assertEqual("CONTRACT_REJECTED", run.run())
            result = json.loads(run.writer.result_path.read_text(encoding="utf-8"))
            receipt = json.loads((run.writer.output_root / "receipt.json").read_text(encoding="utf-8"))
            self.assertEqual("failed", result["status"])
            self.assertEqual("MEDIA_DESTINATION_UNSUPPORTED", result["error"]["code"])
            self.assertEqual("MEDIA_DESTINATION_UNSUPPORTED", receipt["error"]["code"])
            self.assertFalse((run.writer.output_root / "operations.ndjson").exists())

    def test_ordered_original_media_strict_negative_fixtures(self):
        cases = {
            "reversed-question-order": (lambda bundle: bundle["cards"][0]["media_refs"].__setitem__(slice(0, 2), list(reversed(bundle["cards"][0]["media_refs"][:2]))), None, "MEDIA_ORDER_INVALID"),
            "answer-before-question": (lambda bundle: bundle["cards"][0]["media_refs"].insert(0, bundle["cards"][0]["media_refs"].pop()), None, "MEDIA_ORDER_INVALID"),
            "ordinal-gap": (lambda bundle: bundle["cards"][0]["media_refs"][1].update({"ordinal": 3}), None, "MEDIA_ORDER_INVALID"),
            "wrong-role-field": (lambda bundle: bundle["cards"][0]["media_refs"][0].update({"field": "answer"}), None, "MEDIA_ROLE_FIELD_MISMATCH"),
            "legacy-token": (lambda bundle: bundle["cards"][0]["fields"].update({"question": "{{media:capture.image.question.0001}}"}), None, "MEDIA_TOKEN_FORBIDDEN"),
            "duplicate-media-ref": (lambda bundle: bundle["cards"][0]["media_refs"][1].update({"media_id": "capture.image.question.0001"}), None, "DUPLICATE_MEDIA_REF"),
            "non-image": (lambda bundle: bundle["media"][0].update({"kind": "audio"}), None, "MEDIA_DESTINATION_UNSUPPORTED"),
            "missing-question-images": (lambda bundle: bundle["cards"][0].update({"media_refs": [ref for ref in bundle["cards"][0]["media_refs"] if ref["role"] == "answerImages"]}), None, "MEDIA_ORDER_INVALID"),
            "max-bytes": (None, lambda config: config.update({"max_media_bytes": 1}), "MEDIA_TOO_LARGE"),
            "legacy-bundle-schema": (lambda bundle: bundle.update({"schema_version": "1.0"}), None, "SCHEMA_INVALID"),
        }
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for name, (bundle_mutation, config_mutation, expected_code) in cases.items():
                with self.subTest(case=name):
                    job = build_original_media_job(root / name, "http://127.0.0.1:65534", operation="validate")
                    mutate_original_media_job(job, bundle_mutation=bundle_mutation, config_mutation=config_mutation)
                    with mock.patch.object(exporter, "AnkiClient", side_effect=AssertionError("invalid media opened AnkiConnect")):
                        run = make_exporter(job)
                        self.assertEqual("CONTRACT_REJECTED", run.run())
                    receipt = json.loads((run.writer.output_root / "receipt.json").read_text(encoding="utf-8"))
                    self.assertEqual(expected_code, receipt["error"]["code"])
                    self.assertFalse((run.writer.output_root / "operations.ndjson").exists())

    def test_ordered_original_media_fake_live_readback_and_idempotent_replay(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            root = Path(temp)
            first_job = build_original_media_job(root / "first", fake.endpoint)
            seed_job_managed_resources(fake.state, first_job)
            first = make_exporter(first_job)
            self.assertEqual("SUCCEEDED", first.run())
            self.assertEqual(1, len(fake.state.notes))
            note_id = next(iter(fake.state.notes))
            note = fake.state.notes[note_id]
            self.assertEqual("KnowledgeClipper Original Media v1", note["modelName"])
            self.assertEqual("零碎知识剪藏::Canary", note["card_decks"][note["cards"][0]])
            self.assertRegex(note["fields"]["Front"], r'^题目<br><img src="rqc_[0-9a-f]{64}\.png"><br><img src="rqc_[0-9a-f]{64}\.png">$')
            self.assertRegex(note["fields"]["Back"], r'^<img src="rqc_[0-9a-f]{64}\.png">$')
            self.assertEqual({"knowledge_clipper", "original_media", "rqc_managed"}, set(note["tags"]))
            self.assertEqual(3, len(fake.state.media))

            fake.state.calls.clear()
            replay = make_exporter(build_original_media_job(root / "replay", fake.endpoint))
            self.assertEqual("SUCCEEDED", replay.run())
            receipt = json.loads((replay.writer.output_root / "receipt.json").read_text(encoding="utf-8"))
            self.assertEqual(1, receipt["counts"]["already_current"])
            self.assertEqual(1, len(fake.state.notes))
            self.assertEqual(3, len(fake.state.media))
            self.assertFalse({call["action"] for call in fake.state.calls} & exporter.MUTATING_ANKI_ACTIONS)

    def test_ordered_original_media_collision_and_response_loss_keep_existing_failure_semantics(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            root = Path(temp)
            collision_job = build_original_media_job(root / "collision", fake.endpoint)
            seed_job_managed_resources(fake.state, collision_job)
            for item in collision_job["media"]:
                fake.state.media[exporter.media_filename(item)] = b"different-content"
            collision = make_exporter(collision_job)
            self.assertEqual("FAILED", collision.run())
            collision_result = json.loads(collision.writer.result_path.read_text(encoding="utf-8"))
            self.assertEqual("failed", collision_result["status"])
            self.assertIn("MEDIA_NAME_COLLISION", (collision.writer.output_root / "errors.ndjson").read_text(encoding="utf-8"))
            self.assertEqual({}, fake.state.notes)

        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            fake.state.fail_after_store_once = True
            response_loss_job = build_original_media_job(Path(temp) / "response-loss", fake.endpoint)
            seed_job_managed_resources(fake.state, response_loss_job)
            response_loss = make_exporter(response_loss_job)
            self.assertEqual("FAILED", response_loss.run())
            result = json.loads(response_loss.writer.result_path.read_text(encoding="utf-8"))
            self.assertEqual("unknown_external_state", result["status"])
            self.assertFalse(result["error"]["retryable"])
            self.assertEqual("unknown", result["sideEffects"][0]["state"])
            self.assertEqual([], result["outputs"])

    def test_language_both_uses_two_independent_forward_mappings_in_input_order(self):
        with tempfile.TemporaryDirectory() as temp:
            job = build_language_both_job(Path(temp) / "language-both")
            observed = []
            original_render = exporter.render_card

            def observe(card, package):
                observed.append(card["card_id"])
                return original_render(card, package)

            with mock.patch.object(exporter, "AnkiClient", side_effect=AssertionError("language validate-only opened AnkiConnect")), \
                    mock.patch.object(exporter, "render_card", side_effect=observe):
                run = make_exporter(job)
                self.assertEqual("SUCCEEDED", run.run())

            expected_order = ["language-context-card", "language-writing-card"]
            self.assertEqual(expected_order, observed)
            self.assertEqual(expected_order, [card["card_id"] for card in run.package.bundle["cards"]])
            self.assertEqual(2, len(set(expected_order)))
            context_fields, _ = original_render(run.package.bundle["cards"][0], run.package)
            writing_fields, _ = original_render(run.package.bundle["cards"][1], run.package)
            self.assertEqual("他心中仍有疑窦。", context_fields["Front"])
            self.assertEqual("疑窦（yí dòu）：心里的疑问。", context_fields["Back"])
            self.assertEqual("他心中仍有（yí dòu；心里的疑问）。", writing_fields["Front"])
            self.assertNotIn("疑窦", writing_fields["Front"])
            self.assertEqual("疑窦", writing_fields["Back"])
            self.assertTrue(json.loads(run.writer.result_path.read_text(encoding="utf-8"))["payload"]["validateOnly"])
            self.assertFalse((run.writer.output_root / "operations.ndjson").exists())
            validate_attempt_outputs(run)

    def test_language_writing_strict_loader_rejects_choice_and_mapping_leaks(self):
        cases = {
            "mapping-missing": (lambda job: mutate_job_config(job, lambda config: config["note_types"].pop("language_writing")), "MAPPING_MISSING"),
            "choice-leaked-downstream": (lambda job: mutate_job_bundle(job, lambda bundle: bundle["cards"][1].update({"choice": "both"})), "SCHEMA_INVALID"),
            "decision-token-leaked-downstream": (lambda job: mutate_job_bundle(job, lambda bundle: bundle["cards"][1].update({"decision_token": "must-stay-upstream"})), "SCHEMA_INVALID"),
            "context-field-leak": (lambda job: mutate_job_bundle(job, lambda bundle: bundle["cards"][1]["fields"].update({"reading_and_meaning": "must not be consumed"})), "FIELD_UNMAPPED"),
            "wrong-note-type": (lambda job: mutate_job_bundle(job, lambda bundle: bundle["cards"][1].update({"note_type_key": "language_item"})), "FIELD_UNMAPPED"),
            "duplicate-card-id": (lambda job: mutate_job_bundle(job, lambda bundle: bundle["cards"][1].update({"card_id": bundle["cards"][0]["card_id"]})), "DUPLICATE_CARD_ID"),
            "empty-target-term": (lambda job: mutate_job_bundle(job, lambda bundle: bundle["cards"][1]["fields"].update({"back": ""})), "REQUIRED_FIELD_EMPTY"),
        }
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for name, (mutate, expected_code) in cases.items():
                with self.subTest(case=name):
                    job = build_language_both_job(root / name)
                    mutate(job)
                    with mock.patch.object(exporter, "AnkiClient", side_effect=AssertionError("invalid language input opened AnkiConnect")):
                        run = make_exporter(job)
                        self.assertEqual("CONTRACT_REJECTED", run.run())
                    receipt = json.loads((run.writer.output_root / "receipt.json").read_text(encoding="utf-8"))
                    self.assertEqual(expected_code, receipt["error"]["code"])
                    self.assertFalse((run.writer.output_root / "operations.ndjson").exists())
                    validate_attempt_outputs(run)

    def test_unexpected_exception_before_effect_publishes_failed_result(self):
        with tempfile.TemporaryDirectory() as temp:
            job = build_job(Path(temp) / "pkg", "http://127.0.0.1:65534", operation="validate")
            run = make_exporter(job)
            with mock.patch.object(exporter, "load_package", side_effect=RuntimeError("injected internal failure")):
                self.assertEqual("FAILED", run.run())
            result = json.loads(run.writer.result_path.read_text(encoding="utf-8"))
            self.assertEqual("failed", result["status"])
            self.assertEqual("WORKER_INTERNAL_ERROR", result["error"]["code"])
            self.assertEqual("none", result["sideEffects"][0]["state"])
            self.assertEqual([], result["outputs"])
            validate_attempt_outputs(run)

    def test_unexpected_exception_after_effect_is_unknown_and_not_retryable(self):
        with tempfile.TemporaryDirectory() as temp:
            job = build_job(Path(temp) / "pkg", "http://127.0.0.1:65534", operation="export")
            run = make_exporter(job)

            def fail_after_effect():
                run.effect_attempted = True
                raise RuntimeError("injected post-effect failure")

            with mock.patch.object(run, "_run_live", side_effect=fail_after_effect):
                self.assertEqual("FAILED", run.run())
            result = json.loads(run.writer.result_path.read_text(encoding="utf-8"))
            self.assertEqual("unknown_external_state", result["status"])
            self.assertEqual("WORKER_INTERNAL_ERROR", result["error"]["code"])
            self.assertFalse(result["error"]["retryable"])
            self.assertEqual("unknown", result["sideEffects"][0]["state"])
            self.assertEqual([], result["outputs"])
            validate_attempt_outputs(run)

    def test_malformed_ankiconnect_collection_responses_publish_failed_result(self):
        cases = ("modelNames", "deckNames", "findNotes", "cardsInfo")
        for action in cases:
            with self.subTest(action=action), tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
                root = Path(temp)
                seed_job = build_job(root / "seed", fake.endpoint)
                seed_job_managed_resources(fake.state, seed_job)
                self.assertEqual("SUCCEEDED", make_exporter(seed_job).run())
                fake.state.forced_results[action] = None
                run = make_exporter(build_job(root / "malformed", fake.endpoint))
                self.assertEqual("FAILED", run.run())
                result = json.loads(run.writer.result_path.read_text(encoding="utf-8"))
                self.assertEqual("failed", result["status"])
                errors = (run.writer.output_root / "errors.ndjson").read_text(encoding="utf-8")
                self.assertIn("ANKI_RESPONSE_INVALID", errors)
                self.assertEqual(1, len(fake.state.notes))
                validate_attempt_outputs(run)

    def test_validate_only_rejects_unsafe_or_malformed_config_without_network(self):
        cases = (
            ("endpoint-userinfo-smuggling", lambda config: config.update({"endpoint": "http://127.0.0.1:8765@evil.example"})),
            ("endpoint-path", lambda config: config.update({"endpoint": "http://127.0.0.1:8765/anki"})),
            ("endpoint-query", lambda config: config.update({"endpoint": "http://127.0.0.1:8765?target=evil"})),
            ("endpoint-invalid-port", lambda config: config.update({"endpoint": "http://127.0.0.1:notaport"})),
            ("timeout-negative", lambda config: config.update({"timeout_seconds": -1})),
            ("timeout-not-finite", lambda config: config.update({"timeout_seconds": float("nan")})),
            ("provision-not-boolean", lambda config: config.update({"provision_managed": "yes"})),
            ("deck-not-string", lambda config: config["deck_routes"].update({"canary": {"deck": "bad"}})),
            ("required-field-unmapped", lambda config: config["note_types"]["image_qa"]["required_fields"].append("missing")),
            ("field-target-duplicate", lambda config: config["note_types"]["image_qa"]["field_map"].update({"answer": "Front"})),
        )
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for name, mutate in cases:
                with self.subTest(case=name):
                    job = build_job(root / name, "http://127.0.0.1:65534", operation="validate")
                    mutate_job_config(job, mutate)
                    with mock.patch.object(exporter, "AnkiClient", side_effect=AssertionError("invalid validate-only config opened AnkiConnect")):
                        run = make_exporter(job)
                        self.assertEqual("CONTRACT_REJECTED", run.run())
                    receipt = json.loads((run.writer.output_root / "receipt.json").read_text(encoding="utf-8"))
                    self.assertEqual("CONFIG_INVALID", receipt["error"]["code"])
                    self.assertFalse((run.writer.output_root / "operations.ndjson").exists())
                    validate_attempt_outputs(run)

    def test_validate_only_never_constructs_ankiconnect_client(self):
        with tempfile.TemporaryDirectory() as temp:
            job = build_job(Path(temp) / "pkg", "http://127.0.0.1:65534", operation="validate")
            with mock.patch.object(exporter, "AnkiClient", side_effect=AssertionError("validate-only opened AnkiConnect")):
                run = make_exporter(job)
                self.assertEqual("SUCCEEDED", run.run())
            self.assertFalse((run.writer.output_root / "operations.ndjson").exists())
            preflight = json.loads((run.writer.output_root / "preflight.json").read_text(encoding="utf-8"))
            self.assertEqual({"mode": "validate-only", "status": "NOT_RUN"}, {key: preflight[key] for key in ("mode", "status")})
            validate_attempt_outputs(run)

    def test_validate_only_rejects_oversized_media_without_network(self):
        with tempfile.TemporaryDirectory() as temp:
            job = build_job(Path(temp) / "pkg", "http://127.0.0.1:65534", operation="validate")
            mutate_job_config(job, lambda config: config.update({"max_media_bytes": 1}))
            with mock.patch.object(exporter, "AnkiClient", side_effect=AssertionError("oversized media validation opened AnkiConnect")):
                run = make_exporter(job)
                self.assertEqual("CONTRACT_REJECTED", run.run())
            receipt = json.loads((run.writer.output_root / "receipt.json").read_text(encoding="utf-8"))
            self.assertEqual("MEDIA_TOO_LARGE", receipt["error"]["code"])
            self.assertFalse((run.writer.output_root / "operations.ndjson").exists())
            validate_attempt_outputs(run)

    def test_anki_finalized_learning_bundle_remains_supported(self):
        with tempfile.TemporaryDirectory() as temp:
            job = build_job(
                Path(temp) / "learning",
                "http://127.0.0.1:65534",
                operation="validate",
                bundle_kind="LearningBundle",
                artifact_kind="learning-bundle",
            )
            run = make_exporter(job)
            self.assertEqual("SUCCEEDED", run.run())
            result = json.loads(run.writer.result_path.read_text(encoding="utf-8"))
            self.assertEqual("completed", result["status"])
            self.assertTrue(result["payload"]["validateOnly"])
            self.assertEqual("LearningBundle", run.package.bundle["bundle_kind"])
            validate_attempt_outputs(run)

    def test_initial_manifest_inputs_remain_supported(self):
        with tempfile.TemporaryDirectory() as temp:
            job = build_job(Path(temp) / "pkg", "http://127.0.0.1:65534", operation="validate")
            run = make_exporter(job)
            self.assertEqual("SUCCEEDED", run.run())
            result = json.loads(run.writer.result_path.read_text(encoding="utf-8"))
            self.assertEqual(exporter.EXPORTER_VERSION, result["worker"]["version"])
            validate_attempt_outputs(run)

    def test_trusted_core_adapter_derived_input_is_accepted(self):
        with tempfile.TemporaryDirectory() as temp:
            job = build_derived_job(Path(temp) / "pkg", "http://127.0.0.1:65534")
            run = make_exporter(job)
            self.assertEqual("SUCCEEDED", run.run())
            task = json.loads((job["root"] / job["task"]).read_text(encoding="utf-8"))
            request = json.loads((job["root"] / job["request"]).read_text(encoding="utf-8"))
            self.assertNotIn(request["inputRefs"][0]["expectedSha256"], {ref["expectedSha256"] for ref in task["inputRefs"]})
            validate_common(job["root"] / job["task"], "task-manifest.schema.json")
            validate_common(job["root"] / job["request"], "worker-request.schema.json")
            validate_attempt_outputs(run)

    def test_trusted_cross_run_intermediate_ancestry_is_accepted(self):
        with tempfile.TemporaryDirectory() as temp:
            job = build_derived_job(Path(temp) / "pkg", "http://127.0.0.1:65534", defect="cross_run")
            run = make_exporter(job)
            self.assertEqual("SUCCEEDED", run.run())
            request = json.loads((job["root"] / job["request"]).read_text(encoding="utf-8"))
            intermediate = json.loads((job["root"] / job["derived_ref"]["manifestPath"]).read_text(encoding="utf-8"))["inputRefs"][0]
            self.assertNotIn(f"runs/{request['runId']}/stages/", intermediate["manifestPath"])
            self.assertIn("runs/run-upstream-pkg/stages/", intermediate["manifestPath"])
            validate_attempt_outputs(run)

    def test_trusted_intermediate_may_alias_exact_initial_root_file_without_copy(self):
        with tempfile.TemporaryDirectory() as temp:
            job = build_derived_job(
                Path(temp) / "pkg", "http://127.0.0.1:65534", defect="initial_file_alias",
            )
            run = make_exporter(job)
            self.assertEqual("SUCCEEDED", run.run())
            derived = json.loads((job["root"] / job["derived_ref"]["manifestPath"]).read_text(encoding="utf-8"))
            intermediate = derived["inputRefs"][0]
            manifest = json.loads((job["root"] / intermediate["manifestPath"]).read_text(encoding="utf-8"))
            self.assertEqual("artifacts/source-input/payload.txt", manifest["files"][0]["path"])
            self.assertEqual(manifest["files"][0]["path"], manifest["metadata"]["entrypoint"])
            validate_attempt_outputs(run)

    def test_trusted_current_run_flat_common_artifact_is_accepted(self):
        with tempfile.TemporaryDirectory() as temp:
            job = build_derived_job(Path(temp) / "pkg", "http://127.0.0.1:65534", defect="flat_intermediate")
            run = make_exporter(job)
            self.assertEqual("SUCCEEDED", run.run())
            validate_attempt_outputs(run)

    def test_dotted_stage_accepts_versioned_flat_common_artifact_v2(self):
        with tempfile.TemporaryDirectory() as temp:
            job = build_derived_job(
                Path(temp) / "pkg", "http://127.0.0.1:65534", defect="flat_intermediate_v2",
            )
            run = make_exporter(job)
            self.assertEqual("SUCCEEDED", run.run())
            derived = json.loads((job["root"] / job["derived_ref"]["manifestPath"]).read_text(encoding="utf-8"))
            intermediate = derived["inputRefs"][0]
            self.assertIn("/stages/content.understand-and-card/", intermediate["manifestPath"])
            self.assertTrue(intermediate["manifestPath"].endswith("/outputs/learning-bundle.artifact.v2.json"))
            validate_attempt_outputs(run)

    def test_untrusted_or_broken_derived_inputs_are_rejected(self):
        cases = {
            "forged_producer": "DERIVED_PRODUCER_UNTRUSTED",
            "producer_identity": "DERIVED_PRODUCER_UNTRUSTED",
            "broken_ancestry": "ANCESTRY_DISCONNECTED",
            "manifest_hash": "HASH_MISMATCH",
            "tampered_payload": "HASH_MISMATCH",
            "path_escape": "PATH_ESCAPE",
            "initial_path_alias": "PATH_NONCANONICAL",
            "intermediate_path_mismatch": "ANCESTRY_PATH_INVALID",
            "forged_run": "ANCESTRY_PRODUCER_RESULT_INVALID",
            "forged_stage": "ANCESTRY_PATH_INVALID",
            "forged_attempt": "ANCESTRY_PATH_INVALID",
            "producer_result_unknown": "ANCESTRY_PRODUCER_RESULT_INVALID",
            "flat_bad_extension": "ANCESTRY_PATH_INVALID",
            "file_bypass": "ARTIFACT_FILE_BYPASS",
            "file_alias": "PATH_NONCANONICAL",
            "initial_file_alias_descriptor_mismatch": "ARTIFACT_FILE_BYPASS",
            "initial_file_alias_unbound": "ARTIFACT_FILE_BYPASS",
            "flat_v2_forged_stage": "ANCESTRY_PATH_INVALID",
            "flat_v2_forged_attempt": "ANCESTRY_PATH_INVALID",
            "flat_v2_encoded_alias": "PATH_NONCANONICAL",
            "flat_v2_traversal": "PATH_ESCAPE",
            "flat_v2_leading_zero": "ANCESTRY_PATH_INVALID",
        }
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for defect, expected_code in cases.items():
                with self.subTest(defect=defect):
                    job = build_derived_job(root / defect, "http://127.0.0.1:65534", defect=defect)
                    run = make_exporter(job)
                    self.assertEqual("CONTRACT_REJECTED", run.run())
                    receipt = json.loads((run.writer.output_root / "receipt.json").read_text(encoding="utf-8"))
                    self.assertEqual(expected_code, receipt["error"]["code"])
                    validate_attempt_outputs(run)

    def test_atomic_result_and_receipt_publication_beyond_260_chars(self):
        with tempfile.TemporaryDirectory() as temp:
            try:
                attempt = Path(temp) / ("segment-a-" + "x" * 110) / ("segment-b-" + "y" * 110) / "attempt"
                result_path = attempt / "result.json"
                receipt_path = attempt / "outputs" / "receipt.json"
                self.assertGreater(len(str(result_path.resolve())), 260)
                self.assertGreater(len(str(receipt_path.resolve())), 260)
                exporter.write_json_atomic_once(result_path, {"kind": "result"})
                exporter.write_json_atomic_once(receipt_path, {"kind": "receipt"})
                self.assertEqual({"kind": "result"}, exporter.load_json(result_path))
                self.assertEqual({"kind": "receipt"}, exporter.load_json(receipt_path))
                self.assertFalse(any(name.startswith(".~") for name in os.listdir(exporter.native_fs_path(attempt))))
                self.assertFalse(any(name.startswith(".~") for name in os.listdir(exporter.native_fs_path(receipt_path.parent))))
            finally:
                shutil.rmtree(exporter.native_fs_path(Path(temp)), ignore_errors=False)

    def test_standard_describe_and_run_cli(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            described = subprocess.run(
                [sys.executable, str(WORKER / "anki_exporter.py"), "describe", "--json"],
                check=False, capture_output=True, text=True, encoding="utf-8",
            )
            self.assertEqual(0, described.returncode, described.stderr)
            describe_path = root / "worker-describe.json"
            describe_path.write_text(described.stdout, encoding="utf-8")
            validate_common(describe_path, "worker-describe.schema.json")
            descriptor = json.loads(described.stdout)
            self.assertEqual("export.anki", descriptor["workerId"])
            self.assertEqual("external", descriptor["sideEffectLevel"])
            self.assertEqual("reconcilable", descriptor["idempotencyLevel"])
            self.assertNotIn("remnote", json.dumps(descriptor, ensure_ascii=False).lower())

            job = build_job(root / "job", "http://127.0.0.1:65534", operation="validate")
            request_path = (job["root"] / job["request"]).resolve()
            ran = subprocess.run(
                [sys.executable, str(WORKER / "anki_exporter.py"), "run", "--request", str(request_path)],
                check=False, capture_output=True, text=True, encoding="utf-8",
            )
            self.assertEqual(0, ran.returncode, ran.stderr)
            progress = [json.loads(line) for line in ran.stdout.splitlines() if line.strip()]
            self.assertEqual(["started", "finished"], [item["event"] for item in progress])
            validate_common(job["root"] / job["request"], "worker-request.schema.json")
            validate_common(job["root"] / job["request"].replace("request.json", "result.json"), "worker-result.schema.json")

    def test_prestart_cancellation_reasons(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for reason in ("user_stop", "superseded", "task_cancel", "timeout"):
                with self.subTest(reason=reason):
                    job = build_job(root / reason, "http://127.0.0.1:65534", operation="validate")
                    request = json.loads((job["root"] / job["request"]).read_text(encoding="utf-8"))
                    signal = {
                        "schemaVersion": "1.0.0", "attemptId": request["attemptId"], "operationId": request["operationId"],
                        "reason": reason, "requestedAt": "2026-08-25T00:00:00Z", "graceDeadline": "2026-08-25T00:00:02Z",
                    }
                    signal_path = job["root"] / request["cancelSignal"]["path"]
                    dump_json(signal_path, signal)
                    validate_common(signal_path, "worker-cancel-signal.schema.json")
                    run = make_exporter(job)
                    expected_module = "FAILED_TIMEOUT" if reason == "timeout" else "CANCELLED"
                    expected_common = "failed" if reason == "timeout" else "cancelled"
                    self.assertEqual(expected_module, run.run())
                    result = json.loads(run.writer.result_path.read_text(encoding="utf-8"))
                    self.assertEqual(expected_common, result["status"])
                    self.assertEqual([], result["outputs"])
                    validate_attempt_outputs(run)

    def test_live_create_rerun_and_update_preserves_user_notes(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            root = Path(temp)
            first = build_job(root / "pkg-v1", fake.endpoint, revision=1)
            seed_job_managed_resources(fake.state, first)
            self.assertEqual("SUCCEEDED", make_exporter(first).run())
            self.assertEqual(1, len(fake.state.notes))
            note_id = next(iter(fake.state.notes))
            self.assertEqual("", fake.state.notes[note_id]["fields"]["UserNotes"])
            fake.state.notes[note_id]["fields"]["UserNotes"] = "manual note"

            second_attempt = make_exporter(build_job(root / "pkg-rerun", fake.endpoint, revision=1))
            self.assertEqual("SUCCEEDED", second_attempt.run())
            receipt = json.loads((second_attempt.writer.output_root / "receipt.json").read_text(encoding="utf-8"))
            self.assertEqual(1, receipt["counts"]["already_current"])
            self.assertEqual(1, len(fake.state.notes))
            common_result = json.loads(second_attempt.writer.result_path.read_text(encoding="utf-8"))
            self.assertEqual("completed", common_result["status"])
            self.assertEqual("verified", common_result["sideEffects"][0]["state"])
            self.assertTrue(common_result["outputs"][0]["manifestPath"].endswith("anki-export-evidence.artifact.json"))
            operations = [json.loads(line) for line in (second_attempt.writer.output_root / "operations.ndjson").read_text(encoding="utf-8").splitlines()]
            mutating = {"createModel", "createDeck", "storeMediaFile", "addNote", "updateNoteFields", "addTags", "changeDeck"}
            self.assertFalse(any(item["action"] in mutating for item in operations))
            rerun = second_attempt.package
            validate_common(rerun.root / "task.manifest.v1.json", "task-manifest.schema.json")
            validate_common(rerun.request_path, "worker-request.schema.json")
            validate_common(rerun.root / rerun.request["inputRefs"][0]["manifestPath"], "artifact.schema.json")
            validate_common(rerun.root / rerun.request["inputRefs"][1]["manifestPath"], "artifact.schema.json")
            validate_attempt_outputs(second_attempt)

            updated = build_job(root / "pkg-v2", fake.endpoint, revision=2, answer="4")
            self.assertEqual("SUCCEEDED", make_exporter(updated).run())
            self.assertEqual(1, len(fake.state.notes))
            self.assertEqual("4", fake.state.notes[note_id]["fields"]["Back"])
            self.assertEqual("manual note", fake.state.notes[note_id]["fields"]["UserNotes"])
            self.assertEqual("2", fake.state.notes[note_id]["fields"]["RQC_Revision"])

    def test_deck_route_mapping_change_converges_without_note_duplication(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            root = Path(temp)
            first_job = build_job(root / "first", fake.endpoint)
            seed_job_managed_resources(fake.state, first_job)
            self.assertEqual("SUCCEEDED", make_exporter(first_job).run())
            note_id = next(iter(fake.state.notes))

            reroute = build_job(root / "reroute", fake.endpoint)
            mutate_job_config(reroute, lambda config: config["deck_routes"].update({"canary": "零碎知识剪藏::重新路由"}))
            seed_job_managed_resources(fake.state, reroute)
            run = make_exporter(reroute)
            self.assertEqual("SUCCEEDED", run.run())

            self.assertEqual(1, len(fake.state.notes))
            card_id = fake.state.notes[note_id]["cards"][0]
            self.assertEqual("零碎知识剪藏::重新路由", fake.state.notes[note_id]["card_decks"][card_id])
            receipt = json.loads((run.writer.output_root / "receipt.json").read_text(encoding="utf-8"))
            self.assertEqual(1, receipt["counts"]["already_current"])
            operations = [json.loads(line)["action"] for line in (run.writer.output_root / "operations.ndjson").read_text(encoding="utf-8").splitlines()]
            self.assertIn("changeDeck", operations)
            self.assertNotIn("addNote", operations)
            self.assertNotIn("updateNoteFields", operations)
            validate_attempt_outputs(run)

    def test_note_type_mapping_change_detects_global_id_before_mutation(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            root = Path(temp)
            first_job = build_job(root / "first", fake.endpoint)
            seed_job_managed_resources(fake.state, first_job)
            self.assertEqual("SUCCEEDED", make_exporter(first_job).run())
            alternate = "RQC Alternate v1"
            fake.state.models[alternate] = list(MODEL_FIELDS)
            fake.state.model_templates[alternate] = {"Card 1": {"Front": exporter.DEFAULT_FRONT_TEMPLATE, "Back": exporter.DEFAULT_BACK_TEMPLATE}}
            fake.state.model_css[alternate] = exporter.DEFAULT_CSS

            remapped = build_job(root / "remapped", fake.endpoint)
            mutate_job_config(remapped, lambda config: config["note_types"]["image_qa"].update({"model_name": alternate}))
            run = make_exporter(remapped)
            self.assertEqual("FAILED", run.run())

            self.assertEqual(1, len(fake.state.notes))
            errors = (run.writer.output_root / "errors.ndjson").read_text(encoding="utf-8")
            self.assertIn("MODEL_SCHEMA_CONFLICT", errors)
            operations = [json.loads(line)["action"] for line in (run.writer.output_root / "operations.ndjson").read_text(encoding="utf-8").splitlines()]
            self.assertNotIn("addNote", operations)
            self.assertNotIn("updateNoteFields", operations)
            self.assertNotIn("addTags", operations)
            self.assertNotIn("changeDeck", operations)
            validate_attempt_outputs(run)

    def test_stale_and_same_revision_conflicts_are_non_mutating(self):
        cases = (
            ("stale", 1, "stale answer", "STALE_INPUT"),
            ("same-revision-different-content", 2, "conflicting answer", "REVISION_CONFLICT"),
        )
        for name, incoming_revision, incoming_answer, expected_code in cases:
            with self.subTest(case=name), tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
                root = Path(temp)
                current_job = build_job(root / "current", fake.endpoint, revision=2, answer="current answer")
                seed_job_managed_resources(fake.state, current_job)
                self.assertEqual("SUCCEEDED", make_exporter(current_job).run())
                note_id = next(iter(fake.state.notes))

                incoming = build_job(root / "incoming", fake.endpoint, revision=incoming_revision, answer=incoming_answer)
                run = make_exporter(incoming)
                self.assertEqual("FAILED", run.run())

                self.assertEqual(1, len(fake.state.notes))
                self.assertEqual("current answer", fake.state.notes[note_id]["fields"]["Back"])
                errors = (run.writer.output_root / "errors.ndjson").read_text(encoding="utf-8")
                self.assertIn(expected_code, errors)
                operations = [json.loads(line)["action"] for line in (run.writer.output_root / "operations.ndjson").read_text(encoding="utf-8").splitlines()]
                self.assertFalse(any(action in {"createModel", "createDeck", "storeMediaFile", "addNote", "updateNoteFields", "addTags", "changeDeck"} for action in operations))
                validate_attempt_outputs(run)

    def test_duplicate_global_id_is_rejected_before_mutation(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            root = Path(temp)
            first_job = build_job(root / "first", fake.endpoint)
            seed_job_managed_resources(fake.state, first_job)
            self.assertEqual("SUCCEEDED", make_exporter(first_job).run())
            original_id = next(iter(fake.state.notes))
            duplicate_id = original_id + 1
            duplicate = copy.deepcopy(fake.state.notes[original_id])
            duplicate["cards"] = [fake.state.next_card]
            duplicate["card_decks"] = {fake.state.next_card: "Default"}
            fake.state.next_card += 1
            fake.state.notes[duplicate_id] = duplicate

            run = make_exporter(build_job(root / "duplicate", fake.endpoint))
            self.assertEqual("FAILED", run.run())
            self.assertEqual(2, len(fake.state.notes))
            errors = (run.writer.output_root / "errors.ndjson").read_text(encoding="utf-8")
            self.assertIn("DUPLICATE_ID_CONFLICT", errors)
            operations = [json.loads(line)["action"] for line in (run.writer.output_root / "operations.ndjson").read_text(encoding="utf-8").splitlines()]
            self.assertFalse(any(action in {"createModel", "createDeck", "storeMediaFile", "addNote", "updateNoteFields", "addTags", "changeDeck"} for action in operations))
            validate_attempt_outputs(run)

    def test_validate_only_and_unresolved_receipt(self):
        with tempfile.TemporaryDirectory() as temp:
            job = build_job(Path(temp) / "pkg", "http://127.0.0.1:65534", unresolved=True, operation="validate")
            run = make_exporter(job)
            self.assertEqual("SUCCEEDED", run.run())
            self.assertTrue((run.writer.output_root / "receipt.json").is_file())
            validate_attempt_outputs(run)

    def test_path_escape_is_contract_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            job = build_job(Path(temp) / "pkg", "http://127.0.0.1:65534", path_escape=True, operation="validate")
            run = make_exporter(job)
            self.assertEqual("CONTRACT_REJECTED", run.run())
            receipt = json.loads((run.writer.output_root / "receipt.json").read_text(encoding="utf-8"))
            self.assertEqual("PATH_ESCAPE", receipt["error"]["code"])
            validate_attempt_outputs(run)

    def test_missing_ankiconnect_is_blocked_not_fallback(self):
        with tempfile.TemporaryDirectory() as temp:
            job = build_job(Path(temp) / "pkg", "http://127.0.0.1:65534")
            run = make_exporter(job)
            self.assertEqual("BLOCKED_ENVIRONMENT", run.run())
            receipt = json.loads((run.writer.output_root / "receipt.json").read_text(encoding="utf-8"))
            self.assertEqual("ANKICONNECT_UNAVAILABLE", receipt["error"]["code"])
            validate_attempt_outputs(run)

    def test_media_collision_fails_closed(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            job = build_job(Path(temp) / "pkg", fake.endpoint)
            seed_job_managed_resources(fake.state, job)
            fake.state.media[exporter.media_filename(job["media"])] = b"different bytes"
            run = make_exporter(job)
            self.assertEqual("FAILED", run.run())
            errors = (run.writer.output_root / "errors.ndjson").read_text(encoding="utf-8")
            self.assertIn("MEDIA_NAME_COLLISION", errors)
            validate_attempt_outputs(run)

    def test_response_loss_after_add_converges_on_retry(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            root = Path(temp)
            job = build_job(root / "pkg", fake.endpoint)
            seed_job_managed_resources(fake.state, job)
            fake.state.fail_after_add_once = True
            first = make_exporter(job)
            self.assertEqual("FAILED", first.run())
            self.assertEqual(1, len(fake.state.notes))
            first_result = json.loads(first.writer.result_path.read_text(encoding="utf-8"))
            self.assertEqual("unknown_external_state", first_result["status"])
            self.assertFalse(first_result["error"]["retryable"])
            self.assertEqual([], first_result["outputs"])
            validate_attempt_outputs(first)
            second = make_exporter(build_job(root / "pkg-retry", fake.endpoint))
            self.assertEqual("SUCCEEDED", second.run())
            self.assertEqual(1, len(fake.state.notes))
            receipt = json.loads((second.writer.output_root / "receipt.json").read_text(encoding="utf-8"))
            self.assertEqual(1, receipt["counts"]["already_current"])
            validate_attempt_outputs(second)

    def test_response_loss_after_media_store_converges_on_explicit_reconcile(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            root = Path(temp)
            fake.state.fail_after_store_once = True
            first_job = build_job(root / "pkg", fake.endpoint)
            seed_job_managed_resources(fake.state, first_job)
            first = make_exporter(first_job)
            self.assertEqual("FAILED", first.run())
            self.assertEqual(1, len(fake.state.media))
            self.assertEqual(0, len(fake.state.notes))
            first_result = json.loads(first.writer.result_path.read_text(encoding="utf-8"))
            self.assertEqual("unknown_external_state", first_result["status"])
            self.assertFalse(first_result["error"]["retryable"])
            self.assertEqual([], first_result["outputs"])
            validate_attempt_outputs(first)

            reconcile = make_exporter(build_job(root / "pkg-reconcile", fake.endpoint))
            self.assertEqual("SUCCEEDED", reconcile.run())
            self.assertEqual(1, len(fake.state.media))
            self.assertEqual(1, len(fake.state.notes))
            media_events = [json.loads(line) for line in (reconcile.writer.output_root / "media.ndjson").read_text(encoding="utf-8").splitlines()]
            self.assertEqual(["ALREADY_CURRENT"], [event["status"] for event in media_events])
            validate_attempt_outputs(reconcile)


if __name__ == "__main__":
    unittest.main()
