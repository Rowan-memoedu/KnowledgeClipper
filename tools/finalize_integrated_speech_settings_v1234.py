#!/usr/bin/env python3
"""Seal the internal v1.2.34 Desktop/Speech settings integrated candidate."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
from pathlib import Path
from typing import Any

from assemble_integrated_desktop_candidate import inventory


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def load(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def evidence(path: Path, project: Path) -> dict[str, Any]:
    return {"path": path.resolve().relative_to(project.resolve()).as_posix(), "bytes": path.stat().st_size, "sha256": sha(path)}


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def inventory_matches(actual: dict[str, Any], expected: dict[str, Any], label: str) -> None:
    for key in ("fileCount", "totalBytes", "treeSha256"):
        require(str(actual[key]).upper() == str(expected[key]).upper(), f"{label} {key} mismatch")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project-root", type=Path, required=True)
    parser.add_argument("--candidate-root", type=Path, required=True)
    parser.add_argument("--evidence-root", type=Path, required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    args = parser.parse_args()

    project = args.project_root.resolve()
    candidate = args.candidate_root.resolve()
    runtime = args.evidence_root.resolve() / "runtime"
    receipt_path = args.receipt.resolve()
    if receipt_path.exists():
        raise SystemExit(f"append-only receipt already exists: {receipt_path}")

    desktop = project / "dist/desktop-win-x64/knowledge-clipper-speech-settings-actions-20260901T054000Z"
    desktop_receipt_path = desktop / "desktop-build-receipt.v1.json"
    desktop_receipt = load(desktop_receipt_path)
    require(sha(desktop_receipt_path) == "7BDA26E81183B68A38BC02B7FE2EC89B1101A093F2B37AE6E244D099E4F5174A", "Desktop receipt drift")
    require(desktop_receipt["fileCount"] == 645 and desktop_receipt["totalBytes"] == 169755299 and desktop_receipt["treeSha256"].upper() == "327E59CC366F92FBF426319AAC1F5D1F355459B18A797419D5E4A6D705CEE6FE", "Desktop aggregate drift")
    changed: list[str] = []
    for entry in desktop_receipt["files"]:
        source = desktop / entry["path"]
        target = candidate / entry["path"]
        if not target.is_file() or source.stat().st_size != target.stat().st_size or sha(source) != sha(target) or sha(target) != entry["sha256"].upper():
            changed.append(entry["path"])
    require(changed == [], f"Desktop payload equality failed: {changed[:5]}")
    require(sha(candidate / "desktop-build-receipt.v1.json") == sha(desktop_receipt_path), "packaged Desktop receipt drift")

    manifest_path = candidate / "candidate-file-manifest.v1.json"
    manifest = load(manifest_path)
    require(manifest["candidateId"] == "kc-i-speech-settings-v1234-20260901T060000Z", "candidate identity drift")
    payload = inventory(candidate, {manifest_path.name})
    sealed = inventory(candidate)
    inventory_matches(payload, manifest["payload"], "candidate payload")
    require(manifest["desktop"]["fileCount"] == 645 and manifest["desktop"]["treeSha256"] == "327E59CC366F92FBF426319AAC1F5D1F355459B18A797419D5E4A6D705CEE6FE", "manifest Desktop binding drift")

    authority_source = project / "releases/knowledge-clipper-authority-v1.2.34-20260901T043500Z/win-x64"
    authority_embedded = candidate / "authority/win-x64"
    source_authority = inventory(authority_source)
    embedded_authority = inventory(authority_embedded)
    inventory_matches(embedded_authority, source_authority, "embedded authority")
    require(source_authority["fileCount"] == 386 and source_authority["totalBytes"] == 261873123 and source_authority["treeSha256"] == "F73ED8E39B5120D04EF332671CD7DF4F1131C7715ECFE1081517604C2107EFDA", "authority aggregate drift")

    authority_meta = project / "contracts/authority/worker-registry/v1.2.34-20260901T043500Z"
    authority_receipt = project / "receipts/knowledge-clipper-authority-1.2.34-20260901T043600Z.json"
    core_checkpoint = project / "contracts/core/v2.7/checkpoints/v2.7.20-20260901T043300Z/checkpoint.json"
    exact = {
        authority_meta / "checkpoint.json": "3748DA2DDCD4A3E5F65BA5E90AEBE470339A8D8AE91D458AD9B0A95DCD2F2918",
        authority_meta / "worker-registry.v1.json": "96966ADD1BE8B8CDA9D456DCBC3E2860B89F85C752330A0F38AF2A05C6AA71A5",
        authority_meta / "dependency-manifest.v1.json": "A2E7523EC14E8458993C70156653245F4CBBCED98CC62DABD9BAF645569EF26D",
        authority_meta / "portable-schema-closure-gate.v1.json": "CAE3B081A8E79D3264CC0A01FFFC4E1C1CEA74D60D03BD1C45A7206687F1D53D",
        authority_receipt: "047F33677EE2EA17309C34F022C37240745242F46B83CF7D1E723A2CCF47BFCD",
        core_checkpoint: "35250DE307647B496652749A2C70797CA9760E118AAAB18C4C7A50F6D07B47E3",
    }
    for path, expected in exact.items():
        require(sha(path) == expected, f"authority/Core evidence drift: {path}")
    integration = load(candidate / "candidate-integration.json")
    require(integration["AuthorityCheckpointId"] == "knowledge-clipper-authority-1.2.34-20260901T043600Z" and integration["AuthorityTreeSha256"] == source_authority["treeSha256"], "candidate integration authority drift")
    require(integration["AutoStart"] is True and integration["PipeName"].startswith("KnowledgeClipper.InternalV1234."), "candidate IPC/autostart drift")

    startup_path = runtime / "startup.stdout.json"
    registry_path = runtime / "registry.stdout.json"
    query_path = runtime / "integration-query.stdout.json"
    authority_closure_path = runtime / "authority-schema-closure.json"
    desktop_closure_path = runtime / "desktop-automation-closure.json"
    secret_path = runtime / "secret-scan.json"
    startup, registry, query = load(startup_path), load(registry_path), load(query_path)
    authority_closure, desktop_closure, secret = load(authority_closure_path), load(desktop_closure_path), load(secret_path)
    require(startup["result"] == "PASS" and startup["packageFilesVerified"] and startup["packageTreeVerified"] and startup["candidateOrchestratorPathsVerified"] and startup["externalConnections"] == 0, "safe startup failed")
    require(registry["status"] == "valid" and registry["registryVersion"] == "1.2.34" and registry["workers"] == 5, "registry gate failed")
    require(query["result"] == "PASS" and query["orchestratorStartedByDesktop"] and query["namedPipeInvoked"] and query["authorityTreeSha256"] == source_authority["treeSha256"], "Named Pipe query gate failed")
    require(not any(query[key] for key in ("externalWorkerInvoked", "physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")), "query side-effect boundary failed")
    require(authority_closure["status"] == "passed" and authority_closure["schemasChecked"] == 63 and authority_closure["localRefsChecked"] == 188, "authority recursive closure failed")
    require(desktop_closure["status"] == "passed" and desktop_closure["schemasChecked"] == 25 and desktop_closure["validFixturesAccepted"] == 53 and desktop_closure["invalidFixturesRejected"] == 14, "Desktop contract closure failed")
    require(secret["status"] == "passed" and secret["findingCount"] == 0 and secret["secretValuesEmitted"] is False, "secret gate failed")

    module_receipt = project / "receipts/desktop-speech-settings-actions-20260901T054000Z.json"
    quality_receipt = project / "canary/product-gap-audit/20260901T040352Z-speech-settings-module-admission/quality-increment.json"
    windows_receipt = project / "canary/desktop-windows/speech-settings-actions-20260901T053000Z/receipt.json"
    windows_log = windows_receipt.parent / "windows-ui-automation.log"
    require(sha(module_receipt) == "33741C46BF62E7BDAB3D196B0E6896A8733DD7F562D0C8291CAD36AFD7A603EF", "Speech module owner receipt drift")
    require(sha(quality_receipt) == "6B1A98E26FF97A492F017E7414B1476D3F8225AFC943DD7A78640951E606B32B", "Speech Quality receipt drift")
    require(sha(windows_receipt) == "F45A6A5D4CCE7A5A1044FB31E655ACBB9FFA9EF902507906A84545F0CCC716B6" and load(windows_receipt)["automatedWindowsUiCanary"]["assertions"] == 193, "Speech UI receipt drift")
    ui_text = windows_log.read_text(encoding="utf-8-sig")
    for marker in (
        "PASS voice discovery caches one revision/hash-bound catalog without implicit audio preview",
        "PASS one explicit preview button click dispatches exactly one physical-audio module action",
        "PASS settings reopen verifies and restores the persisted voice catalog snapshot revision and SHA without provider access",
        "PASS settings save persists next-task values without implicit voice discovery or physical playback",
        "PASS voice discovery, preview, and RemNote probe use only public module-action IPC and create zero knowledge task commands",
    ):
        require(marker in ui_text, marker)

    result = {
        "schemaVersion": "knowledge-clipper-integrated-desktop-speech-settings-v1.2.34-receipt.v1",
        "createdAt": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "PASS-INTERNAL-CANDIDATE-BOUND-SPEECH-SETTINGS-V1.2.34",
        "candidateId": manifest["candidateId"],
        "entrypoint": (candidate / "KnowledgeClipper.exe").relative_to(project).as_posix(),
        "entrypointSha256": sha(candidate / "KnowledgeClipper.exe"),
        "applicationDllSha256": sha(candidate / "KnowledgeClipper.dll"),
        "candidate": {"manifest": evidence(manifest_path, project), "payload": {key: payload[key] for key in ("fileCount", "totalBytes", "treeSha256")}, "sealed": {key: sealed[key] for key in ("fileCount", "totalBytes", "treeSha256")}},
        "desktopPayloadEquality": {"source": desktop.relative_to(project).as_posix(), "filesCompared": 645, "changedFiles": 0, "treeSha256": desktop_receipt["treeSha256"].upper(), "buildReceipt": evidence(desktop_receipt_path, project)},
        "authority": {"version": "1.2.34", "embeddedCopiedExactly": True, "fileCount": source_authority["fileCount"], "totalBytes": source_authority["totalBytes"], "treeSha256": source_authority["treeSha256"], "checkpoint": evidence(authority_meta / "checkpoint.json", project), "registry": evidence(authority_meta / "worker-registry.v1.json", project), "authorityReceipt": evidence(authority_receipt, project), "coreCheckpoint": evidence(core_checkpoint, project)},
        "speechSettings": {"moduleReceipt": evidence(module_receipt, project), "qualityAdmission": evidence(quality_receipt, project), "windowsUi": evidence(windows_receipt, project), "windowsUiAssertions": 193, "catalogCacheHashBound": True, "previewExplicitSingleAction": True, "implicitPlaybackOnSaveOpenStatus": 0, "knowledgeTasksCreated": 0},
        "automatedGates": {"safeStartup": evidence(startup_path, project), "registry5Of5": evidence(registry_path, project), "namedPipeQueryRecent": evidence(query_path, project), "authorityRecursiveClosure": evidence(authority_closure_path, project), "desktopAutomationClosure": evidence(desktop_closure_path, project), "secretScan": evidence(secret_path, project)},
        "safety": {"externalServicesInvoked": 0, "workerRuns": 0, "destinationWrites": 0, "physicalSpeakerInvocations": 0, "existingUserProcessTerminated": False, "automaticRetentionInvocation": False},
        "promotion": {"userHandTestEntrypoint": False, "finalUniqueEntrypoint": False, "v1233UserCandidateModified": False, "reason": "internal append-only candidate-bound module gate; wait for current v1.2.33 hand-test feedback"},
    }
    receipt_path.parent.mkdir(parents=True, exist_ok=True)
    with receipt_path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    print(json.dumps({"status": result["status"], "candidateRoot": str(candidate), "manifestSha256": sha(manifest_path), "payloadTreeSha256": payload["treeSha256"], "sealedTreeSha256": sealed["treeSha256"], "receipt": str(receipt_path), "receiptSha256": sha(receipt_path)}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
