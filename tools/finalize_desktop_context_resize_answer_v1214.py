from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[1]
CANDIDATE_ID = "kc-i-crq-v1214-20260830T105500Z"
CANDIDATE = PROJECT / "releases" / CANDIDATE_ID / "win-x64"
PREVIOUS_ID = "kc-i-crq-v1212-20260830T114000Z"
PREVIOUS_RECEIPT = PROJECT / "receipts" / "desktop-context-resize-answer-v1.2.12-20260830T115000Z.json"
EVIDENCE = PROJECT / "canary" / "desktop-integrated" / CANDIDATE_ID
AUTHORITY = PROJECT / "contracts" / "authority" / "worker-registry" / "v1.2.14-20260830T184700Z"
AUTHORITY_RECEIPT = PROJECT / "receipts" / "knowledge-clipper-authority-1.2.14-20260830T184700Z.json"
CORE_CHECKPOINT = PROJECT / "contracts" / "core" / "v2.5" / "checkpoints" / "v2.5.3-20260830T184500Z" / "checkpoint.json"
QUESTION_EVIDENCE = PROJECT / "canary" / "sealed-question-anki-v1.2.14" / "20260830T105113Z"
WINDOWS_UI_LOG = PROJECT / "canary" / "desktop-windows" / "context-resize-answer-seam-20260830T112000Z" / "windows-ui-automation.log"
OUTPUT = PROJECT / "receipts" / "desktop-context-resize-answer-v1.2.14-20260830T110000Z.json"

EXPECTED = {
    "desktopReceipt": "66F8A54A69098D1B21C117C403DCC0F74E6FF714E5B0F062B2740E02B4807532",
    "desktopTree": "69B4EA1BD04681826910AE1730A1ED909D6721E5EABD020F9DB692A00ABF80B6",
    "previousReceipt": "113F73D643A2EF26DBECA71887D9B656635678C4ACD3391EEEC7C91E5EAE735A",
    "authorityTree": "30F1A8E65A335811545114474573196B689657DA8BAFB511E7A2F84638DB9D3E",
    "authorityCheckpoint": "C2DD2E6C2099E9D7A2D4C742A53BEDE28EA229CEF50D849A464F7C20314121D4",
    "authorityRegistry": "A1A95EE7FCBBBB3051777088E66389EC2C6578B30AD7562CFB0992DE4729897C",
    "authorityReceipt": "E8D729C8C39EE0BE1CF9C1038F9DAD021668D8720ADDB66DE4EFC0E74225E8D7",
    "coreCheckpoint": "16F39BAB63346AADEA83D1C66B1BE2520998BFE1A8341667D121CFCB84838D9A",
    "questionReceipt": "F23D9D0D4D6A15F62D4696F37E524515D07CBAA0FF89F7FD2395034C9B752466",
    "questionReadable": "A52D148E6F51E343D4001F6CF94C0C703E4518923A91C7A4440B52AAFA186175",
}


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
    rows: list[str] = []
    count = 0
    total = 0
    for path in sorted(
        (item for item in root.rglob("*") if item.is_file()),
        key=lambda item: item.relative_to(root).as_posix(),
    ):
        relative = path.relative_to(root).as_posix()
        size = path.stat().st_size
        rows.append(f"{relative}\t{size}\t{sha(path).lower()}")
        count += 1
        total += size
    return {
        "fileCount": count,
        "totalBytes": total,
        "treeSha256": hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper(),
    }


def evidence(path: Path) -> dict:
    return {
        "path": path.relative_to(PROJECT).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha(path),
    }


