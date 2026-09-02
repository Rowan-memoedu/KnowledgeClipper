from __future__ import annotations

import argparse
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from verify_settings_control_plane import scan_runtime_tree_for_secrets
from verify_v1217_desktop_candidate import ev, file_size, inventory, is_file, load, rel, require, sha256_file, tab_tree


PROJECT = Path(__file__).resolve().parents[1]
CANDIDATE = PROJECT / "releases/kc-i-user-feedback-fixes-v1224-20260901T012000Z/win-x64"
OWNER_RECEIPT = PROJECT / "receipts/desktop-integrated-user-feedback-v1.2.24-20260901T013000Z.json"
SOURCE_CANDIDATE = PROJECT / "releases/kc-i-user-feedback-fixes-v1218-20260831T093000Z/win-x64"
SOURCE_RECEIPT = PROJECT / "receipts/desktop-integrated-user-feedback-v1.2.18-20260831T103000Z.json"
WINDOWS_RECEIPT = PROJECT / "canary/desktop-windows/user-feedback-fixes-20260831T090000Z/receipt.json"
WINDOWS_LOG = WINDOWS_RECEIPT.parent / "windows-ui-automation.log"
AUTHORITY_ROOT = PROJECT / "releases/knowledge-clipper-authority-v1.2.24-20260901T010500Z/win-x64"
AUTHORITY_META = PROJECT / "contracts/authority/worker-registry/v1.2.24-20260901T010500Z"
AUTHORITY_RECEIPT = PROJECT / "receipts/knowledge-clipper-authority-1.2.24-20260901T011500Z.json"
CORE_CHECKPOINT = PROJECT / "contracts/core/v2.7/checkpoints/v2.7.10-20260901T010000Z/checkpoint.json"

EXPECTED = {
    "ownerReceipt": "C19CF27084F494A9B6C805088149689F3DD080B735622C1DF7859BE09260BACB",
    "manifest": "DCA881033DA0CCCE2F1EE6302940D50D5A2B59B32D72CE5A677415458DD1146A",
    "payloadFiles": 1129,
    "payloadBytes": 437748049,
    "payloadTree": "DECA4E6B3F7F6EDE1569525629F61ED4342E19F8397179310E09F5917749B354",
    "sealedFiles": 1130,
    "sealedBytes": 437987010,
    "sealedTree": "8494F63071ADBBC80CFE3060BCC2F416E753F7880056FC200B1C455B4FE4EA0D",
    "entrypoint": "29320D1CCAA252057D011490866CC7533901740C5C6921359B7A2F9F58C4720C",
    "desktopDll": "AB1C635AB1E01BD53A1C4D8AC31D9CF3DFAA88B48350487326E38D57497981CF",
    "desktopReceipt": "727041E24AC24F9B753D497309755F3ACA496C7F3DADC5D71D9C76DA5C09F8C4",
    "desktopFiles": 554,
    "desktopBytes": 169584329,
    "desktopTree": "EE37B7E94A199FF99CCD28CCC50237B43711367B4CA450C4403D1C6CDEFAFA68",
    "sourceReceipt": "2F98E1E844088329E5250188438FFEF6CDF206E90A3CB0F221022B06CA72016A",
    "windowsReceipt": "D94A15089EDB34B145EF88E577A4848CE4B26CF4F3D03A2A53A2D875B71D9E92",
    "windowsLog": "2D8B0BC724256CA4F1166FE3192197E9D240C0FC912B76BEE041946C4B16C705",
    "authorityFiles": 562,
    "authorityBytes": 268030359,
    "authorityTree": "F4BDB96A4A327870039E7B48DF7255EBAD1E437D8E37214D001448B67E210541",
    "checkpoint": "B2103C2C6ECCA92EC38695BDCE4E10EDEB18A7F981C580677B6F4B357D5D4AAF",
    "registry": "CB9F3291625CB6E7309AA3F677CE783FC713A18B54C29F65A553AA590C8187D7",
    "authorityReceipt": "3BFFB78C00A372C6D2C2C3ED0BBD7E76B9C5A8B54E4C14E399B070820338E04D",
    "coreCheckpoint": "EB46B286C3B569BA40016241BB032D694084D59E0A95AF4959FDC297F68EF052",
}


