from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator

import quality_gates
import verify_integrated_candidate_quality as common
import verify_settings_control_plane as settings_quality


PROJECT_ROOT = Path(__file__).resolve().parents[1]
FIXTURE_ROOT = PROJECT_ROOT / "tests" / "Quality.Gates" / "fixtures" / "v1212-candidate-admission"
FIXTURE_SCHEMA = FIXTURE_ROOT / "v1212-candidate-admission.schema.json"
DEFAULT_FIXTURE = FIXTURE_ROOT / "valid" / "integrated-candidate.json"
INVALIDATED_OLD_RECEIPT = PROJECT_ROOT / "canary" / "v1212-candidate-admission" / "20260830T082337Z-final-candidate-bound" / "quality-candidate-receipt.json"
INVALIDATED_OLD_RECEIPT_SHA256 = "42605B57575017D281B04ABB29DAC5C145C26445638D564B346124CF2B715721"

EXPECTED_WORKERS = {
    "image.ocr.mineru": "1.2.2",
    "content.deepseek": "0.7.0",
    "speech.edge": "1.2.4",
    "export.remnote": "2.1.0",
    "export.anki": "1.2.1",
}


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def load_json(path: Path) -> dict[str, Any]:
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        raise GateFailure(f"invalid JSON: {path}: {exc}") from exc


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def resolve_ref(ref: dict[str, Any], label: str) -> Path:
    raw = Path(ref["path"])
    path = raw.resolve() if raw.is_absolute() else (PROJECT_ROOT / raw).resolve()
    require(path.is_relative_to(PROJECT_ROOT.resolve()) and path.is_file(), f"{label}: missing/out-of-project evidence")
    require(sha256(path) == str(ref["sha256"]).upper(), f"{label}: SHA-256 mismatch")
    return path


def evidence(path: Path) -> dict[str, Any]:
    return {
        "path": path.resolve().relative_to(PROJECT_ROOT.resolve()).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha256(path),
    }


def validate_fixtures() -> dict[str, Any]:
    schema = load_json(FIXTURE_SCHEMA)
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema)
    valid = sorted((FIXTURE_ROOT / "valid").glob("*.json"))
    invalid = sorted((FIXTURE_ROOT / "invalid").glob("*.json"))
    require(valid and invalid, "v1.2.12 fixture set incomplete")
    for path in valid:
        errors = list(validator.iter_errors(load_json(path)))
        require(not errors, f"valid fixture rejected: {path.name}")
    for path in invalid:
        require(bool(list(validator.iter_errors(load_json(path)))), f"invalid fixture accepted: {path.name}")
    return {"status": "PASS", "validAccepted": len(valid), "invalidRejected": len(invalid), "schema": evidence(FIXTURE_SCHEMA)}


def verify_core(ref: dict[str, Any]) -> dict[str, Any]:
    path = resolve_ref(ref, "core.checkpoint")
    checkpoint = load_json(path)
    require(checkpoint["checkpointVersion"] == "2.5.1", "Core checkpoint version mismatch")
    require(checkpoint["status"] == "passed-offline-authority-admission-ready", "Core checkpoint status mismatch")
    require(checkpoint["core"]["version"] == "2.5.0", "Core runtime version mismatch")
    require(checkpoint["compatibility"] == {**checkpoint["compatibility"], "frozenContractsV1Modified": False, "priorAuthorityModified": False, "externalCalls": 0, "mineruCalls": 0, "remnoteWrites": 0, "ankiWrites": 0, "physicalSpeakerInvocations": 0}, "Core compatibility/side-effect boundary mismatch")
    retention = checkpoint["validation"]["productionRetention"]
    require(retention == {"status": "disabled-fail-closed", "legacyStartupInvocations": 0, "stateAwareRetentionService": "conditional-not-implemented"}, "retention safety boundary mismatch")
    original = checkpoint["requirements"]["originalImage"]
    require(original == {"status": "terminal-unsupported", "worker": "image.ocr.mineru", "outputKind": "image.original", "providerCalls": 0, "downstreamStarted": False}, "original-image boundary mismatch")
    partial = checkpoint["requirements"]["imagePartial"]
    require(partial == {"status": "failed-items-only", "wholeCardBlocksDownstreamUntilMergedCompletion": True, "successfulItemsReusedByHash": True, "unknownExternalAutomaticRetry": False}, "image partial boundary mismatch")
    rows: list[str] = []
    total = 0
    for item in sorted(checkpoint["tree"]["files"], key=lambda value: value["path"]):
        item_path = (PROJECT_ROOT / item["path"]).resolve()
        require(item_path.is_relative_to(PROJECT_ROOT.resolve()) and item_path.is_file(), f"Core tree missing: {item['path']}")
        actual_hash = sha256(item_path)
        actual_bytes = item_path.stat().st_size
        require(actual_hash == str(item["sha256"]).upper() and actual_bytes == int(item["bytes"]), f"Core tree drift: {item['path']}")
        rows.append(f"{item['path']}\t{actual_bytes}\t{actual_hash.lower()}")
        total += actual_bytes
    tree_hash = hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper()
    tree = checkpoint["tree"]
    require(len(rows) == tree["fileCount"] and total == tree["totalBytes"] and tree_hash == tree["treeSha256"].upper(), "Core checkpoint tree mismatch")
    return {
        "status": "PASS-AUTHORITY-READY",
        "checkpoint": evidence(path),
        "runtimeVersion": "2.5.0",
        "tree": {"fileCount": len(rows), "totalBytes": total, "treeSha256": tree_hash},
        "requirements": checkpoint["requirements"],
        "retention": retention,
    }


