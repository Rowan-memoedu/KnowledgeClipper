"""Validate the content-to-exporter projection fixture without external APIs."""

from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator, FormatChecker


WORKER_ROOT = Path(__file__).resolve().parents[1]
FIXTURE_ROOT = WORKER_ROOT / "fixtures" / "projection-v1"
LEARNING_PATH = FIXTURE_ROOT / "learning-bundle.schema-v1.json"
CARD_PATH = FIXTURE_ROOT / "card-bundle.schema-v1.json"
LEARNING_SCHEMA_PATH = WORKER_ROOT / "schemas" / "learning-bundle.v1.schema.json"
CARD_SCHEMA_PATH = WORKER_ROOT / "schemas" / "card-bundle.v1.schema.json"
CATEGORY_REGISTRY_PATH = WORKER_ROOT / "config" / "categories.v1.json"

SHA256_RE = re.compile(r"^[a-f0-9]{64}$")
PRESENTATION_MARKERS_RE = re.compile(r"\*\*|__")


def load_json(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8") as handle:
        value = json.load(handle)
    require(isinstance(value, dict), f"{path.name}: root must be an object")
    return value


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def require_keys(value: dict[str, Any], required: set[str], allowed: set[str], where: str) -> None:
    missing = required - value.keys()
    extra = value.keys() - allowed
    require(not missing, f"{where}: missing keys {sorted(missing)}")
    require(not extra, f"{where}: unknown keys {sorted(extra)}")


def require_nonempty_string(value: Any, where: str) -> str:
    require(isinstance(value, str) and value != "", f"{where}: expected non-empty string")
    return value


def require_string_list(value: Any, where: str) -> list[str]:
    require(isinstance(value, list), f"{where}: expected array")
    for index, item in enumerate(value):
        require_nonempty_string(item, f"{where}[{index}]")
    return value


def require_sha(value: Any, where: str) -> str:
    require(isinstance(value, str) and SHA256_RE.fullmatch(value) is not None, f"{where}: invalid SHA-256")
    return value


def resolve_fixture_path(relative: str, where: str) -> Path:
    require_nonempty_string(relative, where)
    candidate = (FIXTURE_ROOT / relative).resolve()
    require(candidate.is_relative_to(FIXTURE_ROOT.resolve()), f"{where}: path escapes fixture root")
    return candidate


def validate_existing_schema(instance: dict[str, Any], schema_path: Path) -> str:
    schema = load_json(schema_path)
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema, format_checker=FormatChecker())
    errors = sorted(validator.iter_errors(instance), key=lambda error: list(error.absolute_path))
    require(not errors, f"{schema_path.name}: {errors[0].message if errors else ''}")
    return require_nonempty_string(schema.get("$id"), f"{schema_path.name}.$id")


def validate_gate(gate: Any, where: str) -> None:
    require(isinstance(gate, dict), f"{where}: expected object")
    require_keys(gate, {"gate_id", "status", "evidence"}, {"gate_id", "status", "evidence"}, where)
    require_nonempty_string(gate["gate_id"], f"{where}.gate_id")
    require(gate["status"] in {"pass", "fail", "notice"}, f"{where}.status: unknown value")
    require_nonempty_string(gate["evidence"], f"{where}.evidence")


def validate_gates(value: Any, where: str) -> None:
    require(isinstance(value, list), f"{where}: expected array")
    for index, gate in enumerate(value):
        validate_gate(gate, f"{where}[{index}]")


