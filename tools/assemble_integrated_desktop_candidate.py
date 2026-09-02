from __future__ import annotations

import argparse
import hashlib
import json
import shutil
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


PROJECT_ROOT = Path(__file__).resolve().parents[1]


class AssemblyError(RuntimeError):
    pass


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def inventory(root: Path, excluded: set[str] | None = None) -> dict[str, Any]:
    excluded = excluded or set()
    records: list[dict[str, Any]] = []
    rows: list[str] = []
    total = 0
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        if relative in excluded:
            continue
        size = path.stat().st_size
        digest = sha256_file(path)
        records.append({"path": relative, "bytes": size, "sha256": digest})
        rows.append(f"{relative}\t{size}\t{digest.lower()}")
        total += size
    return {
        "files": records,
        "fileCount": len(records),
        "totalBytes": total,
        "treeSha256": hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper(),
        "treeAlgorithm": "sha256(utf8 ordinal(relativePath + TAB + bytes + TAB + lowercaseSha256) joined by LF)",
    }


def desktop_receipt_inventory(root: Path, receipt: dict[str, Any]) -> dict[str, Any]:
    records = sorted(receipt.get("files", []), key=lambda item: item.get("path", ""))
    material: list[str] = []
    total = 0
    seen: set[str] = set()
    for item in records:
        relative = item.get("path", "")
        path = (root / relative).resolve()
        if (
            not relative
            or relative in seen
            or Path(relative).is_absolute()
            or ".." in Path(relative).parts
            or not path.is_relative_to(root.resolve())
            or not path.is_file()
        ):
            raise AssemblyError(f"desktop build receipt path mismatch: {relative}")
        seen.add(relative)
        size = path.stat().st_size
        digest = sha256_file(path).lower()
        if size != item.get("bytes") or digest != str(item.get("sha256", "")).lower():
            raise AssemblyError(f"desktop build receipt content mismatch: {relative}")
        material.append(f"{relative}\n{digest}\n{size}\n")
        total += size
    tree = hashlib.sha256("".join(material).encode("utf-8")).hexdigest().lower()
    if (
        len(records) != receipt.get("fileCount")
        or total != receipt.get("totalBytes")
        or tree != str(receipt.get("treeSha256", "")).lower()
    ):
        raise AssemblyError("desktop build receipt aggregate mismatch")
    return {"fileCount": len(records), "totalBytes": total, "treeSha256": tree.upper()}


def authority_tree(root: Path) -> dict[str, Any]:
    records: list[dict[str, Any]] = []
    rows: list[str] = []
    total = 0
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        size = path.stat().st_size
        digest = sha256_file(path)
        records.append({"path": relative, "bytes": size, "sha256": digest})
        rows.append(f"{relative}\t{size}\t{digest.lower()}")
        total += size
    return {
        "files": records,
        "fileCount": len(records),
        "totalBytes": total,
        "treeSha256": hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper(),
    }


