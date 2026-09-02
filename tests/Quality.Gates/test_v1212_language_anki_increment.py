from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tools"))

import verify_v1212_language_anki_increment as gate  # noqa: E402


class V1212LanguageAnkiIncrementGateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.fixture = gate.common.load_json(gate.DEFAULT_FIXTURE)
        cls.result = gate.evaluate(copy.deepcopy(cls.fixture))

    def test_fixture_accepts_scope_and_rejects_ocr_as_blocker(self) -> None:
        result = gate.validate_fixtures()
        self.assertEqual("PASS", result["status"])
        self.assertEqual(1, result["validAccepted"])
        self.assertEqual(1, result["invalidRejected"])

    def test_content_live_authority_receipt_and_inventory_are_verified(self) -> None:
        content = self.result["content"]
        self.assertEqual("PASS-LIVE-DEEPSEEK-AUTHORITY-V1.2.12", content["status"])
        self.assertEqual("0.7.0", content["workerVersion"])
        self.assertEqual(["deepseek"], content["externalProvidersCalled"])
        self.assertEqual([], content["externalDestinationTargetsCalled"])
        self.assertFalse(content["credentialsRecorded"])

    def test_exact_final_candidate_and_authority_binding(self) -> None:
        self.assertTrue(self.result["candidateBound"])
        self.assertEqual(gate.EXPECTED_CANDIDATE["manifestSha256"], self.result["candidateHash"])
        self.assertEqual(gate.EXPECTED_CANDIDATE, self.result["candidate"])
        self.assertEqual(gate.EXPECTED_AUTHORITY, self.result["authority"])

    def test_e_f_g_have_exact_completed_worker_attempts(self) -> None:
        for name in ("E", "F", "G"):
            path = self.result["paths"][name]
            self.assertEqual("PASS-BACKEND-HARNESS-CANDIDATE-WORKER-BOUND", path["status"])
            self.assertEqual([("content.deepseek", "0.7.0"), ("export.anki", "1.2.1")], sorted((a["workerId"], a["workerVersion"]) for a in path["attempts"]))
            self.assertTrue(all(a["status"] == "completed" for a in path["attempts"]))

    def test_exact_readback_note_counts_and_template_order(self) -> None:
        expected = {"E": (1, ["language_context"]), "F": (1, ["language_writing"]), "G": (2, ["language_context", "language_writing"])}
        for name, (count, templates) in expected.items():
            readback = self.result["paths"][name]["exactReadback"]
            self.assertEqual(count, readback["noteCount"])
            self.assertEqual(count, readback["cardCount"])
            self.assertEqual(templates, [item["template"] for item in readback["items"]])

    def test_writing_is_one_full_width_replacement_and_both_preserves_order(self) -> None:
        comparison = self.result["readableComparison"]
        writing = comparison["writing"][0]
        self.assertNotIn(gate.TARGET, writing["front"])
        self.assertEqual(gate.TARGET, writing["back"])
        self.assertIn("（", writing["front"])
        self.assertIn("；", writing["front"])
        fields = ("template", "front", "back", "tags")
        semantic = lambda item: {key: item[key] for key in fields}
        self.assertEqual([semantic(item) for item in comparison["both"]], [semantic(comparison["meaningReading"][0]), semantic(writing)])

    def test_aggregate_qualification_and_negative_fail_closed_evidence(self) -> None:
        qualification = self.result["qualification"]
        self.assertEqual("PASS-BACKEND-HARNESS-CANDIDATE-WORKER-BOUND", qualification["status"])
        self.assertEqual(4, qualification["taskCount"])
        self.assertEqual(5, qualification["noteCount"])
        self.assertEqual(5, qualification["externalWriteCount"])
        self.assertEqual(2, qualification["failClosedNegativeCount"])

    def test_ocr_is_user_deferred_nonblocking_without_scope_expansion(self) -> None:
        self.assertEqual({"status": "USER-DEFERRED-NON-BLOCKING", "blocking": False, "mineruCalls": 0}, self.result["ocr"])
        self.assertTrue(self.result["noAdditionalGateIntroduced"])
        self.assertFalse(self.result["classification"]["externalServicesRerunByQuality"])

    def test_backend_harness_is_not_desktop_process_e2e_or_release_go(self) -> None:
        classification = self.result["classification"]
        self.assertEqual("backend-harness-candidate-worker-bound", classification["level"])
        self.assertFalse(classification["candidateDesktopOrchestratorProcessE2E"])
        self.assertFalse(self.result["userHandTestEntrypoint"])
        self.assertFalse(self.result["releaseGo"])


if __name__ == "__main__":
    unittest.main()
