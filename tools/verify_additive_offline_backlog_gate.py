from __future__ import annotations

import argparse
import hashlib
import json
import re
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator, FormatChecker

import verify_settings_control_plane as settings_quality


PROJECT_ROOT = Path(__file__).resolve().parents[1]
FIXTURE_ROOT = PROJECT_ROOT / "tests" / "Quality.Gates" / "fixtures" / "additive-offline-backlog-v1"
FIXTURE_SCHEMA = FIXTURE_ROOT / "additive-offline-backlog-gate.schema.json"
DEFAULT_FIXTURE = FIXTURE_ROOT / "valid" / "offline-backlog.json"


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


def resolve_source(relative: str, label: str) -> Path:
    path = (PROJECT_ROOT / relative).resolve()
    require(path.is_relative_to(PROJECT_ROOT.resolve()) and path.is_file(), f"{label}: missing/out-of-project source")
    return path


def evidence(path: Path) -> dict[str, Any]:
    return {
        "path": path.resolve().relative_to(PROJECT_ROOT.resolve()).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha256(path),
    }


def validate_fixture_set() -> dict[str, Any]:
    schema = load_json(FIXTURE_SCHEMA)
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema, format_checker=FormatChecker())
    valid = sorted((FIXTURE_ROOT / "valid").glob("*.json"))
    invalid = sorted((FIXTURE_ROOT / "invalid").glob("*.json"))
    require(valid and invalid, "offline backlog fixture set incomplete")
    for path in valid:
        errors = list(validator.iter_errors(load_json(path)))
        require(not errors, f"valid fixture rejected: {path.name}: {errors[0].message if errors else ''}")
    for path in invalid:
        require(bool(list(validator.iter_errors(load_json(path)))), f"invalid fixture accepted: {path.name}")
    return {"status": "PASS", "schema": evidence(FIXTURE_SCHEMA), "validAccepted": len(valid), "invalidRejected": len(invalid)}


def verify_baseline(config: dict[str, Any]) -> dict[str, Any]:
    path = resolve_ref(config["qualityReceipt"], "baseline.qualityReceipt")
    receipt = load_json(path)
    require(receipt["status"] == "CONDITIONAL-V1.2.11-CANDIDATE-BOUND-A-D-DPAPI-PASS-B-C-PENDING", "baseline status drifted")
    require(receipt["release"]["candidateHash"] == config["candidateHash"] and receipt["release"]["releaseGo"] is False, "baseline candidate identity/status drifted")
    require(receipt["paths"]["B"]["status"].startswith("PENDING-") and receipt["paths"]["C"]["status"].startswith("PENDING-"), "baseline B/C pending semantics drifted")
    require(receipt["secretScan"]["findingCount"] == 0 and receipt["immutableBaselines"]["frozenV1"]["mismatches"] == 0, "baseline secret/frozen-v1 gate drifted")
    return {"status": "PASS-READ-ONLY-BASELINE", "receipt": evidence(path), "candidateHash": config["candidateHash"], "releaseGo": False}


def verify_requirement_audit(config: dict[str, Any]) -> dict[str, Any]:
    path = resolve_ref(config, "requirementAudit")
    audit = path.read_text(encoding="utf-8-sig")
    required_boundaries = {
        "originalMode": "INCOMPLETE, contradicted by current Core",
        "corePartialContinuation": "PASS module only",
        "retentionSafety": "INCOMPLETE and unsafe",
        "remnoteB": "real path unresolved",
        "mineruC": "Real MinerU C path pending process-only credential injection",
        "finalUserEntrypoint": "NOT YET",
    }
    require("Completion claim: **not achieved**" in audit, "requirement audit no longer preserves the incomplete overall claim")
    for label, marker in required_boundaries.items():
        require(marker in audit, f"requirement audit lost the {label} incomplete boundary")
    return {
        "status": "PASS-READ-ONLY-AUTHORITATIVE-INCOMPLETE-BOUNDARIES",
        "receipt": evidence(path),
        "mustRemainIncomplete": sorted(required_boundaries),
        "narrowTestsMayPromote": False,
    }


def verify_original_mode(config: dict[str, Any]) -> dict[str, Any]:
    source_path = resolve_source(config["currentCoreSource"], "originalMode.currentCoreSource")
    source = source_path.read_text(encoding="utf-8-sig")
    current_blocker = 'throw new OrchestratorDispatchException("resolver.original_media_unsupported"'
    require(config["input"] == {"hasImages": True, "imageHandling": "original"}, "original-mode input fixture drifted")
    require(config["resolver"]["status"] == "accepted", "desired original-mode trace rejects at resolver entry")
    require(config["imageOriginalPassthroughWorkerStarts"] == 1 and config["ocrProviderCalls"] == config["mineruProviderCalls"] == 0, "original mode invoked OCR/MinerU or omitted original passthrough")
    terminal = config["terminal"]
    require(terminal["status"] in {"conditional", "conditional_unsupported", "unsupported"} and terminal["succeeded"] is False, "unsupported original-media terminal was reported succeeded")
    require(terminal["code"] == "media.downstream_unsupported", "original-media terminal is not precisely locatable")
    implementation_markers = (
        "needsOriginalPassthrough",
        'requiredOutput = needsOriginalPassthrough ? "image.original"',
        'new PipelineTerminalBoundary(\n                    "unsupported", "media.downstream_unsupported"',
        'needsOriginalPassthrough ? Empty() : ImageOcrConfig',
    )
    implemented = current_blocker not in source and all(marker in source for marker in implementation_markers)
    return {
        "offlineFixture": "PASS-DESIRED-ROUTE-SEMANTICS",
        "currentOwnerState": "IMPLEMENTED-PENDING-OWNER-RECEIPT" if implemented else "NO-GO-OWNER-SEMANTICS-NOT-PROVEN",
        "ownerSource": evidence(source_path),
        "imageOriginalPassthroughWorkerStarts": 1,
        "ocrProviderCalls": 0,
        "mineruProviderCalls": 0,
        "terminal": terminal,
        "candidateBound": False,
    }


def verify_image_partial_retry(config: dict[str, Any]) -> dict[str, Any]:
    first = config["firstAttempt"]
    retry = config["retryAttempt"]
    require(first["status"] == "partial" and first["partialBlocksDownstream"] is True and first["downstreamStarts"] == 0, "partial image result did not block the whole card downstream")
    completed = {item["itemId"]: item for item in first["items"] if item["status"] == "completed"}
    failed = {item["itemId"]: item for item in first["items"] if item["status"] == "failed"}
    require(set(first["providerItemIds"]) == set(completed) | set(failed) and completed and failed, "first partial attempt item set invalid")
    require(retry["attemptId"] != first["attemptId"] and retry["priorAttemptId"] == first["attemptId"], "failed-items retry did not create a new ancestry-bound attempt")
    require(set(retry["providerItemIds"]) == set(failed), "retry provider call was not failed-items-only")
    reused = {item["itemId"]: item for item in retry["reusedItems"]}
    require(set(reused) == set(completed), "retry did not reuse every and only successful item")
    for item_id, prior in completed.items():
        require(reused[item_id]["sha256"] == prior["sha256"] and reused[item_id]["reusedFromAttemptId"] == first["attemptId"], f"successful item hash/ancestry drifted: {item_id}")
    require({item["itemId"] for item in retry["newItems"]} == set(failed), "retry emitted a non-failed item as new output")
    unknown = config["unknownExternalState"]
    require(unknown == {"status": "unknown_external_state", "retryable": False, "retryAttempts": 0}, "unknown_external_state was made retryable")
    return {
        "status": "PASS-OFFLINE-FAILED-ITEMS-ONLY",
        "firstAttempt": first["attemptId"],
        "retryAttempt": retry["attemptId"],
        "reusedItemHashes": {key: value["sha256"] for key, value in reused.items()},
        "retriedItemIds": retry["providerItemIds"],
        "partialBlocksDownstream": True,
        "unknownExternalStateAutoRetries": 0,
        "candidateBound": False,
    }


