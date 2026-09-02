#!/usr/bin/env python3
"""Validate a real authority-v1.2.2 Core projection with the current Anki candidate, without AnkiConnect."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


WORKER_ROOT = Path(__file__).resolve().parent
PROJECT_ROOT = WORKER_ROOT.parent.parent
sys.path.insert(0, str(WORKER_ROOT))
import anki_exporter as exporter


EXE = PROJECT_ROOT / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"


def read_json(path: Path) -> dict[str, Any]:
    with open(exporter.native_fs_path(path), "r", encoding="utf-8") as stream:
        value = json.load(stream)
    if not isinstance(value, dict):
        raise RuntimeError(f"Expected object: {path}")
    return value


def sha(path: Path) -> str:
    return exporter.sha256_file(path).upper()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-attempt", type=Path, required=True)
    parser.add_argument("--evidence-root", type=Path)
    return parser.parse_args()


def find_job_root(attempt: Path) -> Path:
    current = attempt.resolve()
    while current != current.parent:
        if os.path.isfile(exporter.native_fs_path(current / "task.manifest.v1.json")):
            return current
        current = current.parent
    raise RuntimeError("source attempt is not inside a job with task.manifest.v1.json")


def main() -> int:
    args = parse_args()
    source_attempt = args.source_attempt.resolve()
    job_root = find_job_root(source_attempt)
    source_request = read_json(source_attempt / "request.json")
    source_result = read_json(source_attempt / "result.json")
    if source_result.get("status") != "failed" or source_result.get("error", {}).get("code") != "ANCESTRY_PATH_INVALID":
        raise RuntimeError("source attempt is not the preserved 1.1.4 flat-artifact rejection")
    if source_result.get("sideEffects", [{}])[0].get("state") != "none":
        raise RuntimeError("source attempt does not prove a no-effect rejection")
    card_ref = next(item for item in source_request["inputRefs"] if item["kind"] == "card-bundle")
    card_manifest = read_json(job_root / card_ref["manifestPath"])
    card_bundle = read_json(job_root / card_manifest["metadata"]["entrypoint"])
    card_count = len(card_bundle.get("cards", []))
    if card_count != 4:
        raise RuntimeError(f"real Core projection does not contain four cards: {card_count}")

    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    evidence_root = (args.evidence_root or (WORKER_ROOT / "canary" / "authority-v1.2.2-fixed-consumer" / stamp)).resolve()
    evidence_root.mkdir(parents=True, exist_ok=False)
    attempt_id = "attempt-anki-115-validate-" + uuid.uuid4().hex[:12]
    run_id = source_request["runId"]
    attempt_relative = f"runs/{run_id}/stages/anki-export/attempts/{attempt_id}"
    request = dict(source_request)
    request.update({
        "requestId": "request-" + uuid.uuid4().hex,
        "operationId": "operation-" + uuid.uuid4().hex,
        "attemptId": attempt_id,
        "idempotencyKey": exporter.sha256_bytes(exporter.json_bytes({"source": sha(source_attempt / "result.json"), "mode": "anki-1.1.5-validate-only"})),
        "attemptPath": attempt_relative,
        "outputPath": f"{attempt_relative}/outputs",
        "progressPath": f"{attempt_relative}/progress.ndjson",
        "resultPath": f"{attempt_relative}/result.json",
        "cancelSignal": {"schemaVersion": "1.0.0", "path": f"{attempt_relative}/cancel.json", "gracePeriodMs": 2000},
        "payload": {**source_request.get("payload", {}), "validateOnly": True, "canaryMode": "authority-v1.2.2-flat-artifact-validate-only"},
    })
    request_path = job_root / attempt_relative / "request.json"
    exporter.write_json_atomic_once(request_path, request)
    process = subprocess.run([str(EXE), "run", "--request", str(request_path)], cwd=EXE.parent, capture_output=True, text=True, encoding="utf-8", check=False)
    stdout_path = evidence_root / "worker.stdout.json"
    stderr_path = evidence_root / "worker.stderr.log"
    stdout_path.write_text(process.stdout, encoding="utf-8", newline="\n")
    stderr_path.write_text(process.stderr, encoding="utf-8", newline="\n")
    result_path = job_root / request["resultPath"]
    result = read_json(result_path)
    module_receipt_path = job_root / request["outputPath"] / "receipt.json"
    module_receipt = read_json(module_receipt_path)
    operations_path = job_root / request["outputPath"] / "operations.ndjson"
    if process.returncode != 0 or result.get("status") != "completed" or result.get("worker") != {"id": "export.anki", "version": "1.1.5"}:
        raise RuntimeError(f"1.1.5 production-shape validate-only failed: exit={process.returncode} result={result!r}")
    if any(module_receipt.get("counts", {}).values()) or os.path.exists(exporter.native_fs_path(operations_path)):
        raise RuntimeError("validate-only published outcome counts or Anki operations")
    receipt = {
        "schemaVersion": "export.anki-authority-v122-flat-artifact-acceptance/1.0.0",
        "status": "passed", "canaryId": stamp, "external_api_calls": 0,
        "boundary": "Module candidate acceptance only; authority v1.2.2 still pins export.anki 1.1.4 and is not modified.",
        "sourceAuthorityAttempt": {
            "path": source_attempt.relative_to(PROJECT_ROOT).as_posix(), "resultSha256": sha(source_attempt / "result.json"),
            "workerVersion": "1.1.4", "failureCode": "ANCESTRY_PATH_INVALID", "sideEffects": "none",
        },
        "candidate": {"path": EXE.relative_to(PROJECT_ROOT).as_posix(), "sha256": sha(EXE), "bytes": EXE.stat().st_size, "workerId": "export.anki", "workerVersion": "1.1.5"},
        "validateOnly": {
            "requestPath": request_path.relative_to(PROJECT_ROOT).as_posix(), "requestSha256": sha(request_path),
            "resultPath": result_path.relative_to(PROJECT_ROOT).as_posix(), "resultSha256": sha(result_path),
            "moduleReceiptPath": module_receipt_path.relative_to(PROJECT_ROOT).as_posix(), "moduleReceiptSha256": sha(module_receipt_path),
            "cardCount": card_count, "counts": module_receipt["counts"], "operationsPublished": False,
        },
        "checks": {"realCoreDerivedArtifact": True, "flatCommonManifestAccepted": True, "fourCardsValidated": True, "noAnkiConnect": True},
        "finishedAt": exporter.utc_now(),
    }
    receipt_path = evidence_root / "receipt.json"
    exporter.write_json_atomic_once(receipt_path, receipt)
    print(json.dumps({"status": "passed", "receipt": str(receipt_path), "receiptSha256": sha(receipt_path), "exeSha256": sha(EXE), "external_api_calls": 0}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
