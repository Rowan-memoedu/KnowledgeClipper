#!/usr/bin/env python3
"""Publish an append-only blocker receipt for the exact authority-v1.2.2 Anki route."""

from __future__ import annotations

import json
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


WORKER_ROOT = Path(__file__).resolve().parent
PROJECT_ROOT = WORKER_ROOT.parent.parent
sys.path.insert(0, str(WORKER_ROOT))
import anki_exporter as exporter
import run_authority_v122_idempotent_canary as authority


EXACT_EVIDENCE = WORKER_ROOT / "canary" / "authority-v1.2.2" / "20260828T015849Z-dd7eed30"
FIXED_ACCEPTANCE = WORKER_ROOT / "canary" / "authority-v1.2.2-fixed-consumer" / "20260828T020520Z-179db319" / "receipt.json"
EXE_ACCEPTANCE = PROJECT_ROOT / "workers" / "anki" / "canary" / "xv-20260828T020315-502c" / "exe-validation-result.json"
EXACT_ANKI_ATTEMPT = EXACT_EVIDENCE / "job" / "runs" / "run-authority-anki" / "stages" / "anki-export" / "attempts" / "attempt-20260828015858342-e34518a3709e480193e79f5a54db9bca"
UNKNOWN_ATTEMPT = WORKER_ROOT / "canary" / "authority-v1.2.2" / "20260828T015523Z-0f53ae6d" / "job" / "runs" / "run-authority-anki" / "stages" / "anki-export" / "attempts" / "attempt-20260828015531839-9b8fd34a8bc34e6d81c603f67e312520"


def read_json(path: Path) -> dict[str, Any]:
    with open(exporter.native_fs_path(path), "r", encoding="utf-8") as stream:
        value = json.load(stream)
    if not isinstance(value, dict):
        raise RuntimeError(f"Expected object: {path}")
    return value


def sha(path: Path) -> str:
    return exporter.sha256_file(path).upper()


def rel(path: Path) -> str:
    return path.resolve().relative_to(PROJECT_ROOT.resolve()).as_posix()


def one_result(run_id: str, stage_id: str) -> tuple[Path, dict[str, Any]]:
    return authority.stage_result(EXACT_EVIDENCE / "job", run_id, stage_id, "content.deepseek", "0.5.2")


