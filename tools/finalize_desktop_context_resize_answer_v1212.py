from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[1]
CANDIDATE_ID = "kc-i-crq-v1212-20260830T114000Z"
CANDIDATE = PROJECT / "releases" / CANDIDATE_ID / "win-x64"
EVIDENCE = PROJECT / "canary" / "desktop-integrated" / CANDIDATE_ID
VISUAL = PROJECT / "canary" / "desktop-visual" / "context-resize-answer-20260830T114500Z"
SEALED_PROCESS = PROJECT / "canary" / "sealed-orchestrator-process-v1.2.12" / "20260830T092832Z"
OUTPUT = PROJECT / "receipts" / "desktop-context-resize-answer-v1.2.12-20260830T115000Z.json"


def require(value: bool, message: str) -> None:
    if not value:
        raise RuntimeError(message)


def sha(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def inventory(root: Path) -> dict:
    rows, count, total = [], 0, 0
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        size = path.stat().st_size
        digest = sha(path)
        rows.append(f"{relative}\t{size}\t{digest.lower()}")
        count += 1
        total += size
    return {"fileCount": count, "totalBytes": total, "treeSha256": hashlib.sha256("\n".join(rows).encode()).hexdigest().upper()}


def evidence(path: Path) -> dict:
    return {"path": path.relative_to(PROJECT).as_posix(), "bytes": path.stat().st_size, "sha256": sha(path)}


def main() -> None:
    require(not OUTPUT.exists(), "append-only receipt already exists")
    manifest_path = CANDIDATE / "candidate-file-manifest.v1.json"
    build_receipt_path = CANDIDATE / "desktop-build-receipt.v1.json"
    manifest = load(manifest_path)
    build = load(build_receipt_path)
    startup = load(EVIDENCE / "startup-diagnostic.json")
    registry = load(EVIDENCE / "validate-registry.json")
    integration = load(EVIDENCE / "integration-startup-canary.json")
    sealed_process_receipt = SEALED_PROCESS / "execution-receipt.json"
    sealed_process = load(sealed_process_receipt)

    require(manifest["candidateId"] == CANDIDATE_ID, "candidate identity mismatch")
    require(build["fileCount"] == 544 and build["treeSha256"].upper() == "69B4EA1BD04681826910AE1730A1ED909D6721E5EABD020F9DB692A00ABF80B6", "desktop build drift")
    require(sha(build_receipt_path) == "66F8A54A69098D1B21C117C403DCC0F74E6FF714E5B0F062B2740E02B4807532", "build receipt drift")
    sealed = inventory(CANDIDATE)
    require(sealed == {"fileCount": 842, "totalBytes": 429894711, "treeSha256": "FB00037DAC8F9AC18470BE51F113CCDDF3504FA5FC69BCCC2315EB5CE60BDAFD"}, "sealed candidate drift")
    require(manifest["authority"]["releaseTreeSha256"] == "31CA3150B1A2EF9F97DD67B3B5491E94C49275C23018BC75B8BFECACC504DF13", "authority tree drift")
    require(startup["result"] == "PASS" and startup["packageFilesVerified"] and startup["packageTreeVerified"] and startup["externalConnections"] == 0, "safe startup failed")
    require(registry["status"] == "valid" and registry["workers"] == 5, "registry failed")
    require(integration["result"] == "PASS" and integration["orchestratorStartedByDesktop"] and integration["namedPipeInvoked"], "integrated Named Pipe startup failed")
    require(not any(integration[key] for key in ("externalWorkerInvoked", "physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")), "safe integration crossed external boundary")
    require("PASS desktop automation build and tests" in (EVIDENCE / "desktop-build-and-test.log").read_text(encoding="utf-8"), "desktop build-and-test failed")
    require("VISUAL_PREVIEW_CANARY PASS" in (EVIDENCE / "visual-preview.log").read_text(encoding="utf-8"), "visual preview failed")
    require(len(list(VISUAL.glob("*.png"))) == 16, "visual image count mismatch")
    latest_windows_failure = PROJECT / "canary" / "desktop-windows" / "context-resize-answer-seam-20260830T112000Z" / "windows-ui-automation.log"
    windows_text = latest_windows_failure.read_text(encoding="utf-8")
    require("PASS floating window has a hard minimum size" in windows_text and "PASS restore-size action recovers" in windows_text, "new window-size assertions did not run")
    require("WINDOWS_UI_CANARY FAIL" in windows_text and "inline TextBox receives real mouse focus" in windows_text, "physical UI boundary was not preserved")
    require(sha(sealed_process_receipt) == "AAB758B02B74BE76E1BC43918DB51F7C54D3B404566A8C5CEF8A378020A15E31", "sealed process evidence drift")
    require(sealed_process["inlineQuestion"]["status"] == "succeeded" and sealed_process["inlineQuestion"]["anki"]["status"] == "unsupported-existing-public-chain", "question process boundary drift")

    receipt = {
        "schemaVersion": "knowledge-clipper-desktop-context-resize-answer-checkpoint.v1",
        "status": "PASS-AUTOMATED-CONDITIONAL-PHYSICAL-FOCUS",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "candidateId": CANDIDATE_ID,
        "userHandTestEntrypoint": True,
        "candidate": {
            "root": CANDIDATE.relative_to(PROJECT).as_posix(),
            "entrypoint": (CANDIDATE / "KnowledgeClipper.exe").relative_to(PROJECT).as_posix(),
            "entrypointSha256": sha(CANDIDATE / "KnowledgeClipper.exe"),
            "applicationDllSha256": sha(CANDIDATE / "KnowledgeClipper.dll"),
            "manifest": evidence(manifest_path),
            "sealed": sealed,
        },
        "fixes": {
            "context": "search focused element ancestors, then the owning window Document descendant, then a TextPattern descendant; never search another application",
            "windowSize": "hard 344x210 minimum, borderless edge/corner resize hit-test, persisted safe size, tray/context-menu default-size recovery",
            "answerContinuation": "bottom question success leaves a stable View Answer status action; dialog binds current projection, verifies question.answer.text, and auto-refreshes before existing make-cards",
        },
        "automatedEvidence": {
            "desktopBuildAndTest": evidence(EVIDENCE / "desktop-build-and-test.log"),
            "safeStartup": evidence(EVIDENCE / "startup-diagnostic.json"),
            "registry": evidence(EVIDENCE / "validate-registry.json"),
            "namedPipeQuery": evidence(EVIDENCE / "integration-startup-canary.json"),
            "visualPreview": {"status": "PASS", "imageCount": 16, "root": VISUAL.relative_to(PROJECT).as_posix(), **inventory(VISUAL)},
            "windowsSizeAssertions": {"status": "PASS-BEFORE-PHYSICAL-FOCUS-FAIL", **evidence(latest_windows_failure)},
            "physicalUiBoundary": "FAIL: existing real mouse foreground-focus step timed out; not downgraded or reported as PASS",
        },
        "actualExecutionMatrix": {
            "desktopProcess": "PASS only for safe startup and embedded Orchestrator launch; no business action was submitted by this new candidate process",
            "bottomInput": "PASS semantic/real-keyboard baseline and fake Named Pipe; current full physical run blocked by foreground-focus instability",
            "namedPipe": "PASS candidate-specific query and fake task.submit; no coordinate-based protocol",
            "questionAnswer": "PASS in separately bound actual sealed Orchestrator process; not yet driven through this candidate UI process",
            "makeCards": "PASS Content card-bundle in separately bound actual sealed Orchestrator process; Desktop reuses public question.make-cards",
            "ankiForQuestion": "NO-GO: Core question resume still ends with destinations=[] and has no export stage",
        },
        "sealedProcessEvidence": {
            "receipt": evidence(sealed_process_receipt),
            "readable": evidence(SEALED_PROCESS / "READABLE.md"),
            "classification": "actual sealed Orchestrator process, direct immutable task/IPC driver; not Desktop UI process",
        },
        "safety": {"externalCallsInThisQualification": 0, "remNoteWrites": 0, "ankiWrites": 0, "mineruCalls": 0, "physicalSpeaker": 0, "existingUserProcessTerminated": False},
        "manualBoundary": [
            "Test context in the user's actual browser/editor because UI Automation exposure is application-specific",
            "Test edge/corner resize and the Restore Default Size menu on the user's DPI/display setup",
            "Physical UI foreground-focus Canary is still unstable and remains a recorded failure",
            "Question make-cards to Anki waits for Core export inheritance; Desktop must not invent a private destination payload",
        ],
        "invalidatedNeverPromoted": ["releases/kc-integrated-context-resize-answer-v1212-20260830T113500Z (Windows MAX_PATH copy failure)"],
    }
    OUTPUT.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": receipt["status"], "receipt": str(OUTPUT), "receiptSha256": sha(OUTPUT), "candidateTreeSha256": sealed["treeSha256"], "entrypointSha256": receipt["candidate"]["entrypointSha256"]}, ensure_ascii=False))


if __name__ == "__main__":
    main()