def _run_json(command: list[str], label: str, cwd: Path) -> dict[str, Any]:
    completed = subprocess.run(command, cwd=cwd, capture_output=True, text=True, timeout=30, check=False)
    require(completed.returncode == 0, f"{label}: exit={completed.returncode}: {completed.stderr[:500]}")
    try:
        return json.loads(completed.stdout)
    except json.JSONDecodeError as exc:
        raise GateFailure(f"{label}: stdout is not JSON") from exc


def verify_authority(config: dict[str, Any], run_binaries: bool) -> dict[str, Any]:
    checkpoint_path = resolve_ref(config["checkpoint"], "authority.checkpoint")
    registry_path = resolve_ref(config["registry"], "authority.registry")
    qualification_path = resolve_ref(config["qualification"], "authority.qualification")
    checkpoint = load_json(checkpoint_path)
    registry = load_json(registry_path)
    qualification = load_json(qualification_path)
    require(checkpoint["admissionStatus"] == "authority-admitted", "authority is not admitted")
    require(checkpoint["registry"]["registryVersion"] == registry["registryVersion"] == "1.2.12", "authority registry version mismatch")
    require(checkpoint["registry"]["sha256"].upper() == sha256(registry_path), "checkpoint/registry hash mismatch")
    require(qualification["status"] == "passed-offline-authority-qualified", "offline qualification status mismatch")
    require(qualification["authority"]["checkpoint"]["sha256"].upper() == sha256(checkpoint_path), "qualification/checkpoint mismatch")
    require(qualification["authority"]["registry"]["sha256"].upper() == sha256(registry_path), "qualification/registry mismatch")
    require(checkpoint["frozenV1"]["files"] == 21 and checkpoint["frozenV1"]["mismatches"] == 0, "frozen v1 checkpoint mismatch")
    release_cfg = config["release"]
    release_root = (PROJECT_ROOT / release_cfg["root"]).resolve()
    inventory = common.inventory(release_root)
    expected_inventory = {key: release_cfg[key] for key in ("fileCount", "totalBytes", "treeSha256")}
    require(all(inventory[key] == expected_inventory[key] for key in expected_inventory), "authority release inventory mismatch")
    checkpoint_release = checkpoint["release"]
    require((checkpoint_release["files"], checkpoint_release["bytes"], checkpoint_release["treeSha256"].upper()) == (inventory["fileCount"], inventory["totalBytes"], inventory["treeSha256"]), "authority checkpoint/release tree mismatch")
    registry_by_id = {item["workerId"]: item for item in registry["workers"]}
    require(set(registry_by_id) == set(EXPECTED_WORKERS), "authority worker set mismatch")
    worker_results: dict[str, Any] = {}
    for worker_id, version in EXPECTED_WORKERS.items():
        registered = registry_by_id[worker_id]
        bound = checkpoint["workers"][worker_id]
        require(bound["workerVersion"] == version, f"{worker_id}: version mismatch")
        exe = release_root / registered["entrypoint"]["path"]
        require(exe.is_file(), f"{worker_id}: entrypoint missing")
        require(exe.stat().st_size == bound["executableBytes"] and sha256(exe) == bound["executableSha256"].upper(), f"{worker_id}: entrypoint hash mismatch")
        describe_path = Path(bound["path"])
        stored_describe = load_json(describe_path)
        require(sha256(describe_path) == bound["sha256"].upper(), f"{worker_id}: stored describe hash mismatch")
        require(stored_describe["workerId"] == worker_id and stored_describe["workerVersion"] == version, f"{worker_id}: stored describe identity mismatch")
        require(stored_describe["outputKinds"] == registered["outputKinds"], f"{worker_id}: outputKinds mismatch")
        live_describe = None
        if run_binaries:
            live_describe = _run_json([str(exe), *registered["entrypoint"].get("args", []), "describe", "--json"], f"{worker_id}.describe", release_root)
            require(live_describe == stored_describe, f"{worker_id}: live describe drifted from authority evidence")
        worker_results[worker_id] = {
            "status": "PASS-ENTRYPOINT-DESCRIBE" if run_binaries else "PASS-STATIC-BINDING",
            "workerVersion": version,
            "entrypoint": registered["entrypoint"]["path"],
            "executable": evidence(exe),
            "describe": evidence(describe_path),
            "outputKinds": registered["outputKinds"],
        }
    registry_validation = {"status": "not-run-in-unit-evaluation"}
    if run_binaries:
        orchestrator = release_root / "orchestrator" / "KnowledgeClipper.Orchestrator.exe"
        registry_validation = _run_json([str(orchestrator), "validate-registry", "--registry", str(registry_path), "--release-root", str(release_root)], "orchestrator.validate-registry", release_root)
        require(registry_validation == {"schemaVersion": "1.0.0", "status": "valid", "registryVersion": "1.2.12", "workers": 5}, "orchestrator registry validation mismatch")
    stable = quality_gates.verify_stable_checkpoint(PROJECT_ROOT)
    return {
        "status": "PASS-AUTHORITY-ADMITTED",
        "checkpoint": evidence(checkpoint_path),
        "registry": evidence(registry_path),
        "qualification": evidence(qualification_path),
        "release": {"root": release_cfg["root"], **{key: inventory[key] for key in ("fileCount", "totalBytes", "treeSha256")}},
        "workers": worker_results,
        "registryValidation": registry_validation,
        "frozenV1": stable,
        "sideEffects": {"externalCalls": 0, "workerRuns": 0, "mineruCalls": 0, "ankiWrites": 0, "remnoteWrites": 0, "physicalSpeakerInvocations": 0},
    }


