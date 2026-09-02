from __future__ import annotations

import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tools"))

import verify_integrated_candidate_quality as gate  # noqa: E402


class IntegratedCandidateQualityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.result = gate.verify_all()

    def test_candidate_and_authority_are_independently_byte_exact(self) -> None:
        binding = self.result["candidateBinding"]
        desktop = self.result["gates"]["desktop"]
        self.assertEqual("B5437F88D6A6B77BD194ED85A7ECEE57AA88ACAA7A17F75123CD753BDDFD21F7", binding["candidateTreeSha256"])
        self.assertEqual("7B27BACDB1DBB56991272972A5F9309B1D6F782787EB0B99EBD21248F7959574", binding["authorityTreeSha256"])
        self.assertEqual((331, 400642976), (desktop["candidate"]["files"], desktop["candidate"]["bytes"]))
        self.assertEqual((79, 248982362, 0), (desktop["authority"]["files"], desktop["authority"]["bytes"], desktop["authority"]["mismatches"]))
        self.assertTrue(desktop["candidate"]["unchangedByGate"])
        self.assertEqual((243, 0), (desktop["desktopPayload"]["files"], desktop["desktopPayload"]["changedFiles"]))

    def test_desktop_readback_is_safe_visible_and_cleans_canary_process(self) -> None:
        desktop = self.result["gates"]["desktop"]
        self.assertEqual("PASS-SAFE", desktop["startup"]["status"])
        self.assertEqual((0, False), (desktop["startup"]["externalConnections"], desktop["startup"]["productionWorkDirectoryCreated"]))
        self.assertEqual("PASS-NOT-SILENT", desktop["singleInstance"]["status"])
        self.assertEqual("activate-existing-window-and-message-box", desktop["singleInstance"]["feedbackMode"])
        self.assertEqual("task.status.query-recent", desktop["orchestrator"]["namedPipeAction"])
        self.assertFalse(desktop["orchestrator"]["externalWorkerInvoked"])
        self.assertFalse(desktop["orchestrator"]["processStillRunningAtIndependentReadback"])
        self.assertEqual(("PASS-INHERITED-BYTE-EXACT-DESKTOP", 84, False), (desktop["windowsUi"]["status"], desktop["windowsUi"]["assertions"], desktop["windowsUi"]["manualUiRerunRequired"]))

    def test_ocr_forward_binding_and_current_anki_pass_are_not_conflated_with_history(self) -> None:
        ocr = self.result["gates"]["ocrToDeepSeek"]
        anki = self.result["gates"]["anki"]
        self.assertEqual("PASS-EXACT-AUTHORITY", ocr["status"])
        self.assertEqual((4, 8, 4, 2), tuple(ocr["observed"][key] for key in ("ocrItems", "ocrOutputs", "mineruRawZips", "cards")))
        self.assertEqual("PASS-BYTE-EXACT-UNCHANGED-WORKERS", ocr["forwardBindingToV123"]["status"])
        self.assertEqual(0, ocr["externalCallsPerformedByThisQualityGate"])
        self.assertEqual("PASS-EXACT-AUTHORITY-IDEMPOTENT", anki["status"])
        self.assertEqual(("succeeded", 4, 0, 0), tuple(anki["output"][key] for key in ("orchestratorStatus", "alreadyCurrent", "newNotes", "deleted")))
        blocker = self.result["historicalEvidence"]["v122AnkiBlocker"]
        self.assertEqual(("NO-GO-EXACT-AUTHORITY", "ANCESTRY_PATH_INVALID"), (blocker["status"], blocker["output"]["errorCode"]))

    def test_overall_gate_stays_no_go_without_release_seal_or_physical_boundaries(self) -> None:
        self.assertEqual("NO-GO-RELEASE-BOUNDARIES", self.result["status"])
        self.assertEqual("PASS", self.result["candidateScopedQualification"])
        self.assertIsNone(self.result["candidateBinding"]["releaseManifest"])
        self.assertIsNone(self.result["candidateBinding"]["candidateHash"])
        self.assertFalse(self.result["candidateBinding"]["signed"])
        self.assertEqual(0, self.result["qualityExecution"]["externalCallsPerformed"])
        self.assertFalse(self.result["qualityExecution"]["userManualUiRerun"])
        boundaries = "\n".join(self.result["remainingNoGo"])
        self.assertIn("physical speaker", boundaries)
        self.assertIn("installer and code signing", boundaries)

    def test_v123_authority_is_exactly_one_anki_replacement_and_is_candidate_bound(self) -> None:
        authority = self.result["authority"]
        self.assertEqual("PASS-AUTHORITY-ADMITTED", authority["status"])
        self.assertEqual(("1.2.3", 5), (authority["registry"]["version"], authority["registry"]["workers"]))
        self.assertEqual((79, 248982362, "7B27BACDB1DBB56991272972A5F9309B1D6F782787EB0B99EBD21248F7959574"), tuple(authority["release"][key] for key in ("files", "bytes", "treeSha256")))
        self.assertEqual(["workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe"], authority["deltaFromV122"]["changedFiles"])
        self.assertTrue(authority["deltaFromV122"]["onlyAnki115"])
        self.assertEqual(("PASS", "PASS"), (authority["integratedDesktopCandidate"], authority["exactAuthorityAnkiCanary"]))

    def test_v123_exact_authority_anki_is_idempotent_and_zero_mutation(self) -> None:
        anki = self.result["gates"]["anki"]
        self.assertEqual("PASS-EXACT-AUTHORITY-IDEMPOTENT", anki["status"])
        self.assertEqual("7B27BACDB1DBB56991272972A5F9309B1D6F782787EB0B99EBD21248F7959574", anki["authorityTreeSha256"])
        self.assertEqual(("succeeded", 4, 0, 0), tuple(anki["output"][key] for key in ("orchestratorStatus", "alreadyCurrent", "newNotes", "deleted")))
        self.assertTrue(anki["destination"]["allDeckInventoryUnchanged"])
        self.assertEqual(0, anki["externalCallsPerformedByThisQualityGate"])
        self.assertEqual(4, len(anki["cards"]))

    def test_inventory_mismatch_is_fail_closed(self) -> None:
        with tempfile.TemporaryDirectory(prefix="integrated-candidate-negative-") as temporary:
            root = Path(temporary)
            (root / "one.txt").write_text("one", encoding="utf-8")
            actual = gate.inventory(root)
            drifted = copy.deepcopy(actual)
            drifted["files"][0]["sha256"] = "0" * 64
            with self.assertRaises(gate.GateFailure):
                gate.assert_inventory(actual, drifted, "negative fixture")

    def test_latest_emitted_receipt_and_index_are_self_consistent(self) -> None:
        roots = sorted(
            root
            for root in (PROJECT_ROOT / "canary" / "integrated-candidate-quality").glob("integrated-v123-*")
            if (root / "quality-receipt.json").is_file()
        )
        if not roots:
            self.skipTest("append-only integrated candidate quality receipt has not been emitted yet")
        latest = roots[-1]
        receipt_path = latest / "quality-receipt.json"
        receipt = json.loads(receipt_path.read_text(encoding="utf-8-sig"))
        index = receipt["readableIndex"]
        index_path = PROJECT_ROOT / index["path"]
        self.assertEqual(index["sha256"], gate.sha256_file(index_path))
        self.assertEqual(index["bytes"], index_path.stat().st_size)
        text = index_path.read_text(encoding="utf-8")
        self.assertIn("Anki v1.2.3 exact-authority 隔离目标幂等路径：PASS", text)
        self.assertIn("OCR → DeepSeek：PASS", text)
        self.assertIn("整体发布仍 `NO-GO-RELEASE-BOUNDARIES`", text)


if __name__ == "__main__":
    unittest.main(verbosity=2)
