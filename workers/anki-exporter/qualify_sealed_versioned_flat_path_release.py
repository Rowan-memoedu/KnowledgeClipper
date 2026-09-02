#!/usr/bin/env python3
"""Validate-only replay for sealed versioned flat artifact ancestry paths."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path


WORKER_ROOT = Path(__file__).resolve().parent
PROJECT_ROOT = WORKER_ROOT.parents[1]
sys.path.insert(0, str(WORKER_ROOT))

import anki_exporter as exporter
from build_formal_release import tree
from qualify_sealed_initial_alias_release import clone_tree_append_only


def project_relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT_ROOT.resolve()).as_posix()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--sealed-job", required=True)
    parser.add_argument("--failed-request", required=True)
    parser.add_argument("--release-root", required=True)
    parser.add_argument("--output-root", required=True)
    args = parser.parse_args()

    sealed_job = Path(args.sealed_job).resolve()
    failed_request_path = Path(args.failed_request).resolve()
    release_root = Path(args.release_root).resolve()
    output_root = Path(args.output_root).resolve()
    if output_root.exists():
        raise RuntimeError(f"Append-only qualification root already exists: {output_root}")
    output_root.mkdir(parents=True, exist_ok=False)
    replay_job = output_root / "jobs" / "sealed-replay"
    replay_job.parent.mkdir(parents=True)
    clone_tree_append_only(sealed_job, replay_job)

    source_request = exporter.load_json(failed_request_path)
    source_result_path = sealed_job / source_request["resultPath"]
    source_result = exporter.load_json(source_result_path)
    if (
        source_result.get("status") != "failed"
        or source_result.get("error", {}).get("code") != "ANCESTRY_PATH_INVALID"
        or any(item.get("state") != "none" for item in source_result.get("sideEffects", []))
    ):
        raise RuntimeError("Source is not the expected zero-write ANCESTRY_PATH_INVALID failure")
    message = source_result["error"]["message"]
    prefix = "Intermediate artifact path does not match producer stage/attempt: "
    if not message.startswith(prefix):
        raise RuntimeError(f"Unexpected source failure message: {message}")
    rejected_relative = message[len(prefix):]
    match = re.fullmatch(
        r"runs/([A-Za-z0-9][A-Za-z0-9._-]{0,127})/stages/"
        r"([A-Za-z0-9][A-Za-z0-9._-]{0,127})/attempts/"
        r"([A-Za-z0-9][A-Za-z0-9._-]{0,127})/outputs/"
        r"([a-z0-9][a-z0-9._-]{0,127}\.artifact\.v([1-9][0-9]*)\.json)",
        rejected_relative,
    )
    if match is None:
        raise RuntimeError(f"Rejected path is not the intended public versioned-flat shape: {rejected_relative}")
    rejected_manifest_path = sealed_job / rejected_relative
    rejected_manifest = exporter.load_json(rejected_manifest_path)
    producer = rejected_manifest["producer"]
    if producer["stageId"] != match.group(2) or producer["attemptId"] != match.group(3):
        raise RuntimeError("Sealed path does not exactly bind producer stage/attempt")
    if "." not in producer["stageId"] or int(match.group(5)) < 2:
        raise RuntimeError("Sealed path does not cover dotted stage plus additive flat version")

    attempt_id = "attempt-offline-versioned-flat-v132"
    attempt_rel = (
        f"runs/{source_request['runId']}/stages/{source_request['stageId']}/"
        f"attempts/{attempt_id}"
    )
    request = json.loads(json.dumps(source_request))
    request.update({
        "requestId": "request-offline-versioned-flat-v132",
        "operationId": "operation-offline-versioned-flat-v132",
        "attemptId": attempt_id,
        "operation": "validate",
        "idempotencyKey": exporter.sha256_bytes(exporter.json_bytes({
            "sourceRequest": exporter.sha256_file(failed_request_path),
            "workerVersion": exporter.EXPORTER_VERSION,
            "mode": "validate-only",
        })),
        "attemptPath": attempt_rel,
        "outputPath": f"{attempt_rel}/outputs",
        "progressPath": f"{attempt_rel}/progress.ndjson",
        "resultPath": f"{attempt_rel}/result.json",
        "cancelSignal": {
            "schemaVersion": "1.0.0", "path": f"{attempt_rel}/cancel.json", "gracePeriodMs": 2000,
        },
        "payload": {
            "validateOnly": True,
            "replayOfFailedRequestSha256": exporter.sha256_file(failed_request_path),
        },
    })
    request_path = replay_job / attempt_rel / "request.json"
    exporter.write_json_atomic_once(request_path, request)

    exe = release_root / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
    describe = subprocess.run(
        [str(exe), "describe", "--json"], cwd=release_root,
        check=False, capture_output=True, text=True, encoding="utf-8",
    )
    if describe.returncode != 0:
        raise RuntimeError(f"describe failed: {describe.stderr}")
    descriptor = json.loads(describe.stdout)
    if descriptor.get("workerId") != "export.anki" or descriptor.get("workerVersion") != exporter.EXPORTER_VERSION:
        raise RuntimeError(f"Unexpected formal release identity: {descriptor!r}")
    process = subprocess.run(
        [str(exe), "run", "--request", str(request_path.resolve())], cwd=replay_job,
        check=False, capture_output=True, text=True, encoding="utf-8",
    )
    if process.returncode != 0:
        raise RuntimeError(
            f"Sealed validate-only replay failed: exit={process.returncode}; "
            f"stdout={process.stdout!r}; stderr={process.stderr!r}"
        )
    replay_result_path = replay_job / request["resultPath"]
    replay_outputs = replay_job / request["outputPath"]
    replay_result = exporter.load_json(replay_result_path)
    if replay_result.get("status") != "completed" or not replay_result.get("payload", {}).get("validateOnly"):
        raise RuntimeError("Formal release did not complete validate-only replay")
    if exporter.path_exists(replay_outputs / "operations.ndjson"):
        raise RuntimeError("Validate-only replay published AnkiConnect operations")

    release_tree = tree(release_root)
    receipt = {
        "schemaVersion": "export.anki.sealed-versioned-flat-path-qualification.v1",
        "status": "passed",
        "worker": {"id": descriptor["workerId"], "version": descriptor["workerVersion"]},
        "sourceFailure": {
            "job": project_relative(sealed_job),
            "request": project_relative(failed_request_path),
            "requestSha256": exporter.sha256_file(failed_request_path).upper(),
            "result": project_relative(source_result_path),
            "resultSha256": exporter.sha256_file(source_result_path).upper(),
            "status": source_result["status"], "code": source_result["error"]["code"],
            "sideEffects": source_result["sideEffects"],
        },
        "verifiedPath": {
            "manifestPath": rejected_relative,
            "manifestSha256": exporter.sha256_file(rejected_manifest_path).upper(),
            "runId": match.group(1), "stageId": match.group(2), "attemptId": match.group(3),
            "fileName": match.group(4), "flatArtifactVersion": int(match.group(5)),
            "producer": producer,
        },
        "release": {
            "root": project_relative(release_root), "treeSha256": release_tree["treeSha256"],
            "exe": project_relative(exe), "exeSha256": exporter.sha256_file(exe).upper(),
        },
        "replay": {
            "job": project_relative(replay_job), "request": project_relative(request_path),
            "requestSha256": exporter.sha256_file(request_path).upper(),
            "result": project_relative(replay_result_path),
            "resultSha256": exporter.sha256_file(replay_result_path).upper(),
            "workerReceiptSha256": exporter.sha256_file(replay_outputs / "receipt.json").upper(),
            "status": replay_result["status"], "externalApiCalls": 0,
        },
        "negativeGates": [
            "traversal", "percent-encoded-alias", "stage-mismatch", "attempt-mismatch",
            "noncanonical-flat-version", "cross-job-path", "producer-result-mismatch",
        ],
        "realAnkiConnectCalled": False,
        "realDeckWrites": 0,
        "createdAt": exporter.utc_now(),
    }
    receipt_path = output_root / "qualification-receipt.json"
    exporter.write_json_atomic_once(receipt_path, receipt)
    print(json.dumps({
        "status": "passed", "receipt": str(receipt_path),
        "receiptSha256": exporter.sha256_file(receipt_path).upper(),
        "releaseTreeSha256": release_tree["treeSha256"],
        "exeSha256": exporter.sha256_file(exe).upper(),
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
