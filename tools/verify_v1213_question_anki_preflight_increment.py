from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator

import verify_v1212_external_phase_b as common
import verify_v1212_sealed_process_increment as sealed


PROJECT_ROOT = Path(__file__).resolve().parents[1]
FIXTURE_ROOT = PROJECT_ROOT / "tests" / "Quality.Gates" / "fixtures" / "v1213-question-anki-preflight"
FIXTURE_SCHEMA = FIXTURE_ROOT / "v1213-question-anki-preflight.schema.json"
DEFAULT_FIXTURE = FIXTURE_ROOT / "valid" / "preflight-missing-v1.json"
AUTHORITY_ROOT = PROJECT_ROOT / "contracts" / "authority" / "worker-registry" / "v1.2.13-20260830T100000Z"
AUTHORITY_RELEASE_ROOT = PROJECT_ROOT / "releases" / "knowledge-clipper-authority-v1.2.13-20260830T100000Z" / "win-x64"
AUTHORITY_RECEIPT = PROJECT_ROOT / "receipts" / "knowledge-clipper-authority-1.2.13-20260830T100000Z.json"
ANKI_SOURCE = PROJECT_ROOT / "workers" / "anki-exporter" / "anki_exporter.py"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise common.GateFailure(message)


def upper(value: Any) -> str:
    return str(value).upper()


def validate_fixtures() -> dict[str, Any]:
    schema = common.load_json(FIXTURE_SCHEMA)
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema)
    valid = sorted((FIXTURE_ROOT / "valid").glob("*.json"))
    invalid = sorted((FIXTURE_ROOT / "invalid").glob("*.json"))
    require(valid and invalid, "v1.2.13 preflight fixture set incomplete")
    for path in valid:
        require(not list(validator.iter_errors(common.load_json(path))), f"valid fixture rejected: {path.name}")
    for path in invalid:
        require(bool(list(validator.iter_errors(common.load_json(path)))), f"invalid fixture accepted: {path.name}")
    return {"status": "PASS", "validAccepted": len(valid), "invalidRejected": len(invalid), "schema": common.evidence(FIXTURE_SCHEMA)}


def verify_ref(ref: dict[str, Any], label: str) -> tuple[Path, dict[str, Any]]:
    path = common.resolve_ref(ref, label)
    require(common.sha256(path) == upper(ref["sha256"]), f"{label}: hash drift")
    return path, common.evidence(path)


def verify_authority(fixture: dict[str, Any]) -> dict[str, Any]:
    expected = fixture["authority"]
    checkpoint_path = AUTHORITY_ROOT / "checkpoint.json"
    registry_path = AUTHORITY_ROOT / "worker-registry.v1.json"
    checkpoint = common.load_json(checkpoint_path)
    registry = common.load_json(registry_path)
    receipt = common.load_json(AUTHORITY_RECEIPT)
    require(common.sha256(checkpoint_path) == upper(expected["checkpointSha256"]), "authority checkpoint drift")
    require(common.sha256(registry_path) == upper(expected["registrySha256"]), "authority registry drift")
    require(checkpoint["admissionStatus"] == "authority-admitted", "authority is not admitted")
    require(checkpoint["registry"]["registryVersion"] == registry["registryVersion"] == expected["registryVersion"], "authority version mismatch")
    require(checkpoint["frozenV1"]["files"] == 21 and checkpoint["frozenV1"]["mismatches"] == 0, "frozen v1 drift")
    require(len(registry["workers"]) == 5, "authority worker count mismatch")
    require(checkpoint["workers"]["export.anki"]["workerVersion"] == "1.2.1", "authority Anki version mismatch")
    require(checkpoint["workers"]["content.deepseek"]["workerVersion"] == "0.7.0", "question evidence authority Content version mismatch")
    inventory = sealed.inventory(AUTHORITY_RELEASE_ROOT)
    require(inventory["fileCount"] == checkpoint["release"]["files"], "authority release file count drift")
    require(inventory["totalBytes"] == checkpoint["release"]["bytes"], "authority release byte count drift")
    require(inventory["treeSha256"] == upper(expected["releaseTreeSha256"]) == checkpoint["release"]["treeSha256"], "authority release tree drift")
    require(upper(receipt["checkpoint"]["sha256"]) == upper(expected["checkpointSha256"]), "authority receipt checkpoint mismatch")
    return {
        "status": "PASS-AUTHORITY-V1.2.13-EXACT",
        "checkpoint": common.evidence(checkpoint_path),
        "registry": common.evidence(registry_path),
        "authorityReceipt": common.evidence(AUTHORITY_RECEIPT),
        "release": {k: inventory[k] for k in ("fileCount", "totalBytes", "treeSha256")},
        "workers": {
            "content.deepseek": checkpoint["workers"]["content.deepseek"],
            "export.anki": checkpoint["workers"]["export.anki"],
        },
        "frozenV1": checkpoint["frozenV1"],
    }


