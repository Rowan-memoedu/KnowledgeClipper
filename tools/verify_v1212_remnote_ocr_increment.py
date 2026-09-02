from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator

import verify_v1212_external_phase_b as common


PROJECT_ROOT = Path(__file__).resolve().parents[1]
FIXTURE_ROOT = PROJECT_ROOT / "tests" / "Quality.Gates" / "fixtures" / "v1212-remnote-ocr-increment"
FIXTURE_SCHEMA = FIXTURE_ROOT / "v1212-remnote-ocr-increment.schema.json"
DEFAULT_FIXTURE = FIXTURE_ROOT / "valid" / "live-worker-increment.json"
CANDIDATE_ROOT = PROJECT_ROOT / "releases" / "kc-integrated-settings-v1212-20260830T183500Z" / "win-x64"
ISOLATED_ROOT = "8zNzfX45XLCSKoPSJ"


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
    require(valid and invalid, "RemNote/OCR fixture set incomplete")
    for path in valid:
        require(not list(validator.iter_errors(common.load_json(path))), f"valid fixture rejected: {path.name}")
    for path in invalid:
        require(bool(list(validator.iter_errors(common.load_json(path)))), f"invalid fixture accepted: {path.name}")
    return {"status": "PASS", "validAccepted": len(valid), "invalidRejected": len(invalid), "schema": common.evidence(FIXTURE_SCHEMA)}


def verify_evidence(ref: dict[str, Any], label: str, *, base: Path | None = None) -> Path:
    raw = Path(ref["path"])
    if raw.is_absolute():
        path = common.inside_project(raw, label)
    else:
        root = base or PROJECT_ROOT
        path = (root / raw).resolve()
        require(path.is_relative_to(PROJECT_ROOT.resolve()) and common.file_exists(path), f"{label}: missing or escaped project")
    require(common.io_path(path).stat().st_size == int(ref["bytes"]), f"{label}: bytes mismatch")
    require(common.sha256(path) == upper(ref["sha256"]), f"{label}: SHA-256 mismatch")
    return path


def verify_candidate(fixture: dict[str, Any]) -> dict[str, Any]:
    base_path = common.resolve_ref(fixture["baseCandidateReceipt"], "base candidate quality receipt")
    base = common.load_json(base_path)
    expected = fixture["candidate"]
    authority = fixture["authority"]
    require(base["candidateBound"] is True and upper(base["candidateHash"]) == expected["manifestSha256"], "base candidate hash mismatch")
    require(upper(base["desktop"]["fullInventory"]["treeSha256"]) == expected["sealedTreeSha256"], "base candidate tree mismatch")
    manifest_path = CANDIDATE_ROOT / "candidate-file-manifest.v1.json"
    require(common.sha256(manifest_path) == expected["manifestSha256"], "candidate manifest drifted")
    manifest = common.load_json(manifest_path)
    require(manifest["candidateId"] == expected["candidateId"], "candidate id mismatch")
    require(manifest["authority"]["registryVersion"] == authority["registryVersion"], "candidate authority version mismatch")
    require(upper(manifest["authority"]["registrySha256"]) == authority["registrySha256"], "candidate registry mismatch")
    require(upper(manifest["authority"]["checkpointSha256"]) == authority["checkpointSha256"], "candidate checkpoint mismatch")
    registry = CANDIDATE_ROOT / "authority" / "metadata" / "worker-registry.v1.json"
    checkpoint = CANDIDATE_ROOT / "authority" / "metadata" / "checkpoint.json"
    require(common.sha256(registry) == authority["registrySha256"], "embedded registry hash mismatch")
    require(common.sha256(checkpoint) == authority["checkpointSha256"], "embedded checkpoint hash mismatch")
    return {
        "status": "PASS-EXACT-T183500Z-AUTHORITY-V1.2.12",
        "baseQualityReceipt": common.evidence(base_path),
        "manifest": common.evidence(manifest_path),
        "sealedTreeSha256": expected["sealedTreeSha256"],
        "registry": common.evidence(registry),
        "checkpoint": common.evidence(checkpoint),
    }


