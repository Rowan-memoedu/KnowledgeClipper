from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import jsonschema


WORKER_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(WORKER_ROOT))

import image_ocr_settings_adapter as adapter


SETTINGS_SCHEMA = WORKER_ROOT / "schemas" / "image-ocr-settings.v1.schema.json"
RESULT_SCHEMA = WORKER_ROOT / "schemas" / "image-ocr-settings-adapter-result.v1.schema.json"
DESCRIPTOR_SCHEMA = WORKER_ROOT / "schemas" / "image-ocr-capability-config-descriptor.v6.schema.json"
STATUS_SCHEMA = WORKER_ROOT / "schemas" / "image-ocr-capability-status.v4.schema.json"
TEST_RESULT_SCHEMA = WORKER_ROOT / "schemas" / "image-ocr-companion-test-result.v4.schema.json"
VALIDATE_RESULT_SCHEMA = WORKER_ROOT / "schemas" / "image-ocr-companion-validate-result.v4.schema.json"
OCR_FIXTURE = WORKER_ROOT / "fixtures" / "settings-ocr.v1.json"
ORIGINAL_FIXTURE = WORKER_ROOT / "fixtures" / "settings-original.v1.json"
READABLE_EXAMPLE = WORKER_ROOT / "fixtures" / "settings-adapter-readable-example.v2.json"
CREDENTIAL_METADATA = WORKER_ROOT / "fixtures" / "credential-metadata-dpapi-verified.v1.json"
CREDENTIAL_METADATA_SCHEMA = WORKER_ROOT / "schemas" / "image-ocr-credential-metadata.v1.schema.json"


def load_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


