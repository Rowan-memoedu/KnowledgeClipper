import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import subprocess

from jsonschema import Draft202012Validator
from referencing import Registry, Resource


def build_registry(schema_root: Path):
    registry = Registry()
    schemas = {}
    for path in sorted(schema_root.rglob("*.schema.json")):
        schema = json.loads(path.read_text(encoding="utf-8"))
        # The checked-in schemas use relative $id values. Give every in-memory
        # resource its retrieval URI so nested local fragments retain scope.
        schema["$id"] = path.resolve().as_uri()
        Draft202012Validator.check_schema(schema)
        uri = path.resolve().as_uri()
        registry = registry.with_resource(uri, Resource.from_contents(schema))
        schemas[path.resolve()] = schema
    return registry, schemas


def load_validator(path: Path, registry: Registry, schemas) -> Draft202012Validator:
    return Draft202012Validator(schemas[path.resolve()], registry=registry)


def errors(validator: Draft202012Validator, path: Path):
    instance = json.loads(path.read_text(encoding="utf-8"))
    return sorted(validator.iter_errors(instance), key=lambda error: list(error.absolute_path))


def require_valid(validator, path: Path, label: str):
    found = errors(validator, path)
    if found:
        raise AssertionError(f"{label} invalid: {path}: {found[0].message}")
    print(f"PASS {label}: {path}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--project-root", required=True)
    parser.add_argument("--canary-root")
    parser.add_argument("--unit-root", required=True)
    parser.add_argument("--worker-exe", required=True)
    args = parser.parse_args()

    project = Path(args.project_root).resolve()
    contracts = project / "contracts" / "v1"
    registry, schemas = build_registry(contracts / "schemas")
    artifact = load_validator(contracts / "schemas" / "common" / "artifact.schema.json", registry, schemas)
    speech_request = load_validator(contracts / "schemas" / "speech" / "speech-request.schema.json", registry, schemas)
    speech_result = load_validator(contracts / "schemas" / "speech" / "speech-result.schema.json", registry, schemas)
    worker_describe = load_validator(contracts / "schemas" / "common" / "worker-describe.schema.json", registry, schemas)
    cancel_signal = load_validator(contracts / "schemas" / "common" / "worker-cancel-signal.schema.json", registry, schemas)

    described = subprocess.run(
        [args.worker_exe, "describe", "--json"], check=False, capture_output=True, text=True, encoding="utf-8"
    )
    if described.returncode != 0 or described.stderr.strip():
        raise AssertionError(f"worker describe failed: exit={described.returncode} stderr={described.stderr.strip()}")
    describe_document = json.loads(described.stdout)
    describe_errors = sorted(worker_describe.iter_errors(describe_document), key=lambda error: list(error.absolute_path))
    if describe_errors:
        raise AssertionError(f"worker describe invalid: {describe_errors[0].message}")
    print("PASS worker describe public schema")

    invalid_describes = sorted((contracts / "fixtures" / "common" / "worker-describe" / "invalid").glob("*.json"))
    for path in invalid_describes:
        if not errors(worker_describe, path):
            raise AssertionError(f"public invalid describe fixture unexpectedly passed: {path}")
        print(f"PASS public invalid describe fixture rejected: {path}")

    cancel_fixtures = sorted((contracts / "fixtures" / "common" / "worker-cancel-signal" / "valid").glob("*.json"))
    for path in cancel_fixtures:
        require_valid(cancel_signal, path, "public cancel signal fixture")

    valid_fixtures = sorted((contracts / "fixtures" / "speech" / "valid").glob("*.json"))
    invalid_fixtures = sorted((contracts / "fixtures" / "speech" / "invalid").glob("*.json"))
    if not valid_fixtures or not invalid_fixtures:
        raise AssertionError("public speech fixtures are incomplete")
    validated_results = 0
    validated_artifacts = 0
    validated_requests = 0
    validated_cancel_signals = len(cancel_fixtures)
    for path in valid_fixtures:
        require_valid(speech_request, path, "public valid fixture")
    for path in invalid_fixtures:
        found = errors(speech_request, path)
        if not found:
            raise AssertionError(f"public invalid fixture unexpectedly passed: {path}")
        print(f"PASS public invalid fixture rejected: {path}: {found[0].message}")

    roots = [("unit", Path(args.unit_root))]
    if args.canary_root:
        roots.insert(0, ("canary", Path(args.canary_root)))
    for root_label, root in roots:
        result_paths = sorted(root.rglob("result.json"))
        if not result_paths:
            raise AssertionError(f"no {root_label} result.json files found")
        for path in result_paths:
            require_valid(speech_result, path, f"{root_label} speech result")
            validated_results += 1
        for path in sorted(root.rglob("artifact.json")):
            require_valid(artifact, path, f"{root_label} artifact")
            validated_artifacts += 1
        for path in sorted(root.rglob("cancel.json")):
            require_valid(cancel_signal, path, f"{root_label} cancel signal")
            validated_cancel_signals += 1
        if root_label == "canary":
            for path in sorted(root.rglob("request.json")):
                require_valid(speech_request, path, "canary speech request")
                validated_requests += 1

    receipt = {
        "schemaVersion": 1,
        "suite": "speech-public-contract-compatibility",
        "completedAtUtc": datetime.now(timezone.utc).isoformat(),
        "status": "passed",
        "validFixturesAccepted": len(valid_fixtures),
        "invalidFixturesRejected": len(invalid_fixtures),
        "resultsValidated": validated_results,
        "artifactsValidated": validated_artifacts,
        "requestsValidated": validated_requests,
        "workerDescribeValidated": 1,
        "invalidDescribeFixturesRejected": len(invalid_describes),
        "cancelSignalsValidated": validated_cancel_signals,
    }
    receipt_path = Path(args.unit_root) / "public-contract-validation-receipt.json"
    receipt_path.write_text(json.dumps(receipt, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"RECEIPT {receipt_path}")
    print("PASS PUBLIC CONTRACT COMPATIBILITY")


if __name__ == "__main__":
    main()
