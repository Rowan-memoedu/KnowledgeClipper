#!/usr/bin/env python3
"""Offline qualification for the additive original-image Anki destination.

This script never contacts the default AnkiConnect endpoint. It runs the formal
release EXE against validate-only packages and ephemeral in-process Fake
AnkiConnect servers bound to random loopback ports.
"""

from __future__ import annotations

import argparse
import copy
import json
import subprocess
import sys
from pathlib import Path


WORKER_ROOT = Path(__file__).resolve().parent
PROJECT_ROOT = WORKER_ROOT.parents[1]
sys.path.insert(0, str(WORKER_ROOT))
sys.path.insert(0, str(WORKER_ROOT / "tests"))

import anki_exporter as exporter
from build_formal_release import tree
from test_anki_exporter import (
    FakeAnkiServer,
    base_config,
    build_original_media_job,
    mutate_original_media_job,
    promote_original_media_to_core_derived,
)
from test_anki_settings_and_probe import build_probe_job, seed_ready


EXPECTED_GAP_MATRIX_SHA256 = "2E315A73779377C9C787C8D6E2DB2A444A3E0D6B051E0F58F14B75E5E438CDFD"


def project_relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT_ROOT.resolve()).as_posix()


def run_worker(exe: Path, job: dict) -> subprocess.CompletedProcess[str]:
    request = (job["root"] / job["request"]).resolve()
    return subprocess.run(
        [str(exe), "run", "--request", str(request)], cwd=job["root"],
        check=False, capture_output=True, text=True, encoding="utf-8",
    )


def attempt_paths(job: dict) -> tuple[Path, Path, Path]:
    request = json.loads((job["root"] / job["request"]).read_text(encoding="utf-8"))
    result = job["root"] / request["resultPath"]
    outputs = job["root"] / request["outputPath"]
    return result, outputs / "receipt.json", outputs


def load_result(job: dict) -> tuple[dict, dict, Path]:
    result_path, receipt_path, output_root = attempt_paths(job)
    return (
        json.loads(result_path.read_text(encoding="utf-8")),
        json.loads(receipt_path.read_text(encoding="utf-8")),
        output_root,
    )


