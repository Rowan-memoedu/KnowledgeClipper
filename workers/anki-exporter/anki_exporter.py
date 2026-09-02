#!/usr/bin/env python3
"""Common-contract Anki worker using the local AnkiConnect HTTP API."""

from __future__ import annotations

import argparse
import base64
import datetime as dt
import hashlib
import json
import math
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any, Callable


API_VERSION = 6
EXPORTER_VERSION = "1.4.0"
SUPPORTED_BUNDLE_KINDS = {"CardBundle", "LearningBundle"}
SUPPORTED_BUNDLE_SCHEMAS = {"1.0", "1.1"}
MANAGED_FIELDS = (
    "RQC_ID",
    "Front",
    "Back",
    "Context",
    "Extra",
    "Source",
    "RQC_Revision",
    "RQC_ContentHash",
)
ALL_MODEL_FIELDS = MANAGED_FIELDS + ("UserNotes",)
MEDIA_TOKEN = re.compile(r"\{\{media:([A-Za-z0-9._:-]+)\}\}")
DEFAULT_CSS = ".card { font-family: arial; font-size: 20px; text-align: left; }"
DEFAULT_FRONT_TEMPLATE = "{{Front}}<div class=\"context\">{{Context}}</div>"
DEFAULT_BACK_TEMPLATE = "{{FrontSide}}<hr id=answer>{{Back}}<div class=\"extra\">{{Extra}}</div><div class=\"source\">{{Source}}</div><div class=\"user-notes\">{{UserNotes}}</div>"
ORIGINAL_MEDIA_PROJECTION = "ordered-original-images.v1"
ORIGINAL_MEDIA_CSS = DEFAULT_CSS + "\n.kc-original-media img { max-width: 100%; height: auto; display: block; }"
ORIGINAL_MEDIA_FRONT_TEMPLATE = "<div class=\"kc-original-media kc-question-images\">{{Front}}</div><div class=\"context\">{{Context}}</div>"
ORIGINAL_MEDIA_BACK_TEMPLATE = "{{FrontSide}}<hr id=answer><div class=\"kc-original-media kc-answer-images\">{{Back}}</div><div class=\"extra\">{{Extra}}</div><div class=\"source\">{{Source}}</div><div class=\"user-notes\">{{UserNotes}}</div>"
IDENTIFIER = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
SEMVER = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$")
SHA256 = re.compile(r"^[A-Fa-f0-9]{64}$")
COMMON_SCHEMA_VERSION = "1.0.0"
WORKER_ID = "export.anki"
PROBE_READ_ONLY_ACTIONS = {
    "requestPermission",
    "version",
    "getActiveProfile",
    "apiReflect",
    "modelNames",
    "modelFieldNames",
    "modelTemplates",
    "modelStyling",
    "deckNames",
}
INITIALIZE_OPERATION = "initialize-managed"
INITIALIZE_ALLOWED_ACTIONS = PROBE_READ_ONLY_ACTIONS | {"createModel", "createDeck"}
MUTATING_ANKI_ACTIONS = {
    "createModel",
    "createDeck",
    "storeMediaFile",
    "addNote",
    "updateNoteFields",
    "addTags",
    "changeDeck",
}


class ExporterError(Exception):
    def __init__(self, code: str, message: str, *, retryable: bool = False):
        super().__init__(message)
        self.code = code
        self.retryable = retryable


class ContractError(ExporterError):
    pass


class EnvironmentBlocked(ExporterError):
    pass


class AnkiCallError(ExporterError):
    pass


class CancellationRequested(ExporterError):
    def __init__(self, reason: str):
        super().__init__(f"cancel.{reason}", f"Attempt cancellation requested: {reason}", retryable=reason == "timeout")
        self.reason = reason


def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")


def native_fs_path(path: Path) -> str:
    value = str(path.resolve())
    if os.name != "nt" or value.startswith("\\\\?\\"):
        return value
    if value.startswith("\\\\"):
        return "\\\\?\\UNC\\" + value[2:]
    return "\\\\?\\" + value


def path_exists(path: Path) -> bool:
    return os.path.exists(native_fs_path(path))


def path_is_file(path: Path) -> bool:
    return os.path.isfile(native_fs_path(path))


def path_size(path: Path) -> int:
    return os.path.getsize(native_fs_path(path))


