import hashlib
import json
import sys
from pathlib import Path


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def digest(path: Path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def output(root: Path, scenario: str, name: str):
    return load(root / "scenarios" / scenario / "outputs" / name)


def main():
    if len(sys.argv) != 2:
        raise SystemExit("usage: validate_live_synthetic_matrix.py <matrix-root>")
    root = Path(sys.argv[1]).resolve()
    manifest = load(root / "manifest.json")
    receipt = load(root / "authority-receipt.json")
    assert manifest["schemaVersion"] == "content-live-synthetic-matrix/1.0.0"
    assert manifest["syntheticNonPrivate"] is True
    assert manifest["externalWrites"] == []
    assert receipt["manifestSha256"] == digest(root / "manifest.json")
    assert len(manifest["scenarios"]) == 8
    for entry in manifest["files"]:
        path = root.joinpath(*entry["path"].split("/"))
        assert path.stat().st_size == entry["bytes"]
        assert digest(path) == entry["sha256"]
    for scenario in manifest["scenarios"]:
        assert scenario["externalConnectionEvidence"] == {
            "transport": "https",
            "endpointOrigin": "https://api.deepseek.com",
            "endpointPath": "/chat/completions",
            "credentialRecorded": False,
        }
        assert all(trace["mode"] == "live" and trace["provider"] == "deepseek" and trace["attempts"] >= 1 for trace in scenario["providerEvidence"])
        learning = output(root, scenario["id"], "learning-bundle.schema-v1.json")
        assert learning["ai_trace"]
        assert all(trace["mode"] == "live" and trace["provider"] == "deepseek" and "response" in trace for trace in learning["ai_trace"])

    cleaning = output(root, "cleaning-zh", "learning-bundle.schema-v1.json")
    cleaned = cleaning["items"][0]["cleaning"]["cleaned_text"]
    assert "$R=e^{-t/S}$" in cleaned
    assert "分享 收藏 下载" not in cleaned and "Copyright" not in cleaned
    assert [entry["exact_text"] for entry in cleaning["items"][0]["cleaning"]["removals"]] == [
        "复制", "分享 收藏 下载", "Copyright 2026 示例学习站 版权所有\n"
    ]

    decision = next(item for item in manifest["scenarios"] if item["id"] == "language-decision")
    assert decision["outputKinds"] == ["learning-bundle", "language-practice-decision"]
    assert not (root / "scenarios" / "language-decision" / "outputs" / "card-bundle.schema-v1.json").exists()
    assert len(output(root, "language-decision", "language-practice-decision.schema-v1.json")["decisions"]) == 1

    meaning = output(root, "language-meaning-reading", "card-bundle.schema-v1.json")["cards"]
    writing = output(root, "language-writing", "card-bundle.schema-v1.json")["cards"]
    both = output(root, "language-both", "card-bundle.schema-v1.json")["cards"]
    assert [card["template"] for card in meaning] == ["language_context"]
    assert [card["template"] for card in writing] == ["language_writing"]
    assert [card["template"] for card in both] == ["language_context", "language_writing"]
    for card in [writing[0], both[1]]:
        assert card["back_blocks"][0]["text"] == "pragmatic"
        assert not card["front_blocks"][0]["text"].endswith(("\r", "\n"))
        assert "（" in card["front_blocks"][0]["text"] and "）" in card["front_blocks"][0]["text"]

    initial = output(root, "question-initial-answer", "question-answer.schema-v1.json")
    followup = output(root, "question-followup-answer", "question-answer.schema-v1.json")
    question_cards = output(root, "question-make-cards", "card-bundle.schema-v1.json")
    assert [turn["turn_index"] for turn in initial["turns"]] == [0]
    assert [turn["turn_index"] for turn in followup["turns"]] == [0, 1]
    assert followup["turns"][0]["answer_text"] == initial["turns"][0]["answer_text"]
    assert (root / "scenarios" / "question-initial-answer" / "outputs" / "answer.text").read_text(encoding="utf-8").rstrip("\n") == initial["turns"][-1]["answer_text"]
    assert (root / "scenarios" / "question-followup-answer" / "outputs" / "answer.text").read_text(encoding="utf-8").rstrip("\n") == followup["turns"][-1]["answer_text"]
    assert question_cards["cards"] and all(card["template"] == "basic_qa" for card in question_cards["cards"])
    print(f"PASS live synthetic matrix: scenarios=8 files={len(manifest['files'])} cleaning=1 language=4 question=3")


if __name__ == "__main__":
    main()
