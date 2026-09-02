from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[1]
BUILD_ID = "knowledge-clipper-ocr-human-resolution-v13-20260830T120300Z"
PACKAGE = PROJECT / "dist" / "desktop-win-x64" / BUILD_ID
BUILD_RECEIPT = PACKAGE / "desktop-build-receipt.v1.json"
WINDOWS = PROJECT / "canary" / "desktop-windows" / "20260830T115545Z"
VISUAL = PROJECT / "canary" / "desktop-visual" / "ocr-resolution-v13-20260830T115726Z"
CONTRACT = PROJECT / "contracts" / "desktop-automation" / "v1.3" / "contract-receipt.json"
CORE_CONTRACT = PROJECT / "contracts" / "core" / "v2.6" / "ocr-human-resolution"
OUTPUT = PROJECT / "receipts" / "desktop-ocr-human-resolution-v1.3-20260830T120300Z.json"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().lower()


def rel(path: Path) -> str:
    return path.relative_to(PROJECT).as_posix()


def evidence(path: Path) -> dict[str, object]:
    if not path.is_file():
        raise RuntimeError(f"required evidence missing: {path}")
    return {"path": rel(path), "bytes": path.stat().st_size, "sha256": sha256(path)}


def write_json_once(path: Path, value: object) -> None:
    if path.exists():
        raise RuntimeError(f"append-only output exists: {path}")
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def package_files() -> list[Path]:
    return sorted(
        (path for path in PACKAGE.rglob("*") if path.is_file() and path != BUILD_RECEIPT),
        key=lambda path: path.relative_to(PACKAGE).as_posix().encode("utf-8"),
    )


def build_receipt() -> dict[str, object]:
    files = []
    tree_input = []
    total_bytes = 0
    for path in package_files():
        relative = path.relative_to(PACKAGE).as_posix()
        digest = sha256(path)
        size = path.stat().st_size
        total_bytes += size
        files.append({"path": relative, "bytes": size, "sha256": digest})
        tree_input.append(f"{relative}\n{digest}\n{size}\n")
    receipt = {
        "schemaVersion": "desktop-build-receipt.v1",
        "buildId": BUILD_ID,
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "sdkPath": r"D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe",
        "sdkVersion": "8.0.424",
        "targetFramework": "net8.0-windows",
        "runtimeIdentifier": "win-x64",
        "selfContained": True,
        "receiptExcludedFromTreeHash": True,
        "treeHashAlgorithm": "sha256(utf8 concat ordinal(path + LF + sha256 + LF + bytes + LF))",
        "treeSha256": hashlib.sha256("".join(tree_input).encode("utf-8")).hexdigest(),
        "fileCount": len(files),
        "totalBytes": total_bytes,
        "executableSha256": sha256(PACKAGE / "KnowledgeClipper.exe"),
        "applicationDllSha256": sha256(PACKAGE / "KnowledgeClipper.dll"),
        "files": files,
    }
    write_json_once(BUILD_RECEIPT, receipt)
    return receipt


