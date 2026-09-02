from __future__ import annotations

import argparse
import copy
import hashlib
import json
import os
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource

import verify_integrated_candidate_quality as common
import verify_settings_control_plane as settings_quality


PROJECT_ROOT = Path(__file__).resolve().parents[1]
FIXTURE_ROOT = PROJECT_ROOT / "tests" / "Quality.Gates" / "fixtures" / "phase-b-candidate"
DEFAULT_FIXTURE = FIXTURE_ROOT / "valid" / "phase-b-v1.2.8.json"
FIXTURE_SCHEMA = FIXTURE_ROOT / "phase-b-candidate-gate.schema.json"
COMMON_SCHEMA_ROOT = PROJECT_ROOT / "contracts" / "v1" / "schemas" / "common"


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def load_json(path: Path) -> Any:
    return json.loads(io_path(path).read_text(encoding="utf-8-sig"))


def io_path(path: Path) -> Path:
    text = str(path)
    if os.name == "nt" and path.is_absolute() and not text.startswith("\\\\?\\"):
        return Path("\\\\?\\" + text)
    return path


def logical_path_text(path: Path) -> str:
    text = str(path)
    return text[4:] if text.startswith("\\\\?\\") else text


def walk_files(root: Path) -> list[Path]:
    return [path for path in io_path(root).rglob("*") if path.is_file()]


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with io_path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def resolve_path(value: str) -> Path:
    path = Path(value)
    return path.resolve() if path.is_absolute() else (PROJECT_ROOT / path).resolve()


def display_path(path: Path) -> str:
    resolved = Path(logical_path_text(path.resolve()))
    try:
        return resolved.relative_to(PROJECT_ROOT.resolve()).as_posix()
    except ValueError:
        return str(resolved)


def evidence(path: Path) -> dict[str, Any]:
    native = io_path(path)
    require(native.is_file(), f"missing evidence file: {path}")
    return {"path": display_path(path), "bytes": native.stat().st_size, "sha256": sha256_file(path)}


def assert_inventory_summary(actual: dict[str, Any], expected: dict[str, Any], label: str) -> None:
    for key in ("fileCount", "totalBytes"):
        require(int(actual[key]) == int(expected[key]), f"{label}: {key} mismatch")
    require(str(actual["treeSha256"]).upper() == str(expected["treeSha256"]).upper(), f"{label}: treeSha256 mismatch")


def checked_ref(item: dict[str, Any], label: str) -> Path:
    path = resolve_path(item["path"])
    require(io_path(path).is_file(), f"{label}: missing {path}")
    require(sha256_file(path) == str(item["sha256"]).upper(), f"{label}: SHA-256 mismatch")
    return path


def validate_fixtures() -> dict[str, Any]:
    schema = load_json(FIXTURE_SCHEMA)
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema, format_checker=FormatChecker())
    valid = sorted((FIXTURE_ROOT / "valid").glob("*.json"))
    invalid = sorted((FIXTURE_ROOT / "invalid").glob("*.json"))
    require(valid and invalid, "Phase B gate fixtures are incomplete")
    for path in valid:
        errors = list(validator.iter_errors(load_json(path)))
        require(not errors, f"valid Phase B fixture rejected: {path.name}: {errors[0].message if errors else ''}")
    for path in invalid:
        require(bool(list(validator.iter_errors(load_json(path)))), f"invalid Phase B fixture accepted: {path.name}")
    return {
        "status": "passed",
        "schema": evidence(FIXTURE_SCHEMA),
        "validAccepted": len(valid),
        "invalidRejected": len(invalid),
    }


def validate_common(document: Any, schema_name: str, label: str) -> None:
    schema_path = COMMON_SCHEMA_ROOT / schema_name
    schema = copy.deepcopy(load_json(schema_path))
    schema["$id"] = schema_path.as_uri()
    registry = Registry()
    for path in COMMON_SCHEMA_ROOT.glob("*.schema.json"):
        resource_document = copy.deepcopy(load_json(path))
        resource_document["$id"] = path.as_uri()
        registry = registry.with_resource(path.as_uri(), Resource.from_contents(resource_document))
    errors = sorted(
        Draft202012Validator(schema, registry=registry, format_checker=FormatChecker()).iter_errors(document),
        key=lambda item: list(item.absolute_path),
    )
    require(not errors, f"{label}: common schema rejected: {errors[0].message if errors else ''}")


def validate_project_schema(document: Any, schema_path: Path, label: str) -> None:
    schema_path = schema_path.resolve()
    schema = copy.deepcopy(load_json(schema_path))
    schema["$id"] = schema_path.as_uri()
    registry = Registry()
    for path in (PROJECT_ROOT / "contracts").rglob("*.schema.json"):
        resource_document = copy.deepcopy(load_json(path))
        resource_document["$id"] = path.resolve().as_uri()
        registry = registry.with_resource(path.resolve().as_uri(), Resource.from_contents(resource_document))
    errors = sorted(
        Draft202012Validator(schema, registry=registry, format_checker=FormatChecker()).iter_errors(document),
        key=lambda item: list(item.absolute_path),
    )
    require(not errors, f"{label}: project schema rejected: {errors[0].message if errors else ''}")


def verify_artifact(job_root: Path, output: dict[str, Any], label: str) -> dict[str, Any]:
    manifest = (job_root / output["manifestPath"]).resolve()
    require(manifest.is_relative_to(job_root.resolve()), f"{label}: artifact manifest escapes job root")
    require(io_path(manifest).is_file(), f"{label}: artifact manifest missing")
    require(sha256_file(manifest) == str(output["expectedSha256"]).upper(), f"{label}: artifact manifest hash mismatch")
    document = load_json(manifest)
    validate_common(document, "artifact.schema.json", f"{label}.manifest")
    files: list[dict[str, Any]] = []
    for index, item in enumerate(document["files"]):
        path = (job_root / item["path"]).resolve()
        require(path.is_relative_to(job_root.resolve()) and io_path(path).is_file(), f"{label}.files[{index}]: missing/escaping file")
        require(io_path(path).stat().st_size == int(item["bytes"]), f"{label}.files[{index}]: byte mismatch")
        require(sha256_file(path) == str(item["sha256"]).upper(), f"{label}.files[{index}]: hash mismatch")
        files.append(evidence(path))
    return {"reference": output, "manifest": evidence(manifest), "files": files}


def job_root_for_receipt(receipt_path: Path, receipt: dict[str, Any]) -> Path:
    strict = receipt.get("strictLayout")
    if strict:
        root = Path(strict["jobRoot"]).resolve()
        require(strict["passed"] is True and strict["workerValidationRelaxed"] is False, "strict job layout was relaxed/failed")
        require(root.name.casefold() == str(receipt["taskId"]).casefold(), "strict job root leaf does not equal taskId")
        return root
    legacy = receipt_path.parent / "job"
    require(legacy.is_dir(), f"path receipt has no resolvable job root: {receipt_path}")
    return legacy.resolve()


def verify_path_receipt(receipt_path: Path, expected_authority: dict[str, Any], expected_status: str) -> dict[str, Any]:
    receipt = load_json(receipt_path)
    require(receipt["schemaVersion"] == "knowledge-clipper-phase-b-path.v1", "Phase B path receipt schema mismatch")
    require(receipt["status"] == expected_status, f"Path {receipt.get('path')} status mismatch")
    require(receipt["authority"]["registryVersion"] == expected_authority["registryVersion"], "path authority version mismatch")
    require(receipt["authority"]["registrySha256"].upper() == expected_authority["registrySha256"].upper(), "path authority registry hash mismatch")
    job_root = job_root_for_receipt(receipt_path, receipt)
    attempts: list[dict[str, Any]] = []
    for index, attempt in enumerate(receipt["attempts"]):
        result_path = (job_root / attempt["resultPath"]).resolve()
        require(result_path.is_relative_to(job_root) and io_path(result_path).is_file(), f"attempt[{index}] result missing/escaping")
        require(sha256_file(result_path) == str(attempt["resultSha256"]).upper(), f"attempt[{index}] result hash mismatch")
        result = load_json(result_path)
        validate_common(result, "worker-result.schema.json", f"attempt[{index}].result")
        request_path = result_path.parent / "request.json"
        require(io_path(request_path).is_file(), f"attempt[{index}] request missing")
        request = load_json(request_path)
        validate_common(request, "worker-request.schema.json", f"attempt[{index}].request")
        for key in ("taskId", "runId", "stageId", "attemptId"):
            require(result[key] == request[key], f"attempt[{index}] request/result {key} mismatch")
        require(result["attemptId"] == attempt["attemptId"] and result["stageId"] == attempt["stageId"], f"attempt[{index}] receipt identity mismatch")
        artifacts = [verify_artifact(job_root, item, f"attempt[{index}].outputs[{output_index}]") for output_index, item in enumerate(result["outputs"])]
        attempts.append({
            "stageId": attempt["stageId"],
            "worker": result["worker"],
            "attemptId": attempt["attemptId"],
            "idempotencyKey": attempt["idempotencyKey"],
            "status": result["status"],
            "request": evidence(request_path),
            "result": evidence(result_path),
            "artifacts": artifacts,
            "error": result["error"],
            "payload": result.get("payload", {}),
        })
    return {
        "receipt": evidence(receipt_path),
        "path": receipt["path"],
        "label": receipt["label"],
        "status": receipt["status"],
        "taskId": receipt["taskId"],
        "jobRoot": str(job_root),
        "input": receipt["input"],
        "plan": receipt["plan"],
        "attempts": attempts,
        "invocationCounts": receipt["invocationCounts"],
        "replay": receipt.get("replay"),
    }


