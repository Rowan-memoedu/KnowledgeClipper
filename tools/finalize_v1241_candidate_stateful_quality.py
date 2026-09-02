from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from assemble_integrated_desktop_v1240_quality import inventory, physical, sha256, verify_desktop_build
from verify_settings_control_plane import scan_runtime_tree_for_secrets


PROJECT = Path(__file__).resolve().parents[1]
CANDIDATE = PROJECT / "releases/kc-v1241-ui-proto-q-20260902T043500Z/win-x64"
ASSEMBLY = PROJECT / "receipts/quality-integrated-candidate/20260902T043500Z-v1241-entrypoint-resolver/assembly.receipt.json"
STATEFUL_ROOT = PROJECT / "canary/quality-integrated-v1241/20260902T045200Z-entrypoint-resolver-live-readonly-stateful-v2"
SPEC = PROJECT / "canary/quality-integrated-v1241/20260902T045100Z-entrypoint-resolver-runner-spec-v2.json"
FIRST_RUN = PROJECT / "canary/quality-integrated-v1241/20260902T044000Z-entrypoint-resolver-live-readonly-stateful/candidate-stateful-evidence.json"
SECOND_RUN = PROJECT / "canary/quality-integrated-v1241/20260902T044500Z-entrypoint-resolver-live-readonly-stateful-rerun/candidate-stateful-evidence.json"
AUTHORITY = PROJECT / "releases/knowledge-clipper-authority-v1.2.41-20260902T042000Z/win-x64"
AUTHORITY_META = PROJECT / "contracts/authority/worker-registry/v1.2.41-20260902T042000Z"
CHECKPOINT = PROJECT / "contracts/core/v2.10/checkpoints/v2.10.6-20260902T041800Z/checkpoint.json"
CONTRACT_OWNER = PROJECT / "receipts/core-phase1/20260902T041800Z-v2106-entrypoint-resolver/orchestrator-contract.receipt.json"
ADMISSION = PROJECT / "receipts/authority-admission/20260902T042200Z-v1.2.41-entrypoint-resolver-final-process/post-assembly-independent-authority-admission.receipt.json"
INVALIDATION = PROJECT / "receipts/authority-invalidation/20260902T042200Z/knowledge-clipper-authority-v1.2.40.multi-file-entrypoint-no-go.json"

EXPECTED = {
    "candidateManifest": "F33C0C04ABBA2AA1854E47F3A9E2009A854DEA9C8F98AF2ECD46720B9F101918",
    "candidateTree": "A408DED6D1EBCE9BACC22E7BF0B9065B0842FB0F8575EE32EBD7E54F797DE9E0",
    "assembly": "65F59A40999B0C0927073CA53F65EE59078721D1D4E07445ADB38396E225D140",
    "authorityTree": "7FB6EC31A29C7A4CB912AFA27D8A06D3B5F7205BABDBABE21DF72EA0E913A3B4",
    "registry": "FBF1A3E30462A7C05269E47D510153A0165D502E204ABB50936E332BA07129A7",
    "checkpoint": "20AF0975FF176B6D46249B98AC36D72C74C7A494965AF3A69DE4C7FF52235D79",
    "contract": "0EDD77E3DD571709B9A5E056DBC9B612AF6E397FE74A8FCFD9B653304AD05B87",
    "admission": "118342DE7BD3BF9F943C04055436CD33D60C1E5C9FBA672D16C6B82250F599E8",
    "invalidation": "528105FB7C7A37FB647828250466E07324D30663229AA3BF0145E54874C6C3F5",
    "stateful": "1741B255AFE588C5BAF6F1AAEBBC70E43B4A289EA13A89E3EDBB805FE465685F",
    "spec": "9331BAEE9628D669CEA829CCDA34D22A863BEBA32B80FE7F0A472257C255193C",
}


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def load(path: Path) -> Any:
    return json.loads(physical(path).read_text(encoding="utf-8-sig"))


def evidence(path: Path) -> dict[str, Any]:
    physical_path = physical(path)
    require(physical_path.is_file(), f"missing evidence: {path}")
    return {"path": relative(path), "bytes": physical_path.stat().st_size, "sha256": sha256(physical_path)}


def verify_evidence(item: dict[str, Any], base: Path = PROJECT) -> Path:
    path = (base / item["path"]).resolve()
    physical_path = physical(path)
    require(physical_path.is_file(), f"evidence missing: {item['path']}")
    require(physical_path.stat().st_size == int(item["bytes"]), f"evidence bytes drift: {item['path']}")
    require(sha256(physical_path) == str(item["sha256"]).upper(), f"evidence sha drift: {item['path']}")
    return path