def main() -> int:
    source = authority.source_artifacts()
    exact_result_path = EXACT_ANKI_ATTEMPT / "result.json"
    exact_result = read_json(exact_result_path)
    unknown_result = read_json(UNKNOWN_ATTEMPT / "result.json")
    fixed = read_json(FIXED_ACCEPTANCE)
    exe_validation = read_json(EXE_ACCEPTANCE)
    if exact_result.get("worker") != {"id": "export.anki", "version": "1.1.4"} or exact_result.get("error", {}).get("code") != "ANCESTRY_PATH_INVALID":
        raise RuntimeError("exact candidate blocker result changed")
    if exact_result.get("sideEffects", [{}])[0].get("state") != "none":
        raise RuntimeError("exact candidate blocker is not proven pre-effect")
    if unknown_result.get("status") != "unknown_external_state" or unknown_result.get("error", {}).get("retryable") is not False:
        raise RuntimeError("initial unknown_external_state evidence changed")
    if fixed.get("status") != "passed" or fixed.get("external_api_calls") != 0:
        raise RuntimeError("1.1.5 real-artifact acceptance is not passed/offline")
    if exe_validation.get("status") != "passed" or exe_validation.get("exe", {}).get("sha256", "").upper() != "4BDD812370D4BA45B45D86DF76A42F90D1CAD9A08D018810C9385BD99BB39774":
        raise RuntimeError("1.1.5 EXE acceptance changed")

    preflight_path, preflight = one_result("run-content-preflight", "content.understand-and-card")
    full_content_path, full_content = one_result("run-authority-anki", "content.understand-and-card")
    client = authority.AnkiConnect("http://127.0.0.1:8765")
    readback = authority.exact_readback(client, source["bundle"], source["destination"]["readback"])
    if len(readback) != 4 or any(row["deck"] != authority.CANARY_DECK for row in readback):
        raise RuntimeError("isolated Canary readback changed")
    tree = authority.release_tree()
    if tree["treeSha256"] != authority.EXPECTED["releaseTreeSha256"]:
        raise RuntimeError("authority release tree changed")

    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    root = WORKER_ROOT / "canary" / "authority-v1.2.2-blocker" / stamp
    root.mkdir(parents=True, exist_ok=False)
    readable = root / "readable-blocker-and-card-state.md"
    semantics = authority.semantic_cards(source["bundle"])
    lines = [
        "# authority v1.2.2 → Anki 候选绑定结论", "",
        "- 结论：`BLOCKED_AUTHORITY_CHECKPOINT`；不能把模块 1.1.5 的通过冒充为 v1.2.2 exact candidate 通过。",
        "- exact candidate：Orchestrator + Content 0.5.2 均实际成功；Core 生成生产形状派生产物；Anki 1.1.4 在 AnkiConnect 前拒绝 `outputs/*.artifact.v1.json` ancestry。",
        "- 当前隔离牌组读回：4 note / 4 card，全部字段不变；本轮新增 0、删除/清理 0。这个读回不是 1.1.4 worker outcome。",
        "- 修复候选：Anki 1.1.5 已对同一真实 Core 产物 validate-only PASS，需新 authority registry/checkpoint 才可做候选绑定真实幂等重放。", "",
    ]
    for index, (card, row) in enumerate(zip(semantics, readback, strict=True), 1):
        lines.extend([
            f"## {index}. `{card['template']}` · `{card['cardId']}`", "",
            f"- 原始/清洗来源：{json.dumps(next(item for item in source['learning']['items'] if item['item_id'] == card['itemId'])['cleaning']['cleaned_text'], ensure_ascii=False)}",
            f"- Front：{json.dumps(card['front'], ensure_ascii=False)}", f"- Back：{json.dumps(card['back'], ensure_ascii=False)}",
            f"- 当前 Anki：existing/readback；note=`{row['noteId']}`；card=`{row['cardIds'][0]}`；deck=`{row['deck']}`", "",
        ])
    with open(exporter.native_fs_path(readable), "x", encoding="utf-8", newline="\n") as stream:
        stream.write("\n".join(lines))

    receipt = {
        "schemaVersion": "authority-v1.2.2-anki-route-blocker/1.0.0", "status": "blocked_authority_checkpoint", "canaryId": stamp,
        "requestedAuthority": {
            "checkpointPath": rel(authority.CHECKPOINT_PATH), "checkpointSha256": sha(authority.CHECKPOINT_PATH),
            "releaseRoot": rel(authority.RELEASE_ROOT), **tree,
            "orchestratorSha256": sha(authority.ORCHESTRATOR), "contentSha256": sha(authority.CONTENT_EXE), "anki114Sha256": sha(authority.ANKI_EXE),
        },
        "exactCandidateExecution": {
            "contentPreflight": {"resultPath": rel(preflight_path), "resultSha256": sha(preflight_path), "worker": preflight["worker"], "status": preflight["status"]},
            "contentFullRun": {"resultPath": rel(full_content_path), "resultSha256": sha(full_content_path), "worker": full_content["worker"], "status": full_content["status"]},
            "anki": {"resultPath": rel(exact_result_path), "resultSha256": sha(exact_result_path), "worker": exact_result["worker"], "status": exact_result["status"], "error": exact_result["error"], "sideEffectState": "none"},
            "candidateBoundPass": False,
        },
        "reconciliationHistory": {
            "initialUnknownAttempt": {"resultPath": rel(UNKNOWN_ATTEMPT / "result.json"), "resultSha256": sha(UNKNOWN_ATTEMPT / "result.json"), "status": "unknown_external_state", "retryable": False},
            "finalReadbackUnchanged": True,
        },
        "moduleFix": {
            "workerId": "export.anki", "workerVersion": "1.1.5", "exePath": "workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe",
            "exeSha256": "4BDD812370D4BA45B45D86DF76A42F90D1CAD9A08D018810C9385BD99BB39774", "bytes": 10213662,
            "unitTests": {"passed": 34, "failed": 0},
            "exeValidationReceipt": {"path": rel(EXE_ACCEPTANCE), "sha256": sha(EXE_ACCEPTANCE)},
            "realCoreArtifactValidateOnlyReceipt": {"path": rel(FIXED_ACCEPTANCE), "sha256": sha(FIXED_ACCEPTANCE), "external_api_calls": 0},
        },
        "destinationReadback": {"deck": authority.CANARY_DECK, "notes": 4, "cards": 4, "newNotes": 0, "deleted": 0, "rows": readback},
        "apiCalls": {"ankiConnectReadOnly": sum(client.counts.values()), "byAction": dict(client.counts), "mutating": 0},
        "blocker": {
            "code": "AUTHORITY_ANKI_114_FLAT_ARTIFACT_INCOMPATIBLE",
            "detail": "Authority-pinned export.anki 1.1.4 rejects the Content 0.5.2 flat common artifact manifest path before AnkiConnect.",
            "requiredResolution": "Append a new authority registry/release checkpoint that pins export.anki 1.1.5, then rerun the exact-candidate idempotent destination Canary.",
        },
        "readableEvidence": {"path": rel(readable), "sha256": sha(readable)}, "finishedAt": exporter.utc_now(),
    }
    receipt_path = root / "receipt.json"
    exporter.write_json_atomic_once(receipt_path, receipt)
    print(json.dumps({"status": receipt["status"], "receipt": str(receipt_path), "receiptSha256": sha(receipt_path), "newNotes": 0, "existingReadback": 4}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
