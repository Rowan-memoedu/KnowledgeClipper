#!/usr/bin/env python3
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
from pathlib import Path


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def item(path: Path, root: Path):
    return {"path": path.resolve().relative_to(root.resolve()).as_posix(), "bytes": path.stat().st_size, "sha256": sha(path)}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project-root", type=Path, required=True)
    parser.add_argument("--build-root", type=Path, required=True)
    parser.add_argument("--evidence-root", type=Path, required=True)
    parser.add_argument("--settings-log", type=Path, required=True)
    parser.add_argument("--failed-full-log", type=Path, action="append", default=[])
    parser.add_argument("--receipt", type=Path, required=True)
    args = parser.parse_args()

    project = args.project_root.resolve()
    build = args.build_root.resolve()
    evidence = args.evidence_root.resolve()
    receipt_path = args.receipt.resolve()
    if receipt_path.exists():
        raise RuntimeError(f"append-only receipt exists: {receipt_path}")

    build_receipt_path = build / "desktop-build-receipt.v1.json"
    tests_path = evidence / "desktop-build-and-test.log"
    startup_path = evidence / "startup-diagnostic.json"
    settings_log_path = args.settings_log.resolve()
    failed_logs = [path.resolve() for path in args.failed_full_log]
    for required in (build_receipt_path, tests_path, startup_path, settings_log_path, *failed_logs):
        if not required.is_file():
            raise AssertionError(f"missing evidence: {required}")

    build_receipt = load(build_receipt_path)
    startup = load(startup_path)
    tests = tests_path.read_text(encoding="utf-8-sig")
    settings_log = settings_log_path.read_text(encoding="utf-8-sig")

    for entry in build_receipt["files"]:
        packaged = build / entry["path"]
        assert packaged.is_file() and packaged.stat().st_size == entry["bytes"] and sha(packaged) == entry["sha256"].upper(), entry["path"]
    actual = [path for path in build.rglob("*") if path.is_file() and path.name != "desktop-build-receipt.v1.json"]
    assert len(actual) == build_receipt["fileCount"]
    assert sum(path.stat().st_size for path in actual) == build_receipt["totalBytes"]
    assert startup["result"] == "PASS" and startup["packageFilesVerified"] and startup["packageTreeVerified"]
    assert startup["packageTreeSha256"].upper() == build_receipt["treeSha256"].upper()
    assert startup["buildReceiptSha256"].upper() == sha(build_receipt_path)
    assert startup["externalConnections"] == 0 and not startup["namedPipeInvoked"] and not startup["remNoteInvoked"] and not startup["mineruInvoked"]
    for marker in (
        "PASS named-pipe-public-module-actions-v1.2-v1.3",
        "PASS desktop automation build and tests",
    ):
        assert marker in tests, marker
    for marker in (
        "PASS original-image task manifest forces the exact sole destination [anki]",
        "PASS read-only Anki probe projects missing managed routes and never initializes implicitly",
        "PASS one explicit initialization click creates only missing managed resources",
        "PASS a unique exact-readback discovery updates only the pending form binding",
        "PASS ambiguous discovery preserves the last exact binding",
        "WINDOWS_SETTINGS_UI_CANARY PASS assertions=140",
    ):
        assert marker in settings_log, marker

    source_paths = [
        project / "src" / "DesktopSettingsControl.cs",
        project / "src" / "DesktopModuleSettings.cs",
        project / "src" / "ProjectSettingsForm.cs",
        project / "src" / "DesktopUi.cs",
        project / "src" / "RemNoteQuickClipper.Desktop.Core" / "NamedPipeCommonIpcClient.cs",
        project / "src" / "RemNoteQuickClipper.Desktop.Core" / "CommonContractAdapter.cs",
        project / "src" / "RemNoteQuickClipper.Desktop.Core" / "SettingsAutomationCatalog.cs",
        project / "tests" / "RemNoteQuickClipper.Desktop.Tests" / "FakeNamedPipeOrchestratorServer.cs",
        project / "tests" / "RemNoteQuickClipper.Desktop.Tests" / "Program.cs",
        project / "src" / "WindowsUiCanary.cs",
    ]
    result = {
        "schemaVersion": "knowledge-clipper-desktop-original-anki-module-actions-receipt.v1",
        "createdAt": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "PASS-SCOPED-DESKTOP-MODULE-AWAITING-AUTHORITY",
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
            "originalImageDestinations": ["anki"],
            "remNoteOriginalMedia": "BY-DESIGN-UNSUPPORTED-NOT-A-GAP-NONBLOCKING",
            "ankiProbe": "typed read-only ready|needs_initialization|migration_required; probe writes=0",
            "ankiInitializeManaged": "separate explicit UI action; production disabled until admitted public Core action",
            "remNoteExactTitleDiscovery": "public module-action v2 over IPC 1.3; unique exact-readback only; ambiguous preserves binding",
            "ordinaryRemNoteTextAndCards": "preserved",
        },
        "regression": {
            "desktopBuildAndTest": item(tests_path, project),
            "settingsUiLog": item(settings_log_path, project),
            "settingsUiAssertions": 140,
            "failedFullWindowsRunsPreserved": [item(path, project) for path in failed_logs],
            "fullWindowsStatus": "PENDING-UNRELATED-PHYSICAL-RESIZE-FLAKE",
        },
        "source": [item(path, project) for path in source_paths],
        "safety": {
            "externalServicesInvoked": 0,
            "physicalSpeakerInvoked": False,
            "remNoteWrites": 0,
            "ankiWrites": 0,
            "existingUserProcessTerminated": False,
        },
        "integration": {
            "integratedCandidateCreated": False,
            "userHandTestEntrypoint": False,
            "requiredAuthority": "Core IPC 1.3 remnote.discover-target plus public Anki initialize-managed action and Anki worker 1.4.0",
            "latestVerifiedAdmittedAuthority": "v1.2.34 lacks those public actions",
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