def find_job(root: Path) -> Path:
    jobs = [path for path in (root / "jobs").iterdir() if path.is_dir()]
    require(len(jobs) == 1, f"expected one job, found {len(jobs)}")
    return jobs[0]


def find_results(job_root: Path) -> list[Path]:
    return sorted(path for path in sealed.walk_files(job_root) if path.name == "result.json")


def verify_question_anki(fixture: dict[str, Any]) -> dict[str, Any]:
    evidence_root = (PROJECT_ROOT / fixture["evidenceRoot"]).resolve()
    require(evidence_root.is_relative_to(PROJECT_ROOT) and common.file_exists(evidence_root / "failure-receipt.json"), "question/Anki evidence root missing")
    job_root = find_job(evidence_root)
    manifest_v1 = job_root / "task.manifest.v1.json"
    manifest_v2 = job_root / "task.manifest.v2.json"
    require(not common.file_exists(manifest_v1), "unexpected v1 manifest exists; this is not the recorded failure")
    require(common.file_exists(manifest_v2), "v2 manifest missing")

    results = find_results(job_root)
    content_results: list[dict[str, Any]] = []
    anki_paths: list[Path] = []
    for path in results:
        value = common.load_json(path)
        if value.get("worker", {}).get("id") == "content.deepseek":
            content_results.append({"path": path, "value": value})
        if value.get("worker", {}).get("id") == "export.anki":
            anki_paths.append(path)
    require(len(content_results) == 3, f"expected three Content stage results, found {len(content_results)}")
    require(all(item["value"]["status"] == "completed" for item in content_results), "question content stage not completed")
    stage_ids = {item["value"]["stageId"] for item in content_results}
    require(stage_ids == {"question-answer", "question-follow-up", "question-make-cards"}, f"question stage set mismatch: {stage_ids}")
    require(all(item["value"]["worker"]["version"] == "0.7.0" for item in content_results), "question stage worker is not authority-bound 0.7.0")
    require(len(anki_paths) == 1, f"old task must have exactly one Anki attempt, found {len(anki_paths)}")

    result_path = anki_paths[0]
    attempt_root = result_path.parent
    request_path = attempt_root / "request.json"
    stderr_path = attempt_root / "stderr.log"
    result = common.load_json(result_path)
    request = common.load_json(request_path)
    stderr = common.io_path(stderr_path).read_text(encoding="utf-8-sig").strip()
    stderr_json = json.loads(stderr)
    require(request["workerId"] == "export.anki" and request["operation"] == "export", "Anki request identity mismatch")
    require(result["status"] == "unknown_external_state", "Core conservative terminal projection changed")
    require(result["error"]["retryable"] is False and result["outputs"] == [], "unknown external state retry/output semantics changed")
    require(stderr_json["code"] == "FILE_MISSING" and stderr_json["message"].endswith("task.manifest.v1.json"), "missing v1 manifest stderr mismatch")
    require(not any(path.name in {"operations.ndjson", "items.ndjson", "receipt.json", "preflight.json"} for path in sealed.walk_files(attempt_root)), "external/effect evidence exists despite preflight failure")
    require(not common.file_exists(attempt_root / "outputs"), "outputs directory exists despite constructor preflight failure")
    require(request["attemptId"] == result["attemptId"] and request["operationId"] == result["operationId"], "Anki request/result identity mismatch")

    source_lines = common.io_path(ANKI_SOURCE).read_text(encoding="utf-8").splitlines()
    load_line = next(i for i, line in enumerate(source_lines, 1) if "self.task = load_json(self.task_manifest_path)" in line)
    client_lines = [i for i, line in enumerate(source_lines, 1) if "self.client = AnkiClient(" in line]
    require(client_lines and all(load_line < line for line in client_lines), "Anki client may be constructed before manifest load")

    return {
        "status": "FAILED-PREFLIGHT-MISSING-V1-MANIFEST-BEFORE-ANKI-CLIENT",
        "classification": {
            "actualFailure": "worker-preflight-contract-file-missing",
            "notAnkiServiceFailure": True,
            "coreProjection": "unknown_external_state-conservative",
            "actualExternalStateKnownFromPreflightOrdering": "no-external-call",
            "externalCalls": 0,
            "externalWrites": fixture["classification"]["externalWrites"],
            "oldAttemptRetryAllowed": fixture["classification"]["retryOldAttempt"],
            "rerunPolicy": fixture["classification"]["rerunPolicy"],
        },
        "job": {
            "taskId": common.load_json(manifest_v2)["taskId"],
            "manifestV1": {"path": manifest_v1.relative_to(PROJECT_ROOT).as_posix(), "exists": False},
            "manifestV2": common.evidence(manifest_v2),
            "evidenceInventory": {k: sealed.inventory(evidence_root)[k] for k in ("fileCount", "totalBytes", "treeSha256")},
        },
        "questionStages": [
            {"stageId": item["value"]["stageId"], "status": item["value"]["status"], "worker": item["value"]["worker"], "result": common.evidence(item["path"])}
            for item in sorted(content_results, key=lambda item: item["value"]["stageId"])
        ],
        "ankiAttempt": {
            "attemptId": result["attemptId"],
            "request": common.evidence(request_path),
            "result": common.evidence(result_path),
            "stderr": common.evidence(stderr_path),
            "stderrCode": stderr_json["code"],
            "resultStatus": result["status"],
            "retryable": result["error"]["retryable"],
            "effectFiles": [],
        },
        "initializationOrderEvidence": {
            "source": common.evidence(ANKI_SOURCE),
            "manifestLoadLine": load_line,
            "ankiClientConstructionLines": client_lines,
            "manifestPrecedesClient": True,
        },
    }


