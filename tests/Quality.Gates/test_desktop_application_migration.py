from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT / "tools"))

import verify_desktop_application_migration as gate  # noqa: E402


class DesktopApplicationMigrationQualityTests(unittest.TestCase):
    def test_superseding_owner_source_and_state_chain_pass(self) -> None:
        owner = gate.common.load(gate.OWNER_RECEIPT)
        measured = gate.measure_source_snapshot(owner)
        self.assertEqual(24, measured["files"])
        self.assertEqual(773354, measured["bytes"])
        self.assertEqual("C4E4C2BE749BD4BC6137422E5C04D8B829A4B18682F87D6CC90A29CBE3798A5E", measured["treeSha256"])
        self.assertEqual(measured["treeSha256"], measured["claimedTreeSha256"])
        result = gate.evaluate()
        self.assertEqual("PASS-DESKTOP-APPLICATION-STATE-CHAIN-CANDIDATE-ASSEMBLY-READY", result["status"])
        self.assertFalse(result["promotion"]["userHandTestEntrypoint"])

    def test_supersede_chain_preserves_exact_historical_no_go(self) -> None:
        owner = gate.common.load(gate.OWNER_RECEIPT)
        self.assertEqual(gate.common.sha256_file(gate.PREDECESSOR_RECEIPT), owner["supersedes"]["sha256"].upper())
        self.assertEqual(gate.common.sha256_file(gate.PREDECESSOR_QUALITY_NO_GO), owner["supersedesQualityNoGo"]["sha256"].upper())

    def test_source_snapshot_rejects_sha_drift(self) -> None:
        owner = gate.common.load(gate.OWNER_RECEIPT)
        drift = copy.deepcopy(owner)
        drift["sourceSnapshot"]["entries"][0]["sha256"] = "0" * 64
        with self.assertRaises(RuntimeError):
            gate.verify_source_snapshot(drift)

    def test_windows_boundaries_remain_honest(self) -> None:
        result = gate.verify_windows_receipt()
        receipt = gate.common.load(gate.WINDOWS_RECEIPT)
        self.assertEqual("PASS", result["focusedCtrlC"])
        self.assertEqual("PENDING", receipt["realWindowsCanaries"]["externalApplicationSelectionThroughNoActivateFloatingButton"]["status"])
        self.assertEqual("PENDING", receipt["realWindowsCanaries"]["crossMonitorDpiDrag"]["status"])


if __name__ == "__main__":
    unittest.main()
