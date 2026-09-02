from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator


PROJECT = Path(__file__).resolve().parents[1]
RUNNER = PROJECT / "tools/run_sealed_candidate_stateful_journeys.py"
DRIVER = PROJECT / "tools/Invoke-KnowledgeClipperStatefulUi.ps1"
VERIFIER = PROJECT / "tools/verify_production_state_journeys.py"
FIXTURE_ROOT = PROJECT / "tests/Quality.Gates/fixtures/sealed-candidate-stateful-runner"
CANDIDATE_SCHEMA = PROJECT / "tests/Quality.Gates/fixtures/production-state-journeys/candidate-stateful-evidence.schema.json"
DEFAULT_SOURCE_GATE = PROJECT / "canary/quality-architecture-burndown/20260901T094003Z-core-phase1-current-source/architecture-burndown-receipt.json"
TEST_FILES = [
    PROJECT / "tests/Quality.Gates/test_architecture_burndown.py",
    PROJECT / "tests/Quality.Gates/test_production_state_journey_gate.py",
    PROJECT / "tests/Quality.Gates/test_sealed_candidate_stateful_runner.py",
]


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def rel(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def evidence(path: Path) -> dict[str, Any]:
    return {"path": rel(path), "bytes": path.stat().st_size, "sha256": sha256_file(path)}


def run_command(command: list[str], log_root: Path, name: str) -> tuple[str, str]:
    completed = subprocess.run(command, cwd=PROJECT, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=300, check=False)
    stdout_path = log_root / f"{name}.stdout.log"
    stderr_path = log_root / f"{name}.stderr.log"
    stdout_path.write_text(completed.stdout, encoding="utf-8")
    stderr_path.write_text(completed.stderr, encoding="utf-8")
    require(completed.returncode == 0, f"tooling command failed: {name}")
    return completed.stdout, completed.stderr


def qualify(output_root: Path, source_gate_path: Path) -> dict[str, Any]:
    require(not output_root.exists(), f"append-only output exists: {output_root}")
    output_root.mkdir(parents=True)
    logs = output_root / "logs"
    logs.mkdir()
    source_gate = load(source_gate_path)
    source_clean = source_gate["status"] == "PASS-ARCHITECTURE-SOURCE-GATE" and source_gate["delta"]["promotionAllowed"] is True and source_gate["delta"]["currentViolationCount"] == 0
    source_blocked = source_gate["status"] == "NO-GO-ARCHITECTURE-VIOLATIONS-REMAIN" and source_gate["delta"]["promotionAllowed"] is False and source_gate["delta"]["currentViolationCount"] > 0
    require(source_clean or source_blocked, "source gate status/promotion fields are inconsistent")

    test_results = []
    total = 0
    for test_path in TEST_FILES:
        name = test_path.stem
        stdout, stderr = run_command([sys.executable, str(test_path), "-v"], logs, name)
        match = re.search(r"Ran (\d+) tests?", stdout + stderr)
        require(match is not None, f"test count missing: {test_path.name}")
        count = int(match.group(1))
        total += count
        test_results.append({"suite": rel(test_path), "tests": count, "status": "passed", "stdout": evidence(logs / f"{name}.stdout.log"), "stderr": evidence(logs / f"{name}.stderr.log")})

    validation_stdout, _ = run_command(
        [sys.executable, str(RUNNER), "--spec", str(FIXTURE_ROOT / "valid/future-runner.json"), "--validate-only"],
        logs,
        "runner-validate-only",
    )
    validation = json.loads(validation_stdout)
    require(validation["status"] == "fixture-valid-not-executable" and validation["externalCallsAllowed"] == 0, "runner fixture validation boundary drift")

    driver_stdout, _ = run_command(
        ["pwsh", "-NoLogo", "-NoProfile", "-Command", f"[void][scriptblock]::Create((Get-Content -LiteralPath '{str(DRIVER).replace("'", "''")}' -Raw)); 'POWERSHELL_DRIVER_PARSE_OK'"],
        logs,
        "powershell-driver-parse",
    )
    require("POWERSHELL_DRIVER_PARSE_OK" in driver_stdout, "PowerShell UI driver syntax validation failed")

    schema = load(CANDIDATE_SCHEMA)
    Draft202012Validator.check_schema(schema)
    fixture_files = sorted(path for path in FIXTURE_ROOT.rglob("*") if path.is_file())
    tool_files = [RUNNER, DRIVER, VERIFIER, CANDIDATE_SCHEMA, *fixture_files]
    tooling_status = "PASS-TOOLING-READY-FOR-CANDIDATE-SPEC-NOT-EXECUTED" if source_clean else "PASS-TOOLING-NOT-CANDIDATE-EXECUTED-SOURCE-GATE-NO-GO"
    receipt = {
        "schemaVersion": "knowledge-clipper-sealed-stateful-runner-tooling-receipt.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": tooling_status,
        "tests": {"total": total, "suites": test_results, "schemaSyntax": "passed", "runnerFixture": validation, "powershellDriverSyntax": "passed"},
        "toolchain": {"files": [evidence(path) for path in tool_files]},
        "sourceGate": {
            "receipt": evidence(source_gate_path),
            "status": source_gate["status"],
            "currentViolationCount": source_gate["delta"]["currentViolationCount"],
            "promotionAllowed": source_clean,
        },
        "executionBoundary": {
            "formalCandidateRun": False,
            "candidateRoot": None,
            "desktopProcessesStarted": 0,
            "orchestratorProcessesStarted": 0,
            "externalCalls": 0,
            "fixtureOnlyMayPromote": False,
        },
    }
    receipt_path = output_root / "tooling-receipt.json"
    receipt_path.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return {"status": receipt["status"], "tests": total, "receipt": evidence(receipt_path)}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-root", type=Path, required=True)
    parser.add_argument("--source-gate", type=Path, default=DEFAULT_SOURCE_GATE)
    args = parser.parse_args()
    print(json.dumps(qualify(args.output_root.resolve(), args.source_gate.resolve()), ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