def json_bytes(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with open(native_fs_path(path), "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_json(path: Path) -> dict[str, Any]:
    try:
        with open(native_fs_path(path), "r", encoding="utf-8") as handle:
            value = json.load(handle)
    except FileNotFoundError as exc:
        raise ContractError("FILE_MISSING", f"Missing required file: {path}") from exc
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ContractError("INVALID_JSON", f"Invalid UTF-8 JSON file: {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise ContractError("INVALID_JSON_ROOT", f"JSON root must be an object: {path}")
    return value


def write_json_atomic_once(path: Path, value: Any) -> None:
    os.makedirs(native_fs_path(path.parent), exist_ok=True)
    temporary = path.with_name(f".~{uuid.uuid4().hex[:8]}.tmp")
    try:
        with open(native_fs_path(temporary), "x", encoding="utf-8", newline="\n") as handle:
            json.dump(value, handle, ensure_ascii=False, sort_keys=True, indent=2)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        try:
            os.link(native_fs_path(temporary), native_fs_path(path))
        except FileExistsError as exc:
            raise ContractError("ATTEMPT_NOT_IMMUTABLE", f"Refusing to overwrite: {path}") from exc
    finally:
        try:
            os.unlink(native_fs_path(temporary))
        except FileNotFoundError:
            pass


def safe_relative_path(root: Path, relative: str) -> Path:
    if not isinstance(relative, str) or not relative.strip():
        raise ContractError("INVALID_PATH", "Artifact path must be a non-empty string")
    candidate = Path(relative)
    if candidate.is_absolute():
        raise ContractError("PATH_ESCAPE", f"Absolute paths are not allowed: {relative}")
    resolved_root = root.resolve()
    resolved = (resolved_root / candidate).resolve()
    if resolved != resolved_root and resolved_root not in resolved.parents:
        raise ContractError("PATH_ESCAPE", f"Path leaves job root: {relative}")
    posix = PurePosixPath(relative)
    if (
        relative != relative.strip()
        or "\\" in relative
        or re.search(r"%[0-9A-Fa-f]{2}", relative) is not None
        or str(posix) != relative
        or any(part in {".", ".."} or ":" in part for part in posix.parts)
    ):
        raise ContractError("PATH_NONCANONICAL", f"Path must be canonical job-root-relative POSIX form: {relative}")
    return resolved


def require_string(obj: dict[str, Any], key: str, *, code: str = "SCHEMA_INVALID") -> str:
    value = obj.get(key)
    if not isinstance(value, str) or not value.strip():
        raise ContractError(code, f"{key} must be a non-empty string")
    return value


def require_int(obj: dict[str, Any], key: str) -> int:
    value = obj.get(key)
    if isinstance(value, bool) or not isinstance(value, int) or value < 1:
        raise ContractError("SCHEMA_INVALID", f"{key} must be an integer >= 1")
    return value


def compute_content_hash(card: dict[str, Any], media_by_id: dict[str, dict[str, Any]]) -> str:
    refs = []
    ordered_projection = False
    for raw in card.get("media_refs", []):
        media_id = raw.get("media_id") if isinstance(raw, dict) else None
        media = media_by_id.get(media_id or "")
        entry = {
            "media_id": media_id,
            "field": raw.get("field") if isinstance(raw, dict) else None,
            "placement": raw.get("placement", "replace") if isinstance(raw, dict) else None,
            "kind": media.get("kind") if media else None,
            "sha256": media.get("sha256") if media else None,
        }
        if isinstance(raw, dict) and ("role" in raw or "ordinal" in raw):
            ordered_projection = True
            entry["role"] = raw.get("role")
            entry["ordinal"] = raw.get("ordinal")
        refs.append(entry)
    payload = {
        "card_id": card.get("card_id"),
        "revision": card.get("revision"),
        "deck_route_key": card.get("deck_route_key"),
        "note_type_key": card.get("note_type_key"),
        "fields": card.get("fields"),
        "tags": sorted(card.get("tags", [])),
        "media_refs": refs if ordered_projection else sorted(refs, key=lambda item: (str(item["media_id"]), str(item["field"]))),
    }
    return sha256_bytes(json_bytes(payload))


@dataclass(frozen=True)
class LoadedPackage:
    root: Path
    manifest: dict[str, Any]
    request: dict[str, Any]
    request_path: Path
    bundle_artifact: dict[str, Any]
    config_artifact: dict[str, Any]
    bundle: dict[str, Any]
    config: dict[str, Any]
    bundle_path: Path
    config_path: Path
    media_by_id: dict[str, dict[str, Any]]


class ReceiptWriter:
    def __init__(self, job_root: Path, request: dict[str, Any], progress_sink: Callable[[str], None] | None = None):
        self.job_root = job_root.resolve()
        self.request = request
        self.attempt_id = request["attemptId"]
        self.root = safe_relative_path(self.job_root, request["attemptPath"])
        self.output_root = safe_relative_path(self.job_root, request["outputPath"])
        self.progress_path = safe_relative_path(self.job_root, request["progressPath"])
        self.result_path = safe_relative_path(self.job_root, request["resultPath"])
        self.progress_sink = progress_sink
        os.makedirs(native_fs_path(self.root), exist_ok=True)
        os.makedirs(native_fs_path(self.output_root), exist_ok=False)
        if path_exists(self.result_path) or path_exists(self.progress_path):
            raise ContractError("ATTEMPT_NOT_IMMUTABLE", "Attempt result or progress file already exists")

    def write_once(self, name: str, value: Any) -> None:
        write_json_atomic_once(self.output_root / name, value)

    def append(self, name: str, value: Any) -> None:
        path = self.output_root / name
        with open(native_fs_path(path), "a", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")))
            handle.write("\n")

    def progress(self, event: str, **payload: Any) -> None:
        line = json.dumps({"timestamp": utc_now(), "event": event, **payload}, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        if self.progress_sink is not None:
            self.progress_sink(line)
            return
        with open(native_fs_path(self.progress_path), "a", encoding="utf-8", newline="\n") as handle:
            handle.write(line)
            handle.write("\n")

    def write_result(self, value: dict[str, Any]) -> None:
        write_json_atomic_once(self.result_path, value)

    def relative(self, path: Path) -> str:
        return path.resolve().relative_to(self.job_root).as_posix()


class AnkiClient:
    def __init__(self, url: str, timeout_seconds: float, audit: Callable[[dict[str, Any]], None]):
        self.url = url
        self.timeout_seconds = timeout_seconds
        self.audit = audit

    def invoke(self, action: str, params: dict[str, Any] | None = None) -> Any:
        request_id = uuid.uuid4().hex
        started = utc_now()
        body = json_bytes({"action": action, "version": API_VERSION, "params": params or {}})
        request = urllib.request.Request(
            self.url,
            data=body,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=self.timeout_seconds) as response:
                raw = response.read()
            payload = json.loads(raw.decode("utf-8"))
            if not isinstance(payload, dict) or "error" not in payload or "result" not in payload:
                raise ValueError("response must contain result and error")
            if payload["error"] is not None:
                raise AnkiCallError("ANKI_ACTION_FAILED", f"{action}: {payload['error']}")
            self.audit({"request_id": request_id, "action": action, "started_at": started, "finished_at": utc_now(), "status": "SUCCEEDED"})
            return payload["result"]
        except AnkiCallError as exc:
            self.audit({"request_id": request_id, "action": action, "started_at": started, "finished_at": utc_now(), "status": "FAILED", "error_code": exc.code, "message": str(exc)})
            raise
        except (OSError, urllib.error.URLError, TimeoutError) as exc:
            self.audit({"request_id": request_id, "action": action, "started_at": started, "finished_at": utc_now(), "status": "BLOCKED", "error_code": "ANKICONNECT_UNAVAILABLE", "message": str(exc)})
            raise EnvironmentBlocked("ANKICONNECT_UNAVAILABLE", f"Cannot reach AnkiConnect at {self.url}: {exc}", retryable=True) from exc
        except (UnicodeDecodeError, json.JSONDecodeError, ValueError) as exc:
            self.audit({"request_id": request_id, "action": action, "started_at": started, "finished_at": utc_now(), "status": "FAILED", "error_code": "ANKI_RESPONSE_INVALID", "message": str(exc)})
            raise AnkiCallError("ANKI_RESPONSE_INVALID", f"Invalid AnkiConnect response for {action}: {exc}", retryable=True) from exc


def _exact_keys(obj: dict[str, Any], required: set[str], optional: set[str], label: str) -> None:
    missing = required - obj.keys()
    extra = obj.keys() - required - optional
    if missing or extra:
        raise ContractError("COMMON_CONTRACT_INVALID", f"{label} keys invalid; missing={sorted(missing)}, extra={sorted(extra)}")


def _exact_schema_keys(obj: dict[str, Any], required: set[str], optional: set[str], label: str) -> None:
    missing = required - obj.keys()
    extra = obj.keys() - required - optional
    if missing or extra:
        raise ContractError("SCHEMA_INVALID", f"{label} keys invalid; missing={sorted(missing)}, extra={sorted(extra)}")


def _exact_config_keys(obj: dict[str, Any], required: set[str], optional: set[str], label: str) -> None:
    missing = required - obj.keys()
    extra = obj.keys() - required - optional
    if missing or extra:
        raise ContractError("CONFIG_INVALID", f"{label} keys invalid; missing={sorted(missing)}, extra={sorted(extra)}")


def _identifier(value: Any, label: str) -> str:
    if not isinstance(value, str) or not IDENTIFIER.fullmatch(value):
        raise ContractError("COMMON_CONTRACT_INVALID", f"{label} is not a common identifier")
    return value


def _semver(value: Any, label: str) -> str:
    if not isinstance(value, str) or not SEMVER.fullmatch(value):
        raise ContractError("COMMON_CONTRACT_INVALID", f"{label} is not a semantic version")
    return value


def _date_time(value: Any, label: str) -> str:
    if not isinstance(value, str):
        raise ContractError("COMMON_CONTRACT_INVALID", f"{label} must be date-time text")
    try:
        dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise ContractError("COMMON_CONTRACT_INVALID", f"{label} is not a valid date-time") from exc
    return value


def validate_artifact_reference(ref: Any, label: str) -> dict[str, Any]:
    if not isinstance(ref, dict):
        raise ContractError("COMMON_CONTRACT_INVALID", f"{label} must be an artifact reference")
    _exact_keys(ref, {"artifactRef", "kind", "contractVersion", "manifestPath", "expectedSha256"}, set(), label)
    _identifier(ref.get("artifactRef"), f"{label}.artifactRef")
    kind = ref.get("kind")
    if not isinstance(kind, str) or not re.fullmatch(r"[a-z0-9][a-z0-9._-]{0,127}", kind):
        raise ContractError("COMMON_CONTRACT_INVALID", f"{label}.kind is invalid")
    _semver(ref.get("contractVersion"), f"{label}.contractVersion")
    require_string(ref, "manifestPath", code="COMMON_CONTRACT_INVALID")
    if not isinstance(ref.get("expectedSha256"), str) or not SHA256.fullmatch(ref["expectedSha256"]):
        raise ContractError("COMMON_CONTRACT_INVALID", f"{label}.expectedSha256 must be sha256")
    return ref


def validate_task_input_reference(ref: Any, label: str) -> dict[str, Any]:
    if not isinstance(ref, dict):
        raise ContractError("COMMON_CONTRACT_INVALID", f"{label} must be a task input reference")
    _exact_keys(ref, {"artifactRef", "kind", "contractVersion", "expectedSha256"}, set(), label)
    _identifier(ref.get("artifactRef"), f"{label}.artifactRef")
    kind = ref.get("kind")
    if not isinstance(kind, str) or not re.fullmatch(r"[a-z0-9][a-z0-9._-]{0,127}", kind):
        raise ContractError("COMMON_CONTRACT_INVALID", f"{label}.kind is invalid")
    _semver(ref.get("contractVersion"), f"{label}.contractVersion")
    if not isinstance(ref.get("expectedSha256"), str) or not SHA256.fullmatch(ref["expectedSha256"]):
        raise ContractError("COMMON_CONTRACT_INVALID", f"{label}.expectedSha256 must be sha256")
    return ref


def validate_task_manifest(manifest: dict[str, Any]) -> None:
    required = {"schemaVersion", "taskId", "createdAt", "createdBy", "intent", "executionProfile", "inputRefs", "requestedOutputs", "initialPlanRef", "retentionClass"}
    _exact_keys(manifest, required, {"metadata"}, "task-manifest")
    if manifest.get("schemaVersion") != COMMON_SCHEMA_VERSION:
        raise ContractError("COMMON_CONTRACT_INVALID", "Unsupported common task-manifest schemaVersion")
    _identifier(manifest.get("taskId"), "task-manifest.taskId")
    _date_time(manifest.get("createdAt"), "task-manifest.createdAt")
    created_by = manifest.get("createdBy")
    if not isinstance(created_by, dict):
        raise ContractError("COMMON_CONTRACT_INVALID", "task-manifest.createdBy must be an object")
    _exact_keys(created_by, {"application", "version"}, set(), "task-manifest.createdBy")
    require_string(created_by, "application", code="COMMON_CONTRACT_INVALID")
    _semver(created_by.get("version"), "task-manifest.createdBy.version")
    if manifest.get("intent") not in {"clip.card", "clip.write", "clip.read-aloud", "clip.question"}:
        raise ContractError("COMMON_CONTRACT_INVALID", "task-manifest.intent is invalid")
    if manifest.get("executionProfile") != "cards-anki":
        raise ContractError("WRONG_EXECUTION_PROFILE", "Anki worker requires executionProfile=cards-anki")
    if not isinstance(manifest.get("inputRefs"), list) or not manifest["inputRefs"]:
        raise ContractError("COMMON_CONTRACT_INVALID", "task-manifest.inputRefs must be non-empty")
    for index, ref in enumerate(manifest["inputRefs"]):
        validate_task_input_reference(ref, f"task-manifest.inputRefs[{index}]")
    if not isinstance(manifest.get("requestedOutputs"), list) or not manifest["requestedOutputs"]:
        raise ContractError("COMMON_CONTRACT_INVALID", "task-manifest.requestedOutputs must be non-empty")
    require_string(manifest, "initialPlanRef", code="COMMON_CONTRACT_INVALID")
    if manifest.get("retentionClass") not in {"ephemeral", "standard", "evidence", "pinned"}:
        raise ContractError("COMMON_CONTRACT_INVALID", "task-manifest.retentionClass is invalid")
    if "metadata" in manifest and not isinstance(manifest["metadata"], dict):
        raise ContractError("COMMON_CONTRACT_INVALID", "task-manifest.metadata must be an object")


def validate_worker_request(request: dict[str, Any], task: dict[str, Any], request_relative: str) -> None:
    required = {"schemaVersion", "requestId", "operationId", "taskId", "runId", "stageId", "attemptId", "workerId", "operation", "idempotencyKey", "attemptPath", "outputPath", "progressPath", "resultPath", "cancelSignal", "inputRefs", "configSnapshot", "payload"}
    _exact_keys(request, required, set(), "worker-request")
    if request.get("schemaVersion") != COMMON_SCHEMA_VERSION:
        raise ContractError("COMMON_CONTRACT_INVALID", "Unsupported common worker-request schemaVersion")
    for key in ("requestId", "operationId", "taskId", "runId", "stageId", "attemptId", "workerId", "operation"):
        _identifier(request.get(key), f"worker-request.{key}")
    if request["taskId"] != task["taskId"]:
        raise ContractError("COMMON_CONTRACT_INVALID", "worker-request.taskId does not match task-manifest")
    if request["workerId"] != WORKER_ID:
        raise ContractError("WRONG_WORKER", f"worker-request.workerId must be {WORKER_ID}")
    if request["operation"] not in {"export", "validate", "probe", INITIALIZE_OPERATION}:
        raise ContractError("OPERATION_UNSUPPORTED", f"Unsupported operation: {request['operation']}")
    if not isinstance(request.get("idempotencyKey"), str) or not SHA256.fullmatch(request["idempotencyKey"]):
        raise ContractError("COMMON_CONTRACT_INVALID", "worker-request.idempotencyKey must be sha256")
    attempt = f"runs/{request['runId']}/stages/{request['stageId']}/attempts/{request['attemptId']}"
    expected_paths = {
        "attemptPath": attempt,
        "outputPath": f"{attempt}/outputs",
        "progressPath": f"{attempt}/progress.ndjson",
        "resultPath": f"{attempt}/result.json",
    }
    for key, expected in expected_paths.items():
        if request.get(key) != expected:
            raise ContractError("COMMON_PATH_INVALID", f"worker-request.{key} must be {expected}")
    if request_relative.replace("\\", "/") != f"{attempt}/request.json":
        raise ContractError("COMMON_PATH_INVALID", "worker-request file is outside its canonical attempt path")
    cancel_signal = request.get("cancelSignal")
    if not isinstance(cancel_signal, dict):
        raise ContractError("COMMON_CONTRACT_INVALID", "worker-request.cancelSignal must be an object")
    _exact_keys(cancel_signal, {"schemaVersion", "path", "gracePeriodMs"}, set(), "worker-request.cancelSignal")
    if cancel_signal.get("schemaVersion") != COMMON_SCHEMA_VERSION or cancel_signal.get("path") != f"{attempt}/cancel.json" or cancel_signal.get("gracePeriodMs") != 2000:
        raise ContractError("COMMON_PATH_INVALID", "worker-request.cancelSignal must reference canonical cancel.json with 2000 ms grace")
    if not isinstance(request.get("inputRefs"), list):
        raise ContractError("COMMON_CONTRACT_INVALID", "worker-request.inputRefs must be an array")
    for index, ref in enumerate(request["inputRefs"]):
        validate_artifact_reference(ref, f"worker-request.inputRefs[{index}]")
    if not isinstance(request.get("configSnapshot"), dict) or not isinstance(request.get("payload"), dict):
        raise ContractError("COMMON_CONTRACT_INVALID", "worker-request configSnapshot/payload must be objects")
    config_snapshot = request["configSnapshot"]
    if set(config_snapshot) - {"ankiConfigArtifactId"}:
        raise ContractError("CONFIG_SNAPSHOT_INVALID", "configSnapshot may only reference ankiConfigArtifactId")
    if "ankiConfigArtifactId" in config_snapshot:
        config_id = config_snapshot["ankiConfigArtifactId"]
        config_refs = [ref for ref in request["inputRefs"] if ref.get("kind") == "anki-export-config"]
        if not isinstance(config_id, str) or not any(ref.get("artifactRef") == config_id for ref in config_refs):
            raise ContractError("CONFIG_SNAPSHOT_INVALID", "configSnapshot must reference the request anki-export-config artifact")
    if request["operation"] == INITIALIZE_OPERATION:
        _validate_initialize_authorization(request)


def _validate_initialize_authorization(request: dict[str, Any]) -> None:
    payload = request["payload"]
    _exact_keys(payload, {"sideEffectAuthorization"}, set(), "initialize-managed.payload")
    authorization = payload.get("sideEffectAuthorization")
    if not isinstance(authorization, dict):
        raise ContractError("SIDE_EFFECT_AUTHORIZATION_REQUIRED", "initialize-managed requires sideEffectAuthorization")
    _exact_keys(
        authorization,
        {"schemaVersion", "authorizationId", "mode", "operation", "scope", "taskId", "configArtifactId", "authorizedAt"},
        set(),
        "initialize-managed.sideEffectAuthorization",
    )
    if authorization.get("schemaVersion") != COMMON_SCHEMA_VERSION:
        raise ContractError("SIDE_EFFECT_AUTHORIZATION_INVALID", "sideEffectAuthorization schemaVersion is invalid")
    _identifier(authorization.get("authorizationId"), "sideEffectAuthorization.authorizationId")
    if authorization.get("mode") != "explicit-user-action":
        raise ContractError("SIDE_EFFECT_AUTHORIZATION_INVALID", "initialize-managed must be an explicit user action")
    if authorization.get("operation") != INITIALIZE_OPERATION or authorization.get("scope") != "managed-models-and-decks":
        raise ContractError("SIDE_EFFECT_AUTHORIZATION_INVALID", "sideEffectAuthorization operation/scope mismatch")
    if authorization.get("taskId") != request["taskId"]:
        raise ContractError("SIDE_EFFECT_AUTHORIZATION_INVALID", "sideEffectAuthorization taskId mismatch")
    config_id = request["configSnapshot"].get("ankiConfigArtifactId")
    if authorization.get("configArtifactId") != config_id:
        raise ContractError("SIDE_EFFECT_AUTHORIZATION_INVALID", "sideEffectAuthorization config artifact mismatch")
    _date_time(authorization.get("authorizedAt"), "sideEffectAuthorization.authorizedAt")


def validate_artifact_manifest(artifact: dict[str, Any], ref: dict[str, Any]) -> None:
    required = {"schemaVersion", "artifactId", "kind", "contractVersion", "mediaType", "files", "createdAt", "producer", "inputRefs", "dataClass"}
    _exact_keys(artifact, required, {"metadata"}, f"artifact {ref['artifactRef']}")
    if artifact.get("schemaVersion") != COMMON_SCHEMA_VERSION:
        raise ContractError("COMMON_CONTRACT_INVALID", "Unsupported common artifact schemaVersion")
    if artifact.get("artifactId") != ref["artifactRef"] or any(artifact.get(key) != ref[key] for key in ("kind", "contractVersion")):
        raise ContractError("ARTIFACT_REFERENCE_MISMATCH", f"Artifact manifest does not match reference {ref['artifactRef']}")
    _identifier(artifact.get("artifactId"), "artifact.artifactId")
    _semver(artifact.get("contractVersion"), "artifact.contractVersion")
    require_string(artifact, "mediaType", code="COMMON_CONTRACT_INVALID")
    _date_time(artifact.get("createdAt"), "artifact.createdAt")
    producer = artifact.get("producer")
    if not isinstance(producer, dict):
        raise ContractError("COMMON_CONTRACT_INVALID", "artifact.producer must be an object")
    _exact_keys(producer, {"stageId", "workerId", "workerVersion", "attemptId"}, set(), "artifact.producer")
    for key in ("stageId", "workerId", "attemptId"):
        _identifier(producer.get(key), f"artifact.producer.{key}")
    _semver(producer.get("workerVersion"), "artifact.producer.workerVersion")
    if artifact.get("dataClass") not in {"public", "personal", "sensitive", "secret-prohibited"}:
        raise ContractError("COMMON_CONTRACT_INVALID", "artifact.dataClass is invalid")
    if artifact["dataClass"] == "secret-prohibited":
        raise ContractError("DATA_CLASS_PROHIBITED", f"Artifact {artifact['artifactId']} cannot be consumed")
    if not isinstance(artifact.get("inputRefs"), list):
        raise ContractError("COMMON_CONTRACT_INVALID", "artifact.inputRefs must be an array")
    for index, item in enumerate(artifact["inputRefs"]):
        validate_artifact_reference(item, f"artifact.inputRefs[{index}]")
    if not isinstance(artifact.get("files"), list) or not artifact["files"]:
        raise ContractError("COMMON_CONTRACT_INVALID", "artifact.files must be non-empty")
    if "metadata" in artifact and not isinstance(artifact["metadata"], dict):
        raise ContractError("COMMON_CONTRACT_INVALID", "artifact.metadata must be an object")


def load_artifact(root: Path, ref: dict[str, Any], *, require_entrypoint: bool = True) -> tuple[dict[str, Any], Path | None, set[Path]]:
    manifest_path = safe_relative_path(root, ref["manifestPath"])
    actual_manifest_hash = sha256_file(manifest_path)
    if actual_manifest_hash.lower() != ref["expectedSha256"].lower():
        raise ContractError("HASH_MISMATCH", f"Artifact manifest hash mismatch: {manifest_path}")
    artifact = load_json(manifest_path)
    validate_artifact_manifest(artifact, ref)
    files: set[Path] = set()
    for index, item in enumerate(artifact["files"]):
        if not isinstance(item, dict):
            raise ContractError("COMMON_CONTRACT_INVALID", f"artifact.files[{index}] must be an object")
        _exact_keys(item, {"path", "sha256", "bytes", "mediaType"}, set(), f"artifact.files[{index}]")
        path = safe_relative_path(root, require_string(item, "path", code="COMMON_CONTRACT_INVALID"))
        expected = require_string(item, "sha256", code="COMMON_CONTRACT_INVALID").lower()
        if not SHA256.fullmatch(expected):
            raise ContractError("COMMON_CONTRACT_INVALID", f"Invalid file sha256 in artifact {artifact['artifactId']}")
        require_string(item, "mediaType", code="COMMON_CONTRACT_INVALID")
        if not path_is_file(path) or path_size(path) != item.get("bytes"):
            raise ContractError("SIZE_MISMATCH", f"Artifact file missing or wrong size: {path}")
        if sha256_file(path) != expected:
            raise ContractError("HASH_MISMATCH", f"Artifact file hash mismatch: {path}")
        files.add(path)
    metadata = artifact.get("metadata", {})
    entrypoint_value = metadata.get("entrypoint")
    if entrypoint_value is None and not require_entrypoint:
        entrypoint = None
    else:
        entrypoint = safe_relative_path(root, require_string(metadata, "entrypoint", code="ARTIFACT_ENTRYPOINT_MISSING"))
        if entrypoint not in files:
            raise ContractError("ARTIFACT_ENTRYPOINT_INVALID", f"Artifact entrypoint is not declared in files: {entrypoint}")
    return artifact, entrypoint, files


def _reference_identity(ref: dict[str, Any]) -> tuple[str, str, str, str]:
    return (ref["artifactRef"], ref["kind"], ref["contractVersion"], ref["expectedSha256"].lower())


def _path_within(path: Path, parent: Path) -> bool:
    resolved = path.resolve()
    root = parent.resolve()
    return resolved == root or root in resolved.parents


_IDENTIFIER_PATH_PATTERN = r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}"


def _validate_intermediate_result_provenance(
    root: Path,
    task: dict[str, Any],
    ref: dict[str, Any],
    producer: dict[str, Any],
    normalized_manifest: str,
    run_id: str,
    stage_id: str,
    attempt_id: str,
) -> None:
    result_relative = f"runs/{run_id}/stages/{stage_id}/attempts/{attempt_id}/result.json"
    result_path = safe_relative_path(root, result_relative)
    try:
        result = load_json(result_path)
    except (ContractError, OSError, ValueError, json.JSONDecodeError) as exc:
        raise ContractError(
            "ANCESTRY_PRODUCER_RESULT_INVALID",
            f"Intermediate artifact has no readable canonical producer result: {normalized_manifest}",
        ) from exc
    expected_worker = {"id": producer["workerId"], "version": producer["workerVersion"]}
    if not isinstance(result, dict) or any((
        result.get("taskId") != task["taskId"],
        result.get("runId") != run_id,
        result.get("stageId") != stage_id,
        result.get("attemptId") != attempt_id,
        result.get("worker") != expected_worker,
        result.get("status") not in {"completed", "partial"},
        not isinstance(result.get("outputs"), list),
    )):
        raise ContractError(
            "ANCESTRY_PRODUCER_RESULT_INVALID",
            f"Intermediate producer result identity/status does not match its canonical path: {normalized_manifest}",
        )
    matching_outputs = [
        output for output in result["outputs"]
        if isinstance(output, dict)
        and output.get("manifestPath", "").replace("\\", "/") == normalized_manifest
        and all(output.get(key) == ref[key] for key in ("artifactRef", "kind", "contractVersion", "expectedSha256"))
    ]
    if len(matching_outputs) != 1:
        raise ContractError(
            "ANCESTRY_PRODUCER_RESULT_INVALID",
            f"Intermediate artifact is not bound exactly once by its producer result: {normalized_manifest}",
        )


def validate_request_input_ancestry(root: Path, task: dict[str, Any], request: dict[str, Any]) -> None:
    initial = {_reference_identity(ref): ref for ref in task["inputRefs"]}
    file_binding = tuple[str, int, str]
    cache: dict[
        tuple[str, str],
        tuple[set[tuple[str, str, str, str]], set[Path], dict[Path, file_binding]],
    ] = {}
    artifact_identities: dict[str, tuple[str, str, str, str]] = {}
    active: set[tuple[str, str]] = set()

    def visit(
        ref: dict[str, Any], *, request_root: bool, depth: int,
    ) -> tuple[set[tuple[str, str, str, str]], set[Path], dict[Path, file_binding]]:
        if depth > 32:
            raise ContractError("ANCESTRY_TOO_DEEP", "Artifact ancestry exceeds 32 levels")
        normalized_manifest = ref["manifestPath"].replace("\\", "/")
        key = (normalized_manifest, ref["expectedSha256"].lower())
        identity = _reference_identity(ref)
        prior_identity = artifact_identities.setdefault(ref["artifactRef"], identity)
        if prior_identity != identity:
            raise ContractError("ANCESTRY_IDENTITY_CONFLICT", f"Artifact identity changes within ancestry: {ref['artifactRef']}")
        if key in cache:
            return cache[key]
        if key in active:
            raise ContractError("ANCESTRY_CYCLE", f"Artifact ancestry cycle at {ref['artifactRef']}")
        active.add(key)
        try:
            artifact, _, files = load_artifact(root, ref, require_entrypoint=False)
            declared_bindings: dict[Path, file_binding] = {}
            for declared in artifact["files"]:
                declared_path = safe_relative_path(root, declared["path"])
                binding = (
                    declared["sha256"].lower(),
                    declared["bytes"],
                    declared["mediaType"],
                )
                prior = declared_bindings.setdefault(declared_path, binding)
                if prior != binding:
                    raise ContractError(
                        "ANCESTRY_IDENTITY_CONFLICT",
                        f"Artifact declares conflicting bindings for one file: {declared_path}",
                    )
            if identity in initial:
                expected_manifest = f"artifacts/{ref['artifactRef']}/artifact.json"
                if normalized_manifest != expected_manifest:
                    raise ContractError("INITIAL_ARTIFACT_PATH_INVALID", f"Initial artifact must use canonical manifest path: {expected_manifest}")
                result = ({identity}, files, declared_bindings)
                cache[key] = result
                return result

            producer = artifact["producer"]
            is_core_adapter = producer["workerId"] == "orchestrator.core-adapter"
            if request_root and not is_core_adapter:
                raise ContractError("DERIVED_PRODUCER_UNTRUSTED", f"Derived request input must be produced by orchestrator.core-adapter: {ref['artifactRef']}")
            if is_core_adapter:
                pattern = rf"^runs/({_IDENTIFIER_PATH_PATTERN})/adapters/({_IDENTIFIER_PATH_PATTERN})/{re.escape(ref['artifactRef'])}/v0001/artifact\.json$"
                match = re.fullmatch(pattern, normalized_manifest)
                if match is None or (request_root and match.group(1) != request["runId"]):
                    raise ContractError("DERIVED_PATH_INVALID", f"Core adapter manifest path is not canonical: {normalized_manifest}")
                if producer != {
                    "stageId": "core-adapter",
                    "workerId": "orchestrator.core-adapter",
                    "workerVersion": "1.0.0",
                    "attemptId": f"adapter-{ref['artifactRef']}",
                }:
                    raise ContractError("DERIVED_PRODUCER_UNTRUSTED", f"Core adapter producer identity is invalid: {ref['artifactRef']}")
            else:
                pattern = rf"^runs/({_IDENTIFIER_PATH_PATTERN})/stages/({_IDENTIFIER_PATH_PATTERN})/attempts/({_IDENTIFIER_PATH_PATTERN})/outputs/(.+)$"
                match = re.fullmatch(pattern, normalized_manifest)
                run_id, stage_id, attempt_id, tail = match.groups() if match is not None else ("", "", "", "")
                nested_common_manifest = tail.endswith("/artifact.json")
                flat_common_manifest = re.fullmatch(
                    r"[a-z0-9][a-z0-9._-]{0,127}\.artifact\.v[1-9][0-9]*\.json",
                    tail,
                ) is not None
                if (not tail or not (nested_common_manifest or flat_common_manifest)
                        or stage_id != producer["stageId"] or attempt_id != producer["attemptId"]):
                    raise ContractError("ANCESTRY_PATH_INVALID", f"Intermediate artifact path does not match producer stage/attempt: {normalized_manifest}")
                _validate_intermediate_result_provenance(
                    root, task, ref, producer, normalized_manifest, run_id, stage_id, attempt_id
                )

            parents = artifact["inputRefs"]
            if not parents:
                raise ContractError("ANCESTRY_DISCONNECTED", f"Derived artifact has no path to an initial input: {ref['artifactRef']}")
            leaves: set[tuple[str, str, str, str]] = set()
            parent_files: set[Path] = set()
            initial_file_bindings: dict[Path, file_binding] = {}
            for parent in parents:
                parent_leaves, validated_files, root_bindings = visit(
                    parent, request_root=False, depth=depth + 1,
                )
                leaves.update(parent_leaves)
                parent_files.update(validated_files)
                for path, binding in root_bindings.items():
                    prior = initial_file_bindings.setdefault(path, binding)
                    if prior != binding:
                        raise ContractError(
                            "ANCESTRY_IDENTITY_CONFLICT",
                            f"Initial ancestry declares conflicting bindings for one file: {path}",
                        )
            if not leaves or not leaves.issubset(initial):
                raise ContractError("ANCESTRY_DISCONNECTED", f"Derived artifact ancestry is not rooted in task manifest: {ref['artifactRef']}")

            manifest_directory = safe_relative_path(root, normalized_manifest).parent
            for file_path in files:
                if is_core_adapter:
                    if not _path_within(file_path, manifest_directory) and file_path not in parent_files:
                        raise ContractError("ARTIFACT_FILE_BYPASS", f"Core adapter artifact references an unowned file: {file_path}")
                elif not _path_within(file_path, manifest_directory):
                    # A producer may publish a transparent, zero-copy alias to an
                    # immutable initial input file. The initial artifact manifest
                    # has already been SHA-bound through task.manifest, its path is
                    # canonical, and load_artifact verified the actual file. Exact
                    # descriptor equality prevents relabelling an ancestor path.
                    if initial_file_bindings.get(file_path) != declared_bindings.get(file_path):
                        raise ContractError(
                            "ARTIFACT_FILE_BYPASS",
                            "Intermediate artifact references a file outside its output directory "
                            f"without an exact validated initial-root binding: {file_path}",
                        )
            result = (leaves, files | parent_files, initial_file_bindings)
            cache[key] = result
            return result
        finally:
            active.discard(key)

    for ref in request["inputRefs"]:
        visit(ref, request_root=True, depth=0)


def load_package(root: Path, manifest: dict[str, Any], request: dict[str, Any], request_path: Path) -> LoadedPackage:
    root = root.resolve()
    validate_request_input_ancestry(root, manifest, request)
    card_refs = [ref for ref in request["inputRefs"] if ref["kind"] in {"card-bundle", "learning-bundle"}]
    config_refs = [ref for ref in request["inputRefs"] if ref["kind"] == "anki-export-config"]
    if len(card_refs) != 1 or len(config_refs) != 1:
        raise ContractError("INPUT_ARTIFACTS_INVALID", "Anki worker requires exactly one card/learning bundle and one anki-export-config artifact")
    bundle_artifact, bundle_path, bundle_files = load_artifact(root, card_refs[0])
    config_artifact, config_path, _ = load_artifact(root, config_refs[0])
    assert bundle_path is not None and config_path is not None
    bundle = load_json(bundle_path)
    config = load_json(config_path)
    media_by_id = validate_bundle(root, bundle, bundle_files)
    validate_config(config)
    validate_cards(bundle, media_by_id, config)
    return LoadedPackage(root, manifest, request, request_path, bundle_artifact, config_artifact, bundle, config, bundle_path, config_path, media_by_id)


def load_probe_config(root: Path, manifest: dict[str, Any], request: dict[str, Any]) -> tuple[dict[str, Any], dict[str, Any], Path]:
    root = root.resolve()
    validate_request_input_ancestry(root, manifest, request)
    config_refs = [ref for ref in request["inputRefs"] if ref["kind"] == "anki-export-config"]
    if len(config_refs) != 1 or len(request["inputRefs"]) != 1:
        raise ContractError("INPUT_ARTIFACTS_INVALID", "Anki probe requires exactly one anki-export-config artifact")
    config_artifact, config_path, _ = load_artifact(root, config_refs[0])
    assert config_path is not None
    config = load_json(config_path)
    validate_config(config)
    return config_artifact, config, config_path


def validate_bundle(root: Path, bundle: dict[str, Any], artifact_files: set[Path]) -> dict[str, dict[str, Any]]:
    _exact_schema_keys(bundle, {"bundle_kind", "schema_version", "bundle_id", "cards", "media"}, set(), "bundle")
    if bundle.get("bundle_kind") not in SUPPORTED_BUNDLE_KINDS:
        raise ContractError("BUNDLE_KIND_UNSUPPORTED", f"Unsupported bundle_kind: {bundle.get('bundle_kind')!r}")
    if bundle.get("schema_version") not in SUPPORTED_BUNDLE_SCHEMAS:
        raise ContractError("BUNDLE_SCHEMA_UNSUPPORTED", f"Unsupported bundle schema: {bundle.get('schema_version')!r}")
    require_string(bundle, "bundle_id")
    cards = bundle.get("cards")
    media = bundle.get("media", [])
    if not isinstance(cards, list) or not isinstance(media, list):
        raise ContractError("SCHEMA_INVALID", "bundle.cards and bundle.media must be arrays")
    media_by_id: dict[str, dict[str, Any]] = {}
    for item in media:
        if not isinstance(item, dict):
            raise ContractError("SCHEMA_INVALID", "Each media item must be an object")
        _exact_schema_keys(item, {"media_id", "kind", "relative_path", "sha256", "size", "mime_type"}, set(), "bundle.media item")
        media_id = require_string(item, "media_id")
        if media_id in media_by_id:
            raise ContractError("DUPLICATE_MEDIA_ID", f"Duplicate media_id: {media_id}")
        kind = item.get("kind")
        if kind not in {"image", "audio"}:
            raise ContractError("MEDIA_KIND_UNSUPPORTED", f"Unsupported media kind for {media_id}: {kind!r}")
        if bundle["schema_version"] == "1.1" and (
            kind != "image" or not isinstance(item.get("mime_type"), str) or not item["mime_type"].startswith("image/")
        ):
            raise ContractError("MEDIA_DESTINATION_UNSUPPORTED", f"CardBundle 1.1 original media only accepts image media: {media_id}")
        expected_hash = require_string(item, "sha256").lower()
        if not re.fullmatch(r"[0-9a-f]{64}", expected_hash):
            raise ContractError("SCHEMA_INVALID", f"Invalid media sha256 for {media_id}")
        path = safe_relative_path(root, require_string(item, "relative_path"))
        if path not in artifact_files:
            raise ContractError("MEDIA_OUTSIDE_ARTIFACT", f"Media is not owned by the bundle artifact: {path}")
        if not path_is_file(path):
            raise ContractError("FILE_MISSING", f"Media file missing: {path}")
        actual_size = path_size(path)
        if item.get("size") != actual_size:
            raise ContractError("SIZE_MISMATCH", f"Media size mismatch for {media_id}")
        if sha256_file(path) != expected_hash:
            raise ContractError("HASH_MISMATCH", f"Media hash mismatch for {media_id}")
        item["_resolved_path"] = str(path)
        media_by_id[media_id] = item
    return media_by_id


def validate_config(config: dict[str, Any]) -> None:
    schema_version = config.get("schema_version")
    _exact_config_keys(
        config,
        {"schema_version", "endpoint", "expected_profile", "deck_routes", "note_types"},
        {"timeout_seconds", "max_media_bytes", "provision_managed"},
        "anki-export-config",
    )
    if schema_version not in {"anki-export-config.v1", "anki-export-config.v2"}:
        raise ContractError("CONFIG_UNSUPPORTED", "config.schema_version must be anki-export-config.v1 or anki-export-config.v2")
    endpoint = config.get("endpoint")
    try:
        parsed_endpoint = urllib.parse.urlsplit(endpoint) if isinstance(endpoint, str) else None
        endpoint_port = parsed_endpoint.port if parsed_endpoint is not None else None
    except ValueError as exc:
        raise ContractError("CONFIG_INVALID", "AnkiConnect endpoint has an invalid port") from exc
    canonical_endpoint = f"http://127.0.0.1:{endpoint_port}" if endpoint_port is not None else None
    if (
        parsed_endpoint is None
        or parsed_endpoint.scheme != "http"
        or parsed_endpoint.hostname != "127.0.0.1"
        or parsed_endpoint.username is not None
        or parsed_endpoint.password is not None
        or endpoint_port is None
        or not 1 <= endpoint_port <= 65535
        or parsed_endpoint.path != ""
        or parsed_endpoint.query != ""
        or parsed_endpoint.fragment != ""
        or endpoint != canonical_endpoint
    ):
        raise ContractError("CONFIG_INVALID", "AnkiConnect endpoint must be exactly http://127.0.0.1:<port>")
    if not isinstance(config.get("expected_profile"), str) or not config["expected_profile"].strip():
        raise ContractError("CONFIG_INVALID", "expected_profile must lock the exact Anki profile")
    timeout = config.get("timeout_seconds", 15)
    if isinstance(timeout, bool) or not isinstance(timeout, (int, float)) or not math.isfinite(timeout) or timeout <= 0:
        raise ContractError("CONFIG_INVALID", "timeout_seconds must be a positive finite number")
    if not isinstance(config.get("provision_managed", False), bool):
        raise ContractError("CONFIG_INVALID", "provision_managed must be a boolean")
    routes = config.get("deck_routes")
    note_types = config.get("note_types")
    if not isinstance(routes, dict) or not routes:
        raise ContractError("CONFIG_INVALID", "deck_routes must be a non-empty object")
    if not isinstance(note_types, dict) or not note_types:
        raise ContractError("CONFIG_INVALID", "note_types must be a non-empty object")
    maximum = config.get("max_media_bytes", 25 * 1024 * 1024)
    if isinstance(maximum, bool) or not isinstance(maximum, int) or maximum < 1:
        raise ContractError("CONFIG_INVALID", "max_media_bytes must be a positive integer")
    for key, deck in routes.items():
        if not isinstance(key, str) or IDENTIFIER.fullmatch(key) is None:
            raise ContractError("CONFIG_INVALID", "deck_routes keys must be safe identifiers")
        if (
            not isinstance(deck, str)
            or not deck.strip()
            or deck != deck.strip()
            or any(ord(character) < 32 for character in deck)
        ):
            raise ContractError("CONFIG_INVALID", f"deck_routes.{key} must be a non-empty Anki deck name")
    for key, mapping in note_types.items():
        if not isinstance(key, str) or IDENTIFIER.fullmatch(key) is None:
            raise ContractError("CONFIG_INVALID", "note_types keys must be safe identifiers")
        if not isinstance(mapping, dict):
            raise ContractError("CONFIG_INVALID", f"note_types.{key} must be an object")
        _exact_config_keys(
            mapping,
            {"model_name", "field_map", "required_fields"},
            {"css", "front_template", "back_template", "media_projection"} if schema_version == "anki-export-config.v2" else {"css", "front_template", "back_template"},
            f"note_types.{key}",
        )
        require_string(mapping, "model_name", code="CONFIG_INVALID")
        field_map = mapping.get("field_map")
        required = mapping.get("required_fields")
        if not isinstance(field_map, dict) or not field_map or not isinstance(required, list):
            raise ContractError("CONFIG_INVALID", f"note_types.{key} requires field_map and required_fields")
        if any(not isinstance(source, str) or IDENTIFIER.fullmatch(source) is None for source in field_map):
            raise ContractError("CONFIG_INVALID", f"note_types.{key} has an invalid semantic field name")
        targets = list(field_map.values())
        if any(not isinstance(target, str) or target not in {"Front", "Back", "Context", "Extra", "Source"} for target in targets):
            raise ContractError("CONFIG_INVALID", f"note_types.{key} maps to an unmanaged Anki field")
        if len(set(targets)) != len(targets):
            raise ContractError("CONFIG_INVALID", f"note_types.{key} maps multiple semantic fields to the same Anki field")
        if (
            any(not isinstance(source, str) or IDENTIFIER.fullmatch(source) is None for source in required)
            or len(set(required)) != len(required)
            or not set(required).issubset(field_map)
        ):
            raise ContractError("CONFIG_INVALID", f"note_types.{key}.required_fields must be unique mapped semantic fields")
        for optional_string in ("css", "front_template", "back_template"):
            if optional_string in mapping and not isinstance(mapping[optional_string], str):
                raise ContractError("CONFIG_INVALID", f"note_types.{key}.{optional_string} must be a string")
        media_projection = mapping.get("media_projection")
        if media_projection is not None:
            if media_projection != ORIGINAL_MEDIA_PROJECTION:
                raise ContractError("CONFIG_INVALID", f"note_types.{key}.media_projection is unsupported")
            if mapping["field_map"].get("question") != "Front" or mapping["field_map"].get("answer") != "Back":
                raise ContractError("CONFIG_INVALID", f"note_types.{key} original media must map question->Front and answer->Back")
            if "question" not in mapping["required_fields"] or "answer" in mapping["required_fields"]:
                raise ContractError("CONFIG_INVALID", f"note_types.{key} original media requires question and permits empty answer")
            if not {"css", "front_template", "back_template"}.issubset(mapping):
                raise ContractError("CONFIG_INVALID", f"note_types.{key} original media requires fixed CSS and templates")
            if mapping.get("css", ORIGINAL_MEDIA_CSS) != ORIGINAL_MEDIA_CSS:
                raise ContractError("CONFIG_INVALID", f"note_types.{key} original media CSS is fixed")
            if mapping.get("front_template", ORIGINAL_MEDIA_FRONT_TEMPLATE) != ORIGINAL_MEDIA_FRONT_TEMPLATE:
                raise ContractError("CONFIG_INVALID", f"note_types.{key} original media front template is fixed")
            if mapping.get("back_template", ORIGINAL_MEDIA_BACK_TEMPLATE) != ORIGINAL_MEDIA_BACK_TEMPLATE:
                raise ContractError("CONFIG_INVALID", f"note_types.{key} original media back template is fixed")
    if schema_version == "anki-export-config.v2" and not any(
        mapping.get("media_projection") == ORIGINAL_MEDIA_PROJECTION for mapping in note_types.values()
    ):
        raise ContractError("CONFIG_INVALID", "anki-export-config.v2 requires an ordered original-media note type")


def validate_cards(bundle: dict[str, Any], media_by_id: dict[str, dict[str, Any]], config: dict[str, Any]) -> None:
    seen: set[str] = set()
    maximum = config.get("max_media_bytes", 25 * 1024 * 1024)
    for card in bundle["cards"]:
        if not isinstance(card, dict):
            raise ContractError("SCHEMA_INVALID", "Each card must be an object")
        _exact_schema_keys(
            card,
            {"card_id", "revision", "content_hash", "status", "deck_route_key", "note_type_key", "fields", "tags", "media_refs"},
            set(),
            "bundle.cards item",
        )
        card_id = require_string(card, "card_id")
        if len(card_id) > 200 or not re.fullmatch(r"[A-Za-z0-9._:-]+", card_id):
            raise ContractError("SCHEMA_INVALID", f"card_id contains unsafe characters: {card_id!r}")
        if card_id in seen:
            raise ContractError("DUPLICATE_CARD_ID", f"Duplicate card_id in bundle: {card_id}")
        seen.add(card_id)
        require_int(card, "revision")
        status = card.get("status")
        if status not in {"ready", "unresolved"}:
            raise ContractError("SCHEMA_INVALID", f"Unsupported status for {card_id}: {status!r}")
        route_key = require_string(card, "deck_route_key")
        note_type_key = require_string(card, "note_type_key")
        if route_key not in config["deck_routes"]:
            raise ContractError("MAPPING_MISSING", f"No deck route mapping for {route_key}")
        if note_type_key not in config["note_types"]:
            raise ContractError("MAPPING_MISSING", f"No note type mapping for {note_type_key}")
        fields = card.get("fields")
        tags = card.get("tags", [])
        refs = card.get("media_refs", [])
        if not isinstance(fields, dict) or not all(isinstance(k, str) and isinstance(v, str) for k, v in fields.items()):
            raise ContractError("SCHEMA_INVALID", f"fields must be a string map for {card_id}")
        mapping = config["note_types"][note_type_key]
        ordered_original_media = mapping.get("media_projection") == ORIGINAL_MEDIA_PROJECTION
        if any(IDENTIFIER.fullmatch(field) is None for field in fields):
            raise ContractError("SCHEMA_INVALID", f"fields contains an invalid semantic field name for {card_id}")
        unmapped_fields = set(fields) - set(mapping["field_map"])
        if unmapped_fields:
            raise ContractError("FIELD_UNMAPPED", f"Unmapped semantic field(s) for {card_id}: {sorted(unmapped_fields)}")
        if not isinstance(tags, list) or not all(isinstance(tag, str) and tag.strip() and not re.search(r"\s", tag) for tag in tags):
            raise ContractError("SCHEMA_INVALID", f"tags must be non-empty Anki tags without whitespace for {card_id}")
        if not isinstance(refs, list):
            raise ContractError("SCHEMA_INVALID", f"media_refs must be an array for {card_id}")
        referenced_ids: set[str] = set()
        ordered_roles: list[tuple[str, int]] = []
        for ref in refs:
            if not isinstance(ref, dict):
                raise ContractError("SCHEMA_INVALID", f"Invalid media_ref for {card_id}")
            if bundle["schema_version"] == "1.1":
                _exact_schema_keys(ref, {"media_id", "field", "role", "ordinal"}, {"placement"}, f"media_ref for {card_id}")
            else:
                _exact_schema_keys(ref, {"media_id", "field"}, {"placement"}, f"media_ref for {card_id}")
            media_id = require_string(ref, "media_id")
            field = require_string(ref, "field")
            if media_id not in media_by_id:
                raise ContractError("MEDIA_MISSING", f"Unknown media_id {media_id} for {card_id}")
            if field not in fields:
                raise ContractError("SCHEMA_INVALID", f"Media field {field} missing for {card_id}")
            if ref.get("placement", "replace") not in {"replace", "append"}:
                raise ContractError("SCHEMA_INVALID", f"Invalid media placement for {card_id}")
            if media_by_id[media_id]["size"] > maximum:
                raise ContractError("MEDIA_TOO_LARGE", f"Media exceeds max_media_bytes: {media_id}")
            if bundle["schema_version"] == "1.1":
                role = ref.get("role")
                ordinal = ref.get("ordinal")
                if role not in {"questionImages", "answerImages"} or isinstance(ordinal, bool) or not isinstance(ordinal, int) or ordinal < 0:
                    raise ContractError("MEDIA_ORDER_INVALID", f"Invalid original media role/ordinal for {card_id}")
                if not ordered_original_media:
                    raise ContractError("MEDIA_DESTINATION_UNSUPPORTED", f"Anki destination has no ordered original-media mapping for {card_id}")
                expected_field = "question" if role == "questionImages" else "answer"
                if field != expected_field or ref.get("placement", "replace") != "append":
                    raise ContractError("MEDIA_ROLE_FIELD_MISMATCH", f"Original media role/field/placement mismatch for {card_id}")
                if media_by_id[media_id]["kind"] != "image":
                    raise ContractError("MEDIA_DESTINATION_UNSUPPORTED", f"Anki ordered original-media projection only accepts images: {media_id}")
                if media_id in referenced_ids:
                    raise ContractError("DUPLICATE_MEDIA_REF", f"Original media must be disjoint: {media_id}")
                ordered_roles.append((role, ordinal))
            referenced_ids.add(media_id)
        tokens = {match.group(1) for value in fields.values() for match in MEDIA_TOKEN.finditer(value)}
        if ordered_original_media:
            if bundle["schema_version"] != "1.1":
                raise ContractError("BUNDLE_SCHEMA_UNSUPPORTED", f"Ordered original media requires CardBundle schema 1.1: {card_id}")
            expected_roles = [
                *(('questionImages', index) for index in range(sum(1 for role, _ in ordered_roles if role == 'questionImages'))),
                *(('answerImages', index) for index in range(sum(1 for role, _ in ordered_roles if role == 'answerImages'))),
            ]
            if ordered_roles != expected_roles or not any(role == "questionImages" for role, _ in ordered_roles):
                raise ContractError("MEDIA_ORDER_INVALID", f"Original media refs must be questionImages then answerImages with contiguous ordinals: {card_id}")
            if tokens:
                raise ContractError("MEDIA_TOKEN_FORBIDDEN", f"Ordered original media fields must not contain legacy media tokens: {card_id}")
        if not tokens.issubset(referenced_ids):
            raise ContractError("MEDIA_MISSING", f"Unresolved media token(s) for {card_id}: {sorted(tokens - referenced_ids)}")
        expected_hash = require_string(card, "content_hash").lower()
        actual_hash = compute_content_hash(card, media_by_id)
        if expected_hash != actual_hash:
            raise ContractError("CONTENT_HASH_MISMATCH", f"content_hash mismatch for {card_id}: expected {expected_hash}, got {actual_hash}")


def media_filename(item: dict[str, Any]) -> str:
    suffix = Path(item["relative_path"]).suffix.lower()
    if not re.fullmatch(r"\.[a-z0-9]{1,10}", suffix):
        raise ContractError("MEDIA_EXTENSION_INVALID", f"Unsafe media extension: {suffix!r}")
    return f"rqc_{item['sha256']}{suffix}"


def anki_media_markup(item: dict[str, Any], filename: str) -> str:
    if item["kind"] == "image":
        return f'<img src="{filename}">'
    return f"[sound:{filename}]"


def render_card(card: dict[str, Any], package: LoadedPackage) -> tuple[dict[str, str], list[str]]:
    mapping = package.config["note_types"][card["note_type_key"]]
    semantic = dict(card["fields"])
    used_media: list[str] = []
    for ref in card.get("media_refs", []):
        item = package.media_by_id[ref["media_id"]]
        filename = media_filename(item)
        markup = anki_media_markup(item, filename)
        token = "{{media:" + ref["media_id"] + "}}"
        value = semantic[ref["field"]]
        if token in value:
            semantic[ref["field"]] = value.replace(token, markup)
        elif ref.get("placement", "replace") == "append":
            semantic[ref["field"]] = value + ("<br>" if value else "") + markup
        else:
            raise ContractError("MEDIA_TOKEN_MISSING", f"Expected {token} in field {ref['field']} for {card['card_id']}")
        used_media.append(ref["media_id"])
    if any(MEDIA_TOKEN.search(value) for value in semantic.values()):
        raise ContractError("MEDIA_TOKEN_UNRESOLVED", f"Unresolved media token for {card['card_id']}")
    fields = {name: "" for name in ALL_MODEL_FIELDS}
    fields["RQC_ID"] = card["card_id"]
    fields["RQC_Revision"] = str(card["revision"])
    fields["RQC_ContentHash"] = card["content_hash"]
    for source, target in mapping["field_map"].items():
        if source in semantic:
            fields[target] = semantic[source]
    for required in mapping["required_fields"]:
        target = mapping["field_map"].get(required)
        if target is None or not fields[target].strip():
            raise ContractError("REQUIRED_FIELD_EMPTY", f"Required semantic field {required} is empty for {card['card_id']}")
    return fields, used_media


def search_escape(value: str) -> str:
    return value.replace("\\", "\\\\").replace('"', '\\"')


class Exporter:
    def __init__(self, job_root: Path, task_manifest: str, worker_request: str, *, progress_sink: Callable[[str], None] | None = None):
        self.job_root = job_root.resolve()
        self.task_manifest_path = safe_relative_path(self.job_root, task_manifest)
        self.request_path = safe_relative_path(self.job_root, worker_request)
        self.task = load_json(self.task_manifest_path)
        self.request = load_json(self.request_path)
        validate_task_manifest(self.task)
        validate_worker_request(self.request, self.task, self.request_path.relative_to(self.job_root).as_posix())
        self.validate_only = self.request["operation"] == "validate" or self.request["payload"].get("validateOnly") is True
        self.writer = ReceiptWriter(self.job_root, self.request, progress_sink)
        self.package: LoadedPackage | None = None
        self.probe_config_artifact: dict[str, Any] | None = None
        self.probe_config: dict[str, Any] | None = None
        self.probe_config_path: Path | None = None
        self.connection_status: dict[str, Any] | None = None
        self.initialization_status: dict[str, Any] | None = None
        self.client: AnkiClient | None = None
        self.counts = {"succeeded": 0, "already_current": 0, "blocked_upstream": 0, "failed": 0}
        self.effect_attempted = False
        self.unknown_effect = False
        self.item_diagnostics: list[dict[str, Any]] = []

    def _error_record(self, exc: ExporterError, *, card_id: str | None = None) -> dict[str, Any]:
        return {"timestamp": utc_now(), "card_id": card_id, "error_code": exc.code, "message": str(exc), "retryable": exc.retryable}

    def run(self) -> str:
        started = utc_now()
        self.writer.progress("started", workerId=WORKER_ID, requestId=self.request["requestId"])
        try:
            self._check_cancel()
            if self.request["operation"] in {"probe", INITIALIZE_OPERATION}:
                self.probe_config_artifact, self.probe_config, self.probe_config_path = load_probe_config(self.job_root, self.task, self.request)
                if self.request["operation"] == "probe":
                    self._run_probe()
                else:
                    self._run_initialize_managed()
                status = "SUCCEEDED"
            else:
                self.package = load_package(self.job_root, self.task, self.request, self.request_path)
            if self.request["operation"] in {"probe", INITIALIZE_OPERATION}:
                pass
            elif self.validate_only:
                for card in self.package.bundle["cards"]:
                    self._check_cancel()
                    if card["status"] == "ready":
                        render_card(card, self.package)
                status = "SUCCEEDED"
                self.writer.write_once("preflight.json", {"status": "NOT_RUN", "mode": "validate-only", "timestamp": utc_now()})
            else:
                self._run_live()
                status = self._overall_status()
            self._finalize(status, started)
            return status
        except CancellationRequested as exc:
            self.writer.append("errors.ndjson", self._error_record(exc))
            status = "FAILED_TIMEOUT" if exc.reason == "timeout" else "CANCELLED"
            self._finalize(status, started, exc)
            return status
        except EnvironmentBlocked as exc:
            self.writer.append("errors.ndjson", self._error_record(exc))
            self._finalize("BLOCKED_ENVIRONMENT", started, exc)
            return "BLOCKED_ENVIRONMENT"
        except ContractError as exc:
            self.writer.append("errors.ndjson", self._error_record(exc))
            self._finalize("CONTRACT_REJECTED", started, exc)
            return "CONTRACT_REJECTED"
        except ExporterError as exc:
            self.writer.append("errors.ndjson", self._error_record(exc))
            self._finalize("FAILED", started, exc)
            return "FAILED"
        except Exception as exc:
            if self.effect_attempted:
                self.unknown_effect = True
            internal = ExporterError(
                "WORKER_INTERNAL_ERROR",
                f"Unexpected worker failure ({type(exc).__name__})",
                retryable=False,
            )
            self.writer.append("errors.ndjson", self._error_record(internal))
            self._finalize("FAILED", started, internal)
            return "FAILED"

    def _finalize(self, status: str, started: str, exc: ExporterError | None = None) -> None:
        self.writer.write_once("receipt.json", self._receipt(status, started, exc))
        artifact_ref = self._write_output_artifact()
        result = self._worker_result(status, started, exc, artifact_ref)
        self.writer.progress("finished", moduleStatus=status, status=result["status"])
        self.writer.write_result(result)

    def _receipt(self, status: str, started: str, exc: ExporterError | None = None) -> dict[str, Any]:
        effect_by_operation = {
            "validate": {"effect_class": "local-read-only", "network_scope": "none", "external_mutation": False, "local_artifact_write": True, "may_prompt_user": False},
            "probe": {"effect_class": "provider-read-only", "network_scope": "loopback-only", "external_mutation": False, "local_artifact_write": True, "may_prompt_user": True},
            "export": {"effect_class": "provider-write", "network_scope": "loopback-only", "external_mutation": True, "local_artifact_write": True, "may_prompt_user": True},
            INITIALIZE_OPERATION: {"effect_class": "provider-write", "network_scope": "loopback-only", "external_mutation": True, "local_artifact_write": True, "may_prompt_user": True},
        }
        result = {
            "attempt_id": self.writer.attempt_id,
            "task_id": self.task["taskId"],
            "request_id": self.request["requestId"],
            "exporter_version": EXPORTER_VERSION,
            "status": status,
            "started_at": started,
            "finished_at": utc_now(),
            "counts": self.counts,
            "operation": self.request["operation"],
            "operation_effect": effect_by_operation[self.request["operation"]],
        }
        if self.package:
            result["inputs"] = {
                "task_manifest_sha256": sha256_file(self.task_manifest_path),
                "worker_request_sha256": sha256_file(self.request_path),
                "bundle_id": self.package.bundle.get("bundle_id"),
                "bundle_sha256": sha256_file(self.package.bundle_path),
                "config_sha256": sha256_file(self.package.config_path),
            }
        elif self.probe_config_path is not None:
            result["inputs"] = {
                "task_manifest_sha256": sha256_file(self.task_manifest_path),
                "worker_request_sha256": sha256_file(self.request_path),
                "config_sha256": sha256_file(self.probe_config_path),
            }
            output_schema = "anki-connection-status.v1" if self.request["operation"] == "probe" else "anki-managed-initialization-status.v1"
            result["status_output"] = {
                "schema_version": output_schema,
                "redaction": "sha256-profile-model-deck-identifiers",
            }
        if exc:
            result["error"] = {"code": exc.code, "message": str(exc), "retryable": exc.retryable}
        return result

    def _write_output_artifact(self) -> dict[str, Any]:
        artifact_id = "anki-evidence-" + sha256_bytes(self.request["attemptId"].encode("utf-8"))[:24]
        is_probe = self.request["operation"] == "probe"
        is_initialize = self.request["operation"] == INITIALIZE_OPERATION
        manifest_name = "anki-connection-status.artifact.json" if is_probe else ("anki-managed-initialization-status.artifact.json" if is_initialize else "anki-export-evidence.artifact.json")
        manifest_path = self.writer.output_root / manifest_name
        files = []
        for name in sorted(os.listdir(native_fs_path(self.writer.output_root))):
            path = self.writer.output_root / name
            if path_is_file(path) and path.name != manifest_name:
                media_type = "application/x-ndjson" if path.suffix == ".ndjson" else "application/json"
                files.append({"path": self.writer.relative(path), "sha256": sha256_file(path), "bytes": path_size(path), "mediaType": media_type})
        kind = "anki-connection-status" if is_probe else ("anki-managed-initialization-status" if is_initialize else "anki-export-evidence")
        media_type = "application/vnd.knowledge-clipper.anki-connection-status+json" if is_probe else ("application/vnd.knowledge-clipper.anki-managed-initialization-status+json" if is_initialize else "application/vnd.rqc.anki-export-evidence+json")
        entrypoint_name = "connection-status.json" if is_probe else ("receipt.json" if is_initialize else "receipt.json")
        metadata = {"entrypoint": self.writer.relative(self.writer.output_root / entrypoint_name)}
        if is_initialize and path_exists(self.writer.output_root / "initialization-status.json"):
            metadata["statusPath"] = self.writer.relative(self.writer.output_root / "initialization-status.json")
        artifact = {
            "schemaVersion": COMMON_SCHEMA_VERSION,
            "artifactId": artifact_id,
            "kind": kind,
            "contractVersion": "1.0.0",
            "mediaType": media_type,
            "files": files,
            "createdAt": utc_now(),
            "producer": {"stageId": self.request["stageId"], "workerId": WORKER_ID, "workerVersion": EXPORTER_VERSION, "attemptId": self.request["attemptId"]},
            "inputRefs": self.request["inputRefs"],
            "dataClass": "personal",
            "metadata": metadata,
        }
        self.writer.write_once(manifest_name, artifact)
        return {"artifactRef": artifact_id, "kind": kind, "contractVersion": "1.0.0", "manifestPath": self.writer.relative(manifest_path), "expectedSha256": sha256_file(manifest_path)}

    def _worker_result(self, status: str, started: str, exc: ExporterError | None, artifact_ref: dict[str, Any]) -> dict[str, Any]:
        succeeded = status == "SUCCEEDED"
        common_status = "completed" if succeeded else ("unknown_external_state" if self.unknown_effect else ("partial" if status == "PARTIAL" else ("cancelled" if status == "CANCELLED" else "failed")))
        if exc is None and not succeeded:
            exc = ExporterError("ANKI_EXPORT_PARTIAL" if status == "PARTIAL" else "ANKI_EXPORT_FAILED", f"Anki exporter finished with {status}", retryable=status in {"PARTIAL", "FAILED"})
        error = None
        if exc is not None:
            if isinstance(exc, ContractError):
                category, blame = "contract", "input"
            elif isinstance(exc, EnvironmentBlocked):
                category, blame = "dependency", "dependency"
            elif common_status == "cancelled":
                category, blame = "cancelled", "user"
            elif exc.code == "cancel.timeout":
                category, blame = "transient", "orchestrator"
            elif status == "PARTIAL" or self.effect_attempted:
                category, blame = "effect", "dependency"
            else:
                category, blame = "worker", "worker"
            retryable = False if common_status == "unknown_external_state" else exc.retryable
            errors_path = self.writer.output_root / "errors.ndjson"
            details_path = errors_path if path_exists(errors_path) else self.writer.output_root / "receipt.json"
            error = {"code": exc.code, "category": category, "message": str(exc)[:2000], "retryable": retryable, "blame": blame, "detailsRef": self.writer.relative(details_path)}
        side_state = "verified" if succeeded else ("unknown" if self.unknown_effect else ("committed" if self.effect_attempted else "none"))
        warnings = []
        if self.counts["blocked_upstream"]:
            warnings.append(f"{self.counts['blocked_upstream']} card(s) remained BLOCKED_UPSTREAM")
        return {
            "schemaVersion": COMMON_SCHEMA_VERSION,
            "requestId": self.request["requestId"],
            "operationId": self.request["operationId"],
            "taskId": self.request["taskId"],
            "runId": self.request["runId"],
            "stageId": self.request["stageId"],
            "attemptId": self.request["attemptId"],
            "worker": {"id": WORKER_ID, "version": EXPORTER_VERSION},
            "contractVersion": "1.0.0",
            "idempotencyKey": self.request["idempotencyKey"],
            "status": common_status,
            "startedAt": started,
            "finishedAt": utc_now(),
            "outputs": [artifact_ref] if common_status in {"completed", "partial"} else [],
            "itemDiagnostics": self.item_diagnostics,
            "warnings": warnings,
            "error": error,
            "sideEffects": [] if self.request["operation"] == "probe" else [{"kind": "anki.initialize-managed" if self.request["operation"] == INITIALIZE_OPERATION else "anki.export", "idempotencyKey": self.request["idempotencyKey"], "state": side_state, "receiptRef": artifact_ref}],
            "metrics": dict(self.counts),
            "payload": {
                "moduleStatus": status,
                "validateOnly": self.validate_only,
                "operation": self.request["operation"],
                "connectionStatus": self.connection_status.get("status") if self.connection_status else None,
                "initializationStatus": self.initialization_status.get("status") if self.initialization_status else None,
            },
        }

    def _overall_status(self) -> str:
        if self.counts["failed"]:
            return "PARTIAL" if self.counts["succeeded"] or self.counts["already_current"] else "FAILED"
        if self.counts["blocked_upstream"]:
            return "PARTIAL" if self.counts["succeeded"] or self.counts["already_current"] else "FAILED"
        return "SUCCEEDED"

    def _invoke_effect(self, action: str, params: dict[str, Any]) -> Any:
        assert self.client is not None
        self._check_cancel()
        self.effect_attempted = True
        try:
            result = self.client.invoke(action, params)
        except ExporterError:
            self.unknown_effect = True
            raise
        self._check_cancel()
        return result

    def _check_cancel(self) -> None:
        signal_path = safe_relative_path(self.job_root, self.request["cancelSignal"]["path"])
        if not path_exists(signal_path):
            return
        signal = load_json(signal_path)
        required = {"schemaVersion", "attemptId", "operationId", "reason", "requestedAt", "graceDeadline"}
        _exact_keys(signal, required, {"commandId"}, "worker-cancel-signal")
        if signal.get("schemaVersion") != COMMON_SCHEMA_VERSION or signal.get("attemptId") != self.request["attemptId"] or signal.get("operationId") != self.request["operationId"]:
            raise ContractError("CANCEL_SIGNAL_INVALID", "Cancel signal identity does not match worker request")
        reason = signal.get("reason")
        if reason not in {"user_stop", "superseded", "task_cancel", "timeout"}:
            raise ContractError("CANCEL_SIGNAL_INVALID", "Cancel signal reason is invalid")
        requested = dt.datetime.fromisoformat(_date_time(signal.get("requestedAt"), "cancel.requestedAt").replace("Z", "+00:00"))
        deadline = dt.datetime.fromisoformat(_date_time(signal.get("graceDeadline"), "cancel.graceDeadline").replace("Z", "+00:00"))
        if deadline - requested != dt.timedelta(milliseconds=2000):
            raise ContractError("CANCEL_SIGNAL_INVALID", "Cancel signal grace deadline must be exactly 2000 ms")
        raise CancellationRequested(reason)

    def _item_error(self, card_id: str, exc: ExporterError, *, unknown: bool = False) -> dict[str, Any]:
        errors_path = self.writer.output_root / "errors.ndjson"
        category = "contract" if exc.code == "BLOCKED_UPSTREAM" else ("effect" if unknown else "dependency")
        return {
            "itemId": card_id,
            "status": "unknown_external_state" if unknown else "failed",
            "outputs": [],
            "error": {"code": exc.code, "category": category, "retryable": False if unknown else exc.retryable, "detailsRef": self.writer.relative(errors_path), "message": str(exc)[:2000], "blame": "input" if exc.code == "BLOCKED_UPSTREAM" else "dependency"},
        }

    def _run_live(self) -> None:
        assert self.package is not None
        self._check_cancel()
        config = self.package.config
        self.client = AnkiClient(config["endpoint"], float(config.get("timeout_seconds", 15)), lambda record: self.writer.append("operations.ndjson", record))
        permission = self.client.invoke("requestPermission")
        if not isinstance(permission, dict) or permission.get("permission") != "granted":
            raise EnvironmentBlocked("PERMISSION_DENIED", "AnkiConnect permission was not granted")
        version = self.client.invoke("version")
        if not isinstance(version, int) or version < API_VERSION:
            raise EnvironmentBlocked("API_VERSION_UNSUPPORTED", f"AnkiConnect API v{API_VERSION}+ required, got {version!r}")
        active_profile = self.client.invoke("getActiveProfile")
        expected_profile = config.get("expected_profile")
        if expected_profile and active_profile != expected_profile:
            raise EnvironmentBlocked("WRONG_PROFILE", f"Expected Anki profile {expected_profile!r}, got {active_profile!r}")
        reflected = self.client.invoke("apiReflect", {"scopes": ["actions"], "actions": ["modelNames", "modelFieldNames", "modelTemplates", "modelStyling", "createModel", "deckNames", "createDeck", "findNotes", "notesInfo", "cardsInfo", "addNote", "updateNoteFields", "addTags", "changeDeck", "retrieveMediaFile", "storeMediaFile"]})
        required_actions = {"modelNames", "modelFieldNames", "modelTemplates", "modelStyling", "createModel", "deckNames", "createDeck", "findNotes", "notesInfo", "cardsInfo", "addNote", "updateNoteFields", "addTags", "changeDeck", "retrieveMediaFile", "storeMediaFile"}
        actions = set(reflected.get("actions", [])) if isinstance(reflected, dict) else set()
        if not required_actions.issubset(actions):
            raise EnvironmentBlocked("API_ACTIONS_MISSING", f"AnkiConnect is missing actions: {sorted(required_actions - actions)}")
        self._check_cancel()
        self.writer.write_once("preflight.json", {"status": "SUCCEEDED", "timestamp": utc_now(), "api_version": version, "active_profile_sha256": sha256_bytes(str(active_profile).encode("utf-8")), "actions_verified": sorted(required_actions)})
        self._ensure_models_and_decks()
        ready_cards = [card for card in self.package.bundle["cards"] if card["status"] == "ready"]
        referenced_media = {ref["media_id"] for card in ready_cards for ref in card.get("media_refs", [])}
        for media_id in sorted(referenced_media):
            self._check_cancel()
            self._ensure_media(media_id)
        for card in self.package.bundle["cards"]:
            self._check_cancel()
            if card["status"] == "unresolved":
                self.counts["blocked_upstream"] += 1
                unresolved = ContractError("BLOCKED_UPSTREAM", f"Card {card['card_id']} is unresolved")
                self.writer.append("errors.ndjson", self._error_record(unresolved, card_id=card["card_id"]))
                self.writer.append("items.ndjson", {"timestamp": utc_now(), "card_id": card["card_id"], "status": "BLOCKED_UPSTREAM"})
                self.item_diagnostics.append(self._item_error(card["card_id"], unresolved))
                continue
            unknown_before = self.unknown_effect
            try:
                status, note_id = self._export_card(card)
                if status == "ALREADY_CURRENT":
                    self.counts["already_current"] += 1
                else:
                    self.counts["succeeded"] += 1
                self.writer.append("items.ndjson", {"timestamp": utc_now(), "card_id": card["card_id"], "revision": card["revision"], "status": status, "note_id": note_id})
                self.item_diagnostics.append({"itemId": card["card_id"], "status": "completed", "outputs": [], "error": None})
            except ExporterError as exc:
                self.counts["failed"] += 1
                self.writer.append("errors.ndjson", self._error_record(exc, card_id=card["card_id"]))
                self.writer.append("items.ndjson", {"timestamp": utc_now(), "card_id": card["card_id"], "revision": card["revision"], "status": "FAILED", "error_code": exc.code})
                self.item_diagnostics.append(self._item_error(card["card_id"], exc, unknown=self.unknown_effect and not unknown_before))

    def _ensure_models_and_decks(self) -> None:
        assert self.package is not None and self.client is not None
        config = self.package.config
        raw_model_names = self.client.invoke("modelNames")
        if not isinstance(raw_model_names, list) or not all(isinstance(name, str) for name in raw_model_names):
            raise AnkiCallError("ANKI_RESPONSE_INVALID", "modelNames must return an array of strings", retryable=True)
        model_names = set(raw_model_names)
        for mapping in config["note_types"].values():
            model_name = mapping["model_name"]
            if model_name not in model_names:
                raise EnvironmentBlocked("MODEL_MISSING", f"Managed model missing; run explicit initialize-managed first: {model_name}")
            actual_fields = self.client.invoke("modelFieldNames", {"modelName": model_name})
            if actual_fields != list(ALL_MODEL_FIELDS):
                raise EnvironmentBlocked("MODEL_SCHEMA_CONFLICT", f"Model {model_name!r} fields differ: {actual_fields!r}")
            templates = self.client.invoke("modelTemplates", {"modelName": model_name})
            expected_template = {"Card 1": {"Front": mapping.get("front_template", DEFAULT_FRONT_TEMPLATE), "Back": mapping.get("back_template", DEFAULT_BACK_TEMPLATE)}}
            if templates != expected_template:
                raise EnvironmentBlocked("MODEL_SCHEMA_CONFLICT", f"Model {model_name!r} templates differ")
            styling = self.client.invoke("modelStyling", {"modelName": model_name})
            if not isinstance(styling, dict) or styling.get("css", "").replace("\r\n", "\n").strip() != mapping.get("css", DEFAULT_CSS).replace("\r\n", "\n").strip():
                raise EnvironmentBlocked("MODEL_SCHEMA_CONFLICT", f"Model {model_name!r} styling differs")
        raw_deck_names = self.client.invoke("deckNames")
        if not isinstance(raw_deck_names, list) or not all(isinstance(name, str) for name in raw_deck_names):
            raise AnkiCallError("ANKI_RESPONSE_INVALID", "deckNames must return an array of strings", retryable=True)
        deck_names = set(raw_deck_names)
        for deck in sorted(set(config["deck_routes"].values())):
            if deck not in deck_names:
                raise EnvironmentBlocked("DECK_MISSING", f"Deck missing; run explicit initialize-managed first: {deck}")

    def _run_probe(self) -> None:
        assert self.probe_config is not None
        config = self.probe_config
        observed_actions: list[str] = []

        def audit(record: dict[str, Any]) -> None:
            action = record.get("action")
            if isinstance(action, str):
                observed_actions.append(action)
            self.writer.append("operations.ndjson", record)

        self.client = AnkiClient(config["endpoint"], float(config.get("timeout_seconds", 15)), audit)
        permission = self.client.invoke("requestPermission")
        if not isinstance(permission, dict) or permission.get("permission") != "granted":
            raise EnvironmentBlocked("PERMISSION_DENIED", "AnkiConnect permission was not granted")
        version = self.client.invoke("version")
        if not isinstance(version, int) or isinstance(version, bool) or version < API_VERSION:
            raise EnvironmentBlocked("API_VERSION_UNSUPPORTED", f"AnkiConnect API v{API_VERSION}+ required, got {version!r}")
        active_profile = self.client.invoke("getActiveProfile")
        if active_profile != config["expected_profile"]:
            raise EnvironmentBlocked("WRONG_PROFILE", "The active Anki profile does not match expected_profile")

        reflected_names = PROBE_READ_ONLY_ACTIONS - {"requestPermission", "version", "getActiveProfile", "apiReflect"}
        reflected = self.client.invoke("apiReflect", {"scopes": ["actions"], "actions": sorted(reflected_names)})
        reflected_actions = set(reflected.get("actions", [])) if isinstance(reflected, dict) else set()
        if not reflected_names.issubset(reflected_actions):
            raise EnvironmentBlocked("API_ACTIONS_MISSING", f"AnkiConnect is missing read-only actions: {sorted(reflected_names - reflected_actions)}")

        raw_model_names = self.client.invoke("modelNames")
        if not isinstance(raw_model_names, list) or not all(isinstance(name, str) for name in raw_model_names):
            raise AnkiCallError("ANKI_RESPONSE_INVALID", "modelNames must return an array of strings", retryable=True)
        model_names = set(raw_model_names)
        model_checks: list[dict[str, Any]] = []
        migration_required = False
        initialization_required = False
        for note_type_key, mapping in config["note_types"].items():
            model_name = mapping["model_name"]
            if model_name not in model_names:
                initialization_required = True
                model_checks.append({
                    "noteTypeKey": note_type_key,
                    "modelNameSha256": sha256_bytes(model_name.encode("utf-8")),
                    "status": "missing",
                })
                continue
            fields = self.client.invoke("modelFieldNames", {"modelName": model_name})
            templates = self.client.invoke("modelTemplates", {"modelName": model_name})
            styling = self.client.invoke("modelStyling", {"modelName": model_name})
            expected_template = {"Card 1": {"Front": mapping.get("front_template", DEFAULT_FRONT_TEMPLATE), "Back": mapping.get("back_template", DEFAULT_BACK_TEMPLATE)}}
            field_match = fields == list(ALL_MODEL_FIELDS)
            template_match = templates == expected_template
            css_match = isinstance(styling, dict) and styling.get("css", "").replace("\r\n", "\n").strip() == mapping.get("css", DEFAULT_CSS).replace("\r\n", "\n").strip()
            check_status = "ready" if field_match and template_match and css_match else "migration_required"
            migration_required = migration_required or check_status == "migration_required"
            model_checks.append({
                "noteTypeKey": note_type_key,
                "modelNameSha256": sha256_bytes(model_name.encode("utf-8")),
                "status": check_status,
                "fieldMatch": field_match,
                "templateMatch": template_match,
                "cssMatch": css_match,
            })

        raw_deck_names = self.client.invoke("deckNames")
        if not isinstance(raw_deck_names, list) or not all(isinstance(name, str) for name in raw_deck_names):
            raise AnkiCallError("ANKI_RESPONSE_INVALID", "deckNames must return an array of strings", retryable=True)
        deck_names = set(raw_deck_names)
        deck_checks = []
        for route_key, deck_name in sorted(config["deck_routes"].items()):
            exists = deck_name in deck_names
            initialization_required = initialization_required or not exists
            deck_checks.append({
                "routeKey": route_key,
                "deckNameSha256": sha256_bytes(deck_name.encode("utf-8")),
                "status": "ready" if exists else "missing",
            })

        forbidden = sorted(set(observed_actions) & MUTATING_ANKI_ACTIONS)
        if forbidden:
            raise AnkiCallError("PROBE_SIDE_EFFECT_VIOLATION", f"Read-only probe invoked mutating actions: {forbidden}")
        connection_state = "migration_required" if migration_required else ("needs_initialization" if initialization_required else "ready")
        self.connection_status = {
            "schemaVersion": "anki-connection-status.v1",
            "status": connection_state,
            "checkedAt": utc_now(),
            "operation": "probe",
            "effectClass": "provider-read-only",
            "networkScope": "loopback-only",
            "externalMutation": False,
            "localArtifactWrite": True,
            "mayPromptUser": True,
            "permission": "granted",
            "apiVersion": version,
            "activeProfileSha256": sha256_bytes(str(active_profile).encode("utf-8")),
            "actionsVerified": sorted(reflected_names),
            "actionsObserved": observed_actions,
            "models": model_checks,
            "decks": deck_checks,
            "initializationWouldWrite": initialization_required,
            "migrationRequired": migration_required,
            "mutatingActionsObserved": [],
        }
        self.writer.write_once("connection-status.json", self.connection_status)
        self.writer.write_once("preflight.json", {"status": "SUCCEEDED", "mode": "read-only-probe", "timestamp": utc_now(), "connection_status": connection_state})

    def _run_initialize_managed(self) -> None:
        assert self.probe_config is not None
        config = self.probe_config
        if config.get("provision_managed") is not True:
            raise ContractError("PROVISION_MANAGED_DISABLED", "initialize-managed requires provision_managed=true in the SHA-bound config artifact")
        if "anki-managed-initialization-status" not in self.task["requestedOutputs"]:
            raise ContractError("REQUESTED_OUTPUT_INVALID", "initialize-managed task must request anki-managed-initialization-status")

        observed_actions: list[str] = []

        def audit(record: dict[str, Any]) -> None:
            action = record.get("action")
            if isinstance(action, str):
                observed_actions.append(action)
            self.writer.append("operations.ndjson", record)

        self.client = AnkiClient(config["endpoint"], float(config.get("timeout_seconds", 15)), audit)
        permission = self.client.invoke("requestPermission")
        if not isinstance(permission, dict) or permission.get("permission") != "granted":
            raise EnvironmentBlocked("PERMISSION_DENIED", "AnkiConnect permission was not granted")
        version = self.client.invoke("version")
        if not isinstance(version, int) or isinstance(version, bool) or version < API_VERSION:
            raise EnvironmentBlocked("API_VERSION_UNSUPPORTED", f"AnkiConnect API v{API_VERSION}+ required, got {version!r}")
        active_profile = self.client.invoke("getActiveProfile")
        if active_profile != config["expected_profile"]:
            raise EnvironmentBlocked("WRONG_PROFILE", "The active Anki profile does not match expected_profile")
        reflected = self.client.invoke("apiReflect", {"scopes": ["actions"], "actions": sorted(INITIALIZE_ALLOWED_ACTIONS - {"requestPermission", "version", "getActiveProfile", "apiReflect"})})
        reflected_actions = set(reflected.get("actions", [])) if isinstance(reflected, dict) else set()
        required_reflected = INITIALIZE_ALLOWED_ACTIONS - {"requestPermission", "version", "getActiveProfile", "apiReflect"}
        if not required_reflected.issubset(reflected_actions):
            raise EnvironmentBlocked("API_ACTIONS_MISSING", f"AnkiConnect is missing initialization actions: {sorted(required_reflected - reflected_actions)}")

        model_specs: dict[str, dict[str, Any]] = {}
        model_note_types: dict[str, list[str]] = {}
        for note_type_key, mapping in config["note_types"].items():
            model_name = mapping["model_name"]
            spec = {
                "fields": list(ALL_MODEL_FIELDS),
                "templates": {"Card 1": {"Front": mapping.get("front_template", DEFAULT_FRONT_TEMPLATE), "Back": mapping.get("back_template", DEFAULT_BACK_TEMPLATE)}},
                "css": mapping.get("css", DEFAULT_CSS),
            }
            if model_name in model_specs and model_specs[model_name] != spec:
                raise ContractError("CONFIG_INVALID", f"Managed model {model_name!r} has conflicting note type definitions")
            model_specs[model_name] = spec
            model_note_types.setdefault(model_name, []).append(note_type_key)

        raw_model_names = self.client.invoke("modelNames")
        if not isinstance(raw_model_names, list) or not all(isinstance(name, str) for name in raw_model_names):
            raise AnkiCallError("ANKI_RESPONSE_INVALID", "modelNames must return an array of strings", retryable=True)
        existing_models = set(raw_model_names)
        missing_models: list[str] = []
        model_rows: list[dict[str, Any]] = []
        for model_name in sorted(model_specs):
            spec = model_specs[model_name]
            if model_name not in existing_models:
                missing_models.append(model_name)
                continue
            fields = self.client.invoke("modelFieldNames", {"modelName": model_name})
            templates = self.client.invoke("modelTemplates", {"modelName": model_name})
            styling = self.client.invoke("modelStyling", {"modelName": model_name})
            if fields != spec["fields"] or templates != spec["templates"] or not isinstance(styling, dict) or styling.get("css", "").replace("\r\n", "\n").strip() != spec["css"].replace("\r\n", "\n").strip():
                raise EnvironmentBlocked("MODEL_SCHEMA_CONFLICT", f"Existing managed model differs and will not be modified: {model_name!r}")
            model_rows.append({
                "modelNameSha256": sha256_bytes(model_name.encode("utf-8")),
                "noteTypeKeys": sorted(model_note_types[model_name]),
                "status": "already_current",
                "readback": "exact",
            })

        raw_deck_names = self.client.invoke("deckNames")
        if not isinstance(raw_deck_names, list) or not all(isinstance(name, str) for name in raw_deck_names):
            raise AnkiCallError("ANKI_RESPONSE_INVALID", "deckNames must return an array of strings", retryable=True)
        existing_decks = set(raw_deck_names)
        managed_decks = sorted(set(config["deck_routes"].values()))
        missing_decks = [deck for deck in managed_decks if deck not in existing_decks]
        deck_rows = [
            {"deckNameSha256": sha256_bytes(deck.encode("utf-8")), "status": "already_current", "readback": "exact"}
            for deck in managed_decks if deck in existing_decks
        ]
        self.writer.write_once("preflight.json", {
            "status": "SUCCEEDED",
            "mode": "explicit-managed-initialization",
            "timestamp": utc_now(),
            "api_version": version,
            "active_profile_sha256": sha256_bytes(str(active_profile).encode("utf-8")),
            "missing_models_sha256": [sha256_bytes(name.encode("utf-8")) for name in missing_models],
            "missing_decks_sha256": [sha256_bytes(name.encode("utf-8")) for name in missing_decks],
            "existing_resources_verified_before_write": True,
        })

        for model_name in missing_models:
            spec = model_specs[model_name]
            self._invoke_effect("createModel", {
                "modelName": model_name,
                "inOrderFields": spec["fields"],
                "css": spec["css"],
                "isCloze": False,
                "cardTemplates": [{"Name": "Card 1", "Front": spec["templates"]["Card 1"]["Front"], "Back": spec["templates"]["Card 1"]["Back"]}],
            })
            try:
                names = self.client.invoke("modelNames")
                fields = self.client.invoke("modelFieldNames", {"modelName": model_name})
                templates = self.client.invoke("modelTemplates", {"modelName": model_name})
                styling = self.client.invoke("modelStyling", {"modelName": model_name})
                if model_name not in names or fields != spec["fields"] or templates != spec["templates"] or not isinstance(styling, dict) or styling.get("css", "").replace("\r\n", "\n").strip() != spec["css"].replace("\r\n", "\n").strip():
                    raise AnkiCallError("INITIALIZATION_READBACK_MISMATCH", f"Created model failed exact readback: {model_name!r}")
            except ExporterError:
                self.unknown_effect = True
                raise
            model_rows.append({
                "modelNameSha256": sha256_bytes(model_name.encode("utf-8")),
                "noteTypeKeys": sorted(model_note_types[model_name]),
                "status": "created",
                "readback": "exact",
            })
            self.counts["succeeded"] += 1
            self.item_diagnostics.append({"itemId": "model." + sha256_bytes(model_name.encode("utf-8"))[:24], "status": "completed", "outputs": [], "error": None})

        for deck in missing_decks:
            self._invoke_effect("createDeck", {"deck": deck})
            try:
                names = self.client.invoke("deckNames")
                if not isinstance(names, list) or deck not in names:
                    raise AnkiCallError("INITIALIZATION_READBACK_MISMATCH", "Created deck failed exact readback")
            except ExporterError:
                self.unknown_effect = True
                raise
            deck_rows.append({"deckNameSha256": sha256_bytes(deck.encode("utf-8")), "status": "created", "readback": "exact"})
            self.counts["succeeded"] += 1
            self.item_diagnostics.append({"itemId": "deck." + sha256_bytes(deck.encode("utf-8"))[:24], "status": "completed", "outputs": [], "error": None})

        already_current = len(model_rows) + len(deck_rows) - self.counts["succeeded"]
        self.counts["already_current"] = already_current
        forbidden = sorted(set(observed_actions) & (MUTATING_ANKI_ACTIONS - {"createModel", "createDeck"}))
        if forbidden:
            self.unknown_effect = self.effect_attempted
            raise AnkiCallError("INITIALIZATION_SCOPE_VIOLATION", f"Managed initialization invoked forbidden actions: {forbidden}")
        created = self.counts["succeeded"]
        self.initialization_status = {
            "schemaVersion": "anki-managed-initialization-status.v1",
            "status": "initialized" if created else "already_current",
            "checkedAt": utc_now(),
            "operation": INITIALIZE_OPERATION,
            "effectClass": "provider-write",
            "networkScope": "loopback-only",
            "externalMutation": created > 0,
            "permission": "granted",
            "apiVersion": version,
            "activeProfileSha256": sha256_bytes(str(active_profile).encode("utf-8")),
            "authorization": {"mode": "explicit-user-action", "scope": "managed-models-and-decks"},
            "models": sorted(model_rows, key=lambda item: item["modelNameSha256"]),
            "decks": sorted(deck_rows, key=lambda item: item["deckNameSha256"]),
            "writes": {"modelsCreated": len(missing_models), "decksCreated": len(missing_decks)},
            "actionsObserved": observed_actions,
            "mutatingActionsObserved": [action for action in observed_actions if action in {"createModel", "createDeck"}],
            "forbiddenActionsObserved": [],
            "automaticRetryAllowed": False,
            "replayPolicy": "explicit-reconcile-query-then-create-missing",
            "notesMediaAndExistingResourcesModified": False,
        }
        self.writer.write_once("initialization-status.json", self.initialization_status)

    def _ensure_media(self, media_id: str) -> None:
        assert self.package is not None and self.client is not None
        item = self.package.media_by_id[media_id]
        filename = media_filename(item)
        expected = item["sha256"]
        existing = self.client.invoke("retrieveMediaFile", {"filename": filename})
        if existing is not False:
            try:
                existing_hash = sha256_bytes(base64.b64decode(existing, validate=True))
            except (ValueError, TypeError) as exc:
                raise AnkiCallError("MEDIA_READBACK_INVALID", f"Invalid media readback for {filename}") from exc
            if existing_hash != expected:
                raise AnkiCallError("MEDIA_NAME_COLLISION", f"Anki media collision for {filename}")
            self.writer.append("media.ndjson", {"timestamp": utc_now(), "media_id": media_id, "filename": filename, "status": "ALREADY_CURRENT", "sha256": expected})
            return
        with open(native_fs_path(Path(item["_resolved_path"])), "rb") as handle:
            data = handle.read()
        if len(data) != item["size"] or sha256_bytes(data) != expected:
            raise ContractError("HASH_MISMATCH", f"Media changed after package validation: {media_id}")
        stored = self._invoke_effect("storeMediaFile", {"filename": filename, "data": base64.b64encode(data).decode("ascii"), "deleteExisting": False})
        if stored != filename:
            raise AnkiCallError("MEDIA_FILENAME_CHANGED", f"Anki stored {filename!r} as {stored!r}")
        readback = self.client.invoke("retrieveMediaFile", {"filename": filename})
        try:
            readback_hash = sha256_bytes(base64.b64decode(readback, validate=True))
        except (ValueError, TypeError) as exc:
            raise AnkiCallError("MEDIA_READBACK_INVALID", f"Invalid media readback for {filename}") from exc
        if readback_hash != expected:
            raise AnkiCallError("READBACK_MISMATCH", f"Media readback hash mismatch for {filename}")
        self.writer.append("media.ndjson", {"timestamp": utc_now(), "media_id": media_id, "filename": filename, "status": "STORED", "sha256": expected})

    def _export_card(self, card: dict[str, Any]) -> tuple[str, int]:
        assert self.package is not None and self.client is not None
        mapping = self.package.config["note_types"][card["note_type_key"]]
        model_name = mapping["model_name"]
        deck = self.package.config["deck_routes"][card["deck_route_key"]]
        fields, _ = render_card(card, self.package)
        # RQC_ID is the global identity. Searching inside the currently mapped
        # model could miss an existing managed note after a note-type routing
        # change and tempt a duplicate add before the model conflict is seen.
        query = f'RQC_ID:"{search_escape(card["card_id"])}"'
        note_ids = self.client.invoke("findNotes", {"query": query})
        if (
            not isinstance(note_ids, list)
            or not all(isinstance(note_id, int) and not isinstance(note_id, bool) for note_id in note_ids)
        ):
            raise AnkiCallError("ANKI_RESPONSE_INVALID", "findNotes must return an array of integer note IDs", retryable=True)
        if len(note_ids) > 1:
            raise AnkiCallError("DUPLICATE_ID_CONFLICT", f"Multiple Anki notes found for {card['card_id']}")
        tags = sorted(set(card.get("tags", [])) | {"rqc_managed"})
        if not note_ids:
            note_id = self._invoke_effect("addNote", {"note": {"deckName": deck, "modelName": model_name, "fields": fields, "tags": tags, "options": {"allowDuplicate": False}}})
            if not isinstance(note_id, int):
                raise AnkiCallError("ADD_FAILED", f"Anki did not return a note ID for {card['card_id']}", retryable=True)
            status = "CREATED"
        else:
            note_id = note_ids[0]
            current = self._read_note(note_id)
            if current.get("modelName") != model_name:
                raise AnkiCallError("MODEL_SCHEMA_CONFLICT", f"Existing note uses {current.get('modelName')!r}")
            current_fields = current["fields"]
            try:
                old_revision = int(current_fields["RQC_Revision"]["value"])
                old_hash = current_fields["RQC_ContentHash"]["value"]
            except (KeyError, TypeError, ValueError) as exc:
                raise AnkiCallError("EXISTING_NOTE_INVALID", f"Existing managed note is missing revision/hash for {card['card_id']}") from exc
            if old_revision > card["revision"]:
                raise AnkiCallError("STALE_INPUT", f"Incoming revision {card['revision']} is older than Anki revision {old_revision}")
            if old_revision == card["revision"] and old_hash != card["content_hash"]:
                raise AnkiCallError("REVISION_CONFLICT", f"Same revision has different content for {card['card_id']}")
            if old_revision == card["revision"] and old_hash == card["content_hash"]:
                self._ensure_deck(current.get("cards", []), deck)
                self._verify_note(note_id, fields, deck)
                return "ALREADY_CURRENT", note_id
            self._invoke_effect("updateNoteFields", {"note": {"id": note_id, "fields": {name: value for name, value in fields.items() if name != "UserNotes"}}})
            self._invoke_effect("addTags", {"notes": [note_id], "tags": " ".join(tags)})
            self._ensure_deck(current.get("cards", []), deck)
            status = "UPDATED"
        self._verify_note(note_id, fields, deck)
        return status, note_id

    def _read_note(self, note_id: int) -> dict[str, Any]:
        assert self.client is not None
        values = self.client.invoke("notesInfo", {"notes": [note_id]})
        if not isinstance(values, list) or len(values) != 1 or not isinstance(values[0], dict):
            raise AnkiCallError("READBACK_MISMATCH", f"notesInfo did not return exactly one note for {note_id}", retryable=True)
        return values[0]

    def _ensure_deck(self, card_ids: list[int], deck: str) -> None:
        assert self.client is not None
        if card_ids:
            cards = self.client.invoke("cardsInfo", {"cards": card_ids})
            if not isinstance(cards, list) or not all(isinstance(item, dict) for item in cards):
                raise AnkiCallError("ANKI_RESPONSE_INVALID", "cardsInfo must return an array of card objects", retryable=True)
            if len(cards) != len(card_ids):
                raise AnkiCallError("READBACK_MISMATCH", "cardsInfo did not return all cards before deck routing", retryable=True)
            if any(item.get("deckName") != deck for item in cards):
                self._invoke_effect("changeDeck", {"cards": card_ids, "deck": deck})

    def _verify_note(self, note_id: int, expected_fields: dict[str, str], deck: str) -> None:
        assert self.client is not None
        current = self._read_note(note_id)
        for name in MANAGED_FIELDS:
            actual = current.get("fields", {}).get(name, {}).get("value")
            if actual != expected_fields[name]:
                raise AnkiCallError("READBACK_MISMATCH", f"Field {name} mismatch for note {note_id}", retryable=True)
        card_ids = current.get("cards", [])
        if (
            not isinstance(card_ids, list)
            or not all(isinstance(card_id, int) and not isinstance(card_id, bool) for card_id in card_ids)
        ):
            raise AnkiCallError("ANKI_RESPONSE_INVALID", f"notesInfo returned invalid card IDs for note {note_id}", retryable=True)
        cards = self.client.invoke("cardsInfo", {"cards": card_ids}) if card_ids else []
        if not isinstance(cards, list) or not all(isinstance(item, dict) for item in cards):
            raise AnkiCallError("ANKI_RESPONSE_INVALID", "cardsInfo must return an array of card objects", retryable=True)
        if any(item.get("deckName") != deck for item in cards):
            raise AnkiCallError("READBACK_MISMATCH", f"Deck mismatch for note {note_id}", retryable=True)


def worker_description() -> dict[str, Any]:
    return {
        "schemaVersion": COMMON_SCHEMA_VERSION,
        "workerId": WORKER_ID,
        "workerVersion": EXPORTER_VERSION,
        "contractVersion": COMMON_SCHEMA_VERSION,
        "stages": ["anki-export", "anki-probe", "anki-initialize-managed"],
        "inputKinds": ["card-bundle", "learning-bundle", "anki-export-config"],
        "outputKinds": ["anki-export-evidence", "anki-connection-status", "anki-managed-initialization-status"],
        "concurrency": {"model": "serial", "maxParallelAttempts": 1},
        "sideEffectLevel": "external",
        "idempotencyLevel": "reconcilable",
        "cancellation": {
            "protocol": "attempt-signal-file-v1",
            "supportedReasons": ["user_stop", "superseded", "task_cancel", "timeout"],
        },
    }


def resolve_standard_request(request_path: Path) -> tuple[Path, str, str]:
    if not request_path.is_absolute() or not path_is_file(request_path):
        raise ContractError("CLI_REQUEST_INVALID", "run --request must name an absolute existing request.json")
    resolved = request_path.resolve()
    request = load_json(resolved)
    attempt_path = require_string(request, "attemptPath", code="COMMON_CONTRACT_INVALID")
    suffix = Path(attempt_path) / "request.json"
    suffix_parts = suffix.parts
    if len(resolved.parts) <= len(suffix_parts) or tuple(part.lower() for part in resolved.parts[-len(suffix_parts):]) != tuple(part.lower() for part in suffix_parts):
        raise ContractError("COMMON_PATH_INVALID", "Absolute request path does not match attemptPath/request.json")
    root = resolved
    for _ in suffix_parts:
        root = root.parent
    return root, "task.manifest.v1.json", suffix.as_posix()


def publish_probe_bootstrap_contract_failure(
    job_root: Path,
    worker_request: str,
    exc: ExporterError,
    *,
    progress_sink: Callable[[str], None] | None = None,
) -> bool:
    """Publish a definitive local failure for a verified probe request.

    The strict task manifest remains mandatory.  This narrow bootstrap path only
    prevents a missing manifest from being misclassified by the orchestrator as
    an unknown provider write: probe is provider-read-only, the request is
    independently validated, and no AnkiClient has been constructed yet.
    """
    if exc.code != "FILE_MISSING" or path_exists(job_root / "task.manifest.v1.json"):
        return False
    request_path = safe_relative_path(job_root, worker_request)
    request = load_json(request_path)
    if request.get("operation") != "probe":
        return False
    validate_worker_request(
        request,
        {"taskId": request.get("taskId")},
        request_path.relative_to(job_root).as_posix(),
    )
    started = utc_now()
    writer = ReceiptWriter(job_root, request, progress_sink)
    writer.progress("started", workerId=WORKER_ID, requestId=request["requestId"], bootstrapFailure=True)
    error_record = {
        "timestamp": utc_now(),
        "card_id": None,
        "error_code": exc.code,
        "message": str(exc),
        "retryable": False,
    }
    writer.append("errors.ndjson", error_record)
    writer.write_once(
        "receipt.json",
        {
            "attempt_id": request["attemptId"],
            "task_id": request["taskId"],
            "request_id": request["requestId"],
            "exporter_version": EXPORTER_VERSION,
            "status": "CONTRACT_REJECTED",
            "started_at": started,
            "finished_at": utc_now(),
            "counts": {"succeeded": 0, "already_current": 0, "blocked_upstream": 0, "failed": 0},
            "operation": "probe",
            "operation_effect": {
                "effect_class": "provider-read-only",
                "network_scope": "loopback-only",
                "external_mutation": False,
                "local_artifact_write": True,
                "may_prompt_user": True,
            },
            "bootstrap_contract_failure": True,
            "inputs": {"worker_request_sha256": sha256_file(request_path)},
            "error": {"code": exc.code, "message": str(exc), "retryable": False},
        },
    )
    result = {
        "schemaVersion": COMMON_SCHEMA_VERSION,
        "requestId": request["requestId"],
        "operationId": request["operationId"],
        "taskId": request["taskId"],
        "runId": request["runId"],
        "stageId": request["stageId"],
        "attemptId": request["attemptId"],
        "worker": {"id": WORKER_ID, "version": EXPORTER_VERSION},
        "contractVersion": COMMON_SCHEMA_VERSION,
        "idempotencyKey": request["idempotencyKey"],
        "status": "failed",
        "startedAt": started,
        "finishedAt": utc_now(),
        "outputs": [],
        "itemDiagnostics": [],
        "warnings": [],
        "error": {
            "code": exc.code,
            "category": "contract",
            "message": str(exc)[:2000],
            "retryable": False,
            "blame": "input",
            "detailsRef": writer.relative(writer.output_root / "errors.ndjson"),
        },
        "sideEffects": [],
        "metrics": {"succeeded": 0, "already_current": 0, "blocked_upstream": 0, "failed": 0},
        "payload": {
            "moduleStatus": "CONTRACT_REJECTED",
            "validateOnly": False,
            "operation": "probe",
            "connectionStatus": None,
            "bootstrapFailure": True,
        },
    }
    writer.progress("finished", moduleStatus="CONTRACT_REJECTED", status="failed", bootstrapFailure=True)
    writer.write_result(result)
    return True


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    describe = commands.add_parser("describe")
    describe.add_argument("--json", action="store_true", required=True)
    run = commands.add_parser("run")
    run.add_argument("--request", required=True, type=Path)
    initialize = commands.add_parser(INITIALIZE_OPERATION)
    initialize.add_argument("--request", required=True, type=Path)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv or sys.argv[1:])
    if args.command == "describe":
        print(json.dumps(worker_description(), ensure_ascii=False, sort_keys=True, separators=(",", ":")))
        return 0
    try:
        job_root, task_manifest, worker_request = resolve_standard_request(args.request)
        exporter = Exporter(job_root, task_manifest, worker_request, progress_sink=lambda line: print(line, flush=True))
        if args.command == INITIALIZE_OPERATION and exporter.request["operation"] != INITIALIZE_OPERATION:
            raise ContractError("OPERATION_MISMATCH", f"{INITIALIZE_OPERATION} CLI requires request.operation={INITIALIZE_OPERATION}")
        status = exporter.run()
    except ExporterError as exc:
        if "job_root" in locals() and "worker_request" in locals():
            try:
                if publish_probe_bootstrap_contract_failure(
                    job_root,
                    worker_request,
                    exc,
                    progress_sink=lambda line: print(line, flush=True),
                ):
                    return 2
            except ExporterError:
                pass
        print(json.dumps({"code": exc.code, "message": str(exc)}, ensure_ascii=False), file=sys.stderr)
        return 2
    return {"SUCCEEDED": 0, "PARTIAL": 4, "CANCELLED": 3, "CONTRACT_REJECTED": 2, "BLOCKED_ENVIRONMENT": 3, "FAILED_TIMEOUT": 5, "FAILED": 5}.get(status, 5)


if __name__ == "__main__":
    raise SystemExit(main())
