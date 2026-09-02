from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from verify_settings_control_plane import scan_runtime_tree_for_secrets


PROJECT = Path(__file__).resolve().parents[1]
DEFAULT_CANDIDATE = PROJECT / "releases/kc-i-module-actions-v1217-20260831T063200Z/win-x64"
DEFAULT_RECEIPT = PROJECT / "receipts/desktop-integrated-module-actions-v1.2.17-20260831T072000Z.json"
AUTHORITY_SOURCE = PROJECT / "releases/knowledge-clipper-authority-v1.2.17-20260831T062800Z/win-x64"
AUTHORITY_META = PROJECT / "contracts/authority/worker-registry/v1.2.17-20260831T062800Z"
CORE_CHECKPOINT = PROJECT / "contracts/core/v2.7/checkpoints/v2.7.2-20260831T062500Z/checkpoint.json"
IMAGE_RECEIPT = PROJECT / "canary/image-v124-historical-source-ocr-20260831-150044-3ac851ba/canary-receipt.json"

EXPECTED = {
    "desktopReceipt": "2F45955F551E9EDEB0EDF8E453F931FD2CF30E9E87953FABDA6B9D19ADF2FD52",
    "manifest": "31D9619B26EB83DD0BFD2B311FB4327F2A2670EB6CB62A6F3EA9F9F1D9946380",
    "payloadFiles": 1115,
    "payloadBytes": 437152521,
    "payloadTree": "5E33903BECFECEBDCA765653D5C123866708E2E98567042AF8D44C42DC14185B",
    "sealedFiles": 1116,
    "sealedBytes": 437388302,
    "sealedTree": "A8A8D1B41DA30DC655CEA9F0AC9738E19BF60957FDB79ACF921025F40B62CBE4",
    "entrypoint": "29320D1CCAA252057D011490866CC7533901740C5C6921359B7A2F9F58C4720C",
    "desktopDll": "96C5541CA79F58F88D99D86015AEF2ECB53327424726C6559FE0534571BED32F",
    "desktopFiles": 551,
    "desktopBytes": 169369906,
    "desktopTree": "BCF51386AFC65C2567372CDC10B63475A52D2D405AA9860B869F069C42B5DA37",
    "authorityFiles": 552,
    "authorityBytes": 267756432,
    "authorityTree": "90FE03EA69FBA488ADEB8E1CF46AB6C02154D9120A42DF2E05C8729C66B0E38E",
    "authorityCheckpoint": "862ED5C41EC6BAA5F8B1446C9AA68DD131CEA7784D9EA8985CE54D42F2ACF95D",
    "authorityRegistry": "7F2F9591C564D634AA6408D2B1C4C07D9876342C1B3FA11A99A23A882CF3EFA1",
    "coreCheckpoint": "B22CA9746905AC923CBFFEF913A7CAEB3D7FF0EDDE8507145E6A7B99BBD09F65",
    "authorityQuality": "4218B84EE5D62CB8EAEDEB24169329503EE0B8CF178124212842429B41599086",
    "imageReceipt": "9DC2A4C50CFA9B2B0DF7D5F5ECF6C7B4363CF3D9446E77194443F68DC1473F9F",
    "imageWorker": "2E0C8D2ED9741FDBC9F482330EB4181719A5877F425145FEE8672CD119C86AC4",
}


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def native(path: Path) -> str:
    value = str(path.resolve())
    if os.name == "nt" and not value.startswith("\\\\?\\"):
        return "\\\\?\\" + value
    return value


def file_size(path: Path) -> int:
    return os.stat(native(path)).st_size


