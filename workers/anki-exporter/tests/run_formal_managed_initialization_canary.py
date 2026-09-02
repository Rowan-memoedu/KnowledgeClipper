#!/usr/bin/env python3
"""Offline formal-EXE qualification for explicit managed initialization."""

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
from test_anki_exporter import FakeAnkiServer, MODEL_FIELDS, dump_json, validate_common
from test_anki_settings_and_probe import build_initialize_job


FORBIDDEN = {"storeMediaFile", "addNote", "updateNoteFields", "addTags", "changeDeck"}


def relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def run_exe(exe: Path, release_root: Path, command: str, request: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [str(exe), command, "--request", str(request.resolve())],
        cwd=release_root,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--release-root", required=True)
    args = parser.parse_args()
    release_root = Path(args.release_root).resolve()
    exe = release_root / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
    descriptor = json.loads(subprocess.run(
        [str(exe), "describe", "--json"], cwd=release_root, capture_output=True,
        text=True, encoding="utf-8", check=True,
    ).stdout)
    if descriptor.get("workerId") != "export.anki" or descriptor.get("workerVersion") != "1.4.0":
        raise RuntimeError(f"Unexpected formal identity: {descriptor!r}")
    if "anki-managed-initialization-status" not in descriptor.get("outputKinds", []):
        raise RuntimeError("Formal descriptor omitted initialization output")

    canary_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-formal-v140-managed-init"
    root = WORKER / "canary" / "formal-managed-initialization" / canary_id
    with FakeAnkiServer() as fake:
        unauthorized_job = build_initialize_job(root / "00-unauthorized", fake.endpoint)
        unauthorized_request_path = unauthorized_job["root"] / unauthorized_job["request"]
        unauthorized_request = exporter.load_json(unauthorized_request_path)
        unauthorized_request["payload"] = {}
        dump_json(unauthorized_request_path, unauthorized_request)
        unauthorized = run_exe(exe, release_root, exporter.INITIALIZE_OPERATION, unauthorized_request_path)
        if unauthorized.returncode != 2 or fake.state.calls:
            raise RuntimeError(f"Unauthorized initialization was not rejected before provider access: {unauthorized!r}")
        if (unauthorized_job["root"] / unauthorized_request["resultPath"]).exists():
            raise RuntimeError("Unauthorized constructor rejection unexpectedly published a worker result")

        first_job = build_initialize_job(root / "01-create", fake.endpoint)
        first_request_path = first_job["root"] / first_job["request"]
        first = run_exe(exe, release_root, exporter.INITIALIZE_OPERATION, first_request_path)
        if first.returncode != 0:
            raise RuntimeError(f"Formal initialization failed: stdout={first.stdout!r}, stderr={first.stderr!r}")
        first_request = exporter.load_json(first_request_path)
        first_result_path = first_job["root"] / first_request["resultPath"]
        validate_common(first_result_path, "worker-result.schema.json")
        first_result = exporter.load_json(first_result_path)
        first_manifest_path = first_job["root"] / first_result["outputs"][0]["manifestPath"]
        first_manifest = exporter.load_json(first_manifest_path)
        first_status_path = first_job["root"] / first_manifest["metadata"]["statusPath"]
        first_status = exporter.load_json(first_status_path)
        if first_status["status"] != "initialized" or first_status["writes"] != {"modelsCreated": 1, "decksCreated": 1}:
            raise RuntimeError(f"Unexpected first initialization status: {first_status!r}")
        if fake.state.notes or fake.state.media:
            raise RuntimeError("Managed initialization touched notes or media")
        if fake.state.models.get("RQC Basic v1") != MODEL_FIELDS or "零碎知识剪藏::Canary" not in fake.state.decks:
            raise RuntimeError("Managed model/deck exact readback did not converge")
        first_actions = [item["action"] for item in fake.state.calls]
        if set(first_actions) & FORBIDDEN or first_actions.count("createModel") != 1 or first_actions.count("createDeck") != 1:
            raise RuntimeError(f"Formal initialization action scope mismatch: {first_actions}")

        replay_offset = len(fake.state.calls)
        replay_job = build_initialize_job(root / "02-explicit-replay", fake.endpoint)
        replay_request_path = replay_job["root"] / replay_job["request"]
        replay = run_exe(exe, release_root, "run", replay_request_path)
        if replay.returncode != 0:
            raise RuntimeError(f"Formal replay failed: stdout={replay.stdout!r}, stderr={replay.stderr!r}")
        replay_request = exporter.load_json(replay_request_path)
        replay_result_path = replay_job["root"] / replay_request["resultPath"]
        validate_common(replay_result_path, "worker-result.schema.json")
        replay_result = exporter.load_json(replay_result_path)
        replay_manifest = exporter.load_json(replay_job["root"] / replay_result["outputs"][0]["manifestPath"])
        replay_status_path = replay_job["root"] / replay_manifest["metadata"]["statusPath"]
        replay_status = exporter.load_json(replay_status_path)
        replay_actions = [item["action"] for item in fake.state.calls[replay_offset:]]
        if replay_status["status"] != "already_current" or replay_status["writes"] != {"modelsCreated": 0, "decksCreated": 0}:
            raise RuntimeError(f"Explicit replay was not a no-op: {replay_status!r}")
        if set(replay_actions) & ({"createModel", "createDeck"} | FORBIDDEN):
            raise RuntimeError(f"Explicit replay mutated provider: {replay_actions}")

    readable_path = root / "readable-result.md"
    readable_path.parent.mkdir(parents=True, exist_ok=True)
    readable_path.write_text("\n".join([
        "# export.anki 1.4.0 managed initialization（Fake Anki）",
        "",
        "- 未授权请求：在 provider 连接前拒绝；调用数 0。",
        "- 显式授权首次执行：创建 1 个缺失 managed model、1 个缺失 deck；逐项精确 readback。",
        "- notes/media/既有资源修改：0。",
        "- 同配置显式重放：already_current；createModel/createDeck 均为 0。",
        "- 响应丢失：单元回归要求 unknown_external_state、retryable=false，只允许新显式任务包 reconcile。",
        "- 真实 AnkiConnect：未调用。",
        "",
    ]), encoding="utf-8", newline="\n")
    receipt = {
        "schemaVersion": "export.anki.formal-managed-initialization-canary.v1",
        "status": "passed",
        "externalProvider": "loopback-fake-anki",
        "realAnkiConnectCalls": 0,
        "worker": {"id": descriptor["workerId"], "version": descriptor["workerVersion"]},
        "release": {"root": relative(release_root), "exe": relative(exe), "exeSha256": exporter.sha256_file(exe).upper()},
        "authorizationNegative": {
            "request": relative(unauthorized_request_path),
            "requestSha256": exporter.sha256_file(unauthorized_request_path).upper(),
            "exitCode": unauthorized.returncode,
            "providerCalls": 0,
            "externalMutation": False,
        },
        "firstRun": {
            "request": relative(first_request_path), "requestSha256": exporter.sha256_file(first_request_path).upper(),
            "result": relative(first_result_path), "resultSha256": exporter.sha256_file(first_result_path).upper(),
            "status": relative(first_status_path), "statusSha256": exporter.sha256_file(first_status_path).upper(),
            "outcome": first_status["status"], "writes": first_status["writes"],
            "createModelCalls": first_actions.count("createModel"), "createDeckCalls": first_actions.count("createDeck"),
        },
        "explicitReplay": {
            "request": relative(replay_request_path), "requestSha256": exporter.sha256_file(replay_request_path).upper(),
            "result": relative(replay_result_path), "resultSha256": exporter.sha256_file(replay_result_path).upper(),
            "status": relative(replay_status_path), "statusSha256": exporter.sha256_file(replay_status_path).upper(),
            "outcome": replay_status["status"], "writes": replay_status["writes"],
            "createModelCalls": replay_actions.count("createModel"), "createDeckCalls": replay_actions.count("createDeck"),
        },
        "safety": {
            "forbiddenActions": sorted(FORBIDDEN),
            "forbiddenActionsObserved": [],
            "notesCreatedOrUpdated": 0,
            "mediaStored": 0,
            "existingModelsOrDecksModified": 0,
            "automaticRetryAfterUnknownState": False,
        },
        "readable": relative(readable_path),
        "readableSha256": exporter.sha256_file(readable_path).upper(),
        "createdAt": exporter.utc_now(),
    }
    receipt_path = root / "receipt.json"
    exporter.write_json_atomic_once(receipt_path, receipt)
    print(json.dumps({
        "status": "passed",
        "receipt": str(receipt_path),
        "receiptSha256": exporter.sha256_file(receipt_path).upper(),
        "readable": str(readable_path),
        "readableSha256": exporter.sha256_file(readable_path).upper(),
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
