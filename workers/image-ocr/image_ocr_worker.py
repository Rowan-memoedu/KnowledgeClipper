#!/usr/bin/env python3
"""Immutable, destination-neutral image/OCR worker for KnowledgeClipper.

The worker consumes a frozen ImageRequest v1 file from a task package.  It never
modifies inputs, creates a new attempt for every invocation, and publishes only
new files.  MinerU is loaded as a provider adapter so the existing direct-only
network implementation remains the single provider implementation.
"""

from __future__ import annotations

import argparse
import builtins
import datetime as dt
import hashlib
import importlib.util
import json
import os
import queue
import re
import shutil
import struct
import sys
import threading
import types
import uuid
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Protocol

from image_ocr_settings_adapter import SettingsValidationError, normalize_provider_settings


WORKER_VERSION = "1.2.4"
COMMON_SCHEMA_VERSION = "1.0.0"
COMMON_WORKER_ID = "image.ocr.mineru"
REQUEST_SCHEMA_VERSION = 1
RESULT_SCHEMA_VERSION = 1
ID_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
CAPTURE_REF_PATTERN = re.compile(r"^capture\.[a-z0-9][a-z0-9._-]*$")
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")
URL_PATTERN = re.compile(r"https?://[^\s\"']+", re.IGNORECASE)
MARKDOWN_IMAGE_PATTERN = re.compile(r"!\[([^\]]*)\]\(([^)]+)\)")
MAX_IMAGES_PER_OCR_REQUEST = 50
MAX_ZIP_FILES = 20_000
MAX_ZIP_UNCOMPRESSED_BYTES = 512 * 1024 * 1024


class WorkerFailure(Exception):
    def __init__(
        self,
        code: str,
        message: str,
        *,
        retryable: bool = False,
        status: str = "failed",
    ) -> None:
        super().__init__(message)
        self.code = code
        self.retryable = retryable
        self.status = status


def cancellation_failure(reason: str) -> WorkerFailure:
    if reason == "timeout":
        return WorkerFailure("cancel.timeout", "Attempt timed out", retryable=True, status="failed")
    return WorkerFailure(f"cancel.{reason}", f"Attempt cancelled: {reason}", status="cancelled")


class CancellationMonitor:
    """Poll an atomically published worker-cli-v1 cancel signal."""

    def __init__(
        self,
        signal_path: Path,
        attempt_id: str,
        operation_id: str,
        grace_period_ms: int = 2000,
    ) -> None:
        self.signal_path = signal_path
        self.attempt_id = attempt_id
        self.operation_id = operation_id
        self.grace_period_ms = grace_period_ms
        self._reason: str | None = None

    def check(self) -> None:
        if self._reason is not None:
            raise cancellation_failure(self._reason)
        if not os.path.isfile(native_fs_path(self.signal_path)):
            return
        signal = load_json(self.signal_path)
        required = {"schemaVersion", "attemptId", "operationId", "reason", "requestedAt", "graceDeadline"}
        allowed = required | {"commandId"}
        reason = signal.get("reason")
        try:
            requested_at = dt.datetime.fromisoformat(str(signal.get("requestedAt")).replace("Z", "+00:00"))
            grace_deadline = dt.datetime.fromisoformat(str(signal.get("graceDeadline")).replace("Z", "+00:00"))
        except ValueError as exc:
            raise WorkerFailure("cancel_signal_invalid", "Cancellation timestamps are invalid") from exc
        if (
            not required.issubset(signal)
            or set(signal) - allowed
            or signal.get("schemaVersion") != COMMON_SCHEMA_VERSION
            or signal.get("attemptId") != self.attempt_id
            or signal.get("operationId") != self.operation_id
            or reason not in {"user_stop", "superseded", "task_cancel", "timeout"}
            or grace_deadline - requested_at != dt.timedelta(milliseconds=self.grace_period_ms)
        ):
            raise WorkerFailure("cancel_signal_invalid", "Cancellation signal is malformed")
        self._reason = str(reason)
        raise cancellation_failure(self._reason)


def call_with_cancellation(action: Callable[[], Any], monitor: CancellationMonitor | None) -> Any:
    if monitor is None:
        return action()
    monitor.check()
    outcomes: queue.Queue[tuple[bool, Any]] = queue.Queue(maxsize=1)

    def invoke() -> None:
        try:
            outcomes.put((True, action()))
        except BaseException as exc:
            outcomes.put((False, exc))

    thread = threading.Thread(target=invoke, name="image-ocr-provider-call", daemon=True)
    thread.start()
    while thread.is_alive():
        monitor.check()
        thread.join(0.025)
    monitor.check()
    succeeded, value = outcomes.get_nowait()
    if succeeded:
        return value
    raise value


def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def native_fs_path(path: Path) -> str:
    """Return a Windows extended path for immutable packages with deep attempt IDs."""
    value = str(path.resolve())
    if os.name == "nt" and not value.startswith("\\\\?\\"):
        return "\\\\?\\" + value
    return value


def path_is_file(path: Path) -> bool:
    return os.path.isfile(native_fs_path(path))


def path_size(path: Path) -> int:
    return os.stat(native_fs_path(path)).st_size