def verify_anki(ref: dict[str, Any], authority: dict[str, Any]) -> dict[str, Any]:
    path = resolve_ref(ref, "ankiPostAuthority")
    receipt = load_json(path)
    require(receipt["status"] == receipt["compatibilityStatus"] == receipt["consumerOnboardingStatus"] == "passed", "Anki post-authority qualification failed")
    require(receipt["authority"]["registry"]["sha256"].upper() == authority["registry"]["sha256"], "Anki registry binding mismatch")
    require(receipt["authority"]["checkpoint"]["sha256"].upper() == authority["checkpoint"]["sha256"], "Anki checkpoint binding mismatch")
    require(receipt["candidate"]["workerVersion"] == "1.2.1" and receipt["probe"]["resultStatus"] == "completed" and receipt["probe"]["connectionStatus"] == "ready", "Anki probe result mismatch")
    require(receipt["probe"]["externalMutation"] is False and receipt["probe"]["sideEffects"] == [], "Anki probe reported side effects")
    safety = receipt["safety"]
    require(safety["realAnkiConnectCalls"] == safety["notesWritten"] == safety["mediaWritten"] == 0 and safety["mutatingActions"] == [], "Anki offline boundary mismatch")
    require(all(value == "passed" for value in receipt["gates"].values()), "Anki consumer gate mismatch")
    return {
        "status": "PASS-CANDIDATE-BOUND-OFFLINE-PROBE",
        "receipt": evidence(path),
        "workerVersion": "1.2.1",
        "probe": receipt["probe"],
        "desktop": {"ankiAuthorityAssertionPassed": receipt["desktop"]["ankiAuthorityAssertionPassed"], "supplementalCanaryStatus": receipt["desktop"]["canaryStatus"], "supplementalFailureAttributedToAnki": False},
        "safety": safety,
    }


def _verify_manifest_payload(candidate_root: Path, manifest: dict[str, Any]) -> dict[str, Any]:
    expected = manifest["payload"]
    actual = common.inventory(candidate_root, set(manifest["excludedControlFiles"]))
    require(actual["fileCount"] == expected["fileCount"], "candidate payload file count mismatch")
    require(actual["totalBytes"] == expected["totalBytes"], "candidate payload bytes mismatch")
    require(actual["treeSha256"] == expected["treeSha256"].upper(), "candidate payload tree mismatch")
    expected_files = {item["path"]: item for item in expected["files"]}
    require(set(expected_files) == {item["path"] for item in actual["files"]}, "candidate manifest path set mismatch")
    for item in actual["files"]:
        wanted = expected_files[item["path"]]
        require(item["bytes"] == wanted["bytes"] and item["sha256"] == wanted["sha256"].upper(), f"candidate manifest file drift: {item['path']}")
    return {key: actual[key] for key in ("fileCount", "totalBytes", "treeSha256")}


