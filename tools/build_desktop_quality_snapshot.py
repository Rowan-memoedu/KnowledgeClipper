from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import struct
import subprocess
import tempfile
import uuid
from datetime import datetime, timezone
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[1]
DOTNET = Path(r"D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe")
PROJECT = PROJECT_ROOT / "desktop/RemNoteQuickClipper.Desktop.csproj"
DIST_ROOT = PROJECT_ROOT / "dist/desktop-win-x64"
TEMP_ROOT = Path(r"D:\RemNoteQuickClipperData\quality-publish")
STABLE_RECEIPT = PROJECT_ROOT / "receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json"
STABLE_RECEIPT_SHA256 = "1CB738FE133F6645B9757E246E2FBC210785086342F70BAD32B5015BD19E1B1E"
DESKTOP_CONTRACT_RECEIPT = PROJECT_ROOT / "contracts/desktop-automation/v1/contract-receipt.json"
DESKTOP_CONTRACT_RECEIPT_SHA256 = "A326242A31A55088EC747891BA7FFE553B1250B24281F72B8738A6506C97EF06"
AUTHORITY_REGISTRY = PROJECT_ROOT / "contracts/v1/registry/worker-registry.v1.json"
AUTHORITY_REGISTRY_SHA256 = "ACB4BA01ADBC0A410AAE7050FEC04F527C47EE6D0A98F30B73E7AC2B0E35F6E1"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        stream.write(json.dumps(value, ensure_ascii=False, indent=2) + "\n")


def source_files() -> list[Path]:
    paths = [
        PROJECT,
        PROJECT_ROOT / "desktop/Directory.Build.props",
        PROJECT_ROOT / "appsettings.json",
        PROJECT_ROOT / "src/Core.cs",
        PROJECT_ROOT / "src/Program.cs",
        PROJECT_ROOT / "src/DesktopUi.cs",
        PROJECT_ROOT / "src/WindowsDesktopProviders.cs",
        PROJECT_ROOT / "src/Compat/JavaScriptSerializer.Net8.cs",
    ]
    paths.extend((PROJECT_ROOT / "src/RemNoteQuickClipper.Desktop.Core").glob("*.cs"))
    return sorted(paths, key=lambda path: path.relative_to(PROJECT_ROOT).as_posix())


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
    items: list[dict[str, object]] = []
    lines: list[str] = []
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        size = path.stat().st_size
        digest = sha256_file(path)
        items.append({"path": relative, "bytes": size, "sha256": digest})
        lines.append(f"{relative}\t{size}\t{digest}\n")
    return items, hashlib.sha256("".join(lines).encode("utf-8")).hexdigest().upper()


def restore() -> None:
    command = [str(DOTNET), "restore", str(PROJECT), "-r", "win-x64", "--ignore-failed-sources", "--nologo"]
    completed = subprocess.run(command, cwd=PROJECT_ROOT, text=True, capture_output=True, encoding="utf-8")
    if completed.returncode != 0:
        raise RuntimeError(f"desktop restore failed\n{completed.stdout}\n{completed.stderr}")


def publish(target: Path) -> None:
    command = [
        str(DOTNET), "publish", str(PROJECT),
        "-c", "Release", "-r", "win-x64", "--self-contained", "true", "--no-restore", "--nologo",
        "-p:DebugType=embedded", "-p:DebugSymbols=false", "--output", str(target),
    ]
    completed = subprocess.run(command, cwd=PROJECT_ROOT, text=True, capture_output=True, encoding="utf-8")
    if completed.returncode != 0:
        raise RuntimeError(f"desktop publish failed\n{completed.stdout}\n{completed.stderr}")


def pe_identity(path: Path) -> dict[str, object]:
    data = path.read_bytes()
    if len(data) < 512 or data[:2] != b"MZ":
        raise RuntimeError("desktop entrypoint is not a PE executable")
    pe_offset = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe_offset:pe_offset + 4] != b"PE\0\0":
        raise RuntimeError("desktop entrypoint has no PE signature")
    machine = struct.unpack_from("<H", data, pe_offset + 4)[0]
    optional = pe_offset + 24
    magic = struct.unpack_from("<H", data, optional)[0]
    subsystem = struct.unpack_from("<H", data, optional + 68)[0]
    if machine != 0x8664 or magic != 0x20B or subsystem != 2:
        raise RuntimeError(f"unexpected desktop PE identity: machine={machine:#x} magic={magic:#x} subsystem={subsystem}")
    return {"machine": "Amd64", "optionalHeader": "PE32+", "subsystem": "WindowsGui"}


