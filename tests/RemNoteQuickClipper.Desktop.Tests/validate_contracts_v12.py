import hashlib
import json
import pathlib
import sys


ROOT = pathlib.Path(__file__).resolve().parents[2]
CONTRACT_ROOT = ROOT / "contracts" / "desktop-automation" / "v1.2"
V11_ROOT = ROOT / "contracts" / "desktop-automation" / "v1.1"
V11_RECEIPT_SHA256 = "b0d491b99a37315583efc27a22592c5af13ae150bd9f10f96bdb7a3d1d353953"
V11_TREE_SHA256 = "a05c8720c0ee8a493c403225957f497072e97fe36f836dc566c7d584d44c2276"


def sha256(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    assert sha256(V11_ROOT / "contract-receipt.json") == V11_RECEIPT_SHA256
    v11_receipt = json.loads((V11_ROOT / "contract-receipt.json").read_text(encoding="utf-8"))
    assert v11_receipt["treeSha256"] == V11_TREE_SHA256
    for item in v11_receipt["files"]:
        path = V11_ROOT / item["path"]
        assert sha256(path) == item["sha256"] and path.stat().st_size == item["bytes"]

    contract = json.loads((CONTRACT_ROOT / "desktop-automation.contract.json").read_text(encoding="utf-8"))
    assert contract["contractId"] == "knowledge-clipper.desktop-automation" and contract["contractVersion"] == "1.2.0"
    assert contract["changeType"] == "additive-ui-automation"
    assert contract["extends"]["receiptSha256"] == V11_RECEIPT_SHA256 and contract["extends"]["treeSha256"] == V11_TREE_SHA256
    assert contract["semanticActionSchemaVersion"] == "desktop-automation.v1.1" and contract["semanticActions"] == []
    assert contract["automationIds"] == [{"automationId": "knowledge-clipper.desktop.question.inline-input", "semanticAction": None}]
    assert contract["inlineQuestionInput"]["hint"] == "输入问题，按 Enter 提问"

    receipt = json.loads((CONTRACT_ROOT / "contract-receipt.json").read_text(encoding="utf-8"))
    expected_paths = {path.relative_to(CONTRACT_ROOT).as_posix() for path in CONTRACT_ROOT.rglob("*") if path.is_file() and path.name != "contract-receipt.json"}
    assert receipt["contractId"] == contract["contractId"] and receipt["contractVersion"] == contract["contractVersion"]
    assert {item["path"] for item in receipt["files"]} == expected_paths
    canonical_lines = []
    for item in receipt["files"]:
        path = CONTRACT_ROOT / item["path"]
        digest = sha256(path)
        assert digest == item["sha256"] and path.stat().st_size == item["bytes"]
        canonical_lines.append((item["path"], digest))
    canonical_lines.sort(key=lambda item: item[0].encode("utf-8"))
    tree = hashlib.sha256(("".join(f"{path}|{digest}\n" for path, digest in canonical_lines)).encode("utf-8")).hexdigest()
    assert tree == receipt["treeSha256"]
    print(f"PASS desktop automation v1.2 additiveIds=1 semanticActions=0 receiptFiles={len(receipt['files'])} v1.1Immutable=True")
    return 0


if __name__ == "__main__":
    sys.exit(main())
