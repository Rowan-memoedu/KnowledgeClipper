from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


EXPECTED = {
    "manifest": "1C83A3794FB96A64D605D842807714D8ADE341FC89489855AD93E55998767648",
    "receipt": "84E570ACA176212F6878111A66826F9C724D66E04F2A45F1BB94ADE77B96015B",
    "sdk_manifest": "B3465F8417AC72B0A9105A39B6FB5AF0683ACB2ADD8E13C2772DC8610B8A6FB3",
    "client": "1D06B0F214C63C93699AC14EEE3617A0697B331BB46CDCAF8E43CC316747F38C",
}


class GateFailure(RuntimeError):
    pass


def require(value: bool, message: str) -> None:
    if not value:
        raise GateFailure(message)


def sha(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def evidence(path: Path) -> dict[str, Any]:
    return {"path": str(path.resolve()), "bytes": path.stat().st_size, "sha256": sha(path)}


def verify_manifest(root: Path, manifest: dict[str, Any]) -> tuple[int, int]:
    mismatches: list[str] = []
    for item in manifest["files"]:
        path = root / item["path"]
        if not path.is_file() or path.stat().st_size != item["bytes"] or sha(path) != item["sha256"].upper():
            mismatches.append(item["path"])
    require(not mismatches, f"manifest payload drift: {mismatches[:5]}")
    return len(manifest["files"]), sum(item["bytes"] for item in manifest["files"])


def evaluate(candidate: Path, sdk: Path) -> dict[str, Any]:
    candidate = candidate.resolve()
    sdk = sdk.resolve()
    manifest_path = candidate / "candidate-manifest.json"
    receipt_path = candidate / "canary/fake-question-preview-confirm/receipt.json"
    sdk_manifest_path = sdk / "freeze-manifest.json"
    require(sha(manifest_path) == EXPECTED["manifest"], "candidate manifest SHA drift")
    require(sha(receipt_path) == EXPECTED["receipt"], "fake receipt SHA drift")
    require(sha(sdk_manifest_path) == EXPECTED["sdk_manifest"], "SDK freeze manifest SHA drift")

    manifest = load(manifest_path)
    declared_files, declared_bytes = verify_manifest(candidate, manifest)
    require(manifest["candidateId"] == candidate.name, "candidate identity mismatch")
    client = candidate / "core-v3/KnowledgeClipper.Core.V3.Client.dll"
    require(sha(client) == EXPECTED["client"], "candidate Client DLL drift")

    sdk_manifest = load(sdk_manifest_path)
    sdk_files, sdk_bytes = verify_manifest(sdk, sdk_manifest)
    require(sdk_manifest["source"]["candidate"] == str(candidate), "SDK source candidate mismatch")
    require(sdk_manifest["source"]["candidateManifestSha256"] == EXPECTED["manifest"], "SDK candidate binding drift")
    require(sdk_manifest["source"]["fakeReceiptSha256"] == EXPECTED["receipt"], "SDK receipt binding drift")
    contract = load(sdk / "sdk-contract.v1.json")
    require(contract["assembly"]["sha256"] == EXPECTED["client"], "SDK Client binding drift")
    require(contract["workflow"]["operation"] == "question.make-cards", "SDK operation drift")
    require({"ConfirmFlashcardDraftAsync", "CancelFlashcardDraftAsync"}.issubset(contract["workflow"]["methods"]), "SDK action methods missing")

    receipt = load(receipt_path)
    require(receipt["status"] == "PASS" and Path(receipt["candidate"]).resolve() == candidate, "receipt candidate binding drift")
    require(receipt["externalWorkersExecuted"] is False and receipt["workerDescribeOnly"] is True, "external worker boundary drift")
    require(receipt["speakerPlayback"] is False, "speaker boundary drift")
    require(receipt["exporterBeforeConfirm"] == {"requests": 0, "attempts": 0, "writes": 0}, "pre-confirm exporter boundary drift")
    question = receipt["question"]
    for phase in ("initial", "followUp"):
        require(question[phase]["cardDraft"] is None and question[phase]["ankiSemanticReadback"] is None, f"{phase} leaked draft/export")
    draft = question["draft"]["cardDraft"]
    recovered = question["recoveredDraft"]["cardDraft"]
    require(draft == recovered, "restart changed token/revision/digest/cards")
    require(question["draft"]["ankiSemanticReadback"] is None, "draft triggered destination")
    require(question["staleTokenRejectedBeforeExporter"] is True, "stale token not rejected")
    require(question["doubleConfirmRejected"] is True, "double confirm not rejected")
    confirmed = question["confirmed"]
    require(confirmed["cardDraft"] == draft, "confirmed draft drift")
    require(confirmed["ankiSemanticReadback"] is not None, "confirmed semantic readback missing")
    card = draft["cards"][0]
    readback = confirmed["ankiSemanticReadback"]["cards"][0]
    require(card["front"] == readback["front"] and card["back"] == readback["back"], "readback card text drift")
    after = question["afterQuestionConfirmation"]
    require(after["ankiExportAttempts"] == 1 and after["ankiExternalWriteCount"] == 1 and after["ankiReadbackCount"] == 1, "confirm was not exactly one bounded fake export")
    require(after["remNoteCalls"] == after["minerUCalls"] == after["speakerCalls"] == 0, "unexpected side effect after confirmation")
    cancelled = question["cancelled"]
    require(cancelled["state"] == 4 and cancelled["ankiSemanticReadback"] is None and cancelled["destinationEvidenceArtifact"] is None, "cancel reached destination")

    return {
        "schema": "kc.quality.question-flashcard-preview-candidate/1",
        "createdAt": datetime.now(timezone.utc).isoformat(),
        "status": "PASS-SCOPED-OFFLINE",
        "candidate": {"root": str(candidate), "manifest": evidence(manifest_path), "declaredFiles": declared_files, "declaredBytes": declared_bytes, "mismatches": 0, "client": evidence(client)},
        "sdk": {"root": str(sdk), "manifest": evidence(sdk_manifest_path), "declaredFiles": sdk_files, "declaredBytes": sdk_bytes, "mismatches": 0, "contract": evidence(sdk / "sdk-contract.v1.json")},
        "sourceReceipt": evidence(receipt_path),
        "gates": [
            {"id": "initial-followup-no-export", "status": "PASS"},
            {"id": "draft-no-export", "status": "PASS"},
            {"id": "restart-draft-identity", "status": "PASS"},
            {"id": "stale-token-before-export", "status": "PASS"},
            {"id": "confirm-single-bounded-fake-export-readback", "status": "PASS"},
            {"id": "double-confirm-no-new-export", "status": "PASS"},
            {"id": "cancel-no-destination", "status": "PASS"},
            {"id": "external-effects", "status": "PASS-ZERO"},
        ],
        "readableCard": {"front": card["front"], "back": card["back"], "deck": readback["deckName"]},
        "boundary": {"fakeOnly": True, "desktopCandidateBound": False, "productionExternalE2E": False},
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--sdk", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = evaluate(args.candidate, args.sdk)
    payload = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=False)
        args.output.write_text(payload, encoding="utf-8")
    else:
        print(payload, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
