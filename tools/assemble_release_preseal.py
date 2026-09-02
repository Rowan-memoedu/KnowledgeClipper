from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


PROJECT_ROOT = Path(__file__).resolve().parents[1]
RELEASES_ROOT = PROJECT_ROOT / "releases"
STABLE_CHECKPOINT_SHA256 = "1CB738FE133F6645B9757E246E2FBC210785086342F70BAD32B5015BD19E1B1E"

PINNED_FILES = {
    "dist/desktop-win-x64/desktop-v1-20260825T160000Z/desktop-build-receipt.v1.json":
        "071A05F1CDFB9D8DB03AD396046D78140508184A2059C6194B66BC51C269BAC1",
    "canary/desktop-windows/20260825T144231Z/receipt.json":
        "C33BA65FF86E282274BBE06ED58F34BE500E94FEEDA8D7F65DF87F6C3421FBD1",
    "canary/desktop-production-pipe/desktop-production-pipe-20260825T174500Z/receipt.json":
        "2E0A9DECD86D031CD5ADE2649E57916084920FF23F626548FC20AFFA0032AA1B",
    "canary/desktop-production-submit/desktop-production-submit-20260825T175000Z/receipt.json":
        "F5B24B0EA5EA006E384158218D6FAA720B19F22BDFD94F54198B356B9D751489",
    "canary/edge-tts-windows/edge-tts-windows-20260825T162500Z/receipt.json":
        "D28DA745D9EE9492B8CA86AFBE12FC158D8A1CB54BC896087628FA846E73209E",
    "receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json": STABLE_CHECKPOINT_SHA256,
    "receipts/image-ocr-worker-cli-validation-20260825T140210Z.json":
        "915F7BB81A621820D91A6E05BE4BAB59420A1D0576F92E34DCD9FAA0B11EC351",
    "receipts/content-worker-v0.2.0-final-20260825T142504Z.json":
        "BACA6E785646791262736EFBD39416CA801F41E70A340CB910FCBB92BEB1A94C",
    "receipts/remnote-exporter-worker-v1-20260825T142637Z.json":
        "F7326F4DE30EC05B807FC42EFB78E34380DC3DF3374C280E6F5B063BDA94B163",
    "workers/image-ocr/RemNoteQuickClipper.ImageOcr.Worker.exe":
        "2A5B38911A027D7C05ADB7A423C28CA7BA906D8C134B23C40F114054120D1956",
    "workers/anki/RemNoteQuickClipper.AnkiExporter.Worker.exe":
        "ACE0037F1E3160A88803FDAE1F565213D147D16447066C8B3D3C02ACE98CF17D",
    "workers/anki/canary/xv-20260825T151951-cf28/exe-validation-result.json":
        "842B804563358C1BDC1F1C17E893EF101C707242A2B1C0506BA59B6E1BAED941",
    "workers/remnote/RemNoteQuickClipper.RemNoteExporter.Worker.exe":
        "9F7F7C1380073510A80E964864F6B59DABCB99D2082920273D0CBB28C1688B29",
    "tests/RemNoteQuickClipper.Speech.Tests/registry-canary/r20260825-115811-291091/registry-canary-receipt.json":
        "0A5C393032C6EC2854BEB4489A1B882C81079D025EFF6195A5B098333E7FE537",
    "tests/RemNoteQuickClipper.Speech.Tests/registry-canary/r20260825-115811-291091/release/workers/RemNoteQuickClipper.Speech.Worker.exe":
        "32E65EDF5D23F87A60E5826278DE06F3B5EC8066006D243ECD58D3FD3A272470",
}


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")


def verify_pins() -> None:
    for relative, expected in PINNED_FILES.items():
        path = PROJECT_ROOT / relative
        if not path.is_file():
            raise RuntimeError(f"pinned evidence missing: {relative}")
        actual = sha256_file(path)
        if actual != expected:
            raise RuntimeError(f"pinned evidence mismatch: {relative}: {actual}")


