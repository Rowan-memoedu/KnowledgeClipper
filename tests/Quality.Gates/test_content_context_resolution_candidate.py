import copy
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import verify_content_context_resolution_candidate as gate


def valid():
    return {
        "schema": "kc.quality.content-context-resolution-evidence/1",
        "identity": {"workerId": "content.deepseek", "workerVersion": "0.10.2", "manifestVerified": True, "payloadMismatchCount": 0},
        "inputContract": {"schemaVersion": "standard-input/6.0.0", "selectedTextExact": True, "targetTextExact": True, "contextSegmentsOrdered": True, "allIdentityHashesBound": True, "speechUsesSelectedTextOnly": True},
        "cardinality": {"meaning-reading": 1, "writing": 1, "both": 2},
        "bothOrder": ["language_context", "language_writing"],
        "unresolved": {"workerResultStatus": "completed", "moduleReleaseState": "unresolved", "decisionKind": "language-practice-decision", "decisionContractVersion": "2.0.0", "cardBundleCount": 0, "coreExpectedProjection": "waiting_input"},
        "humanResolution": {"field": "human_overrides.languageTargetResolution", "newAttempt": True, "targetTokenBindsSelectedAndContextHashes": True, "staleTokenError": "INPUT_STALE"},
        "sideEffects": {"deepSeekLive": 0, "exporterWrites": 0},
    }


class Tests(unittest.TestCase):
    def test_valid(self): self.assertEqual("PASS-CONTENT-MODULE-CONDITIONAL-CORE-DESKTOP", gate.evaluate(valid())["status"])
    def reject(self, path, value):
        data = valid(); target = data
        for key in path[:-1]: target = target[key]
        target[path[-1]] = value
        with self.assertRaises(gate.GateFailure): gate.evaluate(data)
    def test_rejects_identity_and_manifest_drift(self):
        self.reject(["identity", "workerVersion"], "0.10.1"); self.reject(["identity", "payloadMismatchCount"], 1)
    def test_rejects_context_or_speech_leak(self):
        self.reject(["inputContract", "contextSegmentsOrdered"], False); self.reject(["inputContract", "speechUsesSelectedTextOnly"], False)
    def test_rejects_cardinality_or_order_drift(self):
        self.reject(["cardinality", "both"], 1); self.reject(["bothOrder"], ["language_writing", "language_context"])
    def test_rejects_unresolved_as_ready(self):
        self.reject(["unresolved", "decisionContractVersion"], "1.0.0"); self.reject(["unresolved", "cardBundleCount"], 1)
    def test_rejects_unbound_or_stale_human_resolution(self):
        self.reject(["humanResolution", "targetTokenBindsSelectedAndContextHashes"], False); self.reject(["humanResolution", "staleTokenError"], "retry")
    def test_rejects_external_side_effects(self): self.reject(["sideEffects", "deepSeekLive"], 1)


if __name__ == "__main__": unittest.main()