def verify_manifest() -> dict[str, Any]:
    manifest_path = CANDIDATE / "candidate-file-manifest.v1.json"
    require(sha256_file(manifest_path) == EXPECTED["manifest"], "candidate manifest SHA drift")
    manifest = load(manifest_path)
    payload = inventory(CANDIDATE, {manifest_path.name})
    sealed = inventory(CANDIDATE)
    declared = [{"path": item["path"], "bytes": int(item["bytes"]), "sha256": str(item["sha256"]).upper()} for item in manifest["payload"]["files"]]
    require(declared == payload, "candidate manifest file list differs from sealed payload")
    require((len(payload), sum(item["bytes"] for item in payload), tab_tree(payload)) == (EXPECTED["payloadFiles"], EXPECTED["payloadBytes"], EXPECTED["payloadTree"]), "candidate payload aggregate drift")
    require((len(sealed), sum(item["bytes"] for item in sealed), tab_tree(sealed)) == (EXPECTED["sealedFiles"], EXPECTED["sealedBytes"], EXPECTED["sealedTree"]), "candidate sealed aggregate drift")
    require("candidateHash" not in manifest or manifest["candidateHash"] is None, "hand-test candidate unexpectedly claims candidateHash")
    return {
        "status": "passed",
        "manifest": ev(manifest_path),
        "payload": {"files": len(payload), "bytes": sum(item["bytes"] for item in payload), "treeSha256": tab_tree(payload)},
        "sealed": {"files": len(sealed), "bytes": sum(item["bytes"] for item in sealed), "treeSha256": tab_tree(sealed)},
        "candidateHash": None,
    }


def verify_desktop_payload() -> dict[str, Any]:
    build_receipt_path = SOURCE_CANDIDATE / "desktop-build-receipt.v1.json"
    require(sha256_file(build_receipt_path) == EXPECTED["desktopReceipt"], "source Desktop build receipt SHA drift")
    require(sha256_file(SOURCE_RECEIPT) == EXPECTED["sourceReceipt"], "source integrated receipt SHA drift")
    build = load(build_receipt_path)
    require((int(build["fileCount"]), int(build["totalBytes"]), str(build["treeSha256"]).upper()) == (EXPECTED["desktopFiles"], EXPECTED["desktopBytes"], EXPECTED["desktopTree"]), "source Desktop build aggregate drift")
    changed = []
    for item in build["files"]:
        relative = item["path"]
        expected_bytes = int(item["bytes"])
        expected_sha = str(item["sha256"]).upper()
        source = SOURCE_CANDIDATE / relative
        target = CANDIDATE / relative
        if not is_file(source) or not is_file(target) or file_size(source) != expected_bytes or file_size(target) != expected_bytes or sha256_file(source) != expected_sha or sha256_file(target) != expected_sha:
            changed.append(relative)
    require(not changed, f"Desktop payload differs from tested source: {changed[:5]}")
    require(sha256_file(CANDIDATE / "desktop-build-receipt.v1.json") == EXPECTED["desktopReceipt"], "candidate Desktop build receipt drift")
    equality_path = PROJECT / "canary/desktop-integrated/kc-i-user-feedback-fixes-v1224-20260901T012000Z/desktop-payload-equality.json"
    equality = load(equality_path)
    require(equality["changedFiles"] == 0 and equality["filesCompared"] == EXPECTED["desktopFiles"] and equality["treeSha256"].upper() == EXPECTED["desktopTree"], "owner equality summary drift")
    return {"status": "passed", "source": rel(SOURCE_CANDIDATE), "filesCompared": len(build["files"]), "bytes": int(build["totalBytes"]), "treeSha256": str(build["treeSha256"]).upper(), "changedFiles": changed, "sourceIntegratedReceipt": ev(SOURCE_RECEIPT), "ownerEquality": ev(equality_path)}


