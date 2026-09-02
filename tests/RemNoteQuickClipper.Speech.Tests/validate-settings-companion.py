import argparse
import hashlib
import json
from pathlib import Path

import jsonschema


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


parser = argparse.ArgumentParser()
parser.add_argument("--project-root", required=True)
parser.add_argument("--unit-root", required=True)
parser.add_argument("--worker-exe", required=True)
args = parser.parse_args()

project_root = Path(args.project_root)
unit_root = Path(args.unit_root)
worker_exe = Path(args.worker_exe)
fixture_root = project_root / "tests" / "RemNoteQuickClipper.Speech.Tests" / "fixtures" / "settings-companion"
evidence_root = unit_root / "settings-companion"
input_schema_path = fixture_root / "speech-settings-companion-probe-input.schema.json"
output_schema_path = fixture_root / "speech-settings-companion-probe-output.schema.json"
binding_fixture_path = fixture_root / "language-override-binding.json"
discovery_input_schema_path = fixture_root / "speech-settings-voice-discovery-input.schema.json"
discovery_output_schema_path = fixture_root / "speech-settings-voice-discovery-output.schema.json"
preview_input_schema_path = fixture_root / "speech-settings-speaker-preview-input.schema.json"
preview_output_schema_path = fixture_root / "speech-settings-speaker-preview-output.schema.json"
receipt_path = evidence_root / "offline-receipt.json"

for path in (
    input_schema_path,
    output_schema_path,
    discovery_input_schema_path,
    discovery_output_schema_path,
    preview_input_schema_path,
    preview_output_schema_path,
    binding_fixture_path,
    evidence_root / "descriptor.json",
    evidence_root / "probe-input.json",
    evidence_root / "probe-output.json",
    evidence_root / "worker-request.json",
    evidence_root / "worker-result.json",
    evidence_root / "voice-discovery-input.json",
    evidence_root / "voice-discovery-output.json",
    evidence_root / "speaker-preview-input.json",
    evidence_root / "speaker-preview-output.json",
    evidence_root / "speaker-preview-fake-invocations.log",
    receipt_path,
    worker_exe,
):
    require(path.is_file(), f"missing settings companion evidence: {path}")

input_schema = json.loads(input_schema_path.read_text(encoding="utf-8"))
output_schema = json.loads(output_schema_path.read_text(encoding="utf-8"))
discovery_input_schema = json.loads(discovery_input_schema_path.read_text(encoding="utf-8"))
discovery_output_schema = json.loads(discovery_output_schema_path.read_text(encoding="utf-8"))
preview_input_schema = json.loads(preview_input_schema_path.read_text(encoding="utf-8"))
preview_output_schema = json.loads(preview_output_schema_path.read_text(encoding="utf-8"))
probe_input = json.loads((evidence_root / "probe-input.json").read_text(encoding="utf-8"))
probe_output = json.loads((evidence_root / "probe-output.json").read_text(encoding="utf-8"))
descriptor = json.loads((evidence_root / "descriptor.json").read_text(encoding="utf-8"))
request = json.loads((evidence_root / "worker-request.json").read_text(encoding="utf-8"))
result = json.loads((evidence_root / "worker-result.json").read_text(encoding="utf-8"))
binding = json.loads(binding_fixture_path.read_text(encoding="utf-8"))
receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
discovery_input = json.loads((evidence_root / "voice-discovery-input.json").read_text(encoding="utf-8"))
discovery_output = json.loads((evidence_root / "voice-discovery-output.json").read_text(encoding="utf-8"))
preview_input = json.loads((evidence_root / "speaker-preview-input.json").read_text(encoding="utf-8"))
preview_output = json.loads((evidence_root / "speaker-preview-output.json").read_text(encoding="utf-8"))

jsonschema.Draft202012Validator.check_schema(input_schema)
jsonschema.Draft202012Validator.check_schema(output_schema)
jsonschema.Draft202012Validator.check_schema(discovery_input_schema)
jsonschema.Draft202012Validator.check_schema(discovery_output_schema)
jsonschema.Draft202012Validator.check_schema(preview_input_schema)
jsonschema.Draft202012Validator.check_schema(preview_output_schema)
jsonschema.validate(probe_input, input_schema)
jsonschema.validate(probe_output, output_schema)
jsonschema.validate(discovery_input, discovery_input_schema)
jsonschema.validate(discovery_output, discovery_output_schema)
jsonschema.validate(preview_input, preview_input_schema)
jsonschema.validate(preview_output, preview_output_schema)

require(descriptor["schemaVersion"] == "speech-settings-companion-descriptor.v1", "descriptor schema drift")
require(descriptor["workerId"] == "speech.edge", "descriptor worker drift")
require(descriptor["scope"]["value"] == "project" and descriptor["scope"]["effectiveAt"] == "next_task", "scope/effectiveAt drift")
require(descriptor["boundaries"]["legacyAiTts"] == "disabled_read_only", "legacy AI boundary drift")
preview = next(action for action in descriptor["actions"] if action["id"] == "speaker.preview")
require(preview["sideEffect"] == "physical_audio" and preview["invocation"] == "explicit_only", "speaker preview boundary drift")
require(preview["implementedByCompanion"] is True and preview["status"] == "implemented", "companion preview implementation drift")
require(preview["explicitAuthorizationRequired"] is True and preview["createsKnowledgeTasks"] is False, "preview authorization/task boundary drift")
require(descriptor["voiceDiscovery"]["readOnly"] is True, "voice discovery must be read-only")
require(descriptor["voiceDiscovery"]["catalogIdentity"] == "catalogRevision+catalogSha256", "voice catalog identity drift")
require(descriptor["boundaries"]["settingsSaveStatusProbePhysicalSpeaker"] == "never", "implicit speaker boundary drift")