def recompute_manifest_tree(root: Path, manifest: dict[str, Any]) -> dict[str, Any]:
    rows: list[str] = []
    total = 0
    for index, item in enumerate(manifest["files"]):
        path = (root / item["path"]).resolve()
        require(path.is_relative_to(root.resolve()) and io_path(path).is_file(), f"content manifest file[{index}] missing/escaping")
        actual_hash = sha256_file(path)
        require(actual_hash == str(item["sha256"]).upper(), f"content manifest file[{index}] hash mismatch")
        require(io_path(path).stat().st_size == int(item["bytes"]), f"content manifest file[{index}] bytes mismatch")
        total += int(item["bytes"])
        rows.append(f"{item['path']}\0{int(item['bytes'])}\0{actual_hash}\n")
    tree = hashlib.sha256("".join(rows).encode("utf-8")).hexdigest().upper()
    require(len(rows) == int(manifest["payloadFileCount"]), "content payload file count mismatch")
    require(total == int(manifest["payloadBytes"]), "content payload bytes mismatch")
    require(tree == str(manifest["payloadTreeSha256"]).upper(), "content payload tree mismatch")
    return {"fileCount": len(rows), "totalBytes": total, "treeSha256": tree}


def verify_authority(config: dict[str, Any], full_inventory: bool) -> dict[str, Any]:
    checkpoint_path = checked_ref(config["checkpoint"], "authority.checkpoint")
    receipt_path = checked_ref(config["receipt"], "authority.receipt")
    dependency_path = checked_ref(config["dependencyManifest"], "authority.dependencyManifest")
    runtime_path = checked_ref(config["runtimeGate"], "authority.runtimeGate")
    checkpoint = load_json(checkpoint_path)
    receipt = load_json(receipt_path)
    dependency = load_json(dependency_path)
    runtime = load_json(runtime_path)
    require(checkpoint["admissionStatus"] == receipt["admissionStatus"] == "authority-admitted", "authority is not admitted")
    require(checkpoint["registry"]["registryVersion"] == config["registryVersion"], "authority version mismatch")
    require(checkpoint["registry"]["sha256"].upper() == config["registrySha256"].upper(), "authority registry hash mismatch")
    require(receipt["checkpoint"]["sha256"].upper() == sha256_file(checkpoint_path), "authority receipt checkpoint mismatch")
    require(checkpoint["release"]["dependencyManifestSha256"].upper() == receipt["release"]["dependencyManifestSha256"].upper() == sha256_file(dependency_path), "authority dependency manifest binding mismatch")
    require(checkpoint["runtimeGates"]["sha256"].upper() == receipt["runtimeGates"]["sha256"].upper() == sha256_file(runtime_path), "authority runtime gate binding mismatch")
    require(runtime["status"] == "passed" and runtime["workerRunInvocations"] == runtime["externalWrites"] == runtime["physicalSpeakerInvocations"] == 0, "authority runtime gate side-effect boundary mismatch")
    require(checkpoint["frozenV1"]["files"] == 21 and checkpoint["frozenV1"]["mismatches"] == 0, "authority frozen v1 mismatch")
    require(len(checkpoint["workers"]) == 5, "authority worker count mismatch")
    release_root = Path(checkpoint["release"]["root"]).resolve()
    expected = {
        "fileCount": int(config["releaseFiles"]),
        "totalBytes": int(config["releaseBytes"]),
        "treeSha256": config["releaseTreeSha256"].upper(),
    }
    if full_inventory:
        inventory = common.inventory(release_root)
        assert_inventory_summary(inventory, expected, f"Phase B authority v{config['registryVersion']}")
        actual = {key: inventory[key] for key in ("fileCount", "totalBytes", "treeSha256")}
    else:
        actual = expected
    workers = {item["workerId"]: item["workerVersion"] for item in checkpoint["workers"]}
    require(workers == {"image.ocr.mineru": "1.2.0", "content.deepseek": "0.6.1", "speech.edge": "1.2.2", "export.remnote": "2.0.0", "export.anki": "1.2.0"}, "authority worker version set mismatch")
    return {"status": "PASS-ADMITTED", "registryVersion": config["registryVersion"], "checkpoint": evidence(checkpoint_path), "receipt": evidence(receipt_path), "dependencyManifest": evidence(dependency_path), "runtimeGate": evidence(runtime_path), "release": actual, "workers": workers, "releaseRoot": str(release_root)}


def verify_invalidated_authority(config: dict[str, Any]) -> dict[str, Any]:
    invalidation_path = checked_ref(config["invalidationReceipt"], "invalidatedAuthority127.invalidationReceipt")
    invalidation = load_json(invalidation_path)
    require(invalidation["registryVersion"] == config["registryVersion"] == "1.2.7", "invalidated authority version mismatch")
    require(invalidation["status"] == "invalidated-never-promoted" and invalidation["authorityBound"] is False and invalidation["checkpointCreated"] is False, "v1.2.7 invalidation boundary mismatch")
    require(invalidation["replacement"]["minimumRegistryVersion"] == config["replacementMinimum"] and invalidation["replacement"]["rebuildFromOriginalFinalInputs"] is True, "v1.2.7 replacement policy mismatch")
    return {
        "status": "REJECTED-INVALIDATED-NEVER-PROMOTED",
        "registryVersion": config["registryVersion"],
        "invalidationReceipt": evidence(invalidation_path),
        "checkpointCreated": False,
        "authorityAdmitted": False,
        "eligibleForCandidate": False,
        "reason": invalidation["reason"],
    }


