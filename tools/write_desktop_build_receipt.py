from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().lower()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--build-id", required=True)
    args = parser.parse_args()
    root = args.root.resolve()
    receipt_path = root / "desktop-build-receipt.v1.json"
    if receipt_path.exists():
        raise RuntimeError(f"append-only receipt exists: {receipt_path}")
    files = []
    material = []
    total = 0
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        size = path.stat().st_size
        digest = sha256(path)
        files.append({"path": relative, "bytes": size, "sha256": digest})
        material.append(f"{relative}\n{digest}\n{size}\n")
        total += size
    receipt = {
        "schemaVersion": "desktop-build-receipt.v1",
        "buildId": args.build_id,
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "sdkPath": r"D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe",
        "sdkVersion": "8.0.424",
        "targetFramework": "net8.0-windows",
        "runtimeIdentifier": "win-x64",
        "selfContained": True,
        "receiptExcludedFromTreeHash": True,
        "treeHashAlgorithm": "sha256(utf8 concat ordinal(path + LF + sha256 + LF + bytes + LF))",
        "treeSha256": hashlib.sha256("".join(material).encode("utf-8")).hexdigest(),
        "fileCount": len(files),
        "totalBytes": total,
        "executableSha256": sha256(root / "KnowledgeClipper.exe"),
        "applicationDllSha256": sha256(root / "KnowledgeClipper.dll"),
        "files": files,
    }
    receipt_path.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"receipt": str(receipt_path), "receiptSha256": sha256(receipt_path), **{key: receipt[key] for key in ("fileCount", "totalBytes", "treeSha256", "executableSha256", "applicationDllSha256")}}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
