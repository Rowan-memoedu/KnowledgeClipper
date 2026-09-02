from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tools"))

import verify_v1212_external_phase_b as gate  # noqa: E402


class V1212ExternalPhaseBGateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.fixture = gate.load_json(gate.DEFAULT_FIXTURE)
        cls.result = gate.evaluate(copy.deepcopy(cls.fixture))

    def test_fixture_rejects_desktop_process_overclaim(self) -> None:
        validation = gate.validate_fixtures()
        self.assertEqual("PASS", validation["status"])
        self.assertEqual(1, validation["validAccepted"])
        self.assertEqual(1, validation["invalidRejected"])

    def test_candidate_and_authority_are_exactly_bound_without_offline_tree_rerun(self) -> None:
        self.assertTrue(self.result["candidateBound"])
        self.assertEqual("5D03C90D42FA2A1145FF6DCC4874BD059AC3EE072628F0A8DC81FB23983B4F3C", self.result["candidateHash"])
        self.assertEqual("4F1021038635984154290467011FBB25AB9D88DD7A1A035F059CFF309231416A", self.result["sealedTreeSha256"])
        self.assertEqual("1.2.12", self.result["authority"]["registryVersion"])

    def test_backend_harness_is_byte_equal_but_not_desktop_process_e2e(self) -> None:
        classification = self.result["classification"]
        self.assertEqual("backend-harness-candidate-worker-bound", classification["level"])
        self.assertTrue(classification["candidateCoreAssemblyByteEqual"])
        self.assertFalse(classification["candidateDesktopOrchestratorProcessE2E"])
        for item in self.result["paths"].values():
            self.assertFalse(item["coreExecution"]["candidateDesktopOrchestratorProcess"])
            self.assertEqual(item["coreExecution"]["assembly"]["sha256"], item["coreExecution"]["candidateAssembly"]["sha256"])

    def test_a_b_d_are_backend_harness_pass_and_all_attempt_hashes_match(self) -> None:
        for path in ("A", "B", "D"):
            item = self.result["paths"][path]
            self.assertEqual("PASS-BACKEND-HARNESS-CANDIDATE-WORKER-BOUND", item["classification"])
            self.assertTrue(all(attempt["status"] == "completed" for attempt in item["attempts"]))
        self.assertEqual("already_applied", self.result["paths"]["B"]["replay"]["payload"]["outcome"])
        self.assertEqual(2, self.result["paths"]["D"]["physicalPlayback"]["physicalSpeakerInvocations"])

    def test_c_and_anki_remain_conditional_without_writes(self) -> None:
        c = self.result["paths"]["C"]
        self.assertEqual("CONDITIONAL-CREDENTIAL-UNAVAILABLE", c["classification"])
        self.assertEqual(0, c["providerCalls"])
        self.assertFalse(c["downstreamStarted"])
        self.assertEqual(0, c["externalWrites"])
        self.assertEqual("CONDITIONAL-SERVICE-UNREACHABLE", self.result["destinations"]["anki"]["status"])
        self.assertEqual(0, self.result["destinations"]["anki"]["writes"])

    def test_remnote_is_isolated_worker_readback_not_unbound_controller_claim(self) -> None:
        remnote = self.result["destinations"]["remnote"]
        self.assertEqual("PASS-WORKER-READBACK-ISOLATED-TARGET", remnote["status"])
        self.assertFalse(remnote["formalClipRootUsed"])
        self.assertTrue(remnote["workerReadbackVerified"])
        self.assertIsNone(remnote["controllerMcpReadbackReceipt"])
        self.assertTrue(remnote["controllerMcpAttestationNotUsedForMachinePass"])

    def test_secret_and_promotion_boundaries_are_preserved(self) -> None:
        self.assertEqual(0, self.result["secretScan"]["findingCount"])
        self.assertFalse(self.result["externalServicesRerunByQuality"])
        self.assertFalse(self.result["userHandTestEntrypoint"])
        self.assertFalse(self.result["releaseGo"])
        self.assertIn("DESKTOP-PROCESS-E2E-PENDING", self.result["status"])

    def test_policy_weakening_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["policy"]["desktopProcessE2E"] = True
        with self.assertRaisesRegex(gate.GateFailure, "policy weakened"):
            gate.evaluate(fixture)

    def test_readable_index_keeps_honest_boundaries(self) -> None:
        text = gate.render_index(self.result)
        self.assertIn("not Desktop-contained Orchestrator process E2E", text)
        self.assertIn("MinerU: credential unavailable", text)
        self.assertIn("Anki: service unreachable", text)
        self.assertIn("userHandTestEntrypoint=false", text)


if __name__ == "__main__":
    unittest.main()