language = request["payload"]["language"]
require(language == {"strategy": "override", "override": "en-US"}, f"language override binding drift: {language}")
require(request["payload"]["voiceSnapshot"]["voicesByLanguage"]["en-US"] == "en-US-AvaMultilingualNeural", "voice binding drift")
require("edgeTtsPath" in request["configSnapshot"], "synthesis configSnapshot missing edgeTtsPath")
require(request["payload"]["playbackPolicy"]["autoPlay"] is True, "autoPlay drift")
require(request["payload"]["playbackPolicy"]["replay"] == "from_start", "replay drift")
require(request["payload"]["cachePolicy"] == {"scope": "task", "reuseSuccessfulAudio": True, "crossTaskReuse": False}, "cache policy drift")
require(result["status"] == "completed", "fake synthesis result not completed")
require(result["payload"]["languageTag"] == "en-US" and result["payload"]["voiceId"] == "en-US-AvaMultilingualNeural", "result route drift")
require(result["payload"]["playbackState"] == "idle_replayable", "result playback state drift")

require(discovery_output["mode"] == "validate_only" and discovery_output["source"] == "supplied_snapshot", "offline discovery source drift")
require(discovery_output["status"] == "ready" and discovery_output["provider"] == "edge-tts", "offline discovery readiness drift")
require(len(discovery_output["catalogSha256"]) == 64 and discovery_output["catalogRevision"].startswith("edge-tts-"), "catalog revision/hash missing")
require(discovery_output["settingsMutationCount"] == 0 and discovery_output["physicalSpeakerPlayback"] is False, "discovery read-only boundary drift")
require(discovery_output["deployment"]["providerQueryCount"] == 0 and discovery_output["deployment"]["cacheWriteCount"] == 0, "validate-only discovery performed I/O")
require(discovery_output["sideEffectsPerformed"] == [], "validate-only discovery performed side effects")
require(len(discovery_output["routes"]) == 5 and all(route["availability"] == "available" for route in discovery_output["routes"]), "five-language natural voice routing missing")

require(preview_input["explicitSideEffectAuthorization"] is True and preview_input["action"] == "speaker.preview", "preview was not explicit")
require(preview_output["status"] == "completed" and preview_output["playbackState"] == "idle", "fake preview did not naturally return idle")
require(preview_output["naturalCompletion"] is True and preview_output["manualStopRequired"] is False, "preview required manual stop")
require(preview_output["previewAttemptCount"] == 1 and preview_output["audioPlayerInvocationCount"] == 1, "preview invocation count drift")
require(all(value == 0 for value in preview_output["taskCreation"].values()), "preview created a knowledge task")
preview_invocations = (evidence_root / "speaker-preview-fake-invocations.log").read_text(encoding="utf-8").splitlines()
require(sum("--write-media" in line for line in preview_invocations) == 1, "fake preview synthesis count drift")
require(sum("-nodisp" in line for line in preview_invocations) == 1, "fake preview player count drift")

require(binding["adapterInput"]["settings.languageOverride"] == "en-US", "Desktop/Core binding fixture drift")
require(binding["expectedWorkerBinding"]["synthesisRequest.payload.language.override"] == "en-US", "binding target drift")
require(receipt["status"] == "passed", "offline receipt failed")
require(receipt["physicalSpeakerCalls"] == 0 and receipt["networkTtsCalls"] == 0, "offline side-effect count drift")
require(receipt["fakePreviewPlayerCalls"] == 1 and receipt["previewAttemptCount"] == 1, "offline preview evidence count drift")
require(receipt["workerVersion"] == descriptor["workerVersion"], "worker version mismatch")

receipt_artifacts = {Path(item["path"]).resolve(): item for item in receipt["artifacts"]}
for path, item in receipt_artifacts.items():
    require(path.is_file(), f"receipt artifact missing: {path}")
    require(path.stat().st_size == item["bytes"], f"receipt bytes mismatch: {path}")
    require(sha256(path) == item["sha256"], f"receipt sha mismatch: {path}")

print(f"PASS SETTINGS COMPANION schema={descriptor['schemaVersion']} companion={descriptor['companionVersion']} worker={descriptor['workerVersion']}")
print(f"PASS OFFLINE OVERRIDE {language['strategy']}:{language['override']} -> {result['payload']['languageTag']} / {result['payload']['voiceId']}")
print(f"PASS READ-ONLY VOICE CATALOG revision={discovery_output['catalogRevision']} sha256={discovery_output['catalogSha256']}")
print(f"PASS EXPLICIT FAKE PREVIEW attempt={preview_output['previewAttemptCount']} player={preview_output['audioPlayerInvocationCount']} state={preview_output['playbackState']}")
print(f"PASS ZERO PHYSICAL SPEAKER speaker={receipt['physicalSpeakerCalls']} networkTts={receipt['networkTtsCalls']}")
print(f"RECEIPT {receipt_path}")