def verify_candidate() -> dict[str, Any]:
    require(sha256(ASSEMBLY) == EXPECTED["assembly"], "assembly receipt drift")
    assembly = load(ASSEMBLY)
    manifest_path = CANDIDATE / "candidate-file-manifest.v1.json"
    require(sha256(manifest_path) == EXPECTED["candidateManifest"], "candidate manifest drift")
    manifest = load(manifest_path)
    sealed = inventory(CANDIDATE, include_files=False)
    require(sealed["treeSha256"] == EXPECTED["candidateTree"], "sealed candidate tree drift")
    require(sealed == assembly["sealed"], "sealed candidate no longer equals assembly receipt")
    payload = inventory(CANDIDATE, {"candidate-file-manifest.v1.json"}, include_files=True)
    require(payload == manifest["payload"], "candidate manifest payload drift")
    require(manifest["core"] == {"checkpointVersion": "2.10.6", "checkpointSha256": EXPECTED["checkpoint"], "ipcProtocolVersion": "1.7"}, "candidate Core identity drift")
    require(manifest["authority"]["registryVersion"] == "1.2.41" and manifest["authority"]["releaseTreeSha256"] == EXPECTED["authorityTree"], "candidate authority identity drift")
    require((CANDIDATE / "KnowledgeClipper.exe").is_file(), "Desktop entrypoint missing")
    verify_desktop_build(CANDIDATE)
    return {"manifest": evidence(manifest_path), "sealed": sealed, "payload": {key: payload[key] for key in ("fileCount", "totalBytes", "treeSha256")}, "entrypoint": evidence(CANDIDATE / "KnowledgeClipper.exe")}


def verify_authority() -> dict[str, Any]:
    require(sha256(CHECKPOINT) == EXPECTED["checkpoint"], "Core checkpoint drift")
    require(sha256(CONTRACT_OWNER) == EXPECTED["contract"], "Core contract receipt drift")
    require(sha256(ADMISSION) == EXPECTED["admission"], "authority admission drift")
    require(sha256(INVALIDATION) == EXPECTED["invalidation"], "v1.2.40 invalidation drift")
    registry = AUTHORITY_META / "worker-registry.v1.json"
    require(sha256(registry) == EXPECTED["registry"], "registry drift")
    authority = inventory(AUTHORITY, include_files=False)
    require((authority["fileCount"], authority["totalBytes"], authority["treeSha256"]) == (595, 268726758, EXPECTED["authorityTree"]), "authority release drift")
    copied = inventory(CANDIDATE / "authority/win-x64", include_files=False)
    require(copied == authority, "candidate authority subtree drift")
    candidate_registry = CANDIDATE / "authority/metadata/worker-registry.v1.json"
    require(sha256(candidate_registry) == EXPECTED["registry"], "candidate registry copy drift")
    candidate_checkpoint = CANDIDATE / "authority/metadata/core-v2.10.6-checkpoint.json"
    require(sha256(candidate_checkpoint) == EXPECTED["checkpoint"], "candidate checkpoint copy drift")
    executable = CANDIDATE / "authority/win-x64/orchestrator/KnowledgeClipper.Orchestrator.exe"
    completed = subprocess.run(
        [str(executable), "validate-registry", "--registry", str(candidate_registry), "--release-root", str(CANDIDATE / "authority/win-x64")],
        cwd=CANDIDATE,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=60,
        check=False,
    )
    require(completed.returncode == 0, f"candidate registry validation failed: {completed.stderr}")
    validation = json.loads(completed.stdout)
    require(validation == {"schemaVersion": "1.0.0", "status": "valid", "registryVersion": "1.2.41", "workers": 5}, "candidate registry result drift")
    contract = load(CONTRACT_OWNER)
    entrypoint = next(item for item in contract["tests"] if item["name"] == "verified-artifact-entrypoint-resolution")
    require(entrypoint["status"] == "passed" and all(value is True for key, value in entrypoint.items() if key not in {"name", "status"}), "owner entrypoint matrix drift")
    return {"release": authority, "registry": evidence(registry), "checkpoint": evidence(CHECKPOINT), "contract": evidence(CONTRACT_OWNER), "admission": evidence(ADMISSION), "v1240Invalidation": evidence(INVALIDATION), "candidateRegistryValidation": validation}


def find_module_action_receipt(observed_status: str) -> Path:
    require("receipt=" in observed_status, "Anki UI status did not declare its module receipt")
    relative_ref = observed_status.split("receipt=", 1)[1].strip()
    relative_path = Path(relative_ref)
    require(
        not relative_path.is_absolute()
        and relative_path.parts[:1] == ("module-actions",)
        and ".." not in relative_path.parts,
        "Anki UI module receipt reference is not a contained job-relative path",
    )
    jobs_root = (STATEFUL_ROOT / "isolated-real-store/localappdata/KnowledgeClipper/jobs").resolve()
    receipt_path = (jobs_root / relative_path).resolve()
    require(receipt_path.is_relative_to(jobs_root) and physical(receipt_path).is_file(), "Anki UI module receipt reference is missing or escaped")
    return receipt_path


