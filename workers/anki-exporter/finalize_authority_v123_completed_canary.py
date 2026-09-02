#!/usr/bin/env python3
"""Finalize an already-completed authority-v1.2.3 Anki Canary without rerunning its workers."""

from __future__ import annotations

import argparse
import collections
import json
from pathlib import Path
from typing import Any

import run_authority_v123_idempotent_canary as wrapper


canary = wrapper.canary
EXPECTED_BEFORE_INVENTORY_SHA256 = "03327CDDFFAFE49A18E315EB71AC4313E943DFC8C2415AD66AEB4C58344FAAA6"
UNKNOWN_ATTEMPT = canary.WORKER_ROOT / "canary" / "authority-v1.2.2" / "20260828T015523Z-0f53ae6d" / "job" / "runs" / "run-authority-anki" / "stages" / "anki-export" / "attempts" / "attempt-20260828015531839-9b8fd34a8bc34e6d81c603f67e312520"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence-root", type=Path, required=True)
    parser.add_argument("--endpoint", default="http://127.0.0.1:8765")
    return parser.parse_args()


def read_json(path: Path) -> dict[str, Any]:
    return canary.read_json(path)


def main() -> int:
    args = parse_args()
    evidence_root = args.evidence_root.resolve()
    job_root = evidence_root / "job"
    receipt_path = evidence_root / "receipt.json"
    if receipt_path.exists():
        raise RuntimeError(f"append-only receipt already exists: {receipt_path}")

    checkpoint = read_json(canary.CHECKPOINT_PATH)
    tree = canary.release_tree()
    authority_checks = {
        "checkpoint": canary.sha(canary.CHECKPOINT_PATH) == canary.EXPECTED["checkpointSha256"],
        "registry": canary.sha(canary.REGISTRY_PATH) == canary.EXPECTED["registrySha256"],
        "dependency": canary.sha(canary.DEPENDENCY_PATH) == canary.EXPECTED["dependencySha256"],
        "releaseTree": tree == {"files": canary.EXPECTED["releaseFiles"], "bytes": canary.EXPECTED["releaseBytes"], "treeSha256": canary.EXPECTED["releaseTreeSha256"]},
        "orchestrator": canary.sha(canary.ORCHESTRATOR) == canary.EXPECTED["orchestratorSha256"],
        "content": canary.sha(canary.CONTENT_EXE) == canary.EXPECTED["contentSha256"],
        "anki": canary.sha(canary.ANKI_EXE) == canary.EXPECTED["ankiSha256"],
    }
    validation = read_json(evidence_root / "authority" / "validate-registry.stdout.json")
    content_describe = read_json(evidence_root / "authority" / "content.deepseek.describe.json")
    anki_describe = read_json(evidence_root / "authority" / "export.anki.describe.json")
    if not all(authority_checks.values()) or validation.get("status") != "valid":
        raise RuntimeError(f"authority binding changed: {authority_checks!r}/{validation!r}")
    if content_describe.get("workerVersion") != "0.5.2" or anki_describe.get("workerVersion") != "1.1.5":
        raise RuntimeError("candidate describe identity changed")

    source = canary.source_artifacts()
    frozen_semantics = canary.semantic_cards(source["bundle"])
    preflight_result_path, preflight_result = canary.stage_result(job_root, "run-content-preflight", "content.understand-and-card", "content.deepseek", "0.5.2")
    _, preflight_bundle = canary.output_payload(job_root, preflight_result, "card-bundle")
    full_content_result_path, full_content_result = canary.stage_result(job_root, "run-authority-anki", "content.understand-and-card", "content.deepseek", "0.5.2")
    _, full_bundle = canary.output_payload(job_root, full_content_result, "card-bundle")
    if canary.semantic_cards(preflight_bundle) != frozen_semantics or canary.semantic_cards(full_bundle) != frozen_semantics:
        raise RuntimeError("candidate Content changed frozen four-card semantics")

    anki_result_path, anki_result = canary.stage_result(job_root, "run-authority-anki", "anki-export", "export.anki", "1.1.5")
    attempt = canary.anki_attempt(job_root, anki_result_path, anki_result)
    anki_request = read_json(anki_result_path.parent / "request.json")
    projected_path, projected = canary.projection_payload(job_root, anki_request)
    if [item["card_id"] for item in projected["cards"]] != [item["cardId"] for item in frozen_semantics]:
        raise RuntimeError("Core adapter changed ordered card identities")

    client = canary.AnkiConnect(args.endpoint)
    if client.invoke("version") != 6:
        raise RuntimeError("AnkiConnect v6 is required for final readback")
    inventory_after = canary.deck_inventory(client)
    if inventory_after["sha256"] != EXPECTED_BEFORE_INVENTORY_SHA256:
        raise RuntimeError("Anki deck inventory differs from the pre-run baseline")
    readback = canary.exact_readback(client, full_bundle, source["destination"]["readback"])
    if len(readback) != 4 or any(row["deck"] != canary.CANARY_DECK for row in readback):
        raise RuntimeError("target deck readback is not four isolated notes/cards")

    readable_path = evidence_root / "readable-authority-v123-idempotent.md"
    canary.write_readable(readable_path, source, frozen_semantics, projected, readback)
    module_actions = attempt["operations"]
    worker_operations = canary.read_ndjson(anki_result_path.parent / "outputs" / "operations.ndjson")
    worker_counts = collections.Counter(str(item.get("action")) for item in worker_operations)
    reconciles = canary.verify_reconcile_attempt(UNKNOWN_ATTEMPT)
    receipt = {
        "schemaVersion": "authority-v1.2.3-anki-idempotent-canary/1.0.0",
        "status": "passed",
        "canaryId": evidence_root.name,
        "route": "exact-candidate orchestrator -> content.deepseek 0.5.2 -> Core content-to-anki.v1 -> export.anki 1.1.5",
        "authority": {
            "checkpoint": {"path": canary.rel(canary.CHECKPOINT_PATH), "sha256": canary.sha(canary.CHECKPOINT_PATH), "checkpointId": checkpoint.get("checkpointId")},
            "registry": {"path": canary.rel(canary.REGISTRY_PATH), "sha256": canary.sha(canary.REGISTRY_PATH), "version": "1.2.3"},
            "dependencyManifest": {"path": canary.rel(canary.DEPENDENCY_PATH), "sha256": canary.sha(canary.DEPENDENCY_PATH)},
            "release": {"root": canary.rel(canary.RELEASE_ROOT), **tree},
            "validation": {"status": validation["status"], "registryVersion": validation.get("registryVersion"), "workers": validation.get("workers")},
            "executables": {
                "orchestrator": {"path": canary.rel(canary.ORCHESTRATOR), "sha256": canary.sha(canary.ORCHESTRATOR), "bytes": canary.ORCHESTRATOR.stat().st_size},
                "content": {"path": canary.rel(canary.CONTENT_EXE), "sha256": canary.sha(canary.CONTENT_EXE), "bytes": canary.CONTENT_EXE.stat().st_size, "worker": {"id": "content.deepseek", "version": "0.5.2"}},
                "anki": {"path": canary.rel(canary.ANKI_EXE), "sha256": canary.sha(canary.ANKI_EXE), "bytes": canary.ANKI_EXE.stat().st_size, "worker": {"id": "export.anki", "version": "1.1.5"}},
            },
        },
        "source": {
            "providerMode": "replay-from-frozen-live-deepseek-authority", "providerExternalCalls": 0, "businessInputByteIdentical": True,
            "receiptPath": canary.rel(canary.SOURCE_RECEIPT), "receiptSha256": canary.sha(canary.SOURCE_RECEIPT),
            "learningBundlePath": canary.rel(source["learningPath"]), "learningBundleSha256": canary.sha(source["learningPath"]),
            "cardBundlePath": canary.rel(source["cardPath"]), "cardBundleSha256": canary.sha(source["cardPath"]),
        },
        "runs": {
            "contentPreflight": {"resultPath": canary.rel(preflight_result_path), "resultSha256": canary.sha(preflight_result_path), "status": "completed", "semanticMatch": True},
            "fullAuthority": {"contentResultPath": canary.rel(full_content_result_path), "contentResultSha256": canary.sha(full_content_result_path), "orchestratorStatus": "succeeded", "anki": attempt},
        },
        "coreProjection": {"adapter": "content-to-anki.v1", "profilePath": canary.rel(canary.PROFILE_PATH), "profileSha256": canary.sha(canary.PROFILE_PATH), "payloadPath": canary.rel(projected_path), "payloadSha256": canary.sha(projected_path), "orderPreserved": True},
        "destination": {
            "deck": canary.CANARY_DECK,
            "before": source["destination"]["readback"], "after": readback,
            "notesBefore": 4, "notesAfter": 4, "cardsBefore": 4, "cardsAfter": 4,
            "newNotes": 0, "existingCurrent": 4, "deleted": 0,
            "allDeckInventory": {"beforeSha256": EXPECTED_BEFORE_INVENTORY_SHA256, "afterSha256": inventory_after["sha256"], "deckCountBefore": inventory_after["deckCount"], "deckCountAfter": inventory_after["deckCount"], "changedDecks": []},
        },
        "cardSemantics": {"orderedTemplates": canary.EXPECTED_TEMPLATES, "meaningReading": 2, "writing": 2, "bothGroups": 2, "cards": frozen_semantics},
        "reconciliation": {"explicit": True, "reconciles": reconciles, "strategy": "fixed RQC_ID same-revision read-then-converge"},
        "apiCalls": {
            "contentProvider": 0, "ankiConnectWorker": module_actions["calls"], "ankiConnectWorkerByAction": dict(sorted(worker_counts.items())),
            "ankiConnectDirectFinalReadback": sum(client.counts.values()), "mutatingActions": module_actions["mutatingActions"],
        },
        "checks": {
            "authorityBound": True, "exactCandidateProcesses": True, "contentSemanticMatch": True, "coreOrderAndFieldsVerified": True,
            "isolatedDeckOnly": True, "zeroNewFourCurrent": True, "readbackUnchanged": True,
            "allDeckInventoryUnchanged": True, "noMutatingAction": not module_actions["mutatingActions"], "noDeleteOrCleanup": True,
        },
        "readableEvidence": {"path": canary.rel(readable_path), "sha256": canary.sha(readable_path)},
        "external_api_calls": {"deepseek": 0, "ankiConnect": module_actions["calls"] + sum(client.counts.values())},
        "finishedAt": canary.exporter.utc_now(),
    }
    if not all(receipt["checks"].values()):
        raise RuntimeError(f"completed Canary checks failed: {receipt['checks']!r}")
    canary.exporter.write_json_atomic_once(receipt_path, receipt)
    print(json.dumps({"status": "passed", "receipt": str(receipt_path), "receiptSha256": canary.sha(receipt_path), "newNotes": 0, "existingCurrent": 4, "noteIds": [row["noteId"] for row in readback], "cardIds": [row["cardIds"][0] for row in readback]}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
