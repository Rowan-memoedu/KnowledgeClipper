#!/usr/bin/env python3
"""Seal the append-only Desktop speech settings module package and evidence."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
from pathlib import Path
from typing import Any


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def load(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def item(path: Path, root: Path) -> dict[str, Any]:
    return {"path": path.resolve().relative_to(root.resolve()).as_posix(), "bytes": path.stat().st_size, "sha256": sha(path)}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project-root", type=Path, required=True)
    parser.add_argument("--build-root", type=Path, required=True)
    parser.add_argument("--evidence-root", type=Path, required=True)
    parser.add_argument("--windows-receipt", type=Path, required=True)
    parser.add_argument("--failed-ui-log", type=Path, required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    args = parser.parse_args()

    project = args.project_root.resolve()
    build = args.build_root.resolve()
    evidence = args.evidence_root.resolve()
    windows_receipt_path = args.windows_receipt.resolve()
    receipt_path = args.receipt.resolve()
    if receipt_path.exists():
        raise SystemExit(f"append-only receipt already exists: {receipt_path}")

    build_receipt_path = build / "desktop-build-receipt.v1.json"
    startup_path = evidence / "startup-diagnostic.json"
    tests_path = evidence / "desktop-build-and-test.log"
    ui_log_path = windows_receipt_path.parent / "windows-ui-automation.log"
    for required in (build_receipt_path, startup_path, tests_path, windows_receipt_path, ui_log_path, args.failed_ui_log.resolve()):
        if not required.is_file():
            raise AssertionError(f"missing evidence: {required}")

    build_receipt = load(build_receipt_path)
    startup = load(startup_path)
    windows_receipt = load(windows_receipt_path)
    tests = tests_path.read_text(encoding="utf-8-sig")
    ui_log = ui_log_path.read_text(encoding="utf-8-sig")

    for entry in build_receipt["files"]:
        packaged = build / Path(entry["path"])
        assert packaged.is_file() and packaged.stat().st_size == entry["bytes"] and sha(packaged) == entry["sha256"].upper(), entry["path"]
    actual = [path for path in build.rglob("*") if path.is_file() and path.name != "desktop-build-receipt.v1.json"]
    assert len(actual) == build_receipt["fileCount"]
    assert sum(path.stat().st_size for path in actual) == build_receipt["totalBytes"]
    assert startup["result"] == "PASS" and startup["packageFilesVerified"] and startup["packageTreeVerified"]
    assert startup["externalConnections"] == 0 and not startup["namedPipeInvoked"] and not startup["remNoteInvoked"] and not startup["mineruInvoked"]
    assert startup["buildReceiptSha256"].upper() == sha(build_receipt_path)
    assert windows_receipt["automatedWindowsUiCanary"]["status"] == "PASS" and windows_receipt["automatedWindowsUiCanary"]["assertions"] == 193
    assert windows_receipt["externalTargetsAndProviders"]["physicalSpeakerInvoked"] is False
    for marker in (
        "PASS voice discovery caches one revision/hash-bound catalog without implicit audio preview",
        "PASS one explicit preview button click dispatches exactly one physical-audio module action",
        "PASS settings reopen verifies and restores the persisted voice catalog snapshot revision and SHA without provider access",
        "PASS settings save persists next-task values without implicit voice discovery or physical playback",
        "PASS voice discovery, preview, and RemNote probe use only public module-action IPC and create zero knowledge task commands",
        "WINDOWS_UI_CANARY PASS assertions=193",
    ):
        assert marker in ui_log, marker
    assert "PASS named-pipe-core-v2.7-public-module-actions" in tests and "PASS desktop automation build and tests" in tests

    forbidden_state_files = [
        entry["path"] for entry in build_receipt["files"]
        if "credential" in entry["path"].lower()
        or "module-actions/" in entry["path"].lower()
        or "desktop-speech-catalog-cache" in entry["path"].lower()
    ]
    assert forbidden_state_files == [], forbidden_state_files

    source_paths = [
        project / "src" / "DesktopModuleSettings.cs",
        project / "src" / "ProjectSettingsForm.cs",
        project / "src" / "WindowsUiCanary.cs",
    ]
    result = {
        "schemaVersion": "knowledge-clipper-desktop-speech-settings-actions-receipt.v1",
        "createdAt": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "PASS-DESKTOP-SPEECH-SETTINGS-ACTIONS-MODULE",
        "build": {
            "root": build.relative_to(project).as_posix(),
            "entrypoint": (build / "KnowledgeClipper.exe").relative_to(project).as_posix(),
            "fileCount": build_receipt["fileCount"],
            "totalBytes": build_receipt["totalBytes"],
            "treeSha256": build_receipt["treeSha256"].upper(),
            "executableSha256": build_receipt["executableSha256"].upper(),
            "applicationDllSha256": build_receipt["applicationDllSha256"].upper(),
            "buildReceipt": item(build_receipt_path, project),
            "selfContainedWinX64": True,
        },
        "behavior": {
            "voiceDiscovery": "public Core v2.7 speech.voices only",
            "catalogCache": "local non-secret cache; Core artifact ref plus snapshot SHA-256 and catalog revision/hash; verified on reopen; corrupt cache rejected",
            "previewAutomationId": "knowledge-clipper.desktop.settings.speech.preview",
            "preview": "public Core v2.7 speech.preview; explicitSideEffectAuthorization=true; exactly one action per click",
            "saveAndStatus": "zero discovery and zero preview actions",
            "knowledgeTasksCreatedBySettingsActions": 0,
            "voiceChoiceTypePreserved": True,
        },
        "regression": {
            "desktopBuildAndTest": item(tests_path, project),
            "windowsUiCanary": item(windows_receipt_path, project),
            "windowsUiLog": item(ui_log_path, project),
            "windowsUiAssertions": 193,
            "preservedInitialFailure": item(args.failed_ui_log.resolve(), project),
            "initialFailureSupersededBy": windows_receipt_path.parent.name,
        },
        "source": [item(path, project) for path in source_paths],
        "safety": {
            "externalServicesInvoked": 0,
            "physicalSpeakerInvoked": False,
            "knowledgeTaskCommandsSent": 0,
            "credentialOrRuntimeStateFilesPackaged": 0,
            "existingUserProcessTerminated": False,
        },
        "integration": {
            "integratedCandidateCreated": False,
            "userHandTestEntrypoint": False,
            "sealedV1233Modified": False,
            "nextAction": "append-only rebase after the current v1.2.33 user hand-test and a new admitted Core authority",
        },
    }
    receipt_path.parent.mkdir(parents=True, exist_ok=True)
    with receipt_path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    print(json.dumps({"receipt": str(receipt_path), "sha256": sha(receipt_path), "status": result["status"]}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
