from __future__ import annotations

import hashlib
import json
import re
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from verify_settings_control_plane import scan_runtime_tree_for_secrets


PROJECT = Path(__file__).resolve().parents[1]
CANDIDATE_ID = "kc-i-module-actions-v1217-20260831T063200Z"
CANDIDATE = PROJECT / "releases" / CANDIDATE_ID / "win-x64"
DESKTOP = PROJECT / "dist/desktop-win-x64/knowledge-clipper-module-actions-v27-20260831T061200Z"
AUTHORITY = PROJECT / "releases/knowledge-clipper-authority-v1.2.17-20260831T062800Z/win-x64"
AUTHORITY_METADATA = PROJECT / "contracts/authority/worker-registry/v1.2.17-20260831T062800Z"
AUTHORITY_RECEIPT = PROJECT / "receipts/knowledge-clipper-authority-1.2.17-20260831T062800Z.json"
CORE_CHECKPOINT = PROJECT / "contracts/core/v2.7/checkpoints/v2.7.2-20260831T062500Z/checkpoint.json"
EVIDENCE = PROJECT / "canary/desktop-integrated" / CANDIDATE_ID
RUNTIME_EVIDENCE = EVIDENCE / "candidate-gates-20260831T071500Z"
PORTABLE_CLOSURE = EVIDENCE / "portable-schema-closure.json"
CHECKLIST = EVIDENCE / "USER-HAND-TEST.md"
WINDOWS_UI = PROJECT / "canary/desktop-windows/20260831T061103Z/receipt.json"
MODULE_RECEIPT = PROJECT / "receipts/desktop-module-actions-v2.7-20260831T061300Z.json"
RECEIPT = PROJECT / "receipts/desktop-integrated-module-actions-v1.2.17-20260831T072000Z.json"
PAYLOAD_EQUALITY = EVIDENCE / "desktop-payload-equality.json"
FINAL_GATE = EVIDENCE / "candidate-final-gates.json"

