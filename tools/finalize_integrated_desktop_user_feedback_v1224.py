from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from verify_settings_control_plane import scan_runtime_tree_for_secrets


PROJECT = Path(__file__).resolve().parents[1]
CANDIDATE_ID = "kc-i-user-feedback-fixes-v1224-20260901T012000Z"
CANDIDATE = PROJECT / "releases" / CANDIDATE_ID / "win-x64"
SOURCE = PROJECT / "releases/kc-i-user-feedback-fixes-v1218-20260831T093000Z/win-x64"
SOURCE_RECEIPT = PROJECT / "receipts/desktop-integrated-user-feedback-v1.2.18-20260831T103000Z.json"
AUTHORITY = PROJECT / "releases/knowledge-clipper-authority-v1.2.24-20260901T010500Z/win-x64"
AUTHORITY_METADATA = PROJECT / "contracts/authority/worker-registry/v1.2.24-20260901T010500Z"
AUTHORITY_RECEIPT = PROJECT / "receipts/knowledge-clipper-authority-1.2.24-20260901T011500Z.json"
EVIDENCE = PROJECT / "canary/desktop-integrated" / CANDIDATE_ID
RUNTIME = EVIDENCE / "candidate-gates-20260901T012500Z"
CHECKLIST = EVIDENCE / "USER-HAND-TEST.md"
PAYLOAD_EQUALITY = EVIDENCE / "desktop-payload-equality.json"
FINAL_GATE = EVIDENCE / "candidate-final-gates.json"
RECEIPT = PROJECT / "receipts/desktop-integrated-user-feedback-v1.2.24-20260901T013000Z.json"

