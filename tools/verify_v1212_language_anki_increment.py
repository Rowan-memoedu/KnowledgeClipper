from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator

import verify_v1212_external_phase_b as common


PROJECT_ROOT = Path(__file__).resolve().parents[1]
FIXTURE_ROOT = PROJECT_ROOT / "tests" / "Quality.Gates" / "fixtures" / "v1212-language-anki-increment"
FIXTURE_SCHEMA = FIXTURE_ROOT / "v1212-language-anki-increment.schema.json"
DEFAULT_FIXTURE = FIXTURE_ROOT / "valid" / "language-unique-target.json"
EXPECTED_CANDIDATE = {
    "candidateId": "kc-integrated-settings-v1212-20260830T183500Z",
    "manifestSha256": "5D03C90D42FA2A1145FF6DCC4874BD059AC3EE072628F0A8DC81FB23983B4F3C",
    "sealedTreeSha256": "4F1021038635984154290467011FBB25AB9D88DD7A1A035F059CFF309231416A",
}
EXPECTED_AUTHORITY = {
    "registryVersion": "1.2.12",
    "registrySha256": "FC105AB65FD80B72779D1601E2C4B6FAA7ECCAF5F5C1BADC774060D8F685495A",
    "checkpointSha256": "552151B3EDBEED94FD1B4ECC7B49F78CD7503321119235BBF3ECAA2A0EEE8580",
}
TARGET = "pragmatic"
AUTHORIZED_DECK = "零碎知识剪藏::Canary::PhaseB-20260828T081109Z"


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
    require(valid and invalid, "language/Anki fixture set incomplete")
    for path in valid:
        require(not list(validator.iter_errors(common.load_json(path))), f"valid fixture rejected: {path.name}")
    for path in invalid:
        require(bool(list(validator.iter_errors(common.load_json(path)))), f"invalid fixture accepted: {path.name}")
    return {
        "status": "PASS",
        "validAccepted": len(valid),
        "invalidRejected": len(invalid),
        "schema": common.evidence(FIXTURE_SCHEMA),
    }


def verify_content(ref: dict[str, Any]) -> dict[str, Any]:
    receipt_path = common.resolve_ref(ref, "Content authority receipt")
    receipt = common.load_json(receipt_path)
    require(receipt["status"] == "completed", "Content receipt is not completed")
    require(receipt["scenarioCount"] == 4 and receipt["allCanariesLive"] is True, "Content live scenario coverage mismatch")
    require(receipt["allCanariesCompleted"] is True and receipt["schemasValidatedSeparately"] is True, "Content completion/schema evidence mismatch")
    require(receipt["externalProvidersCalled"] == ["deepseek"], "Content provider set mismatch")
    require(receipt["externalDestinationTargetsCalled"] == [] and receipt["credentialsRecorded"] is False, "Content destination/secret boundary mismatch")
    manifest_path = common.inside_project(receipt["manifestPath"], "Content live manifest")
    require(common.sha256(manifest_path) == upper(receipt["manifestSha256"]), "Content manifest SHA-256 mismatch")
    manifest = common.load_json(manifest_path)
    require(manifest["release"]["workerVersion"] == "0.7.0", "Content worker version mismatch")
    require(manifest["release"]["authorityRegistryVersion"] == "1.2.12", "Content authority version mismatch")
    require(manifest["externalWrites"] == [] and manifest["destinationNeutral"] is True, "Content destination neutrality mismatch")
    require({item["id"] for item in manifest["scenarios"]} == {
        "ordinary-cleaning-and-card", "language-meaning-reading", "language-writing", "language-both"
    }, "Content scenario set mismatch")
    package_root = manifest_path.parent
    for item in manifest["files"]:
        path = (package_root / item["path"]).resolve()
        require(path.is_relative_to(package_root.resolve()) and common.file_exists(path), f"Content file missing: {item['path']}")
        require(common.io_path(path).stat().st_size == item["bytes"], f"Content file bytes mismatch: {item['path']}")
        require(common.sha256(path) == upper(item["sha256"]), f"Content file hash mismatch: {item['path']}")
    return {
        "status": "PASS-LIVE-DEEPSEEK-AUTHORITY-V1.2.12",
        "receipt": common.evidence(receipt_path),
        "manifest": common.evidence(manifest_path),
        "workerVersion": "0.7.0",
        "scenarioIds": [item["id"] for item in manifest["scenarios"]],
        "externalProvidersCalled": ["deepseek"],
        "externalDestinationTargetsCalled": [],
        "credentialsRecorded": False,
    }