def verify_speech122(config: dict[str, Any]) -> dict[str, Any]:
    formal_root = resolve_path(config["formalRoot"])
    formal_path = checked_ref(config["formalReceipt"], "speech122.formalReceipt")
    independent_path = checked_ref(config["independentValidation"], "speech122.independentValidation")
    unit_path = checked_ref(config["unitReceipt"], "speech122.unitReceipt")
    public_path = checked_ref(config["publicReceipt"], "speech122.publicReceipt")
    deep_path = checked_ref(config["deepCanary"], "speech122.deepCanary")
    deep_public_path = checked_ref(config["deepPublicReceipt"], "speech122.deepPublicReceipt")
    canonical_path = checked_ref(config["canonicalReceipt"], "speech122.canonicalReceipt")
    canonical_validation_path = checked_ref(config["canonicalValidation"], "speech122.canonicalValidation")
    formal = load_json(formal_path)
    independent = load_json(independent_path)
    unit = load_json(unit_path)
    public = load_json(public_path)
    deep = load_json(deep_path)
    deep_public = load_json(deep_public_path)
    canonical = load_json(canonical_path)
    canonical_validation = load_json(canonical_validation_path)

    require(formal["status"] == independent["status"] == canonical["status"] == canonical_validation["status"] == "passed", "Speech 1.2.2 formal/canonical receipt status mismatch")
    require(formal["workerId"] == "speech.edge" and formal["workerVersion"] == config["workerVersion"], "Speech formal identity mismatch")
    require(independent["releaseReceiptSha256"].upper() == sha256_file(formal_path), "Speech independent/formal receipt binding mismatch")
    require(canonical_validation["releaseReceiptSha256"].upper() == sha256_file(canonical_path), "Speech canonical validation binding mismatch")
    require(unit["status"] == "passed" and unit["failures"] == 0 and len(unit["tests"]) == 43 and all(item["status"] == "passed" for item in unit["tests"]), "Speech 43/43 unit evidence mismatch")
    require(public["status"] == "passed" and deep_public["status"] == "passed", "Speech public contract evidence mismatch")
    formal_evidence = {item["kind"]: item for item in formal["evidence"]}
    require(formal_evidence["unit-tests"]["sha256"].upper() == sha256_file(unit_path), "Speech formal/unit evidence binding mismatch")
    require(formal_evidence["public-contracts"]["sha256"].upper() == sha256_file(public_path), "Speech formal/public evidence binding mismatch")

    rows: list[str] = []
    total = 0
    for item in sorted(formal["releaseTree"]["files"], key=lambda value: value["path"]):
        file_path = (formal_root / item["path"]).resolve()
        require(file_path.is_relative_to(formal_root) and io_path(file_path).is_file(), f"Speech formal file missing/escaping: {item['path']}")
        actual_hash = sha256_file(file_path)
        require(actual_hash == str(item["sha256"]).upper() == config["exeSha256"].upper(), "Speech formal EXE hash mismatch")
        require(io_path(file_path).stat().st_size == int(item["bytes"]), "Speech formal EXE bytes mismatch")
        total += int(item["bytes"])
        rows.append(f"{item['path']}|{int(item['bytes'])}|{actual_hash.lower()}")
    tree = hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper()
    require(len(rows) == formal["releaseTree"]["fileCount"] == 1 and total == formal["releaseTree"]["totalBytes"], "Speech formal tree count/bytes mismatch")
    require(tree == formal["releaseTree"]["treeSha256"].upper() == config["formalTreeSha256"].upper(), "Speech formal tree hash mismatch")
    require(independent["entrypointSha256"].upper() == canonical_validation["canonicalWorkerSha256"].upper() == config["exeSha256"].upper(), "Speech formal/canonical EXE mismatch")
    require(formal["workerRequestCanary"]["physicalSpeakerPlayback"] is False and formal["workerRequestCanary"]["networkTtsCalls"] == 0 and formal["workerRequestCanary"]["externalProductionWrites"] == 0, "Speech formal offline boundary mismatch")

    require(deep["deepPathRequired"] and deep["deepPathPassed"] and deep["physicalSpeakerPlayback"] is False and deep["oldAiTtsRouteCount"] == 0, "Speech deep Canary boundary mismatch")
    require(len(deep["results"]) == int(config["deepLanguageCount"]), "Speech deep language count mismatch")
    languages: list[dict[str, Any]] = []
    deep_root = deep_path.parent.resolve()
    for index, item in enumerate(deep["results"]):
        require(item["status"] == "completed" and item["deepPathVerified"] is True, f"Speech deep result[{index}] did not complete")
        require(int(item["audioFullPathLength"]) == int(config["deepPayloadPathLength"]), f"Speech deep result[{index}] recorded path length mismatch")
        audio_path = (deep_root / item["audioPath"]).resolve()
        result_path = (deep_root / item["resultPath"]).resolve()
        require(audio_path.is_relative_to(deep_root) and result_path.is_relative_to(deep_root), f"Speech deep result[{index}] escapes evidence root")
        require(io_path(audio_path).is_file() and len(logical_path_text(audio_path)) == int(config["deepPayloadPathLength"]), f"Speech deep result[{index}] actual payload path length mismatch")
        require(io_path(audio_path).stat().st_size == int(item["audioBytes"]) and sha256_file(audio_path) == str(item["audioSha256"]).upper(), f"Speech deep result[{index}] audio hash/bytes mismatch")
        job_id = Path(item["audioPath"]).parts[1]
        job_root = (deep_root / "jobs" / job_id).resolve()
        input_path = (job_root / item["inputTextPath"]).resolve()
        require(io_path(input_path).is_file() and sha256_file(input_path) == str(item["inputTextSha256"]).upper(), f"Speech deep result[{index}] input hash mismatch")
        require(io_path(input_path).read_text(encoding="utf-8") == item["inputText"], f"Speech deep result[{index}] input text mismatch")
        result = load_json(result_path)
        request_path = result_path.parent / "request.json"
        request = load_json(request_path)
        validate_common(request, "worker-request.schema.json", f"speech.deep[{index}].request")
        validate_common(result, "worker-result.schema.json", f"speech.deep[{index}].result")
        for key in ("taskId", "runId", "stageId", "attemptId", "requestId", "operationId"):
            require(request[key] == result[key], f"Speech deep result[{index}] request/result {key} mismatch")
        require(result["worker"] == {"id": "speech.edge", "version": config["workerVersion"]} and result["status"] == "completed", f"Speech deep result[{index}] worker identity/status mismatch")
        require(result["payload"]["cacheHit"] is False and result["payload"]["languageTag"] == item["language"] and result["payload"]["voiceId"] == item["voice"], f"Speech deep result[{index}] payload mismatch")
        artifacts = [verify_artifact(job_root, output, f"speech.deep[{index}].output[{output_index}]") for output_index, output in enumerate(result["outputs"])]
        require(len(artifacts) == 1 and artifacts[0]["files"][0]["sha256"] == sha256_file(audio_path), f"Speech deep result[{index}] artifact/audio mismatch")
        languages.append({"language": item["language"], "voice": item["voice"], "input": evidence(input_path), "request": evidence(request_path), "result": evidence(result_path), "audio": evidence(audio_path), "audioFullPathLength": len(logical_path_text(audio_path))})
    require({item["language"] for item in languages} == {"zh-CN", "en-US", "es-ES", "ja-JP", "ko-KR"}, "Speech deep language set mismatch")
    return {
        "status": "PASS-MODULE-DEEP-PATH-NOT-AUTHORITY-BOUND",
        "workerVersion": config["workerVersion"],
        "formalReceipt": evidence(formal_path),
        "independentValidation": evidence(independent_path),
        "unitReceipt": evidence(unit_path),
        "unitTests": 43,
        "publicReceipt": evidence(public_path),
        "formalRelease": {"fileCount": len(rows), "totalBytes": total, "treeSha256": tree, "entrypointSha256": config["exeSha256"].upper()},
        "deepCanary": evidence(deep_path),
        "deepPublicReceipt": evidence(deep_public_path),
        "deepLanguages": languages,
        "deepPayloadPathLength": int(config["deepPayloadPathLength"]),
        "physicalSpeakerPlayback": False,
        "canonicalReceipt": evidence(canonical_path),
        "canonicalValidation": evidence(canonical_validation_path),
        "authorityBound": False,
        "candidateBound": False,
    }


