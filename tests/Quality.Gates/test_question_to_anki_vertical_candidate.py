import copy
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import verify_question_to_anki_vertical_candidate as gate


def valid():
    cards = [
        {"front": "F1", "back": "B1", "deckName": "isolated", "noteId": 1, "cardId": 11},
        {"front": "F2", "back": "B2", "deckName": "isolated", "noteId": 2, "cardId": 22},
    ]
    return {
        "schema": "kc.quality.question-to-anki-vertical-evidence/1",
        "identity": {"candidateRoot": "future", "candidateManifestSha256": "A" * 64, "sameCandidateBound": True, "superseded": False, "rejectedCandidateRoots": ["old"], "sealedManifestVerified": True, "sealedTreeVerified": True, "recursiveHashesVerified": True},
        "core": {"evidenceClass": "actual-sealed-orchestrator-process", "newImmutableTask": True, "initialCompleted": True, "followUpEnterSubmittedOnce": True, "followUpNewRevision": True, "oldTokenRejectedBeforeExporter": True, "confirmAcceptedOnce": True, "preConfirmAnkiAttempts": 0, "ankiWorkerVersion": "1.4.1", "confirmAnkiAttempts": 1, "exporterInvocationCount": 1, "logicalExternalWriteCount": 2, "lowLevelMutatingApiActionCount": 2, "operationLevelSideEffectCount": 1, "externalStateKnown": True, "exportStatus": "completed", "previewCards": cards, "notesInfoCards": copy.deepcopy(cards), "cardsInfoCards": copy.deepcopy(cards), "confirmReplayAttemptDelta": 0, "confirmReplayNewNoteDelta": 0, "source": "raw-events-results-artifacts-readback"},
        "desktop": {"evidenceClass": "sealed-desktop-candidate", "enterSubmitCount": 1, "spinnerTerminates": True, "typedFailure": True, "typedTimeout": True, "sharedPreviewComponent": True, "multiCardFrontBackLiteral": True, "preConfirmWrites": 0, "cancelWrites": 0, "doubleClickAdditionalCommands": 0, "doubleClickAdditionalWrites": 0, "source": "candidate-ui-automation-and-typed-port"},
        "runtimeAdmission": {"knowledgeClipperEdgeTtsEnvironmentRemoved": True, "globalPathDependency": False, "developerInstallDependency": False, "packagedToolInsideCandidate": True, "packagedToolHashVerified": True, "authorityBoundRuntimeDescriptor": True, "productDirectEnvironmentRequirement": False, "testsInjectDeveloperAbsolutePath": False, "actualDesktopCandidateButtonProcess": True, "desktopActionRoute": ["button-handler", "capture", "ReadToAnkiUseCase", "typed-gateway"], "directCoreIpcBypass": False, "receiptReplay": False, "actualActionCandidateRoot": "future", "actualActionCandidateManifestSha256": "A" * 64, "reusedPriorCandidateEvidence": False, "synthesisAdmissionPassed": True, "playbackAdmissionPassed": True, "oneCardPreConfirmWrites": 0, "oneCardPostConfirmCompleted": True, "bothPreConfirmWrites": 0, "bothPostConfirmCompleted": True, "bothCardCount": 2},
        "regression": {"languageModes": {"ask": True, "meaning-reading": True, "writing": True, "both": True}, "contentFailureSpeechIndependent": True, "contentFailureTypedTerminal": True, "contentFailureNoPreviewExport": True, "componentOutcomeMatrix": {"contentReadySpeechFailed": {"cardDraftReady": True, "previewVisible": True, "confirmExportAvailable": True, "playbackState": "failed", "typedPlaybackNotice": True}, "contentFailedSpeechCompleted": {"cardDraftReady": False, "previewVisible": False, "confirmExportAvailable": False, "playbackState": "completed", "typedContentNotice": True}}},
        "boundaries": {"moduleSelfReportAccepted": False, "fakeOnlyAccepted": False, "logParsingAccepted": False, "sourceSchemaAccepted": False, "remNoteRequired": False, "ocrRequired": False},
    }


class Tests(unittest.TestCase):
    def reject(self, section, key, value):
        data = valid(); data[section][key] = value
        with self.assertRaises(gate.GateFailure): gate.evaluate(data)

    def test_complete_candidate_passes(self):
        self.assertEqual("PASS-QUESTION-TO-ANKI-USER-HANDTEST", gate.evaluate(valid())["status"])

    def test_fake_core_rejected(self): self.reject("core", "evidenceClass", "fake-only")
    def test_old_token_rejected(self): self.reject("core", "oldTokenRejectedBeforeExporter", False)
    def test_replay_write_rejected(self): self.reject("core", "confirmReplayNewNoteDelta", 1)
    def test_desktop_double_click_rejected(self): self.reject("desktop", "doubleClickAdditionalCommands", 1)

    def test_readback_drift_or_incomplete_identity_rejected(self):
        data = valid(); data["core"]["cardsInfoCards"][0]["back"] = "drift"
        with self.assertRaises(gate.GateFailure): gate.evaluate(data)
        data = valid(); del data["core"]["notesInfoCards"][0]["noteId"]
        with self.assertRaises(gate.GateFailure): gate.evaluate(data)

    def test_export_invocation_write_count_partial_and_unknown_rejected(self):
        for key, value in [("exporterInvocationCount", 2), ("logicalExternalWriteCount", 1), ("logicalExternalWriteCount", 3), ("lowLevelMutatingApiActionCount", 1), ("lowLevelMutatingApiActionCount", 3), ("operationLevelSideEffectCount", 2), ("externalStateKnown", False), ("exportStatus", "partial")]:
            with self.subTest(key=key, value=value): self.reject("core", key, value)

    def test_language_regression_missing_rejected(self):
        data = valid(); data["regression"]["languageModes"]["both"] = False
        with self.assertRaises(gate.GateFailure): gate.evaluate(data)

    def test_component_outcome_independence_matrix_rejected_on_coupling(self):
        data = valid(); data["regression"]["componentOutcomeMatrix"]["contentReadySpeechFailed"]["previewVisible"] = False
        with self.assertRaises(gate.GateFailure): gate.evaluate(data)
        data = valid(); data["regression"]["componentOutcomeMatrix"]["contentFailedSpeechCompleted"]["confirmExportAvailable"] = True
        with self.assertRaises(gate.GateFailure): gate.evaluate(data)

    def test_developer_environment_or_missing_packaged_tool_rejected(self):
        self.reject("runtimeAdmission", "knowledgeClipperEdgeTtsEnvironmentRemoved", False)
        self.reject("runtimeAdmission", "packagedToolInsideCandidate", False)
        self.reject("runtimeAdmission", "productDirectEnvironmentRequirement", True)
        self.reject("runtimeAdmission", "testsInjectDeveloperAbsolutePath", True)

    def test_actual_desktop_chain_required(self): self.reject("runtimeAdmission", "actualDesktopCandidateButtonProcess", False)

    def test_desktop_bypass_replay_and_prior_candidate_rejected(self):
        for key, value in [("directCoreIpcBypass", True), ("receiptReplay", True), ("desktopActionRoute", ["direct-core-ipc"]), ("actualActionCandidateRoot", "old"), ("actualActionCandidateManifestSha256", "B" * 64), ("reusedPriorCandidateEvidence", True)]:
            with self.subTest(key=key): self.reject("runtimeAdmission", key, value)

    def test_both_card_count_rejected(self): self.reject("runtimeAdmission", "bothCardCount", 1)


if __name__ == "__main__": unittest.main()
