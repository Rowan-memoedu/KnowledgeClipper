from __future__ import annotations

import argparse
import json
import subprocess
from pathlib import Path
from typing import Any


class CanaryError(RuntimeError):
    pass


def run_json(command: list[str], cwd: Path, timeout: int = 45) -> dict[str, Any]:
    completed = subprocess.run(
        command,
        cwd=str(cwd),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
        check=False,
    )
    if completed.returncode != 0:
        raise CanaryError(
            f"command failed ({completed.returncode}): {command}: "
            f"{completed.stderr.strip() or completed.stdout.strip()}"
        )
    try:
        value = json.loads(completed.stdout.lstrip("\ufeff"))
    except json.JSONDecodeError as exc:
        raise CanaryError(f"command did not emit one JSON object: {command}: {exc}") from exc
    if not isinstance(value, dict):
        raise CanaryError(f"command output is not a JSON object: {command}")
    return value


def write_once(path: Path, value: dict[str, Any]) -> None:
    if path.exists():
        raise CanaryError(f"append-only evidence already exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise CanaryError(message)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--checkpoint-id", required=True)
    parser.add_argument("--authority-tree", required=True)
    parser.add_argument("--registry-version", required=True)
    parser.add_argument("--single-instance-regression-log", type=Path)
    args = parser.parse_args()

    candidate = args.candidate.resolve()
    evidence = args.evidence.resolve()
    executable = candidate / "KnowledgeClipper.exe"
    orchestrator = candidate / "authority/win-x64/orchestrator/KnowledgeClipper.Orchestrator.exe"
    registry = candidate / "authority/metadata/worker-registry.v1.json"
    release_root = candidate / "authority/win-x64"
    for required in (executable, orchestrator, registry):
        require(required.is_file(), f"candidate input is missing: {required}")

    startup = run_json([str(executable), "--startup-diagnostic"], candidate)
    require(
        startup.get("result") == "PASS"
        and startup.get("candidateOrchestratorAutoStart") is True
        and startup.get("candidateOrchestratorPathsVerified") is True
        and startup.get("authorityCheckpointId") == args.checkpoint_id
        and str(startup.get("authorityTreeSha256", "")).upper() == args.authority_tree.upper()
        and startup.get("externalConnections") == 0
        and startup.get("namedPipeInvoked") is False,
        f"safe startup diagnostic failed: {startup}",
    )
    write_once(evidence / "startup-diagnostic.stdout.log", startup)

    single = run_json([str(executable), "--single-instance-diagnostic"], candidate)
    isolated_single_instance_pass = False
    if args.single_instance_regression_log:
        regression_text = args.single_instance_regression_log.resolve().read_text(encoding="utf-8")
        isolated_single_instance_pass = (
            "PASS existing canonical mutex is detected instead of silently treated as a successful launch" in regression_text
            and "PASS single-instance collision declares a visible feedback mode" in regression_text
            and "DESKTOP_INTEGRATION_CANARY PASS assertions=8" in regression_text
        )
    observed_existing_instance = single.get("existingInstanceDetected") is True
    require(
        single.get("userFeedbackMode") == "activate-existing-window-and-message-box"
        and (
            (observed_existing_instance and int(single.get("existingProcessId", 0)) > 0 and bool(single.get("existingProcessPath")))
            or (not observed_existing_instance and isolated_single_instance_pass)
        ),
        f"single-instance non-silent regression failed: {single}",
    )
    write_once(evidence / "single-instance-diagnostic.stdout.log", single)

    registry_result = run_json(
        [
            str(orchestrator),
            "validate-registry",
            "--registry",
            str(registry),
            "--release-root",
            str(release_root),
        ],
        candidate,
        timeout=90,
    )
    require(
        registry_result.get("status") == "valid"
        and registry_result.get("registryVersion") == args.registry_version
        and registry_result.get("workers") == 5,
        f"authority registry validation failed: {registry_result}",
    )
    write_once(evidence / "authority-validate-registry.log", registry_result)

    jobs = evidence / "integration-jobs"
    integration = run_json([str(executable), "--integration-startup-canary", str(jobs)], candidate, timeout=90)
    require(
        integration.get("result") == "PASS"
        and integration.get("orchestratorStartedByDesktop") is True
        and integration.get("namedPipeInvoked") is True
        and integration.get("action") == "task.status.query-recent"
        and integration.get("authorityCheckpointId") == args.checkpoint_id
        and str(integration.get("authorityTreeSha256", "")).upper() == args.authority_tree.upper()
        and integration.get("externalWorkerInvoked") is False
        and integration.get("physicalSpeakerInvoked") is False
        and integration.get("remNoteInvoked") is False
        and integration.get("ankiInvoked") is False
        and integration.get("mineruInvoked") is False,
        f"integrated startup/Named Pipe canary failed: {integration}",
    )
    write_once(evidence / "integration-startup-canary.stdout.log", integration)
    print(
        json.dumps(
            {
                "status": "PASS",
                "existingProcessId": single["existingProcessId"],
                "singleInstanceCollisionRegression": "observed-process" if observed_existing_instance else "isolated-mutex-pass",
                "integrationOrchestratorProcessId": integration["orchestratorProcessId"],
                "queryItemCount": integration["queryItemCount"],
                "workers": registry_result["workers"],
                "externalWorkersInvoked": False,
                "physicalSpeakerInvoked": False,
            },
            ensure_ascii=False,
        )
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (CanaryError, OSError, ValueError, subprocess.SubprocessError) as exc:
        print(json.dumps({"status": "FAIL", "error": str(exc)}, ensure_ascii=False))
        raise SystemExit(1)
