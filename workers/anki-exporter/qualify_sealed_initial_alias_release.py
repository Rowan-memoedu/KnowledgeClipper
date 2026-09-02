#!/usr/bin/env python3
"""Replay the sealed v1.2.15 package as validate-only against a formal EXE.

The sealed source is never modified. Existing files are hard-linked into a new
append-only qualification job, and a new attempt is added under the same run so
the Core-adapter canonical path remains exact. No AnkiConnect call is allowed.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path


WORKER_ROOT = Path(__file__).resolve().parent
PROJECT_ROOT = WORKER_ROOT.parents[1]
sys.path.insert(0, str(WORKER_ROOT))

import anki_exporter as exporter
from build_formal_release import tree


def project_relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT_ROOT.resolve()).as_posix()


def clone_tree_append_only(source: Path, target: Path) -> None:
    """Clone a tree with Windows extended paths, preferring same-volume links."""
    os.makedirs(exporter.native_fs_path(target), exist_ok=False)
    with os.scandir(exporter.native_fs_path(source)) as entries:
        for entry in entries:
            source_item = source / entry.name
            target_item = target / entry.name
            if entry.is_dir(follow_symlinks=False):
                clone_tree_append_only(source_item, target_item)
                continue
            if not entry.is_file(follow_symlinks=False):
                raise RuntimeError(f"Unsupported sealed package entry: {source_item}")
            try:
                os.link(exporter.native_fs_path(source_item), exporter.native_fs_path(target_item))
            except OSError:
                shutil.copyfile(exporter.native_fs_path(source_item), exporter.native_fs_path(target_item))


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

    failed_request = exporter.load_json(failed_request_path)
    failed_result_path = sealed_job / failed_request["resultPath"]
    failed_result = exporter.load_json(failed_result_path)
    if (
        failed_result.get("status") != "failed"
        or failed_result.get("error", {}).get("code") != "ARTIFACT_FILE_BYPASS"
        or any(item.get("state") != "none" for item in failed_result.get("sideEffects", []))
    ):
        raise RuntimeError("Source failure does not match the sealed zero-write ARTIFACT_FILE_BYPASS evidence")

    attempt_id = "attempt-offline-initial-alias-v131"
    attempt_rel = (
        f"runs/{failed_request['runId']}/stages/{failed_request['stageId']}/"
        f"attempts/{attempt_id}"
    )
    request = json.loads(json.dumps(failed_request))
    request.update({
        "requestId": "request-offline-initial-alias-v131",
        "operationId": "operation-offline-initial-alias-v131",
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
        "payload": {"validateOnly": True, "replayOfFailedRequestSha256": exporter.sha256_file(failed_request_path)},
    })
    request_path = replay_job / attempt_rel / "request.json"
    exporter.write_json_atomic_once(request_path, request)

    exe = release_root / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
    described = subprocess.run(
        [str(exe), "describe", "--json"], cwd=release_root,
        check=False, capture_output=True, text=True, encoding="utf-8",
    )
    if described.returncode != 0:
        raise RuntimeError(f"describe failed: {described.stderr}")
    descriptor = json.loads(described.stdout)
    if descriptor.get("workerId") != "export.anki" or descriptor.get("workerVersion") != exporter.EXPORTER_VERSION:
        raise RuntimeError(f"Unexpected release identity: {descriptor!r}")
    process = subprocess.run(
        [str(exe), "run", "--request", str(request_path.resolve())], cwd=replay_job,
        check=False, capture_output=True, text=True, encoding="utf-8",
    )
    if process.returncode != 0:
        raise RuntimeError(
            f"Sealed validate-only replay failed: exit={process.returncode}; "
            f"stdout={process.stdout!r}; stderr={process.stderr!r}"
        )
    result_path = replay_job / request["resultPath"]
    outputs = replay_job / request["outputPath"]
    result = exporter.load_json(result_path)
    worker_receipt = exporter.load_json(outputs / "receipt.json")
    if result.get("status") != "completed" or not result.get("payload", {}).get("validateOnly"):
        raise RuntimeError("Release did not complete the sealed validate-only replay")
    if (outputs / "operations.ndjson").exists():
        raise RuntimeError("Validate-only replay published provider operations")

    adapter_ref = next(ref for ref in request["inputRefs"] if ref["kind"] == "card-bundle")
    adapter_manifest_path = replay_job / adapter_ref["manifestPath"]
    adapter_manifest = exporter.load_json(adapter_manifest_path)
    initial_ref = next(ref for ref in adapter_manifest["inputRefs"] if ref["artifactRef"] == "capture-original-media")
    initial_manifest_path = replay_job / initial_ref["manifestPath"]
    initial_manifest = exporter.load_json(initial_manifest_path)
    initial_files = {item["path"]: item for item in initial_manifest["files"]}
    transparent_aliases = []
    for parent in adapter_manifest["inputRefs"]:
        if not parent["artifactRef"].startswith("capture.image."):
            continue
        parent_manifest_path = replay_job / parent["manifestPath"]
        parent_manifest = exporter.load_json(parent_manifest_path)
        declared = parent_manifest["files"][0]
        if initial_files.get(declared["path"]) != declared:
            raise RuntimeError(f"Transparent alias is not an exact initial file binding: {parent['artifactRef']}")
        transparent_aliases.append({
            "artifactRef": parent["artifactRef"],
            "manifestPath": parent["manifestPath"],
            "manifestSha256": exporter.sha256_file(parent_manifest_path).upper(),
            "initialFile": declared,
        })
    if len(transparent_aliases) != 3:
        raise RuntimeError(f"Expected three transparent original-image aliases, got {len(transparent_aliases)}")

    adapter_local_media = []
    for item in adapter_manifest["files"]:
        if not item["mediaType"].startswith("image/"):
            continue
        path = replay_job / item["path"]
        if not exporter.path_is_file(path) or exporter.sha256_file(path) != item["sha256"]:
            raise RuntimeError(f"Adapter-local content-addressed media is absent or changed: {item['path']}")
        adapter_local_media.append(item)
    if len(adapter_local_media) != 3:
        raise RuntimeError("Core adapter did not own three localized media files")

    release_tree = tree(release_root)
    receipt = {
        "schemaVersion": "export.anki.sealed-initial-alias-qualification.v1",
        "status": "passed",
        "worker": {"id": descriptor["workerId"], "version": descriptor["workerVersion"]},
        "sourceFailure": {
            "job": project_relative(sealed_job),
            "request": project_relative(failed_request_path),
            "requestSha256": exporter.sha256_file(failed_request_path).upper(),
            "result": project_relative(failed_result_path),
            "resultSha256": exporter.sha256_file(failed_result_path).upper(),
            "status": failed_result["status"], "code": failed_result["error"]["code"],
            "sideEffects": failed_result["sideEffects"],
        },
        "release": {
            "root": project_relative(release_root), "treeSha256": release_tree["treeSha256"],
            "exe": project_relative(exe), "exeSha256": exporter.sha256_file(exe).upper(),
        },
        "replay": {
            "job": project_relative(replay_job), "request": project_relative(request_path),
            "requestSha256": exporter.sha256_file(request_path).upper(),
            "result": project_relative(result_path), "resultSha256": exporter.sha256_file(result_path).upper(),
            "workerReceiptSha256": exporter.sha256_file(outputs / "receipt.json").upper(),
            "status": result["status"], "externalApiCalls": 0, "sideEffects": result["sideEffects"],
        },
        "verifiedInitialRoot": {
            "manifest": project_relative(initial_manifest_path),
            "manifestSha256": exporter.sha256_file(initial_manifest_path).upper(),
            "expectedSha256": initial_ref["expectedSha256"].upper(),
            "canonicalPath": initial_ref["manifestPath"],
        },
        "transparentAliases": transparent_aliases,
        "adapterLocalizedMedia": adapter_local_media,
        "securityPolicy": {
            "allow": "exact path/sha256/bytes/mediaType binding from recursively validated canonical initial artifact",
            "deny": [
                "unmanifested sibling file", "disconnected initial root", "descriptor mismatch",
                "noncanonical initial manifest", "arbitrary job-root path",
            ],
            "finalAdapterOwnershipRequired": True,
        },
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
