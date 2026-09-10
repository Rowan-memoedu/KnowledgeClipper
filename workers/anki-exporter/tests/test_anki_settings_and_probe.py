import copy
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from jsonschema import Draft202012Validator, FormatChecker


WORKER = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(WORKER))
sys.path.insert(0, str(WORKER / "tests"))

import anki_exporter as exporter
import anki_settings_adapter as settings_adapter
from test_anki_exporter import (
    FakeAnkiServer,
    MODEL_FIELDS,
    base_config,
    build_job,
    build_language_both_job,
    dump_json,
    make_exporter,
    prepare_semantic_readback_job,
    validate_common,
)


MUTATING = {"createModel", "createDeck", "storeMediaFile", "addNote", "updateNoteFields", "addTags", "changeDeck"}


def project_settings(endpoint="http://127.0.0.1:8765", *, enabled=True):
    return {
        "schemaVersion": settings_adapter.SETTINGS_SCHEMA_VERSION,
        "enabled": enabled,
        "expectedProfile": "Canary Profile",
        "deckPreset": "knowledge-clipper-default",
        "projectTags": ["knowledge_clipper", "anki_target"],
        "endpoint": endpoint,
    }


def build_probe_job(root, endpoint):
    job = build_job(root, endpoint, operation="probe")
    task_path = root / job["task"]
    request_path = root / job["request"]
    task = json.loads(task_path.read_text(encoding="utf-8"))
    request = json.loads(request_path.read_text(encoding="utf-8"))
    config_ref = next(ref for ref in request["inputRefs"] if ref["kind"] == "anki-export-config")
    task["inputRefs"] = [
        {key: config_ref[key] for key in ("artifactRef", "kind", "contractVersion", "expectedSha256")}
    ]
    task["requestedOutputs"] = ["anki-connection-status"]
    request["inputRefs"] = [config_ref]
    request["configSnapshot"] = {"ankiConfigArtifactId": config_ref["artifactRef"]}
    request["payload"] = {"probeMode": "read-only"}
    request["idempotencyKey"] = exporter.sha256_bytes(exporter.json_bytes({"operation": "probe", "config": config_ref}))
    dump_json(task_path, task)
    dump_json(request_path, request)
    return job


def build_initialize_job(root, endpoint):
    job = build_probe_job(root, endpoint)
    task_path = root / job["task"]
    request_path = root / job["request"]
    task = json.loads(task_path.read_text(encoding="utf-8"))
    request = json.loads(request_path.read_text(encoding="utf-8"))
    config_id = request["configSnapshot"]["ankiConfigArtifactId"]
    request["operation"] = exporter.INITIALIZE_OPERATION
    request["payload"] = {
        "sideEffectAuthorization": {
            "schemaVersion": "1.0.0",
            "authorizationId": "explicit-initialize-action",
            "mode": "explicit-user-action",
            "operation": exporter.INITIALIZE_OPERATION,
            "scope": "managed-models-and-decks",
            "taskId": request["taskId"],
            "configArtifactId": config_id,
            "authorizedAt": "2026-09-01T00:00:00Z",
        }
    }
    request["idempotencyKey"] = exporter.sha256_bytes(exporter.json_bytes({"operation": exporter.INITIALIZE_OPERATION, "config": request["inputRefs"][0]}))
    task["requestedOutputs"] = ["anki-managed-initialization-status"]
    dump_json(task_path, task)
    dump_json(request_path, request)
    return job


def update_job_config(root, job, mutate):
    task_path = root / job["task"]
    request_path = root / job["request"]
    task = json.loads(task_path.read_text(encoding="utf-8"))
    request = json.loads(request_path.read_text(encoding="utf-8"))
    ref = request["inputRefs"][0]
    manifest_path = root / ref["manifestPath"]
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    payload_path = root / manifest["metadata"]["entrypoint"]
    config = json.loads(payload_path.read_text(encoding="utf-8"))
    mutate(config)
    dump_json(payload_path, config)
    manifest["files"][0]["sha256"] = exporter.sha256_file(payload_path)
    manifest["files"][0]["bytes"] = payload_path.stat().st_size
    dump_json(manifest_path, manifest)
    manifest_sha = exporter.sha256_file(manifest_path)
    ref["expectedSha256"] = manifest_sha
    task["inputRefs"][0]["expectedSha256"] = manifest_sha
    request["idempotencyKey"] = exporter.sha256_bytes(exporter.json_bytes({"operation": request["operation"], "config": ref}))
    dump_json(task_path, task)
    dump_json(request_path, request)