def validate_category_registry(registry: dict[str, Any]) -> dict[str, set[str]]:
    require_keys(registry, {"schema_version", "categories"}, {"schema_version", "categories"}, "CategoryRegistry")
    require(registry["schema_version"] == "category-registry/1.0.0", "CategoryRegistry.schema_version mismatch")
    require(isinstance(registry["categories"], list) and registry["categories"], "CategoryRegistry.categories: expected non-empty array")
    allowed_by_category: dict[str, set[str]] = {}
    for index, category in enumerate(registry["categories"]):
        where = f"CategoryRegistry.categories[{index}]"
        require(isinstance(category, dict), f"{where}: expected object")
        required = {"id", "description", "prompt_file", "allowed_templates"}
        require_keys(category, required, required, where)
        category_id = require_nonempty_string(category["id"], f"{where}.id")
        require(category_id not in allowed_by_category, f"{where}.id: duplicate {category_id}")
        require_nonempty_string(category["description"], f"{where}.description")
        require_nonempty_string(category["prompt_file"], f"{where}.prompt_file")
        templates = set(require_string_list(category["allowed_templates"], f"{where}.allowed_templates"))
        require(templates, f"{where}.allowed_templates: expected non-empty array")
        allowed_by_category[category_id] = templates
    return allowed_by_category