def main() -> None:
    require(not OUTPUT.exists(), "append-only receipt already exists")
    manifest_path = CANDIDATE / "candidate-file-manifest.v1.json"
    build_receipt_path = CANDIDATE / "desktop-build-receipt.v1.json"
    manifest = load(manifest_path)
    build = load(build_receipt_path)
    startup_path = EVIDENCE / "startup-diagnostic.stdout.log"
    registry_path = EVIDENCE / "authority-validate-registry.log"
    integration_path = EVIDENCE / "integration-startup-canary.stdout.log"
    single_path = EVIDENCE / "single-instance-diagnostic.stdout.log"
    tree_gate_path = EVIDENCE / "manifest-tree-gate.json"
    equality_path = EVIDENCE / "desktop-payload-equality.json"
    startup = load(startup_path)
    registry = load(registry_path)
    integration = load(integration_path)
    tree_gate = load(tree_gate_path)
    equality = load(equality_path)
    question_receipt_path = QUESTION_EVIDENCE / "execution-receipt.json"
    question_readable_path = QUESTION_EVIDENCE / "READABLE.md"
    question = load(question_receipt_path)

    require(manifest["candidateId"] == CANDIDATE_ID, "candidate identity mismatch")
    require(manifest["desktop"]["fileCount"] == 544, "desktop payload file count drift")
    require(manifest["desktop"]["treeSha256"] == EXPECTED["desktopTree"], "desktop payload tree drift")
    require(sha(build_receipt_path) == EXPECTED["desktopReceipt"], "desktop receipt drift")
    require(build["fileCount"] == 544 and build["treeSha256"].upper() == EXPECTED["desktopTree"], "desktop build drift")
    require(equality["status"] == "PASS" and equality["fileCount"] == 544 and equality["changedFiles"] == 0, "desktop equality gate failed")
    require(equality["previousCandidate"] == PREVIOUS_ID, "desktop equality baseline mismatch")
    require(sha(PREVIOUS_RECEIPT) == EXPECTED["previousReceipt"], "previous user hand-test checkpoint drift")
    previous = load(PREVIOUS_RECEIPT)
    require(previous["userHandTestEntrypoint"] is True, "previous Desktop payload was not user-hand-test admitted")

    sealed = inventory(CANDIDATE)
    require(tree_gate["status"] == "PASS", "manifest/tree gate failed")
    require(tree_gate["candidateTreeSha256"] == sealed["treeSha256"], "candidate tree drift")
    require(tree_gate["authorityTreeSha256"] == EXPECTED["authorityTree"], "embedded authority tree drift")
    require(manifest["authority"]["releaseTreeSha256"] == EXPECTED["authorityTree"], "manifest authority tree drift")
    require(sha(AUTHORITY / "checkpoint.json") == EXPECTED["authorityCheckpoint"], "authority checkpoint drift")
    require(sha(AUTHORITY / "worker-registry.v1.json") == EXPECTED["authorityRegistry"], "authority registry drift")
    require(sha(AUTHORITY_RECEIPT) == EXPECTED["authorityReceipt"], "authority receipt drift")
    require(sha(CORE_CHECKPOINT) == EXPECTED["coreCheckpoint"], "Core checkpoint drift")

    require(startup["result"] == "PASS" and startup["candidateOrchestratorPathsVerified"], "safe startup failed")
    require(startup["externalConnections"] == 0 and startup["namedPipeInvoked"] is False, "safe startup crossed external boundary")
    require(registry["status"] == "valid" and registry["registryVersion"] == "1.2.14" and registry["workers"] == 5, "registry gate failed")
    require(integration["result"] == "PASS" and integration["orchestratorStartedByDesktop"] and integration["namedPipeInvoked"], "embedded Orchestrator query failed")
    require(integration["action"] == "task.status.query-recent", "unexpected integration action")
    require(not any(integration[key] for key in ("externalWorkerInvoked", "physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")), "integration Canary crossed external boundary")

    require(sha(question_receipt_path) == EXPECTED["questionReceipt"], "question-to-Anki receipt drift")
    require(sha(question_readable_path) == EXPECTED["questionReadable"], "question-to-Anki readable drift")
    require(question["status"] == "passed" and question["boundary"] == "sealed-actual-orchestrator-process", "question chain did not pass in sealed process")
    require(question["states"] == ["waiting_input", "waiting_input", "succeeded"], "question state sequence drift")
    require(question["initial"]["destinationWrites"] == 0 and question["followUp"]["destinationWrites"] == 0, "question answer wrote early")
    require(question["anki"]["writeCount"] == 1 and question["anki"]["exactReadbackPassed"] is True, "make-cards Anki readback failed")
    require(question["replay"]["newContentAttempts"] == 0 and question["replay"]["newAnkiAttempts"] == 0 and question["replay"]["newNotes"] == 0, "replay was not idempotent")

    windows_text = WINDOWS_UI_LOG.read_text(encoding="utf-8")
    require("PASS floating window has a hard minimum size" in windows_text, "minimum-size UI assertion missing")
    require("PASS restore-size action recovers" in windows_text, "restore-size UI assertion missing")

    receipt = {
        "schemaVersion": "knowledge-clipper-desktop-context-resize-answer-checkpoint.v2",
        "status": "PASS-AUTOMATED-AUTHORITY-V1.2.14-USER-HANDTEST",
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
        "desktop": {
            "unchangedFromCandidate": PREVIOUS_ID,
            "payloadFiles": equality["fileCount"],
            "changedFiles": equality["changedFiles"],
            "payloadTreeSha256": EXPECTED["desktopTree"],
            "previousUserHandTestReceipt": evidence(PREVIOUS_RECEIPT),
            "uiRetestForAuthorityRebaseRequired": False,
            "reason": "All 544 receipt-bound Desktop files are byte-for-byte identical; only authority and integration metadata changed.",
        },
        "authority": {
            "version": "1.2.14",
            "checkpointId": "knowledge-clipper-authority-1.2.14-20260830T184700Z",
            "checkpoint": evidence(AUTHORITY / "checkpoint.json"),
            "registry": evidence(AUTHORITY / "worker-registry.v1.json"),
            "receipt": evidence(AUTHORITY_RECEIPT),
            "releaseTreeSha256": EXPECTED["authorityTree"],
            "coreRuntime": "2.5.2",
            "coreCheckpoint": evidence(CORE_CHECKPOINT),
            "workers": {"content.deepseek": "0.8.0", "export.anki": "1.2.2"},
        },
        "automatedEvidence": {
            "safeStartup": evidence(startup_path),
            "singleInstance": evidence(single_path),
            "registry5Of5": evidence(registry_path),
            "embeddedOrchestratorNamedPipeQuery": evidence(integration_path),
            "manifestTree": evidence(tree_gate_path),
            "desktopPayloadEquality": evidence(equality_path),
            "priorDesktopBuildAndUi": previous["automatedEvidence"],
        },
        "questionWorkflow": {
            "status": "PASS-SEALED-ACTUAL-ORCHESTRATOR-PROCESS",
            "receipt": evidence(question_receipt_path),
            "readable": evidence(question_readable_path),
            "states": question["states"],
            "initialDestinationWrites": question["initial"]["destinationWrites"],
            "followUpDestinationWrites": question["followUp"]["destinationWrites"],
            "makeCardsAnkiWrites": question["anki"]["writeCount"],
            "exactReadback": question["anki"]["exactReadbackPassed"],
            "replayNewAttempts": question["replay"]["newContentAttempts"] + question["replay"]["newAnkiAttempts"],
            "replayNewNotes": question["replay"]["newNotes"],
            "desktopUiProcessE2E": False,
        },
        "safety": {
            "externalCallsDuringDesktopRebase": 0,
            "workersRunDuringDesktopRebase": 0,
            "remNoteWritesDuringDesktopRebase": 0,
            "ankiWritesDuringDesktopRebase": 0,
            "mineruCallsDuringDesktopRebase": 0,
            "physicalSpeakerDuringDesktopRebase": 0,
            "existingUserProcessTerminated": False,
        },
        "manualBoundary": [
            "Test context recognition in the actual browser/editor because UI Automation exposure is application-specific.",
            "Test edge/corner resize and Restore Default Size on the user's display/DPI setup.",
            "Test bottom question, follow-up and make-cards through the actual Desktop UI; backend public chain is passed, but this exact UI-process E2E was not automated.",
            "OCR remains user-deferred; RemNote is not required for this hand-test route.",
        ],
    }
    OUTPUT.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(
        json.dumps(
            {
                "status": receipt["status"],
                "receipt": str(OUTPUT),
                "receiptSha256": sha(OUTPUT),
                "candidateTreeSha256": sealed["treeSha256"],
                "manifestSha256": sha(manifest_path),
                "entrypointSha256": receipt["candidate"]["entrypointSha256"],
            },
            ensure_ascii=False,
        )
    )


if __name__ == "__main__":
    main()
