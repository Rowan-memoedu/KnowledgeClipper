from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[1]
BASE_RECEIPT = PROJECT / "receipts/desktop-integrated-settings-v1.2.6-20260828T083800Z.json"
OUTPUT = PROJECT / "receipts/desktop-integrated-settings-v1.2.6-20260828T083800Z-module-checkpoint.json"
CANDIDATE = PROJECT / "releases/kc-i-settings-v126-20260828T083800Z/win-x64"
INTEGRATION = PROJECT / "canary/desktop-integrated-settings-v126/kc-i-settings-v126-20260828T083800Z"
WINDOWS_RECEIPT = PROJECT / "canary/desktop-windows/20260828T081550Z/receipt.json"
VISUAL = PROJECT / "canary/desktop-visual/knowledge-clipper-settings-control-v2.4.3-20260828T082500Z"

EXPECTED_BASE_SHA = "6EFE4C5C6A9BAC69C7CA493C3353C6778F1CE7012E3AB1AA38C2D002BBAAE6DA"
EXPECTED_MANIFEST_SHA = "B66013F3B7A750B7091217DC7BCCBEDA963A3CBE1223C59224F3C074594D0980"
EXPECTED_TREE_SHA = "6EA6BE0F7192AB9719D57F5554A98C6E1246CF6BE891D2134CBC53216DEAF973"
EXPECTED_AUTHORITY_TREE_SHA = "1DF64264BB69F4039C3B58956AA9612B98E45C2DF38B12D396B4813377E512EE"


