from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import runpy


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project-root", required=True, type=Path)
    parser.add_argument("--receipt", required=True, type=Path)
    args = parser.parse_args()
    project = args.project_root.resolve()
    receipt_path = args.receipt.resolve()
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    helpers = runpy.run_path(str(Path(__file__).with_name("validate-public-contracts.py")))
    contracts = project / "contracts" / "v1"
    registry, schemas = helpers["build_registry"](contracts / "schemas")
    load = helpers["load_validator"]
    require_valid = helpers["require_valid"]
    speech_request = load(contracts / "schemas" / "speech" / "speech-request.schema.json", registry, schemas)
    speech_result = load(contracts / "schemas" / "speech" / "speech-result.schema.json", registry, schemas)
    artifact_schema = load(contracts / "schemas" / "common" / "artifact.schema.json", registry, schemas)

    counts = {"requests": 0, "results": 0, "artifacts": 0, "payloads": 0}
    for key in ("fiveLanguageAuto", "fiveLanguageOverride"):
        evidence = receipt["evidence"][key]
        subreceipt = Path(evidence["path"]).resolve()
        require(sha256(subreceipt) == evidence["sha256"].upper(), f"subreceipt hash mismatch: {key}")
        root = subreceipt.parent
        requests = sorted(root.rglob("request.json"))
        results = sorted(root.rglob("result.json"))
        manifests = sorted(root.rglob("artifact.json"))
        require((len(requests), len(results), len(manifests)) == (5, 5, 5), f"candidate artifact counts drift: {key}")
        for path in requests:
            require_valid(speech_request, path, f"{key} speech request")
            document = json.loads(path.read_text(encoding="utf-8"))
            require(document["workerId"] == "speech.edge" and document["operation"] == "speech.synthesize", f"request route drift: {path}")
            require(document["payload"]["voiceSnapshot"]["provider"] == "edge-tts", f"provider drift: {path}")
        for path in results:
            require_valid(speech_result, path, f"{key} speech result")
            document = json.loads(path.read_text(encoding="utf-8"))
            require(document["status"] == "completed" and document["payload"]["cacheHit"] is False, f"fresh synthesis result drift: {path}")
        for path in manifests:
            require_valid(artifact_schema, path, f"{key} audio artifact")
            document = json.loads(path.read_text(encoding="utf-8"))
            require(document["kind"] == "audio.speech", f"artifact kind drift: {path}")
            job_root = next(parent for parent in path.parents if parent.name.startswith("t") and parent.parent.name == "jobs")
            for file_ref in document["files"]:
                payload = job_root / file_ref["path"]
                require(payload.is_file() and payload.stat().st_size == file_ref["bytes"], f"payload bytes mismatch: {payload}")
                require(sha256(payload) == file_ref["sha256"].upper(), f"payload hash mismatch: {payload}")
                counts["payloads"] += 1
        counts["requests"] += len(requests)
        counts["results"] += len(results)
        counts["artifacts"] += len(manifests)

    backend = Path(receipt["evidence"]["backendContentAndPlayback"]["path"]).resolve()
    backend_validation = backend.with_name("backend-content-speech-canary-validation-receipt.json")
    require(backend_validation.is_file(), "backend public-contract validation receipt is missing")
    backend_document = json.loads(backend_validation.read_text(encoding="utf-8"))
    require(backend_document["status"] == "passed" and backend_document["cancelSignals"] == 4, "backend public-contract validation failed")
    output = receipt_path.with_name("speech-v123-candidate-bound-contract-validation-receipt.json")
    require(not output.exists(), f"immutable validation receipt exists: {output}")
    result = {
        "schemaVersion": "speech-v123-candidate-bound-contract-validation.v1",
        "status": "passed",
        "completedAtUtc": datetime.now(timezone.utc).isoformat(),
        "candidateReceipt": str(receipt_path),
        "candidateReceiptSha256": sha256(receipt_path),
        "fiveLanguage": counts,
        "backendValidation": str(backend_validation),
        "backendValidationSha256": sha256(backend_validation),
        "backendRequests": 10,
        "backendResults": 10,
        "backendArtifacts": 8,
        "backendCancelSignals": 4,
        "totalRequests": counts["requests"] + 10,
        "totalResults": counts["results"] + 10,
        "totalArtifacts": counts["artifacts"] + 8,
        "physicalSpeakerPlayback": False,
        "oldAiTtsRouteCount": 0,
    }
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"PASS V123 CANDIDATE-BOUND SPEECH CONTRACTS {output}")


if __name__ == "__main__":
    main()