def verify_speech123(config: dict[str, Any]) -> dict[str, Any]:
    formal_root = resolve_path(config["formalRoot"])
    formal_path = checked_ref(config["formalReceipt"], "speech123.formalReceipt")
    independent_path = checked_ref(config["independentValidation"], "speech123.independentValidation")
    unit_path = checked_ref(config["unitReceipt"], "speech123.unitReceipt")
    public_path = checked_ref(config["publicReceipt"], "speech123.publicReceipt")
    canary_path = checked_ref(config["playbackCanary"], "speech123.playbackCanary")
    contract_path = checked_ref(config["playbackContractValidation"], "speech123.playbackContractValidation")
    formal = load_json(formal_path)
    independent = load_json(independent_path)
    unit = load_json(unit_path)
    public = load_json(public_path)
    canary = load_json(canary_path)
    contract = load_json(contract_path)

    require(formal["status"] == independent["status"] == unit["status"] == public["status"] == canary["status"] == contract["status"] == "passed", "Speech 1.2.3 receipt status mismatch")
    require(formal["workerId"] == "speech.edge" and formal["workerVersion"] == config["workerVersion"] == "1.2.3", "Speech 1.2.3 identity mismatch")
    require(independent["releaseReceiptSha256"].upper() == sha256_file(formal_path), "Speech 1.2.3 independent/formal binding mismatch")
    require(len(unit["tests"]) == int(config["unitTests"]) == 44 and unit["failures"] == 0 and all(item["status"] == "passed" for item in unit["tests"]), "Speech 1.2.3 unit evidence mismatch")
    formal_evidence = {item["kind"]: item for item in formal["evidence"]}
    require(formal_evidence["unit-tests"]["sha256"].upper() == sha256_file(unit_path), "Speech 1.2.3 formal/unit binding mismatch")
    require(formal_evidence["public-contracts"]["sha256"].upper() == sha256_file(public_path), "Speech 1.2.3 formal/public binding mismatch")

    rows: list[str] = []
    total = 0
    for item in sorted(formal["releaseTree"]["files"], key=lambda value: value["path"]):
        file_path = (formal_root / item["path"]).resolve()
        require(file_path.is_relative_to(formal_root) and io_path(file_path).is_file(), f"Speech 1.2.3 formal file missing/escaping: {item['path']}")
        actual_hash = sha256_file(file_path)
        require(actual_hash == str(item["sha256"]).upper() == config["exeSha256"].upper(), "Speech 1.2.3 formal EXE hash mismatch")
        require(io_path(file_path).stat().st_size == int(item["bytes"]), "Speech 1.2.3 formal EXE bytes mismatch")
        total += int(item["bytes"])
        rows.append(f"{item['path']}|{int(item['bytes'])}|{actual_hash.lower()}")
    tree = hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper()
    require(len(rows) == formal["releaseTree"]["fileCount"] == 1 and total == formal["releaseTree"]["totalBytes"], "Speech 1.2.3 formal tree count/bytes mismatch")
    require(tree == formal["releaseTree"]["treeSha256"].upper() == independent["releaseTreeSha256"].upper() == config["formalTreeSha256"].upper(), "Speech 1.2.3 formal tree hash mismatch")
    require(independent["entrypointSha256"].upper() == config["exeSha256"].upper(), "Speech 1.2.3 independent EXE mismatch")

    require(canary["canaryMode"] == contract["canaryMode"] == "physical" and canary["deepPathRequired"] is True and canary["deepPathPassed"] is True, "Speech 1.2.3 physical deep-path boundary mismatch")
    require(canary["workerExeSha256"].upper() == config["exeSha256"].upper(), "Speech 1.2.3 Canary EXE mismatch")
    require(len(canary["cases"]) == int(config["playbackCases"]) == 5, "Speech 1.2.3 playback case count mismatch")
    require(contract["requestsValidated"] == contract["resultsValidated"] == 6 and contract["artifactsValidated"] == 1, "Speech 1.2.3 contract validation counts mismatch")
    require(contract["freshAttemptsWithCacheHitFalse"] == 6 and contract["playbackResultsWithReuseAncestry"] == 0, "Speech 1.2.3 playback reuse boundary mismatch")

    canary_root = canary_path.parent.resolve()
    job_root = (canary_root / "jobs" / "tpc").resolve()
    synth_root = job_root / "runs" / "r1" / "stages" / "speech-synthesize" / "attempts" / "s1"
    synth_request_path = synth_root / "request.json"
    synth_result_path = synth_root / "result.json"
    synth_request = load_json(synth_request_path)
    synth_result = load_json(synth_result_path)
    validate_common(synth_request, "worker-request.schema.json", "speech123.synth.request")
    validate_common(synth_result, "worker-result.schema.json", "speech123.synth.result")
    require(synth_result["worker"] == {"id": "speech.edge", "version": config["workerVersion"]} and synth_result["status"] == "completed", "Speech 1.2.3 synthesis result mismatch")
    require(synth_result["payload"]["cacheHit"] is False and "reusedFromAttemptId" not in synth_result["payload"], "Speech 1.2.3 synthesis unexpectedly reused a result")
    synth_artifacts = [verify_artifact(job_root, output, f"speech123.synth.output[{index}]") for index, output in enumerate(synth_result["outputs"])]
    require(len(synth_artifacts) == 1 and len(synth_artifacts[0]["files"]) == 1, "Speech 1.2.3 synthesis artifact count mismatch")

    audio_path = (canary_root / canary["audio"]["path"]).resolve()
    require(audio_path.is_relative_to(canary_root) and io_path(audio_path).is_file(), "Speech 1.2.3 audio missing/escaping")
    audio_length = len(logical_path_text(audio_path))
    require(audio_length == int(canary["audio"]["absolutePathLength"]) == int(config["audioFullPathLength"]) and audio_length > 260, "Speech 1.2.3 actual audio path length mismatch")
    require(io_path(audio_path).stat().st_size == int(canary["audio"]["bytes"]) and sha256_file(audio_path) == str(canary["audio"]["sha256"]).upper(), "Speech 1.2.3 audio hash/bytes mismatch")
    require(synth_artifacts[0]["files"][0]["sha256"] == sha256_file(audio_path), "Speech 1.2.3 artifact/audio mismatch")

    expected = {
        "natural-completion": (None, "completed", "idle_replayable", None),
        "user-stop": ("user_stop", "cancelled", "stopped", "playback.user_stop"),
        "superseded": ("superseded", "cancelled", "superseded", "playback.superseded"),
        "task-cancel": ("task_cancel", "cancelled", "cancelled", "playback.task_cancel"),
        "timeout": ("timeout", "failed", "timed_out", "playback.timeout"),
    }
    attempts: list[dict[str, Any]] = []
    attempt_ids: set[str] = set()
    for index, item in enumerate(canary["cases"]):
        require(item["case"] in expected and (item["cancellationReason"], item["status"], item["playbackState"], item["errorCode"]) == expected[item["case"]], f"Speech 1.2.3 playback case[{index}] semantics mismatch")
        request_path = (canary_root / item["requestPath"]).resolve()
        result_path = (canary_root / item["resultPath"]).resolve()
        require(request_path.is_relative_to(canary_root) and result_path.is_relative_to(canary_root), f"Speech 1.2.3 playback case[{index}] escapes Canary root")
        request = load_json(request_path)
        result = load_json(result_path)
        validate_common(request, "worker-request.schema.json", f"speech123.playback[{index}].request")
        validate_common(result, "worker-result.schema.json", f"speech123.playback[{index}].result")
        for key in ("taskId", "runId", "stageId", "attemptId", "requestId", "operationId"):
            require(request[key] == result[key], f"Speech 1.2.3 playback case[{index}] request/result {key} mismatch")
        require(request["operation"] == "speech.playback" and result["worker"] == {"id": "speech.edge", "version": config["workerVersion"]}, f"Speech 1.2.3 playback case[{index}] identity mismatch")
        require(result["status"] == item["status"] and result["payload"]["cacheHit"] is False and "reusedFromAttemptId" not in result["payload"], f"Speech 1.2.3 playback case[{index}] result reuse/status mismatch")
        require(request["attemptId"] not in attempt_ids, f"Speech 1.2.3 duplicate playback attemptId: {request['attemptId']}")
        attempt_ids.add(request["attemptId"])
        attempts.append({"case": item["case"], "request": evidence(request_path), "result": evidence(result_path), "attemptId": request["attemptId"], "status": result["status"], "cacheHit": False})

    return {
        "status": "PASS-MODULE-DEEP-PLAYBACK-NOT-AUTHORITY-BOUND",
        "workerVersion": config["workerVersion"],
        "formalReceipt": evidence(formal_path),
        "independentValidation": evidence(independent_path),
        "unitReceipt": evidence(unit_path),
        "unitTests": len(unit["tests"]),
        "publicReceipt": evidence(public_path),
        "formalRelease": {"fileCount": len(rows), "totalBytes": total, "treeSha256": tree, "entrypointSha256": config["exeSha256"].upper()},
        "playbackCanary": evidence(canary_path),
        "playbackContractValidation": evidence(contract_path),
        "audio": evidence(audio_path) | {"absolutePathLength": audio_length},
        "attempts": attempts,
        "physicalSpeakerPlaybackInSourceEvidence": True,
        "authorityBound": False,
        "candidateBound": False,
    }


def verify_desktop(config: dict[str, Any], authority: dict[str, Any], full_inventory: bool) -> dict[str, Any]:
    checkpoint_path = checked_ref(config["checkpoint"], "desktop.checkpoint")
    base_path = checked_ref(config["baseReceipt"], "desktop.baseReceipt")
    checkpoint = load_json(checkpoint_path)
    require(checkpoint["status"].startswith("PASS-"), "Desktop module checkpoint did not pass")
    require(checkpoint["baseReceipt"]["sha256"].upper() == sha256_file(base_path), "Desktop base receipt mismatch")
    require(checkpoint["authority"]["version"] == config["embeddedAuthorityVersion"], "Desktop embedded authority version mismatch")
    require(config["targetAuthorityVersion"] == authority["registryVersion"], "Desktop target authority version mismatch")
    require(checkpoint["authority"]["version"] != authority["registryVersion"], "stale Desktop checkpoint was unexpectedly target-authority-bound")
    require(checkpoint["authority"]["embeddedExactly"] is True, "Desktop authority was not embedded exactly")
    require(checkpoint["releaseBoundary"]["classification"] == "module-checkpoint", "Desktop boundary is not module checkpoint")
    require(checkpoint["releaseBoundary"]["userHandTestEntrypoint"] is False, "Desktop module checkpoint was promoted to user entrypoint")
    require(config["integratedReady"] is False, "Desktop pending fixture was promoted without an integrated target-authority receipt")
    entrypoint = resolve_path(checkpoint["entrypoint"])
    require(sha256_file(entrypoint) == checkpoint["entrypointSha256"].upper(), "Desktop entrypoint hash mismatch")
    candidate_root = entrypoint.parent
    expected = {
        "fileCount": int(config["candidateFiles"]),
        "totalBytes": int(config["candidateBytes"]),
        "treeSha256": config["candidateTreeSha256"].upper(),
    }
    if full_inventory:
        inventory = common.inventory(candidate_root)
        assert_inventory_summary(inventory, expected, "Desktop settings module checkpoint")
        actual = {key: inventory[key] for key in ("fileCount", "totalBytes", "treeSha256")}
    else:
        actual = expected
    return {
        "status": "PENDING-DESKTOP-V1.2.8-INTEGRATED-USER-ENTRYPOINT",
        "checkpoint": evidence(checkpoint_path),
        "baseReceipt": evidence(base_path),
        "candidate": actual,
        "entrypoint": evidence(entrypoint),
        "userHandTestEntrypoint": False,
        "embeddedAuthorityVersion": config["embeddedAuthorityVersion"],
        "targetAuthorityVersion": config["targetAuthorityVersion"],
        "targetAuthorityBound": False,
    }


