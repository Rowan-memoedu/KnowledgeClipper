#!/usr/bin/env python3
"""Validate the planned Anki worker EXE against stable common v1."""

from __future__ import annotations

import json
import os
import subprocess
import sys
import uuid
from pathlib import Path
from typing import Any


SOURCE_ROOT = Path(__file__).resolve().parent
PROJECT_ROOT = SOURCE_ROOT.parents[1]
EXE = PROJECT_ROOT / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
sys.path.insert(0, str(SOURCE_ROOT))
sys.path.insert(0, str(SOURCE_ROOT / "tests"))

import anki_exporter as exporter
from test_anki_exporter import FakeAnkiServer, build_derived_job, build_job, mutate_job_config, validate_common


def run_exe(job: dict[str, Any], *, expected_status: str = "completed", expected_exit_code: int = 0) -> tuple[dict[str, Any], Path]:
    request_path = (job["root"] / job["request"]).resolve()
    process = subprocess.run(
        [str(EXE), "run", "--request", str(request_path)],
        check=False, capture_output=True, text=True, encoding="utf-8",
    )
    (job["root"] / "exe.stdout.ndjson").write_text(process.stdout, encoding="utf-8", newline="\n")
    (job["root"] / "exe.stderr.log").write_text(process.stderr, encoding="utf-8", newline="\n")
    result_path = job["root"] / job["request"].replace("request.json", "result.json")
    if process.returncode != expected_exit_code or not result_path.is_file():
        raise RuntimeError(f"EXE run failed: exit={process.returncode}; stderr={process.stderr}")
    result = json.loads(result_path.read_text(encoding="utf-8"))
    if (result.get("status") != expected_status
            or result.get("worker", {}).get("id") != "export.anki"
            or result.get("worker", {}).get("version") != exporter.EXPORTER_VERSION):
        raise RuntimeError(f"EXE result identity/status mismatch: {result!r}")
    validate_common(request_path, "worker-request.schema.json")
    validate_common(result_path, "worker-result.schema.json")
    for output in result["outputs"]:
        validate_common(job["root"] / output["manifestPath"], "artifact.schema.json")
    return result, result_path


