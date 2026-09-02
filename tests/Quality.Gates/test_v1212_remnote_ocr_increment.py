from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tools"))

import verify_v1212_remnote_ocr_increment as gate  # noqa: E402


class V1212RemNoteOcrIncrementTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.fixture = gate.common.load_json(gate.DEFAULT_FIXTURE)
        cls.result = gate.evaluate(copy.deepcopy(cls.fixture))

    def test_fixture_rejects_ocr_blocking_passed_text(self) -> None:
        result = gate.validate_fixtures()
        self.assertEqual("PASS", result["status"])
        self.assertEqual(1, result["validAccepted"])
        self.assertEqual(1, result["invalidRejected"])

    def test_exact_candidate_and_authority_binding(self) -> None:
        self.assertTrue(self.result["candidateBound"])
        self.assertEqual("5D03C90D42FA2A1145FF6DCC4874BD059AC3EE072628F0A8DC81FB23983B4F3C", self.result["candidateHash"])
        self.assertEqual("1.2.12", self.result["authority"]["registryVersion"])
        self.assertEqual("PASS-EXACT-T183500Z-AUTHORITY-V1.2.12", self.result["candidateVerification"]["status"])

    def test_remnote_worker_is_candidate_bound_and_isolated(self) -> None:
        remnote = self.result["remnote"]
        self.assertEqual("PASS-LIVE-ISOLATED-REPLAY-READBACK-WORKER-BINARY-BOUND", remnote["status"])
        self.assertEqual("2.1.0", remnote["workerVersion"])
        self.assertEqual(gate.ISOLATED_ROOT, remnote["isolatedRootRemId"])
        self.assertFalse(remnote["formalClipRootTouched"])
        self.assertEqual(0, remnote["deletes"])

    def test_remnote_date_clip_and_card_export_are_strictly_separated(self) -> None:
        clip = self.result["remnote"]["routes"]["clip"]
        card = self.result["remnote"]["routes"]["card"]
        self.assertTrue(clip["dateRouting"])
        self.assertFalse(card["dateRouting"])
        self.assertEqual("created", clip["originalOutcome"])
        self.assertEqual("created", card["originalOutcome"])

    def test_remnote_replay_and_exact_readback_pass(self) -> None:
        for route in self.result["remnote"]["routes"].values():
            self.assertEqual("already_applied", route["replayOutcome"])
            self.assertTrue(route["readbackVerified"])
            self.assertTrue(route["exactReadback"])

    def test_ocr_has_one_submission_and_completed_terminal_result(self) -> None:
        ocr = self.result["ocr"]
        self.assertEqual("PASS-LIVE-MINERU-TERMINAL-COMPLETED-WORKER-BINARY-BOUND", ocr["status"])
        self.assertEqual("1.2.2", ocr["workerVersion"])
        self.assertEqual(1, ocr["provider"]["submissionCount"])
        self.assertEqual(1, ocr["provider"]["externalAttemptCount"])
        self.assertEqual(2, ocr["workerOutputCount"])
        self.assertEqual(3, ocr["evidenceOutputCount"])
        self.assertEqual(3, len(ocr["outputs"]))

    def test_ocr_downstream_is_pending_and_destination_writes_are_zero(self) -> None:
        ocr = self.result["ocr"]
        self.assertEqual("PENDING-NOT-RUN-BY-THIS-INCREMENT", ocr["contentDownstream"])
        self.assertEqual("PENDING-NOT-RUN-BY-THIS-INCREMENT", ocr["destinationDownstream"])
        self.assertEqual(0, ocr["destinationWrites"])
        self.assertFalse(ocr["blocksPassedText"])

    def test_increment_does_not_rerun_external_services_or_add_gate(self) -> None:
        self.assertFalse(self.result["classification"]["externalServicesRerunByQuality"])
        self.assertFalse(self.result["classification"]["candidateDesktopOrchestratorProcessE2E"])
        self.assertEqual("UNCHANGED-NON-BLOCKED", self.result["existingPassedTextAndHandTest"]["status"])
        self.assertTrue(self.result["noAdditionalGateIntroduced"])


if __name__ == "__main__":
    unittest.main()
