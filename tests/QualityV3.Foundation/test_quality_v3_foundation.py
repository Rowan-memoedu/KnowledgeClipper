from __future__ import annotations

import copy
import json
import shutil
import tempfile
import unittest
from pathlib import Path

import quality_v3_gate as gate


ROOT = Path(__file__).resolve().parent
FIXTURES = ROOT / "fixtures"
CANONICAL = FIXTURES / "canonical-valid"
COUNTEREXAMPLES = FIXTURES / "counterexamples"
VERTICAL = FIXTURES / "vertical-slices"
EXPECTED_COMMIT = "6fc44e18637e00d394227ec4b15ad4c06054102a"
EXPECTED_SOURCE = "75061E04AEB2C107BE0A1BBE74E0694DE522A0D45E4AD9C33DE16AC2622D91B7"


def expect_code(test: unittest.TestCase, code: str, action) -> None:
    with test.assertRaises(gate.GateError) as caught:
        action()
    test.assertEqual(code, caught.exception.code)


def valid_runner_plan() -> dict:
    return gate.load_json(FIXTURES / "runner-plan.valid.json")


class CanonicalBuildManifestTests(unittest.TestCase):
    def test_valid_manifest_rehashes_every_file(self) -> None:
        result = gate.validate_build_manifest(CANONICAL, CANONICAL / "canonical-build-manifest.v1.json", EXPECTED_COMMIT, EXPECTED_SOURCE)
        self.assertEqual("PASS-CANONICAL-BUILD-MANIFEST", result["status"])
        self.assertEqual(8, result["fileCount"])
        self.assertEqual("59AF14B6D5915799CC1581A2B6A065CB3DB8DAE100B5E3B2271C769F29C41432", result["treeSha256"])

    def test_tampered_payload_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory(prefix="quality-v3-build-drift-") as temporary:
            copied = Path(temporary) / "candidate"
            shutil.copytree(CANONICAL, copied)
            (copied / "desktop/KnowledgeClipper.exe").write_text("tampered\n", encoding="utf-8")
            expect_code(self, "build.file-inventory-drift", lambda: gate.validate_build_manifest(copied, copied / "canonical-build-manifest.v1.json", EXPECTED_COMMIT, EXPECTED_SOURCE))

    def test_manifest_source_identity_must_match_quality_observation(self) -> None:
        expect_code(
            self,
            "build.source-commit-mismatch",
            lambda: gate.validate_build_manifest(
                CANONICAL,
                CANONICAL / "canonical-build-manifest.v1.json",
                "1111111111111111111111111111111111111111",
                EXPECTED_SOURCE,
            ),
        )

    def test_manifest_source_snapshot_must_match_quality_observation(self) -> None:
        expect_code(
            self,
            "build.source-snapshot-mismatch",
            lambda: gate.validate_build_manifest(
                CANONICAL,
                CANONICAL / "canonical-build-manifest.v1.json",
                EXPECTED_COMMIT,
                "3333333333333333333333333333333333333333333333333333333333333333",
            ),
        )

    def test_missing_startup_self_verification_is_rejected(self) -> None:
        fixture = gate.load_json(COUNTEREXAMPLES / "candidate-no-self-verification.json")
        with tempfile.TemporaryDirectory(prefix="quality-v3-self-verify-") as temporary:
            copied = Path(temporary) / "candidate"
            shutil.copytree(CANONICAL, copied)
            manifest_path = copied / "canonical-build-manifest.v1.json"
            manifest = gate.load_json(manifest_path)
            manifest.update(fixture["mutation"])
            manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            expect_code(self, fixture["expectedRejectCode"], lambda: gate.validate_build_manifest(copied, manifest_path, EXPECTED_COMMIT, EXPECTED_SOURCE))


