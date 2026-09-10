import copy
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import verify_language_matrix_user_handtest_gate as gate


def valid():
    common = {"preConfirmAnkiAttempts": 0, "confirmAnkiAttempts": 1, "ankiWorkerVersion": "1.4.1", "restartStable": True, "staleRejected": True, "doubleConfirmRejected": True, "cancelZeroDestination": True}
    mr = [{"strategy": "meaning-reading", "front": "f1", "back": "b1"}]
    wr = [{"strategy": "writing", "front": "f2", "back": "b2"}]
    both = mr + wr
    def mode(cards): return {**common, "previewCards": cards, "notesInfoCards": copy.deepcopy(cards), "cardsInfoCards": copy.deepcopy(cards)}
    return {"candidate": "fixture", "candidateBound": True, "modes": {"default": {"choiceOptions": ["meaning-reading", "writing", "both"]}, "meaning-reading": mode(mr), "writing": mode(wr), "both": mode(both)}, "externalEvidence": {"realContentDeepSeek": True, "physicalSpeech": True, "isolatedAnki": True}}


class LanguageMatrixGateTests(unittest.TestCase):
    def test_complete_matrix_passes(self):
        self.assertEqual("PASS-USER-HANDTEST-LANGUAGE-MATRIX", gate.evaluate(valid())["status"])

    def test_missing_mode_fails(self):
        data = valid(); del data["modes"]["writing"]
        with self.assertRaises(gate.GateFailure): gate.evaluate(data)

    def test_preconfirm_export_fails(self):
        data = valid(); data["modes"]["both"]["preConfirmAnkiAttempts"] = 1
        with self.assertRaises(gate.GateFailure): gate.evaluate(data)

    def test_fake_external_evidence_fails(self):
        data = valid(); data["externalEvidence"]["physicalSpeech"] = False
        with self.assertRaises(gate.GateFailure): gate.evaluate(data)

    def test_card_order_or_readback_drift_fails(self):
        data = valid(); data["modes"]["both"]["previewCards"].reverse()
        with self.assertRaises(gate.GateFailure): gate.evaluate(data)


if __name__ == "__main__":
    unittest.main()