def validate_learning_bundle(
    learning: dict[str, Any],
    allowed_by_category: dict[str, set[str]],
) -> tuple[dict[str, Any], dict[tuple[str, str], dict[str, Any]]]:
    require_keys(
        learning,
        {
            "schema_version", "bundle_id", "package_id", "task_id", "input_version", "attempt_id",
            "created_at", "input", "worker", "items", "ai_trace", "release_state", "unresolved_items", "gates",
        },
        {
            "schema_version", "bundle_id", "package_id", "task_id", "input_version", "attempt_id",
            "created_at", "input", "worker", "items", "ai_trace", "release_state", "unresolved_items", "gates",
        },
        "LearningBundle",
    )
    require(learning["schema_version"] == "learning-bundle/1.0.0", "LearningBundle.schema_version mismatch")
    for key in ("bundle_id", "package_id", "task_id", "input_version", "attempt_id", "created_at"):
        require_nonempty_string(learning[key], f"LearningBundle.{key}")

    input_ref = learning["input"]
    require(isinstance(input_ref, dict), "LearningBundle.input: expected object")
    require_keys(
        input_ref,
        {"manifest_path", "manifest_sha256", "standard_input_path", "standard_input_sha256"},
        {"manifest_path", "manifest_sha256", "standard_input_path", "standard_input_sha256"},
        "LearningBundle.input",
    )
    require_nonempty_string(input_ref["manifest_path"], "LearningBundle.input.manifest_path")
    require_sha(input_ref["manifest_sha256"], "LearningBundle.input.manifest_sha256")
    require_nonempty_string(input_ref["standard_input_path"], "LearningBundle.input.standard_input_path")
    require_sha(input_ref["standard_input_sha256"], "LearningBundle.input.standard_input_sha256")

    worker = learning["worker"]
    require(isinstance(worker, dict), "LearningBundle.worker: expected object")
    require_keys(
        worker,
        {"name", "version", "category_registry_sha256"},
        {"name", "version", "category_registry_sha256"},
        "LearningBundle.worker",
    )
    require(worker["name"] == "content-understanding-card-worker", "LearningBundle.worker.name mismatch")
    require_nonempty_string(worker["version"], "LearningBundle.worker.version")
    require_sha(worker["category_registry_sha256"], "LearningBundle.worker.category_registry_sha256")

    require(isinstance(learning["items"], list) and learning["items"], "LearningBundle.items: expected non-empty array")
    item_by_id: dict[str, dict[str, Any]] = {}
    unit_by_key: dict[tuple[str, str], dict[str, Any]] = {}
    all_segment_ids: set[str] = set()
    all_unit_ids: set[str] = set()

    for item_index, item in enumerate(learning["items"]):
        where = f"LearningBundle.items[{item_index}]"
        require(isinstance(item, dict), f"{where}: expected object")
        required = {
            "item_id", "source_kind", "source_refs", "source_segments", "cleaning", "units",
            "state", "unresolved_reasons", "gates",
        }
        require_keys(item, required, required, where)
        item_id = require_nonempty_string(item["item_id"], f"{where}.item_id")
        require(item_id not in item_by_id, f"{where}.item_id: duplicate {item_id}")
        item_by_id[item_id] = item
        require(item["source_kind"] in {"text", "conversation", "media_set"}, f"{where}.source_kind: unknown value")

        require(isinstance(item["source_refs"], list) and item["source_refs"], f"{where}.source_refs: expected non-empty array")
        source_ref_by_path: dict[str, dict[str, Any]] = {}
        for ref_index, source_ref in enumerate(item["source_refs"]):
            ref_where = f"{where}.source_refs[{ref_index}]"
            require(isinstance(source_ref, dict), f"{ref_where}: expected object")
            require_keys(source_ref, {"path", "sha256"}, {"path", "sha256", "role"}, ref_where)
            relative = require_nonempty_string(source_ref["path"], f"{ref_where}.path")
            require(relative not in source_ref_by_path, f"{ref_where}.path: duplicate {relative}")
            source_ref_by_path[relative] = source_ref
            expected_hash = require_sha(source_ref["sha256"], f"{ref_where}.sha256")
            if "role" in source_ref:
                require(source_ref["role"] in {"question", "answer", "context", "unknown"}, f"{ref_where}.role: unknown value")
            source_path = resolve_fixture_path(relative, f"{ref_where}.path")
            require(source_path.is_file(), f"{ref_where}.path: missing file {relative}")
            require(sha256(source_path) == expected_hash, f"{ref_where}.sha256: content mismatch")

        require(isinstance(item["source_segments"], list), f"{where}.source_segments: expected array")
        segment_by_id: dict[str, dict[str, Any]] = {}
        for segment_index, segment in enumerate(item["source_segments"]):
            segment_where = f"{where}.source_segments[{segment_index}]"
            require(isinstance(segment, dict), f"{segment_where}: expected object")
            required_segment = {"segment_index", "segment_id", "source_kind", "source_path", "start", "end", "text"}
            require_keys(segment, required_segment, required_segment | {"turn_id", "role"}, segment_where)
            require(isinstance(segment["segment_index"], int) and segment["segment_index"] >= 0, f"{segment_where}.segment_index")
            segment_id = require_nonempty_string(segment["segment_id"], f"{segment_where}.segment_id")
            require(segment_id not in all_segment_ids, f"{segment_where}.segment_id: duplicate {segment_id}")
            all_segment_ids.add(segment_id)
            segment_by_id[segment_id] = segment
            require(segment["source_kind"] in {"text", "conversation_turn"}, f"{segment_where}.source_kind: unknown value")
            relative = require_nonempty_string(segment["source_path"], f"{segment_where}.source_path")
            require(relative in source_ref_by_path, f"{segment_where}.source_path: missing source_ref")
            require(isinstance(segment["start"], int) and isinstance(segment["end"], int), f"{segment_where}: offsets must be integers")
            require(0 <= segment["start"] <= segment["end"], f"{segment_where}: invalid offsets")
            text_value = require_nonempty_string(segment["text"], f"{segment_where}.text")
            source_text = resolve_fixture_path(relative, f"{segment_where}.source_path").read_text(encoding="utf-8")
            require(source_text[segment["start"]:segment["end"]] == text_value, f"{segment_where}: span/text mismatch")
            if "turn_id" in segment:
                require_nonempty_string(segment["turn_id"], f"{segment_where}.turn_id")
            if "role" in segment:
                require(segment["role"] in {"user", "assistant"}, f"{segment_where}.role: unknown value")

        cleaning = item["cleaning"]
        require(isinstance(cleaning, dict), f"{where}.cleaning: expected object")
        require_keys(cleaning, {"state", "removals", "format_anomalies"}, {"state", "cleaned_text", "removals", "format_anomalies"}, f"{where}.cleaning")
        require(cleaning["state"] in {"ready", "not_applicable", "failed_content_gate", "unresolved", "failed_ai"}, f"{where}.cleaning.state")
        if "cleaned_text" in cleaning:
            require_nonempty_string(cleaning["cleaned_text"], f"{where}.cleaning.cleaned_text")
        require(isinstance(cleaning["removals"], list), f"{where}.cleaning.removals: expected array")
        require(isinstance(cleaning["format_anomalies"], list), f"{where}.cleaning.format_anomalies: expected array")

        require(isinstance(item["units"], list) and item["units"], f"{where}.units: expected non-empty array")
        for unit_index, unit in enumerate(item["units"]):
            unit_where = f"{where}.units[{unit_index}]"
            require(isinstance(unit, dict), f"{unit_where}: expected object")
            required_unit = {
                "unit_id", "source_segment_ids", "category_id", "subtype", "answer_state", "context_state",
                "risk_state", "generated_content", "state", "unresolved_reasons", "gates",
            }
            require_keys(unit, required_unit, required_unit | {"question", "supplied_answer", "target_term"}, unit_where)
            unit_id = require_nonempty_string(unit["unit_id"], f"{unit_where}.unit_id")
            require(unit_id not in all_unit_ids, f"{unit_where}.unit_id: duplicate {unit_id}")
            all_unit_ids.add(unit_id)
            unit_by_key[(item_id, unit_id)] = unit
            for segment_id in require_string_list(unit["source_segment_ids"], f"{unit_where}.source_segment_ids"):
                require(segment_id in segment_by_id, f"{unit_where}.source_segment_ids: missing segment {segment_id}")
            category_id = require_nonempty_string(unit["category_id"], f"{unit_where}.category_id")
            require(category_id in allowed_by_category, f"{unit_where}.category_id: not registered")
            require_nonempty_string(unit["subtype"], f"{unit_where}.subtype")
            require(unit["answer_state"] in {"provided", "missing", "ambiguous", "not_applicable"}, f"{unit_where}.answer_state")
            require(unit["context_state"] in {"provided", "missing", "ambiguous", "not_applicable"}, f"{unit_where}.context_state")
            require(unit["risk_state"] in {"ordinary", "verification_required", "high_stakes"}, f"{unit_where}.risk_state")
            for optional in ("question", "supplied_answer", "target_term"):
                if optional in unit:
                    require_nonempty_string(unit[optional], f"{unit_where}.{optional}")
            require(isinstance(unit["generated_content"], list), f"{unit_where}.generated_content: expected array")
            for generated_index, generated in enumerate(unit["generated_content"]):
                generated_where = f"{unit_where}.generated_content[{generated_index}]"
                require(isinstance(generated, dict), f"{generated_where}: expected object")
                require_keys(generated, {"kind", "text"}, {"kind", "text"}, generated_where)
                require_nonempty_string(generated["kind"], f"{generated_where}.kind")
                require_nonempty_string(generated["text"], f"{generated_where}.text")
            require(unit["state"] in {"ready", "unresolved", "failed_content_gate", "failed_ai"}, f"{unit_where}.state")
            require_string_list(unit["unresolved_reasons"], f"{unit_where}.unresolved_reasons")
            validate_gates(unit["gates"], f"{unit_where}.gates")

        require(item["state"] in {"ready", "partial", "unresolved", "failed_content_gate", "failed_ai"}, f"{where}.state")
        require_string_list(item["unresolved_reasons"], f"{where}.unresolved_reasons")
        validate_gates(item["gates"], f"{where}.gates")

    require(isinstance(learning["ai_trace"], list), "LearningBundle.ai_trace: expected array")
    require(learning["release_state"] in {"ready", "partial", "unresolved", "failed"}, "LearningBundle.release_state")
    require(isinstance(learning["unresolved_items"], list), "LearningBundle.unresolved_items: expected array")
    validate_gates(learning["gates"], "LearningBundle.gates")
    return item_by_id, unit_by_key


