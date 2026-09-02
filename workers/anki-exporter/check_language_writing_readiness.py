#!/usr/bin/env python3
"""Read-only preflight for the upstream language_writing contract.

This probe never runs the Core adapter or the Anki worker.  It only reports
whether the currently checked-in Content fixture, Core projection mapping and
Anki configuration contain the prerequisites for the prepared validate-only
consumer acceptance.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
from pathlib import Path
from typing import Any


CORRECTED_LANGUAGE_CANARY_OUTPUT_RELATIVE = Path(
    "content-worker/canary/jobs/20260826T041418150Z-offline-language-practice-v2-replay-e7deb47e/"
    "runs/run-200a433f248b2eab5eba/stages/content.understand-and-card/"
    "attempts/attempt-200a433f248b2eab5eba/outputs"
)


def _io_path(path: Path) -> Path:
    if os.name == "nt" and path.is_absolute() and not str(path).startswith("\\\\?\\"):
        return Path("\\\\?\\" + str(path))
    return path


def _load_json(path: Path) -> Any:
    with _io_path(path).open("r", encoding="utf-8") as stream:
        return json.load(stream)


def _sha256(path: Path) -> str:
    return hashlib.sha256(_io_path(path).read_bytes()).hexdigest()


def _contains_decision_leak(value: Any) -> bool:
    if isinstance(value, dict):
        return any(
            key.casefold() == "choice"
            or "token" in key.casefold()
            or _contains_decision_leak(child)
            for key, child in value.items()
        )
    if isinstance(value, list):
        return any(_contains_decision_leak(child) for child in value)
    return False


def _source_sentence_without_container_line_ending(value: str) -> str | None:
    if value.endswith("\r\n"):
        sentence = value[:-2]
    elif value.endswith(("\r", "\n")):
        sentence = value[:-1]
    else:
        sentence = value
    if sentence.endswith(("\r", "\n")):
        return None
    return sentence


def _validate_language_writing_semantics(card_bundle: dict[str, Any], learning_bundle: dict[str, Any]) -> list[str]:
    failures: list[str] = []
    if _contains_decision_leak(card_bundle):
        failures.append("authoritative Content CardBundle leaks choice/token decision state")
    items = {
        item.get("item_id"): item
        for item in learning_bundle.get("items", [])
        if isinstance(item, dict)
    }
    for card in card_bundle.get("cards", []):
        if not isinstance(card, dict) or card.get("template") != "language_writing":
            continue
        card_id = card.get("card_id", "<unknown>")
        if "choice" in card:
            failures.append(f"language_writing card {card_id} leaks choice into finalized CardBundle")
        item = items.get(card.get("item_id"))
        if not isinstance(item, dict):
            failures.append(f"language_writing card {card_id} has no matching LearningBundle item")
            continue
        units = {
            unit.get("unit_id"): unit
            for unit in item.get("units", [])
            if isinstance(unit, dict)
        }
        unit = units.get(card.get("unit_id"))
        if not isinstance(unit, dict):
            failures.append(f"language_writing card {card_id} has no matching LearningBundle unit")
            continue
        target_term = unit.get("target_term")
        if not isinstance(target_term, str) or not target_term:
            failures.append(f"language_writing card {card_id} has no non-empty target_term")
            continue
        front_blocks = card.get("front_blocks", [])
        back_blocks = card.get("back_blocks", [])
        if not (
            isinstance(front_blocks, list)
            and len(front_blocks) == 1
            and isinstance(front_blocks[0], dict)
            and isinstance(front_blocks[0].get("text"), str)
            and isinstance(back_blocks, list)
            and len(back_blocks) == 1
            and isinstance(back_blocks[0], dict)
            and isinstance(back_blocks[0].get("text"), str)
        ):
            failures.append(f"language_writing card {card_id} must have one text front and one text back")
            continue
        front = front_blocks[0]["text"]
        back = back_blocks[0]["text"]
        if back != target_term:
            failures.append(f"language_writing card {card_id} back does not preserve target_term verbatim")
        segment_ids = set(unit.get("source_segment_ids", []))
        source_container_text = "".join(
            segment.get("text", "")
            for segment in item.get("source_segments", [])
            if isinstance(segment, dict) and segment.get("segment_id") in segment_ids
        )
        original = _source_sentence_without_container_line_ending(source_container_text)
        if original is None:
            failures.append(f"language_writing card {card_id} source has multiple trailing container line endings")
            continue
        if original.count(target_term) != 1:
            failures.append(f"language_writing card {card_id} source sentence does not contain target_term exactly once")
            continue
        if target_term in front:
            failures.append(f"language_writing card {card_id} front retains target_term")
        annotations = re.findall(r"（[^（）；]+；[^（）]+）", front)
        if len(annotations) != 1:
            failures.append(f"language_writing card {card_id} lacks exactly one full-width reading；meaning annotation")
            continue
        expected_front = original.replace(target_term, annotations[0], 1)
        if front != expected_front:
            failures.append(f"language_writing card {card_id} front is not the exact one-time replacement")
    return failures


def inspect(
    project_root: Path,
    language_output_relative: Path | None = CORRECTED_LANGUAGE_CANARY_OUTPUT_RELATIVE,
) -> dict[str, Any]:
    if language_output_relative is None:
        profile_path = project_root / "contracts" / "core" / "v2.1" / "profile-resolver.v2.1.json"
        config_path = project_root / "workers" / "anki-exporter" / "config" / "anki-export.v1.example.json"
        missing_files = [path for path in (profile_path, config_path) if not _io_path(path).is_file()]
        if missing_files:
            return {
                "status": "invalid_repository_state",
                "acceptancePrepared": True,
                "externalAnkiCalls": 0,
                "missing": [f"missing required file: {path.relative_to(project_root).as_posix()}" for path in missing_files],
                "inputs": {},
            }
        profile = _load_json(profile_path)
        mapping = (
            profile.get("destinationConfigurations", {})
            .get("anki", {})
            .get("projection", {})
            .get("templateMappings", {})
            .get("language_writing")
        )
        missing = ["corrected authoritative Content language both output has not been selected"]
        if not isinstance(mapping, dict):
            missing.append("Core ankiProjection.templateMappings has no language_writing entry")
        return {
            "status": "pending_upstream_schema",
            "acceptancePrepared": True,
            "choiceOwnedByExporter": False,
            "inputOrderRequired": True,
            "externalAnkiCalls": 0,
            "missing": missing,
            "observed": {
                "languageWritingCards": 0,
                "orderedBothGroups": 0,
                "selectedNoteTypeKey": mapping.get("noteTypeKey") if isinstance(mapping, dict) else None,
            },
            "inputs": {
                "coreProfile": {"path": profile_path.relative_to(project_root).as_posix(), "sha256": _sha256(profile_path)},
                "ankiConfig": {"path": config_path.relative_to(project_root).as_posix(), "sha256": _sha256(config_path)},
            },
        }
    language_output = project_root / language_output_relative
    paths = {
        "cardBundle": language_output / "card-bundle.schema-v1.json",
        "learningBundle": language_output / "learning-bundle.schema-v1.json",
        "coreProfile": project_root / "contracts" / "core" / "v2.1" / "profile-resolver.v2.1.json",
        "ankiConfig": project_root / "workers" / "anki-exporter" / "config" / "anki-export.v1.example.json",
    }
    missing_files = [name for name, path in paths.items() if not _io_path(path).is_file()]
    if missing_files:
        return {
            "status": "invalid_repository_state",
            "acceptancePrepared": True,
            "externalAnkiCalls": 0,
            "missing": [f"missing required file: {name}" for name in missing_files],
            "inputs": {},
        }

    card_bundle = _load_json(paths["cardBundle"])
    learning_bundle = _load_json(paths["learningBundle"])
    core_profile = _load_json(paths["coreProfile"])
    anki_config = _load_json(paths["ankiConfig"])

    missing: list[str] = []
    cards = card_bundle.get("cards", [])
    writing_cards = [card for card in cards if card.get("template") == "language_writing"]
    if not writing_cards:
        missing.append("authoritative Content both Canary CardBundle has no language_writing card")
    else:
        missing.extend(_validate_language_writing_semantics(card_bundle, learning_bundle))

    groups: dict[str, list[dict[str, Any]]] = {}
    for card in cards:
        group_id = card.get("variant_group_id")
        if isinstance(group_id, str) and group_id:
            groups.setdefault(group_id, []).append(card)
    both_groups = [
        group
        for group in groups.values()
        if [card.get("template") for card in group] == ["language_context", "language_writing"]
    ]
    if not both_groups:
        missing.append(
            "authoritative Content both Canary has no ordered language_context + language_writing variant group"
        )

    if core_profile.get("schemaVersion") == "knowledge-clipper-profile-resolver.v2.1":
        anki_projection = core_profile.get("destinationConfigurations", {}).get("anki", {}).get("projection", {})
    else:
        anki_projection = core_profile.get("ankiProjection", {})
    template_mappings = anki_projection.get("templateMappings", {})
    writing_mapping = template_mappings.get("language_writing")
    if not isinstance(writing_mapping, dict):
        missing.append("Core ankiProjection.templateMappings has no language_writing entry")
        writing_mapping = {}
    elif not (
        writing_mapping.get("policy") == "project"
        and isinstance(writing_mapping.get("noteTypeKey"), str)
        and writing_mapping.get("noteTypeKey")
        and writing_mapping.get("frontField") == "front"
        and writing_mapping.get("backField") == "back"
    ):
        missing.append("Core language_writing mapping is not an independent ordinary forward projection")

    note_type_key = writing_mapping.get("noteTypeKey")
    note_types = anki_config.get("note_types", {})
    note_type = note_types.get(note_type_key) if isinstance(note_type_key, str) else None
    if isinstance(note_type_key, str) and not isinstance(note_type, dict):
        missing.append("Anki config has no note type selected by Core language_writing mapping")
    elif isinstance(note_type, dict):
        field_map = note_type.get("field_map", {})
        if field_map.get("front") != "Front" or field_map.get("back") != "Back":
            missing.append("Anki language_writing note type does not map front/back to Front/Back")

    learning_items = learning_bundle.get("items", [])
    learning_units = [
        unit
        for item in learning_items
        if isinstance(item, dict)
        for unit in item.get("units", [])
        if isinstance(unit, dict)
    ]
    if not any(isinstance(unit.get("target_term"), str) and unit.get("target_term") for unit in learning_units):
        missing.append("Content projection-v1 LearningBundle has no non-empty target_term")

    return {
        "status": "ready" if not missing else "pending_upstream_schema",
        "acceptancePrepared": True,
        "choiceOwnedByExporter": False,
        "inputOrderRequired": True,
        "externalAnkiCalls": 0,
        "missing": missing,
        "observed": {
            "languageWritingCards": len(writing_cards),
            "orderedBothGroups": len(both_groups),
            "selectedNoteTypeKey": note_type_key,
        },
        "inputs": {
            name: {
                "path": path.relative_to(project_root).as_posix(),
                "sha256": _sha256(path),
            }
            for name, path in paths.items()
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--project-root",
        type=Path,
        default=Path(__file__).resolve().parents[2],
        help="RemNote Quick Clipper project root",
    )
    parser.add_argument(
        "--language-output",
        type=Path,
        default=CORRECTED_LANGUAGE_CANARY_OUTPUT_RELATIVE,
        help="Project-relative outputs directory of the corrected authoritative Content language both Canary",
    )
    args = parser.parse_args()
    if args.language_output is not None and (args.language_output.is_absolute() or ".." in args.language_output.parts):
        parser.error("--language-output must be a project-relative path without '..'")
    result = inspect(args.project_root.resolve(), args.language_output)
    print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
