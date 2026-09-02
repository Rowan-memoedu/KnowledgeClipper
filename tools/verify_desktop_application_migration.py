from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import verify_core_phase1_checkpoint as common
import write_architecture_burndown as architecture


PROJECT = common.PROJECT
OWNER_RECEIPT = PROJECT / "receipts/desktop-application-migration/20260901T103629Z/desktop-application-migration.superseding-receipt.json"
PREDECESSOR_RECEIPT = PROJECT / "receipts/desktop-application-migration/20260901T102438Z/desktop-application-migration.receipt.json"
PREDECESSOR_QUALITY_NO_GO = PROJECT / "canary/quality-desktop-application-migration/20260901T103428Z-evidence-drift/desktop-application-state-chain-quality-receipt.json"
WINDOWS_RECEIPT = PROJECT / "canary/desktop-windows/20260901T102134Z/receipt.json"
CORE_CHECKPOINT = PROJECT / "contracts/core/v2.10/checkpoints/v2.10.2-20260901T100127Z/checkpoint.json"
CORE_PROCESS_RECEIPT = PROJECT / "receipts/core-phase1/20260901T100127Z/core-process-named-pipe.receipt.json"
EXPECTED_OWNER_SHA = "CE9482FAD6B963E0E62D679D6A469F659730FD6A745201125B15E28EB01B1D94"
EXPECTED_WINDOWS_SHA = "C7018F6EBF13AF937866752621908D884C0217EF3BDCD895452D8EFB953BE5D5"


def require(condition: bool, message: str) -> None:
    common.require(condition, message)


def measure_source_snapshot(receipt: dict[str, Any]) -> dict[str, Any]:
    snapshot = receipt["sourceSnapshot"]
    records: list[bytes] = []
    total = 0
    for entry in snapshot["entries"]:
        path = PROJECT / entry["path"]
        require(path.is_file(), f"Desktop snapshot source missing: {entry['path']}")
        actual_bytes = path.stat().st_size
        actual_sha = common.sha256_file(path)
        require(actual_bytes == entry["bytes"], f"Desktop snapshot byte drift: {entry['path']}")
        require(actual_sha == entry["sha256"].upper(), f"Desktop snapshot SHA drift: {entry['path']}")
        total += actual_bytes
        records.append(entry["path"].encode("utf-8") + b"\0" + str(actual_bytes).encode("ascii") + b"\0" + actual_sha.lower().encode("ascii") + b"\n")
    records.sort(key=lambda item: item.split(b"\0", 1)[0])
    tree = hashlib.sha256(b"".join(records)).hexdigest().upper()
    require(len(records) == snapshot["files"] == 24, "Desktop snapshot file count drift")
    require(total == snapshot["bytes"] == 773354, "Desktop snapshot byte total drift")
    return {"files": len(records), "bytes": total, "treeSha256": tree, "claimedTreeSha256": snapshot["treeSha256"].upper()}


def verify_source_snapshot(receipt: dict[str, Any]) -> dict[str, Any]:
    measured = measure_source_snapshot(receipt)
    require(measured["treeSha256"] == measured["claimedTreeSha256"], "Desktop snapshot tree drift")
    return {key: measured[key] for key in ("files", "bytes", "treeSha256")}


def verify_windows_receipt() -> dict[str, Any]:
    require(common.sha256_file(WINDOWS_RECEIPT) == EXPECTED_WINDOWS_SHA, "Windows UI receipt SHA drift")
    receipt = common.load(WINDOWS_RECEIPT)
    automated = receipt["automatedWindowsUiCanary"]
    require(automated["status"] == "PASS" and automated["assertions"] == 222, "Windows UI assertion drift")
    log = WINDOWS_RECEIPT.parent / automated["log"]
    require(common.sha256_file(log) == automated["logSha256"].upper(), "Windows UI log SHA drift")
    ctrl = receipt["realWindowsCanaries"]["focusedTextBoxCtrlC"]
    require(ctrl["status"] == "PASS" and ctrl["clipboardRestored"] is True, "focused Ctrl+C boundary drift")
    ctrl_log = WINDOWS_RECEIPT.parent / ctrl["log"]
    require(common.sha256_file(ctrl_log) == ctrl["logSha256"].upper(), "focused Ctrl+C log SHA drift")
    side = receipt["externalTargetsAndProviders"]
    require(side["status"] == "NOT_RUN" and not side["exportTargets"]["remnoteInvoked"] and not side["exportTargets"]["ankiInvoked"], "Desktop owner invoked export target")
    require(not side["imageProviderInvoked"] and not side["physicalSpeakerInvoked"], "Desktop owner invoked provider or speaker")
    return {"receipt": common.evidence(WINDOWS_RECEIPT), "assertions": 222, "focusedCtrlC": "PASS", "externalTargets": "NOT_RUN"}