def sha(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def require(value: bool, message: str) -> None:
    if not value:
        raise RuntimeError(message)


def evidence(path: Path) -> dict:
    require(path.is_file(), f"missing evidence: {path}")
    return {
        "path": path.relative_to(PROJECT).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha(path),
    }


def tree(root: Path) -> dict:
    rows: list[str] = []
    total = 0
    files = sorted(
        (path for path in root.rglob("*") if path.is_file()),
        key=lambda path: path.relative_to(root).as_posix(),
    )
    for path in files:
        relative = path.relative_to(root).as_posix()
        size = path.stat().st_size
        rows.append(f"{relative}\t{size}\t{sha(path).lower()}")
        total += size
    return {
        "fileCount": len(files),
        "totalBytes": total,
        "treeSha256": hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper(),
    }


def main() -> int:
    require(not OUTPUT.exists(), f"append-only qualification already exists: {OUTPUT}")
    require(sha(BASE_RECEIPT) == EXPECTED_BASE_SHA, "base integrated receipt drift")
    base = load(BASE_RECEIPT)
    require(base["status"] == "PASS-INTEGRATED-SETTINGS-V2.4.3-USER-HAND-TEST-READY", "base receipt is not PASS")
    require(base["candidate"]["treeSha256"] == EXPECTED_TREE_SHA, "candidate tree binding drift")
    require(base["authority"]["treeSha256"] == EXPECTED_AUTHORITY_TREE_SHA, "authority tree binding drift")

    manifest = CANDIDATE / "candidate-file-manifest.v1.json"
    require(sha(manifest) == EXPECTED_MANIFEST_SHA, "candidate manifest drift")
    manifest_json = load(manifest)
    require(manifest_json["payload"]["treeSha256"] == "89129ED25173252C9480CF448313E90EF46DD90072FBA9905F5E65433DE31BD8", "candidate payload tree drift")

    test_log = INTEGRATION / "desktop-build-and-test.log"
    test_text = test_log.read_text(encoding="utf-8-sig")
    require("PASS desktop automation build and tests" in test_text, "desktop full test suite did not pass")
    require("PASS named-pipe-semantic-actions-direct-stage-control-no-task-cancel" in test_text, "direct stage-control regression missing")

    build_log = INTEGRATION / "desktop-net8-build-final.log"
    build_text = build_log.read_text(encoding="utf-8-sig")
    require("已成功生成。" in build_text, "fixed SDK build success marker missing")
    require("0 个警告" in build_text and "0 个错误" in build_text, "fixed SDK build is not clean")

    windows = load(WINDOWS_RECEIPT)
    require(windows["automatedWindowsUiCanary"]["status"] == "PASS", "Windows UI Canary did not pass")
    require(windows["automatedWindowsUiCanary"]["assertions"] == 122, "Windows UI assertion count drift")

    visual = tree(VISUAL)
    require(len(list(VISUAL.glob("settings-*.png"))) == 7, "settings visual pages incomplete")
    require(len(list(VISUAL.glob("*.png"))) == 16, "visual preview inventory drift")

    integration = load(INTEGRATION / "integration-startup-canary.stdout.log")
    require(integration["result"] == "PASS", "safe integrated startup did not pass")
    require(integration["orchestratorStartedByDesktop"] is True, "embedded Orchestrator was not started")
    require(integration["namedPipeInvoked"] is True, "Named Pipe query was not exercised")
    require(not any(integration[key] for key in ("externalWorkerInvoked", "physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")), "external side effect detected")

    receipt = {
        "schemaVersion": "knowledge-clipper-desktop-integrated-settings-module-checkpoint.v1",
        "status": "PASS-INTEGRATED-SETTINGS-V2.4.3-MODULE-CHECKPOINT",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "candidateId": base["candidateId"],
        "entrypoint": base["entrypoint"],
        "entrypointSha256": base["entrypointSha256"],
        "baseReceipt": evidence(BASE_RECEIPT),
        "candidate": {
            "manifest": evidence(manifest),
            "fileCount": base["candidate"]["fileCount"],
            "totalBytes": base["candidate"]["totalBytes"],
            "treeSha256": EXPECTED_TREE_SHA,
        },
        "authority": {
            "version": "1.2.6",
            "treeSha256": EXPECTED_AUTHORITY_TREE_SHA,
            "checkpoint": base["authority"]["checkpoint"],
            "embeddedExactly": True,
        },
        "settings": {
            "coreVersion": "2.4.3",
            "checkpoint": base["settings"]["checkpoint"],
            "adapter": base["settings"]["adapter"],
            "workflowSchema": base["settings"]["schemaVersion"],
            "snapshotSchema": base["settings"]["taskSnapshot"],
            "imageMapped": True,
            "speechMapped": True,
            "contentConfigSnapshotMapped": True,
            "remnotePayloadV2Mapped": True,
            "ankiShaBoundArtifactMapped": True,
        },
        "qualification": {
            "desktopBuildAndTest": evidence(test_log),
            "initialNoRestoreBuildFailure": {
                **evidence(INTEGRATION / "desktop-net8-build.log"),
                "result": "FAIL-NETSDK1047",
                "supersededBy": "desktop-net8-build-final.log",
                "retainedForDiagnosis": True,
            },
            "fixedSdkBuild": evidence(build_log),
            "buildWarnings": 0,
            "buildErrors": 0,
            "windowsUi": evidence(WINDOWS_RECEIPT),
            "windowsUiAssertions": 122,
            "visualPreview": {
                "root": VISUAL.relative_to(PROJECT).as_posix(),
                "pngCount": 16,
                "settingsPageCount": 7,
                **visual,
            },
            "safeStartup": evidence(INTEGRATION / "startup-diagnostic.stdout.log"),
            "singleInstance": evidence(INTEGRATION / "single-instance-diagnostic.stdout.log"),
            "registry": evidence(INTEGRATION / "authority-validate-registry.log"),
            "namedPipeQuery": evidence(INTEGRATION / "integration-startup-canary.stdout.log"),
            "readableInputOutput": evidence(INTEGRATION / "settings-input-output.md"),
        },
        "singleInstance": {
            "existingProcessId": 5976,
            "existingProcessPreserved": True,
            "candidatePipeName": base["singleInstance"]["candidatePipeName"],
            "userFeedbackMode": base["singleInstance"]["feedbackMode"],
        },
        "safety": base["safety"],
        "releaseBoundary": {
            "classification": "module-checkpoint",
            "userHandTestEntrypoint": False,
            "reason": "Phase B awaits content.deepseek 0.6.1 and monotonic speech long-path authority in v1.2.7 or later.",
            "nextStep": "Append-only authority rebase with exact current Desktop payload equality gate; do not overwrite v1.2.6.",
            "repeatUiHandTestRequiredIfDesktopPayloadUnchanged": False,
        },
        "manualBoundary": base["manualBoundary"],
    }
    OUTPUT.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "status": receipt["status"],
        "entrypoint": str(CANDIDATE / "KnowledgeClipper.exe"),
        "candidateFiles": receipt["candidate"]["fileCount"],
        "candidateBytes": receipt["candidate"]["totalBytes"],
        "candidateTreeSha256": receipt["candidate"]["treeSha256"],
        "receipt": str(OUTPUT),
        "receiptSha256": sha(OUTPUT),
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (RuntimeError, OSError, ValueError, KeyError, json.JSONDecodeError) as exc:
        print(json.dumps({"status": "FAIL", "error": str(exc)}, ensure_ascii=False))
        raise SystemExit(1)
