from __future__ import annotations

import json
import sys
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource


ROOT = Path(__file__).resolve().parents[1]
SCHEMAS = ROOT / "contracts" / "v1" / "schemas"
FIXTURES = ROOT / "contracts" / "v1" / "fixtures"
CORE_ADAPTER = ROOT / "contracts" / "core-adapter" / "v1"
CORE_V2 = ROOT / "contracts" / "core" / "v2"
CORE_V21 = ROOT / "contracts" / "core" / "v2.1"
CORE_V21_LANGUAGE = ROOT / "contracts" / "core" / "v2.1" / "language-practice"
CORE_V22_QUESTION = ROOT / "contracts" / "core" / "v2.2" / "question-workflow"
CORE_V23_PLAYBACK = ROOT / "contracts" / "core" / "v2.3" / "playback-control"
CORE_V24_SETTINGS = ROOT / "contracts" / "core" / "v2.4" / "settings-control"
CORE_V25_IMAGE = ROOT / "contracts" / "core" / "v2.5" / "image-orchestration"
CORE_V26_OCR_RESOLUTION = ROOT / "contracts" / "core" / "v2.6" / "ocr-human-resolution"
CORE_V26_ORIGINAL_MEDIA = ROOT / "contracts" / "core" / "v2.6" / "original-media"
CORE_V27_MODULE_CONTROL = ROOT / "contracts" / "core" / "v2.7" / "module-control"
CORE_V27_RETENTION = ROOT / "contracts" / "core" / "v2.7" / "retention"
CORE_V28_DISCOVERY = ROOT / "contracts" / "core" / "v2.8" / "module-control"
CORE_V29_ANKI_INITIALIZATION = ROOT / "contracts" / "core" / "v2.9" / "module-control"
CORE_V3_READ_TO_ANKI = ROOT / "contracts" / "core" / "v3" / "operations" / "clip.read-aloud-cards"
CORE_V3_DESTINATIONS = ROOT / "contracts" / "core" / "v3" / "operations" / "destinations"


def load_json(path: Path) -> object:
    return json.loads(path.read_text(encoding="utf-8"))


def build_registry() -> tuple[Registry, dict[Path, object]]:
    registry = Registry()
    loaded: dict[Path, object] = {}
    schema_paths = (
        list(SCHEMAS.rglob("*.schema.json"))
        + list((CORE_ADAPTER / "schemas").rglob("*.schema.json"))
        + list((CORE_V2 / "schemas").rglob("*.schema.json"))
        + list((CORE_V21 / "schemas").rglob("*.schema.json"))
        + list((CORE_V21_LANGUAGE / "schemas").rglob("*.schema.json"))
        + list((CORE_V22_QUESTION / "schemas").rglob("*.schema.json"))
        + list((CORE_V23_PLAYBACK / "schemas").rglob("*.schema.json"))
        + list((CORE_V24_SETTINGS / "schemas").rglob("*.schema.json"))
        + list((CORE_V25_IMAGE / "schemas").rglob("*.schema.json"))
        + list((CORE_V26_OCR_RESOLUTION / "schemas").rglob("*.schema.json"))
        + list((CORE_V26_ORIGINAL_MEDIA / "schemas").rglob("*.schema.json"))
        + list((CORE_V27_MODULE_CONTROL / "schemas").rglob("*.schema.json"))
        + list((CORE_V27_RETENTION / "schemas").rglob("*.schema.json"))
        + list((CORE_V28_DISCOVERY / "schemas").rglob("*.schema.json"))
        + list((CORE_V29_ANKI_INITIALIZATION / "schemas").rglob("*.schema.json"))
        + list(CORE_V3_READ_TO_ANKI.glob("*.schema.json"))
        + list(CORE_V3_DESTINATIONS.glob("*.schema.json"))
        + list((ROOT / "contracts" / "core" / "v3" / "artifacts" / "schemas").glob("*.schema.json"))
        + [
            ROOT / "content-worker" / "schemas" / "language-practice-decision.v1.schema.json",
            ROOT / "content-worker" / "schemas" / "question-answer.v1.schema.json",
            ROOT / "contracts" / "remnote-destination-settings.v1.schema.json",
            ROOT / "contracts" / "remnote-destination-settings-snapshot.v1.schema.json",
            ROOT / "contracts" / "remnote-export-payload.v2.schema.json",
            ROOT / "contracts" / "remnote-target-discovery-request.v1.schema.json",
            ROOT / "contracts" / "remnote-target-discovery-result.v1.schema.json",
        ]
    )
    for path in sorted(schema_paths):
        schema = load_json(path)
        Draft202012Validator.check_schema(schema)
        loaded[path] = schema
        registry = registry.with_resource(path.resolve().as_uri(), Resource.from_contents(schema))
    return registry, loaded


