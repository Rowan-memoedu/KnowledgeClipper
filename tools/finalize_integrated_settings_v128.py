from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


PROJECT = Path(__file__).resolve().parents[1]
CANDIDATE_ID = "knowledge-clipper-integrated-settings-v1.2.8-20260828T090500Z"
CANDIDATE = PROJECT / "releases/kc-i-settings-v128-20260828T090500Z/win-x64"
PREVIOUS = PROJECT / "releases/kc-i-settings-v126-20260828T083800Z/win-x64"
EVIDENCE = PROJECT / "canary/desktop-integrated-settings-v128/kc-i-settings-v128-20260828T090500Z"
OUTPUT = PROJECT / "receipts/desktop-integrated-settings-v1.2.8-20260828T090500Z-module-checkpoint.json"

AUTHORITY_ROOT = PROJECT / "releases/knowledge-clipper-authority-v1.2.8-20260828T090000Z/win-x64"
AUTHORITY_CONTRACT = PROJECT / "contracts/authority/worker-registry/v1.2.8-20260828T090000Z"
AUTHORITY_RECEIPT = PROJECT / "receipts/knowledge-clipper-authority-1.2.8-20260828T090200Z.json"
SETTINGS_CHECKPOINT = PROJECT / "contracts/core/v2.4/settings-control/checkpoints/v2.4.5-20260828T085200Z/checkpoint.json"
V127_INVALIDATION = PROJECT / "contracts/authority/worker-registry/v1.2.7-20260828T085600Z/invalidation-receipt.v1.json"
PREVIOUS_MODULE_RECEIPT = PROJECT / "receipts/desktop-integrated-settings-v1.2.6-20260828T083800Z-module-checkpoint.json"

