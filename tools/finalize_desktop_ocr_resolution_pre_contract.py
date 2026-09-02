from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[1]
BUILD_ID = "knowledge-clipper-ocr-resolution-settings-conditional-20260830T112634Z"
CANARY_ID = "ocr-resolution-settings-conditional-20260830T112634Z"
OUTPUT = PROJECT / "receipts" / "desktop-ocr-resolution-settings-conditional-20260830T112634Z.json"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().lower()


def relative(path: Path) -> str:
    return path.relative_to(PROJECT).as_posix()


def evidence(path: Path) -> dict[str, object]:
    if not path.is_file():
        raise RuntimeError(f"required evidence is missing: {path}")
    return {"path": relative(path), "bytes": path.stat().st_size, "sha256": sha256(path)}


def main() -> int:
    if OUTPUT.exists():
        raise RuntimeError(f"append-only receipt exists: {OUTPUT}")

    package = PROJECT / "dist" / "desktop-win-x64" / BUILD_ID
    build_receipt_path = package / "desktop-build-receipt.v1.json"
    windows_receipt_path = PROJECT / "canary" / "desktop-windows" / CANARY_ID / "receipt.json"
    windows_log_path = windows_receipt_path.parent / "windows-ui-automation.log"
    build_receipt = json.loads(build_receipt_path.read_text(encoding="utf-8"))
    windows_receipt = json.loads(windows_receipt_path.read_text(encoding="utf-8"))

    if build_receipt["buildId"] != BUILD_ID or not build_receipt["selfContained"]:
        raise RuntimeError("desktop build receipt is not the expected self-contained module package")
    if windows_receipt["automatedWindowsUiCanary"]["status"] != "PASS":
        raise RuntimeError("Windows UI Canary did not pass")
    if windows_receipt["automatedWindowsUiCanary"]["assertions"] != 172:
        raise RuntimeError("Windows UI Canary assertion count drifted")
    windows_log = windows_log_path.read_text(encoding="utf-8")
    required_lines = (
        "PASS OCR resolution dialog is DPI-aware and stays in the owned modal layer",
        "PASS OCR resolution dialog displays original, AI candidate, diff and unresolved reason without fabricating content",
        "PASS OCR resolution edit box accepts actual keyboard input",
        "PASS OCR edited continuation returns the exact user text for later durable-command binding",
        "PASS OCR use-original continuation returns the immutable original text unchanged",
        "PASS OCR resolution cancel closes locally and produces no continuation selection",
        "PASS retention remains read-only and explicitly reports the fail-safe production disablement",
        "PASS original-media availability remains visible and fail-closed instead of pretending to be supported",
        "PASS RemNote probe is visible but remains conditional until an authority-admitted Core action exists",
        "PASS Speech voice catalog exposes snapshot support separately from conditional provider discovery",
        "PASS Speech voice discovery has a stable fail-closed action ID and never lets Desktop call the provider directly",
        "PASS Speech preview remains an explicit conditional physical-audio action",
        "WINDOWS_UI_CANARY PASS assertions=172",
    )
    for line in required_lines:
        if line not in windows_log:
            raise RuntimeError(f"required Windows UI evidence is missing: {line}")

    source_paths = [
        PROJECT / "src" / "RemNoteQuickClipper.Desktop.Core" / "SettingsAutomationCatalog.cs",
        PROJECT / "src" / "RemNoteQuickClipper.Desktop.Core" / "Ports.cs",
        PROJECT / "src" / "RemNoteQuickClipper.Desktop.Core" / "DesktopState.cs",
        PROJECT / "src" / "RemNoteQuickClipper.Desktop.Core" / "CommonContractAdapter.cs",
        PROJECT / "src" / "RemNoteQuickClipper.Desktop.Core" / "DesktopController.cs",
        PROJECT / "src" / "DesktopModuleSettings.cs",
        PROJECT / "src" / "ProjectSettingsForm.cs",
        PROJECT / "src" / "DesktopUi.cs",
        PROJECT / "src" / "WindowsUiCanary.cs",
        PROJECT / "tests" / "RemNoteQuickClipper.Desktop.Tests" / "Program.cs",
    ]

    public_contract_matches = sorted(
        relative(path)
        for path in (PROJECT / "contracts" / "core").rglob("*")
        if path.is_file() and any(token in path.name.lower() for token in ("ocr-human", "human-resolution", "ocr-resolution"))
    )
    if public_contract_matches:
        raise RuntimeError("an OCR human-resolution public contract now exists; this pre-contract receipt must not be emitted")

    receipt = {
        "schemaVersion": "knowledge-clipper-desktop-module-checkpoint.v1",
        "checkpointId": "desktop-ocr-resolution-settings-conditional-20260830T112634Z",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-UI-HOST-PENDING-CORE-DURABLE-CONTRACT",
        "productName": "零碎知识剪藏",
        "userHandTestEntrypoint": False,
        "integratedAuthorityCandidate": False,
        "modulePackage": {
            "root": relative(package),
            "entrypoint": relative(package / "KnowledgeClipper.exe"),
            "fileCount": build_receipt["fileCount"],
            "totalBytes": build_receipt["totalBytes"],
            "treeSha256": build_receipt["treeSha256"],
            "executableSha256": build_receipt["executableSha256"],
            "applicationDllSha256": build_receipt["applicationDllSha256"],
            "buildReceipt": evidence(build_receipt_path),
        },
        "implemented": {
            "attentionRequiredInternalProjection": True,
            "frozenDesktopSemanticContractsChanged": False,
            "ocrResolutionUiHost": {
                "autoShowBound": False,
                "durableCommandBound": False,
                "fields": ["originalText", "candidateText", "difference", "reason", "editableCorrection"],
                "localResults": ["use-original", "use-edited", "cancel"],
                "stableNeutralAutomationIds": True,
                "keyboardDpiOwnerLayerValidated": True,
            },
            "settingsAvailability": {
                "originalMediaDestination": "terminal-unsupported-read-only",
                "remnoteReadOnlyProbe": "conditional-disabled-no-core-action",
                "speechVoiceCatalog": "snapshot-supported-provider-discovery-conditional",
                "speechVoiceDiscovery": "conditional-disabled-stable-action-id-no-desktop-provider-call",
                "speechPreview": "conditional-explicit-only-physical-audio-not-run",
                "retention": "unsupported-read-only-production-legacy-deleter-disabled",
            },
        },
        "coreBoundary": {
            "publicOcrHumanResolutionContractDetected": False,
            "publicContractMatches": public_contract_matches,
            "availableProjection": "task waiting_input plus attentionRequired",
            "missingForBinding": [
                "versioned task-query projection for OCR session and verified artifact references",
                "opaque target token or equivalent Core-owned binding",
                "versioned durable action payload and task.provide_input semantics",
                "accepted/resolved continuation receipt semantics",
            ],
            "privateProtocolInvented": False,
        },
        "evidence": {
            "desktopBuildAndTest": {"status": "PASS", "command": "tests/RemNoteQuickClipper.Desktop.Tests/build-and-test.ps1"},
            "windowsCanary": {**evidence(windows_receipt_path), "status": "PASS", "assertions": 172},
            "windowsUiLog": evidence(windows_log_path),
            "sourceFiles": [evidence(path) for path in source_paths],
        },
        "safety": {
            "externalServicesInvoked": 0,
            "remnoteInvoked": False,
            "ankiInvoked": False,
            "mineruInvoked": False,
            "deepseekInvoked": False,
            "physicalSpeakerInvoked": False,
            "existingUserProcessTerminated": False,
            "legacyRetentionDeletionInvoked": False,
        },
        "remaining": [
            "Core OCR human-resolution public contract and checkpoint",
            "Desktop query/artifact verification/durable command binding",
            "fake Named Pipe accept-correct-cancel and repeated-unresolved state tests",
            "append-only rebase to the next admitted authority",
            "physical browser/editor context, external selection/focus, and mixed-DPI cross-screen checks remain manual boundaries",
        ],
    }
    temporary = OUTPUT.with_name(OUTPUT.name + ".tmp")
    temporary.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    temporary.replace(OUTPUT)
    print(json.dumps({"receipt": str(OUTPUT), "sha256": sha256(OUTPUT), "status": receipt["status"]}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
