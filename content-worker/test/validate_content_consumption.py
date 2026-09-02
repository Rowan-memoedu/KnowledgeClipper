import argparse
import hashlib
import json
import os
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource


def load(path: Path):
    native = Path("\\\\?\\" + str(path)) if os.name == "nt" and path.is_absolute() else path
    return json.loads(native.read_text(encoding="utf-8"))


def digest(path: Path):
    native = Path("\\\\?\\" + str(path)) if os.name == "nt" and path.is_absolute() else path
    return hashlib.sha256(native.read_bytes()).hexdigest()


def size(path: Path):
    native = Path("\\\\?\\" + str(path)) if os.name == "nt" and path.is_absolute() else path
    return native.stat().st_size


def resolve(root: Path, relative: str):
    return root.joinpath(*relative.replace("\\", "/").split("/"))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--job-root", required=True)
    parser.add_argument("--request", required=True)
    parser.add_argument("--descriptor", required=True)
    args = parser.parse_args()
    job_root = Path(args.job_root).resolve()
    request_path = Path(args.request).resolve()
    worker_root = Path(__file__).resolve().parents[1]
    project_root = worker_root.parent
    schema_root = project_root / "contracts" / "v1" / "schemas"
    registry = Registry()
    schemas = {}
    for schema_path in sorted(schema_root.rglob("*.schema.json")):
        schema = load(schema_path)
        schema["$id"] = schema_path.resolve().as_uri()
        Draft202012Validator.check_schema(schema)
        registry = registry.with_resource(schema["$id"], Resource.from_contents(schema))
        schemas[schema_path.resolve()] = schema

    def validate(schema_path: Path, value):
        Draft202012Validator(schemas[schema_path.resolve()], registry=registry, format_checker=FormatChecker()).validate(value)

    common = schema_root / "common"
    validate(common / "worker-describe.schema.json", load(Path(args.descriptor).resolve()))
    request = load(request_path)
    result = load(resolve(job_root, request["resultPath"]))
    validate(common / "worker-request.schema.json", request)
    validate(common / "worker-result.schema.json", result)
    input_manifest = load(resolve(job_root, request["inputRefs"][0]["manifestPath"]))
    validate(common / "artifact.schema.json", input_manifest)
    assert digest(resolve(job_root, request["inputRefs"][0]["manifestPath"])) == request["inputRefs"][0]["expectedSha256"]
    for file in input_manifest["files"]:
        path = resolve(job_root, file["path"])
        assert size(path) == file["bytes"] and digest(path) == file["sha256"]

    local_schema = {
        "learning-bundle": worker_root / "schemas" / "learning-bundle.v1.schema.json",
        "card-bundle": worker_root / "schemas" / "card-bundle.v1.schema.json",
        "language-practice-decision": worker_root / "schemas" / "language-practice-decision.v1.schema.json",
        "question.answer": worker_root / "schemas" / "question-answer.v1.schema.json",
    }
    for output in result["outputs"]:
        manifest_path = resolve(job_root, output["manifestPath"])
        assert digest(manifest_path) == output["expectedSha256"]
        manifest = load(manifest_path)
        validate(common / "artifact.schema.json", manifest)
        bundle_validator = Draft202012Validator(load(local_schema[output["kind"]]), format_checker=FormatChecker())
        for file in manifest["files"]:
            path = resolve(job_root, file["path"])
            assert size(path) == file["bytes"] and digest(path) == file["sha256"]
            bundle_validator.validate(load(path))
    assert result["status"] == "completed"
    print(f"PASS existing content artifact consumption: inputs={len(input_manifest['files'])} outputs={len(result['outputs'])} worker={result['worker']['id']}/{result['worker']['version']}")


if __name__ == "__main__":
    main()