def verify_remnote(ref: dict[str, Any], fixture: dict[str, Any]) -> dict[str, Any]:
    receipt_path = common.resolve_ref(ref, "RemNote live replay receipt")
    value = common.load_json(receipt_path)
    require(value["status"] == "passed", "RemNote receipt not passed")
    authority = value["authority"]
    require(authority["version"] == fixture["authority"]["registryVersion"], "RemNote authority version mismatch")
    registry = verify_evidence(authority["registry"], "RemNote authority registry")
    checkpoint = verify_evidence(authority["checkpoint"], "RemNote authority checkpoint")
    worker = verify_evidence(authority["worker"], "RemNote authority worker")
    require(common.sha256(registry) == fixture["authority"]["registrySha256"], "RemNote registry binding mismatch")
    require(common.sha256(checkpoint) == fixture["authority"]["checkpointSha256"], "RemNote checkpoint binding mismatch")
    candidate_worker = CANDIDATE_ROOT / "authority" / "win-x64" / "workers" / "remnote" / "KnowledgeClipper.RemNoteExporter.Worker.exe"
    require(authority["workerId"] == "export.remnote" and authority["workerVersion"] == "2.1.0", "RemNote worker identity mismatch")
    require(common.sha256(candidate_worker) == common.sha256(worker), "RemNote live worker is not byte-equal to candidate worker")
    safety = value["safety"]
    require(safety == {"isolatedRootRemId": ISOLATED_ROOT, "isolatedRootTitle": "KnowledgeClipper PhaseB Canary 20260828T081109Z", "formalClipRootTouched": False, "deletes": 0, "ocrCalls": 0, "newDeepSeekCalls": 0}, "RemNote isolation/safety mismatch")
    health_result_path = verify_evidence(value["health"]["result"], "RemNote health result")
    verify_evidence(value["health"]["request"], "RemNote health request")
    health = common.load_json(health_result_path)
    require(health["state"] == "healthy" and health["admission"] == "ready" and health["externalWriteCount"] == 0, "RemNote health mismatch")
    require(health["targetBinding"]["rootRemId"] == ISOLATED_ROOT and health["targetBinding"]["matched"] is True, "RemNote health target mismatch")
    sections: dict[str, Any] = {}
    for name, route in (("clip", "clip-daily-route"), ("card", "card-export")):
        section = value[name]
        for key in ("sourceLearningBundle", "originalReceipt", "request", "result", "replayReceipt", "reconcileRequest", "reconcileResult"):
            verify_evidence(section[key], f"RemNote {name} {key}")
        if name == "card":
            verify_evidence(section["sourceCardBundle"], "RemNote card sourceCardBundle")
            readback_ref = section["documentReadback"]
        else:
            verify_evidence(section["dateReadback"], "RemNote clip date readback")
            readback_ref = section["clipReadback"]
        verify_evidence(readback_ref, f"RemNote {name} readback")
        result = common.load_json(common.inside_project(section["result"]["path"], f"RemNote {name} result"))
        replay = common.load_json(common.inside_project(section["replayReceipt"]["path"], f"RemNote {name} replay receipt"))
        reconcile = common.load_json(common.inside_project(section["reconcileResult"]["path"], f"RemNote {name} reconcile result"))
        require(result["status"] == "completed" and result["worker"] == {"id": "export.remnote", "version": "2.1.0"}, f"RemNote {name} terminal result mismatch")
        require(result["payload"]["route"] == route and result["payload"]["outcome"] == "already_applied" and result["payload"]["readbackVerified"] is True, f"RemNote {name} replay payload mismatch")
        require(replay["route"] == route and replay["outcome"] == "already_applied" and replay["readbackVerified"] is True and replay["duplicateCount"] == 0, f"RemNote {name} idempotency mismatch")
        require(reconcile["mode"] == "reconcile" and reconcile["state"] == "healthy" and reconcile["externalWriteCount"] == 0, f"RemNote {name} reconcile mismatch")
        require(reconcile["evidenceChain"]["unknownExternalState"] is False and reconcile["evidenceChain"]["automaticRetryAllowed"] is False, f"RemNote {name} retry boundary mismatch")
        require(section["rootRemId"] == ISOLATED_ROOT and section["readbackVerified"] is True and section["exactReadback"] is True, f"RemNote {name} exact readback mismatch")
        require(section["replayOutcome"] == "already_applied" and section["duplicateCount"] == 0, f"RemNote {name} replay summary mismatch")
        sections[name] = {
            "route": route,
            "originalOutcome": section["originalOutcome"],
            "replayOutcome": section["replayOutcome"],
            "readbackVerified": True,
            "exactReadback": True,
            "rootRemId": ISOLATED_ROOT,
            "dateRouting": name == "clip",
        }
    require(value["clip"]["localDate"] == "2026-08-30" and value["clip"]["dateRemId"] and value["clip"]["clipRemId"], "RemNote date route identity missing")
    require(value["card"]["dateRouting"] is False and value["card"]["documentRemId"], "RemNote card/date separation mismatch")
    report = verify_evidence(value["report"], "RemNote readable report")
    return {
        "status": "PASS-LIVE-ISOLATED-REPLAY-READBACK-WORKER-BINARY-BOUND",
        "receipt": common.evidence(receipt_path),
        "candidateWorker": common.evidence(candidate_worker),
        "workerVersion": "2.1.0",
        "isolatedRootRemId": ISOLATED_ROOT,
        "routes": sections,
        "formalClipRootTouched": False,
        "deletes": 0,
        "readableReport": common.evidence(report),
    }


