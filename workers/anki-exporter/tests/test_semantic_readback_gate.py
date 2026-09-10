import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker


WORKER = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(WORKER))

import semantic_readback_gate as gate


def dump(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2) + "\n", encoding="utf-8")


def semantic_row(ordinal, preview_card, note_id, card_id, deck):
    fields = {
        "RQC_ID": preview_card["cardId"],
        "Front": preview_card["front"],
        "Back": preview_card["back"],
        "Context": "",
        "Extra": "",
        "Source": "offline semantic gate fixture",
        "RQC_Revision": "1",
        "RQC_ContentHash": gate.sha256_bytes((preview_card["cardId"] + "\ncontent").encode("utf-8")),
    }
    notes = {
        "noteId": note_id,
        "cardIds": [card_id],
        "fields": fields,
        "requiredFieldNames": ["Front", "Back"],
        "requiredFields": {"Front": fields["Front"], "Back": fields["Back"]},
        "requiredTags": ["quality_canary", "rqc_managed"],
        "tags": ["quality_canary", "rqc_managed"],
    }
    cards = [{"cardId": card_id, "noteId": note_id, "deckName": deck}]
    body = {"notesInfo": notes, "cardsInfo": cards}
    return {
        "ordinal": ordinal,
        "sourceCardId": preview_card["cardId"],
        "outcome": "ALREADY_CURRENT",
        **body,
        "semanticBodySha256": gate.sha256_bytes(gate.canonical_bytes(body)),
    }


def refresh_fixture(root):
    readback_path = root / "readback.json"
    readback = json.loads(readback_path.read_text(encoding="utf-8"))
    for row in readback["cards"]:
        body = {"notesInfo": row["notesInfo"], "cardsInfo": row["cardsInfo"]}
        row["semanticBodySha256"] = gate.sha256_bytes(gate.canonical_bytes(body))
    readback["cardsSha256"] = gate.sha256_bytes(gate.canonical_bytes(readback["cards"]))
    dump(readback_path, readback)

    artifact_path = root / "artifact.json"
    artifact = json.loads(artifact_path.read_text(encoding="utf-8"))
    readback_sha = gate.sha256_file(readback_path)
    artifact["files"] = [{
        "path": "readback.json",
        "sha256": readback_sha,
        "bytes": readback_path.stat().st_size,
        "mediaType": "application/json",
    }]
    artifact["metadata"]["readback"] = {
        "path": "readback.json",
        "sha256": readback_sha,
        "schemaVersion": "anki-export-readback.v1",
    }
    dump(artifact_path, artifact)

    result_path = root / "worker-result.json"
    result = json.loads(result_path.read_text(encoding="utf-8"))
    output = result["outputs"][0]
    output["expectedSha256"] = gate.sha256_file(artifact_path)
    result["sideEffects"][0]["receiptRef"] = copy.deepcopy(output)
    dump(result_path, result)

    manifest_path = root / "gate-input.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    for name, ref in manifest["evidence"].items():
        ref["sha256"] = gate.sha256_file(root / ref["path"])
    dump(manifest_path, manifest)
    return manifest_path