def _verify_desktop_build(candidate_root: Path, build_receipt: dict[str, Any]) -> dict[str, Any]:
    require(build_receipt["productName"] == "零碎知识剪藏" and build_receipt["assemblyName"] == "KnowledgeClipper" and build_receipt["entrypoint"] == "KnowledgeClipper.exe", "Desktop product identity mismatch")
    require(build_receipt["selfContained"] is True and build_receipt["runtimeIdentifier"] == "win-x64", "Desktop self-contained runtime mismatch")
    rows: list[str] = []
    total = 0
    for item in sorted(build_receipt["files"], key=lambda value: value["path"]):
        path = candidate_root / item["path"]
        require(path.is_file(), f"Desktop payload missing: {item['path']}")
        actual_hash = sha256(path)
        actual_bytes = path.stat().st_size
        require(actual_hash == item["sha256"].upper() and actual_bytes == item["bytes"], f"Desktop payload drift: {item['path']}")
        rows.append(f"{item['path']}\n{actual_hash.lower()}\n{actual_bytes}\n")
        total += actual_bytes
    tree = hashlib.sha256("".join(rows).encode("utf-8")).hexdigest().upper()
    require(len(rows) == build_receipt["fileCount"] and total == build_receipt["totalBytes"] and tree == build_receipt["treeSha256"].upper(), "Desktop build receipt tree mismatch")
    require(sha256(candidate_root / "KnowledgeClipper.exe") == build_receipt["executableSha256"].upper(), "Desktop EXE hash mismatch")
    require(sha256(candidate_root / "KnowledgeClipper.dll") == build_receipt["applicationDllSha256"].upper(), "Desktop DLL hash mismatch")
    return {"fileCount": len(rows), "totalBytes": total, "treeSha256": tree}