def is_file(path: Path) -> bool:
    return os.path.isfile(native(path))


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with open(native(path), "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load(path: Path) -> dict[str, Any]:
    with open(native(path), "r", encoding="utf-8-sig") as stream:
        return json.load(stream)


def rel(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def ev(path: Path) -> dict[str, Any]:
    return {"path": rel(path), "bytes": file_size(path), "sha256": sha256_file(path)}


def inventory(root: Path, exclude: set[str] | None = None) -> list[dict[str, Any]]:
    excluded = exclude or set()
    rows = []
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(root).as_posix()
        if relative in excluded:
            continue
        rows.append({"path": relative, "bytes": file_size(path), "sha256": sha256_file(path)})
    return sorted(rows, key=lambda item: item["path"])


def tab_tree(rows: list[dict[str, Any]]) -> str:
    material = "\n".join(f"{item['path']}\t{item['bytes']}\t{item['sha256'].lower()}" for item in rows)
    return hashlib.sha256(material.encode("utf-8")).hexdigest().upper()


def verify_manifest(candidate: Path) -> dict[str, Any]:
    manifest_path = candidate / "candidate-file-manifest.v1.json"
    require(sha256_file(manifest_path) == EXPECTED["manifest"], "manifest SHA drift")
    manifest = load(manifest_path)
    observed = inventory(candidate, {manifest_path.name})
    declared = manifest["payload"]["files"]
    require(len(observed) == len(declared) == EXPECTED["payloadFiles"], "manifest payload count drift")
    for expected, actual in zip(declared, observed, strict=True):
        require(expected["path"] == actual["path"], f"manifest path drift: {actual['path']}")
        require(int(expected["bytes"]) == actual["bytes"], f"manifest bytes drift: {actual['path']}")
        require(str(expected["sha256"]).upper() == actual["sha256"], f"manifest SHA drift: {actual['path']}")
    payload_bytes = sum(item["bytes"] for item in observed)
    payload_tree = tab_tree(observed)
    require(payload_bytes == int(manifest["payload"]["totalBytes"]) == EXPECTED["payloadBytes"], "payload bytes drift")
    require(payload_tree == str(manifest["payload"]["treeSha256"]).upper() == EXPECTED["payloadTree"], "payload tree drift")
    sealed = inventory(candidate)
    sealed_bytes = sum(item["bytes"] for item in sealed)
    sealed_tree = tab_tree(sealed)
    require(len(sealed) == EXPECTED["sealedFiles"], "sealed file count drift")
    require(sealed_bytes == EXPECTED["sealedBytes"], "sealed bytes drift")
    require(sealed_tree == EXPECTED["sealedTree"], "sealed tree drift")
    require(manifest.get("candidateHash") is None, "hand-test candidate unexpectedly claims candidateHash")
    return {
        "status": "passed",
        "manifest": ev(manifest_path),
        "payload": {"files": len(observed), "bytes": payload_bytes, "treeSha256": payload_tree},
        "sealed": {"files": len(sealed), "bytes": sealed_bytes, "treeSha256": sealed_tree},
        "candidateHash": None,
    }


def verify_desktop_payload(candidate: Path) -> dict[str, Any]:
    equality_path = PROJECT / "canary/desktop-integrated/kc-i-module-actions-v1217-20260831T063200Z/desktop-payload-equality.json"
    equality = load(equality_path)
    source = PROJECT / equality["source"]
    source_rows = inventory(source)
    candidate_rows = []
    for item in source_rows:
        path = candidate / item["path"]
        require(path.is_file(), f"candidate desktop file missing: {item['path']}")
        candidate_rows.append({"path": item["path"], "bytes": path.stat().st_size, "sha256": sha256_file(path)})
    require(source_rows == candidate_rows, "candidate Desktop payload differs from source build")
    require(len(source_rows) == EXPECTED["desktopFiles"], "Desktop payload file count drift")
    require(sum(item["bytes"] for item in source_rows) == EXPECTED["desktopBytes"], "Desktop payload bytes drift")
    require(tab_tree(source_rows) == EXPECTED["desktopTree"], "Desktop payload tree drift")
    require(equality["changedFiles"] == 0 and equality["missingFiles"] == 0 and equality["extraDesktopPayloadFiles"] == 0, "Desktop equality receipt drift")
    return {
        "status": "passed",
        "source": rel(source),
        "files": len(source_rows),
        "bytes": sum(item["bytes"] for item in source_rows),
        "treeSha256": tab_tree(source_rows),
        "equality": ev(equality_path),
        "supersededModuleSummaryTreeSha256": "342A29A2E797B4CC68583E8C9D041504B3BEEA792F60D5D5F15E810C7C80D225",
        "summaryDriftDisposition": "not authoritative; final equality and direct per-file comparison both resolve to BCF51386AFC65C2567372CDC10B63475A52D2D405AA9860B869F069C42B5DA37",
    }


def verify_authority(candidate: Path) -> dict[str, Any]:
    source_rows = inventory(AUTHORITY_SOURCE)
    embedded_rows = inventory(candidate / "authority/win-x64")
    require(source_rows == embedded_rows, "embedded authority differs from admitted v1.2.17")
    require(len(embedded_rows) == EXPECTED["authorityFiles"], "authority file count drift")
    require(sum(item["bytes"] for item in embedded_rows) == EXPECTED["authorityBytes"], "authority bytes drift")
    require(tab_tree(embedded_rows) == EXPECTED["authorityTree"], "authority tree drift")
    checkpoint = AUTHORITY_META / "checkpoint.json"
    registry = AUTHORITY_META / "worker-registry.v1.json"
    require(sha256_file(checkpoint) == EXPECTED["authorityCheckpoint"], "authority checkpoint SHA drift")
    require(sha256_file(registry) == EXPECTED["authorityRegistry"], "authority registry SHA drift")
    require(sha256_file(CORE_CHECKPOINT) == EXPECTED["coreCheckpoint"], "Core checkpoint SHA drift")
    embedded_registry = candidate / "authority/metadata/worker-registry.v1.json"
    require(sha256_file(embedded_registry) == EXPECTED["authorityRegistry"], "embedded registry SHA drift")
    config = load(candidate / "candidate-integration.json")
    require(config["ReleaseRoot"] == "authority/win-x64", "candidate release root binding drift")
    require(config["Registry"] == "authority/metadata/worker-registry.v1.json", "candidate registry binding drift")
    require(config["AuthorityTreeSha256"].upper() == EXPECTED["authorityTree"], "candidate authority tree binding drift")
    prior = PROJECT / "canary/product-gap-audit/20260831T064732Z-authority-v1217-quality-final/quality-authority-receipt.json"
    require(sha256_file(prior) == EXPECTED["authorityQuality"], "independent authority quality receipt drift")
    return {
        "status": "passed",
        "version": "1.2.17",
        "files": len(embedded_rows),
        "bytes": sum(item["bytes"] for item in embedded_rows),
        "treeSha256": tab_tree(embedded_rows),
        "checkpoint": ev(checkpoint),
        "registry": ev(registry),
        "coreCheckpoint": ev(CORE_CHECKPOINT),
        "priorIndependentQualification": ev(prior),
        "integrationConfig": ev(candidate / "candidate-integration.json"),
    }


def verify_receipt_recursively(receipt_path: Path) -> dict[str, Any]:
    require(sha256_file(receipt_path) == EXPECTED["desktopReceipt"], "Desktop receipt SHA drift")
    receipt = load(receipt_path)
    checked: dict[str, dict[str, Any]] = {}

    def walk(value: Any) -> None:
        if isinstance(value, dict):
            if isinstance(value.get("path"), str) and isinstance(value.get("sha256"), str):
                path = PROJECT / value["path"]
                require(path.is_file(), f"receipt evidence missing: {value['path']}")
                require(sha256_file(path) == value["sha256"].upper(), f"receipt evidence SHA drift: {value['path']}")
                if "bytes" in value:
                    require(file_size(path) == int(value["bytes"]), f"receipt evidence bytes drift: {value['path']}")
                checked[value["path"]] = ev(path)
            for child in value.values():
                walk(child)
        elif isinstance(value, list):
            for child in value:
                walk(child)

    walk(receipt)
    require(receipt["userHandTestEntrypoint"] is True, "Desktop did not mark user hand-test entrypoint")
    require(receipt["authority"]["version"] == "1.2.17", "Desktop receipt authority drift")
    require(receipt["corePublicModuleActions"]["operations"] == ["speech.voices", "speech.preview", "remnote.probe"], "module action list drift")
    require(receipt["corePublicModuleActions"]["ocrHumanResolution"] == ["accept_original", "submit_correction", "cancel"], "OCR action list drift")
    require(receipt["corePublicModuleActions"]["originalToAnki"] == "ordered-original-images.v1", "original-to-Anki capability drift")
    require(receipt["corePublicModuleActions"]["remnoteOriginalMedia"] == "unsupported:mcp.media_write_unavailable", "RemNote media boundary drift")
    return {"status": "passed", "receipt": ev(receipt_path), "referencedEvidenceChecked": len(checked)}


def verify_schema_closure(candidate: Path, output_root: Path) -> dict[str, Any]:
    output = output_root / "portable-schema-closure.json"
    command = ["python", str(PROJECT / "tools/validate_release_schema_closure.py"), "--release-root", str(candidate / "authority/win-x64"), "--output", str(output)]
    completed = subprocess.run(command, cwd=PROJECT, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60, check=False)
    require(completed.returncode == 0, f"portable schema closure failed: {completed.stderr or completed.stdout}")
    result = load(output)
    require(result["status"] == "passed" and result["schemasChecked"] == 62 and result["localRefsChecked"] == 188 and not result["failures"], "portable schema closure drift")
    artifact = candidate / "authority/win-x64/contracts/v1/schemas/common/artifact.schema.json"
    require(artifact.is_file(), "portable authority omits common artifact schema")
    return {**result, "receipt": ev(output), "commonArtifactSchema": ev(artifact)}


def verify_ui_and_capabilities(candidate: Path, receipt_path: Path) -> dict[str, Any]:
    contract_path = candidate / "contracts/desktop-automation/v1.3/desktop-automation.contract.json"
    contract = load(contract_path)
    actions = set(contract["semanticActions"])
    require({"ocr-human-resolution.accept-original", "ocr-human-resolution.submit-correction", "ocr-human-resolution.cancel"} <= actions, "OCR semantic actions missing")
    ids = {item["automationId"] for item in contract["automationIds"]}
    required_ids = {
        "knowledge-clipper.desktop.ocr-resolution.window",
        "knowledge-clipper.desktop.ocr-resolution.original-text",
        "knowledge-clipper.desktop.ocr-resolution.ai-candidate",
        "knowledge-clipper.desktop.ocr-resolution.difference",
        "knowledge-clipper.desktop.ocr-resolution.edit-text",
        "knowledge-clipper.desktop.ocr-resolution.use-original",
        "knowledge-clipper.desktop.ocr-resolution.use-edited",
        "knowledge-clipper.desktop.ocr-resolution.cancel",
    }
    require(required_ids <= ids, "OCR attention automation IDs missing")
    receipt = load(receipt_path)
    ui_path = PROJECT / "canary/desktop-windows/20260831T061103Z/receipt.json"
    ui = load(ui_path)
    automated_ui = ui["automatedWindowsUiCanary"]
    require(automated_ui["status"] == "PASS" and int(automated_ui["assertions"]) == 180, "Windows UI evidence drift")
    require(ui["realWindowsCanaries"]["externalApplicationSelectionThroughNoActivateFloatingButton"]["status"] == "PENDING", "external-app hand-test boundary drift")
    require(ui["realWindowsCanaries"]["crossMonitorDpiDrag"]["status"] == "PENDING", "mixed-DPI hand-test boundary drift")
    checklist_path = PROJECT / receipt["userHandTestChecklist"]["path"]
    checklist = checklist_path.read_text(encoding="utf-8")
    for phrase in ["KnowledgeClipper.exe", "使用原文继续", "编辑后继续", "取消", "原图模式导出 Anki", "题图在前、答案图在后", "mcp.media_write_unavailable", "DPI", "刷新声音", "只读连接检查"]:
        require(phrase in checklist, f"hand-test checklist missing: {phrase}")
    return {"status": "passed", "automationContract": ev(contract_path), "requiredAutomationIds": len(required_ids), "windowsUi": ev(ui_path), "windowsUiAssertions": 180, "handTestChecklist": ev(checklist_path)}


def verify_safety(candidate: Path) -> dict[str, Any]:
    scan = scan_runtime_tree_for_secrets(candidate)
    require(scan["status"] == "passed" and scan["findingCount"] == 0, "candidate secret scan failed")
    credentials = [path.relative_to(candidate).as_posix() for path in candidate.rglob("*") if path.is_file() and path.name.casefold() in {"credentials.json", "credentials.json.bak", ".env"}]
    legacy = [path.relative_to(candidate).as_posix() for path in candidate.rglob("*") if path.is_file() and path.name.casefold() in {"smoketest.exe", "ttscanary.exe"}]
    require(not credentials, f"candidate carries credential files: {credentials}")
    require(not legacy, f"candidate carries legacy external canaries: {legacy}")
    return {"status": "passed", "secretScan": scan, "credentialFiles": credentials, "legacyExternalCanaries": legacy}


def run_json(command: list[str], cwd: Path, timeout: int = 60) -> tuple[dict[str, Any], str, str]:
    completed = subprocess.run(command, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout, check=False)
    require(completed.returncode == 0, f"command failed ({completed.returncode}): {completed.stderr or completed.stdout}")
    require(completed.stdout.strip(), f"command returned no JSON: {command}")
    return json.loads(completed.stdout), completed.stdout, completed.stderr


def verify_runtime(candidate: Path, output_root: Path) -> dict[str, Any]:
    runtime_root = output_root / "runtime"
    runtime_root.mkdir(parents=True, exist_ok=False)
    startup, out, err = run_json([str(candidate / "KnowledgeClipper.exe"), "--startup-diagnostic"], candidate)
    (runtime_root / "startup.stdout.json").write_text(out, encoding="utf-8")
    (runtime_root / "startup.stderr.log").write_text(err, encoding="utf-8")
    require(startup["result"] == "PASS", "safe startup failed")
    require(startup["externalConnections"] == 0 and startup["namedPipeInvoked"] is False, "startup performed integration or external action")
    authority = candidate / "authority/win-x64"
    registry_path = candidate / "authority/metadata/worker-registry.v1.json"
    registry, out, err = run_json([str(authority / "orchestrator/KnowledgeClipper.Orchestrator.exe"), "validate-registry", "--registry", str(registry_path), "--release-root", str(authority)], authority)
    (runtime_root / "registry.stdout.json").write_text(out, encoding="utf-8")
    (runtime_root / "registry.stderr.log").write_text(err, encoding="utf-8")
    require(registry["status"] == "valid" and int(registry["workers"]) == 5, "release-local registry validation failed")
    module_root = runtime_root / "offline-module-action"
    command = ["pwsh", "-NoLogo", "-NoProfile", "-File", str(PROJECT / "tools/Invoke-V1217CandidateOfflineModuleActionGate.ps1"), "-CandidateRoot", str(candidate), "-EvidenceRoot", str(module_root)]
    completed = subprocess.run(command, cwd=PROJECT, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60, check=False)
    require(completed.returncode == 0, f"offline Named Pipe module-action gate failed: {completed.stderr or completed.stdout}")
    module = load(module_root / "offline-module-action-receipt.json")
    require(module["status"] == "passed" and module["queryRecent"]["ok"] is True, "offline Named Pipe query failed")
    preview = module["unauthorizedPreview"]
    require(preview == {"ok": False, "errorCode": "ipc.invalid_frame", "receiptCreated": False, "workerRunCount": 0, "externalServiceCalls": 0, "physicalSpeakerInvocations": 0}, "unauthorized preview boundary drift")
    require((module_root / "orchestrator.stderr.log").read_text(encoding="utf-8").strip() == "", "candidate Orchestrator emitted stderr")
    return {
        "status": "passed",
        "startup": startup,
        "registry": registry,
        "offlineNamedPipeModuleAction": ev(module_root / "offline-module-action-receipt.json"),
        "sideEffects": {"externalWorkerRuns": 0, "externalServiceCalls": 0, "speakerInvocations": 0, "destinationWrites": 0},
    }


def verify_image_input() -> dict[str, Any]:
    require(sha256_file(IMAGE_RECEIPT) == EXPECTED["imageReceipt"], "Image 1.2.4 receipt SHA drift")
    data = load(IMAGE_RECEIPT)
    root = IMAGE_RECEIPT.parent
    worker = PROJECT / data["candidateEntrypoint"]
    require(worker.is_file() and sha256_file(worker) == EXPECTED["imageWorker"] == data["candidateSha256"].upper(), "Image 1.2.4 worker identity drift")
    checked = []
    for item in data["preservedFiles"]:
        path = root / item["path"]
        require(is_file(path), f"Image preserved file missing: {item['path']}")
        require(file_size(path) == int(item["bytes"]), f"Image preserved bytes drift: {item['path']}")
        require(sha256_file(path) == item["sha256"].upper(), f"Image preserved SHA drift: {item['path']}")
        checked.append(item["path"])
    old_image = PROJECT / data["sourceImport"]["imagePath"]
    old_receipt = PROJECT / data["sourceImport"]["receiptPath"]
    require(sha256_file(old_image) == data["sourceImport"]["imageSha256"].upper(), "historical source image SHA drift")
    require(sha256_file(old_receipt) == data["sourceImport"]["receiptSha256"].upper(), "historical source receipt SHA drift")
    handoff = load(root / data["contentAdapterHandoff"]["path"])
    require(data["status"] == "completed" and handoff["status"] == "unresolved", "Image result/handoff semantic split drift")
    require(data["humanReviewRecommended"] is True and data["sourceGroundTruthMismatch"] is True, "Image unresolved markers missing")
    require(data["providerCalls"] == 1 and data["providerSubmissionCount"] == 1 and data["destinationWrites"] == [], "Image side-effect accounting drift")
    require(data["submissionPublished"] is True and data["resumePublished"] is False, "Image attempt publication drift")
    require("CANAR " in (root / data["readableText"]["path"]).read_text(encoding="utf-8") and "CANARY " not in (root / data["readableText"]["path"]).read_text(encoding="utf-8"), "Image mismatch sample drift")
    return {
        "status": "INPUT-QUALIFICATION-PASS-CORE-CONTINUATION-PENDING",
        "receipt": ev(IMAGE_RECEIPT),
        "worker": ev(worker),
        "preservedFilesChecked": len(checked),
        "workerResult": "completed",
        "coreHandoff": "unresolved",
        "observedMismatch": "CANAR != CANARY",
        "providerCallsInOwnerCanary": 1,
        "providerCallsInThisAudit": 0,
        "destinationWrites": 0,
        "nextGate": "Core immutable waiting_input -> human command -> resumed downstream evidence",
    }


def render(result: dict[str, Any]) -> str:
    return "\n".join([
        "# KnowledgeClipper v1.2.17 candidate-bound independent quality gate",
        "",
        f"- Status: `{result['status']}`",
        f"- Candidate: `{result['candidate']['root']}`",
        f"- Sealed tree: `{result['candidate']['sealedTreeSha256']}`",
        f"- Manifest SHA-256: `{result['candidate']['manifest']['sha256']}`",
        "- User hand-test entrypoint: `GO` for this complete directory and `KnowledgeClipper.exe`.",
        "- Release promotion: `NO-GO-CONDITIONAL`; this hand-test manifest intentionally has no candidateHash/signing identity.",
        "",
        "## Input -> observable output",
        "",
        "- Image 1.2.4 historical source -> actual MinerU owner Canary -> worker `completed`, but handoff `unresolved` because `CANARY` became `CANAR`; no Core continuation is claimed.",
        "- Candidate startup -> diagnostic PASS with zero external connections and no Named Pipe action.",
        "- Candidate embedded Orchestrator -> release-local registry 5/5 -> isolated Named Pipe query PASS.",
        "- Candidate Named Pipe `speech.preview` without explicit authorization -> `ipc.invalid_frame`; no receipt, worker, external service, or speaker action.",
        "- OCR attention -> original/candidate/diff/editor -> accept original / submit correction / cancel through public task input.",
        "- Original images -> Anki `ordered-original-images.v1`; RemNote original media remains explicit `mcp.media_write_unavailable`.",
        "",
        "## Remaining real boundaries",
        "",
        "- Core-owned real OCR unresolved -> human decision -> immutable resume -> destination chain is pending and must be appended separately.",
        "- Physical external-app selection/focus, image clipboard, screenshot drag, mixed-DPI cross-screen behavior, and any explicitly clicked speaker preview remain hand-test/environment boundaries.",
        "- No RemNote, Anki, MinerU, DeepSeek, external worker run, or physical speaker action was invoked by this quality audit.",
    ]) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", default=str(DEFAULT_CANDIDATE))
    parser.add_argument("--desktop-receipt", default=str(DEFAULT_RECEIPT))
    parser.add_argument("--output-root", required=True)
    args = parser.parse_args()
    candidate = Path(args.candidate).resolve()
    receipt = Path(args.desktop_receipt).resolve()
    output_root = Path(args.output_root).resolve()
    require(candidate.is_dir(), "candidate root missing")
    require(receipt.is_file(), "Desktop final receipt missing")
    require(not output_root.exists(), "append-only output root already exists")
    output_root.mkdir(parents=True)
    require(sha256_file(candidate / "KnowledgeClipper.exe") == EXPECTED["entrypoint"], "entrypoint SHA drift")
    require(sha256_file(candidate / "KnowledgeClipper.dll") == EXPECTED["desktopDll"], "Desktop DLL SHA drift")
    checks = {
        "imageUnresolvedInputQualification": verify_image_input(),
        "candidateManifestAndTree": verify_manifest(candidate),
        "desktopPayloadEquality": verify_desktop_payload(candidate),
        "authorityAndCoreBinding": verify_authority(candidate),
        "desktopReceiptRecursion": verify_receipt_recursively(receipt),
        "portableRecursiveSchemaClosure": verify_schema_closure(candidate, output_root),
        "ocrAttentionAndHandtest": verify_ui_and_capabilities(candidate, receipt),
        "candidateSafety": verify_safety(candidate),
        "safeRuntimeAndNamedPipe": verify_runtime(candidate, output_root),
    }
    result = {
        "schemaVersion": "knowledge-clipper-quality-desktop-v1217-candidate-bound.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-CANDIDATE-BOUND-USER-HANDTEST-READY-RELEASE-CONDITIONAL",
        "candidateBound": True,
        "candidate": {
            "root": rel(candidate),
            "entrypoint": ev(candidate / "KnowledgeClipper.exe"),
            "desktopDll": ev(candidate / "KnowledgeClipper.dll"),
            "manifest": ev(candidate / "candidate-file-manifest.v1.json"),
            "sealedTreeSha256": EXPECTED["sealedTree"],
            "candidateHash": None,
        },
        "desktopFinalReceipt": ev(receipt),
        "userHandTestEntrypoint": True,
        "checks": checks,
        "productGapUpdate": {
            "ocrHumanContinue": "IMAGE-INPUT-QUALIFIED; CORE-REAL-CONTINUATION-PENDING",
            "originalToAnki": "CANDIDATE-CAPABILITY-PASS; REAL-CHAIN-NOT-RUN-BY-THIS-AUDIT",
            "originalToRemNote": "UNSUPPORTED:mcp.media_write_unavailable",
            "speechVoiceDiscoveryAndPreview": "CANDIDATE-IPC-PRESENT; UNAUTHORIZED-PREVIEW-FAIL-CLOSED; EXPLICIT-PHYSICAL-PREVIEW-HANDTEST-ONLY",
            "remnoteReadonlyProbe": "CANDIDATE-IPC-PRESENT; NOT-RUN-IN-THIS-AUDIT",
            "stateAwareRetention": "AUTOMATIC-PRODUCTION-PURGE-DISABLED",
            "desktopPhysicalContextAndMixedDpi": "USER-HANDTEST-BOUNDARY",
        },
        "promotion": {
            "userHandTest": "GO",
            "release": "NO-GO-CONDITIONAL",
            "reason": "hand-test candidate has no candidateHash/signing identity and Core real OCR human-resume chain remains pending",
        },
        "sideEffectsByThisAudit": {"externalWorkerRuns": 0, "externalServiceCalls": 0, "remnoteCalls": 0, "ankiCalls": 0, "mineruCalls": 0, "deepseekCalls": 0, "speakerInvocations": 0, "destinationWrites": 0},
    }
    receipt_path = output_root / "quality-candidate-receipt.json"
    receipt_path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    readable_path = output_root / "READABLE.md"
    readable_path.write_text(render(result), encoding="utf-8")
    summary = {"status": result["status"], "receipt": ev(receipt_path), "readable": ev(readable_path), "candidateTree": EXPECTED["sealedTree"]}
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
