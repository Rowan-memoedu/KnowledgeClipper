from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path


CHECKPOINT_SHA = "3EFB89D6D642F8546ABD14C5A114DE00E14BB7173EF33F03D3FD06DDAA2ADB55"
TREE_SHA = "7B27BACDB1DBB56991272972A5F9309B1D6F782787EB0B99EBD21248F7959574"
WORKER_SHA = "51FF5937C297DAD562AB781D6114374649E1A965EDBB43D575264BEED3DFB19C"
LANGUAGES = {"zh-CN", "en-US", "es-ES", "ja-JP", "ko-KR"}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def tree_inventory(root: Path) -> tuple[int, int, str]:
    rows: list[str] = []
    total = 0
    files = sorted((path for path in root.rglob("*") if path.is_file()), key=lambda path: path.relative_to(root).as_posix())
    for path in files:
        relative = path.relative_to(root).as_posix()
        size = path.stat().st_size
        digest = sha256(path)
        rows.append(f"{relative}\t{size}\t{digest.lower()}")
        total += size
    tree = hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper()
    return len(files), total, tree


def verify_reference(item: dict) -> Path:
    path = Path(item["path"]).resolve()
    require(path.is_file(), f"missing evidence: {path}")
    require(sha256(path) == item["sha256"].upper(), f"evidence hash mismatch: {path}")
    return path


