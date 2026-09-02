from __future__ import annotations

import sys
import unittest
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT / "tools"))

from verify_core_v3_foundation2_quality import tree  # noqa: E402


class CoreV3Foundation2QualityTests(unittest.TestCase):
    def test_tree_uses_ordinal_release_relative_paths(self) -> None:
        root = PROJECT / "tests/Quality.Gates"
        files = [root / "test_core_v3_foundation_quality.py", root / "test_core_v3_foundation2_quality.py"]
        first = tree(files, PROJECT)
        second = tree(reversed(files), PROJECT)
        self.assertEqual(first["treeSha256"], second["treeSha256"])
        self.assertEqual([item["path"] for item in first["entries"]], sorted(item["path"] for item in first["entries"]))


if __name__ == "__main__":
    unittest.main()