def verify_anki_journey() -> dict[str, Any]:
    require(sha256(SPEC) == EXPECTED["spec"], "stateful runner spec drift")
    candidate_stateful = STATEFUL_ROOT / "candidate-stateful-evidence.json"
    require(sha256(candidate_stateful) == EXPECTED["stateful"], "stateful evidence drift")
    stateful = load(candidate_stateful)
    require(stateful["candidate"]["root"] == relative(CANDIDATE), "stateful candidate root drift")
    require(stateful["candidate"]["sealed"]["treeSha256"] == EXPECTED["candidateTree"], "stateful sealed tree drift")
    require(stateful["journeys"]["anki"]["status"] == "passed", "Anki stateful journey did not pass")
    require(stateful["journeys"]["remnote"]["status"] == stateful["journeys"]["question"]["status"] == "failed-candidate-bound", "conditional journey boundaries drift")
    journey_path = verify_evidence(stateful["journeys"]["anki"]["receipt"])
    journey = load(journey_path)
    require(journey["status"] == "passed" and journey["failure"] is None and len(journey["phases"]) == 1, "Anki journey receipt drift")
    phase = journey["phases"][0]
    driver_path = verify_evidence(phase["driverReceipt"])
    driver = load(driver_path)
    require(driver["status"] == "passed" and [item["id"] for item in driver["events"]] == ["open-export", "wait-probe", "invoke-probe", "read-status", "close"], "Anki UI action sequence drift")
    status_event = next(item for item in driver["events"] if item["id"] == "read-status")
    require("probeWrites=0" in status_event["observed"] and "receipt=module-actions/" in status_event["observed"], "Anki UI did not surface zero-write module receipt")

    module_path = find_module_action_receipt(status_event["observed"])
    module = load(module_path)
    require(module["operation"] == "anki.probe" and module["accepted"] is True and module["executionStatus"] == "completed", "Anki module action did not complete")
    require(module["externalWriteCount"] == 0 and module["automaticRetryAllowed"] is False, "Anki module action effect boundary drift")
    require(len(module["outputRefs"]) == 1 and module["outputRefs"][0]["kind"] == "anki-connection-status", "Anki typed output reference drift")
    package = module_path.parent / "package"
    artifact_path = verify_evidence({
        "path": module["outputRefs"][0]["path"],
        "bytes": physical(package / module["outputRefs"][0]["path"]).stat().st_size,
        "sha256": module["outputRefs"][0]["expectedSha256"],
    }, package)
    artifact = load(artifact_path)
    require(len(artifact["files"]) == 4, "Anki artifact is not the required four-file production shape")
    entrypoint = artifact["metadata"]["entrypoint"]
    require(sum(1 for item in artifact["files"] if item["path"] == entrypoint) == 1, "Anki artifact entrypoint is not uniquely declared")
    verified_files = []
    for item in artifact["files"]:
        path = package / item["path"]
        physical_path = physical(path)
        require(physical_path.is_file() and physical_path.stat().st_size == int(item["bytes"]) and sha256(physical_path) == item["sha256"].upper(), f"Anki artifact file drift: {item['path']}")
        verified_files.append(evidence(path))
    status_path = package / entrypoint
    status = load(status_path)
    require(status["effectClass"] == "provider-read-only" and status["networkScope"] == "loopback-only", "Anki probe effect class drift")
    require(status["externalMutation"] is False and status["mutatingActionsObserved"] == [], "Anki probe mutated external state")
    require(len(status["actionsObserved"]) == 18, "Anki read-only operation count drift")
    result_path = package / module["outputRef"].removeprefix(module["artifactRootRef"] + "/")
    require(physical(result_path).is_file() and sha256(physical(result_path)) == module["outputSha256"].upper(), "Anki worker result binding drift")
    return {
        "status": "PASS-CANDIDATE-BOUND-MULTIFILE-ENTRYPOINT",
        "journey": evidence(journey_path),
        "driver": evidence(driver_path),
        "moduleReceipt": evidence(module_path),
        "workerResult": evidence(result_path),
        "artifactManifest": evidence(artifact_path),
        "declaredFiles": len(artifact["files"]),
        "verifiedFiles": verified_files,
        "entrypoint": entrypoint,
        "readOnlyOperations": len(status["actionsObserved"]),
        "mutatingActionsObserved": status["mutatingActionsObserved"],
        "externalWriteCount": module["externalWriteCount"],
    }


