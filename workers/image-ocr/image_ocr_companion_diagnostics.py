#!/usr/bin/env python3
"""Offline-only diagnostic actions for the Image/OCR settings companion.

The diagnostic deliberately injects a local Fake Provider into the real worker
adapter.  It exercises a formal common WorkerRequest without resolving a
credential or opening a network connection.
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any

import image_ocr_worker as worker


TEST_RESULT_VERSION = "image-ocr-companion-test-result.v4"
OFFLINE_FAKE_PROVIDER_ACTION_ID = "image.ocr.test.offline-fake-provider"
CANONICAL_CREDENTIAL_ID = "local-credential://image-ocr/mineru-api-token"
FAKE_PROCESS_TOKEN = "FAKE_IMAGE_OCR_PROCESS_TOKEN_000000000000"
PNG_1X1 = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zt9sAAAAASUVORK5CYII="
)


def _json_bytes(value: Any) -> bytes:
    return (json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2) + "\n").encode("utf-8")


def _sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


class _FakeProvider:
    def __init__(self) -> None:
        self.calls: list[list[str]] = []
        self.observed_settings: list[dict[str, Any]] = []

    def process(self, assets, request, store, resume_submission=None, cancellation=None):
        if resume_submission is not None:
            raise AssertionError("offline readiness test never resumes an external submission")
        refs = [asset.artifact_ref for asset in assets]
        self.calls.append(refs)
        self.observed_settings.append(dict(request.provider_settings))
        store.write_json_once(
            "submission.json",
            {
                "schemaVersion": 1,
                "provider": "local-fake-provider",
                "batchId": "offline-fake-batch-001",
                "submittedAt": worker.utc_now(),
                "requestId": request.request_id,
                "operationId": request.operation_id,
                "items": [{"artifactRef": asset.artifact_ref, "dataId": "fake-" + asset.artifact_ref} for asset in assets],
            },
        )
        return {
            asset.artifact_ref: worker.ProviderItemResult(
                status="completed",
                markdown=f"# OFFLINE FAKE OCR {asset.artifact_ref}\n",
                provider_item_id="fake-" + asset.artifact_ref,
            )
            for asset in assets
        }


def _build_formal_request(job_root: Path, config_snapshot: dict[str, Any]) -> tuple[dict[str, Any], Path]:
    image_ref = "capture.image.question.0001"
    image_path = job_root / "inputs" / "images" / "question.png"
    image_path.parent.mkdir(parents=True)
    image_path.write_bytes(PNG_1X1)

    capture = {
        "schemaVersion": "capture-bundle.v1",
        "captureId": "cap-offline",
        "createdAt": "2026-08-28T00:00:00Z",
        "intent": "screenshot",
        "source": "screenshot",
        "modeSnapshot": {
            "workflow": "card",
            "contextEnabled": False,
            "contextGranularity": "sentence",
            "contextApplied": False,
            "imageHandling": "ocr",
        },
        "artifacts": [
            {
                "artifactRef": image_ref,
                "relativePath": image_path.relative_to(job_root).as_posix(),
                "sha256": _sha256(PNG_1X1),
                "bytes": len(PNG_1X1),
                "mediaType": "image/png",
                "encoding": None,
            }
        ],
        "selectedTextArtifactRef": None,
        "contextSegments": None,
        "questionImages": [image_ref],
        "answerImages": [],
        "questionTurns": [],
    }
    capture_path = job_root / "inputs" / "capture-bundle.v1.json"
    capture_payload = _json_bytes(capture)
    capture_path.write_bytes(capture_payload)

    artifact_id = "capture.bundle.offline"
    capture_file = {
        "path": capture_path.relative_to(job_root).as_posix(),
        "sha256": _sha256(capture_payload),
        "bytes": len(capture_payload),
        "mediaType": "application/vnd.rqc.capture-bundle+json",
    }
    manifest = {
        "schemaVersion": "1.0.0",
        "artifactId": artifact_id,
        "kind": "capture-bundle",
        "contractVersion": "1.0.0",
        "mediaType": "application/vnd.rqc.capture-bundle+json",
        "files": [capture_file],
        "createdAt": "2026-08-28T00:00:00Z",
        "producer": {
            "stageId": "capture",
            "workerId": "desktop-capture",
            "workerVersion": "1.0.0",
            "attemptId": "capture-offline",
        },
        "inputRefs": [],
        "dataClass": "sensitive",
        "metadata": {"entrypoint": capture_file["path"]},
    }
    manifest_path = job_root / "artifacts" / artifact_id / "artifact.json"
    manifest_path.parent.mkdir(parents=True)
    manifest_payload = _json_bytes(manifest)
    manifest_path.write_bytes(manifest_payload)

    attempt = "runs/r/stages/image-ocr/attempts/a"
    attempt_root = job_root / attempt
    attempt_root.mkdir(parents=True)
    operation_id = "op-offline"
    request = {
        "schemaVersion": "1.0.0",
        "requestId": "req-offline",
        "operationId": operation_id,
        "taskId": "t",
        "runId": "r",
        "stageId": "image-ocr",
        "attemptId": "a",
        "workerId": "image.ocr.mineru",
        "operation": "process-images",
        "idempotencyKey": _sha256(f"{operation_id}:a".encode("utf-8")),
        "attemptPath": attempt,
        "outputPath": f"{attempt}/outputs",
        "progressPath": f"{attempt}/progress.ndjson",
        "resultPath": f"{attempt}/result.json",
        "cancelSignal": {
            "schemaVersion": "1.0.0",
            "path": f"{attempt}/cancel.json",
            "gracePeriodMs": 2000,
        },
        "inputRefs": [
            {
                "artifactRef": artifact_id,
                "kind": "capture-bundle",
                "contractVersion": "1.0.0",
                "manifestPath": manifest_path.relative_to(job_root).as_posix(),
                "expectedSha256": _sha256(manifest_payload),
            }
        ],
        "configSnapshot": config_snapshot,
        "payload": {},
    }
    request_path = attempt_root / "request.json"
    request_path.write_bytes(_json_bytes(request))
    return request, request_path


def _write_fake_mineru_script(path: Path) -> None:
    path.write_text(
        """
