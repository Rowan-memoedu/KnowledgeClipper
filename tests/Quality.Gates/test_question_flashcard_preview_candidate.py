import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import verify_question_flashcard_preview_candidate as gate


class QuestionFlashcardGateTests(unittest.TestCase):
    def test_live_evidence_passes(self):
        result = gate.evaluate(
            Path(r"D:\Artifacts\KnowledgeClipper\read-to-anki\rta-20260903T203744Z-00ff5c75"),
            Path(r"D:\Artifacts\KnowledgeClipper\question-flashcard-sdk\question-flashcard-sdk-20260903T203910Z-334eafc1"),
        )
        self.assertEqual("PASS-SCOPED-OFFLINE", result["status"])

    def test_manifest_mutation_fails(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            path = root / "x"
            path.write_text("x", encoding="utf-8")
            with self.assertRaises(gate.GateFailure):
                gate.verify_manifest(root, {"files": [{"path": "x", "bytes": 1, "sha256": "0" * 64}]})

    def test_restart_identity_mutation_is_detectable(self):
        source = json.loads((Path(r"D:\Artifacts\KnowledgeClipper\read-to-anki\rta-20260903T203744Z-00ff5c75") / "canary/fake-question-preview-confirm/receipt.json").read_text(encoding="utf-8"))
        changed = copy.deepcopy(source["question"]["recoveredDraft"]["cardDraft"])
        changed["revision"] = "0" * 64
        self.assertNotEqual(source["question"]["draft"]["cardDraft"], changed)

    def test_negative_and_cancel_evidence_present(self):
        source = json.loads((Path(r"D:\Artifacts\KnowledgeClipper\read-to-anki\rta-20260903T203744Z-00ff5c75") / "canary/fake-question-preview-confirm/receipt.json").read_text(encoding="utf-8"))
        self.assertTrue(source["question"]["staleTokenRejectedBeforeExporter"])
        self.assertTrue(source["question"]["doubleConfirmRejected"])
        self.assertIsNone(source["question"]["cancelled"]["ankiSemanticReadback"])


if __name__ == "__main__":
    unittest.main()
