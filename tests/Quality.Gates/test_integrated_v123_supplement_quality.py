from __future__ import annotations

import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tools"))

import qualify_integrated_candidate_v123 as gate  # noqa: E402


class IntegratedV123SupplementQualityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.result = gate.verify_all()

    def test_content_real_question_and_language_modes_are_exact(self) -> None:
        content = self.result["content"]
        self.assertEqual("PASS-EXACT-AUTHORITY", content["status"])
        self.assertEqual((3, ["waiting_input", "waiting_input", "succeeded"]), (content["question"]["historicalRealCalls"], content["question"]["states"]))
        self.assertEqual(["meaning-reading", "writing", "both"], [item["mode"] for item in content["languagePractice"]])
        self.assertEqual([1, 1, 4], [len(item["cards"]) for item in content["languagePractice"]])
        self.assertEqual((59, 0, 0), (content["tests"]["passed"], content["tests"]["failed"], content["exporterCalls"]))

    def test_speech_candidate_binding_is_real_edge_without_speaker(self) -> None:
        speech = self.result["speech"]
        self.assertEqual("PASS-EXACT-AUTHORITY-NO-SPEAKER", speech["status"])
        self.assertEqual((14, 5, 5, 6, 18), tuple(speech["observed"][key] for key in ("realEdgeSynthesisCalls", "auto", "override", "playbackAttempts", "contractArtifacts")))
        self.assertFalse(speech["physicalSpeakerPlayback"])
        self.assertEqual(0, speech["externalProductionWrites"])

    def test_remnote_blocker_is_preserved_without_retry_or_pass_promotion(self) -> None:
        remnote = self.result["remnote"]
        self.assertEqual("blocked_external_bridge", remnote["status"])
        self.assertEqual((0, 0, False, "NOT_RUN"), (remnote["attempt"]["outputs"], remnote["attempt"]["reportedSideEffects"], remnote["attempt"]["readbackVerified"], remnote["attempt"]["duplicateReplay"]))
        self.assertEqual("READ-ONLY-NO-RETRY", remnote["qualityAction"])
        self.assertFalse(remnote["scope"]["formalClippingDocumentTargeted"])
        self.assertEqual("FAILED-HASH-DRIFT", remnote["evidenceIntegrity"]["plan"]["status"])

    def test_manifest_is_deterministic_and_binds_existing_candidate_without_mutation(self) -> None:
        manifest_one = gate.manifest_document(self.result)
        manifest_two = gate.manifest_document(self.result)
        self.assertEqual(manifest_one, manifest_two)
        self.assertEqual((331, 400642976, "B5437F88D6A6B77BD194ED85A7ECEE57AA88ACAA7A17F75123CD753BDDFD21F7"), tuple(manifest_one["candidate"][key] for key in ("files", "bytes", "treeSha256")))
        self.assertEqual("blocked_external_bridge", manifest_one["qualification"]["remnote"])
        self.assertEqual("NO-GO", manifest_one["qualification"]["releaseDecision"])

    def test_evidence_hash_drift_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory(prefix="v123-supplement-negative-") as temporary:
            path = Path(temporary) / "evidence.txt"
            path.write_text("evidence", encoding="utf-8")
            item = gate.evidence_record(path)
            drifted = copy.deepcopy(item)
            drifted["sha256"] = "0" * 64
            with self.assertRaises(gate.GateFailure):
                gate.verify_item(drifted, "negative evidence")

    def test_latest_emitted_identity_receipt_is_self_consistent(self) -> None:
        roots = sorted((PROJECT_ROOT / "canary" / "integrated-candidate-quality").glob("integrated-v123-supplement-*"))
        if not roots:
            self.skipTest("supplement identity has not been emitted yet")
        root = roots[-1]
        receipt = json.loads((root / "quality-supplement-receipt.json").read_text(encoding="utf-8-sig"))
        manifest = receipt["candidateIdentity"]["manifest"]
        index = receipt["readableIndex"]
        self.assertEqual(receipt["candidateIdentity"]["candidateHash"], manifest["sha256"])
        self.assertEqual(manifest["sha256"], gate.base_gate.sha256_file(PROJECT_ROOT / manifest["path"]))
        self.assertEqual(index["sha256"], gate.base_gate.sha256_file(PROJECT_ROOT / index["path"]))
        self.assertEqual("blocked_external_bridge", receipt["gates"]["remnote"]["status"])
        self.assertEqual(0, receipt["qualityExecution"]["externalCallsPerformed"])
        self.assertFalse(receipt["qualityExecution"]["externalWritesRetried"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