EXPECTED = {
    "manifest": "DCA881033DA0CCCE2F1EE6302940D50D5A2B59B32D72CE5A677415458DD1146A",
    "payloadFiles": 1129,
    "payloadBytes": 437748049,
    "payloadTree": "DECA4E6B3F7F6EDE1569525629F61ED4342E19F8397179310E09F5917749B354",
    "sealedFiles": 1130,
    "sealedBytes": 437987010,
    "sealedTree": "8494F63071ADBBC80CFE3060BCC2F416E753F7880056FC200B1C455B4FE4EA0D",
    "desktopReceipt": "727041E24AC24F9B753D497309755F3ACA496C7F3DADC5D71D9C76DA5C09F8C4",
    "desktopFileCount": 554,
    "desktopBytes": 169584329,
    "desktopTree": "EE37B7E94A199FF99CCD28CCC50237B43711367B4CA450C4403D1C6CDEFAFA68",
    "sourceIntegratedReceipt": "2F98E1E844088329E5250188438FFEF6CDF206E90A3CB0F221022B06CA72016A",
    "entrypoint": "29320D1CCAA252057D011490866CC7533901740C5C6921359B7A2F9F58C4720C",
    "applicationDll": "AB1C635AB1E01BD53A1C4D8AC31D9CF3DFAA88B48350487326E38D57497981CF",
    "authorityFiles": 562,
    "authorityBytes": 268030359,
    "authorityTree": "F4BDB96A4A327870039E7B48DF7255EBAD1E437D8E37214D001448B67E210541",
    "authorityCheckpoint": "B2103C2C6ECCA92EC38695BDCE4E10EDEB18A7F981C580677B6F4B357D5D4AAF",
    "authorityRegistry": "CB9F3291625CB6E7309AA3F677CE783FC713A18B54C29F65A553AA590C8187D7",
    "authorityReceipt": "3BFFB78C00A372C6D2C2C3ED0BBD7E76B9C5A8B54E4C14E399B070820338E04D",
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
        raise GateFailure(f"append-only output already exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def verify_desktop_equality() -> dict[str, Any]:
    build_receipt_path = SOURCE / "desktop-build-receipt.v1.json"
    build_receipt = load(build_receipt_path)
    require(sha(build_receipt_path) == EXPECTED["desktopReceipt"], "source Desktop build receipt drift")
    require((int(build_receipt["fileCount"]), int(build_receipt["totalBytes"]), str(build_receipt["treeSha256"]).upper()) == (EXPECTED["desktopFileCount"], EXPECTED["desktopBytes"], EXPECTED["desktopTree"]), "source Desktop build aggregate drift")
    require(sha(SOURCE_RECEIPT) == EXPECTED["sourceIntegratedReceipt"], "source integrated receipt drift")
    changed = []
    for row in build_receipt["files"]:
        relative = row["path"]
        source = SOURCE / relative
        target = CANDIDATE / relative
        expected_sha = str(row["sha256"]).upper()
        expected_bytes = int(row["bytes"])
        if not source.is_file() or not target.is_file() or source.stat().st_size != expected_bytes or target.stat().st_size != expected_bytes or sha(source) != expected_sha or sha(target) != expected_sha:
            changed.append(relative)
    require(not changed, f"Desktop payload equality failed: {changed[:5]}")
    require(sha(CANDIDATE / "desktop-build-receipt.v1.json") == EXPECTED["desktopReceipt"], "candidate Desktop build receipt drift")
    result = {
        "schemaVersion": "knowledge-clipper-desktop-payload-equality.v1",
        "status": "passed",
        "sourceIntegratedCandidate": rel(SOURCE),
        "sourceIntegratedReceipt": evidence(SOURCE_RECEIPT),
        "targetCandidate": rel(CANDIDATE),
        "filesCompared": len(build_receipt["files"]),
        "totalBytes": int(build_receipt["totalBytes"]),
        "treeSha256": str(build_receipt["treeSha256"]).upper(),
        "changedFiles": 0,
        "buildReceiptCopiedExactly": True,
        "entrypointSha256": sha(CANDIDATE / "KnowledgeClipper.exe"),
        "applicationDllSha256": sha(CANDIDATE / "KnowledgeClipper.dll"),
    }
    require(result["entrypointSha256"] == EXPECTED["entrypoint"] and result["applicationDllSha256"] == EXPECTED["applicationDll"], "Desktop executable equality failed")
    write_once(PAYLOAD_EQUALITY, result)
    return result


def verify_candidate() -> dict[str, Any]:
    manifest_path = CANDIDATE / "candidate-file-manifest.v1.json"
    require(sha(manifest_path) == EXPECTED["manifest"], "candidate manifest SHA drift")
    manifest = load(manifest_path)
    payload = inventory(CANDIDATE, {manifest_path.name})
    sealed = inventory(CANDIDATE)
    require((len(payload), sum(row["bytes"] for row in payload), tree(payload)) == (EXPECTED["payloadFiles"], EXPECTED["payloadBytes"], EXPECTED["payloadTree"]), "candidate payload inventory drift")
    require((len(sealed), sum(row["bytes"] for row in sealed), tree(sealed)) == (EXPECTED["sealedFiles"], EXPECTED["sealedBytes"], EXPECTED["sealedTree"]), "candidate sealed inventory drift")
    declared = [{"path": row["path"], "bytes": int(row["bytes"]), "sha256": str(row["sha256"]).upper()} for row in manifest["payload"]["files"]]
    require(declared == payload, "candidate manifest file list drift")
    return {"status": "passed", "manifest": evidence(manifest_path), "payload": {"fileCount": len(payload), "totalBytes": sum(row["bytes"] for row in payload), "treeSha256": tree(payload)}, "sealed": {"fileCount": len(sealed), "totalBytes": sum(row["bytes"] for row in sealed), "treeSha256": tree(sealed)}}


def verify_authority() -> dict[str, Any]:
    source = inventory(AUTHORITY)
    embedded = inventory(CANDIDATE / "authority/win-x64")
    require(source == embedded, "embedded authority differs from admitted v1.2.24")
    require((len(source), sum(row["bytes"] for row in source), tree(source)) == (EXPECTED["authorityFiles"], EXPECTED["authorityBytes"], EXPECTED["authorityTree"]), "authority tree drift")
    checkpoint = AUTHORITY_METADATA / "checkpoint.json"
    registry = AUTHORITY_METADATA / "worker-registry.v1.json"
    require(sha(checkpoint) == EXPECTED["authorityCheckpoint"] and sha(registry) == EXPECTED["authorityRegistry"] and sha(AUTHORITY_RECEIPT) == EXPECTED["authorityReceipt"], "authority metadata drift")
    require(sha(CANDIDATE / "authority/metadata/checkpoint.json") == EXPECTED["authorityCheckpoint"] and sha(CANDIDATE / "authority/metadata/worker-registry.v1.json") == EXPECTED["authorityRegistry"], "embedded authority metadata drift")
    integration = load(CANDIDATE / "candidate-integration.json")
    require(integration["PipeName"] == "KnowledgeClipper.Integrated.v1224.UserFeedback" and integration["AuthorityTreeSha256"] == EXPECTED["authorityTree"] and integration["IpcConfig"] == "authority/metadata/orchestrator-ipc.candidate.v1.2.json", "candidate integration binding drift")
    return {"status": "passed", "registryVersion": "1.2.24", "fileCount": len(source), "totalBytes": sum(row["bytes"] for row in source), "treeSha256": tree(source), "checkpoint": evidence(checkpoint), "registry": evidence(registry), "authorityReceipt": evidence(AUTHORITY_RECEIPT), "embeddedCopiedExactly": True}


def verify_runtime() -> dict[str, Any]:
    startup_path = RUNTIME / "startup-diagnostic.stdout.log"
    registry_path = RUNTIME / "authority-validate-registry.log"
    integration_path = RUNTIME / "integration-startup-canary.stdout.log"
    single_path = RUNTIME / "single-instance-diagnostic.stdout.log"
    closure_path = RUNTIME / "portable-schema-closure.json"
    startup, registry, integration, single, closure = map(load, (startup_path, registry_path, integration_path, single_path, closure_path))
    require(startup["result"] == "PASS" and startup["packageTreeVerified"] and startup["externalConnections"] == 0 and startup["authorityTreeSha256"] == EXPECTED["authorityTree"], "safe startup gate failed")
    require(registry == {"schemaVersion": "1.0.0", "status": "valid", "registryVersion": "1.2.24", "workers": 5}, "registry 5/5 gate failed")
    require(integration["result"] == "PASS" and integration["orchestratorStartedByDesktop"] and integration["namedPipeInvoked"] and not integration["externalWorkerInvoked"] and integration["authorityTreeSha256"] == EXPECTED["authorityTree"], "embedded Orchestrator/Named Pipe gate failed")
    require(all(not integration[name] for name in ("physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")), "forbidden runtime side effect")
    require(single["userFeedbackMode"] == "activate-existing-window-and-message-box", "single-instance behavior drift")
    require(closure == {"schemaVersion": "knowledge-clipper-portable-schema-closure-gate.v1", "status": "passed", "releaseRoot": ".", "schemasChecked": 62, "localRefsChecked": 188, "failures": []}, "portable schema closure drift")
    return {"status": "passed", "safeStartup": evidence(startup_path), "registry5of5": evidence(registry_path), "namedPipeQuery": evidence(integration_path), "singleInstance": evidence(single_path), "portableSchemaClosure": evidence(closure_path), "externalWorkersInvoked": False, "externalConnections": 0}


def main() -> int:
    for path in (CANDIDATE, SOURCE, SOURCE_RECEIPT, AUTHORITY, AUTHORITY_METADATA, AUTHORITY_RECEIPT, RUNTIME, CHECKLIST):
        require(path.exists(), f"required input missing: {path}")
    require(not any(path.exists() for path in (PAYLOAD_EQUALITY, FINAL_GATE, RECEIPT)), "append-only final output already exists")
    equality = verify_desktop_equality()
    candidate = verify_candidate()
    authority = verify_authority()
    runtime = verify_runtime()
    secret_scan = scan_runtime_tree_for_secrets(CANDIDATE)
    require(secret_scan["status"] == "passed" and secret_scan["findingCount"] == 0, "candidate secret scan failed")
    final_gate = {"schemaVersion": "knowledge-clipper-integrated-desktop-final-gates.v1", "status": "passed", "candidateId": CANDIDATE_ID, "desktopPayloadEquality": evidence(PAYLOAD_EQUALITY), "candidate": candidate, "authority": authority, "runtime": runtime, "secretScan": secret_scan}
    write_once(FINAL_GATE, final_gate)
    receipt = {
        "schemaVersion": "knowledge-clipper-integrated-desktop-candidate-receipt.v1",
        "createdAt": datetime.now(timezone.utc).isoformat(),
        "status": "PASS-APPEND-ONLY-EXACT-REBASE-V1.2.24",
        "candidateId": CANDIDATE_ID,
        "productName": "零碎知识剪藏",
        "userHandTestEntrypoint": True,
        "entrypoint": rel(CANDIDATE / "KnowledgeClipper.exe"),
        "entrypointSha256": EXPECTED["entrypoint"],
        "applicationDllSha256": EXPECTED["applicationDll"],
        "candidate": candidate,
        "desktopPayloadEquality": evidence(PAYLOAD_EQUALITY),
        "desktopChangedFiles": equality["changedFiles"],
        "authority": authority,
        "automatedGates": evidence(FINAL_GATE),
        "userHandTestChecklist": evidence(CHECKLIST),
        "safety": {"secretScan": secret_scan, "externalWorkersInvoked": False, "externalProductionApisInvoked": False, "physicalSpeakerInvoked": False, "ankiProcessRequired": False},
        "boundaries": ["Desktop/UI payload is byte-identical to the v1.2.18 feedback-fixed candidate; no repeated unrelated UI test is required", "real Anki/AnkiConnect E2E is intentionally not a candidate admission dependency", "real external Content, RemNote, OCR, selection/context and mixed-DPI behavior remain separately authorized or physical hand-test boundaries", "RemNote original media remains unsupported:mcp.media_write_unavailable", "state-aware retention is not implemented; legacy purge remains disabled"],
    }
    write_once(RECEIPT, receipt)
    print(json.dumps({"status": receipt["status"], "entrypoint": receipt["entrypoint"], "candidateTreeSha256": candidate["sealed"]["treeSha256"], "manifestSha256": candidate["manifest"]["sha256"], "desktopChangedFiles": equality["changedFiles"], "authorityTreeSha256": authority["treeSha256"], "receipt": evidence(RECEIPT)}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (GateFailure, OSError, ValueError, KeyError, json.JSONDecodeError) as exc:
        print(json.dumps({"status": "FAIL", "error": str(exc)}, ensure_ascii=False))
        raise SystemExit(1)
