from __future__ import annotations

import base64
import hashlib
import importlib.util
import json
import shutil
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

import jsonschema


WORKER_PATH = Path(__file__).resolve().parents[1] / "image_ocr_worker.py"
PROJECT_ROOT = WORKER_PATH.parents[2]
COMMON_SCHEMA_ROOT = PROJECT_ROOT / "contracts" / "v1" / "schemas" / "common"
CAPTURE_SCHEMA_PATH = PROJECT_ROOT / "contracts" / "desktop-automation" / "v1" / "capture-bundle.v1.schema.json"
OFFLINE_FIXTURE_PATH = WORKER_PATH.parent / "fixtures" / "offline-two-image.v1.json"
OFFLINE_FIXTURE_SCHEMA_PATH = WORKER_PATH.parent / "schemas" / "image-ocr-offline-fixture.v1.schema.json"
CORE_CONTINUATION_EVIDENCE_PATH = WORKER_PATH.parent / "fixtures" / "core-continuation-contract-evidence.v3.json"
CORE_CONTINUATION_SCHEMA_PATH = WORKER_PATH.parent / "schemas" / "image-ocr-core-continuation-evidence.v3.schema.json"
CORE_PROVENANCE_EVIDENCE_PATH = WORKER_PATH.parent / "fixtures" / "core-continuation-contract-evidence.v5.json"
CORE_PROVENANCE_SCHEMA_PATH = WORKER_PATH.parent / "schemas" / "image-ocr-core-continuation-evidence.v5.schema.json"
PROVENANCE_BINDING_SCHEMA_PATH = WORKER_PATH.parent / "schemas" / "image-processing-provenance-original-binding.v1.schema.json"
PROVENANCE_BINDING_VALID_PATH = WORKER_PATH.parent / "fixtures" / "provenance-original-binding.valid.v1.json"
PROVENANCE_BINDING_INVALID_PATH = WORKER_PATH.parent / "fixtures" / "provenance-original-binding.invalid-missing-sha.v1.json"
SPEC = importlib.util.spec_from_file_location("image_ocr_worker", WORKER_PATH)
assert SPEC is not None and SPEC.loader is not None
worker = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = worker
SPEC.loader.exec_module(worker)


PNG_1X1 = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zt9sAAAAASUVORK5CYII="
)


def json_bytes(value):
    return (json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2) + "\n").encode("utf-8")


class FakeProvider:
    def __init__(self, outcomes=None, *, fail_after_submission=False):
        self.outcomes = outcomes or {}
        self.fail_after_submission = fail_after_submission
        self.calls = []
        self.settings_calls = []
        self.resume_receipts = []

    def process(self, assets, request, store, resume_submission=None, cancellation=None):
        refs = [asset.artifact_ref for asset in assets]
        self.calls.append(refs)
        self.settings_calls.append(dict(request.provider_settings))
        if resume_submission is None:
            store.write_json_once(
                "submission.json",
                {
                    "schemaVersion": 1,
                    "provider": "mineru",
                    "batchId": "fake-batch-001",
                    "submittedAt": worker.utc_now(),
                    "requestId": request.request_id,
                    "operationId": request.operation_id,
                    "items": [
                        {"artifactRef": asset.artifact_ref, "dataId": "data-" + asset.artifact_ref}
                        for asset in assets
                    ],
                },
            )
        else:
            self.resume_receipts.append(resume_submission)
            store.write_json_once(
                "resume.json",
                {"schemaVersion": 1, "provider": "mineru", "batchId": resume_submission["batchId"]},
            )
        if self.fail_after_submission:
            raise worker.WorkerFailure("network_transient", "injected polling failure", retryable=True)
        results = {}
        for asset in assets:
            outcome = self.outcomes.get(asset.artifact_ref, "completed")
            if outcome in {"completed", "image_ref", "external_image"}:
                markdown = f"# OCR {asset.artifact_ref}\n"
                markdown_source_path = None
                raw_root_relative = f"stages/image-ocr/{store.attempt_id}/raw/fake/{asset.artifact_ref}"
                if outcome == "image_ref":
                    raw_root = store.package_path(raw_root_relative)
                    raw_root.mkdir(parents=True, exist_ok=False)
                    (raw_root / "crop.png").write_bytes(PNG_1X1)
                    markdown_source_path = raw_root / "full.md"
                    markdown = "# OCR with image\n\n![crop](crop.png)\n"
                    markdown_source_path.write_text(markdown, encoding="utf-8")
                elif outcome == "external_image":
                    raw_root = store.package_path(raw_root_relative)
                    raw_root.mkdir(parents=True, exist_ok=False)
                    markdown_source_path = raw_root / "full.md"
                    markdown = "![external](https://example.invalid/private.png)\n"
                    markdown_source_path.write_text(markdown, encoding="utf-8")
                results[asset.artifact_ref] = worker.ProviderItemResult(
                    status="completed",
                    markdown=markdown,
                    markdown_source_path=markdown_source_path,
                    provider_item_id="data-" + asset.artifact_ref,
                    raw_root=raw_root_relative,
                )
            else:
                results[asset.artifact_ref] = worker.ProviderItemResult(
                    status="failed",
                    provider_item_id="data-" + asset.artifact_ref,
                    error_code="provider_timeout",
                    message="injected timeout",
                    retryable=True,
                )
        return results