def verify_image_settings(config: dict[str, Any]) -> dict[str, Any]:
    receipt_path = resolve_ref(config["admissionReceipt"], "imageSettings.admissionReceipt")
    receipt = load_json(receipt_path)
    expected = config["expected"]
    require(receipt["status"] == "ready-for-core-admission", "Image/OCR admission receipt is not module-ready")
    require(receipt["authorityPromotion"] == "NO-GO-until-Core-admission-and-new-authority-checkpoint", "Image/OCR admission boundary was weakened")
    release = receipt["release"]
    require(release["workerId"] == "image.ocr.mineru" and release["workerVersion"] == expected["workerVersion"] and release["companionVersion"] == expected["companionVersion"], "Image/OCR candidate identity drifted")
    candidate_root = (PROJECT_ROOT / expected["candidateRoot"]).resolve()
    require(candidate_root.is_relative_to(PROJECT_ROOT.resolve()) and candidate_root.is_dir(), "Image/OCR candidate root missing/out-of-project")
    require((release["fileCount"], release["totalBytes"], str(release["treeSha256"]).upper()) == (expected["files"], expected["bytes"], expected["treeSha256"]), "Image/OCR admission inventory drifted")

    embedded_refs = [release["buildReceipt"], release["workerExecutable"], release["settingsExecutable"], release["descriptor"], release["coreContinuationEvidence"]]
    resolved: dict[str, Path] = {}
    for item in embedded_refs:
        path = (PROJECT_ROOT / item["path"]).resolve()
        require(path.is_relative_to(PROJECT_ROOT.resolve()) and path.is_file(), f"Image/OCR evidence missing/out-of-project: {item['path']}")
        require(path.stat().st_size == int(item["bytes"]) and sha256(path) == str(item["sha256"]).upper(), f"Image/OCR evidence hash/bytes drifted: {item['path']}")
        resolved[Path(item["path"]).name] = path
    require(sha256(resolved["KnowledgeClipper.ImageOcr.Worker.exe"]) == expected["workerExeSha256"], "Image/OCR Worker EXE expected hash drifted")
    require(sha256(resolved["KnowledgeClipper.ImageOcr.Settings.exe"]) == expected["settingsExeSha256"], "Image/OCR Settings EXE expected hash drifted")
    require(sha256(resolved["image-ocr-capability-config-descriptor.v4.json"]) == expected["descriptorSha256"], "Image/OCR descriptor expected hash drifted")

    build = load_json(resolved["build-receipt.json"])
    require(build["worker"]["version"] == expected["workerVersion"] and build["companion"]["version"] == expected["companionVersion"], "Image/OCR build receipt identity drifted")
    require((build["fileCount"], build["totalBytes"], str(build["treeSha256"]).upper()) == (expected["files"], expected["bytes"], expected["treeSha256"]), "Image/OCR build receipt inventory drifted")
    tree_rows: list[str] = []
    for item in build["files"]:
        path = (candidate_root / item["path"]).resolve()
        require(path.is_relative_to(candidate_root) and path.is_file(), f"Image/OCR candidate file missing/out-of-root: {item['path']}")
        require(path.stat().st_size == int(item["bytes"]) and sha256(path) == str(item["sha256"]).upper(), f"Image/OCR candidate file hash/bytes drifted: {item['path']}")
        tree_rows.append(f"{item['path']}\0{str(item['sha256']).lower()}\0{int(item['bytes'])}\n")
    tree_digest = hashlib.sha256("".join(tree_rows).encode("utf-8")).hexdigest().upper()
    require(tree_digest == expected["treeSha256"], "Image/OCR candidate tree recomputation drifted")

    descriptor = load_json(resolved["image-ocr-capability-config-descriptor.v4.json"])
    core_evidence = load_json(resolved["core-continuation-contract-evidence.v3.json"])
    require(descriptor["schemaVersion"] == "image-ocr-capability-config-descriptor.v4" and descriptor["readOnlyFacts"]["workerVersion"] == expected["workerVersion"], "Image/OCR descriptor identity drifted")
    provider_capabilities = {
        item["snapshotBinding"]["jsonPointer"].split("/")[-1]
        for item in descriptor["capabilities"]
        if item.get("snapshotBinding") and str(item["snapshotBinding"].get("jsonPointer", "")).startswith("/configSnapshot/providerSettings/")
    }
    require(provider_capabilities == set(expected["providerSettingsKeys"]), "Image/OCR providerSettings descriptor coverage drifted")
    secret_capability = next(item for item in descriptor["capabilities"] if item["capabilityId"] == "image.ocr.api-token-reference")
    require(secret_capability["snapshotBinding"]["excludedFromSnapshot"] is True and secret_capability["sensitivity"] == "secret-value-prohibited", "Image/OCR credential is no longer excluded from task snapshots")
    actions = {item["actionId"]: item for item in descriptor["actions"]}
    for action_id in ("image.ocr.status.offline-readiness", "image.ocr.test.validate-only", "image.ocr.test.offline-fake-provider"):
        action = actions[action_id]
        require(action["networkAccess"] == "prohibited" and action["sideEffects"]["externalUpload"] is False, f"Image/OCR offline action gained network/upload side effects: {action_id}")
    require(actions["image.ocr.test.live-connectivity"]["availability"]["state"] == "conditional" and actions["image.ocr.test.live-connectivity"]["executionPolicy"] == "explicit-user-action-only", "Image/OCR live action is no longer explicit conditional")
    require(descriptor["statusSemantics"]["coreOriginalFullPipeline"] == "unsupported", "Image/OCR original route was fabricated as integrated")
    require(descriptor["statusSemantics"]["corePartialFailedOnlyAutomaticContinuation"] == "conditional", "Image/OCR partial continuation was fabricated as integrated")
    require(descriptor["readOnlyFacts"]["retention"]["executorState"] == "unsupported" and descriptor["readOnlyFacts"]["retention"]["automaticDeletionClaim"] is False, "Image/OCR retention executor was fabricated as active")
    require(core_evidence["originalPassThrough"]["coreTerminal"]["state"] == "unsupported", "Image/OCR Core original terminal boundary drifted")
    require(core_evidence["partialFailedItemsOnlyContinuation"]["coreContinuation"]["state"] == "conditional", "Image/OCR Core partial continuation boundary drifted")
    require(core_evidence["retention"]["executorState"] == "unsupported", "Image/OCR retention boundary drifted")

    settings_schema_path = candidate_root / "schemas" / "image-ocr-settings.v1.schema.json"
    request_schema_path = candidate_root / "schemas" / "image-request.v1.schema.json"
    for schema_path in candidate_root.joinpath("schemas").glob("*.schema.json"):
        Draft202012Validator.check_schema(load_json(schema_path))
    settings_schema = load_json(settings_schema_path)
    request_schema = load_json(request_schema_path)
    require(set(settings_schema["properties"]["providerSettings"]["properties"]) == set(expected["providerSettingsKeys"]), "Image/OCR providerSettings settings schema coverage drifted")
    require(set(request_schema["properties"]["providerSettings"]["properties"]) == set(expected["providerSettingsKeys"]), "Image/OCR formal WorkerRequest adapter schema coverage drifted")

    offline_paths: list[Path] = []
    for item in [receipt["offlineCanary"]["input"], *receipt["offlineCanary"]["outputs"]]:
        path = (PROJECT_ROOT / item["path"]).resolve()
        require(path.is_relative_to(PROJECT_ROOT.resolve()) and path.is_file(), f"Image/OCR offline evidence missing/out-of-project: {item['path']}")
        require(path.stat().st_size == int(item["bytes"]) and sha256(path) == str(item["sha256"]).upper(), f"Image/OCR offline evidence hash/bytes drifted: {item['path']}")
        offline_paths.append(path)
    fake_output = load_json(next(path for path in offline_paths if path.name == "offline-fake-provider.json"))
    require(fake_output["status"] == "passed" and fake_output["formalWorkerRequest"]["absoluteExistingRequestUsed"] is True, "Image/OCR formal WorkerRequest was not consumed exactly")
    require(fake_output["fakeProvider"]["exactMatch"] is True and fake_output["fakeProvider"]["observedProviderSettings"] == fake_output["formalWorkerRequest"]["configSnapshotProviderSettings"], "Image/OCR providerSettings changed before fake provider consumption")
    require(fake_output["safety"] == {"credentialResolved": False, "externalUpload": False, "mineruServiceCalled": False, "networkAccess": "prohibited", "secretValuePersisted": False}, "Image/OCR fake-provider evidence performed or persisted a forbidden side effect")
    require(receipt["tests"]["passed"] == expected["unitTests"] and receipt["tests"]["failed"] == 0, "Image/OCR unit evidence drifted")
    require(receipt["safety"] == {"externalProductionApisInvoked": 0, "mineruServiceInvocations": 0, "externalUploads": 0, "credentialValuesResolved": 0, "credentialValuesPersisted": 0, "secretValueProhibited": True}, "Image/OCR admission safety evidence drifted")
    image_secret_scan = settings_quality.scan_paths_for_secrets([candidate_root, receipt_path, *offline_paths])
    require(image_secret_scan["findingCount"] == 0, "Image/OCR candidate/evidence contains plaintext secret material")
    return {
        "moduleStatus": "PASS-IMAGE.OCR.MINERU-1.2.2-OFFLINE-CORE-ADMISSION-EVIDENCE",
        "integrationStatus": "PENDING-NEW-AUTHORITY-CORE-CONTINUATION-DESKTOP-CANDIDATE",
        "release": {"files": expected["files"], "bytes": expected["bytes"], "treeSha256": tree_digest, "workerExe": evidence(resolved["KnowledgeClipper.ImageOcr.Worker.exe"]), "settingsExe": evidence(resolved["KnowledgeClipper.ImageOcr.Settings.exe"]), "descriptor": evidence(resolved["image-ocr-capability-config-descriptor.v4.json"])},
        "providerSettings": {"keys": expected["providerSettingsKeys"], "formalWorkerRequestExactConsumption": "PASS"},
        "offlineActions": "PASS-NETWORK-UPLOAD-AND-CREDENTIAL-RESOLUTION-ZERO",
        "boundaries": {"original": "unsupported", "partialContinuation": "conditional", "retention": "unsupported"},
        "secretScan": image_secret_scan,
        "receipt": evidence(receipt_path),
        "candidateBound": False,
    }


def verify_settings_honesty(config: dict[str, Any]) -> dict[str, Any]:
    require(config["coveragePolicy"] == "all-editable-descriptor-items", "settings coverage policy weakened")
    require(config["plaintextSensitiveValueCount"] == 0, "settings fixture contains plaintext sensitive data")
    editable_ids: set[str] = set()
    for item in config["editable"]:
        setting_id = item["settingId"]
        require(setting_id not in editable_ids, f"duplicate editable setting: {setting_id}")
        editable_ids.add(setting_id)
        require(item["adapterAvailable"] is True and item["editable"] is True and item["descriptorRevision"], f"editable setting lacks adapter/revision: {setting_id}")
        chain = item["valueChain"]
        require(set(chain) == {"descriptorDefault", "settingsRevision", "taskSnapshot", "resolvedPlan", "configSnapshot", "workerRequest"}, f"incomplete settings evidence chain: {setting_id}")
        values = list(chain.values())
        require(all(value == values[0] for value in values[1:]), f"settings value drift across chain: {setting_id}")
    conditional_ids: set[str] = set()
    for item in config["conditional"]:
        conditional_ids.add(item["settingId"])
        require(item["adapterAvailable"] is False and item["editable"] is False and item["status"] == "conditional", f"adapter-less field is a fake editable setting: {item['settingId']}")
    require(editable_ids.isdisjoint(conditional_ids), "setting is both editable and conditional")
    return {
        "offlineFixture": "PASS-EXHAUSTIVE-CHAIN-ALGORITHM",
        "editableSamples": sorted(editable_ids),
        "conditionalSamples": sorted(conditional_ids),
        "requiredOwnerCoverage": "descriptor item set must equal evidence-chain item set",
        "plaintextSensitiveValueCount": 0,
        "ownerCoverage": "PENDING-CORE-AND-MODULE-RECEIPTS",
        "candidateBound": False,
    }


