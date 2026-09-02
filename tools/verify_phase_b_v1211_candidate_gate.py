from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator, FormatChecker

import verify_integrated_candidate_quality as common
import verify_phase_b_candidate_gate as base
import verify_settings_control_plane as settings_quality


PROJECT_ROOT = Path(__file__).resolve().parents[1]
FIXTURE_ROOT = PROJECT_ROOT / "tests" / "Quality.Gates" / "fixtures" / "phase-b-v1211-candidate"
FIXTURE_SCHEMA = FIXTURE_ROOT / "phase-b-v1211-candidate-gate.schema.json"
DEFAULT_FIXTURE = FIXTURE_ROOT / "valid" / "phase-b-v1.2.11.json"


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def load_json(path: Path) -> dict[str, Any]:
    return base.load_json(path)


def checked_ref(item: dict[str, Any], label: str) -> Path:
    try:
        return base.checked_ref(item, label)
    except base.GateFailure as exc:
        raise GateFailure(str(exc)) from exc


def evidence(path: Path) -> dict[str, Any]:
    try:
        return base.evidence(path)
    except base.GateFailure as exc:
        raise GateFailure(str(exc)) from exc


def validate_fixtures() -> dict[str, Any]:
    schema = load_json(FIXTURE_SCHEMA)
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema, format_checker=FormatChecker())
    valid = sorted((FIXTURE_ROOT / "valid").glob("*.json"))
    invalid = sorted((FIXTURE_ROOT / "invalid").glob("*.json"))
    require(valid and invalid, "v1.2.11 gate fixtures are incomplete")
    for path in valid:
        errors = list(validator.iter_errors(load_json(path)))
        require(not errors, f"valid fixture rejected: {path.name}: {errors[0].message if errors else ''}")
    for path in invalid:
        require(bool(list(validator.iter_errors(load_json(path)))), f"invalid fixture accepted: {path.name}")
    return {
        "status": "passed",
        "schema": evidence(FIXTURE_SCHEMA),
        "validAccepted": len(valid),
        "invalidRejected": len(invalid),
    }


def verify_authority(config: dict[str, Any], full_inventory: bool) -> dict[str, Any]:
    checkpoint_path = checked_ref(config["checkpoint"], "authority.checkpoint")
    receipt_path = checked_ref(config["receipt"], "authority.receipt")
    registry_path = checked_ref(config["registry"], "authority.registry")
    dependency_path = checked_ref(config["dependencyManifest"], "authority.dependencyManifest")
    runtime_path = checked_ref(config["runtimeGate"], "authority.runtimeGate")
    checkpoint = load_json(checkpoint_path)
    receipt = load_json(receipt_path)
    registry = load_json(registry_path)
    runtime = load_json(runtime_path)

    require(checkpoint["admissionStatus"] == receipt["admissionStatus"] == "authority-admitted", "v1.2.11 is not admitted")
    require(checkpoint["registry"]["registryVersion"] == registry["registryVersion"] == config["registryVersion"] == "1.2.11", "authority version mismatch")
    require(checkpoint["registry"]["sha256"].upper() == config["registrySha256"].upper() == base.sha256_file(registry_path), "authority registry hash mismatch")
    require(receipt["checkpoint"]["sha256"].upper() == base.sha256_file(checkpoint_path), "authority receipt/checkpoint binding mismatch")
    require(checkpoint["release"]["dependencyManifestSha256"].upper() == receipt["release"]["dependencyManifestSha256"].upper() == base.sha256_file(dependency_path), "authority dependency manifest binding mismatch")
    require(checkpoint["runtimeGates"]["sha256"].upper() == receipt["runtimeGates"]["sha256"].upper() == base.sha256_file(runtime_path), "authority runtime receipt binding mismatch")
    require(runtime["status"] == "passed" and runtime["workerRunInvocations"] == runtime["externalWrites"] == runtime["physicalSpeakerInvocations"] == 0, "authority runtime admission side-effect boundary mismatch")
    require(checkpoint["frozenV1"] == {**checkpoint["frozenV1"], "files": 21, "mismatches": 0}, "authority frozen v1 mismatch")
    require(checkpoint["caveat"] is None, "authority has a caveat")

    workers = {item["workerId"]: item["workerVersion"] for item in checkpoint["workers"]}
    expected_workers = {
        "image.ocr.mineru": "1.2.0",
        "content.deepseek": "0.6.1",
        "speech.edge": "1.2.3",
        "export.remnote": "2.0.0",
        "export.anki": "1.2.0",
    }
    require(workers == expected_workers and len(registry["workers"]) == 5, "authority worker identity/version set mismatch")
    release_root = Path(checkpoint["release"]["root"]).resolve()
    expected_release = {
        "fileCount": int(config["releaseFiles"]),
        "totalBytes": int(config["releaseBytes"]),
        "treeSha256": config["releaseTreeSha256"].upper(),
    }
    require(expected_release == {"fileCount": checkpoint["release"]["files"], "totalBytes": int(checkpoint["release"]["bytes"]), "treeSha256": checkpoint["release"]["treeSha256"].upper()}, "authority checkpoint release summary mismatch")
    if full_inventory:
        inventory = common.inventory(release_root)
        for key, value in expected_release.items():
            require(inventory[key] == value, f"authority release {key} mismatch")
        actual_release = {key: inventory[key] for key in ("fileCount", "totalBytes", "treeSha256")}
    else:
        actual_release = expected_release
    require(checkpoint["coreSettings"]["checkpointVersion"] == "2.4.8" and checkpoint["coreSettings"]["sha256"].upper() == config["coreCheckpointSha256"].upper(), "authority/Core v2.4.8 binding mismatch")
    return {
        "status": "PASS-AUTHORITY-ADMITTED",
        "registryVersion": "1.2.11",
        "checkpoint": evidence(checkpoint_path),
        "receipt": evidence(receipt_path),
        "registry": evidence(registry_path),
        "dependencyManifest": evidence(dependency_path),
        "runtimeGate": evidence(runtime_path),
        "release": {"root": base.display_path(release_root), **actual_release},
        "workers": workers,
    }


