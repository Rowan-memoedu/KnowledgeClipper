import json
import pathlib
import sys

from jsonschema import Draft202012Validator


ROOT = pathlib.Path(__file__).resolve().parents[2]
SCHEMA = json.loads((ROOT / "contracts" / "desktop-automation" / "v1.1" / "semantic-state.schema.json").read_text(encoding="utf-8"))
VALIDATOR = Draft202012Validator(SCHEMA)


def main() -> int:
    count = 0
    final = None
    for line in sys.stdin:
        if not line.strip():
            continue
        response = json.loads(line)
        assert response["schemaVersion"] == "desktop-test-port.v1.1"
        assert response["ok"] is True, response
        VALIDATOR.validate(response["state"])
        final = response["state"]
        count += 1
    assert count >= 18
    assert final["lastAction"] == "language-practice.select"
    assert final["noticeCode"] == "language-practice.input-submitted"
    assert final["recentTask"]["decisionKind"] is None
    assert final["recentTask"]["decisionTargetToken"] is None
    print(f"PASS semantic harness v1.1 responses={count} opaqueTargetForwarded=true")
    return 0


if __name__ == "__main__":
    sys.exit(main())
