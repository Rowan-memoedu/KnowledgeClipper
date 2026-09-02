#!/usr/bin/env python3
"""Module-local project settings adapter for the optional Anki destination."""

from __future__ import annotations

import argparse
import copy
import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import anki_exporter as exporter


ADAPTER_VERSION = "1.2.0"
SETTINGS_SCHEMA_VERSION = "knowledge-clipper.anki-destination-settings.v1"
ADAPTATION_SCHEMA_VERSION = "knowledge-clipper.anki-settings-adaptation.v1"
DEFAULT_DECK_ROUTES = {
    "question": "零碎知识剪藏::题目",
    "language.zh": "零碎知识剪藏::语言::中文",
    "language.en": "零碎知识剪藏::语言::英语",
    "math": "零碎知识剪藏::数学",
    "life": "零碎知识剪藏::生活",
    "other": "零碎知识剪藏::其他",
}
DEFAULT_NOTE_TYPES = {
    "basic_qa": {
        "model_name": "RQC Basic v1",
        "field_map": {"question": "Front", "answer": "Back", "context": "Context", "extra": "Extra", "source": "Source"},
        "required_fields": ["question", "answer"],
    },
    "language_item": {
        "model_name": "RQC Basic v1",
        "field_map": {"context_sentence": "Front", "reading_and_meaning": "Back", "selected_term": "Context", "extra": "Extra", "source": "Source"},
        "required_fields": ["context_sentence", "reading_and_meaning"],
    },
    "language_writing": {
        "model_name": "RQC Basic v1",
        "field_map": {"front": "Front", "back": "Back"},
        "required_fields": ["front", "back"],
    },
    "image_qa": {
        "model_name": "RQC Basic v1",
        "field_map": {"question": "Front", "answer": "Back", "context": "Context", "source": "Source"},
        "required_fields": ["question", "answer"],
    },
    "original_image_qa": {
        "model_name": "KnowledgeClipper Original Media v1",
        "field_map": {"question": "Front", "answer": "Back", "source": "Source"},
        "required_fields": ["question"],
        "media_projection": exporter.ORIGINAL_MEDIA_PROJECTION,
        "css": exporter.ORIGINAL_MEDIA_CSS,
        "front_template": exporter.ORIGINAL_MEDIA_FRONT_TEMPLATE,
        "back_template": exporter.ORIGINAL_MEDIA_BACK_TEMPLATE,
    },
}
SETTING_KEYS = {
    "schemaVersion",
    "enabled",
    "instanceId",
    "expectedProfile",
    "deckPreset",
    "projectTags",
    "endpoint",
    "deckRoutes",
    "provisionManaged",
    "noteTypes",
    "maxMediaBytes",
    "timeoutSeconds",
}
NOTE_TYPE_KEYS = {"modelName", "fieldMap", "requiredFields", "css", "frontTemplate", "backTemplate", "mediaProjection"}


@dataclass(frozen=True)
class AdaptedSettings:
    settings: dict[str, Any]
    config: dict[str, Any]
    task_snapshot: dict[str, Any]


def _non_empty_string(value: Any, label: str) -> str:
    if not isinstance(value, str) or not value.strip() or value != value.strip():
        raise exporter.ContractError("SETTINGS_INVALID", f"{label} must be a non-empty trimmed string")
    return value


def _map_note_types(value: Any) -> dict[str, Any]:
    if value is None:
        return copy.deepcopy(DEFAULT_NOTE_TYPES)
    if not isinstance(value, dict) or not value:
        raise exporter.ContractError("SETTINGS_INVALID", "noteTypes must be a non-empty object")
    result: dict[str, Any] = {}
    for key, mapping in value.items():
        _non_empty_string(key, "noteTypes key")
        if not isinstance(mapping, dict) or set(mapping) - NOTE_TYPE_KEYS:
            raise exporter.ContractError("SETTINGS_INVALID", f"noteTypes.{key} has unsupported fields")
        required_keys = {"modelName", "fieldMap", "requiredFields"}
        if not required_keys.issubset(mapping):
            raise exporter.ContractError("SETTINGS_INVALID", f"noteTypes.{key} is missing required fields")
        converted = {
            "model_name": mapping["modelName"],
            "field_map": mapping["fieldMap"],
            "required_fields": mapping["requiredFields"],
        }
        for source, target in (("css", "css"), ("frontTemplate", "front_template"), ("backTemplate", "back_template")):
            if source in mapping:
                converted[target] = mapping[source]
        if "mediaProjection" in mapping:
            converted["media_projection"] = mapping["mediaProjection"]
        result[key] = converted
    if not any(mapping.get("media_projection") == exporter.ORIGINAL_MEDIA_PROJECTION for mapping in result.values()):
        if "original_image_qa" in result:
            raise exporter.ContractError("SETTINGS_INVALID", "custom original_image_qa must declare mediaProjection=ordered-original-images.v1")
        result["original_image_qa"] = copy.deepcopy(DEFAULT_NOTE_TYPES["original_image_qa"])
    return result


