from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


EXPECTED_RECEIPT = "4D4E49393A4532C81F8E9FF28DA9CA743EE0987FE6F52AED0B15E2A433861C3C"
EXPECTED_RAW = "A6D3646894D7C2CBDCF9C1784E43915865E28AF9322965C06464D0A96615579A"


class GateFailure(RuntimeError):
    pass


def require(value: bool, message: str) -> None:
    if not value:
        raise GateFailure(message)


def sha_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def sha_text(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest().upper()


def evidence(path: Path) -> dict[str, Any]:
    return {"path": str(path.resolve()), "bytes": path.stat().st_size, "sha256": sha_file(path)}


def evaluate(receipt_path: Path) -> dict[str, Any]:
    receipt_path = receipt_path.resolve()
    require(sha_file(receipt_path) == EXPECTED_RECEIPT, "authority fixture receipt SHA drift")
    receipt = json.loads(receipt_path.read_text(encoding="utf-8-sig"))
    require(receipt["status"] == "passed", "owner fixture did not pass")
    original = receipt["originalInput"]
    require(original["sha256"] == EXPECTED_RAW and original["bytes"] == len(original["text"].encode("utf-8")), "original input metadata drift")
    require(sha_text(original["text"]) == EXPECTED_RAW, "embedded original input SHA drift")
    mapping = receipt["configurationMapping"]
    require(mapping["explicitOperationOverride"].endswith("#/human_overrides/languagePracticeMode"), "override field drift")
    require(mapping["settingsDefault"].endswith("#/runtimePolicy/languagePracticeDefault"), "project default field drift")
    require(mapping["precedence"].startswith("explicit human_overrides.languagePracticeMode"), "override precedence drift")
    require("decision artifact instead of CardBundle" in mapping["precedence"], "ask semantics drift")

    scenarios = {item["mode"]: item for item in receipt["scenarios"]}
    require(set(scenarios) == {"meaning-reading", "writing", "both"}, "mode fixture matrix incomplete")
    expected = {"meaning-reading": ["language_context"], "writing": ["language_writing"], "both": ["language_context", "language_writing"]}
    readable = []
    for mode, templates in expected.items():
        scenario = scenarios[mode]
        require(scenario["inputField"] == {"human_overrides.languagePracticeMode": mode}, f"{mode} override input drift")
        source = Path(scenario["sourcePath"])
        bundle_path = Path(scenario["cardBundle"]["path"])
        require(source.is_file() and sha_file(source) == EXPECTED_RAW and source.stat().st_size == original["bytes"], f"{mode} source drift")
        bundle_meta = scenario["cardBundle"]
        require(bundle_path.is_file() and bundle_path.stat().st_size == bundle_meta["bytes"] and sha_file(bundle_path) == bundle_meta["sha256"], f"{mode} bundle file drift")
        cards = bundle_meta["cards"]
        require([item["index"] for item in cards] == list(range(len(templates))), f"{mode} card index drift")
        require([item["template"] for item in cards] == templates, f"{mode} template/count/order drift")
        for card in cards:
            require(sha_text(card["front"]) == card["frontSha256"], f"{mode} front text hash drift")
            require(sha_text(card["back"]) == card["backSha256"], f"{mode} back text hash drift")
        readable.append({"mode": mode, "templates": templates, "cards": [{"front": card["front"], "back": card["back"]} for card in cards], "bundle": evidence(bundle_path)})
    effects = receipt["effects"]
    require(effects == {"deepSeekCallsThisVerification": 0, "ankiWrites": 0, "remNoteWrites": 0, "sourceArtifactsMutated": False, "codeChanged": False}, "verification side-effect boundary drift")
    require(receipt["validation"]["currentWorkerRegression"] == {"workerVersion": "0.9.5", "tests": 90, "pass": 90, "fail": 0, "receiptPath": receipt["validation"]["currentWorkerRegression"]["receiptPath"], "receiptSha256": receipt["validation"]["currentWorkerRegression"]["receiptSha256"]}, "current worker compatibility drift")
    return {
        "schema": "kc.quality.content-language-mode-fixture/1",
        "createdAt": datetime.now(timezone.utc).isoformat(),
        "status": "PASS-CONTENT-FIXTURE-ADMISSION",
        "sourceReceipt": evidence(receipt_path),
        "originalInput": original,
        "configurationMapping": mapping,
        "scenarios": readable,
        "boundary": {"historicalLiveTextFrozen": True, "currentWorkerSemanticRegression": "0.9.5-90/90", "candidateBoundCore": False, "desktopBound": False, "freshLiveModelWordingFrozen": False, "externalCallsThisAudit": 0},
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = evaluate(args.receipt)
    args.output.parent.mkdir(parents=True, exist_ok=False)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{sha_file(args.output)} {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
