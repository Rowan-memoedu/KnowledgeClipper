from __future__ import annotations

import argparse
import json
import re
from pathlib import Path
from typing import Any


class GateFailure(RuntimeError):
    pass


def require(value: bool, message: str) -> None:
    if not value:
        raise GateFailure(message)


RESERVED_PS_VARIABLES = {"host", "error", "input", "args", "pid", "pwd", "home", "matches", "lastexitcode", "psitem", "this"}


def audit_powershell(source: str, authoritative_cli: dict[str, str]) -> dict[str, Any]:
    require(re.search(r"(?im)^\s*Set-StrictMode\s+-Version\s+Latest\s*$", source) is not None, "Set-StrictMode Latest missing")
    assigned = {m.group(1).lower() for m in re.finditer(r"(?im)(?<![\w])\$([A-Za-z_][\w]*)\s*=", source)}
    collisions = sorted(assigned & RESERVED_PS_VARIABLES)
    require(not collisions, f"PowerShell automatic/reserved variable collision: {collisions}")
    require("--settings" not in source, "legacy --settings argument used")
    require(authoritative_cli["settings"] == "--open-settings" and authoritative_cli["settings"] in source, "settings CLI not sourced from authority")
    for variable in ("candidateProcess", "uiaRoot", "pipeClient", "ankiAdapter", "sourceWindow", "logWriter"):
        require(re.search(rf"(?im)^\s*\${variable}\s*=\s*\$null\s*$", source) is not None, f"{variable} not initialized before try/finally")
    require("finally" in source.lower(), "finally missing")
    require(("Close-OwnedResource" in source) or ("Close-FakeResource" in source and "Stop-OwnedProcess" in source), "central null-safe cleanup missing")
    require(("Assert-PreTaskWriteCountsZero" in source) or re.search(r"(?is)Require\s*\(\s*\$businessTasks\s+-eq\s+0\s+-and\s+\$externalCalls\s+-eq\s+0\s+-and\s+\$ankiWrites\s+-eq\s+0", source) is not None, "pre-task write-count assertion missing")
    return {"status": "PASS-STATIC", "reservedCollisions": [], "settingsArgument": authoritative_cli["settings"]}


def evaluate_runtime(evidence: dict[str, Any]) -> dict[str, Any]:
    require(evidence["schema"] == "kc.quality.desktop-actual-harness-runtime/1", "runtime evidence schema drift")
    require(evidence["candidateInventoryVerified"], "candidate inventory not verified")
    isolation = evidence["isolation"]
    require(isolation == {"process": True, "window": True, "localAppData": True, "pipe": True, "dataRoot": True, "destination": True, "businessInputInjected": False}, "isolation boundary invalid")
    require(evidence["settingsUiPath"] == "--open-settings", "actual Settings UI path drift")
    require(evidence["preTaskCounts"] == {"tasks": 0, "ankiWrites": 0, "externalCalls": 0}, "pre-task side effects detected")
    required = {"start-failure", "zero-window", "multiple-window", "settings-open-failure", "settings-save-failure", "pipe-connect-failure", "task-submit-failure", "state-timeout", "anki-adapter-failure", "success", "cleanup-close-throws", "cleanup-kill-required"}
    scenarios = evidence["scenarios"]
    require(set(scenarios) == required, "early-return/exception/finally branch coverage incomplete")
    for name, scenario in scenarios.items():
        require(scenario["externalCalls"] == 0 and scenario["ankiWrites"] == 0, f"offline scenario performed external work: {name}")
        require(scenario["cleanupAttempted"] and scenario["ownedProcessesRemaining"] == 0, f"cleanup failed: {name}")
        require(scenario["cleanupIdempotent"] and scenario["cleanupNullSafe"], f"cleanup was not idempotent/null-safe: {name}")
    require(evidence["fullStateMachineReachedWithFakes"], "fake child/window/pipe/Anki did not reach the complete state machine")
    return {"status": "PASS-OFFLINE-HARNESS-ARCHITECTURE", "scenarios": len(scenarios), "externalCalls": 0}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--powershell", required=True)
    parser.add_argument("--runtime", required=True)
    args = parser.parse_args()
    static = audit_powershell(Path(args.powershell).read_text(encoding="utf-8"), {"settings": "--open-settings"})
    runtime = evaluate_runtime(json.loads(Path(args.runtime).read_text(encoding="utf-8")))
    print(json.dumps({"status": "PASS-OFFLINE-ACTUAL-HARNESS-GATE", "static": static, "runtime": runtime}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
