import datetime
import hashlib
import json
import os
import pathlib
import tempfile


ROOT = pathlib.Path(__file__).resolve().parents[2]
CONTRACT = ROOT / "contracts" / "desktop-automation" / "v1"
RECEIPT = CONTRACT / "contract-receipt.json"


def main() -> int:
    paths = sorted(
        (path for path in CONTRACT.rglob("*") if path.is_file() and path != RECEIPT),
        key=lambda path: path.relative_to(CONTRACT).as_posix().encode("utf-8"),
    )
    files = []
    canonical_lines = []
    for path in paths:
        relative = path.relative_to(CONTRACT).as_posix()
        payload = path.read_bytes()
        sha256 = hashlib.sha256(payload).hexdigest()
        files.append({"path": relative, "sha256": sha256, "bytes": len(payload)})
        canonical_lines.append(f"{relative}|{sha256}")

    tree_sha256 = hashlib.sha256(("\n".join(canonical_lines) + "\n").encode("utf-8")).hexdigest()
    receipt = {
        "contractId": "rqc.desktop-automation",
        "contractVersion": "1.0.0",
        "verifiedAt": datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00", "Z"),
        "treeHashAlgorithm": "sha256(utf8(relativePath UTF-8 ordinal ascending|fileSha256 + LF))",
        "treeSha256": tree_sha256,
        "receiptExcludedFromTreeHash": True,
        "files": files,
    }

    descriptor, temporary_name = tempfile.mkstemp(prefix="contract-receipt.", suffix=".tmp", dir=CONTRACT)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(receipt, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary_name, RECEIPT)
    finally:
        if os.path.exists(temporary_name):
            os.unlink(temporary_name)
    print(f"TREE_SHA256={tree_sha256}")
    print(f"FILES={len(files)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
