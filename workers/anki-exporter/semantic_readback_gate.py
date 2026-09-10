#!/usr/bin/env python3
"""Offline, fail-closed preview-to-Anki semantic readback verifier.

The verifier performs no network calls.  Quality/runner supplies immutable
preview, confirmation command, WorkerRequest/WorkerResult, artifact manifest,
and readback files through one SHA-bound gate manifest.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
from pathlib import Path, PurePosixPath
from typing import Any


SHA256 = re.compile(r"^[a-f0-9]{64}$")
REQUIRED_EVIDENCE = (
    "preview",
    "confirmationCommand",
    "workerRequest",
    "workerResult",
    "artifactManifest",
    "readback",
)


class GateError(Exception):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


def require(condition: bool, code: str, message: str) -> None:
    if not condition:
        raise GateError(code, message)


def canonical_bytes(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise GateError("EVIDENCE_JSON_INVALID", f"Cannot read JSON evidence {path.name}") from exc
    require(isinstance(value, dict), "EVIDENCE_JSON_INVALID", f"{path.name} must contain a JSON object")
    return value


def resolve_evidence(root: Path, relative: str) -> Path:
    require(isinstance(relative, str) and relative and "\\" not in relative, "EVIDENCE_PATH_INVALID", "Evidence path must be canonical POSIX relative text")
    pure = PurePosixPath(relative)
    require(not pure.is_absolute() and all(part not in {"", ".", ".."} for part in pure.parts), "EVIDENCE_PATH_INVALID", f"Evidence path is not canonical: {relative}")
    candidate = (root / Path(*pure.parts)).resolve()
    boundary = os.path.commonpath((str(root), str(candidate)))
    require(boundary == str(root), "EVIDENCE_PATH_INVALID", f"Evidence path escapes package root: {relative}")
    require(candidate.is_file(), "EVIDENCE_FILE_MISSING", f"Evidence file is missing: {relative}")
    return candidate


def load_evidence(manifest_path: Path) -> tuple[dict[str, Any], dict[str, dict[str, Any]], list[dict[str, Any]]]:
    manifest_path = manifest_path.resolve()
    manifest = load_json(manifest_path)
    require(
        set(manifest) == {"schemaVersion", "expectedCardCount", "expectedDeck", "evidence"},
        "GATE_MANIFEST_INVALID",
        "Gate manifest fields are not exact",
    )
    require(manifest.get("schemaVersion") == "anki-semantic-readback-gate-input.v1", "GATE_MANIFEST_INVALID", "Unsupported gate manifest schemaVersion")
    expected_count = manifest.get("expectedCardCount")
    require(isinstance(expected_count, int) and not isinstance(expected_count, bool) and expected_count >= 1, "GATE_MANIFEST_INVALID", "expectedCardCount must be positive")
    require(isinstance(manifest.get("expectedDeck"), str) and manifest["expectedDeck"], "GATE_MANIFEST_INVALID", "expectedDeck is required")
    refs = manifest.get("evidence")
    require(isinstance(refs, dict) and set(refs) == set(REQUIRED_EVIDENCE), "GATE_MANIFEST_INVALID", "Evidence references are incomplete or unknown")
    values: dict[str, dict[str, Any]] = {}
    source_rows: list[dict[str, Any]] = []
    for name in REQUIRED_EVIDENCE:
        ref = refs[name]
        require(isinstance(ref, dict) and set(ref) == {"path", "sha256"}, "GATE_MANIFEST_INVALID", f"Evidence reference {name} is invalid")
        expected_sha = ref.get("sha256")
        require(isinstance(expected_sha, str) and SHA256.fullmatch(expected_sha), "GATE_MANIFEST_INVALID", f"Evidence SHA is invalid: {name}")
        path = resolve_evidence(manifest_path.parent, ref.get("path"))
        actual_sha = sha256_file(path)
        require(actual_sha == expected_sha, "EVIDENCE_HASH_MISMATCH", f"Evidence SHA mismatch: {name}")
        values[name] = load_json(path)
        source_rows.append({"name": name, "path": ref["path"], "sha256": actual_sha})
    return manifest, values, source_rows


def required_string(value: dict[str, Any], name: str, code: str) -> str:
    result = value.get(name)
    require(isinstance(result, str) and result != "", code, f"{name} must be non-empty text")
    return result


def verify(manifest_path: Path) -> dict[str, Any]:
    manifest, evidence, source_rows = load_evidence(manifest_path)
    preview = evidence["preview"]
    confirmation = evidence["confirmationCommand"]
    request = evidence["workerRequest"]
    result = evidence["workerResult"]
    artifact = evidence["artifactManifest"]
    readback = evidence["readback"]
    expected_count = manifest["expectedCardCount"]
    expected_deck = manifest["expectedDeck"]

    require(preview.get("schemaVersion") == "anki-card-preview.v1", "PREVIEW_INVALID", "Preview schema is unsupported")
    operation_id = required_string(preview, "operationId", "PREVIEW_INVALID")
    draft = preview.get("cardDraft")
    require(isinstance(draft, dict), "PREVIEW_INVALID", "Preview cardDraft is required")
    source_operation = required_string(draft, "sourceOperation", "PREVIEW_INVALID")
    token = required_string(draft, "token", "PREVIEW_INVALID")
    revision = required_string(draft, "revision", "PREVIEW_INVALID")
    digest = required_string(draft, "digest", "PREVIEW_INVALID")
    preview_cards = draft.get("cards")
    require(isinstance(preview_cards, list) and len(preview_cards) == expected_count, "CARDINALITY_MISMATCH", "Preview card count differs from expectedCardCount")
    stable_ids: list[str] = []
    for ordinal, card in enumerate(preview_cards):
        require(isinstance(card, dict), "PREVIEW_INVALID", f"Preview card {ordinal} is invalid")
        card_id = required_string(card, "cardId", "PREVIEW_INVALID")
        front = required_string(card, "front", "PREVIEW_INVALID")
        back = required_string(card, "back", "PREVIEW_INVALID")
        template = required_string(card, "template", "PREVIEW_INVALID")
        card_sha = required_string(card, "cardSha256", "PREVIEW_INVALID")
        require(card.get("revision") == revision, "PREVIEW_REVISION_MISMATCH", f"Preview card revision drifted at ordinal {ordinal}")
        calculated = sha256_bytes(f"card\n{revision}\n{ordinal}\n{card_id}\n{template}\n{front}\n{back}".encode("utf-8"))
        require(card_sha == calculated, "PREVIEW_CARD_HASH_MISMATCH", f"Preview card hash mismatch at ordinal {ordinal}")
        metadata = card.get("metadata")
        if metadata is not None:
            require(isinstance(metadata, dict) and metadata.get("ordinal") == ordinal, "PREVIEW_ORDER_MISMATCH", f"Preview metadata ordinal mismatch at {ordinal}")
        require(card_id not in stable_ids, "DUPLICATE_STABLE_CARD_ID", f"Duplicate stable card identity: {card_id}")
        stable_ids.append(card_id)

    require(confirmation.get("schema") == "kc.command/2", "CONFIRMATION_INVALID", "Confirmation command schema is unsupported")
    command_id = required_string(confirmation, "commandId", "CONFIRMATION_INVALID")
    require(confirmation.get("operationId") == operation_id and confirmation.get("operation") == source_operation, "CONFIRMATION_BINDING_MISMATCH", "Confirmation command operation binding drifted")
    authorization = confirmation.get("authorization")
    require(isinstance(authorization, dict), "CONFIRMATION_AUTHORIZATION_INVALID", "Confirmation authorization is required")
    authorization_id = required_string(authorization, "authorizationId", "CONFIRMATION_AUTHORIZATION_INVALID")
    require(authorization.get("policy") == "isolated-canary-deck", "CONFIRMATION_AUTHORIZATION_INVALID", "Confirmation is not isolated-canary-deck authorized")
    body = confirmation.get("body")
    require(isinstance(body, dict) and isinstance(body.get("data"), dict), "CONFIRMATION_INVALID", "Confirmation command body is invalid")
    body_data = body["data"]
    require(body_data.get("action") == "confirm_card_draft" and body_data.get("sourceOperationId") == operation_id, "CONFIRMATION_BINDING_MISMATCH", "Confirmation action/source operation drifted")
    provided_draft = body_data.get("draft")
    require(isinstance(provided_draft, dict), "CONFIRMATION_BINDING_MISMATCH", "Confirmation draft binding is missing")
    require(
        provided_draft.get("token") == token and provided_draft.get("revision") == revision and provided_draft.get("digest") == digest,
        "CONFIRMATION_BINDING_MISMATCH",
        "Confirmation token/revision/digest does not bind the preview",
    )

    require(request.get("workerId") == "export.anki" and request.get("operation") == "export", "WORKER_REQUEST_INVALID", "Final worker request is not export.anki export")
    require(request.get("operationId") == operation_id, "WORKER_REQUEST_BINDING_MISMATCH", "WorkerRequest operationId differs from preview/confirmation")
    request_payload = request.get("payload")
    require(isinstance(request_payload, dict), "WORKER_REQUEST_INVALID", "WorkerRequest payload is missing")
    request_authorization = request_payload.get("sideEffectAuthorization")
    require(
        isinstance(request_authorization, dict)
        and request_authorization.get("authorizationId") == authorization_id
        and request_authorization.get("mode") == "explicit-user-action"
        and request_authorization.get("scope") == authorization.get("policy"),
        "WORKER_REQUEST_AUTHORIZATION_MISMATCH",
        "WorkerRequest authorization differs from the confirmation command",
    )
    request_cards = request_payload.get("expectedCards")
    require(request_payload.get("expectedCardCount") == expected_count and isinstance(request_cards, list) and len(request_cards) == expected_count, "CARDINALITY_MISMATCH", "WorkerRequest expected card count differs from preview")
    require(request_payload.get("expectedDeck") == expected_deck, "DECK_MISMATCH", "WorkerRequest expected deck differs from gate")
    for ordinal, (preview_card, request_card) in enumerate(zip(preview_cards, request_cards, strict=True)):
        require(
            isinstance(request_card, dict)
            and request_card.get("cardId") == preview_card["cardId"]
            and request_card.get("front") == preview_card["front"]
            and request_card.get("back") == preview_card["back"],
            "WORKER_REQUEST_CARD_MISMATCH",
            f"WorkerRequest card identity/content/order differs from preview at ordinal {ordinal}",
        )

    for name in ("requestId", "operationId", "taskId", "runId", "stageId", "attemptId", "idempotencyKey"):
        require(result.get(name) == request.get(name), "WORKER_RESULT_BINDING_MISMATCH", f"WorkerResult {name} differs from WorkerRequest")
    result_worker = result.get("worker")
    require(isinstance(result_worker, dict) and result_worker.get("id") == "export.anki", "WORKER_RESULT_INVALID", "WorkerResult identity is invalid")
    require(result.get("status") == "completed" and result.get("error") is None, "WORKER_RESULT_INVALID", "WorkerResult is not completed")
    outputs = result.get("outputs")
    side_effects = result.get("sideEffects")
    require(isinstance(outputs, list) and len(outputs) == 1 and outputs[0].get("kind") == "anki-export-evidence", "WORKER_RESULT_INVALID", "WorkerResult export evidence cardinality is invalid")
    require(isinstance(side_effects, list) and len(side_effects) == 1 and side_effects[0].get("kind") == "anki.export" and side_effects[0].get("state") == "verified", "WORKER_RESULT_INVALID", "WorkerResult lacks one verified operation-level side effect")
    require(side_effects[0].get("receiptRef") == outputs[0], "WORKER_RESULT_INVALID", "WorkerResult side-effect receipt does not bind the output")
    diagnostics = result.get("itemDiagnostics")
    require(isinstance(diagnostics, list) and [item.get("itemId") for item in diagnostics] == stable_ids, "WORKER_RESULT_ORDER_MISMATCH", "WorkerResult item order differs from preview")

    output_ref = outputs[0]
    artifact_source = next(item for item in source_rows if item["name"] == "artifactManifest")
    artifact_evidence_ref = manifest["evidence"]["artifactManifest"]
    require(
        output_ref.get("manifestPath") == artifact_evidence_ref["path"],
        "ARTIFACT_BINDING_MISMATCH",
        "WorkerResult manifestPath differs from the supplied artifact manifest",
    )
    require(artifact_source["sha256"] == output_ref.get("expectedSha256"), "ARTIFACT_BINDING_MISMATCH", "Artifact manifest SHA differs from WorkerResult")
    require(artifact.get("artifactId") == output_ref.get("artifactRef") and artifact.get("kind") == output_ref.get("kind") and artifact.get("contractVersion") == output_ref.get("contractVersion"), "ARTIFACT_BINDING_MISMATCH", "Artifact manifest identity differs from WorkerResult")
    producer = artifact.get("producer")
    require(
        isinstance(producer, dict)
        and producer.get("workerId") == "export.anki"
        and producer.get("workerVersion") == result_worker.get("version")
        and producer.get("stageId") == request.get("stageId")
        and producer.get("attemptId") == request.get("attemptId"),
        "ARTIFACT_BINDING_MISMATCH",
        "Artifact producer differs from the final worker attempt",
    )
    metadata = artifact.get("metadata")
    readback_source = next(item for item in source_rows if item["name"] == "readback")
    require(isinstance(metadata, dict) and isinstance(metadata.get("readback"), dict), "ARTIFACT_BINDING_MISMATCH", "Artifact readback metadata is missing")
    metadata_readback = metadata["readback"]
    readback_evidence_ref = manifest["evidence"]["readback"]
    require(
        metadata_readback.get("path") == readback_evidence_ref["path"]
        and metadata_readback.get("schemaVersion") == "anki-export-readback.v1"
        and metadata_readback.get("sha256") == readback_source["sha256"],
        "ARTIFACT_BINDING_MISMATCH",
        "Artifact readback metadata path/SHA/schema differs",
    )
    declared = [item for item in artifact.get("files", []) if isinstance(item, dict) and item.get("path") == metadata_readback.get("path")]
    require(len(declared) == 1 and declared[0].get("sha256") == readback_source["sha256"], "ARTIFACT_BINDING_MISMATCH", "Artifact files do not bind readback.json")

    require(readback.get("schemaVersion") == "anki-export-readback.v1", "READBACK_INVALID", "Readback schema is unsupported")
    require(readback.get("worker") == {"workerId": "export.anki", "workerVersion": result_worker.get("version")}, "READBACK_BINDING_MISMATCH", "Readback worker differs from WorkerResult")
    for name in ("taskId", "runId", "stageId", "attemptId"):
        require(readback.get(name) == request.get(name), "READBACK_BINDING_MISMATCH", f"Readback {name} differs from WorkerRequest")
    readback_cards = readback.get("cards")
    require(readback.get("cardCount") == expected_count and isinstance(readback_cards, list) and len(readback_cards) == expected_count, "CARDINALITY_MISMATCH", "Readback count differs from confirmed preview")
    require(readback.get("cardsSha256") == sha256_bytes(canonical_bytes(readback_cards)), "READBACK_HASH_MISMATCH", "Readback cardsSha256 is not reproducible")

    seen_notes: set[int] = set()
    seen_cards: set[int] = set()
    mapped_cards: list[dict[str, Any]] = []
    for ordinal, (preview_card, row) in enumerate(zip(preview_cards, readback_cards, strict=True)):
        require(isinstance(row, dict) and row.get("ordinal") == ordinal and row.get("sourceCardId") == preview_card["cardId"], "READBACK_ORDER_MISMATCH", f"Readback stable identity/order differs at ordinal {ordinal}")
        note = row.get("notesInfo")
        cards_info = row.get("cardsInfo")
        require(isinstance(note, dict) and isinstance(cards_info, list) and len(cards_info) == 1, "READBACK_IDENTITY_INVALID", f"Readback note/card identity is invalid at ordinal {ordinal}")
        note_id = note.get("noteId")
        card_ids = note.get("cardIds")
        require(isinstance(note_id, int) and note_id > 0 and isinstance(card_ids, list) and len(card_ids) == 1 and isinstance(card_ids[0], int) and card_ids[0] > 0, "READBACK_IDENTITY_INVALID", f"Readback Anki IDs are invalid at ordinal {ordinal}")
        card_info = cards_info[0]
        require(card_info.get("noteId") == note_id and card_info.get("cardId") == card_ids[0], "READBACK_IDENTITY_INVALID", f"cardsInfo ownership differs from notesInfo at ordinal {ordinal}")
        fields = note.get("fields")
        require(
            isinstance(fields, dict)
            and fields.get("RQC_ID") == preview_card["cardId"]
            and fields.get("Front") == preview_card["front"]
            and fields.get("Back") == preview_card["back"],
            "READBACK_CONTENT_MISMATCH",
            f"Readback stable identity/Front/Back differs from preview at ordinal {ordinal}",
        )
        require(card_info.get("deckName") == expected_deck, "DECK_MISMATCH", f"Readback deck differs at ordinal {ordinal}")
        semantic_body = {"notesInfo": note, "cardsInfo": cards_info}
        require(row.get("semanticBodySha256") == sha256_bytes(canonical_bytes(semantic_body)), "READBACK_HASH_MISMATCH", f"Readback semantic body hash differs at ordinal {ordinal}")
        require(note_id not in seen_notes and card_ids[0] not in seen_cards, "READBACK_IDENTITY_INVALID", "Readback reuses an Anki note/card identity")
        seen_notes.add(note_id)
        seen_cards.add(card_ids[0])
        mapped_cards.append({
            "ordinal": ordinal,
            "stableCardId": preview_card["cardId"],
            "previewCardSha256": preview_card["cardSha256"],
            "template": preview_card["template"],
            "front": preview_card["front"],
            "back": preview_card["back"],
            "deck": expected_deck,
            "noteId": note_id,
            "cardId": card_ids[0],
            "outcome": row.get("outcome"),
        })

    return {
        "schemaVersion": "anki-semantic-readback-gate.v1",
        "status": "passed",
        "verificationEffect": {
            "scope": "offline-evidence-verification-only",
            "externalApiCalls": 0,
            "externalMutation": False,
            "priorScenarioWritesInferred": False,
        },
        "binding": {
            "operationId": operation_id,
            "confirmationCommandId": command_id,
            "previewToken": token,
            "previewRevision": revision,
            "previewDigest": digest,
            "workerRequestId": request["requestId"],
            "workerAttemptId": request["attemptId"],
            "workerVersion": result_worker["version"],
            "expectedDeck": expected_deck,
            "expectedCardCount": expected_count,
        },
        "assertions": {
            "exactCardinality": True,
            "exactStableIdentity": True,
            "exactOrder": True,
            "exactFrontBack": True,
            "exactDeck": True,
            "exactNoteCardOwnership": True,
            "shaBoundEvidence": True,
        },
        "cards": mapped_cards,
        "evidence": source_rows,
    }


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv or sys.argv[1:])
    try:
        receipt = verify(args.manifest)
        encoded = json.dumps(receipt, ensure_ascii=False, sort_keys=True, indent=2) + "\n"
        if args.output is not None:
            output = args.output.resolve()
            output.parent.mkdir(parents=True, exist_ok=True)
            try:
                with output.open("x", encoding="utf-8", newline="\n") as handle:
                    handle.write(encoded)
            except FileExistsError as exc:
                raise GateError("OUTPUT_EXISTS", f"Append-only output already exists: {output}") from exc
        print(encoded, end="")
        return 0
    except GateError as exc:
        print(json.dumps({
            "schemaVersion": "anki-semantic-readback-gate.v1",
            "status": "failed",
            "code": exc.code,
            "message": str(exc),
            "verificationEffect": {
                "scope": "offline-evidence-verification-only",
                "externalApiCalls": 0,
                "externalMutation": False,
                "priorScenarioWritesInferred": False,
            },
        }, ensure_ascii=False, sort_keys=True), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
