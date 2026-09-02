#!/usr/bin/env python3
"""Candidate-bound Core settings -> current export.anki offline Canary."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


WORKER = Path(__file__).resolve().parents[1]
PROJECT = WORKER.parents[1]
sys.path.insert(0, str(WORKER))
sys.path.insert(0, str(WORKER / "tests"))

import anki_exporter as exporter
import anki_settings_adapter as settings_adapter
from run_settings_adapter_probe_canary import artifact_reference_for_task, build_card_artifact
from test_anki_exporter import FakeAnkiServer
from test_anki_settings_and_probe import MUTATING, seed_ready


def now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def read_json(path: Path) -> Any:
    with open(exporter.native_fs_path(path), "r", encoding="utf-8") as stream:
        return json.load(stream)


def tree(root: Path) -> dict[str, Any]:
    rows = []
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix()):
        rows.append((path.relative_to(root).as_posix(), exporter.path_size(path), exporter.sha256_file(path)))
    digest = exporter.sha256_bytes("\n".join(f"{path}\t{size}\t{sha.lower()}" for path, size, sha in rows).encode("utf-8"))
    return {"files": len(rows), "bytes": sum(item[1] for item in rows), "treeSha256": digest.upper()}


def run(command: list[str], cwd: Path) -> subprocess.CompletedProcess[str]:
    process = subprocess.run(command, cwd=cwd, capture_output=True, text=True, encoding="utf-8", check=False)
    if process.returncode != 0:
        raise RuntimeError(
            f"Command failed with exit {process.returncode}: {command!r}\nstdout={process.stdout}\nstderr={process.stderr}"
        )
    return process


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--release-root", required=True)
    args = parser.parse_args()
    release_root = Path(args.release_root).resolve()
    exe = release_root / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
    manifest_path = release_root / "workers" / "anki" / "release-manifest.v1.json"
    if not exe.is_file() or not manifest_path.is_file():
        raise RuntimeError("Formal release root is missing its EXE or release manifest")
    release_manifest = read_json(manifest_path)
    expected_version = exporter.EXPORTER_VERSION
    if release_manifest.get("worker", {}).get("workerVersion") != expected_version:
        raise RuntimeError(f"Formal release is not export.anki {expected_version}")

    canary_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    root = WORKER / "canary" / "formal-release-settings-probe" / canary_id
    job_root = root / "job"
    created_at = now()
    task_id = "anki-formal-release-settings-probe"

    describe = run([str(exe), "describe", "--json"], release_root)
    if describe.stderr:
        raise RuntimeError(f"Release EXE describe wrote stderr: {describe.stderr}")
    descriptor = json.loads(describe.stdout)
    if descriptor.get("workerId") != "export.anki" or descriptor.get("workerVersion") != expected_version:
        raise RuntimeError("Release-root describe identity mismatch")
    describe_path = root / "release-root.describe.json"
    exporter.write_json_atomic_once(describe_path, descriptor)

    with FakeAnkiServer() as fake:
        if fake.endpoint == "http://127.0.0.1:8765":
            raise RuntimeError("Fake Anki must not bind the real AnkiConnect endpoint")
        settings = {
            "schemaVersion": settings_adapter.SETTINGS_SCHEMA_VERSION,
            "enabled": True,
            "instanceId": "default",
            "expectedProfile": "Canary Profile",
            "deckPreset": "knowledge-clipper-default",
            "projectTags": ["knowledge_clipper", "formal_release_canary"],
            "endpoint": fake.endpoint,
        }
        settings_path = root / "settings.input.json"
        exporter.write_json_atomic_once(settings_path, settings)
        adapted = settings_adapter.adapt_settings(settings, artifact_id="anki-config-formal-release")
        adaptation = settings_adapter.write_config_artifact(
            job_root,
            adapted,
            artifact_id="anki-config-formal-release",
            created_at=created_at,
            attempt_id="settings-adapter-formal-release",
        )
        config_ref = adaptation["configArtifact"]
        card, card_ref, card_payload_path = build_card_artifact(job_root, created_at, settings["projectTags"])
        exporter.write_json_atomic_once(job_root / "plans" / "initial-plan.json", {
            "fixture": "formal-release-settings-probe",
            "destinations": adapted.task_snapshot["destinations"],
        })
        task = {
            "schemaVersion": "1.0.0",
            "taskId": task_id,
            "createdAt": created_at,
            "createdBy": {"application": "KnowledgeClipper.Core.SettingsReleaseCanary", "version": "1.0.0"},
            "intent": "clip.card",
            "executionProfile": "cards-anki",
            "inputRefs": [artifact_reference_for_task(config_ref), artifact_reference_for_task(card_ref)],
            "requestedOutputs": ["anki-export-evidence", "anki-connection-status"],
            "initialPlanRef": "plans/initial-plan.json",
            "retentionClass": "evidence",
        }
        exporter.write_json_atomic_once(job_root / "task.manifest.v1.json", task)
        harness_input = {
            "schemaVersion": "export.anki.core-settings-release-canary.input.v1",
            "taskId": task_id,
            "frozenAt": created_at,
            "configRef": config_ref,
            "cardRef": card_ref,
        }
        harness_input_path = root / "core-harness.input.json"
        exporter.write_json_atomic_once(harness_input_path, harness_input)
        seed_ready(fake.state, adapted.config)

        harness_script = WORKER / "tests" / "Run-CoreSettingsReleaseCanary.ps1"
        core_run = run([
            "pwsh", "-NoLogo", "-NoProfile", "-File", str(harness_script),
            "-ProjectRoot", str(PROJECT),
            "-ReleaseRoot", str(release_root),
            "-JobRoot", str(job_root),
            "-InputJson", str(harness_input_path),
        ], PROJECT)
        core_stdout = root / "core-harness.stdout.log"
        core_stderr = root / "core-harness.stderr.log"
        core_stdout.write_text(core_run.stdout, encoding="utf-8", newline="\n")
        core_stderr.write_text(core_run.stderr, encoding="utf-8", newline="\n")

        core_lines = [line for line in core_run.stdout.splitlines() if line.strip()]
        if not core_lines:
            raise RuntimeError("Core settings harness produced no evidence")
        core_evidence = json.loads(core_lines[-1])
        if core_evidence.get("status") != "passed":
            raise RuntimeError("Core settings/WorkerRequest evidence did not pass")
        core_evidence_path = job_root / "core-settings-worker-evidence.json"
        exporter.write_json_atomic_once(core_evidence_path, core_evidence)
        actions = [item["action"] for item in fake.state.calls]
        mutating = sorted(set(actions) & MUTATING)
        if mutating or fake.state.notes or fake.state.media:
            raise RuntimeError(f"Offline release Canary observed a mutation: {mutating}")
        if not set(actions).issubset(exporter.PROBE_READ_ONLY_ACTIONS):
            raise RuntimeError(f"Offline probe used an unapproved action: {actions}")

        validate_request = Path(core_evidence["validateOnly"]["requestPath"])
        validate_result = Path(core_evidence["validateOnly"]["resultPath"])
        probe_request = Path(core_evidence["offlineProbe"]["requestPath"])
        probe_result = Path(core_evidence["offlineProbe"]["resultPath"])
        probe_result_value = read_json(probe_result)
        probe_manifest = job_root / probe_result_value["outputs"][0]["manifestPath"]
        probe_artifact = read_json(probe_manifest)
        connection_status_path = job_root / probe_artifact["metadata"]["entrypoint"]
        connection_status = read_json(connection_status_path)
        if connection_status.get("status") != "ready":
            raise RuntimeError(f"Offline probe was not ready: {connection_status!r}")
        status_text = json.dumps(connection_status, ensure_ascii=False, sort_keys=True)
        sensitive_values = [
            adapted.config["expected_profile"],
            *[mapping["model_name"] for mapping in adapted.config["note_types"].values()],
            *adapted.config["deck_routes"].values(),
        ]
        if any(value in status_text for value in sensitive_values):
            raise RuntimeError("Connection status leaked a profile, model, or deck identifier")

        expected_request_path = root / "expected-anki-request.not-sent.json"
        bundle_value = read_json(card_payload_path)
        render_package = exporter.LoadedPackage(
            root=job_root,
            manifest=task,
            request=read_json(validate_request),
            request_path=validate_request,
            bundle_artifact={},
            config_artifact={},
            bundle=bundle_value,
            config=adapted.config,
            bundle_path=card_payload_path,
            config_path=job_root / "artifacts" / config_ref["artifactRef"] / "anki-export.v1.json",
            media_by_id={},
        )
        rendered_fields, _ = exporter.render_card(bundle_value["cards"][0], render_package)
        expected_request = {
            "action": "addNote",
            "params": {"note": {
                "deckName": adapted.config["deck_routes"][card["deck_route_key"]],
                "modelName": adapted.config["note_types"][card["note_type_key"]]["model_name"],
                "fields": rendered_fields,
                "tags": sorted(set(card["tags"]) | {"rqc_managed"}),
                "options": {"allowDuplicate": False},
            }},
            "sent": False,
        }
        exporter.write_json_atomic_once(expected_request_path, expected_request)
        readable_path = root / "readable-core-settings-release.md"
        readable_path.write_text("\n".join([
            f"# Core settings → export.anki {expected_version} formal release 离线证据",
            "",
            f"- release-root：`{relative(release_root)}`",
            f"- settings enabled：`true` → Core destinations：`{core_evidence['settings']['destinations']}`",
            f"- SHA-bound config Artifact：`{config_ref['artifactRef']}` / `{config_ref['expectedSha256']}`",
            f"- WorkerRequest configSnapshot：`{core_evidence['validateOnly']['configSnapshot']}`（仅身份，不复制配置正文）",
            f"- validate result：`{core_evidence['validateOnly']['status']}` / worker `{core_evidence['validateOnly']['workerId']} {core_evidence['validateOnly']['workerVersion']}`",
            f"- probe result：`{core_evidence['offlineProbe']['status']}` / connection status `{connection_status['status']}`",
            f"- 原始问题：{card['fields']['question']}",
            f"- 原始答案：{card['fields']['answer']}",
            f"- 最终 deck/model：`{expected_request['params']['note']['deckName']}` / `{expected_request['params']['note']['modelName']}`",
            f"- 最终 Front：{rendered_fields['Front']}",
            f"- 最终 Back：{rendered_fields['Back']}",
            f"- 最终 tags：`{expected_request['params']['note']['tags']}`",
            "- 预期 addNote 仅落样例，`sent=false`。",
            f"- Fake probe actions：`{actions}`；mutating actions：`{mutating}`。",
            "- 真实 AnkiConnect 调用：`0`；牌组/笔记/媒体写入：`0`。",
            "",
        ]), encoding="utf-8", newline="\n")

        release_tree = tree(release_root)
        receipt = {
            "schemaVersion": "export.anki.formal-release-settings-probe-receipt.v1",
            "status": "passed",
            "createdAt": now(),
            "authorityTarget": None,
            "release": {
                "root": relative(release_root),
                **release_tree,
                "manifest": relative(manifest_path),
                "manifestSha256": exporter.sha256_file(manifest_path).upper(),
                "exe": {"path": relative(exe), "bytes": exporter.path_size(exe), "sha256": exporter.sha256_file(exe).upper()},
                "describe": {"path": relative(describe_path), "sha256": exporter.sha256_file(describe_path).upper(), "workerId": descriptor["workerId"], "workerVersion": descriptor["workerVersion"]},
            },
            "core": {
                "settingsControlSha256": exporter.sha256_file(PROJECT / "orchestrator" / "Core" / "SettingsControl.cs").upper(),
                "orchestratorEngineSha256": exporter.sha256_file(PROJECT / "orchestrator" / "Core" / "OrchestratorEngine.cs").upper(),
                "evidence": {"path": relative(core_evidence_path), "sha256": exporter.sha256_file(core_evidence_path).upper()},
                "settingsSnapshotArtifactRef": core_evidence["settings"]["snapshotArtifact"],
                "configArtifactPreserved": core_evidence["settings"]["ankiConfigArtifactRef"] == config_ref["artifactRef"],
                "configShaPreserved": core_evidence["settings"]["ankiConfigExpectedSha256"].lower() == config_ref["expectedSha256"].lower(),
                "registryAvailabilityDoesNotEnableDestination": True,
                "destinationInstance": adapted.task_snapshot["destinationInstances"]["anki"],
            },
            "inputs": {
                "settings": {"path": relative(settings_path), "sha256": exporter.sha256_file(settings_path).upper()},
                "configArtifact": {"path": relative(job_root / config_ref["manifestPath"]), "sha256": config_ref["expectedSha256"].upper()},
                "cardBundle": {"path": relative(card_payload_path), "sha256": exporter.sha256_file(card_payload_path).upper()},
            },
            "attempts": {
                "validateOnly": {"request": relative(validate_request), "requestSha256": exporter.sha256_file(validate_request).upper(), "result": relative(validate_result), "resultSha256": exporter.sha256_file(validate_result).upper(), "status": "completed", "workerVersion": expected_version},
                "offlineProbe": {"request": relative(probe_request), "requestSha256": exporter.sha256_file(probe_request).upper(), "result": relative(probe_result), "resultSha256": exporter.sha256_file(probe_result).upper(), "status": "completed", "workerVersion": expected_version, "connectionStatus": connection_status["status"]},
            },
            "safety": {
                "realAnkiConnectCalls": 0,
                "fakeEndpoint": "random-loopback-test-server",
                "fakeActions": actions,
                "mutatingActions": mutating,
                "notesWritten": len(fake.state.notes),
                "mediaWritten": len(fake.state.media),
                "decksCreated": 0,
                "statusIdentifiersRedacted": True,
            },
            "outputs": {
                "connectionStatus": {"path": relative(connection_status_path), "sha256": exporter.sha256_file(connection_status_path).upper()},
                "expectedAnkiRequestNotSent": {"path": relative(expected_request_path), "sha256": exporter.sha256_file(expected_request_path).upper()},
                "readable": {"path": relative(readable_path), "sha256": exporter.sha256_file(readable_path).upper()},
                "coreStdout": {"path": relative(core_stdout), "sha256": exporter.sha256_file(core_stdout).upper()},
                "coreStderr": {"path": relative(core_stderr), "sha256": exporter.sha256_file(core_stderr).upper()},
            },
        }
        receipt_path = root / "receipt.json"
        exporter.write_json_atomic_once(receipt_path, receipt)
        print(json.dumps({
            "status": "passed",
            "releaseRoot": str(release_root),
            "treeSha256": release_tree["treeSha256"],
            "exeSha256": receipt["release"]["exe"]["sha256"],
            "describe": receipt["release"]["describe"],
            "receipt": str(receipt_path),
            "receiptSha256": exporter.sha256_file(receipt_path).upper(),
            "readable": str(readable_path),
        }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
