import copy
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import verify_desktop_continuous_state_candidate as gate


def valid():
    fault = {"typedVisible": True, "spinnerStopped": True, "nextExtractAvailable": True, "nextReadAvailable": True, "applicationRestarted": False}
    return {
        "schema": "kc.quality.desktop-continuous-state-evidence/1",
        "identity": {"candidateRoot": "new", "sameCandidateBound": True, "superseded": False, "coreManifestSha256": "A" * 64, "desktopBoundCoreManifestSha256": "A" * 64, "contentDescriptorSha256": "B" * 64, "desktopBoundContentDescriptorSha256": "B" * 64},
        "automationContract": {"version": "1.4.0", "schemaCodeParity": True, "cardResolutionActions": ["accept-candidate", "submit-correction", "cancel"], "actionsBoundToExistingControls": True, "actualHarnessUsesHighestCompatibleCandidateContract": True, "legacyIdsRejected": True},
        "externalUiaSurface": {"windowSelection": "exact-pid-and-root-automation-id", "zeroWindowRejected": True, "multipleWindowsRejected": True, "provider": "production-winforms", "visibleAutomationIds": ["root", "context", "read", "extract", "card-resolution-accept", "card-resolution-correction", "card-resolution-cancel"], "evidenceSource": "external-uia-dump", "inProcessControlNameAccepted": False},
        "typedStateMachine": {"choiceSubmittedRemovesOldChoice": True, "unresolvedPipeline": "AwaitingCardResolution", "unresolvedAnkiWrites": 0, "staleChoiceCleared": True, "cardsOnlyPlaybackState": "NotRequested", "readyShowsPreview": True, "unresolvedShowsHumanDecision": True, "failedStopsSpinner": True, "cancelledStopsSpinner": True, "newExtractAvailableAfterEveryBranch": True, "newReadAvailableAfterEveryBranch": True},
        "continuousScenario": {"sameDesktopProcess": True, "applicationRestarted": False, "realExternalWindowsTextHost": True, "actualSelectionAndClipboardCapture": True, "readBothPreviewCards": 2, "readBothConfirmed": True, "readBothExactAnkiCards": 2, "extractSecondSelectionCompleted": True, "extractPreviewConfirmed": True, "readThirdActionCompleted": True, "resolutionAcceptCompleted": True, "resolutionCorrectionCompleted": True, "resolutionCancelCompleted": True, "contextStateSingleSource": True, "resizePreservesState": True, "prePreviewAnkiWrites": 0, "duplicateConfirmNewWrites": 0},
        "contextEvidence": {"floatingToggle": True, "activeProfile": True, "taskSnapshot": True, "floatingRevision": "r1", "profileRevision": "r1", "taskSnapshotRevision": "r1", "selectedTextExact": True, "contextSegmentsNonEmptyOrdered": True, "contentInputHasSelectedTextAndContext": True, "speechInputEqualsSelectedTextOnly": True, "contextOffControlPassed": True, "requestedUnavailableTypedVisibleFailure": True},
        "faultRecovery": {"contentUnresolved": copy.deepcopy(fault), "speechFailed": copy.deepcopy(fault), "corePipeFailed": copy.deepcopy(fault)},
        "productionRoute": ["KnowledgeClipper.exe", "real-button-uia", "clipboard-capture", "typed-gateway", "Core-process"],
        "bypass": {"directFinalTextInjection": False, "directCoreIpc": False, "receiptReplay": False},
        "automationSeam": {"usesProductionSingleInstance": True, "testOnlyMutexIsolation": False, "isolatesPipe": True, "isolatesDataRoot": True, "isolatesDestinationDeck": True, "destinationBoundByAuthoritySettingsSnapshot": True, "uiRequestCannotOverrideDestination": True, "injectsSelectedText": False, "injectsContext": False, "injectsFinalInput": False},
        "compilerGate": {"compiler": "locked-dotnet", "fullScriptExitCode": 0},
    }