def candidate_matches(value: dict[str, Any], expected: dict[str, Any]) -> None:
    require(value.get("candidateId", expected["candidateId"]) == expected["candidateId"], "candidate id mismatch")
    require(upper(value["manifestSha256"]) == expected["manifestSha256"], "candidate manifest mismatch")
    tree = value.get("sealedTreeSha256") or value.get("sealed", {}).get("treeSha256")
    require(upper(tree) == expected["sealedTreeSha256"], "candidate tree mismatch")


def authority_matches(value: dict[str, Any], expected: dict[str, Any], *, require_checkpoint: bool = False) -> None:
    require(value["registryVersion"] == expected["registryVersion"], "authority version mismatch")
    require(upper(value["registrySha256"]) == expected["registrySha256"], "authority registry mismatch")
    if require_checkpoint:
        require(upper(value["checkpointSha256"]) == expected["checkpointSha256"], "authority checkpoint mismatch")


def verify_readback(ref: dict[str, Any], path_name: str, path_receipt: dict[str, Any], expected: dict[str, Any]) -> dict[str, Any]:
    receipt_path = common.resolve_ref(ref, f"path {path_name} exact readback")
    value = common.load_json(receipt_path)
    require(value["status"] == "passed", f"path {path_name}: readback not passed")
    candidate_matches(value["candidate"], EXPECTED_CANDIDATE)
    authority_matches(value["authority"], EXPECTED_AUTHORITY)
    require(value["taskId"] == path_receipt["taskId"], f"path {path_name}: readback task mismatch")
    require(value["export"]["worker"] == "export.anki@1.2.1", f"path {path_name}: Anki version mismatch")
    require(common.sha256(common.inside_project(value["export"]["resultPath"], f"path {path_name} export result")) == upper(value["export"]["resultSha256"]), f"path {path_name}: export result hash mismatch")
    require(value["exactReadback"]["deckName"] == AUTHORIZED_DECK, f"path {path_name}: deck escaped isolation")
    require(value["exactReadback"]["noteCount"] == expected["count"] == value["exactReadback"]["cardCount"], f"path {path_name}: note/card count mismatch")
    require(value["export"]["externalWriteCount"] == expected["count"], f"path {path_name}: external write count mismatch")
    export_items = value["export"]["items"]
    readback_items = value["exactReadback"]["items"]
    require(export_items == readback_items, f"path {path_name}: export/readback content mismatch")
    require([item["template"] for item in readback_items] == expected["templates"], f"path {path_name}: template/order mismatch")
    require(all(item["frontExact"] and item["backExact"] and item["deckExact"] for item in readback_items), f"path {path_name}: exact readback flag false")
    safety = value["safety"]
    require(safety["authorizedDeck"] == AUTHORIZED_DECK and safety["formalDeckCreated"] is False, f"path {path_name}: isolation boundary mismatch")
    require(safety["otherDeckWrites"] == 0 and safety["mediaWrites"] == 0, f"path {path_name}: unexpected side effect")
    require(safety["secretValuesRecorded"] is False and safety["automaticRetryPerformed"] is False, f"path {path_name}: secret/retry boundary mismatch")
    return {"receipt": common.evidence(receipt_path), "items": readback_items, "noteCount": expected["count"], "cardCount": expected["count"]}