import json
import os
import zipfile
from pathlib import Path

_specs = []

def submit_local(paths, args, token):
    global _specs
    inherited = os.environ.get("MINERU_API_TOKEN", "")
    if not inherited or token != inherited:
        raise RuntimeError("process credential was not inherited")
    marker = os.environ.get("FAKE_CREDENTIAL_MARKER")
    if marker:
        Path(marker).write_text(json.dumps({"configured": True, "verified": True, "sourceType": "process-environment-inherited"}), encoding="utf-8")
    settings_log = os.environ.get("FAKE_MINERU_SETTINGS_LOG")
    if settings_log:
        Path(settings_log).write_text(json.dumps({
            "modelVersion": args.model_version,
            "language": args.language,
            "enableTable": args.enable_table,
            "enableFormula": args.enable_formula,
            "timeoutSeconds": args.timeout,
            "pollIntervalSeconds": args.poll_interval,
        }, sort_keys=True), encoding="utf-8")
    _specs = [{"data_id": "fake-%04d" % (index + 1), "file_name": Path(value).name} for index, value in enumerate(paths)]
    return "fake-batch-credential-001", _specs

def poll_batch(batch_id, args, token):
    return [{"data_id": item["data_id"], "state": "done", "full_zip_url": "fake://" + item["data_id"]} for item in _specs]

