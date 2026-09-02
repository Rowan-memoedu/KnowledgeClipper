import json
import sys
import tempfile
import unittest
from pathlib import Path


WORKER = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(WORKER))

import check_language_writing_readiness as readiness


TEST_LANGUAGE_OUTPUT_RELATIVE = Path("language-output")


def write_json(root: Path, relative_path: str, value: object) -> None:
    path = root / relative_path
    readiness._io_path(path.parent).mkdir(parents=True, exist_ok=True)
    readiness._io_path(path).write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")


def build_repository(root: Path, *, ordered: bool = True) -> None:
    output = TEST_LANGUAGE_OUTPUT_RELATIVE.as_posix()
    cards = [
        {"card_id": "context", "variant_group_id": "both-1", "template": "language_context"},
        {
            "card_id": "writing",
            "item_id": "item-1",
            "unit_id": "unit-1",
            "variant_group_id": "both-1",
            "template": "language_writing",
            "front_blocks": [{"type": "text", "text": "他心中仍有（yí dòu；心里的疑问）。"}],
            "back_blocks": [{"type": "text", "text": "疑窦"}],
        },
    ]
    if not ordered:
        cards.reverse()
    write_json(root, f"{output}/card-bundle.schema-v1.json", {"cards": cards})
    write_json(root, f"{output}/learning-bundle.schema-v1.json", {
        "items": [{
            "item_id": "item-1",
            "source_segments": [{"segment_id": "segment-1", "text": "他心中仍有疑窦。\n"}],
            "units": [{"unit_id": "unit-1", "source_segment_ids": ["segment-1"], "target_term": "疑窦"}],
        }]
    })
    write_json(root, "contracts/core/v2.1/profile-resolver.v2.1.json", {
        "schemaVersion": "knowledge-clipper-profile-resolver.v2.1",
        "destinationConfigurations": {
            "anki": {
                "projection": {
                    "templateMappings": {
                        "language_writing": {
                            "policy": "project",
                            "noteTypeKey": "language_writing",
                            "frontField": "front",
                            "backField": "back",
                        }
                    }
                }
            }
        }
    })
    write_json(root, "workers/anki-exporter/config/anki-export.v1.example.json", {
        "note_types": {
            "language_writing": {
                "field_map": {"front": "Front", "back": "Back"}
            }
        }
    })


class LanguageWritingReadinessTests(unittest.TestCase):
    def test_ready_requires_ordered_independent_projection(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            build_repository(root)

            result = readiness.inspect(root, TEST_LANGUAGE_OUTPUT_RELATIVE)

            self.assertEqual("ready", result["status"])
            self.assertEqual([], result["missing"])
            self.assertEqual(1, result["observed"]["orderedBothGroups"])
            self.assertEqual(0, result["externalAnkiCalls"])

    def test_reversed_both_group_is_pending_not_silently_reordered(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            build_repository(root, ordered=False)

            result = readiness.inspect(root, TEST_LANGUAGE_OUTPUT_RELATIVE)

            self.assertEqual("pending_upstream_schema", result["status"])
            self.assertIn(
                "authoritative Content both Canary has no ordered language_context + language_writing variant group",
                result["missing"],
            )
            self.assertEqual(0, result["externalAnkiCalls"])

    def test_retained_target_term_is_pending(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            build_repository(root)
            card_path = root / TEST_LANGUAGE_OUTPUT_RELATIVE / "card-bundle.schema-v1.json"
            bundle = json.loads(readiness._io_path(card_path).read_text(encoding="utf-8"))
            bundle["cards"][1]["front_blocks"][0]["text"] = "他心中仍有疑窦（yí dòu；心里的疑问）。"
            readiness._io_path(card_path).write_text(json.dumps(bundle, ensure_ascii=False), encoding="utf-8")

            result = readiness.inspect(root, TEST_LANGUAGE_OUTPUT_RELATIVE)

            self.assertEqual("pending_upstream_schema", result["status"])
            self.assertIn("language_writing card writing front retains target_term", result["missing"])
            self.assertIn("language_writing card writing front is not the exact one-time replacement", result["missing"])
            self.assertEqual(0, result["externalAnkiCalls"])

    def test_duplicate_target_term_in_source_is_pending(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            build_repository(root)
            learning_path = root / TEST_LANGUAGE_OUTPUT_RELATIVE / "learning-bundle.schema-v1.json"
            bundle = json.loads(readiness._io_path(learning_path).read_text(encoding="utf-8"))
            bundle["items"][0]["source_segments"][0]["text"] = "疑窦之后仍有疑窦。\n"
            readiness._io_path(learning_path).write_text(json.dumps(bundle, ensure_ascii=False), encoding="utf-8")

            result = readiness.inspect(root, TEST_LANGUAGE_OUTPUT_RELATIVE)

            self.assertEqual("pending_upstream_schema", result["status"])
            self.assertIn(
                "language_writing card writing source sentence does not contain target_term exactly once",
                result["missing"],
            )
            self.assertEqual(0, result["externalAnkiCalls"])

    def test_multiple_container_line_endings_are_pending(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            build_repository(root)
            learning_path = root / TEST_LANGUAGE_OUTPUT_RELATIVE / "learning-bundle.schema-v1.json"
            bundle = json.loads(readiness._io_path(learning_path).read_text(encoding="utf-8"))
            bundle["items"][0]["source_segments"][0]["text"] = "他心中仍有疑窦。\n\n"
            readiness._io_path(learning_path).write_text(json.dumps(bundle, ensure_ascii=False), encoding="utf-8")

            result = readiness.inspect(root, TEST_LANGUAGE_OUTPUT_RELATIVE)

            self.assertEqual("pending_upstream_schema", result["status"])
            self.assertIn(
                "language_writing card writing source has multiple trailing container line endings",
                result["missing"],
            )
            self.assertEqual(0, result["externalAnkiCalls"])

    def test_choice_or_token_leak_is_pending(self):
        for leaked_field in ("choice", "decision_token"):
            with self.subTest(leaked_field=leaked_field), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                build_repository(root)
                card_path = root / TEST_LANGUAGE_OUTPUT_RELATIVE / "card-bundle.schema-v1.json"
                bundle = json.loads(readiness._io_path(card_path).read_text(encoding="utf-8"))
                bundle["cards"][1][leaked_field] = "must-stay-upstream"
                readiness._io_path(card_path).write_text(json.dumps(bundle, ensure_ascii=False), encoding="utf-8")

                result = readiness.inspect(root, TEST_LANGUAGE_OUTPUT_RELATIVE)

                self.assertEqual("pending_upstream_schema", result["status"])
                self.assertIn("authoritative Content CardBundle leaks choice/token decision state", result["missing"])
                self.assertEqual(0, result["externalAnkiCalls"])

    def test_missing_files_are_reported_without_side_effects(self):
        with tempfile.TemporaryDirectory() as temp:
            result = readiness.inspect(Path(temp), TEST_LANGUAGE_OUTPUT_RELATIVE)

            self.assertEqual("invalid_repository_state", result["status"])
            self.assertEqual(4, len(result["missing"]))
            self.assertEqual(0, result["externalAnkiCalls"])


if __name__ == "__main__":
    unittest.main()