def verify_desktop(config: dict[str, Any], authority: dict[str, Any], *, run_binaries: bool, runtime_root: Path | None) -> dict[str, Any]:
    explicit = config["integratedCandidateRoot"]
    if explicit:
        candidate_root = (PROJECT_ROOT / explicit).resolve()
        require(candidate_root.is_dir(), "configured Desktop integrated candidate missing")
        manifest_path = candidate_root / "candidate-file-manifest.v1.json"
        build_receipt_path = candidate_root / "desktop-build-receipt.v1.json"
        integration_path = candidate_root / "candidate-integration.json"
        require(sha256(manifest_path) == config["manifestSha256"].upper(), "Desktop candidate manifest hash mismatch")
        manifest = load_json(manifest_path)
        build_receipt = load_json(build_receipt_path)
        integration = load_json(integration_path)
        require(manifest["candidateId"] == candidate_root.parent.name and manifest["entrypoint"] == "KnowledgeClipper.exe", "Desktop candidate identity mismatch")
        payload = _verify_manifest_payload(candidate_root, manifest)
        desktop_payload = _verify_desktop_build(candidate_root, build_receipt)
        entrypoint_path = candidate_root / "KnowledgeClipper.exe"
        application_dll_path = candidate_root / "KnowledgeClipper.dll"
        require(sha256(entrypoint_path) == config["entrypointSha256"].upper(), "Desktop entrypoint hash mismatch")
        require(sha256(application_dll_path) == config["applicationDllSha256"].upper(), "Desktop application DLL hash mismatch")
        require(manifest["desktop"]["buildReceiptSha256"].upper() == sha256(build_receipt_path), "candidate/Desktop build receipt binding mismatch")
        require(manifest["desktop"]["treeSha256"].upper() == desktop_payload["treeSha256"], "candidate/Desktop tree binding mismatch")
        authority_root = candidate_root / "authority" / "win-x64"
        authority_inventory = common.inventory(authority_root)
        require(authority_inventory == {**authority_inventory, "fileCount": authority["release"]["fileCount"], "totalBytes": authority["release"]["totalBytes"], "treeSha256": authority["release"]["treeSha256"]}, "embedded authority subtree mismatch")
        embedded_checkpoint = candidate_root / "authority" / "metadata" / "checkpoint.json"
        embedded_registry = candidate_root / "authority" / "metadata" / "worker-registry.v1.json"
        require(sha256(embedded_checkpoint) == authority["checkpoint"]["sha256"] and sha256(embedded_registry) == authority["registry"]["sha256"], "embedded authority metadata mismatch")
        require(manifest["authority"]["registryVersion"] == "1.2.12" and manifest["authority"]["releaseTreeSha256"].upper() == authority["release"]["treeSha256"], "candidate manifest authority mismatch")
        require(integration["AuthorityCheckpointId"] == manifest["authority"]["checkpointId"] and integration["AuthorityTreeSha256"].upper() == authority["release"]["treeSha256"], "candidate integration authority mismatch")
        require(integration["AutoStart"] is True and integration["OrchestratorExecutable"] == "authority/win-x64/orchestrator/KnowledgeClipper.Orchestrator.exe", "candidate internal Orchestrator binding mismatch")
        settings_ui_log_path = resolve_ref(config["settingsUiAutomationLog"], "desktop.settingsUiAutomationLog")
        settings_ui_log = settings_ui_log_path.read_text(encoding="utf-8-sig")
        require("WINDOWS_SETTINGS_UI_CANARY PASS assertions=97" in settings_ui_log, "Desktop settings UI automation evidence drifted")
        ui_log_path = resolve_ref(config["uiAutomationLog"], "desktop.uiAutomationLog")
        ui_log = ui_log_path.read_text(encoding="utf-8-sig")
        required_ui = (
            "WINDOWS_UI_CANARY PASS assertions=152",
            "context and original-image modes are persistent visible controls",
            "inline question is a real single-line TextBox with the locked hint",
            "inline question is an independent bottom row",
            "settings exposes language override automation ID",
            "settings binds context mode automation ID",
            "settings binds original-image mode automation ID",
        )
        require(all(marker in ui_log for marker in required_ui), "Desktop UI automation evidence missing required assertions")
        physical_ctrl_c_path = resolve_ref(config["physicalCtrlCLog"], "desktop.physicalCtrlCLog")
        physical_ctrl_c = physical_ctrl_c_path.read_text(encoding="utf-8-sig").strip()
        require(physical_ctrl_c == "FAIL Canary window did not receive foreground focus", "physical Ctrl+C boundary evidence drifted")
        secret_scan = settings_quality.scan_runtime_tree_for_secrets(candidate_root)
        require(secret_scan["findingCount"] == 0, "candidate secret scan found plaintext evidence")
        forbidden = [path.relative_to(candidate_root).as_posix() for path in candidate_root.rglob("*") if path.is_file() and path.name.casefold() in {"smoketest.exe", "ttscanary.exe"}]
        require(not forbidden, f"candidate contains legacy external canaries: {forbidden}")
        runtime: dict[str, Any] = {"status": "NOT-RUN-IN-UNIT-EVALUATION"}
        if run_binaries:
            require(runtime_root is not None, "runtime evidence root required")
            runtime_root.mkdir(parents=True, exist_ok=True)
            startup = subprocess.run([str(candidate_root / "KnowledgeClipper.exe"), "--startup-diagnostic"], cwd=candidate_root, capture_output=True, text=True, timeout=30, check=False)
            require(startup.returncode == 0, f"candidate startup diagnostic failed: {startup.stderr[:500]}")
            startup_path = runtime_root / "startup-diagnostic.json"
            startup_path.write_text(startup.stdout.strip() + "\n", encoding="utf-8")
            startup_json = json.loads(startup.stdout)
            require(startup_json["result"] == "PASS" and startup_json["packageTreeVerified"] is True and startup_json["candidateOrchestratorPathsVerified"] is True, "candidate startup diagnostic mismatch")
            require(startup_json["externalConnections"] == 0 and startup_json["remNoteInvoked"] is False and startup_json["mineruInvoked"] is False and startup_json["productionWorkDirectoryCreated"] is False, "candidate startup exceeded safe boundary")
            dotnet = Path(r"D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe")
            harness_project = PROJECT_ROOT / "tests" / "CandidateIpc.QualityHarness" / "CandidateIpc.QualityHarness.csproj"
            built = subprocess.run([str(dotnet), "build", str(harness_project), "--configuration", "Release", "--nologo", "-warnaserror"], cwd=PROJECT_ROOT, capture_output=True, text=True, timeout=120, check=False)
            require(built.returncode == 0, f"candidate IPC harness build failed: {(built.stdout + built.stderr)[-1500:]}")
            harness = PROJECT_ROOT / "tests" / "CandidateIpc.QualityHarness" / "bin" / "Release" / "net8.0-windows" / "CandidateIpc.QualityHarness.dll"
            jobs = runtime_root / "isolated-jobs"
            ipc = subprocess.run([str(dotnet), str(harness), str(candidate_root), str(jobs)], cwd=PROJECT_ROOT, capture_output=True, text=True, timeout=60, check=False)
            require(ipc.returncode == 0, f"candidate internal IPC gate failed: {ipc.stderr[:500]}")
            ipc_path = runtime_root / "candidate-ipc-query.json"
            ipc_path.write_text(ipc.stdout.strip() + "\n", encoding="utf-8")
            ipc_json = json.loads(ipc.stdout)
            require(ipc_json["result"] == "PASS" and ipc_json["namedPipeInvoked"] is True and ipc_json["orchestratorStartedByDesktop"] is True, "candidate IPC result mismatch")
            require(all(ipc_json[key] is False for key in ("externalWorkerInvoked", "physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")), "candidate IPC gate exceeded safe boundary")
            process_check = subprocess.run(["pwsh", "-NoProfile", "-Command", f"if (Get-Process -Id {int(ipc_json['orchestratorProcessId'])} -ErrorAction SilentlyContinue) {{ exit 1 }} else {{ exit 0 }}"], capture_output=True, text=True, timeout=15, check=False)
            require(process_check.returncode == 0, "candidate canary Orchestrator process was not cleaned up")
            single_instance_root = runtime_root / "single-instance"
            single_instance = subprocess.run(
                [str(dotnet), str(harness), "single-instance", str(candidate_root), str(single_instance_root)],
                cwd=PROJECT_ROOT,
                capture_output=True,
                text=True,
                timeout=30,
                check=False,
            )
            require(single_instance.returncode == 0, f"candidate single-instance non-silent gate failed: {single_instance.stderr[:500]}")
            single_instance_path = runtime_root / "single-instance-non-silent.json"
            single_instance_path.write_text(single_instance.stdout.strip() + "\n", encoding="utf-8")
            single_instance_json = json.loads(single_instance.stdout)
            require(single_instance_json["result"] == "PASS" and single_instance_json["canonicalMutexDetected"] is True, "candidate single-instance collision was not detected")
            require(single_instance_json["userFeedbackMode"] == "activate-existing-window-and-message-box" and single_instance_json["existingUserProcessTerminated"] is False, "candidate single-instance behavior was silent or destructive")
            runtime = {
                "status": "PASS",
                "startup": evidence(startup_path),
                "ipc": evidence(ipc_path),
                "singleInstanceNonSilent": evidence(single_instance_path),
                "isolatedJobsRoot": jobs.relative_to(PROJECT_ROOT).as_posix(),
                "canaryOrchestratorProcessId": int(ipc_json["orchestratorProcessId"]),
                "canaryOrchestratorCleanedUp": True,
                "preExistingProcessesTerminated": False,
            }
        full = common.inventory(candidate_root)
        require(full["fileCount"] == config["sealedFileCount"], "candidate sealed file count mismatch")
        require(full["totalBytes"] == config["sealedTotalBytes"], "candidate sealed bytes mismatch")
        require(full["treeSha256"] == config["sealedTreeSha256"].upper(), "candidate sealed tree mismatch")
        owner_receipt_path = resolve_ref(config["ownerReceipt"], "desktop.ownerReceipt")
        owner_receipt = load_json(owner_receipt_path)
        require(owner_receipt["userHandTestEntrypoint"] is False, "owner receipt incorrectly exposes a hand-test entrypoint")
        require(owner_receipt["candidate"]["root"] == explicit, "owner receipt candidate root mismatch")
        require(owner_receipt["candidate"]["manifest"]["sha256"].upper() == sha256(manifest_path), "owner receipt manifest mismatch")
        require(owner_receipt["candidate"]["sealed"] == {
            "fileCount": full["fileCount"],
            "totalBytes": full["totalBytes"],
            "treeSha256": full["treeSha256"],
            "treeAlgorithm": owner_receipt["candidate"]["sealed"]["treeAlgorithm"],
        }, "owner receipt sealed inventory mismatch")
        require(owner_receipt["candidate"]["entrypointSha256"].upper() == sha256(entrypoint_path), "owner receipt entrypoint mismatch")
        require(owner_receipt["candidate"]["applicationDllSha256"].upper() == sha256(application_dll_path), "owner receipt DLL mismatch")
        return {
            "status": "PASS-INTEGRATED-V1.2.12-CANDIDATE-BOUND",
            "root": explicit,
            "candidateBound": True,
            "candidateHash": sha256(manifest_path),
            "manifest": evidence(manifest_path),
            "payload": payload,
            "fullInventory": {key: full[key] for key in ("fileCount", "totalBytes", "treeSha256")},
            "desktopPayload": desktop_payload,
            "authoritySubtree": {key: authority_inventory[key] for key in ("fileCount", "totalBytes", "treeSha256")},
            "entrypoint": evidence(entrypoint_path),
            "applicationDll": evidence(application_dll_path),
            "buildReceipt": evidence(build_receipt_path),
            "integrationConfig": evidence(integration_path),
            "settingsUiAutomation": {"status": "PASS-MODULE-EVIDENCE", "assertions": 97, "log": evidence(settings_ui_log_path)},
            "uiAutomation": {"status": "PASS-MODULE-EVIDENCE", "assertions": 152, "log": evidence(ui_log_path)},
            "physicalCtrlC": {"status": "CONDITIONAL-FOREGROUND-FOCUS-NOT-ACQUIRED", "log": evidence(physical_ctrl_c_path), "candidatePromotionBlocked": False, "manualEnvironmentBoundary": True},
            "runtime": runtime,
            "secretScan": secret_scan,
            "legacyExternalCanaries": {"status": "PASS-ABSENT", "hits": []},
            "ownerReceipt": {"status": "CONSISTENT-WITH-INDEPENDENT-GATE", "trustedForPass": False, "userHandTestEntrypoint": False, "evidence": evidence(owner_receipt_path)},
            "userHandTestEntrypoint": False,
            "releaseGo": False,
        }
    candidates = []
    for path in (PROJECT_ROOT / "releases").glob("*"):
        if path.is_dir() and "1.2.12" in path.name and "authority" not in path.name.casefold():
            candidates.append(path.relative_to(PROJECT_ROOT).as_posix())
    require(not candidates, f"Desktop v1.2.12 candidate appeared but fixture was not bound: {candidates}")
    return {
        "status": "WAITING-DESKTOP-V1.2.12-INTEGRATED-CANDIDATE",
        "requiredRegistryVersion": "1.2.12",
        "candidateBound": False,
        "candidateHash": None,
    }


