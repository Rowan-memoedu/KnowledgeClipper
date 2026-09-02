from __future__ import annotations

import hashlib
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


PROJECT = Path(__file__).resolve().parents[1]
CANDIDATE_ID = "kc-i-dpapi-v1211-20260828T183000Z"
CANDIDATE = PROJECT / "releases" / CANDIDATE_ID / "win-x64"
BASELINE = PROJECT / "releases/kc-i-settings-v1211-20260828T095200Z/win-x64"
CANARY = PROJECT / "canary/desktop-integrated" / CANDIDATE_ID
WINDOWS_RECEIPT = PROJECT / "canary/desktop-windows/credential-dpapi-v1211-20260828T181500Z/receipt.json"
VISUAL = PROJECT / "canary/desktop-visual/knowledge-clipper-credential-dpapi-v1211-20260828T182000Z"
BUILD_LOG = PROJECT / "canary/desktop-security-v1211/credential-dpapi-v1211-20260828T183500Z/desktop-build-and-test.log"
AUTHORITY_SOURCE = PROJECT / "releases/knowledge-clipper-authority-v1.2.11-20260828T092800Z/win-x64"
AUTHORITY_CONTRACT = PROJECT / "contracts/authority/worker-registry/v1.2.11-20260828T092800Z"
AUTHORITY_RECEIPT = PROJECT / "receipts/knowledge-clipper-authority-1.2.11-20260828T093000Z.json"
SETTINGS_CHECKPOINT = PROJECT / "contracts/core/v2.4/settings-control/checkpoints/v2.4.8-20260828T092700Z/checkpoint.json"
V129_INVALIDATION = PROJECT / "contracts/authority/worker-registry/v1.2.9-20260828T091100Z/invalidation-receipt.v1.json"
V1210_INVALIDATION = PROJECT / "contracts/authority/worker-registry/v1.2.10-20260828T092300Z/invalidation-receipt.v1.json"
BASELINE_INVALIDATION = BASELINE.parent / "INVALIDATED-NEVER-PROMOTED.json"
PHASE_B_A = PROJECT / "canary/phase-b-v1.2.11-non-remnote/20260828T095000Z/a/path-receipt.json"
PHASE_B_D = PROJECT / "canary/phase-b-v1.2.11-non-remnote/20260828T095200Z/d/path-receipt.json"
PHASE_B_AGGREGATE = PROJECT / "canary/phase-b-v1.2.11-non-remnote/qualification-20260828T095701Z/qualification-receipt.json"
OUTPUT = PROJECT / "receipts/desktop-integrated-dpapi-v1.2.11-20260828T183000Z-module-checkpoint.json"

EXPECTED = {
    "authorityTree": "ACE70D34A5689CAD65C2FB9BFF81BC9E23F0C24650698A43FED8AD502D42F754",
    "authorityCheckpoint": "A01D0E65DEABA5078D7A57EF57FFFFF547DB3F5BBD03074247135DF463CA7F1C",
    "authorityRegistry": "7DFB0B2EC80FD88142C7DB352A69DA027B2E80F8D7584FBA5C7CD9EE85D9E364",
    "authorityDependency": "01AEC92E8567E7876928776E39A656E6BEB31C2AA897D6D4235CCDBA39E0A418",
    "authorityReceipt": "B56EF33D95BC4D5086F9E4B04020932F22FCDFB83CF4699ADF913390C14A3DB3",
    "settingsCheckpoint": "3306DF313EB6AACF52AE1A447F3F095C95295605A2D68A451ADA5D00AE41C846",
    "windowsReceipt": "23CBC25724D4FC6768F89A57CB7C420EA8229D3BD073F6BBD327B536F41A7736",
    "phaseBA": "590CA2D8EF3896D1BC398D578B1285361B3EBD448AE4AF35E2E321971D659882",
    "phaseBD": "6D5226B80D03DD9934C0A0969FCABBCBD8B76D1FF8E4C9E7C7B07B27F4A7E097",
    "phaseBAggregate": "40590910238E78C84E9FD70A3564ADFE8BD1BBA32D048396CBDD88E76F07AF6A",
}