EXPECTED = {
    "candidateManifest": "31D9619B26EB83DD0BFD2B311FB4327F2A2670EB6CB62A6F3EA9F9F1D9946380",
    "candidateFiles": 1116,
    "candidateBytes": 437388302,
    "candidateTree": "A8A8D1B41DA30DC655CEA9F0AC9738E19BF60957FDB79ACF921025F40B62CBE4",
    "authorityFiles": 552,
    "authorityBytes": 267756432,
    "authorityTree": "90FE03EA69FBA488ADEB8E1CF46AB6C02154D9120A42DF2E05C8729C66B0E38E",
    "authorityRegistry": "7F2F9591C564D634AA6408D2B1C4C07D9876342C1B3FA11A99A23A882CF3EFA1",
    "authorityCheckpoint": "862ED5C41EC6BAA5F8B1446C9AA68DD131CEA7784D9EA8985CE54D42F2ACF95D",
    "authorityReceipt": "A754B32FE2F6D980AA70C5E8A7D37965CCA5A78677D139C4CAEE9C0F07F9D19C",
    "coreCheckpoint": "B22CA9746905AC923CBFFEF913A7CAEB3D7FF0EDDE8507145E6A7B99BBD09F65",
    "desktopReceipt": "170FB7CF2EE9144BF047F5E2665A321EFAA64A9E12D3BD740AA8E0816BBE33D9",
    "desktopSealedFiles": 551,
    "desktopSealedBytes": 169369906,
    "desktopSealedTree": "BCF51386AFC65C2567372CDC10B63475A52D2D405AA9860B869F069C42B5DA37",
    "entrypoint": "29320D1CCAA252057D011490866CC7533901740C5C6921359B7A2F9F58C4720C",
    "desktopDll": "96C5541CA79F58F88D99D86015AEF2ECB53327424726C6559FE0534571BED32F",
    "windowsUiReceipt": "9A252CCC1F639AB38830A8CD773AD7E5EB9F74FBA46A08023B37293722033F75",
}


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def rel(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def evidence(path: Path) -> dict[str, Any]:
    return {"path": rel(path), "bytes": path.stat().st_size, "sha256": sha256_file(path)}


def inventory(root: Path, exclude: set[str] | None = None) -> list[dict[str, Any]]:
    excluded = exclude or set()
    rows: list[dict[str, Any]] = []
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(root).as_posix()
        if relative in excluded:
            continue
        rows.append({"path": relative, "bytes": path.stat().st_size, "sha256": sha256_file(path)})
    return sorted(rows, key=lambda item: item["path"])


def tree(rows: list[dict[str, Any]]) -> str:
    material = "\n".join(
        f"{row['path']}\t{row['bytes']}\t{row['sha256'].lower()}" for row in rows
    )
    return hashlib.sha256(material.encode("utf-8")).hexdigest().upper()


def write_once(path: Path, value: dict[str, Any]) -> None:
    if path.exists():
        raise GateFailure(f"append-only output already exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def verify_desktop_equality() -> dict[str, Any]:
    source_rows = inventory(DESKTOP)
    require(len(source_rows) == EXPECTED["desktopSealedFiles"], "Desktop source file count drift")
    require(sum(row["bytes"] for row in source_rows) == EXPECTED["desktopSealedBytes"], "Desktop source bytes drift")
    require(tree(source_rows) == EXPECTED["desktopSealedTree"], "Desktop source tree drift")
    build_receipt_path = DESKTOP / "desktop-build-receipt.v1.json"
    require(sha256_file(build_receipt_path) == EXPECTED["desktopReceipt"], "Desktop build receipt drift")
    build_receipt = load(build_receipt_path)
    for row in build_receipt["files"]:
        source = DESKTOP / row["path"]
        require(
            source.is_file()
            and source.stat().st_size == int(row["bytes"])
            and sha256_file(source) == str(row["sha256"]).upper(),
            f"Desktop build receipt file drift: {row['path']}",
        )
    require(len(build_receipt["files"]) == int(build_receipt["fileCount"]) == 550, "Desktop build receipt count drift")
    changed: list[str] = []
    for row in source_rows:
        target = CANDIDATE / row["path"]
        if not target.is_file() or target.stat().st_size != row["bytes"] or sha256_file(target) != row["sha256"]:
            changed.append(row["path"])
    require(not changed, f"Desktop payload differs in candidate: {changed[:5]}")
    result = {
        "schemaVersion": "knowledge-clipper-desktop-payload-equality.v1",
        "status": "passed",
        "source": rel(DESKTOP),
        "candidate": rel(CANDIDATE),
        "filesCompared": len(source_rows),
        "totalBytes": sum(row["bytes"] for row in source_rows),
        "treeSha256": tree(source_rows),
        "changedFiles": 0,
        "missingFiles": 0,
        "extraDesktopPayloadFiles": 0,
        "entrypointSha256": sha256_file(CANDIDATE / "KnowledgeClipper.exe"),
        "applicationDllSha256": sha256_file(CANDIDATE / "KnowledgeClipper.dll"),
    }
    require(result["entrypointSha256"] == EXPECTED["entrypoint"], "entrypoint drift")
    require(result["applicationDllSha256"] == EXPECTED["desktopDll"], "Desktop DLL drift")
    write_once(PAYLOAD_EQUALITY, result)
    return result


def verify_candidate_manifest() -> dict[str, Any]:
    path = CANDIDATE / "candidate-file-manifest.v1.json"
    require(sha256_file(path) == EXPECTED["candidateManifest"], "candidate manifest SHA drift")
    manifest = load(path)
    observed_payload = inventory(CANDIDATE, {path.name})
    declared = manifest["payload"]["files"]
    normalized = [
        {"path": row["path"], "bytes": int(row["bytes"]), "sha256": str(row["sha256"]).upper()}
        for row in declared
    ]
    require(normalized == observed_payload, "candidate manifest file inventory drift")
    require(len(observed_payload) == int(manifest["payload"]["fileCount"]), "candidate payload file count drift")
    require(sum(row["bytes"] for row in observed_payload) == int(manifest["payload"]["totalBytes"]), "candidate payload bytes drift")
    require(tree(observed_payload) == str(manifest["payload"]["treeSha256"]).upper(), "candidate payload tree drift")
    sealed = inventory(CANDIDATE)
    require(len(sealed) == EXPECTED["candidateFiles"], "candidate sealed file count drift")
    require(sum(row["bytes"] for row in sealed) == EXPECTED["candidateBytes"], "candidate sealed bytes drift")
    require(tree(sealed) == EXPECTED["candidateTree"], "candidate sealed tree drift")
    return {
        "status": "passed",
        "manifest": evidence(path),
        "payload": {"fileCount": len(observed_payload), "totalBytes": sum(row["bytes"] for row in observed_payload), "treeSha256": tree(observed_payload)},
        "sealed": {"fileCount": len(sealed), "totalBytes": sum(row["bytes"] for row in sealed), "treeSha256": tree(sealed)},
    }


def verify_authority() -> dict[str, Any]:
    source_rows = inventory(AUTHORITY)
    embedded_rows = inventory(CANDIDATE / "authority/win-x64")
    require(source_rows == embedded_rows, "embedded authority differs from admitted v1.2.17 release")
    require(len(embedded_rows) == EXPECTED["authorityFiles"], "authority file count drift")
    require(sum(row["bytes"] for row in embedded_rows) == EXPECTED["authorityBytes"], "authority bytes drift")
    require(tree(embedded_rows) == EXPECTED["authorityTree"], "authority tree drift")
    checkpoint = AUTHORITY_METADATA / "checkpoint.json"
    registry = AUTHORITY_METADATA / "worker-registry.v1.json"
    require(sha256_file(checkpoint) == EXPECTED["authorityCheckpoint"], "authority checkpoint drift")
    require(sha256_file(registry) == EXPECTED["authorityRegistry"], "authority registry drift")
    require(sha256_file(AUTHORITY_RECEIPT) == EXPECTED["authorityReceipt"], "authority receipt drift")
    require(sha256_file(CORE_CHECKPOINT) == EXPECTED["coreCheckpoint"], "Core checkpoint drift")
    require(sha256_file(CANDIDATE / "authority/metadata/checkpoint.json") == EXPECTED["authorityCheckpoint"], "embedded checkpoint drift")
    require(sha256_file(CANDIDATE / "authority/metadata/worker-registry.v1.json") == EXPECTED["authorityRegistry"], "embedded registry drift")
    require(sha256_file(CANDIDATE / "authority/metadata/knowledge-clipper-authority-1.2.17-20260831T062800Z.json") == EXPECTED["authorityReceipt"], "embedded authority receipt drift")
    integration = load(CANDIDATE / "candidate-integration.json")
    require(integration["PipeName"] == "KnowledgeClipper.Integrated.v1217", "candidate pipe drift")
    require(integration["IpcConfig"] == "authority/metadata/orchestrator-ipc.candidate.v1.2.json", "candidate IPC config drift")
    require(integration["AuthorityTreeSha256"] == EXPECTED["authorityTree"], "candidate authority binding drift")
    return {
        "status": "passed",
        "version": "1.2.17",
        "fileCount": len(embedded_rows),
        "totalBytes": sum(row["bytes"] for row in embedded_rows),
        "treeSha256": tree(embedded_rows),
        "checkpoint": evidence(checkpoint),
        "registry": evidence(registry),
        "authorityReceipt": evidence(AUTHORITY_RECEIPT),
        "coreCheckpoint": evidence(CORE_CHECKPOINT),
        "embeddedCopiedExactly": True,
        "ipcProtocol": "1.2",
    }


def verify_runtime_and_contracts() -> dict[str, Any]:
    startup_path = RUNTIME_EVIDENCE / "startup-diagnostic.stdout.log"
    registry_path = RUNTIME_EVIDENCE / "authority-validate-registry.log"
    integration_path = RUNTIME_EVIDENCE / "integration-startup-canary.stdout.log"
    single_path = RUNTIME_EVIDENCE / "single-instance-diagnostic.stdout.log"
    unit_path = RUNTIME_EVIDENCE / "desktop-integration-canary.log"
    startup = load(startup_path)
    registry = load(registry_path)
    integration = load(integration_path)
    single = load(single_path)
    require(startup["result"] == "PASS" and startup["externalConnections"] == 0 and startup["namedPipeInvoked"] is False, "safe startup failed")
    require(registry["status"] == "valid" and registry["registryVersion"] == "1.2.17" and registry["workers"] == 5, "registry validation failed")
    require(integration["result"] == "PASS" and integration["namedPipeInvoked"] is True and integration["externalWorkerInvoked"] is False, "Named Pipe query failed")
    require(all(integration[name] is False for name in ("physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")), "forbidden integration side effect")
    require(single["userFeedbackMode"] == "activate-existing-window-and-message-box", "single-instance feedback regression")
    unit = unit_path.read_text(encoding="utf-8")
    require("DESKTOP_INTEGRATION_CANARY PASS assertions=8" in unit and "existing canonical mutex is detected" in unit, "isolated single-instance regression missing")
    closure = load(PORTABLE_CLOSURE)
    require(closure == {"schemaVersion": "knowledge-clipper-portable-schema-closure-gate.v1", "status": "passed", "releaseRoot": ".", "schemasChecked": 62, "localRefsChecked": 188, "failures": []}, "portable schema closure drift")
    require((CANDIDATE / "authority/win-x64/contracts/v1/schemas/common/artifact.schema.json").is_file(), "candidate frozen common artifact schema missing")
    windows = load(WINDOWS_UI)
    require(sha256_file(WINDOWS_UI) == EXPECTED["windowsUiReceipt"], "Windows UI receipt drift")
    require(windows["automatedWindowsUiCanary"] == {"status": "PASS", "assertions": 180, "log": "windows-ui-automation.log", "logSha256": "854752473ce9b4507bbb725e95465f542d48ea118a92a807bf20efff6a165a02"}, "Windows UI evidence drift")
    source = (PROJECT / "src/Program.cs").read_text(encoding="utf-8")
    block = source[source.index("internal static class ProductionStartupPolicy"):source.index("internal enum ReviewChoice")]
    require(not re.search(r"JobStore\.PurgeExpired\s*\(", block), "production startup invokes legacy purge")
    return {
        "status": "passed",
        "safeStartup": evidence(startup_path),
        "registry5of5": evidence(registry_path),
        "namedPipeQuery": evidence(integration_path),
        "singleInstance": evidence(single_path),
        "isolatedSingleInstanceRegression": evidence(unit_path),
        "portableSchemaClosure": evidence(PORTABLE_CLOSURE),
        "schemasChecked": 62,
        "localRefsChecked": 188,
        "windowsUi": evidence(WINDOWS_UI),
        "windowsUiAssertions": 180,
        "productionRetention": "disabled-until-state-aware-retention-service",
    }


def verify_safety() -> dict[str, Any]:
    scan = scan_runtime_tree_for_secrets(CANDIDATE)
    require(scan["status"] == "passed" and scan["findingCount"] == 0, "candidate secret scan failed")
    credential_files = [
        path.relative_to(CANDIDATE).as_posix()
        for path in CANDIDATE.rglob("*")
        if path.is_file() and path.name.casefold() in {"credentials.json", "credentials.json.bak", ".env"}
    ]
    require(not credential_files, f"candidate includes credential files: {credential_files}")
    return {
        "status": "passed",
        "secretScan": scan,
        "credentialFiles": credential_files,
        "externalWorkerInvoked": False,
        "externalProductionApisInvoked": False,
        "physicalSpeakerInvoked": False,
        "remNoteInvoked": False,
        "ankiInvoked": False,
        "mineruInvoked": False,
    }


def main() -> int:
    for required in (CANDIDATE, DESKTOP, AUTHORITY, AUTHORITY_METADATA, AUTHORITY_RECEIPT, CORE_CHECKPOINT, PORTABLE_CLOSURE, CHECKLIST, WINDOWS_UI, MODULE_RECEIPT):
        require(required.exists(), f"required input missing: {required}")
    require(not RECEIPT.exists() and not PAYLOAD_EQUALITY.exists() and not FINAL_GATE.exists(), "append-only output already exists")
    equality = verify_desktop_equality()
    manifest = verify_candidate_manifest()
    authority = verify_authority()
    runtime = verify_runtime_and_contracts()
    safety = verify_safety()
    final_gate = {
        "schemaVersion": "knowledge-clipper-integrated-desktop-final-gates.v1",
        "status": "passed",
        "candidateId": CANDIDATE_ID,
        "desktopPayloadEquality": evidence(PAYLOAD_EQUALITY),
        "candidateManifest": manifest,
        "authority": authority,
        "runtimeAndContracts": runtime,
        "safety": safety,
    }
    write_once(FINAL_GATE, final_gate)
    receipt = {
        "schemaVersion": "knowledge-clipper-integrated-desktop-candidate-receipt.v1",
        "createdAt": datetime.now(timezone.utc).isoformat(),
        "status": "PASS-AUTOMATED-USER-HAND-TEST-READY",
        "candidateId": CANDIDATE_ID,
        "productName": "零碎知识剪藏",
        "userHandTestEntrypoint": True,
        "entrypoint": rel(CANDIDATE / "KnowledgeClipper.exe"),
        "entrypointSha256": EXPECTED["entrypoint"],
        "applicationDllSha256": EXPECTED["desktopDll"],
        "candidate": manifest,
        "desktopPayloadEquality": evidence(PAYLOAD_EQUALITY),
        "desktopChangedFiles": equality["changedFiles"],
        "authority": authority,
        "corePublicModuleActions": {
            "protocolVersion": "1.2",
            "operations": ["speech.voices", "speech.preview", "remnote.probe"],
            "ocrHumanResolution": ["accept_original", "submit_correction", "cancel"],
            "originalToAnki": "ordered-original-images.v1",
            "remnoteOriginalMedia": "unsupported:mcp.media_write_unavailable",
        },
        "automatedGates": evidence(FINAL_GATE),
        "moduleReceipt": evidence(MODULE_RECEIPT),
        "userHandTestChecklist": evidence(CHECKLIST),
        "safety": safety,
        "boundaries": [
            "external application selection and browser/editor context remain a physical Windows hand-test boundary",
            "cross-monitor mixed-DPI drag/resize and physical screenshot drag remain hand-test boundaries",
            "speech.preview may produce physical audio only after the user explicitly clicks Preview",
            "RemNote original-media remains unsupported with mcp.media_write_unavailable",
            "state-aware retention service is not implemented; legacy production purge remains disabled",
            "real external provider/export outcomes depend on the user's local configuration and are not part of this automated admission",
        ],
    }
    write_once(RECEIPT, receipt)
    print(json.dumps({
        "status": receipt["status"],
        "candidate": rel(CANDIDATE),
        "entrypoint": receipt["entrypoint"],
        "candidateTreeSha256": manifest["sealed"]["treeSha256"],
        "manifestSha256": manifest["manifest"]["sha256"],
        "desktopChangedFiles": equality["changedFiles"],
        "authorityTreeSha256": authority["treeSha256"],
        "receipt": evidence(RECEIPT),
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (GateFailure, OSError, ValueError, KeyError, json.JSONDecodeError) as exc:
        print(json.dumps({"status": "FAIL", "error": str(exc)}, ensure_ascii=False))
        raise SystemExit(1)
