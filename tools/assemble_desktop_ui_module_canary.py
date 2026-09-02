from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


PROJECT_ROOT = Path(__file__).resolve().parents[1]


class UiModuleCanaryFailure(RuntimeError):
    pass


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def inventory(root: Path) -> dict[str, Any]:
    records: list[dict[str, Any]] = []
    lines: list[str] = []
    total_bytes = 0
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        size = path.stat().st_size
        digest = sha256_file(path)
        records.append({"path": relative, "bytes": size, "sha256": digest})
        lines.append(f"{relative}\n{digest.lower()}\n{size}\n")
        total_bytes += size
    return {
        "files": records,
        "fileCount": len(records),
        "totalBytes": total_bytes,
        "treeSha256": hashlib.sha256("".join(lines).encode("utf-8")).hexdigest().upper(),
    }


def inventory_from_build_receipt(source_root: Path, build_receipt: dict[str, Any]) -> dict[str, Any]:
    records = build_receipt.get("files", [])
    lines: list[str] = []
    total_bytes = 0
    seen: set[str] = set()
    for item in sorted(records, key=lambda value: value.get("path", "")):
        relative = item.get("path", "")
        path = (source_root / relative).resolve()
        if (
            not relative
            or relative in seen
            or not path.is_relative_to(source_root.resolve())
            or not path.is_file()
            or path.stat().st_size != item.get("bytes")
            or sha256_file(path) != item.get("sha256", "").upper()
        ):
            raise UiModuleCanaryFailure(f"desktop build inventory mismatch: {relative}")
        seen.add(relative)
        total_bytes += item["bytes"]
        lines.append(f"{relative}\n{item['sha256'].lower()}\n{item['bytes']}\n")
    return {
        "files": records,
        "fileCount": len(records),
        "totalBytes": total_bytes,
        "treeSha256": hashlib.sha256("".join(lines).encode("utf-8")).hexdigest().upper(),
    }


def verify_source(source_root: Path, module_receipt_path: Path, expected_receipt_sha256: str) -> dict[str, Any]:
    if not source_root.is_dir():
        raise UiModuleCanaryFailure(f"desktop source root is missing: {source_root}")
    if not module_receipt_path.is_file() or sha256_file(module_receipt_path) != expected_receipt_sha256.upper():
        raise UiModuleCanaryFailure("desktop module receipt is missing or hash-mismatched")
    module_receipt = load_json(module_receipt_path)
    package = module_receipt.get("package", {})
    expected_root = (PROJECT_ROOT / Path(module_receipt.get("startEntrypoint", ""))).resolve().parent
    if expected_root != source_root.resolve():
        raise UiModuleCanaryFailure("desktop source root does not match the module receipt")
    build_receipt_path = PROJECT_ROOT / package.get("buildReceiptPath", "")
    if not build_receipt_path.is_file() or sha256_file(build_receipt_path) != package.get("buildReceiptSha256", "").upper():
        raise UiModuleCanaryFailure("desktop build receipt is missing or hash-mismatched")
    build_receipt = load_json(build_receipt_path)
    source_inventory = inventory_from_build_receipt(source_root, build_receipt)
    authorized = {item["path"] for item in source_inventory["files"]}
    control_files = {build_receipt_path.resolve()}
    excluded = sorted(
        path.relative_to(source_root).as_posix()
        for path in source_root.rglob("*")
        if path.is_file() and path.relative_to(source_root).as_posix() not in authorized and path.resolve() not in control_files
    )
    if (
        module_receipt.get("schemaVersion") != "desktop-ui-clickable-canary.v1"
        or module_receipt.get("productName") != "零碎知识剪藏"
        or Path(module_receipt.get("startEntrypoint", "")).name != "KnowledgeClipper.exe"
        or package.get("selfContained") is not True
        or package.get("runtimeIdentifier") != "win-x64"
        or source_inventory["fileCount"] != 243
        or source_inventory["fileCount"] != package.get("fileCount")
        or source_inventory["totalBytes"] != package.get("totalBytes")
        or source_inventory["treeSha256"] != package.get("treeSha256", "").upper()
        or source_inventory["treeSha256"] != build_receipt.get("treeSha256", "").upper()
        or module_receipt.get("automatedEvidence", {}).get("windowsUiCanary", {}).get("assertions") != 72
        or module_receipt.get("automatedEvidence", {}).get("startupDiagnostic", {}).get("externalConnections") != 0
        or module_receipt.get("realWindowsBoundaries", {}).get("desktopToRealRemNoteE2E") != "NOT_RUN"
    ):
        raise UiModuleCanaryFailure("desktop module receipt does not satisfy the UI-only source gate")
    return {
        "moduleReceipt": module_receipt,
        "buildReceiptPath": build_receipt_path,
        "sourceInventory": source_inventory,
        "excludedSourceFiles": excluded,
    }


