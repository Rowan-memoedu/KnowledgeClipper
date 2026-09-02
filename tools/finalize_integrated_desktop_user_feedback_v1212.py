from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[1]
CANDIDATE_ID = "kc-integrated-user-feedback-v1212-20260830T103000Z"
CANDIDATE = PROJECT / "releases" / CANDIDATE_ID / "win-x64"
EVIDENCE_ROOT = PROJECT / "canary" / "desktop-integrated" / CANDIDATE_ID
WINDOWS_ROOT = PROJECT / "canary" / "desktop-windows" / "user-feedback-regression-20260830T094500Z"
VISUAL_ROOT = PROJECT / "canary" / "desktop-visual" / "user-feedback-regression-20260830T103500Z"
CONTENT_ROOT = PROJECT / "content-worker" / "canary" / "authority" / "content.deepseek-live-question-v070-authority-v1212-20260830T0915Z"
OUTPUT = PROJECT / "receipts" / "desktop-integrated-user-feedback-v1.2.12-20260830T104000Z.json"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def sha(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def inventory(root: Path, excluded: set[str] | None = None) -> dict:
    excluded = excluded or set()
    files = []
    rows = []
    total = 0
    paths = sorted(
        (path for path in root.rglob("*") if path.is_file()),
        key=lambda path: path.relative_to(root).as_posix(),
    )
    for path in paths:
        relative = path.relative_to(root).as_posix()
        if relative in excluded:
            continue
        size = path.stat().st_size
        digest = sha(path)
        files.append({"path": relative, "bytes": size, "sha256": digest})
        rows.append(f"{relative}\t{size}\t{digest.lower()}")
        total += size
    return {
        "fileCount": len(files),
        "totalBytes": total,
        "treeSha256": hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper(),
        "treeAlgorithm": "sha256(utf8 ordinal(relativePath + TAB + bytes + TAB + lowercaseSha256) joined by LF)",
        "files": files,
    }


def evidence(path: Path) -> dict:
    return {
        "path": path.relative_to(PROJECT).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha(path),
    }


def main() -> None:
    require(not OUTPUT.exists(), f"append-only receipt already exists: {OUTPUT}")
    manifest_path = CANDIDATE / "candidate-file-manifest.v1.json"
    build_receipt_path = CANDIDATE / "desktop-build-receipt.v1.json"
    manifest = load(manifest_path)
    build_receipt = load(build_receipt_path)
    windows_receipt = load(WINDOWS_ROOT / "receipt.json")
    startup = load(EVIDENCE_ROOT / "startup-diagnostic.json")
    integration = load(EVIDENCE_ROOT / "integration-startup-canary.json")
    registry = load(EVIDENCE_ROOT / "validate-registry.json")

    require(manifest["candidateId"] == CANDIDATE_ID, "candidate identity mismatch")
    require(build_receipt["buildId"] == "knowledge-clipper-user-feedback-fixes-20260830T102500Z", "desktop build identity mismatch")
    require(build_receipt["fileCount"] == 543, "desktop build file count mismatch")
    require(build_receipt["treeSha256"].upper() == "A5109F75BCE7900F97159DB38E6EBA581D4C57684FB8BD9DED367A1055F1C7FB", "desktop build tree mismatch")
    require(sha(build_receipt_path) == "21B58FD2E3E885DC6CC774F02D49EC1EDA982096DDD01273D8A4C35262F98D2D", "desktop build receipt drift")

    payload = inventory(CANDIDATE, {"candidate-file-manifest.v1.json"})
    for key in ("fileCount", "totalBytes", "treeSha256"):
        expected = manifest["payload"][key]
        actual = payload[key]
        require(str(actual).upper() == str(expected).upper(), f"candidate payload {key} mismatch")
    recorded = {item["path"]: item for item in manifest["payload"]["files"]}
    require(len(recorded) == payload["fileCount"], "candidate payload manifest entry count mismatch")
    for item in payload["files"]:
        expected = recorded.get(item["path"])
        require(expected is not None, f"candidate payload entry missing: {item['path']}")
        require(expected["bytes"] == item["bytes"] and expected["sha256"].upper() == item["sha256"], f"candidate payload entry drift: {item['path']}")

    sealed = inventory(CANDIDATE)
    require(sealed["fileCount"] == 841 and sealed["totalBytes"] == 429826541, "sealed candidate aggregate mismatch")
    require(sealed["treeSha256"] == "3D65F0F157E1209CCE38213275715E8867FEEE8C0BA5F636684D56938E4CB86B", "sealed candidate tree mismatch")
    authority = inventory(CANDIDATE / "authority" / "win-x64")
    require(authority["fileCount"] == 284 and authority["totalBytes"] == 260374870, "authority aggregate mismatch")
    require(authority["treeSha256"] == "31CA3150B1A2EF9F97DD67B3B5491E94C49275C23018BC75B8BFECACC504DF13", "authority tree mismatch")
    require(sha(CANDIDATE / "authority/metadata/checkpoint.json") == "552151B3EDBEED94FD1B4ECC7B49F78CD7503321119235BBF3ECAA2A0EEE8580", "authority checkpoint drift")
    require(sha(CANDIDATE / "authority/metadata/worker-registry.v1.json") == "FC105AB65FD80B72779D1601E2C4B6FAA7ECCAF5F5C1BADC774060D8F685495A", "authority registry drift")

    require(startup["result"] == "PASS", "safe startup failed")
    require(startup["packageFilesVerified"] and startup["packageTreeVerified"], "desktop package startup verification failed")
    require(startup["candidateIntegrationParsed"] and startup["candidateOrchestratorPathsVerified"], "candidate integration startup verification failed")
    require(startup["packageTreeSha256"].upper() == build_receipt["treeSha256"].upper(), "startup/build tree mismatch")
    require(startup["externalConnections"] == 0 and not startup["remNoteInvoked"] and not startup["mineruInvoked"], "safe startup crossed external boundary")
    require(integration["result"] == "PASS" and integration["orchestratorStartedByDesktop"] and integration["namedPipeInvoked"], "integrated Orchestrator/Named Pipe gate failed")
    require(integration["action"] == "task.status.query-recent", "integrated query action mismatch")
    require(not any(integration[key] for key in ("externalWorkerInvoked", "physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")), "integrated startup crossed external boundary")
    require(registry["status"] == "valid" and registry["registryVersion"] == "1.2.12" and registry["workers"] == 5, "registry gate failed")

    automated = windows_receipt["automatedWindowsUiCanary"]
    require(automated["status"] == "PASS" and automated["assertions"] == 158, "Windows UI regression gate failed")
    require(windows_receipt["realWindowsCanaries"]["focusedTextBoxCtrlC"]["status"] == "PASS", "focused Ctrl+C regression gate failed")
    build_log = EVIDENCE_ROOT / "desktop-build-and-test.log"
    build_text = build_log.read_text(encoding="utf-8")
    require("PASS desktop automation build and tests" in build_text, "desktop build-and-test gate failed")
    visual_log = EVIDENCE_ROOT / "visual-preview-fixed-sdk.log"
    require("VISUAL_PREVIEW_CANARY PASS" in visual_log.read_text(encoding="utf-8"), "visual preview gate failed")
    visual = inventory(VISUAL_ROOT)
    require(sum(1 for path in VISUAL_ROOT.glob("*.png")) == 16, "visual preview image count mismatch")

    content_receipt = CONTENT_ROOT / "authority-receipt.json"
    content_readable = CONTENT_ROOT / "READABLE-WORKFLOW.md"
    require(sha(content_receipt) == "0DFD0DF842EA86B1BC89A74198684DD5649D97DA6B757F3C6755ED6D43B334B7", "Content question authority evidence drift")
    require(sha(content_readable) == "9B86EB9C2AFB642E191C535E26D471D4C7FDBD7315CFF0B2ACFED463E708D2C7", "Content readable workflow evidence drift")

    forbidden_files = [
        path.relative_to(CANDIDATE).as_posix()
        for path in CANDIDATE.rglob("*")
        if path.is_file() and path.name.lower() in {"credentials.json", "credentials.json.bak"}
    ]
    leaked_markers = []
    for path in (path for path in CANDIDATE.rglob("*") if path.is_file()):
        data = path.read_bytes()
        if b"canary-deepseek-local-only" in data or b"canary-mineru-local-only" in data:
            leaked_markers.append(path.relative_to(CANDIDATE).as_posix())
    require(not forbidden_files and not leaked_markers, "candidate secret scan failed")

    receipt = {
        "schemaVersion": "knowledge-clipper-desktop-integrated-user-feedback-checkpoint.v1",
        "status": "PASS-INTEGRATED-AUTOMATED-CONDITIONAL",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "candidateId": CANDIDATE_ID,
        "userHandTestEntrypoint": True,
        "candidate": {
            "root": CANDIDATE.relative_to(PROJECT).as_posix(),
            "entrypoint": (CANDIDATE / "KnowledgeClipper.exe").relative_to(PROJECT).as_posix(),
            "entrypointSha256": sha(CANDIDATE / "KnowledgeClipper.exe"),
            "applicationDllSha256": sha(CANDIDATE / "KnowledgeClipper.dll"),
            "manifest": evidence(manifest_path),
            "sealed": {key: sealed[key] for key in ("fileCount", "totalBytes", "treeSha256", "treeAlgorithm")},
        },
        "desktop": {
            "buildId": build_receipt["buildId"],
            "buildReceipt": evidence(build_receipt_path),
            "payload": {key: build_receipt[key] for key in ("fileCount", "totalBytes", "treeSha256")},
        },
        "authority": {
            "version": "1.2.12",
            "checkpointId": "knowledge-clipper-authority-1.2.12-20260830T075600Z",
            "checkpointSha256": "552151B3EDBEED94FD1B4ECC7B49F78CD7503321119235BBF3ECAA2A0EEE8580",
            "registrySha256": "FC105AB65FD80B72779D1601E2C4B6FAA7ECCAF5F5C1BADC774060D8F685495A",
            "releaseFiles": authority["fileCount"],
            "releaseBytes": authority["totalBytes"],
            "releaseTreeSha256": authority["treeSha256"],
        },
        "fixes": {
            "settingsDoesNotDisableFloatingWindow": "Settings window is modeless and singleton; floating window remains enabled and draggable",
            "productionPipeBinding": "Desktop resolves candidate-integration.json PipeName and uses the embedded Orchestrator Named Pipe",
            "initialQuestionRouting": "clip.question publishes cards with no export destinations, independent from the active Anki/RemNote profile",
            "contextCapture": "Windows UI Automation TextPattern captures ordered prefix/selection/suffix when the source application exposes reliable accessibility text",
        },
        "automatedEvidence": {
            "desktopBuildAndTest": evidence(build_log),
            "windowsUi": {"status": "PASS", "assertions": 158, **evidence(WINDOWS_ROOT / "receipt.json")},
            "visualPreview": {"status": "PASS", "fileCount": visual["fileCount"], "treeSha256": visual["treeSha256"], "root": VISUAL_ROOT.relative_to(PROJECT).as_posix()},
            "visualInitialSdkPathFailurePreserved": evidence(EVIDENCE_ROOT / "visual-preview.log"),
            "visualFixedSdk": evidence(visual_log),
            "safeStartup": evidence(EVIDENCE_ROOT / "startup-diagnostic.json"),
            "registry": evidence(EVIDENCE_ROOT / "validate-registry.json"),
            "namedPipeQuery": evidence(EVIDENCE_ROOT / "integration-startup-canary.json"),
            "contentQuestionBackendAuthority": evidence(content_receipt),
            "contentQuestionReadableWorkflow": evidence(content_readable),
        },
        "safety": {
            "externalWorkerRunsInThisDesktopQualification": 0,
            "externalProductionApis": 0,
            "physicalSpeaker": 0,
            "remNoteWrites": 0,
            "ankiConnectionsOrWrites": 0,
            "mineruUploads": 0,
            "existingUserProcessTerminated": False,
            "credentialFilesInCandidate": 0,
            "knownCanarySecretMarkersInCandidate": 0,
        },
        "manualBoundary": [
            "External applications differ in UI Automation support; physical context capture must be checked in the user's actual browser/editor",
            "Physical cross-monitor mixed-DPI drag, image clipboard, and system screenshot drag remain user/system interaction checks",
            "Actual Content/Anki production calls were not repeated by this Desktop qualification; the separately bound backend authority evidence passed",
            "OCR is user-deferred and RemNote is not part of this hand-test admission",
        ],
        "supersededBuildAttempts": [
            "dist/desktop-win-x64/knowledge-clipper-user-feedback-fixes-20260830T100500Z (NETSDK1047; preserved)",
            "dist/desktop-win-x64/knowledge-clipper-user-feedback-fixes-20260830T101500Z (non-ordinal receipt; preserved)",
        ],
    }
    OUTPUT.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "status": receipt["status"],
        "receipt": str(OUTPUT),
        "receiptSha256": sha(OUTPUT),
        "candidateTreeSha256": sealed["treeSha256"],
        "manifestSha256": sha(manifest_path),
        "entrypointSha256": receipt["candidate"]["entrypointSha256"],
    }, ensure_ascii=False))


if __name__ == "__main__":
    main()