def assert_process(process: subprocess.CompletedProcess[str], expected: int, label: str) -> None:
    if process.returncode != expected:
        raise RuntimeError(
            f"{label} exit mismatch: expected {expected}, got {process.returncode}; "
            f"stdout={process.stdout!r}; stderr={process.stderr!r}"
        )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--release-root", required=True)
    parser.add_argument("--output-root", required=True)
    parser.add_argument("--gap-matrix", required=True)
    args = parser.parse_args()

    release_root = Path(args.release_root).resolve()
    output_root = Path(args.output_root).resolve()
    gap_matrix = Path(args.gap_matrix).resolve()
    if output_root.exists():
        raise RuntimeError(f"Append-only qualification root already exists: {output_root}")
    output_root.mkdir(parents=True, exist_ok=False)
    exe = release_root / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
    release_manifest = release_root / "workers" / "anki" / "release-manifest.v1.json"
    if not exe.is_file() or not release_manifest.is_file():
        raise RuntimeError("Formal release is incomplete")
    gap_sha = exporter.sha256_file(gap_matrix).upper()
    if gap_sha != EXPECTED_GAP_MATRIX_SHA256:
        raise RuntimeError(f"Gap matrix SHA mismatch: {gap_sha}")

    described = subprocess.run(
        [str(exe), "describe", "--json"], cwd=release_root,
        check=False, capture_output=True, text=True, encoding="utf-8",
    )
    assert_process(described, 0, "describe")
    descriptor = json.loads(described.stdout)
    if descriptor.get("workerId") != "export.anki" or descriptor.get("workerVersion") != exporter.EXPORTER_VERSION:
        raise RuntimeError(f"Unexpected formal worker identity: {descriptor!r}")

    # Strict derived ancestry + validate-only: the dead endpoint is deliberate;
    # success proves that the release did not instantiate AnkiClient.
    validate_job = promote_original_media_to_core_derived(build_original_media_job(
        output_root / "jobs" / "validate-derived", "http://127.0.0.1:65534",
        operation="validate", question_text="题目原图（两张，按捕获顺序）", answer_text="答案原图",
    ))
    validate_process = run_worker(exe, validate_job)
    assert_process(validate_process, 0, "derived validate-only")
    validate_result, validate_receipt, validate_outputs = load_result(validate_job)
    if validate_result["status"] != "completed" or not validate_result["payload"]["validateOnly"]:
        raise RuntimeError("Validate-only did not complete")
    if (validate_outputs / "operations.ndjson").exists():
        raise RuntimeError("Validate-only published provider operations")

    card = validate_job["bundle"]["cards"][0]
    media_by_id = {item["media_id"]: item for item in validate_job["bundle"]["media"]}
    question_names = [
        exporter.media_filename(media_by_id[ref["media_id"]])
        for ref in card["media_refs"] if ref["role"] == "questionImages"
    ]
    answer_names = [
        exporter.media_filename(media_by_id[ref["media_id"]])
        for ref in card["media_refs"] if ref["role"] == "answerImages"
    ]
    expected_front = card["fields"]["question"] + "".join(f'<br><img src="{name}">' for name in question_names)
    expected_back = card["fields"]["answer"] + "".join(f'<br><img src="{name}">' for name in answer_names)

    # Unsupported destination mapping must be a destination failure, never a
    # completed task, and must remain connection-free.
    unsupported_job = build_original_media_job(
        output_root / "jobs" / "unsupported", "http://127.0.0.1:65534", operation="validate",
    )

    def make_legacy(config: dict) -> None:
        config["schema_version"] = "anki-export-config.v1"
        mapping = config["note_types"]["original_image_qa"]
        for key in ("media_projection", "css", "front_template", "back_template"):
            mapping.pop(key, None)

    mutate_original_media_job(unsupported_job, config_mutation=make_legacy)
    unsupported_process = run_worker(exe, unsupported_job)
    assert_process(unsupported_process, 2, "unsupported media")
    unsupported_result, unsupported_receipt, unsupported_outputs = load_result(unsupported_job)
    if unsupported_result["status"] != "failed" or unsupported_result["error"]["code"] != "MEDIA_DESTINATION_UNSUPPORTED":
        raise RuntimeError("Unsupported media was not destination-scoped failed/unsupported")
    if (unsupported_outputs / "operations.ndjson").exists():
        raise RuntimeError("Unsupported media contacted provider")

    # Fake provider write/readback/replay. This exercises provider-facing
    # requests without contacting a user Anki process.
    with FakeAnkiServer() as fake:
        first_job = promote_original_media_to_core_derived(build_original_media_job(
            output_root / "jobs" / "fake-first", fake.endpoint,
            question_text="题目原图（两张，按捕获顺序）", answer_text="答案原图",
        ))
        first_process = run_worker(exe, first_job)
        assert_process(first_process, 0, "fake first export")
        first_result, first_receipt, _ = load_result(first_job)
        note_id = next(iter(fake.state.notes))
        note = copy.deepcopy(fake.state.notes[note_id])
        card_id = note["cards"][0]
        first_calls = copy.deepcopy(fake.state.calls)
        if note["fields"]["Front"] != expected_front or note["fields"]["Back"] != expected_back:
            raise RuntimeError("Fake provider readback fields differ from exact projection")
        if len(fake.state.media) != 3:
            raise RuntimeError("Fake provider did not store exactly three content-addressed images")

        fake.state.calls.clear()
        replay_job = promote_original_media_to_core_derived(build_original_media_job(
            output_root / "jobs" / "fake-replay", fake.endpoint,
            question_text="题目原图（两张，按捕获顺序）", answer_text="答案原图",
        ))
        replay_process = run_worker(exe, replay_job)
        assert_process(replay_process, 0, "fake idempotent replay")
        replay_result, replay_receipt, _ = load_result(replay_job)
        replay_calls = copy.deepcopy(fake.state.calls)
        if replay_receipt["counts"]["already_current"] != 1 or len(fake.state.notes) != 1 or len(fake.state.media) != 3:
            raise RuntimeError("Fake replay was not already_current and cardinality-stable")
        if {item["action"] for item in replay_calls} & exporter.MUTATING_ANKI_ACTIONS:
            raise RuntimeError("Fake idempotent replay issued a mutating action")

    with FakeAnkiServer() as fake:
        collision_job = promote_original_media_to_core_derived(build_original_media_job(
            output_root / "jobs" / "fake-collision", fake.endpoint,
        ))
        collision_item = collision_job["bundle"]["media"][0]
        fake.state.media[exporter.media_filename(collision_item)] = b"different-content"
        collision_process = run_worker(exe, collision_job)
        assert_process(collision_process, 5, "fake same-name collision")
        collision_result, collision_receipt, _ = load_result(collision_job)
        if collision_result["status"] != "failed" or "MEDIA_NAME_COLLISION" not in json.dumps(collision_receipt):
            raise RuntimeError("Same-name/different-content collision was not rejected")

    with FakeAnkiServer() as fake:
        fake.state.fail_after_store_once = True
        unknown_job = promote_original_media_to_core_derived(build_original_media_job(
            output_root / "jobs" / "fake-unknown-state", fake.endpoint,
        ))
        unknown_process = run_worker(exe, unknown_job)
        assert_process(unknown_process, 5, "fake response loss")
        unknown_result, unknown_receipt, _ = load_result(unknown_job)
        if (
            unknown_result["status"] != "unknown_external_state"
            or unknown_result["error"]["retryable"]
            or unknown_result["outputs"]
        ):
            raise RuntimeError("Response loss did not preserve unknown_external_state semantics")

    with FakeAnkiServer() as fake:
        probe_job = build_probe_job(output_root / "jobs" / "fake-read-only-probe", fake.endpoint)
        probe_config = base_config(fake.endpoint)
        seed_ready(fake.state, probe_config)
        before = copy.deepcopy((fake.state.models, fake.state.decks, fake.state.media, fake.state.notes))
        probe_process = run_worker(exe, probe_job)
        assert_process(probe_process, 0, "fake read-only probe")
        probe_result, probe_receipt, probe_outputs = load_result(probe_job)
        after = (fake.state.models, fake.state.decks, fake.state.media, fake.state.notes)
        probe_calls = copy.deepcopy(fake.state.calls)
        status = json.loads((probe_outputs / "connection-status.json").read_text(encoding="utf-8"))
        if before != after or status["externalMutation"] or status["mutatingActionsObserved"]:
            raise RuntimeError("Probe mutated Fake provider state")
        if {item["action"] for item in probe_calls} & exporter.MUTATING_ANKI_ACTIONS:
            raise RuntimeError("Probe issued a mutating action")

    source_manifest_path = validate_job["root"] / validate_job["source_manifest"]
    derived_manifest_path = validate_job["root"] / validate_job["derived_ref"]["manifestPath"]
    derived_manifest = json.loads(derived_manifest_path.read_text(encoding="utf-8"))
    release_tree = tree(release_root)

    readable = output_root / "original-input-to-anki-fields.md"
    readable_text = f"""# export.anki {exporter.EXPORTER_VERSION} 原图媒体离线验收

## 原始输入与可信派生

- 初始 CaptureBundle：`{project_relative(source_manifest_path)}`
- 初始 artifact SHA-256：`{exporter.sha256_file(source_manifest_path).upper()}`
- Core adapter 派生 manifest：`{project_relative(derived_manifest_path)}`
- 派生 manifest SHA-256：`{exporter.sha256_file(derived_manifest_path).upper()}`
- producer：`orchestrator.core-adapter / core-adapter / adapter-card-derived`
- ancestry：`card-derived -> capture-original-media + anki-config`，均落到 task.manifest 初始根。
- 原始 questionImages 顺序：`{', '.join(item['media_id'] for item in validate_job['media'] if '.question.' in item['media_id'])}`
- 原始 answerImages 顺序：`{', '.join(item['media_id'] for item in validate_job['media'] if '.answer.' in item['media_id'])}`

## 最终卡片字段

- note type：`KnowledgeClipper Original Media v1`
- deck：`零碎知识剪藏::Canary`（仅 Fake provider）
- Front：`{expected_front}`
- Back：`{expected_back}`
- tags：`knowledge_clipper, original_media, rqc_managed`
- HTML 规则：文本后按输入顺序追加 `<br><img src="rqc_<sha256>.png">`；不 trim，不使用 legacy token。

## Worker 结果

- release EXE validate-only：`{validate_result['status']}`；外部 API 调用 `0`；严格 ancestry `PASS`。
- Fake 首次写入/readback：noteId `{note_id}`，cardId `{card_id}`；notes `1`，cards `1`，media `3`。
- Fake 幂等重放：`already_current={replay_receipt['counts']['already_current']}`；新增 `0`；mutating actions `0`。
- 同名异内容：`{collision_result['status']} / MEDIA_NAME_COLLISION`。
- 响应丢失：`{unknown_result['status']}`，retryable=false，outputs=[]。
- read-only probe：`{status['status']}`，externalMutation=false，mutating actions `0`。
- 不支持的媒体卡型：`{unsupported_result['status']} / {unsupported_result['error']['code']}`；没有误报 completed/succeeded。

## 真实边界

- 本验收只使用 validate-only 与进程内 Fake AnkiConnect；没有连接真实 AnkiConnect，也没有写入真实牌组。
- 当前 Core `media_qa/asset_ref` 活跃策略仍是 `reject-media-deferred`。
- `ordered-original-images.v1` 是 exporter 候选能力；Core 正式 input contract 与隔离真实 Canary 尚未完成，因此整体能力保持 `conditional`。
"""
    readable.write_text(readable_text, encoding="utf-8", newline="\n")

    receipt = {
        "schemaVersion": "export.anki.original-media-qualification.v1",
        "status": "passed",
        "worker": {"id": descriptor["workerId"], "version": descriptor["workerVersion"]},
        "release": {
            "root": project_relative(release_root),
            "treeSha256": release_tree["treeSha256"],
            "exe": project_relative(exe), "exeSha256": exporter.sha256_file(exe).upper(),
            "manifest": project_relative(release_manifest),
            "manifestSha256": exporter.sha256_file(release_manifest).upper(),
        },
        "sourceGapMatrix": {"path": project_relative(gap_matrix), "sha256": gap_sha},
        "inputProvenance": {
            "initialManifest": project_relative(source_manifest_path),
            "initialManifestSha256": exporter.sha256_file(source_manifest_path).upper(),
            "derivedManifest": project_relative(derived_manifest_path),
            "derivedManifestSha256": exporter.sha256_file(derived_manifest_path).upper(),
            "producer": derived_manifest["producer"],
            "inputRefs": derived_manifest["inputRefs"],
            "media": [
                {"mediaId": item["media_id"], "sha256": item["sha256"].upper(), "bytes": item["size"]}
                for item in validate_job["bundle"]["media"]
            ],
        },
        "projection": {
            "policy": exporter.ORIGINAL_MEDIA_PROJECTION,
            "questionImages": question_names, "answerImages": answer_names,
            "front": expected_front, "back": expected_back,
            "deck": "零碎知识剪藏::Canary",
            "noteType": "KnowledgeClipper Original Media v1",
            "tags": ["knowledge_clipper", "original_media", "rqc_managed"],
        },
        "checks": {
            "validateOnly": {"status": validate_result["status"], "externalApiCalls": 0},
            "fakeFirst": {
                "status": first_result["status"], "noteId": note_id, "cardId": card_id,
                "providerCalls": len(first_calls), "notes": 1, "cards": 1, "media": 3,
            },
            "fakeReplay": {
                "status": replay_result["status"], "alreadyCurrent": replay_receipt["counts"]["already_current"],
                "newNotes": 0, "mutatingActions": 0, "providerCalls": len(replay_calls),
            },
            "collision": {"status": collision_result["status"], "code": "MEDIA_NAME_COLLISION"},
            "unknownState": {
                "status": unknown_result["status"], "retryable": unknown_result["error"]["retryable"],
                "outputs": unknown_result["outputs"],
            },
            "probe": {
                "status": status["status"], "externalMutation": status["externalMutation"],
                "mutatingActions": len(status["mutatingActionsObserved"]),
                "actions": [item["action"] for item in probe_calls],
            },
            "unsupported": {
                "resultStatus": unsupported_result["status"],
                "destinationStatus": "conditional/unsupported",
                "code": unsupported_result["error"]["code"], "externalApiCalls": 0,
            },
        },
        "boundaries": {
            "realAnkiConnectCalled": False,
            "realDeckWrites": 0,
            "activeCoreMediaPolicy": "reject-media-deferred",
            "candidatePolicy": exporter.ORIGINAL_MEDIA_PROJECTION,
            "formalCoreInputContract": "pending",
            "realIsolatedCanary": "pending-formal-core-contract",
        },
        "readableEvidence": {
            "path": project_relative(readable), "sha256": exporter.sha256_file(readable).upper(),
        },
        "createdAt": exporter.utc_now(),
    }
    receipt_path = output_root / "qualification-receipt.json"
    exporter.write_json_atomic_once(receipt_path, receipt)
    print(json.dumps({
        "status": "passed", "receipt": str(receipt_path),
        "receiptSha256": exporter.sha256_file(receipt_path).upper(),
        "readable": str(readable), "readableSha256": exporter.sha256_file(readable).upper(),
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
