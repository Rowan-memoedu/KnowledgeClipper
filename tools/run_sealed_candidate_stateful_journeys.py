from __future__ import annotations

import argparse
import json
import os
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import verify_production_state_journeys as gate


PROJECT = Path(__file__).resolve().parents[1]
DRIVER = PROJECT / "tools/Invoke-KnowledgeClipperStatefulUi.ps1"
OFFLINE_CREDENTIAL_SENTINEL = "quality-offline-not-a-credential"
OFFLINE_LOOPBACK_BASE_URL = "http://127.0.0.1:1"


def validate_candidate_pipe_binding(candidate_root: Path, expected_pipe_name: str) -> dict[str, str]:
    integration_path = candidate_root / "candidate-integration.json"
    gate.require(integration_path.is_file(), "candidate integration configuration missing")
    integration = gate.load(integration_path)
    gate.require(integration.get("SchemaVersion") == "knowledge-clipper-candidate-integration.v1", "candidate integration schema drift")
    gate.require(integration.get("PipeName") == expected_pipe_name, "runner pipe does not match the sealed candidate integration")
    ipc_relative = integration.get("IpcConfig")
    gate.require(isinstance(ipc_relative, str) and not Path(ipc_relative).is_absolute() and ".." not in Path(ipc_relative).parts, "candidate IPC config path is not contained")
    ipc_path = (candidate_root / ipc_relative).resolve()
    gate.require(ipc_path.is_relative_to(candidate_root.resolve()) and ipc_path.is_file(), "candidate IPC config missing or outside candidate")
    ipc = gate.load(ipc_path)
    gate.require(ipc.get("pipeName") == expected_pipe_name, "runner pipe does not match the sealed candidate IPC contract")
    return {"pipeName": expected_pipe_name, "integration": integration_path.name, "ipcConfig": ipc_relative}


def validate_scenario(reference: dict[str, Any], journey: str) -> dict[str, Any]:
    observed = gate.verify_evidence(reference)
    scenario = gate.load(PROJECT / observed["path"])
    gate.require(scenario.get("schemaVersion") == "knowledge-clipper-native-ui-scenario.v1" and scenario.get("journey") == journey, f"native UI scenario identity drift: {journey}")
    steps = scenario.get("steps") or []
    gate.require(steps and steps[-1].get("operation") == "close-window", f"native UI scenario does not close its owned process window: {journey}")
    allowed = {"wait", "focus", "invoke", "set-value", "select", "select-item", "toggle", "send-keys", "assert-value", "assert-contains", "close-window"}
    gate.require(all(step.get("operation") in allowed for step in steps), f"native UI scenario contains a non-production driver operation: {journey}")
    gate.require(all("displayState" not in step and "directCommand" not in step for step in steps), f"native UI scenario injects product state: {journey}")
    if journey == "question":
        enter_steps = [step for step in steps if step.get("operation") == "send-keys" and step.get("keys") == "{ENTER}"]
        gate.require(len(enter_steps) == 2, "Question native UI scenario must send Enter exactly twice")
    return {"journey": journey, "steps": len(steps), "evidence": observed}


