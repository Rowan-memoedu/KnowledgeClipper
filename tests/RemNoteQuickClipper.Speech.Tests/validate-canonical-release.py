import argparse
import hashlib
import json
import os
import pathlib
import subprocess
import tempfile


def sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--receipt", required=True)
    args = parser.parse_args()

    receipt_path = pathlib.Path(args.receipt).resolve()
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    candidate_root = receipt_path.parent
    quality_copy_root = pathlib.Path(receipt["qualityCopyRoot"]).resolve()
    expected_copy_root = (candidate_root / "workers" / "speech").resolve()
    require(quality_copy_root == expected_copy_root, "qualityCopyRoot is outside the canonical release tree")
    require(receipt["status"] == "passed", "release receipt is not passing")
    require(receipt["workerId"] == "speech.edge", "workerId drift")
    require(receipt["workerVersion"] == "1.2.3", "workerVersion drift")
    require(receipt["contractVersion"] == "1.0.0", "frozen contract version drift")

    release_tree = receipt["releaseTree"]
    require(len(release_tree) == 1, "workers/speech is not minimal")
    item = release_tree[0]
    require(item["path"] == "workers/speech/KnowledgeClipper.Speech.Worker.exe", "canonical entrypoint drift")
    worker = (candidate_root / pathlib.PurePosixPath(item["path"])).resolve()
    require(worker.is_file(), "canonical worker is missing")
    require(worker.parent == quality_copy_root, "canonical worker escaped qualityCopyRoot")
    require(worker.stat().st_size == item["bytes"], "canonical worker byte count mismatch")
    require(sha256(worker) == item["sha256"], "canonical worker SHA-256 mismatch")
    actual_files = sorted(path.relative_to(quality_copy_root).as_posix() for path in quality_copy_root.rglob("*") if path.is_file())
    require(actual_files == ["KnowledgeClipper.Speech.Worker.exe"], "quality copy root contains non-minimal files")

    process = subprocess.run(
        [str(worker), "describe", "--json"],
        cwd=tempfile.gettempdir(),
        text=True,
        encoding="utf-8",
        capture_output=True,
        check=False,
    )
    require(process.returncode == 0, f"published describe failed from temp cwd: {process.stderr}")
    descriptor = json.loads(process.stdout)
    require(descriptor == receipt["descriptor"], "live descriptor differs from release receipt")
    require("remnote" not in process.stdout.lower() and "anki" not in process.stdout.lower(), "descriptor is destination-specific")

    cwd_checks = receipt["arbitraryCwdChecks"]
    require(sum(check["command"] == "describe --json" and check["status"] == "passed" for check in cwd_checks) >= 3, "insufficient describe cwd coverage")
    run_checks = [check for check in cwd_checks if check["command"].startswith("run --request")]
    require(len(run_checks) == 1 and run_checks[0]["status"] == "passed", "missing arbitrary-cwd run evidence")
    require(set(run_checks[0]["operations"]) == {"speech.synthesize", "speech.playback"}, "run evidence misses a speech stage")

    semantics = receipt["semantics"]
    require(semantics["naturalCompletion"] == "idle_replayable", "natural completion drift")
    require(semantics["replay"] == "fresh-playback-attempt-from-start", "replay drift")
    require(semantics["playbackResultReuse"] is False, "playback reuse must remain disabled")
    require(semantics["oldAiTtsRouteCount"] == 0, "old AI TTS route detected")
    require(semantics["externalProductionWrites"] == 0, "external production write detected")
    require(semantics["physicalSpeakerPlayback"] is False, "physical speaker playback was not allowed")

    evidence_by_kind = {}
    for evidence in receipt["evidence"]:
        evidence_path = pathlib.Path(evidence["path"]).resolve()
        require(evidence_path.is_file(), f"missing evidence: {evidence_path}")
        require(sha256(evidence_path) == evidence["sha256"], f"evidence hash mismatch: {evidence_path}")
        evidence_by_kind[evidence["kind"]] = evidence_path
    required_kinds = {
        "unit-tests",
        "public-contracts",
        "destination-neutral-identity",
        "synthetic-playback-canary",
        "canary-public-contracts",
    }
    require(required_kinds.issubset(evidence_by_kind), "release evidence set is incomplete")

    unit = json.loads(evidence_by_kind["unit-tests"].read_text(encoding="utf-8"))
    tests = {test["name"]: test["status"] for test in unit["tests"]}
    for test_name in (
        "reject old provider before synthesis",
        "synthesis publishes immutable audio beyond MAX_PATH through short tool workspace",
        "playback stages audio beyond MAX_PATH into a short player workspace",
        "replay starts a fresh session from the same audio",
        "playback natural completion resets idle replayable",
        "playback cancellation maps all public reasons",
    ):
        require(tests.get(test_name) == "passed", f"required unit evidence missing: {test_name}")

    canary = json.loads(evidence_by_kind["synthetic-playback-canary"].read_text(encoding="utf-8"))
    cases = {case["case"]: case for case in canary["cases"]}
    expected_states = {
        "natural-completion": "idle_replayable",
        "user-stop": "stopped",
        "superseded": "superseded",
        "task-cancel": "cancelled",
        "timeout": "timed_out",
    }
    for case_name, state in expected_states.items():
        require(cases.get(case_name, {}).get("playbackState") == state, f"Canary state mismatch: {case_name}")
        require(cases[case_name]["cacheHit"] is False, f"playback was reused: {case_name}")

    validation_path = candidate_root / "speech-canonical-release-validation-receipt.json"
    require(not validation_path.exists(), f"immutable validation receipt already exists: {validation_path}")
    validation = {
        "schemaVersion": 1,
        "receiptType": "speech-canonical-release-independent-validation-v1",
        "status": "passed",
        "releaseReceipt": str(receipt_path),
        "releaseReceiptSha256": sha256(receipt_path),
        "canonicalWorker": str(worker),
        "canonicalWorkerBytes": worker.stat().st_size,
        "canonicalWorkerSha256": sha256(worker),
        "liveDescribeFromTempCwd": descriptor,
        "qualityCopyTree": actual_files,
        "verifiedEvidenceKinds": sorted(evidence_by_kind),
        "physicalSpeakerPlayback": False,
        "externalProductionWrites": 0,
        "oldAiTtsRouteCount": 0,
    }
    validation_path.write_text(json.dumps(validation, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"PASS INDEPENDENT SPEECH RELEASE VALIDATION {validation_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