def build_fixture(root):
    operation_id = "operation-offline-semantic-gate"
    revision = "a" * 64
    cards = [
        {
            "cardId": "stable-language-context",
            "revision": revision,
            "front": "The material remains resilient under change.",
            "back": "resilient /rɪˈzɪliənt/ 有韧性的",
            "template": "language_context",
            "metadata": {"ordinal": 0, "sourceArtifactId": "artifact-card-bundle"},
        },
        {
            "cardId": "stable-language-writing",
            "revision": revision,
            "front": "The material remains （/rɪˈzɪliənt/；有韧性的） under change.",
            "back": "resilient",
            "template": "language_writing",
            "metadata": {"ordinal": 1, "sourceArtifactId": "artifact-card-bundle"},
        },
    ]
    for ordinal, card in enumerate(cards):
        card["cardSha256"] = gate.sha256_bytes(
            f"card\n{revision}\n{ordinal}\n{card['cardId']}\n{card['template']}\n{card['front']}\n{card['back']}".encode("utf-8")
        )
    preview = {
        "schemaVersion": "anki-card-preview.v1",
        "operationId": operation_id,
        "cardDraft": {
            "sourceOperation": "clip.read-aloud-cards",
            "token": "b" * 64,
            "revision": revision,
            "digest": "c" * 64,
            "cards": cards,
        },
    }
    confirmation = {
        "schema": "kc.command/2",
        "commandId": "command-confirm-offline-semantic-gate",
        "operationId": operation_id,
        "operation": "clip.read-aloud-cards",
        "subject": {"type": "operation", "id": operation_id},
        "idempotencyKey": "d" * 64,
        "expectedRevision": None,
        "authorization": {"authorizationId": "authorization-offline-canary", "policy": "isolated-canary-deck"},
        "inputs": [],
        "body": {
            "schema": "kc.read-aloud-cards-command/1",
            "data": {
                "action": "confirm_card_draft",
                "sourceOperationId": operation_id,
                "draft": {"token": "b" * 64, "revision": revision, "digest": "c" * 64},
            },
        },
    }
    expected_deck = "零碎知识剪藏::Canary::SemanticGate"
    request = {
        "schemaVersion": "1.0.0",
        "requestId": "request-offline-semantic-gate",
        "operationId": operation_id,
        "taskId": "task-offline-semantic-gate",
        "runId": "run-offline-semantic-gate",
        "stageId": "anki-export",
        "attemptId": "attempt-offline-semantic-gate",
        "workerId": "export.anki",
        "operation": "export",
        "idempotencyKey": "e" * 64,
        "payload": {
            "sideEffectAuthorization": {"authorizationId": "authorization-offline-canary", "mode": "explicit-user-action", "scope": "isolated-canary-deck"},
            "expectedCardCount": 2,
            "expectedCards": [{"cardId": item["cardId"], "front": item["front"], "back": item["back"]} for item in cards],
            "expectedDeck": expected_deck,
        },
    }
    result_identity = {name: request[name] for name in ("requestId", "operationId", "taskId", "runId", "stageId", "attemptId", "idempotencyKey")}
    output = {
        "artifactRef": "anki-evidence-offline-semantic-gate",
        "kind": "anki-export-evidence",
        "contractVersion": "1.0.0",
        "manifestPath": "artifact.json",
        "expectedSha256": "0" * 64,
    }
    result = {
        "schemaVersion": "1.0.0",
        **result_identity,
        "worker": {"id": "export.anki", "version": "1.5.0"},
        "contractVersion": "1.0.0",
        "status": "completed",
        "outputs": [output],
        "itemDiagnostics": [{"itemId": item["cardId"], "status": "completed", "outputs": [], "error": None} for item in cards],
        "warnings": [],
        "error": None,
        "sideEffects": [{"kind": "anki.export", "idempotencyKey": request["idempotencyKey"], "state": "verified", "receiptRef": copy.deepcopy(output)}],
        "metrics": {"succeeded": 0, "already_current": 2, "blocked_upstream": 0, "failed": 0},
        "payload": {"moduleStatus": "SUCCEEDED", "validateOnly": False, "operation": "export"},
    }
    readback_cards = [
        semantic_row(0, cards[0], 11001, 21001, expected_deck),
        semantic_row(1, cards[1], 11002, 21002, expected_deck),
    ]
    readback = {
        "schemaVersion": "anki-export-readback.v1",
        "worker": {"workerId": "export.anki", "workerVersion": "1.5.0"},
        "taskId": request["taskId"],
        "runId": request["runId"],
        "stageId": request["stageId"],
        "attemptId": request["attemptId"],
        "dataClass": "personal",
        "redaction": "excludes-profile-endpoint-model-user-notes-and-secrets",
        "api": {"version": 6, "actions": ["notesInfo", "cardsInfo"]},
        "sourceBundle": {"bundleId": "bundle-offline-semantic-gate", "sha256": "f" * 64},
        "cardCount": 2,
        "cards": readback_cards,
        "cardsSha256": gate.sha256_bytes(gate.canonical_bytes(readback_cards)),
    }
    artifact = {
        "schemaVersion": "1.0.0",
        "artifactId": output["artifactRef"],
        "kind": output["kind"],
        "contractVersion": output["contractVersion"],
        "mediaType": "application/vnd.rqc.anki-export-evidence+json",
        "files": [],
        "createdAt": "2026-09-08T00:00:00Z",
        "producer": {"stageId": request["stageId"], "workerId": "export.anki", "workerVersion": "1.5.0", "attemptId": request["attemptId"]},
        "inputRefs": [],
        "dataClass": "personal",
        "metadata": {"entrypoint": "receipt.json", "readback": {}},
    }
    files = {
        "preview.json": preview,
        "confirmation-command.json": confirmation,
        "worker-request.json": request,
        "worker-result.json": result,
        "artifact.json": artifact,
        "readback.json": readback,
    }
    for name, value in files.items():
        dump(root / name, value)
    gate_manifest = {
        "schemaVersion": "anki-semantic-readback-gate-input.v1",
        "expectedCardCount": 2,
        "expectedDeck": expected_deck,
        "evidence": {
            "preview": {"path": "preview.json", "sha256": "0" * 64},
            "confirmationCommand": {"path": "confirmation-command.json", "sha256": "0" * 64},
            "workerRequest": {"path": "worker-request.json", "sha256": "0" * 64},
            "workerResult": {"path": "worker-result.json", "sha256": "0" * 64},
            "artifactManifest": {"path": "artifact.json", "sha256": "0" * 64},
            "readback": {"path": "readback.json", "sha256": "0" * 64},
        },
    }
    dump(root / "gate-input.json", gate_manifest)
    return refresh_fixture(root)