def adapt_settings(settings: dict[str, Any], *, artifact_id: str = "anki-config") -> AdaptedSettings:
    if not isinstance(settings, dict):
        raise exporter.ContractError("SETTINGS_INVALID", "Anki destination settings must be an object")
    if set(settings) - SETTING_KEYS:
        raise exporter.ContractError("SETTINGS_INVALID", f"Unsupported Anki setting(s): {sorted(set(settings) - SETTING_KEYS)}")
    required = {"schemaVersion", "enabled", "expectedProfile", "deckPreset", "projectTags"}
    if not required.issubset(settings):
        raise exporter.ContractError("SETTINGS_INVALID", f"Missing Anki setting(s): {sorted(required - set(settings))}")
    if settings.get("schemaVersion") != SETTINGS_SCHEMA_VERSION:
        raise exporter.ContractError("SETTINGS_UNSUPPORTED", f"schemaVersion must be {SETTINGS_SCHEMA_VERSION}")
    if not isinstance(settings.get("enabled"), bool):
        raise exporter.ContractError("SETTINGS_INVALID", "enabled must be a boolean")
    instance_id = settings.get("instanceId", "default")
    if instance_id != "default":
        raise exporter.ContractError("SETTINGS_INVALID", "Only the fixed Anki destination instanceId 'default' is supported")
    expected_profile = _non_empty_string(settings.get("expectedProfile"), "expectedProfile")
    endpoint = settings.get("endpoint", "http://127.0.0.1:8765")
    deck_preset = settings.get("deckPreset")
    overrides = settings.get("deckRoutes")
    if deck_preset == "knowledge-clipper-default":
        routes = copy.deepcopy(DEFAULT_DECK_ROUTES)
        if overrides is not None:
            if not isinstance(overrides, dict):
                raise exporter.ContractError("SETTINGS_INVALID", "deckRoutes must be an object")
            routes.update(overrides)
    elif deck_preset == "custom":
        if not isinstance(overrides, dict) or not overrides:
            raise exporter.ContractError("SETTINGS_INVALID", "custom deckPreset requires non-empty deckRoutes")
        routes = copy.deepcopy(overrides)
    else:
        raise exporter.ContractError("SETTINGS_INVALID", "deckPreset must be knowledge-clipper-default or custom")

    tags = settings.get("projectTags")
    if not isinstance(tags, list) or not all(isinstance(tag, str) and tag.strip() == tag and tag and not any(char.isspace() for char in tag) for tag in tags):
        raise exporter.ContractError("SETTINGS_INVALID", "projectTags must be non-empty Anki tags without whitespace")
    if len(set(tags)) != len(tags):
        raise exporter.ContractError("SETTINGS_INVALID", "projectTags must be unique")
    if "rqc_managed" in tags:
        raise exporter.ContractError("SETTINGS_INVALID", "rqc_managed is reserved and added by the exporter")
    provision = settings.get("provisionManaged", False)
    if not isinstance(provision, bool):
        raise exporter.ContractError("SETTINGS_INVALID", "provisionManaged must be a boolean")

    config = {
        "schema_version": "anki-export-config.v2",
        "endpoint": endpoint,
        "expected_profile": expected_profile,
        "timeout_seconds": settings.get("timeoutSeconds", 15),
        "max_media_bytes": settings.get("maxMediaBytes", 25 * 1024 * 1024),
        "provision_managed": provision,
        "deck_routes": routes,
        "note_types": _map_note_types(settings.get("noteTypes")),
    }
    exporter.validate_config(config)
    task_snapshot = {
        "destinations": ["anki"] if settings["enabled"] else [],
        "destinationInstances": {
            "anki": {"instanceId": instance_id, "configArtifactId": artifact_id}
        },
        "configSnapshot": {"ankiConfigArtifactId": artifact_id},
        "coreProjectionPatch": {"tags": list(tags)},
        "media": {
            "status": "candidate_pending_core_contract",
            "activePolicy": "reject-media-deferred",
            "candidatePolicy": exporter.ORIGINAL_MEDIA_PROJECTION,
        },
    }
    normalized_settings = copy.deepcopy(settings)
    normalized_settings["instanceId"] = instance_id
    return AdaptedSettings(normalized_settings, config, task_snapshot)