def main() -> int:
    parser = argparse.ArgumentParser(description="Create a deterministic desktop quality snapshot")
    parser.add_argument("--version", required=True)
    parser.add_argument("--receipt", required=True, type=Path)
    args = parser.parse_args()

    from brand_identity_gates import block_legacy_release_builder
    block_legacy_release_builder("build_desktop_quality_snapshot.py")

    target = DIST_ROOT / args.version
    receipt_path = args.receipt.resolve() if args.receipt.is_absolute() else (PROJECT_ROOT / args.receipt).resolve()
    if target.exists() or receipt_path.exists():
        raise RuntimeError("append-only desktop snapshot target or receipt already exists")
    pins = {
        STABLE_RECEIPT: STABLE_RECEIPT_SHA256,
        DESKTOP_CONTRACT_RECEIPT: DESKTOP_CONTRACT_RECEIPT_SHA256,
        AUTHORITY_REGISTRY: AUTHORITY_REGISTRY_SHA256,
    }
    for path, expected in pins.items():
        if not path.is_file() or sha256_file(path) != expected:
            raise RuntimeError(f"pinned desktop build input changed: {path}")
    if json.loads(AUTHORITY_REGISTRY.read_text(encoding="utf-8")).get("workers") != []:
        raise RuntimeError("authority registry is no longer empty")

    restore()
    inputs = source_files()
    before = snapshot(inputs)
    TEMP_ROOT.mkdir(parents=True, exist_ok=True)
    first = Path(tempfile.mkdtemp(prefix="desktop-a-", dir=TEMP_ROOT))
    second = Path(tempfile.mkdtemp(prefix="desktop-b-", dir=TEMP_ROOT))
    staging: Path | None = None
    try:
        publish(first)
        publish(second)
        after = snapshot(inputs)
        if before != after:
            raise RuntimeError("desktop source changed during quality snapshot build")
        first_items, first_tree = inventory(first)
        second_items, second_tree = inventory(second)
        if first_items != second_items or first_tree != second_tree:
            raise RuntimeError("desktop self-contained rebuilds are not byte-identical")
        executable = first / "RemNoteQuickClipper.exe"
        identity = pe_identity(executable)
        target.parent.mkdir(parents=True, exist_ok=True)
        staging = target.parent / f".{target.name}.{uuid.uuid4().hex[:8]}.staging"
        shutil.copytree(first, staging)
        staged_items, staged_tree = inventory(staging)
        if staged_items != first_items or staged_tree != first_tree:
            raise RuntimeError("desktop same-volume staging copy changed the build")
        os.replace(staging, target)
        staging = None
        receipt = {
            "schemaVersion": "desktop-quality-snapshot-receipt.v1",
            "createdAt": utc_now(),
            "status": "passed-scoped-not-candidate-bound",
            "version": args.version,
            "sourceSnapshot": {"files": len(before), "changedDuringBuild": False, "items": before},
            "deterministicBuild": {
                "rebuildsCompared": 2,
                "matched": True,
                "files": len(first_items),
                "treeSha256": first_tree,
            },
            "release": {
                "root": target.relative_to(PROJECT_ROOT).as_posix(),
                "entrypoint": "RemNoteQuickClipper.exe",
                "bytes": (target / "RemNoteQuickClipper.exe").stat().st_size,
                "sha256": sha256_file(target / "RemNoteQuickClipper.exe"),
                "applicationDllSha256": sha256_file(target / "RemNoteQuickClipper.dll"),
                "selfContained": True,
                "peIdentity": identity,
            },
            "desktopContract": {"receiptSha256": DESKTOP_CONTRACT_RECEIPT_SHA256, "modified": False},
            "stableCheckpoint": {"receiptSha256": STABLE_RECEIPT_SHA256, "filesChecked": 21, "mismatches": 0, "modified": False},
            "authorityRegistry": {"sha256": AUTHORITY_REGISTRY_SHA256, "entries": 0, "modified": False},
            "physicalWindowsBoundaries": "pending-not-claimed",
            "candidateHash": None,
            "externalProductionApisInvoked": False,
        }
        write_json(receipt_path, receipt)
        print(json.dumps({
            "target": str(target),
            "receipt": str(receipt_path),
            "receiptSha256": sha256_file(receipt_path),
            "release": receipt["release"],
            "files": len(first_items),
            "treeSha256": first_tree,
        }, ensure_ascii=False))
        return 0
    finally:
        shutil.rmtree(first, ignore_errors=True)
        shutil.rmtree(second, ignore_errors=True)
        if staging is not None:
            shutil.rmtree(staging, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
