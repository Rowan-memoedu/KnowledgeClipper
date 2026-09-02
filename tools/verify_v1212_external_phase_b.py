from __future__ import annotations

import argparse
import hashlib
import json
import os
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator

import verify_settings_control_plane as settings_quality


PROJECT_ROOT = Path(__file__).resolve().parents[1]
FIXTURE_ROOT = PROJECT_ROOT / "tests" / "Quality.Gates" / "fixtures" / "v1212-external-phase-b"
FIXTURE_SCHEMA = FIXTURE_ROOT / "v1212-external-phase-b.schema.json"
DEFAULT_FIXTURE = FIXTURE_ROOT / "valid" / "phase-b-084700.json"


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def io_path(path: Path) -> Path:
    absolute = path.resolve()
    if os.name == "nt" and not str(absolute).startswith("\\\\?\\"):
        return Path("\\\\?\\" + str(absolute))
    return absolute


def file_exists(path: Path) -> bool:
    return io_path(path).is_file()


def directory_exists(path: Path) -> bool:
    return io_path(path).is_dir()


def load_json(path: Path) -> dict[str, Any]:
    try:
        return json.loads(io_path(path).read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        raise GateFailure(f"invalid JSON: {path}: {exc}") from exc


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with io_path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def inside_project(raw: str | Path, label: str, *, directory: bool = False) -> Path:
    value = Path(raw)
    path = value.resolve() if value.is_absolute() else (PROJECT_ROOT / value).resolve()
    require(path.is_relative_to(PROJECT_ROOT.resolve()), f"{label}: escaped project")
    require(directory_exists(path) if directory else file_exists(path), f"{label}: missing")
    return path


def resolve_ref(ref: dict[str, Any], label: str) -> Path:
    path = inside_project(ref["path"], label)
    require(sha256(path) == str(ref["sha256"]).upper(), f"{label}: SHA-256 mismatch")
    return path


def evidence(path: Path) -> dict[str, Any]:
    normal = path.resolve()
    return {
        "path": normal.relative_to(PROJECT_ROOT.resolve()).as_posix(),
        "bytes": io_path(normal).stat().st_size,
        "sha256": sha256(normal),
    }


def validate_fixtures() -> dict[str, Any]:
    schema = load_json(FIXTURE_SCHEMA)
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema)
    valid = sorted((FIXTURE_ROOT / "valid").glob("*.json"))
    invalid = sorted((FIXTURE_ROOT / "invalid").glob("*.json"))
    require(valid and invalid, "external Phase B fixture set incomplete")
    for path in valid:
        require(not list(validator.iter_errors(load_json(path))), f"valid fixture rejected: {path.name}")
    for path in invalid:
        require(bool(list(validator.iter_errors(load_json(path)))), f"invalid fixture accepted: {path.name}")
    return {"status": "PASS", "validAccepted": len(valid), "invalidRejected": len(invalid), "schema": evidence(FIXTURE_SCHEMA)}