def verify_language_paths(fixture: dict[str, Any]) -> tuple[dict[str, Any], dict[str, Any]]:
    execution_path = common.resolve_ref(fixture["executionReceipt"], "E/F/G execution receipt")
    execution = common.load_json(execution_path)
    candidate_matches(execution["candidate"], fixture["candidate"])
    authority_matches(execution["authority"], fixture["authority"], require_checkpoint=True)
    require(execution["secretsWritten"] == 0, "E/F/G execution wrote secrets")
    require(execution["remnoteWritesDisabled"] is True, "E/F/G RemNote writes were not disabled")
    mineru = execution["mineruCredential"]
    require(mineru["required"] is False and mineru["source"] == "not-required" and mineru["secretPersisted"] is False, "OCR is not user-deferred/nonblocking")
    runs = {item["path"]: item for item in execution["runs"]}
    require(set(runs) == {"E", "F", "G"}, "E/F/G run set mismatch")
    expected = {
        "E": {"count": 1, "templates": ["language_context"]},
        "F": {"count": 1, "templates": ["language_writing"]},
        "G": {"count": 2, "templates": ["language_context", "language_writing"]},
    }
    candidate_root = common.inside_project(execution["candidate"]["root"], "candidate root", directory=True)
    phase_root = execution_path.parent
    results: dict[str, Any] = {}
    raw: dict[str, Any] = {}
    for path_name in ("E", "F", "G"):
        run = runs[path_name]
        require(run["exitCode"] == 0, f"path {path_name}: nonzero exit")
        receipt_path = common.inside_project(run["receipt"], f"path {path_name} receipt")
        require(receipt_path.is_relative_to(phase_root), f"path {path_name}: receipt escaped execution root")
        value = common.load_json(receipt_path)
        raw[path_name] = value
        require(value["path"] == path_name and value["status"] == "succeeded", f"path {path_name}: status mismatch")
        candidate_matches(value["candidate"], fixture["candidate"])
        authority_matches(value["authority"], fixture["authority"])
        strict = value["strictLayout"]
        require(strict["passed"] is True and strict["workerValidationRelaxed"] is False, f"path {path_name}: strict worker layout mismatch")
        job_root = common.inside_project(strict["jobRoot"], f"path {path_name} job root", directory=True)
        attempts = [common.verify_attempt(job_root, item) for item in value["attempts"]]
        require(sorted((item["workerId"], item["workerVersion"]) for item in attempts) == [("content.deepseek", "0.7.0"), ("export.anki", "1.2.1")], f"path {path_name}: attempt set mismatch")
        require(value["invocationCounts"] == {"export.anki": 1, "content.deepseek": 1}, f"path {path_name}: invocation count mismatch")
        require(value["deepSeekCredentialBoundary"] == {"present": True, "secretPersisted": False, "taskPackageReferences": 0}, f"path {path_name}: DeepSeek secret boundary mismatch")
        core = common.inside_project(value["candidate"]["coreAssemblyPath"], f"path {path_name} Core assembly")
        candidate_core = candidate_root / "KnowledgeClipper.Orchestrator.dll"
        require(not core.is_relative_to(candidate_root) and common.sha256(core) == common.sha256(candidate_core), f"path {path_name}: backend Core is not byte-equal to candidate Core")
        readback = verify_readback(fixture["readbacks"][path_name], path_name, value, expected[path_name])
        readable = receipt_path.parent / "readable-output.md"
        require(common.file_exists(readable), f"path {path_name}: readable output missing")
        results[path_name] = {
            "status": "PASS-BACKEND-HARNESS-CANDIDATE-WORKER-BOUND",
            "taskId": value["taskId"],
            "pathReceipt": common.evidence(receipt_path),
            "attempts": attempts,
            "exactReadback": readback,
            "readableOutput": common.evidence(readable),
            "candidateCoreByteEqual": True,
            "candidateDesktopOrchestratorProcess": False,
        }
    e_items = results["E"]["exactReadback"]["items"]
    f_items = results["F"]["exactReadback"]["items"]
    g_items = results["G"]["exactReadback"]["items"]
    original = e_items[0]["front"]
    writing = f_items[0]
    require(original.count(TARGET) == 1, "writing source target is not unique")
    prefix, suffix = original.split(TARGET)
    require(writing["front"].startswith(prefix + "（") and writing["front"].endswith("）" + suffix), "writing front is not a single full-width replacement")
    replacement = writing["front"][len(prefix): len(writing["front"]) - len(suffix)] if suffix else writing["front"][len(prefix):]
    require(replacement.startswith("（") and replacement.endswith("）") and "；" in replacement, "writing replacement format mismatch")
    require(TARGET not in writing["front"] and writing["back"] == TARGET, "writing target leakage/back mismatch")
    semantic_fields = ("template", "front", "back", "tags")
    def semantic(item: dict[str, Any]) -> dict[str, Any]:
        return {key: item[key] for key in semantic_fields}
    require([semantic(item) for item in g_items] == [semantic(e_items[0]), semantic(f_items[0])], "both output is not meaning-reading then writing")
    return results, {"source": original, "meaningReading": e_items, "writing": f_items, "both": g_items}


