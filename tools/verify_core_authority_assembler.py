from __future__ import annotations

import argparse
import json
import re
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import verify_core_phase1_checkpoint as common


PROJECT = common.PROJECT
DEFAULT_TOOL = PROJECT / "tools/New-KnowledgeClipperCoreAuthorityRelease.ps1"
DEFAULT_CHECKPOINT = PROJECT / "contracts/core/v2.10/checkpoints/v2.10.2-20260901T100127Z/checkpoint.json"
DEFAULT_QUALIFICATION = PROJECT / "receipts/tool-qualification/20260901T102207Z/core-authority-release-tool.qualification.json"


def require(condition: bool, message: str) -> None:
    common.require(condition, message)


def evidence(path: Path) -> dict[str, Any]:
    resolved = path.resolve()
    try:
        relative = resolved.relative_to(PROJECT.resolve()).as_posix()
    except ValueError:
        relative = f"external-test-fixture/{resolved.name}"
    return {"path": relative, "bytes": resolved.stat().st_size, "sha256": common.sha256_file(resolved)}


def inspect_source(path: Path = DEFAULT_TOOL) -> dict[str, Any]:
    text = path.read_text(encoding="utf-8-sig")
    required_parameters = {
        "CoreContractRoot",
        "CoreCheckpointPath",
        "ExpectedCoreCheckpointSha256",
        "ExpectedIpcProtocolVersion",
        "ExpectedRequestContract",
        "ExpectedReceiptContract",
        "PreflightOnly",
    }
    parameters = set(re.findall(r"\$(\w+)", text[: text.find("$ErrorActionPreference")]))
    missing = sorted(required_parameters - parameters)
    stale_literals = []
    for pattern, label in (
        (r"contracts[\\/]core[\\/]v2\.3[\\/]playback-control", "hardcoded-core-v2.3"),
        (r"ipcProtocolVersion\s*=\s*['\"]1\.1['\"]", "hardcoded-ipc-1.1"),
        (r"receiptSchemaVersion\s*=\s*['\"]2\.3\.0['\"]", "hardcoded-receipt-2.3.0"),
    ):
        if re.search(pattern, text, re.IGNORECASE):
            stale_literals.append(label)

    invariants = {
        "checkpointHashVerifiedBeforeDescribe": text.find("Get-Sha256 $CoreCheckpointPath") < text.find("$coreCheckpoint = Read-Json"),
        "checkpointStatusRequiredPassed": "StartsWith('passed'" in text,
        "fileBindingsRequiredAndRehashed": "$coreCheckpoint.fileBindings" in text and "Core checkpoint file binding drifted" in text,
        "ipcDerivedFromCheckpoint": "$ipc = $coreCheckpoint.authority.ipc" in text,
        "checkpointBoundIpcConfiguration": "IPC configuration is not checkpoint-bound" in text,
        "checkpointBoundRequestReceiptSchemas": "Checkpoint-bound request contract schema" in text and "Checkpoint-bound receipt contract schema" in text,
        "appendOnlyTargets": "Append-only target already exists" in text,
        "preflightExitsBeforeCopy": text.find("if ($PreflightOnly)") < text.find("Copy-Directory $BaseReleaseRoot"),
        "stagingCleanupScoped": "Remove-VerifiedStagingDirectory" in text,
        "authorityOnlyRebaseRejected": "Base release unexpectedly contains Core contract root" in text,
        "packagesOnlyCheckpointBoundFiles": "foreach ($contract in $packageBindings)" in text and "Copy-Directory $CoreContractRoot" not in text,
    }
    return {
        "path": evidence(path),
        "requiredParameters": sorted(required_parameters),
        "missingParameters": missing,
        "staleLiterals": stale_literals,
        "invariants": invariants,
        "sourceReady": not missing and not stale_literals and all(invariants.values()),
    }


def verify_preflight(receipt_path: Path, checkpoint_path: Path = DEFAULT_CHECKPOINT) -> dict[str, Any]:
    receipt = common.load(receipt_path)
    checkpoint = common.load(checkpoint_path)
    expected_sha = common.sha256_file(checkpoint_path)
    require(receipt.get("schemaVersion") == "knowledge-clipper-core-authority-tool-preflight.v1", "authority preflight schema drift")
    require(receipt.get("status") == "passed", "authority preflight did not pass")
    require(receipt.get("checkpointVersion") == checkpoint["checkpointVersion"], "authority preflight checkpoint version drift")
    require(receipt.get("checkpointSha256", "").upper() == expected_sha, "authority preflight checkpoint SHA drift")
    ipc = receipt.get("ipc") or {}
    expected_ipc = checkpoint["authority"]["ipc"]
    require(ipc.get("protocolVersion") == expected_ipc["protocolVersion"], "authority preflight IPC protocol drift")
    require(ipc.get("schemaVersion") == expected_ipc["schemaVersion"], "authority preflight IPC schema drift")
    require(ipc.get("requestContract") == expected_ipc["request"], "authority preflight request contract drift")
    require(ipc.get("receiptContract") == expected_ipc["receipt"], "authority preflight receipt contract drift")
    require(receipt.get("boundFiles") == len(checkpoint["fileBindings"]), "authority preflight binding count drift")
    return {"receipt": evidence(receipt_path), "checkpoint": common.evidence(checkpoint_path), "validated": True}


