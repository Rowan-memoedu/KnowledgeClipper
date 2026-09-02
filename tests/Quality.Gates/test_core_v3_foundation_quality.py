from __future__ import annotations

import sys
import unittest
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT / "tools"))

from verify_core_v3_foundation_quality import summarize_ref_observations, tree_hash  # noqa: E402


class CoreV3FoundationQualityTests(unittest.TestCase):
    def test_tree_hash_makes_windows_ordering_explicit(self) -> None:
        entries = [
            {"path": "a/file", "sha256": "A" * 64, "bytes": 1},
            {"path": "Z/file", "sha256": "B" * 64, "bytes": 2},
        ]
        self.assertNotEqual(
            tree_hash(entries, casefold_order=True),
            tree_hash(entries, casefold_order=False),
        )

    def test_drift_is_classified_instead_of_hidden_or_thrown(self) -> None:
        summary = summarize_ref_observations(
            [
                {"status": "unchanged"},
                {"status": "drifted"},
                {"status": "missing"},
            ]
        )
        self.assertEqual(
            summary,
            {"total": 3, "unchanged": 1, "drifted": 1, "missing": 1},
        )


if __name__ == "__main__":
    unittest.main()