class KnownFailureRegressionTests(unittest.TestCase):
    def test_button_invoke_without_command_is_rejected(self) -> None:
        fixture = gate.load_json(COUNTEREXAMPLES / "button-invoke-no-command.json")
        expect_code(self, fixture["expectedRejectCode"], lambda: gate.validate_ui_action_evidence(fixture["subject"]))

    def test_four_file_artifact_rejects_legacy_single_file_consumer(self) -> None:
        manifest = COUNTEREXAMPLES / "four-file-single-file-consumer/artifact-manifest.json"
        fixture = gate.load_json(manifest)
        expect_code(self, fixture["expectedRejectCode"], lambda: gate.verify_artifact_manifest(manifest, manifest.parent))

    def test_rendered_writes_field_is_not_a_contract(self) -> None:
        fixture = gate.load_json(COUNTEREXAMPLES / "writes-field-rendered-text.json")
        expect_code(self, fixture["expectedRejectCode"], lambda: gate.validate_ui_contract(fixture["subject"]))

    def test_promotion_emitter_and_verifier_keys_must_match(self) -> None:
        fixture = gate.load_json(COUNTEREXAMPLES / "promotion-key-contradiction.json")
        plan = valid_runner_plan()
        plan["promotionContract"] = {
            "emittedEvidenceKeys": fixture["emittedEvidenceKeys"],
            "acceptedEvidenceKeys": fixture["acceptedEvidenceKeys"],
        }
        expect_code(self, fixture["expectedRejectCode"], lambda: gate.validate_runner_plan(plan))


class RunnerAndEffectBoundaryTests(unittest.TestCase):
    def test_private_runner_is_collect_all_and_run_owned(self) -> None:
        result = gate.validate_runner_plan(valid_runner_plan())
        self.assertEqual("PASS-PRIVATE-RUNNER-PLAN", result["status"])
        self.assertEqual(["anki-probe", "question-to-anki"], result["journeys"])

    def test_cross_journey_fail_fast_is_rejected(self) -> None:
        plan = valid_runner_plan()
        plan["aggregation"]["businessJourneyMode"] = "global-fail-fast"
        expect_code(self, "runner.cross-journey-fail-fast", lambda: gate.validate_runner_plan(plan))

    def test_shared_tools_snapshot_is_rejected(self) -> None:
        plan = valid_runner_plan()
        plan["runnerSnapshot"]["root"] = "tools/shared-runner"
        expect_code(self, "runner.snapshot-not-private", lambda: gate.validate_runner_plan(plan))

    def test_non_allowlisted_provider_call_is_rejected(self) -> None:
        evidence = gate.load_json(VERTICAL / "anki-probe/journey-evidence.json")["sideEffectAudits"][0]
        evidence = copy.deepcopy(evidence)
        evidence["observedCalls"].append({"action": "addNotes", "effect": "write"})
        evidence["observedCallCount"] += 1
        evidence["observedWriteCount"] = 1
        with tempfile.TemporaryDirectory(prefix="quality-v3-effects-") as temporary:
            root = Path(temporary)
            log = root / "calls.ndjson"
            log.write_text("{\"action\":\"version\",\"effect\":\"read\"}\n{\"action\":\"deckNames\",\"effect\":\"read\"}\n{\"action\":\"addNotes\",\"effect\":\"write\"}\n", encoding="utf-8")
            evidence["callLog"] = "calls.ndjson"
            evidence["callLogSha256"] = gate.sha256_file(log)
            expect_code(self, "effects.action-not-allowlisted", lambda: gate.validate_side_effect_audit(evidence, root))

    def test_worker_reported_calls_cannot_override_proxy_log(self) -> None:
        evidence = gate.load_json(VERTICAL / "anki-probe/journey-evidence.json")["sideEffectAudits"][0]
        evidence = copy.deepcopy(evidence)
        evidence["observedCalls"] = []
        evidence["observedCallCount"] = 0
        expect_code(self, "effects.call-log-content", lambda: gate.validate_side_effect_audit(evidence, VERTICAL / "anki-probe"))


class VerticalSlicePreparationTests(unittest.TestCase):
    def test_anki_probe_gate_accepts_typed_multifile_readonly_fixture(self) -> None:
        result = gate.validate_anki_probe_journey(VERTICAL / "anki-probe/journey-evidence.json")
        self.assertEqual("PASS-ANKI-PROBE-GATE", result["status"])
        self.assertEqual(4, result["artifactFiles"])
        self.assertEqual(0, result["ankiWrites"])

    def test_question_to_anki_gate_accepts_correlated_bounded_fixture(self) -> None:
        result = gate.validate_question_to_anki_journey(VERTICAL / "question-to-anki/journey-evidence.json")
        self.assertEqual("PASS-QUESTION-TO-ANKI-GATE", result["status"])
        self.assertEqual(1, result["deepseekCalls"])
        self.assertEqual(1, result["ankiWrites"])
        self.assertEqual(1, result["readBack"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
