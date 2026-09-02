import importlib.util
import sys
import unittest
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT / "tools"))
SPEC = importlib.util.spec_from_file_location("v1237_desktop_candidate", PROJECT / "tools/verify_v1237_desktop_candidate.py")
gate = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(gate)


class V1237DesktopCandidateTests(unittest.TestCase):
    def test_manifest_and_sealed_tree_are_distinct(self):
        result = gate.verify_manifest()
        self.assertEqual(1063, result["payload"]["fileCount"])
        self.assertEqual(1064, result["sealed"]["fileCount"])
        self.assertNotEqual(result["payload"]["treeSha256"], result["sealed"]["treeSha256"])

    def test_authority_is_exactly_embedded(self):
        result = gate.verify_authority()
        self.assertEqual("1.2.37", result["version"])
        self.assertEqual(404, result["fileCount"])
        self.assertEqual(gate.EXPECTED["authority"][2], result["treeSha256"])

    def test_historical_failure_is_retained(self):
        result = gate.verify_history()
        self.assertEqual("retained-immutable", result["status"])
        self.assertTrue(result["currentPassDoesNotEraseHistory"])


if __name__ == "__main__":
    unittest.main()
