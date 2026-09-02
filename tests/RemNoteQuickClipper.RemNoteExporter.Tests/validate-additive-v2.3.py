import argparse
import json
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource


ROOT = Path(__file__).resolve().parents[2]
CONTRACTS = ROOT / "contracts"
FIXTURES = Path(__file__).resolve().parent / "fixtures"


def validator(name: str) -> Draft202012Validator:
    schema = json.loads((CONTRACTS / name).read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    resources = []
    for path in CONTRACTS.glob("*.schema.json"):
        item = json.loads(path.read_text(encoding="utf-8"))
        if "$id" in item:
            resources.append((item["$id"], Resource.from_contents(item)))
    return Draft202012Validator(schema, registry=Registry().with_resources(resources), format_checker=FormatChecker())


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--descriptor", required=True)
    parser.add_argument("--result", required=True)
    args = parser.parse_args()

    request = validator("remnote-target-discovery-request.v1.schema.json")
    result = validator("remnote-target-discovery-result.v1.schema.json")
    descriptor = validator("remnote-destination-companion-descriptor.v3.schema.json")

    request.validate(load(FIXTURES / "target-discovery-request-v1.valid.json"))
    print("PASS valid target discovery request")
    if not list(request.iter_errors(load(FIXTURES / "target-discovery-request-v1.spoofed-id.invalid.json"))):
        raise AssertionError("spoofed rootRemId fixture accepted")
    print("PASS invalid spoofed rootRemId rejected")
    if not list(result.iter_errors(load(FIXTURES / "target-discovery-result-v1.false-green.invalid.json"))):
        raise AssertionError("false-green discovery result accepted")
    print("PASS invalid false-green result rejected")
    descriptor.validate(load(Path(args.descriptor)))
    print("PASS emitted companion descriptor v3")
    result.validate(load(Path(args.result)))
    print("PASS emitted target discovery result v1")
    print("PASS additive RemNote exporter 2.3 contracts schemas=3 valid=1 invalid=2 emitted=2")


if __name__ == "__main__":
    main()
