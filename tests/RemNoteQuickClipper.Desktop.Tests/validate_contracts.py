import json
import hashlib
import pathlib
import sys

from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource


ROOT = pathlib.Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "contracts" / "desktop-automation" / "v1"
COMMON = ROOT / "contracts" / "v1" / "schemas" / "common"


def load(path: pathlib.Path):
    return json.loads(path.read_text(encoding="utf-8"))


def validate(schema_name: str, instance):
    schema_path = CONTRACT / schema_name
    schema = load(schema_path)
    schema["$id"] = schema_path.resolve().as_uri()
    common_artifact_path = COMMON / "artifact.schema.json"
    common_artifact = load(common_artifact_path)
    common_artifact["$id"] = common_artifact_path.resolve().as_uri()
    registry = Registry().with_resource(
        common_artifact_path.resolve().as_uri(),
        Resource.from_contents(common_artifact),
    )
    Draft202012Validator(schema, registry=registry, format_checker=FormatChecker()).validate(instance)


def main() -> int:
    contract = load(CONTRACT / "desktop-automation.contract.json")
    actions = contract["semanticActions"]
    assert len(actions) == len(set(actions))
    automation_ids = [item["automationId"] for item in contract["automationIds"]]
    assert len(automation_ids) == len(set(automation_ids))
    mapped_actions = {item["semanticAction"] for item in contract["automationIds"] if item["semanticAction"]}
    assert set(actions) == mapped_actions

    action_lines = (CONTRACT / "fixtures" / "semantic-actions.valid.ndjson").read_text(encoding="utf-8").splitlines()
    for line in action_lines:
        if line.strip():
            validate("semantic-action.schema.json", json.loads(line))

    validate("capture-bundle.v1.schema.json", load(CONTRACT / "fixtures" / "capture-bundle.multi-image.valid.json"))
    boundary_valid = load(CONTRACT / "fixtures" / "capture-bundle.artifact-ref-128.valid.json")
    boundary_invalid = load(CONTRACT / "fixtures" / "capture-bundle.artifact-ref-129.invalid.json")
    valid_ref = boundary_valid["artifacts"][0]["artifactRef"]
    invalid_ref = boundary_invalid["artifacts"][0]["artifactRef"]
    assert len(valid_ref) == 128 and boundary_valid["questionImages"] == [valid_ref]
    assert len(invalid_ref) == 129 and boundary_invalid["questionImages"] == [invalid_ref]
    validate("capture-bundle.v1.schema.json", boundary_valid)
    try:
        validate("capture-bundle.v1.schema.json", boundary_invalid)
    except Exception:
        pass
    else:
        raise AssertionError("129-character capture artifactRef fixture must fail")

    capture_schema = load(CONTRACT / "capture-bundle.v1.schema.json")
    common_ref = capture_schema["$defs"]["captureArtifactRef"]["allOf"][0]["$ref"].split("#", 1)[0]
    assert (CONTRACT / common_ref).resolve() == (COMMON / "artifact.schema.json").resolve()
    invalid = load(CONTRACT / "fixtures" / "capture-bundle.answer-only.invalid.json")
    try:
        validate("capture-bundle.v1.schema.json", invalid)
    except Exception:
        pass
    else:
        raise AssertionError("answer-only screenshot fixture must fail")

    forbidden = {
        "task-command.schema.json",
        "command-receipt.schema.json",
        "task-status.schema.json",
        "task-query.schema.json",
        "task-query-result.schema.json",
        "orchestrator-ipc.v1.schema.json",
        "worker-result.schema.json",
    }
    assert not forbidden.intersection({p.name for p in CONTRACT.rglob("*.json")})

    receipt = load(CONTRACT / "contract-receipt.json")
    expected_paths = {p.relative_to(CONTRACT).as_posix() for p in CONTRACT.rglob("*") if p.is_file() and p.name != "contract-receipt.json"}
    receipt_paths = {item["path"] for item in receipt["files"]}
    assert receipt_paths == expected_paths
    canonical_lines = []
    for item in sorted(receipt["files"], key=lambda value: value["path"]):
        payload = (CONTRACT / item["path"]).read_bytes()
        assert len(payload) == item["bytes"]
        actual = hashlib.sha256(payload).hexdigest()
        assert actual == item["sha256"]
        canonical_lines.append(f'{item["path"]}|{actual}')
    tree = hashlib.sha256(("\n".join(canonical_lines) + "\n").encode("utf-8")).hexdigest()
    assert tree == receipt["treeSha256"]

    references = contract["commonContractReferences"]
    for relative in references.values():
        assert (CONTRACT / relative).resolve().is_file(), relative
    checkpoint = contract["commonCheckpoint"]
    checkpoint_path = (CONTRACT / checkpoint["receipt"]).resolve()
    assert checkpoint_path.is_file()
    assert hashlib.sha256(checkpoint_path.read_bytes()).hexdigest() == checkpoint["receiptSha256"]
    assert load(checkpoint_path)["checkpointId"] == checkpoint["checkpointId"]

    fixtures = pathlib.Path(__file__).resolve().parent / "fixtures"
    common_documents = []
    for path in COMMON.glob("*.json"):
        document = load(path)
        document["$id"] = path.resolve().as_uri()
        common_documents.append((path.resolve().as_uri(), Resource.from_contents(document)))
    registry = Registry().with_resources(common_documents)
    common_pairs = [
        ("task-command.schema.json", "task-submit.valid.json"),
        ("task-query.schema.json", "task-query-recent.valid.json"),
        ("task-query-result.schema.json", "task-query-result.valid.json"),
    ]
    for schema_name, fixture_name in common_pairs:
        schema_path = COMMON / schema_name
        schema = load(schema_path)
        schema["$id"] = schema_path.resolve().as_uri()
        Draft202012Validator(schema, registry=registry, format_checker=FormatChecker()).validate(load(fixtures / fixture_name))
    print("PASS desktop contract schemas and fixtures")
    return 0


if __name__ == "__main__":
    sys.exit(main())
