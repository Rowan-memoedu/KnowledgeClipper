import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import verify_content_language_mode_fixture as gate

RECEIPT = Path(r"D:\Artifacts\KnowledgeClipper\content-language-card-modes\20260903T120000Z-existing-live-reuse\receipt.v1.json")


class ContentLanguageModeFixtureTests(unittest.TestCase):
    def test_authority_fixture_passes(self):
        result = gate.evaluate(RECEIPT)
        self.assertEqual("PASS-CONTENT-FIXTURE-ADMISSION", result["status"])
        self.assertEqual(["language_context", "language_writing"], result["scenarios"][2]["templates"])

    def test_text_hash_is_utf8_exact(self):
        source = json.loads(RECEIPT.read_text(encoding="utf-8"))
        self.assertEqual(gate.EXPECTED_RAW, gate.sha_text(source["originalInput"]["text"]))

    def test_missing_mode_is_rejected(self):
        source = json.loads(RECEIPT.read_text(encoding="utf-8"))
        source["scenarios"].pop()
        self.assertNotEqual({item["mode"] for item in source["scenarios"]}, {"meaning-reading", "writing", "both"})

    def test_both_order_is_strict(self):
        source = json.loads(RECEIPT.read_text(encoding="utf-8"))
        both = next(item for item in source["scenarios"] if item["mode"] == "both")
        self.assertEqual(["language_context", "language_writing"], [card["template"] for card in both["cardBundle"]["cards"]])


if __name__ == "__main__":
    unittest.main()
