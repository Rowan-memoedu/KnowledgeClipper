import hashlib
import json
import pathlib
import sys

from jsonschema import Draft202012Validator, ValidationError


ROOT = pathlib.Path(__file__).resolve().parents[2]
CONTRACT_ROOT = ROOT / "contracts" / "desktop-automation" / "v1.1"


def expect_invalid(validator, value, label):
    try:
        validator.validate(value)
    except ValidationError:
        return
    raise AssertionError(f"expected invalid: {label}")


def main() -> int:
    action_schema = json.loads((CONTRACT_ROOT / "semantic-action.schema.json").read_text(encoding="utf-8"))
    state_schema = json.loads((CONTRACT_ROOT / "semantic-state.schema.json").read_text(encoding="utf-8"))
    contract = json.loads((CONTRACT_ROOT / "desktop-automation.contract.json").read_text(encoding="utf-8"))
    action_validator = Draft202012Validator(action_schema)
    Draft202012Validator.check_schema(action_schema)
    Draft202012Validator.check_schema(state_schema)

    valid_count = 0
    for line in (CONTRACT_ROOT / "fixtures" / "semantic-actions.valid.ndjson").read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        action_validator.validate(json.loads(line))
        valid_count += 1

    expect_invalid(action_validator, {"schemaVersion": "desktop-automation.v1", "requestId": "old", "action": "clip.capture"}, "frozen v1 action on v1.1 port")
    expect_invalid(action_validator, {"schemaVersion": "desktop-automation.v1.1", "requestId": "missing", "action": "language-practice.select"}, "selection required")
    expect_invalid(action_validator, {"schemaVersion": "desktop-automation.v1.1", "requestId": "unknown", "action": "language-practice.select", "arguments": {"selection": "guessed"}}, "selection enum")
    expect_invalid(action_validator, {"schemaVersion": "desktop-automation.v1.1", "requestId": "binding", "action": "language-practice.select", "arguments": {"selection": "both", "decisionTargetToken": "desktop-constructed"}}, "desktop cannot construct decision binding")

    assert contract["contractId"] == "knowledge-clipper.desktop-automation"
    assert contract["contractVersion"] == "1.1.0"
    assert all(item["automationId"].startswith("knowledge-clipper.desktop.") for item in contract["automationIds"])
    assert not any(item["automationId"].startswith("rqc.desktop.") for item in contract["automationIds"])
    assert contract["languagePracticeInput"]["submission"] == "common task.provide_input"
    receipt = json.loads((CONTRACT_ROOT / "contract-receipt.json").read_text(encoding="utf-8"))
    expected_paths = {path.relative_to(CONTRACT_ROOT).as_posix() for path in CONTRACT_ROOT.rglob("*") if path.is_file() and path.name != "contract-receipt.json"}
    assert receipt["contractId"] == "knowledge-clipper.desktop-automation" and receipt["contractVersion"] == "1.1.0"
    assert {item["path"] for item in receipt["files"]} == expected_paths
    canonical_lines = []
    for item in receipt["files"]:
        payload = (CONTRACT_ROOT / item["path"]).read_bytes()
        digest = hashlib.sha256(payload).hexdigest()
        assert digest == item["sha256"] and len(payload) == item["bytes"]
        canonical_lines.append((item["path"], digest))
    canonical_lines.sort(key=lambda item: item[0].encode("utf-8"))
    tree = hashlib.sha256(("".join(f"{path}|{digest}\n" for path, digest in canonical_lines)).encode("utf-8")).hexdigest()
    assert tree == receipt["treeSha256"]
    print(f"PASS desktop automation v1.1 contracts validActions={valid_count} invalidCases=4 neutralAutomationIds={len(contract['automationIds'])} receiptFiles={len(receipt['files'])}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