def verify_invalidations(config: dict[str, Any]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for version, key, replacement in (("1.2.9", "v129", "1.2.10"), ("1.2.10", "v1210", "1.2.11")):
        path = checked_ref(config[key], f"invalidations.{key}")
        document = load_json(path)
        require(document["registryVersion"] == version, f"{version} invalidation identity mismatch")
        require(document["status"] == "invalidated-never-promoted" and document["authorityBound"] is False, f"{version} was not invalidated-never-promoted")
        require(document["replacement"]["minimumRegistryVersion"] == replacement and document["replacement"]["rebuildFromOriginalFinalInputs"] is True, f"{version} replacement policy mismatch")
        result[version] = {"status": "REJECTED-INVALIDATED-NEVER-PROMOTED", "receipt": evidence(path), "eligibleForCandidate": False, "reason": document["reason"]}
    return result


def verify_core(config: dict[str, Any]) -> dict[str, Any]:
    checkpoint_path = checked_ref(config["checkpoint"], "core.checkpoint")
    checkpoint = load_json(checkpoint_path)
    require(checkpoint["checkpointVersion"] == "2.4.8" and checkpoint["status"] == "passed-core-runtime-authority-ready", "Core v2.4.8 status mismatch")
    require(checkpoint["core"]["version"] == "2.4.6", "Core runtime version mismatch")
    tree = checkpoint["tree"]
    rows: list[str] = []
    total = 0
    current_source_advance: list[dict[str, Any]] = []
    files: list[dict[str, Any]] = []
    for item in sorted(tree["files"], key=lambda value: value["path"]):
        path = (PROJECT_ROOT / item["path"]).resolve()
        if not path.is_relative_to(PROJECT_ROOT.resolve()) or not base.io_path(path).is_file():
            raise GateFailure(f"Core v2.4.8 evidence missing/escaping: {item['path']}")
        actual_hash = base.sha256_file(path)
        actual_bytes = base.io_path(path).stat().st_size
        if actual_hash != str(item["sha256"]).upper() or actual_bytes != int(item["bytes"]):
            current_source_advance.append({
                "path": item["path"],
                "checkpointSha256": str(item["sha256"]).upper(),
                "currentSha256": actual_hash,
                "checkpointBytes": int(item["bytes"]),
                "currentBytes": actual_bytes,
            })
        total += int(item["bytes"])
        rows.append(f"{item['path']}\0{str(item['sha256']).lower()}\0{int(item['bytes'])}\n")
        files.append(evidence(path))
    actual_tree = hashlib.sha256("".join(rows).encode("utf-8")).hexdigest().upper()
    require(len(tree["files"]) == int(config["treeFiles"]) == 37 and total == int(config["treeBytes"]) == 68581140, "Core v2.4.8 tree count/bytes mismatch")
    require(actual_tree == str(tree["treeSha256"]).upper() == config["treeSha256"].upper(), "Core v2.4.8 tree hash mismatch")
    allowed_advance = {"tests/PhaseB.Canary/Program.cs", "tools/Invoke-KnowledgeClipperPhaseB.ps1"}
    require({item["path"] for item in current_source_advance} <= allowed_advance, f"Core v2.4.8 unexpected evidence drift: {current_source_advance}")
    validation = checkpoint["validation"]
    require(validation["orchestrator"]["status"] == "passed" and validation["orchestrator"]["tests"] == 23, "Core orchestrator regression mismatch")
    require(validation["build"]["status"] == "passed" and validation["build"]["warnings"] == validation["build"]["errors"] == 0, "Core warnings-as-errors build mismatch")
    require(validation["settings"]["status"] == "passed" and validation["settings"]["externalCalls"] == validation["settings"]["physicalSpeakerInvocations"] == 0, "Core settings side-effect boundary mismatch")
    return {
        "status": "PASS-AUTHORITY-BOUND",
        "checkpoint": evidence(checkpoint_path),
        "runtimeVersion": checkpoint["core"]["version"],
        "tree": {"fileCount": len(files), "totalBytes": total, "treeSha256": actual_tree, "checkpointInternalMismatches": 0},
        "sourceAdvancedAfterCheckpoint": current_source_advance,
        "orchestratorTests": 23,
        "build": {"warnings": 0, "errors": 0},
        "files": files,
    }


def _artifact_json(path_result: dict[str, Any], kind: str, filename: str) -> tuple[Path, dict[str, Any]]:
    for attempt in path_result["attempts"]:
        for artifact in attempt["artifacts"]:
            if artifact["reference"]["kind"] != kind:
                continue
            for item in artifact["files"]:
                path = base.resolve_path(item["path"])
                if path.name == filename:
                    return path, load_json(path)
    raise GateFailure(f"{path_result['path']}: missing {kind}/{filename}")


def _verify_execution(path: Path, expected_path: str, expected_receipt: Path, authority: dict[str, Any]) -> dict[str, Any]:
    document = load_json(path)
    require(document["schemaVersion"] == "knowledge-clipper-phase-b-execution.v1", f"{expected_path}: execution schema mismatch")
    require(document["authority"]["registryVersion"] == "1.2.11" and document["authority"]["registrySha256"].upper() == authority["registry"]["sha256"], f"{expected_path}: execution authority mismatch")
    require(document["authority"]["checkpointSha256"].upper() == authority["checkpoint"]["sha256"], f"{expected_path}: execution checkpoint mismatch")
    require(document["coreSettings"]["integrationCheckpoint"] == "2.4.8", f"{expected_path}: execution Core settings mismatch")
    require(document["secretsWritten"] == 0 and document["remnoteWritesDisabled"] is True, f"{expected_path}: execution secret/RemNote boundary mismatch")
    require(document["isolatedDestinations"]["anki"]["existingDeckUsed"] is False, f"{expected_path}: non-isolated Anki deck used")
    require(document["isolatedDestinations"]["remnote"]["writesDisabled"] is True, f"{expected_path}: RemNote writes were enabled")
    runs = document["runs"]
    require(len(runs) == 1 and runs[0]["path"] == expected_path and runs[0]["exitCode"] == 0 and Path(runs[0]["receipt"]).resolve() == expected_receipt.resolve(), f"{expected_path}: execution/run binding mismatch")
    return {"receipt": evidence(path), "isolatedDestinations": document["isolatedDestinations"], "secretsWritten": 0}


def verify_paths(config: dict[str, Any], authority: dict[str, Any]) -> dict[str, Any]:
    qualification_path = checked_ref(config["qualificationReceipt"], "paths.qualificationReceipt")
    qualification_readable_path = checked_ref(config["qualificationReadable"], "paths.qualificationReadable")
    qualification = load_json(qualification_path)
    require(qualification["status"] == "passed-conditional" and qualification["authority"]["registryVersion"] == "1.2.11", "A/D qualification identity/status mismatch")
    require(qualification["authority"]["checkpointSha256"].upper() == authority["checkpoint"]["sha256"], "A/D qualification authority mismatch")

    path_results: dict[str, Any] = {}
    card_pairs: dict[str, tuple[str, str]] = {}
    for path_name in ("A", "D"):
        group = config[path_name]
        receipt_path = checked_ref(group["receipt"], f"paths.{path_name}.receipt")
        readable_path = checked_ref(group["readable"], f"paths.{path_name}.readable")
        execution_path = checked_ref(group["executionReceipt"], f"paths.{path_name}.executionReceipt")
        try:
            result = base.verify_path_receipt(receipt_path, {"registryVersion": "1.2.11", "registrySha256": authority["registry"]["sha256"]}, "succeeded")
            snapshot_chain = base.verify_settings_snapshot_chain(result)
        except base.GateFailure as exc:
            raise GateFailure(str(exc)) from exc
        execution = _verify_execution(execution_path, path_name, receipt_path, authority)
        require(result["plan"]["routing"] == {"processingProfile": "cards", "destinations": ["anki"], "source": "knowledge-clipper-task-settings-snapshot.v2"}, f"{path_name}: routing mismatch")
        require(all(item["status"] == "completed" for item in result["attempts"]), f"{path_name}: incomplete attempt")
        require(not any(item["worker"]["id"] == "export.remnote" for item in result["attempts"]), f"{path_name}: RemNote worker unexpectedly started")
        card_path, card_bundle = _artifact_json(result, "card-bundle", "card-bundle.schema-v1.json")
        base.validate_project_schema(card_bundle, PROJECT_ROOT / "content-worker" / "schemas" / "card-bundle.v1.schema.json", f"path{path_name}.cardBundle")
        require(len(card_bundle["cards"]) == 1, f"{path_name}: expected one card")
        card = card_bundle["cards"][0]
        front = "\n".join(item["text"] for item in card["front_blocks"] if item["type"] == "text")
        back = "\n".join(item["text"] for item in card["back_blocks"] if item["type"] == "text")
        card_pairs[path_name] = (front, back)
        anki_receipt_path, anki_receipt = _artifact_json(result, "anki-export-evidence", "receipt.json")
        require(anki_receipt["status"] == "SUCCEEDED" and anki_receipt["counts"] == {"already_current": 0, "blocked_upstream": 0, "failed": 0, "succeeded": 1}, f"{path_name}: Anki export receipt mismatch")
        path_results[path_name] = {
            **result,
            "status": "PASS-CANDIDATE-BOUND-SCOPED",
            "readable": evidence(readable_path),
            "execution": execution,
            "settingsSnapshotChain": snapshot_chain,
            "cardBundle": evidence(card_path),
            "card": {"front": front, "back": back},
            "ankiReceipt": evidence(anki_receipt_path),
        }

    a = path_results["A"]
    require(a["invocationCounts"] == {"export.anki": 1, "content.deepseek": 1}, "A invocation counts mismatch")
    d = path_results["D"]
    require(d["invocationCounts"] == {"export.anki": 1, "content.deepseek": 1, "speech.edge": 3}, "D invocation counts mismatch")
    synth = [item for item in d["attempts"] if item["stageId"] == "speech-synthesize"]
    playback = [item for item in d["attempts"] if item["stageId"] == "speech-playback"]
    require(len(synth) == 1 and len(playback) == 2 and len({item["attemptId"] for item in playback}) == 2, "D fresh synth/playback attempt count mismatch")
    require(all(item["payload"].get("cacheHit") is False and "reusedFromAttemptId" not in item["payload"] and item["payload"].get("playbackState") == "idle_replayable" for item in playback), "D playback reuse/natural completion mismatch")
    require(d["replay"] == {"type": "playback-from-start", "status": "succeeded", "planStageCount": 1, "operation": "speech.playback", "exportStages": 0, "contentStages": 0}, "D playback replay boundary mismatch")
    audio_artifacts = [artifact for item in synth for artifact in item["artifacts"] if artifact["reference"]["kind"] == "audio.speech"]
    require(len(audio_artifacts) == 1 and len(audio_artifacts[0]["files"]) == 1, "D audio artifact mismatch")
    audio = audio_artifacts[0]["files"][0]
    audio_path = base.resolve_path(audio["path"])
    audio_length = len(base.logical_path_text(audio_path.resolve()))
    require(audio_length == qualification["pathD"]["audio"]["absolutePathLength"] == 277 and audio["sha256"] == qualification["pathD"]["audio"]["sha256"], "D audio long-path/hash mismatch")
    require(qualification["pathD"]["playback"] == {"physicalSpeakerInvocations": 2, "naturalCompletion": "idle_replayable", "replay": "from_start", "replayStatus": "succeeded", "replayExportStages": 0, "replayContentStages": 0}, "D physical playback aggregation mismatch")

    readback = qualification["ankiReadback"]
    require(readback["deck"] == a["execution"]["isolatedDestinations"]["anki"]["deck"] == d["execution"]["isolatedDestinations"]["anki"]["deck"], "Anki isolated deck mismatch")
    require(readback["noteCount"] == len(readback["notes"]) == 2 and len(readback["cards"]) == 2, "Anki readback count mismatch")
    note_pairs = {(item["fields"]["Front"]["value"], item["fields"]["Back"]["value"]) for item in readback["notes"]}
    require(note_pairs == set(card_pairs.values()), "Anki readback does not match A/D card bundles")
    require(all(item["deckName"] == readback["deck"] for item in readback["cards"]), "Anki readback escaped isolated deck")
    require(qualification["pathA"]["deepSeekProviderCalls"] == qualification["pathD"]["deepSeekProviderCalls"] == 3, "DeepSeek provider call counts mismatch")
    require(qualification["remnote"] == {**qualification["remnote"], "workerInvocations": 0, "writes": 0, "oldDCommitUnknown": "unresolved", "automaticRetry": False}, "RemNote no-retry boundary mismatch")
    require(qualification["pathC"]["status"] == "blocked-credential-not-run" and qualification["pathC"]["mineruProviderCalls"] == 0, "C credential/pending boundary mismatch")
    readable_text = base.io_path(qualification_readable_path).read_text(encoding="utf-8-sig")
    require("$deck" in readable_text, "known qualification readable placeholder drifted; gate must be reviewed")
    return {
        "A": path_results["A"],
        "D": {**path_results["D"], "audio": audio | {"absolutePathLength": audio_length}, "physicalSpeakerInvocationsInSourceEvidence": 2},
        "B": {"status": "PENDING-REMNOTE-RECONCILIATION", "reason": qualification["remnote"]["blocker"], "oldCommitUnknown": "unresolved", "automaticRetry": False},
        "C": {"status": "PENDING-MINERU-CREDENTIAL", "reason": qualification["pathC"]["reason"], "mineruProviderCalls": 0, "secretPersisted": False},
        "ankiReadback": {"status": "PASS-ISOLATED", "deck": readback["deck"], "notes": readback["noteCount"], "cards": len(readback["cards"]), "pairs": [{"front": front, "back": back} for front, back in sorted(note_pairs)]},
        "qualificationReceipt": evidence(qualification_path),
        "sourceReadable": {"status": "REJECTED-TEMPLATE-PLACEHOLDER", "evidence": evidence(qualification_readable_path), "reason": "literal $deck placeholder"},
    }


def verify_desktop_credential(config: dict[str, Any]) -> dict[str, Any]:
    receipt_path = checked_ref(config["receipt"], "desktopCredential.receipt")
    automation_log_path = checked_ref(config["automationLog"], "desktopCredential.automationLog")
    ctrl_c_log_path = checked_ref(config["ctrlCLog"], "desktopCredential.ctrlCLog")
    receipt = load_json(receipt_path)
    automated = receipt["automatedWindowsUiCanary"]
    require(receipt["schemaVersion"] == "desktop-windows-canary.v1", "Desktop credential receipt schema mismatch")
    assertions = int(config["assertions"])
    require(automated["status"] == "PASS" and automated["assertions"] == assertions == 131, "Desktop credential Windows UI assertions mismatch")
    require(automated["logSha256"].upper() == base.sha256_file(automation_log_path), "Desktop credential automation log binding mismatch")
    ctrl_c = receipt["realWindowsCanaries"]["focusedTextBoxCtrlC"]
    require(ctrl_c["status"] == "PASS" and ctrl_c["clipboardRestored"] is True and ctrl_c["logSha256"].upper() == base.sha256_file(ctrl_c_log_path), "Desktop credential Ctrl+C boundary mismatch")
    require(receipt["desktopToRealRemNoteE2E"] == {"status": "NOT_RUN", "remNoteInvoked": False, "minerUInvoked": False}, "Desktop credential Canary external boundary mismatch")
    log = base.io_path(automation_log_path).read_text(encoding="utf-8-sig")
    required_assertions = [
        "PASS DPAPI CurrentUser storage decrypts the exact credentials for the same Windows user",
        "PASS credential primary and backup contain only DPAPI envelopes and zero plaintext properties or values",
        "PASS local credentials map only to worker-owned environment variables at process start",
        "PASS credentials never enter the immutable task package, manifest, or payload files",
        "PASS corrupt credential primary falls back to the verified DPAPI-protected .bak copy",
        "PASS legacy plaintext credentials migrate once and read back exactly through DPAPI CurrentUser",
        "PASS successful migration replaces plaintext primary/backup and writes a secret-free migration marker",
        "PASS migration failure fails closed, injects no credential, and preserves the unconfirmed legacy source",
        "PASS post-migration disk secret scan reports zero plaintext across primary, backup, and marker",
        "PASS complete Windows UI Canary evidence tree contains zero persisted credential plaintext",
    ]
    missing = [item for item in required_assertions if item not in log]
    require(not missing, f"Desktop credential DPAPI assertions missing: {missing}")
    local_scan = settings_quality.scan_paths_for_secrets([base.io_path(receipt_path.parent)])
    require(local_scan["findingCount"] == 0, "Desktop credential Canary contains plaintext secret evidence")
    return {
        "status": "PASS-DPAPI-CURRENTUSER-SCOPED",
        "receipt": evidence(receipt_path),
        "automationLog": evidence(automation_log_path),
        "ctrlCLog": evidence(ctrl_c_log_path),
        "assertions": assertions,
        "dpapiEnvelope": True,
        "backupFallback": True,
        "legacyMigration": True,
        "migrationFailureFailClosed": True,
        "taskPackageSecretExclusion": True,
        "localSecretScan": local_scan,
        "candidateBound": True,
    }


def _load_json_log(path: Path, label: str) -> dict[str, Any]:
    try:
        return json.loads(base.io_path(path).read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        raise GateFailure(f"{label}: invalid JSON log: {exc}") from exc


def _verify_build_receipt(candidate_root: Path, receipt: dict[str, Any], config: dict[str, Any]) -> dict[str, Any]:
    records: list[dict[str, Any]] = []
    rows: list[str] = []
    total = 0
    for item in sorted(receipt["files"], key=lambda value: value["path"]):
        path = (candidate_root / item["path"]).resolve()
        require(path.is_relative_to(candidate_root) and base.io_path(path).is_file(), f"Desktop payload missing/escaping: {item['path']}")
        actual = evidence(path)
        require(actual["bytes"] == int(item["bytes"]) and actual["sha256"] == str(item["sha256"]).upper(), f"Desktop payload drift: {item['path']}")
        records.append({"path": item["path"], "bytes": actual["bytes"], "sha256": actual["sha256"]})
        rows.append(f"{item['path']}\n{actual['sha256'].lower()}\n{actual['bytes']}\n")
        total += actual["bytes"]
    tree_hash = hashlib.sha256("".join(rows).encode("utf-8")).hexdigest().upper()
    expected = {
        "fileCount": int(config["desktopFiles"]),
        "totalBytes": int(config["desktopBytes"]),
        "treeSha256": config["desktopTreeSha256"].upper(),
    }
    actual_summary = {"fileCount": len(records), "totalBytes": total, "treeSha256": tree_hash}
    require(actual_summary == expected, "Desktop build receipt inventory mismatch")
    require(receipt["fileCount"] == expected["fileCount"] and receipt["totalBytes"] == expected["totalBytes"] and receipt["treeSha256"].upper() == expected["treeSha256"], "Desktop build receipt summary mismatch")
    require(receipt["productName"] == "零碎知识剪藏" and receipt["assemblyName"] == "KnowledgeClipper" and receipt["entrypoint"] == "KnowledgeClipper.exe" and receipt["selfContained"] is True, "Desktop product/runtime identity mismatch")
    require(receipt["executableSha256"].upper() == config["exeSha256"].upper() and receipt["applicationDllSha256"].upper() == config["dllSha256"].upper(), "Desktop entry hash mismatch")
    return actual_summary


def verify_desktop_candidate(config: dict[str, Any], authority: dict[str, Any], full_inventory: bool) -> dict[str, Any]:
    deprecated_gate_path = checked_ref(config["deprecatedManifestGate"], "desktop.deprecatedManifestGate")
    deprecated_gate = load_json(deprecated_gate_path)
    require(config["integratedReady"] is True, "new Desktop candidate was not marked ready for quality admission")
    require(config["deprecatedCandidateId"] == "kc-i-settings-v1211-20260828T095200Z", "deprecated Desktop candidate identity mismatch")
    require(deprecated_gate["status"] == "PASS" and deprecated_gate["authorityTreeSha256"].upper() == authority["release"]["treeSha256"], "deprecated Desktop payload authority binding drifted")

    candidate_root = base.resolve_path(config["candidateRoot"])
    require(candidate_root.is_dir() and candidate_root.name == "win-x64", "Desktop candidate root missing")
    require(config["candidateId"] == candidate_root.parent.name == "kc-i-dpapi-v1211-20260828T183000Z", "Desktop candidate identity mismatch")
    manifest_path = checked_ref(config["manifest"], "desktop.manifest")
    build_receipt_path = checked_ref(config["buildReceipt"], "desktop.buildReceipt")
    integration_config_path = checked_ref(config["integrationConfig"], "desktop.integrationConfig")
    owner_receipt_path = checked_ref(config["ownerReceipt"], "desktop.ownerReceipt")
    manifest_gate_path = checked_ref(config["manifestGate"], "desktop.manifestGate")
    drift_path = checked_ref(config["payloadDrift"], "desktop.payloadDrift")
    startup_path = checked_ref(config["startupLog"], "desktop.startupLog")
    single_path = checked_ref(config["singleInstanceLog"], "desktop.singleInstanceLog")
    integration_path = checked_ref(config["integrationLog"], "desktop.integrationLog")
    registry_path = checked_ref(config["registryLog"], "desktop.registryLog")
    deprecated_invalidation_path = checked_ref(config["deprecatedInvalidation"], "desktop.deprecatedInvalidation")
    require(manifest_path.parent == candidate_root and build_receipt_path.parent == candidate_root and integration_config_path.parent == candidate_root, "candidate control file is outside the candidate root")

    manifest = load_json(manifest_path)
    require(manifest["schemaVersion"] == "knowledge-clipper-integrated-candidate-manifest.v1" and manifest["candidateId"] == config["candidateId"] and manifest["entrypoint"] == "KnowledgeClipper.exe", "candidate manifest identity mismatch")
    require(manifest["excludedControlFiles"] == ["candidate-file-manifest.v1.json"], "candidate manifest exclusion policy mismatch")
    require(manifest["authority"]["registryVersion"] == "1.2.11" and manifest["authority"]["checkpointSha256"].upper() == authority["checkpoint"]["sha256"], "candidate manifest authority checkpoint mismatch")
    require(manifest["authority"]["registrySha256"].upper() == authority["registry"]["sha256"] and manifest["authority"]["releaseTreeSha256"].upper() == authority["release"]["treeSha256"], "candidate manifest authority tree/registry mismatch")

    payload_expected = {
        "fileCount": int(config["payloadFiles"]),
        "totalBytes": int(config["payloadBytes"]),
        "treeSha256": config["payloadTreeSha256"].upper(),
        "files": manifest["payload"]["files"],
    }
    require(manifest["payload"]["fileCount"] == payload_expected["fileCount"] and manifest["payload"]["totalBytes"] == payload_expected["totalBytes"] and manifest["payload"]["treeSha256"].upper() == payload_expected["treeSha256"], "candidate manifest payload summary mismatch")
    if full_inventory:
        try:
            payload_inventory = common.inventory(candidate_root, {"candidate-file-manifest.v1.json"})
            common.assert_inventory(payload_inventory, payload_expected, "candidate.payload")
        except common.GateFailure as exc:
            raise GateFailure(str(exc)) from exc
        candidate_inventory = common.inventory(candidate_root)
        require({key: candidate_inventory[key] for key in ("fileCount", "totalBytes", "treeSha256")} == {
            "fileCount": int(config["candidateFiles"]),
            "totalBytes": int(config["candidateBytes"]),
            "treeSha256": config["candidateTreeSha256"].upper(),
        }, "candidate complete inventory mismatch")
        authority_inventory = common.inventory(candidate_root / "authority" / "win-x64")
        require({key: authority_inventory[key] for key in ("fileCount", "totalBytes", "treeSha256")} == {key: authority["release"][key] for key in ("fileCount", "totalBytes", "treeSha256")}, "candidate authority subtree is not byte-exact v1.2.11")
    else:
        payload_inventory = {key: payload_expected[key] for key in ("fileCount", "totalBytes", "treeSha256")}
        candidate_inventory = {"fileCount": int(config["candidateFiles"]), "totalBytes": int(config["candidateBytes"]), "treeSha256": config["candidateTreeSha256"].upper()}
        authority_inventory = authority["release"]

    build_receipt = load_json(build_receipt_path)
    desktop_inventory = _verify_build_receipt(candidate_root, build_receipt, config)
    require(manifest["desktop"]["buildReceiptSha256"].upper() == base.sha256_file(build_receipt_path) and manifest["desktop"]["treeSha256"].upper() == desktop_inventory["treeSha256"], "candidate manifest/Desktop build binding mismatch")
    integration_config = load_json(integration_config_path)
    require(integration_config["AuthorityCheckpointId"] == manifest["authority"]["checkpointId"] and integration_config["AuthorityTreeSha256"].upper() == authority["release"]["treeSha256"], "candidate integration authority binding mismatch")
    require(integration_config["OrchestratorExecutable"] == "authority/win-x64/orchestrator/KnowledgeClipper.Orchestrator.exe" and integration_config["AutoStart"] is True, "candidate internal Orchestrator binding mismatch")

    manifest_gate = load_json(manifest_gate_path)
    require(manifest_gate["status"] == "PASS" and manifest_gate["manifestSha256"].upper() == base.sha256_file(manifest_path), "manifest gate binding mismatch")
    require(manifest_gate["candidateTreeSha256"].upper() == candidate_inventory["treeSha256"] and manifest_gate["authorityTreeSha256"].upper() == authority["release"]["treeSha256"], "manifest gate tree mismatch")
    drift = load_json(drift_path)
    require(drift["status"] == "PASS-EXPECTED-DRIFT" and drift["currentCandidate"] == config["candidateId"] and drift["baselineCandidate"] == config["deprecatedCandidateId"], "Desktop payload drift classification mismatch")
    require(drift["currentExeSha256"].upper() == config["exeSha256"].upper() == base.sha256_file(candidate_root / "KnowledgeClipper.exe"), "candidate EXE mismatch")
    require(drift["currentDllSha256"].upper() == config["dllSha256"].upper() == base.sha256_file(candidate_root / "KnowledgeClipper.dll"), "candidate DLL mismatch")

    startup = _load_json_log(startup_path, "desktop.startupLog")
    require(startup["result"] == "PASS" and startup["packageTreeVerified"] is True and startup["candidateIntegrationParsed"] is True and startup["candidateOrchestratorPathsVerified"] is True, "candidate safe startup failed")
    require(startup["productionWorkDirectoryCreated"] is False and startup["namedPipeInvoked"] is False and startup["externalConnections"] == 0 and startup["remNoteInvoked"] is False and startup["mineruInvoked"] is False, "candidate startup exceeded safe boundary")
    single = _load_json_log(single_path, "desktop.singleInstanceLog")
    require(single["existingInstanceDetected"] is True and single["userFeedbackMode"] == "activate-existing-window-and-message-box", "single-instance gate was silent or destructive")
    integration = _load_json_log(integration_path, "desktop.integrationLog")
    require(integration["result"] == "PASS" and integration["orchestratorStartedByDesktop"] is True and integration["namedPipeInvoked"] is True and integration["action"] == "task.status.query-recent", "candidate internal Orchestrator/Named Pipe query failed")
    require(integration["externalWorkerInvoked"] is False and integration["physicalSpeakerInvoked"] is False and integration["remNoteInvoked"] is False and integration["ankiInvoked"] is False and integration["mineruInvoked"] is False, "candidate integration exceeded safe boundary")
    registry = _load_json_log(registry_path, "desktop.registryLog")
    require(registry == {"schemaVersion": "1.0.0", "status": "valid", "registryVersion": "1.2.11", "workers": 5}, "candidate registry validation mismatch")

    owner = load_json(owner_receipt_path)
    require(owner["schemaVersion"] == "knowledge-clipper-desktop-integrated-dpapi-module-checkpoint.v1" and owner["status"] == "PASS-INTEGRATED-DPAPI-CURRENTUSER-AUTHORITY-V1.2.11-MODULE-CHECKPOINT", "Desktop owner receipt status mismatch")
    require(owner["candidateId"] == config["candidateId"] and base.resolve_path(owner["candidate"]["root"]) == candidate_root, "Desktop owner receipt candidate mismatch")
    require(owner["candidate"]["manifest"]["sha256"].upper() == base.sha256_file(manifest_path) and owner["candidate"]["treeSha256"].upper() == candidate_inventory["treeSha256"], "Desktop owner receipt manifest/tree mismatch")
    require(owner["candidate"]["fileCount"] == candidate_inventory["fileCount"] and owner["candidate"]["totalBytes"] == candidate_inventory["totalBytes"], "Desktop owner receipt count/bytes mismatch")
    require(owner["credentialProtection"]["storage"] == "Windows DPAPI CurrentUser" and owner["credentialProtection"]["plaintextJsonPrimary"] is False and owner["credentialProtection"]["encryptedBackup"] is True, "Desktop owner DPAPI summary mismatch")
    require(owner["credentialProtection"]["candidateCredentialFiles"] == owner["credentialProtection"]["candidateKnownSecretHits"] == 0 and owner["credentialProtection"]["taskPackageSecretReferences"] == [], "Desktop owner secret exclusion mismatch")
    require(owner["authority"]["embeddedExactly"] is True and owner["authority"]["releaseTreeSha256"].upper() == authority["release"]["treeSha256"], "Desktop owner authority binding mismatch")
    require(all(value == 0 for key, value in owner["safety"].items() if key != "preExistingDesktopProcessTerminated") and owner["safety"]["preExistingDesktopProcessTerminated"] is False, "Desktop owner safety boundary mismatch")
    require(owner["releaseBoundary"]["classification"] == "module-checkpoint" and owner["releaseBoundary"]["userHandTestEntrypoint"] is False and owner["releaseBoundary"]["repeatUiHandTestRequested"] is False, "Desktop owner hand-test boundary mismatch")
    invalidation = load_json(deprecated_invalidation_path)
    require(invalidation["candidateId"] == config["deprecatedCandidateId"] and invalidation["status"] == "invalidated-never-promoted", "old Desktop candidate was not invalidated-never-promoted")

    runtime_secret_scan = settings_quality.scan_runtime_tree_for_secrets(candidate_root)
    require(runtime_secret_scan["findingCount"] == 0, "candidate runtime secret scan found leakage")
    allowed_negative = {
        "authority/win-x64/contracts/core/v2.4/settings-control/fixtures/workflow-settings/invalid/secret-and-unknown-field.json",
        "authority/win-x64/contracts/core/v2.4/settings-control/fixtures/workflow-settings-v2/invalid/raw-secret.json",
    }
    excluded_negative = set(runtime_secret_scan["excludedNegativeFixtures"])
    require(allowed_negative <= excluded_negative and len(excluded_negative) == 17, "candidate secret-scan fixture exclusions drifted")
    require(all(path.startswith("authority/win-x64/contracts/") and "/fixtures/" in path and "/invalid/" in path for path in excluded_negative), "secret scan excluded a non-authority negative fixture")
    credential_named = {
        path.relative_to(candidate_root).as_posix()
        for path in candidate_root.rglob("*")
        if path.is_file() and any(token in path.name.casefold() for token in ("credential", "secret", ".bak", "migration-marker"))
    }
    require(credential_named == allowed_negative, f"candidate contains an unexpected credential/secret store: {sorted(credential_named - allowed_negative)}")
    return {
        "status": "PASS-INTEGRATED-V1.2.11-CANDIDATE-BOUND",
        "candidateId": config["candidateId"],
        "root": base.display_path(candidate_root),
        "manifest": evidence(manifest_path),
        "candidateHash": base.sha256_file(manifest_path),
        "payload": {key: payload_inventory[key] for key in ("fileCount", "totalBytes", "treeSha256")},
        "candidateInventory": {key: candidate_inventory[key] for key in ("fileCount", "totalBytes", "treeSha256")},
        "desktopInventory": desktop_inventory,
        "authoritySubtree": {key: authority_inventory[key] for key in ("fileCount", "totalBytes", "treeSha256")},
        "entrypoint": evidence(candidate_root / "KnowledgeClipper.exe"),
        "applicationDll": evidence(candidate_root / "KnowledgeClipper.dll"),
        "buildReceipt": evidence(build_receipt_path),
        "ownerReceipt": evidence(owner_receipt_path),
        "integrationConfig": evidence(integration_config_path),
        "manifestGate": evidence(manifest_gate_path),
        "payloadDrift": evidence(drift_path),
        "startup": {"status": "PASS-SAFE", "log": evidence(startup_path)},
        "singleInstance": {"status": "PASS-FAIL-NOT-SILENT", "log": evidence(single_path)},
        "internalOrchestrator": {"status": "PASS-QUERY-ONLY", "log": evidence(integration_path)},
        "registry": {"status": "PASS-5-OF-5", "log": evidence(registry_path)},
        "runtimeSecretScan": runtime_secret_scan,
        "userCredentialFilesIncluded": 0,
        "deprecatedCandidate": {
            "candidateId": config["deprecatedCandidateId"],
            "status": "SUPERSEDED-NOT-ELIGIBLE-AS-FINAL",
            "manifestGate": evidence(deprecated_gate_path),
            "invalidation": evidence(deprecated_invalidation_path),
        },
        "candidateIdentityEligible": True,
        "ownerTerminalReceipt": {"status": "PASS-HASH-BOUND", "receipt": evidence(owner_receipt_path)},
    }


def render_index(result: dict[str, Any]) -> str:
    paths = result["paths"]
    lines = [
        "# Phase B v1.2.11 candidate-bound quality index",
        "",
        f"- Overall: `{result['status']}`",
        f"- Authority: v{result['authority']['registryVersion']} / `{result['authority']['release']['treeSha256']}`",
        f"- Desktop: `{result['desktop']['status']}`",
        f"- Desktop credential protection: `{result['desktopCredential']['status']}`; candidate-bound DPAPI and secret exclusion verified.",
        f"- Candidate manifest/candidateHash: `{result['release']['candidateHash']}`",
        "- Quality process external calls: `0`",
        "",
        "## A — text -> Content -> isolated Anki",
        "",
        f"- Status: `{paths['A']['status']}`",
        f"- Input: {paths['A']['input']['text'].replace(chr(10), ' / ')}",
        f"- Card: {paths['A']['card']['front']} -> {paths['A']['card']['back']}",
        f"- Readable evidence: `{paths['A']['readable']['path']}`",
        "",
        "## D — text -> Content -> isolated Anki + synth + physical playback + replay",
        "",
        f"- Status: `{paths['D']['status']}`",
        f"- Input: {paths['D']['input']['text'].replace(chr(10), ' / ')}",
        f"- Card: {paths['D']['card']['front']} -> {paths['D']['card']['back']}",
        f"- Audio: `{paths['D']['audio']['path']}`, {paths['D']['audio']['bytes']} bytes, SHA-256 `{paths['D']['audio']['sha256']}`, absolute path length {paths['D']['audio']['absolutePathLength']}",
        "- Physical playback source evidence: 2 natural completions; replay is a fresh attempt from start, with no export/content stage replay.",
        "",
        "## Pending, not failed",
        "",
        f"- B/RemNote: `{paths['B']['status']}`. Old commit_unknown remains unresolved and automatic retry is forbidden.",
        f"- C/MinerU: `{paths['C']['status']}`. Provider calls: 0; no credential was persisted.",
        "- Desktop integrated candidate is byte-exact and candidate-bound; it is not a user hand-test entrypoint and does not imply production export E2E.",
        "",
        "## Evidence caveat",
        "",
        "- The owner aggregation Markdown contained a literal `$deck` placeholder and is not used as authoritative readback. This index is regenerated from the path receipts, artifacts, and qualification JSON.",
    ]
    return "\n".join(lines) + "\n"


def evaluate(fixture: dict[str, Any], full_inventory: bool = True) -> dict[str, Any]:
    require(fixture["policy"] == {
        "oldAuthorityEvidenceMayQualify": False,
        "pendingPathsAreFailures": False,
        "modulePassImpliesFullE2E": False,
        "retryUnknownExternalState": False,
        "desktopRequiredForCandidateIdentity": True,
    }, "candidate policy was weakened")
    authority = verify_authority(fixture["authority"], full_inventory)
    invalidations = verify_invalidations(fixture["invalidations"])
    core = verify_core(fixture["core"])
    require(authority["checkpoint"]["sha256"] == fixture["authority"]["checkpoint"]["sha256"].upper(), "authority checkpoint drift")
    paths = verify_paths(fixture["paths"], authority)
    desktop_credential = verify_desktop_credential(fixture["desktopCredential"])
    desktop = verify_desktop_candidate(fixture["desktop"], authority, full_inventory)
    superseded_path = checked_ref(fixture["supersededEvidence"]["qualityReceipt"], "supersededEvidence.qualityReceipt")
    require(fixture["supersededEvidence"]["eligible"] is False, "old 152727Z evidence was made eligible")
    scan_roots = [
        checked_ref(fixture["authority"]["checkpoint"], "scan.authority").parent,
        checked_ref(fixture["core"]["checkpoint"], "scan.core").parent,
        checked_ref(fixture["paths"]["A"]["receipt"], "scan.A").parent,
        checked_ref(fixture["paths"]["D"]["receipt"], "scan.D").parent,
        checked_ref(fixture["paths"]["qualificationReceipt"], "scan.qualification").parent,
        checked_ref(fixture["invalidations"]["v129"], "scan.v129").parent,
        checked_ref(fixture["invalidations"]["v1210"], "scan.v1210").parent,
        checked_ref(fixture["desktopCredential"]["receipt"], "scan.desktopCredential").parent,
        checked_ref(fixture["desktop"]["ownerReceipt"], "scan.desktopOwnerReceipt"),
        checked_ref(fixture["desktop"]["manifest"], "scan.desktopManifest"),
        checked_ref(fixture["desktop"]["manifestGate"], "scan.desktopIntegratedEvidence").parent,
    ]
    secret_scan = settings_quality.scan_paths_for_secrets([base.io_path(path) for path in scan_roots])
    require(secret_scan["findingCount"] == 0, "secret scan found leakage")
    baselines = settings_quality.verify_immutable_baselines()
    require(baselines["frozenV1"]["filesChecked"] == 21 and baselines["frozenV1"]["mismatches"] == 0, "frozen v1 baseline drift")
    pending = [
        "B/RemNote remains pending; old commit_unknown is unresolved and must not be automatically retried",
        "C remains pending because no MinerU process/user credential was available; provider calls stayed at 0",
        "Desktop physical external-app selection, real image clipboard, screenshot drag, and mixed-DPI cross-screen drag remain environment/user checks",
        "installer and code signing are not present; this quality result is an unsigned integrated candidate, not a production release GO",
    ]
    return {
        "schemaVersion": "knowledge-clipper-phase-b-v1211-candidate-quality-receipt.v1",
        "generatedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "CONDITIONAL-V1.2.11-CANDIDATE-BOUND-A-D-DPAPI-PASS-B-C-PENDING",
        "scope": "candidate-bound scoped quality admission; evidence-only; no external services invoked",
        "fixtureValidation": validate_fixtures(),
        "authority": authority,
        "invalidations": invalidations,
        "core": core,
        "paths": paths,
        "desktop": desktop,
        "desktopCredential": desktop_credential,
        "supersededEvidence": {"status": "SUPERSEDED-TIMING-DRIFT", "receipt": evidence(superseded_path), "eligible": False},
        "secretScan": secret_scan,
        "immutableBaselines": baselines,
        "pending": pending,
        "qualitySideEffects": {"externalCalls": 0, "workerStarts": 0, "remnoteWrites": 0, "ankiWrites": 0, "mineruCalls": 0, "physicalSpeakerInvocations": 0},
        "release": {
            "sealed": True,
            "classification": "unsigned-integrated-candidate",
            "releaseGo": False,
            "manifest": desktop["manifest"],
            "candidateHash": desktop["candidateHash"],
            "signed": False,
            "installer": False,
            "userHandTestEntrypoint": False,
            "reason": "candidate identity is sealed; B/RemNote and C/MinerU remain conditional and production release boundaries remain open",
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify scoped Phase B v1.2.11 candidate-bound evidence without external calls.")
    parser.add_argument("--fixture", default=str(DEFAULT_FIXTURE))
    parser.add_argument("--emit-root")
    parser.add_argument("--skip-full-inventory", action="store_true")
    args = parser.parse_args()
    fixture_path = base.resolve_path(args.fixture)
    result = evaluate(load_json(fixture_path), full_inventory=not args.skip_full_inventory)
    result["fixture"] = evidence(fixture_path)
    artifacts: dict[str, Any] = {}
    if args.emit_root:
        root = base.resolve_path(args.emit_root)
        require(not root.exists(), f"append-only output root already exists: {root}")
        root.mkdir(parents=True)
        receipt_path = root / "quality-receipt.json"
        index_path = root / "input-output-index.md"
        receipt_path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        index_path.write_text(render_index(result), encoding="utf-8")
        artifacts = {"root": base.display_path(root), "receipt": evidence(receipt_path), "readableIndex": evidence(index_path)}
    print(json.dumps({"result": result, "artifacts": artifacts}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
