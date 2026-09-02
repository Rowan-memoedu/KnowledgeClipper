import json
import os
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker


def load(path: Path):
    native = Path("\\\\?\\" + str(path)) if os.name == "nt" and path.is_absolute() else path
    return json.loads(native.read_text(encoding="utf-8"))


def main():
    worker_root = Path(__file__).resolve().parents[1]
    schema_path = worker_root / "schemas" / "content-input.v3.schema.json"
    validator = Draft202012Validator(load(schema_path), format_checker=FormatChecker())
    expectations = {
        "offline-question-answer-v3": "schema-valid/runtime-completed",
        "offline-question-followup-v3": "schema-valid/runtime-completed",
        "offline-question-make-cards-v3": "schema-valid/runtime-completed",
        "offline-question-empty-v3": "schema-invalid/runtime-rejected",
        "offline-question-ancestry-missing-v3": "schema-valid/runtime-rejected-semantic",
        "offline-question-turn-order-invalid-v3": "schema-valid/runtime-rejected-semantic",
        "offline-question-answer-noise-v3": "schema-valid/runtime-unresolved",
        "offline-question-answer-unresolved-v3": "schema-valid/runtime-unresolved",
    }
    observed = []
    for fixture, expectation in expectations.items():
        input_path = worker_root / "fixtures" / fixture / "inputs" / "standard-input.v3.json"
        errors = sorted(validator.iter_errors(load(input_path)), key=lambda error: list(error.absolute_path))
        schema_valid = not errors
        expected_schema_valid = not expectation.startswith("schema-invalid")
        if schema_valid != expected_schema_valid:
            detail = "; ".join(error.message for error in errors) or "unexpectedly schema-valid"
            raise AssertionError(f"{fixture}: {detail}")
        observed.append({"fixture": fixture, "schema": "PASS" if schema_valid else "EXPECTED_INVALID", "runtimeExpectation": expectation.split("/", 1)[1]})

    print(json.dumps({
        "status": "PASS",
        "schemaVersion": "standard-input/3.0.0",
        "fixturesChecked": len(observed),
        "fixtures": observed,
        "note": "Cross-record ancestry and turn order are semantic runtime gates covered by worker.test.ts.",
    }, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
