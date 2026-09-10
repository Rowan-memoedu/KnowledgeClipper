from __future__ import annotations

from typing import Any


class GateFailure(RuntimeError):
    pass


def require(value: bool, message: str) -> None:
    if not value:
        raise GateFailure(message)


def evaluate(data: dict[str, Any]) -> dict[str, Any]:
    require(data["schema"] == "kc.quality.desktop-continuous-state-evidence/1", "evidence schema drift")
    identity = data["identity"]
    require(identity["sameCandidateBound"] and not identity["superseded"], "candidate identity invalid")
    require(identity["coreManifestSha256"] == identity["desktopBoundCoreManifestSha256"], "Desktop/Core candidate binding drift")
    require(identity["contentDescriptorSha256"] == identity["desktopBoundContentDescriptorSha256"], "Desktop/Content candidate binding drift")
    contract = data["automationContract"]
    require(contract["version"] == "1.4.0", "desktop automation contract is not v1.4")
    require(contract["schemaCodeParity"], "desktop automation schema/code parity failed")
    require(contract["cardResolutionActions"] == ["accept-candidate", "submit-correction", "cancel"], "card-resolution action catalog drift")
    require(contract["actionsBoundToExistingControls"], "card-resolution actions are not bound to existing controls")
    require(contract["actualHarnessUsesHighestCompatibleCandidateContract"], "actual harness did not resolve IDs from the candidate highest compatible contract")
    require(contract["legacyIdsRejected"], "legacy automation IDs were accepted")
    surface = data["externalUiaSurface"]
    require(surface["windowSelection"] == "exact-pid-and-root-automation-id", "UIA window selection is not PID/root-ID exact")
    require(surface["zeroWindowRejected"] and surface["multipleWindowsRejected"], "ambiguous UIA window selection was not rejected")
    require(surface["provider"] == "production-winforms", "non-production accessibility provider used")
    require(set(surface["visibleAutomationIds"]) == {"root", "context", "read", "extract", "card-resolution-accept", "card-resolution-correction", "card-resolution-cancel"}, "external UIA surface is incomplete")
    require(surface["evidenceSource"] == "external-uia-dump" and not surface["inProcessControlNameAccepted"], "in-process Control.Name was used as external UIA evidence")

    states = data["typedStateMachine"]
    require(states["choiceSubmittedRemovesOldChoice"], "old language choice remained visible")
    require(states["unresolvedPipeline"] == "AwaitingCardResolution", "unresolved did not project the typed AwaitingCardResolution state")
    require(states["unresolvedAnkiWrites"] == 0, "unresolved branch reached Anki")
    require(states["staleChoiceCleared"], "unresolved transition retained a stale language choice")
    require(states["cardsOnlyPlaybackState"] == "NotRequested", "cards-only path requested playback")
    require(states["readyShowsPreview"] and states["unresolvedShowsHumanDecision"], "ready/unresolved projection drift")
    require(states["failedStopsSpinner"] and states["cancelledStopsSpinner"], "terminal state did not stop spinner")
    require(states["newExtractAvailableAfterEveryBranch"] and states["newReadAvailableAfterEveryBranch"], "terminal/attention branch globally wedged subsequent actions")

    scenario = data["continuousScenario"]
    require(scenario["sameDesktopProcess"] and not scenario["applicationRestarted"], "continuous scenario used an application restart")
    require(scenario["realExternalWindowsTextHost"] and scenario["actualSelectionAndClipboardCapture"], "scenario bypassed real Windows capture")
    require(scenario["readBothPreviewCards"] == 2 and scenario["readBothConfirmed"] and scenario["readBothExactAnkiCards"] == 2, "Read+both chain incomplete")
    require(scenario["extractSecondSelectionCompleted"] and scenario["extractPreviewConfirmed"], "subsequent Extract chain incomplete")
    require(scenario["readThirdActionCompleted"], "subsequent Read chain incomplete")
    require(scenario["resolutionAcceptCompleted"] and scenario["resolutionCorrectionCompleted"] and scenario["resolutionCancelCompleted"], "CardResolution action matrix incomplete")
    require(scenario["contextStateSingleSource"] and scenario["resizePreservesState"], "context state or resize continuity drift")
    require(scenario["prePreviewAnkiWrites"] == 0 and scenario["duplicateConfirmNewWrites"] == 0, "write boundary failed")

    context = data["contextEvidence"]
    require(context["floatingToggle"] and context["activeProfile"] and context["taskSnapshot"], "Context setting evidence incomplete")
    require(context["floatingRevision"] == context["profileRevision"] == context["taskSnapshotRevision"], "Context revision chain drift")
    require(context["selectedTextExact"] and context["contextSegmentsNonEmptyOrdered"], "capture selectedText/contextSegments invalid")
    require(context["contentInputHasSelectedTextAndContext"] and context["speechInputEqualsSelectedTextOnly"], "Content/Speech context routing drift")
    require(context["contextOffControlPassed"] and context["requestedUnavailableTypedVisibleFailure"], "Context off/unavailable controls missing")

    faults = data["faultRecovery"]
    expected = {"contentUnresolved", "speechFailed", "corePipeFailed"}
    require(set(faults) == expected, "fault matrix incomplete")
    for name, item in faults.items():
        require(item["typedVisible"] and item["spinnerStopped"] and item["nextExtractAvailable"] and item["nextReadAvailable"] and not item["applicationRestarted"], f"fault recovery failed: {name}")

    route = data["productionRoute"]
    require(route == ["KnowledgeClipper.exe", "real-button-uia", "clipboard-capture", "typed-gateway", "Core-process"], "production Desktop route was bypassed")
    require(not data["bypass"]["directFinalTextInjection"] and not data["bypass"]["directCoreIpc"] and not data["bypass"]["receiptReplay"], "prohibited bypass evidence used")
    seam = data["automationSeam"]
    require(seam == {"usesProductionSingleInstance": True, "testOnlyMutexIsolation": False, "isolatesPipe": True, "isolatesDataRoot": True, "isolatesDestinationDeck": True, "destinationBoundByAuthoritySettingsSnapshot": True, "uiRequestCannotOverrideDestination": True, "injectsSelectedText": False, "injectsContext": False, "injectsFinalInput": False}, "automation seam exceeded isolation boundary, lacks authority-bound destination, or bypassed the production single-instance entry")
    require(data["compilerGate"]["compiler"] == "locked-dotnet" and data["compilerGate"]["fullScriptExitCode"] == 0, "full Windows script did not pass with locked dotnet compiler")
    return {"status": "PASS-DESKTOP-CONTINUOUS-STATE-CANDIDATE", "candidateRoot": identity["candidateRoot"]}