def verify_attempt(job_root: Path, summary: dict[str, Any]) -> dict[str, Any]:
    result_path = (job_root / summary["resultPath"]).resolve()
    require(result_path.is_relative_to(job_root) and file_exists(result_path), f"{summary['stageId']}: result path invalid")
    require(sha256(result_path) == str(summary["resultSha256"]).upper(), f"{summary['stageId']}: result hash mismatch")
    request_path = result_path.parent / "request.json"
    require(file_exists(request_path), f"{summary['stageId']}: request missing")
    request = load_json(request_path)
    result = load_json(result_path)
    require(result["status"] == summary["status"] == "completed", f"{summary['stageId']}: non-completed result")
    require(request["attemptId"] == result["attemptId"] == summary["attemptId"], f"{summary['stageId']}: attempt identity mismatch")
    require(request["stageId"] == result["stageId"] == summary["stageId"], f"{summary['stageId']}: stage identity mismatch")
    require(request["workerId"] == result["worker"]["id"] == summary["workerId"], f"{summary['stageId']}: worker identity mismatch")
    require(result["worker"]["version"] == summary["workerVersion"], f"{summary['stageId']}: worker version mismatch")
    require(request["idempotencyKey"] == result["idempotencyKey"] == summary["idempotencyKey"], f"{summary['stageId']}: idempotency mismatch")
    require(request["operationId"] == result["operationId"], f"{summary['stageId']}: operation identity mismatch")
    output_evidence: list[dict[str, Any]] = []
    for output in result.get("outputs", []):
        manifest_path = (job_root / output["manifestPath"]).resolve()
        require(manifest_path.is_relative_to(job_root) and file_exists(manifest_path), f"{summary['stageId']}: output manifest invalid")
        require(sha256(manifest_path) == str(output["expectedSha256"]).upper(), f"{summary['stageId']}: output manifest hash mismatch")
        output_evidence.append({"kind": output["kind"], "artifactRef": output["artifactRef"], "manifest": evidence(manifest_path)})
    input_evidence: list[dict[str, Any]] = []
    for input_ref in request.get("inputRefs", []):
        manifest_path = (job_root / input_ref["manifestPath"]).resolve()
        require(manifest_path.is_relative_to(job_root) and file_exists(manifest_path), f"{summary['stageId']}: input manifest invalid")
        require(sha256(manifest_path) == str(input_ref["expectedSha256"]).upper(), f"{summary['stageId']}: input manifest hash mismatch")
        input_evidence.append({"kind": input_ref["kind"], "artifactRef": input_ref["artifactRef"], "manifest": evidence(manifest_path)})
    return {
        "stageId": summary["stageId"],
        "workerId": summary["workerId"],
        "workerVersion": summary["workerVersion"],
        "attemptId": summary["attemptId"],
        "status": "completed",
        "request": evidence(request_path),
        "result": evidence(result_path),
        "inputs": input_evidence,
        "outputs": output_evidence,
        "payload": result.get("payload", {}),
    }