def verify_qualification(fixture: dict[str, Any], language: dict[str, Any]) -> dict[str, Any]:
    receipt_path = common.resolve_ref(fixture["qualificationReceipt"], "Content/Anki qualification receipt")
    readable_path = common.resolve_ref(fixture["readableSummary"], "Content/Anki readable summary")
    value = common.load_json(receipt_path)
    require(value["status"] == "passed-backend-harness-candidate-worker-bound", "aggregate qualification status mismatch")
    require(value["executionClass"] == "backend-harness candidate-worker-bound; exact packaged Core DLL loaded in-process; not sealed Desktop/Orchestrator process E2E", "aggregate execution class mismatch")
    candidate_matches(value["candidate"], fixture["candidate"])
    authority_matches(value["authority"], fixture["authority"], require_checkpoint=True)
    require([item["id"] for item in value["paths"]] == ["ordinary-text", "meaning-reading", "writing", "both"], "aggregate path order mismatch")
    require(all(item["status"] == "passed" and item["content"]["worker"] == "content.deepseek@0.7.0" and item["anki"]["worker"] == "export.anki@1.2.1" for item in value["paths"]), "aggregate worker/status mismatch")
    require([len(item["anki"]["notes"]) for item in value["paths"]] == [1, 1, 1, 2], "aggregate note counts mismatch")
    require(value["totals"] == {"tasks": 4, "contentWorkerRuns": 4, "ankiWorkerRuns": 4, "deepSeekCalls": 13.0, "ankiNotes": 5, "ankiExternalWrites": 5.0, "remnoteWrites": 0, "mineruCalls": 0, "physicalSpeakerInvocations": 0}, "aggregate totals mismatch")
    require(value["safety"] == {"authorizedDeck": AUTHORIZED_DECK, "otherDeckWrites": 0, "formalDeckCreated": False, "mediaWrites": 0, "secretsRecorded": False, "automaticRetryPerformed": False, "ocr": "user-deferred"}, "aggregate safety mismatch")
    require(value["remainingBoundary"]["ocr"] == "user-deferred-nonblocking", "aggregate OCR boundary mismatch")
    negatives = value["supersededFailClosedEvidence"]
    require(len(negatives) == 2, "fail-closed evidence count mismatch")
    for item in negatives:
        path = common.inside_project(item["path"], "fail-closed path receipt")
        require(common.sha256(path) == upper(item["sha256"]), "fail-closed evidence hash mismatch")
        raw = common.load_json(path)
        require(item["status"] == raw["status"] == "waiting_input", "fail-closed status mismatch")
        require(item["reason"] == "language_writing_target_not_unique" and item["ankiWrites"] == 0, "fail-closed reason/write mismatch")
    require(value["paths"][1]["anki"]["notes"] == language["E"]["exactReadback"]["items"], "aggregate E content drift")
    require(value["paths"][2]["anki"]["notes"] == language["F"]["exactReadback"]["items"], "aggregate F content drift")
    require(value["paths"][3]["anki"]["notes"] == language["G"]["exactReadback"]["items"], "aggregate G content drift")
    return {
        "status": "PASS-BACKEND-HARNESS-CANDIDATE-WORKER-BOUND",
        "receipt": common.evidence(receipt_path),
        "readableSummary": common.evidence(readable_path),
        "taskCount": 4,
        "noteCount": 5,
        "externalWriteCount": 5,
        "failClosedNegativeCount": 2,
    }


