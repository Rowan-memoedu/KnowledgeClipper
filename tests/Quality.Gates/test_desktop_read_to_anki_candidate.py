from __future__ import annotations

import sys
import unittest
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT / "tools"))

import verify_desktop_read_to_anki_candidate as gate  # noqa: E402


class DesktopReadToAnkiCandidateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.result = gate.evaluate(gate.DEFAULT_CANDIDATE, gate.DEFAULT_OWNER_EVIDENCE, gate.DEFAULT_CORE_QUALITY, gate.DEFAULT_RUNTIME_EVIDENCE, gate.DEFAULT_OLD_UNSEALED)

    def test_candidate_manifest_and_sealed_inventory_are_exact(self) -> None:
        self.assertEqual((795, 342042574, gate.EXPECTED["payloadTree"]), tuple(self.result["candidate"]["payload"][key] for key in ("files", "bytes", "treeSha256")))
        self.assertEqual((796, 342195158, gate.EXPECTED["sealedTree"]), tuple(self.result["candidate"]["sealed"][key] for key in ("files", "bytes", "treeSha256")))

    def test_exact_core_sdk_and_authority_binding(self) -> None:
        self.assertEqual(gate.EXPECTED["client"], self.result["bindings"]["coreClient"]["sha256"])
        self.assertEqual(gate.EXPECTED["sdkFreeze"], self.result["bindings"]["sdkFreezeManifest"]["sha256"])
        self.assertEqual(gate.EXPECTED["authorityTree"], self.result["bindings"]["authorityTreeSha256"])

    def test_offline_startup_named_pipe_and_tests_pass(self) -> None:
        self.assertTrue(self.result["independentRuntime"]["orchestratorProcessCleaned"])
        self.assertEqual(8, self.result["independentRuntime"]["namedPipeCapabilities"])
        self.assertEqual((64, 21, 8), tuple(self.result["independentTests"][key]["assertions"] for key in ("application", "gateway", "windowsPreview")))

    def test_user_handtest_is_scoped_and_not_release_go(self) -> None:
        self.assertTrue(self.result["decision"]["userHandTestEntrypoint"])
        self.assertEqual("NO-GO", self.result["decision"]["releasePromotion"])
        self.assertEqual("PENDING-NEXT-SDK", self.result["decision"]["questionFollowupPromotion"])

    def test_old_contaminated_candidate_is_not_entrypoint(self) -> None:
        self.assertFalse(self.result["invalidatedInputs"][0]["userEntrypoint"])
        self.assertEqual("unsealed-directory-contamination", self.result["invalidatedInputs"][0]["reason"])


if __name__ == "__main__":
    unittest.main()