def verify_path(
    path_name: str,
    run: dict[str, Any],
    expected_status: str,
    phase_root: Path,
    candidate_root: Path,
    candidate: dict[str, Any],
    authority: dict[str, Any],
) -> tuple[dict[str, Any], dict[str, Any]]:
    receipt_path = inside_project(run["receipt"], f"path {path_name} receipt")
    stdout_path = inside_project(run["stdout"], f"path {path_name} stdout")
    stderr_path = inside_project(run["stderr"], f"path {path_name} stderr")
    require(receipt_path.is_relative_to(phase_root), f"path {path_name}: receipt outside execution root")
    require(stdout_path.is_relative_to(phase_root) and stderr_path.is_relative_to(phase_root), f"path {path_name}: log outside execution root")
    path_receipt = load_json(receipt_path)
    require(run["path"] == path_receipt["path"] == path_name and run["exitCode"] == 0, f"path {path_name}: execution identity/exit mismatch")
    require(path_receipt["status"] == expected_status, f"path {path_name}: status mismatch")
    require(path_receipt["candidate"]["candidateId"] == candidate["candidateId"], f"path {path_name}: candidate id mismatch")
    require(path_receipt["candidate"]["manifestSha256"].upper() == candidate["manifestSha256"].upper(), f"path {path_name}: manifest mismatch")
    require(path_receipt["candidate"]["sealedTreeSha256"].upper() == candidate["sealedTreeSha256"].upper(), f"path {path_name}: sealed tree mismatch")
    require(path_receipt["authority"]["registryVersion"] == authority["registryVersion"], f"path {path_name}: authority version mismatch")
    require(path_receipt["authority"]["registrySha256"].upper() == authority["registrySha256"].upper(), f"path {path_name}: registry mismatch")
    core_path = inside_project(path_receipt["candidate"]["coreAssemblyPath"], f"path {path_name} Core assembly")
    require(sha256(core_path) == path_receipt["candidate"]["coreAssemblySha256"].upper(), f"path {path_name}: Core assembly hash mismatch")
    candidate_core = candidate_root / "KnowledgeClipper.Orchestrator.dll"
    require(file_exists(candidate_core) and sha256(candidate_core) == sha256(core_path), f"path {path_name}: harness Core is not byte-equal to candidate Core")
    require(not core_path.is_relative_to(candidate_root), f"path {path_name}: expected backend harness Core path, got candidate process path")
    if path_name == "C":
        boundary = path_receipt["credentialBoundary"]
        require(path_receipt["taskCreated"] is False and not path_receipt["attempts"] and path_receipt["downstreamStarted"] is False, "path C: task/downstream unexpectedly started")
        require(boundary["providerCalls"] == 0 and boundary["automaticRetryAllowed"] is False and boundary["secretPersisted"] is False, "path C: credential safety boundary mismatch")
        require(path_receipt["externalWrites"] == 0, "path C: unexpected external write")
        details = {
            "classification": "CONDITIONAL-CREDENTIAL-UNAVAILABLE",
            "input": path_receipt["input"],
            "providerCalls": 0,
            "attempts": [],
            "downstreamStarted": False,
            "externalWrites": 0,
        }
    else:
        strict = path_receipt["strictLayout"]
        require(strict["passed"] is True and strict["workerValidationRelaxed"] is False, f"path {path_name}: strict layout/worker validation mismatch")
        job_root = inside_project(strict["jobRoot"], f"path {path_name} job root", directory=True)
        require(job_root.is_relative_to(receipt_path.parent) and job_root.name == path_receipt["taskId"], f"path {path_name}: job root mismatch")
        attempts = [verify_attempt(job_root, item) for item in path_receipt["attempts"]]
        require(all(item["workerId"] in {"content.deepseek", "export.remnote", "speech.edge"} for item in attempts), f"path {path_name}: unexpected worker")
        require(path_receipt["deepSeekCredentialBoundary"] == {"present": True, "secretPersisted": False, "taskPackageReferences": 0}, f"path {path_name}: DeepSeek secret boundary mismatch")
        readable_path = receipt_path.parent / "readable-output.md"
        require(file_exists(readable_path), f"path {path_name}: readable output missing")
        remnote_attempts = [item for item in attempts if item["workerId"] == "export.remnote"]
        isolated_root = "8zNzfX45XLCSKoPSJ"
        for item in remnote_attempts:
            request = load_json(Path(item["request"]["path"]).is_absolute() and Path(item["request"]["path"]) or PROJECT_ROOT / item["request"]["path"])
            require(request["payload"]["target"]["rootRemId"] == isolated_root, f"path {path_name}: RemNote target escaped isolated root")
            require(item["payload"].get("readbackVerified") is True, f"path {path_name}: worker readback not verified")
        details = {
            "classification": "PASS-BACKEND-HARNESS-CANDIDATE-WORKER-BOUND",
            "taskId": path_receipt["taskId"],
            "input": path_receipt["input"],
            "attempts": attempts,
            "invocationCounts": path_receipt["invocationCounts"],
            "readableOutput": evidence(readable_path),
            "replay": path_receipt.get("replay"),
            "physicalPlayback": path_receipt.get("physicalPlayback"),
        }
        if path_name == "B":
            replay = path_receipt["replay"]
            require(replay["status"] == "completed" and replay["payload"]["outcome"] == "already_applied" and replay["payload"]["readbackVerified"] is True, "path B: idempotent replay mismatch")
        if path_name == "D":
            playback = path_receipt["physicalPlayback"]
            require(playback == {"physicalSpeakerInvocations": 2, "naturalCompletion": "idle_replayable", "replayPolicy": "from_start", "muted": False, "simulated": False}, "path D: physical playback mismatch")
            playback_attempts = [item for item in attempts if item["stageId"] == "speech-playback"]
            require(len(playback_attempts) == 2 and playback_attempts[0]["attemptId"] != playback_attempts[1]["attemptId"], "path D: playback was reused instead of restarted")
    common = {
        "path": path_name,
        "status": expected_status,
        "receipt": evidence(receipt_path),
        "stdout": evidence(stdout_path),
        "stderr": evidence(stderr_path),
        "coreExecution": {
            "classification": "backend-harness-candidate-core-byte-equal",
            "assembly": evidence(core_path),
            "candidateAssembly": evidence(candidate_core),
            "candidateDesktopOrchestratorProcess": False,
        },
    }
    return {**common, **details}, path_receipt