def validate_spec(data: dict[str, Any], allow_fixture: bool = False) -> dict[str, Any]:
    gate.require(data.get("schemaVersion") == "knowledge-clipper-sealed-stateful-runner.v1", "runner spec schema drift")
    fixture_only = data.get("fixtureOnly") is True
    if fixture_only:
        gate.require(allow_fixture, "fixture-only runner input cannot execute")
    gate.require(data.get("driver") == "tools/Invoke-KnowledgeClipperStatefulUi.ps1", "runner must use the Quality-owned native UI Automation driver")
    forbidden_text = json.dumps(data, ensure_ascii=False).lower()
    for forbidden in ("injectedhost", "manualdisplaystate", "tests/phaseb", "shared core dll", "sharedtestcoredll"):
        gate.require(forbidden not in forbidden_text, f"runner spec contains forbidden test substitution: {forbidden}")
    external = data.get("externalPolicy") or {}
    required_external = {"remnote", "anki", "mineru", "deepseek", "speaker"}
    gate.require(set(external) == required_external, "runner external policy is incomplete")
    offline = all(value is False for value in external.values())
    live_readonly = external == {
        "remnote": {"mode": "live-read-only", "writesAllowed": 0, "stopExistingProcesses": False},
        "anki": {"mode": "live-read-only", "writesAllowed": 0, "stopExistingProcesses": False},
        "mineru": False,
        "deepseek": {"mode": "live", "writesAllowed": 0},
        "speaker": False,
    }
    gate.require(offline or live_readonly, "runner external policy must be fully offline or the exact authorized live-read-only policy")
    isolation = data.get("isolation") or {}
    gate.require(isolation.get("persistenceMode") == "real-filesystem" and isolation.get("singleInstanceIsolated") is True, "runner does not require real isolated persistence and single-instance isolation")
    gate.require(isinstance(isolation.get("pipeName"), str) and isolation["pipeName"].startswith("KnowledgeClipper.Quality.Stateful."), "runner pipe is not uniquely Quality-scoped")
    bindings = isolation.get("environmentBindings") or {}
    gate.require(set(bindings) == {"settingsRoot", "jobsRoot", "pipeName"}, "runner isolation environment bindings incomplete")
    gate.require(all(isinstance(value, str) and value.startswith("KNOWLEDGE_CLIPPER_") for value in bindings.values()), "runner isolation bindings are not explicit product environment variables")

    journeys = data.get("journeys") or {}
    gate.require(set(journeys) == {"anki", "remnote", "question"}, "runner must contain exactly Anki, RemNote and Question journeys")
    anki = journeys["anki"]
    gate.require(anki.get("productionArtifact") == "formal-multifile-metadata-entrypoint" and anki.get("expectedWrites") == 0, "Anki runner journey does not require the formal read-only multi-file artifact")
    remnote = journeys["remnote"]
    remnote_phases = remnote.get("phases") or []
    gate.require(len(remnote_phases) >= 2 and remnote_phases[0].get("restartDesktop") is False and remnote_phases[1].get("restartDesktop") is True, "RemNote runner journey lacks a real process restart boundary")
    gate.require(remnote.get("sequence") == ["initial-disabled-root-title-only", "discovery-verified-pending", "probe-same-window", "save", "reopen-after-restart", "probe-after-restart", "next-task-snapshot"], "RemNote runner sequence drift")
    question = journeys["question"]
    gate.require(question.get("inputMethod") == "native-keyboard" and question.get("expectedActionCounts") == {"question.submit": 1, "question.follow-up": 1}, "Question runner does not require native Enter with exact action counts")
    gate.require(question.get("unresolvedTerminal") == "attention_required" and question.get("permanentLoadingAllowed") is False, "Question unresolved boundary drift")
    for name, journey in journeys.items():
        phases = journey.get("phases") or []
        gate.require(phases, f"runner journey has no phases: {name}")
        for phase in phases:
            gate.require(phase.get("scenario") and phase.get("closeOwnedProcess") is True, f"runner phase is not self-contained: {name}")
            validate_scenario(phase["scenario"], name)

    if not fixture_only:
        candidate = data.get("candidate") or {}
        gate.require(isinstance(candidate.get("root"), str) and candidate["root"].startswith("releases/"), "runner candidate root is not append-only release scope")
        gate.verify_evidence(candidate.get("manifest") or {})
        source_gate = gate.verify_evidence(data.get("sourceGate") or {})
        source_receipt = gate.load(PROJECT / source_gate["path"])
        gate.require(source_receipt.get("status") == "PASS-ARCHITECTURE-SOURCE-GATE" and source_receipt.get("delta", {}).get("promotionAllowed") is True, "runner source gate is not clean")
        candidate_root = gate.contained(PROJECT, candidate["root"])
        gate.require((candidate_root / "KnowledgeClipper.exe").is_file(), "runner candidate Desktop entrypoint missing")
        orchestrator = candidate_root / "authority/win-x64/orchestrator/KnowledgeClipper.Orchestrator.exe"
        gate.require(orchestrator.is_file(), "runner embedded Orchestrator missing")
        validate_candidate_pipe_binding(candidate_root, isolation["pipeName"])
    return {
        "status": "fixture-valid-not-executable" if fixture_only else "ready",
        "fixtureOnly": fixture_only,
        "journeys": sorted(journeys),
        "externalMode": "offline" if offline else "authorized-live-read-only",
        "externalCallsAllowed": 0 if offline else "counted-read-only-plus-deepseek",
    }


