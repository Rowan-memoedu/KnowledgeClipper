from __future__ import annotations

import hashlib
import json
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


PROJECT = Path(__file__).resolve().parents[1]
CANDIDATE_ID = "kc-i-ocr-v1215-20260830T121500Z"
CANDIDATE = PROJECT / "releases" / CANDIDATE_ID / "win-x64"
EVIDENCE = PROJECT / "canary" / "desktop-integrated" / CANDIDATE_ID
OUTPUT = PROJECT / "receipts" / "desktop-ocr-human-resolution-integrated-v1.2.15-20260830T121900Z.json"

MODULE_RECEIPT = PROJECT / "receipts" / "desktop-ocr-human-resolution-v1.3-20260830T120300Z.json"
WINDOWS_RECEIPT = PROJECT / "canary" / "desktop-windows" / "20260830T121624Z" / "receipt.json"
WINDOWS_LOG = WINDOWS_RECEIPT.parent / "windows-ui-automation.log"
CORE_CHECKPOINT = PROJECT / "contracts" / "core" / "v2.6" / "checkpoints" / "v2.6.0-20260830T120900Z" / "checkpoint.json"
CORE_QUALIFICATION = PROJECT / "canary" / "core-v2.6.0-offline" / "20260830T120500Z" / "authority-qualification.receipt.json"
AUTHORITY = PROJECT / "contracts" / "authority" / "worker-registry" / "v1.2.15-20260830T121200Z"
AUTHORITY_RECEIPT = PROJECT / "receipts" / "knowledge-clipper-authority-1.2.15-20260830T121200Z.json"
QUALITY_RECEIPT = PROJECT / "canary" / "product-gap-audit" / "20260830T121557Z-core-authority-v1215-offline-increment" / "audit-receipt.json"
CHECKLIST = EVIDENCE / "USER-HANDTEST-CHECKLIST.md"

EXPECTED = {
    "moduleReceipt": "A6C36435520C0AEB12ED90C45D68F9A47C7818A11B1B61714F2ECED857AF0A7D",
    "coreCheckpoint": "F913D27758E0AD20859B7C0C2B5B8756B0D8487E346FE0FD9732D3D480C34D65",
    "coreQualification": "A17AEBF6D992CE3DA9B7DEFDA549523B22FAF7BA397937E2D0DCB55B4DE73312",
    "authorityCheckpoint": "B5F1EF63EF812D86C360D0AFB46ECDFB220C96C24CD6CF2252E18411D82084A1",
    "authorityRegistry": "0EA0131DDA8A13667FBD9F758E05C27C8C8ACA7ED982939CCBE6E042D8A35830",
    "authorityTree": "E5067C1B93A47B5650A2B1A049F974A73F66E31C3A5CEFC5DBD4D33B623190D4",
    "authorityReceipt": "D3D981F455817F770BF80CBDD995A82BF1BF26AB9C6B7A2E61544EA43D11210F",
    "qualityReceipt": "4AA7B23C5AF75F4A895B6D6936355FBED3AEF3E5306B7397AAEC4591D0FD8BF1",
    "manifest": "D3843FDED77D58AC3376F25398C312203E3B3812B749BEABAD2744817A81D098",
    "candidateTree": "46CB26033A40FF3A15875EF155CB16D53F0BF092E2577E40B43AA176F6B92EFA",
    "entrypoint": "29320D1CCAA252057D011490866CC7533901740C5C6921359B7A2F9F58C4720C",
    "applicationDll": "711575C645629019DAD182F02988378996C57F9B7109630CE50B175716DE3623",
}


class GateFailure(RuntimeError):
    pass


def require(value: bool, message: str) -> None:
    if not value:
        raise GateFailure(message)


def sha(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def inventory(root: Path) -> dict[str, Any]:
    rows: list[str] = []
    count = 0
    total = 0
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix()):
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


def evidence(path: Path) -> dict[str, Any]:
    return {
        "path": path.relative_to(PROJECT).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha(path),
    }