def verify_content061(config: dict[str, Any]) -> dict[str, Any]:
    manifest_path = checked_ref(config["manifest"], "content061.manifest")
    offline_path = checked_ref(config["offlineReceipt"], "content061.offlineReceipt")
    live_path = checked_ref(config["liveReceipt"], "content061.liveReceipt")
    manifest = load_json(manifest_path)
    offline = load_json(offline_path)
    live = load_json(live_path)
    require(manifest["worker"]["workerId"] == "content.deepseek" and manifest["worker"]["workerVersion"] == config["workerVersion"], "Content 0.6.1 manifest identity mismatch")
    tree = recompute_manifest_tree(manifest_path.parent, manifest)
    require(tree["fileCount"] == int(config["payloadFiles"]) and tree["treeSha256"] == config["payloadTreeSha256"].upper(), "Content 0.6.1 expected payload mismatch")
    require(offline["status"] == "PASS" and offline["worker"] == {"id": "content.deepseek", "version": "0.6.1"}, "Content formal offline receipt mismatch")
    require(offline["settingsSnapshotReplay"]["externalConnections"] == 0 and offline["settingsSnapshotReplay"]["credentialsRecorded"] is False, "Content offline boundary mismatch")
    require(offline["release"]["manifest"]["sha256"].upper() == sha256_file(manifest_path), "Content offline manifest binding mismatch")
    require(live["status"] == "completed" and live["mode"] == "live" and live["moduleReleaseState"] == "ready", "Content live receipt mismatch")
    require(live["externalConnectionEvidence"]["credentialRecorded"] is False and len(live["providerEvidence"]) > 0, "Content live provider evidence missing/credentialed")
    result_path = Path(live["resultPath"]).resolve()
    require(sha256_file(result_path) == live["resultSha256"].upper(), "Content live result hash mismatch")
    result = load_json(result_path)
    validate_common(result, "worker-result.schema.json", "Content 0.6.1 live result")
    require(result["worker"] == {"id": "content.deepseek", "version": "0.6.1"}, "Content live result identity mismatch")
    return {
        "status": "PASS-MODULE-NOT-AUTHORITY-BOUND",
        "manifest": evidence(manifest_path),
        "payload": tree,
        "offlineReceipt": evidence(offline_path),
        "liveReceipt": evidence(live_path),
        "liveProviderCalls": len(live["providerEvidence"]),
        "liveResult": evidence(result_path),
        "workerVersion": "0.6.1",
    }


def verify_core_resolver(config: dict[str, Any]) -> dict[str, Any]:
    checkpoint_path = checked_ref(config["checkpoint"], "coreResolver.checkpoint")
    superseded_path = checked_ref(config["supersededCheckpoint"], "coreResolver.supersededCheckpoint")
    regression_path = checked_ref(config["regressionReceipt"], "coreResolver.regressionReceipt")
    checkpoint = load_json(checkpoint_path)
    superseded = load_json(superseded_path)
    require(checkpoint["checkpointVersion"] == config["checkpointVersion"] == "2.4.5", "Core resolver checkpoint version mismatch")
    require(checkpoint["status"] == "passed-authority-admission-ready", "Core authority admission checkpoint status mismatch")
    require(resolve_path(checkpoint["supersedes"]) == superseded_path, "Core admission checkpoint supersedes binding mismatch")
    behavior = superseded["behavior"]
    require(behavior["learningIntentRequestedOutput"] == "learning-bundle", "Core learning resolver output mismatch")
    require(behavior["remnoteRoute"]["kind"] == "clip-daily-route" and behavior["planPayloadBound"] and behavior["workerRequestPayloadBound"], "Core learning-to-RemNote route binding mismatch")
    require(behavior["ankiStage"] is False and behavior["workerPathValidationRelaxed"] is False, "Core resolver stage/layout boundary mismatch")
    regression = load_json(regression_path)
    require(superseded["validation"]["orchestrator"]["tests"] == int(config["expectedTests"]), "Core regression test count mismatch")
    require(checkpoint["authorityBoundary"]["authorityBound"] is False and checkpoint["authorityBoundary"]["intendedAuthority"] == "1.2.7", "Core admission authority boundary drifted")
    require(checkpoint["speech"]["worker"] == "speech.edge@1.2.2" and checkpoint["content"]["worker"] == "content.deepseek@0.6.1", "Core admission module identities mismatch")
    require(checkpoint["speech"]["formalReceiptSha256"].upper() == "A4C3AD17A3AB5837CE1294CD0ADA4E85C7C7F341F32BCB636C1E810A43B028BF", "Core admission Speech receipt binding mismatch")
    require(checkpoint["content"]["manifestSha256"].upper() == "B716E6476D34D325322F6C8930819AAEDD33E06FAFBE50F883E22ACDA10D39E9", "Core admission Content manifest binding mismatch")
    files: list[dict[str, Any]] = []
    for item in checkpoint["files"]:
        path = resolve_path(item["path"])
        require(io_path(path).is_file() and sha256_file(path) == str(item["sha256"]).upper() and io_path(path).stat().st_size == int(item["bytes"]), f"Core admission evidence drift: {item['path']}")
        files.append(evidence(path))
    return {
        "status": "PASS-ADMISSION-READY-NOT-AUTHORITY-BOUND",
        "checkpoint": evidence(checkpoint_path),
        "supersededCheckpoint": evidence(superseded_path),
        "regressionReceipt": evidence(regression_path),
        "tests": int(config["expectedTests"]),
        "files": files,
        "metadataWarning": None,
        "intendedAuthority": "1.2.7",
    }


def verify_historical_failure(config: dict[str, Any]) -> dict[str, Any]:
    execution_path = checked_ref(config["executionReceipt"], "historical.executionReceipt")
    execution = load_json(execution_path)
    historical_authority = {"registryVersion": config["authorityVersion"], "registrySha256": config["authorityRegistrySha256"]}
    require(execution["authority"]["registryVersion"] == historical_authority["registryVersion"], "historical authority version mismatch")
    require(execution["authority"]["registrySha256"].upper() == historical_authority["registrySha256"].upper(), "historical authority hash mismatch")
    require(execution["isolatedDestinations"]["remnote"]["formalClipRootUsed"] is False, "historical run used formal RemNote root")
    require(execution["isolatedDestinations"]["anki"]["existingDeckUsed"] is False, "historical run used existing Anki deck")
    runs = {item["path"]: item for item in execution["runs"]}
    require(runs["B"]["exitCode"] != 0 and runs["B"]["receipt"] is None, "historical B failure evidence drifted")
    path_results: dict[str, Any] = {}
    for name, key in (("A", "pathAReceipt"), ("C", "pathCReceipt"), ("D", "pathDReceipt")):
        receipt_path = checked_ref(config[key], f"historical.path{name}")
        result = verify_path_receipt(receipt_path, historical_authority, "failed")
        path_results[name] = result
    require(any(item["worker"]["version"] == config["contentWorkerVersion"] and item["error"] and item["error"]["code"] == "content.gate-failed" for item in path_results["A"]["attempts"]), "historical Content 0.6.0 gate failure missing")
    d_stderr_codes: list[str] = []
    for item in path_results["D"]["attempts"]:
        if item["stageId"] == "speech-synthesize" and item["error"]:
            details = item["error"].get("detailsRef")
            if details:
                path = Path(path_results["D"]["jobRoot"]) / details
                if io_path(path).is_file():
                    try:
                        d_stderr_codes.append(load_json(path)["error"]["code"])
                    except (json.JSONDecodeError, KeyError):
                        pass
    require("task_path_mismatch" in d_stderr_codes, "historical standard-root speech failure detail missing")
    return {
        "status": "HISTORICAL-FAILED-IMMUTABLE",
        "executionReceipt": evidence(execution_path),
        "isolatedDestinations": execution["isolatedDestinations"],
        "pathStatuses": {"A": "failed", "B": "process-failed-no-receipt", "C": "failed", "D": "failed"},
        "pathEvidence": path_results,
        "speechFailureCodes": sorted(set(d_stderr_codes)),
    }