def evaluate(fixture: dict[str, Any], *, run_binaries: bool = False, runtime_root: Path | None = None) -> dict[str, Any]:
    require(fixture["policy"] == {"externalCalls": 0, "workerRuns": 0, "candidateHashBeforeDesktop": False, "pendingIsFailure": False}, "quality policy weakened")
    require(INVALIDATED_OLD_RECEIPT.is_file() and sha256(INVALIDATED_OLD_RECEIPT) == INVALIDATED_OLD_RECEIPT_SHA256, "superseded 17:50 quality receipt evidence drifted")
    core = verify_core(fixture["core"])
    authority = verify_authority(fixture["authority"], run_binaries)
    require(authority["checkpoint"]["sha256"] == "552151B3EDBEED94FD1B4ECC7B49F78CD7503321119235BBF3ECAA2A0EEE8580", "authority identity drifted")
    anki = verify_anki(fixture["ankiPostAuthority"], authority)
    desktop = verify_desktop(fixture["desktop"], authority, run_binaries=run_binaries, runtime_root=runtime_root)
    image = {
        "status": "PASS-AUTHORITY-BOUND",
        "workerVersion": "1.2.2",
        "promotionNoGoResolved": True,
        "originalMode": core["requirements"]["originalImage"],
        "partial": core["requirements"]["imagePartial"],
        "boundary": "original image downstream remains terminal-unsupported; this is not a promotion blocker for image.ocr.mineru",
    }
    content = {
        "status": "PASS-AUTHORITY-BOUND-OFFLINE",
        "workerVersion": authority["workers"]["content.deepseek"]["workerVersion"],
        "configSchemaVersion": core["requirements"]["contentSettings"]["configSchemaVersion"],
        "runtimePolicyFrozen": core["requirements"]["contentSettings"]["runtimePolicyFrozen"],
        "promptAfterRecognition": core["requirements"]["contentSettings"]["promptAfterRecognition"],
    }
    speech = {
        "status": "PASS-AUTHORITY-BOUND-OFFLINE",
        "workerVersion": authority["workers"]["speech.edge"]["workerVersion"],
        "languageOverrideBound": core["requirements"]["speechSettings"]["languageOverrideBound"],
        "speakerPreview": core["requirements"]["speechSettings"]["speakerPreview"],
    }
    remnote = {
        "status": "PASS-AUTHORITY-BOUND-OFFLINE",
        "workerVersion": authority["workers"]["export.remnote"]["workerVersion"],
        "probe": core["requirements"]["remnoteProbe"],
    }
    candidate_bound = desktop["candidateBound"]
    return {
        "schemaVersion": "knowledge-clipper-v1212-candidate-admission-readiness.v1",
        "generatedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "CONDITIONAL-CANDIDATE-BOUND-V1.2.12-OWNER-MODULES-PASS-EXTERNAL-BOUNDARIES-PENDING" if candidate_bound else "WAITING-DESKTOP-V1.2.12-INTEGRATED-CANDIDATE",
        "scope": "candidate-bound offline qualification" if candidate_bound else "candidate admission readiness; authority/Core/module bound; Desktop integrated candidate pending",
        "candidateBound": candidate_bound,
        "candidateHash": desktop.get("candidateHash"),
        "fixtureValidation": validate_fixtures(),
        "core": core,
        "authority": authority,
        "modules": {"image": image, "content": content, "speech": speech, "remnote": remnote, "anki": anki},
        "desktop": desktop,
        "boundaries": {
            "originalImageDownstream": "terminal-unsupported",
            "remnoteProbe": "conditional-cli-only",
            "speakerPreview": "conditional-explicit-physical-audio-not-run",
            "retention": "disabled-fail-closed; state-aware service not implemented",
            "physicalDesktopSelection": "conditional-foreground-focus-not-acquired",
        },
        "qualitySideEffects": {"externalCalls": 0, "workerRuns": 0, "mineruCalls": 0, "ankiWrites": 0, "remnoteWrites": 0, "physicalSpeakerInvocations": 0},
        "supersedes": [{
            "path": INVALIDATED_OLD_RECEIPT.relative_to(PROJECT_ROOT).as_posix(),
            "sha256": INVALIDATED_OLD_RECEIPT_SHA256,
            "status": "INVALIDATED-WRONG-SUPERSEDED-CANDIDATE",
            "reason": "receipt bound releases/kc-integrated-settings-v1212-20260830T175000Z/win-x64 instead of the sealed 20260830T183500Z candidate",
        }],
    }