class ImageOcrSettingsAdapterTests(unittest.TestCase):
    def validate(self, value, schema_path: Path):
        schema = load_json(schema_path)
        resolver = jsonschema.RefResolver(base_uri=schema_path.parent.as_uri() + "/", referrer=schema)
        jsonschema.Draft202012Validator(schema, resolver=resolver).validate(value)

    def test_descriptor_is_schema_valid_layered_and_destination_neutral(self):
        descriptor = adapter.capability_descriptor()
        self.validate(descriptor, DESCRIPTOR_SCHEMA)
        capabilities = {item["capabilityId"]: item for item in descriptor["capabilities"]}
        self.assertEqual("common", capabilities["image.processing.mode"]["display"]["tier"])
        self.assertEqual("advanced", capabilities["image.ocr.model-version"]["display"]["tier"])
        self.assertEqual("developer-diagnostics", capabilities["image.ocr.api-token-reference"]["display"]["tier"])
        self.assertTrue(capabilities["image.ocr.api-token-reference"]["snapshotBinding"]["excludedFromSnapshot"])
        self.assertEqual("secret-value-prohibited", capabilities["image.ocr.api-token-reference"]["sensitivity"])
        self.assertTrue(all(item["statusProbe"] is not None for item in descriptor["capabilities"]))
        actions = {item["actionId"]: item for item in descriptor["actions"]}
        self.assertEqual("prohibited", actions[adapter.OFFLINE_READINESS_ACTION_ID]["networkAccess"])
        self.assertFalse(actions[adapter.VALIDATE_ONLY_ACTION_ID]["sideEffects"]["externalUpload"])
        self.assertFalse(actions[adapter.OFFLINE_FAKE_PROVIDER_ACTION_ID]["sideEffects"]["externalUpload"])
        live = actions[adapter.LIVE_CONNECTIVITY_ACTION_ID]
        self.assertEqual("explicit-user-action-only", live["executionPolicy"])
        self.assertTrue(live["sideEffects"]["externalUpload"])
        self.assertTrue(live["sideEffects"]["requiresConsent"])
        self.assertEqual("unsupported", descriptor["readOnlyFacts"]["retention"]["executorState"])
        self.assertFalse(descriptor["readOnlyFacts"]["retention"]["automaticDeletionClaim"])
        self.assertEqual("1.3.1", descriptor["companionVersion"])
        self.assertEqual(">=1.2.4 <2.0.0", descriptor["workerBinding"]["workerVersionRange"])
        serialized = json.dumps(descriptor, ensure_ascii=False).lower()
        self.assertNotIn("remnote", serialized)
        self.assertNotIn("anki", serialized)

    def test_ocr_fixture_projects_mode_provider_settings_path_and_reference_only(self):
        settings = load_json(OCR_FIXTURE)
        self.validate(settings, SETTINGS_SCHEMA)
        result = adapter.adapt_settings(settings)
        self.validate(result, RESULT_SCHEMA)
        expected = load_json(READABLE_EXAMPLE)
        projection = expected["expectedTaskProjection"]
        self.assertEqual(projection["captureBundle.modeSnapshot.imageHandling"], result["captureModeSnapshot"]["imageHandling"])
        self.assertEqual(projection["workerRequest.configSnapshot.providerSettings"], result["workerRequestProjection"]["configSnapshot"]["providerSettings"])
        self.assertEqual(projection["workerRequest.configSnapshot.mineruScriptPath"], result["workerRequestProjection"]["configSnapshot"]["mineruScriptPath"])
        self.assertEqual(projection["derived.cloudUploadAuthorized"], result["workerRequestProjection"]["derivedInternalRequest"]["cloudUploadAuthorized"])
        self.assertFalse(result["runtimeSecretBinding"]["valuePersisted"])
        self.assertNotIn("tokenValue", json.dumps(result))
        self.assertEqual("available", result["status"]["coreSettingsControlPlaneBinding"]["state"])
        self.assertEqual("unsupported", result["status"]["originalFullPipeline"]["state"])
        self.assertEqual("available", result["status"]["partialFailedOnlyAutomaticContinuation"]["state"])
        self.assertEqual("unsupported", result["status"]["retentionExecution"]["state"])

    def test_original_omits_provider_snapshot_and_never_resolves_secret(self):
        settings = load_json(ORIGINAL_FIXTURE)
        result = adapter.adapt_settings(settings)
        self.assertEqual("original", result["captureModeSnapshot"]["imageHandling"])
        self.assertEqual({}, result["workerRequestProjection"]["configSnapshot"])
        self.assertEqual("original_passthrough", result["workerRequestProjection"]["derivedInternalRequest"]["mode"])
        self.assertFalse(result["workerRequestProjection"]["derivedInternalRequest"]["cloudUploadAuthorized"])
        calls = []
        environment = adapter.resolve_runtime_environment(settings, lambda ref: calls.append(ref) or "SHOULD_NOT_BE_USED")
        self.assertEqual({}, environment)
        self.assertEqual([], calls)

    def test_ocr_secret_is_resolved_only_to_process_environment_and_never_serialized(self):
        settings = load_json(OCR_FIXTURE)
        fake_token = "FAKE_MINERU_TOKEN_FOR_PROCESS_ONLY_123456"
        calls = []
        environment = adapter.resolve_runtime_environment(settings, lambda ref: calls.append(ref) or fake_token)
        self.assertEqual(["local-credential://image-ocr/mineru-api-token"], calls)
        self.assertEqual({"MINERU_API_TOKEN": fake_token}, environment)
        result_text = json.dumps(adapter.adapt_settings(settings), ensure_ascii=False)
        self.assertNotIn(fake_token, result_text)

    def test_realtime_status_is_separate_safe_and_reports_real_local_readiness(self):
        self.validate(load_json(CREDENTIAL_METADATA), CREDENTIAL_METADATA_SCHEMA)
        with tempfile.TemporaryDirectory() as temporary:
            script = Path(temporary) / "fake_provider.py"
            script.write_text("# local fake provider\n", encoding="utf-8")
            settings = load_json(OCR_FIXTURE)
            settings["deployment"]["mineruScriptPath"] = str(script.resolve())
            status = adapter.probe_status(settings, load_json(CREDENTIAL_METADATA))
            self.validate(status, STATUS_SCHEMA)
            self.assertEqual("ready", status["checks"]["offlineReadiness"]["state"])
            self.assertEqual("available", status["checks"]["providerScript"]["state"])
            self.assertEqual(
                {"configured": True, "verified": True, "sourceType": "desktop-dpapi-current-user"},
                status["checks"]["credential"],
            )
            self.assertEqual("not-run", status["checks"]["liveConnectivity"]["state"])
            self.assertFalse(status["safety"]["networkAccessPerformed"])
            self.assertTrue(status["safety"]["secretValueProhibited"])

        missing = load_json(OCR_FIXTURE)
        missing["deployment"]["mineruScriptPath"] = str((Path(temporary) / "missing.py").resolve())
        missing["credential"] = {}
        missing_status = adapter.probe_status(missing)
        self.assertEqual("not-ready", missing_status["checks"]["offlineReadiness"]["state"])
        self.assertEqual("unavailable", missing_status["checks"]["providerScript"]["state"])
        self.assertEqual(
            {"configured": False, "verified": False, "sourceType": "not-configured"},
            missing_status["checks"]["credential"],
        )

        original = adapter.probe_status(load_json(ORIGINAL_FIXTURE))
        self.assertEqual("ready", original["checks"]["offlineReadiness"]["state"])
        self.assertEqual("not-required", original["checks"]["providerScript"]["state"])
        self.assertEqual("unsupported", original["checks"]["coreIntegration"]["originalFullPipeline"])
        self.assertEqual("unsupported", original["checks"]["retentionExecution"]["state"])

    def test_credential_metadata_gate_rejects_secret_fields_and_invalid_verified_state(self):
        invalid = [
            {
                "schemaVersion": adapter.CREDENTIAL_METADATA_VERSION,
                "credentialId": adapter.CANONICAL_CREDENTIAL_ID,
                "configured": True,
                "verified": True,
                "sourceType": "desktop-dpapi-current-user",
                "token": "secret-prohibited",
            },
            {
                "schemaVersion": adapter.CREDENTIAL_METADATA_VERSION,
                "credentialId": adapter.CANONICAL_CREDENTIAL_ID,
                "configured": False,
                "verified": True,
                "sourceType": "desktop-dpapi-current-user",
            },
            {
                "schemaVersion": adapter.CREDENTIAL_METADATA_VERSION,
                "credentialId": "local-credential://wrong/id",
                "configured": True,
                "verified": True,
                "sourceType": "desktop-dpapi-current-user",
            },
        ]
        for value in invalid:
            with self.subTest(value=value):
                with self.assertRaises(adapter.SettingsValidationError):
                    adapter.normalize_credential_metadata(value)

    def test_offline_fake_provider_action_consumes_formal_snapshot_without_network(self):
        from image_ocr_companion_diagnostics import run_fake_provider_test

        settings = load_json(OCR_FIXTURE)
        result = run_fake_provider_test(adapter.normalize_settings(settings), load_json(CREDENTIAL_METADATA))
        self.validate(result, TEST_RESULT_SCHEMA)
        self.assertEqual("passed", result["status"])
        self.assertTrue(result["formalWorkerRequest"]["absoluteExistingRequestUsed"])
        self.assertEqual(
            settings["providerSettings"],
            result["fakeProvider"]["observedProviderSettings"],
        )
        self.assertTrue(result["fakeProvider"]["exactMatch"])
        self.assertTrue(result["processInheritance"]["workerObservedInjectedCredential"])
        self.assertEqual(0, result["leakScan"]["taskPackageLogReceiptSecretOccurrences"])
        self.assertFalse(result["safety"]["externalUpload"])
        self.assertFalse(result["safety"]["mineruServiceCalled"])

    def test_validate_only_projects_mode_and_provider_settings_without_provider_or_secret(self):
        settings = load_json(OCR_FIXTURE)
        result = adapter.validate_only(settings)
        self.validate(result, VALIDATE_RESULT_SCHEMA)
        self.assertEqual("passed", result["status"])
        self.assertEqual("ocr", result["captureModeSnapshot"]["imageHandling"])
        self.assertEqual(settings["providerSettings"], result["workerRequestProjection"]["configSnapshot"]["providerSettings"])
        self.assertEqual("local-credential://image-ocr/mineru-api-token", result["credentialReference"]["reference"])
        self.assertFalse(result["safety"]["providerInvoked"])
        self.assertFalse(result["safety"]["credentialResolved"])
        self.assertNotIn("MINERU_API_TOKEN", json.dumps(result))

        original = adapter.validate_only(load_json(ORIGINAL_FIXTURE))
        self.assertEqual("original", original["captureModeSnapshot"]["imageHandling"])
        self.assertEqual({}, original["workerRequestProjection"]["configSnapshot"])
        self.assertFalse(original["workerRequestProjection"]["derivedInternalRequest"]["cloudUploadAuthorized"])

    def test_strict_validation_rejects_wrong_types_ranges_unknown_keys_and_raw_secret(self):
        invalid = [
            {"schemaVersion": adapter.SCHEMA_VERSION, "mode": "ocr", "providerSettings": {"modelVersion": "other"}},
            {"schemaVersion": adapter.SCHEMA_VERSION, "mode": "ocr", "providerSettings": {"language": ""}},
            {"schemaVersion": adapter.SCHEMA_VERSION, "mode": "ocr", "providerSettings": {"enableTable": 1}},
            {"schemaVersion": adapter.SCHEMA_VERSION, "mode": "ocr", "providerSettings": {"enableFormula": "true"}},
            {"schemaVersion": adapter.SCHEMA_VERSION, "mode": "ocr", "providerSettings": {"timeoutSeconds": 29}},
            {"schemaVersion": adapter.SCHEMA_VERSION, "mode": "ocr", "providerSettings": {"timeoutSeconds": 3601}},
            {"schemaVersion": adapter.SCHEMA_VERSION, "mode": "ocr", "providerSettings": {"pollIntervalSeconds": 0}},
            {"schemaVersion": adapter.SCHEMA_VERSION, "mode": "ocr", "providerSettings": {"pollIntervalSeconds": 61}},
            {"schemaVersion": adapter.SCHEMA_VERSION, "mode": "ocr", "providerSettings": {"timeoutSeconds": 30, "pollIntervalSeconds": 30}},
            {"schemaVersion": adapter.SCHEMA_VERSION, "mode": "ocr", "credential": {"mineruToken": "RAW_SECRET_MUST_BE_REJECTED"}},
            {"schemaVersion": adapter.SCHEMA_VERSION, "mode": "ocr", "deployment": {"mineruScriptPath": "relative.py"}},
        ]
        for value in invalid:
            with self.subTest(value=value):
                with self.assertRaises(adapter.SettingsValidationError):
                    adapter.adapt_settings(value)

    def test_cli_describe_and_adapt_emit_single_json_without_secret_value(self):
        describe = subprocess.run(
            [sys.executable, "-B", str(WORKER_ROOT / "image_ocr_settings_adapter.py"), "describe", "--json"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=10,
            check=False,
        )
        self.assertEqual(0, describe.returncode, describe.stderr)
        self.assertEqual(1, len(describe.stdout.splitlines()))
        self.validate(json.loads(describe.stdout), DESCRIPTOR_SCHEMA)
        adapt = subprocess.run(
            [sys.executable, "-B", str(WORKER_ROOT / "image_ocr_settings_adapter.py"), "adapt", "--settings", str(OCR_FIXTURE.resolve())],
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=10,
            check=False,
        )
        self.assertEqual(0, adapt.returncode, adapt.stderr)
        self.assertEqual(1, len(adapt.stdout.splitlines()))
        self.validate(json.loads(adapt.stdout), RESULT_SCHEMA)

        status = subprocess.run(
            [sys.executable, "-B", str(WORKER_ROOT / "image_ocr_settings_adapter.py"), "status", "--settings", str(OCR_FIXTURE.resolve()), "--credential-metadata", str(CREDENTIAL_METADATA.resolve()), "--json"],
            capture_output=True, text=True, encoding="utf-8", timeout=10, check=False,
        )
        self.assertEqual(0, status.returncode, status.stderr)
        self.assertEqual(1, len(status.stdout.splitlines()))
        self.validate(json.loads(status.stdout), STATUS_SCHEMA)

        validate = subprocess.run(
            [sys.executable, "-B", str(WORKER_ROOT / "image_ocr_settings_adapter.py"), "validate", "--settings", str(OCR_FIXTURE.resolve()), "--json"],
            capture_output=True, text=True, encoding="utf-8", timeout=10, check=False,
        )
        self.assertEqual(0, validate.returncode, validate.stderr)
        self.assertEqual(1, len(validate.stdout.splitlines()))
        self.validate(json.loads(validate.stdout), VALIDATE_RESULT_SCHEMA)

        test = subprocess.run(
            [
                sys.executable, "-B", str(WORKER_ROOT / "image_ocr_settings_adapter.py"), "test",
                "--settings", str(OCR_FIXTURE.resolve()), "--credential-metadata", str(CREDENTIAL_METADATA.resolve()),
                "--action", adapter.OFFLINE_FAKE_PROVIDER_ACTION_ID, "--json",
            ],
            capture_output=True, text=True, encoding="utf-8", timeout=20, check=False,
        )
        self.assertEqual(0, test.returncode, test.stderr)
        self.assertEqual(1, len(test.stdout.splitlines()))
        self.validate(json.loads(test.stdout), TEST_RESULT_SCHEMA)
        self.assertNotIn("live-connectivity", " ".join(test.args))


if __name__ == "__main__":
    unittest.main()
