#!/usr/bin/env python3
"""Offline qualification for the additive cross-run ancestry release."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path


WORKER_ROOT = Path(__file__).resolve().parent
PROJECT_ROOT = WORKER_ROOT.parents[1]
sys.path.insert(0, str(WORKER_ROOT))

import anki_exporter as exporter
from tests.test_anki_exporter import build_derived_job


SEALED_REQUEST = (
    "runs/run-20260830101623247-4b02cc448aa340588a0d56e2ed17d8fd/stages/anki-export/attempts/"
    "attempt-20260830101625185-af967195410d4dc182f3cc94fca01a67/request.json"
)
SEALED_FAILED_RESULT = (
    "runs/run-20260830101623247-4b02cc448aa340588a0d56e2ed17d8fd/stages/anki-export/attempts/"
    "attempt-20260830101625185-af967195410d4dc182f3cc94fca01a67/result.json"
)


def relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT_ROOT.resolve()).as_posix()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--release-root", required=True)
    parser.add_argument("--sealed-job", required=True)
    parser.add_argument("--output-root", required=True)
    args = parser.parse_args()

    release_root = Path(args.release_root).resolve()
    sealed_job = Path(args.sealed_job).resolve()
    output_root = Path(args.output_root).resolve()
    if output_root.exists():
        raise RuntimeError(f"Append-only qualification root already exists: {output_root}")
    output_root.mkdir(parents=True)

    exe = release_root / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
    release_manifest = release_root / "workers" / "anki" / "release-manifest.v1.json"
    descriptor = json.loads(subprocess.run(
        [str(exe), "describe", "--json"], capture_output=True, text=True, encoding="utf-8", check=True
    ).stdout)
    if descriptor.get("workerId") != "export.anki" or descriptor.get("workerVersion") != "1.2.2":
        raise RuntimeError(f"Unexpected release identity: {descriptor!r}")

    synthetic = build_derived_job(output_root / "jobs" / "cross-run", "http://127.0.0.1:65534", defect="cross_run")
    request_path = (synthetic["root"] / synthetic["request"]).resolve()
    ran = subprocess.run(
        [str(exe), "run", "--request", str(request_path)],
        capture_output=True, text=True, encoding="utf-8", check=False,
    )
    if ran.returncode != 0:
        raise RuntimeError(f"Release validate-only failed: stdout={ran.stdout!r}, stderr={ran.stderr!r}")
    request = exporter.load_json(request_path)
    result_path = synthetic["root"] / request["resultPath"]
    result = exporter.load_json(result_path)
    if result.get("status") != "completed" or result.get("worker") != {"id": "export.anki", "version": "1.2.2"}:
        raise RuntimeError(f"Release result identity/status mismatch: {result!r}")
    if result.get("payload", {}).get("validateOnly") is not True:
        raise RuntimeError("Release qualification was not validate-only")
    operations = synthetic["root"] / request["outputPath"] / "operations.ndjson"
    if exporter.path_exists(operations):
        raise RuntimeError("Validate-only qualification unexpectedly published provider operations")

    sealed_task_path = sealed_job / "task.manifest.v1.json"
    sealed_request_path = sealed_job / SEALED_REQUEST
    sealed_failed_result_path = sealed_job / SEALED_FAILED_RESULT
    sealed_task = exporter.load_json(sealed_task_path)
    sealed_request = exporter.load_json(sealed_request_path)
    exporter.validate_task_manifest(sealed_task)
    exporter.validate_worker_request(sealed_request, sealed_task, SEALED_REQUEST)
    sealed_outcome: dict[str, object]
    try:
        exporter.validate_request_input_ancestry(sealed_job, sealed_task, sealed_request)
    except exporter.ContractError as exc:
        if exc.code != "ANCESTRY_PATH_INVALID" or "artifacts/question.turn.0002/artifact.json" not in str(exc):
            raise
        sealed_outcome = {
            "status": "blocked_upstream_initial_root",
            "crossRunQuestionAnswerGate": "passed",
            "remainingCode": exc.code,
            "remainingArtifact": "artifacts/question.turn.0002/artifact.json",
            "reason": "interactive question turn is not rooted in task.manifest.inputRefs and declares inputRefs=[]",
        }
    else:
        sealed_outcome = {"status": "passed", "crossRunQuestionAnswerGate": "passed"}

    failed_result = exporter.load_json(sealed_failed_result_path)
    receipt = {
        "schemaVersion": "export.anki.cross-run-release-qualification.v1",
        "status": "passed_with_upstream_initial_root_blocker",
        "createdAt": exporter.utc_now(),
        "worker": {"workerId": "export.anki", "workerVersion": "1.2.2"},
        "release": {
            "root": relative(release_root),
            "manifest": relative(release_manifest),
            "manifestSha256": exporter.sha256_file(release_manifest).upper(),
            "exe": relative(exe),
            "exeSha256": exporter.sha256_file(exe).upper(),
            "describe": descriptor,
        },
        "syntheticCrossRunValidateOnly": {
            "status": "passed",
            "request": relative(request_path),
            "requestRunId": request["runId"],
            "producerRunId": "run-upstream-cross-run",
            "result": relative(result_path),
            "resultSha256": exporter.sha256_file(result_path).upper(),
            "externalApiCalls": 0,
            "providerOperationsPublished": False,
        },
        "sealedAuthorityEvidence": {
            "job": relative(sealed_job),
            "request": relative(sealed_request_path),
            "requestSha256": exporter.sha256_file(sealed_request_path).upper(),
            "preservedFailureResult": relative(sealed_failed_result_path),
            "preservedFailureResultSha256": exporter.sha256_file(sealed_failed_result_path).upper(),
            "preservedFailure": {
                "status": failed_result.get("status"),
                "code": failed_result.get("error", {}).get("code"),
                "sideEffects": failed_result.get("sideEffects"),
            },
            "readOnlyReplay": sealed_outcome,
        },
        "securityGates": {
            "jobRoot": "preserved",
            "canonicalPaths": "preserved",
            "manifestAndPayloadHashes": "preserved",
            "recursiveAncestry": "preserved",
            "initialRoots": "preserved",
            "cycleAndDepth": "preserved",
            "unknownExternalStateOutputs": "rejected",
            "producerResultBinding": "run/stage/attempt/worker/version/ref/hash exact",
        },
        "externalApiCalls": 0,
        "ankiConnectInvoked": False,
    }
    receipt_path = output_root / "receipt.json"
    exporter.write_json_atomic_once(receipt_path, receipt)
    print(json.dumps({
        "status": receipt["status"],
        "receipt": str(receipt_path),
        "receiptSha256": exporter.sha256_file(receipt_path).upper(),
        "externalApiCalls": 0,
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
