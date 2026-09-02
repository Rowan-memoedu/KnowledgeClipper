from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tools"))

import verify_v1212_sealed_process_increment as gate  # noqa: E402


class V1212SealedProcessIncrementTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.fixture = gate.common.load_json(gate.DEFAULT_FIXTURE)
        cls.result = gate.evaluate(copy.deepcopy(cls.fixture))

    def test_fixture_rejects_hidden_ocr_product_gap(self) -> None:
        result = gate.validate_fixtures()
        self.assertEqual("PASS", result["status"])
        self.assertEqual(1, result["validAccepted"])
        self.assertEqual(1, result["invalidRejected"])

    def test_exact_candidate_authority_and_worker_entrypoints(self) -> None:
        candidate = self.result["candidateVerification"]
        self.assertEqual("PASS-EXACT-T183500Z-AUTHORITY-V1.2.12", candidate["status"])
        self.assertEqual("5D03C90D42FA2A1145FF6DCC4874BD059AC3EE072628F0A8DC81FB23983B4F3C", self.result["candidateHash"])
        self.assertEqual({"content.deepseek", "export.anki"}, set(candidate["workers"]))

    def test_raw_evidence_inventory_and_zero_stderr_are_hashed(self) -> None:
        evidence = self.result["evidenceRoot"]
        self.assertGreater(evidence["fileCount"], 100)
        self.assertGreater(evidence["totalBytes"], 0)
        self.assertEqual(64, len(evidence["treeSha256"]))
        self.assertEqual(0, self.result["process"]["stderr"]["bytes"])

    def test_named_pipe_commands_queries_and_durable_events_pass(self) -> None:
        ipc = self.result["ipc"]
        self.assertEqual("PASS", ipc["status"])
        self.assertEqual(26, ipc["requestCount"])
        self.assertEqual(26, ipc["responseCount"])
        self.assertEqual({"process-text": "succeeded", "process-ocr-handoff": "succeeded", "process-question": "waiting_input"}, ipc["finalStates"])

    def test_two_completed_tasks_used_real_candidate_worker_versions(self) -> None:
        for name in ("process-text", "process-ocr-handoff"):
            task = self.result["tasks"][name]
            self.assertEqual("PROCESS-COMPLETED", task["status"])
            workers = {(item["workerId"], item["workerVersion"]) for item in task["attempts"]}
            self.assertEqual({("content.deepseek", "0.7.0"), ("export.anki", "1.2.1")}, workers)
            self.assertEqual("succeeded", task["events"]["terminalState"])

    def test_anki_notes_info_cards_info_and_exact_card_content(self) -> None:
        text = self.result["tasks"]["process-text"]
        ocr = self.result["tasks"]["process-ocr-handoff"]
        self.assertEqual(1788081994612, text["card"]["noteId"])
        self.assertEqual(1788082002544, ocr["card"]["noteId"])
        for task in (text, ocr):
            self.assertEqual("SUCCEEDED", task["ankiEvidence"]["notesInfo"])
            self.assertEqual("SUCCEEDED", task["ankiEvidence"]["cardsInfo"])
        self.assertEqual({"question": "What is 19 + 23?", "answer": "42"}, {"question": ocr["card"]["question"], "answer": ocr["card"]["answer"]})

    def test_ocr_canar_is_a_real_product_gap_despite_completed_chain(self) -> None:
        task = self.result["tasks"]["process-ocr-handoff"]
        gap = task["ocrCorrection"]
        self.assertEqual("PRODUCT-GAP-OCR-CORRECTION-UNMET", gap["status"])
        self.assertIn("CANAR", gap["observed"])
        self.assertIn("CANARY", gap["expected"])
        self.assertFalse(gap["providerRerun"])
        self.assertTrue(self.result["classification"]["completedDoesNotImplyOcrCorrectionQuality"])

    def test_question_is_not_promoted_beyond_initial_answer(self) -> None:
        question = self.result["tasks"]["process-question"]
        self.assertEqual("PENDING-WAITING_INPUT", question["status"])
        self.assertEqual("COMPLETED", question["initialAnswer"])
        self.assertEqual("PENDING", question["followUp"])
        self.assertEqual("PENDING", question["makeCards"])
        self.assertFalse(question["fullChainPassed"])

    def test_quality_did_not_rerun_external_services_or_add_gate(self) -> None:
        self.assertFalse(self.result["classification"]["externalServicesRerunByQuality"])
        self.assertFalse(self.result["classification"]["ocrProviderRerunInEvidence"])
        self.assertEqual("UNCHANGED-NON-BLOCKED", self.result["existingPassedTextAndManualUi"]["status"])
        self.assertTrue(self.result["noAdditionalGateIntroduced"])


if __name__ == "__main__":
    unittest.main()