def verify_content(fixture: dict[str, Any]) -> dict[str, Any]:
    admitted_path, admitted_ref = verify_ref(fixture["content"]["admissionReceipt"], "Content 0.8.0 admission")
    rejected_path, rejected_ref = verify_ref(fixture["content"]["rejectionReceipt"], "Content 0.8.1 rejection")
    admitted = common.load_json(admitted_path)
    rejected = common.load_json(rejected_path)
    require(admitted["status"] == "PASS" and admitted["worker"] == {"id": "content.deepseek", "version": "0.8.0"}, "Content 0.8.0 admission mismatch")
    live = admitted["realDeepSeekCanary"]
    require(live["workerResultStatus"] == "completed" and live["moduleReleaseState"] == "unresolved", "Content CANAR unresolved evidence mismatch")
    require(" CANAR " in live["before"] and live["after"] == live["before"], "CANAR was changed or not present")
    require(live["cardBundlePublished"] is False and live["exporterCalls"] == 0, "unresolved CANAR leaked downstream")
    result_path = Path(live["resultPath"])
    result = common.load_json(result_path)
    require(result["status"] == "completed" and result["payload"]["moduleReleaseState"] == "unresolved", "live WorkerResult unresolved projection drift")
    require(admitted["coreAdmission"]["whenOcrUnresolved"].find("waiting_input") >= 0, "unresolved does not require waiting_input")
    require(rejected["status"] == "NEVER_PROMOTED" and rejected["admissionAllowed"] is False and rejected["authorityRegistryAllowed"] is False, "Content 0.8.1 disqualification drift")
    require(fixture["classification"]["promoteRejectedContent"] is False, "fixture permits rejected Content promotion")
    return {
        "content080": {
            "status": "PASS-MODULE-ADMISSION-AUTHORITY-PENDING",
            "receipt": admitted_ref,
            "formalRelease": admitted["formalRelease"],
            "liveCanar": {
                "before": live["before"],
                "after": live["after"],
                "moduleReleaseState": "unresolved",
                "requiredCoreProjection": "waiting_input",
                "cardBundlePublished": False,
                "exporterCalls": 0,
                "result": common.evidence(result_path),
            },
        },
        "content081": {
            "status": "REJECTED-NEVER-PROMOTED",
            "receipt": rejected_ref,
            "authorityRegistryAllowed": False,
            "requiredDisposition": rejected["requiredDisposition"],
        },
    }


