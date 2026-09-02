from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT / "tools"))

from verify_core_v3_phase2_slice_quality import GateFailure, resolve_release_ref, tree  # noqa: E402


class CoreV3Phase2SliceQualityTests(unittest.TestCase):
    def test_tree_is_ordinal_and_order_independent(self) -> None:
        root = PROJECT / "tests/Quality.Gates"
        files = [root / "test_core_v3_phase2_slice_quality.py", root / "test_core_v3_foundation2_quality.py"]
        first = tree(files, PROJECT)
        second = tree(reversed(files), PROJECT)
        self.assertEqual(first["treeSha256"], second["treeSha256"])
        self.assertEqual([item["path"] for item in first["entries"]], sorted(item["path"] for item in first["entries"]))

    def test_release_reference_rejects_escape(self) -> None:
        with self.assertRaises(GateFailure):
            resolve_release_ref({"path": "../checkpoint.json", "bytes": 0, "sha256": "0" * 64}, "negative")

    def test_tree_changes_when_bytes_change(self) -> None:
        with tempfile.TemporaryDirectory() as value:
            root = Path(value)
            path = root / "fixture.txt"
            path.write_text("one", encoding="utf-8")
            first = tree([path], root)["treeSha256"]
            path.write_text("two", encoding="utf-8")
            second = tree([path], root)["treeSha256"]
            self.assertNotEqual(first, second)


if __name__ == "__main__":
    unittest.main()
