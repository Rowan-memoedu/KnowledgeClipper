import argparse
import json
from pathlib import Path

from jsonschema import Draft202012Validator
from referencing import Registry, Resource


def registry_for(root: Path):
    registry = Registry()
    schemas = {}
    for path in sorted(root.rglob("*.schema.json")):
        schema = json.loads(path.read_text(encoding="utf-8"))
        schema["$id"] = path.resolve().as_uri()
        Draft202012Validator.check_schema(schema)
        registry = registry.with_resource(path.resolve().as_uri(), Resource.from_contents(schema))
        schemas[path.resolve()] = schema
    return registry, schemas


def validator(path: Path, registry, schemas):
    return Draft202012Validator(schemas[path.resolve()], registry=registry)


def require_valid(checker, path: Path, label: str):
    instance = json.loads(path.read_text(encoding="utf-8"))
    errors = sorted(checker.iter_errors(instance), key=lambda item: list(item.absolute_path))
    if errors:
        raise AssertionError(f"{label} invalid: {path}: {errors[0].message}")
    print(f"PASS {label}: {path}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--project-root", required=True)
    parser.add_argument("--canary-root", required=True)
    parser.add_argument("--describe", required=True)
    parser.add_argument("--expected-result-count", type=int, default=3)
    parser.add_argument("--expected-statuses", nargs="+", default=["completed", "cancelled", "unknown_external_state"])
    parser.add_argument("--suite", default="remnote-exporter-stable-common-adapter")
    args = parser.parse_args()
    project = Path(args.project_root).resolve()
    canary = Path(args.canary_root).resolve()
    schemas_root = project / "contracts" / "v1" / "schemas"
    registry, schemas = registry_for(schemas_root)
    common = schemas_root / "common"
    request_validator = validator(common / "worker-request.schema.json", registry, schemas)
    result_validator = validator(common / "worker-result.schema.json", registry, schemas)
    artifact_validator = validator(common / "artifact.schema.json", registry, schemas)
    describe_validator = validator(common / "worker-describe.schema.json", registry, schemas)
    require_valid(describe_validator, Path(args.describe), "worker describe")
    for path in sorted(canary.rglob("request.json")):
        require_valid(request_validator, path, "worker request")
    results = sorted(canary.rglob("result.json"))
    if len(results) != args.expected_result_count:
        raise AssertionError(f"expected {args.expected_result_count} result files, found {len(results)}")
    for path in results:
        require_valid(result_validator, path, "worker result")
    for path in sorted(canary.rglob("artifact.json")):
        require_valid(artifact_validator, path, "artifact")
    statuses = {json.loads(path.read_text(encoding="utf-8"))["status"] for path in results}
    if statuses != set(args.expected_statuses):
        raise AssertionError(f"unexpected terminal statuses: {statuses}")
    unknown_results = [json.loads(path.read_text(encoding="utf-8")) for path in results if json.loads(path.read_text(encoding="utf-8"))["status"] == "unknown_external_state"]
    for unknown in unknown_results:
        if unknown["error"]["retryable"] is not False or unknown["outputs"]:
            raise AssertionError("unknown_external_state must be non-retryable and publish no outputs")
    receipt = {
        "schemaVersion": "1.0.0",
        "suite": args.suite,
        "status": "passed",
        "requestsValidated": len(list(canary.rglob("request.json"))),
        "resultsValidated": len(results),
        "terminalStatuses": sorted(statuses),
        "protectedCommonSchemasCopied": False,
    }
    receipt_path = canary / "contract-canary-receipt.json"
    receipt_path.write_text(json.dumps(receipt, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"RECEIPT {receipt_path}")


if __name__ == "__main__":
    main()
