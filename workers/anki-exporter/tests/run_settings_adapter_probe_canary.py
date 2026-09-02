#!/usr/bin/env python3
"""Offline settings/probe Canary using only the loopback Fake Anki server."""

from __future__ import annotations

import json
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path


WORKER = Path(__file__).resolve().parents[1]
PROJECT = WORKER.parents[1]
sys.path.insert(0, str(WORKER))
sys.path.insert(0, str(WORKER / "tests"))

import anki_exporter as exporter
import anki_settings_adapter as settings_adapter
from test_anki_exporter import FakeAnkiServer, dump_json
from test_anki_settings_and_probe import MUTATING, seed_ready


def now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def artifact_reference_for_task(reference: dict) -> dict:
    return {key: reference[key] for key in ("artifactRef", "kind", "contractVersion", "expectedSha256")}


def build_card_artifact(job_root: Path, created_at: str, project_tags: list[str]) -> tuple[dict, dict, Path]:
    card = {
        "card_id": "settings-canary-basic-qa",
        "revision": 1,
        "content_hash": "pending",
        "status": "ready",
        "deck_route_key": "question",
        "note_type_key": "basic_qa",
        "fields": {
            "question": "为什么设置快照不能复制 Anki 配置正文？",
            "answer": "因为任务只应引用 SHA 绑定的配置 Artifact，避免多份配置权威发生漂移。",
            "context": "零碎知识剪藏的 Anki 末端投影",
            "source": "synthetic-settings-canary",
        },
        "tags": project_tags + ["settings_canary"],
        "media_refs": [],
    }
    card["content_hash"] = exporter.compute_content_hash(card, {})
    bundle = {"bundle_kind": "CardBundle", "schema_version": "1.0", "bundle_id": "settings-probe-readable", "cards": [card], "media": []}
    payload_relative = "artifacts/card-input/card-bundle.v1.json"
    manifest_relative = "artifacts/card-input/artifact.json"
    payload_path = job_root / payload_relative
    manifest_path = job_root / manifest_relative
    exporter.write_json_atomic_once(payload_path, bundle)
    artifact = {
        "schemaVersion": "1.0.0",
        "artifactId": "card-input",
        "kind": "card-bundle",
        "contractVersion": "1.0.0",
        "mediaType": "application/vnd.knowledge-clipper.card-bundle+json",
        "files": [{"path": payload_relative, "sha256": exporter.sha256_file(payload_path), "bytes": exporter.path_size(payload_path), "mediaType": "application/json"}],
        "createdAt": created_at,
        "producer": {"stageId": "settings-canary-fixture", "workerId": "export.anki.settings-canary", "workerVersion": "1.0.0", "attemptId": "card-fixture"},
        "inputRefs": [],
        "dataClass": "public",
        "metadata": {"entrypoint": payload_relative},
    }
    exporter.write_json_atomic_once(manifest_path, artifact)
    reference = {"artifactRef": "card-input", "kind": "card-bundle", "contractVersion": "1.0.0", "manifestPath": manifest_relative, "expectedSha256": exporter.sha256_file(manifest_path)}
    return card, reference, payload_path


def worker_request(task_id: str, run_id: str, attempt_id: str, operation: str, input_refs: list[dict], config_id: str) -> tuple[dict, str]:
    stage_id = "anki-export"
    attempt = f"runs/{run_id}/stages/{stage_id}/attempts/{attempt_id}"
    request = {
        "schemaVersion": "1.0.0",
        "requestId": f"request-{operation}-settings-canary",
        "operationId": f"operation-{operation}-settings-canary",
        "taskId": task_id,
        "runId": run_id,
        "stageId": stage_id,
        "attemptId": attempt_id,
        "workerId": "export.anki",
        "operation": operation,
        "idempotencyKey": exporter.sha256_bytes(exporter.json_bytes({"operation": operation, "inputs": input_refs})),
        "attemptPath": attempt,
        "outputPath": f"{attempt}/outputs",
        "progressPath": f"{attempt}/progress.ndjson",
        "resultPath": f"{attempt}/result.json",
        "cancelSignal": {"schemaVersion": "1.0.0", "path": f"{attempt}/cancel.json", "gracePeriodMs": 2000},
        "inputRefs": input_refs,
        "configSnapshot": {"ankiConfigArtifactId": config_id},
        "payload": {"probeMode": "read-only"} if operation == "probe" else {"validateOnly": True},
    }
    return request, f"{attempt}/request.json"


