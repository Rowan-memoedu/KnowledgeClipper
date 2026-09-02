from __future__ import annotations

import argparse
import base64
import datetime as dt
import hashlib
import json
import os
import subprocess
import sys
import traceback
import uuid
from pathlib import Path
from typing import Any

import jsonschema


WORKER_ROOT = Path(__file__).resolve().parent
PROJECT_ROOT = WORKER_ROOT.parents[1]
EXE = WORKER_ROOT / "candidates" / "1.2.4" / "KnowledgeClipper.ImageOcr.Worker.exe"
COMMON_SCHEMAS = PROJECT_ROOT / "contracts" / "v1" / "schemas" / "common"
CAPTURE_SCHEMA = PROJECT_ROOT / "contracts" / "desktop-automation" / "v1" / "capture-bundle.v1.schema.json"
FIXTURE_SCHEMA = WORKER_ROOT / "schemas" / "image-ocr-offline-fixture.v1.schema.json"
DEFAULT_FIXTURE = WORKER_ROOT / "fixtures" / "offline-two-image.v1.json"
FAKE_TOKEN = "OFFLINE_CANARY_FAKE_TOKEN"


def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")


def json_bytes(value: object) -> bytes:
    return (json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2) + "\n").encode("utf-8")


def write_new(path: Path, payload: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("xb") as handle:
        handle.write(payload)


def sha256_bytes(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest()


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_json(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"Expected a JSON object: {path}")
    return value


def validate_common(value: object, schema_name: str) -> None:
    schema = load_json(COMMON_SCHEMAS / schema_name)
    resolver = jsonschema.RefResolver(base_uri=COMMON_SCHEMAS.as_uri() + "/", referrer=schema)
    jsonschema.Draft202012Validator(
        schema,
        resolver=resolver,
        format_checker=jsonschema.Draft202012Validator.FORMAT_CHECKER,
    ).validate(value)


def validate_capture(value: object) -> None:
    schema = load_json(CAPTURE_SCHEMA)
    artifact_schema = load_json(COMMON_SCHEMAS / "artifact.schema.json")
    resolver = jsonschema.RefResolver(
        base_uri=CAPTURE_SCHEMA.as_uri(),
        referrer=schema,
        store={"https://local.invalid/rqc/v1/schemas/common/artifact.schema.json": artifact_schema},
    )
    jsonschema.Draft202012Validator(
        schema,
        resolver=resolver,
        format_checker=jsonschema.Draft202012Validator.FORMAT_CHECKER,
    ).validate(value)


def load_fixture(path: Path) -> dict[str, Any]:
    fixture = load_json(path)
    jsonschema.Draft202012Validator(load_json(FIXTURE_SCHEMA)).validate(fixture)
    images = fixture["images"]
    refs = [item["artifactRef"] for item in images]
    if len(refs) != len(set(refs)):
        raise ValueError("Fixture image artifactRefs must be unique")
    file_names = [item["fileName"] for item in images]
    if len(file_names) != len(set(file_names)):
        raise ValueError("Fixture image fileNames must be unique")
    ref_set = set(refs)
    if set(fixture["artifactsArrayOrder"]) != ref_set or len(fixture["artifactsArrayOrder"]) != len(refs):
        raise ValueError("Fixture artifactsArrayOrder must contain each image exactly once")
    role_refs = {
        "question": [item["artifactRef"] for item in images if item["role"] == "question"],
        "answer": [item["artifactRef"] for item in images if item["role"] == "answer"],
    }
    if fixture["questionImages"] != role_refs["question"] or fixture["answerImages"] != role_refs["answer"]:
        raise ValueError("Fixture role arrays must exactly match image roles and fixture order")
    for item in images:
        payload = base64.b64decode(item["contentBase64"], validate=True)
        if len(payload) != item["bytes"] or sha256_bytes(payload) != item["sha256"]:
            raise ValueError(f"Fixture image integrity mismatch: {item['artifactRef']}")
    recovery = fixture["scenarios"]["partial-retry"]
    failed = recovery["firstAttemptFailureRefs"]
    if any(ref not in ref_set for ref in failed) or recovery["expectedRetryRefs"] != failed:
        raise ValueError("partial-retry refs must identify the same known failed images")
    expected_order = fixture["questionImages"] + fixture["answerImages"]
    if fixture["scenarios"]["resume"]["expectedResumeRefs"] != expected_order:
        raise ValueError("resume refs must match the explicit question/answer order")
    if fixture["scenarios"]["original"] != {"expectedOutputKind": "image.original", "providerMustNotRun": True}:
        raise ValueError("original scenario must require provider-free image.original outputs")
    provider_failure = fixture["scenarios"]["provider-failed"]
    if provider_failure["failureRefs"] != expected_order or provider_failure["expectedTopErrorCode"] != "all_items_failed":
        raise ValueError("provider-failed must fail every image and expect all_items_failed")
    tampered = fixture["scenarios"]["tampered-input"]
    if tampered["tamperArtifactRef"] not in ref_set or tampered["expectedErrorCode"] != "integrity_mismatch" or tampered["providerMustNotRun"] is not True:
        raise ValueError("tampered-input must corrupt one known image before provider startup")
    if fixture["scenarios"]["unknown-external-state"] != {
        "expectedStatus": "unknown_external_state",
        "expectedErrorCode": "provider_submission_unknown",
        "expectedRetryable": False,
        "expectedCommonOutputs": 0,
    }:
        raise ValueError("unknown-external-state must remain non-retryable with no common outputs")
    return fixture


def fake_provider_source() -> bytes:
    return r'''
import json
import os
import zipfile
from pathlib import Path

_specs = []

def _log_call(event):
    call_log = os.environ.get("OFFLINE_PROVIDER_CALL_LOG")
    if call_log:
        with open(call_log, "a", encoding="utf-8") as handle:
            handle.write(json.dumps({
                "event": event,
                "fileNames": [item["file_name"] for item in _specs],
                "dataIds": [item["data_id"] for item in _specs]
            }, ensure_ascii=False, separators=(",", ":")) + "\n")

def submit_local(paths, args, token):
    global _specs
    _specs = [
        {"data_id": "offline-%04d" % (index + 1), "file_name": Path(path).name}
        for index, path in enumerate(paths)
    ]
    if os.environ.get("OFFLINE_SUBMIT_UNKNOWN") == "1":
        _log_call("submit-ambiguous")
        raise RuntimeError("offline injected ambiguous submission")
    _log_call("submit")
    return "offline-batch-001", _specs

def poll_batch(batch_id, args, token):
    global _specs
    if not _specs:
        file_names = [value for value in os.environ.get("OFFLINE_RESUME_FILE_NAMES", "").split(",") if value]
        data_ids = [value for value in os.environ.get("OFFLINE_RESUME_DATA_IDS", "").split(",") if value]
        if len(file_names) != len(data_ids):
            raise RuntimeError("offline resume fixture mapping is invalid")
        _specs = [{"data_id": data_id, "file_name": file_name} for data_id, file_name in zip(data_ids, file_names)]
        _log_call("resume-poll")
    failed_names = {value for value in os.environ.get("OFFLINE_FAIL_FILENAMES", "").split(",") if value}
    results = []
    for item in reversed(_specs):
        if item["file_name"] in failed_names:
            results.append({"data_id": item["data_id"], "state": "failed", "err_msg": "offline injected failure"})
        else:
            results.append({
                "data_id": item["data_id"],
                "state": "done",
                "full_zip_url": "offline://" + item["data_id"]
            })
    return results

def download_file(url, path):
    data_id = url.rsplit("/", 1)[-1]
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("full.md", "# OFFLINE OCR CANARY\n\nproviderDataId=" + data_id + "\n")
'''.lstrip().encode("utf-8")


def build_package(
    canary_root: Path,
    fixture: dict[str, Any],
    image_handling: str = "ocr",
    corrupt_ref: str | None = None,
) -> dict[str, Any]:
    task_id = f"task-iof-{uuid.uuid4().hex[:8]}"
    job = canary_root / "jobs" / task_id
    inputs = job / "inputs"
    images_root = inputs / "images"
    images_root.mkdir(parents=True)
    provider_path = canary_root / "provider" / "fake_mineru.py"
    write_new(provider_path, fake_provider_source())
    descriptors_by_ref: dict[str, dict[str, Any]] = {}
    file_name_by_ref: dict[str, str] = {}
    for item in fixture["images"]:
        payload = base64.b64decode(item["contentBase64"], validate=True)
        image_path = images_root / item["fileName"]
        stored_payload = payload + b"\x00" if item["artifactRef"] == corrupt_ref else payload
        write_new(image_path, stored_payload)
        descriptors_by_ref[item["artifactRef"]] = {
            "artifactRef": item["artifactRef"],
            "relativePath": image_path.relative_to(job).as_posix(),
            "sha256": item["sha256"],
            "bytes": item["bytes"],
            "mediaType": item["mediaType"],
            "encoding": None,
        }
        file_name_by_ref[item["artifactRef"]] = item["fileName"]
    capture = {
        "schemaVersion": "capture-bundle.v1",
        "captureId": f"capture-{uuid.uuid4().hex[:8]}",
        "createdAt": utc_now(),
        "intent": "screenshot",
        "source": "screenshot",
        "modeSnapshot": {"workflow": "card", "contextEnabled": False, "contextGranularity": "sentence", "contextApplied": False, "imageHandling": image_handling},
        "artifacts": [descriptors_by_ref[ref] for ref in fixture["artifactsArrayOrder"]],
        "selectedTextArtifactRef": None,
        "contextSegments": None,
        "questionImages": fixture["questionImages"],
        "answerImages": fixture["answerImages"],
        "questionTurns": [],
    }
    validate_capture(capture)
    capture_path = inputs / "capture-bundle.v1.json"
    write_new(capture_path, json_bytes(capture))
    artifact_id = f"capture.bundle.{uuid.uuid4().hex}"
    capture_relative = capture_path.relative_to(job).as_posix()
    manifest = {
        "schemaVersion": "1.0.0",
        "artifactId": artifact_id,
        "kind": "capture-bundle",
        "contractVersion": "1.0.0",
        "mediaType": "application/vnd.rqc.capture-bundle+json",
        "files": [{"path": capture_relative, "sha256": sha256_file(capture_path), "bytes": capture_path.stat().st_size, "mediaType": "application/vnd.rqc.capture-bundle+json"}],
        "createdAt": utc_now(),
        "producer": {"stageId": "capture", "workerId": "desktop-capture", "workerVersion": "1.0.0", "attemptId": "capture-attempt-0001"},
        "inputRefs": [],
        "dataClass": "sensitive",
        "metadata": {"entrypoint": capture_relative},
    }
    validate_common(manifest, "artifact.schema.json")
    manifest_path = job / "artifacts" / artifact_id / "artifact.json"
    write_new(manifest_path, json_bytes(manifest))
    capture_ref = {"artifactRef": artifact_id, "kind": "capture-bundle", "contractVersion": "1.0.0", "manifestPath": manifest_path.relative_to(job).as_posix(), "expectedSha256": sha256_file(manifest_path)}
    return {
        "job": job,
        "taskId": task_id,
        "providerPath": provider_path,
        "providerCallLog": canary_root / "provider-calls.ndjson",
        "captureRef": capture_ref,
        "fileNameByRef": file_name_by_ref,
        "runId": f"run-{uuid.uuid4().hex[:8]}",
        "operationId": f"operation-{uuid.uuid4().hex[:8]}",
    }


def create_request(
    package: dict[str, Any],
    attempt_number: int,
    retry_module_result: str | None,
    resume_submission: str | None,
) -> tuple[dict[str, Any], Path]:
    attempt_id = f"attempt-{attempt_number}"
    attempt = f"runs/{package['runId']}/stages/image-ocr/attempts/{attempt_id}"
    attempt_root = package["job"] / attempt
    attempt_root.mkdir(parents=True)
    payload: dict[str, Any] = {}
    if retry_module_result is not None:
        payload["retryModuleResultPath"] = retry_module_result
    if resume_submission is not None:
        payload["resumeSubmissionPath"] = resume_submission
    request = {
        "schemaVersion": "1.0.0",
        "requestId": f"request-{uuid.uuid4().hex[:8]}",
        "operationId": package["operationId"],
        "taskId": package["taskId"],
        "runId": package["runId"],
        "stageId": "image-ocr",
        "attemptId": attempt_id,
        "workerId": "image.ocr.mineru",
        "operation": "process-images",
        "idempotencyKey": sha256_bytes(f"{package['operationId']}:{attempt_id}".encode("utf-8")),
        "attemptPath": attempt,
        "outputPath": f"{attempt}/outputs",
        "progressPath": f"{attempt}/progress.ndjson",
        "resultPath": f"{attempt}/result.json",
        "cancelSignal": {"schemaVersion": "1.0.0", "path": f"{attempt}/cancel.json", "gracePeriodMs": 2000},
        "inputRefs": [package["captureRef"]],
        "configSnapshot": {"providerSettings": {"pollIntervalSeconds": 1, "timeoutSeconds": 30}},
        "payload": payload,
    }
    validate_common(request, "worker-request.schema.json")
    request_path = attempt_root / "request.json"
    write_new(request_path, json_bytes(request))
    return request, request_path


def run_attempt(
    canary_root: Path,
    package: dict[str, Any],
    attempt_number: int,
    failure_refs: list[str],
    retry_module_result: str | None = None,
    resume_submission: str | None = None,
    resume_refs: list[str] | None = None,
    submit_unknown: bool = False,
) -> dict[str, Any]:
    request, request_path = create_request(package, attempt_number, retry_module_result, resume_submission)
    environment = os.environ.copy()
    environment["MINERU_API_TOKEN"] = FAKE_TOKEN
    environment["KNOWLEDGE_CLIPPER_MINERU_SCRIPT"] = str(package["providerPath"].resolve())
    environment.pop("RQC_MINERU_SCRIPT", None)
    environment["OFFLINE_PROVIDER_CALL_LOG"] = str(package["providerCallLog"].resolve())
    environment["OFFLINE_FAIL_FILENAMES"] = ",".join(package["fileNameByRef"][ref] for ref in failure_refs)
    if submit_unknown:
        environment["OFFLINE_SUBMIT_UNKNOWN"] = "1"
    if resume_refs:
        environment["OFFLINE_RESUME_FILE_NAMES"] = ",".join(package["fileNameByRef"][ref] for ref in resume_refs)
        environment["OFFLINE_RESUME_DATA_IDS"] = ",".join(f"resume-{index + 1:04d}" for index in range(len(resume_refs)))
    completed = subprocess.run([str(EXE), "run", "--request", str(request_path.resolve())], capture_output=True, text=True, encoding="utf-8", env=environment, timeout=30, check=False)
    stdout_path = canary_root / f"attempt-{attempt_number}.stdout.ndjson"
    stderr_path = canary_root / f"attempt-{attempt_number}.stderr.txt"
    write_new(stdout_path, completed.stdout.encode("utf-8"))
    write_new(stderr_path, completed.stderr.encode("utf-8"))
    result_path = package["job"] / request["resultPath"]
    if not result_path.is_file():
        raise RuntimeError(f"attempt-{attempt_number} produced no result.json: {completed.stderr.strip()}")
    result = load_json(result_path)
    validate_common(result, "worker-result.schema.json")
    module_result_path = package["job"] / request["attemptPath"] / "module-result.json"
    module_result = load_json(module_result_path) if module_result_path.is_file() else None
    for output in result["outputs"]:
        validate_common(load_json(package["job"] / output["manifestPath"]), "artifact.schema.json")
    return {
        "request": request,
        "process": completed,
        "stdoutPath": stdout_path,
        "stderrPath": stderr_path,
        "result": result,
        "resultPath": result_path,
        "moduleResult": module_result,
        "moduleResultPath": module_result_path if module_result is not None else None,
        "stdoutStates": [json.loads(line)["state"] for line in completed.stdout.splitlines() if line.strip()],
    }


def item_statuses(result: dict[str, Any]) -> dict[str, str]:
    return {item["itemId"]: item["status"] for item in result["itemDiagnostics"]}


def markdown_previews(package: dict[str, Any], module_result: dict[str, Any]) -> list[dict[str, str]]:
    previews: list[dict[str, str]] = []
    outputs = {item["artifactRef"]: item for item in module_result["outputs"]}
    for item in module_result["details"]["items"]:
        if item["status"] != "completed":
            continue
        if item["ocr"] is None:
            continue
        descriptor = outputs[item["ocr"]["markdownArtifactRef"]]
        previews.append({"itemId": item["artifactRef"], "text": (package["job"] / descriptor["path"]).read_text(encoding="utf-8").strip()})
    return previews


def module_output_payload(package: dict[str, Any], module_result: dict[str, Any], kind: str) -> dict[str, Any]:
    matches = [item for item in module_result["outputs"] if item.get("artifactKind") == kind]
    if len(matches) != 1:
        raise ValueError(f"Expected exactly one {kind} module output, found {len(matches)}")
    return load_json(package["job"] / matches[0]["path"])


def attempt_receipt(canary_root: Path, attempt: dict[str, Any]) -> dict[str, Any]:
    process = attempt["process"]
    result = attempt["result"]
    return {
        "attemptId": attempt["request"]["attemptId"],
        "exitCode": process.returncode,
        "stderrEmpty": process.stderr == "",
        "stdoutStates": attempt["stdoutStates"],
        "stdoutPath": attempt["stdoutPath"].relative_to(canary_root).as_posix(),
        "stderrPath": attempt["stderrPath"].relative_to(canary_root).as_posix(),
        "resultPath": attempt["resultPath"].relative_to(canary_root).as_posix(),
        "resultSha256": sha256_file(attempt["resultPath"]),
        "moduleResultPath": attempt["moduleResultPath"].relative_to(canary_root).as_posix() if attempt["moduleResultPath"] else None,
        "moduleResultSha256": sha256_file(attempt["moduleResultPath"]) if attempt["moduleResultPath"] else None,
        "status": result["status"],
        "outputs": len(result["outputs"]),
        "itemStatuses": item_statuses(result),
        "partialBlocksDownstream": result["payload"].get("partialBlocksDownstream"),
    }


def read_provider_calls(path: Path) -> list[dict[str, Any]]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def run_scenario(canary_root: Path, fixture_path: Path, fixture: dict[str, Any], scenario: str) -> tuple[dict[str, Any], bool]:
    tamper_ref = fixture["scenarios"]["tampered-input"]["tamperArtifactRef"] if scenario == "tampered-input" else None
    package = build_package(
        canary_root,
        fixture,
        "original" if scenario == "original" else "ocr",
        corrupt_ref=tamper_ref,
    )
    expected_order = fixture["questionImages"] + fixture["answerImages"]
    attempts: list[dict[str, Any]] = []
    recovery: dict[str, Any] | None = None
    if scenario == "success":
        final_attempt = run_attempt(canary_root, package, 1, [])
        attempts.append(final_attempt)
        result = final_attempt["result"]
        provenance = module_output_payload(package, final_attempt["moduleResult"], "image.processing.provenance")
        passed = final_attempt["process"].returncode == 0 and final_attempt["process"].stderr == "" and final_attempt["stdoutStates"] == ["starting", "completed"] and result["status"] == "completed" and [item["itemId"] for item in result["itemDiagnostics"]] == expected_order and len(result["outputs"]) == len(expected_order) * 3 + 1 and provenance["continuation"]["defaultOcrRerun"] is False and len([item for item in result["outputs"] if item["kind"] == "image.original"]) == len(expected_order)
    elif scenario == "partial-retry":
        failure_refs = fixture["scenarios"][scenario]["firstAttemptFailureRefs"]
        first = run_attempt(canary_root, package, 1, failure_refs)
        attempts.append(first)
        prior_module = first["moduleResultPath"].relative_to(package["job"]).as_posix()
        second = run_attempt(canary_root, package, 2, [], retry_module_result=prior_module)
        attempts.append(second)
        final_attempt = second
        first_statuses = item_statuses(first["result"])
        second_statuses = item_statuses(second["result"])
        retry_refs = fixture["scenarios"][scenario]["expectedRetryRefs"]
        expected_reused = [ref for ref in expected_order if ref not in retry_refs]
        reused_refs = [item["artifactRef"] for item in second["moduleResult"]["details"]["items"] if item.get("reusedFromAttempt") == "attempt-1"]
        calls = read_provider_calls(package["providerCallLog"])
        retry_file_names = [package["fileNameByRef"][ref] for ref in retry_refs]
        first_outputs = {item["artifactRef"]: item for item in first["moduleResult"]["outputs"]}
        second_outputs = {item["artifactRef"]: item for item in second["moduleResult"]["outputs"]}
        reused_output_hashes_match = all(first_outputs[output_ref]["sha256"] == second_outputs[output_ref]["sha256"] for item in second["moduleResult"]["details"]["items"] if item["artifactRef"] in expected_reused for output_ref in (item["ocr"]["markdownArtifactRef"], item["ocr"]["structuredArtifactRef"]))
        first_provenance = module_output_payload(package, first["moduleResult"], "image.processing.provenance")
        second_provenance = module_output_payload(package, second["moduleResult"], "image.processing.provenance")
        continuation_contract = (
            first_provenance["continuation"]["retryScope"] == "failed-items-only"
            and first_provenance["continuation"]["defaultOcrRerun"] is False
            and first_provenance["continuation"]["failedArtifactRefs"] == failure_refs
            and first_provenance["continuation"]["reusableCompletedArtifactRefs"] == expected_reused
            and second_provenance["continuation"]["defaultOcrRerun"] is False
        )
        passed = first["process"].returncode == 1 and first["process"].stderr == "" and first["stdoutStates"] == ["starting", "partial"] and first["result"]["status"] == "partial" and first["result"]["payload"].get("partialBlocksDownstream") is True and all(first_statuses[ref] == "failed" for ref in failure_refs) and second["process"].returncode == 0 and second["process"].stderr == "" and second["stdoutStates"] == ["starting", "completed"] and second["result"]["status"] == "completed" and all(status == "completed" for status in second_statuses.values()) and reused_refs == expected_reused and len(calls) == 2 and calls[1]["fileNames"] == retry_file_names and reused_output_hashes_match and continuation_contract and len(second["result"]["outputs"]) == len(expected_order) * 3 + 1
        recovery = {"firstAttemptFailedRefs": failure_refs, "retryExpectedRefs": retry_refs, "providerCalls": calls, "reusedRefs": reused_refs, "reusedOutputHashesMatch": reused_output_hashes_match, "continuationContract": {"defaultOcrRerun": first_provenance["continuation"]["defaultOcrRerun"], "retryScope": first_provenance["continuation"]["retryScope"], "requiresOriginalArtifactHashMatch": first_provenance["continuation"]["requiresOriginalArtifactHashMatch"], "requiresSuccessfulOutputHashMatch": first_provenance["continuation"]["requiresSuccessfulOutputHashMatch"]}, "finalStatus": second["result"]["status"]}
    elif scenario == "resume":
        resume_refs = fixture["scenarios"]["resume"]["expectedResumeRefs"]
        resume_path = package["job"] / "inputs" / "resume-submission.json"
        resume_submission = {
            "schemaVersion": 1,
            "provider": "mineru",
            "batchId": "offline-resume-batch-001",
            "submittedAt": utc_now(),
            "requestId": "offline-prior-request",
            "operationId": package["operationId"],
            "items": [
                {"artifactRef": ref, "dataId": f"resume-{index + 1:04d}"}
                for index, ref in enumerate(resume_refs)
            ],
        }
        write_new(resume_path, json_bytes(resume_submission))
        final_attempt = run_attempt(
            canary_root,
            package,
            1,
            [],
            resume_submission=resume_path.relative_to(package["job"]).as_posix(),
            resume_refs=resume_refs,
        )
        attempts.append(final_attempt)
        attempt_root = package["job"] / final_attempt["request"]["attemptPath"]
        calls = read_provider_calls(package["providerCallLog"])
        result = final_attempt["result"]
        passed = (
            final_attempt["process"].returncode == 0
            and final_attempt["process"].stderr == ""
            and final_attempt["stdoutStates"] == ["starting", "completed"]
            and result["status"] == "completed"
            and [item["itemId"] for item in result["itemDiagnostics"]] == expected_order
            and len(result["outputs"]) == len(expected_order) * 3 + 1
            and (attempt_root / "resume.json").is_file()
            and not (attempt_root / "submission.json").exists()
            and len(calls) == 1
            and calls[0]["event"] == "resume-poll"
            and calls[0]["fileNames"] == [package["fileNameByRef"][ref] for ref in resume_refs]
        )
        recovery = {
            "resumeRefs": resume_refs,
            "providerCalls": calls,
            "resumeReceiptPublished": (attempt_root / "resume.json").is_file(),
            "newSubmissionPublished": (attempt_root / "submission.json").exists(),
            "finalStatus": result["status"],
        }
    elif scenario == "original":
        final_attempt = run_attempt(canary_root, package, 1, [])
        attempts.append(final_attempt)
        result = final_attempt["result"]
        attempt_root = package["job"] / final_attempt["request"]["attemptPath"]
        provider_called = package["providerCallLog"].exists()
        expected_kind = fixture["scenarios"]["original"]["expectedOutputKind"]
        output_kinds = [output["kind"] for output in result["outputs"]]
        media_bundle = module_output_payload(package, final_attempt["moduleResult"], "image.original.bundle")
        provenance = module_output_payload(package, final_attempt["moduleResult"], "image.processing.provenance")
        fixture_by_ref = {item["artifactRef"]: item for item in fixture["images"]}
        bundled_images = media_bundle["questionImages"] + media_bundle["answerImages"]
        bundle_hashes_match = all(item["sha256"] == fixture_by_ref[item["artifactRef"]]["sha256"] for item in bundled_images)
        bundle_order_matches = (
            [item["artifactRef"] for item in media_bundle["questionImages"]] == fixture["questionImages"]
            and [item["artifactRef"] for item in media_bundle["answerImages"]] == fixture["answerImages"]
        )
        passed = (
            final_attempt["process"].returncode == 0
            and final_attempt["process"].stderr == ""
            and final_attempt["stdoutStates"] == ["starting", "completed"]
            and result["status"] == "completed"
            and result["payload"].get("mode") == "original_passthrough"
            and [item["itemId"] for item in result["itemDiagnostics"]] == expected_order
            and len(result["outputs"]) == len(expected_order) + 2
            and output_kinds.count(expected_kind) == len(expected_order)
            and output_kinds.count("image.original.bundle") == 1
            and output_kinds.count("image.processing.provenance") == 1
            and media_bundle["providerCalls"] == 0
            and bundle_hashes_match
            and bundle_order_matches
            and provenance["provider"]["calls"] == 0
            and provenance["continuation"]["defaultOcrRerun"] is False
            and not provider_called
            and not (attempt_root / "submission.json").exists()
            and not (attempt_root / "resume.json").exists()
        )
        recovery = {
            "providerCalled": provider_called,
            "submissionPublished": (attempt_root / "submission.json").exists(),
            "resumeReceiptPublished": (attempt_root / "resume.json").exists(),
            "outputKinds": output_kinds,
            "mediaBundle": media_bundle,
            "bundleHashesMatch": bundle_hashes_match,
            "bundleOrderMatches": bundle_order_matches,
            "provenance": provenance,
            "finalStatus": result["status"],
        }
    elif scenario == "provider-failed":
        failure_refs = fixture["scenarios"][scenario]["failureRefs"]
        final_attempt = run_attempt(canary_root, package, 1, failure_refs)
        attempts.append(final_attempt)
        result = final_attempt["result"]
        statuses = item_statuses(result)
        calls = read_provider_calls(package["providerCallLog"])
        expected_error = fixture["scenarios"][scenario]["expectedTopErrorCode"]
        passed = (
            final_attempt["process"].returncode == 1
            and final_attempt["process"].stderr == ""
            and final_attempt["stdoutStates"] == ["starting", "failed"]
            and result["status"] == "failed"
            and result["outputs"] == []
            and [item["itemId"] for item in result["itemDiagnostics"]] == expected_order
            and all(statuses[ref] == "failed" for ref in failure_refs)
            and result["error"]["code"] == expected_error
            and len(calls) == 1
            and calls[0]["fileNames"] == [package["fileNameByRef"][ref] for ref in expected_order]
        )
        recovery = {
            "providerCalls": calls,
            "failedRefs": failure_refs,
            "topErrorCode": result["error"]["code"],
            "commonOutputs": len(result["outputs"]),
            "finalStatus": result["status"],
        }
    elif scenario == "tampered-input":
        final_attempt = run_attempt(canary_root, package, 1, [])
        attempts.append(final_attempt)
        result = final_attempt["result"]
        provider_called = package["providerCallLog"].exists()
        expected_error = fixture["scenarios"][scenario]["expectedErrorCode"]
        passed = (
            final_attempt["process"].returncode == 1
            and final_attempt["process"].stderr == ""
            and final_attempt["stdoutStates"] == ["starting", "failed"]
            and result["status"] == "failed"
            and result["outputs"] == []
            and result["itemDiagnostics"] == []
            and result["error"]["code"] == expected_error
            and final_attempt["moduleResult"] is not None
            and final_attempt["moduleResult"]["status"] == "failed"
            and final_attempt["moduleResult"]["error"]["code"] == expected_error
            and not provider_called
        )
        recovery = {
            "tamperedArtifactRef": tamper_ref,
            "providerCalled": provider_called,
            "moduleResultPublished": final_attempt["moduleResult"] is not None,
            "moduleErrorCode": final_attempt["moduleResult"]["error"]["code"] if final_attempt["moduleResult"] else None,
            "moduleMode": final_attempt["moduleResult"].get("mode") if final_attempt["moduleResult"] else None,
            "topErrorCode": result["error"]["code"],
            "validationBoundary": "module-input-integrity-before-provider-call",
            "commonOutputs": len(result["outputs"]),
            "finalStatus": result["status"],
        }
    else:
        expected = fixture["scenarios"][scenario]
        final_attempt = run_attempt(canary_root, package, 1, [], submit_unknown=True)
        attempts.append(final_attempt)
        result = final_attempt["result"]
        attempt_root = package["job"] / final_attempt["request"]["attemptPath"]
        calls = read_provider_calls(package["providerCallLog"])
        passed = (
            final_attempt["process"].returncode == 1
            and final_attempt["process"].stderr == ""
            and final_attempt["stdoutStates"] == ["starting", "unknown_external_state"]
            and result["status"] == expected["expectedStatus"]
            and result["error"]["code"] == expected["expectedErrorCode"]
            and result["error"]["retryable"] is expected["expectedRetryable"]
            and len(result["outputs"]) == expected["expectedCommonOutputs"]
            and result["itemDiagnostics"] == []
            and len(calls) == 1
            and calls[0]["event"] == "submit-ambiguous"
            and not (attempt_root / "submission.json").exists()
        )
        recovery = {
            "providerCalls": calls,
            "submissionPublished": (attempt_root / "submission.json").exists(),
            "topErrorCode": result["error"]["code"],
            "retryable": result["error"]["retryable"],
            "commonOutputs": len(result["outputs"]),
            "finalStatus": result["status"],
        }
    text_files = [path for path in package["job"].rglob("*") if path.is_file() and path.suffix in {".json", ".md", ".txt", ".ndjson"}]
    secret_leak = any(FAKE_TOKEN in path.read_text(encoding="utf-8") for path in text_files)
    passed = passed and not secret_leak
    final_result = final_attempt["result"]
    receipt = {
        "schemaVersion": "image-ocr-offline-canary-receipt.v4",
        "canaryId": canary_root.name,
        "createdAt": utc_now(),
        "scenario": scenario,
        "status": "passed" if passed else "failed",
        "worker": {
            "id": final_result["worker"]["id"],
            "version": final_result["worker"]["version"],
            "candidateEntrypoint": EXE.relative_to(PROJECT_ROOT).as_posix(),
            "exeSha256": sha256_file(EXE),
            "legacyCompatibilityAliasUsed": False,
            "destinationNeutral": True,
        },
        "fixture": {"fixtureId": fixture["fixtureId"], "path": fixture_path.relative_to(PROJECT_ROOT).as_posix(), "sha256": sha256_file(fixture_path), "dataClass": fixture["dataClass"]},
        "packagePath": canary_root.relative_to(PROJECT_ROOT).as_posix(),
        "input": {"artifactsArrayOrder": fixture["artifactsArrayOrder"], "questionImages": fixture["questionImages"], "answerImages": fixture["answerImages"]},
        "attempts": [attempt_receipt(canary_root, attempt) for attempt in attempts],
        "recovery": recovery,
        "finalResult": {"path": final_attempt["resultPath"].relative_to(canary_root).as_posix(), "sha256": sha256_file(final_attempt["resultPath"]), "status": final_result["status"], "orderedItems": [item["itemId"] for item in final_result["itemDiagnostics"]], "outputs": final_result["outputs"], "markdownPreviews": markdown_previews(package, final_attempt["moduleResult"]) if final_attempt["moduleResult"] is not None else []},
        "checks": {"fixtureSchema": True, "fixtureIntegrity": scenario != "tampered-input", "captureSchema": True, "workerRequestSchemas": True, "workerResultSchemas": True, "artifactSchemas": True, "explicitRoleOrder": [item["itemId"] for item in final_result["itemDiagnostics"]] == expected_order if scenario not in {"tampered-input", "unknown-external-state"} else final_result["itemDiagnostics"] == [], "providerReturnedReverseOrder": scenario in {"success", "partial-retry", "resume", "provider-failed"}, "secretLeakDetected": secret_leak},
    }
    return receipt, passed


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run immutable offline image/OCR worker Canaries")
    parser.add_argument("--scenario", choices=["success", "partial-retry", "resume", "original", "provider-failed", "tampered-input", "unknown-external-state"], default="success")
    parser.add_argument("--fixture", type=Path, default=DEFAULT_FIXTURE)
    parser.add_argument("--worker", type=Path, default=EXE)
    return parser.parse_args()


def main() -> int:
    global EXE
    args = parse_args()
    EXE = args.worker.resolve()
    if not EXE.is_file():
        raise RuntimeError("Planned image/OCR worker EXE is missing")
    fixture_path = args.fixture.resolve()
    fixture = load_fixture(fixture_path)
    prefix = {"success": "iof", "partial-retry": "iof-recovery", "resume": "iof-resume", "original": "iof-original", "provider-failed": "iof-provider-failed", "tampered-input": "iof-tampered-input", "unknown-external-state": "iof-unknown-external"}[args.scenario]
    canary_id = f"{prefix}-{dt.datetime.now().strftime('%Y%m%d-%H%M%S')}-{uuid.uuid4().hex[:8]}"
    canary_root = PROJECT_ROOT / "canary" / canary_id
    canary_root.mkdir(parents=True)
    try:
        receipt, passed = run_scenario(canary_root, fixture_path, fixture, args.scenario)
        receipt_path = canary_root / "canary-receipt.json"
        write_new(receipt_path, json_bytes(receipt))
        print(json.dumps({"status": receipt["status"], "scenario": args.scenario, "canaryId": canary_id, "attempts": [{"attemptId": attempt["attemptId"], "status": attempt["status"], "outputs": attempt["outputs"], "itemStatuses": attempt["itemStatuses"]} for attempt in receipt["attempts"]], "recovery": receipt["recovery"], "finalMarkdownPreviews": receipt["finalResult"]["markdownPreviews"], "receipt": str(receipt_path)}, ensure_ascii=False, indent=2))
        return 0 if passed else 1
    except BaseException as exc:
        diagnostic_path = canary_root / "failure-diagnostic.json"
        if not diagnostic_path.exists():
            write_new(diagnostic_path, json_bytes({"schemaVersion": "image-ocr-offline-canary-failure.v1", "canaryId": canary_id, "createdAt": utc_now(), "scenario": args.scenario, "status": "failed", "errorType": type(exc).__name__, "message": str(exc), "traceback": traceback.format_exc()}))
        print(json.dumps({"status": "failed", "scenario": args.scenario, "diagnostic": str(diagnostic_path), "error": str(exc)}, ensure_ascii=False), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
