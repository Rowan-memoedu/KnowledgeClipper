import hashlib
import json
import pathlib
import sys

import jsonschema


ROOT = pathlib.Path(__file__).resolve().parents[2]
CONTRACT_ROOT = ROOT / "contracts" / "desktop-automation" / "v1.4"
V13_ROOT = ROOT / "contracts" / "desktop-automation" / "v1.3"
V13_RECEIPT_SHA256 = "919aaf5b74712c6e1245769dbb035d81005ff8300c0bffe0c911c470b8d3087e"
V13_TREE_SHA256 = "866e70dd7af5980012b817f22950dc2132914abcf39e63091ff2aac6181cd83a"


def sha256(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    assert sha256(V13_ROOT / "contract-receipt.json") == V13_RECEIPT_SHA256
    v13_receipt = json.loads((V13_ROOT / "contract-receipt.json").read_text(encoding="utf-8"))
    assert v13_receipt["treeSha256"] == V13_TREE_SHA256
    for item in v13_receipt["files"]:
        path = V13_ROOT / item["path"]
        assert sha256(path) == item["sha256"] and path.stat().st_size == item["bytes"]

    contract = json.loads((CONTRACT_ROOT / "desktop-automation.contract.json").read_text(encoding="utf-8"))
    assert contract["contractVersion"] == "1.4.0" and contract["schemaVersion"] == "desktop-automation.v1.4"
    assert contract["extends"]["receiptSha256"] == V13_RECEIPT_SHA256 and contract["extends"]["treeSha256"] == V13_TREE_SHA256
    assert contract["semanticActions"] == ["card-resolution.accept-candidate", "card-resolution.submit-correction", "card-resolution.cancel"]
    ids = {item["automationId"] for item in contract["automationIds"]}
    root_id = contract["controlRoles"]["floatingRoot"]
    assert root_id == "knowledge-clipper.desktop.floating-window" and root_id in ids
    assert len(ids) == 7 and len([value for value in ids if value.startswith("knowledge-clipper.desktop.card-resolution.")]) == 6

    schema = json.loads((CONTRACT_ROOT / "semantic-action.schema.json").read_text(encoding="utf-8"))
    validator = jsonschema.Draft202012Validator(schema)
    valid = list((CONTRACT_ROOT / "fixtures" / "semantic-actions" / "valid").glob("*.json"))
    invalid = list((CONTRACT_ROOT / "fixtures" / "semantic-actions" / "invalid").glob("*.json"))
    assert len(valid) == 3 and len(invalid) == 2
    for path in valid:
        validator.validate(json.loads(path.read_text(encoding="utf-8")))
    for path in invalid:
        try:
            validator.validate(json.loads(path.read_text(encoding="utf-8")))
        except jsonschema.ValidationError:
            continue
        raise AssertionError(f"invalid fixture accepted: {path}")

    receipt = json.loads((CONTRACT_ROOT / "contract-receipt.json").read_text(encoding="utf-8"))
    expected = {path.relative_to(CONTRACT_ROOT).as_posix() for path in CONTRACT_ROOT.rglob("*") if path.is_file() and path.name != "contract-receipt.json"}
    assert {item["path"] for item in receipt["files"]} == expected
    lines = []
    for item in receipt["files"]:
        path = CONTRACT_ROOT / item["path"]
        digest = sha256(path)
        assert digest == item["sha256"] and path.stat().st_size == item["bytes"]
        lines.append((item["path"], digest))
    lines.sort(key=lambda item: item[0].encode("utf-8"))
    tree = hashlib.sha256("".join(f"{path}|{digest}\n" for path, digest in lines).encode("utf-8")).hexdigest()
    assert receipt["treeSha256"] == tree
    print(f"PASS desktop automation v1.4 semanticActions={len(contract['semanticActions'])} automationIds={len(ids)} valid={len(valid)} invalid={len(invalid)} v1.3Immutable=True")
    return 0


if __name__ == "__main__":
    sys.exit(main())