class SemanticReadbackGateTests(unittest.TestCase):
    def test_valid_two_card_fixture_maps_every_semantic_field_and_id(self):
        with tempfile.TemporaryDirectory() as temp:
            manifest_path = build_fixture(Path(temp))
            input_schema = json.loads((WORKER / "contracts" / "anki-semantic-readback-gate-input.v1.schema.json").read_text(encoding="utf-8"))
            receipt_schema = json.loads((WORKER / "contracts" / "anki-semantic-readback-gate.v1.schema.json").read_text(encoding="utf-8"))
            Draft202012Validator(input_schema, format_checker=FormatChecker()).validate(json.loads(manifest_path.read_text(encoding="utf-8")))
            receipt = gate.verify(manifest_path)
            Draft202012Validator(receipt_schema, format_checker=FormatChecker()).validate(receipt)
            self.assertEqual("passed", receipt["status"])
            self.assertEqual([0, 1], [item["ordinal"] for item in receipt["cards"]])
            self.assertEqual(["stable-language-context", "stable-language-writing"], [item["stableCardId"] for item in receipt["cards"]])
            self.assertEqual([11001, 11002], [item["noteId"] for item in receipt["cards"]])
            self.assertEqual([21001, 21002], [item["cardId"] for item in receipt["cards"]])
            self.assertEqual(0, receipt["verificationEffect"]["externalApiCalls"])
            self.assertFalse(receipt["verificationEffect"]["priorScenarioWritesInferred"])

    def test_count_two_but_reordered_readback_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            manifest = build_fixture(root)
            readback_path = root / "readback.json"
            value = json.loads(readback_path.read_text(encoding="utf-8"))
            value["cards"].reverse()
            for ordinal, row in enumerate(value["cards"]):
                row["ordinal"] = ordinal
            dump(readback_path, value)
            refresh_fixture(root)
            with self.assertRaises(gate.GateError) as caught:
                gate.verify(manifest)
            self.assertEqual("READBACK_ORDER_MISMATCH", caught.exception.code)

    def test_count_two_but_front_back_drift_is_rejected(self):
        for field in ("Front", "Back"):
            with self.subTest(field=field), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                manifest = build_fixture(root)
                readback_path = root / "readback.json"
                value = json.loads(readback_path.read_text(encoding="utf-8"))
                value["cards"][1]["notesInfo"]["fields"][field] = "wrong but non-empty"
                dump(readback_path, value)
                refresh_fixture(root)
                with self.assertRaises(gate.GateError) as caught:
                    gate.verify(manifest)
                self.assertEqual("READBACK_CONTENT_MISMATCH", caught.exception.code)

    def test_count_two_but_deck_or_note_card_ownership_drift_is_rejected(self):
        cases = {
            "deck": lambda value: value["cards"][1]["cardsInfo"][0].update({"deckName": "Default"}),
            "ownership": lambda value: value["cards"][1]["cardsInfo"][0].update({"noteId": 11001}),
        }
        for name, mutate in cases.items():
            with self.subTest(case=name), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                manifest = build_fixture(root)
                readback_path = root / "readback.json"
                value = json.loads(readback_path.read_text(encoding="utf-8"))
                mutate(value)
                dump(readback_path, value)
                refresh_fixture(root)
                with self.assertRaises(gate.GateError) as caught:
                    gate.verify(manifest)
                self.assertEqual("DECK_MISMATCH" if name == "deck" else "READBACK_IDENTITY_INVALID", caught.exception.code)

    def test_confirmation_or_worker_request_semantic_drift_is_rejected(self):
        cases = {
            "confirmation": ("confirmation-command.json", lambda value: value["body"]["data"]["draft"].update({"digest": "0" * 64}), "CONFIRMATION_BINDING_MISMATCH"),
            "request-order": ("worker-request.json", lambda value: value["payload"]["expectedCards"].reverse(), "WORKER_REQUEST_CARD_MISMATCH"),
        }
        for name, (file_name, mutate, expected_code) in cases.items():
            with self.subTest(case=name), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                manifest = build_fixture(root)
                path = root / file_name
                value = json.loads(path.read_text(encoding="utf-8"))
                mutate(value)
                dump(path, value)
                refresh_fixture(root)
                with self.assertRaises(gate.GateError) as caught:
                    gate.verify(manifest)
                self.assertEqual(expected_code, caught.exception.code)


if __name__ == "__main__":
    unittest.main()