def parse_one_json(stdout: str) -> dict[str, Any]:
    try:
        document = json.loads(stdout)
    except json.JSONDecodeError as exc:
        raise UiModuleCanaryFailure("startup diagnostic did not return one JSON document") from exc
    if not isinstance(document, dict):
        raise UiModuleCanaryFailure("startup diagnostic result is not an object")
    return document


def assemble(
    source_root: Path,
    release_root: Path,
    module_receipt_path: Path,
    expected_receipt_sha256: str,
) -> tuple[Path, Path]:
    project = PROJECT_ROOT.resolve()
    release_root = release_root.resolve()
    if not release_root.is_relative_to((project / "releases").resolve()):
        raise UiModuleCanaryFailure("UI module release root must be below the project releases directory")
    if release_root.exists():
        raise UiModuleCanaryFailure("UI module release root already exists; append-only assembly refuses overwrite")
    verified = verify_source(source_root.resolve(), module_receipt_path.resolve(), expected_receipt_sha256)
    desktop_root = release_root / "desktop"
    desktop_root.parent.mkdir(parents=True, exist_ok=False)
    desktop_root.mkdir()
    for item in verified["sourceInventory"]["files"]:
        source = source_root / item["path"]
        destination = desktop_root / item["path"]
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)
    shutil.copy2(verified["buildReceiptPath"], desktop_root / "desktop-build-receipt.v1.json")
    copied_payload = inventory_from_build_receipt(desktop_root, load_json(desktop_root / "desktop-build-receipt.v1.json"))
    if copied_payload != verified["sourceInventory"]:
        raise UiModuleCanaryFailure("copied desktop payload does not exactly match the verified build inventory")
    copied_before = inventory(desktop_root)

    executable = desktop_root / "KnowledgeClipper.exe"
    completed = subprocess.run(
        [str(executable), "--startup-diagnostic"],
        cwd=desktop_root,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=30,
        check=False,
    )
    if completed.returncode != 0:
        raise UiModuleCanaryFailure(f"copied KnowledgeClipper startup diagnostic failed: {completed.stderr[:500]}")
    diagnostic = parse_one_json(completed.stdout)
    copied_after = inventory(desktop_root)
    if copied_after != copied_before:
        raise UiModuleCanaryFailure("startup diagnostic changed the copied desktop directory")
    if (
        diagnostic.get("result") != "PASS"
        or diagnostic.get("selfContainedHost") is not True
        or diagnostic.get("packageFilesVerified") is not True
        or diagnostic.get("packageTreeVerified") is not True
        or diagnostic.get("productionWorkDirectoryCreated") is not False
        or diagnostic.get("externalConnections") != 0
        or diagnostic.get("namedPipeInvoked") is not False
        or diagnostic.get("remNoteInvoked") is not False
        or diagnostic.get("mineruInvoked") is not False
    ):
        raise UiModuleCanaryFailure("startup diagnostic violates the UI-only zero-side-effect boundary")

    startup_receipt_path = release_root / "startup-diagnostic.receipt.json"
    startup_receipt = {
        "schemaVersion": "knowledge-clipper.desktop-ui-startup-diagnostic/1.0.0",
        "createdAt": utc_now(),
        "status": "passed",
        "scope": "desktop-ui-module-only",
        "entrypoint": str(executable),
        "entrypointSha256": sha256_file(executable),
        "exitCode": completed.returncode,
        "diagnostic": diagnostic,
        "desktopTreeBeforeSha256": copied_before["treeSha256"],
        "desktopTreeAfterSha256": copied_after["treeSha256"],
        "hiddenWrites": 0,
        "externalProductionApisInvoked": False,
    }
    startup_receipt_path.write_text(json.dumps(startup_receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    readme_path = release_root / "README-UI-ONLY.txt"
    readme_path.write_text(
        "零碎知识剪藏 - 桌面 UI 模块试用包\n\n"
        "启动：双击 desktop\\KnowledgeClipper.exe。\n"
        "请保留整个目录，不能只复制 EXE。\n\n"
        "本包只用于检查窗口能否打开、界面布局和本地桌面交互。\n"
        "它不是完整 feature candidate，不证明 production Core、worker、RemNote/Anki 导出或端到端链路。\n",
        encoding="utf-8",
    )
    handoff_path = release_root / "ui-module-handoff.receipt.json"
    module_receipt = verified["moduleReceipt"]
    handoff = {
        "schemaVersion": "knowledge-clipper.desktop-ui-module-handoff/1.0.0",
        "createdAt": utc_now(),
        "status": "passed-ui-module-only-full-feature-no-go",
        "productId": "KnowledgeClipper",
        "productDisplayName": "零碎知识剪藏",
        "releaseRoot": str(release_root),
        "entrypoint": str(executable),
        "entrypointSha256": sha256_file(executable),
        "desktop": {
            "selfContained": True,
            "runtimeIdentifier": "win-x64",
            "fileCountIncludingBuildReceipt": copied_after["fileCount"],
            "payloadFileCount": copied_payload["fileCount"],
            "totalBytes": copied_after["totalBytes"],
            "treeSha256": copied_after["treeSha256"],
            "payloadTreeSha256": copied_payload["treeSha256"],
        },
        "sourceEvidence": {
            "moduleReceiptPath": str(module_receipt_path.resolve()),
            "moduleReceiptSha256": expected_receipt_sha256.upper(),
            "buildReceiptSha256": module_receipt["package"]["buildReceiptSha256"].upper(),
            "windowsUiAssertions": 72,
            "excludedUnreceiptedSourceFiles": len(verified["excludedSourceFiles"]),
            "excludedSourcePathListSha256": hashlib.sha256(
                "\n".join(verified["excludedSourceFiles"]).encode("utf-8")
            ).hexdigest().upper(),
        },
        "startupDiagnosticReceipt": {
            "path": str(startup_receipt_path),
            "sha256": sha256_file(startup_receipt_path),
        },
        "scope": {
            "uiWindowAndLocalInteractionTrial": True,
            "productionCoreE2E": False,
            "workerChainE2E": False,
            "exportE2E": False,
            "candidateBound": False,
            "releaseManifest": None,
            "candidateHash": None,
            "signed": False,
        },
        "remainingBoundaries": [
            "Core v2.2 final completion/production integration receipt",
            "neutral authority registry descriptor refresh",
            "candidate-bound five-worker offline chain",
            "physical external-application and cross-monitor DPI user experience",
            "desktop-to-real-export E2E",
        ],
        "hiddenWrites": 0,
        "externalProductionApisInvoked": False,
    }
    handoff_path.write_text(json.dumps(handoff, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return handoff_path, startup_receipt_path


def main() -> int:
    parser = argparse.ArgumentParser(description="Assemble an append-only KnowledgeClipper desktop UI module Canary")
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--release-root", type=Path, required=True)
    parser.add_argument("--module-receipt", type=Path, required=True)
    parser.add_argument("--module-receipt-sha256", required=True)
    args = parser.parse_args()
    try:
        handoff, startup = assemble(args.source_root, args.release_root, args.module_receipt, args.module_receipt_sha256)
        print(json.dumps({"status": "passed", "handoffReceipt": str(handoff), "startupReceipt": str(startup)}, ensure_ascii=False))
        return 0
    except (UiModuleCanaryFailure, OSError, subprocess.TimeoutExpired) as exc:
        print(json.dumps({"status": "failed", "error": str(exc)}, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
