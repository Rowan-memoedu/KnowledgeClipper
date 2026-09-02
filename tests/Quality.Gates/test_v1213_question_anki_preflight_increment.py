from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tools"))

import verify_v1213_question_anki_preflight_increment as gate  # noqa: E402


class V1213QuestionAnkiPreflightIncrementTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.fixture = gate.common.load_json(gate.DEFAULT_FIXTURE)
        cls.result = gate.evaluate(copy.deepcopy(cls.fixture))

    def test_fixture_rejects_service_failure_retry_and_rejected_promotion(self) -> None:
        fixture_result = gate.validate_fixtures()
        self.assertEqual("PASS", fixture_result["status"])
        self.assertEqual(1, fixture_result["validAccepted"])
        self.assertEqual(1, fixture_result["invalidRejected"])

    def test_authority_v1213_is_exact_and_frozen_v1_has_zero_drift(self) -> None:
        authority = self.result["authority"]
        self.assertEqual("PASS-AUTHORITY-V1.2.13-EXACT", authority["status"])
        self.assertEqual("0A82124F5408D5239414C4F995AFCC81AEC7DC29C04723F8D8BA7BC3FDE507FB", authority["release"]["treeSha256"])
        self.assertEqual(0, authority["frozenV1"]["mismatches"])

    def test_all_three_question_content_stages_completed(self) -> None:
        stages = self.result["sealedQuestionAnki"]["questionStages"]
        self.assertEqual({"question-answer", "question-follow-up", "question-make-cards"}, {item["stageId"] for item in stages})
        self.assertTrue(all(item["status"] == "completed" for item in stages))

    def test_anki_failure_is_missing_v1_preflight_not_service_failure(self) -> None:
        observed = self.result["sealedQuestionAnki"]
        self.assertEqual("FAILED-PREFLIGHT-MISSING-V1-MANIFEST-BEFORE-ANKI-CLIENT", observed["status"])
        self.assertTrue(observed["classification"]["notAnkiServiceFailure"])
        self.assertEqual("FILE_MISSING", observed["ankiAttempt"]["stderrCode"])
        self.assertFalse(observed["job"]["manifestV1"]["exists"])

    def test_unknown_external_state_is_conservative_but_actual_calls_are_zero(self) -> None:
        observed = self.result["sealedQuestionAnki"]
        self.assertEqual("unknown_external_state", observed["ankiAttempt"]["resultStatus"])
        self.assertEqual("no-external-call", observed["classification"]["actualExternalStateKnownFromPreflightOrdering"])
        self.assertEqual(0, observed["classification"]["externalCalls"])
        self.assertEqual(0, observed["classification"]["externalWrites"])
        self.assertEqual([], observed["ankiAttempt"]["effectFiles"])

    def test_old_attempt_is_not_retryable_and_rerun_requires_new_task(self) -> None:
        classification = self.result["sealedQuestionAnki"]["classification"]
        self.assertFalse(classification["oldAttemptRetryAllowed"])
        self.assertEqual("new-immutable-task-only", classification["rerunPolicy"])
        self.assertFalse(self.result["sealedQuestionAnki"]["ankiAttempt"]["retryable"])

    def test_content_080_canar_is_unresolved_and_waiting_input(self) -> None:
        content = self.result["content"]["content080"]
        self.assertEqual("PASS-MODULE-ADMISSION-AUTHORITY-PENDING", content["status"])
        self.assertEqual(content["liveCanar"]["before"], content["liveCanar"]["after"])
        self.assertEqual("unresolved", content["liveCanar"]["moduleReleaseState"])
        self.assertEqual("waiting_input", content["liveCanar"]["requiredCoreProjection"])
        self.assertFalse(content["liveCanar"]["cardBundlePublished"])

    def test_content_081_is_rejected_never_promoted(self) -> None:
        content = self.result["content"]["content081"]
        self.assertEqual("REJECTED-NEVER-PROMOTED", content["status"])
        self.assertFalse(content["authorityRegistryAllowed"])

    def test_quality_run_has_no_external_side_effects(self) -> None:
        execution = self.result["qualityExecution"]
        self.assertEqual(0, execution["externalServicesInvoked"])
        self.assertEqual(0, execution["externalWrites"])
        self.assertFalse(execution["oldAttemptRetried"])
        self.assertFalse(execution["frozenContractsModified"])
        self.assertFalse(execution["authorityModified"])


if __name__ == "__main__":
    unittest.main()
