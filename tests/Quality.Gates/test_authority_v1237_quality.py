import importlib.util
import unittest
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("authority_v1237_quality", PROJECT / "tools/verify_authority_v1237_quality.py")
gate = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(gate)


class AuthorityV1237QualityTests(unittest.TestCase):
    def test_expected_inputs_are_full_sha256_values(self):
        self.assertTrue(all(len(value) == 64 for value in gate.EXPECTED.values()))

    def test_release_tree_is_recomputed_from_files(self):
        observed, files = gate.inventory(gate.RELEASE)
        self.assertEqual(404, observed["fileCount"])
        self.assertEqual(262238747, observed["totalBytes"])
        self.assertEqual("973E63B7F876A650D6EF4CFAF6D31F6386510BDE81FB2F096F83C403982CECCC", observed["treeSha256"])
        self.assertEqual(5, sum(1 for path in files if path.endswith(".Worker.exe")))

    def test_unadmitted_versions_are_rejected(self):
        for version, path in gate.INVALIDATIONS.items():
            data = gate.load(path)
            self.assertEqual(version, data["registryVersion"])
            self.assertEqual("invalidated-never-authority-admitted", data["status"])
            self.assertFalse(data["authorityCheckpointIssued"])
            self.assertFalse(data["authorityReceiptIssued"])

    def test_history_hashes_are_not_current_authority_hashes(self):
        self.assertEqual(gate.EXPECTED["historicalPlayback"], gate.sha256(gate.PATHS["historicalPlayback"]))
        self.assertNotEqual(gate.EXPECTED["historicalPlayback"], gate.EXPECTED["coreValidation"])


if __name__ == "__main__":
    unittest.main()