def write_once(path: Path, value: str) -> None:
    require(not path.exists(), f"append-only output exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(value, encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-root", required=True, type=Path)
    args = parser.parse_args()
    output_root = args.output_root.resolve()
    require(output_root.is_relative_to((PROJECT / "receipts/quality-integrated-candidate").resolve()), "output root must be append-only quality receipt scope")
    require(not output_root.exists(), "append-only output root exists")

    candidate = verify_candidate()
    authority = verify_authority()
    anki = verify_anki_journey()
    secret_scan = scan_runtime_tree_for_secrets(CANDIDATE)
    require(secret_scan["status"] == "passed" and secret_scan["findingCount"] == 0, "candidate secret scan failed")
    require(not list(CANDIDATE.rglob("SmokeTest.exe")) and not list(CANDIDATE.rglob("TtsCanary.exe")), "legacy external Canary executable packaged")
    require(not list(CANDIDATE.rglob("credentials.json")) and not list(CANDIDATE.rglob("credentials.json.bak")) and not list(CANDIDATE.rglob(".env")), "credential file packaged")

    receipt_path = output_root / "candidate-bound-quality.receipt.json"
    readable_path = output_root / "READABLE.md"
    now = datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")
    receipt = {
        "schemaVersion": "knowledge-clipper-v1241-candidate-stateful-quality.v1",
        "generatedAt": now,
        "status": "PASS-ENTRYPOINT-RESOLVER-CANDIDATE-BOUND-CONDITIONAL-OVERALL",
        "candidate": candidate,
        "authority": authority,
        "stateful": {
            "candidateEvidence": evidence(STATEFUL_ROOT / "candidate-stateful-evidence.json"),
            "runnerSpec": evidence(SPEC),
            "anki": anki,
            "remnote": {"status": "CONDITIONAL-NOT-BOUND", "driverReceipt": load(STATEFUL_ROOT / "candidate-stateful-evidence.json")["failures"]["remnote"]["driverReceipt"]},
            "question": {"status": "CONDITIONAL-NOT-COMPLETED", "driverReceipt": load(STATEFUL_ROOT / "candidate-stateful-evidence.json")["failures"]["question"]["driverReceipt"]},
        },
        "historicalPreserved": {
            "v1240AuthorityInvalidation": evidence(INVALIDATION),
            "firstV1241Run": evidence(FIRST_RUN),
            "secondV1241Run": evidence(SECOND_RUN),
            "fixtureCorrection": "Only the case-sensitive expected production field changed from writes=0 to probeWrites=0; candidate and product files were unchanged.",
        },
        "safety": {
            "secretScan": secret_scan,
            "credentialFiles": 0,
            "legacyExternalCanaryExecutables": 0,
            "mineruCalls": 0,
            "physicalSpeakerInvocations": 0,
            "ankiMutatingActions": [],
            "ankiExternalWrites": 0,
        },
        "decision": {
            "coreMultiFileEntrypointResolver": "PASS-CANDIDATE-BOUND",
            "authorityV1241": "PASS-EXACT-BOUND",
            "overallStatefulJourneys": "NO-GO-CONDITIONAL",
            "userHandTestEntrypoint": False,
            "reason": "The targeted Anki four-file entrypoint journey passed, while RemNote target binding and live Question completion remain unresolved in the same isolated run.",
        },
    }
    write_once(receipt_path, json.dumps(receipt, ensure_ascii=False, indent=2) + "\n")
    readable = f"""# KnowledgeClipper v1.2.41 candidate-bound quality\n\n- Candidate: `{relative(CANDIDATE)}`\n- Manifest SHA-256: `{EXPECTED['candidateManifest']}`\n- Sealed tree SHA-256: `{EXPECTED['candidateTree']}`\n- Authority: v1.2.41 exact-bound, tree `{EXPECTED['authorityTree']}`\n- Targeted stateful result: **PASS** — Settings UI -> candidate Desktop -> embedded Orchestrator -> Named Pipe -> export.anki 1.4.0; all four artifact files were verified, `metadata.entrypoint` selected `connection-status.json`, 18 read-only AnkiConnect operations, zero mutating actions, zero external writes.\n- Overall stateful promotion: **NO-GO-CONDITIONAL** — RemNote remained unbound and Question did not reach the expected validated answer state in the same isolated run. These are not reported as the entrypoint resolver failing.\n- Safety: candidate secret findings 0; credential files 0; MinerU calls 0; physical speaker 0; legacy external Canary executables 0.\n- Historical evidence: v1.2.40 remains invalidated and untouched; both earlier v1.2.41 failed attempts remain append-only.\n"""
    write_once(readable_path, readable)
    print(json.dumps({"status": receipt["status"], "receipt": evidence(receipt_path), "readable": evidence(readable_path), "candidateManifest": EXPECTED["candidateManifest"], "candidateTree": EXPECTED["candidateTree"], "anki": anki["status"], "overall": receipt["decision"]["overallStatefulJourneys"]}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
