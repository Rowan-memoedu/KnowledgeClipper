import argparse
import json
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource


ROOT = Path(__file__).resolve().parents[2]
CONTRACTS = ROOT / "contracts"
FIXTURES = Path(__file__).resolve().parent / "fixtures"


def checker(schema_name: str) -> Draft202012Validator:
    schema_path = CONTRACTS / schema_name
    schema = json.loads(schema_path.read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    resources = []
    for path in CONTRACTS.glob("*.schema.json"):
        value = json.loads(path.read_text(encoding="utf-8"))
        if "$id" in value:
            resources.append((value["$id"], Resource.from_contents(value)))
    registry = Registry().with_resources(resources)
    return Draft202012Validator(schema, registry=registry, format_checker=FormatChecker())


def fixture(name: str):
    return json.loads((FIXTURES / name).read_text(encoding="utf-8"))


def valid(validator, name: str) -> None:
    errors = list(validator.iter_errors(fixture(name)))
    if errors:
        raise AssertionError(f"valid fixture rejected: {name}: {errors[0].message}")
    print(f"PASS valid {name}")


def invalid(validator, name: str) -> None:
    if not list(validator.iter_errors(fixture(name))):
        raise AssertionError(f"invalid fixture accepted: {name}")
    print(f"PASS invalid rejected {name}")


def validate_file(schema_name: str, file_name: str, label: str) -> None:
    checker(schema_name).validate(json.loads(Path(file_name).read_text(encoding="utf-8")))
    print(f"PASS emitted {label}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--companion-descriptor", required=True)
    parser.add_argument("--public-probe-result", required=True)
    args = parser.parse_args()

    media = checker("remnote-original-media.v2.schema.json")
    payload = checker("remnote-export-payload.v3.schema.json")
    request = checker("remnote-public-probe-request.v2.schema.json")
    result = checker("remnote-public-probe-result.v2.schema.json")
    receipt = checker("remnote-write-receipt.v3.schema.json")
    checker("remnote-export-task.v3.schema.json")
    checker("remnote-destination-companion-descriptor.v2.schema.json")

    valid(media, "original-media-v2.valid.json")
    invalid(media, "original-media-v2.missing-sha.invalid.json")
    valid(payload, "payload-v3.media.valid.json")
    invalid(payload, "payload-v3.clip.invalid.json")
    valid(request, "public-probe-request-v2.valid.json")
    invalid(request, "public-probe-request-v2.formal.invalid.json")
    valid(result, "public-probe-result-v2.valid.json")
    invalid(result, "public-probe-result-v2.false-green.invalid.json")
    valid(receipt, "receipt-v3.media.valid.json")
    invalid(receipt, "receipt-v3.media-dropped.invalid.json")
    validate_file("remnote-destination-companion-descriptor.v2.schema.json", args.companion_descriptor, "companion descriptor v2")
    validate_file("remnote-public-probe-result.v2.schema.json", args.public_probe_result, "public probe result v2")
    print("PASS additive RemNote exporter 2.2 contracts schemas=7 valid=5 invalid=5 emitted=2")


if __name__ == "__main__":
    main()
