import copy
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import verify_content_failure_recovery_candidate as gate

CANDIDATE = Path(r"D:\Artifacts\KnowledgeClipper\read-to-anki\rta-20260903T204425Z-47f6889a")
SDK = Path(r"D:\Artifacts\KnowledgeClipper\read-content-failure-sdk\read-content-failure-sdk-20260903T204530Z-8513dd89")
RECEIPT = CANDIDATE / "canary/fake-content-failure-and-question/receipt.json"


class ContentFailureRecoveryTests(unittest.TestCase):
    def test_live_candidate_passes(self):
        self.assertEqual("PASS-SCOPED-OFFLINE-REGRESSION", gate.evaluate(CANDIDATE, SDK)["status"])

    def test_anki_attempt_is_rejected(self):
        data = json.loads(RECEIPT.read_text(encoding="utf-8"))
        data["contentFailure"]["ankiAttempts"] = 1
        with self.assertRaises(gate.GateFailure):
            gate.verify_semantics(data)

    def test_missing_speech_artifact_is_rejected(self):
        data = json.loads(RECEIPT.read_text(encoding="utf-8"))
        data["contentFailure"]["artifacts"] = [x for x in data["contentFailure"]["artifacts"] if x["kind"] != "speech-playback"]
        with self.assertRaises(gate.GateFailure):
            gate.verify_semantics(data)

    def test_untyped_failure_is_rejected(self):
        data = json.loads(RECEIPT.read_text(encoding="utf-8"))
        data["contentFailure"]["result"]["pipeline"] = "running"
        with self.assertRaises(gate.GateFailure):
            gate.verify_semantics(data)


if __name__ == "__main__":
    unittest.main()