def evaluate(fixture: dict[str, Any]) -> dict[str, Any]:
    expected_policy = {"classification": "backend-harness-candidate-worker-bound", "desktopProcessE2E": False, "userHandTestEntrypoint": False, "rerunExternalServices": False}
    require(fixture["policy"] == expected_policy, "external qualification policy weakened")
    base_path = resolve_ref(fixture["baseQualityReceipt"], "base quality receipt")
    base = load_json(base_path)
    candidate = fixture["candidate"]
    authority = fixture["authority"]
    require(base["candidateBound"] is True and base["candidateHash"] == candidate["manifestSha256"], "base offline candidate binding mismatch")
    require(base["desktop"]["fullInventory"]["treeSha256"] == candidate["sealedTreeSha256"], "base sealed tree mismatch")
    require(base["desktop"]["userHandTestEntrypoint"] is False, "base receipt already exposed hand-test entrypoint")
    execution_path = resolve_ref(fixture["executionReceipt"], "Phase B execution receipt")
    execution = load_json(execution_path)
    candidate_root = inside_project(execution["candidate"]["root"], "candidate root", directory=True)
    phase_root = execution_path.parent
    manifest_path = inside_project(execution["candidate"]["manifestPath"], "candidate manifest")
    require(sha256(manifest_path) == candidate["manifestSha256"], "candidate manifest drifted")
    require(execution["candidate"]["manifestSha256"] == candidate["manifestSha256"], "execution candidate hash mismatch")
    require(execution["candidate"]["sealed"]["treeSha256"] == candidate["sealedTreeSha256"], "execution sealed tree mismatch")
    registry_path = inside_project(execution["authority"]["registryPath"], "embedded registry")
    checkpoint_path = inside_project(execution["authority"]["checkpointPath"], "embedded authority checkpoint")
    require(sha256(registry_path) == authority["registrySha256"] and sha256(checkpoint_path) == authority["checkpointSha256"], "embedded authority drifted")
    require(execution["authority"]["registryVersion"] == authority["registryVersion"], "authority version mismatch")
    require(execution["secretsWritten"] == 0, "execution receipt reports persisted secrets")
    runs = {item["path"]: item for item in execution["runs"]}
    require(set(runs) == {"A", "B", "C", "D"}, "Phase B path set mismatch")
    paths: dict[str, Any] = {}
    raw: dict[str, Any] = {}
    for path_name, expected_status in fixture["expectedPaths"].items():
        paths[path_name], raw[path_name] = verify_path(path_name, runs[path_name], expected_status, phase_root, candidate_root, candidate, authority)
    anki = execution["preflight"]["anki"]
    require(anki["state"] == "service-unreachable" and anki["writes"] == 0 and anki["externalMutation"] is False and anki["automaticRetryPerformed"] is False, "Anki conditional boundary mismatch")
    require(all(not any(attempt.get("workerId") == "export.anki" for attempt in raw[name].get("attempts", [])) for name in raw), "Anki worker unexpectedly ran")
    secret_scan = settings_quality.scan_runtime_tree_for_secrets(phase_root)
    require(secret_scan["findingCount"] == 0, "Phase B evidence contains plaintext secret")
    harness_path = inside_project(execution["harness"]["path"], "Phase B harness")
    require(sha256(harness_path) == execution["harness"]["sha256"], "Phase B harness hash mismatch")
    return {
        "schemaVersion": "knowledge-clipper-v1212-external-qualification.v1",
        "generatedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "CONDITIONAL-BACKEND-HARNESS-CANDIDATE-WORKER-BOUND-PASS-DESKTOP-PROCESS-E2E-PENDING",
        "candidateBound": True,
        "candidateHash": candidate["manifestSha256"],
        "sealedTreeSha256": candidate["sealedTreeSha256"],
        "authority": authority,
        "classification": {
            "level": "backend-harness-candidate-worker-bound",
            "candidateCoreAssemblyByteEqual": True,
            "candidateDesktopOrchestratorProcessE2E": False,
            "reason": "PhaseB.Canary loaded a byte-equal shared Core DLL outside the sealed root; the Desktop-contained Orchestrator process did not create these tasks",
        },
        "baseOfflineGate": evidence(base_path),
        "executionReceipt": evidence(execution_path),
        "harness": evidence(harness_path),
        "paths": paths,
        "destinations": {
            "remnote": {
                "status": "PASS-WORKER-READBACK-ISOLATED-TARGET",
                "rootRemId": execution["isolatedDestinations"]["remnote"]["rootRemId"],
                "formalClipRootUsed": execution["isolatedDestinations"]["remnote"]["formalClipRootUsed"],
                "workerReadbackVerified": True,
                "controllerMcpReadbackReceipt": None,
                "controllerMcpAttestationNotUsedForMachinePass": True,
            },
            "anki": {
                "status": "CONDITIONAL-SERVICE-UNREACHABLE",
                "deck": execution["isolatedDestinations"]["anki"]["deck"],
                "realWorkerRuns": 0,
                "writes": 0,
                "automaticRetryPerformed": False,
            },
        },
        "secretScan": secret_scan,
        "boundaries": {
            "mineru": "conditional-credential-unavailable-provider-calls-0",
            "anki": "conditional-service-unreachable-writes-0",
            "physicalDesktopCapture": "pending-external-app-focus-clipboard-screenshot-mixed-dpi",
            "desktopProcessE2E": "pending-candidate-contained-orchestrator-new-task",
        },
        "userHandTestEntrypoint": False,
        "releaseGo": False,
        "externalServicesRerunByQuality": False,
        "fixtureValidation": validate_fixtures(),
    }