def verify_current_c(config: dict[str, Any], authority_config: dict[str, Any]) -> dict[str, Any]:
    receipt_path = checked_ref(config["receipt"], "current.C.receipt")
    readable_path = checked_ref(config["readable"], "current.C.readable")
    result = verify_path_receipt(receipt_path, authority_config, "succeeded")
    require(result["plan"]["routing"]["destinations"] == [], "Path C unexpectedly has a destination")
    workers = {item["worker"]["id"]: item for item in result["attempts"]}
    require(workers["image.ocr.mineru"]["status"] == workers["content.deepseek"]["status"] == "completed", "Path C provider chain incomplete")
    image_attempt_dir = Path(workers["image.ocr.mineru"]["result"]["path"])
    if not image_attempt_dir.is_absolute():
        image_attempt_dir = PROJECT_ROOT / image_attempt_dir
    provider_path = image_attempt_dir.parent / "provider-result.redacted.json"
    provider = load_json(provider_path)
    require(provider["provider"] == "mineru" and all(item["state"] == "done" for item in provider["items"]), "Path C MinerU readback incomplete")
    learning_files = [Path(file["path"]) for item in workers["content.deepseek"]["artifacts"] if item["reference"]["kind"] == "learning-bundle" for file in item["files"]]
    require(len(learning_files) == 1, "Path C learning bundle missing")
    learning_path = learning_files[0] if learning_files[0].is_absolute() else PROJECT_ROOT / learning_files[0]
    learning = load_json(learning_path)
    traces = [item for item in learning["ai_trace"] if item["provider"] == "deepseek"]
    require(traces and all(len(item["response_sha256"]) == 64 for item in traces), "Path C DeepSeek trace missing")
    return {
        **result,
        "status": f"PASS-SCOPED-AUTHORITY-V{authority_config['registryVersion']}-REAL-MINERU-DEEPSEEK-NO-DESTINATION",
        "readable": evidence(readable_path),
        "externalReadback": {"mineruItemsDone": len(provider["items"]), "deepSeekTraceCount": len(traces)},
        "idempotency": "not-applicable-no-destination-side-effect",
        "finalAuthorityRebindRequired": True,
    }


def verify_current_d(config: dict[str, Any], authority_config: dict[str, Any], historical: dict[str, Any]) -> dict[str, Any]:
    receipt_path = checked_ref(config["receipt"], "current.D.receipt")
    readable_path = checked_ref(config["readable"], "current.D.readable")
    result = verify_path_receipt(receipt_path, authority_config, "succeeded")
    require(result["plan"]["routing"]["destinations"] == [], "Path D unexpectedly has a destination")
    synth = [item for item in result["attempts"] if item["stageId"] == "speech-synthesize"]
    playback = [item for item in result["attempts"] if item["stageId"] == "speech-playback"]
    require(len(synth) == 1 and len(playback) == 2, "Path D synth/playback attempt count mismatch")
    require(all(item["status"] == "completed" for item in synth + playback), "Path D attempt did not complete")
    require(len({item["attemptId"] for item in playback}) == 2 and all(item["payload"].get("cacheHit") is False for item in playback), "Path D playback was reused/cached")
    require(result["replay"] and result["replay"]["status"] == "succeeded" and result["replay"]["operation"] == "speech.playback", "Path D replay evidence missing")
    audio = [artifact for item in synth for artifact in item["artifacts"] if artifact["reference"]["kind"] == "audio.speech"]
    require(len(audio) == 1 and audio[0]["files"][0]["bytes"] > 0, "Path D audio output missing")
    short_root = receipt_path.parent
    short_lengths = [len(logical_path_text(path.resolve())) for path in walk_files(short_root)]
    historical_root = resolve_path(historical["executionReceipt"]["path"]).parent / "path-d"
    historical_lengths = [len(logical_path_text(path.resolve())) for path in walk_files(historical_root)]
    require(max(short_lengths) <= 260, "Path D evidence is not actually a short-root run")
    require(any(value > 260 for value in historical_lengths), "historical standard-root D does not contain >260 path evidence")
    return {
        **result,
        "status": "CONDITIONAL-SHORT-ROOT-PHYSICAL-PLAYBACK-STANDARD-LONG-ROOT-PENDING",
        "readable": evidence(readable_path),
        "audio": audio[0],
        "playbackAttempts": len(playback),
        "workerStarts": result["invocationCounts"]["speech.edge"],
        "pathLengthEvidence": {"shortRootMax": max(short_lengths), "historicalStandardRootMax": max(historical_lengths)},
        "idempotency": "playback-replay-created-new-attempt",
        "finalAuthorityRebindRequired": True,
    }


def verify_settings_snapshot_chain(path_result: dict[str, Any]) -> dict[str, Any]:
    job_root = Path(path_result["jobRoot"]).resolve()
    manifest_path = job_root / "task.manifest.v2.json"
    plan_path = job_root / "plans" / "resolved" / "run-initial" / "pipeline.v2.json"
    require(io_path(manifest_path).is_file() and io_path(plan_path).is_file(), f"Path {path_result['path']}: task manifest/resolved plan missing")
    manifest = load_json(manifest_path)
    plan = load_json(plan_path)
    snapshot_dirs = [path for path in io_path(job_root / "artifacts").iterdir() if path.is_dir() and path.name.startswith("task-settings-snapshot-")]
    require(len(snapshot_dirs) == 1, f"Path {path_result['path']}: expected exactly one task settings snapshot artifact")
    snapshot_dir = snapshot_dirs[0]
    artifact_path = snapshot_dir / "artifact.json"
    snapshot_path = snapshot_dir / "task-settings-snapshot.v1.json"
    require(io_path(artifact_path).is_file() and io_path(snapshot_path).is_file(), f"Path {path_result['path']}: settings snapshot files missing")
    artifact = load_json(artifact_path)
    snapshot = load_json(snapshot_path)
    validate_common(artifact, "artifact.schema.json", f"Path {path_result['path']}.settingsArtifact")
    validate_project_schema(snapshot, PROJECT_ROOT / "contracts" / "core" / "v2.4" / "settings-control" / "schemas" / "task-settings-snapshot.v2.schema.json", f"Path {path_result['path']}.settingsSnapshot")
    require(snapshot["taskId"] == manifest["taskId"] == plan["taskId"] == path_result["taskId"], f"Path {path_result['path']}: settings task identity mismatch")
    input_refs = {item["artifactRef"]: item for item in manifest["inputRefs"]}
    snapshot_ref = manifest["metadata"]["settingsSnapshotArtifactRef"]
    require(snapshot_ref == snapshot_dir.name and snapshot_ref in input_refs, f"Path {path_result['path']}: settings snapshot manifest reference mismatch")
    require(input_refs[snapshot_ref]["expectedSha256"].upper() == sha256_file(artifact_path), f"Path {path_result['path']}: settings artifact hash binding mismatch")
    artifact_files = {item["path"]: item for item in artifact["files"]}
    snapshot_relative = f"artifacts/{snapshot_dir.name}/task-settings-snapshot.v1.json"
    require(snapshot_relative in artifact_files, f"Path {path_result['path']}: settings snapshot payload not listed")
    require(artifact_files[snapshot_relative]["sha256"].upper() == sha256_file(snapshot_path) and int(artifact_files[snapshot_relative]["bytes"]) == io_path(snapshot_path).stat().st_size, f"Path {path_result['path']}: settings snapshot payload hash/bytes mismatch")
    require(plan["routing"]["source"] == "knowledge-clipper-task-settings-snapshot.v2", f"Path {path_result['path']}: resolved plan did not cite settings snapshot")
    require(plan["routing"]["processingProfile"] == snapshot["workflow"]["processingProfile"] and plan["routing"]["destinations"] == snapshot["workflow"]["destinations"], f"Path {path_result['path']}: settings workflow/plan mismatch")
    plan_stages = {item["stageId"]: item for item in plan["stages"]}
    for attempt in path_result["attempts"]:
        stage = plan_stages[attempt["stageId"]]
        request = load_json(resolve_path(attempt["request"]["path"]))
        require(request["workerId"] == stage["workerId"] and request["operation"] == stage["operation"], f"Path {path_result['path']}.{attempt['stageId']}: plan/request identity mismatch")
        require(request["configSnapshot"] == stage["configSnapshot"], f"Path {path_result['path']}.{attempt['stageId']}: plan/request config snapshot mismatch")
        require(all(request["payload"].get(key) == value for key, value in stage["payload"].items()), f"Path {path_result['path']}.{attempt['stageId']}: plan/request payload mismatch")
    content_stage = plan_stages.get("content.understand-and-card")
    if content_stage:
        require(content_stage["configSnapshot"] == {"promptSet": snapshot["modules"]["content"]["promptSet"], "categoryRegistry": snapshot["modules"]["content"]["categoryRegistry"]}, f"Path {path_result['path']}: Content settings were not projected to plan")
        require(content_stage["payload"]["providerMode"] == snapshot["modules"]["content"]["providerMode"], f"Path {path_result['path']}: Content provider mode mismatch")
    remnote_stage = plan_stages.get("export-remnote")
    if remnote_stage:
        require(remnote_stage["payload"]["route"] == snapshot["modules"]["remnote"]["route"], f"Path {path_result['path']}: RemNote route mismatch")
        require(remnote_stage["payload"]["target"] == snapshot["modules"]["remnote"]["settings"]["target"], f"Path {path_result['path']}: RemNote target mismatch")
    speech_stage = plan_stages.get("speech-synthesize")
    if speech_stage:
        speech_settings = snapshot["modules"]["speech"]
        voice_snapshot = speech_stage["payload"]["voiceSnapshot"]
        require(voice_snapshot["voicesByLanguage"] == speech_settings["voicesByLanguage"], f"Path {path_result['path']}: Speech voices mismatch")
        for key in ("ratePercent", "volumePercent", "pitchHz"):
            require(voice_snapshot[key] == speech_settings[key], f"Path {path_result['path']}: Speech {key} mismatch")
        require(speech_stage["payload"]["playbackPolicy"]["autoPlay"] == speech_settings["autoPlay"], f"Path {path_result['path']}: Speech autoPlay mismatch")
    require(snapshot["secretReferences"] == [] and all(value.get("secretReferences", []) == [] for value in snapshot["modules"].values() if isinstance(value, dict)), f"Path {path_result['path']}: secret references leaked into task snapshot")
    return {"manifest": evidence(manifest_path), "snapshotArtifact": evidence(artifact_path), "snapshot": evidence(snapshot_path), "resolvedPlan": evidence(plan_path), "status": "PASS"}