def write_config_artifact(
    job_root: Path,
    adapted: AdaptedSettings,
    *,
    artifact_id: str,
    created_at: str,
    attempt_id: str,
) -> dict[str, Any]:
    if exporter.IDENTIFIER.fullmatch(artifact_id) is None:
        raise exporter.ContractError("SETTINGS_INVALID", "artifact_id must be a common identifier")
    exporter._date_time(created_at, "created_at")
    exporter._identifier(attempt_id, "attempt_id")
    root = job_root.resolve()
    relative_root = f"artifacts/{artifact_id}"
    payload_relative = f"{relative_root}/anki-export.v2.json"
    manifest_relative = f"{relative_root}/artifact.json"
    payload_path = exporter.safe_relative_path(root, payload_relative)
    manifest_path = exporter.safe_relative_path(root, manifest_relative)
    exporter.write_json_atomic_once(payload_path, adapted.config)
    artifact = {
        "schemaVersion": "1.0.0",
        "artifactId": artifact_id,
        "kind": "anki-export-config",
        "contractVersion": "1.0.0",
        "mediaType": "application/vnd.knowledge-clipper.anki-export-config+json",
        "files": [{
            "path": payload_relative,
            "sha256": exporter.sha256_file(payload_path),
            "bytes": exporter.path_size(payload_path),
            "mediaType": "application/json",
        }],
        "createdAt": created_at,
        "producer": {
            "stageId": "anki-settings-adapter",
            "workerId": "export.anki.settings-adapter",
            "workerVersion": ADAPTER_VERSION,
            "attemptId": attempt_id,
        },
        "inputRefs": [],
        "dataClass": "personal",
        "metadata": {
            "entrypoint": payload_relative,
            "destinationId": "anki",
            "destinationInstanceId": adapted.task_snapshot["destinationInstances"]["anki"]["instanceId"],
        },
    }
    exporter.write_json_atomic_once(manifest_path, artifact)
    reference = {
        "artifactRef": artifact_id,
        "kind": "anki-export-config",
        "contractVersion": "1.0.0",
        "manifestPath": manifest_relative,
        "expectedSha256": exporter.sha256_file(manifest_path),
    }
    return {
        "schemaVersion": ADAPTATION_SCHEMA_VERSION,
        "destinationId": "anki",
        "taskSnapshot": adapted.task_snapshot,
        "configArtifact": reference,
        "configPayloadSha256": exporter.sha256_file(payload_path),
    }


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    describe = commands.add_parser("describe")
    describe.add_argument("--json", action="store_true", required=True)
    adapt = commands.add_parser("adapt")
    adapt.add_argument("--settings", type=Path, required=True)
    adapt.add_argument("--job-root", type=Path, required=True)
    adapt.add_argument("--artifact-id", required=True)
    adapt.add_argument("--created-at", required=True)
    adapt.add_argument("--attempt-id", required=True)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    if args.command == "describe":
        descriptor = Path(__file__).resolve().parent / "contracts" / "anki-destination-settings-descriptor.v1.2.json"
        print(json.dumps(exporter.load_json(descriptor), ensure_ascii=False, sort_keys=True, separators=(",", ":")))
        return 0
    if not args.settings.is_absolute() or not args.job_root.is_absolute():
        raise exporter.ContractError("SETTINGS_PATH_INVALID", "settings and job-root must be absolute paths")
    settings = exporter.load_json(args.settings)
    adapted = adapt_settings(settings, artifact_id=args.artifact_id)
    result = write_config_artifact(args.job_root, adapted, artifact_id=args.artifact_id, created_at=args.created_at, attempt_id=args.attempt_id)
    print(json.dumps(result, ensure_ascii=False, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
