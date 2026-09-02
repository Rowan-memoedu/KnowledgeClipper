from __future__ import annotations

import argparse
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from verify_v1217_desktop_candidate import ev, file_size, inventory, is_file, load, rel, require, sha256_file, tab_tree


PROJECT = Path(__file__).resolve().parents[1]
AUTHORITY_ROOT = PROJECT / "releases/knowledge-clipper-authority-v1.2.18-20260831T085000Z/win-x64"
AUTHORITY_META = PROJECT / "contracts/authority/worker-registry/v1.2.18-20260831T085000Z"
QUALIFICATION = PROJECT / "canary/authority-v1.2.18-offline-qualification/20260831T085700Z/qualification-receipt.json"
FIRST_ROOT = PROJECT / "canary/sealed-live-ocr-anki-v1.2.18/20260831T090000Z"
SECOND_ROOT = PROJECT / "canary/sealed-live-ocr-anki-v1.2.18/20260831T091200Z"

EXPECTED = {
    "tree": "4BB31FC006661E72E285972C1FF6BA592D97B61F95AD127CE548077E99B7B88E",
    "files": 556,
    "bytes": 267800440,
    "registry": "C07266A9F4EB3E1A872C0C3CFCBD763315BBDB0F491DE5448A49DD606406C742",
    "checkpoint": "8D61B39DFF413A063E09145C2CBC6CC26B679F7CEEC67BE85CBF5B6796B639E0",
    "qualification": "36E863AF5FE980561F645C191945F4B56A842422D8947DAEBA84BCC0009B9F0E",
}


def evidence_from_absolute(value: dict[str, Any]) -> dict[str, Any]:
    path = Path(value["path"])
    require(is_file(path), f"referenced evidence missing: {path}")
    require(sha256_file(path) == str(value["sha256"]).upper(), f"referenced evidence SHA drift: {path}")
    if "bytes" in value:
        require(file_size(path) == int(value["bytes"]), f"referenced evidence bytes drift: {path}")
    return ev(path)


def verify_authority(output_root: Path) -> dict[str, Any]:
    rows = inventory(AUTHORITY_ROOT)
    require(len(rows) == EXPECTED["files"], "authority release file count drift")
    require(sum(item["bytes"] for item in rows) == EXPECTED["bytes"], "authority release bytes drift")
    require(tab_tree(rows) == EXPECTED["tree"], "authority release tree drift")
    registry = AUTHORITY_META / "worker-registry.v1.json"
    checkpoint = AUTHORITY_META / "checkpoint.json"
    require(sha256_file(registry) == EXPECTED["registry"], "authority registry SHA drift")
    require(sha256_file(checkpoint) == EXPECTED["checkpoint"], "authority checkpoint SHA drift")
    closure_path = output_root / "portable-schema-closure.json"
    completed = subprocess.run(
        ["python", str(PROJECT / "tools/validate_release_schema_closure.py"), "--release-root", str(AUTHORITY_ROOT), "--output", str(closure_path)],
        cwd=PROJECT,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=60,
        check=False,
    )
    require(completed.returncode == 0, f"portable schema closure failed: {completed.stderr or completed.stdout}")
    closure = load(closure_path)
    require(closure["status"] == "passed" and closure["schemasChecked"] == 62 and closure["localRefsChecked"] == 188 and not closure["failures"], "portable closure drift")
    source_artifact = PROJECT / "contracts/v1/schemas/common/artifact.schema.json"
    packed_artifact = AUTHORITY_ROOT / "contracts/v1/schemas/common/artifact.schema.json"
    require(sha256_file(source_artifact) == sha256_file(packed_artifact) == "14AAA3BA918B201280DF462677C39A373ACDF421C8DA06C1D4A7B6D619DA0E91", "packed frozen artifact schema drift")
    return {
        "status": "passed",
        "release": {"root": rel(AUTHORITY_ROOT), "files": len(rows), "bytes": sum(item["bytes"] for item in rows), "treeSha256": tab_tree(rows)},
        "registry": ev(registry),
        "checkpoint": ev(checkpoint),
        "portableSchemaClosure": ev(closure_path),
        "frozenArtifactSchema": {"source": ev(source_artifact), "packed": ev(packed_artifact), "equal": True},
    }


def verify_offline_qualification() -> dict[str, Any]:
    require(sha256_file(QUALIFICATION) == EXPECTED["qualification"], "offline qualification SHA drift")
    data = load(QUALIFICATION)
    require(data["status"] == "passed-offline-authority-qualified", "offline qualification status drift")
    require(data["validation"]["registry"]["status"] == "valid" and data["validation"]["registry"]["workers"] == 5, "offline registry result drift")
    require(len(data["workers"]) == 5, "offline worker descriptor count drift")
    checked = []
    for branch in ["registry", "checkpoint", "dependencyManifest", "assemblyReceipt", "assemblyPathGate"]:
        checked.append(evidence_from_absolute(data["authority"][branch]))
    checked.append(evidence_from_absolute(data["core"]["checkpoint"]))
    checked.append(evidence_from_absolute(data["core"]["runtimeEvidence"]))
    checked.append(evidence_from_absolute(data["validation"]["portableSchemaClosure"]["gate"]))
    require(all(data["boundaries"][key] == 0 for key in ["workerRunInvocations", "externalCalls", "mineruCalls", "ankiWrites", "remnoteWrites", "physicalSpeakerInvocations"]), "offline qualification side-effect boundary drift")
    return {"status": "passed", "receipt": ev(QUALIFICATION), "referencedEvidenceChecked": len(checked), "workers": data["workers"], "sideEffects": data["boundaries"]}


