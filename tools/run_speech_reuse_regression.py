from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import tempfile
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[1]
FAKE_WORKER = PROJECT_ROOT / "tests/FakeWorker/bin/Release/net8.0/RemNoteQuickClipper.FakeWorker.exe"


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")


def result(job_root: Path, run_id: str, stage_id: str) -> dict:
    paths = list((job_root / "runs" / run_id / "stages" / stage_id).rglob("result.json"))
    if len(paths) != 1:
        raise AssertionError(f"expected one result for {run_id}/{stage_id}, got {len(paths)}")
    return json.loads(paths[0].read_text(encoding="utf-8"))


def worker_started(job_root: Path, run_id: str, stage_id: str) -> bool:
    return any((job_root / "runs" / run_id / "stages" / stage_id).rglob("progress.ndjson"))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--orchestrator", required=True, type=Path)
    args = parser.parse_args()
    orchestrator = args.orchestrator.resolve()
    if not orchestrator.is_file() or not FAKE_WORKER.is_file():
        raise RuntimeError("orchestrator or built FakeWorker executable is missing")

    test_root = Path(tempfile.mkdtemp(prefix="rqc-speech-reuse-"))
    try:
        job_root = test_root / "task-speech-reuse"
        relative_worker = FAKE_WORKER.relative_to(PROJECT_ROOT).as_posix()
        registry = {
            "schemaVersion": "1.0.0",
            "registryVersion": "1.0.0",
            "profiles": ["parity-remnote", "cards-remnote", "cards-anki"],
            "workers": [
                {
                    "workerId": "speech.edge",
                    "contractVersionRange": ">=1.0.0 <2.0.0",
                    "entrypoint": {"path": relative_worker, "args": ["--worker-id", "speech.edge"]},
                    "stages": ["speech.synthesize", "speech.playback"],
                    "inputKinds": ["text.spoken", "audio.speech"],
                    "outputKinds": ["audio.speech"],
                    "requiredForProfiles": [],
                    "sideEffectLevel": "local",
                    "idempotencyLevel": "idempotent",
                    "enabledByDefault": True,
                },
                {
                    "workerId": "test.fake",
                    "contractVersionRange": ">=1.0.0 <2.0.0",
                    "entrypoint": {"path": relative_worker, "args": []},
                    "stages": ["test.grace"],
                    "inputKinds": [],
                    "outputKinds": [],
                    "requiredForProfiles": [],
                    "sideEffectLevel": "local",
                    "idempotencyLevel": "idempotent",
                    "enabledByDefault": True,
                },
            ],
        }
        plan = {
            "schemaVersion": "1.0.0",
            "planVersion": "1.1.0",
            "taskId": "task-speech-reuse",
            "maxConcurrency": 2,
            "stages": [
                {
                    "stageId": "speech-synthesize",
                    "workerId": "speech.edge",
                    "operation": "speech.synthesize",
                    "dependsOn": [],
                    "inputRefs": [],
                    "outputKinds": ["audio.speech"],
                    "timeoutMs": 5000,
                    "retryPolicy": {"maxAttempts": 1, "backoffMs": 0},
                    "configSnapshot": {},
                    "payload": {"spokenText": "quality reuse fixture"},
                },
                {
                    "stageId": "speech-playback",
                    "workerId": "speech.edge",
                    "operation": "speech.playback",
                    "dependsOn": ["speech-synthesize"],
                    "inputRefs": [],
                    "outputKinds": [],
                    "timeoutMs": 5000,
                    "retryPolicy": {"maxAttempts": 1, "backoffMs": 0},
                    "configSnapshot": {},
                    "payload": {"playback": "from_start"},
                },
                {
                    "stageId": "ordinary-transform",
                    "workerId": "test.fake",
                    "operation": "test.grace",
                    "dependsOn": [],
                    "inputRefs": [],
                    "outputKinds": [],
                    "timeoutMs": 5000,
                    "retryPolicy": {"maxAttempts": 1, "backoffMs": 0},
                    "configSnapshot": {},
                    "payload": {"mode": "success"},
                },
            ],
        }
        registry_path = test_root / "registry.json"
        plan_path = test_root / "pipeline.json"
        write_json(registry_path, registry)
        write_json(plan_path, plan)
        runs = []
        for run_id in ("run-001", "run-002"):
            completed = subprocess.run(
                [
                    str(orchestrator), "run",
                    "--pipeline", str(plan_path),
                    "--registry", str(registry_path),
                    "--release-root", str(PROJECT_ROOT),
                    "--job-root", str(job_root),
                    "--run-id", run_id,
                ],
                capture_output=True,
                text=True,
                timeout=60,
                check=False,
            )
            if completed.returncode != 0:
                raise AssertionError(f"{run_id} failed: {completed.stdout}\n{completed.stderr}")
            runs.append(json.loads(completed.stdout))

        first_synth = result(job_root, "run-001", "speech-synthesize")
        second_synth = result(job_root, "run-002", "speech-synthesize")
        first_playback = result(job_root, "run-001", "speech-playback")
        second_playback = result(job_root, "run-002", "speech-playback")
        second_ordinary = result(job_root, "run-002", "ordinary-transform")
        synth_payload = second_synth.get("payload") or {}
        ordinary_payload = second_ordinary.get("payload") or {}
        assert second_synth["operationId"] == synth_payload.get("operationId")
        assert synth_payload.get("cacheHit") is True
        assert synth_payload.get("reusedFromAttemptId") == first_synth["attemptId"]
        assert not worker_started(job_root, "run-002", "speech-synthesize")
        assert second_playback["attemptId"] != first_playback["attemptId"]
        assert worker_started(job_root, "run-002", "speech-playback")
        assert not any(item.startswith("Reused successful outputs") for item in second_playback.get("warnings", []))
        assert "cacheHit" not in ordinary_payload
        assert "reusedFromAttemptId" not in ordinary_payload
        assert "operationId" not in ordinary_payload
        observed = {
            "schemaVersion": "speech-reuse-black-box.v1",
            "status": "passed",
            "orchestrator": str(orchestrator),
            "runs": runs,
            "synthesize": {
                "firstAttemptId": first_synth["attemptId"],
                "reusedAttemptId": second_synth["attemptId"],
                "operationId": second_synth["operationId"],
                "payloadOperationId": synth_payload["operationId"],
                "cacheHit": synth_payload["cacheHit"],
                "reusedFromAttemptId": synth_payload["reusedFromAttemptId"],
                "workerStartedOnReuse": False,
                "audioOutputs": sum(item.get("kind") == "audio.speech" for item in second_synth.get("outputs", [])),
            },
            "playback": {
                "firstAttemptId": first_playback["attemptId"],
                "secondAttemptId": second_playback["attemptId"],
                "workerStartedOnSecondRun": True,
                "reused": False,
            },
            "nonSpeechReuseMetadataPollution": False,
            "externalProductionApisInvoked": False,
        }
        print(json.dumps(observed, ensure_ascii=False, indent=2))
        return 0
    finally:
        shutil.rmtree(test_root, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
