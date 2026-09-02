#!/usr/bin/env python3
"""Module-local settings adapter for the KnowledgeClipper image/OCR worker.

This adapter projects validated project settings into immutable capture and
worker-request snapshots.  It never resolves or serializes credential values;
the launcher may resolve a local credential reference immediately before
starting the worker and inject it only through the child process environment.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import re
import sys
from pathlib import Path, PureWindowsPath
from typing import Any, Callable


SCHEMA_VERSION = "image-ocr-settings.v1"
ADAPTER_RESULT_VERSION = "image-ocr-settings-adapter-result.v1"
DESCRIPTOR_VERSION = "image-ocr-capability-config-descriptor.v6"
STATUS_VERSION = "image-ocr-capability-status.v4"
TEST_RESULT_VERSION = "image-ocr-companion-test-result.v4"
VALIDATE_RESULT_VERSION = "image-ocr-companion-validate-result.v4"
COMPANION_VERSION = "1.3.1"
WORKER_ID = "image.ocr.mineru"
WORKER_VERSION = "1.2.4"
PROVIDER = "mineru"
CANONICAL_CREDENTIAL_ID = "local-credential://image-ocr/mineru-api-token"
CREDENTIAL_METADATA_VERSION = "image-ocr-credential-metadata.v1"
CREDENTIAL_SOURCE_TYPES = (
    "desktop-dpapi-current-user",
    "desktop-dpapi-current-user-backup",
    "desktop-dpapi-current-user-migrated",
    "not-probed",
    "not-configured",
)

OFFLINE_READINESS_ACTION_ID = "image.ocr.status.offline-readiness"
VALIDATE_ONLY_ACTION_ID = "image.ocr.test.validate-only"
OFFLINE_FAKE_PROVIDER_ACTION_ID = "image.ocr.test.offline-fake-provider"
LIVE_CONNECTIVITY_ACTION_ID = "image.ocr.test.live-connectivity"

MODEL_VERSIONS = ("vlm", "pipeline", "MinerU-HTML")
DEFAULT_PROVIDER_SETTINGS: dict[str, Any] = {
    "modelVersion": "vlm",
    "language": "ch",
    "enableTable": True,
    "enableFormula": True,
    "timeoutSeconds": 900,
    "pollIntervalSeconds": 5,
}
PROVIDER_TIMEOUT_RANGE = (30, 3600)
POLL_INTERVAL_RANGE = (1, 60)
LANGUAGE_PATTERN = re.compile(r"^[A-Za-z][A-Za-z0-9_-]{0,31}$")
CREDENTIAL_REF_PATTERN = re.compile(r"^local-credential://[A-Za-z0-9][A-Za-z0-9._/-]{0,223}$")


class SettingsValidationError(ValueError):
    """A stable, user-actionable settings validation failure."""


def _expect_object(value: Any, field: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise SettingsValidationError(f"{field} must be an object")
    return value


def _reject_unknown(value: dict[str, Any], allowed: set[str], field: str) -> None:
    unknown = sorted(set(value) - allowed)
    if unknown:
        raise SettingsValidationError(f"{field} contains unsupported fields: {', '.join(unknown)}")


def normalize_provider_settings(value: Any) -> dict[str, Any]:
    settings = _expect_object(value, "providerSettings")
    allowed = set(DEFAULT_PROVIDER_SETTINGS)
    _reject_unknown(settings, allowed, "providerSettings")
    normalized = dict(DEFAULT_PROVIDER_SETTINGS)
    normalized.update(settings)

    model = normalized["modelVersion"]
    if not isinstance(model, str) or model not in MODEL_VERSIONS:
        raise SettingsValidationError(f"providerSettings.modelVersion must be one of: {', '.join(MODEL_VERSIONS)}")

    language = normalized["language"]
    if not isinstance(language, str) or not LANGUAGE_PATTERN.fullmatch(language):
        raise SettingsValidationError("providerSettings.language must be a 1-32 character MinerU language code")

    for field in ("enableTable", "enableFormula"):
        if type(normalized[field]) is not bool:
            raise SettingsValidationError(f"providerSettings.{field} must be a boolean")

    timeout = normalized["timeoutSeconds"]
    if type(timeout) is not int or not PROVIDER_TIMEOUT_RANGE[0] <= timeout <= PROVIDER_TIMEOUT_RANGE[1]:
        raise SettingsValidationError(
            f"providerSettings.timeoutSeconds must be an integer between {PROVIDER_TIMEOUT_RANGE[0]} and {PROVIDER_TIMEOUT_RANGE[1]}"
        )

    poll = normalized["pollIntervalSeconds"]
    if type(poll) is not int or not POLL_INTERVAL_RANGE[0] <= poll <= POLL_INTERVAL_RANGE[1]:
        raise SettingsValidationError(
            f"providerSettings.pollIntervalSeconds must be an integer between {POLL_INTERVAL_RANGE[0]} and {POLL_INTERVAL_RANGE[1]}"
        )
    if poll >= timeout:
        raise SettingsValidationError("providerSettings.pollIntervalSeconds must be smaller than timeoutSeconds")
    return normalized


def _absolute_path(value: Any, field: str) -> str:
    if not isinstance(value, str) or not value.strip() or value != value.strip():
        raise SettingsValidationError(f"{field} must be a non-empty absolute path")
    if not (Path(value).is_absolute() or PureWindowsPath(value).is_absolute()):
        raise SettingsValidationError(f"{field} must be an absolute path")
    return value


def normalize_settings(value: Any) -> dict[str, Any]:
    settings = _expect_object(value, "settings")
    _reject_unknown(settings, {"schemaVersion", "mode", "providerSettings", "deployment", "credential"}, "settings")
    if settings.get("schemaVersion") != SCHEMA_VERSION:
        raise SettingsValidationError(f"settings.schemaVersion must be {SCHEMA_VERSION}")
    mode = settings.get("mode")
    if mode not in {"ocr", "original"}:
        raise SettingsValidationError("settings.mode must be ocr or original")

    deployment = _expect_object(settings.get("deployment", {}), "settings.deployment")
    _reject_unknown(deployment, {"mineruScriptPath"}, "settings.deployment")
    script_path = deployment.get("mineruScriptPath")
    if script_path is not None:
        script_path = _absolute_path(script_path, "settings.deployment.mineruScriptPath")

    credential = _expect_object(settings.get("credential", {}), "settings.credential")
    _reject_unknown(credential, {"mineruTokenRef"}, "settings.credential")
    credential_ref = credential.get("mineruTokenRef")
    if credential_ref is not None:
        if not isinstance(credential_ref, str) or not CREDENTIAL_REF_PATTERN.fullmatch(credential_ref):
            raise SettingsValidationError(
                "settings.credential.mineruTokenRef must be a local-credential:// reference, never a token value"
            )
        if credential_ref != CANONICAL_CREDENTIAL_ID:
            raise SettingsValidationError(f"settings.credential.mineruTokenRef must be {CANONICAL_CREDENTIAL_ID}")

    return {
        "schemaVersion": SCHEMA_VERSION,
        "mode": mode,
        "providerSettings": normalize_provider_settings(settings.get("providerSettings", {})),
        "deployment": {"mineruScriptPath": script_path} if script_path is not None else {},
        "credential": {"mineruTokenRef": credential_ref} if credential_ref is not None else {},
    }


def normalize_credential_metadata(value: Any) -> dict[str, Any]:
    metadata = _expect_object(value, "credentialMetadata")
    _reject_unknown(metadata, {"schemaVersion", "credentialId", "configured", "verified", "sourceType"}, "credentialMetadata")
    if metadata.get("schemaVersion") != CREDENTIAL_METADATA_VERSION:
        raise SettingsValidationError(f"credentialMetadata.schemaVersion must be {CREDENTIAL_METADATA_VERSION}")
    if metadata.get("credentialId") != CANONICAL_CREDENTIAL_ID:
        raise SettingsValidationError(f"credentialMetadata.credentialId must be {CANONICAL_CREDENTIAL_ID}")
    configured = metadata.get("configured")
    verified = metadata.get("verified")
    source_type = metadata.get("sourceType")
    if type(configured) is not bool or type(verified) is not bool:
        raise SettingsValidationError("credentialMetadata configured/verified must be booleans")
    if source_type not in CREDENTIAL_SOURCE_TYPES:
        raise SettingsValidationError("credentialMetadata.sourceType is unsupported")
    if verified and not configured:
        raise SettingsValidationError("credentialMetadata.verified requires configured=true")
    if source_type.startswith("desktop-dpapi-") and not configured:
        raise SettingsValidationError("desktop DPAPI sourceType requires configured=true")
    if verified and not source_type.startswith("desktop-dpapi-"):
        raise SettingsValidationError("verified=true requires a Desktop DPAPI sourceType")
    return {
        "schemaVersion": CREDENTIAL_METADATA_VERSION,
        "credentialId": CANONICAL_CREDENTIAL_ID,
        "configured": configured,
        "verified": verified,
        "sourceType": source_type,
    }


def _path_status(path: str | None) -> dict[str, Any]:
    if path is None:
        return {"state": "auto-discovery", "configured": False, "exists": None}
    return {"state": "available" if os.path.isfile(path) else "unavailable", "configured": True, "exists": os.path.isfile(path)}


def adapt_settings(value: Any) -> dict[str, Any]:
    normalized = normalize_settings(value)
    mode = normalized["mode"]
    ocr = mode == "ocr"
    script_path = normalized["deployment"].get("mineruScriptPath")
    credential_ref = normalized["credential"].get("mineruTokenRef")

    config_snapshot: dict[str, Any] = {}
    if ocr:
        config_snapshot["providerSettings"] = normalized["providerSettings"]
        if script_path is not None:
            config_snapshot["mineruScriptPath"] = script_path

    return {
        "schemaVersion": ADAPTER_RESULT_VERSION,
        "normalizedSettings": normalized,
        "captureModeSnapshot": {"imageHandling": mode},
        "workerRequestProjection": {
            "workerId": WORKER_ID,
            "operation": "process-images",
            "configSnapshot": config_snapshot,
            "derivedInternalRequest": {
                "mode": "ocr_mineru" if ocr else "original_passthrough",
                "cloudUploadAuthorized": ocr,
            },
        },
        "runtimeSecretBinding": {
            "configured": credential_ref is not None,
            "requiredForSelectedMode": ocr,
            "injectForSelectedMode": ocr and credential_ref is not None,
            "reference": credential_ref,
            "environmentVariable": "MINERU_API_TOKEN",
            "valuePersisted": False,
        },
        "readOnlyFacts": {
            "provider": PROVIDER,
            "workerId": WORKER_ID,
            "workerVersion": WORKER_VERSION,
            "scriptPath": _path_status(script_path),
            "limits": {
                "maxImagesPerOcrRequest": 50,
                "maxZipFiles": 20000,
                "maxZipUncompressedBytes": 536870912,
            },
            "retention": {
                "requestMetadataDays": 30,
                "executorState": "unsupported",
                "automaticDeletionClaim": False,
            },
            "coreStage": {
                "timeoutMs": 600000,
                "retryPolicy": {"maxAttempts": 2, "backoffMs": 1000},
                "authority": "shared-core-read-only-observation",
            },
        },
        "status": {
            "settingsProjection": {"state": "available"},
            "coreSettingsControlPlaneBinding": {
                "state": "available",
                "reason": "core_v2_5_settings_snapshot_and_worker_request_projection_verified",
            },
            "originalFullPipeline": {
                "state": "unsupported",
                "reason": "core_original_terminal_unsupported",
            },
            "partialFailedOnlyAutomaticContinuation": {
                "state": "available",
                "reason": "core_v2_5_failed_items_only_hash_reuse_verified",
            },
            "retentionExecution": {
                "state": "unsupported",
                "reason": "lifecycle_executor_not_connected",
            },
        },
    }


def resolve_runtime_environment(value: Any, resolver: Callable[[str], str]) -> dict[str, str]:
    """Resolve the selected OCR credential for process-only worker injection."""
    normalized = normalize_settings(value)
    if normalized["mode"] == "original":
        return {}
    reference = normalized["credential"].get("mineruTokenRef")
    if reference is None:
        raise SettingsValidationError("OCR mode requires a configured local MinerU credential reference")
    token = resolver(reference)
    if not isinstance(token, str) or len(token.strip()) < 20:
        raise SettingsValidationError("Resolved MinerU credential is unavailable or invalid")
    return {"MINERU_API_TOKEN": token.strip()}


def _worker_binding() -> dict[str, str]:
    return {
        "workerId": WORKER_ID,
        "workerVersionRange": ">=1.2.4 <2.0.0",
        "contractVersionRange": ">=1.0.0 <2.0.0",
    }


def _probe_reference(action_id: str, trigger: str = "manual") -> dict[str, str]:
    return {"actionId": action_id, "trigger": trigger, "resultJsonPointer": ""}


def validate_only(value: Any) -> dict[str, Any]:
    """Validate settings and freeze their exact capture/request projection without side effects."""
    projection = adapt_settings(value)
    secret = projection["runtimeSecretBinding"]
    return {
        "schemaVersion": VALIDATE_RESULT_VERSION,
        "actionId": VALIDATE_ONLY_ACTION_ID,
        "status": "passed",
        "worker": {"id": WORKER_ID, "version": WORKER_VERSION},
        "captureModeSnapshot": projection["captureModeSnapshot"],
        "workerRequestProjection": projection["workerRequestProjection"],
        "credentialReference": {
            "configured": secret["configured"],
            "reference": secret["reference"],
            "requiredForSelectedMode": secret["requiredForSelectedMode"],
            "secretValueProhibited": True,
        },
        "validation": {
            "providerSettingsEnumTypeRange": "passed",
            "cloudUploadAuthorizationDerivedFromMode": True,
            "unknownFieldsRejected": True,
        },
        "safety": {
            "networkAccessPerformed": False,
            "externalUploadPerformed": False,
            "providerInvoked": False,
            "credentialResolved": False,
            "secretValuePersisted": False,
        },
    }


def companion_actions() -> list[dict[str, Any]]:
    no_side_effects = {"externalUpload": False, "localWrite": False, "requiresConsent": False}
    return [
        {
            "actionId": OFFLINE_READINESS_ACTION_ID,
            "title": "离线就绪状态",
            "kind": "status-probe",
            "availability": {"state": "available"},
            "invocation": {
                "protocol": "command-json-v1",
                "executable": "KnowledgeClipper.ImageOcr.Settings.exe",
                "argumentsTemplate": [
                    "status", "--settings", "{absoluteSettingsPath}",
                    "--credential-metadata", "{absoluteCredentialMetadataPath}", "--json"
                ],
            },
            "networkAccess": "prohibited",
            "executionPolicy": "safe-read-only",
            "resultSchemaVersion": STATUS_VERSION,
            "sideEffects": no_side_effects,
            "secretValueProhibited": True,
        },
        {
            "actionId": VALIDATE_ONLY_ACTION_ID,
            "title": "设置与快照 Validate-only",
            "kind": "diagnostic-test",
            "availability": {"state": "available"},
            "invocation": {
                "protocol": "command-json-v1",
                "executable": "KnowledgeClipper.ImageOcr.Settings.exe",
                "argumentsTemplate": ["validate", "--settings", "{absoluteSettingsPath}", "--json"],
            },
            "networkAccess": "prohibited",
            "executionPolicy": "safe-read-only",
            "resultSchemaVersion": VALIDATE_RESULT_VERSION,
            "sideEffects": no_side_effects,
            "secretValueProhibited": True,
        },
        {
            "actionId": OFFLINE_FAKE_PROVIDER_ACTION_ID,
            "title": "Fake Provider 离线测试",
            "kind": "diagnostic-test",
            "availability": {"state": "available", "requiresMode": "ocr"},
            "invocation": {
                "protocol": "command-json-v1",
                "executable": "KnowledgeClipper.ImageOcr.Settings.exe",
                "argumentsTemplate": [
                    "test", "--settings", "{absoluteSettingsPath}",
                    "--credential-metadata", "{absoluteCredentialMetadataPath}",
                    "--action", OFFLINE_FAKE_PROVIDER_ACTION_ID, "--json"
                ],
            },
            "networkAccess": "prohibited",
            "executionPolicy": "explicit-safe-action",
            "resultSchemaVersion": TEST_RESULT_VERSION,
            "sideEffects": no_side_effects,
            "secretValueProhibited": True,
        },
        {
            "actionId": LIVE_CONNECTIVITY_ACTION_ID,
            "title": "MinerU 实时连接测试",
            "kind": "live-connectivity-test",
            "availability": {
                "state": "conditional",
                "reason": "external_upload_action_requires_explicit_launcher_and_user_consent",
            },
            "invocation": {
                "protocol": "delegated-external-upload-v1",
                "executable": None,
                "argumentsTemplate": [],
            },
            "networkAccess": "required",
            "executionPolicy": "explicit-user-action-only",
            "resultSchemaVersion": TEST_RESULT_VERSION,
            "sideEffects": {"externalUpload": True, "localWrite": True, "requiresConsent": True},
            "secretValueProhibited": True,
        },
    ]


def probe_status(value: Any, credential_metadata: Any | None = None) -> dict[str, Any]:
    """Return local readiness from value-free Desktop credential metadata."""
    normalized = normalize_settings(value)
    mode = normalized["mode"]
    script_path = normalized["deployment"].get("mineruScriptPath")
    credential_ref = normalized["credential"].get("mineruTokenRef")
    if credential_metadata is None:
        credential = {
            "configured": credential_ref == CANONICAL_CREDENTIAL_ID,
            "verified": False,
            "sourceType": "not-probed" if credential_ref == CANONICAL_CREDENTIAL_ID else "not-configured",
        }
    else:
        normalized_metadata = normalize_credential_metadata(credential_metadata)
        if credential_ref != CANONICAL_CREDENTIAL_ID and normalized_metadata["configured"]:
            raise SettingsValidationError("credential metadata cannot configure a credential omitted from settings")
        credential = {
            "configured": normalized_metadata["configured"],
            "verified": normalized_metadata["verified"],
            "sourceType": normalized_metadata["sourceType"],
        }

    if mode == "original":
        script = {"state": "not-required", "configured": script_path is not None, "path": script_path, "exists": None}
        readiness_state = "ready"
        readiness_reasons = ["original_mode_requires_no_provider_or_credential"]
    else:
        raw_script = _path_status(script_path)
        script_state = raw_script["state"] if script_path is not None else "auto-discovery-unverified"
        script = {**raw_script, "state": script_state, "path": script_path}
        readiness_reasons = []
        if script_state == "unavailable":
            readiness_reasons.append("configured_provider_script_missing")
        elif script_state == "auto-discovery-unverified":
            readiness_reasons.append("provider_script_auto_discovery_not_verified")
        if not credential["configured"]:
            readiness_reasons.append("credential_not_configured")
        elif not credential["verified"]:
            readiness_reasons.append("credential_not_verified_by_desktop_dpapi_store")
        if script_state == "available" and credential["verified"]:
            readiness_state = "ready"
            readiness_reasons = ["provider_script_and_desktop_dpapi_credential_verified"]
        elif script_state == "unavailable" or not credential["configured"]:
            readiness_state = "not-ready"
        else:
            readiness_state = "conditional"

    return {
        "schemaVersion": STATUS_VERSION,
        "moduleId": "image-ocr",
        "companionVersion": COMPANION_VERSION,
        "workerBinding": _worker_binding(),
        "observedAt": dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
        "selectedMode": mode,
        "effectiveProviderSettings": normalized["providerSettings"],
        "checks": {
            "settings": {"state": "valid"},
            "providerScript": script,
            "credential": credential,
            "offlineReadiness": {
                "state": readiness_state,
                "reasons": readiness_reasons,
                "actionId": OFFLINE_READINESS_ACTION_ID,
                "networkAccessPerformed": False,
            },
            "liveConnectivity": {
                "state": "not-run",
                "actionId": LIVE_CONNECTIVITY_ACTION_ID,
                "externalUpload": True,
                "requiresConsent": True,
            },
            "retentionExecution": {
                "state": "unsupported",
                "requestMetadataDays": 30,
                "automaticDeletionClaim": False,
            },
            "coreIntegration": {
                "originalFullPipeline": "unsupported",
                "partialFailedOnlyAutomaticContinuation": "available",
            },
        },
        "safety": {
            "networkAccessPerformed": False,
            "externalUploadPerformed": False,
            "credentialResolved": False,
            "credentialValueObserved": False,
            "secretValueProhibited": True,
        },
    }


def capability_descriptor() -> dict[str, Any]:
    base_binding = _worker_binding()
    readiness_probe = _probe_reference(OFFLINE_READINESS_ACTION_ID, "on-open-or-manual")

    def item(
        capability_id: str,
        title: str,
        tier: str,
        control_kind: str,
        scope: str,
        value_schema: dict[str, Any],
        effective_at: str,
        snapshot_binding: dict[str, Any] | None,
        *,
        default: Any = None,
        sensitivity: str = "public-config",
        availability: dict[str, Any] | None = None,
        side_effects: dict[str, bool] | None = None,
        status_probe: dict[str, str] | None = None,
    ) -> dict[str, Any]:
        result = {
            "capabilityId": capability_id,
            "moduleId": "image-ocr",
            "workerBinding": base_binding,
            "display": {"title": title, "tier": tier},
            "controlKind": control_kind,
            "scope": scope,
            "valueSchema": value_schema,
            "sensitivity": sensitivity,
            "effectiveAt": effective_at,
            "snapshotBinding": snapshot_binding,
            "availability": availability or {"state": "available"},
            "compatibility": {"legacyAliases": []},
            "sideEffects": side_effects or {"externalUpload": False, "localWrite": False, "requiresConsent": False},
            "statusProbe": status_probe,
        }
        if default is not None:
            result["defaultValue"] = default
        return result

    capabilities = [
        item(
            "image.processing.mode", "图片处理模式", "common", "selectable", "project",
            {"type": "string", "enum": ["ocr", "original"]}, "next-capture-session",
            {"target": "capture-bundle", "jsonPointer": "/modeSnapshot/imageHandling", "excludedFromSnapshot": False},
            default="ocr", side_effects={"externalUpload": True, "localWrite": True, "requiresConsent": True},
            status_probe=readiness_probe,
        ),
        item(
            "image.ocr.model-version", "OCR 模型", "advanced", "selectable", "project",
            {"type": "string", "enum": list(MODEL_VERSIONS)}, "next-task",
            {"target": "worker-request", "jsonPointer": "/configSnapshot/providerSettings/modelVersion", "excludedFromSnapshot": False},
            default="vlm", status_probe=readiness_probe,
        ),
        item(
            "image.ocr.language", "OCR 语言", "advanced", "selectable", "project",
            {"type": "string", "pattern": LANGUAGE_PATTERN.pattern, "minLength": 1, "maxLength": 32}, "next-task",
            {"target": "worker-request", "jsonPointer": "/configSnapshot/providerSettings/language", "excludedFromSnapshot": False},
            default="ch", status_probe=readiness_probe,
        ),
        item(
            "image.ocr.table-recognition", "表格识别", "advanced", "selectable", "project",
            {"type": "boolean"}, "next-task",
            {"target": "worker-request", "jsonPointer": "/configSnapshot/providerSettings/enableTable", "excludedFromSnapshot": False},
            default=True, status_probe=readiness_probe,
        ),
        item(
            "image.ocr.formula-recognition", "公式识别", "advanced", "selectable", "project",
            {"type": "boolean"}, "next-task",
            {"target": "worker-request", "jsonPointer": "/configSnapshot/providerSettings/enableFormula", "excludedFromSnapshot": False},
            default=True, status_probe=readiness_probe,
        ),
        item(
            "image.ocr.provider-timeout-seconds", "Provider 超时", "advanced", "selectable", "project",
            {"type": "integer", "minimum": PROVIDER_TIMEOUT_RANGE[0], "maximum": PROVIDER_TIMEOUT_RANGE[1]}, "next-attempt",
            {"target": "worker-request", "jsonPointer": "/configSnapshot/providerSettings/timeoutSeconds", "excludedFromSnapshot": False},
            default=900, status_probe=readiness_probe,
        ),
        item(
            "image.ocr.poll-interval-seconds", "轮询间隔", "advanced", "selectable", "project",
            {"type": "integer", "minimum": POLL_INTERVAL_RANGE[0], "maximum": POLL_INTERVAL_RANGE[1]}, "next-attempt",
            {"target": "worker-request", "jsonPointer": "/configSnapshot/providerSettings/pollIntervalSeconds", "excludedFromSnapshot": False},
            default=5, status_probe=readiness_probe,
        ),
        item(
            "image.ocr.mineru-script-path", "MinerU 脚本路径", "developer-diagnostics", "deployment-path", "deployment",
            {"type": "string", "minLength": 1}, "next-attempt",
            {"target": "worker-request", "jsonPointer": "/configSnapshot/mineruScriptPath", "excludedFromSnapshot": False},
            sensitivity="local-path", status_probe=readiness_probe,
        ),
        item(
            "image.ocr.api-token-reference", "MinerU 本地凭据引用", "developer-diagnostics", "secret-reference", "device",
            {"type": "string", "const": CANONICAL_CREDENTIAL_ID}, "process-start",
            {"target": "worker-process-environment", "jsonPointer": "/MINERU_API_TOKEN", "excludedFromSnapshot": True},
            sensitivity="secret-value-prohibited",
            side_effects={"externalUpload": True, "localWrite": False, "requiresConsent": True}, status_probe=readiness_probe,
        ),
        item(
            "image.ocr.credential-status", "MinerU 凭据状态", "developer-diagnostics", "read-only-status", "device",
            {
                "type": "object",
                "additionalProperties": False,
                "required": ["configured", "verified", "sourceType"],
                "properties": {
                    "configured": {"type": "boolean"},
                    "verified": {"type": "boolean"},
                    "sourceType": {"type": "string", "enum": list(CREDENTIAL_SOURCE_TYPES)},
                },
            },
            "immediate", None, sensitivity="secret-value-prohibited", status_probe=readiness_probe,
        ),
        item(
            "image.ocr.offline-readiness", "Image/OCR 离线就绪", "developer-diagnostics", "read-only-status", "device",
            {"type": "string", "enum": ["ready", "conditional", "not-ready"]}, "immediate", None,
            status_probe=readiness_probe,
        ),
        item(
            "image.ocr.validate-only", "设置与快照 Validate-only", "developer-diagnostics", "diagnostic-action", "device",
            {"type": "string", "enum": [VALIDATE_ONLY_ACTION_ID]}, "immediate", None,
            status_probe=_probe_reference(VALIDATE_ONLY_ACTION_ID),
        ),
        item(
            "image.ocr.offline-fake-provider-test", "Fake Provider 离线测试", "developer-diagnostics", "diagnostic-action", "device",
            {"type": "string", "enum": [OFFLINE_FAKE_PROVIDER_ACTION_ID]}, "immediate", None,
            status_probe=_probe_reference(OFFLINE_FAKE_PROVIDER_ACTION_ID),
        ),
        item(
            "image.ocr.live-connectivity-test", "MinerU 实时连接测试", "developer-diagnostics", "diagnostic-action", "device",
            {"type": "string", "enum": [LIVE_CONNECTIVITY_ACTION_ID]}, "immediate", None,
            availability={"state": "conditional", "reason": "explicit_external_upload_action_only"},
            side_effects={"externalUpload": True, "localWrite": True, "requiresConsent": True},
            status_probe=_probe_reference(LIVE_CONNECTIVITY_ACTION_ID),
        ),
        item(
            "image.ocr.retention-execution", "本地保留期执行状态", "developer-diagnostics", "read-only-status", "device",
            {"type": "string", "enum": ["unsupported"]}, "release-bind", None,
            availability={"state": "unsupported", "reason": "lifecycle_executor_not_connected"},
            status_probe=readiness_probe,
        ),
    ]
    return {
        "schemaVersion": DESCRIPTOR_VERSION,
        "moduleId": "image-ocr",
        "companionVersion": COMPANION_VERSION,
        "workerBinding": base_binding,
        "capabilities": capabilities,
        "actions": companion_actions(),
        "readOnlyFacts": {
            "provider": PROVIDER,
            "workerId": WORKER_ID,
            "workerVersion": WORKER_VERSION,
            "credentialBridge": {
                "credentialId": CANONICAL_CREDENTIAL_ID,
                "source": "desktop-dpapi-current-user",
                "workerInjection": "process-environment-inherited",
                "taskSnapshotContainsValue": False,
                "workerRequestContainsReference": False,
                "legacyKnowledgeBaseBootstrapAuthoritative": False,
            },
            "provenance": {
                "mediaBundleKind": "image.original.bundle",
                "processingProvenanceKind": "image.processing.provenance",
                "ocrSameRunOriginalKind": "image.original",
                "ocrSameRunOriginalHashBound": True,
                "defaultOcrRerunAfterHumanDecision": False,
            },
            "limits": {"maxImagesPerOcrRequest": 50, "maxZipFiles": 20000, "maxZipUncompressedBytes": 536870912},
            "retention": {"requestMetadataDays": 30, "executorState": "unsupported", "automaticDeletionClaim": False},
        },
        "statusSemantics": {
            "staticDescriptorContainsRuntimeState": False,
            "realtimeStatusSchemaVersion": STATUS_VERSION,
            "offlineReadinessStates": ["ready", "conditional", "not-ready"],
            "liveConnectivityDefaultState": "not-run",
            "secretValueProhibited": True,
            "realtimeCredentialFields": ["configured", "verified", "sourceType"],
            "coreOriginalFullPipeline": "unsupported",
            "corePartialFailedOnlyAutomaticContinuation": "available",
        },
    }


def _load_json(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise SettingsValidationError("settings file must contain one JSON object")
    return value


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Describe, project, or safely probe KnowledgeClipper image/OCR settings")
    subparsers = parser.add_subparsers(dest="command", required=True)
    describe = subparsers.add_parser("describe")
    describe.add_argument("--json", action="store_true", required=True)
    adapt = subparsers.add_parser("adapt")
    adapt.add_argument("--settings", required=True)
    status = subparsers.add_parser("status")
    status.add_argument("--settings", required=True)
    status.add_argument("--credential-metadata")
    status.add_argument("--json", action="store_true", required=True)
    validate = subparsers.add_parser("validate")
    validate.add_argument("--settings", required=True)
    validate.add_argument("--json", action="store_true", required=True)
    test = subparsers.add_parser("test")
    test.add_argument("--settings", required=True)
    test.add_argument("--credential-metadata")
    test.add_argument("--action", required=True, choices=[OFFLINE_FAKE_PROVIDER_ACTION_ID])
    test.add_argument("--json", action="store_true", required=True)
    args = parser.parse_args(argv)
    if args.command == "describe":
        output = capability_descriptor()
    else:
        path = Path(args.settings)
        if not path.is_absolute() or not path.is_file():
            raise SettingsValidationError("--settings must reference an existing absolute JSON file")
        settings = _load_json(path)
        if args.command == "adapt":
            output = adapt_settings(settings)
        elif args.command == "status":
            metadata = None
            if args.credential_metadata:
                metadata_path = Path(args.credential_metadata)
                if not metadata_path.is_absolute() or not metadata_path.is_file():
                    raise SettingsValidationError("--credential-metadata must reference an existing absolute JSON file")
                metadata = _load_json(metadata_path)
            output = probe_status(settings, metadata)
        elif args.command == "validate":
            output = validate_only(settings)
        else:
            from image_ocr_companion_diagnostics import run_fake_provider_test

            try:
                metadata = None
                if args.credential_metadata:
                    metadata_path = Path(args.credential_metadata)
                    if not metadata_path.is_absolute() or not metadata_path.is_file():
                        raise SettingsValidationError("--credential-metadata must reference an existing absolute JSON file")
                    metadata = _load_json(metadata_path)
                output = run_fake_provider_test(normalize_settings(settings), metadata)
            except ValueError as exc:
                raise SettingsValidationError(str(exc)) from exc
    print(json.dumps(output, ensure_ascii=False, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except SettingsValidationError as exc:
        print(json.dumps({"error": {"code": "settings_invalid", "message": str(exc)}}, ensure_ascii=False), file=sys.stderr)
        raise SystemExit(2)