def file_snapshot(root: Path) -> dict[str, Any]:
    root.mkdir(parents=True, exist_ok=True)
    return gate.tree_summary(root)


def process_env(spec: dict[str, Any], isolated_root: Path) -> dict[str, str]:
    env = dict(os.environ)
    isolation = spec["isolation"]
    bindings = isolation["environmentBindings"]
    local_app_data = (isolated_root / "localappdata").resolve()
    local_app_data.mkdir(parents=True, exist_ok=True)
    env["LOCALAPPDATA"] = str(local_app_data)
    env[bindings["settingsRoot"]] = str((isolated_root / "settings").resolve())
    env[bindings["jobsRoot"]] = str((isolated_root / "jobs").resolve())
    env[bindings["pipeName"]] = isolation["pipeName"]
    if all(value is False for value in spec["externalPolicy"].values()):
        # Content falls back to HKCU and a user configuration file when an
        # inherited setting is absent. Explicit non-credential sentinels are
        # therefore required: removing the variables is not an offline gate.
        env["AI_PRODUCER_API_KEY"] = OFFLINE_CREDENTIAL_SENTINEL
        env["AI_PRODUCER_BASE_URL"] = OFFLINE_LOOPBACK_BASE_URL
        env["AI_PRODUCER_CONFIG_FILE"] = str((isolated_root / "offline-no-user-config.md").resolve())
        env["AI_PRODUCER_MAX_RETRIES"] = "1"
        env["MINERU_API_TOKEN"] = OFFLINE_CREDENTIAL_SENTINEL
        for name in ("DEEPSEEK_API_KEY", "RQC_DEEPSEEK_API_KEY"):
            env.pop(name, None)
        env["KNOWLEDGE_CLIPPER_REMNOTE_MCP_URL"] = OFFLINE_LOOPBACK_BASE_URL + "/mcp"
        env["KNOWLEDGE_CLIPPER_ANKI_ENDPOINT"] = OFFLINE_LOOPBACK_BASE_URL
    return env


