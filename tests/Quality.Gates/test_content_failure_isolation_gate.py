import copy
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import verify_content_failure_isolation_gate as gate

FIXTURE = ROOT / "tests/Quality.Gates/fixtures/content-failure-isolation/real-user-t122254.json"


class ContentFailureIsolationTests(unittest.TestCase):
    def setUp(self):
        self.fixture = json.loads(FIXTURE.read_text(encoding="utf-8"))

    def test_real_user_failure_is_no_go(self):
        result = gate.evaluate(self.fixture)
        self.assertEqual("NO-GO-REAL-USER-FAILURE", result["status"])
        self.assertIn("speechCompletedIndependently", result["missingNextCandidateGates"])

    def test_export_after_content_failure_is_rejected(self):
        changed = copy.deepcopy(self.fixture)
        changed["observed"]["ankiAttempts"] = 1
        with self.assertRaises(gate.GateFailure):
            gate.evaluate(changed)

    def test_unrelated_failure_code_is_rejected(self):
        changed = copy.deepcopy(self.fixture)
        changed["observed"]["content"]["errorCode"] = "service_unreachable"
        with self.assertRaises(gate.GateFailure):
            gate.evaluate(changed)


if __name__ == "__main__":
    unittest.main()
