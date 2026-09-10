from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


DEFAULT_CANDIDATE = Path(r"D:\Artifacts\KnowledgeClipper\read-to-anki\rta-20260903T200942Z-b3b69727")
DEFAULT_CREATED_CANDIDATE = Path(r"D:\Artifacts\KnowledgeClipper\read-to-anki\rta-20260903T200740Z-ba3ee72e")
DEFAULT_FAILED_CANDIDATE = Path(r"D:\Artifacts\KnowledgeClipper\read-to-anki\rta-20260903T201107Z-0568b046")
DEFAULT_GATE_MATRIX = Path(r"D:\Artifacts\KnowledgeClipper\read-to-anki\quality\20260903T115121Z\flashcard-preview-gate-matrix.json")

EXPECTED = {
    "manifest": "1AE6547EEDAC4812B5D9FB8F67367E2A2E85F93DABCC3206F18A236931284D3D",
    "manifestFiles": 145,
    "manifestBytes": 172817632,
    "client": "64B4D2F86409621B34208F08F16D5DF917CE684B949410EFACB0E810F3B046DE",
    "anki": "102C105615DA449FEE48190452474431927E7E72493230647F409AA65378F8D0",
    "fakeReceipt": "E3002A15D97C69CC0D9BB2805FB9683E8D2F78AC3F610862C9D4F839671B4C63",
    "realReceipt": "1AC975B4C1552B3E9ED48AE2A282AAF0F4F78F1301FA1D3150E11CF577EC2867",
    "createdReceipt": "97D19D2898A628F110A883EF7A8A0CE6D567667ADDBEA30C90AF3E6814E314D5",
    "dependencyManifest": "294F1D935FF24320BE05B914FF723482C434FCAFE9B8A1CFBC8623DF47E6EF75",
    "gateMatrix": "B0EAE568A3F417D208990ACFD6569B300BAA1BD38E81F0FD205B04C973DC4A08",
}


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def file_evidence(path: Path) -> dict[str, Any]:
    return {"path": str(path.resolve()), "bytes": path.stat().st_size, "sha256": sha256_file(path)}


def manifest_entry(manifest: dict[str, Any], relative_path: str) -> dict[str, Any]:
    return next(item for item in manifest["files"] if item["path"] == relative_path)


def read_events(stream_root: Path) -> list[dict[str, Any]]:
    events: list[dict[str, Any]] = []
    for path in sorted(stream_root.glob("*.events.ndjson"), key=lambda item: item.name):
        events.extend(json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip())
    return sorted(events, key=lambda item: item["sequence"])


def find_one(paths: list[Path], label: str) -> Path:
    require(len(paths) == 1, f"expected one {label}, found {len(paths)}")
    return paths[0]


