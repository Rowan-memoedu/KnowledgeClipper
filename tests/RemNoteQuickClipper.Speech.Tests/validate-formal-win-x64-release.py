import argparse
import hashlib
import json
import pathlib
import subprocess

import jsonschema


def filesystem_path(path: pathlib.Path) -> pathlib.Path:
    resolved = path.resolve()
    text = str(resolved)
    if text.startswith("\\\\?\\") or len(text) < 248:
        return resolved
    if text.startswith("\\\\"):
        return pathlib.Path("\\\\?\\UNC\\" + text[2:])
    return pathlib.Path("\\\\?\\" + text)


def sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with filesystem_path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def read_json(path: pathlib.Path) -> dict:
    return json.loads(filesystem_path(path).read_text(encoding="utf-8"))


def tree(root: pathlib.Path) -> dict:
    files = []
    for path in sorted((p for p in root.rglob("*") if p.is_file()), key=lambda p: str(p).lower()):
        files.append({"path": path.relative_to(root).as_posix(), "bytes": path.stat().st_size, "sha256": sha256(path)})
    material = "\n".join(f"{item['path']}|{item['bytes']}|{item['sha256']}" for item in files)
    return {
        "files": files,
        "fileCount": len(files),
        "totalBytes": sum(item["bytes"] for item in files),
        "treeSha256": hashlib.sha256(material.encode("utf-8")).hexdigest(),
    }