def download_file(url, path):
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("full.md", "# OFFLINE PROCESS-INHERITED FAKE OCR\\n")
""".lstrip(),
        encoding="utf-8",
    )


def _worker_command(request_path: Path) -> list[str]:
    if getattr(sys, "frozen", False):
        executable = Path(sys.executable).with_name("KnowledgeClipper.ImageOcr.Worker.exe")
        if not executable.is_file():
            raise ValueError("sibling KnowledgeClipper.ImageOcr.Worker.exe is required")
        return [str(executable), "run", "--request", str(request_path)]
    return [sys.executable, "-B", str(Path(__file__).with_name("image_ocr_worker.py")), "run", "--request", str(request_path)]


def _count_secret_occurrences(root: Path, secret: str, extra: list[str]) -> tuple[int, list[str]]:
    occurrences = 0
    scanned = []
    needle = secret.encode("utf-8")
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        payload = path.read_bytes()
        count = payload.count(needle)
        if count:
            occurrences += count
            scanned.append(path.relative_to(root).as_posix())
    for value in extra:
        occurrences += value.encode("utf-8").count(needle)
    return occurrences, scanned


def run_fake_provider_test(normalized_settings: dict[str, Any], credential_metadata: Any | None = None) -> dict[str, Any]:
    if normalized_settings["mode"] != "ocr":
        raise ValueError("offline Fake Provider test requires mode=ocr")
    from image_ocr_settings_adapter import normalize_credential_metadata

    if credential_metadata is None:
        raise ValueError("offline Fake Provider test requires value-free Desktop credential metadata")
    credential_gate = normalize_credential_metadata(credential_metadata)
    if not credential_gate["configured"] or not credential_gate["verified"]:
        raise ValueError("offline Fake Provider test requires configured=true and verified=true")
    config_snapshot: dict[str, Any] = {"providerSettings": normalized_settings["providerSettings"]}
    script_path = normalized_settings["deployment"].get("mineruScriptPath")
    if script_path is not None:
        config_snapshot["mineruScriptPath"] = script_path

    with tempfile.TemporaryDirectory(prefix="knowledge-clipper-image-ocr-offline-") as temporary:
        job_root = Path(temporary) / "jobs" / "t"
        provider_path = job_root / "diagnostics" / "fake_mineru.py"
        provider_path.parent.mkdir(parents=True)
        _write_fake_mineru_script(provider_path)
        config_snapshot["mineruScriptPath"] = str(provider_path.resolve())
        request, request_path = _build_formal_request(job_root, config_snapshot)
        marker_path = job_root / "diagnostics" / "credential-observed.json"
        settings_log = job_root / "diagnostics" / "provider-settings.json"
        environment = os.environ.copy()
        environment["MINERU_API_TOKEN"] = FAKE_PROCESS_TOKEN
        environment["FAKE_CREDENTIAL_MARKER"] = str(marker_path)
        environment["FAKE_MINERU_SETTINGS_LOG"] = str(settings_log)
        completed = subprocess.run(
            _worker_command(request_path.resolve()),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            env=environment,
            timeout=30,
            check=False,
        )
        result_path = job_root / request["resultPath"]
        result = json.loads(result_path.read_text(encoding="utf-8")) if result_path.is_file() else {}
        expected = normalized_settings["providerSettings"]
        observed = json.loads(settings_log.read_text(encoding="utf-8")) if settings_log.is_file() else None
        output_kinds = []
        for output in result.get("outputs", []):
            manifest = json.loads((job_root / output["manifestPath"]).read_text(encoding="utf-8"))
            output_kinds.append(manifest["kind"])
        original_ref = next((item for item in result.get("outputs", []) if item.get("kind") == "image.original"), None)
        provenance_ref = next((item for item in result.get("outputs", []) if item.get("kind") == "image.processing.provenance"), None)
        original_binding = False
        if original_ref is not None and provenance_ref is not None:
            original_manifest = json.loads((job_root / original_ref["manifestPath"]).read_text(encoding="utf-8"))
            provenance_manifest = json.loads((job_root / provenance_ref["manifestPath"]).read_text(encoding="utf-8"))
            provenance_payload = json.loads((job_root / provenance_manifest["files"][0]["path"]).read_text(encoding="utf-8"))
            representation = provenance_payload["items"][0]["representations"]["original"]
            original_file = original_manifest["files"][0]
            original_binding = (
                representation["artifactRef"] == original_ref["artifactRef"]
                and representation["sha256"] == original_file["sha256"]
                and representation["bytes"] == original_file["bytes"]
                and representation["mediaType"] == original_file["mediaType"]
                and provenance_payload["items"][0]["role"] == "question"
                and provenance_payload["sourceOrder"] == {
                    "questionImages": ["capture.image.question.0001"],
                    "answerImages": [],
                }
            )
        passed = (
            completed.returncode == 0
            and result.get("status") == "completed"
            and marker_path.is_file()
            and observed == expected
            and sorted(output_kinds) == ["image-ocr.markdown", "image-ocr.structured", "image.original", "image.processing.provenance"]
            and original_binding
        )
        secret_occurrences, leaked_files = _count_secret_occurrences(
            job_root,
            FAKE_PROCESS_TOKEN,
            [completed.stdout, completed.stderr],
        )
        passed = passed and secret_occurrences == 0
        return {
            "schemaVersion": TEST_RESULT_VERSION,
            "actionId": OFFLINE_FAKE_PROVIDER_ACTION_ID,
            "status": "passed" if passed else "failed",
            "worker": {"id": result.get("worker", {}).get("id"), "version": result.get("worker", {}).get("version")},
            "formalWorkerRequest": {
                "schemaVersion": request["schemaVersion"],
                "operation": request["operation"],
                "absoluteExistingRequestUsed": request_path.is_absolute() and request_path.is_file(),
                "configSnapshotProviderSettings": expected,
            },
            "credentialGate": {
                "credentialId": CANONICAL_CREDENTIAL_ID,
                "configured": credential_gate["configured"],
                "verified": credential_gate["verified"],
                "sourceType": credential_gate["sourceType"],
                "valueObservedByCompanion": False,
            },
            "processInheritance": {
                "workerStarted": completed.returncode == 0,
                "workerObservedInjectedCredential": marker_path.is_file(),
                "injectionBoundary": "parent-process-environment-to-worker-inheritance",
            },
            "fakeProvider": {
                "calls": [["capture.image.question.0001"]] if marker_path.is_file() else [],
                "observedProviderSettings": observed,
                "exactMatch": observed == expected,
            },
            "terminalResult": {
                "status": result.get("status"),
                "outputKinds": sorted(output_kinds),
                "itemDiagnostics": len(result.get("itemDiagnostics", [])),
                "provenanceOutput": provenance_ref,
                "originalOutput": original_ref,
                "originalBinding": {
                    "hashBytesMediaTypeRoleOrderMatch": original_binding,
                    "sameCompletedResult": original_ref is not None and provenance_ref is not None,
                },
            },
            "leakScan": {
                "taskPackageLogReceiptSecretOccurrences": secret_occurrences,
                "filesContainingSecret": leaked_files,
                "passed": secret_occurrences == 0,
            },
            "safety": {
                "networkAccess": "prohibited",
                "externalUpload": False,
                "mineruServiceCalled": False,
                "credentialResolved": False,
                "credentialValueSource": "synthetic-test-only-process-environment",
                "secretValuePersisted": False,
            },
        }
