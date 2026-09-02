#!/usr/bin/env python3
"""Run the planned EXE against a test-only finalized language both bundle."""

from __future__ import annotations

import datetime as dt
import json
import subprocess
import sys
import uuid
from pathlib import Path


TESTS = Path(__file__).resolve().parent
WORKER = TESTS.parent
PROJECT = WORKER.parents[1]
sys.path.insert(0, str(TESTS))
sys.path.insert(0, str(WORKER))

import anki_exporter as exporter
import test_anki_exporter as fixtures


def main() -> int:
    run_id = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    canary_root = WORKER / "canary" / f"language-writing-{run_id}"
    job = fixtures.build_language_both_job(canary_root / "job")
    request = (job["root"] / job["request"]).resolve()
    exe = PROJECT / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
    completed = subprocess.run(
        [str(exe), "run", "--request", str(request)],
        cwd=PROJECT,
        capture_output=True,
        text=True,
        encoding="utf-8",
        check=False,
    )
    if completed.returncode != 0:
        raise RuntimeError(f"validate-only EXE failed with exit code {completed.returncode}")

    attempt = request.parent
    result_path = attempt / "result.json"
    receipt_path = attempt / "outputs" / "receipt.json"
    operations_path = attempt / "outputs" / "operations.ndjson"
    bundle_path = job["root"] / "artifacts" / "card-input" / "card-bundle.v1.json"
    result = json.loads(result_path.read_text(encoding="utf-8"))
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    bundle = json.loads(bundle_path.read_text(encoding="utf-8"))
    context, writing = bundle["cards"]

    assert result["status"] == "completed"
    assert result["worker"] == {"id": "export.anki", "version": exporter.EXPORTER_VERSION}
    assert result["payload"]["validateOnly"] is True
    assert not operations_path.exists()
    assert [card["card_id"] for card in bundle["cards"]] == ["language-context-card", "language-writing-card"]
    assert context["fields"] == {
        "context_sentence": "他心中仍有疑窦。",
        "reading_and_meaning": "疑窦（yí dòu）：心里的疑问。",
    }
    assert writing["fields"] == {"front": "他心中仍有（yí dòu；心里的疑问）。", "back": "疑窦"}
    assert writing["fields"]["back"] not in writing["fields"]["front"]

    sample_path = canary_root / "readable-sample.md"
    sample_path.write_text(
        "\n".join([
            "# language_writing / both validate-only 样例",
            "",
            "- 边界：test-only finalized CardBundle；不是尚未冻结的 Content/Core 派生产物",
            "- 原始句子：`他心中仍有疑窦。`",
            "- target_term：`疑窦`",
            "- 读音：`yí dòu`",
            "- 意思：`心里的疑问`",
            "- choice：由上游决定为 both；未进入 Exporter 输入",
            "",
            "## 卡 1：language_context",
            "",
            "- note type：`language_item`",
            "- Front：`他心中仍有疑窦。`",
            "- Back：`疑窦（yí dòu）：心里的疑问。`",
            "",
            "## 卡 2：language_writing",
            "",
            "- note type：`language_writing`",
            "- Front：`他心中仍有（yí dòu；心里的疑问）。`",
            "- Back：`疑窦`",
            "- 断言：正面不含 `疑窦`；唯一原词已被全角括号注释完整替换",
            "",
            "## Worker 结果",
            "",
            "- status：`completed`",
            "- validateOnly：`true`",
            "- externalCalls：`0`",
            "- operations.ndjson：未创建",
            "",
        ]),
        encoding="utf-8",
        newline="\n",
    )
    canary_result = {
        "schemaVersion": "anki-language-writing-validate-only-canary.v1",
        "createdAt": dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "pass",
        "boundary": "test-only-finalized-card-bundle-not-content-core-acceptance",
        "worker": {"workerId": "export.anki", "workerVersion": exporter.EXPORTER_VERSION},
        "externalAnkiCalls": 0,
        "inputOrder": ["language-context-card", "language-writing-card"],
        "writingFront": writing["fields"]["front"],
        "writingBack": writing["fields"]["back"],
        "exe": {"path": exe.relative_to(PROJECT).as_posix(), "sha256": exporter.sha256_file(exe)},
        "result": {"path": result_path.relative_to(PROJECT).as_posix(), "sha256": exporter.sha256_file(result_path)},
        "receipt": {"path": receipt_path.relative_to(PROJECT).as_posix(), "sha256": exporter.sha256_file(receipt_path)},
        "readableSample": {"path": sample_path.relative_to(PROJECT).as_posix(), "sha256": exporter.sha256_file(sample_path)},
    }
    canary_result_path = canary_root / "canary-result.json"
    canary_result_path.write_text(json.dumps(canary_result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps({
        "status": "PASS",
        "canaryResult": str(canary_result_path),
        "canaryResultSha256": exporter.sha256_file(canary_result_path),
        "readableSample": str(sample_path),
        "externalAnkiCalls": 0,
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
