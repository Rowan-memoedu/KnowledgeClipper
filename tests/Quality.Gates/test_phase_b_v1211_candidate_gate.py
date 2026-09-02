from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tools"))

import verify_phase_b_v1211_candidate_gate as gate  # noqa: E402


class PhaseBV1211CandidateGateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.fixture = gate.load_json(gate.DEFAULT_FIXTURE)
        cls.result = gate.evaluate(copy.deepcopy(cls.fixture), full_inventory=False)

    def test_fixture_schema_accepts_current_and_rejects_pending_as_failure(self) -> None:
        validation = gate.validate_fixtures()
        self.assertEqual("passed", validation["status"])
        self.assertEqual(1, validation["validAccepted"])
        self.assertEqual(1, validation["invalidRejected"])

    def test_overall_is_conditional_not_failure_or_release_pass(self) -> None:
        self.assertEqual("CONDITIONAL-V1.2.11-CANDIDATE-BOUND-A-D-DPAPI-PASS-B-C-PENDING", self.result["status"])
        self.assertTrue(self.result["release"]["sealed"])
        self.assertFalse(self.result["release"]["releaseGo"])
        self.assertEqual("FAB6BA4C1D2B05B316D9416CF3E43D2D0099DA68222E1CABAA1B56C4B8F96953", self.result["release"]["candidateHash"])
        self.assertEqual("PASS-INTEGRATED-V1.2.11-CANDIDATE-BOUND", self.result["desktop"]["status"])

    def test_authority_is_exact_v1211_with_five_workers(self) -> None:
        authority = self.result["authority"]
        self.assertEqual("PASS-AUTHORITY-ADMITTED", authority["status"])
        self.assertEqual("1.2.11", authority["registryVersion"])
        self.assertEqual(214, authority["release"]["fileCount"])
        self.assertEqual(249697213, authority["release"]["totalBytes"])
        self.assertEqual("ACE70D34A5689CAD65C2FB9BFF81BC9E23F0C24650698A43FED8AD502D42F754", authority["release"]["treeSha256"])
        self.assertEqual("2.0.0", authority["workers"]["export.remnote"])
        self.assertEqual("1.2.3", authority["workers"]["speech.edge"])

    def test_v129_and_v1210_are_rejected(self) -> None:
        for version in ("1.2.9", "1.2.10"):
            self.assertEqual("REJECTED-INVALIDATED-NEVER-PROMOTED", self.result["invalidations"][version]["status"])
            self.assertFalse(self.result["invalidations"][version]["eligibleForCandidate"])

    def test_core_tree_and_warnings_as_errors_are_exact(self) -> None:
        core = self.result["core"]
        self.assertEqual("PASS-AUTHORITY-BOUND", core["status"])
        self.assertEqual({"fileCount": 37, "totalBytes": 68581140, "treeSha256": "BDA58C84FB2CEC77C7E59B6DC1B3EA553EE91EF296BB014AA4EFA0981D8B6F6C", "checkpointInternalMismatches": 0}, core["tree"])
        self.assertEqual({"tests/PhaseB.Canary/Program.cs", "tools/Invoke-KnowledgeClipperPhaseB.ps1"}, {item["path"] for item in core["sourceAdvancedAfterCheckpoint"]})
        self.assertEqual({"warnings": 0, "errors": 0}, core["build"])

    def test_a_is_scoped_candidate_bound_not_full_e2e(self) -> None:
        path = self.result["paths"]["A"]
        self.assertEqual("PASS-CANDIDATE-BOUND-SCOPED", path["status"])
        self.assertEqual({"export.anki": 1, "content.deepseek": 1}, path["invocationCounts"])
        self.assertNotIn("export.remnote", {item["worker"]["id"] for item in path["attempts"]})
        self.assertEqual("PASS", path["settingsSnapshotChain"]["status"])

    def test_d_has_two_fresh_natural_playbacks_and_long_audio(self) -> None:
        path = self.result["paths"]["D"]
        playback = [item for item in path["attempts"] if item["stageId"] == "speech-playback"]
        self.assertEqual(2, len(playback))
        self.assertEqual(2, len({item["attemptId"] for item in playback}))
        self.assertTrue(all(item["payload"]["cacheHit"] is False for item in playback))
        self.assertTrue(all(item["payload"]["playbackState"] == "idle_replayable" for item in playback))
        self.assertEqual(277, path["audio"]["absolutePathLength"])
        self.assertEqual(2, path["physicalSpeakerInvocationsInSourceEvidence"])
        self.assertEqual(0, path["replay"]["exportStages"])
        self.assertEqual(0, path["replay"]["contentStages"])

    def test_anki_readback_is_isolated_and_matches_card_bundles(self) -> None:
        readback = self.result["paths"]["ankiReadback"]
        self.assertEqual("PASS-ISOLATED", readback["status"])
        self.assertEqual(2, readback["notes"])
        self.assertEqual(2, readback["cards"])
        self.assertIn("::Canary::", readback["deck"])
        self.assertEqual(2, len(readback["pairs"]))

    def test_b_and_c_are_pending_not_failed(self) -> None:
        self.assertTrue(self.result["paths"]["B"]["status"].startswith("PENDING-"))
        self.assertTrue(self.result["paths"]["C"]["status"].startswith("PENDING-"))
        self.assertFalse(self.result["paths"]["B"]["automaticRetry"])
        self.assertEqual(0, self.result["paths"]["C"]["mineruProviderCalls"])

    def test_owner_readable_placeholder_is_rejected(self) -> None:
        self.assertEqual("REJECTED-TEMPLATE-PLACEHOLDER", self.result["paths"]["sourceReadable"]["status"])

    def test_dpapi_current_user_evidence_is_candidate_bound(self) -> None:
        credential = self.result["desktopCredential"]
        self.assertEqual("PASS-DPAPI-CURRENTUSER-SCOPED", credential["status"])
        self.assertEqual(131, credential["assertions"])
        self.assertTrue(credential["dpapiEnvelope"])
        self.assertTrue(credential["backupFallback"])
        self.assertTrue(credential["legacyMigration"])
        self.assertTrue(credential["migrationFailureFailClosed"])
        self.assertEqual(0, credential["localSecretScan"]["findingCount"])
        self.assertTrue(credential["candidateBound"])

    def test_new_desktop_candidate_identity_and_owner_receipt_are_bound(self) -> None:
        desktop = self.result["desktop"]
        self.assertEqual("kc-i-dpapi-v1211-20260828T183000Z", desktop["candidateId"])
        self.assertEqual(472, desktop["candidateInventory"]["fileCount"])
        self.assertEqual(402158662, desktop["candidateInventory"]["totalBytes"])
        self.assertEqual("E9452D8F670CD7476CB750DD731B28337A800B71D46CCB4BF780944828970592", desktop["candidateInventory"]["treeSha256"])
        self.assertEqual("PASS-HASH-BOUND", desktop["ownerTerminalReceipt"]["status"])
        self.assertEqual(0, desktop["runtimeSecretScan"]["findingCount"])
        self.assertEqual(0, desktop["userCredentialFilesIncluded"])

    def test_old_095200_desktop_payload_is_explicitly_ineligible(self) -> None:
        deprecated = self.result["desktop"]["deprecatedCandidate"]
        self.assertEqual("kc-i-settings-v1211-20260828T095200Z", deprecated["candidateId"])
        self.assertEqual("SUPERSEDED-NOT-ELIGIBLE-AS-FINAL", deprecated["status"])

    def test_secret_frozen_and_quality_side_effect_gates_are_zero(self) -> None:
        self.assertEqual(0, self.result["secretScan"]["findingCount"])
        self.assertEqual(21, self.result["immutableBaselines"]["frozenV1"]["filesChecked"])
        self.assertEqual(0, self.result["immutableBaselines"]["frozenV1"]["mismatches"])
        self.assertTrue(all(value == 0 for value in self.result["qualitySideEffects"].values()))

    def test_policy_weakening_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["policy"]["pendingPathsAreFailures"] = True
        with self.assertRaisesRegex(gate.GateFailure, "policy was weakened"):
            gate.evaluate(fixture, full_inventory=False)

    def test_hash_drift_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["paths"]["D"]["receipt"]["sha256"] = "0" * 64
        with self.assertRaisesRegex(gate.GateFailure, "paths.D.receipt: SHA-256 mismatch"):
            gate.evaluate(fixture, full_inventory=False)

    def test_desktop_owner_receipt_hash_drift_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["desktop"]["ownerReceipt"]["sha256"] = "0" * 64
        with self.assertRaisesRegex(gate.GateFailure, "desktop.ownerReceipt: SHA-256 mismatch"):
            gate.evaluate(fixture, full_inventory=False)

    def test_readable_index_preserves_scoped_boundaries(self) -> None:
        text = gate.render_index(self.result)
        self.assertIn("Pending, not failed", text)
        self.assertIn("literal `$deck` placeholder", text)
        self.assertIn("candidate-bound DPAPI and secret exclusion verified", text)
        self.assertEqual("unsigned-integrated-candidate", self.result["release"]["classification"])


if __name__ == "__main__":
    unittest.main()