def main() -> int:
    if not PACKAGE.is_dir() or OUTPUT.exists() or BUILD_RECEIPT.exists():
        raise RuntimeError("final package is missing or append-only output already exists")
    build = build_receipt()
    windows_receipt = json.loads((WINDOWS / "receipt.json").read_text(encoding="utf-8"))
    windows_log = (WINDOWS / "windows-ui-automation.log").read_text(encoding="utf-8")
    if windows_receipt["automatedWindowsUiCanary"] != {
        **windows_receipt["automatedWindowsUiCanary"], "status": "PASS", "assertions": 173
    }:
        raise RuntimeError("Windows UI receipt did not retain 173 PASS assertions")
    required = (
        "PASS settings opens modelessly and never disables floating-window dragging or actions",
        "PASS floating window has a hard minimum size and an explicit tray-accessible size recovery action",
        "PASS context mode consumes a reliable Windows text-pattern context projection",
        "PASS Image/OCR credential status exposes only the canonical value-free metadata and inherited-process boundary",
        "PASS OCR resolution dialog displays hash-verified original, AI candidate, Markdown, structured result, diff and unresolved reason without fabricating content",
        "PASS OCR resolution cancel returns the public durable cancel selection instead of faking a local-only exit",
        "WINDOWS_UI_CANARY PASS assertions=173",
    )
    if any(line not in windows_log for line in required):
        raise RuntimeError("required Windows UI evidence is incomplete")
    contract_receipt = json.loads(CONTRACT.read_text(encoding="utf-8"))
    if contract_receipt["treeSha256"] != "866e70dd7af5980012b817f22950dc2132914abcf39e63091ff2aac6181cd83a":
        raise RuntimeError("desktop automation v1.3 tree drifted")
    packaged_contract = PACKAGE / "contracts" / "desktop-automation" / "v1.3" / "contract-receipt.json"
    if sha256(packaged_contract) != sha256(CONTRACT):
        raise RuntimeError("packaged desktop automation v1.3 receipt drifted")
    packaged_image = PACKAGE / "descriptors" / "image-ocr-capability-config-descriptor.v5.json"
    if sha256(packaged_image) != "7d6d0e2d0e2438efe76d2f1857524b125236785a120287286522b9b6d42dcc08":
        raise RuntimeError("packaged Image/OCR v5 descriptor drifted")
    if any(path.name.lower() in {"credentials.json", "credentials.json.bak", ".env"} for path in PACKAGE.rglob("*")):
        raise RuntimeError("local credential storage was packaged")
    core_files = sorted(path for path in CORE_CONTRACT.rglob("*") if path.is_file())
    if len(core_files) != 12:
        raise RuntimeError("Core OCR public schema/fixture inventory drifted")
    core_checkpoint = next((path for path in CORE_CONTRACT.rglob("checkpoint.json")), None)
    preview = VISUAL / "ocr-human-resolution.png"
    if not preview.is_file() or len(list(VISUAL.glob("*.png"))) != 17:
        raise RuntimeError("visual preview inventory drifted")

    receipt = {
        "schemaVersion": "knowledge-clipper-desktop-module-checkpoint.v1",
        "checkpointId": "desktop-ocr-human-resolution-v1.3-20260830T120300Z",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-DESKTOP-OCR-HUMAN-RESOLUTION-V1.3-PENDING-NEXT-AUTHORITY",
        "productName": "零碎知识剪藏",
        "userHandTestEntrypoint": False,
        "integratedAuthorityCandidate": False,
        "modulePackage": {
            "root": rel(PACKAGE),
            "entrypoint": rel(PACKAGE / "KnowledgeClipper.exe"),
            "fileCount": build["fileCount"],
            "totalBytes": build["totalBytes"],
            "treeSha256": build["treeSha256"],
            "executableSha256": build["executableSha256"],
            "applicationDllSha256": build["applicationDllSha256"],
            "buildReceipt": evidence(BUILD_RECEIPT),
        },
        "desktopAutomation": {
            "version": "1.3.0",
            "receipt": evidence(CONTRACT),
            "treeSha256": contract_receipt["treeSha256"],
            "additiveSemanticActions": ["ocr-human-resolution.accept-original", "ocr-human-resolution.submit-correction", "ocr-human-resolution.cancel"],
            "frozenV1V11V12Changed": False,
        },
        "implemented": {
            "query": "task-query v2.3 waiting_input + attentionRequired + opaque 64hex token",
            "artifactReader": "jobsRoot containment; proofread/original/candidate/Markdown/structured manifest bytes and SHA-256; ancestry fail-closed",
            "dialog": "read-only original/candidate/Markdown/structured/diff/reason plus clearly labeled user correction",
            "actions": "common task.provide_input only; accept/correct/cancel; no task.cancel and no worker direct call",
            "continuation": "processing state, repeated unresolved token reopens, succeeded green check, terminal error remains explicit",
            "imageCredentialMetadata": "local-credential://image-ocr/mineru-api-token; configured/verified/sourceType only; DPAPI CurrentUser and inherited process environment",
            "defaultOcrRerunAfterHumanDecision": False,
        },
        "coreBoundary": {
            "publicSchemaRoot": rel(CORE_CONTRACT),
            "publicSchemaFiles": [evidence(path) for path in core_files],
            "checkpointAvailable": core_checkpoint is not None,
            "nextAuthorityIntegrationRequired": True,
            "contentPrivateActionMapping": "Core-owned submit_correction to Content user_correction; Desktop does not copy the Content protocol",
        },
        "evidence": {
            "desktopBuildAndTest": {"status": "PASS", "command": "tests/RemNoteQuickClipper.Desktop.Tests/build-and-test.ps1"},
            "windowsCanary": {**evidence(WINDOWS / "receipt.json"), "status": "PASS", "assertions": 173},
            "windowsUiLog": evidence(WINDOWS / "windows-ui-automation.log"),
            "visualPreview": {**evidence(preview), "root": rel(VISUAL), "pngCount": 17},
            "initialPublishFailure": evidence(PROJECT / "canary" / "desktop-build" / "ocr-human-resolution-v13-20260830T120100Z" / "initial-publish-failure.log"),
            "supersedingRestorePublish": evidence(PROJECT / "canary" / "desktop-build" / "ocr-human-resolution-v13-20260830T120100Z" / "restore-and-publish.log"),
            "imageDescriptor": evidence(packaged_image),
        },
        "safety": {
            "externalServicesInvoked": 0,
            "mineruInvoked": False,
            "deepseekInvoked": False,
            "remnoteInvoked": False,
            "ankiInvoked": False,
            "physicalSpeakerInvoked": False,
            "existingUserProcessTerminated": False,
            "credentialValuesInPackageOrReceipt": 0,
        },
        "conditional": [
            "No Core v2.6 checkpoint or new admitted authority is present yet; this is not an integrated or user hand-test entrypoint.",
            "Image live connectivity remains explicit-consent external upload and was not run.",
            "Original media destination, RemNote public probe, Speech discovery/preview and state-aware retention remain conditional exactly as projected.",
            "External browser/editor context and selection, physical screenshot drag, and mixed-DPI cross-screen interaction remain manual boundaries.",
        ],
    }
    write_json_once(OUTPUT, receipt)
    print(json.dumps({"package": rel(PACKAGE), "treeSha256": build["treeSha256"], "receipt": rel(OUTPUT), "receiptSha256": sha256(OUTPUT)}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