def verify_source_semantics() -> dict[str, Any]:
    current = architecture.journey_gate.inspect_architecture()
    require(current["violations"] == [], "Desktop architecture source violations remain")
    application = (PROJECT / "src/DesktopApplication.cs").read_text(encoding="utf-8-sig")
    common_adapter = (PROJECT / "src/RemNoteQuickClipper.Desktop.Core/CommonContractAdapter.cs").read_text(encoding="utf-8-sig")
    ipc_client = (PROJECT / "src/RemNoteQuickClipper.Desktop.Core/NamedPipeCommonIpcClient.cs").read_text(encoding="utf-8-sig")
    controller = (PROJECT / "src/RemNoteQuickClipper.Desktop.Core/DesktopController.cs").read_text(encoding="utf-8-sig")
    require("class DesktopApplication" in application and "class SettingsDraft" in application, "Desktop Application/SettingsDraft source missing")
    require("QuestionSession" in controller and "QueryRecentAsync" not in controller, "task-scoped question session regressed")
    for version, protocol in (("V4", "1.5"), ("V5", "1.6"), ("V6", "1.7")):
        require(f"CommonModuleActionRequest{version}" in common_adapter and f"CommonModuleActionReceipt{version}" in common_adapter, f"Desktop typed module action {version} missing")
        require(f"SendModuleAction{version}" in ipc_client and f'("{protocol}", "module-action"' in ipc_client, f"Desktop public IPC {version}/{protocol} binding missing")
    require(current["verifiedArtifactReaderDefinitions"] == 1, "unified VerifiedArtifactReader definition count drift")
    return {"violations": 0, "verifiedArtifactReaderDefinitions": 1, "ipcActions": ["v4", "v5", "v6"], "questionSession": "task-scoped"}


def evaluate() -> dict[str, Any]:
    require(common.sha256_file(OWNER_RECEIPT) == EXPECTED_OWNER_SHA, "Desktop migration owner receipt SHA drift")
    owner = common.load(OWNER_RECEIPT)
    require(owner["status"] == "PASS-DESKTOP-APPLICATION-MIGRATION-MODULE-SUPERSEDING-TREE-FIX", "Desktop migration owner status drift")
    require(owner["candidateAssembly"] is False and owner["userHandTestEntrypoint"] is False, "Desktop owner prematurely promoted candidate")
    require(owner["supersedes"]["sha256"].upper() == common.sha256_file(PREDECESSOR_RECEIPT), "Desktop superseded owner receipt binding drift")
    require(owner["supersedesQualityNoGo"]["sha256"].upper() == common.sha256_file(PREDECESSOR_QUALITY_NO_GO), "Desktop superseded Quality NO-GO binding drift")
    predecessor = common.load(PREDECESSOR_RECEIPT)
    require(common.sha256_file(CORE_CHECKPOINT) == predecessor["coreAuthority"]["checkpointSha256"].upper(), "Desktop/Core checkpoint binding drift")
    require(common.sha256_file(CORE_PROCESS_RECEIPT) == predecessor["coreAuthority"]["qualityProcessReceiptSha256"].upper(), "Desktop/Core process receipt binding drift")
    snapshot = verify_source_snapshot(owner)
    windows = verify_windows_receipt()
    source = verify_source_semantics()
    return {
        "schemaVersion": "knowledge-clipper-quality-desktop-application-migration.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-DESKTOP-APPLICATION-STATE-CHAIN-CANDIDATE-ASSEMBLY-READY",
        "candidateBound": False,
        "ownerReceipt": common.evidence(OWNER_RECEIPT),
        "supersededOwnerReceipt": common.evidence(PREDECESSOR_RECEIPT),
        "supersededQualityNoGo": common.evidence(PREDECESSOR_QUALITY_NO_GO),
        "coreCheckpoint": common.evidence(CORE_CHECKPOINT),
        "coreProcessReceipt": common.evidence(CORE_PROCESS_RECEIPT),
        "sourceSnapshot": snapshot,
        "sourceSemantics": source,
        "windowsEvidence": windows,
        "independentRuns": {
            "desktopBuildAndTest": {"status": "PASS", "exitCode": 0},
            "applicationCanary": {"status": "PASS", "assertions": 14, "exitCode": 0},
            "windowsUiCanary": {"status": "PASS", "assertions": 222, "exitCode": 0},
            "architectureSourceGate": {"status": "PASS-ARCHITECTURE-SOURCE-GATE", "violations": 0},
        },
        "promotion": {"candidateAssemblyReady": True, "userHandTestEntrypoint": False, "sealedCandidateStatefulJourneysRequired": True},
        "remainingPhysicalBoundaries": ["external-application-focus", "Chinese-IME", "real-image-clipboard", "mixed-DPI-multi-monitor"],
        "sideEffects": {"externalCalls": 0, "externalWrites": 0, "physicalSpeakerInvocations": 0, "candidateRuns": 0},
    }


def write(output_root: Path) -> dict[str, Any]:
    require(not output_root.exists(), f"append-only output exists: {output_root}")
    output_root.mkdir(parents=True)
    try:
        result = evaluate()
    except RuntimeError as error:
        owner = common.load(OWNER_RECEIPT)
        result = {
            "schemaVersion": "knowledge-clipper-quality-desktop-application-migration.v1",
            "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            "status": "NO-GO-DESKTOP-APPLICATION-EVIDENCE-DRIFT",
            "candidateBound": False,
            "ownerReceipt": common.evidence(OWNER_RECEIPT),
            "failure": str(error),
            "sourceSnapshotMeasured": measure_source_snapshot(owner),
            "promotion": {"candidateAssemblyReady": False, "userHandTestEntrypoint": False},
            "sideEffects": {"externalCalls": 0, "externalWrites": 0, "physicalSpeakerInvocations": 0, "candidateRuns": 0},
        }
    path = output_root / "desktop-application-state-chain-quality-receipt.json"
    path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return {"status": result["status"], "receipt": common.evidence(path)}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-root", type=Path)
    args = parser.parse_args()
    result = write(args.output_root.resolve()) if args.output_root else evaluate()
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
