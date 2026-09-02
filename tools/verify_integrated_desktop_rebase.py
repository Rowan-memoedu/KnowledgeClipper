from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any


class VerificationError(RuntimeError):
    pass


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def inventory(root: Path, excluded: set[str] | None = None) -> dict[str, Any]:
    excluded = excluded or set()
    rows: list[str] = []
    files = 0
    total = 0
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        if relative in excluded:
            continue
        size = path.stat().st_size
        digest = sha256_file(path)
        rows.append(f"{relative}\t{size}\t{digest.lower()}")
        files += 1
        total += size
    return {
        "fileCount": files,
        "totalBytes": total,
        "treeSha256": hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper(),
    }


def write_once(path: Path, value: dict[str, Any]) -> None:
    if path.exists():
        raise VerificationError(f"append-only evidence already exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")


def verify(current: Path, previous: Path, output_dir: Path, expected_authority_tree: str) -> tuple[Path, Path]:
    current = current.resolve()
    previous = previous.resolve()
    manifest_path = current / "candidate-file-manifest.v1.json"
    manifest = load_json(manifest_path)
    payload = inventory(current, {"candidate-file-manifest.v1.json"})
    expected_payload = manifest.get("payload", {})
    if any(payload[key] != expected_payload.get(key) for key in ("fileCount", "totalBytes", "treeSha256")):
        raise VerificationError("candidate manifest payload aggregate mismatch")
    for entry in expected_payload.get("files", []):
        path = current / entry["path"]
        if not path.is_file() or path.stat().st_size != entry["bytes"] or sha256_file(path) != entry["sha256"]:
            raise VerificationError(f"candidate manifest file mismatch: {entry['path']}")
    authority = inventory(current / "authority/win-x64")
    if authority["treeSha256"] != expected_authority_tree.upper():
        raise VerificationError("embedded authority tree mismatch")
    sealed = inventory(current)

    current_receipt_path = current / "desktop-build-receipt.v1.json"
    previous_receipt_path = previous / "desktop-build-receipt.v1.json"
    if sha256_file(current_receipt_path) != sha256_file(previous_receipt_path):
        raise VerificationError("desktop build receipt changed")
    desktop_receipt = load_json(current_receipt_path)
    equal_files: list[dict[str, Any]] = []
    for entry in sorted(desktop_receipt.get("files", []), key=lambda item: item["path"]):
        current_file = current / entry["path"]
        previous_file = previous / entry["path"]
        if not current_file.is_file() or not previous_file.is_file():
            raise VerificationError(f"desktop file missing: {entry['path']}")
        current_hash = sha256_file(current_file)
        previous_hash = sha256_file(previous_file)
        if (
            current_file.stat().st_size != entry["bytes"]
            or previous_file.stat().st_size != entry["bytes"]
            or current_hash != str(entry["sha256"]).upper()
            or previous_hash != str(entry["sha256"]).upper()
        ):
            raise VerificationError(f"desktop file changed: {entry['path']}")
        equal_files.append({"path": entry["path"], "bytes": entry["bytes"], "sha256": current_hash})
    if len(equal_files) != desktop_receipt.get("fileCount"):
        raise VerificationError("desktop file count mismatch")

    manifest_gate = {
        "schemaVersion": "knowledge-clipper-integrated-manifest-tree-gate.v1",
        "status": "PASS",
        "manifestSha256": sha256_file(manifest_path),
        "manifestPayloadFileCount": payload["fileCount"],
        "manifestPayloadBytes": payload["totalBytes"],
        "manifestPayloadTreeSha256": payload["treeSha256"],
        "candidateFileCount": sealed["fileCount"],
        "candidateTotalBytes": sealed["totalBytes"],
        "candidateTreeSha256": sealed["treeSha256"],
        "authorityFileCount": authority["fileCount"],
        "authorityTotalBytes": authority["totalBytes"],
        "authorityTreeSha256": authority["treeSha256"],
    }
    desktop_equality = {
        "schemaVersion": "knowledge-clipper-desktop-payload-equality.v1",
        "status": "PASS",
        "currentCandidate": current.parent.name,
        "previousCandidate": previous.parent.name,
        "buildReceiptSha256": sha256_file(current_receipt_path),
        "fileCount": len(equal_files),
        "changedFiles": 0,
        "exeSha256": sha256_file(current / "KnowledgeClipper.exe"),
        "dllSha256": sha256_file(current / "KnowledgeClipper.dll"),
        "files": equal_files,
    }
    manifest_gate_path = output_dir / "manifest-tree-gate.json"
    desktop_equality_path = output_dir / "desktop-payload-equality.json"
    write_once(manifest_gate_path, manifest_gate)
    write_once(desktop_equality_path, desktop_equality)
    return manifest_gate_path, desktop_equality_path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--current", type=Path, required=True)
    parser.add_argument("--previous", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--expected-authority-tree", required=True)
    args = parser.parse_args()
    try:
        paths = verify(args.current, args.previous, args.output_dir, args.expected_authority_tree)
        print(json.dumps({"status": "PASS", "evidence": [str(path) for path in paths]}, ensure_ascii=False))
        return 0
    except (VerificationError, OSError, ValueError, json.JSONDecodeError) as exc:
        print(json.dumps({"status": "FAIL", "error": str(exc)}, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