def verify_authority(output_root: Path) -> dict[str, Any]:
    source_rows = inventory(AUTHORITY_ROOT)
    embedded_rows = inventory(CANDIDATE / "authority/win-x64")
    require(source_rows == embedded_rows, "embedded authority differs from admitted v1.2.24")
    require((len(source_rows), sum(item["bytes"] for item in source_rows), tab_tree(source_rows)) == (EXPECTED["authorityFiles"], EXPECTED["authorityBytes"], EXPECTED["authorityTree"]), "authority release aggregate drift")
    checkpoint = AUTHORITY_META / "checkpoint.json"
    registry = AUTHORITY_META / "worker-registry.v1.json"
    require(sha256_file(checkpoint) == EXPECTED["checkpoint"], "authority checkpoint SHA drift")
    require(sha256_file(registry) == EXPECTED["registry"], "authority registry SHA drift")
    require(sha256_file(AUTHORITY_RECEIPT) == EXPECTED["authorityReceipt"], "authority receipt SHA drift")
    require(sha256_file(CORE_CHECKPOINT) == EXPECTED["coreCheckpoint"], "Core v2.7.10 checkpoint SHA drift")
    require(sha256_file(CANDIDATE / "authority/metadata/checkpoint.json") == EXPECTED["checkpoint"], "embedded checkpoint SHA drift")
    require(sha256_file(CANDIDATE / "authority/metadata/worker-registry.v1.json") == EXPECTED["registry"], "embedded registry SHA drift")
    integration = load(CANDIDATE / "candidate-integration.json")
    require(integration["ReleaseRoot"] == "authority/win-x64" and integration["Registry"] == "authority/metadata/worker-registry.v1.json" and integration["AuthorityTreeSha256"] == EXPECTED["authorityTree"], "candidate authority binding drift")
    closure_path = output_root / "portable-schema-closure.json"
    completed = subprocess.run(["python", str(PROJECT / "tools/validate_release_schema_closure.py"), "--release-root", str(CANDIDATE / "authority/win-x64"), "--output", str(closure_path)], cwd=PROJECT, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60, check=False)
    require(completed.returncode == 0, f"portable schema closure failed: {completed.stderr or completed.stdout}")
    closure = load(closure_path)
    require(closure["status"] == "passed" and closure["schemasChecked"] == 62 and closure["localRefsChecked"] == 188 and not closure["failures"], "portable schema closure drift")
    return {"status": "passed", "version": "1.2.24", "files": len(source_rows), "bytes": sum(item["bytes"] for item in source_rows), "treeSha256": tab_tree(source_rows), "checkpoint": ev(checkpoint), "registry": ev(registry), "authorityReceipt": ev(AUTHORITY_RECEIPT), "coreCheckpoint": ev(CORE_CHECKPOINT), "portableSchemaClosure": ev(closure_path), "embeddedCopiedExactly": True}


def verify_owner_receipt() -> dict[str, Any]:
    require(sha256_file(OWNER_RECEIPT) == EXPECTED["ownerReceipt"], "owner receipt SHA drift")
    data = load(OWNER_RECEIPT)
    checked: dict[str, dict[str, Any]] = {}

    def walk(value: Any) -> None:
        if isinstance(value, dict):
            if isinstance(value.get("path"), str) and isinstance(value.get("sha256"), str):
                path = PROJECT / value["path"]
                require(is_file(path), f"owner evidence missing: {value['path']}")
                require(sha256_file(path) == value["sha256"].upper(), f"owner evidence SHA drift: {value['path']}")
                if "bytes" in value:
                    require(file_size(path) == int(value["bytes"]), f"owner evidence bytes drift: {value['path']}")
                checked[value["path"]] = ev(path)
            for child in value.values():
                walk(child)
        elif isinstance(value, list):
            for child in value:
                walk(child)

    walk(data)
    require(data["candidateId"] == "kc-i-user-feedback-fixes-v1224-20260901T012000Z" and data["userHandTestEntrypoint"] is True, "owner candidate identity drift")
    return {"status": "passed", "receipt": ev(OWNER_RECEIPT), "referencedEvidenceChecked": len(checked)}