def validate_card_bundle(
    cards_bundle: dict[str, Any],
    learning: dict[str, Any],
    item_by_id: dict[str, dict[str, Any]],
    unit_by_key: dict[tuple[str, str], dict[str, Any]],
    allowed_by_category: dict[str, set[str]],
) -> tuple[set[str], set[str], set[str]]:
    required = {
        "schema_version", "bundle_id", "package_id", "task_id", "attempt_id", "created_at",
        "learning_bundle_path", "learning_bundle_sha256", "cards", "withheld_units", "release_state", "gates",
    }
    require_keys(cards_bundle, required, required, "CardBundle")
    require(cards_bundle["schema_version"] == "card-bundle/1.0.0", "CardBundle.schema_version mismatch")
    for key in ("bundle_id", "package_id", "task_id", "attempt_id", "created_at"):
        require_nonempty_string(cards_bundle[key], f"CardBundle.{key}")
    for key in ("package_id", "task_id", "attempt_id"):
        require(cards_bundle[key] == learning[key], f"CardBundle.{key}: LearningBundle lineage mismatch")

    learning_relative = require_nonempty_string(cards_bundle["learning_bundle_path"], "CardBundle.learning_bundle_path")
    linked_learning = resolve_fixture_path(learning_relative, "CardBundle.learning_bundle_path")
    require(linked_learning == LEARNING_PATH.resolve(), "CardBundle.learning_bundle_path: unexpected target")
    require(linked_learning.is_file(), "CardBundle.learning_bundle_path: target missing")
    require_sha(cards_bundle["learning_bundle_sha256"], "CardBundle.learning_bundle_sha256")
    require(sha256(linked_learning) == cards_bundle["learning_bundle_sha256"], "CardBundle.learning_bundle_sha256: content mismatch")

    require(isinstance(cards_bundle["cards"], list) and cards_bundle["cards"], "CardBundle.cards: expected non-empty array")
    card_ids: set[str] = set()
    block_ids: set[str] = set()
    templates: set[str] = set()
    block_types: set[str] = set()
    categories: set[str] = set()

    for card_index, card in enumerate(cards_bundle["cards"]):
        where = f"CardBundle.cards[{card_index}]"
        require(isinstance(card, dict), f"{where}: expected object")
        required_card = {
            "card_id", "item_id", "unit_id", "variant_group_id", "category_id", "subtype", "template",
            "front_blocks", "back_blocks", "answer_provenance", "source_segment_ids",
        }
        require_keys(card, required_card, required_card, where)
        card_id = require_nonempty_string(card["card_id"], f"{where}.card_id")
        require(card_id not in card_ids, f"{where}.card_id: duplicate {card_id}")
        card_ids.add(card_id)
        item_id = require_nonempty_string(card["item_id"], f"{where}.item_id")
        unit_id = require_nonempty_string(card["unit_id"], f"{where}.unit_id")
        require(item_id in item_by_id, f"{where}.item_id: missing LearningItem")
        require((item_id, unit_id) in unit_by_key, f"{where}.unit_id: missing LearningUnit")
        item = item_by_id[item_id]
        unit = unit_by_key[(item_id, unit_id)]
        require_nonempty_string(card["variant_group_id"], f"{where}.variant_group_id")
        category = require_nonempty_string(card["category_id"], f"{where}.category_id")
        require(category == unit["category_id"], f"{where}.category_id: LearningUnit mismatch")
        categories.add(category)
        subtype = require_nonempty_string(card["subtype"], f"{where}.subtype")
        require(subtype == unit["subtype"], f"{where}.subtype: LearningUnit mismatch")
        template = require_nonempty_string(card["template"], f"{where}.template")
        require(template in allowed_by_category[category], f"{where}.template: not allowed for category {category}")
        templates.add(template)
        require(card["answer_provenance"] in {"source", "generated", "human_confirmed", "not_applicable"}, f"{where}.answer_provenance")

        source_segment_ids = require_string_list(card["source_segment_ids"], f"{where}.source_segment_ids")
        item_segments = {segment["segment_id"]: segment for segment in item["source_segments"]}
        for segment_id in source_segment_ids:
            require(segment_id in item_segments, f"{where}.source_segment_ids: missing segment {segment_id}")
        source_text = "".join(item_segments[segment_id]["text"] for segment_id in source_segment_ids)
        generated_texts = {entry["text"] for entry in unit["generated_content"]}
        source_paths = {source_ref["path"] for source_ref in item["source_refs"]}

        for side in ("front_blocks", "back_blocks"):
            blocks = card[side]
            require(isinstance(blocks, list) and blocks, f"{where}.{side}: expected non-empty array")
            for block_index, block in enumerate(blocks):
                block_where = f"{where}.{side}[{block_index}]"
                require(isinstance(block, dict), f"{block_where}: expected object")
                require_keys(block, {"block_id", "type", "provenance"}, {"block_id", "type", "text", "material_path", "provenance"}, block_where)
                block_id = require_nonempty_string(block["block_id"], f"{block_where}.block_id")
                require(block_id not in block_ids, f"{block_where}.block_id: duplicate {block_id}")
                block_ids.add(block_id)
                block_type = block["type"]
                require(block_type in {"markdown", "text", "asset_ref"}, f"{block_where}.type: unknown value")
                block_types.add(block_type)
                provenance = block["provenance"]
                require(provenance in {"source", "generated", "human_confirmed"}, f"{block_where}.provenance: unknown value")

                if block_type in {"markdown", "text"}:
                    text_value = require_nonempty_string(block.get("text"), f"{block_where}.text")
                    require("material_path" not in block, f"{block_where}: text block cannot carry material_path")
                    if provenance == "source":
                        trace_text = PRESENTATION_MARKERS_RE.sub("", text_value)
                        require(trace_text in source_text, f"{block_where}: source text is not traceable")
                    elif provenance == "generated":
                        require(text_value in generated_texts, f"{block_where}: generated text is not exact LearningUnit content")
                else:
                    material_path = require_nonempty_string(block.get("material_path"), f"{block_where}.material_path")
                    require("text" not in block, f"{block_where}: asset_ref cannot carry text")
                    require(provenance == "source", f"{block_where}: fixture asset_ref must have source provenance")
                    require(material_path in source_paths, f"{block_where}.material_path: missing LearningItem source_ref")
                    require(resolve_fixture_path(material_path, f"{block_where}.material_path").is_file(), f"{block_where}.material_path: file missing")

        require(unit["state"] == "ready", f"{where}: referenced unit is not ready")

    require(isinstance(cards_bundle["withheld_units"], list), "CardBundle.withheld_units: expected array")
    require(cards_bundle["release_state"] in {"ready", "partial"}, "CardBundle.release_state")
    validate_gates(cards_bundle["gates"], "CardBundle.gates")
    return categories, templates, block_types


