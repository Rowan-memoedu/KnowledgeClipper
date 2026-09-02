#!/usr/bin/env python3
"""Create an append-only module receipt for Desktop contract packaging closure."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import pathlib
import subprocess
import sys
from typing import Any


def load(path: pathlib.Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha256(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def evidence(path: pathlib.Path, root: pathlib.Path) -> dict[str, Any]:
    return {
        "path": path.resolve().relative_to(root.resolve()).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha256(path),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project-root", required=True, type=pathlib.Path)
    parser.add_argument("--build-root", required=True, type=pathlib.Path)
    parser.add_argument("--canary-root", required=True, type=pathlib.Path)
    parser.add_argument("--negative-root", required=True, type=pathlib.Path)
    parser.add_argument("--receipt", required=True, type=pathlib.Path)
    args = parser.parse_args()

    project = args.project_root.resolve()
    build = args.build_root.resolve()
    canary = args.canary_root.resolve()
    negative = args.negative_root.resolve()
    receipt_path = args.receipt.resolve()
    if receipt_path.exists():
        raise SystemExit(f"append-only receipt already exists: {receipt_path}")

    build_receipt_path = build / "desktop-build-receipt.v1.json"
    closure_path = canary / "contract-closure.json"
    startup_path = canary / "startup-diagnostic.stdout.log"
    secret_path = canary / "secret-scan.json"
    tests_path = canary / "build-and-test.log"
    required = [build_receipt_path, closure_path, startup_path, secret_path, tests_path]
    for path in required:
        if not path.is_file():
            raise AssertionError(f"required evidence missing: {path}")

    build_receipt = load(build_receipt_path)
    closure = load(closure_path)
    startup = load(startup_path)
    secret_scan = load(secret_path)
    test_log = tests_path.read_text(encoding="utf-8-sig")
    assert closure["status"] == "passed" and closure["failures"] == []
    assert [item["version"] for item in closure["versions"]] == ["v1", "v1.1", "v1.2", "v1.3"]
    assert closure["commonCheckpoint"]["filesChecked"] == 21
    assert closure["schemasChecked"] == 25
    assert closure["validFixturesAccepted"] == 53 and closure["invalidFixturesRejected"] == 14
    assert startup["result"] == "PASS"
    assert startup["selfContainedHost"] is True
    assert startup["packageReceiptParsed"] is True
    assert startup["packageFilesVerified"] is True and startup["packageTreeVerified"] is True
    assert startup["buildReceiptSha256"].upper() == sha256(build_receipt_path)
    assert startup["externalConnections"] == 0
    assert startup["namedPipeInvoked"] is False
    assert startup["remNoteInvoked"] is False and startup["mineruInvoked"] is False
    assert secret_scan["status"] == "passed" and secret_scan["findingCount"] == 0
    assert "PASS desktop automation build and tests" in test_log

    validator = project / "tools" / "validate_desktop_automation_release_closure.py"
    negative_run = subprocess.run(
        [sys.executable, str(validator), "--release-root", str(negative)],
        cwd=project,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=60,
        check=False,
    )
    assert negative_run.returncode == 1, f"negative closure gate returned {negative_run.returncode}"
    negative_result = json.loads(negative_run.stdout)
    assert negative_result["status"] == "failed"
    assert any("common checkpoint file missing" in failure for failure in negative_result["failures"])

    packaged_contract_files = [
        item
        for item in build_receipt["files"]
        if item["path"].startswith("contracts/desktop-automation/")
        or item["path"].startswith("contracts/v1/")
        or item["path"] == "receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json"
    ]
    assert len(packaged_contract_files) == 100, len(packaged_contract_files)

    result = {
        "schemaVersion": "knowledge-clipper-desktop-automation-packaging-closure-receipt.v1",
        "createdAt": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "PASS-DESKTOP-AUTOMATION-PACKAGING-CLOSURE",
        "scope": "desktop-module-only; packaging and offline validation; no UI or business behavior changes",
        "build": {
            "buildId": build_receipt["buildId"],
            "root": build.relative_to(project).as_posix(),
            "fileCount": build_receipt["fileCount"],
            "totalBytes": build_receipt["totalBytes"],
            "treeSha256": build_receipt["treeSha256"].upper(),
            "executableSha256": build_receipt["executableSha256"].upper(),
            "applicationDllSha256": build_receipt["applicationDllSha256"].upper(),
            "receipt": evidence(build_receipt_path, project),
            "selfContainedWinX64": True,
        },
        "contractClosure": {
            "packagedFiles": len(packaged_contract_files),
            "frozenVersions": ["v1", "v1.1", "v1.2", "v1.3"],
            "commonCheckpointFiles": 21,
            "recursiveSchemaReferences": "release-root-contained",
            "schemasChecked": closure["schemasChecked"],
            "validFixturesAccepted": closure["validFixturesAccepted"],
            "invalidFixturesRejected": closure["invalidFixturesRejected"],
            "evidence": evidence(closure_path, project),
        },
        "negativeGate": {
            "missingCommonDependencyRejected": True,
            "expectedExitCode": 1,
            "actualExitCode": negative_run.returncode,
            "failure": negative_result["failures"][0],
            "fixtureRoot": negative.relative_to(project).as_posix(),
        },
        "regression": {
            "desktopBuildAndTest": evidence(tests_path, project),
            "desktopBuildAndTestStatus": "passed",
            "startupDiagnostic": evidence(startup_path, project),
            "startupStatus": "passed-no-side-effects",
            "secretScan": evidence(secret_path, project),
            "secretFindings": 0,
        },
        "changeBoundary": {
            "modifiedProductionFiles": ["desktop/RemNoteQuickClipper.Desktop.csproj"],
            "addedVerificationFiles": [
                "tools/validate_desktop_automation_release_closure.py",
                "tools/finalize_desktop_automation_packaging_closure.py",
            ],
            "modifiedUiOrBusinessSourceFiles": [],
            "fullDesktopRegressionReplayed": True,
        },
        "safety": {
            "externalServicesInvoked": 0,
            "orchestratorInvoked": False,
            "namedPipeInvoked": False,
            "remNoteInvoked": False,
            "ankiInvoked": False,
            "mineruInvoked": False,
            "deepSeekInvoked": False,
            "physicalSpeakerInvoked": False,
            "existingUserProcessTerminated": False,
        },
        "integration": {
            "integratedCandidateCreated": False,
            "reason": "waiting for final monotonic authority; do not bind a superseded authority",
            "nextAction": "append-only equality rebase using this receipt-bound Desktop package",
        },
    }
    receipt_path.parent.mkdir(parents=True, exist_ok=True)
    with receipt_path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    print(json.dumps({"receipt": str(receipt_path), "sha256": sha256(receipt_path), "status": result["status"]}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