class UnknownStateProvider:
    def __init__(self):
        self.calls = 0

    def process(self, assets, request, store, resume_submission=None, cancellation=None):
        self.calls += 1
        raise worker.WorkerFailure(
            "provider_submission_unknown",
            "injected ambiguous submission",
            retryable=True,
            status="unknown_external_state",
        )


class ImageOcrWorkerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def make_package(self, mode="ocr_mineru", question_count=2, answer_count=1):
        assets_dir = self.root / "assets" / "original"
        inputs_dir = self.root / "inputs"
        assets_dir.mkdir(parents=True)
        inputs_dir.mkdir(parents=True)
        question_refs = [f"capture.image.question.{index + 1:04d}" for index in range(question_count)]
        answer_refs = [f"capture.image.answer.{index + 1:04d}" for index in range(answer_count)]
        descriptors = []
        for position, artifact_ref in enumerate(question_refs + answer_refs):
            path = assets_dir / f"capture-{position + 1:03d}.png"
            path.write_bytes(PNG_1X1)
            descriptors.append(
                {
                    "artifactRef": artifact_ref,
                    "relativePath": path.relative_to(self.root).as_posix(),
                    "sha256": hashlib.sha256(PNG_1X1).hexdigest(),
                    "bytes": len(PNG_1X1),
                    "mediaType": "image/png",
                }
            )
        capture = {
            "schemaVersion": "capture-bundle.v1",
            "captureId": "capture-bundle-001",
            "createdAt": "2026-08-25T12:00:00Z",
            "intent": "screenshot",
            "source": "screenshot",
            "modeSnapshot": {
                "workflow": "card",
                "contextEnabled": False,
                "contextGranularity": "sentence",
                "contextApplied": False,
                "imageHandling": "ocr" if mode == "ocr_mineru" else "original",
            },
            "artifacts": list(reversed(descriptors)),
            "selectedTextArtifactRef": None,
            "contextSegments": None,
            "questionImages": question_refs,
            "answerImages": answer_refs,
            "questionTurns": [],
        }
        capture_path = inputs_dir / "capture-bundle.v1.json"
        capture_payload = json_bytes(capture)
        capture_path.write_bytes(capture_payload)
        request = {
            "schemaVersion": 1,
            "requestId": "image-request-001",
            "operationId": "image-operation-001",
            "mode": mode,
            "retentionDays": 30,
            "cloudUploadAuthorized": mode == "ocr_mineru",
            "captureBundle": {
                "artifactRef": "capture.bundle.001",
                "path": capture_path.relative_to(self.root).as_posix(),
                "sha256": hashlib.sha256(capture_payload).hexdigest(),
                "bytes": len(capture_payload),
                "mediaType": "application/vnd.rqc.capture-bundle+json",
            },
            "providerSettings": {
                "modelVersion": "vlm",
                "language": "ch",
                "enableTable": True,
                "enableFormula": True,
                "timeoutSeconds": 900,
                "pollIntervalSeconds": 5,
            },
        }
        request_path = inputs_dir / "image-request.v1.json"
        request_path.write_bytes(json_bytes(request))
        return request, capture, request_path.relative_to(self.root).as_posix()

    def read_result(self, attempt_id):
        return json.loads((self.root / "stages" / "image-ocr" / attempt_id / "result.json").read_text(encoding="utf-8"))

    def validate_common(self, value, schema_name):
        schema_path = COMMON_SCHEMA_ROOT / schema_name
        schema = json.loads(schema_path.read_text(encoding="utf-8"))
        resolver = jsonschema.RefResolver(base_uri=COMMON_SCHEMA_ROOT.as_uri() + "/", referrer=schema)
        jsonschema.Draft202012Validator(
            schema,
            resolver=resolver,
            format_checker=jsonschema.Draft202012Validator.FORMAT_CHECKER,
        ).validate(value)

    def test_offline_fixture_schema_integrity_and_recovery_semantics(self):
        fixture = json.loads(OFFLINE_FIXTURE_PATH.read_text(encoding="utf-8"))
        schema = json.loads(OFFLINE_FIXTURE_SCHEMA_PATH.read_text(encoding="utf-8"))
        jsonschema.Draft202012Validator(schema).validate(fixture)
        refs = [item["artifactRef"] for item in fixture["images"]]
        self.assertEqual(set(refs), set(fixture["artifactsArrayOrder"]))
        self.assertEqual(
            fixture["questionImages"] + fixture["answerImages"],
            ["capture.image.question.0001", "capture.image.answer.0001"],
        )
        self.assertEqual(
            fixture["scenarios"]["partial-retry"]["firstAttemptFailureRefs"],
            fixture["scenarios"]["partial-retry"]["expectedRetryRefs"],
        )
        self.assertEqual(
            fixture["questionImages"] + fixture["answerImages"],
            fixture["scenarios"]["resume"]["expectedResumeRefs"],
        )
        self.assertEqual(
            {"expectedOutputKind": "image.original", "providerMustNotRun": True},
            fixture["scenarios"]["original"],
        )
        self.assertEqual(
            fixture["questionImages"] + fixture["answerImages"],
            fixture["scenarios"]["provider-failed"]["failureRefs"],
        )
        self.assertEqual(
            "capture.image.question.0001",
            fixture["scenarios"]["tampered-input"]["tamperArtifactRef"],
        )
        self.assertTrue(fixture["scenarios"]["tampered-input"]["providerMustNotRun"])
        self.assertEqual(
            {
                "expectedStatus": "unknown_external_state",
                "expectedErrorCode": "provider_submission_unknown",
                "expectedRetryable": False,
                "expectedCommonOutputs": 0,
            },
            fixture["scenarios"]["unknown-external-state"],
        )
        for item in fixture["images"]:
            payload = base64.b64decode(item["contentBase64"], validate=True)
            self.assertEqual(item["bytes"], len(payload))
            self.assertEqual(item["sha256"], hashlib.sha256(payload).hexdigest())

    def add_common_request(self, capture_ref, *, attempt_id, request_id, operation_id="operation-image-001", payload=None, config_snapshot=None):
        run_id = "run-image-001"
        stage_id = "image-ocr"
        attempt = f"runs/{run_id}/stages/{stage_id}/attempts/{attempt_id}"
        attempt_root = self.root / attempt
        attempt_root.mkdir(parents=True)
        request = {
            "schemaVersion": "1.0.0",
            "requestId": request_id,
            "operationId": operation_id,
            "taskId": "task-image-001",
            "runId": run_id,
            "stageId": stage_id,
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
            "configSnapshot": config_snapshot or {
                "providerSettings": {
                    "modelVersion": "vlm",
                    "language": "ch",
                    "enableTable": True,
                    "enableFormula": True,
                    "timeoutSeconds": 900,
                    "pollIntervalSeconds": 5,
                }
            },
            "payload": payload or {},
        }
        request_path = attempt_root / "worker-request.json"
        request_path.write_bytes(json_bytes(request))
        return request, request_path.relative_to(self.root).as_posix()

    def make_common_package(self, mode="ocr_mineru", question_count=2, answer_count=1):
        _, capture, _ = self.make_package(mode=mode, question_count=question_count, answer_count=answer_count)
        capture_path = self.root / "inputs" / "capture-bundle.v1.json"
        capture_item = {
            "path": capture_path.relative_to(self.root).as_posix(),
            "sha256": worker.sha256_file(capture_path),
            "bytes": capture_path.stat().st_size,
            "mediaType": "application/vnd.rqc.capture-bundle+json",
        }
        manifest = {
            "schemaVersion": "1.0.0",
            "artifactId": "capture.bundle.001",
            "kind": "capture-bundle",
            "contractVersion": "1.0.0",
            "mediaType": "application/vnd.rqc.capture-bundle+json",
            "files": [capture_item],
            "createdAt": "2026-08-25T12:00:00Z",
            "producer": {
                "stageId": "capture",
                "workerId": "desktop-capture",
                "workerVersion": "1.0.0",
                "attemptId": "capture-attempt-001",
            },
            "inputRefs": [],
            "dataClass": "sensitive",
            "metadata": {"entrypoint": capture_item["path"]},
        }
        manifest_path = self.root / "artifacts" / "capture.bundle.001" / "artifact.json"
        manifest_path.parent.mkdir(parents=True)
        manifest_path.write_bytes(json_bytes(manifest))
        capture_ref = {
            "artifactRef": manifest["artifactId"],
            "kind": manifest["kind"],
            "contractVersion": manifest["contractVersion"],
            "manifestPath": manifest_path.relative_to(self.root).as_posix(),
            "expectedSha256": worker.sha256_file(manifest_path),
        }
        request, request_path = self.add_common_request(
            capture_ref,
            attempt_id="attempt-common-001",
            request_id="request-common-001",
        )
        return capture, capture_ref, request, request_path

    def test_non_default_provider_settings_reach_fake_provider_exactly(self):
        capture, capture_ref, _, _ = self.make_common_package(question_count=1, answer_count=0)
        expected = {
            "modelVersion": "pipeline",
            "language": "en",
            "enableTable": False,
            "enableFormula": True,
            "timeoutSeconds": 120,
            "pollIntervalSeconds": 3,
        }
        request, request_path = self.add_common_request(
            capture_ref,
            attempt_id="attempt-settings-effective",
            request_id="request-settings-effective",
            config_snapshot={"providerSettings": expected},
        )
        provider = FakeProvider()
        result, _ = worker.run_common_adapter(self.root, request_path, provider=provider)
        self.assertEqual("ocr", capture["modeSnapshot"]["imageHandling"])
        self.assertEqual(expected, request["configSnapshot"]["providerSettings"])
        self.assertEqual([expected], provider.settings_calls)
        self.assertEqual("completed", result["status"])

    def test_common_adapter_strictly_rejects_invalid_provider_settings_before_provider(self):
        invalid_values = [
            {"modelVersion": "unknown"},
            {"language": ""},
            {"enableTable": 1},
            {"enableFormula": "true"},
            {"timeoutSeconds": 29},
            {"timeoutSeconds": 3601},
            {"pollIntervalSeconds": 0},
            {"pollIntervalSeconds": 61},
            {"timeoutSeconds": 30, "pollIntervalSeconds": 30},
            {"unexpected": True},
        ]
        for index, provider_settings in enumerate(invalid_values, start=1):
            with self.subTest(provider_settings=provider_settings):
                self.temp.cleanup()
                self.temp = tempfile.TemporaryDirectory()
                self.root = Path(self.temp.name)
                _, capture_ref, _, _ = self.make_common_package(question_count=1, answer_count=0)
                _, request_path = self.add_common_request(
                    capture_ref,
                    attempt_id=f"attempt-invalid-settings-{index}",
                    request_id=f"request-invalid-settings-{index}",
                    config_snapshot={"providerSettings": provider_settings},
                )
                provider = FakeProvider()
                result, _ = worker.run_common_adapter(self.root, request_path, provider=provider)
                self.assertEqual("failed", result["status"])
                self.assertEqual("schema_invalid", result["error"]["code"])
                self.assertEqual([], provider.calls)

    def test_original_passthrough_preserves_explicit_role_order_without_provider(self):
        _, capture, request_path = self.make_package(mode="original_passthrough")
        result, _ = worker.run_worker(self.root, request_path, attempt_id="attempt-original")
        self.assertEqual("completed", result["status"])
        items = result["details"]["items"]
        expected = capture["questionImages"] + capture["answerImages"]
        self.assertEqual(expected, [item["artifactRef"] for item in items])
        self.assertEqual(["question", "question", "answer"], [item["role"] for item in items])
        self.assertTrue(all(item["ocr"] is None for item in items))
        self.assertFalse((self.root / "stages" / "image-ocr" / "attempt-original" / "submission.json").exists())

    def test_ocr_result_order_ignores_provider_and_images_array_order(self):
        _, capture, request_path = self.make_package()
        provider = FakeProvider()
        result, _ = worker.run_worker(self.root, request_path, attempt_id="attempt-ocr", provider=provider)
        self.assertEqual("completed", result["status"])
        expected = capture["questionImages"] + capture["answerImages"]
        self.assertEqual([expected], provider.calls)
        self.assertEqual(expected, [item["artifactRef"] for item in result["details"]["items"]])
        self.assertEqual(10, len(result["outputs"]))
        self.assertEqual(3, len(result["itemDiagnostics"]))
        self.assertTrue(all(item["status"] == "completed" for item in result["itemDiagnostics"]))
        self.assertEqual(expected, [item["artifactRef"] for item in result["outputs"] if item.get("origin") == "input"])

    def test_ocr_same_run_original_is_hash_bound_to_provenance_and_negative_schema_fails(self):
        _, capture, request_path = self.make_package(question_count=1, answer_count=1)
        result, _ = worker.run_worker(self.root, request_path, attempt_id="attempt-same-run-original", provider=FakeProvider())
        expected = capture["questionImages"] + capture["answerImages"]
        originals = {item["artifactRef"]: item for item in result["outputs"] if item.get("origin") == "input"}
        self.assertEqual(expected, list(originals))
        provenance_output = next(item for item in result["outputs"] if item["artifactRef"] == "image.processing.provenance")
        provenance = json.loads((self.root / provenance_output["path"]).read_text(encoding="utf-8"))
        jsonschema.validate(provenance, json.loads(PROVENANCE_BINDING_SCHEMA_PATH.read_text(encoding="utf-8")))
        self.assertEqual(capture["questionImages"], provenance["sourceOrder"]["questionImages"])
        self.assertEqual(capture["answerImages"], provenance["sourceOrder"]["answerImages"])
        for item in provenance["items"]:
            original = originals[item["artifactRef"]]
            representation = item["representations"]["original"]
            self.assertEqual(original["artifactRef"], representation["artifactRef"])
            self.assertEqual(original["sha256"], representation["sha256"])
            self.assertEqual(original["bytes"], representation["bytes"])
            self.assertEqual(original["mediaType"], representation["mediaType"])
        binding_schema = json.loads(PROVENANCE_BINDING_SCHEMA_PATH.read_text(encoding="utf-8"))
        jsonschema.validate(json.loads(PROVENANCE_BINDING_VALID_PATH.read_text(encoding="utf-8")), binding_schema)
        with self.assertRaises(jsonschema.ValidationError):
            jsonschema.validate(json.loads(PROVENANCE_BINDING_INVALID_PATH.read_text(encoding="utf-8")), binding_schema)

    def test_mineru_markdown_images_become_stable_artifact_refs(self):
        _, capture, request_path = self.make_package(question_count=1, answer_count=0)
        ref = capture["questionImages"][0]
        result, _ = worker.run_worker(
            self.root,
            request_path,
            attempt_id="attempt-image-ref",
            provider=FakeProvider({ref: "image_ref"}),
        )
        self.assertEqual("completed", result["status"])
        image_output = next(item for item in result["outputs"] if item["artifactRef"].endswith("image.0001"))
        markdown_output = next(item for item in result["outputs"] if item["artifactRef"].endswith("markdown"))
        structured_output = next(item for item in result["outputs"] if item["artifactRef"].endswith("structured"))
        markdown = (self.root / markdown_output["path"]).read_text(encoding="utf-8")
        structured = json.loads((self.root / structured_output["path"]).read_text(encoding="utf-8"))
        self.assertIn("{{image-ref:" + image_output["artifactRef"] + "}}", markdown)
        self.assertEqual(image_output["artifactRef"], structured["imageRefs"][0]["imageArtifactRef"])
        self.assertEqual("image_ref", structured["blocks"][1]["type"])

    def test_external_markdown_image_url_fails_the_item(self):
        _, capture, request_path = self.make_package(question_count=1, answer_count=0)
        ref = capture["questionImages"][0]
        result, _ = worker.run_worker(
            self.root,
            request_path,
            attempt_id="attempt-external-image",
            provider=FakeProvider({ref: "external_image"}),
        )
        self.assertEqual("failed", result["status"])
        self.assertEqual("provider_artifact_invalid", result["details"]["items"][0]["error"]["code"])
        self.assertEqual("provider_artifact_invalid", result["itemDiagnostics"][0]["code"])

    def test_module_schemas_accept_valid_request_and_result(self):
        request, _, request_path = self.make_package(mode="original_passthrough")
        request_schema = json.loads((WORKER_PATH.parent / "schemas" / "image-request.v1.schema.json").read_text(encoding="utf-8"))
        result_schema = json.loads((WORKER_PATH.parent / "schemas" / "image-result-adapter.v1.schema.json").read_text(encoding="utf-8"))
        jsonschema.Draft202012Validator.check_schema(request_schema)
        jsonschema.Draft202012Validator.check_schema(result_schema)
        jsonschema.validate(request, request_schema)
        result, _ = worker.run_worker(self.root, request_path, attempt_id="attempt-schema")
        jsonschema.validate(result, result_schema)

    def test_partial_retry_only_failed_and_reuses_successful_artifacts(self):
        _, capture, request_path = self.make_package()
        failed_ref = capture["questionImages"][1]
        first_provider = FakeProvider({failed_ref: "failed"})
        first, _ = worker.run_worker(self.root, request_path, attempt_id="attempt-partial", provider=first_provider)
        self.assertEqual("partial", first["status"])
        self.assertTrue(first["details"]["partialBlocksDownstream"])

        second_provider = FakeProvider()
        second, _ = worker.run_worker(
            self.root,
            request_path,
            attempt_id="attempt-retry",
            provider=second_provider,
            retry_result_relative_path="stages/image-ocr/attempt-partial/result.json",
        )
        self.assertEqual([[failed_ref]], second_provider.calls)
        self.assertEqual("completed", second["status"])
        self.assertEqual(capture["questionImages"] + capture["answerImages"], [item["artifactRef"] for item in second["details"]["items"]])
        reused = [item for item in second["details"]["items"] if item.get("reusedFromAttempt")]
        self.assertEqual(2, len(reused))
        self.assertEqual(10, len(second["outputs"]))

    def test_resume_uses_existing_batch_without_new_submission(self):
        _, _, request_path = self.make_package(question_count=1, answer_count=0)
        interrupted = FakeProvider(fail_after_submission=True)
        first, _ = worker.run_worker(self.root, request_path, attempt_id="attempt-interrupted", provider=interrupted)
        self.assertEqual("failed", first["status"])
        self.assertEqual("network_transient", first["error"]["code"])
        submission = self.root / "stages" / "image-ocr" / "attempt-interrupted" / "submission.json"
        self.assertTrue(submission.exists())

        resumed = FakeProvider()
        second, _ = worker.run_worker(
            self.root,
            request_path,
            attempt_id="attempt-resumed",
            provider=resumed,
            resume_submission_relative_path="stages/image-ocr/attempt-interrupted/submission.json",
        )
        self.assertEqual("completed", second["status"])
        self.assertEqual(1, len(resumed.resume_receipts))
        self.assertFalse((self.root / "stages" / "image-ocr" / "attempt-resumed" / "submission.json").exists())

    def test_hash_mismatch_fails_before_provider(self):
        request, _, request_path = self.make_package()
        capture_path = self.root / request["captureBundle"]["path"]
        capture = json.loads(capture_path.read_text(encoding="utf-8"))
        capture["artifacts"][0]["sha256"] = "0" * 64
        payload = json_bytes(capture)
        capture_path.write_bytes(payload)
        request["captureBundle"]["sha256"] = hashlib.sha256(payload).hexdigest()
        request["captureBundle"]["bytes"] = len(payload)
        (self.root / request_path).write_bytes(json_bytes(request))
        provider = FakeProvider()
        result, _ = worker.run_worker(self.root, request_path, attempt_id="attempt-bad-hash", provider=provider)
        self.assertEqual("failed", result["status"])
        self.assertEqual("integrity_mismatch", result["error"]["code"])
        self.assertEqual([], provider.calls)

    def test_single_image_artifacts_array_is_accepted(self):
        _, capture, request_path = self.make_package(question_count=1, answer_count=0)
        provider = FakeProvider()
        result, _ = worker.run_worker(self.root, request_path, attempt_id="attempt-single-array", provider=provider)
        self.assertEqual("completed", result["status"])
        self.assertEqual([capture["questionImages"]], provider.calls)
        self.assertEqual(
            [
                "capture.image.question.0001",
                "ocr.capture.image.question.0001.markdown",
                "ocr.capture.image.question.0001.structured",
                "image.processing.provenance",
            ],
            [output["artifactRef"] for output in result["outputs"]],
        )

    def test_single_image_artifacts_object_is_rejected_before_provider(self):
        request, _, request_path = self.make_package(question_count=1, answer_count=0)
        capture_path = self.root / request["captureBundle"]["path"]
        capture = json.loads(capture_path.read_text(encoding="utf-8"))
        capture["artifacts"] = capture["artifacts"][0]
        payload = json_bytes(capture)
        capture_path.write_bytes(payload)
        request["captureBundle"]["sha256"] = hashlib.sha256(payload).hexdigest()
        request["captureBundle"]["bytes"] = len(payload)
        (self.root / request_path).write_bytes(json_bytes(request))
        provider = FakeProvider()
        result, _ = worker.run_worker(self.root, request_path, attempt_id="attempt-single-object", provider=provider)
        self.assertEqual("failed", result["status"])
        self.assertEqual("schema_invalid", result["error"]["code"])
        self.assertEqual("captureBundle.artifacts must be an array", result["error"]["message"])
        self.assertEqual([], provider.calls)

    def test_unassigned_or_duplicate_role_reference_is_rejected(self):
        request, _, request_path = self.make_package()
        capture_path = self.root / request["captureBundle"]["path"]
        capture = json.loads(capture_path.read_text(encoding="utf-8"))
        capture["answerImages"].append(capture["questionImages"][0])
        payload = json_bytes(capture)
        capture_path.write_bytes(payload)
        request["captureBundle"]["sha256"] = hashlib.sha256(payload).hexdigest()
        request["captureBundle"]["bytes"] = len(payload)
        (self.root / request_path).write_bytes(json_bytes(request))
        result, _ = worker.run_worker(self.root, request_path, attempt_id="attempt-role-error", provider=FakeProvider())
        self.assertEqual("failed", result["status"])
        self.assertEqual("schema_invalid", result["error"]["code"])

    def test_capture_ref_over_common_identifier_limit_has_explicit_diagnostic(self):
        request, _, request_path = self.make_package(question_count=1, answer_count=0)
        capture_path = self.root / request["captureBundle"]["path"]
        capture = json.loads(capture_path.read_text(encoding="utf-8"))
        oversized = "capture." + ("a" * 121)
        capture["artifacts"][0]["artifactRef"] = oversized
        capture["questionImages"] = [oversized]
        payload = json_bytes(capture)
        capture_path.write_bytes(payload)
        request["captureBundle"]["sha256"] = hashlib.sha256(payload).hexdigest()
        request["captureBundle"]["bytes"] = len(payload)
        (self.root / request_path).write_bytes(json_bytes(request))
        result, _ = worker.run_worker(self.root, request_path, attempt_id="attempt-long-ref", provider=FakeProvider())
        self.assertEqual("failed", result["status"])
        self.assertEqual("capture_ref_common_incompatible", result["error"]["code"])

    def test_attempt_is_immutable(self):
        _, _, request_path = self.make_package(mode="original_passthrough")
        worker.run_worker(self.root, request_path, attempt_id="attempt-fixed")
        with self.assertRaises(worker.WorkerFailure) as raised:
            worker.run_worker(self.root, request_path, attempt_id="attempt-fixed")
        self.assertEqual("immutable_attempt_exists", raised.exception.code)

    def test_receipts_do_not_persist_token_or_signed_url(self):
        _, _, request_path = self.make_package()
        secret = "SECRET_TOKEN_SHOULD_NOT_APPEAR"
        old = worker.os.environ.get("MINERU_API_TOKEN")
        worker.os.environ["MINERU_API_TOKEN"] = secret
        try:
            result, _ = worker.run_worker(self.root, request_path, attempt_id="attempt-privacy", provider=FakeProvider())
        finally:
            if old is None:
                worker.os.environ.pop("MINERU_API_TOKEN", None)
            else:
                worker.os.environ["MINERU_API_TOKEN"] = old
        self.assertEqual("completed", result["status"])
        attempt_root = self.root / "stages" / "image-ocr" / "attempt-privacy"
        persisted = "\n".join(path.read_text(encoding="utf-8", errors="ignore") for path in attempt_root.rglob("*") if path.is_file())
        self.assertNotIn(secret, persisted)
        self.assertNotIn("https://", persisted)

    def test_common_original_adapter_validates_authoritative_contracts(self):
        evidence = json.loads(CORE_CONTINUATION_EVIDENCE_PATH.read_text(encoding="utf-8"))
        evidence_schema = json.loads(CORE_CONTINUATION_SCHEMA_PATH.read_text(encoding="utf-8"))
        jsonschema.validate(evidence, evidence_schema)
        self.assertFalse(evidence["publicContractReuse"]["newPublicContractCreated"])
        self.assertEqual("unsupported", evidence["originalPassThrough"]["coreTerminal"]["state"])
        capture, _, request, request_path = self.make_common_package(mode="original_passthrough")
        capture_schema = json.loads(CAPTURE_SCHEMA_PATH.read_text(encoding="utf-8"))
        artifact_schema = json.loads((COMMON_SCHEMA_ROOT / "artifact.schema.json").read_text(encoding="utf-8"))
        capture_resolver = jsonschema.RefResolver(
            base_uri=CAPTURE_SCHEMA_PATH.as_uri(),
            referrer=capture_schema,
            store={"https://local.invalid/rqc/v1/schemas/common/artifact.schema.json": artifact_schema},
        )
        jsonschema.Draft202012Validator(
            capture_schema,
            resolver=capture_resolver,
            format_checker=jsonschema.Draft202012Validator.FORMAT_CHECKER,
        ).validate(capture)
        self.validate_common(request, "worker-request.schema.json")

        result, result_path = worker.run_common_adapter(self.root, request_path)
        self.validate_common(result, "worker-result.schema.json")
        self.assertEqual(self.root / request["resultPath"], result_path)
        expected = capture["questionImages"] + capture["answerImages"]
        self.assertEqual(expected, [item["itemId"] for item in result["itemDiagnostics"]])
        self.assertEqual(expected, [item["artifactRef"] for item in result["outputs"] if item["kind"] == "image.original"])
        media_bundle_ref = next(item for item in result["outputs"] if item["kind"] == "image.original.bundle")
        provenance_ref = next(item for item in result["outputs"] if item["kind"] == "image.processing.provenance")
        media_manifest = json.loads((self.root / media_bundle_ref["manifestPath"]).read_text(encoding="utf-8"))
        media_payload = json.loads((self.root / media_manifest["files"][0]["path"]).read_text(encoding="utf-8"))
        jsonschema.validate(media_payload, json.loads((WORKER_PATH.parent / "schemas" / "image-original-media-bundle.v1.schema.json").read_text(encoding="utf-8")))
        self.assertEqual(0, media_payload["providerCalls"])
        self.assertEqual(capture["questionImages"], [item["artifactRef"] for item in media_payload["questionImages"]])
        self.assertEqual(capture["answerImages"], [item["artifactRef"] for item in media_payload["answerImages"]])
        provenance_manifest = json.loads((self.root / provenance_ref["manifestPath"]).read_text(encoding="utf-8"))
        provenance = json.loads((self.root / provenance_manifest["files"][0]["path"]).read_text(encoding="utf-8"))
        jsonschema.validate(provenance, json.loads((WORKER_PATH.parent / "schemas" / "image-processing-provenance.v1.schema.json").read_text(encoding="utf-8")))
        self.assertFalse(provenance["continuation"]["defaultOcrRerun"])
        self.assertEqual(0, provenance["provider"]["calls"])
        self.assertEqual(evidence["originalPassThrough"]["workerOutput"]["status"], result["status"])
        self.assertEqual(evidence["originalPassThrough"]["workerOutput"]["payload.mode"], result["payload"]["mode"])
        self.assertFalse((self.root / request["attemptPath"] / "submission.json").exists())
        for output in result["outputs"]:
            manifest = json.loads((self.root / output["manifestPath"]).read_text(encoding="utf-8"))
            self.validate_common(manifest, "artifact.schema.json")
            if output["kind"] == "image.original":
                self.assertEqual(evidence["originalPassThrough"]["workerOutput"]["artifactKind"], manifest["kind"])

    def test_module_provenance_continuation_evidence_keeps_downstream_pending(self):
        evidence = json.loads(CORE_PROVENANCE_EVIDENCE_PATH.read_text(encoding="utf-8"))
        schema = json.loads(CORE_PROVENANCE_SCHEMA_PATH.read_text(encoding="utf-8"))
        jsonschema.validate(evidence, schema)
        self.assertFalse(evidence["ocrHumanDecision"]["defaultOcrRerun"])
        self.assertTrue(evidence["ocrHumanDecision"]["requiresOriginalArtifactHashMatch"])
        self.assertTrue(evidence["ocrSameRunOriginal"]["publishedInCompletedOcrResult"])
        self.assertEqual(0, evidence["ocrSameRunOriginal"]["additionalProviderCalls"])
        self.assertEqual(0, evidence["originalPassThrough"]["providerCalls"])
        self.assertFalse(evidence["downstreamIntegration"]["claimedComplete"])

    def test_common_partial_result_and_retry_reuse_only_failed_item(self):
        evidence = json.loads(CORE_CONTINUATION_EVIDENCE_PATH.read_text(encoding="utf-8"))
        capture, capture_ref, request, request_path = self.make_common_package()
        failed_ref = capture["questionImages"][1]
        first_provider = FakeProvider({failed_ref: "failed"})
        first, _ = worker.run_common_adapter(self.root, request_path, provider=first_provider)
        self.validate_common(first, "worker-result.schema.json")
        expected_first = evidence["partialFailedItemsOnlyContinuation"]["firstAttemptOutput"]
        self.assertEqual(expected_first["status"], first["status"])
        self.assertEqual(8, len(first["outputs"]))
        self.assertEqual([capture["questionImages"] + capture["answerImages"]], first_provider.calls)
        failed_diagnostic = next(item for item in first["itemDiagnostics"] if item["status"] == "failed")
        self.assertEqual(expected_first["failedItemDiagnosticCode"], failed_diagnostic["error"]["code"])
        self.assertEqual(expected_first["failedItemRetryable"], failed_diagnostic["error"]["retryable"])
        self.assertEqual([expected_first["warning"]], first["warnings"])
        self.assertEqual(expected_first["payload.partialBlocksDownstream"], first["payload"]["partialBlocksDownstream"])

        prior_module = f"{request['attemptPath']}/module-result.json"
        _, retry_request_path = self.add_common_request(
            capture_ref,
            attempt_id="attempt-common-002",
            request_id="request-common-002",
            payload={"retryModuleResultPath": prior_module},
        )
        retry_provider = FakeProvider()
        retry, _ = worker.run_common_adapter(self.root, retry_request_path, provider=retry_provider)
        self.validate_common(retry, "worker-result.schema.json")
        self.assertEqual(
            evidence["partialFailedItemsOnlyContinuation"]["workerRetryOutput"]["terminalStatusAfterRecovery"],
            retry["status"],
        )
        self.assertEqual([[failed_ref]], retry_provider.calls)
        self.assertEqual(10, len(retry["outputs"]))
        provenance_ref = next(item for item in retry["outputs"] if item["kind"] == "image.processing.provenance")
        provenance_manifest = json.loads((self.root / provenance_ref["manifestPath"]).read_text(encoding="utf-8"))
        provenance = json.loads((self.root / provenance_manifest["files"][0]["path"]).read_text(encoding="utf-8"))
        self.assertFalse(provenance["continuation"]["defaultOcrRerun"])
        self.assertEqual([], provenance["continuation"]["failedArtifactRefs"])
        reused_items = [item for item in provenance["items"] if item["reusedFromAttempt"]]
        self.assertEqual(2, len(reused_items))
        for item in reused_items:
            self.assertRegex(item["representations"]["original"]["sha256"], r"^[0-9a-f]{64}$")
            self.assertRegex(item["representations"]["markdown"]["sha256"], r"^[0-9a-f]{64}$")
            self.assertRegex(item["representations"]["structured"]["sha256"], r"^[0-9a-f]{64}$")
        retry_module = json.loads((self.root / retry["payload"]["moduleResultPath"]).read_text(encoding="utf-8"))
        reused = [item for item in retry_module["details"]["items"] if item.get("reusedFromAttempt")]
        self.assertEqual(2, len(reused))

    def test_common_adapter_rejects_tampered_capture_manifest_before_provider(self):
        _, capture_ref, request, request_path = self.make_common_package()
        manifest_path = self.root / capture_ref["manifestPath"]
        manifest_path.write_bytes(manifest_path.read_bytes() + b" ")
        provider = FakeProvider()
        result, result_path = worker.run_common_adapter(self.root, request_path, provider=provider)
        self.validate_common(result, "worker-result.schema.json")
        self.assertEqual("failed", result["status"])
        self.assertEqual("integrity_mismatch", result["error"]["code"])
        self.assertEqual([], result["outputs"])
        self.assertEqual("preflight", result["payload"]["phase"])
        self.assertEqual(self.root / request["resultPath"], result_path)
        self.assertEqual(result, json.loads(result_path.read_text(encoding="utf-8")))
        self.assertEqual([], provider.calls)

    def test_common_prestart_cancel_publishes_cancelled_result_without_provider(self):
        _, _, request, request_path = self.make_common_package()
        cancel_path = self.root / request["cancelSignal"]["path"]
        cancel_path.write_bytes(
            json_bytes(
                {
                    "schemaVersion": "1.0.0",
                    "attemptId": request["attemptId"],
                    "operationId": request["operationId"],
                    "reason": "task_cancel",
                    "requestedAt": "2026-08-25T12:01:00Z",
                    "graceDeadline": "2026-08-25T12:01:02Z",
                }
            )
        )
        provider = FakeProvider()
        result, _ = worker.run_common_adapter(self.root, request_path, provider=provider)
        self.validate_common(result, "worker-result.schema.json")
        self.assertEqual("cancelled", result["status"])
        self.assertEqual("cancelled", result["error"]["category"])
        self.assertEqual([], provider.calls)

    def test_common_unknown_external_state_is_non_retryable_and_publishes_no_outputs(self):
        _, _, _, request_path = self.make_common_package()
        provider = UnknownStateProvider()
        result, _ = worker.run_common_adapter(self.root, request_path, provider=provider)
        self.validate_common(result, "worker-result.schema.json")
        self.assertEqual("unknown_external_state", result["status"])
        self.assertFalse(result["error"]["retryable"])
        self.assertEqual("effect", result["error"]["category"])
        self.assertEqual([], result["outputs"])
        self.assertEqual(1, provider.calls)

    def test_safe_zip_extraction_supports_deep_windows_attempt_paths(self):
        archive_path = self.root / "provider.zip"
        with zipfile.ZipFile(archive_path, "w") as archive:
            archive.writestr("provider-content-list-with-a-long-generated-name-0000000000000001.json", "{}")
            archive.writestr("full.md", "# deep path\n")
        destination = self.root.joinpath(*(["deep-attempt-segment"] * 12), "extracted")
        try:
            extracted = worker._safe_extract_zip(archive_path, destination)
            markdown = next(path for path in extracted if path.name == "full.md")
            self.assertGreater(len(str(next(path for path in extracted if path.suffix == ".json"))), 260)
            self.assertEqual(b"# deep path\n", worker.read_file_bytes(markdown))
        finally:
            deep_root = self.root / "deep-attempt-segment"
            if deep_root.exists():
                shutil.rmtree(worker.native_fs_path(deep_root))


if __name__ == "__main__":
    unittest.main()
