from __future__ import annotations

import copy
import json
import sys
import unittest
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tools"))

import verify_phase_b_candidate_gate as gate  # noqa: E402


class PhaseBCandidateGateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.fixture = gate.load_json(gate.DEFAULT_FIXTURE)
        cls.result = gate.evaluate(copy.deepcopy(cls.fixture), full_inventory=False)

    def test_quality_fixture_schema_accepts_current_and_rejects_weakened_policy(self) -> None:
        result = gate.validate_fixtures()
        self.assertEqual("passed", result["status"])
        self.assertEqual(1, result["validAccepted"])
        self.assertEqual(1, result["invalidRejected"])

    def test_current_matrix_is_conditional_not_release_pass(self) -> None:
        self.assertEqual(
            "NO-GO-V1.2.8-AUTHORITY-ADMITTED-PHASE-B-PARTIAL-DESKTOP-PENDING",
            self.result["status"],
        )
        self.assertEqual("PASS-ADMITTED", self.result["authority"]["status"])
        self.assertEqual("1.2.8", self.result["authority"]["registryVersion"])
        self.assertEqual("REJECTED-INVALIDATED-NEVER-PROMOTED", self.result["invalidatedAuthority127"]["status"])
        self.assertFalse(self.result["invalidatedAuthority127"]["authorityAdmitted"])
        self.assertEqual("PENDING-DESKTOP-V1.2.8-INTEGRATED-USER-ENTRYPOINT", self.result["desktop"]["status"])
        self.assertIsNone(self.result["release"]["candidateHash"])

    def test_content_061_presence_does_not_close_060_failures(self) -> None:
        self.assertEqual("PASS-MODULE-AND-AUTHORITY-BOUND-NOT-PHASE-B-FINAL", self.result["content061"]["status"])
        self.assertEqual("partial", self.result["paths"]["A"]["status"])
        self.assertEqual("partial", self.result["paths"]["B"]["status"])
        self.assertEqual("HISTORICAL-FAILED-IMMUTABLE", self.result["historicalFailure"]["status"])

    def test_prior_c_is_preserved_but_not_reused_as_v128_candidate_evidence(self) -> None:
        prior = self.result["priorScopedPaths"]["C"]
        current = self.result["paths"]["C"]
        self.assertTrue(prior["status"].startswith("PASS-SCOPED-AUTHORITY-V1.2.6"))
        self.assertEqual("partial", current["status"])
        self.assertEqual("1.2.8", self.result["phaseB128"]["authorityVersion"])

    def test_d_short_root_cannot_qualify_standard_long_root(self) -> None:
        prior = self.result["priorScopedPaths"]["D"]
        current = self.result["paths"]["D"]
        self.assertTrue(prior["status"].startswith("CONDITIONAL-SHORT-ROOT"))
        self.assertLessEqual(prior["pathLengthEvidence"]["shortRootMax"], 260)
        self.assertGreater(self.result["phaseB128"]["standardRootMaxPath"], 260)
        playback = [item for item in current["attempts"] if item["stageId"] == "speech-playback"]
        self.assertEqual(2, len(playback))
        self.assertTrue(all(item["status"] == "failed" for item in playback))

    def test_speech_122_formal_and_deep_path_evidence_is_module_only(self) -> None:
        speech = self.result["speech122"]
        self.assertEqual("PASS-MODULE-DEEP-PATH-AND-AUTHORITY-BOUND-NOT-PHASE-B-FINAL", speech["status"])
        self.assertEqual(43, speech["unitTests"])
        self.assertEqual(5, len(speech["deepLanguages"]))
        self.assertEqual({268}, {item["audioFullPathLength"] for item in speech["deepLanguages"]})
        self.assertFalse(speech["physicalSpeakerPlayback"])
        self.assertTrue(speech["authorityBound"])
        self.assertFalse(speech["candidateBound"])
        self.assertEqual("1.2.2", self.result["authority"]["workers"]["speech.edge"])

    def test_speech_123_deep_playback_is_module_only_and_does_not_promote_v128(self) -> None:
        speech = self.result["speech123"]
        self.assertEqual("PASS-MODULE-DEEP-PLAYBACK-NOT-AUTHORITY-BOUND", speech["status"])
        self.assertEqual(44, speech["unitTests"])
        self.assertEqual(261, speech["audio"]["absolutePathLength"])
        self.assertTrue(speech["physicalSpeakerPlaybackInSourceEvidence"])
        self.assertFalse(speech["authorityBound"])
        self.assertFalse(speech["candidateBound"])
        self.assertEqual(5, len(speech["attempts"]))
        self.assertTrue(all(not item["cacheHit"] for item in speech["attempts"]))
        self.assertEqual("1.2.2", self.result["authority"]["workers"]["speech.edge"])
        self.assertEqual("partial", self.result["paths"]["D"]["status"])

    def test_attempt_request_result_artifact_hashes_and_schemas_were_checked(self) -> None:
        for path_name in ("A", "B", "C", "D"):
            for attempt in self.result["paths"][path_name]["attempts"]:
                self.assertEqual(64, len(attempt["request"]["sha256"]))
                self.assertEqual(64, len(attempt["result"]["sha256"]))
                for artifact in attempt["artifacts"]:
                    self.assertEqual(64, len(artifact["manifest"]["sha256"]))
                    self.assertTrue(artifact["files"])

    def test_secret_and_immutable_baseline_gates_are_zero_drift(self) -> None:
        self.assertEqual(0, self.result["secretScan"]["findingCount"])
        self.assertEqual(21, self.result["immutableBaselines"]["frozenV1"]["filesChecked"])
        self.assertEqual(0, self.result["immutableBaselines"]["frozenV1"]["mismatches"])

    def test_authority_hash_drift_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["authority"]["checkpoint"]["sha256"] = "0" * 64
        with self.assertRaisesRegex(gate.GateFailure, "authority.checkpoint: SHA-256 mismatch"):
            gate.evaluate(fixture, full_inventory=False)

    def test_policy_weakening_is_rejected_before_evidence_admission(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["qualificationPolicy"]["shortRootMayQualify"] = True
        with self.assertRaisesRegex(gate.GateFailure, "policy was weakened"):
            gate.evaluate(fixture, full_inventory=False)

    def test_core_resolver_pass_retains_authority_and_metadata_boundaries(self) -> None:
        core = self.result["coreResolver"]
        self.assertEqual("PASS-AUTHORITY-BOUND-NOT-PHASE-B-FINAL", core["status"])
        self.assertEqual("1.2.7", core["intendedAuthority"])
        self.assertIsNone(core["metadataWarning"])
        self.assertTrue(core["authorityBound"])

    def test_invalidated_v127_cannot_be_promoted(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["invalidatedAuthority127"]["replacementMinimum"] = "1.2.7"
        with self.assertRaisesRegex(gate.GateFailure, "replacement policy mismatch"):
            gate.evaluate(fixture, full_inventory=False)

    def test_v128_partial_failure_matrix_is_exact_and_not_promoted(self) -> None:
        phase = self.result["phaseB128"]
        self.assertEqual("NO-GO-CANDIDATE-BOUND-PARTIAL", phase["status"])
        self.assertFalse(phase["finalQualification"])
        self.assertIn(("anki-export", "unknown_external_state", "effect.worker_exit_unknown"), [tuple(item) for item in phase["failureMatrix"]["A"]])
        self.assertIn(("export-remnote", "failed", "binding.remnote_inline_unsupported"), [tuple(item) for item in phase["failureMatrix"]["B"]])
        self.assertIn(("export-remnote", "unknown_external_state", "effect.commit_unknown"), [tuple(item) for item in phase["failureMatrix"]["D"]])

    def test_settings_snapshot_to_plan_to_request_chain_is_hashed_and_schema_valid(self) -> None:
        for path in self.result["paths"].values():
            chain = path["settingsSnapshotChain"]
            self.assertEqual("PASS", chain["status"])
            for key in ("manifest", "snapshotArtifact", "snapshot", "resolvedPlan"):
                self.assertEqual(64, len(chain[key]["sha256"]))

    def test_readable_index_accepts_structured_original_inputs(self) -> None:
        index = gate.render_index(self.result)
        self.assertIn("# Phase B candidate quality index", index)
        self.assertIn("Speech 1.2.3 independently proves physical playback", index)
        self.assertIn("NO-GO-V1.2.8", index)


if __name__ == "__main__":
    unittest.main()
