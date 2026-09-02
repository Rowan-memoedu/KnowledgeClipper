from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT / "tools"))

import verify_core_phase1_checkpoint as gate  # noqa: E402


class CorePhase1CheckpointTests(unittest.TestCase):
    def test_current_checkpoint_and_process_evidence_pass(self) -> None:
        result = gate.evaluate()
        self.assertEqual("PASS-CORE-PROCESS-EVIDENCE-SOURCE-ADVANCED-DESKTOP-MIGRATION-PENDING", result["status"])
        self.assertEqual(8, result["core"]["fileBindingsChecked"])
        self.assertEqual(5, result["core"]["fileBindingsMatchedCurrentSource"])
        self.assertEqual(3, result["core"]["fileBindingsDriftedAfterCheckpoint"])
        self.assertEqual("PENDING", result["desktopBoundary"]["status"])
        self.assertFalse(result["semantics"]["fakeDisplayProjection"])

    def test_business_and_execution_status_are_independent_and_valid(self) -> None:
        module_receipt = gate.load(gate.EVIDENCE_ROOT / "jobs/module-actions/anki-probe-process-canary/receipt.json")
        task_result = gate.load(gate.EVIDENCE_ROOT / "task-get-result.v2.4.json")
        gate.verify_runtime_semantics(module_receipt, task_result)
        self.assertEqual("completed", module_receipt["executionStatus"])
        self.assertEqual("ready", module_receipt["status"])

    def test_recent_query_is_rejected_by_semantic_gate(self) -> None:
        module_receipt = gate.load(gate.EVIDENCE_ROOT / "jobs/module-actions/anki-probe-process-canary/receipt.json")
        task_result = gate.load(gate.EVIDENCE_ROOT / "task-get-result.v2.4.json")
        task_result["type"] = "task.query_recent"
        with self.assertRaisesRegex(gate.GateFailure, "recent query"):
            gate.verify_runtime_semantics(module_receipt, task_result)

    def test_readonly_probe_write_or_retry_drift_is_rejected(self) -> None:
        original = gate.load(gate.EVIDENCE_ROOT / "jobs/module-actions/anki-probe-process-canary/receipt.json")
        task_result = gate.load(gate.EVIDENCE_ROOT / "task-get-result.v2.4.json")
        for field, value, message in [
            ("externalWriteCount", 1, "reported writes"),
            ("automaticRetryAllowed", True, "allows automatic retry"),
        ]:
            receipt = copy.deepcopy(original)
            receipt[field] = value
            with self.assertRaisesRegex(gate.GateFailure, message):
                gate.verify_runtime_semantics(receipt, task_result)

    def test_fake_display_projection_is_not_in_runtime_receipt(self) -> None:
        process_receipt = gate.load(gate.PROCESS_RECEIPT)
        self.assertEqual("worker-only-real-artifact-producer", process_receipt["fakeWorkerRole"])
        self.assertFalse(process_receipt["displayProjectionReturnedByFake"])


if __name__ == "__main__":
    unittest.main()