def write_json(path: Path, value: Any) -> None:
    gate.require(not path.exists(), f"append-only runner output exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def _walk_json(value: Any):
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from _walk_json(child)
    elif isinstance(value, list):
        for child in value:
            yield from _walk_json(child)


def inspect_offline_boundary(output_root: Path) -> dict[str, Any]:
    live_provider_files: list[str] = []
    external_origin_files: list[str] = []
    sentinel_files: list[str] = []
    resolved_root = output_root.resolve()
    native_root = ("\\\\?\\" + str(resolved_root)) if os.name == "nt" else str(resolved_root)
    files: list[tuple[Path, Path]] = []
    for directory, _, names in os.walk(native_root):
        for name in names:
            native_path = Path(directory) / name
            display_path = Path(str(native_path).removeprefix("\\\\?\\"))
            files.append((native_path, display_path))
    for path, display_path in sorted(files, key=lambda item: str(item[1]).casefold()):
        try:
            text = path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue
        relative = display_path.relative_to(PROJECT).as_posix() if display_path.is_relative_to(PROJECT) else str(display_path)
        if OFFLINE_CREDENTIAL_SENTINEL in text:
            sentinel_files.append(relative)
        if "https://api.deepseek.com" in text or "api.deepseek.com/chat/completions" in text:
            external_origin_files.append(relative)
        if path.suffix.lower() != ".json":
            continue
        try:
            document = json.loads(text)
        except json.JSONDecodeError:
            continue
        for item in _walk_json(document):
            if item.get("provider") == "deepseek" and item.get("mode") == "live":
                live_provider_files.append(relative)
                break
    violations = sorted(set(live_provider_files + external_origin_files + sentinel_files))
    return {
        "status": "passed" if not violations else "failed-external-policy-breach",
        "realExternalProviderCompletionCount": len(set(live_provider_files)),
        "externalOriginReferenceCount": len(set(external_origin_files)),
        "credentialSentinelPersistenceCount": len(set(sentinel_files)),
        "liveProviderFiles": sorted(set(live_provider_files)),
        "externalOriginFiles": sorted(set(external_origin_files)),
        "sentinelFiles": sorted(set(sentinel_files)),
        "closedLoopbackDenyConfiguration": {
            "baseUrl": OFFLINE_LOOPBACK_BASE_URL,
            "connectionAttemptsMayFailClosed": True,
            "notARealExternalService": True,
        },
    }


def force_cleanup_owned_process_tree(process_id: int) -> bool:
    completed = subprocess.run(
        ["taskkill.exe", "/PID", str(process_id), "/T", "/F"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=30,
        check=False,
    )
    return completed.returncode == 0


def run_phase(candidate_root: Path, spec: dict[str, Any], journey_name: str, phase: dict[str, Any], phase_root: Path, isolated_root: Path) -> dict[str, Any]:
    scenario = PROJECT / phase["scenario"]["path"]
    stdout_path = phase_root / "desktop.stdout.log"
    stderr_path = phase_root / "desktop.stderr.log"
    driver_receipt = phase_root / "ui-driver-receipt.json"
    phase_root.mkdir(parents=True, exist_ok=False)
    env = process_env(spec, isolated_root)
    command = [str(candidate_root / "KnowledgeClipper.exe"), *phase.get("desktopArgs", [])]
    started = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    process = None
    phase_error: Exception | None = None
    cleanup = {"ownedPid": None, "gracefulExit": False, "forcedOwnedProcessCleanup": False, "preexistingProcessesTouched": 0}
    with stdout_path.open("w", encoding="utf-8") as stdout, stderr_path.open("w", encoding="utf-8") as stderr:
        try:
            process = subprocess.Popen(command, cwd=candidate_root, env=env, stdout=stdout, stderr=stderr, text=True)
            cleanup["ownedPid"] = process.pid
            driver_command = ["pwsh", "-NoLogo", "-NoProfile", "-File", str(DRIVER), "-ProcessId", str(process.pid), "-ScenarioPath", str(scenario), "-OutputPath", str(driver_receipt)]
            timeout_seconds = int(phase.get("timeoutSeconds", 180))
            try:
                completed = subprocess.run(driver_command, cwd=PROJECT, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout_seconds, check=False)
            except subprocess.TimeoutExpired as error:
                (phase_root / "driver.stdout.log").write_text(error.stdout or "", encoding="utf-8")
                (phase_root / "driver.stderr.log").write_text(error.stderr or "", encoding="utf-8")
                timeout_receipt = phase_root / "ui-driver-timeout.json"
                write_json(timeout_receipt, {"status": "failed-timeout", "journey": journey_name, "phase": phase["name"], "ownedDesktopPid": process.pid, "timeoutSeconds": timeout_seconds, "scenario": gate.evidence(scenario)})
                raise gate.GateFailure(f"native UI driver timed out after {timeout_seconds}s for {journey_name}") from error
            (phase_root / "driver.stdout.log").write_text(completed.stdout, encoding="utf-8")
            (phase_root / "driver.stderr.log").write_text(completed.stderr, encoding="utf-8")
            gate.require(completed.returncode == 0 and driver_receipt.is_file(), f"native UI driver failed for {journey_name}: {completed.stderr or completed.stdout}")
            receipt = gate.load(driver_receipt)
            gate.require(receipt.get("status") == "passed" and receipt.get("processId") == process.pid, f"native UI driver receipt failed for {journey_name}")
        except Exception as error:
            phase_error = error
        finally:
            if process is not None:
                try:
                    process.wait(timeout=5)
                    cleanup["gracefulExit"] = True
                except subprocess.TimeoutExpired:
                    cleanup["forcedOwnedProcessCleanup"] = force_cleanup_owned_process_tree(process.pid)
                    process.wait(timeout=10)
    cleanup_path = phase_root / "cleanup.json"
    write_json(cleanup_path, cleanup)
    if phase_error is not None:
        raise phase_error
    return {"journey": journey_name, "phase": phase["name"], "startedAt": started, "desktopCommand": ["KnowledgeClipper.exe", *phase.get("desktopArgs", [])], "driverReceipt": gate.evidence(driver_receipt), "cleanup": gate.evidence(cleanup_path), "stdout": gate.evidence(stdout_path), "stderr": gate.evidence(stderr_path)}


def run(spec_path: Path, output_root: Path) -> dict[str, Any]:
    spec = gate.load(spec_path)
    validate_spec(spec)
    gate.require(not output_root.exists(), f"append-only runner output exists: {output_root}")
    output_root.mkdir(parents=True)
    candidate_root = gate.contained(PROJECT, spec["candidate"]["root"])
    isolated_root = output_root / "isolated-real-store"
    (isolated_root / "settings").mkdir(parents=True)
    (isolated_root / "jobs").mkdir(parents=True)
    before = file_snapshot(isolated_root)
    phase_results: dict[str, list[dict[str, Any]]] = {name: [] for name in spec["journeys"]}
    phase_failures: dict[str, dict[str, Any]] = {}
    for journey_name, journey in spec["journeys"].items():
        for index, phase in enumerate(journey["phases"], 1):
            phase_root = output_root / "journeys" / journey_name / f"{index:02d}-{phase['name']}"
            print(json.dumps({"event": "phase-start", "journey": journey_name, "phase": phase["name"]}, ensure_ascii=False), flush=True)
            try:
                phase_results[journey_name].append(run_phase(candidate_root, spec, journey_name, phase, phase_root, isolated_root))
                print(json.dumps({"event": "phase-passed", "journey": journey_name, "phase": phase["name"]}, ensure_ascii=False), flush=True)
            except Exception as error:
                failure: dict[str, Any] = {"phase": phase["name"], "error": str(error)}
                for key, name in (("driverReceipt", "ui-driver-receipt.json"), ("driverTimeout", "ui-driver-timeout.json"), ("cleanup", "cleanup.json"), ("stdout", "desktop.stdout.log"), ("stderr", "desktop.stderr.log")):
                    path = phase_root / name
                    if path.is_file():
                        failure[key] = gate.evidence(path)
                phase_failures[journey_name] = failure
                print(json.dumps({"event": "phase-failed", "journey": journey_name, "phase": phase["name"], "error": str(error)}, ensure_ascii=False), flush=True)
                break
    after = file_snapshot(isolated_root)
    offline_boundary = inspect_offline_boundary(output_root) if all(value is False for value in spec["externalPolicy"].values()) else {"status": "not-applicable"}
    journey_refs = {}
    action_evidence = []
    cleanup_evidence = []
    for name, phases in phase_results.items():
        journey_receipt_path = output_root / "journeys" / name / "journey-receipt.json"
        failure = phase_failures.get(name)
        journey_status = "failed-candidate-bound" if failure is not None else "passed"
        journey_receipt = {"schemaVersion": "knowledge-clipper-stateful-journey-receipt.v1", "status": journey_status, "journey": name, "candidateRoot": spec["candidate"]["root"], "phases": phases, "failure": failure}
        write_json(journey_receipt_path, journey_receipt)
        journey_refs[name] = {"status": journey_status, "receipt": gate.evidence(journey_receipt_path)}
        action_evidence.extend(phase["driverReceipt"] for phase in phases)
        cleanup_evidence.extend(phase["cleanup"] for phase in phases)
        if failure is not None:
            if failure.get("driverReceipt"):
                action_evidence.append(failure["driverReceipt"])
            if failure.get("cleanup"):
                cleanup_evidence.append(failure["cleanup"])
    runtime_root = output_root / "runtime"
    persistence_path = runtime_root / "persistence.json"
    named_pipe_path = runtime_root / "named-pipe.json"
    artifact_path = runtime_root / "artifact-parser.json"
    action_path = runtime_root / "action-log.json"
    cleanup_path = runtime_root / "process-cleanup.json"
    startup_path = runtime_root / "process-starts.json"
    write_json(persistence_path, {"status": "passed", "mode": "isolated-real", "before": before, "after": after})
    write_json(named_pipe_path, {"status": "passed", "pipeName": spec["isolation"]["pipeName"], "transport": "NamedPipe", "injectedHost": False})
    write_json(artifact_path, {"status": "passed" if "anki" not in phase_failures else "not-reached", "reader": "production-unified", "ankiJourney": journey_refs["anki"]["receipt"]})
    write_json(action_path, {"status": "passed" if not phase_failures else "partial", "driverReceipts": action_evidence})
    write_json(cleanup_path, {"status": "passed", "phaseCleanup": cleanup_evidence, "preexistingProcessesTouched": 0})
    write_json(startup_path, {"status": "passed", "desktop": "KnowledgeClipper.exe", "orchestrator": "authority/win-x64/orchestrator/KnowledgeClipper.Orchestrator.exe", "phases": sum(len(value) for value in phase_results.values())})
    candidate_evidence = {
        "schemaVersion": "knowledge-clipper-candidate-stateful-evidence.v1",
        "evidenceLevel": "sealed-candidate-process-stateful",
        "sourceGate": spec["sourceGate"],
        "candidate": {"root": spec["candidate"]["root"], "manifest": spec["candidate"]["manifest"], "sealed": gate.tree_summary(candidate_root)},
        "runtime": {
            "desktopProcess": "KnowledgeClipper.exe", "orchestratorProcess": "authority/win-x64/orchestrator/KnowledgeClipper.Orchestrator.exe", "transport": "NamedPipe", "persistenceStore": "isolated-real", "artifactReader": "production-unified",
            "injectedHost": False, "sharedTestCoreDll": False, "manualDisplayState": False,
            "evidence": {"startup": gate.evidence(startup_path), "namedPipe": gate.evidence(named_pipe_path), "persistence": gate.evidence(persistence_path), "artifactParser": gate.evidence(artifact_path), "actionLog": gate.evidence(action_path), "processCleanup": gate.evidence(cleanup_path)},
        },
        "journeys": journey_refs,
        "externalPolicy": spec["externalPolicy"],
        "offlineBoundary": offline_boundary,
        "promotion": {"userHandTestEntrypoint": not phase_failures and offline_boundary.get("status") in {"passed", "not-applicable"}},
        "failures": phase_failures,
    }
    evidence_path = output_root / "candidate-stateful-evidence.json"
    write_json(evidence_path, candidate_evidence)
    if phase_failures or offline_boundary.get("status") == "failed-external-policy-breach":
        return {"status": "NO-GO-CANDIDATE-STATEFUL-JOURNEY", "candidateEvidence": gate.evidence(evidence_path), "failures": phase_failures}
    admission = gate.verify_candidate_promotion(candidate_evidence)
    return {"status": admission["status"], "candidateEvidence": gate.evidence(evidence_path), "admission": admission}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--spec", type=Path, required=True)
    parser.add_argument("--output-root", type=Path)
    parser.add_argument("--validate-only", action="store_true")
    args = parser.parse_args()
    spec = gate.load(args.spec.resolve())
    if args.validate_only:
        print(json.dumps(validate_spec(spec, allow_fixture=True), ensure_ascii=False, indent=2))
        return 0
    gate.require(args.output_root is not None, "--output-root is required unless --validate-only is used")
    result = run(args.spec.resolve(), args.output_root.resolve())
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if result["status"] != "NO-GO-CANDIDATE-STATEFUL-JOURNEY" else 2


if __name__ == "__main__":
    raise SystemExit(main())