def verify_first_failure() -> dict[str, Any]:
    receipt = FIRST_ROOT / "failure-receipt.json"
    data = load(receipt)
    require(data["status"] == "failed" and data["authority"] == "1.2.18", "first sealed task failure classification drift")
    events = sorted((FIRST_ROOT / "jobs").rglob("events/*.json"))
    require(len(events) == 5, "first sealed task event count drift")
    event_data = [load(path) for path in events]
    failed = [item for item in event_data if item.get("type") == "command.execution_failed"]
    require(len(failed) == 1 and failed[0]["payload"]["errorCode"] == "settings.snapshot_invalid", "first task did not fail at settings snapshot preflight")
    require(not list((FIRST_ROOT / "jobs").rglob("attempt-*")), "first task unexpectedly started a worker attempt")
    return {
        "status": "HISTORICAL-PREFLIGHT-FAILURE",
        "receipt": ev(receipt),
        "eventsChecked": len(events),
        "errorCode": "settings.snapshot_invalid",
        "workerAttempts": 0,
        "externalCalls": 0,
        "retryPolicy": "do-not-retry-old-immutable-task",
    }


def verify_second_partial() -> dict[str, Any]:
    receipt = SECOND_ROOT / "initial-receipt.json"
    data = load(receipt)
    require(data["authority"]["version"] == "1.2.18", "second sealed task authority drift")
    require(data["projection"]["status"] == "partial" and data["status"] == "unexpected-terminal", "second sealed task terminal classification drift")
    checked = [evidence_from_absolute(data["authority"]["registry"]), evidence_from_absolute(data["authority"]["checkpoint"]), evidence_from_absolute(data["authority"]["orchestrator"]), evidence_from_absolute(data["task"]["manifest"]), evidence_from_absolute(data["task"]["taskSettings"])]
    workers = {item["worker"]["id"]: item for item in data["workers"]}
    require(set(workers) == {"image.ocr.mineru", "content.deepseek"}, "second task worker set drift")
    require(workers["image.ocr.mineru"]["worker"]["version"] == "1.2.4" and workers["image.ocr.mineru"]["status"] == "completed", "Image attempt drift")
    require(workers["content.deepseek"]["worker"]["version"] == "0.9.0" and workers["content.deepseek"]["status"] == "failed", "Content attempt drift")
    for item in workers.values():
        checked.append(evidence_from_absolute(item))
    content = load(Path(workers["content.deepseek"]["path"]))
    require(content["error"]["code"] == "content.gate-failed" and content["error"]["retryable"] is False, "Content coordinate failure semantics drift")
    require("before 无法在声明范围逐字定位" in content["error"]["message"], "Content coordinate failure detail drift")
    require(data["sideEffects"] == {"mineruSubmissions": 1, "ankiWrites": 0, "remnoteWrites": 0, "speakerInvocations": 0}, "second task side-effect accounting drift")
    return {
        "status": "PARTIAL-CONTENT-COORDINATE-GATE-NO-DOWNSTREAM-WRITES",
        "receipt": ev(receipt),
        "referencedEvidenceChecked": len(checked),
        "image": "completed:image.ocr.mineru@1.2.4",
        "content": {"status": "failed", "workerVersion": "0.9.0", "errorCode": content["error"]["code"], "retryable": False, "message": content["error"]["message"]},
        "sideEffects": data["sideEffects"],
        "productGap": "OCR unresolved must become waiting_input; current Content coordinate gate terminates partial before human action",
    }


def render(result: dict[str, Any]) -> str:
    return "\n".join([
        "# Authority v1.2.18 and sealed OCR increment",
        "",
        f"- Status: `{result['status']}`",
        f"- Authority release tree: `{result['checks']['authority']['release']['treeSha256']}`",
        "- Offline authority packaging: PASS (5/5 workers; 62 schemas / 188 local refs).",
        "- Historical 090000Z task: settings snapshot preflight failure, zero worker attempts and zero external calls; no retry.",
        "- New 091200Z task: one Image 1.2.4 MinerU submission completed; Content 0.9.0 failed its coordinate gate; task is partial, not waiting_input.",
        "- Downstream writes: Anki 0, RemNote 0; physical speaker 0.",
        "",
        "## Current product boundary",
        "",
        "Authority v1.2.18 is admitted offline, but OCR human continuation remains NO-GO: the Content coordinate failure must project to the Core waiting_input/human-resolution path in a new immutable task. This receipt does not authorize retrying either historical task.",
    ]) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-root", required=True)
    args = parser.parse_args()
    output_root = Path(args.output_root).resolve()
    require(not output_root.exists(), "append-only output root already exists")
    output_root.mkdir(parents=True)
    checks = {
        "authority": verify_authority(output_root),
        "offlineQualification": verify_offline_qualification(),
        "historicalPreflightFailure": verify_first_failure(),
        "newSealedPartial": verify_second_partial(),
    }
    result = {
        "schemaVersion": "knowledge-clipper-quality-authority-v1218-live-increment.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "AUTHORITY-PASS-OFFLINE-OCR-HUMAN-CONTINUATION-NO-GO",
        "candidateBound": False,
        "checks": checks,
        "promotion": {"authority": "PASS-OFFLINE", "ocrHumanContinuation": "NO-GO", "release": "NO-GO", "reason": "sealed task terminates partial at Content coordinate gate instead of waiting_input"},
        "sideEffectsByThisAudit": {"workerRuns": 0, "externalCalls": 0, "mineruCalls": 0, "ankiWrites": 0, "remnoteWrites": 0, "speakerInvocations": 0},
    }
    receipt = output_root / "quality-increment-receipt.json"
    readable = output_root / "READABLE.md"
    receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    readable.write_text(render(result), encoding="utf-8")
    print(json.dumps({"status": result["status"], "receipt": ev(receipt), "readable": ev(readable)}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