def verify_phase_b128_attempt(config: dict[str, Any], authority_config: dict[str, Any]) -> dict[str, Any]:
    execution_path = checked_ref(config["executionReceipt"], "phaseB128.executionReceipt")
    execution = load_json(execution_path)
    require(execution["authority"]["registryVersion"] == authority_config["registryVersion"] == "1.2.8", "Phase B v1.2.8 authority version mismatch")
    require(execution["authority"]["registrySha256"].upper() == authority_config["registrySha256"].upper(), "Phase B v1.2.8 registry hash mismatch")
    require(execution["authority"]["checkpointSha256"].upper() == authority_config["checkpoint"]["sha256"].upper(), "Phase B v1.2.8 checkpoint hash mismatch")
    require(execution["isolatedDestinations"]["remnote"]["formalClipRootUsed"] is False and execution["isolatedDestinations"]["anki"]["existingDeckUsed"] is False, "Phase B v1.2.8 used a formal destination")
    require(execution["secretsWritten"] == 0 and execution["mineruCredential"]["secretPersisted"] is False, "Phase B v1.2.8 persisted a credential")
    run_rows = {item["path"]: item for item in execution["runs"]}
    require(set(run_rows) == {"A", "B", "C", "D"} and all(item["exitCode"] == 0 for item in run_rows.values()), "Phase B v1.2.8 process execution receipt mismatch")
    paths: dict[str, Any] = {}
    for name in ("A", "B", "C", "D"):
        receipt_path = checked_ref(config[f"path{name}Receipt"], f"phaseB128.path{name}Receipt")
        readable_path = checked_ref(config[f"path{name}Readable"], f"phaseB128.path{name}Readable")
        require(Path(run_rows[name]["receipt"]).resolve() == receipt_path, f"Phase B v1.2.8 path {name} execution binding mismatch")
        result = verify_path_receipt(receipt_path, authority_config, config["expectedStatus"])
        result["readable"] = evidence(readable_path)
        result["settingsSnapshotChain"] = verify_settings_snapshot_chain(result)
        paths[name] = result
    codes = {name: [(item["stageId"], item["status"], item["error"]["code"] if item["error"] else None) for item in result["attempts"]] for name, result in paths.items()}
    require(("anki-export", "unknown_external_state", "effect.worker_exit_unknown") in codes["A"], "Phase B v1.2.8 A Anki failure evidence drifted")
    require(("export-remnote", "failed", "binding.remnote_inline_unsupported") in codes["B"], "Phase B v1.2.8 B RemNote binding failure evidence drifted")
    require(("anki-export", "unknown_external_state", "effect.worker_exit_unknown") in codes["C"], "Phase B v1.2.8 C Anki failure evidence drifted")
    require(("export-remnote", "unknown_external_state", "effect.commit_unknown") in codes["D"], "Phase B v1.2.8 D RemNote unknown-state evidence drifted")
    d_playback = [item for item in paths["D"]["attempts"] if item["stageId"] == "speech-playback"]
    require(len(d_playback) == 2 and all(item["status"] == "failed" and item["error"]["code"] == "playback_failed" for item in d_playback), "Phase B v1.2.8 D long-path playback failure evidence drifted")
    require(paths["D"]["replay"] and paths["D"]["replay"]["status"] == "failed" and paths["D"]["invocationCounts"]["speech.edge"] == 3, "Phase B v1.2.8 D replay failure evidence drifted")
    d_lengths = [len(logical_path_text(path.resolve())) for path in walk_files(Path(paths["D"]["jobRoot"]))]
    require(max(d_lengths) > 260, "Phase B v1.2.8 D is not a standard/deep-root failure")
    remnote_readbacks: dict[str, Any] = {}
    for name in ("A", "C"):
        completed = [item for item in paths[name]["attempts"] if item["stageId"] == "export-remnote" and item["status"] == "completed"]
        require(len(completed) == 1, f"Phase B v1.2.8 {name} RemNote completed evidence missing")
        receipts = [file for artifact in completed[0]["artifacts"] for file in artifact["files"] if Path(file["path"]).name == "receipt.json"]
        require(len(receipts) == 1, f"Phase B v1.2.8 {name} RemNote write receipt missing")
        readback = load_json(resolve_path(receipts[0]["path"]))
        require(readback["rootRemId"] == execution["isolatedDestinations"]["remnote"]["rootRemId"] and readback["readbackVerified"] is True and readback["outcome"] == "created", f"Phase B v1.2.8 {name} RemNote isolated readback mismatch")
        remnote_readbacks[name] = {"receipt": receipts[0], "outcome": readback["outcome"], "documentRemId": readback["documentRemId"], "readbackVerified": True}
    return {
        "status": "NO-GO-CANDIDATE-BOUND-PARTIAL",
        "executionReceipt": evidence(execution_path),
        "authorityVersion": "1.2.8",
        "paths": paths,
        "failureMatrix": codes,
        "isolatedDestinations": execution["isolatedDestinations"],
        "remnoteScopedReadbacks": remnote_readbacks,
        "standardRootMaxPath": max(d_lengths),
        "finalQualification": False,
    }


def preview(value: Any, limit: int = 600) -> str:
    source = value if isinstance(value, str) else json.dumps(value, ensure_ascii=False, sort_keys=True)
    text = " ".join(source.split())
    return text[:limit] + ("…" if len(text) > limit else "")