def verify_speech_settings(config: dict[str, Any]) -> dict[str, Any]:
    paths = {
        name: resolve_ref(config[name], f"speechSettings.{name}")
        for name in (
            "offlineReceipt",
            "descriptor",
            "languageOverrideFixture",
            "probeInput",
            "probeOutput",
            "workerRequest",
            "formalReleaseReceipt",
            "formalValidationReceipt",
            "formalManifest",
            "formalExe",
            "liveDeepPathReceipt",
        )
    }
    offline = load_json(paths["offlineReceipt"])
    descriptor = load_json(paths["descriptor"])
    binding_fixture = load_json(paths["languageOverrideFixture"])
    probe_input = load_json(paths["probeInput"])
    probe_output = load_json(paths["probeOutput"])
    worker_request = load_json(paths["workerRequest"])
    formal_receipt = load_json(paths["formalReleaseReceipt"])
    formal_validation = load_json(paths["formalValidationReceipt"])
    formal_manifest = load_json(paths["formalManifest"])
    live_receipt = load_json(paths["liveDeepPathReceipt"])
    expected = config["expected"]

    require(offline["status"] == "passed" and offline["workerId"] == "speech.edge", "speech companion module receipt is not passed")
    require(offline["companionVersion"] == expected["companionVersion"] and offline["workerVersion"] == expected["workerVersion"], "speech companion version drifted")
    require(offline["physicalSpeakerCalls"] == offline["networkTtsCalls"] == 0, "speech offline companion performed a side effect")
    artifact_by_hash = {str(item["sha256"]).upper(): item for item in offline["artifacts"]}
    for name in ("descriptor", "languageOverrideFixture", "probeInput", "probeOutput", "workerRequest"):
        digest = sha256(paths[name])
        require(digest in artifact_by_hash, f"speech receipt does not bind {name}")
        require(artifact_by_hash[digest]["bytes"] == paths[name].stat().st_size, f"speech receipt byte count drifted: {name}")

    require(descriptor["scope"] == {
        "value": expected["scope"],
        "persistenceOwner": "desktop-core",
        "effectiveAt": expected["effectiveAt"],
        "currentTaskEffect": expected["currentTaskEffect"],
    }, "speech descriptor scope/effect drifted")
    settings = {item["id"]: item for item in descriptor["settings"]}
    expected_targets = {
        "languageStrategy": "synthesisRequest.payload.language.strategy",
        "languageOverride": "synthesisRequest.payload.language.override",
        "voicesByLanguage": "synthesisRequest.payload.voiceSnapshot.voicesByLanguage",
        "ratePercent": "synthesisRequest.payload.voiceSnapshot.ratePercent",
        "volumePercent": "synthesisRequest.payload.voiceSnapshot.volumePercent",
        "pitchHz": "synthesisRequest.payload.voiceSnapshot.pitchHz",
    }
    for setting_id, target in expected_targets.items():
        require(settings.get(setting_id, {}).get("editable") is True and settings[setting_id]["target"] == target, f"speech editable descriptor target drifted: {setting_id}")

    project_draft = probe_input["adapterInput"]["settings"]
    require(probe_input["scope"] == expected["scope"] and probe_input["effectiveAt"] == expected["effectiveAt"], "speech project draft scope/effect drifted")
    require(project_draft["languageStrategy"] == expected["languageStrategy"] and project_draft["languageOverride"] == expected["languageOverrideInput"], "speech project draft language override drifted")
    request = probe_output["synthesisRequest"]
    require(probe_output["scope"] == expected["scope"] and probe_output["effectiveAt"] == expected["effectiveAt"], "speech task projection scope/effect drifted")
    require(probe_output["currentTaskEffect"] == expected["currentTaskEffect"], "speech existing-task immutability drifted")
    require(request["workerId"] == "speech.edge" and request["operation"] == "speech.synthesize", "speech WorkerRequest identity drifted")
    require(request["requestId"] == request["payload"]["requestId"] and request["operationId"] == request["payload"]["operationId"], "speech projected WorkerRequest identity is internally inconsistent")
    require(worker_request["requestId"] == worker_request["payload"]["requestId"] and worker_request["operationId"] == worker_request["payload"]["operationId"], "speech immutable WorkerRequest fixture identity is internally inconsistent")
    require(request["payload"]["language"] == {"strategy": expected["languageStrategy"], "override": expected["languageOverrideCanonical"]}, "speech language strategy/override did not reach synthesis payload")
    voice_snapshot = request["payload"]["voiceSnapshot"]
    require(voice_snapshot["voicesByLanguage"] == project_draft["voicesByLanguage"], "speech voice catalog changed between project draft and WorkerRequest")
    require(voice_snapshot["voicesByLanguage"][expected["languageOverrideCanonical"]] == expected["voiceId"], "speech override voice binding drifted")
    for field in ("ratePercent", "volumePercent", "pitchHz"):
        require(voice_snapshot[field] == project_draft[field], f"speech voice snapshot drifted: {field}")
    edge_path = request["configSnapshot"].get("edgeTtsPath")
    require(edge_path and Path(edge_path).is_absolute() and edge_path == probe_input["adapterInput"]["deployment"]["edgeTtsPath"], "speech WorkerRequest configSnapshot path is missing or not frozen from deployment")
    require(worker_request["payload"]["language"] == request["payload"]["language"], "speech immutable WorkerRequest language projection drifted")
    require(worker_request["payload"]["voiceSnapshot"] == voice_snapshot, "speech immutable WorkerRequest voice snapshot drifted")
    require(Path(worker_request["configSnapshot"].get("edgeTtsPath", "")).is_absolute(), "speech immutable WorkerRequest lacks an absolute configSnapshot path")

    require(binding_fixture["scope"] == expected["scope"] and binding_fixture["effectiveAt"] == expected["effectiveAt"], "speech languageOverride fixture scope drifted")
    require(binding_fixture["currentTaskSemantics"] == "unchanged; immutable snapshot remains authoritative", "speech existing-task fixture no longer freezes snapshots")
    require(binding_fixture["expectedWorkerBinding"]["synthesisRequest.payload.language.override"] == expected["languageOverrideCanonical"], "speech languageOverride fixture does not bind the canonical override")
    preview = probe_output["speakerPreview"]
    action = next((item for item in descriptor["actions"] if item["id"] == "speaker.preview"), None)
    require(action is not None and action["invocation"] == expected["speakerPreviewInvocation"] and action["status"] == expected["speakerPreviewStatus"], "speaker preview is not explicit-only/conditional")
    require(preview == {"actionId": "speaker.preview", "status": "not_run", "sideEffect": "physical_audio", "requiresExplicitInvocation": True}, "speech offline probe ran or weakened speaker preview")
    require(probe_output["sideEffectsPerformed"] == [], "speech settings probe recorded side effects")

    formal_root = (PROJECT_ROOT / expected["formalReleaseRoot"]).resolve()
    require(formal_root.is_relative_to(PROJECT_ROOT.resolve()) and formal_root.is_dir(), "speech formal release root missing/out-of-project")
    require(Path(formal_receipt["formalReleaseRoot"]).resolve() == formal_root, "speech formal release receipt root drifted")
    require(formal_receipt["status"] == "passed" and formal_receipt["admissionState"] == "formal_module_candidate_for_core_admission", "speech formal module release is not passed/pending Core admission")
    require(formal_receipt["authorityMutated"] is False, "speech formal release mutated authority")
    require(formal_receipt["workerId"] == "speech.edge" and formal_receipt["workerVersion"] == expected["workerVersion"], "speech formal receipt identity drifted")
    require(Path(formal_receipt["entrypoint"]).resolve() == paths["formalExe"], "speech formal receipt entrypoint drifted")
    require(formal_receipt["exe"]["sha256"].upper() == sha256(paths["formalExe"]), "speech formal receipt EXE hash drifted")
    require(formal_receipt["releaseManifest"]["sha256"].upper() == sha256(paths["formalManifest"]), "speech formal receipt manifest hash drifted")

    release_tree = formal_receipt["releaseTree"]
    require(release_tree["fileCount"] == expected["formalFiles"] and release_tree["totalBytes"] == expected["formalBytes"], "speech formal release count/bytes drifted")
    tree_rows: list[str] = []
    for item in release_tree["files"]:
        path = (formal_root / item["path"]).resolve()
        require(path.is_relative_to(formal_root) and path.is_file(), f"speech formal release file missing/out-of-root: {item['path']}")
        require(path.stat().st_size == int(item["bytes"]) and sha256(path) == str(item["sha256"]).upper(), f"speech formal release file hash/bytes drifted: {item['path']}")
        tree_rows.append(f"{item['path']}|{int(item['bytes'])}|{str(item['sha256']).lower()}")
    tree_digest = hashlib.sha256("\n".join(tree_rows).encode("utf-8")).hexdigest().upper()
    require(tree_digest == expected["formalTreeSha256"] == str(release_tree["treeSha256"]).upper(), "speech formal release tree drifted")
    require(sha256(paths["formalManifest"]) == expected["formalManifestSha256"], "speech formal manifest expected hash drifted")
    require(formal_manifest["workerId"] == "speech.edge" and formal_manifest["workerVersion"] == expected["workerVersion"], "speech formal manifest identity drifted")
    require(formal_manifest["entrypoint"] == "workers/speech/KnowledgeClipper.Speech.Worker.exe", "speech formal manifest entrypoint drifted")
    require(formal_manifest["authorityMutation"] is False, "speech formal manifest claims authority mutation")

    require(formal_validation["status"] == "passed" and formal_validation["authorityMutated"] is False, "speech independent formal validation is not passed/read-only")
    require(Path(formal_validation["releaseReceipt"]).resolve() == paths["formalReleaseReceipt"], "speech independent validation receipt binding drifted")
    require(str(formal_validation["releaseReceiptSha256"]).upper() == sha256(paths["formalReleaseReceipt"]), "speech independent validation release receipt hash drifted")
    require(str(formal_validation["releaseTreeSha256"]).upper() == tree_digest, "speech independent validation tree hash drifted")
    require(str(formal_validation["releaseManifestSha256"]).upper() == sha256(paths["formalManifest"]), "speech independent validation manifest hash drifted")
    require(str(formal_validation["entrypointSha256"]).upper() == sha256(paths["formalExe"]), "speech independent validation EXE hash drifted")

    unit_ref = next((item for item in formal_receipt["evidence"] if item["kind"] == "unit-tests"), None)
    require(unit_ref is not None, "speech formal receipt omitted unit-test evidence")
    unit_path = Path(unit_ref["path"]).resolve()
    require(unit_path.is_relative_to(PROJECT_ROOT.resolve()) and unit_path.is_file(), "speech unit-test receipt missing/out-of-project")
    require(sha256(unit_path) == str(unit_ref["sha256"]).upper(), "speech unit-test receipt hash drifted")
    unit_receipt = load_json(unit_path)
    require(unit_receipt["status"] == "passed" and unit_receipt["failures"] == 0 and len(unit_receipt["tests"]) == expected["unitTests"] and all(item["status"] == "passed" for item in unit_receipt["tests"]), "speech formal unit-test evidence drifted")

    require(str(formal_receipt["liveDeepPathEdgeCanary"]["receiptSha256"]).upper() == sha256(paths["liveDeepPathReceipt"]), "speech formal receipt live Canary binding drifted")
    results = live_receipt["results"]
    require(len(results) == expected["liveLanguages"] and all(item["status"] == "completed" for item in results), "speech five-language live evidence is incomplete")
    minimum_path = min(int(item["audioFullPathLength"]) for item in results)
    require(live_receipt["deepPathPassed"] is True and minimum_path >= expected["minimumDeepPathLength"], "speech live deep-path evidence no longer meets the path boundary")
    require(live_receipt["physicalSpeakerPlayback"] is False and live_receipt["oldAiTtsRouteCount"] == 0, "speech live synth evidence played a speaker or used legacy AI TTS")
    require(formal_receipt["liveDeepPathEdgeCanary"]["networkTtsCalls"] == expected["liveLanguages"], "speech historical live Edge call count drifted")
    speech_secret_scan = settings_quality.scan_paths_for_secrets([formal_root, paths["formalReleaseReceipt"], paths["formalValidationReceipt"], paths["liveDeepPathReceipt"]])
    require(speech_secret_scan["findingCount"] == 0, "speech formal evidence contains plaintext secret material")

    return {
        "moduleStatus": "PASS-SPEECH.EDGE-1.2.4-FORMAL-MODULE-RELEASE",
        "integrationStatus": "PENDING-CORE-AUTHORITY-ADMISSION",
        "chain": {
            "descriptor": "PASS",
            "projectDraft": "PASS",
            "newTaskSnapshotProjection": "PASS-MODULE-BOUNDARY",
            "synthesisPayloadLanguage": request["payload"]["language"],
            "voiceId": expected["voiceId"],
            "workerRequestConfigPath": edge_path,
            "existingTasks": "IMMUTABLE",
        },
        "speakerPreview": "EXPLICIT-ONLY-CONDITIONAL-NOT-RUN",
        "formalRelease": {
            "files": release_tree["fileCount"],
            "bytes": release_tree["totalBytes"],
            "treeSha256": tree_digest,
            "unitTests": expected["unitTests"],
            "liveLanguages": expected["liveLanguages"],
            "minimumAudioPathLength": minimum_path,
            "manifest": evidence(paths["formalManifest"]),
            "exe": evidence(paths["formalExe"]),
        },
        "moduleHistoricalSideEffects": {"networkTtsCalls": expected["liveLanguages"], "physicalSpeakerCalls": 0, "externalProductionWrites": 0},
        "qualityInvocationSideEffects": {"externalCalls": 0, "workerStarts": 0, "physicalSpeakerCalls": 0},
        "secretScan": speech_secret_scan,
        "evidence": [evidence(path) for path in paths.values()] + [evidence(unit_path)],
        "candidateBound": False,
    }