def verify_qualification(receipt_path: Path = DEFAULT_QUALIFICATION) -> dict[str, Any]:
    receipt = common.load(receipt_path)
    require(receipt.get("schemaVersion") == "knowledge-clipper-core-authority-tool-qualification.v1", "authority qualification schema drift")
    require(receipt.get("status") == "passed", "authority qualification did not pass")
    require(receipt["tool"]["path"] == "tools/New-KnowledgeClipperCoreAuthorityRelease.ps1", "authority qualification tool path drift")
    require(receipt["tool"]["sha256"].upper() == common.sha256_file(DEFAULT_TOOL), "authority qualification tool SHA drift")
    qualification_path = PROJECT / receipt["qualification"]["path"]
    require(receipt["qualification"]["sha256"].upper() == common.sha256_file(qualification_path), "authority qualification harness SHA drift")
    require(receipt["checkpoint"]["sha256"].upper() == common.sha256_file(DEFAULT_CHECKPOINT), "authority qualification checkpoint SHA drift")
    require(receipt["qualification"]["externalServicesCalled"] == 0, "authority qualification called external service")
    require(receipt["qualification"]["authorityTargetsCreated"] == receipt["qualification"]["candidateTargetsCreated"] == 0, "authority qualification created a release target")
    cases = {item["name"]: item for item in receipt["cases"]}
    expected = {
        "v2.10.2-derived-metadata": "passed-accepted",
        "independent-synthetic-v9.9": "passed-accepted",
        "old-ipc-1.1": "passed-rejected",
        "old-receipt-2.3.0": "passed-rejected",
        "checkpoint-binding-drift": "passed-rejected",
        "unknown-receipt-metadata": "passed-rejected",
    }
    require({name: cases.get(name, {}).get("status") for name in expected} == expected, "authority qualification case/status drift")
    current = cases["v2.10.2-derived-metadata"]["evidence"]
    checkpoint = common.load(DEFAULT_CHECKPOINT)
    require(current["checkpointSha256"].upper() == common.sha256_file(DEFAULT_CHECKPOINT), "qualification current checkpoint SHA drift")
    require(current["contractRoot"] == current["releaseContractRoot"] == "contracts/core/v2.10", "qualification release contract root drift")
    require(current["ipc"]["protocolVersion"] == checkpoint["authority"]["ipc"]["protocolVersion"], "qualification current IPC drift")
    require(current["ipc"]["requestContract"] == checkpoint["authority"]["ipc"]["request"], "qualification current request drift")
    require(current["ipc"]["receiptContract"] == checkpoint["authority"]["ipc"]["receipt"], "qualification current receipt drift")
    require(current["packagedBoundFiles"] < current["boundFiles"], "qualification did not distinguish package bindings from all bindings")
    synthetic = cases["independent-synthetic-v9.9"]["evidence"]
    require(synthetic["checkpointVersion"] == "9.9.0" and synthetic["ipc"]["protocolVersion"] == "9.9", "qualification synthetic checkpoint is not independent")
    return {"receipt": common.evidence(receipt_path), "harness": common.evidence(qualification_path), "cases": expected, "validated": True}


def evaluate(preflight_receipt: Path | None = None, qualification_receipt: Path | None = None) -> dict[str, Any]:
    source = inspect_source()
    require(source["sourceReady"], "Core authority assembler source is not checkpoint-driven and release-safe")
    preflight = verify_preflight(preflight_receipt) if preflight_receipt else None
    qualification = verify_qualification(qualification_receipt) if qualification_receipt else None
    tool_ready = preflight is not None or qualification is not None
    return {
        "schemaVersion": "knowledge-clipper-quality-core-authority-assembler-gate.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-ASSEMBLER-QUALIFIED-AUTHORITY-ASSEMBLY-NOT-RUN" if qualification else ("PASS-ASSEMBLER-SOURCE-PREFLIGHT-VALIDATED" if preflight else "CONDITIONAL-ASSEMBLER-SOURCE-READY-PREFLIGHT-PENDING"),
        "candidateBound": False,
        "source": source,
        "preflight": preflight,
        "qualification": qualification,
        "releaseDecision": {
            "authorityAssemblyAllowed": tool_ready,
            "candidateAssemblyAllowed": False,
            "reason": "Desktop owner checkpoint and exact candidate inputs are pending; source readiness alone cannot build authority or candidate.",
        },
        "sideEffects": {"authorityWrites": 0, "candidateWrites": 0, "workerRuns": 0, "externalCalls": 0},
    }


def write(output_root: Path, preflight_receipt: Path | None = None, qualification_receipt: Path | None = None) -> dict[str, Any]:
    require(not output_root.exists(), f"append-only output exists: {output_root}")
    output_root.mkdir(parents=True)
    result = evaluate(preflight_receipt, qualification_receipt)
    path = output_root / "core-authority-assembler-quality-receipt.json"
    path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return {"status": result["status"], "receipt": common.evidence(path)}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-root", type=Path)
    parser.add_argument("--preflight-receipt", type=Path)
    parser.add_argument("--qualification-receipt", type=Path)
    args = parser.parse_args()
    preflight = args.preflight_receipt.resolve() if args.preflight_receipt else None
    qualification = args.qualification_receipt.resolve() if args.qualification_receipt else None
    result = write(args.output_root.resolve(), preflight, qualification) if args.output_root else evaluate(preflight, qualification)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