def verify_audio_receipt(path: Path, expected_strategy: str) -> tuple[list[dict], list[Path]]:
    receipt = json.loads(path.read_text(encoding="utf-8"))
    require(receipt["languageStrategy"] == expected_strategy, f"strategy mismatch: {path}")
    require(receipt["workerExeSha256"].upper() == WORKER_SHA, f"worker drift: {path}")
    require(receipt["physicalSpeakerPlayback"] is False and receipt["oldAiTtsRouteCount"] == 0, f"safety drift: {path}")
    results = receipt["results"]
    require({item["detectedLanguage"] for item in results} == LANGUAGES and len(results) == 5, f"language set drift: {path}")
    audio: list[Path] = []
    root = path.parent
    for item in results:
        require(item["strategy"] == expected_strategy and item["status"] == "completed", f"route failed: {item}")
        expected_override = item["language"] if expected_strategy == "override" else None
        require(item["override"] == expected_override, f"override mismatch: {item['language']}")
        snapshot = item["voiceSnapshot"]
        require(snapshot["provider"] == "edge-tts" and snapshot["voicesByLanguage"][item["detectedLanguage"]] == item["voice"], f"voice snapshot mismatch: {item['language']}")
        require((snapshot["ratePercent"], snapshot["volumePercent"], snapshot["pitchHz"]) == (
            receipt["voiceSettings"]["ratePercent"], receipt["voiceSettings"]["volumePercent"], receipt["voiceSettings"]["pitchHz"]
        ), f"voice tuning drift: {item['language']}")
        payload = root / item["audioPath"]
        require(payload.is_file() and payload.stat().st_size == item["audioBytes"], f"audio bytes mismatch: {payload}")
        require(sha256(payload) == item["audioSha256"].upper(), f"audio SHA mismatch: {payload}")
        header = payload.read_bytes()[:3]
        require(item["audioBytes"] > 1000 and (header == b"ID3" or header[:1] == b"\xff"), f"audio is not playable MP3: {payload}")
        audio.append(payload)
    return results, audio


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--receipt", required=True, type=Path)
    args = parser.parse_args()
    receipt_path = args.receipt.resolve()
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    require(receipt["status"] == "passed", "candidate-bound Canary failed")
    authority = receipt["authority"]
    require(sha256(Path(authority["checkpoint"]["path"])) == CHECKPOINT_SHA, "checkpoint hash mismatch")
    release = Path(authority["release"]["root"]).resolve()
    files, total, tree = tree_inventory(release)
    require((files, total, tree) == (79, 248982362, TREE_SHA), f"release tree mismatch: {(files, total, tree)}")
    worker = Path(receipt["worker"]["path"])
    require(worker.is_relative_to(release), "worker is outside authority release root")
    require((worker.stat().st_size, sha256(worker), receipt["worker"]["workerId"], receipt["worker"]["workerVersion"]) == (74240, WORKER_SHA, "speech.edge", "1.2.0"), "worker identity mismatch")

    backend_path = verify_reference(receipt["evidence"]["backendContentAndPlayback"])
    auto_path = verify_reference(receipt["evidence"]["fiveLanguageAuto"])
    override_path = verify_reference(receipt["evidence"]["fiveLanguageOverride"])
    core_path = verify_reference(receipt["evidence"]["coreReuse"])
    verify_reference(receipt["evidence"]["readable"])
    auto_results, auto_audio = verify_audio_receipt(auto_path, "auto")
    override_results, override_audio = verify_audio_receipt(override_path, "override")
    require(len(auto_audio) + len(override_audio) == 10, "expected ten real Edge audio files")

    backend = json.loads(backend_path.read_text(encoding="utf-8"))
    require(backend["fakePlayer"]["physicalSpeakerPlayback"] is False, "physical speaker playback detected")
    require(backend["semantics"]["oldAiTtsRouteCount"] == 0 and backend["semantics"]["externalProductionWrites"] == 0, "backend safety boundary drift")
    playback = {item["case"]: item for item in backend["playbackCases"]}
    expected = {"natural-completion": "idle_replayable", "replay": "idle_replayable", "user-stop": "stopped", "superseded": "superseded", "task-cancel": "cancelled", "timeout": "timed_out"}
    require({name: item["playbackState"] for name, item in playback.items()} == expected, "playback state mismatch")
    require(all(item["cacheHit"] is False for item in playback.values()), "playback result was reused")
    require(playback["natural-completion"]["attemptId"] != playback["replay"]["attemptId"], "replay did not create fresh attempt")
    require(playback["natural-completion"]["audioManifestSha256"] == playback["replay"]["audioManifestSha256"], "replay audio ancestry mismatch")

    core = json.loads(core_path.read_text(encoding="utf-8"))
    require(core["status"] == "passed" and core["synthesize"]["cacheHit"] is True, "Core synth cache reuse failed")
    require(core["synthesize"]["workerStartedOnReuse"] is False, "worker started on synth cache hit")
    require(core["playback"]["reused"] is False and core["playback"]["workerStartedOnSecondRun"] is True, "Core playback was reused")
    assertions = receipt["assertions"]
    require(assertions["physicalSpeakerPlayback"] is False and assertions["oldAiTtsRouteCount"] == 0, "receipt safety assertion drift")

    validation = {
        "schemaVersion": "speech-v123-candidate-bound-validation.v1",
        "status": "passed",
        "completedAtUtc": datetime.now(timezone.utc).isoformat(),
        "candidateReceipt": str(receipt_path),
        "candidateReceiptSha256": sha256(receipt_path),
        "authorityCheckpointSha256": CHECKPOINT_SHA,
        "authorityReleaseTreeSha256": tree,
        "authorityReleaseFiles": files,
        "authorityReleaseBytes": total,
        "workerSha256": WORKER_SHA,
        "realEdgeSynthesisCalls": 14,
        "fiveLanguageAuto": len(auto_results),
        "fiveLanguageOverride": len(override_results),
        "playbackAttempts": len(playback),
        "physicalSpeakerPlayback": False,
        "oldAiTtsRouteCount": 0,
    }
    output = receipt_path.with_name("speech-v123-candidate-bound-validation-receipt.json")
    require(not output.exists(), f"immutable validation receipt exists: {output}")
    output.write_text(json.dumps(validation, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"PASS V123 CANDIDATE-BOUND SPEECH VALIDATION {output}")


if __name__ == "__main__":
    main()