def verify_anki_settings(config: dict[str, Any]) -> dict[str, Any]:
    receipt_path = resolve_ref(config["moduleReceipt"], "ankiSettings.moduleReceipt")
    manifest_path = resolve_ref(config["releaseManifest"], "ankiSettings.releaseManifest")
    exe_path = resolve_ref(config["workerExe"], "ankiSettings.workerExe")
    compatibility_path = resolve_ref(config["compatibilityReceipt"], "ankiSettings.compatibilityReceipt")
    receipt = load_json(receipt_path)
    manifest = load_json(manifest_path)
    compatibility = load_json(compatibility_path)
    expected = config["expected"]

    require(receipt["status"] == "passed" and receipt["authorityTarget"] is None, "Anki settings receipt is not a module-only PASS")
    require(receipt["release"]["manifestSha256"] == sha256(manifest_path), "Anki receipt manifest hash drifted")
    require(receipt["release"]["exe"]["sha256"] == sha256(exe_path), "Anki receipt EXE hash drifted")
    release_root = (PROJECT_ROOT / receipt["release"]["root"]).resolve()
    require(release_root.is_relative_to(PROJECT_ROOT.resolve()) and release_root.is_dir(), "Anki release root missing/out-of-project")
    require(manifest_path.relative_to(release_root).as_posix() == expected["manifestRelativePath"], "Anki release manifest is not at the worker-owned path")
    require(receipt["release"]["manifest"] == manifest_path.relative_to(PROJECT_ROOT).as_posix(), "Anki receipt points to a different release manifest")

    rows: list[tuple[str, int, str]] = []
    for path in sorted((item for item in release_root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(release_root).as_posix()):
        rows.append((path.relative_to(release_root).as_posix(), path.stat().st_size, sha256(path)))
    tree_digest = hashlib.sha256("\n".join(f"{path}\t{size}\t{digest.lower()}" for path, size, digest in rows).encode("utf-8")).hexdigest().upper()
    require(tree_digest == expected["releaseTreeSha256"] == receipt["release"]["treeSha256"], "Anki module release tree drifted")
    require(receipt["release"]["files"] == len(rows) and receipt["release"]["bytes"] == sum(item[1] for item in rows), "Anki module release count/bytes drifted")

    require(manifest["worker"] == {
        "contractVersion": "1.0.0",
        "entrypoint": "workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe",
        "outputKinds": ["anki-export-evidence", "anki-connection-status"],
        "workerId": "export.anki",
        "workerVersion": expected["workerVersion"],
    }, "Anki manifest worker identity drifted")
    require(manifest["promotedToCanonicalRegistry"] is False, "Anki module release was misrepresented as authority-promoted")
    probe = manifest["probe"]
    require(probe["effectClass"] == "provider-read-only" and probe["networkScope"] == expected["probeNetworkScope"] and probe["readOnly"] is expected["probeReadOnly"], "Anki probe is not read-only loopback")
    require(probe["externalMutation"] is False, "Anki probe permits external mutation")
    descriptor_path = release_root / manifest["settings"]["descriptor"]
    descriptor = load_json(descriptor_path)
    probe_policy = descriptor["tests"]["probeConnection"]
    require(probe_policy["executionPolicy"] == "explicit-user-action", "Anki real probe is not explicit-user-action only")
    require(probe_policy["effectClass"] == "provider-read-only" and probe_policy["externalMutation"] is False, "Anki descriptor probe policy permits mutation")
    require(descriptor["operations"]["probe"]["mayPromptUser"] is True, "Anki descriptor lost the permission-prompt boundary")
    safety = receipt["safety"]
    require(safety["realAnkiConnectCalls"] == 0 and safety["mutatingActions"] == [], "Anki module evidence performed a real or mutating action")
    require(safety["notesWritten"] == safety["mediaWritten"] == safety["decksCreated"] == 0, "Anki module evidence wrote provider data")

    require(compatibility["status"] == "conditional" and compatibility["compatibilityStatus"] == "passed", "Anki shared consumer compatibility is not a conditional PASS")
    require(compatibility["consumerOnboardingStatus"] == expected["consumerOnboardingStatus"], "Anki consumer onboarding status drifted")
    authority = compatibility["authority"]
    candidate = compatibility["candidate"]
    require(authority["workerVersion"] == expected["authorityWorkerVersion"] and candidate["workerVersion"] == expected["candidateWorkerVersion"], "Anki authority/candidate worker version boundary drifted")
    require(candidate["exe"]["sha256"] == sha256(exe_path), "Anki compatibility receipt candidate EXE hash drifted")
    require(authority["entrypoint"] == manifest["worker"]["entrypoint"] and authority["outputKinds"] == candidate["outputKinds"] == manifest["worker"]["outputKinds"], "Anki authority/candidate entrypoint or outputKinds drifted")

    referenced = [
        authority["checkpoint"],
        authority["registry"],
        authority["describe"],
        candidate["describe"],
        compatibility["core"]["candidateHarnessReceipt"],
        compatibility["core"]["probeRequest"],
        compatibility["core"]["probeResult"],
        compatibility["core"]["configArtifact"],
        compatibility["core"]["connectionStatus"],
    ]
    resolved_compatibility_refs: list[Path] = []
    for item in referenced:
        logical_path = (PROJECT_ROOT / item["path"]).resolve()
        path = Path("\\\\?\\" + str(logical_path)) if len(str(logical_path)) >= 248 else logical_path
        require(logical_path.is_relative_to(PROJECT_ROOT.resolve()) and path.is_file(), f"Anki compatibility evidence missing/out-of-project: {item['path']}")
        require(path.stat().st_size == int(item["bytes"]) and sha256(path) == str(item["sha256"]).upper(), f"Anki compatibility evidence hash/bytes drifted: {item['path']}")
        resolved_compatibility_refs.append(path)

    authority_describe = load_json(resolved_compatibility_refs[2])
    candidate_describe = load_json(resolved_compatibility_refs[3])
    require(authority_describe["workerVersion"] == expected["authorityWorkerVersion"], "Anki physical authority describe no longer reports 1.2.0")
    require(candidate_describe["workerVersion"] == expected["candidateWorkerVersion"], "Anki formal candidate describe no longer reports 1.2.1")
    require(authority_describe["workerId"] == candidate_describe["workerId"] == "export.anki", "Anki authority/candidate workerId drifted")
    require(authority_describe["outputKinds"] == candidate_describe["outputKinds"] == manifest["worker"]["outputKinds"], "Anki physical describes are not output-compatible")

    core_request = load_json(resolved_compatibility_refs[5])
    core_result = load_json(resolved_compatibility_refs[6])
    require(core_request["workerId"] == "export.anki" and core_request["operation"] == "probe", "Anki Core consumer request identity drifted")
    require(core_result["worker"]["id"] == "export.anki" and core_result["worker"]["version"] == expected["candidateWorkerVersion"] and core_result["status"] == "completed", "Anki Core consumer result is not completed by candidate 1.2.1")
    require(compatibility["probe"] == {"connectionStatus": "ready", "executionPolicy": "explicit-user-action", "externalMutation": False, "operation": "probe", "outputKind": "anki-connection-status", "resultStatus": "completed", "sideEffects": []}, "Anki shared consumer probe semantics drifted")
    required_gate_values = {
        "authorityBindsCandidate121": "no-go",
        "authorityEntrypointAndOutputKinds": "passed",
        "coreProbeConsumerWiring": "passed",
        "desktopExplicitActionSurface": "passed",
        "desktopNoDirectAnkiConnect": "passed",
        "desktopProbeDispatch": "no-go",
        "explicitUserAction": "passed",
        "externalMutationFalse": "passed",
        "probeRequestResult": "passed",
        "shaBoundAnkiExportConfig": "passed",
    }
    require(compatibility["gates"] == required_gate_values, "Anki compatibility gate outcomes drifted")
    require(compatibility["desktop"]["canaryStatus"] == "supplemental-failed" and compatibility["desktop"]["canaryAssertions"] is None, "Anki Desktop supplemental failure boundary was erased")
    compatibility_safety = compatibility["safety"]
    require(compatibility_safety["realAnkiConnectCalls"] == 0 and compatibility_safety["mutatingActions"] == [], "Anki compatibility evidence performed a real/mutating action")
    require(compatibility_safety["notesWritten"] == compatibility_safety["mediaWritten"] == 0, "Anki compatibility evidence wrote provider data")
    anki_secret_scan = settings_quality.scan_paths_for_secrets([receipt_path, manifest_path, compatibility_path] + resolved_compatibility_refs)
    require(anki_secret_scan["findingCount"] == 0, "Anki module/compatibility evidence contains plaintext secret material")

    return {
        "moduleStatus": "PASS-EXPORT.ANKI-1.2.1-RELEASE-AND-OFFLINE-PROBE",
        "integrationStatus": "NO-GO-AUTHORITY-V1.2.11-BINDS-1.2.0-PENDING-APPEND-ONLY-1.2.1",
        "release": {"treeSha256": tree_digest, "files": len(rows), "bytes": sum(item[1] for item in rows), "manifest": evidence(manifest_path), "exe": evidence(exe_path)},
        "probe": {
            "executionPolicy": expected["probeInvocation"],
            "networkScope": probe["networkScope"],
            "readOnly": True,
            "mayPromptUser": True,
            "permissionPromptIsMutation": False,
            "realAnkiConnectCalls": 0,
            "mutatingActions": [],
            "writes": 0,
        },
        "compatibility": {
            "status": "PASS-ENTRYPOINT-OUTPUTKINDS-AND-CORE-FAKE-CONSUMER",
            "authorityWorkerVersion": authority["workerVersion"],
            "candidateWorkerVersion": candidate["workerVersion"],
            "authorityBindsCandidate": False,
            "desktopProbeDispatch": "NO-GO-SUPPLEMENTAL-FAILED",
            "realAnkiConnectCalls": 0,
            "mutatingActions": [],
            "receipt": evidence(compatibility_path),
        },
        "secretScan": anki_secret_scan,
        "receipt": evidence(receipt_path),
        "candidateBound": False,
    }


def verify_remnote_settings(config: dict[str, Any]) -> dict[str, Any]:
    paths = {
        name: resolve_ref(config[name], f"remnoteSettings.{name}")
        for name in ("releaseReceipt", "releaseManifest", "offlineReceipt", "workerExe", "companionDescriptor", "workerDescribe")
    }
    release_receipt = load_json(paths["releaseReceipt"])
    manifest = load_json(paths["releaseManifest"])
    offline = load_json(paths["offlineReceipt"])
    companion = load_json(paths["companionDescriptor"])
    describe = load_json(paths["workerDescribe"])
    expected = config["expected"]
    release_root = (PROJECT_ROOT / expected["releaseRoot"]).resolve()

    require(release_root.is_relative_to(PROJECT_ROOT.resolve()) and release_root.is_dir(), "RemNote 2.1 release root missing/out-of-project")
    require(Path(release_receipt["releaseRoot"]).resolve() == release_root, "RemNote release receipt root drifted")
    require(release_receipt["status"] == "passed", "RemNote release receipt is not passed")
    require(release_receipt["worker"] == {
        "workerId": expected["workerId"],
        "workerVersion": expected["workerVersion"],
        "executableSha256": sha256(paths["workerExe"]),
    }, "RemNote release worker identity/hash drifted")
    require(Path(release_receipt["manifest"]["path"]).resolve() == paths["releaseManifest"] and release_receipt["manifest"]["sha256"] == sha256(paths["releaseManifest"]), "RemNote release manifest binding drifted")
    require(release_receipt["sourceCanary"]["sha256"] == sha256(paths["offlineReceipt"]), "RemNote packaged offline receipt binding drifted")
    source_canary = Path(release_receipt["sourceCanary"]["path"]).resolve()
    require(source_canary.is_relative_to(PROJECT_ROOT.resolve()) and source_canary.is_file() and sha256(source_canary) == sha256(paths["offlineReceipt"]), "RemNote source Canary receipt is missing or differs from the packaged evidence")

    require(manifest["workerId"] == expected["workerId"] and manifest["workerVersion"] == expected["workerVersion"], "RemNote manifest worker identity drifted")
    require(manifest["additive"] is True and manifest["frozenCommonV1Modified"] is False, "RemNote release is not additive/frozen-v1-safe")
    require(manifest["realRemNoteCalled"] is False and manifest["realMcpCalled"] is False, "RemNote offline release called a real external service")
    entries = manifest["files"]
    require(len(entries) == expected["payloadFiles"], "RemNote payload file count drifted")
    total_bytes = 0
    tree_rows: list[str] = []
    for index, item in enumerate(entries):
        path = (release_root / item["path"]).resolve()
        require(path.is_relative_to(release_root) and path.is_file(), f"RemNote manifest file missing/out-of-root: {item['path']}")
        require(path.stat().st_size == int(item["bytes"]) and sha256(path) == str(item["sha256"]).upper(), f"RemNote manifest file hash/bytes drifted: {item['path']}")
        total_bytes += int(item["bytes"])
        tree_rows.append(f"{item['path']}\t{str(item['sha256']).upper()}\t{int(item['bytes'])}")
    tree_digest = hashlib.sha256("\n".join(tree_rows).encode("utf-8")).hexdigest().upper()
    require(total_bytes == expected["payloadBytes"], "RemNote payload byte count drifted")
    require(tree_digest == expected["payloadTreeSha256"] == manifest["payloadTreeSha256"] == release_receipt["payloadTreeSha256"], "RemNote payload tree hash drifted")

    schema_paths = [release_root / item["path"] for item in entries if str(item["path"]).startswith("contracts/") and str(item["path"]).endswith(".schema.json")]
    require(len(schema_paths) == expected["schemaFiles"], "RemNote release schema inventory drifted")
    for schema_path in schema_paths:
        Draft202012Validator.check_schema(load_json(schema_path))
    required_schemas = {
        "remnote-destination-companion-descriptor.v1.schema.json",
        "remnote-destination-settings.v1.schema.json",
        "remnote-destination-settings-snapshot.v1.schema.json",
        "remnote-export-payload.v2.schema.json",
        "remnote-export-task.v2.schema.json",
        "remnote-readonly-probe-request.v1.schema.json",
        "remnote-readonly-probe-result.v1.schema.json",
        "remnote-write-receipt.v2.schema.json",
    }
    require(required_schemas <= {path.name for path in schema_paths}, "RemNote release omitted a required additive schema")

    require(describe["workerId"] == expected["workerId"] and describe["workerVersion"] == expected["workerVersion"] and describe["contractVersion"] == "1.0.0", "RemNote describe identity drifted")
    require(paths["workerExe"].name == "KnowledgeClipper.RemNoteExporter.Worker.exe" and paths["workerExe"].parent == release_root, "RemNote entrypoint is not the exact release-root executable")
    require(offline["status"] == "passed" and offline["worker"]["workerId"] == expected["workerId"] and offline["worker"]["workerVersion"] == expected["workerVersion"], "RemNote offline receipt identity/status drifted")
    require(offline["realRemNoteCalled"] is False and offline["realMcpCalled"] is False, "RemNote offline receipt performed a real call")
    require("unknown-no-auto-retry" in offline["tests"]["fakeCoverage"] and "unknown-external-state" in offline["tests"]["fakeCoverage"], "RemNote offline coverage lost unknown-state no-auto-retry")
    fixed = companion["fixedSemantics"]
    require(expected["automaticRetryAllowed"] is False and fixed["automaticRetryAllowed"] is False, "RemNote unknown state became automatically retryable")
    require(fixed["unknownExternalStateRetry"] == expected["unknownExternalStateRetry"], "RemNote unknown-state retry policy drifted")
    require(offline["readOnlyProbeEntry"]["externalWriteCount"] == 0 and companion["probe"]["externalWriteCount"] == 0, "RemNote read-only probe permits an external write")
    require(release_receipt["authorityRegistryAdmission"] == expected["authorityRegistryAdmission"] and release_receipt["realReadOnlyProbe"] == "not-run-in-offline-release", "RemNote module evidence was misrepresented as authority/live-probe admitted")

    secret_scan = settings_quality.scan_paths_for_secrets([release_root])
    require(secret_scan["findingCount"] == 0, "RemNote 2.1 release contains plaintext secret evidence")
    return {
        "moduleStatus": "PASS-EXPORT.REMNOTE-2.1.0-OFFLINE-RELEASE",
        "integrationStatus": "PENDING-CORE-AUTHORITY-AND-CANDIDATE-BINDING",
        "release": {
            "root": release_root.relative_to(PROJECT_ROOT).as_posix(),
            "payloadFiles": len(entries),
            "payloadBytes": total_bytes,
            "payloadTreeSha256": tree_digest,
            "schemasValidated": len(schema_paths),
            "entrypoint": evidence(paths["workerExe"]),
            "manifest": evidence(paths["releaseManifest"]),
        },
        "unknownExternalState": {"automaticRetryAllowed": False, "retryPolicy": fixed["unknownExternalStateRetry"]},
        "secretScan": secret_scan,
        "sideEffects": {"realRemNoteCalls": 0, "realMcpCalls": 0, "externalWrites": 0},
        "receipt": evidence(paths["releaseReceipt"]),
        "offlineReceipt": evidence(paths["offlineReceipt"]),
        "candidateBound": False,
    }


def verify_content_settings(config: dict[str, Any]) -> dict[str, Any]:
    receipt_path = resolve_ref(config["compatibilityReceipt"], "contentSettings.compatibilityReceipt")
    receipt = load_json(receipt_path)
    expected = config["expected"]
    require(receipt["status"] == "PASS" and receipt["worker"] == {"id": "content.deepseek", "version": expected["workerVersion"]}, "Content compatibility receipt identity/status drifted")
    require(receipt["scope"] == "content-worker compatibility acceptance; Core/Desktop source read-only", "Content receipt scope was broadened")
    require(receipt["provider"] == {"mode": "strict-replay", "externalConnections": expected["externalConnections"], "credentialsRecorded": False}, "Content compatibility evidence performed a live call or recorded credentials")

    def resolve_absolute(item: dict[str, Any], label: str) -> Path:
        logical_path = Path(item["path"]).resolve()
        path = Path("\\\\?\\" + str(logical_path)) if len(str(logical_path)) >= 248 else logical_path
        require(logical_path.is_relative_to(PROJECT_ROOT.resolve()) and path.is_file(), f"{label}: missing/out-of-project evidence")
        require(path.stat().st_size == int(item["bytes"]) and sha256(path) == str(item["sha256"]).upper(), f"{label}: hash/bytes drifted")
        return path

    formal_exe = resolve_absolute(receipt["formalRelease"]["entrypoint"], "Content formal entrypoint")
    require(formal_exe.name == "KnowledgeClipper.Content.Worker.exe", "Content formal entrypoint identity drifted")
    core = receipt["coreSettingsV3"]
    require(core["status"] == "compatible-sha-bound-runtime-policy-projection" and core["fixtureCount"] == expected["bindings"], "Content Core v3 projection evidence drifted")
    require(core["taskArtifactRuntimePolicyProjection"] is True and core["workflowProfileDirectRuntimePolicyFields"] is False, "Content Core v3 runtimePolicy ownership drifted")
    adapter_path = resolve_absolute(core["adapter"], "Content Core adapter")
    workflow_schema_path = resolve_absolute(core["workflowSchema"], "Content workflow schema")
    task_schema_path = resolve_absolute(core["taskSnapshotSchema"], "Content task snapshot schema")
    Draft202012Validator.check_schema(load_json(workflow_schema_path))
    task_schema = load_json(task_schema_path)
    Draft202012Validator.check_schema(task_schema)
    allowed_versions = task_schema["properties"]["modules"]["properties"]["content"]["properties"]["configSchemaVersion"]["enum"]
    require("content-config-snapshot/1.1.0" in allowed_versions, "Content Core task snapshot does not admit runtimePolicy 1.1")
    adapter = load_json(adapter_path)
    adapter_text = json.dumps(adapter, ensure_ascii=False)
    for pointer in ("/content/runtimePolicy/languagePracticeDefault", "/content/runtimePolicy/providerModel", "/content/runtimePolicy/providerMaxRetries"):
        require(pointer in adapter_text, f"Content Core adapter omitted runtimePolicy binding: {pointer}")

    desktop = receipt["desktopConsumer"]
    desktop_source = Path(desktop["source"]["path"]).resolve()
    require(desktop_source.is_relative_to(PROJECT_ROOT.resolve()) and desktop_source.is_file(), "Content Desktop source missing/out-of-project")
    desktop_source_snapshot_matches = desktop_source.stat().st_size == int(desktop["source"]["bytes"]) and sha256(desktop_source) == str(desktop["source"]["sha256"]).upper()
    require(desktop["status"] == "conditional" and desktop["publishedSnapshotSchema"] == expected["desktopPublishedSnapshotSchema"], "Content Desktop published schema conditional boundary drifted")
    require(desktop["promptCopyEditValidateDiff"] is True and desktop["runtimeAskChoiceSubmission"] is True, "Content Desktop existing supported surfaces drifted")
    require(desktop["runtimePolicyAuthoring"] is False, "Content Desktop GUI runtimePolicy authoring was fabricated as connected")
    require(core["existingIntegration"]["status"] == "CONDITIONAL_EXTERNAL_SPEECH_FAILURE" and core["existingIntegration"]["exactContentWorkerRequestAssertionReached"] is False, "Content Core external Speech blocker boundary drifted")
    require(core["existingIntegration"]["contentCheckpointsReachedBeforeFailure"] == ["1.1 runtimePolicy frozen into task snapshot", "1.1 artifact identity reached resolved plan"], "Content Core checkpoints before external Speech failure drifted")

    tests = receipt["tests"]
    require(tests["content"]["total"] == tests["content"]["pass"] == expected["contentTests"] and tests["content"]["fail"] == 0, "Content unit evidence drifted")
    require(tests["compatibilitySchemas"]["total"] == tests["compatibilitySchemas"]["pass"] == expected["schemas"], "Content schema compatibility evidence drifted")
    require(tests["compatibilitySchemas"]["bindingTotal"] == tests["compatibilitySchemas"]["bindingPass"] == expected["bindings"], "Content binding compatibility evidence drifted")
    require(tests["desktopExistingConsumer"]["status"] == "PASS", "Content Desktop consumer suite is not passed")
    require(tests["coreExistingIntegration"]["status"] == "CONDITIONAL_EXTERNAL_SPEECH_FAILURE" and tests["coreExistingIntegration"]["externalCalls"] == 0, "Content Core suite blocker was misattributed or performed an external call")
    log_paths = [resolve_absolute(tests[group]["log"], f"Content {group} log") for group in ("content", "compatibilitySchemas", "coreExistingIntegration", "desktopExistingConsumer")]

    scenario_ids = [item["id"] for item in receipt["scenarios"]]
    require(scenario_ids == expected["scenarioIds"], "Content scenario inventory/order drifted")
    scenario_paths: list[Path] = []
    scenarios = {item["id"]: item for item in receipt["scenarios"]}
    for scenario in receipt["scenarios"]:
        require(scenario["status"] == "completed" and scenario["providerMaxRetries"] == 3, f"Content scenario status/retry drifted: {scenario['id']}")
        for key in ("request", "workerResult", "learningBundle", "cardBundle", "decisionBundle"):
            if scenario[key] is not None:
                scenario_paths.append(resolve_absolute(scenario[key], f"Content scenario {scenario['id']} {key}"))
    require(scenarios["ask"]["cardCount"] == 0 and scenarios["ask"]["unresolvedReasons"] == ["language_practice_mode_required"], "Content ask mode no-card decision boundary drifted")
    require(scenarios["meaning-reading"]["cardTemplates"] == ["language_context"], "Content meaning-reading output template drifted")
    require(scenarios["writing"]["cardTemplates"] == ["language_writing"], "Content writing output template drifted")
    require(scenarios["both"]["cardTemplates"] == ["language_context", "language_writing", "language_context", "language_writing"], "Content both-mode ordering drifted")
    require(scenarios["non-language"]["decisionBundle"] is None and all(not value.startswith("language_") for value in scenarios["non-language"]["cardTemplates"]), "Content non-language input incorrectly published language cards/decision")
    require(scenarios["target-nonunique"]["cardCount"] == 0 and scenarios["target-nonunique"]["unresolvedReasons"] == ["language_writing_target_not_unique"], "Content ambiguous target no longer fails closed")
    require(scenarios["details-unreliable"]["cardCount"] == 0 and scenarios["details-unreliable"]["unresolvedReasons"] == ["language_writing_details_required"], "Content unreliable writing details no longer fail closed")

    prompt = receipt["promptWorkflow"]
    prompt_paths = [resolve_absolute(prompt[key], f"Content Prompt {key}") for key in ("copy", "diff", "editedPrompt", "validate")]
    require(len(prompt["promptSetSha256"]) == 64 and all(scenario["promptSetSha256"] == prompt["promptSetSha256"] for scenario in receipt["scenarios"]), "Content Prompt set was not SHA-bound across scenarios")
    readable_path = resolve_absolute(receipt["canary"]["readable"], "Content readable compatibility index")
    result_path = resolve_absolute(receipt["canary"]["result"], "Content compatibility result")
    resolve_absolute(receipt["canary"]["provisionalReceipt"], "Content provisional Canary receipt")
    resolve_absolute(receipt["wiringDifferences"], "Content wiring differences")
    content_secret_scan = settings_quality.scan_paths_for_secrets([receipt_path, readable_path, result_path, *prompt_paths, *scenario_paths, *log_paths])
    require(content_secret_scan["findingCount"] == 0, "Content compatibility evidence contains plaintext secret material")
    return {
        "moduleStatus": "PASS-CONTENT.DEEPSEEK-0.7.0-OFFLINE-COMPATIBILITY",
        "integrationStatus": "CONDITIONAL-DESKTOP-RUNTIMEPOLICY-1.0-AND-CORE-SPEECH-V3-ASSERTION",
        "tests": {"content": expected["contentTests"], "schemas": expected["schemas"], "bindings": expected["bindings"], "desktopSuite": "PASS"},
        "scenarios": [{"id": item["id"], "status": item["status"], "cardCount": item["cardCount"], "cardTemplates": item["cardTemplates"], "unresolvedReasons": item["unresolvedReasons"]} for item in receipt["scenarios"]],
        "promptWorkflow": "PASS-COPY-EDIT-VALIDATE-DIFF-SHA-BOUND",
        "coreProjection": "PASS-RUNTIMEPOLICY-1.1-TO-TASK-SNAPSHOT-AND-RESOLVED-PLAN",
        "desktopPublishedSnapshotSchema": desktop["publishedSnapshotSchema"],
        "desktopGuiRuntimePolicyAuthoring": "CONDITIONAL-NOT-CONNECTED",
        "desktopSourceSnapshot": "MATCHED" if desktop_source_snapshot_matches else "POST-RECEIPT-DRIFT-DETECTED-OWNER-RECEIPT-REQUIRED",
        "externalCoreBlocker": "SPEECH-V3-WORKERREQUEST-ASSERTION-NOT-CONTENT",
        "readable": evidence(readable_path),
        "secretScan": content_secret_scan,
        "sideEffects": {"liveDeepSeekCalls": 0, "externalConnections": 0},
        "receipt": evidence(receipt_path),
        "candidateBound": False,
    }


def verify_language_cards(config: dict[str, Any]) -> dict[str, Any]:
    meaning = config["meaningReading"]
    require(meaning["cards"] == [meaning["existingCard"]], "meaning-reading is not byte-equivalent to the existing card")
    writing = config["writing"]
    term = writing["targetTerm"]
    require(writing["rawText"].count(term) == 1, "writing positive fixture target is not unique")
    annotation = f"（{writing['pronunciation']}；{writing['meaning']}）"
    expected_front = writing["rawText"].replace(term, annotation, 1)
    expected_writing = {"template": "language_writing", "front": expected_front, "back": term}
    require(writing["cards"] == [expected_writing] and term not in writing["cards"][0]["front"], "writing card is not the exact unique replacement")
    both = config["both"]
    require(both["cards"] == [meaning["existingCard"], expected_writing], "both is not exactly original card then writing card")
    non_language = config["nonLanguage"]
    require(non_language["status"] == "not_language" and non_language["cards"] == [], "non-language negative emitted cards")
    ambiguous = config["ambiguous"]
    require(ambiguous["rawText"].count(ambiguous["targetTerm"]) != 1 and ambiguous["status"] == "waiting_input" and ambiguous["resolution"] == "unresolved" and ambiguous["cards"] == [], "ambiguous target did not remain waiting_input/unresolved")
    return {
        "status": "PASS-OFFLINE-READABLE-COMPARISON",
        "meaningReading": {"rawText": meaning["rawText"], "cards": meaning["cards"]},
        "writing": {"rawText": writing["rawText"], "cards": writing["cards"]},
        "both": {"rawText": writing["rawText"], "cards": both["cards"]},
        "negative": {"nonLanguage": non_language, "ambiguous": ambiguous},
        "candidateBound": False,
    }


def verify_desktop_automation(config: dict[str, Any]) -> dict[str, Any]:
    v11_path = resolve_source(config["automationV11"], "desktopAutomation.automationV11")
    v12_path = resolve_source(config["automationV12"], "desktopAutomation.automationV12")
    settings_path = resolve_source(config["settingsCatalog"], "desktopAutomation.settingsCatalog")
    ui_path = resolve_source(config["desktopUi"], "desktopAutomation.desktopUi")
    v11 = v11_path.read_text(encoding="utf-8-sig")
    v12 = v12_path.read_text(encoding="utf-8-sig")
    settings = settings_path.read_text(encoding="utf-8-sig")
    ui = ui_path.read_text(encoding="utf-8-sig")
    require(config["contextModeInFloatingWindow"] is config["originalModeInFloatingWindow"] is config["bottomInlineInputEditable"] is True, "Desktop automation requirement weakened")
    require('"knowledge-clipper.desktop.mode.context"' in v11 and '"knowledge-clipper.desktop.mode.original-image"' in v11, "floating context/original mode automation IDs missing")
    require('InlineQuestionInputAutomationId = "knowledge-clipper.desktop.question.inline-input"' in v12, "bottom input automation ID missing")
    require("contextSection = ModeSection" in ui and "originalSection = ModeSection" in ui and "root.Controls.Add(inlineQuestionInput, 0, 3)" in ui, "floating modes or bottom input layout drifted")
    require("Multiline = false" in ui and "TabStop = true" in ui and "ShortcutsEnabled = true" in ui, "bottom input is not editable")
    ids = re.findall(r'internal const string\s+\w+\s*=\s*"([^"]+)"', settings)
    minimum_ids = int(config["minimumSettingsAutomationIdCount"])
    require(minimum_ids == 66 and len(ids) >= minimum_ids and len(set(ids)) == len(ids), "settings automation ID count/uniqueness drifted")
    require(all(item.startswith("knowledge-clipper.desktop.") for item in ids), "settings automation ID is not neutral/stable")
    return {
        "sourceBaseline": "PASS-NOT-CANDIDATE-BOUND",
        "floatingContextMode": "PASS-SOURCE",
        "floatingOriginalMode": "PASS-SOURCE",
        "bottomInlineInput": "PASS-SOURCE",
        "settingsAutomationIds": {"count": len(ids), "unique": len(set(ids)), "minimum": minimum_ids, "prefix": "knowledge-clipper.desktop."},
        "evidence": [evidence(path) for path in (v11_path, v12_path, settings_path, ui_path)],
        "ownerAutomationReceipt": "PENDING-DESKTOP",
        "candidateBound": False,
    }


def verify_retention_safety(config: dict[str, Any]) -> dict[str, Any]:
    program_path = resolve_source(config["programSource"], "retentionSafety.programSource")
    job_store_path = resolve_source(config["legacyJobStoreSource"], "retentionSafety.legacyJobStoreSource")
    program = program_path.read_text(encoding="utf-8-sig")
    job_store = job_store_path.read_text(encoding="utf-8-sig")
    legacy_call = "JobStore.PurgeExpired(config, 30);" in program
    unsafe_implementation = all(marker in job_store for marker in ("GetCreationTimeUtc", "Directory.Delete(full, true)", "public static void PurgeExpired"))
    safe_startup_markers = (
        "ProductionStartupPolicy.Apply(config);",
        'RetentionStatus = "disabled-until-state-aware-retention-service"',
        "Legacy JobStore.PurgeExpired is intentionally not called",
    )
    safe_startup_disabled = not legacy_call and all(marker in program for marker in safe_startup_markers)
    require(legacy_call or safe_startup_disabled, "production startup removed the known purge call without an explicit fail-closed retention policy")
    require(config["isolatedProbeOnly"] is True and config["productionJobsRootUsed"] is False, "retention probe may touch production jobs")
    require(config["legacyPurgeInvocationsAllowedAtStartup"] == 0, "legacy purge was allowed at startup")
    fixtures = config["oldTaskFixtures"]
    expected_states = {"active", "waiting_input", "unknown_external_state"}
    require(expected_states <= {item["state"] for item in fixtures}, "retention fixture omitted a protected nonterminal state")
    require(any(item.get("pinned") is True for item in fixtures) and any(item.get("retentionClass") == "evidence" for item in fixtures), "retention fixture omitted pinned/evidence tasks")
    require(all(item["preserved"] is True for item in fixtures), "retention fixture permits startup deletion")

    with tempfile.TemporaryDirectory(prefix="knowledge-clipper-retention-quality-") as temp:
        root = Path(temp)
        paths: dict[str, Path] = {}
        for item in fixtures:
            path = root / item["taskId"]
            path.mkdir()
            (path / "task-state.json").write_text(json.dumps(item, ensure_ascii=False), encoding="utf-8")
            paths[item["taskId"]] = path
        before = {task_id: sha256(path / "task-state.json") for task_id, path in paths.items()}
        # This probe never invokes the legacy implementation. It only proves that the
        # quality harness itself preserves isolated state while inspecting startup policy.
        probe_action = "BLOCKED-LEGACY-PURGE-NOT-EXECUTED" if legacy_call else "SAFE-STARTUP-PROBE-OWNER-RECEIPT-REQUIRED"
        after = {task_id: sha256(path / "task-state.json") for task_id, path in paths.items() if path.is_dir()}
        require(before == after and len(after) == len(fixtures), "isolated startup guard deleted or changed an old task")

    current_status = "NO-GO-LEGACY-STARTUP-PURGE-CALL" if legacy_call else "IMPLEMENTED-PENDING-ISOLATED-OWNER-STARTUP-RECEIPT"
    return {
        "status": current_status,
        "legacyCallPresent": legacy_call,
        "legacyImplementationUnsafe": unsafe_implementation,
        "safeStartupRetentionDisabled": safe_startup_disabled,
        "startupLegacyPurgeInvocations": 0,
        "productionJobsRootUsed": False,
        "isolatedProbe": {"status": probe_action, "oldTasksChecked": len(fixtures), "oldTasksPreserved": len(fixtures), "states": [item["state"] for item in fixtures]},
        "evidence": [evidence(program_path), evidence(job_store_path)],
        "requiredPromotionCondition": "production startup has no JobStore.PurgeExpired call and owner isolated-startup receipt proves state-aware retention",
        "candidateBound": False,
    }


def verify_legacy_canary_safety(config: dict[str, Any]) -> dict[str, Any]:
    build_path = resolve_ref(config["buildScript"], "legacyCanarySafety.buildScript")
    readme_path = resolve_ref(config["readme"], "legacyCanarySafety.readme")
    build = build_path.read_text(encoding="utf-8-sig")
    readme = readme_path.read_text(encoding="utf-8-sig")
    opt_in = config["explicitOptInSwitch"]
    require(config["defaultBuildIncludesLegacyExternalCanaries"] is False and config["executeLegacyCanaries"] is False, "legacy external Canary policy was weakened")
    require(f"[switch]${opt_in}" in build, "root build lacks an explicit legacy external Canary opt-in switch")
    forbidden = config["forbiddenBasenames"]
    require(set(forbidden) == {"SmokeTest.exe", "TtsCanary.exe"}, "legacy external Canary forbidden inventory was weakened")
    for name in forbidden:
        require(build.count(name) == 1, f"root build legacy output declaration drifted: {name}")
        position = build.index(name)
        guard = build.rfind(f"if (${opt_in}) {{", 0, position)
        require(guard >= 0 and position - guard < 600, f"root build emits {name} outside the explicit opt-in guard")
    require("Write-Warning 'Building legacy external Canary executables." in build, "legacy external Canary opt-in lacks a warning")
    require("SmokeTest.exe" in readme and "不属于当前安全验收入口" in readme and "不得在日常启动或正式" in readme, "README does not retire the legacy external Canary route")

    raw_root = Path(config["candidateRoot"])
    candidate_root = raw_root.resolve() if raw_root.is_absolute() else (PROJECT_ROOT / raw_root).resolve()
    require(candidate_root.is_relative_to(PROJECT_ROOT.resolve()) and candidate_root.is_dir(), "candidate inventory root missing/out-of-project")
    files = [path for path in candidate_root.rglob("*") if path.is_file()]
    forbidden_lower = {name.lower() for name in forbidden}
    hits = [path for path in files if path.name.lower() in forbidden_lower]
    require(not hits, "integrated candidate contains a legacy external Canary executable")
    return {
        "status": "PASS-DEFAULT-BUILD-OPT-IN-ONLY-AND-CANDIDATE-INVENTORY-CLEAN",
        "buildDefaultLegacyExternalCanaries": False,
        "explicitOptInSwitch": opt_in,
        "legacyExecutablesRunByQuality": 0,
        "candidateInventory": {
            "root": candidate_root.relative_to(PROJECT_ROOT).as_posix(),
            "filesChecked": len(files),
            "forbiddenBasenames": forbidden,
            "forbiddenHits": 0,
        },
        "evidence": [evidence(build_path), evidence(readme_path)],
        "candidatePayloadReadOnly": True,
    }


def render_index(result: dict[str, Any]) -> str:
    language = result["languageCards"]
    return "\n".join([
        "# Additive offline quality backlog gate",
        "",
        f"- Overall: `{result['status']}`",
        f"- Baseline candidateHash: `{result['baseline']['candidateHash']}` (read-only; no new candidate binding)",
        f"- Requirement audit: `{result['requirementAudit']['status']}`; narrow tests cannot promote its incomplete boundaries.",
        "- Quality external calls / worker starts: `0 / 0`",
        "",
        "## Original mode",
        "",
        f"- Offline desired trace: `{result['originalMode']['offlineFixture']}`; original passthrough worker / OCR provider / MinerU = `1/0/0`.",
        f"- Current Core owner: `{result['originalMode']['currentOwnerState']}`; source implementation is not owner receipt or candidate evidence.",
        "- Required terminal if media cannot continue: locatable conditional/unsupported, never succeeded.",
        "",
        "## Image partial failed-items-only",
        "",
        f"- Status: `{result['imagePartialRetry']['status']}`; reused hashes: `{json.dumps(result['imagePartialRetry']['reusedItemHashes'], ensure_ascii=False)}`; retried: `{result['imagePartialRetry']['retriedItemIds']}`.",
        "- Whole-card partial blocks downstream; unknown_external_state automatic retries = 0.",
        f"- Image/OCR 1.2.2 module: `{result['imageSettings']['moduleStatus']}`; integration: `{result['imageSettings']['integrationStatus']}`.",
        f"- Candidate tree: `{result['imageSettings']['release']['treeSha256']}`; providerSettings exact WorkerRequest consumption: `{result['imageSettings']['providerSettings']['formalWorkerRequestExactConsumption']}`.",
        "- Owner boundaries remain original=unsupported, partial continuation=conditional, retention=unsupported; MinerU/network/upload calls by Quality = 0.",
        "",
        "## Language raw text -> cards",
        "",
        f"- Meaning-reading: `{language['meaningReading']['rawText']}` -> `{json.dumps(language['meaningReading']['cards'], ensure_ascii=False)}`",
        f"- Writing: `{language['writing']['rawText']}` -> `{json.dumps(language['writing']['cards'], ensure_ascii=False)}`",
        f"- Both: `{language['both']['rawText']}` -> `{json.dumps(language['both']['cards'], ensure_ascii=False)}`",
        "- Non-language emits no cards; ambiguous target remains waiting_input/unresolved.",
        "",
        "## Speech settings companion",
        "",
        f"- Module: `{result['speechSettings']['moduleStatus']}`; integration: `{result['speechSettings']['integrationStatus']}`.",
        f"- Language: `{json.dumps(result['speechSettings']['chain']['synthesisPayloadLanguage'], ensure_ascii=False)}`; voice: `{result['speechSettings']['chain']['voiceId']}`; existing tasks: `{result['speechSettings']['chain']['existingTasks']}`.",
        f"- Speaker preview: `{result['speechSettings']['speakerPreview']}`; Quality current invocation external/network TTS / speaker calls = `0/0`; module historical Edge synth calls = `{result['speechSettings']['moduleHistoricalSideEffects']['networkTtsCalls']}`.",
        f"- Formal release: {result['speechSettings']['formalRelease']['files']} files / `{result['speechSettings']['formalRelease']['treeSha256']}`; 49 unit tests; historical five-language Edge synth = 5, Quality replay calls = 0.",
        "",
        "## Anki settings/probe module",
        "",
        f"- Module: `{result['ankiSettings']['moduleStatus']}`; integration: `{result['ankiSettings']['integrationStatus']}`.",
        f"- Release tree: `{result['ankiSettings']['release']['treeSha256']}`; probe: explicit user action, read-only loopback, real calls/mutations/writes = `0/0/0`.",
        f"- Compatibility: authority reports `{result['ankiSettings']['compatibility']['authorityWorkerVersion']}`, module candidate reports `{result['ankiSettings']['compatibility']['candidateWorkerVersion']}`; entrypoint/outputKinds/Core Fake consumer PASS, authority binding and Desktop probe dispatch remain NO-GO.",
        "- A permission prompt is explicitly possible but is not evidence of a write.",
        "",
        "## RemNote exporter module",
        "",
        f"- Module: `{result['remnoteSettings']['moduleStatus']}`; integration: `{result['remnoteSettings']['integrationStatus']}`.",
        f"- Payload: {result['remnoteSettings']['release']['payloadFiles']} files / {result['remnoteSettings']['release']['payloadBytes']} bytes / `{result['remnoteSettings']['release']['payloadTreeSha256']}`; schemas validated: {result['remnoteSettings']['release']['schemasValidated']}.",
        "- Real RemNote/MCP/write calls = `0/0/0`; unknown_external_state automatic retry = false (manual-only).",
        "",
        "## Content 0.7 compatibility",
        "",
        f"- Module: `{result['contentSettings']['moduleStatus']}`; integration: `{result['contentSettings']['integrationStatus']}`.",
        f"- Tests: {result['contentSettings']['tests']['content']}/{result['contentSettings']['tests']['content']} Content, {result['contentSettings']['tests']['schemas']}/{result['contentSettings']['tests']['schemas']} schemas, {result['contentSettings']['tests']['bindings']}/{result['contentSettings']['tests']['bindings']} bindings; live DeepSeek calls = 0.",
        f"- Readable scenarios: `{json.dumps(result['contentSettings']['scenarios'], ensure_ascii=False)}`",
        "- Core v3 runtimePolicy 1.1 projection is evidenced; Desktop still publishes 1.0 and GUI policy authoring remains conditional. Core suite's Speech v3 assertion blocker is not attributed to Content.",
        "",
        "## Pending owner evidence",
        "",
        "- Core: original-mode resolver/terminal route and exhaustive settings chain receipt.",
        "- Image: 1.2.2 module/admission evidence PASS; new authority, Core continuation, and Desktop candidate remain pending.",
        "- Content: 0.7.0 offline compatibility PASS; Desktop runtimePolicy GUI authoring and same-candidate revalidation remain conditional.",
        "- Desktop: candidate-scoped automation receipt for floating modes, bottom input, and settings IDs.",
        "- Speech: 1.2.4 formal module is PASS; Core authority admission remains pending.",
        "- Anki: 1.2.1 module/consumer compatibility is PASS, but v1.2.11 physically binds 1.2.0; append-only authority and Desktop probe dispatch remain NO-GO.",
        "- RemNote: 2.1.0 offline release is PASS; Core-owned authority and candidate binding remain pending.",
        "- Existing B/RemNote and C/MinerU remain pending, not failed.",
        "",
        "## Retention safety",
        "",
        f"- Current production startup: `{result['retentionSafety']['status']}`.",
        f"- Isolated quality guard preserved {result['retentionSafety']['isolatedProbe']['oldTasksPreserved']}/{result['retentionSafety']['isolatedProbe']['oldTasksChecked']} old task fixtures and never opened the production jobs root.",
        "",
        "## Legacy external Canary safety",
        "",
        f"- `{result['legacyCanarySafety']['status']}`; inspected {result['legacyCanarySafety']['candidateInventory']['filesChecked']} candidate files; forbidden hits = 0.",
        "- Quality did not execute SmokeTest.exe or TtsCanary.exe.",
    ]) + "\n"


def evaluate(fixture: dict[str, Any]) -> dict[str, Any]:
    require(fixture["policy"] == {"externalCalls": 0, "workerStarts": 0, "pendingIsFailure": False, "candidateBound": False, "bAndCRemainPending": True}, "offline policy weakened")
    require(fixture["ownerEvidence"] == {"core": "pending", "content": "module-passed-integration-conditional", "image": "module-passed-integration-pending", "desktop": "pending", "speech": "module-passed-integration-pending", "anki": "module-passed-integration-pending", "remnote": "module-passed-authority-pending"}, "owner evidence was fabricated as complete")
    baseline = verify_baseline(fixture["baseline"])
    requirement_audit = verify_requirement_audit(fixture["requirementAudit"])
    original = verify_original_mode(fixture["originalMode"])
    image = verify_image_partial_retry(fixture["imagePartialRetry"])
    image_settings = verify_image_settings(fixture["imageSettings"])
    settings = verify_settings_honesty(fixture["settingsHonesty"])
    speech = verify_speech_settings(fixture["speechSettings"])
    anki = verify_anki_settings(fixture["ankiSettings"])
    remnote = verify_remnote_settings(fixture["remnoteSettings"])
    content = verify_content_settings(fixture["contentSettings"])
    language = verify_language_cards(fixture["languageCards"])
    desktop = verify_desktop_automation(fixture["desktopAutomation"])
    retention = verify_retention_safety(fixture["retentionSafety"])
    legacy_canaries = verify_legacy_canary_safety(fixture["legacyCanarySafety"])
    secret_scan_paths = [
        DEFAULT_FIXTURE,
        resolve_ref(fixture["requirementAudit"], "secretScan.requirementAudit"),
        resolve_source(fixture["originalMode"]["currentCoreSource"], "secretScan.core"),
        resolve_source(fixture["desktopAutomation"]["desktopUi"], "secretScan.desktopUi"),
        resolve_source(fixture["desktopAutomation"]["settingsCatalog"], "secretScan.settingsCatalog"),
        resolve_ref(fixture["speechSettings"]["descriptor"], "secretScan.speechDescriptor"),
        resolve_ref(fixture["speechSettings"]["probeInput"], "secretScan.speechProbeInput"),
        resolve_ref(fixture["speechSettings"]["probeOutput"], "secretScan.speechProbeOutput"),
        resolve_ref(fixture["ankiSettings"]["moduleReceipt"], "secretScan.ankiReceipt"),
        resolve_ref(fixture["ankiSettings"]["releaseManifest"], "secretScan.ankiManifest"),
        resolve_ref(fixture["ankiSettings"]["compatibilityReceipt"], "secretScan.ankiCompatibility"),
        resolve_ref(fixture["imageSettings"]["admissionReceipt"], "secretScan.imageAdmission"),
        resolve_ref(fixture["contentSettings"]["compatibilityReceipt"], "secretScan.contentCompatibility"),
        resolve_ref(fixture["remnoteSettings"]["releaseReceipt"], "secretScan.remnoteReceipt"),
        resolve_ref(fixture["remnoteSettings"]["releaseManifest"], "secretScan.remnoteManifest"),
        resolve_ref(fixture["remnoteSettings"]["offlineReceipt"], "secretScan.remnoteOfflineReceipt"),
        resolve_ref(fixture["legacyCanarySafety"]["buildScript"], "secretScan.buildScript"),
        resolve_ref(fixture["legacyCanarySafety"]["readme"], "secretScan.readme"),
    ]
    secret_scan = settings_quality.scan_paths_for_secrets(secret_scan_paths)
    require(secret_scan["findingCount"] == 0, "offline fixtures/source baseline contain plaintext secret evidence")
    immutable = settings_quality.verify_immutable_baselines()
    require(immutable["frozenV1"]["filesChecked"] == 21 and immutable["frozenV1"]["mismatches"] == 0, "frozen v1 baseline drifted")
    return {
        "schemaVersion": "knowledge-clipper-additive-offline-backlog-quality-receipt.v1",
        "generatedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "NO-GO-OFFLINE-GATE-READY-RETENTION-LEGACY-PURGE-OWNER-EVIDENCE-PENDING" if retention["legacyCallPresent"] else "CONDITIONAL-OFFLINE-GATE-READY-OWNER-EVIDENCE-PENDING",
        "scope": "quality-only additive fake/offline admission; no external services; not candidate-bound",
        "fixtureValidation": validate_fixture_set(),
        "baseline": baseline,
        "requirementAudit": requirement_audit,
        "originalMode": original,
        "imagePartialRetry": image,
        "imageSettings": image_settings,
        "settingsHonesty": settings,
        "speechSettings": speech,
        "ankiSettings": anki,
        "remnoteSettings": remnote,
        "contentSettings": content,
        "languageCards": language,
        "desktopAutomation": desktop,
        "retentionSafety": retention,
        "legacyCanarySafety": legacy_canaries,
        "secretScan": secret_scan,
        "immutableBaselines": immutable,
        "ownerEvidence": fixture["ownerEvidence"],
        "existingPathState": {"B": "PENDING-UNCHANGED", "C": "PENDING-UNCHANGED"},
        "qualitySideEffects": {"externalCalls": 0, "workerStarts": 0, "remnoteWrites": 0, "ankiWrites": 0, "mineruCalls": 0, "physicalSpeakerInvocations": 0},
        "candidateBinding": {"status": "PENDING-OWNER-RECEIPTS", "candidateHash": None, "baselineCandidateHashReadOnly": baseline["candidateHash"]},
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify additive fake/offline backlog gates without external services.")
    parser.add_argument("--fixture", default=str(DEFAULT_FIXTURE))
    parser.add_argument("--emit-root")
    args = parser.parse_args()
    fixture_path = Path(args.fixture).resolve()
    result = evaluate(load_json(fixture_path))
    result["fixture"] = evidence(fixture_path)
    artifacts: dict[str, Any] = {}
    if args.emit_root:
        root = Path(args.emit_root).resolve()
        require(root.is_relative_to(PROJECT_ROOT.resolve()), "output root escaped project")
        require(not root.exists(), f"append-only output root exists: {root}")
        root.mkdir(parents=True)
        receipt_path = root / "quality-receipt.json"
        index_path = root / "input-output-index.md"
        receipt_path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        index_path.write_text(render_index(result), encoding="utf-8")
        artifacts = {"root": root.relative_to(PROJECT_ROOT).as_posix(), "receipt": evidence(receipt_path), "readableIndex": evidence(index_path)}
    print(json.dumps({"result": result, "artifacts": artifacts}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