def write_json_once(path: Path, value: dict[str, Any]) -> None:
    if path.exists():
        raise AssemblyError(f"append-only file already exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def assemble(
    desktop_root: Path,
    candidate_root: Path,
    candidate_id: str,
    authority_contract_dir: Path,
    authority_root: Path,
    authority_receipt_path: Path,
    ipc_path: Path,
    expected_authority_tree: str | None,
    expected_checkpoint_sha: str | None,
    expected_registry_sha: str | None,
    pipe_name: str | None,
) -> dict[str, Any]:
    project = PROJECT_ROOT.resolve()
    desktop_root = desktop_root.resolve()
    candidate_root = candidate_root.resolve()
    releases = (project / "releases").resolve()
    if not candidate_root.is_relative_to(releases):
        raise AssemblyError("candidate root must stay below releases")
    if candidate_root.exists():
        raise AssemblyError(f"append-only candidate already exists: {candidate_root}")

    desktop_receipt_path = desktop_root / "desktop-build-receipt.v1.json"
    if not desktop_receipt_path.is_file():
        raise AssemblyError("desktop build receipt is missing")
    desktop_receipt = load_json(desktop_receipt_path)
    desktop_verified = desktop_receipt_inventory(desktop_root, desktop_receipt)
    if desktop_receipt.get("selfContained") is not True or desktop_receipt.get("runtimeIdentifier") != "win-x64":
        raise AssemblyError("desktop source is not the required self-contained win-x64 build")

    authority_contract_dir = authority_contract_dir.resolve()
    authority_root = authority_root.resolve()
    authority_receipt_path = authority_receipt_path.resolve()
    ipc_path = ipc_path.resolve()
    checkpoint_path = authority_contract_dir / "checkpoint.json"
    registry_path = authority_contract_dir / "worker-registry.v1.json"
    dependency_path = authority_contract_dir / "dependency-manifest.v1.json"
    for required in (checkpoint_path, registry_path, dependency_path, ipc_path, authority_receipt_path):
        if not required.is_file():
            raise AssemblyError(f"authority metadata is missing: {required}")
    if not authority_root.is_dir():
        raise AssemblyError("authority release root is missing")
    checkpoint = load_json(checkpoint_path)
    authority = authority_tree(authority_root)
    registry_version = str(checkpoint.get("registry", {}).get("registryVersion", ""))
    if (
        checkpoint.get("admissionStatus") != "authority-admitted"
        or not registry_version
        or authority["fileCount"] != checkpoint.get("release", {}).get("files")
        or authority["totalBytes"] != checkpoint.get("release", {}).get("bytes")
        or authority["treeSha256"] != checkpoint.get("release", {}).get("treeSha256")
    ):
        raise AssemblyError("authority checkpoint/tree mismatch")
    if expected_authority_tree and authority["treeSha256"] != expected_authority_tree.upper():
        raise AssemblyError("authority tree does not match the requested exact release root")
    if expected_checkpoint_sha and sha256_file(checkpoint_path) != expected_checkpoint_sha.upper():
        raise AssemblyError("authority checkpoint SHA-256 mismatch")
    if expected_registry_sha and sha256_file(registry_path) != expected_registry_sha.upper():
        raise AssemblyError("authority registry SHA-256 mismatch")

    candidate_root.mkdir(parents=True)
    # Copy only the immutable, receipt-bound desktop payload. A prior integrated
    # candidate is therefore a valid source without leaking its integration
    # metadata or manifest into the new append-only candidate.
    for entry in desktop_receipt.get("files", []):
        relative = entry["path"]
        destination = candidate_root / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(desktop_root / relative, destination)
    shutil.copy2(desktop_receipt_path, candidate_root / desktop_receipt_path.name)

    copied_authority_root = candidate_root / "authority/win-x64"
    shutil.copytree(authority_root, copied_authority_root)
    metadata = candidate_root / "authority/metadata"
    metadata.mkdir(parents=True)
    for source in (checkpoint_path, registry_path, dependency_path, ipc_path, authority_receipt_path):
        shutil.copy2(source, metadata / source.name)
    ipc_schema_dir = ipc_path.parent / "schemas"
    if ipc_schema_dir.is_dir():
        shutil.copytree(ipc_schema_dir, metadata / "schemas")

    selected_ipc_name = ipc_path.name
    selected_pipe_name = pipe_name or load_json(ipc_path)["pipeName"]
    if pipe_name:
        if not pipe_name.startswith("KnowledgeClipper.") or "\\" in pipe_name or "/" in pipe_name:
            raise AssemblyError("candidate pipe name must be a neutral KnowledgeClipper local pipe identifier")
        derived_ipc = load_json(ipc_path)
        derived_ipc["pipeName"] = pipe_name
        derived_ipc["endpoint"] = rf"\\.\pipe\{pipe_name}"
        schema_version = str(derived_ipc.get("schemaVersion", "")).replace(".", "_")
        if schema_version not in {"1_1_0", "1_2_0", "1_3_0", "1_4_0"}:
            raise AssemblyError("candidate IPC schemaVersion is unsupported")
        selected_ipc_name = f"orchestrator-ipc.candidate.v{schema_version}.json"
        write_json_once(metadata / selected_ipc_name, derived_ipc)

    copied_authority = authority_tree(copied_authority_root)
    if copied_authority != authority:
        raise AssemblyError("copied authority release root differs from the requested source")

    integration = {
        "SchemaVersion": "knowledge-clipper-candidate-integration.v1",
        "AutoStart": True,
        "OrchestratorExecutable": "authority/win-x64/orchestrator/KnowledgeClipper.Orchestrator.exe",
        "IpcConfig": f"authority/metadata/{selected_ipc_name}",
        "Registry": "authority/metadata/worker-registry.v1.json",
        "ReleaseRoot": "authority/win-x64",
        "JobsRoot": "%LOCALAPPDATA%\\KnowledgeClipper\\jobs",
        "PipeName": selected_pipe_name,
        "MaxActiveTasks": 4,
        "AuthorityCheckpointId": checkpoint["checkpointId"],
        "AuthorityTreeSha256": authority["treeSha256"],
    }
    write_json_once(candidate_root / "candidate-integration.json", integration)
    (candidate_root / "README-候选说明.txt").write_text(
        "零碎知识剪藏 - 集成桌面候选\n\n"
        "启动：双击 KnowledgeClipper.exe。请保留整个目录。\n"
        f"本候选包含桌面端与 authority v{registry_version}，并在首次启动时从候选内相对路径启动 orchestrator。\n"
        "若旧版本仍在运行，会尝试唤醒旧窗口并给出明确提示，不会静默假装打开成功，也不会强制结束旧进程。\n",
        encoding="utf-8",
    )

    payload = inventory(candidate_root, {"candidate-file-manifest.v1.json"})
    manifest = {
        "schemaVersion": "knowledge-clipper-integrated-candidate-manifest.v1",
        "candidateId": candidate_id,
        "createdAt": utc_now(),
        "entrypoint": "KnowledgeClipper.exe",
        "desktop": {
            "buildId": desktop_receipt.get("buildId"),
            "buildReceiptSha256": sha256_file(desktop_receipt_path),
            **desktop_verified,
        },
        "authority": {
            "checkpointId": checkpoint["checkpointId"],
            "checkpointSha256": sha256_file(checkpoint_path),
            "registryVersion": registry_version,
            "registrySha256": sha256_file(registry_path),
            "releaseRoot": "authority/win-x64",
            "releaseFiles": authority["fileCount"],
            "releaseBytes": authority["totalBytes"],
            "releaseTreeSha256": authority["treeSha256"],
        },
        "payload": payload,
        "excludedControlFiles": ["candidate-file-manifest.v1.json"],
    }
    manifest_path = candidate_root / "candidate-file-manifest.v1.json"
    write_json_once(manifest_path, manifest)
    sealed = inventory(candidate_root)
    return {
        "status": "assembled",
        "candidateId": candidate_id,
        "candidateRoot": str(candidate_root),
        "entrypoint": str(candidate_root / "KnowledgeClipper.exe"),
        "manifest": str(manifest_path),
        "manifestSha256": sha256_file(manifest_path),
        "sealedFileCount": sealed["fileCount"],
        "sealedTotalBytes": sealed["totalBytes"],
        "sealedTreeSha256": sealed["treeSha256"],
        "authorityTreeSha256": authority["treeSha256"],
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--desktop-root", type=Path, required=True)
    parser.add_argument("--candidate-root", type=Path, required=True)
    parser.add_argument("--candidate-id", required=True)
    parser.add_argument(
        "--authority-contract-dir",
        type=Path,
        default=PROJECT_ROOT / "contracts/authority/worker-registry/v1.2.2-20260828T012500Z",
    )
    parser.add_argument(
        "--authority-root",
        type=Path,
        default=PROJECT_ROOT / "releases/knowledge-clipper-authority-v1.2.2-20260828T012500Z/win-x64",
    )
    parser.add_argument(
        "--authority-receipt",
        type=Path,
        default=PROJECT_ROOT / "receipts/knowledge-clipper-authority-1.2.2-20260828T012800Z.json",
    )
    parser.add_argument(
        "--ipc-config",
        type=Path,
        default=PROJECT_ROOT / "contracts/core/v2.3/playback-control/orchestrator-ipc.v1.1.json",
    )
    parser.add_argument("--expected-authority-tree")
    parser.add_argument("--expected-checkpoint-sha")
    parser.add_argument("--expected-registry-sha")
    parser.add_argument("--pipe-name")
    args = parser.parse_args()
    try:
        print(
            json.dumps(
                assemble(
                    args.desktop_root,
                    args.candidate_root,
                    args.candidate_id,
                    args.authority_contract_dir,
                    args.authority_root,
                    args.authority_receipt,
                    args.ipc_config,
                    args.expected_authority_tree,
                    args.expected_checkpoint_sha,
                    args.expected_registry_sha,
                    args.pipe_name,
                ),
                ensure_ascii=False,
            )
        )
        return 0
    except (AssemblyError, OSError, ValueError, json.JSONDecodeError) as exc:
        print(json.dumps({"status": "failed", "error": str(exc)}, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
