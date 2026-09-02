import hashlib
import json
import pathlib
import sys

import jsonschema


ROOT = pathlib.Path(__file__).resolve().parents[2]
CONTRACT_ROOT = ROOT / "contracts" / "desktop-automation" / "v1.3"
V12_ROOT = ROOT / "contracts" / "desktop-automation" / "v1.2"
V12_RECEIPT_SHA256 = "33a3526c367ba14f623888be8bbad1429c3f99dfbb9bac586d7794af2f3d371d"
V12_TREE_SHA256 = "d105b10364e3015a85d4b6086a103e7094d7c3158e308280c3a31e06afb65637"


def sha256(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    assert sha256(V12_ROOT / "contract-receipt.json") == V12_RECEIPT_SHA256
    v12_receipt = json.loads((V12_ROOT / "contract-receipt.json").read_text(encoding="utf-8"))
    assert v12_receipt["treeSha256"] == V12_TREE_SHA256
    for item in v12_receipt["files"]:
        path = V12_ROOT / item["path"]
        assert sha256(path) == item["sha256"] and path.stat().st_size == item["bytes"]

    contract = json.loads((CONTRACT_ROOT / "desktop-automation.contract.json").read_text(encoding="utf-8"))
    assert contract["contractVersion"] == "1.3.0" and contract["schemaVersion"] == "desktop-automation.v1.3"
    assert contract["extends"]["receiptSha256"] == V12_RECEIPT_SHA256 and contract["extends"]["treeSha256"] == V12_TREE_SHA256
    assert contract["semanticActions"] == ["ocr-human-resolution.accept-original", "ocr-human-resolution.submit-correction", "ocr-human-resolution.cancel"]
    ids = {item["automationId"] for item in contract["automationIds"]}
    assert len(ids) == 12 and all(value.startswith("knowledge-clipper.desktop.ocr-resolution.") for value in ids)
    assert "task.provide_input" in contract["submissionPolicy"] and "task.cancel" in contract["submissionPolicy"]

    schema = json.loads((CONTRACT_ROOT / "semantic-action.schema.json").read_text(encoding="utf-8"))
    validator = jsonschema.Draft202012Validator(schema)
    valid = list((CONTRACT_ROOT / "fixtures" / "semantic-actions" / "valid").glob("*.json"))
    invalid = list((CONTRACT_ROOT / "fixtures" / "semantic-actions" / "invalid").glob("*.json"))
    assert len(valid) == 3 and len(invalid) == 3
    for path in valid:
        validator.validate(json.loads(path.read_text(encoding="utf-8")))
    for path in invalid:
        try:
            validator.validate(json.loads(path.read_text(encoding="utf-8")))
        except jsonschema.ValidationError:
            continue
        raise AssertionError(f"invalid fixture accepted: {path}")

    receipt = json.loads((CONTRACT_ROOT / "contract-receipt.json").read_text(encoding="utf-8"))
    expected_paths = {path.relative_to(CONTRACT_ROOT).as_posix() for path in CONTRACT_ROOT.rglob("*") if path.is_file() and path.name != "contract-receipt.json"}
    assert {item["path"] for item in receipt["files"]} == expected_paths
    lines = []
    for item in receipt["files"]:
        path = CONTRACT_ROOT / item["path"]
        digest = sha256(path)
        assert digest == item["sha256"] and path.stat().st_size == item["bytes"]
        lines.append((item["path"], digest))
    lines.sort(key=lambda item: item[0].encode("utf-8"))
    tree = hashlib.sha256("".join(f"{path}|{digest}\n" for path, digest in lines).encode("utf-8")).hexdigest()
    assert receipt["treeSha256"] == tree
    print(f"PASS desktop automation v1.3 semanticActions={len(contract['semanticActions'])} automationIds={len(ids)} valid={len(valid)} invalid={len(invalid)} v1.2Immutable=True")
    return 0


if __name__ == "__main__":
    sys.exit(main())