def validator(path: Path, registry: Registry, loaded: dict[Path, object]) -> Draft202012Validator:
    return Draft202012Validator(
        loaded[path],
        registry=registry,
        _resolver=registry.resolver(path.resolve().as_uri()),
        format_checker=FormatChecker(),
    )


def validate_fixture_group(
    schema_path: Path,
    fixture_root: Path,
    registry: Registry,
    loaded: dict[Path, object],
) -> tuple[int, int]:
    check = validator(schema_path, registry, loaded)
    valid_count = 0
    invalid_count = 0
    for path in sorted((fixture_root / "valid").glob("*.json")):
        errors = sorted(check.iter_errors(load_json(path)), key=lambda item: list(item.path))
        if errors:
            raise AssertionError(f"valid fixture rejected: {path}: {errors[0].message}")
        valid_count += 1
    for path in sorted((fixture_root / "invalid").glob("*.json")):
        errors = list(check.iter_errors(load_json(path)))
        if not errors:
            raise AssertionError(f"invalid fixture accepted: {path}")
        invalid_count += 1
    return valid_count, invalid_count


def main() -> int:
    registry, loaded = build_registry()
    groups = [
        (CORE_V3_DESTINATIONS / "command.v1.schema.json", CORE_V3_DESTINATIONS / "fixtures"),
        (
            CORE_V3_READ_TO_ANKI / "discovery-result.v1.schema.json",
            CORE_V3_READ_TO_ANKI / "fixtures" / "discovery-v1",
        ),
        (
            CORE_V3_READ_TO_ANKI / "playback-stop.v1.schema.json",
            CORE_V3_READ_TO_ANKI / "fixtures" / "playback-stop-v1",
        ),
        (
            CORE_V3_READ_TO_ANKI / "playback-control-projection.v1.schema.json",
            CORE_V3_READ_TO_ANKI / "fixtures" / "playback-control-v1",
        ),
        (
            CORE_V3_READ_TO_ANKI / "action-discovery-result.v1.schema.json",
            CORE_V3_READ_TO_ANKI / "fixtures" / "action-discovery-v1",
        ),
        (
            CORE_V3_READ_TO_ANKI / "context-evidence.v1.schema.json",
            CORE_V3_READ_TO_ANKI / "fixtures" / "context-evidence-v1",
        ),
        (
            CORE_V3_READ_TO_ANKI / "settings-evidence.v1.schema.json",
            CORE_V3_READ_TO_ANKI / "fixtures" / "settings-evidence-v1",
        ),
        (
            CORE_V3_READ_TO_ANKI / "projection.v3.schema.json",
            CORE_V3_READ_TO_ANKI / "fixtures" / "projection-v3",
        ),
        (
            CORE_V3_READ_TO_ANKI / "projection.v4.schema.json",
            CORE_V3_READ_TO_ANKI / "fixtures" / "projection-v4",
        ),
        (
            CORE_V27_RETENTION / "schemas" / "retention-policy.v1.schema.json",
            CORE_V27_RETENTION / "fixtures" / "retention-policy",
        ),
        (
            SCHEMAS / "speech" / "speech-request.schema.json",
            FIXTURES / "speech",
        ),
        (
            SCHEMAS / "speech" / "speech-result.schema.json",
            FIXTURES / "speech-result",
        ),
        (
            SCHEMAS / "common" / "worker-result.schema.json",
            FIXTURES / "common" / "worker-result",
        ),
        (
            SCHEMAS / "common" / "worker-describe.schema.json",
            FIXTURES / "common" / "worker-describe",
        ),
        (
            SCHEMAS / "common" / "worker-cancel-signal.schema.json",
            FIXTURES / "common" / "worker-cancel-signal",
        ),
        (
            SCHEMAS / "common" / "task-cancel-payload.schema.json",
            FIXTURES / "common" / "task-cancel-payload",
        ),
        (
            SCHEMAS / "common" / "ipc-request.schema.json",
            FIXTURES / "common" / "ipc-request",
        ),
        (
            SCHEMAS / "common" / "ipc-response.schema.json",
            FIXTURES / "common" / "ipc-response",
        ),
        (
            SCHEMAS / "common" / "task-command-receipt.schema.json",
            FIXTURES / "common" / "task-command",
        ),
        (
            SCHEMAS / "quality" / "verification-receipt.schema.json",
            FIXTURES / "quality" / "verification-receipt",
        ),
        (
            CORE_ADAPTER / "schemas" / "desktop-intent.v1.schema.json",
            CORE_ADAPTER / "fixtures" / "desktop-intent",
        ),
        (
            CORE_ADAPTER / "schemas" / "resolved-pipeline.v1.schema.json",
            CORE_ADAPTER / "fixtures" / "resolved-pipeline",
        ),
        (
            CORE_ADAPTER / "schemas" / "profile-resolver.v1.schema.json",
            CORE_ADAPTER / "fixtures" / "profile-resolver",
        ),
        (
            CORE_ADAPTER / "schemas" / "content-to-remnote.v1.schema.json",
            CORE_ADAPTER / "fixtures" / "content-to-remnote",
        ),
        (
            CORE_ADAPTER / "schemas" / "content-to-anki.v1.schema.json",
            CORE_ADAPTER / "fixtures" / "content-to-anki",
        ),
        (
            CORE_ADAPTER / "schemas" / "playback-control.v1.schema.json",
            CORE_ADAPTER / "fixtures" / "playback-control",
        ),
        (
            CORE_V2 / "schemas" / "clip-intent.v2.schema.json",
            CORE_V2 / "fixtures" / "clip-intent",
        ),
        (
            CORE_V2 / "schemas" / "task-manifest.v2.schema.json",
            CORE_V2 / "fixtures" / "task-manifest",
        ),
        (
            CORE_V2 / "schemas" / "pipeline-plan.v2.schema.json",
            CORE_V2 / "fixtures" / "pipeline-plan",
        ),
        (
            CORE_V21 / "schemas" / "profile-resolver.v2.1.schema.json",
            CORE_V21 / "fixtures" / "profile-resolver",
        ),
        (
            CORE_V21_LANGUAGE / "schemas" / "language-practice-worker-decision.v2.schema.json",
            CORE_V21_LANGUAGE / "fixtures" / "worker-decision",
        ),
        (
            CORE_V21_LANGUAGE / "schemas" / "language-practice-decision.v2.schema.json",
            CORE_V21_LANGUAGE / "fixtures" / "decision",
        ),
        (
            CORE_V21_LANGUAGE / "schemas" / "language-practice-choice.v2.schema.json",
            CORE_V21_LANGUAGE / "fixtures" / "choice",
        ),
        (
            CORE_V21_LANGUAGE / "schemas" / "task-query.v2.schema.json",
            CORE_V21_LANGUAGE / "fixtures" / "task-query",
        ),
        (
            CORE_V21_LANGUAGE / "schemas" / "task-query-result.v2.schema.json",
            CORE_V21_LANGUAGE / "fixtures" / "task-query-result",
        ),
        (
            CORE_V21_LANGUAGE / "schemas" / "language-practice-input-adapter.v2.schema.json",
            CORE_V21_LANGUAGE / "fixtures" / "input-adapter",
        ),
        (
            CORE_V22_QUESTION / "schemas" / "question-action.v2.schema.json",
            CORE_V22_QUESTION / "fixtures" / "question-action",
        ),
        (
            CORE_V22_QUESTION / "schemas" / "task-query.v2.2.schema.json",
            CORE_V22_QUESTION / "fixtures" / "task-query",
        ),
        (
            CORE_V22_QUESTION / "schemas" / "task-query-result.v2.2.schema.json",
            CORE_V22_QUESTION / "fixtures" / "task-query-result",
        ),
        (
            CORE_V23_PLAYBACK / "schemas" / "stage-control-request.v1.schema.json",
            CORE_V23_PLAYBACK / "fixtures" / "stage-control-request",
        ),
        (
            CORE_V23_PLAYBACK / "schemas" / "stage-control-receipt.v1.schema.json",
            CORE_V23_PLAYBACK / "fixtures" / "stage-control-receipt",
        ),
        (
            CORE_V23_PLAYBACK / "schemas" / "ipc-request.v1.1.schema.json",
            CORE_V23_PLAYBACK / "fixtures" / "stage-control",
        ),
        (
            CORE_V24_SETTINGS / "schemas" / "workflow-settings.v1.schema.json",
            CORE_V24_SETTINGS / "fixtures" / "workflow-settings",
        ),
        (
            CORE_V24_SETTINGS / "schemas" / "task-settings-override.v1.schema.json",
            CORE_V24_SETTINGS / "fixtures" / "task-settings-override",
        ),
        (
            CORE_V24_SETTINGS / "schemas" / "task-settings-snapshot.v1.schema.json",
            CORE_V24_SETTINGS / "fixtures" / "task-settings-snapshot",
        ),
        (
            CORE_V24_SETTINGS / "schemas" / "workflow-settings.v2.schema.json",
            CORE_V24_SETTINGS / "fixtures" / "workflow-settings-v2",
        ),
        (
            CORE_V24_SETTINGS / "schemas" / "task-settings-override.v2.schema.json",
            CORE_V24_SETTINGS / "fixtures" / "task-settings-override-v2",
        ),
        (
            CORE_V24_SETTINGS / "schemas" / "task-settings-snapshot.v2.schema.json",
            CORE_V24_SETTINGS / "fixtures" / "task-settings-snapshot-v2",
        ),
        (
            CORE_V24_SETTINGS / "schemas" / "workflow-settings.v3.schema.json",
            CORE_V24_SETTINGS / "fixtures" / "workflow-settings-v3",
        ),
        (
            CORE_V24_SETTINGS / "schemas" / "task-settings-snapshot.v3.schema.json",
            CORE_V24_SETTINGS / "fixtures" / "task-settings-snapshot-v3",
        ),
        (
            CORE_V24_SETTINGS / "schemas" / "task-settings-override.v3.schema.json",
            CORE_V24_SETTINGS / "fixtures" / "task-settings-override-v3",
        ),
        (
            CORE_V25_IMAGE / "schemas" / "pipeline-plan.v2.1.schema.json",
            CORE_V25_IMAGE / "fixtures" / "pipeline-plan",
        ),
        (
            CORE_V26_OCR_RESOLUTION / "schemas" / "ocr-human-resolution-action.v1.schema.json",
            CORE_V26_OCR_RESOLUTION / "fixtures" / "action",
        ),
        (
            CORE_V26_OCR_RESOLUTION / "schemas" / "task-query.v2.3.schema.json",
            CORE_V26_OCR_RESOLUTION / "fixtures" / "task-query",
        ),
        (
            CORE_V26_OCR_RESOLUTION / "schemas" / "task-query-result.v2.3.schema.json",
            CORE_V26_OCR_RESOLUTION / "fixtures" / "task-query-result",
        ),
        (
            CORE_V26_ORIGINAL_MEDIA / "schemas" / "media-bundle.v1.schema.json",
            CORE_V26_ORIGINAL_MEDIA / "fixtures" / "media-bundle",
        ),
        (
            CORE_V26_ORIGINAL_MEDIA / "schemas" / "ordered-original-images-adapter.v1.schema.json",
            CORE_V26_ORIGINAL_MEDIA / "fixtures" / "ordered-original-images-adapter",
        ),
        (
            CORE_V27_MODULE_CONTROL / "schemas" / "module-action-request.v1.schema.json",
            CORE_V27_MODULE_CONTROL / "fixtures" / "module-action-request",
        ),
        (
            CORE_V28_DISCOVERY / "schemas" / "module-action-request.v2.schema.json",
            CORE_V28_DISCOVERY / "fixtures" / "module-action-request",
        ),
        (
            CORE_V29_ANKI_INITIALIZATION / "schemas" / "module-action-request.v3.schema.json",
            CORE_V29_ANKI_INITIALIZATION / "fixtures" / "module-action-request",
        ),
        (
            CORE_V29_ANKI_INITIALIZATION / "schemas" / "anki-managed-initialization-action.v1.schema.json",
            CORE_V29_ANKI_INITIALIZATION / "fixtures" / "anki-managed-initialization-action",
        ),
        (
            ROOT / "contracts" / "remnote-target-discovery-request.v1.schema.json",
            ROOT / "contracts" / "fixtures" / "remnote-target-discovery-request",
        ),
        (
            ROOT / "contracts" / "remnote-target-discovery-result.v1.schema.json",
            ROOT / "contracts" / "fixtures" / "remnote-target-discovery-result",
        ),
    ]
    valid_total = 0
    invalid_total = 0
    for schema_path, fixture_root in groups:
        valid_count, invalid_count = validate_fixture_group(
            schema_path, fixture_root, registry, loaded
        )
        valid_total += valid_count
        invalid_total += invalid_count

    resolver_document = load_json(CORE_ADAPTER / "profile-resolver.v1.json")
    validator(
        CORE_ADAPTER / "schemas" / "profile-resolver.v1.schema.json", registry, loaded
    ).validate(resolver_document)

    identity_v2 = load_json(CORE_V2 / "product-identity.v2.json")
    validator(
        CORE_V2 / "schemas" / "product-identity.v2.schema.json", registry, loaded
    ).validate(identity_v2)
    resolver_v2 = load_json(CORE_V2 / "profile-resolver.v2.json")
    validator(
        CORE_V2 / "schemas" / "profile-resolver.v2.schema.json", registry, loaded
    ).validate(resolver_v2)
    resolver_v21 = load_json(CORE_V21 / "profile-resolver.v2.1.json")
    validator(
        CORE_V21 / "schemas" / "profile-resolver.v2.1.schema.json", registry, loaded
    ).validate(resolver_v21)
    settings_adapter = load_json(CORE_V24_SETTINGS / "module-settings-adapter.v1.json")
    validator(
        CORE_V24_SETTINGS / "schemas" / "module-settings-adapter.v1.schema.json", registry, loaded
    ).validate(settings_adapter)
    settings_adapter_v2 = load_json(CORE_V24_SETTINGS / "module-settings-adapter.v2.json")
    validator(
        CORE_V24_SETTINGS / "schemas" / "module-settings-adapter.v2.schema.json", registry, loaded
    ).validate(settings_adapter_v2)
    settings_adapter_v3 = load_json(CORE_V24_SETTINGS / "module-settings-adapter.v3.json")
    validator(
        CORE_V24_SETTINGS / "schemas" / "module-settings-adapter.v3.schema.json", registry, loaded
    ).validate(settings_adapter_v3)

    category_registry = load_json(ROOT / "content-worker" / "config" / "categories.v1.json")
    if category_registry["schema_version"] != resolver_document["ankiProjection"]["categoryRegistryVersion"]:
        raise AssertionError("Anki projection category registry version drifted")
    registry_categories = {item["id"] for item in category_registry["categories"]}
    registry_templates = {template for item in category_registry["categories"] for template in item["allowed_templates"]}
    if set(resolver_document["ankiProjection"]["categoryToDeckRoute"]) != registry_categories:
        raise AssertionError("Anki projection category mapping is not exhaustive")
    if set(resolver_document["ankiProjection"]["templateMappings"]) != registry_templates:
        raise AssertionError("Anki projection template mapping is not exhaustive")
    if set(resolver_document["remNoteProjection"]["templateMappings"]) != registry_templates:
        raise AssertionError("RemNote projection template mapping is not exhaustive")
    if set(resolver_v2["destinationConfigurations"]["anki"]["projection"]["categoryToDeckRoute"]) != registry_categories:
        raise AssertionError("KnowledgeClipper Anki destination category mapping is not exhaustive")
    if set(resolver_v2["destinationConfigurations"]["anki"]["projection"]["templateMappings"]) != registry_templates:
        raise AssertionError("KnowledgeClipper Anki destination template mapping is not exhaustive")
    if set(resolver_v2["destinationConfigurations"]["remnote"]["projection"]["templateMappings"]) != registry_templates:
        raise AssertionError("KnowledgeClipper RemNote destination template mapping is not exhaustive")
    category_registry_v2 = load_json(ROOT / "content-worker" / "config" / "categories.v2.json")
    registry_v2_categories = {item["id"] for item in category_registry_v2["categories"]}
    registry_v2_templates = {template for item in category_registry_v2["categories"] for template in item["allowed_templates"]}
    if category_registry_v2["schema_version"] != resolver_v21["destinationConfigurations"]["anki"]["projection"]["categoryRegistryVersion"]:
        raise AssertionError("KnowledgeClipper v2.1 category registry version drifted")
    if set(resolver_v21["destinationConfigurations"]["anki"]["projection"]["categoryToDeckRoute"]) != registry_v2_categories:
        raise AssertionError("KnowledgeClipper v2.1 Anki category mapping is not exhaustive")
    if set(resolver_v21["destinationConfigurations"]["anki"]["projection"]["templateMappings"]) != registry_v2_templates:
        raise AssertionError("KnowledgeClipper v2.1 Anki template mapping is not exhaustive")
    if set(resolver_v21["destinationConfigurations"]["remnote"]["projection"]["templateMappings"]) != registry_v2_templates:
        raise AssertionError("KnowledgeClipper v2.1 RemNote template mapping is not exhaustive")

    registry_schema = validator(
        SCHEMAS / "common" / "worker-registry.schema.json", registry, loaded
    )
    registry_document = load_json(
        ROOT / "contracts" / "v1" / "registry" / "worker-registry.v1.json"
    )
    registry_schema.validate(registry_document)
    authority_ids = {item["workerId"] for item in registry_document["workers"]}

    planned_document = load_json(
        ROOT / "contracts" / "v1" / "registry" / "planned-workers.v1.json"
    )
    planned_ids = {item["workerId"] for item in planned_document["workers"]}
    unknown_authority_ids = authority_ids - planned_ids
    if unknown_authority_ids:
        raise AssertionError(
            "authority registry contains an unknown worker ID: "
            + ", ".join(sorted(unknown_authority_ids))
        )

    cli_document = load_json(
        ROOT / "contracts" / "v1" / "worker" / "worker-cli.v1.json"
    )
    if cli_document["describe"]["arguments"] != ["describe", "--json"]:
        raise AssertionError("worker describe CLI arguments drifted")
    if cli_document["run"]["arguments"] != [
        "run",
        "--request",
        "<absolute-request.json>",
    ]:
        raise AssertionError("worker run CLI arguments drifted")
    expected_reasons = {"user_stop", "superseded", "task_cancel", "timeout"}
    if set(cli_document["cancellation"]["reasons"]) != expected_reasons:
        raise AssertionError("worker cancellation reasons drifted")
    if cli_document["cancellation"]["publicStatusMapping"] != {
        "user_stop": "cancelled",
        "superseded": "cancelled",
        "task_cancel": "cancelled",
        "timeout": "failed",
    }:
        raise AssertionError("worker cancellation status mapping drifted")
    if cli_document["cancellation"]["gracePeriodMs"] != 2000:
        raise AssertionError("worker cancellation grace period drifted")

    ipc_document = load_json(
        ROOT / "contracts" / "v1" / "ipc" / "orchestrator-ipc.v1.json"
    )
    expected_framing = {
        "protocol": "utf-8-json-lines-v1",
        "requestPerConnection": 1,
        "responsePerConnection": 1,
        "delimiter": "LF",
        "acceptedDelimiter": ["LF", "CRLF"],
        "bomAllowed": False,
        "jsonNewlines": "escaped-only",
        "byteLimitExcludesDelimiter": True,
        "serverPush": False,
        "connectionMode": "unary",
        "concurrency": "multiple-connections",
    }
    if ipc_document["maxMessageBytes"] != 1048576 or ipc_document["framing"] != expected_framing:
        raise AssertionError("orchestrator IPC framing drifted")
    playback_ipc_document = load_json(CORE_V23_PLAYBACK / "orchestrator-ipc.v1.1.json")
    if playback_ipc_document["schemaVersion"] != "1.1.0" or playback_ipc_document["framing"] != expected_framing:
        raise AssertionError("append-only stage-control IPC framing drifted")
    if playback_ipc_document["allowedMessages"] != ["task-command", "task-query", "stage-control"]:
        raise AssertionError("append-only stage-control IPC message registry drifted")

    print(
        json.dumps(
            {
                "schemaVersion": "1.0.0",
                "schemasChecked": len(loaded),
                "validFixturesAccepted": valid_total,
                "invalidFixturesRejected": invalid_total,
                "authoritativeRegistryEntries": len(registry_document["workers"]),
                "workerCliDescriptorChecked": True,
                "speechEdgeRegistryState": "authority-pending" if "speech.edge" not in authority_ids else "published",
                "imageOcrRegistryState": "authority-pending" if "image.ocr.mineru" not in authority_ids else "published",
                "ipcFramingChecked": True,
                "stageControlIpcVersion": "1.1",
                "stageControlReceiptVersion": "2.3.0",
                "coreAdapterContractVersion": "2.0.0",
                "dynamicPipelineSchemaVersion": "2.0.0",
                "productIdentity": identity_v2["productId"],
                "destinationNeutral": identity_v2["architecture"] == "destination-neutral",
            },
            ensure_ascii=False,
        )
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"CONTRACT_VALIDATION_FAILED: {exc}", file=sys.stderr)
        raise