def evaluate(fixture: dict[str, Any]) -> dict[str, Any]:
    return {
        "schemaVersion": "knowledge-clipper-v1213-question-anki-preflight-quality-receipt.v1",
        "status": "CONDITIONAL-ANKI-PREFLIGHT-FAILED-NO-EXTERNAL-WRITE",
        "authority": verify_authority(fixture),
        "sealedQuestionAnki": verify_question_anki(fixture),
        "content": verify_content(fixture),
        "qualityExecution": {
            "externalServicesInvoked": 0,
            "externalWrites": 0,
            "oldAttemptRetried": False,
            "frozenContractsModified": False,
            "authorityModified": False,
        },
        "releaseDecision": {
            "status": "NO-GO-FOR-QUESTION-TO-ANKI-UNTIL-NEW-IMMUTABLE-TASK-PASSES",
            "reason": "Anki worker request reached the authority-bound process but constructor preflight could not find task.manifest.v1.json; no Anki client was constructed.",
            "notAnkiServiceFailure": True,
        },
    }


def write_outputs(result: dict[str, Any], output_root: Path) -> tuple[Path, Path]:
    output_root.mkdir(parents=True, exist_ok=False)
    receipt = output_root / "question-anki-preflight-quality-receipt.json"
    readable = output_root / "READABLE.md"
    result = dict(result)
    result["generatedAt"] = datetime.now(timezone.utc).isoformat()
    receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    stages = result["sealedQuestionAnki"]["questionStages"]
    stage_summary = ", ".join(f"{item['stageId']}={item['status']}" for item in stages)
    readable.write_text(
        "# v1.2.13 question -> Anki preflight quality increment\n\n"
        f"- authority: `{result['authority']['status']}`\n"
        f"- question stages: {stage_summary}\n"
        "- Anki: request reached `export.anki@1.2.1`, then failed before AnkiClient construction because `task.manifest.v1.json` was absent; `task.manifest.v2.json` exists.\n"
        "- Core terminal result: `unknown_external_state` (conservative generic projection); observed external calls/writes: `0/0`. This is not an Anki service failure.\n"
        "- retry: old immutable attempt is not retried; rerun must use a new immutable task.\n"
        "- Content 0.8.0: module admission PASS; live `CANAR` remains unchanged and unresolved, so Core must project `waiting_input` and must not export.\n"
        "- Content 0.8.1: `NEVER_PROMOTED`; forbidden from authority/candidate.\n"
        "- release decision: `NO-GO-FOR-QUESTION-TO-ANKI-UNTIL-NEW-IMMUTABLE-TASK-PASSES`.\n",
        encoding="utf-8",
    )
    return receipt, readable


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--fixture", type=Path, default=DEFAULT_FIXTURE)
    parser.add_argument("--output-root", type=Path)
    parser.add_argument("--validate-only", action="store_true")
    args = parser.parse_args()
    validate_fixtures()
    result = evaluate(common.load_json(args.fixture))
    if args.output_root and not args.validate_only:
        receipt, readable = write_outputs(result, args.output_root)
        print(json.dumps({"status": result["status"], "receipt": str(receipt), "receiptSha256": common.sha256(receipt), "readable": str(readable), "readableSha256": common.sha256(readable)}, ensure_ascii=False))
    else:
        print(json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
