from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT / "tools"))

import verify_core_phase1_v2101_checkpoint as gate  # noqa: E402
import verify_core_phase1_checkpoint as common  # noqa: E402


class CorePhase1V2101CheckpointTests(unittest.TestCase):
    def test_current_checkpoint_and_process_evidence_pass(self) -> None:
        result = gate.evaluate()
        self.assertEqual("PASS-CORE-V2.10.1-PREREQUISITE-DESKTOP-MIGRATION-PENDING", result["status"])
        self.assertEqual(11, result["fileBindings"]["matched"])
        self.assertEqual(0, result["frozenV1"]["mismatches"])
        self.assertEqual("PENDING", result["desktopBoundary"]["status"])

    def test_authorization_drift_fixture_is_schema_valid_but_policy_invalid(self) -> None:
        value = common.load(gate.EVIDENCE_ROOT / "jobs/module-actions/content-settings-copy-rejected-process-canary/input.json")
        self.assertNotEqual(value["targetRoot"], value["authorization"]["targetRoot"])

    def test_copy_retry_or_write_drift_is_rejected(self) -> None:
        root = gate.EVIDENCE_ROOT / "jobs/module-actions/content-settings-copy-process-canary"
        receipt = common.load(root / "receipt.json")
        self.assertFalse(receipt["automaticRetryAllowed"])
        self.assertEqual(0, receipt["externalWriteCount"])
        for field, value in (("automaticRetryAllowed", True), ("externalWriteCount", 1)):
            bad = copy.deepcopy(receipt)
            bad[field] = value
            schema = common.load(PROJECT / "contracts/core/v2.10/module-control/schemas/module-action-receipt.v5.schema.json")
            from jsonschema import Draft202012Validator
            self.assertTrue(list(Draft202012Validator(schema).iter_errors(bad)))

    def test_all_four_runtime_actions_are_sha_bound(self) -> None:
        result = gate.evaluate()
        self.assertEqual({"describe", "copy", "validate", "diff"}, set(result["runtimeEvidence"]["actions"]))


if __name__ == "__main__":
    unittest.main()