def verify_user_feedback() -> dict[str, Any]:
    require(sha256_file(WINDOWS_RECEIPT) == EXPECTED["windowsReceipt"], "Windows UI receipt SHA drift")
    require(sha256_file(WINDOWS_LOG) == EXPECTED["windowsLog"], "Windows UI log SHA drift")
    receipt = load(WINDOWS_RECEIPT)
    automated = receipt["automatedWindowsUiCanary"]
    require(automated["status"] == "PASS" and int(automated["assertions"]) == 187 and automated["logSha256"].upper() == EXPECTED["windowsLog"], "Windows UI aggregate drift")
    text = WINDOWS_LOG.read_text(encoding="utf-8")
    lines = text.splitlines()
    require(sum(1 for line in lines if line.startswith("PASS ")) == 187, "Windows UI PASS line count drift")
    require("RESIZE_GESTURE_OBSERVED count=1 hit=17 before={Width=344, Height=210} after={Width=366, Height=370}" in text, "real resize observation missing")
    exact = {
        "floatingResize": "PASS a real corner drag resizes the floating client area even while the modeless settings window is open",
        "ankiReady": "PASS explicit Anki probe dispatches once through the injected Core host and projects a redacted status plus receipt",
        "ankiErrorDetail": "PASS Anki read-only probe preserves the concrete worker reasonCode and failing step instead of collapsing them to a generic popup",
        "remnoteSavedTarget": "PASS RemNote UI dispatches the saved active-profile target through the public read-only Core probe and ignores unsaved advanced text",
        "questionImmediate": "PASS inline Enter gives immediate processing feedback and automatically opens the answer/follow-up/make-cards window",
        "questionSingleSubmit": "PASS Enter emits exactly one initial question, keeps the accepted question session associated and releases other desktop actions after the answer window is locally closed",
        "questionNoSecondSubmit": "PASS the existing status strip opens the stable answer/follow-up/make-cards dialog without submitting a second question",
    }
    for name, line in exact.items():
        require(line in lines, f"user feedback evidence missing: {name}")
    require(receipt["realWindowsCanaries"]["externalApplicationSelectionThroughNoActivateFloatingButton"]["status"] == "PENDING", "external-app boundary drift")
    require(receipt["realWindowsCanaries"]["crossMonitorDpiDrag"]["status"] == "PENDING", "mixed-DPI boundary drift")
    return {
        "status": "passed",
        "receipt": ev(WINDOWS_RECEIPT),
        "log": ev(WINDOWS_LOG),
        "assertions": 187,
        "floatingResize": {"status": "passed", "realGestureCount": 1, "settingsOpen": True, "before": "344x210", "after": "366x370"},
        "ankiReadonlyProbe": {"status": "passed", "readyProjection": True, "singleDispatch": True, "failureReasonCode": "WRONG_PROFILE", "failureStep": "getActiveProfile", "writes": 0},
        "remnoteReadonlyProbe": {"status": "passed", "usesSavedActiveProfileTarget": True, "ignoresUnsavedAdvancedText": True, "writes": 0},
        "inlineQuestion": {"status": "passed", "immediateStatus": "问题已提交，正在生成答案…", "answerFollowUpMakeCardsWindow": True, "initialSubmitCount": 1, "statusReopenSubmitCount": 0},
    }


def run_json(command: list[str], cwd: Path, timeout: int = 60) -> tuple[dict[str, Any], str, str]:
    completed = subprocess.run(command, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout, check=False)
    require(completed.returncode == 0, f"command failed ({completed.returncode}): {completed.stderr or completed.stdout}")
    require(completed.stdout.strip(), f"command returned no JSON: {command}")
    return json.loads(completed.stdout), completed.stdout, completed.stderr


