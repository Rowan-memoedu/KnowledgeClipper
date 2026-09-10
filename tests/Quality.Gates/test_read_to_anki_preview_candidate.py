from __future__ import annotations

import sys
import unittest
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT / "tools"))

import verify_read_to_anki_preview_candidate as gate  # noqa: E402


class ReadToAnkiPreviewCandidateQualityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.result = gate.evaluate(
            gate.DEFAULT_CANDIDATE,
            gate.DEFAULT_CREATED_CANDIDATE,
            gate.DEFAULT_FAILED_CANDIDATE,
            gate.DEFAULT_GATE_MATRIX,
        )

    def test_manifest_and_entrypoints_are_exact(self) -> None:
        candidate = self.result["candidate"]
        self.assertEqual((145, 172817632, 0), (candidate["declaredFiles"], candidate["declaredBytes"], candidate["declaredMismatches"]))
        self.assertEqual(gate.EXPECTED["client"], candidate["coreClient"]["sha256"])
        self.assertEqual(gate.EXPECTED["anki"], candidate["ankiEntrypoint"]["sha256"])
        self.assertEqual([], candidate["unexpectedPostManifestFiles"])

    def test_preview_precedes_export_and_card_is_exact(self) -> None:
        gates = {item["id"]: item["status"] for item in self.result["gates"]}
        self.assertEqual("PASS", gates["before-confirm-zero-exporter"])
        self.assertEqual("PASS", gates["preview-card-bundle-equivalence"])
        self.assertTrue(self.result["inputOutput"]["previewToReadbackExact"])
        self.assertEqual("ALREADY_CURRENT", self.result["inputOutput"]["outcome"])

    def test_restart_recovery_and_replay_are_exact(self) -> None:
        self.assertEqual(2, len(self.result["runtime"]["ownerInstanceIds"]))
        self.assertEqual(2, self.result["runtime"]["attemptCounts"]["speech-playback"])
        self.assertEqual(1, self.result["runtime"]["attemptCounts"]["anki-export"])

    def test_missing_negative_command_payloads_remain_conditional(self) -> None:
        gates = {item["id"]: item["status"] for item in self.result["gates"]}
        self.assertFalse(self.result["runtime"]["rawConfirmPayloadPersisted"])
        self.assertFalse(self.result["runtime"]["durableNegativeEvidence"])
        self.assertEqual("CONDITIONAL", gates["confirm-token-binding-and-single-use"])
        self.assertEqual("PENDING", gates["cancel-followup-revision"])

    def test_historical_created_and_later_failure_are_not_conflated(self) -> None:
        history = self.result["historicalEvidence"]
        self.assertEqual(1, history["priorCreatedDifferentCandidate"]["externalWriteCount"])
        self.assertEqual("edge_tts_failed", history["laterSpeechFailurePreserved"]["errorCode"])
        self.assertEqual(0, history["laterSpeechFailurePreserved"]["ankiAttempts"])

    def test_quality_gate_has_no_external_effects(self) -> None:
        self.assertEqual(
            (0, 0, 0, False),
            (
                self.result["executionBoundary"]["externalCallsByQualityGate"],
                self.result["executionBoundary"]["externalWritesByQualityGate"],
                self.result["executionBoundary"]["speakerInvocationsByQualityGate"],
                self.result["executionBoundary"]["candidateBinariesExecutedByQualityGate"],
            ),
        )


if __name__ == "__main__":
    unittest.main()
