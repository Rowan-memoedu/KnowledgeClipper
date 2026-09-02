from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from verify_settings_control_plane import scan_runtime_tree_for_secrets


PROJECT = Path(__file__).resolve().parents[1]
CANDIDATE_ID = "kc-i-user-feedback-fixes-v1218-20260831T093000Z"
CANDIDATE = PROJECT / "releases" / CANDIDATE_ID / "win-x64"
DESKTOP = PROJECT / "dist/desktop-win-x64/knowledge-clipper-user-feedback-fixes-v1218-20260831T091500Z"
AUTHORITY = PROJECT / "releases/knowledge-clipper-authority-v1.2.18-20260831T085000Z/win-x64"
AUTHORITY_METADATA = PROJECT / "contracts/authority/worker-registry/v1.2.18-20260831T085000Z"
AUTHORITY_RECEIPT = PROJECT / "receipts/knowledge-clipper-authority-1.2.18-20260831T085000Z.json"
CORE_CHECKPOINT = PROJECT / "contracts/core/v2.7/checkpoints/v2.7.3-20260831T084500Z/checkpoint.json"
EVIDENCE = PROJECT / "canary/desktop-integrated" / CANDIDATE_ID
RUNTIME = EVIDENCE / "candidate-gates-20260831T101500Z"
VISUAL = EVIDENCE / "visual-preview-20260831T102000Z"
WINDOWS = PROJECT / "canary/desktop-windows/user-feedback-fixes-20260831T090000Z/receipt.json"
CHECKLIST = EVIDENCE / "USER-HAND-TEST.md"
PAYLOAD_EQUALITY = EVIDENCE / "desktop-payload-equality.json"
FINAL_GATE = EVIDENCE / "candidate-final-gates.json"
RECEIPT = PROJECT / "receipts/desktop-integrated-user-feedback-v1.2.18-20260831T103000Z.json"

EXPECTED = {
    "manifest": "34E49313A5308D55CB20FF3CBE3C5EC63A963C2EF1D338BCAA894B6F0F6635FA",
    "payloadFiles": 1123,
    "payloadBytes": 437513389,
    "payloadTree": "3B41A84D06B128CCE30BF5987EA58F169AE703F4BB84A1A515DB69028A5CD307",
    "sealedFiles": 1124,
    "sealedBytes": 437750916,
    "sealedTree": "F656C7FA77F4A39F78AB205DEA7E544A8D57FC9BFF0DEA0B32EABE5B6C4E752E",
    "desktopFiles": 555,
    "desktopBytes": 169686727,
    "desktopTree": "04F911EF9BAEED20BF2927DAD7659C8E72F34C4267ECDF3E0E672214979F4A9C",
    "desktopBuildPayloadFiles": 554,
    "desktopBuildPayloadBytes": 169584329,
    "desktopBuildPayloadTree": "EE37B7E94A199FF99CCD28CCC50237B43711367B4CA450C4403D1C6CDEFAFA68",
    "desktopReceipt": "727041E24AC24F9B753D497309755F3ACA496C7F3DADC5D71D9C76DA5C09F8C4",
    "exe": "29320D1CCAA252057D011490866CC7533901740C5C6921359B7A2F9F58C4720C",
    "dll": "AB1C635AB1E01BD53A1C4D8AC31D9CF3DFAA88B48350487326E38D57497981CF",
    "authorityFiles": 556,
    "authorityBytes": 267800440,
    "authorityTree": "4BB31FC006661E72E285972C1FF6BA592D97B61F95AD127CE548077E99B7B88E",
    "registry": "C07266A9F4EB3E1A872C0C3CFCBD763315BBDB0F491DE5448A49DD606406C742",
    "authorityCheckpoint": "8D61B39DFF413A063E09145C2CBC6CC26B679F7CEEC67BE85CBF5B6796B639E0",
    "authorityReceipt": "A83F609D0704012901323FBD4F33F6B599F20A353EFEB1BC983A18B83E39624F",
    "coreCheckpoint": "7F468B39DBB55F7D719D786D8ED248850CD7C38CD3BDB60AB05E88D15BF99545",
    "windows": "D94A15089EDB34B145EF88E577A4848CE4B26CF4F3D03A2A53A2D875B71D9E92",
}


