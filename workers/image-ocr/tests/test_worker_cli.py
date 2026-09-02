from __future__ import annotations

import base64
import datetime as dt
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

import jsonschema


WORKER_ROOT = Path(__file__).resolve().parents[1]
WORKER_PATH = WORKER_ROOT / "image_ocr_worker.py"
PROJECT_ROOT = WORKER_ROOT.parents[1]
COMMON_SCHEMAS = PROJECT_ROOT / "contracts" / "v1" / "schemas" / "common"
PLANNED_EXE = WORKER_ROOT / "KnowledgeClipper.ImageOcr.Worker.exe"
SETTINGS_CANDIDATE_EXE = WORKER_ROOT / "candidates" / "1.2.0" / "KnowledgeClipper.ImageOcr.Worker.exe"
SETTINGS_CANDIDATE_RECEIPT = SETTINGS_CANDIDATE_EXE.parent / "build-receipt.json"
COMPANION_CANDIDATE_ROOT = WORKER_ROOT / "candidates" / "1.2.2"
COMPANION_WORKER_EXE = COMPANION_CANDIDATE_ROOT / "KnowledgeClipper.ImageOcr.Worker.exe"
COMPANION_SETTINGS_EXE = COMPANION_CANDIDATE_ROOT / "KnowledgeClipper.ImageOcr.Settings.exe"
COMPANION_BUILD_RECEIPT = COMPANION_CANDIDATE_ROOT / "build-receipt.json"
COMPANION_DESCRIPTOR = COMPANION_CANDIDATE_ROOT / "image-ocr-capability-config-descriptor.v4.json"
LEGACY_COMPATIBILITY_EXE = WORKER_ROOT / "RemNoteQuickClipper.ImageOcr.Worker.exe"
PNG_1X1 = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zt9sAAAAASUVORK5CYII="
)


def json_bytes(value):
    return (json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2) + "\n").encode("utf-8")


def native_path(path):
    resolved = str(path.resolve())
    if os.name == "nt" and not resolved.startswith("\\\\?\\"):
        return "\\\\?\\" + resolved
    return resolved


class WorkerCliTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.base = Path(self.temp.name)
        self.long_path_cleanup = []
        self.fake_provider = self.base / "fake_mineru_provider.py"
        self.fake_provider.write_text(
            """
import json
import os
import time
import zipfile
from pathlib import Path

_specs = []

def submit_local(paths, args, token):
    global _specs
    marker = os.environ.get("FAKE_MINERU_PHASE_MARKER")
    if marker:
        Path(marker).write_text("submit", encoding="utf-8")
    time.sleep(float(os.environ.get("FAKE_MINERU_SUBMIT_SECONDS", "0")))
    data_id_length = int(os.environ.get("FAKE_MINERU_DATA_ID_LENGTH", "0"))
    _specs = [
        {
            "data_id": (("d" * data_id_length) if data_id_length else ("fake-%04d" % (index + 1))),
            "file_name": Path(path).name,
        }
        for index, path in enumerate(paths)
    ]
    call_log = os.environ.get("FAKE_MINERU_CALL_LOG")
    if call_log:
        with open(call_log, "a", encoding="utf-8") as handle:
            handle.write("|".join(item["file_name"] for item in _specs) + "\\n")
    settings_log = os.environ.get("FAKE_MINERU_SETTINGS_LOG")
    if settings_log:
        Path(settings_log).write_text(json.dumps({
            "modelVersion": args.model_version,
            "language": args.language,
            "enableTable": args.enable_table,
            "enableFormula": args.enable_formula,
            "timeoutSeconds": args.timeout,
            "pollIntervalSeconds": args.poll_interval,
        }, ensure_ascii=False, sort_keys=True), encoding="utf-8")
    if os.environ.get("FAKE_MINERU_SUBMIT_UNKNOWN") == "1":
        raise RuntimeError("injected ambiguous submission")
    return "fake-batch-001", _specs

def poll_batch(batch_id, args, token):
    marker = os.environ.get("FAKE_MINERU_PHASE_MARKER")
    if marker:
        Path(marker).write_text("poll", encoding="utf-8")
    time.sleep(float(os.environ.get("FAKE_MINERU_POLL_SECONDS", "0")))
    failed = {value for value in os.environ.get("FAKE_MINERU_FAIL_DATA_IDS", "").split(",") if value}
    return [
        (
            {"data_id": item["data_id"], "state": "failed", "err_msg": "injected CLI failure"}
            if item["data_id"] in failed
            else {"data_id": item["data_id"], "state": "done", "full_zip_url": "fake://" + item["data_id"]}
        )
        for item in reversed(_specs)
    ]

def download_file(url, path):
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("full.md", "# FAKE OCR CLI CANARY\\n")
""".lstrip(),
            encoding="utf-8",
        )

    def tearDown(self):
        for path in self.long_path_cleanup:
            native = native_path(path)
            if os.path.isdir(native):
                shutil.rmtree(native)
        for attempt in range(10):
            try:
                self.temp.cleanup()
                break
            except OSError as exc:
                if getattr(exc, "winerror", None) != 145 or attempt == 9:
                    raise
                time.sleep(0.1 * (attempt + 1))

    def command(self, *args, exe=False):
        if exe:
            return [str(PLANNED_EXE), *args]
        return [sys.executable, "-B", str(WORKER_PATH), *args]

    def env(self, *, poll_seconds=0, submit_seconds=0, phase_marker=None, data_id_length=0, fail_data_ids=None, call_log=None, submit_unknown=False):
        value = os.environ.copy()
        value["PYTHONDONTWRITEBYTECODE"] = "1"
        value["MINERU_API_TOKEN"] = "FAKE_CLI_TOKEN_NOT_A_SECRET"
        value["FAKE_MINERU_POLL_SECONDS"] = str(poll_seconds)
        value["FAKE_MINERU_SUBMIT_SECONDS"] = str(submit_seconds)
        value["FAKE_MINERU_DATA_ID_LENGTH"] = str(data_id_length)
        value["FAKE_MINERU_FAIL_DATA_IDS"] = ",".join(fail_data_ids or [])
        value["FAKE_MINERU_SUBMIT_UNKNOWN"] = "1" if submit_unknown else "0"
        if phase_marker is not None:
            value["FAKE_MINERU_PHASE_MARKER"] = str(phase_marker)
        if call_log is not None:
            value["FAKE_MINERU_CALL_LOG"] = str(call_log)
        return value

    def validate_common(self, value, schema_name):
        schema = json.loads((COMMON_SCHEMAS / schema_name).read_text(encoding="utf-8"))
        resolver = jsonschema.RefResolver(base_uri=COMMON_SCHEMAS.as_uri() + "/", referrer=schema)
        jsonschema.Draft202012Validator(
            schema,
            resolver=resolver,
            format_checker=jsonschema.Draft202012Validator.FORMAT_CHECKER,
        ).validate(value)

    def make_job(self, *, mode="original", suffix="001", include_answer=False):
        task_id = f"task-image-cli-{suffix}"
        run_id = f"run-image-cli-{suffix}"
        attempt_id = f"attempt-{suffix}"
        job = self.base / "jobs" / task_id
        inputs = job / "inputs"
        image_dir = inputs / "images"
        image_dir.mkdir(parents=True)
        image_path = image_dir / "question.png"
        image_path.write_bytes(PNG_1X1)
        image_ref = "capture.image.question.0001"
        question_descriptor = {
            "artifactRef": image_ref,
            "relativePath": image_path.relative_to(job).as_posix(),
            "sha256": hashlib.sha256(PNG_1X1).hexdigest(),
            "bytes": len(PNG_1X1),
            "mediaType": "image/png",
            "encoding": None,
        }
        answer_ref = "capture.image.answer.0001"
        answer_descriptors = []
        if include_answer:
            answer_path = image_dir / "answer.png"
            answer_path.write_bytes(PNG_1X1)
            answer_descriptors.append(
                {
                    "artifactRef": answer_ref,
                    "relativePath": answer_path.relative_to(job).as_posix(),
                    "sha256": hashlib.sha256(PNG_1X1).hexdigest(),
                    "bytes": len(PNG_1X1),
                    "mediaType": "image/png",
                    "encoding": None,
                }
            )
        capture = {
            "schemaVersion": "capture-bundle.v1",
            "captureId": f"capture-cli-{suffix}",
            "createdAt": "2026-08-25T12:00:00Z",
            "intent": "screenshot",
            "source": "screenshot",
            "modeSnapshot": {
                "workflow": "card",
                "contextEnabled": False,
                "contextGranularity": "sentence",
                "contextApplied": False,
                "imageHandling": mode,
            },
            "artifacts": answer_descriptors + [question_descriptor],
            "selectedTextArtifactRef": None,
            "contextSegments": None,
            "questionImages": [image_ref],
            "answerImages": [answer_ref] if include_answer else [],
            "questionTurns": [],
        }
        capture_path = inputs / "capture-bundle.v1.json"
        capture_path.write_bytes(json_bytes(capture))
        capture_file = {
            "path": capture_path.relative_to(job).as_posix(),
            "sha256": hashlib.sha256(capture_path.read_bytes()).hexdigest(),
            "bytes": capture_path.stat().st_size,
            "mediaType": "application/vnd.rqc.capture-bundle+json",
        }
        artifact_id = f"capture.bundle.cli.{suffix}"
        manifest = {
            "schemaVersion": "1.0.0",
            "artifactId": artifact_id,
            "kind": "capture-bundle",
            "contractVersion": "1.0.0",
            "mediaType": "application/vnd.rqc.capture-bundle+json",
            "files": [capture_file],
            "createdAt": "2026-08-25T12:00:00Z",
            "producer": {
                "stageId": "capture",
                "workerId": "desktop-capture",
                "workerVersion": "1.0.0",
                "attemptId": f"capture-{suffix}",
            },
            "inputRefs": [],
            "dataClass": "sensitive",
            "metadata": {"entrypoint": capture_file["path"]},
        }
        manifest_path = job / "artifacts" / artifact_id / "artifact.json"
        manifest_path.parent.mkdir(parents=True)
        manifest_path.write_bytes(json_bytes(manifest))
        capture_ref = {
            "artifactRef": artifact_id,
            "kind": "capture-bundle",
            "contractVersion": "1.0.0",
            "manifestPath": manifest_path.relative_to(job).as_posix(),
            "expectedSha256": hashlib.sha256(manifest_path.read_bytes()).hexdigest(),
        }
        attempt = f"runs/{run_id}/stages/image-ocr/attempts/{attempt_id}"
        attempt_root = job / attempt
        attempt_root.mkdir(parents=True)
        operation_id = f"operation-image-cli-{suffix}"
        request = {
            "schemaVersion": "1.0.0",
            "requestId": f"request-image-cli-{suffix}",
            "operationId": operation_id,
            "taskId": task_id,
            "runId": run_id,
            "stageId": "image-ocr",
            "attemptId": attempt_id,
            "workerId": "image.ocr.mineru",
            "operation": "process-images",
            "idempotencyKey": hashlib.sha256(f"{operation_id}:{attempt_id}".encode()).hexdigest(),
            "attemptPath": attempt,
            "outputPath": f"{attempt}/outputs",
            "progressPath": f"{attempt}/progress.ndjson",
            "resultPath": f"{attempt}/result.json",
            "cancelSignal": {"schemaVersion": "1.0.0", "path": f"{attempt}/cancel.json", "gracePeriodMs": 2000},
            "inputRefs": [capture_ref],
            "configSnapshot": {
                "mineruScriptPath": str(self.fake_provider.resolve()),
                "providerSettings": {"pollIntervalSeconds": 1, "timeoutSeconds": 30},
            },
            "payload": {},
        }
        request_path = attempt_root / "request.json"
        request_path.write_bytes(json_bytes(request))
        return job, request, request_path, attempt_root

    def make_retry_request(self, job, prior_request, prior_module_result, suffix):
        attempt_id = f"attempt-{suffix}"
        attempt = f"runs/{prior_request['runId']}/stages/image-ocr/attempts/{attempt_id}"
        attempt_root = job / attempt
        attempt_root.mkdir(parents=True)
        request = dict(prior_request)
        request.update(
            {
                "requestId": f"request-image-cli-{suffix}",
                "attemptId": attempt_id,
                "idempotencyKey": hashlib.sha256(f"{prior_request['operationId']}:{attempt_id}".encode()).hexdigest(),
                "attemptPath": attempt,
                "outputPath": f"{attempt}/outputs",
                "progressPath": f"{attempt}/progress.ndjson",
                "resultPath": f"{attempt}/result.json",
                "cancelSignal": {"schemaVersion": "1.0.0", "path": f"{attempt}/cancel.json", "gracePeriodMs": 2000},
                "payload": {"retryModuleResultPath": prior_module_result},
            }
        )
        request_path = attempt_root / "request.json"
        request_path.write_bytes(json_bytes(request))
        return request, request_path, attempt_root

    def assert_progress_stdout(self, stdout):
        lines = [line for line in stdout.splitlines() if line.strip()]
        self.assertGreaterEqual(len(lines), 1)
        records = [json.loads(line) for line in lines]
        self.assertTrue(all(set(record) == {"timestampUtc", "state", "message"} for record in records))
        return records

    def wait_for_file(self, path, process, message, timeout=5):
        deadline = time.monotonic() + timeout
        while not path.exists() and process.poll() is None and time.monotonic() < deadline:
            time.sleep(0.01)
        self.assertTrue(path.exists(), message)

    def publish_cancel(self, request, attempt_root, reason="task_cancel"):
        requested_at = dt.datetime.now(dt.timezone.utc)
        signal = {
            "schemaVersion": "1.0.0",
            "attemptId": request["attemptId"],
            "operationId": request["operationId"],
            "reason": reason,
            "requestedAt": requested_at.isoformat().replace("+00:00", "Z"),
            "graceDeadline": (requested_at + dt.timedelta(milliseconds=2000)).isoformat().replace("+00:00", "Z"),
        }
        signal_path = attempt_root / "cancel.json"
        temporary = attempt_root / ".cancel.tmp"
        temporary.write_bytes(json_bytes(signal))
        started = time.monotonic()
        temporary.replace(signal_path)
        return started

    def assert_cancelled_process(self, process, attempt_root, started, reason="task_cancel"):
        stdout, stderr = process.communicate(timeout=5)
        elapsed = time.monotonic() - started
        self.assertLess(elapsed, 2.0, f"{reason} took {elapsed:.3f}s")
        self.assertEqual("", stderr)
        self.assertEqual("cancelled", self.assert_progress_stdout(stdout)[-1]["state"])
        result = json.loads((attempt_root / "result.json").read_text(encoding="utf-8"))
        self.validate_common(result, "worker-result.schema.json")
        self.assertEqual("cancelled", result["status"])
        self.assertEqual([], result["outputs"])
        self.assertEqual(reason, result["payload"]["cancellationReason"])
        self.assertEqual(0, process.returncode)
        return result

    def exercise_submission_cancel(self, *, exe, suffix):
        _, request, request_path, attempt_root = self.make_job(mode="ocr", suffix=suffix)
        marker = attempt_root / "provider-phase.txt"
        process = subprocess.Popen(
            self.command("run", "--request", str(request_path.resolve()), exe=exe),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            env=self.env(submit_seconds=10, phase_marker=marker),
        )
        self.wait_for_file(marker, process, "provider did not enter submission")
        self.assertEqual("submit", marker.read_text(encoding="utf-8"))
        started = self.publish_cancel(request, attempt_root)
        self.assert_cancelled_process(process, attempt_root, started)
        self.assertFalse((attempt_root / "submission.json").exists())

    def exercise_resume_cancel(self, *, exe, suffix):
        job, request, request_path, attempt_root = self.make_job(mode="ocr", suffix=suffix)
        resume_path = job / "inputs" / "resume-submission.json"
        resume_path.write_bytes(
            json_bytes(
                {
                    "schemaVersion": 1,
                    "provider": "mineru",
                    "batchId": "fake-resume-batch-001",
                    "submittedAt": "2026-08-25T12:00:00Z",
                    "requestId": "prior-request",
                    "operationId": request["operationId"],
                    "items": [{"artifactRef": "capture.image.question.0001", "dataId": "fake-0001"}],
                }
            )
        )
        request["payload"]["resumeSubmissionPath"] = resume_path.relative_to(job).as_posix()
        request_path.write_bytes(json_bytes(request))
        marker = attempt_root / "provider-phase.txt"
        process = subprocess.Popen(
            self.command("run", "--request", str(request_path.resolve()), exe=exe),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            env=self.env(poll_seconds=10, phase_marker=marker),
        )
        resume_receipt = attempt_root / "resume.json"
        self.wait_for_file(resume_receipt, process, "worker did not publish resume receipt")
        self.wait_for_file(marker, process, "provider did not enter resume poll")
        self.assertEqual("poll", marker.read_text(encoding="utf-8"))
        started = self.publish_cancel(request, attempt_root)
        self.assert_cancelled_process(process, attempt_root, started)

    def test_describe_is_single_schema_valid_json_with_exact_identity(self):
        process = subprocess.run(self.command("describe", "--json"), capture_output=True, text=True, env=self.env(), timeout=10)
        self.assertEqual(0, process.returncode, process.stderr)
        self.assertEqual("", process.stderr)
        self.assertEqual(1, len(process.stdout.splitlines()))
        describe = json.loads(process.stdout)
        self.validate_common(describe, "worker-describe.schema.json")
        self.assertEqual("image.ocr.mineru", describe["workerId"])
        self.assertEqual("1.2.4", describe["workerVersion"])
        self.assertNotIn("remnote", json.dumps(describe).lower())
        self.assertNotIn("anki", json.dumps(describe).lower())

    def test_candidate_entrypoint_is_knowledge_clipper_and_legacy_alias_is_compatibility_only(self):
        self.assertEqual("KnowledgeClipper.ImageOcr.Worker.exe", PLANNED_EXE.name)
        self.assertNotIn("remnote", PLANNED_EXE.name.lower())
        self.assertNotIn("anki", PLANNED_EXE.name.lower())
        self.assertTrue(LEGACY_COMPATIBILITY_EXE.is_file(), "legacy compatibility alias is missing")
        legacy = subprocess.run(
            [str(LEGACY_COMPATIBILITY_EXE), "describe", "--json"],
            capture_output=True,
            text=True,
            env=self.env(),
            timeout=15,
        )
        self.assertEqual(0, legacy.returncode, legacy.stderr)
        self.assertEqual("image.ocr.mineru", json.loads(legacy.stdout)["workerId"])

    def test_neutral_provider_environment_variable_is_supported(self):
        _, request, request_path, attempt_root = self.make_job(mode="ocr", suffix="neutral-provider-env")
        request["configSnapshot"].pop("mineruScriptPath")
        request_path.write_bytes(json_bytes(request))
        environment = self.env()
        environment["KNOWLEDGE_CLIPPER_MINERU_SCRIPT"] = str(self.fake_provider.resolve())
        environment.pop("RQC_MINERU_SCRIPT", None)
        process = subprocess.run(
            self.command("run", "--request", str(request_path.resolve())),
            capture_output=True,
            text=True,
            env=environment,
            timeout=20,
        )
        self.assertEqual(0, process.returncode, process.stderr)
        result = json.loads((attempt_root / "result.json").read_text(encoding="utf-8"))
        self.assertEqual("completed", result["status"])

    def test_absolute_request_original_mode_success_and_stdout_discipline(self):
        _, request, request_path, attempt_root = self.make_job(mode="original", suffix="original")
        process = subprocess.run(self.command("run", "--request", str(request_path.resolve())), capture_output=True, text=True, env=self.env(), timeout=15)
        self.assertEqual(0, process.returncode, process.stderr)
        self.assertEqual("", process.stderr)
        records = self.assert_progress_stdout(process.stdout)
        self.assertEqual(["starting", "completed"], [record["state"] for record in records])
        result = json.loads((attempt_root / "result.json").read_text(encoding="utf-8"))
        self.validate_common(result, "worker-result.schema.json")
        self.assertEqual("image.ocr.mineru", result["worker"]["id"])
        self.assertEqual(3, len(result["outputs"]))
        self.assertEqual(
            ["image.original", "image.original.bundle", "image.processing.provenance"],
            sorted(item["kind"] for item in result["outputs"]),
        )
        self.assertFalse(any(attempt_root.glob(".result.json.*.tmp")))
        original_hash = hashlib.sha256((attempt_root / "result.json").read_bytes()).hexdigest()
        second = subprocess.run(self.command("run", "--request", str(request_path.resolve())), capture_output=True, text=True, env=self.env(), timeout=15)
        self.assertNotEqual(0, second.returncode)
        self.assertEqual(original_hash, hashlib.sha256((attempt_root / "result.json").read_bytes()).hexdigest())
        self.assertEqual(request["resultPath"], result["error"]["detailsRef"] if result["error"] else request["resultPath"])

    def test_relative_request_is_rejected_without_stdout(self):
        _, _, request_path, _ = self.make_job(mode="original", suffix="relative")
        process = subprocess.run(
            self.command("run", "--request", "request.json"),
            cwd=request_path.parent,
            capture_output=True,
            text=True,
            env=self.env(),
            timeout=10,
        )
        self.assertEqual(2, process.returncode)
        self.assertEqual("", process.stdout)
        diagnostic = json.loads(process.stderr)
        self.assertEqual("request_path_not_absolute", diagnostic["error"]["code"])
        self.assertFalse((request_path.parent / "result.json").exists())

    def test_fake_provider_cli_canary(self):
        _, _, request_path, attempt_root = self.make_job(mode="ocr", suffix="fake")
        process = subprocess.run(self.command("run", "--request", str(request_path.resolve())), capture_output=True, text=True, env=self.env(), timeout=20)
        self.assertEqual(0, process.returncode, process.stderr)
        self.assertEqual("", process.stderr)
        records = self.assert_progress_stdout(process.stdout)
        self.assertEqual("completed", records[-1]["state"])
        result = json.loads((attempt_root / "result.json").read_text(encoding="utf-8"))
        self.validate_common(result, "worker-result.schema.json")
        self.assertEqual("completed", result["status"])
        self.assertEqual(4, len(result["outputs"]))
        self.assertEqual(
            ["image-ocr.markdown", "image-ocr.structured", "image.original", "image.processing.provenance"],
            sorted(item["kind"] for item in result["outputs"]),
        )

    def test_provider_all_items_failed_publishes_empty_common_outputs_with_item_diagnostics(self):
        _, _, request_path, attempt_root = self.make_job(
            mode="ocr",
            suffix="provider-all-failed",
            include_answer=True,
        )
        call_log = self.base / "provider-all-failed-calls.txt"
        process = subprocess.run(
            self.command("run", "--request", str(request_path.resolve())),
            capture_output=True,
            text=True,
            env=self.env(
                fail_data_ids=["fake-0001", "fake-0002"],
                call_log=call_log,
            ),
            timeout=20,
        )
        self.assertEqual(1, process.returncode)
        self.assertEqual("", process.stderr)
        self.assertEqual(["starting", "failed"], [item["state"] for item in self.assert_progress_stdout(process.stdout)])
        result = json.loads((attempt_root / "result.json").read_text(encoding="utf-8"))
        self.validate_common(result, "worker-result.schema.json")
        self.assertEqual("failed", result["status"])
        self.assertEqual([], result["outputs"])
        self.assertEqual("all_items_failed", result["error"]["code"])
        self.assertEqual(
            {
                "capture.image.question.0001": "failed",
                "capture.image.answer.0001": "failed",
            },
            {item["itemId"]: item["status"] for item in result["itemDiagnostics"]},
        )
        self.assertEqual(["question.png|answer.png"], call_log.read_text(encoding="utf-8").splitlines())

    def test_tampered_image_fails_module_integrity_before_provider_call(self):
        job, _, request_path, attempt_root = self.make_job(mode="ocr", suffix="tampered-image")
        capture = json.loads((job / "inputs" / "capture-bundle.v1.json").read_text(encoding="utf-8"))
        image_path = job / capture["artifacts"][0]["relativePath"]
        image_path.write_bytes(image_path.read_bytes() + b"\x00")
        call_log = self.base / "tampered-image-provider-calls.txt"
        process = subprocess.run(
            self.command("run", "--request", str(request_path.resolve())),
            capture_output=True,
            text=True,
            env=self.env(call_log=call_log),
            timeout=20,
        )
        self.assertEqual(1, process.returncode)
        self.assertEqual("", process.stderr)
        self.assertEqual(["starting", "failed"], [item["state"] for item in self.assert_progress_stdout(process.stdout)])
        result = json.loads((attempt_root / "result.json").read_text(encoding="utf-8"))
        module_result = json.loads((attempt_root / "module-result.json").read_text(encoding="utf-8"))
        self.validate_common(result, "worker-result.schema.json")
        self.assertEqual("failed", result["status"])
        self.assertEqual([], result["outputs"])
        self.assertEqual([], result["itemDiagnostics"])
        self.assertEqual("integrity_mismatch", result["error"]["code"])
        self.assertEqual("integrity_mismatch", module_result["error"]["code"])
        self.assertFalse(call_log.exists())

    def test_ambiguous_submission_publishes_non_retryable_unknown_external_state(self):
        _, _, request_path, attempt_root = self.make_job(
            mode="ocr",
            suffix="unknown-external-state",
            include_answer=True,
        )
        call_log = self.base / "unknown-external-state-provider-calls.txt"
        process = subprocess.run(
            self.command("run", "--request", str(request_path.resolve())),
            capture_output=True,
            text=True,
            env=self.env(call_log=call_log, submit_unknown=True),
            timeout=20,
        )
        self.assertEqual(1, process.returncode)
        self.assertEqual("", process.stderr)
        self.assertEqual(
            ["starting", "unknown_external_state"],
            [item["state"] for item in self.assert_progress_stdout(process.stdout)],
        )
        result = json.loads((attempt_root / "result.json").read_text(encoding="utf-8"))
        self.validate_common(result, "worker-result.schema.json")
        self.assertEqual("unknown_external_state", result["status"])
        self.assertEqual("provider_submission_unknown", result["error"]["code"])
        self.assertEqual("effect", result["error"]["category"])
        self.assertFalse(result["error"]["retryable"])
        self.assertEqual([], result["outputs"])
        self.assertEqual([], result["itemDiagnostics"])
        self.assertFalse((attempt_root / "submission.json").exists())
        self.assertEqual(["question.png|answer.png"], call_log.read_text(encoding="utf-8").splitlines())

    def test_cli_partial_retry_only_calls_failed_answer_and_reuses_question(self):
        job, first_request, first_request_path, first_attempt_root = self.make_job(
            mode="ocr",
            suffix="partial-retry-first",
            include_answer=True,
        )
        call_log = self.base / "partial-retry-provider-calls.txt"
        first_process = subprocess.run(
            self.command("run", "--request", str(first_request_path.resolve())),
            capture_output=True,
            text=True,
            env=self.env(fail_data_ids=["fake-0002"], call_log=call_log),
            timeout=20,
        )
        self.assertEqual(1, first_process.returncode)
        self.assertEqual("", first_process.stderr)
        self.assertEqual(["starting", "partial"], [item["state"] for item in self.assert_progress_stdout(first_process.stdout)])
        first_result = json.loads((first_attempt_root / "result.json").read_text(encoding="utf-8"))
        self.validate_common(first_result, "worker-result.schema.json")
        self.assertEqual("partial", first_result["status"])
        self.assertTrue(first_result["payload"]["partialBlocksDownstream"])
        self.assertEqual(5, len(first_result["outputs"]))
        self.assertEqual(
            {"capture.image.question.0001": "completed", "capture.image.answer.0001": "failed"},
            {item["itemId"]: item["status"] for item in first_result["itemDiagnostics"]},
        )

        prior_module = f"{first_request['attemptPath']}/module-result.json"
        _, retry_request_path, retry_attempt_root = self.make_retry_request(
            job,
            first_request,
            prior_module,
            "partial-retry-second",
        )
        retry_process = subprocess.run(
            self.command("run", "--request", str(retry_request_path.resolve())),
            capture_output=True,
            text=True,
            env=self.env(call_log=call_log),
            timeout=20,
        )
        self.assertEqual(0, retry_process.returncode, retry_process.stderr)
        self.assertEqual("", retry_process.stderr)
        self.assertEqual("completed", self.assert_progress_stdout(retry_process.stdout)[-1]["state"])
        retry_result = json.loads((retry_attempt_root / "result.json").read_text(encoding="utf-8"))
        self.validate_common(retry_result, "worker-result.schema.json")
        self.assertEqual("completed", retry_result["status"])
        self.assertEqual(7, len(retry_result["outputs"]))
        retry_module = json.loads((retry_attempt_root / "module-result.json").read_text(encoding="utf-8"))
        reused = [item for item in retry_module["details"]["items"] if item.get("reusedFromAttempt")]
        self.assertEqual(["capture.image.question.0001"], [item["artifactRef"] for item in reused])
        self.assertEqual(first_request["attemptId"], reused[0]["reusedFromAttempt"])
        self.assertEqual(
            ["question.png|answer.png", "answer.png"],
            call_log.read_text(encoding="utf-8").splitlines(),
        )

    def test_long_windows_job_path_provider_download_succeeds(self):
        _, _, request_path, attempt_root = self.make_job(mode="ocr", suffix="long-download")
        self.long_path_cleanup.append(attempt_root / "raw")
        projected_zip = attempt_root / "raw" / "mineru" / ("d" * 128) / "mineru-result.zip"
        self.assertGreater(len(str(projected_zip.resolve())), 260)
        process = subprocess.run(
            self.command("run", "--request", str(request_path.resolve())),
            capture_output=True,
            text=True,
            env=self.env(data_id_length=128),
            timeout=30,
        )
        self.assertEqual(0, process.returncode, process.stderr)
        self.assertEqual("", process.stderr)
        with open(native_path(attempt_root / "result.json"), encoding="utf-8") as handle:
            result = json.load(handle)
        self.validate_common(result, "worker-result.schema.json")
        self.assertEqual("completed", result["status"])
        self.assertEqual(4, len(result["outputs"]))

    def test_preflight_integrity_failure_publishes_schema_valid_terminal_result(self):
        job, _, request_path, attempt_root = self.make_job(mode="ocr", suffix="preflight-integrity")
        request = json.loads(request_path.read_text(encoding="utf-8"))
        manifest_path = job / request["inputRefs"][0]["manifestPath"]
        manifest_path.write_bytes(manifest_path.read_bytes() + b" ")
        process = subprocess.run(
            self.command("run", "--request", str(request_path.resolve())),
            capture_output=True,
            text=True,
            env=self.env(),
            timeout=15,
        )
        self.assertEqual(1, process.returncode)
        self.assertEqual("", process.stderr)
        self.assertEqual(["starting", "failed"], [item["state"] for item in self.assert_progress_stdout(process.stdout)])
        result = json.loads((attempt_root / "result.json").read_text(encoding="utf-8"))
        self.validate_common(result, "worker-result.schema.json")
        self.assertEqual("failed", result["status"])
        self.assertEqual("integrity_mismatch", result["error"]["code"])
        self.assertEqual([], result["outputs"])
        self.assertEqual("preflight", result["payload"]["phase"])

    def test_explicit_missing_provider_publishes_preflight_failure_without_fallback(self):
        _, request, request_path, attempt_root = self.make_job(mode="ocr", suffix="preflight-provider")
        request["configSnapshot"]["mineruScriptPath"] = str((self.base / "missing-provider.py").resolve())
        request_path.write_bytes(json_bytes(request))
        process = subprocess.run(
            self.command("run", "--request", str(request_path.resolve())),
            capture_output=True,
            text=True,
            env=self.env(),
            timeout=15,
        )
        self.assertEqual(1, process.returncode)
        self.assertEqual("", process.stderr)
        self.assertEqual("failed", self.assert_progress_stdout(process.stdout)[-1]["state"])
        result = json.loads((attempt_root / "result.json").read_text(encoding="utf-8"))
        self.validate_common(result, "worker-result.schema.json")
        self.assertEqual("provider_unavailable", result["error"]["code"])
        self.assertEqual([], result["outputs"])
        self.assertEqual("preflight", result["payload"]["phase"])

    def test_planned_exe_describe_and_fake_provider_canary(self):
        self.assertTrue(PLANNED_EXE.is_file(), "planned worker EXE is missing")
        describe_process = subprocess.run(
            self.command("describe", "--json", exe=True),
            capture_output=True,
            text=True,
            env=self.env(),
            timeout=15,
        )
        self.assertEqual(0, describe_process.returncode, describe_process.stderr)
        self.assertEqual("", describe_process.stderr)
        describe = json.loads(describe_process.stdout)
        self.validate_common(describe, "worker-describe.schema.json")
        self.assertEqual("image.ocr.mineru", describe["workerId"])
        self.assertEqual("1.1.0", describe["workerVersion"])

        _, _, request_path, attempt_root = self.make_job(mode="ocr", suffix="exe-fake")
        process = subprocess.run(
            self.command("run", "--request", str(request_path.resolve()), exe=True),
            capture_output=True,
            text=True,
            env=self.env(),
            timeout=30,
        )
        self.assertEqual(0, process.returncode, process.stderr)
        self.assertEqual("", process.stderr)
        self.assertEqual("completed", self.assert_progress_stdout(process.stdout)[-1]["state"])
        result = json.loads((attempt_root / "result.json").read_text(encoding="utf-8"))
        self.validate_common(result, "worker-result.schema.json")
        self.assertEqual("image.ocr.mineru", result["worker"]["id"])
        self.assertNotIn("remnote", json.dumps(result).lower())
        self.assertNotIn("anki", json.dumps(result).lower())
        self.assertEqual(2, len(result["outputs"]))
        for output in result["outputs"]:
            manifest = json.loads((self.base / "jobs" / result["taskId"] / output["manifestPath"]).read_text(encoding="utf-8"))
            self.assertEqual("image.ocr.mineru", manifest["producer"]["workerId"])
            self.assertNotIn("remnote", json.dumps(manifest).lower())
            self.assertNotIn("anki", json.dumps(manifest).lower())

    def test_settings_candidate_describe_and_non_default_settings_reach_fake_provider(self):
        self.assertTrue(SETTINGS_CANDIDATE_EXE.is_file(), "versioned settings candidate EXE is missing")
        receipt = json.loads(SETTINGS_CANDIDATE_RECEIPT.read_text(encoding="utf-8"))
        self.assertEqual(SETTINGS_CANDIDATE_EXE.stat().st_size, receipt["bytes"])
        self.assertEqual(hashlib.sha256(SETTINGS_CANDIDATE_EXE.read_bytes()).hexdigest(), receipt["sha256"])
        self.assertEqual("NO-GO-until-new-authority-checkpoint", receipt["registryOnboarding"])
        self.assertFalse(receipt["externalServiceCalled"])
        describe = subprocess.run(
            [str(SETTINGS_CANDIDATE_EXE), "describe", "--json"],
            capture_output=True,
            text=True,
            env=self.env(),
            timeout=15,
        )
        self.assertEqual(0, describe.returncode, describe.stderr)
        self.assertEqual("1.2.0", json.loads(describe.stdout)["workerVersion"])
        _, request, request_path, attempt_root = self.make_job(mode="ocr", suffix="settings-candidate-effective")
        self.long_path_cleanup.append(attempt_root / "outputs")
        expected = {
            "modelVersion": "pipeline",
            "language": "en",
            "enableTable": False,
            "enableFormula": True,
            "timeoutSeconds": 120,
            "pollIntervalSeconds": 3,
        }
        request["configSnapshot"]["providerSettings"] = expected
        request_path.write_bytes(json_bytes(request))
        settings_log = self.base / "settings-candidate-provider-settings.json"
        environment = self.env()
        environment["FAKE_MINERU_SETTINGS_LOG"] = str(settings_log)
        process = subprocess.run(
            [str(SETTINGS_CANDIDATE_EXE), "run", "--request", str(request_path.resolve())],
            capture_output=True,
            text=True,
            env=environment,
            timeout=30,
        )
        self.assertEqual(0, process.returncode, process.stderr)
        self.assertEqual(expected, json.loads(settings_log.read_text(encoding="utf-8")))
        result = json.loads((attempt_root / "result.json").read_text(encoding="utf-8"))
        self.assertEqual("completed", result["status"])
        self.assertEqual("1.2.0", result["worker"]["version"])

    def test_companion_candidate_release_root_and_formal_worker_request_are_offline_admitted(self):
        receipt = json.loads(COMPANION_BUILD_RECEIPT.read_text(encoding="utf-8"))
        self.assertEqual("1.2.2", receipt["worker"]["version"])
        self.assertEqual("1.2.0", receipt["companion"]["version"])
        self.assertEqual(12, receipt["fileCount"])
        self.assertIsInstance(receipt["totalBytes"], int)
        self.assertGreater(receipt["totalBytes"], 0)
        rows = []
        total = 0
        for item in receipt["files"]:
            path = COMPANION_CANDIDATE_ROOT / item["path"]
            self.assertTrue(path.is_file(), item["path"])
            self.assertEqual(item["bytes"], path.stat().st_size)
            self.assertEqual(item["sha256"], hashlib.sha256(path.read_bytes()).hexdigest())
            total += item["bytes"]
            rows.append(f"{item['path']}\0{item['sha256']}\0{item['bytes']}\n")
        self.assertEqual(receipt["totalBytes"], total)
        self.assertEqual(receipt["treeSha256"], hashlib.sha256("".join(rows).encode("utf-8")).hexdigest())
        self.assertEqual(0, receipt["safety"]["externalUploads"])

        worker_describe = subprocess.run([str(COMPANION_WORKER_EXE), "describe", "--json"], capture_output=True, text=True, timeout=15)
        self.assertEqual(0, worker_describe.returncode, worker_describe.stderr)
        self.assertEqual("1.2.2", json.loads(worker_describe.stdout)["workerVersion"])

        settings_describe = subprocess.run([str(COMPANION_SETTINGS_EXE), "describe", "--json"], capture_output=True, text=True, timeout=15)
        self.assertEqual(0, settings_describe.returncode, settings_describe.stderr)
        self.assertEqual(json.loads(COMPANION_DESCRIPTOR.read_text(encoding="utf-8")), json.loads(settings_describe.stdout))
        settings_fixture = WORKER_ROOT / "fixtures" / "settings-ocr.v1.json"
        validate = subprocess.run(
            [str(COMPANION_SETTINGS_EXE), "validate", "--settings", str(settings_fixture.resolve()), "--json"],
            capture_output=True, text=True, timeout=15,
        )
        self.assertEqual(0, validate.returncode, validate.stderr)
        self.assertFalse(json.loads(validate.stdout)["safety"]["providerInvoked"])
        status = subprocess.run(
            [str(COMPANION_SETTINGS_EXE), "status", "--settings", str(settings_fixture.resolve()), "--json"],
            capture_output=True, text=True, timeout=15,
        )
        self.assertEqual(0, status.returncode, status.stderr)
        status_json = json.loads(status.stdout)
        self.assertFalse(status_json["safety"]["networkAccessPerformed"])
        self.assertEqual("not-run", status_json["checks"]["liveConnectivity"]["state"])
        fake = subprocess.run(
            [str(COMPANION_SETTINGS_EXE), "test", "--settings", str(settings_fixture.resolve()), "--action", "image.ocr.test.offline-fake-provider", "--json"],
            capture_output=True, text=True, timeout=30,
        )
        self.assertEqual(0, fake.returncode, fake.stderr)
        self.assertTrue(json.loads(fake.stdout)["fakeProvider"]["exactMatch"])

        _, request, request_path, attempt_root = self.make_job(mode="ocr", suffix="companion-candidate-effective")
        self.long_path_cleanup.append(attempt_root / "outputs")
        expected = {
            "modelVersion": "MinerU-HTML",
            "language": "ja",
            "enableTable": True,
            "enableFormula": False,
            "timeoutSeconds": 180,
            "pollIntervalSeconds": 7,
        }
        request["configSnapshot"]["providerSettings"] = expected
        request_path.write_bytes(json_bytes(request))
        settings_log = self.base / "companion-candidate-provider-settings.json"
        environment = self.env()
        environment["FAKE_MINERU_SETTINGS_LOG"] = str(settings_log)
        process = subprocess.run(
            [str(COMPANION_WORKER_EXE), "run", "--request", str(request_path.resolve())],
            capture_output=True, text=True, env=environment, timeout=30,
        )
        self.assertEqual(0, process.returncode, process.stderr)
        self.assertEqual(expected, json.loads(settings_log.read_text(encoding="utf-8")))
        result = json.loads((attempt_root / "result.json").read_text(encoding="utf-8"))
        self.assertEqual("completed", result["status"])
        self.assertEqual("1.2.2", result["worker"]["version"])

    def test_planned_exe_preflight_integrity_failure_publishes_terminal_result(self):
        job, _, request_path, attempt_root = self.make_job(mode="ocr", suffix="exe-preflight-integrity")
        request = json.loads(request_path.read_text(encoding="utf-8"))
        manifest_path = job / request["inputRefs"][0]["manifestPath"]
        manifest_path.write_bytes(manifest_path.read_bytes() + b" ")
        process = subprocess.run(
            self.command("run", "--request", str(request_path.resolve()), exe=True),
            capture_output=True,
            text=True,
            env=self.env(),
            timeout=20,
        )
        self.assertEqual(1, process.returncode)
        self.assertEqual("", process.stderr)
        self.assertEqual(["starting", "failed"], [item["state"] for item in self.assert_progress_stdout(process.stdout)])
        result = json.loads((attempt_root / "result.json").read_text(encoding="utf-8"))
        self.validate_common(result, "worker-result.schema.json")
        self.assertEqual("integrity_mismatch", result["error"]["code"])
        self.assertEqual([], result["outputs"])
        self.assertEqual("preflight", result["payload"]["phase"])

    def test_four_cancel_reasons_publish_terminal_result_inside_grace(self):
        expected = {"user_stop": "cancelled", "superseded": "cancelled", "task_cancel": "cancelled", "timeout": "failed"}
        for index, (reason, status) in enumerate(expected.items(), start=1):
            with self.subTest(reason=reason):
                _, request, request_path, attempt_root = self.make_job(mode="ocr", suffix=f"cancel-{index}")
                process = subprocess.Popen(
                    self.command("run", "--request", str(request_path.resolve())),
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    text=True,
                    env=self.env(poll_seconds=10),
                )
                submission = attempt_root / "submission.json"
                self.wait_for_file(submission, process, f"provider did not reach poll stage for {reason}")
                started = self.publish_cancel(request, attempt_root, reason)
                stdout, stderr = process.communicate(timeout=5)
                elapsed = time.monotonic() - started
                self.assertLess(elapsed, 2.0, f"{reason} took {elapsed:.3f}s")
                self.assertEqual("", stderr)
                self.assert_progress_stdout(stdout)
                result = json.loads((attempt_root / "result.json").read_text(encoding="utf-8"))
                self.validate_common(result, "worker-result.schema.json")
                self.assertEqual(status, result["status"])
                self.assertEqual([], result["outputs"])
                self.assertEqual(reason, result["payload"]["cancellationReason"])
                self.assertEqual(0 if status == "cancelled" else 1, process.returncode)

    def test_cancel_during_provider_submission_publishes_terminal_result_inside_grace(self):
        self.exercise_submission_cancel(exe=False, suffix="cancel-submit")

    def test_planned_exe_cancel_during_provider_submission_inside_grace(self):
        self.exercise_submission_cancel(exe=True, suffix="exe-cancel-submit")

    def test_cancel_during_resume_poll_publishes_terminal_result_inside_grace(self):
        self.exercise_resume_cancel(exe=False, suffix="cancel-resume")

    def test_planned_exe_cancel_during_resume_poll_inside_grace(self):
        self.exercise_resume_cancel(exe=True, suffix="exe-cancel-resume")


if __name__ == "__main__":
    unittest.main()
