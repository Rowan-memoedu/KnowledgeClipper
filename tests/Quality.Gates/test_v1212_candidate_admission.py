from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tools"))

import verify_v1212_candidate_admission as gate  # noqa: E402


class V1212CandidateAdmissionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.fixture = gate.load_json(gate.DEFAULT_FIXTURE)
        cls.result = gate.evaluate(copy.deepcopy(cls.fixture), run_binaries=False)

    def test_fixture_accepts_current_and_rejects_premature_candidate_hash(self) -> None:
        result = gate.validate_fixtures()
        self.assertEqual("PASS", result["status"])
        self.assertEqual(2, result["validAccepted"])
        self.assertEqual(1, result["invalidRejected"])

    def test_authority_and_frozen_v1_are_exact(self) -> None:
        authority = self.result["authority"]
        self.assertEqual("PASS-AUTHORITY-ADMITTED", authority["status"])
        self.assertEqual(284, authority["release"]["fileCount"])
        self.assertEqual(260374870, authority["release"]["totalBytes"])
        self.assertEqual("31CA3150B1A2EF9F97DD67B3B5491E94C49275C23018BC75B8BFECACC504DF13", authority["release"]["treeSha256"])
        self.assertEqual(21, authority["frozenV1"]["filesChecked"])
        self.assertEqual(0, authority["frozenV1"]["mismatches"])

    def test_all_five_workers_are_bound_to_latest_versions(self) -> None:
        actual = {key: value["workerVersion"] for key, value in self.result["authority"]["workers"].items()}
        self.assertEqual(gate.EXPECTED_WORKERS, actual)
        self.assertTrue(all(value["status"] == "PASS-STATIC-BINDING" for value in self.result["authority"]["workers"].values()))

    def test_image_is_promoted_but_original_boundary_remains_unsupported(self) -> None:
        image = self.result["modules"]["image"]
        self.assertEqual("PASS-AUTHORITY-BOUND", image["status"])
        self.assertTrue(image["promotionNoGoResolved"])
        self.assertEqual("terminal-unsupported", image["originalMode"]["status"])
        self.assertEqual(0, image["originalMode"]["providerCalls"])
        self.assertFalse(image["originalMode"]["downstreamStarted"])
        self.assertEqual("failed-items-only", image["partial"]["status"])
        self.assertFalse(image["partial"]["unknownExternalAutomaticRetry"])

    def test_anki_post_authority_probe_is_bound_and_side_effect_free(self) -> None:
        anki = self.result["modules"]["anki"]
        self.assertEqual("PASS-CANDIDATE-BOUND-OFFLINE-PROBE", anki["status"])
        self.assertEqual("ready", anki["probe"]["connectionStatus"])
        self.assertFalse(anki["probe"]["externalMutation"])
        self.assertEqual([], anki["safety"]["mutatingActions"])
        self.assertEqual(0, anki["safety"]["realAnkiConnectCalls"])
        self.assertFalse(anki["desktop"]["supplementalFailureAttributedToAnki"])

    def test_all_five_module_outcomes_are_present_without_boundary_promotion(self) -> None:
        modules = self.result["modules"]
        self.assertEqual({"image", "content", "speech", "remnote", "anki"}, set(modules))
        self.assertEqual("PASS-AUTHORITY-BOUND-OFFLINE", modules["content"]["status"])
        self.assertEqual("PASS-AUTHORITY-BOUND-OFFLINE", modules["speech"]["status"])
        self.assertIn("conditional", modules["speech"]["speakerPreview"])
        self.assertEqual("conditional-cli-only", modules["remnote"]["probe"]["status"])

    def test_desktop_is_candidate_bound_but_not_release_go(self) -> None:
        self.assertTrue(self.result["status"].startswith("CONDITIONAL-CANDIDATE-BOUND-V1.2.12"))
        self.assertTrue(self.result["candidateBound"])
        self.assertEqual("5D03C90D42FA2A1145FF6DCC4874BD059AC3EE072628F0A8DC81FB23983B4F3C", self.result["candidateHash"])
        self.assertEqual("PASS-INTEGRATED-V1.2.12-CANDIDATE-BOUND", self.result["desktop"]["status"])
        self.assertFalse(self.result["desktop"]["userHandTestEntrypoint"])
        self.assertFalse(self.result["desktop"]["releaseGo"])

    def test_desktop_manifest_authority_ui_secret_and_inventory_are_bound(self) -> None:
        desktop = self.result["desktop"]
        self.assertEqual(837, desktop["payload"]["fileCount"])
        self.assertEqual(429419977, desktop["payload"]["totalBytes"])
        self.assertEqual("3B7028EE0AA31559FAB65FC0C7F6E5E04A0081A6AFCBB620025F6292ED8F4A4F", desktop["payload"]["treeSha256"])
        self.assertEqual({"fileCount": 838, "totalBytes": 429594994, "treeSha256": "4F1021038635984154290467011FBB25AB9D88DD7A1A035F059CFF309231416A"}, desktop["fullInventory"])
        self.assertEqual({"fileCount": 540, "totalBytes": 168920549, "treeSha256": "31A2FA970E47853F1EF0DF53AF2038E2F5572A9D80BB5068336CA39F1C17D6B6"}, desktop["desktopPayload"])
        self.assertEqual(284, desktop["authoritySubtree"]["fileCount"])
        self.assertEqual(97, desktop["settingsUiAutomation"]["assertions"])
        self.assertEqual(152, desktop["uiAutomation"]["assertions"])
        self.assertEqual("CONDITIONAL-FOREGROUND-FOCUS-NOT-ACQUIRED", desktop["physicalCtrlC"]["status"])
        self.assertTrue(desktop["physicalCtrlC"]["manualEnvironmentBoundary"])
        self.assertEqual(0, desktop["secretScan"]["findingCount"])
        self.assertEqual("PASS-ABSENT", desktop["legacyExternalCanaries"]["status"])
        self.assertEqual("CONSISTENT-WITH-INDEPENDENT-GATE", desktop["ownerReceipt"]["status"])
        self.assertFalse(desktop["ownerReceipt"]["trustedForPass"])

    def test_old_wrong_candidate_receipt_is_explicitly_invalidated(self) -> None:
        superseded = self.result["supersedes"]
        self.assertEqual(1, len(superseded))
        self.assertEqual("42605B57575017D281B04ABB29DAC5C145C26445638D564B346124CF2B715721", superseded[0]["sha256"])
        self.assertEqual("INVALIDATED-WRONG-SUPERSEDED-CANDIDATE", superseded[0]["status"])

    def test_real_boundaries_are_preserved(self) -> None:
        self.assertEqual("conditional-cli-only", self.result["boundaries"]["remnoteProbe"])
        self.assertIn("conditional", self.result["boundaries"]["speakerPreview"])
        self.assertIn("state-aware service not implemented", self.result["boundaries"]["retention"])
        self.assertTrue(all(value == 0 for value in self.result["qualitySideEffects"].values()))

    def test_policy_weakening_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["policy"]["candidateHashBeforeDesktop"] = True
        with self.assertRaisesRegex(gate.GateFailure, "policy weakened"):
            gate.evaluate(fixture)

    def test_authority_hash_drift_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["authority"]["checkpoint"]["sha256"] = "0" * 64
        with self.assertRaisesRegex(gate.GateFailure, "authority.checkpoint: SHA-256 mismatch"):
            gate.evaluate(fixture)

    def test_readable_index_preserves_candidate_and_no_go_boundaries(self) -> None:
        text = gate.render_index(self.result)
        self.assertIn("Candidate-bound: `true`", text)
        self.assertIn("terminal = `unsupported`", text)
        self.assertIn("real AnkiConnect calls/writes = 0", text)


if __name__ == "__main__":
    unittest.main()