def render_index(result: dict[str, Any]) -> str:
    paths = result["paths"]
    lines = [
        "# KnowledgeClipper v1.2.12 external qualification index",
        "",
        f"- Status: `{result['status']}`",
        f"- CandidateHash: `{result['candidateHash']}`",
        "- Classification: `backend-harness-candidate-worker-bound`; this is not Desktop-contained Orchestrator process E2E.",
        "",
        "## Original input -> observed output",
        "",
    ]
    for name in ("A", "B", "C", "D"):
        item = paths[name]
        if name == "C":
            lines.append(f"- C: `{item['input']['text']}` + `{item['input']['syntheticImage']}` -> credential unavailable; MinerU/provider calls 0; no task, downstream, or write.")
            continue
        remnote = [attempt for attempt in item["attempts"] if attempt["workerId"] == "export.remnote"]
        remnote_values = [attempt["payload"] for attempt in remnote]
        if name == "D":
            lines.append(f"- D: `{item['input']['text']}` -> Content completed -> isolated RemNote `{remnote_values[0].get('documentRemId')}` readback=true -> Edge synth -> physical playback twice, both natural completion.")
        elif name == "B":
            lines.append(f"- B: `{item['input']['text']}` -> Content completed -> isolated RemNote date `{remnote_values[0].get('dateRemId')}` / clip `{remnote_values[0].get('clipRemId')}` -> replay `already_applied`, same IDs, readback=true.")
        else:
            lines.append(f"- A: `{item['input']['text']}` -> Content completed -> isolated RemNote document `{remnote_values[0].get('documentRemId')}` readback=true; Anki not run because its service was unavailable.")
    lines.extend([
        "",
        "## Honest boundaries",
        "",
        "- Anki: service unreachable; one read-only preflight action, worker runs 0, writes 0; not PASS.",
        "- MinerU: credential unavailable; provider calls 0; not PASS.",
        "- The controller reported a separate RemNote MCP readback, but no immutable receipt was supplied to this gate; it is not used for machine PASS.",
        "- Physical Desktop capture/focus/clipboard/screenshot/mixed-DPI and candidate-contained Orchestrator new-task E2E remain pending.",
        "- `userHandTestEntrypoint=false`; release GO remains false.",
    ])
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify v1.2.12 external Phase B receipts without rerunning external services or the sealed offline tree.")
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
        require(not root.exists(), "append-only output root already exists")
        root.mkdir(parents=True)
        receipt = root / "external-qualification-receipt.json"
        index = root / "input-output-index.md"
        receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        index.write_text(render_index(result), encoding="utf-8")
        artifacts = {"root": root.relative_to(PROJECT_ROOT).as_posix(), "receipt": evidence(receipt), "readableIndex": evidence(index)}
    print(json.dumps({"result": result, "artifacts": artifacts}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
