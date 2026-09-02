from __future__ import annotations

import sys
import copy
import unittest
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT / "tools"))

import verify_core_phase1_v2102_checkpoint as gate  # noqa: E402
import verify_core_phase1_checkpoint as common  # noqa: E402


class CorePhase1V2102CheckpointTests(unittest.TestCase):
    def test_current_checkpoint_and_typed_process_evidence_pass(self) -> None:
        result = gate.evaluate()
        self.assertEqual("PASS-CORE-V2.10.2-PREREQUISITE-DESKTOP-MIGRATION-PENDING", result["status"])
        self.assertEqual(11, result["fileBindings"]["matched"])
        self.assertEqual(0, result["frozenV1"]["mismatches"])

    def test_remnote_operations_are_exact_and_readonly(self) -> None:
        result = common.load(gate.EVIDENCE_ROOT / "jobs/module-actions/typed-remnote-probe-process-canary/result.json")
        self.assertEqual(["status", "search", "list", "read"], [item["operation"] for item in result["toolChecks"]])
        self.assertEqual(0, result["externalWriteCount"])
        self.assertTrue(result["sanitized"])

    def test_voice_discovery_has_no_mutation_or_playback(self) -> None:
        result = common.load(gate.EVIDENCE_ROOT / "jobs/module-actions/typed-speech-voices-process-canary/result.json")
        self.assertEqual(0, result["settingsMutationCount"])
        self.assertFalse(result["physicalSpeakerPlayback"])
        self.assertEqual([], result["sideEffectsPerformed"])

    def test_preview_authorization_drift_produces_no_receipt_or_result(self) -> None:
        root = gate.EVIDENCE_ROOT / "jobs/module-actions/typed-speech-preview-rejected-process-canary"
        action = common.load(root / "input.json")
        self.assertNotEqual("typed-speech-preview-rejected-process-canary", action["authorization"]["actionId"])
        self.assertFalse((root / "receipt.json").exists())
        self.assertFalse((root / "result.json").exists())

    def test_preview_is_one_fake_attempt_without_physical_speaker(self) -> None:
        receipt = common.load(gate.PROCESS_RECEIPT)
        result = common.load(gate.EVIDENCE_ROOT / "jobs/module-actions/typed-speech-preview-process-canary/result.json")
        self.assertEqual(1, result["previewAttemptCount"])
        self.assertEqual(1, result["audioPlayerInvocationCount"])
        self.assertEqual(0, receipt["typedPublicSettings"]["speechPreview"]["physicalSpeakerInvocationsDuringCanary"])

    def test_remnote_blocked_is_a_completed_business_outcome(self) -> None:
        receipt = common.load(gate.EVIDENCE_ROOT / "jobs/module-actions/typed-remnote-probe-process-canary/receipt.json")
        receipt["status"] = "blocked"
        common.validate(PROJECT / "contracts/core/v2.10/module-control/schemas/module-action-receipt.v6.schema.json", receipt)
        self.assertTrue(receipt["accepted"])
        self.assertEqual("completed", receipt["executionStatus"])

    def test_failure_and_unknown_state_never_retry_or_publish_output(self) -> None:
        base = common.load(gate.EVIDENCE_ROOT / "jobs/module-actions/typed-speech-preview-process-canary/receipt.json")
        for status in ("failed", "unknown_external_state"):
            receipt = copy.deepcopy(base)
            receipt.update({"accepted": False, "status": status, "executionStatus": status, "outputRef": None, "outputSha256": None, "outputRefs": [], "error": {"code": "worker_exit_or_timeout"}, "automaticRetryAllowed": False})
            gate.verify_terminal_failure(receipt)

    def test_output_refs_reject_absolute_traversal_and_sha_drift(self) -> None:
        base = common.load(gate.EVIDENCE_ROOT / "jobs/module-actions/typed-remnote-probe-process-canary/receipt.json")
        for path in ("C:/outside/result.json", "module-actions/../../outside.json"):
            receipt = copy.deepcopy(base)
            receipt["outputRefs"][0]["path"] = path
            with self.assertRaisesRegex(common.GateFailure, "escaped jobs-root"):
                gate.verify_output_reference(receipt, gate.EVIDENCE_ROOT / "jobs")
        receipt = copy.deepcopy(base)
        receipt["outputRefs"][0]["expectedSha256"] = "0" * 64
        with self.assertRaisesRegex(common.GateFailure, "SHA drift"):
            gate.verify_output_reference(receipt, gate.EVIDENCE_ROOT / "jobs")


if __name__ == "__main__":
    unittest.main()