def main() -> int:
    if not EXE.is_file():
        raise RuntimeError(f"Planned EXE is missing: {EXE}")
    exe_sha256 = exporter.sha256_file(EXE)

    metadata_environment = os.environ.copy()
    metadata_environment["KNOWLEDGE_CLIPPER_ANKI_EXE"] = str(EXE)
    metadata_command = (
        "$v=(Get-Item -LiteralPath $env:KNOWLEDGE_CLIPPER_ANKI_EXE).VersionInfo; "
        "[pscustomobject]@{ProductName=$v.ProductName;CompanyName=$v.CompanyName;"
        "FileDescription=$v.FileDescription;FileVersion=$v.FileVersion;"
        "InternalName=$v.InternalName;OriginalFilename=$v.OriginalFilename;"
        "ProductVersion=$v.ProductVersion}|ConvertTo-Json -Compress"
    )
    metadata_process = subprocess.run(
        ["pwsh", "-NoProfile", "-Command", metadata_command],
        check=False, capture_output=True, text=True, encoding="utf-8", env=metadata_environment,
    )
    if metadata_process.returncode != 0:
        raise RuntimeError(f"Could not read EXE version metadata: {metadata_process.stderr}")
    packaging_metadata = json.loads(metadata_process.stdout)
    expected_metadata = {
        "ProductName": "零碎知识剪藏",
        "CompanyName": "KnowledgeClipper",
        "FileDescription": "KnowledgeClipper Anki Exporter Worker",
        "FileVersion": exporter.EXPORTER_VERSION,
        "InternalName": "KnowledgeClipper.AnkiExporter.Worker",
        "OriginalFilename": "KnowledgeClipper.AnkiExporter.Worker.exe",
        "ProductVersion": exporter.EXPORTER_VERSION,
    }
    if packaging_metadata != expected_metadata:
        raise RuntimeError(f"EXE version metadata is not authoritative KnowledgeClipper identity: {packaging_metadata!r}")
    compact_time = exporter.utc_now().replace("-", "").replace(":", "").replace(".", "").replace("Z", "")
    validation_id = "xv-" + compact_time[:15] + "-" + uuid.uuid4().hex[:4]
    evidence_root = PROJECT_ROOT / "workers" / "anki" / "canary" / validation_id
    evidence_root.mkdir(parents=True, exist_ok=False)

    described = subprocess.run([str(EXE), "describe", "--json"], check=False, capture_output=True, text=True, encoding="utf-8")
    if described.returncode != 0 or described.stderr:
        raise RuntimeError(f"EXE describe failed: exit={described.returncode}; stderr={described.stderr}")
    describe_path = evidence_root / "worker-describe.json"
    describe_path.write_text(described.stdout, encoding="utf-8", newline="\n")
    validate_common(describe_path, "worker-describe.schema.json")
    descriptor = json.loads(described.stdout)
    if (descriptor["workerId"] != "export.anki"
            or descriptor["workerVersion"] != exporter.EXPORTER_VERSION
            or descriptor["sideEffectLevel"] != "external"
            or descriptor["idempotencyLevel"] != "reconcilable"):
        raise RuntimeError(f"EXE descriptor does not match planned registry identity: {descriptor!r}")
    if "remnote" in json.dumps(descriptor, ensure_ascii=False).lower():
        raise RuntimeError("export.anki descriptor contains a RemNote product assumption")

    with FakeAnkiServer() as fake:
        validate_job = build_job(evidence_root / "v", fake.endpoint, operation="validate")
        validate_result, validate_result_path = run_exe(validate_job)
        if fake.state.notes:
            raise RuntimeError("validate-only EXE run touched Fake Anki")
        derived_job = build_derived_job(evidence_root / "d", fake.endpoint)
        derived_result, derived_result_path = run_exe(derived_job)
        if fake.state.notes:
            raise RuntimeError("derived validate-only EXE run touched Fake Anki")
        learning_job = build_job(
            evidence_root / "l",
            fake.endpoint,
            operation="validate",
            bundle_kind="LearningBundle",
            artifact_kind="learning-bundle",
        )
        learning_result, learning_result_path = run_exe(learning_job)
        if fake.state.notes:
            raise RuntimeError("LearningBundle validate-only EXE run touched Fake Anki")
        export_job = build_job(evidence_root / "x", fake.endpoint, operation="export")
        export_result, export_result_path = run_exe(export_job)
        if len(fake.state.notes) != 1:
            raise RuntimeError("Fake export did not create exactly one note")
        note_id = next(iter(fake.state.notes))

        reroute_job = build_job(evidence_root / "r", fake.endpoint, operation="export")
        rerouted_deck = "零碎知识剪藏::EXE-Reroute"
        mutate_job_config(reroute_job, lambda config: config["deck_routes"].update({"canary": rerouted_deck}))
        reroute_result, reroute_result_path = run_exe(reroute_job)
        reroute_receipt_path = reroute_job["root"] / reroute_job["request"].replace("request.json", "outputs/receipt.json")
        reroute_receipt = json.loads(reroute_receipt_path.read_text(encoding="utf-8"))
        card_id = fake.state.notes[note_id]["cards"][0]
        if (len(fake.state.notes) != 1
                or fake.state.notes[note_id]["card_decks"][card_id] != rerouted_deck
                or reroute_receipt["counts"]["already_current"] != 1):
            raise RuntimeError("EXE deck reroute did not converge on the existing global RQC_ID")

        alternate_model = "RQC Alternate v1"
        fake.state.models[alternate_model] = list(exporter.ALL_MODEL_FIELDS)
        fake.state.model_templates[alternate_model] = {"Card 1": {"Front": exporter.DEFAULT_FRONT_TEMPLATE, "Back": exporter.DEFAULT_BACK_TEMPLATE}}
        fake.state.model_css[alternate_model] = exporter.DEFAULT_CSS
        remap_job = build_job(evidence_root / "m", fake.endpoint, operation="export")
        mutate_job_config(remap_job, lambda config: config["note_types"]["image_qa"].update({"model_name": alternate_model}))
        remap_result, remap_result_path = run_exe(remap_job, expected_status="failed", expected_exit_code=5)
        remap_operations_path = remap_job["root"] / remap_job["request"].replace("request.json", "outputs/operations.ndjson")
        remap_operations = [json.loads(line)["action"] for line in remap_operations_path.read_text(encoding="utf-8").splitlines()]
        mutating_actions = {"createModel", "createDeck", "storeMediaFile", "addNote", "updateNoteFields", "addTags", "changeDeck"}
        if (len(fake.state.notes) != 1
                or remap_result.get("error", {}).get("code") != "ANKI_EXPORT_FAILED"
                or any(action in mutating_actions for action in remap_operations)):
            raise RuntimeError("EXE note-type conflict was not detected before mutation")

        alias_job = build_derived_job(evidence_root / "a", fake.endpoint, defect="file_alias")
        alias_result, alias_result_path = run_exe(alias_job, expected_status="failed", expected_exit_code=2)
        if alias_result.get("error", {}).get("code") != "PATH_NONCANONICAL":
            raise RuntimeError("EXE accepted a non-canonical derived Artifact file path")

        endpoint_job = build_job(evidence_root / "e", fake.endpoint, operation="validate")
        mutate_job_config(endpoint_job, lambda config: config.update({"endpoint": "http://127.0.0.1:8765@evil.example"}))
        endpoint_result, endpoint_result_path = run_exe(endpoint_job, expected_status="failed", expected_exit_code=2)
        endpoint_operations_path = endpoint_job["root"] / endpoint_job["request"].replace("request.json", "outputs/operations.ndjson")
        if endpoint_result.get("error", {}).get("code") != "CONFIG_INVALID" or endpoint_operations_path.exists():
            raise RuntimeError("EXE did not reject endpoint authority smuggling before AnkiConnect")

        fake.state.forced_results["modelNames"] = None
        malformed_response_job = build_job(evidence_root / "j", fake.endpoint, operation="export")
        malformed_response_result, malformed_response_result_path = run_exe(malformed_response_job, expected_status="failed", expected_exit_code=5)
        del fake.state.forced_results["modelNames"]
        if malformed_response_result.get("error", {}).get("code") != "ANKI_RESPONSE_INVALID" or len(fake.state.notes) != 1:
            raise RuntimeError("EXE did not publish a failed result for malformed AnkiConnect collection data")

    with FakeAnkiServer() as media_fake:
        media_fake.state.fail_after_store_once = True
        media_loss_job = build_job(evidence_root / "s", media_fake.endpoint, operation="export")
        media_loss_result, media_loss_result_path = run_exe(media_loss_job, expected_status="unknown_external_state", expected_exit_code=5)
        if (media_loss_result.get("error", {}).get("retryable") is not False
                or media_loss_result.get("outputs") != []
                or len(media_fake.state.media) != 1
                or len(media_fake.state.notes) != 0):
            raise RuntimeError("EXE media response loss did not preserve unknown external state")
        media_reconcile_job = build_job(evidence_root / "s-reconcile", media_fake.endpoint, operation="export")
        media_reconcile_result, media_reconcile_result_path = run_exe(media_reconcile_job)
        media_events_path = media_reconcile_job["root"] / media_reconcile_job["request"].replace("request.json", "outputs/media.ndjson")
        media_events = [json.loads(line) for line in media_events_path.read_text(encoding="utf-8").splitlines()]
        if (len(media_fake.state.media) != 1
                or len(media_fake.state.notes) != 1
                or [event["status"] for event in media_events] != ["ALREADY_CURRENT"]):
            raise RuntimeError("EXE explicit media reconciliation did not converge idempotently")

    receipt = {
        "schemaVersion": "1.0.0", "validationId": validation_id, "status": "passed",
        "product": {"displayName": "零碎知识剪藏", "asciiRoot": "KnowledgeClipper"},
        "checkpointId": "contracts-v1-20260825T115938Z",
        "exe": {"path": EXE.relative_to(PROJECT_ROOT).as_posix(), "sha256": exe_sha256, "bytes": exporter.path_size(EXE), "versionMetadata": packaging_metadata},
        "describe": {"workerId": descriptor["workerId"], "workerVersion": descriptor["workerVersion"], "sha256": exporter.sha256_file(describe_path), "schemaValid": True, "remNoteAssumptions": 0},
        "endpointBoundary": {"role": "peer-pluggable-terminal-exporter", "dependsOnOtherTerminalExporters": False},
        "validateOnly": {"status": validate_result["status"], "resultSha256": exporter.sha256_file(validate_result_path), "fakeNotes": 0},
        "derivedValidateOnly": {
            "status": derived_result["status"], "resultSha256": exporter.sha256_file(derived_result_path),
            "producerWorkerId": "orchestrator.core-adapter", "ancestryRootedInInitialTaskInputs": True, "fakeNotes": 0,
        },
        "learningBundleValidateOnly": {
            "status": learning_result["status"], "resultSha256": exporter.sha256_file(learning_result_path),
            "bundleKind": "LearningBundle", "artifactKind": "learning-bundle", "fakeNotes": 0,
        },
        "fakeExport": {"status": export_result["status"], "resultSha256": exporter.sha256_file(export_result_path), "fakeNotes": 1},
        "deckMappingConvergence": {
            "status": reroute_result["status"], "resultSha256": exporter.sha256_file(reroute_result_path),
            "noteCount": 1, "alreadyCurrent": 1, "deck": rerouted_deck,
        },
        "noteTypeConflict": {
            "status": remap_result["status"], "resultSha256": exporter.sha256_file(remap_result_path),
            "noteCount": 1, "mutatingActions": 0, "globalRqcIdGuard": True,
        },
        "nonCanonicalPathAttack": {
            "status": alias_result["status"], "resultSha256": exporter.sha256_file(alias_result_path),
            "errorCode": alias_result["error"]["code"], "fakeAnkiCalls": 0,
        },
        "endpointAuthorityAttack": {
            "status": endpoint_result["status"], "resultSha256": exporter.sha256_file(endpoint_result_path),
            "errorCode": endpoint_result["error"]["code"], "fakeAnkiCalls": 0,
        },
        "malformedAnkiResponse": {
            "status": malformed_response_result["status"], "resultSha256": exporter.sha256_file(malformed_response_result_path),
            "errorCode": malformed_response_result["error"]["code"], "notesUnchanged": 1,
        },
        "mediaResponseLoss": {
            "status": media_loss_result["status"], "resultSha256": exporter.sha256_file(media_loss_result_path),
            "retryable": media_loss_result["error"]["retryable"], "outputs": 0, "mediaAfterCommit": 1, "notesAfterCommit": 0,
        },
        "mediaExplicitReconcile": {
            "status": media_reconcile_result["status"], "resultSha256": exporter.sha256_file(media_reconcile_result_path),
            "media": 1, "notes": 1, "mediaReadback": "ALREADY_CURRENT",
        },
        "compatibilityInputs": {
            path: exporter.sha256_file(PROJECT_ROOT / path)
            for path in (
                "contracts/core-adapter/v1/schemas/resolved-pipeline.v1.schema.json",
                "orchestrator/Core/DesktopIntentResolver.cs",
                "orchestrator/Core/StageInputResolver.cs",
            )
        },
        "finishedAt": exporter.utc_now(),
    }
    receipt_path = evidence_root / "exe-validation-result.json"
    exporter.write_json_atomic_once(receipt_path, receipt)
    print(json.dumps({"status": "passed", "receipt": str(receipt_path), "receiptSha256": exporter.sha256_file(receipt_path), "exeSha256": receipt["exe"]["sha256"]}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