def evaluate(candidate: Path, created_candidate: Path, failed_candidate: Path, gate_matrix: Path) -> dict[str, Any]:
    candidate = candidate.resolve()
    manifest_path = candidate / "candidate-manifest.json"
    fake_receipt_path = candidate / "canary/fake-preview-confirm/receipt.json"
    real_receipt_path = candidate / "canary/real-preview-confirm-recovery/receipt.json"
    dependency_path = candidate / "authority/dependency-manifest.v1.json"

    require(sha256_file(manifest_path) == EXPECTED["manifest"], "candidate manifest SHA drift")
    require(sha256_file(fake_receipt_path) == EXPECTED["fakeReceipt"], "fake receipt SHA drift")
    require(sha256_file(real_receipt_path) == EXPECTED["realReceipt"], "real receipt SHA drift")
    require(sha256_file(dependency_path) == EXPECTED["dependencyManifest"], "dependency manifest SHA drift")
    require(sha256_file(gate_matrix) == EXPECTED["gateMatrix"], "quality gate matrix SHA drift")

    manifest = load(manifest_path)
    require(manifest["candidateId"] == candidate.name, "candidate identity mismatch")
    declared = manifest["files"]
    require(len(declared) == EXPECTED["manifestFiles"], "declared file count drift")
    require(sum(item["bytes"] for item in declared) == EXPECTED["manifestBytes"], "declared bytes drift")
    mismatches: list[str] = []
    for item in declared:
        path = candidate / item["path"]
        if not path.is_file() or path.stat().st_size != item["bytes"] or sha256_file(path) != item["sha256"].upper():
            mismatches.append(item["path"])
    require(not mismatches, f"manifest payload drift: {mismatches[:5]}")
    actual_files = sorted(path for path in candidate.rglob("*") if path.is_file())
    declared_paths = {item["path"] for item in declared}
    extras = sorted(path.relative_to(candidate).as_posix() for path in actual_files if path.relative_to(candidate).as_posix() not in declared_paths)
    unexpected_extras = [path for path in extras if path != "candidate-manifest.json" and not path.startswith("canary/")]
    require(not unexpected_extras, f"unexpected post-manifest files: {unexpected_extras}")

    client_path = candidate / "core-v3/KnowledgeClipper.Core.V3.Client.dll"
    anki_path = candidate / "authority/win-x64/workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe"
    require(sha256_file(client_path) == EXPECTED["client"], "Core Client SHA drift")
    require(sha256_file(anki_path) == EXPECTED["anki"], "Anki entrypoint SHA drift")

    dependency = load(dependency_path)
    require(dependency["portableRecursiveSchemaValidation"] == "passed", "dependency schema closure not passed")
    schema_a = candidate / "authority/win-x64/contracts/schemas/common/artifact.schema.json"
    schema_b = candidate / "authority/win-x64/contracts/v1/schemas/common/artifact.schema.json"
    require(schema_a.is_file() and schema_b.is_file(), "dual common schema layout missing")
    require(sha256_file(schema_a) == sha256_file(schema_b), "dual common schema copies drift")
    require((candidate / "authority/win-x64/workers/anki/contracts/anki-export-readback.v1.schema.json").is_file(), "Anki readback schema missing")

    real = load(real_receipt_path)
    require(real["status"] == "PASS" and Path(real["candidate"]).resolve() == candidate, "real receipt candidate binding drift")
    runtime_root = Path(real["runtimeDataRoot"])
    require(runtime_root.is_dir(), "runtime evidence root missing")
    job_root = find_one([path for path in (runtime_root / "worker-process-v3/jobs").iterdir() if path.is_dir()], "runtime job")
    task_manifest = load(job_root / "task.manifest.v1.json")
    task_id = task_manifest["taskId"]
    run_root = find_one([path for path in (job_root / "runs").iterdir() if path.is_dir()], "runtime run")
    operation_id = real["operationId"]
    stream_root = runtime_root / "events-v3/streams" / operation_id
    events = read_events(stream_root)
    require([item["sequence"] for item in events] == list(range(1, 17)), "event sequence is not contiguous 1..16")
    require(events[6]["data"]["result"]["status"] == "awaiting_card_confirmation", "initial flow did not stop at preview")
    require(events[6]["data"]["externalWriteCount"] == 0, "initial flow reported external write")
    require(all(event["data"].get("pipeline") != "writing_anki" for event in events[:7]), "Anki stage occurred before confirmation")
    require(not any(ref.get("kind") == "anki-readback" for event in events[:7] for ref in event.get("artifacts", [])), "Anki readback existed before confirmation")
    require(events[7]["eventType"] == "command.accepted" and events[7]["correlationId"].find("confirm") >= 0, "confirm command boundary missing")
    require(events[9]["data"]["pipeline"] == "writing_anki", "confirmed flow did not enter Anki stage")
    require(events[10]["data"]["result"]["status"] == "completed", "confirmed flow not completed")
    require(events[15]["data"]["result"]["replayed"] is True, "replay completion missing")

    attempts: dict[str, list[Path]] = {}
    for stage in (run_root / "stages").iterdir():
        if stage.is_dir():
            attempts[stage.name] = sorted((stage / "attempts").iterdir()) if (stage / "attempts").is_dir() else []
    expected_attempts = {"content.understand-and-card": 1, "speech-synthesize": 1, "speech-playback": 2, "anki-export": 1}
    require({name: len(paths) for name, paths in attempts.items()} == expected_attempts, "worker attempt topology drift")

    execution_evidence: list[dict[str, Any]] = []
    for stage, stage_attempts in attempts.items():
        for attempt in stage_attempts:
            evidence_path = attempt / "worker-execution.evidence.v1.json"
            require(evidence_path.is_file(), f"worker execution evidence missing: {stage}/{attempt.name}")
            evidence = load(evidence_path)
            entry = evidence["startedEntrypoint"]
            executable = candidate / entry["candidateRelativePath"]
            require(executable.is_file(), f"candidate entrypoint missing: {entry['candidateRelativePath']}")
            require(executable.stat().st_size == entry["bytes"], f"entrypoint bytes drift: {stage}")
            require(sha256_file(executable) == entry["sha256"].upper(), f"entrypoint SHA drift: {stage}")
            require(evidence["process"]["exitCode"] == 0, f"worker process failed: {stage}")
            execution_evidence.append({"stage": stage, "attemptId": attempt.name, "entrypoint": entry, "evidence": file_evidence(evidence_path)})

    content_attempt = attempts["content.understand-and-card"][0]
    content_card_path = content_attempt / "outputs/card-bundle.schema-v1.json"
    content_learning_path = content_attempt / "outputs/learning-bundle.schema-v1.json"
    anki_attempt = attempts["anki-export"][0]
    adapter_card_path = find_one(list((run_root / "adapters/anki-export").glob("*/v0001/card-bundle.v1.json")), "Anki adapter card bundle")
    readback_path = anki_attempt / "outputs/readback.json"
    content_card = load(content_card_path)
    content_learning = load(content_learning_path)
    adapter_card = load(adapter_card_path)
    readback = load(readback_path)
    require(task_manifest["taskId"] == content_card["task_id"] == content_learning["task_id"] == readback["taskId"], "task identity drift")
    preview = real["draftProjection"]["cardDraft"]["cards"][0]
    final_card = real["card"]
    source_card = content_card["cards"][0]
    adapted_card = adapter_card["cards"][0]
    readback_card = readback["cards"][0]
    fronts = [preview["front"], final_card["front"], source_card["front_blocks"][0]["text"], adapted_card["fields"]["question"], readback_card["notesInfo"]["requiredFields"]["Front"]]
    backs = [preview["back"], final_card["back"], source_card["back_blocks"][0]["text"], adapted_card["fields"]["answer"], readback_card["notesInfo"]["requiredFields"]["Back"]]
    require(len(set(fronts)) == 1 and len(set(backs)) == 1, "preview/content/adapter/readback card text drift")
    require(final_card["deck"] == readback_card["cardsInfo"][0]["deckName"], "deck readback drift")
    require(sha256_file(readback_path) == real["ankiEvidence"]["semanticReadback"]["sha256"].upper(), "saved readback SHA drift")
    require(readback["worker"] == {"workerId": "export.anki", "workerVersion": "1.4.1"}, "Anki readback worker identity drift")
    require(real["preConfirmExporter"] == {"requests": 0, "attempts": 0, "writes": 0, "ankiReadbackArtifact": False}, "pre-confirm exporter evidence drift")
    require(real["replayWorkerAttemptDelta"] == {"content": 0, "synthesis": 0, "playback": 1, "anki": 0}, "replay worker delta drift")
    require(real["replayExternalWriteDelta"] == 0, "replay write delta drift")
    require(real["restartRecovery"] == {"queriedOnly": True, "workerAttemptDelta": 0}, "restart recovery evidence drift")

    claims = [load(path) for path in (runtime_root / "command-index-v3/claims").glob("*.claim.json")]
    operation_claims = [item for item in claims if item.get("operationId") == operation_id]
    owner_ids = sorted({item["ownerInstanceId"] for item in operation_claims})
    require(len(operation_claims) == 3 and len(owner_ids) == 2, "restart owner evidence drift")

    token = real["draftProjection"]["cardDraft"]["token"]
    token_hits = []
    for path in runtime_root.rglob("*"):
        if path.is_file() and path.suffix.lower() in {".json", ".ndjson", ".log", ".txt"}:
            try:
                if token in path.read_text(encoding="utf-8", errors="ignore"):
                    token_hits.append(str(path))
            except OSError:
                pass
    raw_confirm_payload_persisted = any("command" in Path(path).name.lower() and "receipt" not in Path(path).name.lower() and "claim" not in Path(path).name.lower() for path in token_hits)

    fake_root = candidate / "canary/fake-preview-confirm"
    negative_terms = {term: 0 for term in ("stale", "double-confirm", "follow_up", "follow-up", "cancel")}
    for path in fake_root.rglob("*"):
        if path.is_file() and path.suffix.lower() in {".json", ".ndjson", ".log", ".txt", ".md"}:
            text = path.read_text(encoding="utf-8", errors="ignore").lower()
            for term in negative_terms:
                negative_terms[term] += text.count(term)
    durable_negative_evidence = sum(negative_terms.values()) > 0

    created_receipt_path = created_candidate / "canary/real-preview-confirm-recovery/receipt.json"
    require(sha256_file(created_receipt_path) == EXPECTED["createdReceipt"], "prior CREATED receipt SHA drift")
    created = load(created_receipt_path)
    created_manifest = load(created_candidate / "candidate-manifest.json")
    require(created["ankiEvidence"]["status"] == "created" and created["ankiEvidence"]["externalWriteCount"] == 1, "prior candidate did not preserve CREATED write")
    require(manifest_entry(created_manifest, "core-v3/KnowledgeClipper.Core.V3.Client.dll")["sha256"].upper() == EXPECTED["client"], "prior candidate Core Client differs")
    require(manifest_entry(created_manifest, "authority/win-x64/workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe")["sha256"].upper() == EXPECTED["anki"], "prior candidate Anki differs")

    failed_receipt_path = failed_candidate / "canary/real-preview-confirm-recovery/receipt.json"
    failed = load(failed_receipt_path)
    require(failed["status"] == "FAIL", "later failure evidence lost")
    failed_runtime = Path(failed["runtimeDataRoot"])
    failed_speech_results = list(failed_runtime.glob("worker-process-v3/jobs/*/runs/*/stages/speech-synthesize/attempts/*/result.json"))
    require(len(failed_speech_results) == 1, "later failure speech result missing")
    failed_speech = load(failed_speech_results[0])
    require(failed_speech["status"] == "failed" and failed_speech["error"]["code"] == "edge_tts_failed", "later failure cause drift")
    failed_anki_attempts = list(failed_runtime.glob("worker-process-v3/jobs/*/runs/*/stages/anki-export/attempts/*"))
    require(len(failed_anki_attempts) == 0, "later speech failure unexpectedly reached Anki")

    gates = [
        {"id": "before-confirm-zero-exporter", "status": "PASS", "evidence": "event 1..7 contains no Anki stage/readback; persisted preConfirmExporter is 0/0/0"},
        {"id": "preview-card-bundle-equivalence", "status": "PASS", "evidence": "front/back are byte-for-byte equal across preview, Content, adapter and saved Anki readback"},
        {"id": "confirm-token-binding-and-single-use", "status": "CONDITIONAL", "evidence": "valid token/revision/digest are visible in projections, but raw confirm payload and stale/double-confirm negative commands are not durably persisted"},
        {"id": "cancel-followup-revision", "status": "PENDING", "evidence": "candidate evidence contains no independently replayable cancel/follow-up/new-revision scenario"},
        {"id": "shared-core-desktop-path", "status": "PENDING", "evidence": "Core candidate is qualified; matching Desktop candidate does not yet exist"},
        {"id": "restart-recovery", "status": "PASS", "evidence": "three operation claims span two process owners; recovery adds zero worker attempts"},
        {"id": "anki-confirmed-semantic-readback", "status": "PASS-SCOPED-ALREADY-CURRENT", "evidence": "one post-confirm Anki attempt saved exact readback; current candidate made 0 writes, prior same-binary candidate separately preserves CREATED=1"},
    ]

    return {
        "schema": "kc.quality.read-to-anki-preview-candidate/1",
        "status": "CONDITIONAL-CORE-QUALIFIED",
        "candidate": {
            "root": str(candidate),
            "manifest": file_evidence(manifest_path),
            "declaredFiles": len(declared),
            "declaredBytes": sum(item["bytes"] for item in declared),
            "declaredMismatches": 0,
            "actualFilesIncludingEvidence": len(actual_files),
            "actualBytesIncludingEvidence": sum(path.stat().st_size for path in actual_files),
            "postManifestEvidenceFiles": len(extras),
            "unexpectedPostManifestFiles": unexpected_extras,
            "coreClient": file_evidence(client_path),
            "ankiEntrypoint": file_evidence(anki_path),
            "dependencyManifest": file_evidence(dependency_path),
            "dualSchemaCopyShaEqual": True,
        },
        "sourceGateMatrix": file_evidence(gate_matrix),
        "receipts": {"fake": file_evidence(fake_receipt_path), "real": file_evidence(real_receipt_path)},
        "runtime": {
            "root": str(runtime_root),
            "taskId": task_id,
            "runId": run_root.name,
            "operationId": operation_id,
            "eventSequences": [item["sequence"] for item in events],
            "attemptCounts": expected_attempts,
            "workerExecutions": execution_evidence,
            "ownerInstanceIds": owner_ids,
            "rawConfirmPayloadPersisted": raw_confirm_payload_persisted,
            "fakeNegativeEvidenceTerms": negative_terms,
            "durableNegativeEvidence": durable_negative_evidence,
        },
        "inputOutput": {
            "rawText": real["rawText"],
            "cleanedText": real["cleanedText"],
            "front": final_card["front"],
            "back": final_card["back"],
            "deck": final_card["deck"],
            "previewToReadbackExact": True,
            "readback": file_evidence(readback_path),
            "noteId": readback_card["notesInfo"]["noteId"],
            "cardIds": readback_card["notesInfo"]["cardIds"],
            "outcome": readback_card["outcome"],
        },
        "historicalEvidence": {
            "priorCreatedDifferentCandidate": {"candidate": str(created_candidate.resolve()), "receipt": file_evidence(created_receipt_path), "sameCoreClientSha": True, "sameAnkiSha": True, "externalWriteCount": 1},
            "laterSpeechFailurePreserved": {"candidate": str(failed_candidate.resolve()), "receipt": file_evidence(failed_receipt_path), "errorCode": "edge_tts_failed", "ankiAttempts": 0},
        },
        "gates": gates,
        "decision": {
            "corePreviewQualification": "CONDITIONAL-PASS",
            "desktopCandidatePromotion": "PENDING",
            "fullProductPromotion": "NO-GO",
            "blockingEvidence": ["durable stale/double-confirm negative commands", "cancel/follow-up revision evidence", "matching Desktop candidate and same-path UI automation"],
        },
        "executionBoundary": {"externalCallsByQualityGate": 0, "externalWritesByQualityGate": 0, "speakerInvocationsByQualityGate": 0, "candidateBinariesExecutedByQualityGate": False},
    }


