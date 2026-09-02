from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tools"))

import verify_v1214_question_anki_qualification as gate  # noqa: E402


class V1214QuestionAnkiQualificationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.fixture = gate.common.load_json(gate.DEFAULT_FIXTURE)
        cls.result = gate.evaluate(copy.deepcopy(cls.fixture))

    def test_fixture_rejects_premature_desktop_write_retry_and_081(self) -> None:
        value = gate.validate_fixtures()
        self.assertEqual((1, 1), (value["validAccepted"], value["invalidRejected"]))

    def test_authority_v1214_release_and_frozen_v1_are_exact(self) -> None:
        authority = self.result["authority"]
        self.assertEqual("PASS-AUTHORITY-V1.2.14-EXACT", authority["status"])
        self.assertEqual((296, 260669250, "30F1A8E65A335811545114474573196B689657DA8BAFB511E7A2F84638DB9D3E"), (authority["release"]["fileCount"], authority["release"]["totalBytes"], authority["release"]["treeSha256"]))
        self.assertEqual(0, authority["frozenV1"]["mismatches"])

    def test_core_offline_receipts_and_ocr_unresolved_projection_pass(self) -> None:
        core = self.result["core"]
        self.assertEqual("PASS-CORE-2.5.3-OFFLINE", core["status"])
        self.assertEqual({"adapterGroups": 12, "orchestratorTests": 23, "schemas": 64, "validFixtures": 67, "invalidFixtures": 55}, core["counts"])
        self.assertEqual({"taskStatus": "waiting_input", "exportBlocked": True, "automaticRetry": False}, core["ocrUnresolved"])

    def test_actual_packaged_orchestrator_started_exact_workers(self) -> None:
        sealed = self.result["sealedQuestionAnki"]
        self.assertEqual("PASS-SEALED-ACTUAL-ORCHESTRATOR-QUESTION-ANKI", sealed["status"])
        self.assertEqual({"content.deepseek@0.8.0": 3, "export.anki@1.2.2": 1}, sealed["workers"])

    def test_initial_followup_write_zero_and_make_cards_write_one(self) -> None:
        sealed = self.result["sealedQuestionAnki"]
        self.assertEqual(0, sealed["content"]["initialWrites"])
        self.assertEqual(0, sealed["content"]["followUpWrites"])
        self.assertEqual(1, sealed["anki"]["writeCount"])
        self.assertTrue(sealed["anki"]["exactReadbackPassed"])

    def test_replay_creates_no_attempt_or_note(self) -> None:
        replay = self.result["sealedQuestionAnki"]["replay"]
        self.assertEqual((0, 0, 0), (replay["newContentAttempts"], replay["newAnkiAttempts"], replay["newNotes"]))
        self.assertTrue(replay["commandIdentityReused"])

    def test_interactive_command_root_is_durable_and_not_desktop_artifact(self) -> None:
        root = self.result["sealedQuestionAnki"]["interactiveCommandRoot"]
        self.assertEqual("PASS-DURABLE-COMMAND-ROOT", root["status"])
        self.assertFalse(root["desktopArtifactUsedAsRoot"])
        self.assertIn("commands/", root["payload"]["path"])

    def test_content_081_is_still_rejected(self) -> None:
        self.assertEqual("REJECTED-NEVER-PROMOTED", self.result["content081"]["status"])
        self.assertFalse(self.result["content081"]["authorityRegistryAllowed"])

    def test_v1213_failure_is_preserved_and_new_task_supersedes_it(self) -> None:
        supersedes = self.result["supersedesHistoricalFailure"]
        self.assertFalse(supersedes["oldAttemptRetried"])
        self.assertTrue(supersedes["newImmutableTask"].startswith("question-anki-v1214-"))

    def test_desktop_candidate_gate_remains_pending(self) -> None:
        desktop = self.result["desktopCandidateGate"]
        self.assertEqual("PENDING-FINAL-REBASE", desktop["status"])
        self.assertFalse(desktop["candidateBound"])


if __name__ == "__main__":
    unittest.main()
