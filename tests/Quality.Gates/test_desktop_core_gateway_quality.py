from __future__ import annotations

import sys
import unittest
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT / "tools"))

from verify_desktop_core_gateway_quality import DELTA_MANIFEST, DELTA_PATHS, load, source_tree  # noqa: E402


class DesktopCoreGatewayQualityTests(unittest.TestCase):
    def test_current_delta_count_and_bytes_are_exact_but_tree_is_recomputed(self) -> None:
        observed = source_tree(PROJECT / path for path in DELTA_PATHS)
        self.assertEqual((9, 59149), (observed["files"], observed["bytes"]))
        self.assertEqual(64, len(observed["treeSha256"]))

    def test_superseding_manifest_matches_current_files(self) -> None:
        observed = source_tree(PROJECT / path for path in DELTA_PATHS)
        manifest = load(DELTA_MANIFEST)
        self.assertEqual(observed["treeSha256"], manifest["sourceSnapshot"]["treeSha256"].upper())
        self.assertEqual("evidence-drift-superseded", manifest["supersedes"]["status"])


if __name__ == "__main__":
    unittest.main()
