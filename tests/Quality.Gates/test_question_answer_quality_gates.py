from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tools"))

import question_answer_quality_gates as gates  # noqa: E402


class QuestionAnswerQualityGateTests(unittest.TestCase):
    def test_current_modules_pass_but_core_and_candidate_boundaries_remain_no_go(self) -> None:
        result = gates.current_status()
        self.assertEqual("blocked-awaiting-neutral-five-worker-authority-and-new-desktop-canary", result["status"])
        self.assertEqual("desktop-v2.2-module-passed-production-core-final-pending", result["desktopWindowsUiCanary"]["status"])
        self.assertEqual(72, result["desktopWindowsUiCanary"]["assertionsPassed"])
        self.assertEqual("historical-failed-superseded-by-recovery", result["historicalDesktopWindowsUiFailure"]["status"])
        self.assertEqual(
            "Assertion failed: meaning-reading choice is returned unchanged",
            result["historicalDesktopWindowsUiFailure"]["failedAssertion"],
        )
        self.assertEqual(
            "core-v2.2-authority-checkpoint-historical-source-advanced-v1.2.3-candidate-pending",
            result["coreQuestionBoundary"]["status"],
        )
        self.assertIn(
            "orchestrator/Core/StageInputResolver.cs",
            result["coreQuestionBoundary"]["contractTests"]["sourceEvidenceDrifted"],
        )
        self.assertEqual(58, result["contentModuleEvidence"]["testsPassed"])
        self.assertEqual(14, result["exporterAcceptanceEvidence"]["export.remnote"]["checksPassed"])
        self.assertEqual(4, result["exporterAcceptanceEvidence"]["export.anki"]["checksPassed"])
        self.assertFalse(result["candidateEligible"])
        self.assertFalse(result["frozenV1Modified"])
        self.assertFalse(result["externalProductionApisInvoked"])

    def test_fixture_rejects_writable_answer_or_production_calls(self) -> None:
        base = gates.load_json(gates.FIXTURE_PATH)
        writable = copy.deepcopy(base)
        writable["requiredAdditiveEvidence"]["desktop"]["answerRegion"]["readOnly"] = False
        production = copy.deepcopy(base)
        production["acceptancePolicy"]["externalProductionApisInvoked"] = True
        for document in [writable, production]:
            with self.assertRaises(gates.QuestionAnswerGateFailure):
                gates.validate_fixture(document)


if __name__ == "__main__":
    unittest.main(verbosity=2)
