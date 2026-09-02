from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT / "tools"))

import verify_production_state_journeys as journey_gate  # noqa: E402
import write_architecture_burndown as gate  # noqa: E402


class ArchitectureBurndownTests(unittest.TestCase):
    def setUp(self) -> None:
        self.baseline = journey_gate.load(gate.DEFAULT_BASELINE)

    def test_current_delta_is_honest(self) -> None:
        result = gate.run(gate.DEFAULT_BASELINE)
        expected = "PASS-ARCHITECTURE-SOURCE-GATE" if not result["currentSource"]["violations"] else "NO-GO-ARCHITECTURE-VIOLATIONS-REMAIN"
        self.assertEqual(expected, result["status"])
        self.assertEqual(sum(item["currentCount"] for item in result["delta"]["rows"]), result["delta"]["currentViolationCount"])

    def test_resolved_violation_is_counted_without_rewriting_baseline(self) -> None:
        current = copy.deepcopy(self.baseline["architecture"])
        current["violations"] = current["violations"][1:]
        result = gate.compare(self.baseline, current)
        self.assertEqual(1, result["resolvedViolationCount"])
        self.assertEqual(0, result["introducedViolationCount"])

    def test_new_violation_is_reported(self) -> None:
        current = copy.deepcopy(self.baseline["architecture"])
        current["violations"].append({"code": "new-architecture-regression"})
        result = gate.compare(self.baseline, current)
        self.assertEqual(1, result["introducedViolationCount"])
        self.assertFalse(result["promotionAllowed"])

    def test_reader_file_appearance_is_not_enough(self) -> None:
        current = copy.deepcopy(self.baseline["architecture"])
        current["verifiedArtifactReaderDefinitions"] = 2
        result = gate.compare(self.baseline, current)
        self.assertFalse(result["positiveInvariants"]["fileAppearanceAloneCountsAsFix"])
        self.assertFalse(result["positiveInvariants"]["allProductionArtifactConsumersUnified"])

    def test_only_zero_current_violations_can_pass(self) -> None:
        current = copy.deepcopy(self.baseline["architecture"])
        current["violations"] = []
        result = gate.compare(self.baseline, current)
        self.assertEqual("PASS-ARCHITECTURE-SOURCE-GATE", result["status"])
        self.assertTrue(result["promotionAllowed"])

    def test_quality_owned_synthetic_promotion_regression_is_closed(self) -> None:
        current = journey_gate.inspect_architecture()
        self.assertFalse(any(item["code"] == "synthetic-evidence-promoted-to-user-handtest" for item in current["violations"]))


if __name__ == "__main__":
    unittest.main()