def verify_desktop_receipt() -> dict[str, Any]:
    source = PROJECT_ROOT / "dist/desktop-win-x64/desktop-v1-20260825T160000Z"
    receipt = json.loads((source / "desktop-build-receipt.v1.json").read_text(encoding="utf-8"))
    mismatches: list[str] = []
    for item in receipt["files"]:
        path = source / item["path"]
        if not path.is_file() or path.stat().st_size != item["bytes"] or sha256_file(path).lower() != item["sha256"].lower():
            mismatches.append(item["path"])
    if len(receipt["files"]) != 243 or mismatches:
        raise RuntimeError(f"desktop build receipt mismatch: files={len(receipt['files'])}, mismatches={mismatches}")
    desktop_exe = source / "RemNoteQuickClipper.exe"
    expected = "D6A478C72462BB86916F3C5899DB0DBED13F56FD0A7D154A778A08F265108772"
    if sha256_file(desktop_exe) != expected:
        raise RuntimeError("desktop executable hash mismatch")
    return {"filesChecked": 243, "mismatches": 0, "executableSha256": expected}


def copy_tree(source: Path, target: Path) -> None:
    if not source.is_dir():
        raise RuntimeError(f"source directory missing: {source}")
    shutil.copytree(source, target)


def publish_single_file(project: Path, target: Path, dotnet: Path, expected_executable: str) -> None:
    short_root = Path(r"D:\RemNoteQuickClipperData\rc-publish")
    short_root.mkdir(parents=True, exist_ok=True)
    publish_root = Path(tempfile.mkdtemp(prefix="rqc-", dir=short_root))
    command = [
        str(dotnet), "publish", str(project),
        "-c", "Release", "-r", "win-x64", "--self-contained", "true",
        "-p:PublishSingleFile=true", "-p:DebugType=None", "-p:DebugSymbols=false",
        "--output", str(publish_root),
    ]
    try:
        completed = subprocess.run(command, cwd=PROJECT_ROOT, text=True, capture_output=True, encoding="utf-8")
        if completed.returncode != 0:
            raise RuntimeError(f"self-contained bootstrap publish failed: {project}\n{completed.stdout}\n{completed.stderr}")
        executable = publish_root / expected_executable
        if not executable.is_file():
            raise RuntimeError(f"self-contained executable was not produced: {executable}")
        shutil.copytree(publish_root, target)
    finally:
        shutil.rmtree(publish_root, ignore_errors=True)


def copy_python_edge_runtime(target: Path) -> None:
    base = Path(r"C:\Users\fy\AppData\Local\Python\pythoncore-3.14-64")
    edge_site_packages = Path(r"D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Lib\site-packages")

    def ignore_base(directory: str, names: list[str]) -> set[str]:
        current = Path(directory)
        ignored = {name for name in names if name == "__pycache__"}
        if current == base / "Lib":
            ignored.add("site-packages")
        if current == base:
            ignored.update({"Scripts", "Doc", "include", "Tools"})
        return ignored

    shutil.copytree(base, target, ignore=ignore_base)
    shutil.copytree(
        edge_site_packages,
        target / "Lib/site-packages",
        ignore=shutil.ignore_patterns("__pycache__", "*.pyc", "pip", "pip-*.dist-info"),
    )


def copy_remnote_cli_runtime(target: Path) -> None:
    source = Path(r"C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote MCP Integration\node_modules")

    def ignore_runtime(_directory: str, names: list[str]) -> set[str]:
        ignored: set[str] = set()
        for name in names:
            lower = name.lower()
            if lower in {"examples", "example", "tests", "test", "__tests__", "docs", "doc"}:
                ignored.add(name)
            elif lower.endswith((".d.ts", ".d.ts.map", ".js.map", ".cjs.map", ".mjs.map")):
                ignored.add(name)
        return ignored

    shutil.copytree(source, target, ignore=ignore_runtime)


def candidate_paths(installed_root: Path, version: str) -> dict[str, str]:
    return {
        "nodePath": str(installed_root / "runtime/node-v24.18.0-win-x64/node.exe"),
        "remnoteCliJsPath": str(installed_root / "runtime/remnote-mcp/node_modules/remnote-mcp-server/dist/remnote-cli/index.js"),
        "pythonPath": str(installed_root / "runtime/python-edge-tts-3.14.6/python.exe"),
        "edgeTtsPath": str(installed_root / "runtime/edge-tts-7.2.8/edge-tts.exe"),
        "ffplayPath": str(installed_root / "runtime/ffplay-8.1.2/ffplay.exe"),
        "workDirectory": f"%LOCALAPPDATA%\\RemNoteQuickClipper\\RC\\{version}",
    }