def readable(result: dict[str, Any]) -> str:
    rows = "\n".join(f"| {item['id']} | {item['status']} | {item['evidence']} |" for item in result["gates"])
    io = result["inputOutput"]
    return f"""# Read-to-Anki preview candidate quality result

- Status: `{result['status']}`
- Candidate: `{result['candidate']['root']}`
- Manifest SHA-256: `{result['candidate']['manifest']['sha256']}`
- Core Client SHA-256: `{result['candidate']['coreClient']['sha256']}`
- Anki EXE SHA-256: `{result['candidate']['ankiEntrypoint']['sha256']}`
- Quality external calls/writes/speaker: `0 / 0 / 0`

## Input to output

- Raw: {io['rawText']}
- Cleaned: {io['cleanedText']}
- Front: {io['front']}
- Back: {io['back']}
- Deck: {io['deck']}
- Saved semantic readback: note `{io['noteId']}`, card `{io['cardIds'][0]}`, outcome `{io['outcome']}`

## Gates

| Gate | Status | Independent evidence |
|---|---|---|
{rows}

## Decision

Core preview mainline is independently qualified only as a conditional pass. The current immutable candidate proves preview-before-export, exact card propagation, restart recovery, one post-confirm Anki attempt, semantic readback and replay without new writes. It does not durably preserve stale/double-confirm, cancel, or follow-up/new-revision negative command payloads, and it is not a Desktop candidate.

The prior different candidate preserves the one real `CREATED` write with the same Core Client and Anki binary hashes. The current candidate proves `ALREADY_CURRENT`; these are intentionally not conflated. The later distinct candidate preserves `edge_tts_failed` with zero Anki attempts.
"""


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", type=Path, default=DEFAULT_CANDIDATE)
    parser.add_argument("--created-candidate", type=Path, default=DEFAULT_CREATED_CANDIDATE)
    parser.add_argument("--failed-candidate", type=Path, default=DEFAULT_FAILED_CANDIDATE)
    parser.add_argument("--gate-matrix", type=Path, default=DEFAULT_GATE_MATRIX)
    parser.add_argument("--output-root", type=Path)
    args = parser.parse_args()
    result = evaluate(args.candidate, args.created_candidate, args.failed_candidate, args.gate_matrix)
    result["verifiedAt"] = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    if args.output_root:
        require(not args.output_root.exists(), f"append-only output root already exists: {args.output_root}")
        args.output_root.mkdir(parents=True)
        receipt_path = args.output_root / "quality-preview-confirm-receipt.json"
        readable_path = args.output_root / "READABLE.md"
        receipt_path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        readable_path.write_text(readable(result), encoding="utf-8")
        print(json.dumps({"status": result["status"], "receipt": file_evidence(receipt_path), "readable": file_evidence(readable_path)}, ensure_ascii=False))
    else:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