def validate_fixture_coverage(
    learning: dict[str, Any],
    cards_bundle: dict[str, Any],
    categories: set[str],
    templates: set[str],
    block_types: set[str],
    allowed_by_category: dict[str, set[str]],
) -> None:
    require(categories == set(allowed_by_category), "coverage: category registry is not fully represented")
    expected_templates = set().union(*allowed_by_category.values())
    require(templates == expected_templates, "coverage: allowed category templates are not fully represented")
    require(block_types == {"markdown", "text", "asset_ref"}, "coverage: block type set mismatch")
    require(learning["ai_trace"] == [], "coverage: ai_trace must demonstrate an empty array")
    require(learning["unresolved_items"] == [], "coverage: unresolved_items must demonstrate an empty array")
    require(cards_bundle["withheld_units"] == [], "coverage: withheld_units must demonstrate an empty array")

    items = {item["item_id"]: item for item in learning["items"]}
    zh_unit = items["item-zh"]["units"][0]
    en_unit = items["item-en"]["units"][0]
    qa_unit = items["item-qa"]["units"][0]
    media_item = items["item-media"]
    media_unit = media_item["units"][0]
    for unit, where in ((zh_unit, "unit-zh"), (en_unit, "unit-en")):
        require("question" not in unit and "supplied_answer" not in unit, f"coverage: {where} optional fields must be omitted")
    require("supplied_answer" not in qa_unit and "target_term" not in qa_unit, "coverage: unit-qa optional fields must be omitted")
    require("cleaned_text" not in media_item["cleaning"], "coverage: media cleaned_text must be omitted")
    require(all(field not in media_unit for field in ("question", "supplied_answer", "target_term")), "coverage: media optional unit fields must be omitted")
    require(media_item["source_segments"] == [] and media_unit["source_segment_ids"] == [], "coverage: media empty segment arrays missing")
    media_card = next(card for card in cards_bundle["cards"] if card["card_id"] == "card-media-pair")
    require(media_card["source_segment_ids"] == [], "coverage: media card empty source_segment_ids missing")
    require("role" not in items["item-zh"]["source_refs"][0], "coverage: optional source role omission missing")
    require({ref["role"] for ref in media_item["source_refs"]} == {"question", "answer"}, "coverage: media roles missing")
    general_cards = [card for card in cards_bundle["cards"] if card["category_id"] == "general_learning"]
    require(len(general_cards) >= 2, "coverage: general_learning requires at least two representative cards")
    templates_allowed_elsewhere = set().union(
        *(templates for category, templates in allowed_by_category.items() if category != "general_learning")
    )
    general_exclusive_templates = allowed_by_category["general_learning"] - templates_allowed_elsewhere
    require(
        general_exclusive_templates <= {card["template"] for card in general_cards},
        "coverage: templates unique to general_learning are not fully represented",
    )


