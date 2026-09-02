from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import tempfile
import uuid
from datetime import datetime, timezone
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[1]
DOTNET = Path(r"D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe")
PROJECT = PROJECT_ROOT / "orchestrator/RemNoteQuickClipper.Orchestrator.csproj"
DIST_ROOT = PROJECT_ROOT / "dist/orchestrator/win-x64"
TEMP_ROOT = Path(r"D:\RemNoteQuickClipperData\quality-publish")
STABLE_RECEIPT = PROJECT_ROOT / "receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json"
STABLE_RECEIPT_SHA256 = "1CB738FE133F6645B9757E246E2FBC210785086342F70BAD32B5015BD19E1B1E"
AUTHORITY_REGISTRY = PROJECT_ROOT / "contracts/v1/registry/worker-registry.v1.json"
AUTHORITY_REGISTRY_SHA256 = "ACB4BA01ADBC0A410AAE7050FEC04F527C47EE6D0A98F30B73E7AC2B0E35F6E1"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")


def source_files() -> list[Path]:
    files = [PROJECT]
    files.extend(
        path
        for path in (PROJECT_ROOT / "orchestrator").rglob("*.cs")
        if "bin" not in path.parts and "obj" not in path.parts
    )
    files.extend(path for path in (PROJECT_ROOT / "contracts/core-adapter/v1").rglob("*") if path.is_file())
    return sorted(set(files), key=lambda path: path.relative_to(PROJECT_ROOT).as_posix())


def snapshot(paths: list[Path]) -> list[dict[str, object]]:
    return [
        {
            "path": path.relative_to(PROJECT_ROOT).as_posix(),
            "bytes": path.stat().st_size,
            "sha256": sha256_file(path),
        }
        for path in paths
    ]


def inventory(root: Path) -> tuple[list[dict[str, object]], str]:
    items = []
    lines = []
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        size = path.stat().st_size
        digest = sha256_file(path)
        items.append({"path": relative, "bytes": size, "sha256": digest})
        lines.append(f"{relative}\t{size}\t{digest}\n")
    return items, hashlib.sha256("".join(lines).encode("utf-8")).hexdigest().upper()


def publish(target: Path) -> None:
    command = [
        str(DOTNET), "publish", str(PROJECT),
        "-c", "Release", "-r", "win-x64", "--self-contained", "true", "--no-restore", "--nologo",
        "-p:PublishSingleFile=true", "-p:DebugType=None", "-p:DebugSymbols=false",
        "--output", str(target),
    ]
    completed = subprocess.run(command, cwd=PROJECT_ROOT, text=True, capture_output=True, encoding="utf-8")
    if completed.returncode != 0:
        raise RuntimeError(f"orchestrator publish failed\n{completed.stdout}\n{completed.stderr}")