def read_file_bytes(path: Path) -> bytes:
    with open(native_fs_path(path), "rb") as handle:
        return handle.read()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with open(native_fs_path(path), "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def clean_message(message: str, secrets: tuple[str, ...] = ()) -> str:
    value = URL_PATTERN.sub("[redacted-url]", (message or "").replace("\r", " ").replace("\n", " "))
    for secret in secrets:
        if secret:
            value = value.replace(secret, "[redacted-secret]")
    value = re.sub(r"\s+", " ", value).strip()
    return value[:500]


def require_id(value: Any, field: str) -> str:
    if not isinstance(value, str) or not ID_PATTERN.fullmatch(value):
        raise WorkerFailure("schema_invalid", f"{field} is not a valid identifier")
    return value


def require_string(value: Any, field: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise WorkerFailure("schema_invalid", f"{field} must be a non-empty string")
    return value


def require_capture_ref(value: Any, field: str) -> str:
    if not isinstance(value, str) or not CAPTURE_REF_PATTERN.fullmatch(value):
        raise WorkerFailure("schema_invalid", f"{field} must be a capture artifactRef")
    if len(value) > 128:
        raise WorkerFailure(
            "capture_ref_common_incompatible",
            f"{field} exceeds the common artifact identifier limit of 128 characters",
        )
    return value


def require_index(value: Any, field: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise WorkerFailure("schema_invalid", f"{field} must be an integer >= 0")
    return value


def load_json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(read_file_bytes(path).decode("utf-8"))
    except FileNotFoundError as exc:
        raise WorkerFailure("file_missing", f"Missing required file: {path}") from exc
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise WorkerFailure("invalid_json", f"Invalid UTF-8 JSON file: {path.name}") from exc
    if not isinstance(value, dict):
        raise WorkerFailure("invalid_json_root", f"JSON root must be an object: {path.name}")
    return value


def safe_relative_path(root: Path, relative: str) -> Path:
    if not isinstance(relative, str) or not relative.strip():
        raise WorkerFailure("invalid_path", "Artifact path must be a non-empty string")
    candidate = Path(relative)
    if candidate.is_absolute():
        raise WorkerFailure("path_escape", f"Absolute paths are not allowed: {relative}")
    resolved_root = root.resolve()
    resolved = (resolved_root / candidate).resolve()
    if resolved != resolved_root and resolved_root not in resolved.parents:
        raise WorkerFailure("path_escape", f"Path leaves task package: {relative}")
    return resolved


def package_relative(root: Path, path: Path) -> str:
    try:
        return path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError as exc:
        raise WorkerFailure("path_escape", f"Path is outside task package: {path}") from exc


def png_dimensions(path: Path) -> tuple[int, int]:
    try:
        with open(native_fs_path(path), "rb") as handle:
            header = handle.read(24)
    except OSError as exc:
        raise WorkerFailure("decode_failed", f"Cannot read image: {path.name}") from exc
    if len(header) != 24 or header[:8] != b"\x89PNG\r\n\x1a\n" or header[12:16] != b"IHDR":
        raise WorkerFailure("decode_failed", f"Invalid PNG image: {path.name}")
    width, height = struct.unpack(">II", header[16:24])
    if width < 1 or height < 1:
        raise WorkerFailure("decode_failed", f"Invalid PNG dimensions: {path.name}")
    return width, height


@dataclass(frozen=True)
class ValidatedAsset:
    artifact_ref: str
    group_id: str
    role: str
    group_index: int
    image_index: int
    relative_path: str
    path: Path
    sha256: str
    size_bytes: int
    media_type: str
    width: int
    height: int

    def source_descriptor(self) -> dict[str, Any]:
        return {
            "artifactRef": self.artifact_ref,
            "path": self.relative_path,
            "sha256": self.sha256,
            "bytes": self.size_bytes,
            "mediaType": self.media_type,
            "width": self.width,
            "height": self.height,
        }


@dataclass(frozen=True)
class ValidatedRequest:
    raw: dict[str, Any]
    request_id: str
    operation_id: str
    card_id: str
    capture_bundle_id: str
    mode: str
    retention_days: int
    cloud_upload_authorized: bool
    assets: tuple[ValidatedAsset, ...]
    provider_settings: dict[str, Any]


class AttemptStore:
    def __init__(
        self,
        package_root: Path,
        attempt_id: str | None = None,
        attempt_relative_path: str | None = None,
        allow_existing_root: bool = False,
    ) -> None:
        self.package_root = package_root.resolve()
        if attempt_id is None:
            attempt_id = dt.datetime.now().strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex
        self.attempt_id = require_id(attempt_id, "attemptId")
        if attempt_relative_path is None:
            self.root = self.package_root / "stages" / "image-ocr" / self.attempt_id
        else:
            self.root = safe_relative_path(self.package_root, attempt_relative_path)
            if self.root.name != self.attempt_id:
                raise WorkerFailure("attempt_path_mismatch", "attemptPath basename must equal attemptId")
        try:
            os.makedirs(native_fs_path(self.root), exist_ok=allow_existing_root)
        except FileExistsError as exc:
            raise WorkerFailure("immutable_attempt_exists", f"Attempt already exists: {self.attempt_id}") from exc

    def write_json_once(self, relative: str, value: Any) -> Path:
        path = safe_relative_path(self.root, relative)
        os.makedirs(native_fs_path(path.parent), exist_ok=True)
        try:
            with open(native_fs_path(path), "x", encoding="utf-8", newline="\n") as handle:
                json.dump(value, handle, ensure_ascii=False, sort_keys=True, indent=2)
                handle.write("\n")
        except FileExistsError as exc:
            raise WorkerFailure("immutable_file_exists", f"Refusing to overwrite: {relative}") from exc
        return path

    def write_bytes_once(self, relative: str, value: bytes) -> Path:
        path = safe_relative_path(self.root, relative)
        os.makedirs(native_fs_path(path.parent), exist_ok=True)
        try:
            with open(native_fs_path(path), "xb") as handle:
                handle.write(value)
        except FileExistsError as exc:
            raise WorkerFailure("immutable_file_exists", f"Refusing to overwrite: {relative}") from exc
        return path

    def publish_bytes(
        self,
        artifact_ref: str,
        payload_name: str,
        value: bytes,
        media_type: str,
        encoding: str | None = None,
    ) -> dict[str, Any]:
        require_id(artifact_ref, "artifactRef")
        if Path(payload_name).name != payload_name or payload_name in {".", ".."}:
            raise WorkerFailure("invalid_output_name", f"Unsafe output name: {payload_name}")
        relative = f"outputs/{artifact_ref}/v0001/{payload_name}"
        path = self.write_bytes_once(relative, value)
        descriptor: dict[str, Any] = {
            "artifactRef": artifact_ref,
            "path": package_relative(self.package_root, path),
            "sha256": sha256_bytes(value),
            "bytes": len(value),
            "mediaType": media_type,
            "origin": "produced",
        }
        if encoding:
            descriptor["encoding"] = encoding
        self.write_json_once(f"outputs/{artifact_ref}/v0001/module-artifact.json", descriptor)
        return descriptor

    def package_path(self, relative: str) -> Path:
        return safe_relative_path(self.package_root, relative)


def validate_request(package_root: Path, request: dict[str, Any]) -> ValidatedRequest:
    if request.get("schemaVersion") != REQUEST_SCHEMA_VERSION:
        raise WorkerFailure("unsupported_schema_version", "ImageRequest schemaVersion must be 1")
    request_id = require_id(request.get("requestId"), "requestId")
    operation_id = require_id(request.get("operationId"), "operationId")
    mode = request.get("mode")
    if mode not in {"original_passthrough", "ocr_mineru"}:
        raise WorkerFailure("schema_invalid", "mode must be original_passthrough or ocr_mineru")
    retention_days = request.get("retentionDays")
    if retention_days != 30:
        raise WorkerFailure("retention_policy_mismatch", "retentionDays must be the confirmed value 30")
    cloud_upload_authorized = request.get("cloudUploadAuthorized")
    if not isinstance(cloud_upload_authorized, bool):
        raise WorkerFailure("schema_invalid", "cloudUploadAuthorized must be a boolean")
    if mode == "ocr_mineru" and not cloud_upload_authorized:
        raise WorkerFailure("cloud_upload_not_authorized", "MinerU OCR requires explicit cloud upload authorization")
    if mode == "original_passthrough" and cloud_upload_authorized:
        raise WorkerFailure("schema_invalid", "original_passthrough must not authorize cloud upload")

    capture_ref = request.get("captureBundle")
    if not isinstance(capture_ref, dict):
        raise WorkerFailure("schema_invalid", "captureBundle must be an artifact reference")
    require_id(capture_ref.get("artifactRef"), "captureBundle.artifactRef")
    capture_path = safe_relative_path(package_root, require_string(capture_ref.get("path"), "captureBundle.path"))
    if not path_is_file(capture_path):
        raise WorkerFailure("file_missing", "Capture bundle artifact is missing")
    capture_hash = capture_ref.get("sha256")
    if not isinstance(capture_hash, str) or not SHA256_PATTERN.fullmatch(capture_hash):
        raise WorkerFailure("schema_invalid", "captureBundle.sha256 is invalid")
    capture_bytes = capture_ref.get("bytes")
    if isinstance(capture_bytes, bool) or not isinstance(capture_bytes, int) or capture_bytes < 1:
        raise WorkerFailure("schema_invalid", "captureBundle.bytes is invalid")
    if path_size(capture_path) != capture_bytes or sha256_file(capture_path) != capture_hash:
        raise WorkerFailure("integrity_mismatch", "Capture bundle integrity check failed")
    if capture_ref.get("mediaType") != "application/vnd.rqc.capture-bundle+json":
        raise WorkerFailure("schema_invalid", "captureBundle.mediaType is unsupported")
    capture = load_json(capture_path)
    if capture.get("schemaVersion") != "capture-bundle.v1":
        raise WorkerFailure("capture_bundle_unsupported", "Capture bundle must use schemaVersion capture-bundle.v1")
    capture_bundle_id = require_string(capture.get("captureId"), "captureBundle.captureId")
    if len(capture_bundle_id) > 128:
        raise WorkerFailure("schema_invalid", "captureBundle.captureId exceeds 128 characters")
    card_id = capture_bundle_id
    raw_images = capture.get("artifacts")
    question_refs = capture.get("questionImages")
    answer_refs = capture.get("answerImages")
    if not isinstance(raw_images, list):
        raise WorkerFailure("schema_invalid", "captureBundle.artifacts must be an array")
    if not isinstance(question_refs, list) or not question_refs:
        raise WorkerFailure("schema_invalid", "captureBundle.questionImages must be a non-empty array")
    if not isinstance(answer_refs, list):
        raise WorkerFailure("schema_invalid", "captureBundle.answerImages must be an array")
    for index, artifact_ref in enumerate(question_refs):
        require_capture_ref(artifact_ref, f"captureBundle.questionImages[{index}]")
    for index, artifact_ref in enumerate(answer_refs):
        require_capture_ref(artifact_ref, f"captureBundle.answerImages[{index}]")
    ordered_refs = question_refs + answer_refs
    if len(set(ordered_refs)) != len(ordered_refs):
        raise WorkerFailure("schema_invalid", "Each image artifactRef must appear in exactly one role array")

    images_by_ref: dict[str, dict[str, Any]] = {}
    for raw_asset in raw_images:
        if not isinstance(raw_asset, dict):
            raise WorkerFailure("schema_invalid", "Each capture image must be an object")
        artifact_ref = require_capture_ref(raw_asset.get("artifactRef"), "image.artifactRef")
        if artifact_ref in images_by_ref:
            raise WorkerFailure("schema_invalid", f"Duplicate artifactRef: {artifact_ref}")
        images_by_ref[artifact_ref] = raw_asset
    if not set(ordered_refs).issubset(images_by_ref):
        missing = sorted(set(ordered_refs) - set(images_by_ref))
        raise WorkerFailure("schema_invalid", f"Capture role arrays contain unresolved artifacts: {missing}")

    mode_snapshot = capture.get("modeSnapshot")
    if not isinstance(mode_snapshot, dict):
        raise WorkerFailure("schema_invalid", "captureBundle.modeSnapshot must be an object")
    expected_handling = "ocr" if mode == "ocr_mineru" else "original"
    if mode_snapshot.get("imageHandling") != expected_handling:
        raise WorkerFailure("mode_mismatch", "ImageRequest mode does not match captureBundle.modeSnapshot.imageHandling")

    assets: list[ValidatedAsset] = []
    for group_index, (role, refs) in enumerate((("question", question_refs), ("answer", answer_refs))):
        for image_index, artifact_ref in enumerate(refs):
            raw_asset = images_by_ref[artifact_ref]
            relative_path = require_string(raw_asset.get("relativePath"), "image.relativePath")
            path = safe_relative_path(package_root, relative_path)
            if not path_is_file(path):
                raise WorkerFailure("file_missing", f"Image file is missing: {relative_path}")
            expected_hash = raw_asset.get("sha256")
            if not isinstance(expected_hash, str) or not SHA256_PATTERN.fullmatch(expected_hash):
                raise WorkerFailure("schema_invalid", f"Invalid sha256 for {artifact_ref}")
            expected_size = raw_asset.get("bytes")
            if isinstance(expected_size, bool) or not isinstance(expected_size, int) or expected_size < 1:
                raise WorkerFailure("schema_invalid", f"Invalid bytes for {artifact_ref}")
            actual_size = path_size(path)
            if actual_size != expected_size:
                raise WorkerFailure("integrity_mismatch", f"Byte length mismatch for {artifact_ref}")
            actual_hash = sha256_file(path)
            if actual_hash != expected_hash:
                raise WorkerFailure("integrity_mismatch", f"SHA-256 mismatch for {artifact_ref}")
            media_type = raw_asset.get("mediaType")
            if media_type != "image/png":
                raise WorkerFailure("unsupported_media", f"ImageRequest v1 only accepts image/png: {artifact_ref}")
            width, height = png_dimensions(path)
            assets.append(
                ValidatedAsset(
                    artifact_ref=artifact_ref,
                    group_id=role,
                    role=role,
                    group_index=group_index,
                    image_index=image_index,
                    relative_path=relative_path.replace("\\", "/"),
                    path=path,
                    sha256=actual_hash,
                    size_bytes=actual_size,
                    media_type=media_type,
                    width=width,
                    height=height,
                )
            )
    assets.sort(key=lambda item: (item.group_index, item.image_index))
    if mode == "ocr_mineru" and len(assets) > MAX_IMAGES_PER_OCR_REQUEST:
        raise WorkerFailure("provider_limit_exceeded", "MinerU accepts at most 50 images per batch")

    try:
        provider_settings = normalize_provider_settings(request.get("providerSettings", {}))
    except SettingsValidationError as exc:
        raise WorkerFailure("schema_invalid", str(exc)) from exc
    return ValidatedRequest(
        raw=request,
        request_id=request_id,
        operation_id=operation_id,
        card_id=card_id,
        capture_bundle_id=capture_bundle_id,
        mode=mode,
        retention_days=retention_days,
        cloud_upload_authorized=cloud_upload_authorized,
        assets=tuple(assets),
        provider_settings=provider_settings,
    )


@dataclass
class ProviderItemResult:
    status: str
    markdown: str | None = None
    markdown_source_path: Path | None = None
    provider_item_id: str | None = None
    raw_root: str | None = None
    error_code: str | None = None
    message: str | None = None
    retryable: bool = False


class Provider(Protocol):
    def process(
        self,
        assets: list[ValidatedAsset],
        request: ValidatedRequest,
        store: AttemptStore,
        resume_submission: dict[str, Any] | None = None,
        cancellation: CancellationMonitor | None = None,
    ) -> dict[str, ProviderItemResult]: ...


def _load_module(path: Path) -> types.ModuleType:
    if not path_is_file(path):
        raise WorkerFailure("provider_unavailable", f"MinerU script not found: {path}")
    spec = importlib.util.spec_from_file_location("knowledge_clipper_mineru_provider", path)
    if spec is None or spec.loader is None:
        raise WorkerFailure("provider_unavailable", f"Cannot load MinerU script: {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.__dict__["print"] = lambda *args, **kwargs: builtins.print(
        *args,
        **{**kwargs, "file": sys.stderr},
    )
    return module


def _safe_extract_zip(
    zip_path: Path,
    destination: Path,
    cancellation: CancellationMonitor | None = None,
) -> tuple[Path, ...]:
    os.makedirs(native_fs_path(destination), exist_ok=False)
    destination_resolved = destination.resolve()
    extracted_files: list[Path] = []
    with zipfile.ZipFile(native_fs_path(zip_path)) as archive:
        infos = archive.infolist()
        if len(infos) > MAX_ZIP_FILES:
            raise WorkerFailure("provider_artifact_invalid", "MinerU ZIP contains too many files")
        total = sum(info.file_size for info in infos)
        if total > MAX_ZIP_UNCOMPRESSED_BYTES:
            raise WorkerFailure("provider_artifact_invalid", "MinerU ZIP is too large after extraction")
        for info in infos:
            if cancellation:
                cancellation.check()
            if (info.external_attr >> 16) & 0o170000 == 0o120000:
                raise WorkerFailure("provider_artifact_invalid", "MinerU ZIP contains a symbolic link")
            target = (destination / info.filename).resolve()
            if target != destination_resolved and destination_resolved not in target.parents:
                raise WorkerFailure("provider_artifact_invalid", "MinerU ZIP contains an unsafe path")
            if info.is_dir():
                os.makedirs(native_fs_path(target), exist_ok=True)
                continue
            os.makedirs(native_fs_path(target.parent), exist_ok=True)
            with archive.open(info) as source, open(native_fs_path(target), "xb") as output:
                shutil.copyfileobj(source, output)
            extracted_files.append(target)
    return tuple(extracted_files)


class MineruProvider:
    def __init__(self, script_path: Path) -> None:
        self.script_path = script_path.resolve()

    def process(
        self,
        assets: list[ValidatedAsset],
        request: ValidatedRequest,
        store: AttemptStore,
        resume_submission: dict[str, Any] | None = None,
        cancellation: CancellationMonitor | None = None,
    ) -> dict[str, ProviderItemResult]:
        if cancellation:
            cancellation.check()
        token = os.environ.get("MINERU_API_TOKEN", "").strip()
        if not token:
            raise WorkerFailure("credential_unavailable", "MINERU_API_TOKEN is not available")
        module = _load_module(self.script_path)
        settings = request.provider_settings
        args = types.SimpleNamespace(
            inputs=[str(asset.path) for asset in assets],
            model_version=settings.get("modelVersion", "vlm"),
            language=settings.get("language", "ch"),
            enable_table=settings.get("enableTable", True),
            enable_formula=settings.get("enableFormula", True),
            extra_format=[],
            page_ranges=None,
            is_ocr=True,
            no_cache=False,
            timeout=settings.get("timeoutSeconds", 900),
            poll_interval=settings.get("pollIntervalSeconds", 5),
        )

        if resume_submission is None:
            try:
                batch_id, specs = call_with_cancellation(
                    lambda: module.submit_local([asset.path for asset in assets], args, token),
                    cancellation,
                )
            except Exception as exc:
                if isinstance(exc, WorkerFailure):
                    raise
                raise WorkerFailure(
                    "provider_submission_unknown",
                    clean_message(str(exc), (token,)),
                    status="unknown_external_state",
                ) from exc
            if cancellation:
                cancellation.check()
            mappings = [
                {"artifactRef": asset.artifact_ref, "dataId": spec.get("data_id")}
                for asset, spec in zip(assets, specs, strict=True)
            ]
            submission = {
                "schemaVersion": 1,
                "provider": "mineru",
                "batchId": batch_id,
                "submittedAt": utc_now(),
                "requestId": request.request_id,
                "operationId": request.operation_id,
                "items": mappings,
            }
            store.write_json_once("submission.json", submission)
        else:
            if cancellation:
                cancellation.check()
            if resume_submission.get("provider") != "mineru":
                raise WorkerFailure("resume_invalid", "Resume receipt is not a MinerU submission")
            if resume_submission.get("operationId") != request.operation_id:
                raise WorkerFailure("resume_invalid", "Resume receipt belongs to another operation")
            batch_id = require_string(resume_submission.get("batchId"), "batchId")
            mappings = resume_submission.get("items")
            if not isinstance(mappings, list):
                raise WorkerFailure("resume_invalid", "Resume receipt items are invalid")
            expected = [asset.artifact_ref for asset in assets]
            actual = [entry.get("artifactRef") for entry in mappings if isinstance(entry, dict)]
            if actual != expected:
                raise WorkerFailure("resume_invalid", "Resume receipt assets do not match retry targets")
            store.write_json_once(
                "resume.json",
                {
                    "schemaVersion": 1,
                    "provider": "mineru",
                    "batchId": batch_id,
                    "resumedAt": utc_now(),
                    "sourceSubmissionSha256": sha256_bytes(
                        json.dumps(resume_submission, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
                    ),
                },
            )
            if cancellation:
                cancellation.check()

        try:
            raw_results = call_with_cancellation(
                lambda: module.poll_batch(batch_id, args, token),
                cancellation,
            )
        except Exception as exc:
            if isinstance(exc, WorkerFailure):
                raise
            raise WorkerFailure("network_transient", clean_message(str(exc), (token,)), retryable=True) from exc

        if cancellation:
            cancellation.check()

        redacted_results = []
        by_data_id: dict[str, dict[str, Any]] = {}
        for item in raw_results:
            data_id = str(item.get("data_id") or "")
            if data_id:
                by_data_id[data_id] = item
            redacted_results.append(
                {
                    "dataId": data_id,
                    "state": item.get("state"),
                    "error": clean_message(str(item.get("err_msg") or ""), (token,)),
                }
            )
        store.write_json_once("provider-result.redacted.json", {"provider": "mineru", "batchId": batch_id, "items": redacted_results})

        results: dict[str, ProviderItemResult] = {}
        for asset, mapping in zip(assets, mappings, strict=True):
            if cancellation:
                cancellation.check()
            data_id = mapping.get("dataId") if isinstance(mapping, dict) else None
            item = by_data_id.get(str(data_id))
            if item is None:
                results[asset.artifact_ref] = ProviderItemResult(
                    status="failed",
                    provider_item_id=str(data_id or ""),
                    error_code="provider_result_missing",
                    message="MinerU returned no item for this image",
                    retryable=True,
                )
                continue
            if item.get("state") == "failed":
                results[asset.artifact_ref] = ProviderItemResult(
                    status="failed",
                    provider_item_id=str(data_id),
                    error_code="provider_failed",
                    message=clean_message(str(item.get("err_msg") or "MinerU parsing failed"), (token,)),
                )
                continue
            zip_url = item.get("full_zip_url")
            if not isinstance(zip_url, str) or not zip_url:
                results[asset.artifact_ref] = ProviderItemResult(
                    status="failed",
                    provider_item_id=str(data_id),
                    error_code="artifact_missing",
                    message="MinerU result has no downloadable ZIP",
                    retryable=True,
                )
                continue
            safe_data_id = re.sub(r"[^A-Za-z0-9._-]", "_", str(data_id))[:128] or "result"
            raw_root = store.root / "raw" / "mineru" / safe_data_id
            os.makedirs(native_fs_path(raw_root), exist_ok=False)
            zip_path = raw_root / "mineru-result.zip"
            try:
                call_with_cancellation(lambda: module.download_file(zip_url, native_fs_path(zip_path)), cancellation)
                if cancellation:
                    cancellation.check()
                if not zipfile.is_zipfile(native_fs_path(zip_path)):
                    raise WorkerFailure("provider_artifact_invalid", "MinerU download is not a valid ZIP")
                extracted = raw_root / "extracted"
                extracted_files = _safe_extract_zip(zip_path, extracted, cancellation)
                if cancellation:
                    cancellation.check()
                markdown_path = next((path for path in extracted_files if path.name == "full.md"), None)
                if markdown_path is None:
                    markdown_path = next((path for path in extracted_files if path.suffix.lower() == ".md"), None)
                if markdown_path is None:
                    raise WorkerFailure("artifact_missing", "MinerU ZIP contains no Markdown")
                markdown = read_file_bytes(markdown_path).decode("utf-8")
                if not markdown.strip():
                    raise WorkerFailure("artifact_missing", "MinerU Markdown is empty")
                results[asset.artifact_ref] = ProviderItemResult(
                    status="completed",
                    markdown=markdown,
                    markdown_source_path=markdown_path,
                    provider_item_id=str(data_id),
                    raw_root=package_relative(store.package_root, raw_root),
                )
                if cancellation:
                    cancellation.check()
            except WorkerFailure as exc:
                if exc.code.startswith("cancel."):
                    raise
                results[asset.artifact_ref] = ProviderItemResult(
                    status="failed",
                    provider_item_id=str(data_id),
                    raw_root=package_relative(store.package_root, raw_root),
                    error_code=exc.code,
                    message=clean_message(str(exc), (token,)),
                    retryable=exc.retryable,
                )
            except Exception as exc:
                results[asset.artifact_ref] = ProviderItemResult(
                    status="failed",
                    provider_item_id=str(data_id),
                    raw_root=package_relative(store.package_root, raw_root),
                    error_code="artifact_download_failed",
                    message=clean_message(str(exc), (token,)),
                    retryable=True,
                )
        return results


def _load_prior_result(package_root: Path, relative: str, request: ValidatedRequest) -> dict[str, Any]:
    prior = load_json(safe_relative_path(package_root, relative))
    if prior.get("operationId") != request.operation_id:
        raise WorkerFailure("retry_invalid", "Retry result belongs to another operation")
    if prior.get("worker") != COMMON_WORKER_ID:
        raise WorkerFailure("retry_invalid", f"Retry result was not produced by {COMMON_WORKER_ID}")
    details = prior.get("details") if isinstance(prior.get("details"), dict) else {}
    if details.get("captureBundleId") != request.capture_bundle_id or prior.get("mode") != request.mode:
        raise WorkerFailure("retry_invalid", "Retry result belongs to another capture bundle or mode")
    return prior


def _load_resume_submission(package_root: Path, relative: str, request: ValidatedRequest) -> dict[str, Any]:
    submission = load_json(safe_relative_path(package_root, relative))
    if submission.get("operationId") != request.operation_id:
        raise WorkerFailure("resume_invalid", "Resume receipt belongs to another operation")
    return submission


def _input_output_descriptor(asset: ValidatedAsset) -> dict[str, Any]:
    value = asset.source_descriptor()
    value["origin"] = "input"
    return value


def _publish_original_media_bundle(
    store: AttemptStore,
    request: ValidatedRequest,
    cancellation: CancellationMonitor | None = None,
) -> dict[str, Any]:
    """Publish one destination-neutral, explicitly ordered original-image bundle."""
    if cancellation:
        cancellation.check()
    grouped = {"questionImages": [], "answerImages": []}
    for asset in request.assets:
        entry = {
            "artifactRef": asset.artifact_ref,
            "role": asset.role,
            "groupIndex": asset.group_index,
            "imageIndex": asset.image_index,
            "sha256": asset.sha256,
            "bytes": asset.size_bytes,
            "mediaType": asset.media_type,
            "width": asset.width,
            "height": asset.height,
        }
        grouped["questionImages" if asset.role == "question" else "answerImages"].append(entry)
    bundle = {
        "schemaVersion": "image-original-media-bundle.v1",
        "captureId": request.capture_bundle_id,
        "mode": "original_passthrough",
        "providerCalls": 0,
        "questionImages": grouped["questionImages"],
        "answerImages": grouped["answerImages"],
    }
    descriptor = store.publish_bytes(
        "image.original.bundle",
        "media-bundle.json",
        (json.dumps(bundle, ensure_ascii=False, sort_keys=True, indent=2) + "\n").encode("utf-8"),
        "application/vnd.knowledge-clipper.image-original-media-bundle+json",
        "utf-8",
    )
    descriptor["artifactKind"] = "image.original.bundle"
    descriptor["payloadSchemaVersion"] = bundle["schemaVersion"]
    return descriptor


def _publish_processing_provenance(
    store: AttemptStore,
    request: ValidatedRequest,
    items: list[dict[str, Any]],
    outputs: list[dict[str, Any]],
    status: str,
    provider_calls: int,
    cancellation: CancellationMonitor | None = None,
) -> dict[str, Any]:
    """Publish source-to-representation provenance for review and safe continuation."""
    if cancellation:
        cancellation.check()
    ordered_items = []
    outputs_by_ref = {
        output.get("artifactRef"): output
        for output in outputs
        if isinstance(output, dict) and isinstance(output.get("artifactRef"), str)
    }
    for item in items:
        source = item.get("source") if isinstance(item.get("source"), dict) else {}
        ocr = item.get("ocr") if isinstance(item.get("ocr"), dict) else None
        representations: dict[str, Any] = {
            "original": {
                "artifactRef": item.get("artifactRef"),
                "sha256": source.get("sha256"),
                "bytes": source.get("bytes"),
                "mediaType": source.get("mediaType"),
            }
        }
        if ocr and item.get("status") == "completed":
            markdown_output = outputs_by_ref.get(ocr.get("markdownArtifactRef"), {})
            structured_output = outputs_by_ref.get(ocr.get("structuredArtifactRef"), {})
            representations["markdown"] = {
                "artifactRef": ocr.get("markdownArtifactRef"),
                "sha256": markdown_output.get("sha256"),
                "bytes": markdown_output.get("bytes"),
                "mediaType": markdown_output.get("mediaType"),
            }
            representations["structured"] = {
                "artifactRef": ocr.get("structuredArtifactRef"),
                "sha256": structured_output.get("sha256"),
                "bytes": structured_output.get("bytes"),
                "mediaType": structured_output.get("mediaType"),
            }
            if ocr.get("imageArtifactRefs"):
                representations["extractedImages"] = [
                    {"artifactRef": artifact_ref} for artifact_ref in ocr["imageArtifactRefs"]
                ]
        ordered_items.append(
            {
                "artifactRef": item.get("artifactRef"),
                "role": item.get("role"),
                "groupIndex": item.get("groupIndex"),
                "imageIndex": item.get("imageIndex"),
                "status": item.get("status"),
                "reusedFromAttempt": item.get("reusedFromAttempt"),
                "representations": representations,
                "error": item.get("error"),
            }
        )
    failed_refs = [item["artifactRef"] for item in ordered_items if item["status"] == "failed"]
    completed_refs = [item["artifactRef"] for item in ordered_items if item["status"] == "completed"]
    provenance = {
        "schemaVersion": "image-processing-provenance.v1",
        "captureId": request.capture_bundle_id,
        "operationId": request.operation_id,
        "attemptId": store.attempt_id,
        "mode": request.mode,
        "terminalStatus": status,
        "provider": {
            "id": "mineru" if request.mode == "ocr_mineru" else None,
            "calls": provider_calls,
            "cloudUploadAuthorized": request.cloud_upload_authorized,
        },
        "sourceOrder": {
            "questionImages": [asset.artifact_ref for asset in request.assets if asset.role == "question"],
            "answerImages": [asset.artifact_ref for asset in request.assets if asset.role == "answer"],
        },
        "items": ordered_items,
        "continuation": {
            "humanJudgmentAvailable": bool(completed_refs),
            "automaticDownstreamAllowed": status == "completed" and request.mode == "ocr_mineru",
            "partialBlocksDownstream": True,
            "defaultOcrRerun": False,
            "humanActions": ["accept", "correct"],
            "requiresOriginalArtifactHashMatch": True,
            "requiresSuccessfulOutputHashMatch": True,
            "retryScope": "failed-items-only" if failed_refs else "none",
            "failedArtifactRefs": failed_refs,
            "reusableCompletedArtifactRefs": completed_refs,
        },
    }
    descriptor = store.publish_bytes(
        "image.processing.provenance",
        "provenance.json",
        (json.dumps(provenance, ensure_ascii=False, sort_keys=True, indent=2) + "\n").encode("utf-8"),
        "application/vnd.knowledge-clipper.image-processing-provenance+json",
        "utf-8",
    )
    descriptor["artifactKind"] = "image.processing.provenance"
    descriptor["payloadSchemaVersion"] = provenance["schemaVersion"]
    return descriptor


def _publish_ocr_item(
    store: AttemptStore,
    asset: ValidatedAsset,
    provider_result: ProviderItemResult,
    cancellation: CancellationMonitor | None = None,
) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    if cancellation:
        cancellation.check()
    if provider_result.markdown is None:
        raise WorkerFailure("artifact_missing", f"Provider returned no Markdown for {asset.artifact_ref}")
    produced_outputs: list[dict[str, Any]] = []
    image_refs: list[dict[str, Any]] = []
    normalized_markdown = provider_result.markdown
    if provider_result.markdown_source_path is not None and provider_result.raw_root:
        raw_root = store.package_path(provider_result.raw_root).resolve()
        markdown_parent = provider_result.markdown_source_path.resolve().parent
        published_by_path: dict[Path, tuple[str, dict[str, Any]]] = {}

        resolved_matches: list[tuple[re.Match[str], Path, str]] = []
        for match in MARKDOWN_IMAGE_PATTERN.finditer(normalized_markdown):
            raw_target = match.group(2).strip().strip("<>")
            if URL_PATTERN.fullmatch(raw_target):
                raise WorkerFailure("provider_artifact_invalid", "MinerU Markdown contains an external image URL")
            source = (markdown_parent / raw_target).resolve()
            if source != raw_root and raw_root not in source.parents:
                raise WorkerFailure("provider_artifact_invalid", "MinerU Markdown image path leaves raw artifact root")
            if not path_is_file(source):
                raise WorkerFailure("artifact_missing", f"MinerU Markdown image is missing: {Path(raw_target).name}")
            suffix = source.suffix.lower()
            if suffix not in {".png", ".jpg", ".jpeg", ".gif", ".webp"}:
                raise WorkerFailure("unsupported_media", f"Unsupported MinerU image artifact: {suffix or '(none)'}")
            resolved_matches.append((match, source, suffix))

        for _, source, suffix in resolved_matches:
            if cancellation:
                cancellation.check()
            if source in published_by_path:
                continue
            media_types = {
                ".png": "image/png",
                ".jpg": "image/jpeg",
                ".jpeg": "image/jpeg",
                ".gif": "image/gif",
                ".webp": "image/webp",
            }
            image_ref = f"ocr.{asset.artifact_ref}.image.{len(published_by_path) + 1:04d}"
            descriptor = store.publish_bytes(image_ref, "image" + suffix, read_file_bytes(source), media_types[suffix])
            published_by_path[source] = (image_ref, descriptor)
            produced_outputs.append(descriptor)

        def replace_image(match: re.Match[str]) -> str:
            alt = match.group(1)
            raw_target = match.group(2).strip().strip("<>")
            source = (markdown_parent / raw_target).resolve()
            image_ref, _ = published_by_path[source]
            image_refs.append(
                {
                    "imageArtifactRef": image_ref,
                    "sourceImageRef": asset.artifact_ref,
                    "order": len(image_refs),
                }
            )
            return f"![{alt}]({{{{image-ref:{image_ref}}}}})"

        normalized_markdown = MARKDOWN_IMAGE_PATTERN.sub(replace_image, normalized_markdown)

    markdown_ref = f"ocr.{asset.artifact_ref}.markdown"
    if cancellation:
        cancellation.check()
    markdown_output = store.publish_bytes(
        markdown_ref,
        "content.md",
        normalized_markdown.encode("utf-8"),
        "text/markdown",
        "utf-8",
    )
    structured_ref = f"ocr.{asset.artifact_ref}.structured"
    structured = {
        "schemaVersion": 1,
        "sourceImageRef": asset.artifact_ref,
        "markdownArtifactRef": markdown_ref,
        "blocks": [
            {
                "blockId": f"{asset.artifact_ref}.block.0000",
                "type": "document",
                "order": 0,
                "sourceImageRef": asset.artifact_ref,
                "markdownArtifactRef": markdown_ref,
            },
            *[
                {
                    "blockId": f"{asset.artifact_ref}.image.{entry['order'] + 1:04d}",
                    "type": "image_ref",
                    "order": entry["order"] + 1,
                    "sourceImageRef": asset.artifact_ref,
                    "imageArtifactRef": entry["imageArtifactRef"],
                }
                for entry in image_refs
            ],
        ],
        "imageRefs": image_refs,
    }
    if cancellation:
        cancellation.check()
    structured_output = store.publish_bytes(
        structured_ref,
        "content.json",
        (json.dumps(structured, ensure_ascii=False, sort_keys=True, indent=2) + "\n").encode("utf-8"),
        "application/json",
        "utf-8",
    )
    item = {
        "artifactRef": asset.artifact_ref,
        "groupId": asset.group_id,
        "role": asset.role,
        "groupIndex": asset.group_index,
        "imageIndex": asset.image_index,
        "status": "completed",
        "source": asset.source_descriptor(),
        "ocr": {
            "provider": "mineru",
            "providerItemId": provider_result.provider_item_id,
            "markdownArtifactRef": markdown_ref,
            "structuredArtifactRef": structured_ref,
            "imageArtifactRefs": [entry["imageArtifactRef"] for entry in image_refs],
            "rawArtifactsRoot": provider_result.raw_root,
        },
        "error": None,
    }
    produced_outputs.extend([markdown_output, structured_output])
    return item, produced_outputs


def _item_diagnostics(items: list[dict[str, Any]]) -> list[dict[str, Any]]:
    diagnostics = []
    for item in items:
        error = item.get("error") if isinstance(item.get("error"), dict) else None
        diagnostics.append(
            {
                "artifactRef": item.get("artifactRef"),
                "status": item.get("status"),
                "code": error.get("code") if error else None,
                "retryable": bool(error.get("retryable")) if error else False,
            }
        )
    return diagnostics


def _failed_item(asset: ValidatedAsset, provider_result: ProviderItemResult) -> dict[str, Any]:
    return {
        "artifactRef": asset.artifact_ref,
        "groupId": asset.group_id,
        "role": asset.role,
        "groupIndex": asset.group_index,
        "imageIndex": asset.image_index,
        "status": "failed",
        "source": asset.source_descriptor(),
        "ocr": {
            "provider": "mineru",
            "providerItemId": provider_result.provider_item_id,
            "rawArtifactsRoot": provider_result.raw_root,
        },
        "error": {
            "code": provider_result.error_code or "provider_failed",
            "message": clean_message(provider_result.message or "OCR failed"),
            "retryable": bool(provider_result.retryable),
        },
    }


def run_worker(
    package_root: Path,
    request_relative_path: str,
    *,
    attempt_id: str | None = None,
    provider: Provider | None = None,
    retry_result_relative_path: str | None = None,
    resume_submission_relative_path: str | None = None,
    request_override: dict[str, Any] | None = None,
    attempt_relative_path: str | None = None,
    result_filename: str = "result.json",
    allow_existing_attempt_root: bool = False,
    cancel_signal_relative_path: str | None = None,
    cancellation_monitor: CancellationMonitor | None = None,
) -> tuple[dict[str, Any], Path]:
    package_root = package_root.resolve()
    if not package_root.is_dir():
        raise WorkerFailure("missing_package", f"Task package does not exist: {package_root}")
    store = AttemptStore(package_root, attempt_id, attempt_relative_path, allow_existing_attempt_root)
    started_at = utc_now()
    request_id = "unknown"
    operation_id = "unknown"
    mode = "unknown"
    try:
        request_path = safe_relative_path(package_root, request_relative_path)
        request_bytes = read_file_bytes(request_path)
        store.write_json_once(
            "request-ref.json",
            {
                "path": package_relative(package_root, request_path),
                "sha256": sha256_bytes(request_bytes),
                "bytes": len(request_bytes),
            },
        )
        request_raw = request_override if request_override is not None else load_json(request_path)
        request = validate_request(package_root, request_raw)
        request_id = request.request_id
        operation_id = request.operation_id
        mode = request.mode

        cancellation = cancellation_monitor
        if cancellation is None and cancel_signal_relative_path:
            cancellation = CancellationMonitor(
                safe_relative_path(package_root, cancel_signal_relative_path),
                store.attempt_id,
                request.operation_id,
            )
        if cancellation:
            cancellation.check()

        prior_items: dict[str, dict[str, Any]] = {}
        prior_outputs: list[dict[str, Any]] = []
        retry_targets: set[str] | None = None
        retry_source_attempt: str | None = None
        if retry_result_relative_path:
            if cancellation:
                cancellation.check()
            prior = _load_prior_result(package_root, retry_result_relative_path, request)
            raw_items = prior.get("details", {}).get("items") if isinstance(prior.get("details"), dict) else None
            if not isinstance(raw_items, list):
                raise WorkerFailure("retry_invalid", "Prior result has no item list")
            prior_items = {
                item.get("artifactRef"): item
                for item in raw_items
                if isinstance(item, dict) and isinstance(item.get("artifactRef"), str)
            }
            raw_prior_outputs = prior.get("outputs")
            if not isinstance(raw_prior_outputs, list):
                raise WorkerFailure("retry_invalid", "Prior result has no output list")
            prior_outputs = [item for item in raw_prior_outputs if isinstance(item, dict)]
            retry_targets = {ref for ref, item in prior_items.items() if item.get("status") == "failed"}
            if not retry_targets:
                raise WorkerFailure("retry_invalid", "Prior result has no failed images")
            retry_source_attempt = prior.get("attemptId")

        assets_to_process = [asset for asset in request.assets if retry_targets is None or asset.artifact_ref in retry_targets]
        resume_submission = None
        if resume_submission_relative_path:
            if cancellation:
                cancellation.check()
            resume_submission = _load_resume_submission(package_root, resume_submission_relative_path, request)
            expected = [asset.artifact_ref for asset in assets_to_process]
            actual = [entry.get("artifactRef") for entry in resume_submission.get("items", []) if isinstance(entry, dict)]
            if actual != expected:
                raise WorkerFailure("resume_invalid", "Resume submission does not match the current target images")

        outputs: list[dict[str, Any]] = []
        items: list[dict[str, Any]] = []
        provider_calls = 0
        if request.mode == "original_passthrough":
            if retry_result_relative_path or resume_submission_relative_path:
                raise WorkerFailure("retry_invalid", "original_passthrough has no OCR retry stage")
            for asset in request.assets:
                if cancellation:
                    cancellation.check()
                outputs.append(_input_output_descriptor(asset))
                items.append(
                    {
                        "artifactRef": asset.artifact_ref,
                        "groupId": asset.group_id,
                        "role": asset.role,
                        "groupIndex": asset.group_index,
                        "imageIndex": asset.image_index,
                        "status": "completed",
                        "source": asset.source_descriptor(),
                        "ocr": None,
                        "error": None,
                    }
                )
            status = "completed"
            outputs.append(_publish_original_media_bundle(store, request, cancellation))
        else:
            if provider is None:
                raise WorkerFailure("provider_unavailable", "No MinerU provider was configured")
            # OCR review and human correction must remain hash-bound to the exact
            # source image in the same completed WorkerResult. These descriptors
            # reference the immutable capture bytes; they do not copy, transform,
            # or upload the image a second time.
            for asset in request.assets:
                if cancellation:
                    cancellation.check()
                outputs.append(_input_output_descriptor(asset))
            provider_results = provider.process(assets_to_process, request, store, resume_submission, cancellation)
            provider_calls = 1
            for asset in request.assets:
                if cancellation:
                    cancellation.check()
                if retry_targets is not None and asset.artifact_ref not in retry_targets:
                    prior_item = prior_items.get(asset.artifact_ref)
                    if prior_item is None or prior_item.get("status") != "completed":
                        raise WorkerFailure("retry_invalid", f"Missing reusable item: {asset.artifact_ref}")
                    prior_source = prior_item.get("source") if isinstance(prior_item.get("source"), dict) else {}
                    if prior_source.get("sha256") != asset.sha256 or prior_source.get("bytes") != asset.size_bytes:
                        raise WorkerFailure("retry_invalid", f"Reusable item hash changed: {asset.artifact_ref}")
                    reused = dict(prior_item)
                    reused["reusedFromAttempt"] = retry_source_attempt
                    items.append(reused)
                    ocr = reused.get("ocr") if isinstance(reused.get("ocr"), dict) else {}
                    reused_refs = {ocr.get("markdownArtifactRef"), ocr.get("structuredArtifactRef")}
                    for prior_output in prior_outputs:
                        prior_ref = prior_output.get("artifactRef")
                        if prior_ref in reused_refs or (isinstance(prior_ref, str) and prior_ref.startswith(f"ocr.{asset.artifact_ref}.image.")):
                            descriptor = dict(prior_output)
                            output_path = safe_relative_path(package_root, require_string(descriptor.get("path"), "reused output path"))
                            if (
                                not path_is_file(output_path)
                                or path_size(output_path) != descriptor.get("bytes")
                                or sha256_file(output_path) != descriptor.get("sha256")
                            ):
                                raise WorkerFailure("retry_invalid", f"Reusable output integrity failed: {descriptor.get('artifactRef')}")
                            descriptor["reusedFromAttempt"] = retry_source_attempt
                            outputs.append(descriptor)
                    continue
                provider_result = provider_results.get(asset.artifact_ref)
                if provider_result is None:
                    provider_result = ProviderItemResult(
                        status="failed",
                        error_code="provider_result_missing",
                        message="Provider returned no item result",
                        retryable=True,
                    )
                if provider_result.status == "completed":
                    try:
                        item, produced = _publish_ocr_item(store, asset, provider_result, cancellation)
                        items.append(item)
                        outputs.extend(produced)
                    except WorkerFailure as exc:
                        items.append(
                            _failed_item(
                                asset,
                                ProviderItemResult(
                                    status="failed",
                                    provider_item_id=provider_result.provider_item_id,
                                    raw_root=provider_result.raw_root,
                                    error_code=exc.code,
                                    message=str(exc),
                                    retryable=exc.retryable,
                                ),
                            )
                        )
                else:
                    items.append(_failed_item(asset, provider_result))
            completed_count = sum(item.get("status") == "completed" for item in items)
            failed_count = sum(item.get("status") == "failed" for item in items)
            if failed_count == 0:
                status = "completed"
            elif completed_count:
                status = "partial"
            else:
                status = "failed"

        outputs.append(_publish_processing_provenance(store, request, items, outputs, status, provider_calls, cancellation))

        if cancellation:
            cancellation.check()
        result = {
            "schemaVersion": RESULT_SCHEMA_VERSION,
            "requestId": request.request_id,
            "operationId": request.operation_id,
            "worker": COMMON_WORKER_ID,
            "workerVersion": WORKER_VERSION,
            "attemptId": store.attempt_id,
            "status": status,
            "mode": request.mode,
            "cardId": request.card_id,
            "startedAt": started_at,
            "finishedAt": utc_now(),
            "outputs": outputs,
            "itemDiagnostics": _item_diagnostics(items),
            "details": {
                "captureBundleId": request.capture_bundle_id,
                "retentionDays": request.retention_days,
                "cloudUploadAuthorized": request.cloud_upload_authorized,
                "providerCalls": provider_calls,
                "partialBlocksDownstream": True,
                "items": items,
            },
            "error": None,
        }
        result_path = store.write_json_once(result_filename, result)
        return result, result_path
    except WorkerFailure as exc:
        result = {
            "schemaVersion": RESULT_SCHEMA_VERSION,
            "requestId": request_id,
            "operationId": operation_id,
            "worker": COMMON_WORKER_ID,
            "workerVersion": WORKER_VERSION,
            "attemptId": store.attempt_id,
            "status": exc.status,
            "mode": mode,
            "cardId": None,
            "startedAt": started_at,
            "finishedAt": utc_now(),
            "outputs": [],
            "itemDiagnostics": [],
            "details": {"partialBlocksDownstream": True, "items": []},
            "error": {"code": exc.code, "message": clean_message(str(exc)), "retryable": exc.retryable},
        }
        result_path = store.write_json_once(result_filename, result)
        return result, result_path
    except Exception as exc:
        result = {
            "schemaVersion": RESULT_SCHEMA_VERSION,
            "requestId": request_id,
            "operationId": operation_id,
            "worker": COMMON_WORKER_ID,
            "workerVersion": WORKER_VERSION,
            "attemptId": store.attempt_id,
            "status": "failed",
            "mode": mode,
            "cardId": None,
            "startedAt": started_at,
            "finishedAt": utc_now(),
            "outputs": [],
            "itemDiagnostics": [],
            "details": {"partialBlocksDownstream": True, "items": []},
            "error": {"code": "unexpected_error", "message": clean_message(str(exc)), "retryable": False},
        }
        result_path = store.write_json_once(result_filename, result)
        return result, result_path


def _validate_common_artifact_reference(value: Any, field: str) -> dict[str, Any]:
    if not isinstance(value, dict) or set(value) != {
        "artifactRef", "kind", "contractVersion", "manifestPath", "expectedSha256"
    }:
        raise WorkerFailure("common_contract_invalid", f"{field} must be a common artifact reference")
    require_id(value.get("artifactRef"), f"{field}.artifactRef")
    kind = value.get("kind")
    if not isinstance(kind, str) or not re.fullmatch(r"^[a-z0-9][a-z0-9._-]{0,127}$", kind):
        raise WorkerFailure("common_contract_invalid", f"{field}.kind is invalid")
    if value.get("contractVersion") != COMMON_SCHEMA_VERSION:
        raise WorkerFailure("common_contract_invalid", f"{field}.contractVersion must be {COMMON_SCHEMA_VERSION}")
    require_string(value.get("manifestPath"), f"{field}.manifestPath")
    expected_hash = value.get("expectedSha256")
    if not isinstance(expected_hash, str) or not SHA256_PATTERN.fullmatch(expected_hash.lower()):
        raise WorkerFailure("common_contract_invalid", f"{field}.expectedSha256 is invalid")
    return value


def _load_common_capture_artifact(
    package_root: Path,
    reference: dict[str, Any],
) -> tuple[dict[str, Any], Path]:
    manifest_path = safe_relative_path(package_root, reference["manifestPath"])
    if not path_is_file(manifest_path) or sha256_file(manifest_path) != reference["expectedSha256"].lower():
        raise WorkerFailure("integrity_mismatch", "Capture bundle artifact manifest integrity check failed")
    manifest = load_json(manifest_path)
    required = {
        "schemaVersion", "artifactId", "kind", "contractVersion", "mediaType", "files",
        "createdAt", "producer", "inputRefs", "dataClass",
    }
    if not required.issubset(manifest) or set(manifest) - (required | {"metadata"}):
        raise WorkerFailure("common_contract_invalid", "Capture bundle artifact manifest has invalid fields")
    if (
        manifest.get("schemaVersion") != COMMON_SCHEMA_VERSION
        or manifest.get("artifactId") != reference["artifactRef"]
        or manifest.get("kind") != reference["kind"]
        or manifest.get("contractVersion") != reference["contractVersion"]
    ):
        raise WorkerFailure("artifact_reference_mismatch", "Capture bundle artifact manifest does not match inputRef")
    if manifest.get("dataClass") == "secret-prohibited":
        raise WorkerFailure("data_class_prohibited", "secret-prohibited artifacts cannot be processed")
    files = manifest.get("files")
    if not isinstance(files, list) or not files:
        raise WorkerFailure("common_contract_invalid", "Capture bundle artifact must declare files")
    resolved: dict[str, tuple[dict[str, Any], Path]] = {}
    for index, item in enumerate(files):
        if not isinstance(item, dict) or set(item) != {"path", "sha256", "bytes", "mediaType"}:
            raise WorkerFailure("common_contract_invalid", f"artifact.files[{index}] is invalid")
        path = safe_relative_path(package_root, require_string(item.get("path"), f"artifact.files[{index}].path"))
        expected_hash = item.get("sha256")
        expected_bytes = item.get("bytes")
        if not isinstance(expected_hash, str) or not SHA256_PATTERN.fullmatch(expected_hash.lower()):
            raise WorkerFailure("common_contract_invalid", f"artifact.files[{index}].sha256 is invalid")
        if isinstance(expected_bytes, bool) or not isinstance(expected_bytes, int) or expected_bytes < 0:
            raise WorkerFailure("common_contract_invalid", f"artifact.files[{index}].bytes is invalid")
        if not path_is_file(path) or path_size(path) != expected_bytes or sha256_file(path) != expected_hash.lower():
            raise WorkerFailure("integrity_mismatch", f"Artifact file integrity check failed: {item['path']}")
        resolved[item["path"].replace("\\", "/")] = (item, path)
    metadata = manifest.get("metadata") if isinstance(manifest.get("metadata"), dict) else {}
    entrypoint = metadata.get("entrypoint")
    if isinstance(entrypoint, str):
        selected = resolved.get(entrypoint.replace("\\", "/"))
        if selected is None:
            raise WorkerFailure("artifact_entrypoint_invalid", "Capture bundle entrypoint is not declared in files")
    else:
        candidates = [value for value in resolved.values() if value[0].get("mediaType") == "application/vnd.rqc.capture-bundle+json"]
        if len(candidates) != 1:
            raise WorkerFailure("artifact_entrypoint_missing", "Capture bundle artifact needs one JSON entrypoint")
        selected = candidates[0]
    selected_item, selected_path = selected
    if selected_item.get("mediaType") != "application/vnd.rqc.capture-bundle+json":
        raise WorkerFailure("unsupported_media", "Capture bundle entrypoint mediaType is unsupported")
    return manifest, selected_path


def _validate_common_request(package_root: Path, request_relative_path: str) -> tuple[dict[str, Any], dict[str, Any], Path]:
    request_path = safe_relative_path(package_root, request_relative_path)
    request = load_json(request_path)
    required = {
        "schemaVersion", "requestId", "operationId", "taskId", "runId", "stageId", "attemptId",
        "workerId", "operation", "idempotencyKey", "attemptPath", "outputPath", "progressPath",
        "resultPath", "cancelSignal", "inputRefs", "configSnapshot", "payload",
    }
    if set(request) != required or request.get("schemaVersion") != COMMON_SCHEMA_VERSION:
        raise WorkerFailure("common_contract_invalid", "Unsupported or malformed common worker-request")
    for key in ("requestId", "operationId", "taskId", "runId", "stageId", "attemptId", "workerId", "operation"):
        require_id(request.get(key), f"worker-request.{key}")
    if request["workerId"] != COMMON_WORKER_ID:
        raise WorkerFailure("wrong_worker", f"workerId must be {COMMON_WORKER_ID}")
    if request["operation"] != "process-images":
        raise WorkerFailure("operation_unsupported", "operation must be process-images")
    if not isinstance(request.get("idempotencyKey"), str) or not SHA256_PATTERN.fullmatch(request["idempotencyKey"].lower()):
        raise WorkerFailure("common_contract_invalid", "worker-request.idempotencyKey is invalid")
    canonical_attempt = f"runs/{request['runId']}/stages/{request['stageId']}/attempts/{request['attemptId']}"
    expected_paths = {
        "attemptPath": canonical_attempt,
        "outputPath": f"{canonical_attempt}/outputs",
        "progressPath": f"{canonical_attempt}/progress.ndjson",
        "resultPath": f"{canonical_attempt}/result.json",
    }
    for key, expected in expected_paths.items():
        if request.get(key) != expected:
            raise WorkerFailure("common_path_invalid", f"worker-request.{key} must be {expected}")
    normalized_request_path = request_relative_path.replace("\\", "/")
    if normalized_request_path not in {f"{canonical_attempt}/request.json", f"{canonical_attempt}/worker-request.json"}:
        raise WorkerFailure("common_path_invalid", "Common worker-request must be inside its canonical attempt")
    cancel_signal = request.get("cancelSignal")
    if (
        not isinstance(cancel_signal, dict)
        or set(cancel_signal) != {"schemaVersion", "path", "gracePeriodMs"}
        or cancel_signal.get("schemaVersion") != COMMON_SCHEMA_VERSION
        or cancel_signal.get("path") != f"{canonical_attempt}/cancel.json"
        or cancel_signal.get("gracePeriodMs") != 2000
    ):
        raise WorkerFailure("common_path_invalid", "worker-request.cancelSignal must reference the canonical cancel.json")
    if not isinstance(request.get("configSnapshot"), dict) or not isinstance(request.get("payload"), dict):
        raise WorkerFailure("common_contract_invalid", "configSnapshot and payload must be objects")
    input_refs = request.get("inputRefs")
    if not isinstance(input_refs, list):
        raise WorkerFailure("common_contract_invalid", "inputRefs must be an array")
    validated_refs = [_validate_common_artifact_reference(value, f"inputRefs[{index}]") for index, value in enumerate(input_refs)]
    capture_refs = [value for value in validated_refs if value["kind"] == "capture-bundle"]
    if len(capture_refs) != 1 or len(validated_refs) != 1:
        raise WorkerFailure("input_artifacts_invalid", "image-ocr requires exactly one capture-bundle inputRef")
    return request, capture_refs[0], request_path


def _write_json_new(package_root: Path, relative: str, value: Any) -> Path:
    path = safe_relative_path(package_root, relative)
    os.makedirs(native_fs_path(path.parent), exist_ok=True)
    try:
        with open(native_fs_path(path), "x", encoding="utf-8", newline="\n") as handle:
            json.dump(value, handle, ensure_ascii=False, sort_keys=True, indent=2)
            handle.write("\n")
    except FileExistsError as exc:
        raise WorkerFailure("immutable_file_exists", f"Refusing to overwrite: {relative}") from exc
    return path


def _write_json_atomic_create_once(package_root: Path, relative: str, value: Any) -> Path:
    final_path = safe_relative_path(package_root, relative)
    os.makedirs(native_fs_path(final_path.parent), exist_ok=True)
    temporary = final_path.parent / f".{final_path.name}.{uuid.uuid4().hex}.tmp"
    try:
        with open(native_fs_path(temporary), "xb") as handle:
            payload = (json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2) + "\n").encode("utf-8")
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        try:
            os.link(native_fs_path(temporary), native_fs_path(final_path))
        except FileExistsError as exc:
            raise WorkerFailure("immutable_file_exists", f"Refusing to overwrite: {relative}") from exc
    finally:
        try:
            os.unlink(native_fs_path(temporary))
        except FileNotFoundError:
            pass
    return final_path


def _common_error_category(code: str, retryable: bool, status: str) -> tuple[str, str]:
    if status == "unknown_external_state":
        return "effect", "dependency"
    if status == "cancelled":
        return "cancelled", "user"
    if retryable:
        return "transient", "dependency"
    if code.startswith(("schema_", "common_", "capture_", "input_", "integrity_", "mode_", "path_", "retention_")) or code in {
        "artifact_reference_mismatch", "artifact_entrypoint_invalid", "artifact_entrypoint_missing", "data_class_prohibited"
    }:
        return "contract", "input"
    if code in {"credential_unavailable", "provider_unavailable", "provider_failed", "unsupported_media"}:
        return "dependency", "dependency"
    return "worker", "worker"


def _write_common_error(
    package_root: Path,
    attempt_path: str,
    name: str,
    error: dict[str, Any],
    status: str,
) -> dict[str, Any]:
    code = str(error.get("code") or "worker_failed")
    retryable = bool(error.get("retryable")) and status != "unknown_external_state"
    category, blame = _common_error_category(code, retryable, status)
    relative = f"{attempt_path}/errors/{name}.json"
    _write_json_new(
        package_root,
        relative,
        {
            "code": code,
            "message": clean_message(str(error.get("message") or code)),
            "retryable": retryable,
            "status": status,
            "recordedAt": utc_now(),
        },
    )
    return {
        "code": code,
        "category": category,
        "retryable": retryable,
        "detailsRef": relative,
        "message": clean_message(str(error.get("message") or code)),
        "blame": blame,
    }


def _common_artifact_kind(descriptor: dict[str, Any]) -> str:
    explicit_kind = descriptor.get("artifactKind")
    if explicit_kind in {"image.original.bundle", "image.processing.provenance"}:
        return explicit_kind
    if descriptor.get("origin") == "input":
        return "image.original"
    media_type = descriptor.get("mediaType")
    if media_type == "text/markdown":
        return "image-ocr.markdown"
    if media_type == "application/json":
        return "image-ocr.structured"
    if isinstance(media_type, str) and media_type.startswith("image/"):
        return "image-ocr.image"
    return "image-ocr.output"


def _publish_common_artifact(
    package_root: Path,
    request: dict[str, Any],
    capture_reference: dict[str, Any],
    descriptor: dict[str, Any],
    cancellation: CancellationMonitor | None = None,
) -> dict[str, Any]:
    if cancellation:
        cancellation.check()
    artifact_id = require_id(descriptor.get("artifactRef"), "module output artifactRef")
    payload_path = safe_relative_path(package_root, require_string(descriptor.get("path"), "module output path"))
    expected_hash = descriptor.get("sha256")
    expected_bytes = descriptor.get("bytes")
    if (
        not path_is_file(payload_path)
        or not isinstance(expected_hash, str)
        or sha256_file(payload_path) != expected_hash
        or isinstance(expected_bytes, bool)
        or not isinstance(expected_bytes, int)
        or path_size(payload_path) != expected_bytes
    ):
        raise WorkerFailure("integrity_mismatch", f"Module output integrity check failed: {artifact_id}")
    manifest = {
        "schemaVersion": COMMON_SCHEMA_VERSION,
        "artifactId": artifact_id,
        "kind": _common_artifact_kind(descriptor),
        "contractVersion": COMMON_SCHEMA_VERSION,
        "mediaType": descriptor["mediaType"],
        "files": [
            {
                "path": descriptor["path"],
                "sha256": descriptor["sha256"],
                "bytes": descriptor["bytes"],
                "mediaType": descriptor["mediaType"],
            }
        ],
        "createdAt": utc_now(),
        "producer": {
            "stageId": request["stageId"],
            "workerId": COMMON_WORKER_ID,
            "workerVersion": WORKER_VERSION,
            "attemptId": request["attemptId"],
        },
        "inputRefs": [capture_reference],
        "dataClass": "sensitive",
        "metadata": {
            "entrypoint": descriptor["path"],
            "moduleArtifactRef": artifact_id,
            "origin": descriptor.get("origin", "produced"),
            "reusedFromAttempt": descriptor.get("reusedFromAttempt"),
            "payloadSchemaVersion": descriptor.get("payloadSchemaVersion"),
        },
    }
    manifest_relative = f"{request['outputPath']}/{artifact_id}/v0001/artifact.json"
    if cancellation:
        cancellation.check()
    manifest_path = _write_json_new(package_root, manifest_relative, manifest)
    return {
        "artifactRef": artifact_id,
        "kind": manifest["kind"],
        "contractVersion": COMMON_SCHEMA_VERSION,
        "manifestPath": manifest_relative,
        "expectedSha256": sha256_file(manifest_path),
    }


def _project_common_result(
    package_root: Path,
    request: dict[str, Any],
    capture_reference: dict[str, Any],
    module_result: dict[str, Any],
    module_result_path: Path,
    cancellation: CancellationMonitor | None = None,
) -> dict[str, Any]:
    common_by_module_ref: dict[str, dict[str, Any]] = {}
    for descriptor in module_result.get("outputs", []):
        if cancellation:
            cancellation.check()
        common_ref = _publish_common_artifact(package_root, request, capture_reference, descriptor, cancellation)
        common_by_module_ref[descriptor["artifactRef"]] = common_ref

    diagnostics: list[dict[str, Any]] = []
    raw_items = module_result.get("details", {}).get("items", [])
    for index, item in enumerate(raw_items):
        if cancellation:
            cancellation.check()
        item_id = require_id(item.get("artifactRef"), f"module item {index}.artifactRef")
        item_output_refs: list[dict[str, Any]] = []
        if item.get("status") == "completed":
            if module_result.get("mode") == "original_passthrough":
                module_refs = [item_id]
            else:
                ocr = item.get("ocr") if isinstance(item.get("ocr"), dict) else {}
                module_refs = [
                    ref for ref in common_by_module_ref
                    if ref in {item_id, ocr.get("markdownArtifactRef"), ocr.get("structuredArtifactRef")}
                    or ref.startswith(f"ocr.{item_id}.image.")
                ]
            item_output_refs = [common_by_module_ref[ref] for ref in module_refs if ref in common_by_module_ref]
            item_error = None
            item_status = "completed"
        else:
            raw_error = item.get("error") if isinstance(item.get("error"), dict) else {"code": "item_failed"}
            item_status = "unknown_external_state" if module_result.get("status") == "unknown_external_state" else "failed"
            item_error = _write_common_error(package_root, request["attemptPath"], f"item-{index + 1:04d}", raw_error, item_status)
        diagnostics.append({"itemId": item_id, "status": item_status, "outputs": item_output_refs, "error": item_error})

    status = module_result["status"]
    common_outputs = list(common_by_module_ref.values()) if status in {"completed", "partial"} else []
    top_error = None
    if status in {"failed", "cancelled", "unknown_external_state"}:
        raw_error = module_result.get("error") if isinstance(module_result.get("error"), dict) else {
            "code": "all_items_failed",
            "message": "All image items failed; inspect itemDiagnostics",
            "retryable": any(bool(item.get("error", {}).get("retryable")) for item in raw_items if isinstance(item, dict)),
        }
        top_error = _write_common_error(package_root, request["attemptPath"], "worker", raw_error, status)
    module_relative = package_relative(package_root, module_result_path)
    provenance_output = common_by_module_ref.get("image.processing.provenance")
    media_bundle_output = common_by_module_ref.get("image.original.bundle")
    if cancellation:
        cancellation.check()
    return {
        "schemaVersion": COMMON_SCHEMA_VERSION,
        "requestId": request["requestId"],
        "operationId": request["operationId"],
        "taskId": request["taskId"],
        "runId": request["runId"],
        "stageId": request["stageId"],
        "attemptId": request["attemptId"],
        "worker": {"id": COMMON_WORKER_ID, "version": WORKER_VERSION},
        "contractVersion": COMMON_SCHEMA_VERSION,
        "idempotencyKey": request["idempotencyKey"],
        "status": status,
        "outputs": common_outputs,
        "itemDiagnostics": diagnostics,
        "error": top_error,
        "startedAt": module_result["startedAt"],
        "finishedAt": utc_now(),
        "warnings": ["partial result blocks automatic downstream execution"] if status == "partial" else [],
        "metrics": {
            "items": len(raw_items),
            "completed": sum(item.get("status") == "completed" for item in raw_items),
            "failed": sum(item.get("status") == "failed" for item in raw_items),
        },
        "payload": {
            "captureId": module_result.get("details", {}).get("captureBundleId"),
            "mode": module_result.get("mode"),
            "retentionDays": 30,
            "partialBlocksDownstream": True,
            "moduleResultPath": module_relative,
            "moduleResultSha256": sha256_file(module_result_path),
            "provenanceOutput": provenance_output,
            "originalMediaBundleOutput": media_bundle_output,
        },
    }


def _interrupted_common_result(
    package_root: Path,
    request: dict[str, Any],
    module_result: dict[str, Any],
    module_result_path: Path,
    failure: WorkerFailure,
) -> dict[str, Any]:
    status = failure.status
    raw_error = {"code": failure.code, "message": str(failure), "retryable": failure.retryable}
    return {
        "schemaVersion": COMMON_SCHEMA_VERSION,
        "requestId": request["requestId"],
        "operationId": request["operationId"],
        "taskId": request["taskId"],
        "runId": request["runId"],
        "stageId": request["stageId"],
        "attemptId": request["attemptId"],
        "worker": {"id": COMMON_WORKER_ID, "version": WORKER_VERSION},
        "contractVersion": COMMON_SCHEMA_VERSION,
        "idempotencyKey": request["idempotencyKey"],
        "status": status,
        "outputs": [],
        "itemDiagnostics": [],
        "error": _write_common_error(package_root, request["attemptPath"], "terminal", raw_error, status),
        "startedAt": module_result.get("startedAt", utc_now()),
        "finishedAt": utc_now(),
        "warnings": [],
        "metrics": {"items": 0, "completed": 0, "failed": 0},
        "payload": {
            "captureId": module_result.get("details", {}).get("captureBundleId"),
            "mode": module_result.get("mode"),
            "retentionDays": 30,
            "partialBlocksDownstream": True,
            "moduleResultPath": package_relative(package_root, module_result_path),
            "moduleResultSha256": sha256_file(module_result_path),
            "cancellationReason": failure.code.removeprefix("cancel."),
        },
    }


def _preflight_common_result(
    package_root: Path,
    request: dict[str, Any],
    failure: WorkerFailure,
    started_at: str,
) -> dict[str, Any]:
    status = failure.status
    raw_error = {"code": failure.code, "message": str(failure), "retryable": failure.retryable}
    payload: dict[str, Any] = {
        "phase": "preflight",
        "retentionDays": 30,
        "partialBlocksDownstream": True,
    }
    if failure.code.startswith("cancel."):
        payload["cancellationReason"] = failure.code.removeprefix("cancel.")
    return {
        "schemaVersion": COMMON_SCHEMA_VERSION,
        "requestId": request["requestId"],
        "operationId": request["operationId"],
        "taskId": request["taskId"],
        "runId": request["runId"],
        "stageId": request["stageId"],
        "attemptId": request["attemptId"],
        "worker": {"id": COMMON_WORKER_ID, "version": WORKER_VERSION},
        "contractVersion": COMMON_SCHEMA_VERSION,
        "idempotencyKey": request["idempotencyKey"],
        "status": status,
        "outputs": [],
        "itemDiagnostics": [],
        "error": _write_common_error(package_root, request["attemptPath"], "preflight", raw_error, status),
        "startedAt": started_at,
        "finishedAt": utc_now(),
        "warnings": [],
        "metrics": {"items": 0, "completed": 0, "failed": 0},
        "payload": payload,
    }


def _configured_mineru_provider(request: dict[str, Any]) -> MineruProvider:
    configured = request.get("configSnapshot", {}).get("mineruScriptPath")
    if configured is None:
        configured = os.environ.get("KNOWLEDGE_CLIPPER_MINERU_SCRIPT")
    if configured is None:
        configured = os.environ.get("RQC_MINERU_SCRIPT")  # Legacy compatibility alias.
    if configured:
        candidate = Path(str(configured))
        if not candidate.is_absolute():
            raise WorkerFailure("provider_path_invalid", "mineruScriptPath must be absolute")
        if not path_is_file(candidate):
            raise WorkerFailure("provider_unavailable", "Configured MinerU provider script is unavailable")
        return MineruProvider(candidate)
    candidates = [
        Path(sys.executable).resolve().parent / "mineru_ocr.py",
        Path(__file__).resolve().parents[2].parent / "MinerU OCR Integration" / "mineru_ocr.py",
    ]
    for candidate in candidates:
        if path_is_file(candidate):
            return MineruProvider(candidate)
    raise WorkerFailure("provider_unavailable", "MinerU provider script is unavailable")


def run_common_adapter(
    package_root: Path,
    request_relative_path: str,
    *,
    provider: Provider | None = None,
) -> tuple[dict[str, Any], Path]:
    package_root = package_root.resolve()
    started_at = utc_now()
    request, capture_reference, capture_manifest_path = _validate_common_request(package_root, request_relative_path)
    cancellation = CancellationMonitor(
        safe_relative_path(package_root, request["cancelSignal"]["path"]),
        request["attemptId"],
        request["operationId"],
        request["cancelSignal"]["gracePeriodMs"],
    )
    try:
        cancellation.check()
        _, capture_path = _load_common_capture_artifact(package_root, capture_reference)
        capture_payload = read_file_bytes(capture_path)
        capture = load_json(capture_path)
        mode_snapshot = capture.get("modeSnapshot") if isinstance(capture.get("modeSnapshot"), dict) else {}
        handling = mode_snapshot.get("imageHandling")
        if handling not in {"ocr", "original"}:
            raise WorkerFailure("capture_bundle_unsupported", "capture bundle imageHandling is invalid")
        if handling == "ocr" and provider is None:
            provider = _configured_mineru_provider(request)
        provider_settings = request["configSnapshot"].get("providerSettings", {})
        retry_path = request["payload"].get("retryModuleResultPath")
        resume_path = request["payload"].get("resumeSubmissionPath")
        for value, field in ((retry_path, "retryModuleResultPath"), (resume_path, "resumeSubmissionPath")):
            if value is not None:
                require_string(value, field)
                safe_relative_path(package_root, value)
        internal_request = {
            "schemaVersion": REQUEST_SCHEMA_VERSION,
            "requestId": request["requestId"],
            "operationId": request["operationId"],
            "mode": "ocr_mineru" if handling == "ocr" else "original_passthrough",
            "retentionDays": 30,
            "cloudUploadAuthorized": handling == "ocr",
            "captureBundle": {
                "artifactRef": capture_reference["artifactRef"],
                "path": package_relative(package_root, capture_path),
                "sha256": sha256_bytes(capture_payload),
                "bytes": len(capture_payload),
                "mediaType": "application/vnd.rqc.capture-bundle+json",
            },
            "providerSettings": provider_settings,
        }
    except WorkerFailure as exc:
        common_result = _preflight_common_result(package_root, request, exc, started_at)
        common_result_path = _write_json_atomic_create_once(package_root, request["resultPath"], common_result)
        return common_result, common_result_path
    module_result, module_result_path = run_worker(
        package_root,
        request_relative_path,
        attempt_id=request["attemptId"],
        provider=provider,
        retry_result_relative_path=retry_path,
        resume_submission_relative_path=resume_path,
        request_override=internal_request,
        attempt_relative_path=request["attemptPath"],
        result_filename="module-result.json",
        allow_existing_attempt_root=True,
        cancellation_monitor=cancellation,
    )
    try:
        common_result = _project_common_result(
            package_root,
            request,
            capture_reference,
            module_result,
            module_result_path,
            cancellation,
        )
        cancellation.check()
    except WorkerFailure as exc:
        common_result = _interrupted_common_result(package_root, request, module_result, module_result_path, exc)
    common_result_path = _write_json_atomic_create_once(package_root, request["resultPath"], common_result)
    return common_result, common_result_path


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run the immutable KnowledgeClipper image/OCR worker")
    parser.add_argument("--package", required=True, help="Task package root")
    parser.add_argument("--request", default="inputs/image-request.v1.json", help="Request path relative to task package")
    parser.add_argument("--common-request", help="Common worker-request path relative to task package")
    parser.add_argument("--attempt-id")
    parser.add_argument("--mineru-script", help="Path to the existing MinerU integration script")
    parser.add_argument("--retry-result", help="Prior result path relative to task package")
    parser.add_argument("--resume-submission", help="Prior submission receipt path relative to task package")
    return parser.parse_args(argv)


def worker_describe() -> dict[str, Any]:
    return {
        "schemaVersion": COMMON_SCHEMA_VERSION,
        "workerId": COMMON_WORKER_ID,
        "workerVersion": WORKER_VERSION,
        "contractVersion": COMMON_SCHEMA_VERSION,
        "stages": ["image-ocr"],
        "inputKinds": ["capture-bundle"],
        "outputKinds": [
            "image.original",
            "image.original.bundle",
            "image-ocr.markdown",
            "image-ocr.structured",
            "image-ocr.image",
            "image.processing.provenance",
        ],
        "concurrency": {"model": "parallel", "maxParallelAttempts": 4},
        "sideEffectLevel": "external",
        "idempotencyLevel": "reconcilable",
        "cancellation": {
            "protocol": "attempt-signal-file-v1",
            "supportedReasons": ["user_stop", "superseded", "task_cancel", "timeout"],
        },
    }


def derive_cli_context(request_argument: str) -> tuple[Path, str, dict[str, Any]]:
    request_path = Path(request_argument)
    if not request_path.is_absolute():
        raise WorkerFailure("request_path_not_absolute", "--request must be an absolute path")
    request_path = request_path.resolve()
    if request_path.name.lower() != "request.json":
        raise WorkerFailure("invalid_request_filename", "--request must point to request.json")
    if not path_is_file(request_path):
        raise WorkerFailure("missing_request", "request.json does not exist")
    request = load_json(request_path)
    attempt_path = request.get("attemptPath")
    if not isinstance(attempt_path, str):
        raise WorkerFailure("common_contract_invalid", "worker-request.attemptPath is missing")
    parts = Path(attempt_path.replace("\\", "/")).parts
    if (
        len(parts) != 6
        or parts[0] != "runs"
        or parts[2] != "stages"
        or parts[4] != "attempts"
        or parts[1] != request.get("runId")
        or parts[3] != request.get("stageId")
        or parts[5] != request.get("attemptId")
    ):
        raise WorkerFailure("common_path_invalid", "attemptPath must use runs/<run>/stages/<stage>/attempts/<attempt>")
    package_root = request_path.parent
    for _ in parts:
        package_root = package_root.parent
    package_root = package_root.resolve()
    if package_root.name != request.get("taskId") or package_root.parent.name.lower() != "jobs":
        raise WorkerFailure("common_path_invalid", "Job root must be jobs/<taskId>")
    expected_request = safe_relative_path(package_root, f"{attempt_path}/request.json")
    if expected_request != request_path:
        raise WorkerFailure("common_path_invalid", "request.json does not match attemptPath")
    return package_root, package_relative(package_root, request_path), request


def _append_progress(package_root: Path, request: dict[str, Any], state: str, message: str) -> dict[str, Any]:
    record = {"timestampUtc": utc_now(), "state": state, "message": message}
    progress_path = safe_relative_path(package_root, request["progressPath"])
    os.makedirs(native_fs_path(progress_path.parent), exist_ok=True)
    with open(native_fs_path(progress_path), "ab") as handle:
        handle.write((json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8"))
        handle.flush()
    print(json.dumps(record, ensure_ascii=False, separators=(",", ":")), flush=True)
    return record


def _official_cli(argv: list[str]) -> int:
    if argv[0] == "describe":
        if argv != ["describe", "--json"]:
            raise WorkerFailure("cli_invalid", "describe accepts only --json")
        print(json.dumps(worker_describe(), ensure_ascii=False, separators=(",", ":")))
        return 0
    if argv[0] != "run" or len(argv) != 3 or argv[1] != "--request":
        raise WorkerFailure("cli_invalid", "Usage: describe --json | run --request <absolute-request.json>")
    package_root, request_relative, request = derive_cli_context(argv[2])
    _append_progress(package_root, request, "starting", "image OCR worker started")
    result, _ = run_common_adapter(package_root, request_relative)
    _append_progress(package_root, request, result["status"], "terminal result published")
    return 0 if result["status"] in {"completed", "cancelled"} else 1


def _legacy_main(argv: list[str]) -> int:
    args = parse_args(argv)
    provider: Provider | None = None
    if args.mineru_script:
        provider = MineruProvider(Path(args.mineru_script))
    if args.common_request:
        if args.attempt_id or args.retry_result or args.resume_submission:
            raise WorkerFailure("cli_invalid", "Common adapter takes attempt/retry/resume paths from worker-request")
        result, path = run_common_adapter(Path(args.package), args.common_request, provider=provider)
    else:
        result, path = run_worker(
            Path(args.package),
            args.request,
            attempt_id=args.attempt_id,
            provider=provider,
            retry_result_relative_path=args.retry_result,
            resume_submission_relative_path=args.resume_submission,
        )
    print(json.dumps({"status": result["status"], "attemptId": result["attemptId"], "result": str(path)}, ensure_ascii=False))
    return {"completed": 0, "partial": 2, "cancelled": 3, "unknown_external_state": 4}.get(result["status"], 1)


def main(argv: list[str]) -> int:
    try:
        if argv and argv[0] in {"describe", "run"}:
            return _official_cli(argv)
        return _legacy_main(argv)
    except WorkerFailure as exc:
        diagnostic = {
            "schemaVersion": COMMON_SCHEMA_VERSION,
            "status": exc.status,
            "error": {
                "code": exc.code,
                "category": "contract" if exc.code.startswith(("cli_", "request_", "common_")) else "worker",
                "retryable": exc.retryable,
                "message": clean_message(str(exc)),
            },
        }
        print(json.dumps(diagnostic, ensure_ascii=False, separators=(",", ":")), file=sys.stderr)
        return 2
    except Exception as exc:
        diagnostic = {
            "schemaVersion": COMMON_SCHEMA_VERSION,
            "status": "failed",
            "error": {"code": "worker_cli_error", "category": "internal", "retryable": False, "message": clean_message(str(exc))},
        }
        print(json.dumps(diagnostic, ensure_ascii=False, separators=(",", ":")), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
