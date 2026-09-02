#!/usr/bin/env python3
"""Run one explicit formal-worker probe against real Anki without mutation."""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlparse


WORKER = Path(__file__).resolve().parents[1]
PROJECT = WORKER.parents[1]
sys.path.insert(0, str(WORKER))
sys.path.insert(0, str(WORKER / "tests"))

import anki_exporter as exporter
from test_anki_exporter import dump_json, validate_common
from test_anki_settings_and_probe import MUTATING, build_probe_job


READ_ONLY_ACTIONS = {
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


def relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--release-root", required=True)
    parser.add_argument("--source-artifact-manifest", required=True)
    args = parser.parse_args()

    release_root = Path(args.release_root).resolve()
    exe = release_root / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
    source_manifest_path = Path(args.source_artifact_manifest).resolve()
    if not exe.is_file() or not source_manifest_path.is_file():
        raise RuntimeError("Formal EXE or source config artifact manifest is missing")
    source_manifest = json.loads(source_manifest_path.read_text(encoding="utf-8"))
    if source_manifest.get("kind") != "anki-export-config" or len(source_manifest.get("files", [])) != 1:
        raise RuntimeError("Source artifact is not one strict anki-export-config")
    source_job_root = source_manifest_path.parents[2]
    source_payload_path = source_job_root / source_manifest["metadata"]["entrypoint"]
    config = json.loads(source_payload_path.read_text(encoding="utf-8"))
    endpoint = urlparse(config["endpoint"])
    if endpoint.scheme != "http" or endpoint.hostname not in {"127.0.0.1", "localhost", "::1"}:
        raise RuntimeError("Live probe accepts loopback HTTP endpoint only")
    if exporter.sha256_file(source_payload_path) != source_manifest["files"][0]["sha256"]:
        raise RuntimeError("Source config payload hash mismatch")

    descriptor = json.loads(subprocess.run(
        [str(exe), "describe", "--json"], cwd=release_root, capture_output=True,
        text=True, encoding="utf-8", check=True,
    ).stdout)
    if descriptor.get("workerId") != "export.anki" or descriptor.get("workerVersion") != "1.3.3":
        raise RuntimeError(f"Unexpected formal identity: {descriptor!r}")

    canary_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-formal-v133-live-read-only"
    root = WORKER / "canary" / "live-settings-probe" / canary_id
    job_root = root / "job"
    job = build_probe_job(job_root, config["endpoint"])

    generated_config = job_root / "artifacts" / "anki-config"
    shutil.rmtree(generated_config)
    target_manifest_path = job_root / source_manifest_path.relative_to(source_job_root)
    shutil.copytree(source_manifest_path.parent, target_manifest_path.parent)
    manifest_sha = exporter.sha256_file(target_manifest_path)
    config_ref = {
        "artifactRef": source_manifest["artifactId"],
        "kind": source_manifest["kind"],
        "contractVersion": source_manifest["contractVersion"],
        "manifestPath": target_manifest_path.relative_to(job_root).as_posix(),
        "expectedSha256": manifest_sha,
    }

    task_path = job_root / job["task"]
    task = json.loads(task_path.read_text(encoding="utf-8"))
    task["inputRefs"] = [{key: config_ref[key] for key in ("artifactRef", "kind", "contractVersion", "expectedSha256")}]
    task["requestedOutputs"] = ["anki-connection-status"]
    task["retentionClass"] = "evidence"
    dump_json(task_path, task)

    request_path = job_root / job["request"]
    request = json.loads(request_path.read_text(encoding="utf-8"))
    request["inputRefs"] = [config_ref]
    request["configSnapshot"] = {"ankiConfigArtifactId": config_ref["artifactRef"]}
    request["payload"] = {"probeMode": "read-only"}
    request["idempotencyKey"] = exporter.sha256_bytes(exporter.json_bytes({"operation": "probe", "config": config_ref}))
    dump_json(request_path, request)

    completed = subprocess.run(
        [str(exe), "run", "--request", str(request_path.resolve())],
        cwd=release_root,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )
    result_path = job_root / request["resultPath"]
    if completed.returncode != 0 or not result_path.is_file():
        raise RuntimeError(
            f"Formal read-only probe failed: exit={completed.returncode}, "
            f"stdout={completed.stdout!r}, stderr={completed.stderr!r}"
        )
    validate_common(result_path, "worker-result.schema.json")
    result = json.loads(result_path.read_text(encoding="utf-8"))
    if result.get("status") != "completed" or result.get("sideEffects") != [] or len(result.get("outputs", [])) != 1:
        raise RuntimeError(f"Unexpected common probe result: {result!r}")
    output_manifest_path = job_root / result["outputs"][0]["manifestPath"]
    output_manifest = json.loads(output_manifest_path.read_text(encoding="utf-8"))
    status_path = job_root / output_manifest["metadata"]["entrypoint"]
    status = json.loads(status_path.read_text(encoding="utf-8"))
    if status.get("externalMutation") is not False or status.get("effectClass") != "provider-read-only":
        raise RuntimeError(f"Probe status is not read-only: {status!r}")

    operations_path = result_path.parent / "outputs" / "operations.ndjson"
    operations = [json.loads(line) for line in operations_path.read_text(encoding="utf-8").splitlines() if line.strip()]
    actions = [item["action"] for item in operations]
    unexpected = sorted(set(actions) - READ_ONLY_ACTIONS)
    mutating = sorted(set(actions) & MUTATING)
    if unexpected or mutating:
        raise RuntimeError(f"Probe called forbidden actions: unexpected={unexpected}, mutating={mutating}")

    receipt = {
        "schemaVersion": "export.anki.live-settings-probe-receipt.v1",
        "status": "passed",
        "scope": "module-owned explicit read-only probe; not Desktop integrated-authority evidence",
        "worker": {"id": descriptor["workerId"], "version": descriptor["workerVersion"]},
        "release": {
            "root": relative(release_root),
            "exe": relative(exe),
            "exeSha256": exporter.sha256_file(exe).upper(),
        },
        "sourceConfig": {
            "artifactId": source_manifest["artifactId"],
            "sourceManifestSha256": exporter.sha256_file(source_manifest_path).upper(),
            "copiedManifest": relative(target_manifest_path),
            "copiedManifestSha256": manifest_sha.upper(),
            "payloadSha256": exporter.sha256_file(source_payload_path).upper(),
            "endpoint": {"scheme": endpoint.scheme, "host": endpoint.hostname, "port": endpoint.port},
            "expectedProfileSha256": exporter.sha256_bytes(config["expected_profile"].encode("utf-8")).upper(),
        },
        "request": {
            "path": relative(request_path),
            "sha256": exporter.sha256_file(request_path).upper(),
            "operation": request["operation"],
            "configSnapshot": request["configSnapshot"],
        },
        "result": {
            "path": relative(result_path),
            "sha256": exporter.sha256_file(result_path).upper(),
            "status": result["status"],
            "sideEffects": result["sideEffects"],
        },
        "connectionStatus": {
            "path": relative(status_path),
            "sha256": exporter.sha256_file(status_path).upper(),
            "status": status["status"],
            "effectClass": status["effectClass"],
            "externalMutation": status["externalMutation"],
            "providerVersion": status.get("apiVersion"),
            "profileMatch": status.get("activeProfileSha256") == exporter.sha256_bytes(config["expected_profile"].encode("utf-8")),
            "modelsReady": sum(1 for item in status.get("models", []) if item.get("status") == "ready"),
            "modelsTotal": len(status.get("models", [])),
            "decksReady": sum(1 for item in status.get("decks", []) if item.get("status") == "ready"),
            "decksMissing": sum(1 for item in status.get("decks", []) if item.get("status") == "missing"),
            "decksTotal": len(status.get("decks", [])),
        },
        "providerCalls": {
            "count": len(actions),
            "actions": actions,
            "mutatingActions": mutating,
        },
        "externalMutation": False,
        "createdAt": exporter.utc_now(),
    }
    receipt_path = root / "receipt.json"
    exporter.write_json_atomic_once(receipt_path, receipt)
    print(json.dumps({
        "status": "passed",
        "connectionStatus": status["status"],
        "providerCalls": len(actions),
        "mutatingActions": mutating,
        "receipt": str(receipt_path),
        "receiptSha256": exporter.sha256_file(receipt_path).upper(),
        "result": str(result_path),
        "resultSha256": exporter.sha256_file(result_path).upper(),
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
