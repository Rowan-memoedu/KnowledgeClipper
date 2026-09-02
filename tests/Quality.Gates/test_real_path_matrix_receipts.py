from __future__ import annotations

import hashlib
import json
import os
import unittest
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]


def load_json(path: Path) -> dict:
    return json.loads(windows_path(path).read_text(encoding="utf-8-sig"))


def windows_path(path: Path) -> Path:
    resolved = path.resolve()
    if os.name == "nt" and not str(resolved).startswith("\\\\?\\"):
        return Path("\\\\?\\" + str(resolved))
    return resolved


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with windows_path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def project_path(value: str, *, base: Path | None = None) -> Path:
    candidate = Path(value)
    if candidate.is_absolute():
        return candidate
    if base is not None:
        return base / candidate
    return PROJECT_ROOT / candidate


def assert_file_hash(test: unittest.TestCase, path: Path, expected: str) -> None:
    test.assertTrue(windows_path(path).is_file(), f"missing evidence: {path}")
    test.assertEqual(expected.lower(), sha256(path), f"evidence drift: {path}")


class RealPathMatrixReceiptTests(unittest.TestCase):
    def test_latest_matrix_has_machine_readable_authority_and_no_stale_boundary(self) -> None:
        roots = sorted((PROJECT_ROOT / "canary/path-matrix").glob("quality-real-path-matrix-*"))
        self.assertTrue(roots)
        latest = roots[-1]
        receipt = load_json(latest / "matrix-receipt.json")
        index = (latest / "input-output-index.md").read_text(encoding="utf-8-sig")
        stale = "新 exporter 尚未进入新 authority checkpoint"
        current = "已绑定 v1.2.2 authority；仍仅隔离 RemNote，desktop→real-export E2E 延后"
        self.assertNotIn(stale, index)
        self.assertIn(current, index)
        authority = receipt.get("authority")
        self.assertIsNotNone(authority)
        self.assertEqual(("PASS", "authority-admitted", "1.2.2", 5), (authority["status"], authority["admissionStatus"], authority["registryVersion"], authority["workers"]))
        self.assertEqual(0, authority["releaseTree"]["qualificationDifferences"])
        self.assertEqual("f15f21e5489b4002086b0664144606a7e2ef5bd40e1aa02ab90f34ec277c2503", authority["releaseTree"]["sha256"])
        for name in ("checkpoint", "receipt", "qualification"):
            item = authority[name]
            self.assertTrue(item["path"])
            self.assertEqual(64, len(item["sha256"]))
            assert_file_hash(self, PROJECT_ROOT / item["path"], item["sha256"])

    def test_stable_21_remains_byte_exact(self) -> None:
        receipt_path = PROJECT_ROOT / "receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json"
        receipt = load_json(receipt_path)
        self.assertEqual("contracts-v1-20260825T115938Z", receipt["checkpointId"])
        self.assertEqual(21, len(receipt["files"]))
        for item in receipt["files"]:
            assert_file_hash(self, Path(item["path"]), item["sha256"])

    def test_path_a_is_real_isolated_remnote_readback_not_filename_inference(self) -> None:
        receipt_path = PROJECT_ROOT / "canary/pm/A/061950-8598/path-A-receipt.json"
        assert_file_hash(self, receipt_path, "1a19dce1e2d6663bef4ef2b7bb4a21a8ac4d1631eb0ffcffd642dbd80a6456cc")
        receipt = load_json(receipt_path)
        self.assertEqual(("A", "passed"), (receipt["pathId"], receipt["status"]))
        self.assertTrue(receipt["result"]["readbackVerified"])
        self.assertEqual("created", receipt["result"]["outcome"])
        self.assertFalse(receipt["isolation"]["formalClipRootTouched"])
        self.assertTrue(receipt["noDelete"])
        for section, hash_key in (
            (receipt["originalInput"], "contentReceiptSha256"),
            (receipt["projection"], "sha256"),
            (receipt["result"], "sha256"),
            (receipt["writeReceipt"], "sha256"),
            (receipt["independentReadback"], "sha256"),
        ):
            path_key = "contentReceipt" if hash_key == "contentReceiptSha256" else "path"
            assert_file_hash(self, project_path(section[path_key]), section[hash_key])

    def test_path_b_is_real_isolated_anki_readback_not_filename_inference(self) -> None:
        receipt_path = PROJECT_ROOT / "canary/pm/B/062210-e4bc/path-B-receipt.json"
        assert_file_hash(self, receipt_path, "9ba6cd843ddc00cdf08cdab9abcae88d0e3481113fef0ba7ae918c141f5e8c14")
        receipt = load_json(receipt_path)
        self.assertEqual(("B", "passed"), (receipt["pathId"], receipt["status"]))
        self.assertEqual("零碎知识剪藏 Canary", receipt["isolation"]["deck"])
        self.assertFalse(receipt["isolation"]["dailyDeckTouched"])
        self.assertEqual(4, receipt["observed"]["firstWrite"]["succeeded"])
        self.assertEqual(4, receipt["observed"]["sameRevision"]["already_current"])
        self.assertTrue(receipt["independentReadback"]["frontBackExact"])
        assert_file_hash(self, project_path(receipt["source"]["moduleReceipt"]), receipt["source"]["moduleReceiptSha256"])
        assert_file_hash(self, project_path(receipt["source"]["contentReceipt"]), receipt["source"]["contentReceiptSha256"])
        assert_file_hash(self, project_path(receipt["independentReadback"]["path"]), receipt["independentReadback"]["sha256"])

    def test_path_c_mineru_to_live_deepseek_has_original_and_stage_hashes(self) -> None:
        receipt_path = PROJECT_ROOT / "canary/path-matrix/C-image-mineru-deepseek-20260826T061529Z/canary-receipt.json"
        receipt = load_json(receipt_path)
        self.assertEqual("passed", receipt["status"])
        self.assertEqual("content.deepseek", receipt["worker"]["id"])
        self.assertEqual("0.5.2", receipt["worker"]["version"])
        self.assertEqual("completed", receipt["result"]["status"])
        self.assertTrue(receipt["providerEvidence"])
        self.assertTrue(all(item["provider"] == "deepseek" and item["mode"] == "live" for item in receipt["providerEvidence"]))
        assert_file_hash(self, project_path(receipt["source"]["path"]), receipt["source"]["sha256"])
        assert_file_hash(self, project_path(receipt["source"]["receipt"]), receipt["source"]["receiptSha256"])
        for section in ("request", "result", "learningBundle"):
            assert_file_hash(self, project_path(receipt[section]["path"], base=receipt_path.parent), receipt[section]["sha256"])

    def test_path_d_real_edge_audio_is_preserved_without_physical_playback(self) -> None:
        receipt_path = PROJECT_ROOT / "canary/pm/D/012237-a335/path-D-receipt.json"
        assert_file_hash(self, receipt_path, "41ed299c845aa48266da314528b817b7d361176eea1446335026dc7f68003fbb")
        receipt = load_json(receipt_path)
        self.assertEqual(("D", "passed"), (receipt["pathId"], receipt["status"]))
        self.assertEqual("quality-real-path/1.1.0", receipt["schemaVersion"])
        self.assertEqual(2, receipt["externalCalls"]["deepseekLiveSources"])
        self.assertEqual(4, receipt["externalCalls"]["edgeTts"])
        self.assertEqual(0, receipt["externalCalls"]["physicalSpeakerPlayback"])
        self.assertTrue(receipt["reproducibility"]["allReferencesUseSingleBase"])
        refs = [receipt["source"]["moduleReceipt"], receipt["source"]["validationReceipt"]]
        for source in receipt["contentSources"]:
            refs.extend((source["original"], source["cleanedSpokenText"]))
        refs.extend(receipt["audio"])
        self.assertEqual(10, len(refs))
        for item in refs:
            self.assertEqual("projectRoot", item["pathBase"])
            self.assertFalse(Path(item["path"]).is_absolute())
            assert_file_hash(self, PROJECT_ROOT / item["path"], item["sha256"])
            self.assertEqual(item["bytes"], windows_path(PROJECT_ROOT / item["path"]).stat().st_size)

    def test_legacy_path_d_ambiguous_base_is_rejected_as_reproducible_evidence(self) -> None:
        legacy = load_json(PROJECT_ROOT / "canary/pm/D/011556-7ad9/path-D-receipt.json")
        self.assertEqual("quality-real-path/1.0.0", legacy["schemaVersion"])
        self.assertTrue(any(item["original"]["path"].startswith("inputs/") for item in legacy["contentSources"]))
        self.assertTrue(any(item["path"].startswith("jobs/sbc/") for item in legacy["audio"]))
        self.assertFalse(all("pathBase" in item["original"] for item in legacy["contentSources"]))

    def test_content_052_authority_contains_eight_completed_live_scenarios(self) -> None:
        receipt_path = PROJECT_ROOT / "receipts/content-worker-v0.5.2-live-synthetic-authority-20260826T0612Z.json"
        assert_file_hash(self, receipt_path, "7118355e0273e070e8a667688222205cc28ab5c33e092d8024fe5f384579faec")
        receipt = load_json(receipt_path)
        self.assertEqual("completed", receipt["status"])
        self.assertEqual(8, receipt["liveMatrix"]["scenarioCount"])
        self.assertEqual(8, len(receipt["completedCanaries"]))
        self.assertTrue(all(item["status"] == "completed" and item["providerEvidence"] for item in receipt["completedCanaries"]))
        for item in receipt["completedCanaries"]:
            assert_file_hash(self, PROJECT_ROOT / item["path"], item["sha256"])

    def test_registry_121_remains_historical_runtime_snapshot(self) -> None:
        root = PROJECT_ROOT / "contracts/authority/worker-registry/v1.2.1-20260826T061500Z"
        registry = load_json(root / "worker-registry.v1.json")
        dependency = load_json(root / "dependency-manifest.v1.json")
        self.assertEqual("1.2.1", registry["registryVersion"])
        self.assertEqual(5, len(registry["workers"]))
        self.assertFalse((root / "checkpoint.json").exists())
        release_root = PROJECT_ROOT / dependency["releaseRoot"]
        for item in dependency["keyFiles"]:
            assert_file_hash(self, release_root / item["path"], item["sha256"])

    def test_registry_122_is_append_only_authority_admitted_and_tree_exact(self) -> None:
        root = PROJECT_ROOT / "contracts/authority/worker-registry/v1.2.2-20260828T012500Z"
        checkpoint_path = root / "checkpoint.json"
        authority_receipt_path = PROJECT_ROOT / "receipts/knowledge-clipper-authority-1.2.2-20260828T012800Z.json"
        assert_file_hash(self, checkpoint_path, "7b2efe4d6a144494b0c18f0b84150906bb0f229374dd82f6bb83c6136a2dbfa5")
        assert_file_hash(self, authority_receipt_path, "03f43c0194354f3bb9fbe2cbc37147df7681ee986d32bf5eb312840f80f2c831")
        checkpoint = load_json(checkpoint_path)
        authority_receipt = load_json(authority_receipt_path)
        self.assertEqual("authority-admitted", checkpoint["admissionStatus"])
        self.assertEqual("authority-admitted", authority_receipt["admissionStatus"])
        self.assertEqual((21, 0), (checkpoint["frozenV1"]["files"], checkpoint["frozenV1"]["mismatches"]))
        self.assertEqual(("1.2.2", 5), (checkpoint["registry"]["registryVersion"], checkpoint["registry"]["workers"]))
        self.assertEqual((79, 248977927), (checkpoint["release"]["files"], int(checkpoint["release"]["bytes"])))
        self.assertEqual("valid", checkpoint["validation"]["status"])
        self.assertEqual("1.0.1", next(item["workerVersion"] for item in checkpoint["workers"] if item["workerId"] == "export.remnote"))
        release_root = Path(checkpoint["release"]["root"])
        files = [path for path in windows_path(release_root).rglob("*") if path.is_file()]
        entries: list[str] = []
        total_bytes = 0
        for file_path in files:
            normalized_file = Path(str(file_path).removeprefix("\\\\?\\"))
            relative = str(normalized_file.relative_to(release_root)).replace("\\", "/")
            size = file_path.stat().st_size
            total_bytes += size
            entries.append(f"{relative}\t{size}\t{sha256(normalized_file)}")
        tree_hash = hashlib.sha256("\n".join(sorted(entries)).encode("utf-8")).hexdigest()
        self.assertEqual((79, 248977927), (len(files), total_bytes))
        self.assertEqual(checkpoint["release"]["treeSha256"].lower(), tree_hash)

    def test_core_remnote_failures_remain_two_distinct_no_go_boundaries(self) -> None:
        first = PROJECT_ROOT / "canary/backend-full-pipeline/20260826T061000Z/jobs/backend-remnote-20260826t061000z/runs/run-001/stages/export-remnote/attempts/attempt-20260826060138904-1fe053d879e6465b97b1364298dff9a2/result.json"
        second = PROJECT_ROOT / "canary/backend-full-pipeline/20260826T062000Z/jobs/backend-remnote-20260826t062000z/runs/run-001/stages/export-remnote/attempts/attempt-20260826060610504-e1ea692f7db9451e87a26f7af5ae33b1/result.json"
        first_result = load_json(first)
        second_result = load_json(second)
        self.assertEqual(("failed", "binding.entrypoint_missing"), (first_result["status"], first_result["error"]["code"]))
        self.assertEqual(("unknown_external_state", "effect.worker_exit_unknown"), (second_result["status"], second_result["error"]["code"]))
        self.assertEqual("export.remnote", second_result["worker"]["id"])
        self.assertNotEqual(sha256(first), sha256(second))

    def test_current_core_remnote_long_path_and_idempotency_pass_scoped(self) -> None:
        root = PROJECT_ROOT / "canary/backend-full-pipeline/20260828T012314Z-general-max-path-regression-and-isolated-remnote-destination"
        result_path = root / "result.remnote.json"
        replay_path = root / "result.remnote-idempotency.json"
        readable_path = root / "readable-output.remnote.md"
        assert_file_hash(self, result_path, "6194dbaa96e0fb5632916c86a85ed7a7eb636e640d5a2df5ceb48e3c02da9a56")
        assert_file_hash(self, replay_path, "9f57a9a0e1e4878eccf601f96b53c16f921a034f1d8775b0f7268e4b4e6d10c4")
        assert_file_hash(self, readable_path, "339018461c44a89b0638f3f84c49b9e7edc9d35014d8001771623659fff96347")
        result = load_json(result_path)
        replay = load_json(replay_path)
        self.assertEqual("succeeded", result["status"])
        self.assertEqual({"content.deepseek": 1, "export.remnote": 1}, result["invocationCounts"])
        self.assertEqual(["0.5.2", "1.0.1"], [item["workerVersion"] for item in result["attempts"]])
        self.assertTrue(all(item["status"] == "completed" for item in result["attempts"]))
        self.assertEqual("mCthh3jTMJ8zMqMEc", result["remnote"]["isolatedParentRemId"])
        self.assertEqual("created", result["remnote"]["outcome"])
        self.assertTrue(result["remnote"]["readbackVerified"])
        self.assertEqual("already_applied", replay["outcome"])
        self.assertTrue(replay["readbackVerified"])
        self.assertEqual(result["remnote"]["documentRemId"], replay["documentRemId"])
        job_root = root / "jobs/backend-remnote-20260828t012314z"
        for item in result["outputs"]:
            assert_file_hash(self, job_root / item["manifestPath"], item["expectedSha256"])
        # The shared Canary root later received append-only v1.2.3 runs. Pin
        # this historical assertion to its immutable run-001 subtree; the
        # full original 43/24 aggregate remains receipt-bound in the authority
        # qualification test below.
        files = [path for path in windows_path(job_root / "runs/run-001").rglob("*") if path.is_file()]
        lengths = [len(str(path).removeprefix("\\\\?\\")) for path in files]
        long_lengths = [length for length in lengths if length > 260]
        self.assertEqual((26, 18, 304, 361), (len(files), len(long_lengths), min(long_lengths), max(long_lengths)))

    def test_speech_core_reuse_and_playback_non_reuse_pass(self) -> None:
        receipt_path = PROJECT_ROOT / "receipts/knowledge-clipper-speech-reuse-20260828T012200Z.json"
        assert_file_hash(self, receipt_path, "302149dae22d5125359c3f29a868090ac1b3d3fcf0683c06ded3331171afbc0f")
        receipt = load_json(receipt_path)
        synthesize = receipt["synthesize"]
        playback = receipt["playback"]
        self.assertEqual("passed", receipt["status"])
        self.assertEqual(synthesize["envelopeOperationId"], synthesize["payloadOperationId"])
        self.assertTrue(synthesize["cacheHit"])
        self.assertEqual(synthesize["firstAttemptId"], synthesize["reusedFromAttemptId"])
        self.assertEqual(1, synthesize["workerStarts"])
        self.assertEqual(2, playback["workerStarts"])
        self.assertFalse(playback["resultReuse"])
        self.assertTrue(playback["fromStart"])
        self.assertTrue(receipt["isolation"]["nonSpeechPayloadUntouched"])
        assert_file_hash(self, Path(receipt["source"]["engine"]["path"]), receipt["source"]["engine"]["sha256"])
        # OrchestratorHost.cs advanced after this historical receipt. Do not
        # mistake the mutable source tree for immutable candidate evidence;
        # current v1.2.3 Core reuse is independently pinned by the candidate-
        # bound Speech receipt in the supplement gate.
        host = receipt["source"]["host"]
        self.assertTrue(Path(host["path"]).is_file())
        self.assertNotEqual(host["sha256"].lower(), sha256(Path(host["path"])))

    def test_authority_122_qualification_binds_core_result_and_readback(self) -> None:
        receipt_path = PROJECT_ROOT / "receipts/knowledge-clipper-authority-1.2.2-e2e-qualification-20260828T013200Z.json"
        assert_file_hash(self, receipt_path, "0a89a8b5786cfa74c40e7413ee3de5c34e80a94040b358b21c29acaf8f273967")
        receipt = load_json(receipt_path)
        self.assertEqual("passed", receipt["status"])
        self.assertEqual("1.2.2", receipt["authority"]["registry"]["version"])
        self.assertEqual((79, 0), (receipt["authority"]["releaseFiles"], receipt["authority"]["canaryReleaseDifferences"]))
        self.assertEqual(["0.5.2", "1.0.1"], [item["workerVersion"] for item in receipt["pipeline"]["attempts"]])
        self.assertEqual("created", receipt["remnote"]["initialOutcome"])
        self.assertEqual("already_applied", receipt["remnote"]["replayOutcome"])
        self.assertEqual(receipt["remnote"]["documentRemId"], receipt["remnote"]["replayDocumentRemId"])
        self.assertFalse(receipt["remnote"]["deletedOrCleaned"])
        self.assertEqual((43, 24, 304, 361), tuple(receipt["longPath"][key] for key in ("files", "over260", "shortest", "longest")))
        for item in receipt["evidence"].values():
            assert_file_hash(self, Path(item["path"]), item["sha256"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