def render_index(receipt: dict[str, Any]) -> str:
    paths = receipt["paths"]
    return f"""# Phase B candidate quality index

- Overall: `{receipt['status']}`
- Authority: v{receipt['authority']['registryVersion']} is admitted and its release tree was byte-recomputed.
- Rejected authority: v{receipt['invalidatedAuthority127']['registryVersion']} is `{receipt['invalidatedAuthority127']['status']}` and cannot enter any candidate.
- Phase B run: `{receipt['phaseB128']['status']}`; all A/B/C/D evidence below is bound to v1.2.8 and was regenerated from original inputs.
- Desktop: `{receipt['desktop']['status']}`.
- Quality-gate external calls in this run: `0`

## Original input -> readable output

### A — normal text -> dual export

- Current: `{paths['A']['status']}`
- Original input: {preview(paths['A']['input'])}
- Readable output: `{paths['A']['readable']['path']}`
- Observed: Content and isolated RemNote completed/read back; Anki ended `unknown_external_state` with `WRONG_EXECUTION_PROFILE`. No successful idempotent replay exists.

### B — language cards -> dual export

- Current: `{paths['B']['status']}`
- Original input: {preview(paths['B']['input'].get('text', ''))}
- Readable output: `{paths['B']['readable']['path']}`
- Observed: Content completed; RemNote projection failed with `binding.remnote_inline_unsupported`. No destination readback/replay qualifies.

### C — image -> MinerU -> DeepSeek

- Current: `{paths['C']['status']}`
- Original input: {preview(json.dumps(paths['C']['input'], ensure_ascii=False))}
- Readable output: `{paths['C']['readable']['path']}`
- Observed: MinerU, Content and isolated RemNote completed/read back; Anki ended `unknown_external_state` with `WRONG_EXECUTION_PROFILE`. No successful idempotent replay exists.

### D — cleaned spoken text -> Edge speech -> playback replay

- Current: `{paths['D']['status']}`
- Original input: {preview(json.dumps(paths['D']['input'], ensure_ascii=False))}
- Readable output: `{paths['D']['readable']['path']}`
- Observed: synthesis completed, but two standard-root playback attempts failed with `playback_failed`; replay failed; RemNote was `unknown_external_state`; Anki used the wrong execution profile.
- Standard-root evidence max path length: `{receipt['phaseB128']['standardRootMaxPath']}`. Speech 1.2.3 independently proves physical playback at audio path length `{receipt['speech123']['audio']['absolutePathLength']}`, but v1.2.8 still embeds Speech 1.2.2 and its failed D run remains immutable; the newer module proof is not authority/candidate-bound.

## Pending / NO-GO boundaries

""" + "\n".join(f"- {item}" for item in receipt["pending"]) + f"""

## Repeat command

```powershell
python tools/verify_phase_b_candidate_gate.py --fixture tests/Quality.Gates/fixtures/phase-b-candidate/valid/phase-b-v1.2.8.json --emit-root <new-append-only-root>
```
"""


def evaluate(fixture: dict[str, Any], full_inventory: bool = True) -> dict[str, Any]:
    policy = fixture["qualificationPolicy"]
    require(policy == {
        "standardLongRootRequired": True,
        "shortRootMayQualify": False,
        "contentFormalReleaseMayCloseHistoricalFailure": False,
        "desktopModuleCheckpointMayBeUserEntrypoint": False,
    }, "Phase B qualification policy was weakened")
    authority = verify_authority(fixture["authority"], full_inventory)
    invalidated_authority = verify_invalidated_authority(fixture["invalidatedAuthority127"])
    desktop = verify_desktop(fixture["desktop"], authority, full_inventory)
    content = verify_content061(fixture["content061"])
    speech = verify_speech122(fixture["speech122"])
    speech123 = verify_speech123(fixture["speech123"])
    core = verify_core_resolver(fixture["coreResolver"])
    require(authority["workers"]["content.deepseek"] == content["workerVersion"], "Content 0.6.1 is not bound to admitted authority")
    require(authority["workers"]["speech.edge"] == speech["workerVersion"], "Speech 1.2.2 is not bound to admitted authority")
    content["status"] = "PASS-MODULE-AND-AUTHORITY-BOUND-NOT-PHASE-B-FINAL"
    content["authorityBound"] = True
    speech["status"] = "PASS-MODULE-DEEP-PATH-AND-AUTHORITY-BOUND-NOT-PHASE-B-FINAL"
    speech["authorityBound"] = True
    core["status"] = "PASS-AUTHORITY-BOUND-NOT-PHASE-B-FINAL"
    core["authorityBound"] = True
    historical = verify_historical_failure(fixture["historicalFailure"])
    historical_authority = {"registryVersion": fixture["historicalFailure"]["authorityVersion"], "registrySha256": fixture["historicalFailure"]["authorityRegistrySha256"]}
    prior_c = verify_current_c(fixture["currentPaths"]["C"], historical_authority)
    prior_d = verify_current_d(fixture["currentPaths"]["D"], historical_authority, historical)
    phase_b = verify_phase_b128_attempt(fixture["phaseB128"], fixture["authority"])
    paths = phase_b["paths"]
    require(all(item["status"] == "partial" for item in paths.values()), "partial Phase B evidence was improperly promoted")
    scan_roots = [
        resolve_path(fixture["historicalFailure"]["root"]),
        checked_ref(fixture["currentPaths"]["C"]["receipt"], "scan.C").parent,
        checked_ref(fixture["currentPaths"]["D"]["receipt"], "scan.D").parent,
        checked_ref(fixture["content061"]["manifest"], "scan.contentManifest").parent,
        checked_ref(fixture["content061"]["offlineReceipt"], "scan.contentOffline").parent,
        checked_ref(fixture["content061"]["liveReceipt"], "scan.contentLive").parent,
        resolve_path(fixture["speech122"]["formalRoot"]).parent,
        checked_ref(fixture["speech122"]["deepCanary"], "scan.speechDeep").parent,
        checked_ref(fixture["speech122"]["canonicalReceipt"], "scan.speechCanonical").parent,
        resolve_path(fixture["speech123"]["formalRoot"]).parent,
        checked_ref(fixture["speech123"]["playbackCanary"], "scan.speech123Playback").parent,
        resolve_path(fixture["phaseB128"]["root"]),
        checked_ref(fixture["invalidatedAuthority127"]["invalidationReceipt"], "scan.invalidated127").parent,
    ]
    secret_scan = settings_quality.scan_paths_for_secrets([io_path(root) for root in scan_roots])
    require(secret_scan["findingCount"] == 0, "Phase B evidence secret scan found leakage")
    baselines = settings_quality.verify_immutable_baselines()
    pending = [
        "Core must publish an append-only authority >= v1.2.9 that binds the final module builds, including Speech 1.2.3 or later; v1.2.8 remains an admitted historical checkpoint",
        "Core must rerun A/B/C/D from their original inputs under that same new authority; v1.2.8 external results cannot be reused as candidate-bound evidence",
        "Path D must prove standard-root synthesis plus fresh playback attempts without result reuse; Speech 1.2.3 physical deep-path evidence is module-level only until authority-bound",
        "Desktop must produce an integrated user entrypoint bound to the same new authority; the v1.2.6 module checkpoint and any intermediate v1.2.8 Desktop checkpoint are not reusable",
        "Only after the successful same-authority A/B/C/D and Desktop evidence may quality generate the exact candidate manifest/tree/candidateHash and select one user entrypoint",
    ]
    return {
        "schemaVersion": "knowledge-clipper-phase-b-candidate-quality-receipt.v1",
        "generatedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "NO-GO-V1.2.8-AUTHORITY-ADMITTED-PHASE-B-PARTIAL-DESKTOP-PENDING",
        "scope": "append-only-quality-evidence-admission; no external services invoked",
        "qualityImplementation": [
            evidence(Path(__file__).resolve()),
            evidence(PROJECT_ROOT / "tests" / "Quality.Gates" / "test_phase_b_candidate_gate.py"),
            evidence(FIXTURE_SCHEMA),
            evidence(DEFAULT_FIXTURE),
            evidence(FIXTURE_ROOT / "invalid" / "short-root-final.json"),
        ],
        "fixtureValidation": validate_fixtures(),
        "authority": authority,
        "invalidatedAuthority127": invalidated_authority,
        "desktop": desktop,
        "content061": content,
        "speech122": speech,
        "speech123": speech123,
        "coreResolver": core,
        "historicalFailure": historical,
        "priorScopedPaths": {"C": prior_c, "D": prior_d},
        "phaseB128": phase_b,
        "paths": paths,
        "secretScan": secret_scan,
        "immutableBaselines": baselines,
        "pending": pending,
        "sideEffectsByQualityGate": {"externalServiceCalls": 0, "workerStarts": 0, "remnoteWrites": 0, "ankiWrites": 0, "mineruUploads": 0, "physicalSpeakerInvocations": 0},
        "release": {"sealed": False, "candidateHash": None, "authorityModified": False, "frozenV1Modified": False, "priorReceiptsModified": False},
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify additive Phase B candidate evidence without starting external services.")
    parser.add_argument("--fixture", default=str(DEFAULT_FIXTURE))
    parser.add_argument("--emit-root")
    parser.add_argument("--skip-full-inventory", action="store_true", help="Unit-test helper; final receipts must not use this flag.")
    args = parser.parse_args()
    fixture_path = resolve_path(args.fixture)
    result = evaluate(load_json(fixture_path), full_inventory=not args.skip_full_inventory)
    result["fixture"] = evidence(fixture_path)
    artifacts: dict[str, Any] = {}
    if args.emit_root:
        root = resolve_path(args.emit_root)
        require(not root.exists(), f"append-only output root already exists: {root}")
        root.mkdir(parents=True)
        receipt_path = root / "quality-receipt.json"
        index_path = root / "input-output-index.md"
        receipt_path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        index_path.write_text(render_index(result), encoding="utf-8")
        artifacts = {"root": display_path(root), "receipt": evidence(receipt_path), "readableIndex": evidence(index_path)}
    print(json.dumps({"result": result, "artifacts": artifacts}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