def configure_candidate(root: Path, installed_root: Path, version: str) -> None:
    paths = candidate_paths(installed_root, version)
    desktop_config = {
        "NodePath": paths["nodePath"],
        "RemnoteCliJsPath": paths["remnoteCliJsPath"],
        "RemnoteMcpUrl": "http://127.0.0.1:43101/mcp",
        "PythonPath": paths["pythonPath"],
        "MineruScriptPath": "",
        "SpeechKitPath": "",
        "EdgeTtsPath": paths["edgeTtsPath"],
        "FfplayPath": paths["ffplayPath"],
        "WorkDirectory": paths["workDirectory"],
        "TargetDocumentTitle": "剪藏",
        "DateFormat": "yyyy-MM-dd",
        "DeepSeekBaseUrl": "https://api.deepseek.com",
        "DeepSeekModel": "deepseek-chat",
        "DeepSeekMaxRetries": 3,
    }
    write_json(root / "desktop/appsettings.json", desktop_config)
    write_json(root / "workers/remnote/appsettings.json", desktop_config)
    write_json(root / "config/worker-runtime-paths.v1.json", {
        "schemaVersion": "1.0.0",
        "releaseVersion": version,
        "paths": paths,
        "externalServices": {
            "deepseek": "required-at-runtime-not-invoked-during-preseal",
            "mineru": "required-at-runtime-not-invoked-during-preseal",
            "remnoteLocalMcp": "required-at-runtime-not-invoked-during-preseal",
            "ankiConnect": "required-at-runtime-not-invoked-during-preseal",
            "edgeTtsNetwork": "required-at-runtime-not-invoked-during-preseal",
        },
        "authorityRegistry": "unchanged-empty-stable-v1",
    })


def inventory(root: Path) -> tuple[int, int, str]:
    entries: list[str] = []
    total_bytes = 0
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        size = path.stat().st_size
        digest = sha256_file(path)
        total_bytes += size
        entries.append(f"{relative}\t{size}\t{digest}\n")
    tree_hash = hashlib.sha256("".join(entries).encode("utf-8")).hexdigest().upper()
    return len(entries), total_bytes, tree_hash


