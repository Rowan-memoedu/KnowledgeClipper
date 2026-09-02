from __future__ import annotations

import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tools"))

import qualify_integrated_candidate_v124 as gate  # noqa: E402


class IntegratedV124QualityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.result = gate.verify_all()

    def test_candidate_and_authority_are_exact(self) -> None:
        candidate = self.result["candidateInventory"]
        authority = self.result["authority"]
        self.assertEqual(gate.EXPECTED_CANDIDATE, (candidate["fileCount"], candidate["totalBytes"], candidate["treeSha256"]))
        self.assertEqual(gate.EXPECTED_AUTHORITY, (authority["release"]["fileCount"], authority["release"]["totalBytes"], authority["release"]["treeSha256"]))
        self.assertEqual("PASS-AUTHORITY-ADMITTED", authority["status"])
        self.assertEqual((21, 0, 5), (authority["frozenV1"]["files"], authority["frozenV1"]["mismatches"], len(authority["workers"])))

    def test_stage_control_is_direct_and_does_not_cancel_whole_run(self) -> None:
        stage = self.result["stageControl"]
        self.assertEqual("PASS-DIRECT-IPC-1.1-STAGE-ISOLATED", stage["status"])
        self.assertEqual({"operation": "speech.playback", "selector": "current-active", "reason": "user_stop"}, stage["request"])
        self.assertEqual({"state": "stop-requested", "noticeCode": "playback.stop-requested", "claimsStopped": False}, stage["response"])
        self.assertEqual((0, False, 0, 0, 0), (stage["taskCancelMessagesObserved"], stage["wholeRunCancelRequestedWritten"], stage["workerRunInvocations"], stage["physicalSpeakerInvocations"], stage["externalWrites"]))

    def test_old_process_and_external_side_effect_boundaries_are_preserved(self) -> None:
        old = self.result["desktop"]["oldProcess"]
        safety = self.result["desktop"]["receipt"]["safety"]
        self.assertTrue(old["preservedByCanary"])
        self.assertGreater(old["processId"], 0)
        self.assertFalse(safety["preExistingDesktopProcessTerminated"])
        self.assertFalse(any(safety[key] for key in ("externalWorkersInvoked", "physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked", "externalProductionApisInvoked")))

    def test_remnote_is_still_blocked_and_not_retried(self) -> None:
        remnote = self.result["remnote"]
        self.assertEqual("blocked_external_bridge", remnote["status"])
        self.assertEqual("READ-ONLY-NO-RETRY", remnote["qualityAction"])
        self.assertEqual((0, 0, False, "NOT_RUN"), (remnote["attempt"]["outputs"], remnote["attempt"]["reportedSideEffects"], remnote["attempt"]["readbackVerified"], remnote["attempt"]["duplicateReplay"]))

    def test_manifest_is_deterministic_and_keeps_release_no_go(self) -> None:
        one = gate.manifest_document(self.result)
        two = gate.manifest_document(self.result)
        self.assertEqual(one, two)
        self.assertEqual("NO-GO", one["qualification"]["releaseDecision"])
        self.assertEqual("blocked_external_bridge", one["qualification"]["remnote"])
        self.assertEqual(gate.EXPECTED_CANDIDATE[2], one["candidate"]["treeSha256"])

    def test_reference_hash_drift_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory(prefix="v124-negative-") as temporary:
            path = Path(temporary) / "evidence.json"
            path.write_text("{}\n", encoding="utf-8")
            item = gate.evidence(path)
            drifted = copy.deepcopy(item)
            drifted["sha256"] = "0" * 64
            with self.assertRaises(gate.GateFailure):
                gate.verify_ref(drifted, "negative fixture")

    def test_latest_emitted_receipt_is_self_consistent(self) -> None:
        roots = sorted((PROJECT_ROOT / "canary" / "integrated-candidate-quality").glob("integrated-v124-*"))
        if not roots:
            self.skipTest("v1.2.4 detached identity has not been emitted yet")
        root = roots[-1]
        receipt = json.loads((root / "quality-receipt.json").read_text(encoding="utf-8-sig"))
        manifest = receipt["candidateIdentity"]["manifest"]
        index = receipt["readableIndex"]
        self.assertEqual(receipt["candidateIdentity"]["candidateHash"], manifest["sha256"])
        self.assertEqual(manifest["sha256"], gate.common.sha256_file(PROJECT_ROOT / manifest["path"]))
        self.assertEqual(index["sha256"], gate.common.sha256_file(PROJECT_ROOT / index["path"]))
        self.assertEqual("blocked_external_bridge", receipt["gates"]["remnote"]["status"])
        self.assertEqual((0, 0, 0, 0, 0, 0), tuple(receipt["qualityExecution"][key] for key in ("externalWorkersStarted", "physicalSpeakerInvocations", "remNoteInvocations", "ankiInvocations", "mineruInvocations", "productionApiCalls")))


if __name__ == "__main__":
    unittest.main(verbosity=2)