class GateFailure(RuntimeError):
    pass


def require(value: bool, message: str) -> None:
    if not value:
        raise GateFailure(message)


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def rel(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def evidence(path: Path) -> dict[str, Any]:
    return {"path": rel(path), "bytes": path.stat().st_size, "sha256": sha(path)}


def inventory(root: Path, exclude: set[str] | None = None) -> list[dict[str, Any]]:
    excluded = exclude or set()
    rows = []
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(root).as_posix()
        if relative in excluded:
            continue
        rows.append({"path": relative, "bytes": path.stat().st_size, "sha256": sha(path)})
    return sorted(rows, key=lambda row: row["path"])


def tree(rows: list[dict[str, Any]]) -> str:
    material = "\n".join(f"{row['path']}\t{row['bytes']}\t{row['sha256'].lower()}" for row in rows)
    return hashlib.sha256(material.encode("utf-8")).hexdigest().upper()


def write_once(path: Path, value: dict[str, Any]) -> None:
    if path.exists():
        raise GateFailure(f"append-only output exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def verify_payload() -> dict[str, Any]:
    source = inventory(DESKTOP)
    require((len(source), sum(row["bytes"] for row in source), tree(source)) == (EXPECTED["desktopFiles"], EXPECTED["desktopBytes"], EXPECTED["desktopTree"]), "Desktop payload drift")
    require(sha(DESKTOP / "desktop-build-receipt.v1.json") == EXPECTED["desktopReceipt"], "Desktop receipt drift")
    build_receipt = load(DESKTOP / "desktop-build-receipt.v1.json")
    require(
        (int(build_receipt["fileCount"]), int(build_receipt["totalBytes"]), str(build_receipt["treeSha256"]).upper())
        == (EXPECTED["desktopBuildPayloadFiles"], EXPECTED["desktopBuildPayloadBytes"], EXPECTED["desktopBuildPayloadTree"]),
        "Desktop build payload receipt drift",
    )
    changed = [row["path"] for row in source if not (CANDIDATE / row["path"]).is_file() or sha(CANDIDATE / row["path"]) != row["sha256"]]
    require(not changed, f"candidate Desktop payload differs: {changed[:5]}")
    result = {
        "schemaVersion": "knowledge-clipper-desktop-payload-equality.v1",
        "status": "passed",
        "source": rel(DESKTOP),
        "candidate": rel(CANDIDATE),
        "filesCompared": len(source),
        "totalBytes": sum(row["bytes"] for row in source),
        "treeSha256": tree(source),
        "changedFiles": 0,
        "entrypointSha256": sha(CANDIDATE / "KnowledgeClipper.exe"),
        "applicationDllSha256": sha(CANDIDATE / "KnowledgeClipper.dll"),
    }
    require(result["entrypointSha256"] == EXPECTED["exe"] and result["applicationDllSha256"] == EXPECTED["dll"], "Desktop entrypoint drift")
    if PAYLOAD_EQUALITY.exists():
        require(load(PAYLOAD_EQUALITY) == result, "existing append-only payload equality evidence drift")
    else:
        write_once(PAYLOAD_EQUALITY, result)
    return result


def verify_manifest() -> dict[str, Any]:
    path = CANDIDATE / "candidate-file-manifest.v1.json"
    require(sha(path) == EXPECTED["manifest"], "candidate manifest drift")
    declared = load(path)["payload"]
    payload = inventory(CANDIDATE, {path.name})
    sealed = inventory(CANDIDATE)
    require((len(payload), sum(row["bytes"] for row in payload), tree(payload)) == (EXPECTED["payloadFiles"], EXPECTED["payloadBytes"], EXPECTED["payloadTree"]), "candidate payload inventory drift")
    require((len(sealed), sum(row["bytes"] for row in sealed), tree(sealed)) == (EXPECTED["sealedFiles"], EXPECTED["sealedBytes"], EXPECTED["sealedTree"]), "candidate sealed inventory drift")
    normalized = [{"path": row["path"], "bytes": int(row["bytes"]), "sha256": str(row["sha256"]).upper()} for row in declared["files"]]
    require(normalized == payload, "candidate manifest file list drift")
    return {"status": "passed", "manifest": evidence(path), "payload": {"fileCount": len(payload), "totalBytes": sum(row["bytes"] for row in payload), "treeSha256": tree(payload)}, "sealed": {"fileCount": len(sealed), "totalBytes": sum(row["bytes"] for row in sealed), "treeSha256": tree(sealed)}}


def verify_authority() -> dict[str, Any]:
    source = inventory(AUTHORITY)
    embedded = inventory(CANDIDATE / "authority/win-x64")
    require(source == embedded, "embedded authority differs from admitted v1.2.18")
    require((len(source), sum(row["bytes"] for row in source), tree(source)) == (EXPECTED["authorityFiles"], EXPECTED["authorityBytes"], EXPECTED["authorityTree"]), "authority tree drift")
    checkpoint = AUTHORITY_METADATA / "checkpoint.json"
    registry = AUTHORITY_METADATA / "worker-registry.v1.json"
    require(sha(checkpoint) == EXPECTED["authorityCheckpoint"] and sha(registry) == EXPECTED["registry"], "authority metadata drift")
    require(sha(AUTHORITY_RECEIPT) == EXPECTED["authorityReceipt"] and sha(CORE_CHECKPOINT) == EXPECTED["coreCheckpoint"], "authority/Core checkpoint drift")
    require(sha(CANDIDATE / "authority/metadata/checkpoint.json") == EXPECTED["authorityCheckpoint"], "embedded authority checkpoint drift")
    require(sha(CANDIDATE / "authority/metadata/worker-registry.v1.json") == EXPECTED["registry"], "embedded registry drift")
    integration = load(CANDIDATE / "candidate-integration.json")
    require(integration["PipeName"] == "KnowledgeClipper.Integrated.v1218.UserFeedback" and integration["AuthorityTreeSha256"] == EXPECTED["authorityTree"], "candidate authority/pipe binding drift")
    return {"status": "passed", "version": "1.2.18", "fileCount": len(source), "totalBytes": sum(row["bytes"] for row in source), "treeSha256": tree(source), "checkpoint": evidence(checkpoint), "registry": evidence(registry), "authorityReceipt": evidence(AUTHORITY_RECEIPT), "coreCheckpoint": evidence(CORE_CHECKPOINT), "embeddedCopiedExactly": True}


def verify_runtime() -> dict[str, Any]:
    startup_path = RUNTIME / "startup-diagnostic.stdout.log"
    registry_path = RUNTIME / "authority-validate-registry.log"
    integration_path = RUNTIME / "integration-startup-canary.stdout.log"
    single_path = RUNTIME / "single-instance-diagnostic.stdout.log"
    closure_path = RUNTIME / "portable-schema-closure.json"
    startup, registry, integration, single, closure = map(load, (startup_path, registry_path, integration_path, single_path, closure_path))
    require(startup["result"] == "PASS" and startup["packageTreeVerified"] and startup["externalConnections"] == 0, "safe startup failed")
    require(registry == {"schemaVersion": "1.0.0", "status": "valid", "registryVersion": "1.2.18", "workers": 5}, "registry gate failed")
    require(integration["result"] == "PASS" and integration["orchestratorStartedByDesktop"] and integration["namedPipeInvoked"] and not integration["externalWorkerInvoked"], "candidate Named Pipe gate failed")
    require(all(not integration[name] for name in ("physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")), "forbidden integration side effect")
    require(single["userFeedbackMode"] == "activate-existing-window-and-message-box" and single["existingInstanceDetected"], "single-instance user feedback regression")
    require(closure == {"schemaVersion": "knowledge-clipper-portable-schema-closure-gate.v1", "status": "passed", "releaseRoot": ".", "schemasChecked": 62, "localRefsChecked": 188, "failures": []}, "portable schema closure drift")
    windows = load(WINDOWS)
    require(sha(WINDOWS) == EXPECTED["windows"] and windows["automatedWindowsUiCanary"]["status"] == "PASS" and windows["automatedWindowsUiCanary"]["assertions"] == 187, "Windows UI gate drift")
    previews = inventory(VISUAL)
    preview_images = [row for row in previews if row["path"].endswith(".png")]
    require(len(preview_images) == 17 and any(row["path"] == "floating-inline-input.png" for row in preview_images), "visual preview gate failed")
    return {"status": "passed", "safeStartup": evidence(startup_path), "registry5of5": evidence(registry_path), "namedPipeQuery": evidence(integration_path), "singleInstance": evidence(single_path), "portableSchemaClosure": evidence(closure_path), "windowsUi": evidence(WINDOWS), "windowsUiAssertions": 187, "visualPreview": {"root": rel(VISUAL), "fileCount": len(previews), "imageCount": len(preview_images), "treeSha256": tree(previews)}, "productionRetention": "disabled-until-state-aware-retention-service"}


def main() -> int:
    for path in (CANDIDATE, DESKTOP, AUTHORITY, AUTHORITY_METADATA, AUTHORITY_RECEIPT, CORE_CHECKPOINT, WINDOWS, CHECKLIST, VISUAL, RUNTIME):
        require(path.exists(), f"required input missing: {path}")
    require(not any(path.exists() for path in (FINAL_GATE, RECEIPT)), "append-only final output already exists")
    payload = verify_payload()
    manifest = verify_manifest()
    authority = verify_authority()
    runtime = verify_runtime()
    secret_scan = scan_runtime_tree_for_secrets(CANDIDATE)
    require(secret_scan["status"] == "passed" and secret_scan["findingCount"] == 0, "candidate secret scan failed")
    final_gate = {"schemaVersion": "knowledge-clipper-integrated-desktop-final-gates.v1", "status": "passed", "candidateId": CANDIDATE_ID, "desktopPayloadEquality": evidence(PAYLOAD_EQUALITY), "candidate": manifest, "authority": authority, "runtime": runtime, "secretScan": secret_scan}
    write_once(FINAL_GATE, final_gate)
    receipt = {
        "schemaVersion": "knowledge-clipper-integrated-desktop-candidate-receipt.v1",
        "createdAt": datetime.now(timezone.utc).isoformat(),
        "status": "PASS-AUTOMATED-USER-HAND-TEST-READY",
        "candidateId": CANDIDATE_ID,
        "productName": "零碎知识剪藏",
        "userHandTestEntrypoint": True,
        "entrypoint": rel(CANDIDATE / "KnowledgeClipper.exe"),
        "entrypointSha256": EXPECTED["exe"],
        "applicationDllSha256": EXPECTED["dll"],
        "candidate": manifest,
        "desktopPayloadEquality": evidence(PAYLOAD_EQUALITY),
        "desktopChangedFiles": payload["changedFiles"],
        "authority": authority,
        "automatedGates": evidence(FINAL_GATE),
        "userHandTestChecklist": evidence(CHECKLIST),
        "safety": {"secretScan": secret_scan, "externalWorkersInvokedByAdmission": False, "externalProductionApisInvokedByAdmission": False, "physicalSpeakerInvokedByAdmission": False},
        "coveredFixes": ["resizable floating window while Settings remains open", "detailed Anki read-only probe reasonCode/step projection", "RemNote probe uses saved active-profile target", "inline Enter shows immediate progress and reopens the same answer session without duplicate submit"],
        "boundaries": ["actual Desktop process to embedded Orchestrator query is covered; a fully automated visible inline-question to live Content answer is not exposed as a stable candidate CLI seam and is therefore a hand-test boundary", "external application selection/context and mixed-DPI cross-monitor interaction remain physical Windows boundaries", "real Content/Anki/RemNote/provider results depend on the user's configuration and were not called by admission", "RemNote original media remains unsupported:mcp.media_write_unavailable", "state-aware retention is not implemented; legacy purge remains disabled"],
    }
    write_once(RECEIPT, receipt)
    print(json.dumps({"status": receipt["status"], "entrypoint": receipt["entrypoint"], "candidateTreeSha256": manifest["sealed"]["treeSha256"], "manifestSha256": manifest["manifest"]["sha256"], "authorityTreeSha256": authority["treeSha256"], "receipt": evidence(RECEIPT)}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (GateFailure, OSError, ValueError, KeyError, json.JSONDecodeError) as exc:
        print(json.dumps({"status": "FAIL", "error": str(exc)}, ensure_ascii=False))
        raise SystemExit(1)