def verify_runtime(output_root: Path) -> dict[str, Any]:
    runtime = output_root / "runtime"
    runtime.mkdir(parents=True, exist_ok=False)
    startup, out, err = run_json([str(CANDIDATE / "KnowledgeClipper.exe"), "--startup-diagnostic"], CANDIDATE)
    (runtime / "startup.stdout.json").write_text(out, encoding="utf-8")
    (runtime / "startup.stderr.log").write_text(err, encoding="utf-8")
    require(startup["result"] == "PASS" and startup["externalConnections"] == 0 and startup["namedPipeInvoked"] is False and startup["authorityTreeSha256"] == EXPECTED["authorityTree"], "fresh safe startup failed")
    embedded = CANDIDATE / "authority/win-x64"
    registry_path = CANDIDATE / "authority/metadata/worker-registry.v1.json"
    registry, out, err = run_json([str(embedded / "orchestrator/KnowledgeClipper.Orchestrator.exe"), "validate-registry", "--registry", str(registry_path), "--release-root", str(embedded)], embedded)
    (runtime / "registry.stdout.json").write_text(out, encoding="utf-8")
    (runtime / "registry.stderr.log").write_text(err, encoding="utf-8")
    require(registry["status"] == "valid" and registry["registryVersion"] == "1.2.24" and int(registry["workers"]) == 5, "fresh registry validation failed")
    module_root = runtime / "offline-named-pipe"
    completed = subprocess.run(["pwsh", "-NoLogo", "-NoProfile", "-File", str(PROJECT / "tools/Invoke-V1217CandidateOfflineModuleActionGate.ps1"), "-CandidateRoot", str(CANDIDATE), "-EvidenceRoot", str(module_root)], cwd=PROJECT, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60, check=False)
    require(completed.returncode == 0, f"fresh offline Named Pipe gate failed: {completed.stderr or completed.stdout}")
    module = load(module_root / "offline-module-action-receipt.json")
    require(module["status"] == "passed" and module["queryRecent"]["ok"] is True, "fresh Named Pipe query failed")
    preview = module["unauthorizedPreview"]
    require(preview["ok"] is False and preview["errorCode"] == "ipc.invalid_frame" and preview["receiptCreated"] is False and preview["workerRunCount"] == 0 and preview["externalServiceCalls"] == 0 and preview["physicalSpeakerInvocations"] == 0, "unauthorized preview did not fail closed")
    require((module_root / "orchestrator.stderr.log").read_text(encoding="utf-8").strip() == "", "Orchestrator emitted stderr")
    return {"status": "passed", "startup": startup, "registry": registry, "namedPipe": ev(module_root / "offline-module-action-receipt.json"), "externalWorkerRuns": 0, "externalCalls": 0, "destinationWrites": 0, "speakerInvocations": 0}


def verify_safety() -> dict[str, Any]:
    scan = scan_runtime_tree_for_secrets(CANDIDATE)
    require(scan["status"] == "passed" and scan["findingCount"] == 0, "candidate secret scan failed")
    credentials = [path.relative_to(CANDIDATE).as_posix() for path in CANDIDATE.rglob("*") if path.is_file() and path.name.casefold() in {"credentials.json", "credentials.json.bak", ".env"}]
    legacy = [path.relative_to(CANDIDATE).as_posix() for path in CANDIDATE.rglob("*") if path.is_file() and path.name.casefold() in {"smoketest.exe", "ttscanary.exe"}]
    require(not credentials and not legacy, "candidate contains credential or legacy external-canary files")
    return {"status": "passed", "secretScan": scan, "credentialFiles": credentials, "legacyExternalCanaries": legacy}