class GateError(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateError(message)


def sha(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def inventory(root: Path, excluded: set[str] | None = None) -> dict[str, Any]:
    excluded = excluded or set()
    files: list[dict[str, Any]] = []
    rows: list[str] = []
    total = 0
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        if relative in excluded:
            continue
        size = path.stat().st_size
        digest = sha(path)
        files.append({"path": relative, "bytes": size, "sha256": digest})
        rows.append(f"{relative}\t{size}\t{digest.lower()}")
        total += size
    return {
        "files": files,
        "fileCount": len(files),
        "totalBytes": total,
        "treeSha256": hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper(),
        "treeAlgorithm": "sha256(utf8 ordinal(relativePath + TAB + bytes + TAB + lowercaseSha256) joined by LF)",
    }


def evidence(path: Path) -> dict[str, Any]:
    require(path.is_file(), f"missing evidence: {path}")
    return {"path": path.relative_to(PROJECT).as_posix(), "bytes": path.stat().st_size, "sha256": sha(path)}


def process_exists(pid: int) -> bool:
    result = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"], capture_output=True, text=True, encoding="utf-8", errors="replace", check=False)
    return result.returncode == 0 and f'"{pid}"' in result.stdout


def main() -> int:
    require(not OUTPUT.exists(), f"append-only receipt already exists: {OUTPUT}")
    for path in (CANDIDATE, BASELINE, CANARY, VISUAL, AUTHORITY_SOURCE, AUTHORITY_CONTRACT):
        require(path.is_dir(), f"missing required directory: {path}")

    manifest_path = CANDIDATE / "candidate-file-manifest.v1.json"
    manifest = load(manifest_path)
    require(manifest["candidateId"] == CANDIDATE_ID, "candidate identity mismatch")
    payload = inventory(CANDIDATE, {"candidate-file-manifest.v1.json"})
    require(all(payload[key] == manifest["payload"][key] for key in ("fileCount", "totalBytes", "treeSha256")), "manifest payload mismatch")
    sealed = inventory(CANDIDATE)
    authority = inventory(AUTHORITY_SOURCE)
    embedded_authority = inventory(CANDIDATE / "authority/win-x64")
    require(authority["fileCount"] == 214 and authority["totalBytes"] == 249697213 and authority["treeSha256"] == EXPECTED["authorityTree"], "source authority mismatch")
    require(embedded_authority == authority, "embedded authority differs from admitted source")

    exact_files = {
        AUTHORITY_CONTRACT / "checkpoint.json": EXPECTED["authorityCheckpoint"],
        AUTHORITY_CONTRACT / "worker-registry.v1.json": EXPECTED["authorityRegistry"],
        AUTHORITY_CONTRACT / "dependency-manifest.v1.json": EXPECTED["authorityDependency"],
        AUTHORITY_RECEIPT: EXPECTED["authorityReceipt"],
        SETTINGS_CHECKPOINT: EXPECTED["settingsCheckpoint"],
        PHASE_B_A: EXPECTED["phaseBA"],
        PHASE_B_D: EXPECTED["phaseBD"],
        PHASE_B_AGGREGATE: EXPECTED["phaseBAggregate"],
    }
    for path, expected in exact_files.items():
        require(path.is_file() and sha(path) == expected, f"bound evidence drift: {path}")

    checkpoint = load(AUTHORITY_CONTRACT / "checkpoint.json")
    require(checkpoint["checkpointId"] == "knowledge-clipper-authority-1.2.11-20260828T093000Z" and checkpoint["admissionStatus"] == "authority-admitted", "authority is not admitted v1.2.11")
    settings = load(SETTINGS_CHECKPOINT)
    require(settings["checkpointVersion"] == "2.4.8" and settings["status"] == "passed-core-runtime-authority-ready", "Core settings v2.4.8 checkpoint failed")

    windows = load(WINDOWS_RECEIPT)
    require(sha(WINDOWS_RECEIPT) == EXPECTED["windowsReceipt"], "Windows UI receipt hash drift")
    require(windows["automatedWindowsUiCanary"] == {"status": "PASS", "assertions": 131, "log": "windows-ui-automation.log", "logSha256": windows["automatedWindowsUiCanary"]["logSha256"]}, "Windows UI Canary did not pass 131 assertions")
    require(windows["desktopToRealRemNoteE2E"]["status"] == "NOT_RUN", "external E2E boundary drift")

    build_text = BUILD_LOG.read_text(encoding="utf-8-sig")
    require("PASS desktop automation build and tests" in build_text and "FAIL" not in build_text, "Desktop full build-and-test log failed")
    visual = inventory(VISUAL)
    visual_pngs = [item for item in visual["files"] if item["path"].endswith(".png")]
    require(len(visual_pngs) == 16 and len([item for item in visual_pngs if item["path"].startswith("settings-")]) == 7, "visual preview inventory mismatch")

    drift_path = CANARY / "desktop-payload-drift.json"
    drift = load(drift_path)
    expected_changed = {"KnowledgeClipper.deps.json", "KnowledgeClipper.dll", "KnowledgeClipper.Orchestrator.dll"}
    require(drift["status"] == "PASS-EXPECTED-DRIFT" and drift["currentFileCount"] == 244 and drift["unchangedFiles"] == 241 and drift["changedFiles"] == 3, "Desktop drift aggregate mismatch")
    require({item["path"] for item in drift["changes"]} == expected_changed, "Desktop drift scope mismatch")

    manifest_gate = load(CANARY / "manifest-tree-gate.json")
    startup = load(CANARY / "startup-diagnostic.stdout.log")
    single = load(CANARY / "single-instance-diagnostic.stdout.log")
    integrated = load(CANARY / "integration-startup-canary.stdout.log")
    registry = load(CANARY / "authority-validate-registry.log")
    require(manifest_gate["status"] == "PASS" and manifest_gate["candidateTreeSha256"] == sealed["treeSha256"], "manifest/tree gate mismatch")
    require(startup["result"] == "PASS" and startup["candidateOrchestratorPathsVerified"] is True and startup["externalConnections"] == 0 and startup["namedPipeInvoked"] is False, "safe startup failed")
    require(single["existingInstanceDetected"] is True and single["existingProcessId"] == 5976 and single["userFeedbackMode"] == "activate-existing-window-and-message-box", "single-instance regression failed")
    require(process_exists(5976), "pre-existing PID 5976 was not preserved")
    require(integrated["result"] == "PASS" and integrated["orchestratorStartedByDesktop"] is True and integrated["action"] == "task.status.query-recent", "built-in Orchestrator/IPC query failed")
    require(not process_exists(int(integrated["orchestratorProcessId"])), "Canary-owned Orchestrator still runs")
    require(not any(integrated[key] for key in ("externalWorkerInvoked", "physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")), "Desktop Canary crossed external side-effect boundary")
    require(registry == {"schemaVersion": "1.0.0", "status": "valid", "registryVersion": "1.2.11", "workers": 5}, "candidate registry validation failed")

    for invalidation in (V129_INVALIDATION, V1210_INVALIDATION, BASELINE_INVALIDATION):
        require(load(invalidation)["status"] == "invalidated-never-promoted", f"invalidated input boundary missing: {invalidation}")

    credential_files = [path.relative_to(CANDIDATE).as_posix() for path in CANDIDATE.rglob("credentials.json")]
    markers = (b"canary-deepseek-local-only", b"canary-mineru-local-only", b"legacy-dpapi-deepseek-value", b"legacy-dpapi-mineru-value", b"legacy-migration-must-remain-on-failure")
    secret_hits: list[str] = []
    for path in CANDIDATE.rglob("*"):
        if path.is_file():
            data = path.read_bytes()
            if any(marker in data for marker in markers):
                secret_hits.append(path.relative_to(CANDIDATE).as_posix())
    require(not credential_files and not secret_hits, f"credential material leaked into candidate: files={credential_files} hits={secret_hits}")

    phase_b_aggregate = load(PHASE_B_AGGREGATE)
    require(phase_b_aggregate.get("status") == "passed-conditional", "Phase B aggregate boundary mismatch")

    receipt = {
        "schemaVersion": "knowledge-clipper-desktop-integrated-dpapi-module-checkpoint.v1",
        "status": "PASS-INTEGRATED-DPAPI-CURRENTUSER-AUTHORITY-V1.2.11-MODULE-CHECKPOINT",
        "createdAt": datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z"),
        "candidateId": CANDIDATE_ID,
        "candidate": {
            "root": CANDIDATE.relative_to(PROJECT).as_posix(),
            "entrypoint": (CANDIDATE / "KnowledgeClipper.exe").relative_to(PROJECT).as_posix(),
            "entrypointSha256": sha(CANDIDATE / "KnowledgeClipper.exe"),
            "applicationDllSha256": sha(CANDIDATE / "KnowledgeClipper.dll"),
            "manifest": evidence(manifest_path),
            **sealed,
        },
        "desktopPayloadDrift": {
            "baselineCandidate": BASELINE.parent.name,
            "baselineUsedAsPayloadSource": False,
            "evidence": evidence(drift_path),
            "currentFiles": 244,
            "unchangedFiles": 241,
            "changedFiles": 3,
            "changedPaths": sorted(expected_changed),
            "classification": drift["classification"],
            "uiRetestRequired": True,
            "uiRetestCompleted": True,
        },
        "credentialProtection": {
            "storage": "Windows DPAPI CurrentUser",
            "plaintextJsonPrimary": False,
            "encryptedBackup": True,
            "legacyMigration": "read-once, encrypted write plus exact readback before source replacement",
            "migrationFailure": "fail-closed and preserves unconfirmed legacy source",
            "processInjectionOnly": ["AI_PRODUCER_API_KEY", "MINERU_API_TOKEN"],
            "taskPackageSecretReferences": [],
            "candidateCredentialFiles": len(credential_files),
            "candidateKnownSecretHits": len(secret_hits),
        },
        "desktopValidation": {
            "buildAndTest": evidence(BUILD_LOG),
            "windowsUi": evidence(WINDOWS_RECEIPT),
            "windowsUiAssertions": 131,
            "visualPreview": {**visual, "root": VISUAL.relative_to(PROJECT).as_posix(), "pngFiles": len(visual_pngs), "settingsPages": 7},
        },
        "authority": {
            "version": "1.2.11",
            "checkpointId": checkpoint["checkpointId"],
            "checkpoint": evidence(AUTHORITY_CONTRACT / "checkpoint.json"),
            "registry": evidence(AUTHORITY_CONTRACT / "worker-registry.v1.json"),
            "dependencyManifest": evidence(AUTHORITY_CONTRACT / "dependency-manifest.v1.json"),
            "receipt": evidence(AUTHORITY_RECEIPT),
            "embeddedExactly": True,
            "releaseFiles": authority["fileCount"],
            "releaseBytes": authority["totalBytes"],
            "releaseTreeSha256": authority["treeSha256"],
        },
        "settingsComponent": {"version": "2.4.8", "runtime": settings["core"]["version"], "checkpoint": evidence(SETTINGS_CHECKPOINT)},
        "automatedCanaries": {
            "manifestTree": evidence(CANARY / "manifest-tree-gate.json"),
            "desktopPayloadDrift": evidence(drift_path),
            "safeStartup": evidence(CANARY / "startup-diagnostic.stdout.log"),
            "singleInstance": {**evidence(CANARY / "single-instance-diagnostic.stdout.log"), "existingProcessId": 5976, "existingProcessPreserved": True},
            "builtInOrchestrator": {**evidence(CANARY / "integration-startup-canary.stdout.log"), "processId": integrated["orchestratorProcessId"], "processStoppedAfterCanary": True},
            "registry": evidence(CANARY / "authority-validate-registry.log"),
        },
        "invalidatedNeverPromoted": [evidence(V129_INVALIDATION), evidence(V1210_INVALIDATION), evidence(BASELINE_INVALIDATION)],
        "backendCandidateBoundary": {
            "status": "passed-conditional",
            "aContentAnki": evidence(PHASE_B_A),
            "dContentAnkiSpeech": evidence(PHASE_B_D),
            "aggregate": evidence(PHASE_B_AGGREGATE),
            "desktopRebaseInvokedPhysicalSpeaker": False,
            "remNote": "conditional-read-timeout-no-new-write",
            "mineru": "conditional-process-credential-unavailable",
        },
        "safety": {
            "externalWorkersInvoked": 0,
            "externalProductionApisInvoked": 0,
            "physicalSpeakerInvokedByDesktopRebase": 0,
            "remNoteInvokedByDesktopRebase": 0,
            "ankiInvokedByDesktopRebase": 0,
            "mineruInvokedByDesktopRebase": 0,
            "preExistingDesktopProcessTerminated": False,
        },
        "releaseBoundary": {
            "classification": "module-checkpoint",
            "userHandTestEntrypoint": False,
            "qualityCandidateBoundGatePending": True,
            "repeatUiHandTestRequested": False,
            "reason": "Credential security and current authority binding changed Desktop code, so automated Windows UI was rerun; Quality still owns promotion.",
        },
        "noGoBoundaries": [
            "manual RemNote route remains conditional because read/reconcile times out; no new write was attempted",
            "MinerU credential-backed path was not run",
            "physical external-application selection, real image clipboard, screenshot drag and mixed-DPI cross-screen drag remain environment/user checks",
            "no sealed installer or code signing",
        ],
    }
    OUTPUT.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "status": receipt["status"],
        "candidateRoot": str(CANDIDATE),
        "manifestSha256": sha(manifest_path),
        "candidateTreeSha256": sealed["treeSha256"],
        "candidateFiles": sealed["fileCount"],
        "candidateBytes": sealed["totalBytes"],
        "receipt": str(OUTPUT),
        "receiptSha256": sha(OUTPUT),
        "userHandTestEntrypoint": False,
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (GateError, OSError, ValueError, KeyError, json.JSONDecodeError, subprocess.SubprocessError) as exc:
        print(json.dumps({"status": "FAIL", "error": str(exc)}, ensure_ascii=False))
        raise SystemExit(1)
