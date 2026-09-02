import json
import pathlib
import sys

from jsonschema import Draft202012Validator


ROOT = pathlib.Path(__file__).resolve().parents[2]
SCHEMA = json.loads((ROOT / "contracts" / "desktop-automation" / "v1" / "semantic-state.schema.json").read_text(encoding="utf-8"))
VALIDATOR = Draft202012Validator(SCHEMA)


def main() -> int:
    count = 0
    for line in sys.stdin:
        if not line.strip():
            continue
        response = json.loads(line)
        assert response["schemaVersion"] == "desktop-test-port.v1"
        assert response["ok"] is True, response
        VALIDATOR.validate(response["state"])
        count += 1
    assert count >= 17
    print(f"PASS semantic harness responses={count}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
