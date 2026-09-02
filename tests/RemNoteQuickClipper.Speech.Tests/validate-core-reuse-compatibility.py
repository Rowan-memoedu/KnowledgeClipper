#!/usr/bin/env python3
"""Independently validate the core speech reuse evidence bundle."""

from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


def load(root: Path, name: str) -> dict:
    path = root / name
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--evidence-root", required=True, type=Path)
    args = parser.parse_args()
    root = args.evidence_root.resolve()

    contract = load(root, "orchestrator-contract-receipt.json")
    first_synth = load(root, "synthesis-first-result.json")
    reused_synth = load(root, "synthesis-reused-result.json")
    first_playback = load(root, "playback-first-result.json")
    second_playback = load(root, "playback-second-result.json")
    artifact = load(root, "audio-artifact.json")
    input_bundle = load(root, "input.json")

    require(contract["status"] == "passed", "orchestrator contract suite failed")
    named = {item["name"]: item for item in contract["tests"]}
    semantic = named["speech-synthesize-reuse-and-playback-no-reuse"]
    evidence = named["speech-synthesis-reuse-and-playback-effect"]
    isolation = named["reuse-candidate-corruption-isolation"]
    require(semantic["status"] == "passed" and evidence["status"] == "passed", "core speech reuse tests failed")
    require(isolation["status"] == "passed", "core reuse candidate isolation failed")
    require(isolation["corruptedArtifactCandidateReused"] is False, "corrupted audio candidate was reused")
    require(isolation["freshWorkerStarted"] is True, "corrupted audio did not trigger fresh synthesis")
    require(isolation["malformedResultSkipped"] is True, "malformed result poisoned reuse scanning")
    require(isolation["forgedIdentitySkipped"] is True, "result/request identity mismatch was reused")
    require(isolation["fallbackAttemptId"] != isolation["reusedAttemptId"], "fallback reuse did not create a new attempt")
    require(isolation["workerStartedOnFallbackReuse"] is False, "valid fallback reuse unexpectedly started a worker")

    require(first_synth["status"] == "completed" and reused_synth["status"] == "completed", "synthesis did not complete")
    require(first_synth["attemptId"] != reused_synth["attemptId"], "reuse did not create a new attempt")
    require(first_synth["payload"]["cacheHit"] is False, "first synthesis was marked as a cache hit")
    require(reused_synth["payload"]["cacheHit"] is True, "reused synthesis was not marked as a cache hit")
    require(reused_synth["payload"]["reusedFromAttemptId"] == first_synth["attemptId"], "reuse ancestry mismatch")
    require(reused_synth["payload"]["operationId"] == reused_synth["operationId"], "reuse operationId mismatch")
    require(first_synth["outputs"][0]["artifactRef"] == reused_synth["outputs"][0]["artifactRef"], "artifact reference changed")
    require(first_synth["outputs"][0]["expectedSha256"] == reused_synth["outputs"][0]["expectedSha256"], "artifact hash changed")
    require(artifact["producer"]["attemptId"] == first_synth["attemptId"], "artifact producer ancestry changed")
    require(input_bundle["reusedAudioInput"]["expectedSha256"] == reused_synth["outputs"][0]["expectedSha256"], "playback input hash differs from reused audio")

    for result in (first_playback, second_playback):
        require(result["status"] == "completed", "playback did not complete")
        require(result["payload"]["cacheHit"] is False, "playback was mislabeled as a cache hit")
        require(not any(item.startswith("Reused successful outputs") for item in result["warnings"]), "playback result was reused")
    require(first_playback["attemptId"] != second_playback["attemptId"], "playback reused an attempt")
    require(evidence["playback"]["workerStarts"] == 2, "both playback attempts did not start workers")
    require(evidence["playback"]["fromStart"] is True, "replay was not from_start")

    inputs = [
        "orchestrator-contract-receipt.json",
        "input.json",
        "synthesis-first-result.json",
        "synthesis-reused-result.json",
        "playback-first-result.json",
        "playback-second-result.json",
        "audio-artifact.json",
    ]
    receipt = {
        "schemaVersion": "speech-core-reuse-compatibility.v1",
        "status": "passed",
        "validatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "checks": {
            "synthesisNewAttemptOnReuse": True,
            "synthesisCacheHitTransition": [False, True],
            "reuseAncestryPreserved": True,
            "operationIdentityCurrent": True,
            "immutableAudioReferencePreserved": True,
            "artifactProducerPreserved": True,
            "corruptedArtifactCandidateReused": False,
            "corruptedArtifactTriggeredFreshSynthesis": True,
            "malformedResultSkipped": True,
            "forgedIdentitySkipped": True,
            "validFallbackReuseStartedWorker": False,
            "playbackAttempts": 2,
            "playbackWorkerStarts": 2,
            "playbackResultReuse": False,
            "replayFromStart": True,
        },
        "observed": {
            "firstSynthesisAttemptId": first_synth["attemptId"],
            "reusedSynthesisAttemptId": reused_synth["attemptId"],
            "reusedFromAttemptId": reused_synth["payload"]["reusedFromAttemptId"],
            "audioArtifactRef": reused_synth["outputs"][0]["artifactRef"],
            "audioArtifactSha256": reused_synth["outputs"][0]["expectedSha256"],
            "fallbackAttemptId": isolation["fallbackAttemptId"],
            "fallbackReuseAttemptId": isolation["reusedAttemptId"],
            "firstPlaybackAttemptId": first_playback["attemptId"],
            "secondPlaybackAttemptId": second_playback["attemptId"],
        },
        "inputs": [
            {
                "path": name,
                "bytes": (root / name).stat().st_size,
                "sha256": hashlib.sha256((root / name).read_bytes()).hexdigest(),
            }
            for name in inputs
        ],
    }
    output = root / "speech-core-reuse-compatibility-receipt.json"
    with output.open("x", encoding="utf-8", newline="\n") as handle:
        json.dump(receipt, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
    print(json.dumps(receipt, ensure_ascii=False, indent=2))
    print(f"RECEIPT {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