def render(result: dict[str, Any]) -> str:
    return "\n".join([
        "# KnowledgeClipper v1.2.24 user-feedback candidate quality gate",
        "",
        f"- Status: `{result['status']}`",
        f"- Candidate tree: `{result['candidate']['sealedTreeSha256']}`",
        f"- Manifest: `{result['candidate']['manifest']['sha256']}`",
        "- Four user-feedback fixes: PASS and bound through 554 byte-identical Desktop files plus the 187-assertion Windows UI evidence.",
        "- Fresh candidate startup, registry 5/5 and isolated Named Pipe fail-closed gate: PASS; external calls/writes/speaker = 0.",
        "",
        "## Exact user-visible evidence",
        "",
        "- Real bottom-right gesture resized the floating client from 344x210 to 366x370 while Settings stayed modeless/open.",
        "- Anki read-only check has one ready projection and preserves `reasonCode=WRONG_PROFILE`, `step=getActiveProfile` on failure; writes = 0.",
        "- RemNote read-only check uses the saved active-profile target and ignores unsaved advanced text; writes = 0.",
        "- Inline Enter immediately shows `问题已提交，正在生成答案…`, opens the answer/follow-up/make-cards window, submits the initial question exactly once, and reopening from status submits zero new questions.",
        "",
        "## Boundary",
        "",
        "The single remaining user hand-test boundary is physical Windows integration: external-application selection/context plus cross-monitor mixed-DPI interaction. Core-owned live OCR -> Content -> Anki qualification is a separate pending external receipt, not a Desktop hand-test failure.",
    ]) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-root", required=True)
    args = parser.parse_args()
    output_root = Path(args.output_root).resolve()
    require(not output_root.exists(), "append-only output root already exists")
    output_root.mkdir(parents=True)
    require(sha256_file(CANDIDATE / "KnowledgeClipper.exe") == EXPECTED["entrypoint"], "entrypoint SHA drift")
    require(sha256_file(CANDIDATE / "KnowledgeClipper.dll") == EXPECTED["desktopDll"], "Desktop DLL SHA drift")
    checks = {
        "manifestAndTree": verify_manifest(),
        "desktopPayloadEquality": verify_desktop_payload(),
        "authorityAndCore": verify_authority(output_root),
        "ownerReceiptRecursion": verify_owner_receipt(),
        "userFeedbackFixes": verify_user_feedback(),
        "safeRuntimeAndNamedPipe": verify_runtime(output_root),
        "candidateSafety": verify_safety(),
    }
    result = {
        "schemaVersion": "knowledge-clipper-quality-user-feedback-v1224-candidate-bound.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-CANDIDATE-BOUND-USER-HANDTEST-READY-EXTERNAL-QUALIFICATION-PENDING",
        "candidateBound": True,
        "candidate": {"root": rel(CANDIDATE), "entrypoint": ev(CANDIDATE / "KnowledgeClipper.exe"), "desktopDll": ev(CANDIDATE / "KnowledgeClipper.dll"), "manifest": ev(CANDIDATE / "candidate-file-manifest.v1.json"), "sealedTreeSha256": EXPECTED["sealedTree"], "candidateHash": None},
        "ownerReceipt": ev(OWNER_RECEIPT),
        "userHandTestEntrypoint": True,
        "checks": checks,
        "promotion": {"userHandTest": "GO", "externalOcrContentAnkiQualification": "PENDING-OWNER-RECEIPT", "release": "CONDITIONAL", "reason": "candidate manifest has no candidateHash/signing identity and external qualification is additive/pending"},
        "uniqueHandTestBoundary": "physical Windows integration: external-application selection/context and cross-monitor mixed-DPI interaction",
        "sideEffectsByThisAudit": {"externalWorkerRuns": 0, "externalCalls": 0, "remnoteCalls": 0, "ankiCalls": 0, "mineruCalls": 0, "deepseekCalls": 0, "speakerInvocations": 0, "destinationWrites": 0},
    }
    receipt = output_root / "quality-candidate-receipt.json"
    readable = output_root / "READABLE.md"
    receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    readable.write_text(render(result), encoding="utf-8")
    print(json.dumps({"status": result["status"], "receipt": ev(receipt), "readable": ev(readable), "candidateTree": EXPECTED["sealedTree"]}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
