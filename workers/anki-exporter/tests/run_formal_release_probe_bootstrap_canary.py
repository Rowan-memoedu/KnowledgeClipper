#!/usr/bin/env python3
"""Verify the formal EXE publishes a zero-client result for a missing probe task manifest."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path


WORKER = Path(__file__).resolve().parents[1]
PROJECT = WORKER.parents[1]
sys.path.insert(0, str(WORKER))
sys.path.insert(0, str(WORKER / "tests"))

import anki_exporter as exporter
from test_anki_exporter import validate_common
from test_anki_settings_and_probe import build_probe_job


def relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--release-root", required=True)
    args = parser.parse_args()

    release_root = Path(args.release_root).resolve()
    exe = release_root / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
    if not exe.is_file():
        raise RuntimeError(f"Missing formal worker EXE: {exe}")
    descriptor = json.loads(subprocess.run(
        [str(exe), "describe", "--json"],
        cwd=release_root,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=True,
    ).stdout)
    if descriptor.get("workerId") != "export.anki" or descriptor.get("workerVersion") != "1.3.3":
        raise RuntimeError(f"Unexpected formal identity: {descriptor!r}")

    canary_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-formal-v133-bootstrap"
    root = WORKER / "canary" / "probe-bootstrap-contract-failure" / canary_id
    job_root = root / "job"
    job = build_probe_job(job_root, "http://127.0.0.1:65534")
    request_path = (job_root / job["request"]).resolve()
    task_path = (job_root / job["task"]).resolve()
    request = json.loads(request_path.read_text(encoding="utf-8"))
    task_path.unlink()

    completed = subprocess.run(
        [str(exe), "run", "--request", str(request_path)],
        cwd=release_root,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )
    result_path = job_root / request["resultPath"]
    if completed.returncode != 2 or not result_path.is_file():
        raise RuntimeError(
            f"Expected exit 2 plus common result; exit={completed.returncode}, "
            f"stdout={completed.stdout!r}, stderr={completed.stderr!r}"
        )
    result = json.loads(result_path.read_text(encoding="utf-8"))
    validate_common(result_path, "worker-result.schema.json")
    if result.get("status") != "failed" or result.get("error", {}).get("code") != "FILE_MISSING":
        raise RuntimeError(f"Unexpected bootstrap result: {result!r}")
    if result.get("outputs") != [] or result.get("sideEffects") != []:
        raise RuntimeError(f"Bootstrap result claimed outputs or side effects: {result!r}")
    if result.get("payload", {}).get("bootstrapFailure") is not True:
        raise RuntimeError(f"Bootstrap evidence flag missing: {result!r}")
    operations_path = result_path.parent / "outputs" / "operations.ndjson"
    if operations_path.exists():
        raise RuntimeError("Bootstrap failure unexpectedly created an Anki operation log")

    receipt = {
        "schemaVersion": "export.anki.probe-bootstrap-formal-canary.v1",
        "status": "passed",
        "worker": {"id": descriptor["workerId"], "version": descriptor["workerVersion"]},
        "release": {
            "root": relative(release_root),
            "exe": relative(exe),
            "exeSha256": exporter.sha256_file(exe).upper(),
        },
        "fixture": {
            "request": relative(request_path),
            "requestSha256": exporter.sha256_file(request_path).upper(),
            "requiredManifest": relative(task_path),
            "requiredManifestPresent": False,
            "operation": request["operation"],
        },
        "result": {
            "path": relative(result_path),
            "sha256": exporter.sha256_file(result_path).upper(),
            "status": result["status"],
            "code": result["error"]["code"],
            "retryable": result["error"]["retryable"],
            "outputs": result["outputs"],
            "sideEffects": result["sideEffects"],
            "bootstrapFailure": result["payload"]["bootstrapFailure"],
        },
        "safety": {
            "ankiClientConstructed": False,
            "externalApiCalls": 0,
            "externalMutation": False,
            "operationsLogPresent": False,
        },
        "process": {
            "exitCode": completed.returncode,
            "stdoutSha256": exporter.sha256_bytes(completed.stdout.encode("utf-8")).upper(),
            "stderrSha256": exporter.sha256_bytes(completed.stderr.encode("utf-8")).upper(),
        },
        "createdAt": exporter.utc_now(),
    }
    receipt_path = root / "receipt.json"
    exporter.write_json_atomic_once(receipt_path, receipt)
    print(json.dumps({
        "status": "passed",
        "receipt": str(receipt_path),
        "receiptSha256": exporter.sha256_file(receipt_path).upper(),
        "result": str(result_path),
        "resultSha256": exporter.sha256_file(result_path).upper(),
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
