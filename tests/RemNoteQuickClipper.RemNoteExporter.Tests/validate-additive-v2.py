import argparse
import json
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker


ROOT = Path(__file__).resolve().parents[2]
CONTRACTS = ROOT / "contracts"
FIXTURES = Path(__file__).resolve().parent / "fixtures"


def checker(schema_name: str) -> Draft202012Validator:
    schema = json.loads((CONTRACTS / schema_name).read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema, format_checker=FormatChecker())


def load(name: str):
    return json.loads((FIXTURES / name).read_text(encoding="utf-8"))


def require_valid(validator: Draft202012Validator, name: str) -> None:
    errors = list(validator.iter_errors(load(name)))
    if errors:
        raise AssertionError(f"valid fixture rejected: {name}: {errors[0].message}")
    print(f"PASS valid {name}")


def require_invalid(validator: Draft202012Validator, name: str) -> None:
    if not list(validator.iter_errors(load(name))):
        raise AssertionError(f"invalid fixture accepted: {name}")
    print(f"PASS invalid rejected {name}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--settings-descriptor")
    parser.add_argument("--settings-snapshot")
    parser.add_argument("--companion-descriptor")
    args = parser.parse_args()
    payload = checker("remnote-export-payload.v2.schema.json")
    task = checker("remnote-export-task.v2.schema.json")
    settings = checker("remnote-destination-settings.v1.schema.json")
    receipt = checker("remnote-write-receipt.v2.schema.json")
    probe_request = checker("remnote-readonly-probe-request.v1.schema.json")
    probe_result = checker("remnote-readonly-probe-result.v1.schema.json")

    require_valid(payload, "payload-v2.clip.valid.json")
    require_valid(payload, "payload-v2.card.valid.json")
    require_invalid(payload, "payload-v2.kind-only.invalid.json")
    require_invalid(payload, "payload-v2.card-date-leak.invalid.json")
    require_invalid(payload, "payload-v2.timezone.invalid.json")
    require_valid(task, "task-v2.clip.valid.json")
    require_valid(task, "task-v2.card.valid.json")
    require_invalid(task, "task-v2.clip-card-leak.invalid.json")
    require_invalid(task, "task-v2.card-date-routing.invalid.json")
    require_valid(settings, "settings-v1.valid.json")
    require_invalid(settings, "settings-v1.secret.invalid.json")
    require_valid(receipt, "receipt-v2.clip.valid.json")
    require_valid(receipt, "receipt-v2.card.valid.json")
    require_invalid(receipt, "receipt-v2.card-date-leak.invalid.json")
    require_valid(probe_request, "probe-request.health.valid.json")
    require_valid(probe_request, "probe-request.reconcile.valid.json")
    require_invalid(probe_request, "probe-request.timeout.invalid.json")
    require_invalid(probe_request, "probe-request.formal-canary.invalid.json")
    require_valid(probe_result, "probe-result.healthy.valid.json")
    require_invalid(probe_result, "probe-result.false-green.invalid.json")
    if args.settings_descriptor:
        descriptor = checker("remnote-destination-settings-descriptor.v1.schema.json")
        descriptor.validate(json.loads(Path(args.settings_descriptor).read_text(encoding="utf-8")))
        print("PASS emitted settings descriptor")
    if args.settings_snapshot:
        snapshot = checker("remnote-destination-settings-snapshot.v1.schema.json")
        snapshot.validate(json.loads(Path(args.settings_snapshot).read_text(encoding="utf-8")))
        print("PASS emitted settings snapshot")
    if args.companion_descriptor:
        companion = checker("remnote-destination-companion-descriptor.v1.schema.json")
        companion.validate(json.loads(Path(args.companion_descriptor).read_text(encoding="utf-8")))
        print("PASS emitted companion descriptor")
    print("PASS additive RemNote exporter contracts valid=10 invalid=10")


if __name__ == "__main__":
    main()