def verify_ocr(ref: dict[str, Any], fixture: dict[str, Any]) -> dict[str, Any]:
    receipt_path = common.resolve_ref(ref, "OCR live receipt")
    value = common.load_json(receipt_path)
    root = receipt_path.parent
    require(value["status"] == "completed", "OCR terminal status mismatch")
    require(value["worker"]["id"] == "image.ocr.mineru" and value["worker"]["version"] == "1.2.2", "OCR worker identity mismatch")
    candidate_worker = CANDIDATE_ROOT / "authority" / "win-x64" / "workers" / "image-ocr" / "KnowledgeClipper.ImageOcr.Worker.exe"
    require(common.sha256(candidate_worker) == upper(value["worker"]["executableSha256"]), "OCR worker is not candidate-bound")
    input_path = verify_evidence(value["input"]["evidence"], "OCR input", base=root)
    request_path = verify_evidence(value["request"], "OCR request", base=root)
    result_path = verify_evidence(value["result"], "OCR result", base=root)
    module_path = verify_evidence(value["moduleResult"], "OCR module result", base=root)
    submission = [verify_evidence(item, "OCR submission", base=root) for item in value["provider"]["submissionEvidence"]]
    outputs = [verify_evidence(item, "OCR output", base=root) for item in value["outputs"]]
    request = common.load_json(request_path)
    result = common.load_json(result_path)
    module = common.load_json(module_path)
    require(request["workerId"] == result["worker"]["id"] == "image.ocr.mineru", "OCR request/result identity mismatch")
    require(request["attemptId"] == result["attemptId"] == module["attemptId"], "OCR attempt identity mismatch")
    require(request["operationId"] == result["operationId"] == module["operationId"], "OCR operation identity mismatch")
    require(result["status"] == module["status"] == "completed" and len(result["outputs"]) == len(module["outputs"]) == 2, "OCR result/module terminal mismatch")
    provider = value["provider"]
    require(provider["name"] == "mineru" and provider["invocations"] == 1 and provider["submissionReceiptPresent"] is True and len(submission) == 1, "OCR provider submission count mismatch")
    require(value["postProcessing"]["externalAttemptCount"] == 1 and value["postProcessing"]["externalRerunPerformed"] is False, "OCR external attempt/replay mismatch")
    safety = value["safety"]
    require(safety == {"syntheticNonPrivateInput": True, "remnoteWrites": 0, "ankiWrites": 0, "destinationWrites": 0, "secretValuesPersisted": 0}, "OCR destination/secret safety mismatch")
    consumer = common.load_json(root / "content-consumer-evidence.v1.json")
    require(consumer["status"] == "ready-for-content-consumer" and consumer["destinationNeutral"] is True, "OCR consumer handoff mismatch")
    require(upper(consumer["markdown"]["sha256"]) == common.sha256(outputs[0]) and upper(consumer["structured"]["sha256"]) == common.sha256(outputs[1]), "OCR consumer output hash mismatch")
    return {
        "status": "PASS-LIVE-MINERU-TERMINAL-COMPLETED-WORKER-BINARY-BOUND",
        "receipt": common.evidence(receipt_path),
        "candidateWorker": common.evidence(candidate_worker),
        "workerVersion": "1.2.2",
        "input": common.evidence(input_path),
        "provider": {"name": "mineru", "submissionCount": 1, "externalAttemptCount": 1},
        "outputs": [common.evidence(path) for path in outputs],
        "workerOutputCount": 2,
        "evidenceOutputCount": 3,
        "ocrText": value["ocrSummary"]["markdown"],
        "contentDownstream": "PENDING-NOT-RUN-BY-THIS-INCREMENT",
        "destinationDownstream": "PENDING-NOT-RUN-BY-THIS-INCREMENT",
        "destinationWrites": 0,
        "blocksPassedText": False,
    }