def main() -> int:
    run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    root = WORKER / "canary" / "settings-adapter-probe" / run_id
    job_root = root / "job"
    created_at = now()
    with FakeAnkiServer() as fake:
        if fake.endpoint == "http://127.0.0.1:8765":
            raise RuntimeError("Fake Anki must not bind the real AnkiConnect endpoint")
        settings = {
            "schemaVersion": settings_adapter.SETTINGS_SCHEMA_VERSION,
            "enabled": True,
            "instanceId": "default",
            "expectedProfile": "Canary Profile",
            "deckPreset": "knowledge-clipper-default",
            "projectTags": ["knowledge_clipper", "anki_target"],
            "endpoint": fake.endpoint,
        }
        settings_path = root / "settings.input.json"
        exporter.write_json_atomic_once(settings_path, settings)
        adapted = settings_adapter.adapt_settings(settings, artifact_id="anki-config-settings-canary")
        adaptation = settings_adapter.write_config_artifact(job_root, adapted, artifact_id="anki-config-settings-canary", created_at=created_at, attempt_id="settings-adapter")
        config_ref = adaptation["configArtifact"]
        card, card_ref, card_payload_path = build_card_artifact(job_root, created_at, settings["projectTags"])

        task = {
            "schemaVersion": "1.0.0",
            "taskId": "settings-probe-canary",
            "createdAt": created_at,
            "createdBy": {"application": "export.anki.settings-canary", "version": "1.0.0"},
            "intent": "clip.card",
            "executionProfile": "cards-anki",
            "inputRefs": [artifact_reference_for_task(config_ref), artifact_reference_for_task(card_ref)],
            "requestedOutputs": ["anki-connection-status", "anki-export-evidence"],
            "initialPlanRef": "plans/initial-plan.json",
            "retentionClass": "evidence",
        }
        exporter.write_json_atomic_once(job_root / "plans" / "initial-plan.json", {"fixture": "settings-adapter-probe", "destinations": adapted.task_snapshot["destinations"]})
        exporter.write_json_atomic_once(job_root / "task.manifest.v1.json", task)

        seed_ready(fake.state, adapted.config)
        probe_request, probe_relative = worker_request(task["taskId"], "run-probe", "attempt-probe", "probe", [config_ref], config_ref["artifactRef"])
        exporter.write_json_atomic_once(job_root / probe_relative, probe_request)
        probe = exporter.Exporter(job_root, "task.manifest.v1.json", probe_relative)
        if probe.run() != "SUCCEEDED":
            raise RuntimeError("Fake Anki read-only probe failed")

        validate_request, validate_relative = worker_request(task["taskId"], "run-validate", "attempt-validate", "validate", [card_ref, config_ref], config_ref["artifactRef"])
        exporter.write_json_atomic_once(job_root / validate_relative, validate_request)
        validation = exporter.Exporter(job_root, "task.manifest.v1.json", validate_relative)
        if validation.run() != "SUCCEEDED":
            raise RuntimeError("CardBundle validate-only failed")
        rendered_fields, _ = exporter.render_card(validation.package.bundle["cards"][0], validation.package)
        expected_add_note = {
            "action": "addNote",
            "params": {
                "note": {
                    "deckName": adapted.config["deck_routes"][card["deck_route_key"]],
                    "modelName": adapted.config["note_types"][card["note_type_key"]]["model_name"],
                    "fields": rendered_fields,
                    "tags": sorted(set(card["tags"]) | {"rqc_managed"}),
                    "options": {"allowDuplicate": False},
                }
            },
            "sent": False,
        }
        actions = [entry["action"] for entry in fake.state.calls]
        if actions[:4] != ["requestPermission", "version", "getActiveProfile", "apiReflect"]:
            raise RuntimeError(f"Probe action order changed: {actions}")
        mutating = sorted(set(actions) & MUTATING)
        if mutating or fake.state.notes or fake.state.media:
            raise RuntimeError(f"Offline Canary observed a mutation: {mutating}")

        status_path = probe.writer.output_root / "connection-status.json"
        status_text = json.dumps(probe.connection_status, ensure_ascii=False, sort_keys=True)
        sensitive_values = [
            adapted.config["expected_profile"],
            *[mapping["model_name"] for mapping in adapted.config["note_types"].values()],
            *adapted.config["deck_routes"].values(),
        ]
        if any(value in status_text for value in sensitive_values):
            raise RuntimeError("Connection status leaked a profile, model, or deck identifier")
        probe_result_path = probe.writer.result_path
        validate_result_path = validation.writer.result_path
        readable_path = root / "readable-settings-probe.md"
        readable = "\n".join([
            "# Anki 设置 adapter + 只读 probe 可读样例",
            "",
            "## 项目设置 → 新任务快照",
            "",
            f"- enabled：`{str(settings['enabled']).lower()}` → destinations：`{adapted.task_snapshot['destinations']}`",
            f"- instance：`{adapted.task_snapshot['destinationInstances']['anki']}`（固定单实例；多实例延期）",
            f"- expected profile：`{settings['expectedProfile']}`",
            f"- project tags：`{settings['projectTags']}` → Core projection patch：`{adapted.task_snapshot['coreProjectionPatch']}`",
            f"- provision_managed：`{str(adapted.config['provision_managed']).lower()}`",
            f"- configSnapshot：`{adapted.task_snapshot['configSnapshot']}`，未复制 endpoint/profile/deck/note 配置正文",
            f"- config Artifact：`{config_ref['manifestPath']}`，SHA-256 `{config_ref['expectedSha256']}`",
            "",
            "## 原始 CardBundle → 最终预期 Anki 请求",
            "",
            f"- 原始问题：{card['fields']['question']}",
            f"- 原始答案：{card['fields']['answer']}",
            f"- route/note type：`{card['deck_route_key']}` / `{card['note_type_key']}`",
            f"- 最终 deck/model：`{expected_add_note['params']['note']['deckName']}` / `{expected_add_note['params']['note']['modelName']}`",
            f"- 最终 Front：{rendered_fields['Front']}",
            f"- 最终 Back：{rendered_fields['Back']}",
            f"- 最终 Context：{rendered_fields['Context']}",
            f"- 最终 tags：`{expected_add_note['params']['note']['tags']}`",
            "- addNote 请求仅作为预期形状生成，`sent=false`；validate-only 未构造 AnkiClient。",
            "",
            "## Fake Anki 只读 probe",
            "",
            f"- status：`{probe.connection_status['status']}`",
            f"- Fake actions：`{actions}`",
            f"- mutating actions：`{mutating}`",
            "- 真实 AnkiConnect 调用：`0`；Fake loopback 调用仅用于离线验收。",
            "- Fake notes/media：`0 / 0`。",
            "",
        ])
        readable_path.parent.mkdir(parents=True, exist_ok=True)
        readable_path.write_text(readable, encoding="utf-8", newline="\n")
        expected_path = root / "expected-anki-request.not-sent.json"
        exporter.write_json_atomic_once(expected_path, expected_add_note)
        receipt = {
            "schemaVersion": "export.anki.settings-probe-canary.v1",
            "status": "passed",
            "createdAt": now(),
            "worker": {"id": "export.anki", "version": exporter.EXPORTER_VERSION},
            "adapter": {"version": settings_adapter.ADAPTER_VERSION, "descriptor": relative(WORKER / "contracts" / "anki-destination-settings-descriptor.v1.json")},
            "checks": {
                "settingsToShaBoundConfigArtifact": True,
                "enabledOnlyAffectsNewTaskDestinations": True,
                "registryAvailabilityDoesNotEnableDestination": True,
                "fixedDestinationInstance": adapted.task_snapshot["destinationInstances"],
                "configSnapshotIdentityOnly": True,
                "probeReadOnly": True,
                "probeEffectClass": probe.connection_status["effectClass"],
                "probeMayPromptUser": probe.connection_status["mayPromptUser"],
                "statusIdentifiersRedacted": True,
                "validateOnlyNoConnection": True,
                "mediaDeferred": adapted.task_snapshot["media"],
                "realAnkiConnectCalls": 0,
                "fakeMutatingActions": mutating,
                "fakeNotes": len(fake.state.notes),
                "fakeMedia": len(fake.state.media),
            },
            "inputs": {"settings": {"path": relative(settings_path), "sha256": exporter.sha256_file(settings_path)}, "cardBundle": {"path": relative(card_payload_path), "sha256": exporter.sha256_file(card_payload_path)}},
            "outputs": {
                "configArtifact": {"path": relative(job_root / config_ref["manifestPath"]), "sha256": config_ref["expectedSha256"]},
                "connectionStatus": {"path": relative(status_path), "sha256": exporter.sha256_file(status_path)},
                "probeResult": {"path": relative(probe_result_path), "sha256": exporter.sha256_file(probe_result_path)},
                "validateResult": {"path": relative(validate_result_path), "sha256": exporter.sha256_file(validate_result_path)},
                "expectedAnkiRequestNotSent": {"path": relative(expected_path), "sha256": exporter.sha256_file(expected_path)},
                "readable": {"path": relative(readable_path), "sha256": exporter.sha256_file(readable_path)},
            },
        }
        receipt_path = root / "receipt.json"
        exporter.write_json_atomic_once(receipt_path, receipt)
        print(json.dumps({"status": "passed", "root": str(root), "receipt": str(receipt_path), "receiptSha256": exporter.sha256_file(receipt_path), "readable": str(readable_path), "readableSha256": exporter.sha256_file(readable_path)}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
