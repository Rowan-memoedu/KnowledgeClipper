from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT / "tools"))

import verify_next_desktop_regressions as gate  # noqa: E402


class NextDesktopRegressionGateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.fixture = gate.load(gate.DEFAULT_FIXTURE)

    def test_waiting_state_is_honest_and_scoped(self) -> None:
        result = gate.evaluate(copy.deepcopy(self.fixture))
        self.assertEqual("WAITING-NEXT-AUTHORITY-DESKTOP-EVIDENCE", result["status"])
        self.assertIsNone(result["candidateBinding"])
        self.assertEqual("NOT-EVALUATED-NO-NEXT-CANDIDATE", result["releaseDecision"])

    def test_original_mode_is_anki_only_and_remnote_media_is_nonblocking(self) -> None:
        result = gate.evaluate(copy.deepcopy(self.fixture))
        self.assertEqual(["anki"], result["policy"]["originalModeDestinations"])
        self.assertEqual("BY-DESIGN-UNSUPPORTED-NOT-A-GAP-NONBLOCKING", result["policy"]["remnoteOriginalMedia"])

    def test_remnote_text_and_cards_still_require_exact_title_and_probe(self) -> None:
        result = gate.evaluate(copy.deepcopy(self.fixture))
        self.assertEqual("EXACT-TITLE-DISCOVERY-AND-READONLY-PROBE-REQUIRED", result["policy"]["remnoteTextAndCards"])
        self.assertEqual("WAITING-CORE-ADMISSION", result["regressions"]["remnoteExactTitleDiscovery"]["moduleStatus"])

    def test_three_regressions_are_the_only_next_candidate_scope(self) -> None:
        result = gate.evaluate(copy.deepcopy(self.fixture))
        self.assertEqual({"ankiReadonlyProbe", "remnoteExactTitleDiscovery", "questionFollowUpContinuation"}, set(result["regressions"]))

    def test_remnote_original_media_cannot_be_reintroduced_as_blocker(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["productPolicy"]["originalMode"]["remnoteOriginalMedia"]["releaseBlocker"] = True
        with self.assertRaisesRegex(gate.GateFailure, "release blocker"):
            gate.evaluate(fixture)

    def test_candidate_cannot_pass_without_all_three_exact_regressions(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["nextCandidateEvidence"] = {
            "authorityCheckpoint": fixture["moduleEvidence"]["productPolicyDelta"],
            "candidateManifest": fixture["moduleEvidence"]["desktopProducerQuality"],
            "regressions": {
                "ankiReadonlyProbe": {"status": "passed", "v1V2PlanConfigMatched": True, "localPackageAndConnectionErrorsDistinct": True},
                "remnoteExactTitleDiscovery": {"status": "waiting", "exactTitleMatched": False, "readOnlyProbe": True, "originalMediaEvaluated": False},
                "questionFollowUpContinuation": {"status": "passed", "canonicalOrdinalArtifact": True, "taskGlobalManifest": True, "commandOwnedPayload": True},
            },
        }
        with self.assertRaisesRegex(gate.GateFailure, "RemNote exact-title"):
            gate.evaluate(fixture)

    def test_fixture_pair_accepts_valid_and_rejects_legacy_blocker(self) -> None:
        self.assertEqual({"status": "PASS", "validAccepted": 1, "invalidRejected": 1}, gate.validate_fixtures())


if __name__ == "__main__":
    unittest.main()