def evaluate(fixture: dict[str, Any]) -> dict[str, Any]:
    require(fixture["policy"] == {"externalRerun": False, "ocrDownstream": "pending", "blocksPassedText": False}, "increment policy weakened")
    candidate = verify_candidate(fixture)
    remnote = verify_remnote(fixture["remnoteReceipt"], fixture)
    ocr = verify_ocr(fixture["ocrReceipt"], fixture)
    return {
        "schemaVersion": "knowledge-clipper-v1212-remnote-ocr-external-increment-quality.v1",
        "generatedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-INCREMENTAL-REMNOTE-AND-OCR-LIVE-WORKER-BOUND",
        "candidateBound": True,
        "candidateHash": fixture["candidate"]["manifestSha256"],
        "candidate": fixture["candidate"],
        "authority": fixture["authority"],
        "candidateVerification": candidate,
        "classification": {
            "level": "live-module-worker-binary-bound-to-final-candidate",
            "candidateDesktopOrchestratorProcessE2E": False,
            "externalServicesRerunByQuality": False,
        },
        "remnote": remnote,
        "ocr": ocr,
        "existingPassedTextAndHandTest": {"status": "UNCHANGED-NON-BLOCKED", "blockedByOcr": False},
        "noAdditionalGateIntroduced": True,
    }


def render_readable(result: dict[str, Any]) -> str:
    clip = result["remnote"]["routes"]["clip"]
    card = result["remnote"]["routes"]["card"]
    return (
        "# v1.2.12 RemNote + OCR external increment\n\n"
        f"- status: {result['status']}\n"
        f"- candidateHash: `{result['candidateHash']}`\n"
        "- Quality external rerun: 0\n\n"
        "## RemNote\n\n"
        f"- date clip: original={clip['originalOutcome']}; replay={clip['replayOutcome']}; exactReadback={str(clip['exactReadback']).lower()}\n"
        f"- card export: original={card['originalOutcome']}; replay={card['replayOutcome']}; exactReadback={str(card['exactReadback']).lower()}; dateRouting=false\n"
        f"- isolated root: `{result['remnote']['isolatedRootRemId']}`; formal clip root touched=false; deletes=0\n\n"
        "## OCR\n\n"
        f"- input: `{result['ocr']['input']['path']}`\n"
        f"- output text: {result['ocr']['ocrText']}\n"
        "- MinerU submissions=1; terminal=completed; destination writes=0\n"
        "- Content downstream=PENDING; destination downstream=PENDING\n"
        "- OCR does not block already passed text/manual UI evidence.\n\n"
        "## Classification\n\n"
        "Both live worker binaries are byte-equal to the final T183500Z candidate authority subtree. This increment is not sealed Desktop/Orchestrator process E2E and introduces no new gate.\n"
    )


def write_outputs(result: dict[str, Any], output_root: Path) -> tuple[Path, Path]:
    require(not output_root.exists(), f"append-only output already exists: {output_root}")
    output_root.mkdir(parents=True)
    receipt = output_root / "external-increment-quality-receipt.json"
    readable = output_root / "input-output-index.md"
    receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    readable.write_text(render_readable(result), encoding="utf-8")
    return receipt, readable


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--fixture", type=Path, default=DEFAULT_FIXTURE)
    parser.add_argument("--output-root", type=Path)
    args = parser.parse_args()
    fixture = common.load_json(args.fixture)
    result = evaluate(fixture)
    result["fixtureValidation"] = validate_fixtures()
    if args.output_root:
        receipt, readable = write_outputs(result, args.output_root.resolve())
        print(json.dumps({"status": result["status"], "receipt": common.evidence(receipt), "readable": common.evidence(readable)}, ensure_ascii=False, indent=2))
    else:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