def evaluate(fixture: dict[str, Any]) -> dict[str, Any]:
    require(fixture["policy"] == {"externalRerun": False, "ocrStatus": "user-deferred", "ocrBlocking": False, "desktopProcessE2E": False}, "increment policy weakened")
    require(fixture["candidate"] == EXPECTED_CANDIDATE, "fixture does not bind final T183500Z candidate")
    require(fixture["authority"] == EXPECTED_AUTHORITY, "fixture does not bind authority v1.2.12")
    base_path = common.resolve_ref(fixture["baseExternalReceipt"], "base external qualification")
    base = common.load_json(base_path)
    require(base["candidateBound"] is True and upper(base["candidateHash"]) == EXPECTED_CANDIDATE["manifestSha256"], "base candidate binding mismatch")
    content = verify_content(fixture["contentReceipt"])
    paths, readable = verify_language_paths(fixture)
    qualification = verify_qualification(fixture, paths)
    return {
        "schemaVersion": "knowledge-clipper-v1212-language-anki-increment-quality.v1",
        "generatedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-INCREMENTAL-CONTENT-ANKI-CANDIDATE-WORKER-BOUND",
        "candidateBound": True,
        "candidateHash": EXPECTED_CANDIDATE["manifestSha256"],
        "candidate": EXPECTED_CANDIDATE,
        "authority": EXPECTED_AUTHORITY,
        "classification": {
            "level": "backend-harness-candidate-worker-bound",
            "candidateDesktopOrchestratorProcessE2E": False,
            "externalServicesRerunByQuality": False,
            "scope": "Content authority plus ordinary/meaning-reading/writing/both Anki qualification only",
        },
        "baseExternalQualification": common.evidence(base_path),
        "content": content,
        "paths": paths,
        "qualification": qualification,
        "readableComparison": readable,
        "ocr": {"status": "USER-DEFERRED-NON-BLOCKING", "blocking": False, "mineruCalls": 0},
        "safety": {"secretFindingCount": 0, "remnoteWrites": 0, "mediaWrites": 0, "physicalSpeakerInvocations": 0},
        "userHandTestEntrypoint": False,
        "releaseGo": False,
        "noAdditionalGateIntroduced": True,
    }


def render_readable(result: dict[str, Any]) -> str:
    comparison = result["readableComparison"]
    lines = [
        "# v1.2.12 Content → Anki 增量候选验收",
        "",
        f"- status: {result['status']}",
        f"- candidateHash: `{result['candidateHash']}`",
        "- classification: backend-harness candidate-worker-bound；不是 sealed Desktop/Orchestrator process E2E。",
        "- OCR: USER-DEFERRED-NON-BLOCKING；本轮 MinerU 调用 0，不构成阻塞。",
        "- Quality 未重跑外部服务。",
        "",
        "## 原始输入 → 最终卡片",
        "",
        f"原文：{comparison['source']}",
        "",
    ]
    for name, key in (("meaning-reading", "meaningReading"), ("writing", "writing"), ("both", "both")):
        lines.append(f"### {name}")
        lines.append("")
        for index, item in enumerate(comparison[key], 1):
            lines.extend([f"{index}. `{item['template']}`", f"   - front: {item['front']}", f"   - back: {item['back']}"])
        lines.append("")
    lines.extend([
        "## 聚合读回",
        "",
        "ordinary / meaning-reading / writing / both：任务 4，Content 4/4 completed，Anki 4/4 completed，notes/cards 精确读回 1/1/1/2，共 5 张。",
        "",
        "双 occurrence 的 writing/both 历史输入保持 `waiting_input` + `language_writing_target_not_unique`，Anki 写入 0；未覆盖。",
        "",
        "## 边界",
        "",
        "本回执只增量接纳指定 Content 与 Content→Anki 证据，不把它外推为桌面进程 E2E，也不新增 OCR 或其他发布门槛。",
    ])
    return "\n".join(lines) + "\n"


def write_outputs(result: dict[str, Any], output_root: Path) -> tuple[Path, Path]:
    require(not output_root.exists(), f"append-only output already exists: {output_root}")
    output_root.mkdir(parents=True)
    receipt_path = output_root / "language-anki-increment-quality-receipt.json"
    index_path = output_root / "input-output-index.md"
    receipt_path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    index_path.write_text(render_readable(result), encoding="utf-8")
    return receipt_path, index_path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--fixture", type=Path, default=DEFAULT_FIXTURE)
    parser.add_argument("--output-root", type=Path)
    args = parser.parse_args()
    fixture = common.load_json(args.fixture)
    validation = validate_fixtures()
    result = evaluate(fixture)
    result["fixtureValidation"] = validation
    if args.output_root:
        receipt, index = write_outputs(result, args.output_root.resolve())
        print(json.dumps({"status": result["status"], "receipt": common.evidence(receipt), "index": common.evidence(index)}, ensure_ascii=False, indent=2))
    else:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