def run_json(worker: pathlib.Path, release_root: pathlib.Path, *arguments: str) -> dict:
    process = subprocess.run(
        [str(worker), *arguments], cwd=release_root, text=True, encoding="utf-8", capture_output=True
    )
    require(process.returncode == 0, f"worker command failed ({' '.join(arguments)}): {process.stderr}")
    return json.loads(process.stdout)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--receipt", required=True)
    args = parser.parse_args()
    receipt_path = pathlib.Path(args.receipt).resolve()
    receipt = read_json(receipt_path)
    release_root = pathlib.Path(receipt["formalReleaseRoot"]).resolve()
    worker = pathlib.Path(receipt["entrypoint"]).resolve()
    worker_root = release_root / "workers" / "speech"

    require(receipt["status"] == "passed", "formal receipt is not passing")
    require(receipt["admissionState"] == "formal_module_candidate_for_core_admission", "admission state drift")
    require(receipt["authorityMutated"] is False, "formal publish claimed an authority mutation")
    require(receipt["workerId"] == "speech.edge" and receipt["workerVersion"] == "1.2.5", "worker identity drift")
    require(receipt["runtimeIdentifier"] == "win-x64" and receipt["contractVersion"] == "1.0.0", "runtime/contract drift")
    require(worker == worker_root / "KnowledgeClipper.Speech.Worker.exe", "entrypoint layout drift")
    require(worker.is_file(), "formal worker missing")

    actual_tree = tree(release_root)
    expected_tree = receipt["releaseTree"]
    require(actual_tree["files"] == expected_tree["files"], "release file tree drift")
    require(actual_tree["fileCount"] == expected_tree["fileCount"] == 11, "release file count drift")
    require(actual_tree["totalBytes"] == expected_tree["totalBytes"], "release byte count drift")
    require(actual_tree["treeSha256"] == expected_tree["treeSha256"], "release tree SHA drift")
    require(sha256(worker) == receipt["exe"]["sha256"] and worker.stat().st_size == receipt["exe"]["bytes"], "EXE identity drift")

    descriptor = run_json(worker, release_root, "describe", "--json")
    require(descriptor == receipt["descriptor"], "live describe differs from receipt")
    require(descriptor["workerId"] == "speech.edge" and descriptor["workerVersion"] == "1.2.5", "live descriptor identity drift")

    evidence = {item["kind"]: pathlib.Path(item["path"]).resolve() for item in receipt["evidence"]}
    required = {
        "unit-tests", "public-contracts", "release-public-contracts", "destination-neutral-identity", "describe",
        "release-manifest", "companion-descriptor", "companion-input-schema", "companion-output-schema",
        "voice-discovery-input-schema", "voice-discovery-output-schema", "speaker-preview-input-schema",
        "speaker-preview-output-schema",
        "language-override-fixture", "settings-input", "settings-snapshot", "companion-probe-input",
        "companion-probe-output", "voice-discovery-validate-input", "voice-discovery-validate-output",
        "voice-discovery-live-input", "voice-discovery-live-output", "speaker-preview-input",
        "speaker-preview-output", "speaker-preview-fake-invocations", "synthesis-request", "synthesis-result", "playback-request", "playback-result",
        "fake-tool-invocations", "live-deep-edge-canary",
    }
    require(required.issubset(evidence), "formal evidence set incomplete")
    for item in receipt["evidence"]:
        path = pathlib.Path(item["path"]).resolve()
        require(path.is_file() and sha256(path) == item["sha256"], f"evidence hash drift: {path}")

    manifest_path = worker_root / "release-manifest.json"
    require(evidence["release-manifest"] == manifest_path, "release manifest evidence path drift")
    manifest = read_json(manifest_path)
    require(manifest["schemaVersion"] == "speech-edge-release-manifest.v1", "release manifest schema drift")
    require(manifest["workerId"] == "speech.edge" and manifest["workerVersion"] == "1.2.5", "release manifest identity drift")
    require(manifest["runtimeIdentifier"] == "win-x64" and manifest["authorityMutation"] is False, "release manifest boundary drift")
    require(manifest["entrypoint"] == "workers/speech/KnowledgeClipper.Speech.Worker.exe", "release manifest entrypoint drift")
    bundled = manifest["bundledFiles"]
    require(len(bundled) == 10 and all(item["path"] != "workers/speech/release-manifest.json" for item in bundled), "manifest bundled file set drift")
    for item in bundled:
        path = release_root / pathlib.PurePosixPath(item["path"])
        require(path.is_file(), f"manifest-bound file missing: {path}")
        require(path.stat().st_size == item["bytes"] and sha256(path) == item["sha256"], f"manifest-bound file drift: {path}")
    require(manifest["dependencies"]["bundledAssemblies"] == [], "unexpected bundled assembly")
    external = {item["id"]: item for item in manifest["dependencies"]["external"]}
    require(set(external) == {"edge-tts", "ffplay"}, "external dependency set drift")
    require(all(item["bundled"] is False for item in external.values()), "external dependency incorrectly marked bundled")
    require(manifest["companion"]["version"] == "1.1.0", "companion manifest version drift")
    require(manifest["companion"]["voiceDiscovery"] == "read_only_revision_and_sha_bound", "voice discovery manifest boundary drift")
    require(manifest["companion"]["speakerPreview"] == "explicit_only_implemented", "speaker preview manifest boundary drift")

    packaged_worker_descriptor = read_json(worker_root / "descriptors" / "worker-describe.json")
    require(packaged_worker_descriptor == descriptor, "packaged worker descriptor drift")
    companion = run_json(worker, release_root, "settings", "companion", "describe", "--json")
    packaged_companion = read_json(worker_root / "descriptors" / "settings-companion-describe.json")
    require(companion == packaged_companion, "live companion descriptor differs from packaged descriptor")
    require(companion["schemaVersion"] == "speech-settings-companion-descriptor.v1", "companion descriptor schema drift")
    require(companion["workerVersion"] == "1.2.5" and companion["companionVersion"] == "1.1.0", "companion version drift")
    previews = [item for item in companion["actions"] if item["id"] == "speaker.preview"]
    require(len(previews) == 1, "speaker.preview action missing")
    preview = previews[0]
    require(preview["sideEffect"] == "physical_audio" and preview["invocation"] == "explicit_only", "speaker.preview side-effect boundary drift")
    require(preview["implementedByCompanion"] is True and preview["explicitAuthorizationRequired"] is True, "speaker.preview implementation boundary drift")

    input_schema = read_json(worker_root / "schemas" / "settings-companion" / "speech-settings-companion-probe-input.schema.json")
    output_schema = read_json(worker_root / "schemas" / "settings-companion" / "speech-settings-companion-probe-output.schema.json")
    discovery_input_schema = read_json(worker_root / "schemas" / "settings-companion" / "speech-settings-voice-discovery-input.schema.json")
    discovery_output_schema = read_json(worker_root / "schemas" / "settings-companion" / "speech-settings-voice-discovery-output.schema.json")
    preview_input_schema = read_json(worker_root / "schemas" / "settings-companion" / "speech-settings-speaker-preview-input.schema.json")
    preview_output_schema = read_json(worker_root / "schemas" / "settings-companion" / "speech-settings-speaker-preview-output.schema.json")
    for schema in (input_schema, output_schema, discovery_input_schema, discovery_output_schema, preview_input_schema, preview_output_schema):
        jsonschema.Draft202012Validator.check_schema(schema)
    probe_input = read_json(evidence["companion-probe-input"])
    probe_output = read_json(evidence["companion-probe-output"])
    jsonschema.Draft202012Validator(input_schema).validate(probe_input)
    jsonschema.Draft202012Validator(output_schema).validate(probe_output)
    require(probe_input["scope"] == "project" and probe_input["effectiveAt"] == "next_task", "companion scope/effectiveAt drift")
    require(probe_input["action"] == "validate_only", "companion probe action drift")
    require(probe_output["status"] == "ready", "companion probe is not ready")
    require(probe_output["routing"]["override"] == "en-US" and probe_output["routing"]["languageTag"] == "en-US", "language override binding drift")
    require(probe_output["routing"]["voiceId"] == "en-US-AvaMultilingualNeural", "override voice binding drift")
    require(probe_output["speakerPreview"]["status"] == "not_run" and probe_output["sideEffectsPerformed"] == [], "companion probe performed a side effect")

    discovery_validate_input = read_json(evidence["voice-discovery-validate-input"])
    discovery_validate_output = read_json(evidence["voice-discovery-validate-output"])
    discovery_live_input = read_json(evidence["voice-discovery-live-input"])
    discovery_live_output = read_json(evidence["voice-discovery-live-output"])
    for discovery_input, discovery_output in (
        (discovery_validate_input, discovery_validate_output),
        (discovery_live_input, discovery_live_output),
    ):
        jsonschema.Draft202012Validator(discovery_input_schema).validate(discovery_input)
        jsonschema.Draft202012Validator(discovery_output_schema).validate(discovery_output)
        require(discovery_output["status"] == "ready", "voice discovery is not ready")
        require(discovery_output["catalogSha256"] and len(discovery_output["catalogSha256"]) == 64, "voice catalog is not hash-bound")
        require(discovery_output["catalogRevision"].endswith(discovery_output["catalogSha256"][:12]), "voice catalog revision/hash binding drift")
        require(discovery_output["settingsMutationCount"] == 0 and discovery_output["physicalSpeakerPlayback"] is False, "voice discovery mutated settings or played audio")
        require(discovery_output["deployment"]["cacheWriteCount"] == 0 and discovery_output["sideEffectsPerformed"] == [], "voice discovery was not read-only")
        require(all(value == 0 for value in discovery_output["taskCreation"].values()), "voice discovery created a knowledge task")
        require({route["languageTag"] for route in discovery_output["routes"]} == {"zh-CN", "en-US", "es-ES", "ja-JP", "ko-KR"}, "voice discovery route coverage drift")
    require(discovery_validate_output["mode"] == "validate_only" and discovery_validate_output["source"] == "supplied_snapshot", "offline discovery mode/source drift")
    require(discovery_validate_output["deployment"]["providerQueryCount"] == 0, "offline discovery queried provider")
    require(discovery_live_output["mode"] == "discover" and discovery_live_output["source"] == "live", "live discovery mode/source drift")
    require(discovery_live_output["deployment"]["providerQueryCount"] == 1 and len(discovery_live_output["voices"]) >= 5, "live discovery provider evidence insufficient")

    preview_input = read_json(evidence["speaker-preview-input"])
    preview_output = read_json(evidence["speaker-preview-output"])
    jsonschema.Draft202012Validator(preview_input_schema).validate(preview_input)
    jsonschema.Draft202012Validator(preview_output_schema).validate(preview_output)
    require(preview_input["action"] == "speaker.preview" and preview_input["explicitSideEffectAuthorization"] is True, "preview was not explicitly authorized")
    require(preview_output["status"] == "completed" and preview_output["naturalCompletion"] is True, "preview did not naturally complete")
    require(preview_output["playbackState"] == "idle" and preview_output["activePlayback"] is False, "preview did not return idle")
    require(preview_output["manualStopRequired"] is False and preview_output["temporaryArtifactsRetained"] is False, "preview stop/temp boundary drift")
    require(preview_output["previewAttemptCount"] == 1 and preview_output["audioPlayerInvocationCount"] == 1, "preview invocation cardinality drift")
    require(all(value == 0 for value in preview_output["taskCreation"].values()), "preview created a capture/export task")
    preview_invocations = evidence["speaker-preview-fake-invocations"].read_text(encoding="utf-8").splitlines()
    require(len(preview_invocations) == 2, "preview fake invocation count drift")
    require(sum("--write-media" in item for item in preview_invocations) == 1, "preview synthesis count drift")
    require(sum("-nodisp" in item for item in preview_invocations) == 1, "preview player count drift")
    override_fixture = read_json(evidence["language-override-fixture"])
    require(override_fixture["scope"] == "project" and override_fixture["effectiveAt"] == "next_task", "override fixture scope drift")
    require(override_fixture["uiInput"] == {"languageStrategy": "override", "languageOverride": "en-US"}, "override fixture UI binding drift")

    unit = read_json(evidence["unit-tests"])
    tests = {item["name"]: item["status"] for item in unit["tests"]}
    require(unit["status"] == "passed" and unit["failures"] == 0 and len(tests) >= 53, "unit suite is insufficient")
    require(tests.get("synthesis publishes immutable audio beyond MAX_PATH through short tool workspace") == "passed", "deep-path synthesis regression evidence missing")
    require(tests.get("playback stages audio beyond MAX_PATH into a short player workspace") == "passed", "deep-path playback regression evidence missing")
    require(tests.get("voice discovery validates supplied catalog offline without mutation or speaker") == "passed", "offline discovery regression evidence missing")
    require(tests.get("voice discovery returns revision and hash-bound natural Edge catalog read-only") == "passed", "live discovery regression evidence missing")
    require(tests.get("speaker preview requires explicit action and naturally returns idle through fake player") == "passed", "explicit preview regression evidence missing")
    require(tests.get("settings adapt status and probe never invoke speaker preview") == "passed", "implicit preview regression evidence missing")

    for kind in ("public-contracts", "release-public-contracts"):
        public = read_json(evidence[kind])
        require(public["status"] == "passed" and public["workerDescribeValidated"] == 1, f"{kind} validation drift")
        require(public["validFixturesAccepted"] >= 2 and public["invalidFixturesRejected"] >= 4, f"{kind} fixture validation insufficient")
    identity = read_json(evidence["destination-neutral-identity"])
    require(identity["status"] == "passed", "destination-neutral identity gate drift")

    snapshot = read_json(evidence["settings-snapshot"])
    speech = snapshot["synthesisRequest"]["payload"]
    require(snapshot["schemaVersion"] == "speech-settings-adapter-snapshot.v1", "settings output schema drift")
    require(speech["language"] == {"strategy": "auto", "override": None}, "language snapshot drift")
    require(speech["voiceSnapshot"]["provider"] == "edge-tts", "provider drift")
    require(speech["voiceSnapshot"]["ratePercent"] == 17, "rate snapshot drift")
    require(speech["voiceSnapshot"]["volumePercent"] == -8, "volume snapshot drift")
    require(speech["voiceSnapshot"]["pitchHz"] == 23, "pitch snapshot drift")
    require(snapshot["fixedSettings"]["legacyAiTts"] == "disabled", "legacy AI TTS boundary drift")
    require(snapshot["fixedSettings"]["playbackPolicy"]["naturalCompletion"] == "idle_replayable", "natural completion policy drift")
    require(snapshot["fixedSettings"]["playbackPolicy"]["replay"] == "from_start", "replay policy drift")
    require(snapshot["fixedSettings"]["cachePolicy"] == {"scope": "task", "reuseSuccessfulAudio": True, "crossTaskReuse": False}, "cache policy drift")
    require(bool(snapshot["synthesisRequest"]["configSnapshot"].get("edgeTtsPath")), "Edge path was not frozen")
    require(bool(snapshot["playbackRequest"]["configSnapshot"].get("ffplayPath")), "player path was not frozen")

    synth_request = read_json(evidence["synthesis-request"])
    play_request = read_json(evidence["playback-request"])
    synth_result = read_json(evidence["synthesis-result"])
    play_result = read_json(evidence["playback-result"])
    require(synth_request["payload"] == snapshot["synthesisRequest"]["payload"], "formal synth request did not consume settings snapshot")
    require(synth_request["configSnapshot"] == snapshot["synthesisRequest"]["configSnapshot"], "formal synth config drift")
    require(play_request["configSnapshot"] == snapshot["playbackRequest"]["configSnapshot"], "formal playback config drift")
    require(len(play_request["inputRefs"]) == 1 and play_request["inputRefs"][0]["kind"] == "audio.speech", "playback did not consume synth audio")
    require(synth_result["status"] == "completed" and synth_result["payload"]["languageTag"] == "zh-CN", "synthesis result drift")
    require(play_result["status"] == "completed" and play_result["payload"]["playbackState"] == "idle_replayable", "playback result drift")
    require(play_result["payload"]["cacheHit"] is False, "playback was reused")
    invocations = evidence["fake-tool-invocations"].read_text(encoding="utf-8").splitlines()
    require(len(invocations) == 2 and "--write-media" in invocations[0] and "-nodisp" in invocations[1], "fake/suppressed invocation evidence drift")
    offline = receipt["offlineWorkerRequestCanary"]
    require(offline["physicalSpeakerPlayback"] is False and offline["networkTtsCalls"] == 0 and offline["externalProductionWrites"] == 0, "offline safety boundary drift")
    companion_summary = receipt["settingsCompanion"]
    require(companion_summary["companionVersion"] == "1.1.0" and companion_summary["sideEffectsPerformed"] == [], "companion receipt summary drift")
    discovery_summary = companion_summary["voiceDiscovery"]
    require(discovery_summary["readOnly"] is True and discovery_summary["speakerPlayback"] is False, "discovery receipt safety drift")
    require(discovery_summary["liveCatalogSha256"] == discovery_live_output["catalogSha256"], "discovery receipt hash drift")
    require(discovery_summary["liveProviderQueryCount"] == 1 and discovery_summary["cacheWriteCount"] == 0, "discovery receipt query/cache drift")
    preview_summary = companion_summary["speakerPreview"]
    require(preview_summary["implemented"] is True and preview_summary["testMode"] == "fake_no_speaker", "preview receipt implementation drift")
    require(preview_summary["previewAttemptCount"] == 1 and preview_summary["audioPlayerInvocationCount"] == 1, "preview receipt cardinality drift")
    require(preview_summary["physicalSpeakerPlayback"] is False and preview_summary["knowledgeTaskCreation"] == 0, "preview receipt safety drift")

    live_path = evidence["live-deep-edge-canary"]
    live = read_json(live_path)
    require(live["workerExeSha256"] == sha256(worker), "live Canary worker hash drift")
    require(live["languageStrategy"] == "override", "live Canary strategy drift")
    require(live["deepPathRequired"] is True and live["deepPathPassed"] is True, "live deep-path requirement not met")
    require(live["physicalSpeakerPlayback"] is False and live["oldAiTtsRouteCount"] == 0, "live Canary safety boundary drift")
    require(len(live["results"]) == 5, "live Canary language count drift")
    expected_languages = {"zh-CN", "en-US", "es-ES", "ja-JP", "ko-KR"}
    require({item["language"] for item in live["results"]} == expected_languages, "live Canary language coverage drift")
    for result in live["results"]:
        require(result["status"] == "completed" and result["deepPathVerified"] is True, f"live result did not complete deeply: {result['language']}")
        require(result["audioFullPathLength"] > 260 and result["audioBytes"] > 0, f"live audio path/bytes insufficient: {result['language']}")
        audio = live_path.parent / pathlib.PurePosixPath(result["audioPath"])
        result_path = live_path.parent / pathlib.PurePosixPath(result["resultPath"])
        audio_fs = filesystem_path(audio)
        result_fs = filesystem_path(result_path)
        require(audio_fs.is_file() and result_fs.is_file(), f"live artifact/result missing: {result['language']}")
        require(audio_fs.stat().st_size == result["audioBytes"] and sha256(audio) == result["audioSha256"], f"live audio identity drift: {result['language']}")

    live_summary = receipt["liveDeepPathEdgeCanary"]
    require(live_summary["networkTtsCalls"] == 5 and live_summary["physicalSpeakerPlayback"] is False, "live receipt safety summary drift")
    require(live_summary["oldAiTtsRouteCount"] == 0 and live_summary["externalProductionWrites"] == 0, "live route/write boundary drift")

    validation_path = receipt_path.parent / "speech-formal-win-x64-independent-validation-receipt.json"
    require(not validation_path.exists(), "independent validation receipt already exists")
    validation = {
        "schemaVersion": 1,
        "receiptType": "speech-formal-win-x64-independent-validation-v1",
        "status": "passed",
        "releaseReceipt": str(receipt_path),
        "releaseReceiptSha256": sha256(receipt_path),
        "formalReleaseRoot": str(release_root),
        "releaseTreeSha256": actual_tree["treeSha256"],
        "releaseManifestSha256": sha256(manifest_path),
        "entrypoint": str(worker),
        "entrypointBytes": worker.stat().st_size,
        "entrypointSha256": sha256(worker),
        "liveDescribe": descriptor,
        "liveCompanionDescribe": companion,
        "settingsSnapshotWorkerRequestCanary": "passed",
        "companionSchemaAndFixtureValidation": "passed",
        "voiceDiscovery": {
            "offlineValidateOnly": "passed",
            "liveReadOnly": "passed",
            "catalogRevision": discovery_live_output["catalogRevision"],
            "catalogSha256": discovery_live_output["catalogSha256"],
            "providerQueryCount": 1,
            "cacheWriteCount": 0,
            "physicalSpeakerPlayback": False,
        },
        "speakerPreview": {
            "implementation": "explicit_only",
            "validationMode": "fake_no_speaker",
            "previewAttemptCount": 1,
            "audioPlayerInvocationCount": 1,
            "naturalCompletion": "idle",
            "knowledgeTaskCreation": 0,
            "physicalSpeakerPlayback": False,
        },
        "publicContractValidation": "passed",
        "destinationNeutralIdentity": "passed",
        "liveDeepPathEdgeLanguages": sorted(expected_languages),
        "liveDeepPathMinimumAudioPathLength": min(item["audioFullPathLength"] for item in live["results"]),
        "physicalSpeakerPlayback": False,
        "networkTtsCalls": 5,
        "oldAiTtsRouteCount": 0,
        "externalProductionWrites": 0,
        "authorityMutated": False,
    }
    validation_path.write_text(json.dumps(validation, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"PASS INDEPENDENT SPEECH FORMAL WIN-X64 VALIDATION {validation_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