class Tests(unittest.TestCase):
    def test_valid_passes(self): self.assertEqual("PASS-DESKTOP-CONTINUOUS-STATE-CANDIDATE", gate.evaluate(valid())["status"])

    def reject(self, section, key, value):
        data = valid(); data[section][key] = value
        with self.assertRaises(gate.GateFailure): gate.evaluate(data)

    def test_state_machine_failures_rejected(self):
        for key in ["choiceSubmittedRemovesOldChoice", "staleChoiceCleared", "readyShowsPreview", "unresolvedShowsHumanDecision", "failedStopsSpinner", "cancelledStopsSpinner", "newExtractAvailableAfterEveryBranch", "newReadAvailableAfterEveryBranch"]:
            with self.subTest(key=key): self.reject("typedStateMachine", key, False)

    def test_exact_unresolved_and_cards_only_states_required(self):
        for key, value in [("unresolvedPipeline", "partially_failed"), ("unresolvedAnkiWrites", 1), ("cardsOnlyPlaybackState", "Completed"), ("cardsOnlyPlaybackState", "idle_replayable")]:
            with self.subTest(key=key): self.reject("typedStateMachine", key, value)

    def test_restart_and_incomplete_sequence_rejected(self):
        for key, value in [("applicationRestarted", True), ("readBothPreviewCards", 1), ("readBothExactAnkiCards", 1), ("extractSecondSelectionCompleted", False), ("readThirdActionCompleted", False), ("prePreviewAnkiWrites", 1), ("duplicateConfirmNewWrites", 1)]:
            with self.subTest(key=key): self.reject("continuousScenario", key, value)

    def test_context_revision_and_payload_failures_rejected(self):
        for key in ["selectedTextExact", "contextSegmentsNonEmptyOrdered", "contentInputHasSelectedTextAndContext", "speechInputEqualsSelectedTextOnly", "contextOffControlPassed", "requestedUnavailableTypedVisibleFailure"]:
            with self.subTest(key=key): self.reject("contextEvidence", key, False)
        data = valid(); data["contextEvidence"]["taskSnapshotRevision"] = "drift"
        with self.assertRaises(gate.GateFailure): gate.evaluate(data)

    def test_each_fault_must_recover_without_restart(self):
        for branch in valid()["faultRecovery"]:
            data = valid(); data["faultRecovery"][branch]["nextReadAvailable"] = False
            with self.assertRaises(gate.GateFailure): gate.evaluate(data)
            data = valid(); data["faultRecovery"][branch]["applicationRestarted"] = True
            with self.assertRaises(gate.GateFailure): gate.evaluate(data)

    def test_binding_and_bypass_failures_rejected(self):
        self.reject("identity", "desktopBoundCoreManifestSha256", "C" * 64)
        self.reject("productionRoute", 0, "fake")
        for key in valid()["bypass"]:
            with self.subTest(key=key): self.reject("bypass", key, True)

    def test_automation_contract_v14_and_legacy_id_boundary(self):
        self.reject("automationContract", "version", "1.3.0")
        self.reject("automationContract", "schemaCodeParity", False)
        self.reject("automationContract", "cardResolutionActions", ["cancel"])
        self.reject("automationContract", "actualHarnessUsesHighestCompatibleCandidateContract", False)
        self.reject("automationContract", "legacyIdsRejected", False)

    def test_external_uia_surface_must_be_unique_and_real(self):
        self.reject("externalUiaSurface", "windowSelection", "title-only")
        self.reject("externalUiaSurface", "zeroWindowRejected", False)
        self.reject("externalUiaSurface", "multipleWindowsRejected", False)
        self.reject("externalUiaSurface", "visibleAutomationIds", ["root"])
        self.reject("externalUiaSurface", "evidenceSource", "in-process-control-name")
        self.reject("externalUiaSurface", "inProcessControlNameAccepted", True)

    def test_seam_resolution_resize_and_compiler_failures_rejected(self):
        self.reject("automationSeam", "usesProductionSingleInstance", False)
        self.reject("automationSeam", "testOnlyMutexIsolation", True)
        self.reject("automationSeam", "destinationBoundByAuthoritySettingsSnapshot", False)
        self.reject("automationSeam", "uiRequestCannotOverrideDestination", False)
        self.reject("automationSeam", "injectsSelectedText", True)
        self.reject("automationSeam", "injectsContext", True)
        self.reject("continuousScenario", "resolutionCorrectionCompleted", False)
        self.reject("continuousScenario", "contextStateSingleSource", False)
        self.reject("continuousScenario", "resizePreservesState", False)
        self.reject("compilerGate", "fullScriptExitCode", 1)


if __name__ == "__main__": unittest.main()
