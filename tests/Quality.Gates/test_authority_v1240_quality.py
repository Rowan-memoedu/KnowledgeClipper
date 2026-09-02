from __future__ import annotations

import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[2]
SCRIPT = PROJECT / "tools" / "verify_authority_v1240_quality_independent.py"
sys.path.insert(0, str(PROJECT / "tools"))
SPEC = importlib.util.spec_from_file_location("verify_authority_v1240_quality", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class AuthorityV1240QualityTests(unittest.TestCase):
    def test_inventory_is_ordinal_and_content_bound(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "b.txt").write_text("b", encoding="utf-8")
            (root / "A.txt").write_text("a", encoding="utf-8")
            first = MODULE.common.inventory(root, include_files=True)
            self.assertEqual(["A.txt", "b.txt"], [item["path"] for item in first["fileInventory"]])
            (root / "b.txt").write_text("changed", encoding="utf-8")
            second = MODULE.common.inventory(root)
            self.assertNotEqual(first["treeSha256"], second["treeSha256"])

    def test_compare_inventory_rejects_tree_drift(self) -> None:
        actual = {"files": 1, "bytes": 1, "treeSha256": "A" * 64}
        with self.assertRaises(MODULE.GateFailure):
            MODULE.compare_inventory(actual, 1, 1, "B" * 64, "fixture")

    def test_release_and_reference_constants_are_exact(self) -> None:
        self.assertEqual("knowledge-clipper-authority-v1.2.40-20260902T012900Z", MODULE.common.RELEASE_ID)
        self.assertEqual(64, len(MODULE.common.CHECKPOINT_SHA))
        self.assertEqual(64, len(MODULE.common.EXE_SHA))
        self.assertEqual(64, len(MODULE.CORE_REFERENCE_SHA))


if __name__ == "__main__":
    unittest.main()
