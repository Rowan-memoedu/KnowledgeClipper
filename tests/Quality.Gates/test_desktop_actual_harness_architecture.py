import copy
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import verify_desktop_actual_harness_architecture as gate


GOOD_PS = r'''Set-StrictMode -Version Latest
$candidateProcess = $null
$uiaRoot = $null
$pipeClient = $null
$ankiAdapter = $null
$sourceWindow = $null
$logWriter = $null
$settingsArgument = '--open-settings'
try { Assert-PreTaskWriteCountsZero; $candidateProcess = Start-FakeChild $settingsArgument }
finally { Close-OwnedResource $ankiAdapter; Close-OwnedResource $pipeClient; Close-OwnedResource $uiaRoot; Close-OwnedResource $candidateProcess }
'''


def runtime():
    scenario = {"externalCalls": 0, "ankiWrites": 0, "cleanupAttempted": True, "ownedProcessesRemaining": 0, "cleanupIdempotent": True, "cleanupNullSafe": True}
    names = ["start-failure", "zero-window", "multiple-window", "settings-open-failure", "settings-save-failure", "pipe-connect-failure", "task-submit-failure", "state-timeout", "anki-adapter-failure", "success", "cleanup-close-throws", "cleanup-kill-required"]
    return {"schema": "kc.quality.desktop-actual-harness-runtime/1", "candidateInventoryVerified": True, "isolation": {"process": True, "window": True, "localAppData": True, "pipe": True, "dataRoot": True, "destination": True, "businessInputInjected": False}, "settingsUiPath": "--open-settings", "preTaskCounts": {"tasks": 0, "ankiWrites": 0, "externalCalls": 0}, "scenarios": {name: copy.deepcopy(scenario) for name in names}, "fullStateMachineReachedWithFakes": True}


class Tests(unittest.TestCase):
    def test_static_valid(self): self.assertEqual("PASS-STATIC", gate.audit_powershell(GOOD_PS, {"settings": "--open-settings"})["status"])
    def test_rejects_reserved_host_case_insensitively(self):
        for name in ("host", "Host", "HOST"):
            with self.subTest(name=name), self.assertRaises(gate.GateFailure): gate.audit_powershell(GOOD_PS + f"\n${name} = 1", {"settings": "--open-settings"})
    def test_rejects_legacy_cli_or_missing_strict_mode(self):
        with self.assertRaises(gate.GateFailure): gate.audit_powershell(GOOD_PS.replace("--open-settings", "--settings"), {"settings": "--open-settings"})
        with self.assertRaises(gate.GateFailure): gate.audit_powershell(GOOD_PS.replace("Set-StrictMode -Version Latest", ""), {"settings": "--open-settings"})
    def test_rejects_uninitialized_resource(self):
        with self.assertRaises(gate.GateFailure): gate.audit_powershell(GOOD_PS.replace("$pipeClient = $null", "$pipeClient = New-Object object"), {"settings": "--open-settings"})
    def test_runtime_valid(self): self.assertEqual(12, gate.evaluate_runtime(runtime())["scenarios"])
    def test_rejects_missing_branch_or_cleanup_failure(self):
        data = runtime(); del data["scenarios"]["zero-window"]
        with self.assertRaises(gate.GateFailure): gate.evaluate_runtime(data)
        data = runtime(); data["scenarios"]["cleanup-close-throws"]["ownedProcessesRemaining"] = 1
        with self.assertRaises(gate.GateFailure): gate.evaluate_runtime(data)
    def test_rejects_external_work_or_business_input_injection(self):
        data = runtime(); data["scenarios"]["success"]["ankiWrites"] = 1
        with self.assertRaises(gate.GateFailure): gate.evaluate_runtime(data)
        data = runtime(); data["isolation"]["businessInputInjected"] = True
        with self.assertRaises(gate.GateFailure): gate.evaluate_runtime(data)


if __name__ == "__main__": unittest.main()
