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
    return json.loads(path.read_text(encoding="utf-8-sig"))


def inventory(root: Path, excluded: set[str] | None = None) -> dict[str, Any]:
    excluded = excluded or set()
    rows: list[str] = []
    total = 0
    count = 0
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        if relative in excluded:
            continue
        size = path.stat().st_size
        rows.append(f"{relative}\t{size}\t{sha256_file(path).lower()}")
        total += size
        count += 1
    return {
        "fileCount": count,
        "totalBytes": total,
        "treeSha256": hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper(),
    }


def write_once(path: Path, value: dict[str, Any]) -> None:
    if path.exists():
        raise VerificationError(f"append-only evidence already exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def verified_receipt_files(root: Path, receipt: dict[str, Any]) -> dict[str, dict[str, Any]]:
    result: dict[str, dict[str, Any]] = {}
    for entry in receipt.get("files", []):
        relative = str(entry.get("path", ""))
        path = root / relative
        if not relative or relative in result or not path.is_file():
            raise VerificationError(f"desktop receipt path invalid: {relative}")
        actual = {"bytes": path.stat().st_size, "sha256": sha256_file(path)}
        if actual["bytes"] != entry.get("bytes") or actual["sha256"] != str(entry.get("sha256", "")).upper():
            raise VerificationError(f"desktop receipt content mismatch: {relative}")
        result[relative] = actual
    if len(result) != receipt.get("fileCount") or sum(item["bytes"] for item in result.values()) != receipt.get("totalBytes"):
        raise VerificationError("desktop receipt aggregate mismatch")
    return result


def verify(current: Path, previous: Path, output_dir: Path, expected_authority_tree: str, expected_changed: set[str]) -> tuple[Path, Path]:
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
    current_receipt = load_json(current_receipt_path)
    previous_receipt = load_json(previous_receipt_path)
    current_files = verified_receipt_files(current, current_receipt)
    previous_files = verified_receipt_files(previous, previous_receipt)

    changes: list[dict[str, Any]] = []
    unchanged = 0
    for relative in sorted(set(current_files) | set(previous_files)):
        before = previous_files.get(relative)
        after = current_files.get(relative)
        if before == after:
            unchanged += 1
            continue
        kind = "added" if before is None else "removed" if after is None else "changed"
        changes.append({"path": relative, "kind": kind, "before": before, "after": after})
    actual_changed = {item["path"] for item in changes}
    if actual_changed != expected_changed:
        raise VerificationError(f"desktop drift scope mismatch: expected={sorted(expected_changed)} actual={sorted(actual_changed)}")

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
    drift = {
        "schemaVersion": "knowledge-clipper-desktop-payload-drift.v1",
        "status": "PASS-EXPECTED-DRIFT",
        "currentCandidate": current.parent.name,
        "baselineCandidate": previous.parent.name,
        "currentBuildReceiptSha256": sha256_file(current_receipt_path),
        "baselineBuildReceiptSha256": sha256_file(previous_receipt_path),
        "currentFileCount": len(current_files),
        "baselineFileCount": len(previous_files),
        "unchangedFiles": unchanged,
        "changedFiles": len(changes),
        "changes": changes,
        "currentExeSha256": sha256_file(current / "KnowledgeClipper.exe"),
        "currentDllSha256": sha256_file(current / "KnowledgeClipper.dll"),
        "classification": "intentional Desktop credential-protection and admitted-settings binding change; not an equality rebase",
    }
    manifest_gate_path = output_dir / "manifest-tree-gate.json"
    drift_path = output_dir / "desktop-payload-drift.json"
    write_once(manifest_gate_path, manifest_gate)
    write_once(drift_path, drift)
    return manifest_gate_path, drift_path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--current", type=Path, required=True)
    parser.add_argument("--previous", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--expected-authority-tree", required=True)
    parser.add_argument("--expected-changed", action="append", default=[])
    args = parser.parse_args()
    try:
        paths = verify(args.current, args.previous, args.output_dir, args.expected_authority_tree, set(args.expected_changed))
        print(json.dumps({"status": "PASS-EXPECTED-DRIFT", "evidence": [str(path) for path in paths]}, ensure_ascii=False))
        return 0
    except (VerificationError, OSError, ValueError, json.JSONDecodeError) as exc:
        print(json.dumps({"status": "FAIL", "error": str(exc)}, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