def main() -> None:
    learning = load_json(LEARNING_PATH)
    cards_bundle = load_json(CARD_PATH)
    registry = load_json(CATEGORY_REGISTRY_PATH)
    allowed_by_category = validate_category_registry(registry)
    learning_schema_id = validate_existing_schema(learning, LEARNING_SCHEMA_PATH)
    card_schema_id = validate_existing_schema(cards_bundle, CARD_SCHEMA_PATH)
    item_by_id, unit_by_key = validate_learning_bundle(learning, allowed_by_category)
    categories, templates, block_types = validate_card_bundle(cards_bundle, learning, item_by_id, unit_by_key, allowed_by_category)
    validate_fixture_coverage(learning, cards_bundle, categories, templates, block_types, allowed_by_category)

    result = {
        "status": "PASS",
        "external_api_calls": 0,
        "schemas": [learning_schema_id, card_schema_id],
        "category_registry": {
            "schema": registry["schema_version"],
            "path": str(CATEGORY_REGISTRY_PATH),
            "sha256": sha256(CATEGORY_REGISTRY_PATH),
        },
        "fixtures": {
            "learning_bundle": {
                "path": str(LEARNING_PATH),
                "sha256": sha256(LEARNING_PATH),
                "items": len(learning["items"]),
                "units": sum(len(item["units"]) for item in learning["items"]),
            },
            "card_bundle": {
                "path": str(CARD_PATH),
                "sha256": sha256(CARD_PATH),
                "cards": len(cards_bundle["cards"]),
            },
        },
        "coverage": {
            "categories": sorted(categories),
            "templates": sorted(templates),
            "block_types": sorted(block_types),
            "optional_fields": "omitted-and-empty-array-cases-covered",
        },
        "checks": [
            "existing-json-schemas",
            "strict-nested-types",
            "bundle-lineage-and-sha256",
            "source-file-sha256-and-spans",
            "cross-bundle-references",
            "block-provenance",
            "fixture-coverage",
        ],
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
