from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tools"))

import verify_v1214_desktop_candidate_addendum as gate  # noqa: E402


class V1214DesktopCandidateAddendumTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.fixture = gate.load_json(gate.DEFAULT_FIXTURE)
        cls.result = gate.evaluate(copy.deepcopy(cls.fixture))

    def test_fixture_rejects_superseded_root_and_manual_boundary_as_release_failure(self) -> None:
        value = gate.validate_fixtures()
        self.assertEqual((1, 1), (value["validAccepted"], value["invalidRejected"]))

    def test_exact_final_candidate_manifest_and_sealed_tree(self) -> None:
        candidate = self.result["candidate"]
        self.assertEqual("kc-i-crq-v1214-20260830T105500Z", candidate["id"])
        self.assertEqual("E4D18D1DE41AC6CC001B0E9D1E70F40BB8206A65D6573E1BC480B345D5886FDF", self.result["candidateHash"])
        self.assertEqual((854, 430192317, "DEDDB7EA56CB410804A54FD376D4B4EF9D6D440CD3C39EEFF7D608C289A2CB9A"), (candidate["sealed"]["fileCount"], candidate["sealed"]["totalBytes"], candidate["sealed"]["treeSha256"]))

    def test_manifest_lists_every_non_control_payload_file(self) -> None:
        payload = self.result["candidate"]["payload"]
        self.assertEqual((853, 430013890, "6EC3DCE388FF0650472ECB90847EFD6A70472E6DD79C2A0F1DFB56B3BD199018"), (payload["fileCount"], payload["totalBytes"], payload["treeSha256"]))

    def test_desktop_receipt_recursively_verifies_544_files(self) -> None:
        desktop = self.result["desktop"]
        self.assertEqual("PASS-544-FILES-RECURSIVE", desktop["status"])
        self.assertEqual((544, 169218660, "69B4EA1BD04681826910AE1730A1ED909D6721E5EABD020F9DB692A00ABF80B6"), (desktop["fileCount"], desktop["totalBytes"], desktop["treeSha256"]))
        self.assertEqual(0, desktop["equality"]["changedFiles"])

    def test_authority_source_and_embedded_subtree_are_identical(self) -> None:
        authority = self.result["authority"]
        self.assertEqual("PASS-AUTHORITY-V1.2.14-EXACT", authority["status"])
        self.assertEqual((296, 0), (authority["filesCompared"], authority["changedFiles"]))
        self.assertEqual(0, authority["frozenV1Mismatches"])
        self.assertEqual(5, len(authority["workers"]))

    def test_hash_bound_startup_single_instance_registry_and_query_logs_pass(self) -> None:
        logs = self.result["automatedEvidence"]
        self.assertEqual("PASS-INDEPENDENT-LOG-READBACK", logs["status"])
        self.assertEqual("PASS", logs["startup"]["result"])
        self.assertEqual(0, logs["startup"]["externalConnections"])
        self.assertEqual("activate-existing-window-and-message-box", logs["singleInstance"]["userFeedbackMode"])
        self.assertFalse(logs["singleInstance"]["existingInstanceDetected"])
        self.assertTrue(logs["query"]["namedPipeInvoked"])
        self.assertFalse(logs["query"]["externalWorkerInvoked"])

    def test_secret_and_legacy_external_canary_scan_is_zero(self) -> None:
        safety = self.result["safety"]
        self.assertEqual("PASS-ZERO-FINDINGS", safety["status"])
        self.assertEqual(0, safety["findingCount"])
        self.assertEqual([], safety["credentialFiles"])
        self.assertEqual([], safety["legacyExternalCanaries"])
        self.assertGreater(safety["excludedNegativeFixtureCount"], 0)

    def test_gate_did_not_execute_candidate_or_external_services(self) -> None:
        boundary = self.result["executionBoundary"]
        self.assertFalse(boundary["externalRerun"])
        self.assertFalse(boundary["candidateBinariesExecutedByThisGate"])
        self.assertEqual((0, 0, 0), (boundary["externalWorkerCalls"], boundary["externalWrites"], boundary["physicalSpeakerInvocations"]))

    def test_physical_focus_and_mixed_dpi_are_hand_test_boundaries_not_release_failure(self) -> None:
        decision = self.result["promotionDecision"]
        self.assertEqual("PASS-FOR-USER-HANDTEST-ENTRYPOINT", decision["status"])
        self.assertTrue(decision["userHandTestEntrypoint"])
        self.assertFalse(decision["releaseFailure"])
        self.assertEqual("hand-test-not-release-failure", decision["manualBoundaryPolicy"])


if __name__ == "__main__":
    unittest.main()