def render_index(result: dict[str, Any]) -> str:
    workers = result["authority"]["workers"]
    lines = [
        "# KnowledgeClipper v1.2.12 candidate admission readiness",
        "",
        f"- Status: `{result['status']}`",
        f"- Candidate-bound: `{str(result['candidateBound']).lower()}`; candidateHash: `{result['candidateHash'] or 'none'}`.",
        f"- Authority: `1.2.12`, release tree `{result['authority']['release']['treeSha256']}`.",
        f"- Frozen contracts/v1: {result['authority']['frozenV1']['filesChecked']} files, {result['authority']['frozenV1']['mismatches']} mismatch.",
        "",
        "## Authority-bound workers",
        "",
    ]
    for worker_id in EXPECTED_WORKERS:
        item = workers[worker_id]
        lines.append(f"- `{worker_id}@{item['workerVersion']}` -> `{item['entrypoint']}` -> `{item['status']}`")
    lines.extend([
        "",
        "## Input -> output boundaries",
        "",
        "- Image/OCR original mode -> `image.original`; provider/MinerU calls = 0; terminal = `unsupported`; downstream not started.",
        "- Image/OCR partial -> retry failed items only; successful item hashes reused; whole-card downstream blocked until merged completion; unknown external state is not retried.",
        "- Anki explicit probe -> SHA-bound WorkerRequest -> authority `export.anki@1.2.1` -> `anki-connection-status: ready`; real AnkiConnect calls/writes = 0.",
        "",
        "## Still pending / NO-GO boundaries",
        "",
        "- Candidate manifest/tree/startup/IPC/secret/inventory are bound when candidate-bound is true; this does not promote external paths.",
        "- RemNote probe remains conditional; speaker preview remains explicit/conditional; retention remains disabled fail-closed until a state-aware service exists.",
        "- Physical Ctrl+C selection remains a manual environment boundary because the Canary window did not acquire foreground focus; 152 automated UI assertions remain PASS.",
        "- The earlier `20260830T082337Z-final-candidate-bound` receipt is invalidated because it bound the superseded 17:50 candidate; it is retained unchanged as historical evidence.",
    ])
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify v1.2.12 authority/Core/module readiness without external worker runs.")
    parser.add_argument("--fixture", default=str(DEFAULT_FIXTURE))
    parser.add_argument("--run-binaries", action="store_true", help="Run describe and orchestrator validate-registry only; never run workers.")
    parser.add_argument("--emit-root")
    args = parser.parse_args()
    fixture_path = Path(args.fixture).resolve()
    artifacts: dict[str, Any] = {}
    root = None
    if args.emit_root:
        root = Path(args.emit_root).resolve()
        require(root.is_relative_to(PROJECT_ROOT.resolve()), "output root escaped project")
        require(not root.exists(), f"append-only output root exists: {root}")
        root.mkdir(parents=True)
    result = evaluate(load_json(fixture_path), run_binaries=args.run_binaries, runtime_root=(root / "runtime" if root else None))
    result["fixture"] = evidence(fixture_path)
    if root:
        receipt = root / ("quality-candidate-receipt.json" if result["candidateBound"] else "quality-readiness-receipt.json")
        readable = root / "input-output-index.md"
        receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        readable.write_text(render_index(result), encoding="utf-8")
        artifacts = {"root": root.relative_to(PROJECT_ROOT).as_posix(), "receipt": evidence(receipt), "readableIndex": evidence(readable)}
    print(json.dumps({"result": result, "artifacts": artifacts}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