EXPECTED = {
    "candidateManifest": "6DD596B33B59D3ABFD309879DB07134803AFFBE1F8982F6BFA51C0887625E9BD",
    "candidateTree": "2879388F97FACF218F16DEB71DEB1E4AEED486D3F08C828B2331A5B39BEBAF04",
    "authorityTree": "0C89556014F69E775D1E9F9F9EF14D5EA20F57E2359E193DAC91720820A38121",
    "checkpoint": "4D2997B2ABEE89E41DC3F77815AE43122079E2DAFEA7C773A22FCB29C5BBAE11",
    "registry": "CAC29BBBBC614E4DC2B60A781CC0239D422775B212A9C2AF2201E3EC3B95D7AC",
    "dependency": "6B250A54F4634A4EDBC3646FD3592F8289C1C536FBA7B533C714D5F20D8F9B6D",
    "authorityReceipt": "EFA64E5F020FC9157483814192D77728D5C485F1B04D6B0DDA3D05AF15265720",
    "settingsCheckpoint": "55778934CD4A4323704DC028FA1E6E1880C2FA3E7A0178F4B1BA5032FC89B764",
    "previousModuleReceipt": "3B824A0A02C78E6B3C6A16413FC9643016690385BA95DD121DF1F3E89D16584F",
    "desktopBuildReceipt": "2F4FC8AFD0E6169256192D94327DD0F9807D35707E96759672CEA190E7C0A603",
    "desktopExe": "29320D1CCAA252057D011490866CC7533901740C5C6921359B7A2F9F58C4720C",
    "desktopDll": "CFAB7649D747416988D6BDFF3FACD8EE374591600731B755DAEE77494267D48A",
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


def evidence(path: Path) -> dict[str, Any]:
    require(path.is_file(), f"missing evidence: {path}")
    return {
        "path": path.relative_to(PROJECT).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha(path),
    }


def inventory(root: Path, excluded: set[str] | None = None) -> dict[str, Any]:
    excluded = excluded or set()
    rows: list[str] = []
    total = 0
    count = 0
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        if relative in excluded:
            continue
        size = path.stat().st_size
        rows.append(f"{relative}\t{size}\t{sha(path).lower()}")
        total += size
        count += 1
    return {
        "fileCount": count,
        "totalBytes": total,
        "treeSha256": hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper(),
    }


def main() -> int:
    require(not OUTPUT.exists(), f"append-only receipt already exists: {OUTPUT}")

    manifest_path = CANDIDATE / "candidate-file-manifest.v1.json"
    require(sha(manifest_path) == EXPECTED["candidateManifest"], "candidate manifest SHA mismatch")
    manifest = load(manifest_path)
    require(manifest["candidateId"] == CANDIDATE_ID, "candidate identity mismatch")
    payload = inventory(CANDIDATE, {"candidate-file-manifest.v1.json"})
    require(all(payload[key] == manifest["payload"][key] for key in ("fileCount", "totalBytes", "treeSha256")), "candidate payload/manifest mismatch")
    sealed = inventory(CANDIDATE)
    require(sealed == {"fileCount": 450, "totalBytes": 402051569, "treeSha256": EXPECTED["candidateTree"]}, "sealed candidate inventory mismatch")

    authority = inventory(AUTHORITY_ROOT)
    embedded_authority = inventory(CANDIDATE / "authority/win-x64")
    require(authority == {"fileCount": 192, "totalBytes": 249613600, "treeSha256": EXPECTED["authorityTree"]}, "source authority inventory mismatch")
    require(embedded_authority == authority, "embedded authority differs from admitted v1.2.8")

    expected_files = {
        AUTHORITY_CONTRACT / "checkpoint.json": EXPECTED["checkpoint"],
        AUTHORITY_CONTRACT / "worker-registry.v1.json": EXPECTED["registry"],
        AUTHORITY_CONTRACT / "dependency-manifest.v1.json": EXPECTED["dependency"],
        AUTHORITY_RECEIPT: EXPECTED["authorityReceipt"],
        SETTINGS_CHECKPOINT: EXPECTED["settingsCheckpoint"],
        PREVIOUS_MODULE_RECEIPT: EXPECTED["previousModuleReceipt"],
    }
    for path, expected_hash in expected_files.items():
        require(path.is_file() and sha(path) == expected_hash, f"bound evidence drift: {path}")

    checkpoint = load(AUTHORITY_CONTRACT / "checkpoint.json")
    require(checkpoint["checkpointId"] == "knowledge-clipper-authority-1.2.8-20260828T090200Z", "authority checkpoint identity mismatch")
    require(checkpoint["admissionStatus"] == "authority-admitted", "authority is not admitted")
    require(checkpoint["registry"]["registryVersion"] == "1.2.8" and checkpoint["registry"]["workers"] == 5, "authority registry checkpoint mismatch")

    settings = load(SETTINGS_CHECKPOINT)
    require(settings["status"] == "passed-authority-admission-ready", "settings v2.4.5 component checkpoint failed")
    require(settings["content"]["worker"] == "content.deepseek@0.6.1", "content worker version mismatch")
    require(settings["speech"]["worker"] == "speech.edge@1.2.2", "speech worker version mismatch")
    require(settings["speech"]["deepPath"]["status"] == "passed", "speech deep-path gate failed")
    require(settings["authorityBoundary"]["speechDeepPathReleasePending"] is False, "speech deep-path release is still pending")
    require(settings["authorityBoundary"]["phaseBFinal"] is False, "intermediate checkpoint must not claim Phase B final")

    invalidation = load(V127_INVALIDATION)
    require(invalidation["registryVersion"] == "1.2.7" and invalidation["status"] == "invalidated-never-promoted", "v1.2.7 invalidation evidence mismatch")
    require(invalidation["replacement"]["minimumRegistryVersion"] == "1.2.8", "v1.2.7 replacement boundary mismatch")

    equality_path = EVIDENCE / "desktop-payload-equality.json"
    equality = load(equality_path)
    require(equality["status"] == "PASS" and equality["fileCount"] == 244 and equality["changedFiles"] == 0, "Desktop payload equality failed")
    require(equality["buildReceiptSha256"] == EXPECTED["desktopBuildReceipt"], "Desktop build receipt changed")
    require(equality["exeSha256"] == EXPECTED["desktopExe"] and equality["dllSha256"] == EXPECTED["desktopDll"], "Desktop executable payload changed")

    manifest_gate = load(EVIDENCE / "manifest-tree-gate.json")
    require(manifest_gate["status"] == "PASS" and manifest_gate["candidateTreeSha256"] == EXPECTED["candidateTree"], "manifest/tree gate failed")
    require(manifest_gate["authorityTreeSha256"] == EXPECTED["authorityTree"], "manifest authority binding failed")

    startup = load(EVIDENCE / "startup-diagnostic.stdout.log")
    single = load(EVIDENCE / "single-instance-diagnostic.stdout.log")
    registry = load(EVIDENCE / "authority-validate-registry.log")
    integrated = load(EVIDENCE / "integration-startup-canary.stdout.log")
    require(startup["result"] == "PASS" and startup["externalConnections"] == 0 and startup["namedPipeInvoked"] is False, "safe startup gate failed")
    require(single["existingInstanceDetected"] is True and single["existingProcessId"] == 5976 and single["userFeedbackMode"] == "activate-existing-window-and-message-box", "single-instance regression failed")
    require(registry == {"schemaVersion": "1.0.0", "status": "valid", "registryVersion": "1.2.8", "workers": 5}, "candidate registry validation failed")
    require(integrated["result"] == "PASS" and integrated["orchestratorStartedByDesktop"] is True and integrated["namedPipeInvoked"] is True, "integrated startup/Named Pipe gate failed")
    require(integrated["action"] == "task.status.query-recent" and integrated["queryItemCount"] == 0, "integrated Canary performed an unexpected action")
    require(not any(integrated[key] for key in ("externalWorkerInvoked", "physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")), "external side effect boundary failed")

    require(not list(CANDIDATE.rglob("credentials.json")), "runtime credential store leaked into candidate")
    known_secret_markers = (b"canary-deepseek-local-only", b"canary-mineru-local-only", b"canary-credential")
    secret_hits: list[str] = []
    for path in CANDIDATE.rglob("*"):
        if path.is_file() and path.suffix.lower() != ".pdb":
            data = path.read_bytes()
            if any(marker in data for marker in known_secret_markers):
                secret_hits.append(path.relative_to(CANDIDATE).as_posix())
    require(not secret_hits, f"known Canary secret leaked into candidate: {secret_hits}")

    receipt = {
        "schemaVersion": "knowledge-clipper-desktop-integrated-settings-module-checkpoint.v1",
        "status": "PASS-INTEGRATED-SETTINGS-V2.4.5-AUTHORITY-V1.2.8-MODULE-CHECKPOINT",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "candidateId": CANDIDATE_ID,
        "candidate": {
            "root": CANDIDATE.relative_to(PROJECT).as_posix(),
            "manifest": evidence(manifest_path),
            "entrypoint": (CANDIDATE / "KnowledgeClipper.exe").relative_to(PROJECT).as_posix(),
            "entrypointSha256": EXPECTED["desktopExe"],
            **sealed,
        },
        "desktopPayloadEquality": {
            "baselineCandidate": PREVIOUS.parent.name,
            "baselineModuleReceipt": evidence(PREVIOUS_MODULE_RECEIPT),
            "evidence": evidence(equality_path),
            "fileCount": 244,
            "changedFiles": 0,
            "buildReceiptSha256": EXPECTED["desktopBuildReceipt"],
            "exeSha256": EXPECTED["desktopExe"],
            "dllSha256": EXPECTED["desktopDll"],
            "uiRetestRequired": False,
        },
        "authority": {
            "version": "1.2.8",
            "checkpointId": checkpoint["checkpointId"],
            "checkpoint": evidence(AUTHORITY_CONTRACT / "checkpoint.json"),
            "registry": evidence(AUTHORITY_CONTRACT / "worker-registry.v1.json"),
            "dependencyManifest": evidence(AUTHORITY_CONTRACT / "dependency-manifest.v1.json"),
            "receipt": evidence(AUTHORITY_RECEIPT),
            "embeddedExactly": True,
            **authority,
        },
        "settingsComponent": {
            "version": "2.4.5",
            "checkpoint": evidence(SETTINGS_CHECKPOINT),
            "contentWorker": settings["content"]["worker"],
            "speechWorker": settings["speech"]["worker"],
            "speechDeepPathReleasePending": False,
            "phaseBFinal": False,
        },
        "excludedAuthority": {
            "version": "1.2.7",
            "status": invalidation["status"],
            "invalidationReceipt": evidence(V127_INVALIDATION),
            "usedByCandidate": False,
        },
        "automatedCanaries": {
            "manifestTree": evidence(EVIDENCE / "manifest-tree-gate.json"),
            "desktopPayloadEquality": evidence(equality_path),
            "safeStartup": evidence(EVIDENCE / "startup-diagnostic.stdout.log"),
            "singleInstance": evidence(EVIDENCE / "single-instance-diagnostic.stdout.log"),
            "registry": evidence(EVIDENCE / "authority-validate-registry.log"),
            "namedPipeQuery": evidence(EVIDENCE / "integration-startup-canary.stdout.log"),
        },
        "singleInstance": {
            "existingProcessId": 5976,
            "existingProcessPreserved": True,
            "feedbackMode": single["userFeedbackMode"],
            "candidatePipeName": "KnowledgeClipper.Orchestrator.v1.settings-v128",
            "canaryOrchestratorProcessId": integrated["orchestratorProcessId"],
            "canaryOrchestratorStoppedAfterTest": True,
        },
        "safety": {
            "externalWorkersInvoked": 0,
            "externalProductionApisInvoked": 0,
            "physicalSpeakerInvoked": 0,
            "remNoteInvoked": 0,
            "ankiInvoked": 0,
            "mineruInvoked": 0,
            "existingUserProcessTerminated": False,
            "runtimeCredentialFilesInCandidate": 0,
            "knownCanarySecretHits": 0,
        },
        "releaseBoundary": {
            "classification": "module-checkpoint",
            "userHandTestEntrypoint": False,
            "reason": "Core final A/B/C/D and Quality candidate-bound gates are pending.",
            "desktopPayloadChanged": False,
            "repeatUiHandTestRequired": False,
            "candidateMayBePromotedWithoutLaterCandidateBoundGates": False,
        },
    }
    OUTPUT.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "status": receipt["status"],
        "candidateRoot": str(CANDIDATE),
        "manifestSha256": EXPECTED["candidateManifest"],
        "candidateTreeSha256": EXPECTED["candidateTree"],
        "desktopPayloadFiles": 244,
        "desktopChangedFiles": 0,
        "receipt": str(OUTPUT),
        "receiptSha256": sha(OUTPUT),
        "userHandTestEntrypoint": False,
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (GateError, OSError, ValueError, KeyError, json.JSONDecodeError) as exc:
        print(json.dumps({"status": "FAIL", "error": str(exc)}, ensure_ascii=False))
        raise SystemExit(1)
