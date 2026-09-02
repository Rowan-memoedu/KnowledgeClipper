import json
import os
import sys
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource


def load(path: Path):
    native = Path("\\\\?\\" + str(path)) if os.name == "nt" and path.is_absolute() else path
    return json.loads(native.read_text(encoding="utf-8"))


def job_path(job_root: Path, relative: str):
    return job_root.joinpath(*relative.replace("\\", "/").split("/"))


def build_registry(schema_root: Path):
    registry = Registry()
    schemas = {}
    for path in sorted(schema_root.rglob("*.schema.json")):
        schema = load(path)
        schema["$id"] = path.resolve().as_uri()
        Draft202012Validator.check_schema(schema)
        registry = registry.with_resource(schema["$id"], Resource.from_contents(schema))
        schemas[path.resolve()] = schema
    return registry, schemas


def validator(path: Path, registry, schemas):
    return Draft202012Validator(schemas[path.resolve()], registry=registry, format_checker=FormatChecker())


def main():
    if len(sys.argv) != 2:
        raise SystemExit("usage: validate_canary.py <canary-receipt.json>")
    receipt_path = Path(sys.argv[1]).resolve()
    receipt = load(receipt_path)
    job_root = Path(receipt["jobRoot"])
    worker_root = Path(__file__).resolve().parents[1]
    project_root = worker_root.parent
    common_root = project_root / "contracts" / "v1" / "schemas"
    registry, schemas = build_registry(common_root)
    common = common_root / "common"

    validator(common / "worker-describe.schema.json", registry, schemas).validate(load(job_root / "describe.json"))
    request = load(Path(receipt["requestPath"]))
    result = load(Path(receipt["resultPath"]))
    validator(common / "worker-request.schema.json", registry, schemas).validate(request)
    validator(common / "worker-result.schema.json", registry, schemas).validate(result)
    artifact_validator = validator(common / "artifact.schema.json", registry, schemas)
    artifact_validator.validate(load(job_path(job_root, request["inputRefs"][0]["manifestPath"])))

    local_schema_by_kind = {
        "learning-bundle": worker_root / "schemas" / "learning-bundle.v1.schema.json",
        "card-bundle": worker_root / "schemas" / "card-bundle.v1.schema.json",
        "language-practice-decision": worker_root / "schemas" / "language-practice-decision.v1.schema.json",
        "question.answer": worker_root / "schemas" / "question-answer.v1.schema.json",
    }
    for output in result["outputs"]:
        manifest = load(job_path(job_root, output["manifestPath"]))
        artifact_validator.validate(manifest)
        local_schema = local_schema_by_kind[output["kind"]]
        local_validator = Draft202012Validator(load(local_schema), format_checker=FormatChecker())
        for file in manifest["files"]:
            local_validator.validate(load(job_path(job_root, file["path"])))

    content_input = load(job_path(job_root, request["payload"]["contentInputPath"]))
    input_schema_by_version = {
        "standard-input/1.0.0": worker_root / "schemas" / "content-input.v1.schema.json",
        "standard-input/2.0.0": worker_root / "schemas" / "content-input.v2.schema.json",
        "standard-input/3.0.0": worker_root / "schemas" / "content-input.v3.schema.json",
    }
    Draft202012Validator(load(input_schema_by_version[content_input["schema_version"]]), format_checker=FormatChecker()).validate(content_input)
    print(
        "PASS content common contract canary: "
        f"describe=1 request=1 result=1 inputArtifacts=1 outputArtifacts={len(result['outputs'])} "
        f"moduleBundles={len(result['outputs'])}"
    )


if __name__ == "__main__":
    main()