def assemble(version: str) -> tuple[Path, dict[str, Any]]:
    target = RELEASES_ROOT / version / "win-x64"
    if target.exists() or target.parent.exists():
        raise RuntimeError(f"append-only target already exists: {target.parent}")
    RELEASES_ROOT.mkdir(parents=True, exist_ok=True)
    staging_parent = RELEASES_ROOT / f".p-{uuid.uuid4().hex[:8]}"
    root = staging_parent / "win-x64"
    root.mkdir(parents=True)
    try:
        verify_pins()
        desktop = verify_desktop_receipt()
        copy_tree(PROJECT_ROOT / "dist/desktop-win-x64/desktop-v1-20260825T160000Z", root / "desktop")
        (root / "workers/image-ocr").mkdir(parents=True)
        shutil.copy2(PROJECT_ROOT / "workers/image-ocr/RemNoteQuickClipper.ImageOcr.Worker.exe", root / "workers/image-ocr")
        (root / "workers/anki").mkdir(parents=True)
        shutil.copy2(PROJECT_ROOT / "workers/anki/RemNoteQuickClipper.AnkiExporter.Worker.exe", root / "workers/anki")
        (root / "workers/remnote").mkdir(parents=True)
        shutil.copy2(PROJECT_ROOT / "workers/remnote/RemNoteQuickClipper.RemNoteExporter.Worker.exe", root / "workers/remnote")
        (root / "workers/speech").mkdir(parents=True)
        shutil.copy2(
            PROJECT_ROOT / "tests/RemNoteQuickClipper.Speech.Tests/registry-canary/r20260825-115811-291091/release/workers/RemNoteQuickClipper.Speech.Worker.exe",
            root / "workers/speech/RemNoteQuickClipper.Speech.Worker.exe",
        )

        dotnet = Path(r"D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe")
        publish_single_file(
            PROJECT_ROOT / "content-worker/launcher/RemNoteQuickClipper.Content.Worker.csproj",
            root / "workers/content",
            dotnet,
            "RemNoteQuickClipper.Content.Worker.exe",
        )
        copy_tree(PROJECT_ROOT / "content-worker/dist", root / "content-worker/dist")
        copy_tree(PROJECT_ROOT / "content-worker/prompts", root / "content-worker/prompts")
        copy_tree(PROJECT_ROOT / "content-worker/config", root / "content-worker/config")
        copy_tree(PROJECT_ROOT / "content-worker/schemas", root / "content-worker/schemas")
        shutil.copy2(PROJECT_ROOT / "content-worker/package.json", root / "content-worker/package.json")

        (root / "runtime/node-v24.18.0-win-x64").mkdir(parents=True)
        shutil.copy2(Path(r"C:\Program Files\nodejs\node.exe"), root / "runtime/node-v24.18.0-win-x64/node.exe")
        copy_python_edge_runtime(root / "runtime/python-edge-tts-3.14.6")
        publish_single_file(
            PROJECT_ROOT / "tools/release_bootstraps/EdgeTtsBootstrap/EdgeTtsBootstrap.csproj",
            root / "runtime/edge-tts-7.2.8",
            dotnet,
            "edge-tts.exe",
        )
        (root / "runtime/ffplay-8.1.2").mkdir(parents=True)
        shutil.copy2(Path(r"D:\Applications\P47\FFmpeg\ffmpeg-8.1.2-full_build\bin\ffplay.exe"), root / "runtime/ffplay-8.1.2/ffplay.exe")
        copy_remnote_cli_runtime(root / "runtime/remnote-mcp/node_modules")
        copy_tree(PROJECT_ROOT / "contracts/v1", root / "contracts/v1")
        copy_tree(PROJECT_ROOT / "contracts/desktop-automation/v1", root / "contracts/desktop-automation/v1")
        configure_candidate(root, target, version)
        write_json(root / "PRESEAL.v1.json", {
            "schemaVersion": "release-preseal.v1",
            "releaseVersion": version,
            "state": "unsealed-awaiting-core-profile-intent-resolver",
            "releaseManifest": None,
            "candidateHash": None,
            "oldProductionPipeReceipt": "invalidated-T161500Z",
            "authoritativeProductionQueryReceipt": {
                "path": "canary/desktop-production-pipe/desktop-production-pipe-20260825T174500Z/receipt.json",
                "sha256": PINNED_FILES["canary/desktop-production-pipe/desktop-production-pipe-20260825T174500Z/receipt.json"],
            },
            "safeSubmitFailureReceipt": {
                "path": "canary/desktop-production-submit/desktop-production-submit-20260825T175000Z/receipt.json",
                "sha256": PINNED_FILES["canary/desktop-production-submit/desktop-production-submit-20260825T175000Z/receipt.json"],
                "reason": "desktop-intent.v1-is-not-a-PipelinePlan",
            },
            "pending": [
                "new-orchestrator-completion-receipt-and-executable",
                "release-manifest-and-candidateHash",
                "authority-registry-checkpoint",
                "physical-windows-boundaries",
                "candidate-bound-worker-canaries",
                "desktop-to-real-remnote-e2e",
            ],
        })
        files, total_bytes, tree_hash = inventory(root)
        receipt = {
            "schemaVersion": "release-preseal-layout-receipt.v1",
            "createdAt": utc_now(),
            "releaseVersion": version,
            "releaseRoot": str(target),
            "status": "unsealed-awaiting-core",
            "releaseManifest": None,
            "candidateHash": None,
            "desktopSource": desktop,
            "inventory": {"files": files, "bytes": total_bytes, "presealTreeSha256": tree_hash},
            "stableCheckpoint": {"filesChecked": 21, "mismatches": 0, "receiptSha256": STABLE_CHECKPOINT_SHA256},
            "authorityRegistryModified": False,
            "externalProductionApisInvoked": False,
        }
        os.replace(staging_parent, target.parent)
        return target, receipt
    except Exception:
        shutil.rmtree(staging_parent, ignore_errors=True)
        raise


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument("--receipt", required=True, type=Path)
    args = parser.parse_args()
    try:
        from brand_identity_gates import block_legacy_release_builder
        block_legacy_release_builder("assemble_release_preseal.py")
        receipt_path = args.receipt if args.receipt.is_absolute() else PROJECT_ROOT / args.receipt
        if receipt_path.exists():
            raise RuntimeError(f"append-only receipt already exists: {receipt_path}")
        target, receipt = assemble(args.version)
        write_json(receipt_path, receipt)
        print(json.dumps({
            "releaseRoot": str(target),
            "receiptPath": str(receipt_path),
            "receiptSha256": sha256_file(receipt_path),
            "inventory": receipt["inventory"],
        }, ensure_ascii=False))
        return 0
    except Exception as error:
        print(f"PRESEAL_ASSEMBLY_FAILED {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