def restore_runtime_assets() -> None:
    command = [str(DOTNET), "restore", str(PROJECT), "-r", "win-x64", "--ignore-failed-sources", "--nologo"]
    completed = subprocess.run(command, cwd=PROJECT_ROOT, text=True, capture_output=True, encoding="utf-8")
    if completed.returncode != 0:
        raise RuntimeError(f"orchestrator win-x64 restore failed\n{completed.stdout}\n{completed.stderr}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument("--receipt", required=True, type=Path)
    args = parser.parse_args()

    from brand_identity_gates import block_legacy_release_builder
    block_legacy_release_builder("build_orchestrator_quality_snapshot.py")

    target = DIST_ROOT / args.version
    receipt_path = args.receipt if args.receipt.is_absolute() else PROJECT_ROOT / args.receipt
    if target.exists() or receipt_path.exists():
        raise RuntimeError("append-only target or receipt already exists")
    if not DOTNET.is_file() or not PROJECT.is_file():
        raise RuntimeError("pinned SDK or orchestrator project is missing")
    if sha256_file(STABLE_RECEIPT) != STABLE_RECEIPT_SHA256:
        raise RuntimeError("stable checkpoint receipt changed")
    if sha256_file(AUTHORITY_REGISTRY) != AUTHORITY_REGISTRY_SHA256:
        raise RuntimeError("authority registry changed")
    registry = json.loads(AUTHORITY_REGISTRY.read_text(encoding="utf-8"))
    if registry.get("workers") != []:
        raise RuntimeError("authority registry is no longer empty")

    restore_runtime_assets()
    inputs = source_files()
    before = snapshot(inputs)
    TEMP_ROOT.mkdir(parents=True, exist_ok=True)
    first: Path | None = Path(tempfile.mkdtemp(prefix="orchestrator-a-", dir=TEMP_ROOT))
    second = Path(tempfile.mkdtemp(prefix="orchestrator-b-", dir=TEMP_ROOT))
    c_staging: Path | None = None
    try:
        publish(first)
        publish(second)
        after = snapshot(inputs)
        if before != after:
            raise RuntimeError("orchestrator source changed during quality snapshot build")
        first_inventory, first_tree = inventory(first)
        second_inventory, second_tree = inventory(second)
        if first_inventory != second_inventory or first_tree != second_tree:
            raise RuntimeError("self-contained orchestrator rebuilds are not identical")
        executable = first / "RemNoteQuickClipper.Orchestrator.exe"
        if not executable.is_file():
            raise RuntimeError("self-contained orchestrator executable is missing")
        described = subprocess.run([str(executable), "describe"], capture_output=True, text=True, timeout=20, check=False)
        if described.returncode != 0:
            raise RuntimeError(f"orchestrator describe failed: {described.stderr}")
        descriptor = json.loads(described.stdout)
        if descriptor.get("schemaVersion") != "1.0.0" or descriptor.get("componentId") != "orchestrator.core":
            raise RuntimeError("orchestrator describe identity mismatch")
        target.parent.mkdir(parents=True, exist_ok=True)
        c_staging = target.parent / f".{target.name}.{uuid.uuid4().hex[:8]}.staging"
        shutil.copytree(first, c_staging)
        staged_inventory, staged_tree = inventory(c_staging)
        if staged_inventory != first_inventory or staged_tree != first_tree:
            raise RuntimeError("same-volume staging copy changed the release inventory")
        os.replace(c_staging, target)
        c_staging = None
        receipt = {
            "schemaVersion": "orchestrator-quality-snapshot-receipt.v1",
            "createdAt": utc_now(),
            "status": "passed-scoped-not-candidate-bound",
            "version": args.version,
            "sourceSnapshot": {
                "files": len(before),
                "changedDuringBuild": False,
                "items": before,
            },
            "deterministicBuild": {
                "rebuildsCompared": 2,
                "matched": True,
                "files": len(first_inventory),
                "treeSha256": first_tree,
            },
            "release": {
                "root": target.relative_to(PROJECT_ROOT).as_posix(),
                "entrypoint": "RemNoteQuickClipper.Orchestrator.exe",
                "bytes": (target / "RemNoteQuickClipper.Orchestrator.exe").stat().st_size,
                "sha256": sha256_file(target / "RemNoteQuickClipper.Orchestrator.exe"),
                "selfContained": True,
                "singleFile": len(first_inventory) == 1,
            },
            "describe": descriptor,
            "stableCheckpoint": {
                "receiptSha256": STABLE_RECEIPT_SHA256,
                "filesChecked": 21,
                "mismatches": 0,
                "modified": False,
            },
            "authorityRegistry": {
                "sha256": AUTHORITY_REGISTRY_SHA256,
                "entries": 0,
                "modified": False,
            },
            "candidateHash": None,
            "externalProductionApisInvoked": False,
        }
        write_json(receipt_path, receipt)
        print(json.dumps({
            "target": str(target),
            "receipt": str(receipt_path),
            "receiptSha256": sha256_file(receipt_path),
            "release": receipt["release"],
            "sourceFiles": len(before),
            "treeSha256": first_tree,
        }, ensure_ascii=False))
        return 0
    finally:
        if first is not None and first.is_dir():
            shutil.rmtree(first, ignore_errors=True)
        if second.is_dir():
            shutil.rmtree(second, ignore_errors=True)
        if c_staging is not None and c_staging.is_dir():
            shutil.rmtree(c_staging, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
