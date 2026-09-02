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
    return digest.hexdigest()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project-root", required=True)
    parser.add_argument("--canary-root", required=True)
    args = parser.parse_args()
    project = Path(args.project_root).resolve()
    root = Path(args.canary_root).resolve()
    receipt_path = root / "backend-content-speech-canary-receipt.json"
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    require(receipt["status"] == "passed", "backend Canary did not pass")
    require(receipt["canaryMode"] == "backend-no-speaker", "physical playback boundary drift")
    require(receipt["fakePlayer"]["physicalSpeakerPlayback"] is False, "physical speaker was used")
    require(receipt["edgeTts"]["provider"] == "edge-tts" and receipt["edgeTts"]["realSynthesisCalls"] == 4, "real Edge synthesis evidence drift")
    require(receipt["semantics"]["oldAiTtsRouteCount"] == 0, "old AI TTS route detected")
    require(receipt["semantics"]["spokenTextRecleanedBySpeech"] is False, "speech worker re-cleaned content")
    require(receipt["semantics"]["externalProductionWrites"] == 0, "external production write detected")

    helpers = runpy.run_path(str(Path(__file__).with_name("validate-public-contracts.py")))
    contracts = project / "contracts" / "v1"
    registry, schemas = helpers["build_registry"](contracts / "schemas")
    load = helpers["load_validator"]
    require_valid = helpers["require_valid"]
    worker_request = load(contracts / "schemas" / "common" / "worker-request.schema.json", registry, schemas)
    speech_request = load(contracts / "schemas" / "speech" / "speech-request.schema.json", registry, schemas)
    speech_result = load(contracts / "schemas" / "speech" / "speech-result.schema.json", registry, schemas)
    artifact_schema = load(contracts / "schemas" / "common" / "artifact.schema.json", registry, schemas)
    cancel_signal = load(contracts / "schemas" / "common" / "worker-cancel-signal.schema.json", registry, schemas)

    requests = sorted(root.rglob("request.json"))
    results = sorted(root.rglob("result.json"))
    manifests = sorted(root.rglob("artifact.json"))
    signals = sorted(root.rglob("cancel.json"))
    require(len(requests) == 10, f"expected 10 requests, found {len(requests)}")
    require(len(results) == 10, f"expected 10 results, found {len(results)}")
    require(len(manifests) == 8, f"expected 8 artifacts, found {len(manifests)}")
    require(len(signals) == 4, f"expected 4 cancel signals, found {len(signals)}")
    job_root = root / "jobs" / "sbc"

    for path in requests:
        document = json.loads(path.read_text(encoding="utf-8"))
        require_valid(speech_request if document["operation"] == "speech.synthesize" else worker_request, path, f"{document['operation']} request")
        require(Path(document["attemptPath"]).parts[:1] == ("runs",), f"attempt path drift: {path}")
    for path in results:
        require_valid(speech_result, path, "speech result")
        result = json.loads(path.read_text(encoding="utf-8"))
        require(result["payload"]["cacheHit"] is False, f"attempt reported cache hit: {path}")
        if result["stageId"] == "speech-playback":
            require("reusedFromAttemptId" not in result["payload"], f"playback result carried reuse ancestry: {path}")
    for path in signals:
        require_valid(cancel_signal, path, "cancel signal")

    artifacts = {}
    audio_payloads = []
    for path in manifests:
        require_valid(artifact_schema, path, "artifact")
        document = json.loads(path.read_text(encoding="utf-8"))
        artifacts[document["artifactId"]] = (path, document)
        for file_ref in document["files"]:
            payload = job_root / file_ref["path"]
            require(payload.is_file(), f"artifact payload missing: {payload}")
            require(payload.stat().st_size == file_ref["bytes"], f"artifact bytes mismatch: {payload}")
            require(sha256(payload) == file_ref["sha256"].lower(), f"artifact hash mismatch: {payload}")
            if document["kind"] == "audio.speech":
                require(file_ref["mediaType"] == "audio/mpeg" and file_ref["bytes"] > 1000, f"audio is not playable-sized MPEG: {payload}")
                header = payload.read_bytes()[:3]
                require(header == b"ID3" or (header and header[0] == 0xFF), f"audio lacks an MP3 signature: {payload}")
                audio_payloads.append(payload)

    require(len(audio_payloads) == 4, f"expected 4 real Edge audio files, found {len(audio_payloads)}")
    for key in ("zh", "en"):
        content_id = f"content-live-{key}"
        spoken_id = f"spoken-{key}"
        require(content_id in artifacts and spoken_id in artifacts, f"source artifact pair missing: {key}")
        _, content = artifacts[content_id]
        _, spoken = artifacts[spoken_id]
        require(content["producer"]["workerId"] == "content.deepseek", f"Content producer drift: {key}")
        require(content["metadata"]["provider"] == "deepseek" and content["metadata"]["providerMode"] == "live", f"Content is not live DeepSeek: {key}")
        require(spoken["kind"] == "text.spoken" and spoken["mediaType"] == "text/plain", f"spoken artifact drift: {key}")
        require(len(spoken["inputRefs"]) == 1 and spoken["inputRefs"][0]["artifactRef"] == content_id, f"spoken provenance missing: {key}")
        require(spoken["metadata"]["assembly"] == "content-live-cleaned-text-adapted-without-recleaning", f"spoken assembly drift: {key}")

    content_sources = {item["language"]: item for item in receipt["contentSources"]}
    require(set(content_sources) == {"zh-CN", "en-US"}, "Content language evidence drift")
    for language, source in content_sources.items():
        original = job_root / source["original"]["path"]
        cleaned = job_root / source["cleanedSpokenText"]["path"]
        require(original.is_file() and cleaned.is_file(), f"preserved text missing: {language}")
        require(sha256(original) == source["original"]["sha256"], f"original text hash mismatch: {language}")
        require(sha256(cleaned) == source["cleanedSpokenText"]["sha256"], f"cleaned spoken text hash mismatch: {language}")
        require(source["provider"] == "deepseek" and source["providerMode"] == "live", f"source provider drift: {language}")

    synth = {case["case"]: case for case in receipt["synthesisCases"]}
    expected_synth = {
        "zh-auto": ("auto", None, "zh-CN", "zh-CN-XiaoxiaoNeural"),
        "zh-override": ("override", "zh-CN", "zh-CN", "zh-CN-XiaoxiaoNeural"),
        "en-auto": ("auto", None, "en-US", "en-US-AvaMultilingualNeural"),
        "en-override": ("override", "en-US", "en-US", "en-US-AvaMultilingualNeural"),
    }
    require(set(synth) == set(expected_synth), "synthesis case set drift")
    for name, (strategy, override, language, voice) in expected_synth.items():
        case = synth[name]
        require((case["strategy"], case["override"], case["languageTag"], case["voiceId"]) == (strategy, override, language, voice), f"route/voice mismatch: {name}")
        require(case["voiceSnapshot"]["provider"] == "edge-tts", f"provider snapshot mismatch: {name}")
        audio = root / case["audio"]["path"]
        require(audio.is_file() and audio.stat().st_size == case["audio"]["bytes"] and sha256(audio) == case["audio"]["sha256"], f"receipt audio mismatch: {name}")

    playback = {case["case"]: case for case in receipt["playbackCases"]}
    expected_states = {
        "natural-completion": "idle_replayable",
        "replay": "idle_replayable",
        "user-stop": "stopped",
        "superseded": "superseded",
        "task-cancel": "cancelled",
        "timeout": "timed_out",
    }
    require(set(playback) == set(expected_states), "playback case set drift")
    for name, state in expected_states.items():
        require(playback[name]["playbackState"] == state, f"playback state mismatch: {name}")
        require(playback[name]["cacheHit"] is False, f"playback result was reused: {name}")
    require(playback["natural-completion"]["attemptId"] != playback["replay"]["attemptId"], "replay did not create a new attempt")
    require(playback["natural-completion"]["audioManifestSha256"] == playback["replay"]["audioManifestSha256"], "replay did not reuse the same successful audio")

    validation = {
        "schemaVersion": 1,
        "suite": "speech-backend-content-live-edge-contract-and-integrity",
        "completedAtUtc": datetime.now(timezone.utc).isoformat(),
        "status": "passed",
        "contentSources": 2,
        "deepSeekLiveSources": 2,
        "synthesisRequests": 4,
        "realEdgeAudioFiles": len(audio_payloads),
        "playbackAttempts": 6,
        "cancelSignals": len(signals),
        "playbackCacheHits": 0,
        "oldAiTtsRouteCount": 0,
        "physicalSpeakerPlayback": False,
        "externalProductionWrites": 0,
        "canaryReceipt": str(receipt_path),
        "canaryReceiptSha256": sha256(receipt_path),
    }
    validation_path = root / "backend-content-speech-canary-validation-receipt.json"
    require(not validation_path.exists(), f"immutable validation receipt already exists: {validation_path}")
    validation_path.write_text(json.dumps(validation, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"PASS BACKEND CONTENT SPEECH CANARY VALIDATION {validation_path}")


if __name__ == "__main__":
    main()