def write_once(path: Path, value: dict[str, Any]) -> None:
    require(not path.exists(), f"append-only output already exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main() -> None:
    require(not OUTPUT.exists(), "append-only receipt already exists")
    manifest_path = CANDIDATE / "candidate-file-manifest.v1.json"
    build_receipt_path = CANDIDATE / "desktop-build-receipt.v1.json"
    manifest_gate_path = EVIDENCE / "manifest-tree-gate.json"
    drift_path = EVIDENCE / "desktop-payload-drift.json"
    startup_path = EVIDENCE / "startup-diagnostic.stdout.log"
    single_path = EVIDENCE / "single-instance-diagnostic.stdout.log"
    registry_path = EVIDENCE / "authority-validate-registry.log"
    integration_path = EVIDENCE / "integration-startup-canary.stdout.log"
    secret_path = EVIDENCE / "secret-scan.json"

    for path in (
        manifest_path,
        build_receipt_path,
        manifest_gate_path,
        drift_path,
        startup_path,
        single_path,
        registry_path,
        integration_path,
        MODULE_RECEIPT,
        WINDOWS_RECEIPT,
        WINDOWS_LOG,
        CORE_CHECKPOINT,
        CORE_QUALIFICATION,
        AUTHORITY / "checkpoint.json",
        AUTHORITY / "worker-registry.v1.json",
        AUTHORITY_RECEIPT,
        QUALITY_RECEIPT,
        CHECKLIST,
    ):
        require(path.is_file(), f"required evidence missing: {path}")

    require(sha(MODULE_RECEIPT) == EXPECTED["moduleReceipt"], "Desktop module receipt drift")
    require(sha(CORE_CHECKPOINT) == EXPECTED["coreCheckpoint"], "Core checkpoint drift")
    require(sha(CORE_QUALIFICATION) == EXPECTED["coreQualification"], "Core qualification drift")
    require(sha(AUTHORITY / "checkpoint.json") == EXPECTED["authorityCheckpoint"], "authority checkpoint drift")
    require(sha(AUTHORITY / "worker-registry.v1.json") == EXPECTED["authorityRegistry"], "authority registry drift")
    require(sha(AUTHORITY_RECEIPT) == EXPECTED["authorityReceipt"], "authority receipt drift")
    require(sha(QUALITY_RECEIPT) == EXPECTED["qualityReceipt"], "Quality increment receipt drift")
    require(sha(manifest_path) == EXPECTED["manifest"], "candidate manifest drift")
    require(sha(CANDIDATE / "KnowledgeClipper.exe") == EXPECTED["entrypoint"], "entrypoint drift")
    require(sha(CANDIDATE / "KnowledgeClipper.dll") == EXPECTED["applicationDll"], "Desktop application DLL drift")

    manifest = load(manifest_path)
    manifest_gate = load(manifest_gate_path)
    drift = load(drift_path)
    startup = load(startup_path)
    single = load(single_path)
    registry = load(registry_path)
    integration = load(integration_path)
    module = load(MODULE_RECEIPT)
    windows = load(WINDOWS_RECEIPT)
    core = load(CORE_QUALIFICATION)

    sealed = inventory(CANDIDATE)
    require(sealed["treeSha256"] == EXPECTED["candidateTree"], "candidate sealed tree drift")
    require(sealed["fileCount"] == 1105 and sealed["totalBytes"] == 437389476, "candidate sealed aggregate drift")
    require(manifest["candidateId"] == CANDIDATE_ID, "candidate id mismatch")
    require(manifest["authority"]["releaseTreeSha256"] == EXPECTED["authorityTree"], "manifest authority tree mismatch")
    require(manifest_gate["status"] == "PASS" and manifest_gate["candidateTreeSha256"] == EXPECTED["candidateTree"], "manifest/tree gate failed")
    require(drift["status"] == "PASS-EXPECTED-DRIFT" and drift["changedFiles"] == 13, "Desktop drift gate failed")
    require(module["status"] == "PASS-DESKTOP-OCR-HUMAN-RESOLUTION-V1.3-PENDING-NEXT-AUTHORITY", "module checkpoint mismatch")
    windows_ui = windows["automatedWindowsUiCanary"]
    require(windows_ui["status"] == "PASS" and windows_ui["assertions"] == 173, "Windows UI Canary failed")
    require(startup["result"] == "PASS" and startup["externalConnections"] == 0, "safe startup failed")
    require(single["userFeedbackMode"] == "activate-existing-window-and-message-box", "single-instance behavior regressed")
    require(registry["status"] == "valid" and registry["registryVersion"] == "1.2.15" and registry["workers"] == 5, "registry 5/5 failed")
    require(integration["result"] == "PASS" and integration["orchestratorStartedByDesktop"] and integration["namedPipeInvoked"], "built-in Orchestrator/Named Pipe failed")
    require(not any(integration[key] for key in ("externalWorkerInvoked", "physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")), "integration Canary crossed side-effect boundary")

    flows = core["qualifiedFlows"]
    ocr = flows["ocrHumanResolution"]
    original = flows["originalMedia"]
    require(ocr["actions"] == ["accept_original", "submit_correction", "cancel"], "OCR public actions mismatch")
    require(ocr["rerunOcr"] is False and ocr["destinationsRestored"] is True and ocr["commandReplayIdempotent"] is True, "OCR continuation semantics mismatch")
    require(original["anki"] == "ordered-original-images.v1 supported", "Anki original media is not admitted")
    require(original["remnote"] == "unsupported:mcp.media_write_unavailable", "RemNote original media must remain unsupported")

    sys.path.insert(0, str((PROJECT / "tools").resolve()))
    import verify_settings_control_plane as settings_gate

    secret_scan = settings_gate.scan_runtime_tree_for_secrets(CANDIDATE)
    require(secret_scan["status"] == "passed" and secret_scan["findingCount"] == 0, "candidate secret scan failed")
    write_once(secret_path, secret_scan)

    receipt = {
        "schemaVersion": "knowledge-clipper-desktop-ocr-integrated-checkpoint.v1",
        "status": "PASS-AUTOMATED-AUTHORITY-V1.2.15-USER-HANDTEST",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "candidateId": CANDIDATE_ID,
        "userHandTestEntrypoint": True,
        "candidate": {
            "root": CANDIDATE.relative_to(PROJECT).as_posix(),
            "entrypoint": (CANDIDATE / "KnowledgeClipper.exe").relative_to(PROJECT).as_posix(),
            "entrypointSha256": EXPECTED["entrypoint"],
            "applicationDllSha256": EXPECTED["applicationDll"],
            "manifest": evidence(manifest_path),
            "sealed": sealed,
        },
        "desktop": {
            "moduleReceipt": evidence(MODULE_RECEIPT),
            "buildReceipt": evidence(build_receipt_path),
            "automationVersion": "1.3.0",
            "expectedDriftFromV1214": evidence(drift_path),
            "windowsUi": {**evidence(WINDOWS_RECEIPT), "assertions": 173},
            "userHandTestChecklist": evidence(CHECKLIST),
        },
        "authority": {
            "version": "1.2.15",
            "checkpoint": evidence(AUTHORITY / "checkpoint.json"),
            "registry": evidence(AUTHORITY / "worker-registry.v1.json"),
            "receipt": evidence(AUTHORITY_RECEIPT),
            "releaseTreeSha256": EXPECTED["authorityTree"],
            "coreCheckpoint": evidence(CORE_CHECKPOINT),
            "coreQualification": evidence(CORE_QUALIFICATION),
            "qualityIncrement": evidence(QUALITY_RECEIPT),
        },
        "qualified": {
            "ocrWaitingInput": True,
            "ocrActions": ocr["actions"],
            "ocrNewImmutableRun": ocr["resume"],
            "ocrRerun": ocr["rerunOcr"],
            "destinationsRestored": ocr["destinationsRestored"],
            "ankiOriginalMedia": original["anki"],
            "remnoteOriginalMedia": original["remnote"],
        },
        "automatedEvidence": {
            "safeStartup": evidence(startup_path),
            "singleInstance": evidence(single_path),
            "registry5Of5": evidence(registry_path),
            "builtInOrchestratorNamedPipe": evidence(integration_path),
            "manifestTree": evidence(manifest_gate_path),
            "secretScan": evidence(secret_path),
        },
        "conditional": {
            "speechVoiceCatalogPersistenceAndPreviewCoreAction": "conditional-not-wired",
            "remnoteReadonlyProbeCoreDispatch": "conditional-module-cli-admitted",
            "remnoteOriginalMedia": "unsupported:mcp.media_write_unavailable",
            "productionRetentionAutomaticInvocation": "disabled",
        },
        "safety": {
            "externalCallsDuringDesktopIntegration": 0,
            "workersRunDuringDesktopIntegration": 0,
            "physicalSpeakerInvocations": 0,
            "existingUserProcessTerminated": False,
            "secretFindings": 0,
        },
    }
    write_once(OUTPUT, receipt)
    print(json.dumps({
        "status": receipt["status"],
        "entrypoint": str(CANDIDATE / "KnowledgeClipper.exe"),
        "candidateTreeSha256": sealed["treeSha256"],
        "manifestSha256": sha(manifest_path),
        "receipt": str(OUTPUT),
        "receiptSha256": sha(OUTPUT),
        "checklist": str(CHECKLIST),
    }, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except (GateFailure, OSError, ValueError, KeyError, json.JSONDecodeError) as exc:
        print(json.dumps({"status": "FAIL", "error": str(exc)}, ensure_ascii=False))
        raise SystemExit(1)
