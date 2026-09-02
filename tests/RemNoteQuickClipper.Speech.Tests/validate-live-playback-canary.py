import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import runpy


def native_path(path: Path) -> Path:
    absolute = path.resolve()
    text = str(absolute)
    if os.name == "nt" and len(text) >= 248 and not text.startswith("\\\\?\\"):
        return Path("\\\\?\\" + text)
    return absolute


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--project-root", required=True)
    parser.add_argument("--canary-root", required=True)
    args = parser.parse_args()

    project = Path(args.project_root).resolve()
    root = Path(args.canary_root).resolve()
    canary_receipt_path = root / "playback-canary-receipt.json"
    canary_receipt = json.loads(canary_receipt_path.read_text(encoding="utf-8"))
    if canary_receipt["status"] != "passed":
        raise AssertionError(f"playback Canary did not pass: {canary_receipt_path}")
    canary_mode = canary_receipt.get("canaryMode", "physical")
    if canary_mode not in {"physical", "synthetic"}:
        raise AssertionError(f"unsupported playback Canary mode: {canary_mode}")
    helpers = runpy.run_path(str(Path(__file__).with_name("validate-public-contracts.py")))
    contracts = project / "contracts" / "v1"
    registry, schemas = helpers["build_registry"](contracts / "schemas")
    load = helpers["load_validator"]
    require_valid = helpers["require_valid"]
    worker_request = load(contracts / "schemas" / "common" / "worker-request.schema.json", registry, schemas)
    speech_request = load(contracts / "schemas" / "speech" / "speech-request.schema.json", registry, schemas)
    speech_result = load(contracts / "schemas" / "speech" / "speech-result.schema.json", registry, schemas)
    artifact = load(contracts / "schemas" / "common" / "artifact.schema.json", registry, schemas)
    cancel_signal = load(contracts / "schemas" / "common" / "worker-cancel-signal.schema.json", registry, schemas)

    requests = sorted(root.rglob("request.json"))
    results = sorted(root.rglob("result.json"))
    signals = sorted(root.rglob("cancel.json"))
    task_ids = {json.loads(path.read_text(encoding="utf-8"))["taskId"] for path in requests}
    if len(task_ids) != 1:
        raise AssertionError(f"playback Canary spans multiple tasks: {task_ids}")
    job_root = root / "jobs" / next(iter(task_ids))
    manifest_refs = {
        output["manifestPath"]
        for path in results
        for output in json.loads(path.read_text(encoding="utf-8"))["outputs"]
        if output["kind"] == "audio.speech"
    }
    manifests = sorted(native_path(job_root / PurePosixPath(relative)) for relative in manifest_refs)
    if len(requests) != 6 or len(results) != 6 or len(manifests) != 1 or len(signals) != 4:
        raise AssertionError(
            f"unexpected playback Canary shape: requests={len(requests)} results={len(results)} manifests={len(manifests)} signals={len(signals)}"
        )
    if not all(path.is_file() for path in manifests):
        raise AssertionError(f"referenced audio manifest is missing: {manifests}")

    for path in requests:
        document = json.loads(path.read_text(encoding="utf-8"))
        validator = speech_request if document["operation"] == "speech.synthesize" else worker_request
        require_valid(validator, path, f"{document['operation']} request")
    for path in results:
        require_valid(speech_result, path, "speech result")
    for path in manifests:
        require_valid(artifact, path, "audio artifact")
        document = json.loads(path.read_text(encoding="utf-8"))
        for item in document["files"]:
            payload = native_path(job_root / item["path"])
            if not payload.is_file() or payload.stat().st_size != item["bytes"] or sha256(payload) != item["sha256"].lower():
                raise AssertionError(f"artifact payload integrity mismatch: {payload}")
    for path in signals:
        require_valid(cancel_signal, path, "playback cancel signal")

    for path in results:
        result = json.loads(path.read_text(encoding="utf-8"))
        if result["payload"]["cacheHit"] is not False:
            raise AssertionError(f"fresh synth/playback attempt was mislabeled as a cache hit: {path}")
        if result["stageId"] == "speech-playback" and "reusedFromAttemptId" in result["payload"]:
            raise AssertionError(f"playback result carried synthesis reuse ancestry: {path}")
        for output in result["outputs"]:
            manifest = native_path(job_root / output["manifestPath"])
            if sha256(manifest) != output["expectedSha256"].lower():
                raise AssertionError(f"published manifest hash mismatch: {manifest}")

    receipt = {
        "schemaVersion": 1,
        "suite": "speech-live-playback-contract-and-integrity",
        "completedAtUtc": datetime.now(timezone.utc).isoformat(),
        "status": "passed",
        "canaryMode": canary_mode,
        "requestsValidated": len(requests),
        "resultsValidated": len(results),
        "artifactsValidated": len(manifests),
        "cancelSignalsValidated": len(signals),
        "payloadHashesValidated": sum(len(json.loads(path.read_text(encoding="utf-8"))["files"]) for path in manifests),
        "freshAttemptsWithCacheHitFalse": len(results),
        "playbackResultsWithReuseAncestry": 0,
    }
    receipt_path = root / "playback-contract-validation-receipt.json"
    if receipt_path.exists():
        raise AssertionError(f"immutable receipt already exists: {receipt_path}")
    receipt_path.write_text(json.dumps(receipt, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"PASS LIVE PLAYBACK CONTRACTS {receipt_path}")


if __name__ == "__main__":
    main()