def seed_ready(state, config):
    for mapping in config["note_types"].values():
        model = mapping["model_name"]
        state.models[model] = list(MODEL_FIELDS)
        state.model_templates[model] = {
            "Card 1": {
                "Front": mapping.get("front_template", exporter.DEFAULT_FRONT_TEMPLATE),
                "Back": mapping.get("back_template", exporter.DEFAULT_BACK_TEMPLATE),
            }
        }
        state.model_css[model] = mapping.get("css", exporter.DEFAULT_CSS)
    state.decks.update(config["deck_routes"].values())


class AnkiSettingsAdapterTests(unittest.TestCase):
    def test_descriptor_and_settings_schema_are_strict_module_contracts(self):
        descriptor = json.loads((WORKER / "contracts" / "anki-destination-settings-descriptor.v1.2.json").read_text(encoding="utf-8"))
        descriptor_schema = json.loads((WORKER / "contracts" / "anki-destination-settings-descriptor.v1.2.schema.json").read_text(encoding="utf-8"))
        schema = json.loads((WORKER / "contracts" / "anki-destination-settings.v1.schema.json").read_text(encoding="utf-8"))
        Draft202012Validator(descriptor_schema, format_checker=FormatChecker()).validate(descriptor)
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(project_settings())
        self.assertEqual("export.anki", descriptor["workerBinding"]["workerId"])
        self.assertEqual("1.3.0", descriptor["workerBinding"]["minimumVersion"])
        self.assertFalse(descriptor["supportsMultipleInstances"])
        self.assertEqual("default", descriptor["instanceModel"]["fixedInstanceId"])
        self.assertTrue(descriptor["enablement"]["registryDoesNotEnableDestination"])
        self.assertEqual([], descriptor["secretRefs"])
        self.assertEqual("conditional", descriptor["capabilities"]["media"]["status"])
        self.assertEqual("reject-media-deferred", descriptor["capabilities"]["media"]["activePolicy"])
        self.assertTrue(descriptor["capabilities"]["media"]["requiresCoreInputContract"])
        provision = next(item for item in descriptor["fields"] if item["path"] == "provisionManaged")
        note_types = next(item for item in descriptor["fields"] if item["path"] == "noteTypes")
        self.assertFalse(provision["default"])
        self.assertEqual("explicit-initialization-action", provision["activation"])
        self.assertEqual("migration-required", note_types["activation"])
        self.assertEqual("local-read-only", descriptor["operations"]["validate"]["effectClass"])
        self.assertEqual("none", descriptor["operations"]["validate"]["networkScope"])
        self.assertEqual("provider-read-only", descriptor["operations"]["probe"]["effectClass"])
        self.assertFalse(descriptor["operations"]["probe"]["externalMutation"])
        self.assertTrue(descriptor["operations"]["probe"]["localArtifactWrite"])
        self.assertTrue(descriptor["operations"]["probe"]["mayPromptUser"])
        self.assertEqual("explicit-user-action", descriptor["tests"]["probeConnection"]["executionPolicy"])
        self.assertTrue(descriptor["operations"]["export"]["externalMutation"])

    def test_v13_descriptor_exposes_only_explicit_managed_initialization(self):
        descriptor = json.loads((WORKER / "contracts" / "anki-destination-settings-descriptor.v1.3.json").read_text(encoding="utf-8"))
        schema = json.loads((WORKER / "contracts" / "anki-destination-settings-descriptor.v1.3.schema.json").read_text(encoding="utf-8"))
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(descriptor)
        operation = descriptor["operations"]["initializeManaged"]
        self.assertEqual("initialize-managed", operation["operation"])
        self.assertTrue(operation["workerOperation"])
        self.assertEqual("provider-write", operation["effectClass"])
        self.assertEqual({"createModel", "createDeck"}, set(operation["allowedActions"]) & exporter.MUTATING_ANKI_ACTIONS)
        self.assertTrue(descriptor["capabilities"]["provisioning"]["requiresExplicitExternalWriteAction"])
        self.assertFalse(descriptor["capabilities"]["provisioning"]["automaticRetryAfterUnknownState"])
        self.assertEqual("ready", descriptor["capabilities"]["media"]["status"])
        self.assertEqual("ordered-original-images.v1", descriptor["capabilities"]["media"]["activePolicy"])
        self.assertEqual("anki-only", descriptor["capabilities"]["media"]["destinationPolicy"])

    def test_enabled_only_changes_new_task_destinations(self):
        enabled = settings_adapter.adapt_settings(project_settings(enabled=True), artifact_id="anki-config")
        disabled = settings_adapter.adapt_settings(project_settings(enabled=False), artifact_id="anki-config")
        self.assertEqual(enabled.config, disabled.config)
        self.assertEqual(["anki"], enabled.task_snapshot["destinations"])
        self.assertEqual([], disabled.task_snapshot["destinations"])
        self.assertEqual(enabled.task_snapshot["coreProjectionPatch"], disabled.task_snapshot["coreProjectionPatch"])
        self.assertFalse(enabled.config["provision_managed"])
        self.assertEqual("anki-export-config.v2", enabled.config["schema_version"])
        self.assertEqual(exporter.ORIGINAL_MEDIA_PROJECTION, enabled.config["note_types"]["original_image_qa"]["media_projection"])
        self.assertEqual({"ankiConfigArtifactId": "anki-config"}, enabled.task_snapshot["configSnapshot"])
        self.assertNotIn("endpoint", enabled.task_snapshot["configSnapshot"])
        self.assertEqual(
            {"anki": {"instanceId": "default", "configArtifactId": "anki-config"}},
            enabled.task_snapshot["destinationInstances"],
        )
        self.assertEqual(enabled.task_snapshot["destinationInstances"], disabled.task_snapshot["destinationInstances"])

    def test_settings_write_sha_bound_personal_append_only_config_artifact(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "job"
            adapted = settings_adapter.adapt_settings(project_settings(), artifact_id="anki-settings")
            result = settings_adapter.write_config_artifact(
                root,
                adapted,
                artifact_id="anki-settings",
                created_at="2026-08-28T00:00:00Z",
                attempt_id="settings-attempt",
            )
            manifest = root / result["configArtifact"]["manifestPath"]
            validate_common(manifest, "artifact.schema.json")
            value = json.loads(manifest.read_text(encoding="utf-8"))
            self.assertEqual("personal", value["dataClass"])
            self.assertEqual("default", value["metadata"]["destinationInstanceId"])
            self.assertEqual(exporter.sha256_file(manifest), result["configArtifact"]["expectedSha256"])
            payload = root / value["metadata"]["entrypoint"]
            self.assertTrue(payload.name.endswith("v2.json"))
            self.assertEqual(exporter.sha256_file(payload), result["configPayloadSha256"])
            self.assertFalse(json.loads(payload.read_text(encoding="utf-8"))["provision_managed"])
            with self.assertRaisesRegex(exporter.ContractError, "Refusing to overwrite"):
                settings_adapter.write_config_artifact(
                    root,
                    adapted,
                    artifact_id="anki-settings",
                    created_at="2026-08-28T00:00:00Z",
                    attempt_id="settings-attempt",
                )

    def test_settings_adapter_rejects_fake_or_unsupported_controls(self):
        cases = {
            "remote-endpoint": lambda value: value.update({"endpoint": "http://example.com:8765"}),
            "blank-profile": lambda value: value.update({"expectedProfile": " "}),
            "duplicate-tag": lambda value: value.update({"projectTags": ["same", "same"]}),
            "whitespace-tag": lambda value: value.update({"projectTags": ["bad tag"]}),
            "reserved-tag": lambda value: value.update({"projectTags": ["rqc_managed"]}),
            "media-toggle": lambda value: value.update({"mediaEnabled": True}),
            "secret-inline": lambda value: value.update({"apiKey": "not-allowed"}),
            "multiple-instances": lambda value: value.update({"instances": []}),
            "unsupported-instance": lambda value: value.update({"instanceId": "second"}),
            "custom-without-routes": lambda value: value.update({"deckPreset": "custom"}),
        }
        for name, mutate in cases.items():
            with self.subTest(case=name):
                value = project_settings()
                mutate(value)
                with self.assertRaises(exporter.ContractError):
                    settings_adapter.adapt_settings(value)

    def test_settings_adapter_cli_describe_and_adapt(self):
        described = subprocess.run(
            [sys.executable, str(WORKER / "anki_settings_adapter.py"), "describe", "--json"],
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
        )
        self.assertEqual(0, described.returncode, described.stderr)
        self.assertEqual("anki", json.loads(described.stdout)["destinationId"])
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            settings_path = root / "settings.json"
            job_root = root / "job"
            dump_json(settings_path, project_settings())
            adapted = subprocess.run(
                [
                    sys.executable,
                    str(WORKER / "anki_settings_adapter.py"),
                    "adapt",
                    "--settings",
                    str(settings_path.resolve()),
                    "--job-root",
                    str(job_root.resolve()),
                    "--artifact-id",
                    "anki-cli-config",
                    "--created-at",
                    "2026-08-28T00:00:00Z",
                    "--attempt-id",
                    "cli-adapter",
                ],
                check=False,
                capture_output=True,
                text=True,
                encoding="utf-8",
            )
            self.assertEqual(0, adapted.returncode, adapted.stderr)
            result = json.loads(adapted.stdout)
            self.assertEqual(["anki"], result["taskSnapshot"]["destinations"])
            self.assertEqual({"ankiConfigArtifactId": "anki-cli-config"}, result["taskSnapshot"]["configSnapshot"])
            validate_common(job_root / result["configArtifact"]["manifestPath"], "artifact.schema.json")


class AnkiReadOnlyProbeTests(unittest.TestCase):
    def test_probe_ready_uses_only_read_actions_and_publishes_common_result(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            root = Path(temp) / "probe"
            job = build_probe_job(root, fake.endpoint)
            config = base_config(fake.endpoint)
            seed_ready(fake.state, config)
            before = copy.deepcopy((fake.state.models, fake.state.decks, fake.state.media, fake.state.notes))
            run = make_exporter(job)
            self.assertEqual("SUCCEEDED", run.run())
            after = (fake.state.models, fake.state.decks, fake.state.media, fake.state.notes)
            self.assertEqual(before, after)
            actions = [item["action"] for item in fake.state.calls]
            self.assertEqual(["requestPermission", "version", "getActiveProfile", "apiReflect"], actions[:4])
            self.assertFalse(set(actions) & MUTATING)
            self.assertTrue(set(actions).issubset(exporter.PROBE_READ_ONLY_ACTIONS))
            status_path = run.writer.output_root / "connection-status.json"
            status = json.loads(status_path.read_text(encoding="utf-8"))
            schema = json.loads((WORKER / "contracts" / "anki-connection-status.v1.schema.json").read_text(encoding="utf-8"))
            Draft202012Validator(schema, format_checker=FormatChecker()).validate(status)
            self.assertEqual("ready", status["status"])
            self.assertEqual("provider-read-only", status["effectClass"])
            self.assertEqual("loopback-only", status["networkScope"])
            self.assertFalse(status["externalMutation"])
            self.assertTrue(status["localArtifactWrite"])
            self.assertTrue(status["mayPromptUser"])
            self.assertEqual([], status["mutatingActionsObserved"])
            status_text = status_path.read_text(encoding="utf-8")
            self.assertNotIn(config["expected_profile"], status_text)
            for mapping in config["note_types"].values():
                self.assertNotIn(mapping["model_name"], status_text)
            for deck_name in config["deck_routes"].values():
                self.assertNotIn(deck_name, status_text)
            result = json.loads(run.writer.result_path.read_text(encoding="utf-8"))
            self.assertEqual("completed", result["status"])
            self.assertEqual([], result["sideEffects"])
            self.assertEqual("anki-connection-status", result["outputs"][0]["kind"])
            validate_common(run.writer.output_root / "anki-connection-status.artifact.json", "artifact.schema.json")
            validate_common(run.writer.result_path, "worker-result.schema.json")
            receipt = json.loads((run.writer.output_root / "receipt.json").read_text(encoding="utf-8"))
            self.assertEqual("provider-read-only", receipt["operation_effect"]["effect_class"])
            self.assertFalse(receipt["operation_effect"]["external_mutation"])
            self.assertEqual("sha256-profile-model-deck-identifiers", receipt["status_output"]["redaction"])

    def test_probe_missing_resources_reports_initialization_without_provisioning(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            job = build_probe_job(Path(temp) / "probe", fake.endpoint)
            run = make_exporter(job)
            self.assertEqual("SUCCEEDED", run.run())
            status = json.loads((run.writer.output_root / "connection-status.json").read_text(encoding="utf-8"))
            self.assertEqual("needs_initialization", status["status"])
            self.assertTrue(status["initializationWouldWrite"])
            self.assertEqual({}, fake.state.models)
            self.assertEqual(set(), set(fake.state.decks) - {"Default"})
            self.assertFalse({item["action"] for item in fake.state.calls} & MUTATING)

    def test_probe_schema_conflict_is_migration_required_without_write(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            job = build_probe_job(Path(temp) / "probe", fake.endpoint)
            config = base_config(fake.endpoint)
            seed_ready(fake.state, config)
            fake.state.models["RQC Basic v1"] = ["Front", "Back"]
            run = make_exporter(job)
            self.assertEqual("SUCCEEDED", run.run())
            status = json.loads((run.writer.output_root / "connection-status.json").read_text(encoding="utf-8"))
            self.assertEqual("migration_required", status["status"])
            self.assertTrue(status["migrationRequired"])
            self.assertFalse({item["action"] for item in fake.state.calls} & MUTATING)

    def test_probe_rejects_snapshot_body_and_card_input_before_network(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for case in ("snapshot-body", "card-input"):
                with self.subTest(case=case):
                    job = build_probe_job(root / case, "http://127.0.0.1:65534")
                    request_path = job["root"] / job["request"]
                    request = json.loads(request_path.read_text(encoding="utf-8"))
                    if case == "snapshot-body":
                        request["configSnapshot"]["endpoint"] = "http://127.0.0.1:8765"
                    else:
                        original = json.loads((job["root"] / "artifacts/card-input/artifact.json").read_text(encoding="utf-8"))
                        request["inputRefs"].append({
                            "artifactRef": "card-input",
                            "kind": "card-bundle",
                            "contractVersion": "1.0.0",
                            "manifestPath": "artifacts/card-input/artifact.json",
                            "expectedSha256": exporter.sha256_file(job["root"] / "artifacts/card-input/artifact.json"),
                        })
                    dump_json(request_path, request)
                    with mock.patch.object(exporter, "AnkiClient", side_effect=AssertionError("rejected probe opened AnkiConnect")):
                        if case == "snapshot-body":
                            with self.assertRaisesRegex(exporter.ContractError, "configSnapshot"):
                                make_exporter(job)
                        else:
                            run = make_exporter(job)
                            self.assertEqual("CONTRACT_REJECTED", run.run())
                            self.assertFalse((run.writer.output_root / "operations.ndjson").exists())

    def test_probe_missing_task_manifest_publishes_failed_result_without_network(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "probe"
            job = build_probe_job(root, "http://127.0.0.1:65534")
            request_path = (root / job["request"]).resolve()
            (root / job["task"]).unlink()
            request = json.loads(request_path.read_text(encoding="utf-8"))
            result_path = root / request["resultPath"]
            with mock.patch.object(exporter, "AnkiClient", side_effect=AssertionError("bootstrap failure opened AnkiConnect")):
                self.assertEqual(2, exporter.main(["run", "--request", str(request_path)]))
            result = json.loads(result_path.read_text(encoding="utf-8"))
            self.assertEqual("failed", result["status"])
            self.assertEqual("FILE_MISSING", result["error"]["code"])
            self.assertEqual("contract", result["error"]["category"])
            self.assertFalse(result["error"]["retryable"])
            self.assertEqual([], result["outputs"])
            self.assertEqual([], result["sideEffects"])
            self.assertTrue(result["payload"]["bootstrapFailure"])
            self.assertFalse((result_path.parent / "outputs" / "operations.ndjson").exists())
            validate_common(result_path, "worker-result.schema.json")

    def test_missing_manifest_bootstrap_result_is_probe_only(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "export"
            job = build_job(root, "http://127.0.0.1:65534", operation="export")
            request_path = (root / job["request"]).resolve()
            request = json.loads(request_path.read_text(encoding="utf-8"))
            (root / job["task"]).unlink()
            with mock.patch.object(exporter, "AnkiClient", side_effect=AssertionError("bootstrap failure opened AnkiConnect")):
                self.assertEqual(2, exporter.main(["run", "--request", str(request_path)]))
            self.assertFalse((root / request["resultPath"]).exists())
            self.assertFalse((root / request["outputPath"]).exists())

    def test_semantic_readback_missing_manifest_is_definitive_and_never_connects(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "semantic-readback"
            job = prepare_semantic_readback_job(build_language_both_job(root))
            request_path = (root / job["request"]).resolve()
            request = json.loads(request_path.read_text(encoding="utf-8"))
            result_path = root / request["resultPath"]
            (root / job["task"]).unlink()
            with mock.patch.object(exporter, "AnkiClient", side_effect=AssertionError("bootstrap failure opened AnkiConnect")):
                self.assertEqual(2, exporter.main(["run", "--request", str(request_path)]))
            result = json.loads(result_path.read_text(encoding="utf-8"))
            self.assertEqual("failed", result["status"])
            self.assertEqual("FILE_MISSING", result["error"]["code"])
            self.assertEqual([], result["outputs"])
            self.assertEqual([], result["sideEffects"])
            self.assertTrue(result["payload"]["semanticReadbackOnly"])
            self.assertFalse(result["payload"]["priorScenarioWritesInferred"])
            self.assertFalse((result_path.parent / "outputs" / "operations.ndjson").exists())
            validate_common(result_path, "worker-result.schema.json")


class AnkiManagedInitializationTests(unittest.TestCase):
    def test_initialize_requires_exact_explicit_authorization_before_client(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "initialize"
            job = build_initialize_job(root, "http://127.0.0.1:65534")
            request_path = root / job["request"]
            request = json.loads(request_path.read_text(encoding="utf-8"))
            request["payload"] = {}
            dump_json(request_path, request)
            with mock.patch.object(exporter, "AnkiClient", side_effect=AssertionError("unauthorized initialization opened AnkiConnect")):
                with self.assertRaisesRegex(exporter.ContractError, "sideEffectAuthorization"):
                    make_exporter(job)

    def test_initialize_requires_provision_flag_and_requested_output_without_network(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "initialize"
            job = build_initialize_job(root, "http://127.0.0.1:65534")
            update_job_config(root, job, lambda config: config.update({"provision_managed": False}))
            with mock.patch.object(exporter, "AnkiClient", side_effect=AssertionError("disabled initialization opened AnkiConnect")):
                run = make_exporter(job)
                self.assertEqual("CONTRACT_REJECTED", run.run())
                self.assertFalse((run.writer.output_root / "operations.ndjson").exists())

    def test_initialize_creates_only_missing_resources_exactly_and_replay_is_noop(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            first_job = build_initialize_job(Path(temp) / "first", fake.endpoint)
            first = make_exporter(first_job)
            self.assertEqual("SUCCEEDED", first.run())
            status_path = first.writer.output_root / "initialization-status.json"
            status = json.loads(status_path.read_text(encoding="utf-8"))
            status_schema = json.loads((WORKER / "contracts" / "anki-managed-initialization-status.v1.schema.json").read_text(encoding="utf-8"))
            Draft202012Validator(status_schema, format_checker=FormatChecker()).validate(status)
            self.assertEqual("initialized", status["status"])
            self.assertEqual({"modelsCreated": 1, "decksCreated": 1}, status["writes"])
            self.assertEqual([], status["forbiddenActionsObserved"])
            actions = [item["action"] for item in fake.state.calls]
            self.assertEqual(1, actions.count("createModel"))
            self.assertEqual(1, actions.count("createDeck"))
            self.assertFalse(set(actions) & {"addNote", "updateNoteFields", "addTags", "changeDeck", "storeMediaFile"})
            result = json.loads(first.writer.result_path.read_text(encoding="utf-8"))
            validate_common(first.writer.result_path, "worker-result.schema.json")
            self.assertEqual("completed", result["status"])
            self.assertEqual("anki.initialize-managed", result["sideEffects"][0]["kind"])
            self.assertEqual("verified", result["sideEffects"][0]["state"])

            before_replay = len(fake.state.calls)
            replay_job = build_initialize_job(Path(temp) / "replay", fake.endpoint)
            replay = make_exporter(replay_job)
            self.assertEqual("SUCCEEDED", replay.run())
            replay_status = json.loads((replay.writer.output_root / "initialization-status.json").read_text(encoding="utf-8"))
            self.assertEqual("already_current", replay_status["status"])
            self.assertEqual({"modelsCreated": 0, "decksCreated": 0}, replay_status["writes"])
            replay_actions = [item["action"] for item in fake.state.calls[before_replay:]]
            self.assertNotIn("createModel", replay_actions)
            self.assertNotIn("createDeck", replay_actions)

    def test_existing_model_conflict_fails_before_any_mutation(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            fake.state.models["RQC Basic v1"] = ["Front", "Back"]
            fake.state.model_templates["RQC Basic v1"] = {"Card 1": {"Front": "wrong", "Back": "wrong"}}
            fake.state.model_css["RQC Basic v1"] = "wrong"
            job = build_initialize_job(Path(temp) / "conflict", fake.endpoint)
            run = make_exporter(job)
            self.assertEqual("BLOCKED_ENVIRONMENT", run.run())
            actions = [item["action"] for item in fake.state.calls]
            self.assertNotIn("createModel", actions)
            self.assertNotIn("createDeck", actions)
            result = json.loads(run.writer.result_path.read_text(encoding="utf-8"))
            self.assertEqual("failed", result["status"])
            self.assertEqual("MODEL_SCHEMA_CONFLICT", result["error"]["code"])
            self.assertEqual("none", result["sideEffects"][0]["state"])

    def test_response_loss_is_unknown_non_retryable_and_explicit_replay_converges(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            fake.state.fail_after_create_model_once = True
            first_job = build_initialize_job(Path(temp) / "lost-response", fake.endpoint)
            first = make_exporter(first_job)
            self.assertEqual("FAILED", first.run())
            result = json.loads(first.writer.result_path.read_text(encoding="utf-8"))
            validate_common(first.writer.result_path, "worker-result.schema.json")
            self.assertEqual("unknown_external_state", result["status"])
            self.assertFalse(result["error"]["retryable"])
            self.assertEqual("unknown", result["sideEffects"][0]["state"])
            self.assertIn("RQC Basic v1", fake.state.models)

            before_replay = len(fake.state.calls)
            replay_job = build_initialize_job(Path(temp) / "explicit-reconcile", fake.endpoint)
            replay = make_exporter(replay_job)
            self.assertEqual("SUCCEEDED", replay.run())
            replay_actions = [item["action"] for item in fake.state.calls[before_replay:]]
            self.assertNotIn("createModel", replay_actions)
            self.assertEqual(1, replay_actions.count("createDeck"))
            replay_status = json.loads((replay.writer.output_root / "initialization-status.json").read_text(encoding="utf-8"))
            self.assertEqual("initialized", replay_status["status"])

    def test_initialize_cli_alias_requires_initialize_request(self):
        with tempfile.TemporaryDirectory() as temp, FakeAnkiServer() as fake:
            root = Path(temp) / "cli"
            job = build_initialize_job(root, fake.endpoint)
            self.assertEqual(0, exporter.main([exporter.INITIALIZE_OPERATION, "--request", str((root / job["request"]).resolve())]))
            result = json.loads((root / json.loads((root / job["request"]).read_text(encoding="utf-8"))["resultPath"]).read_text(encoding="utf-8"))
            self.assertEqual("completed", result["status"])


if __name__ == "__main__":
    unittest.main()
