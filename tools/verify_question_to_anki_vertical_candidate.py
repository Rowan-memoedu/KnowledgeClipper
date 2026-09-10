from __future__ import annotations

from typing import Any


class GateFailure(RuntimeError):
    pass


def require(value: bool, message: str) -> None:
    if not value:
        raise GateFailure(message)


def evaluate(data: dict[str, Any]) -> dict[str, Any]:
    require(data["schema"] == "kc.quality.question-to-anki-vertical-evidence/1", "quality evidence schema drift")
    identity = data["identity"]
    require(identity["sameCandidateBound"] is True, "Core and Desktop are not bound to one candidate")
    require(identity["superseded"] is False and identity["candidateRoot"] not in identity["rejectedCandidateRoots"], "superseded candidate supplied")
    require(identity["sealedManifestVerified"] and identity["sealedTreeVerified"] and identity["recursiveHashesVerified"], "sealed inventory not independently verified")

    core = data["core"]
    require(core["evidenceClass"] == "actual-sealed-orchestrator-process", "Core evidence is not an actual sealed process")
    require(core["newImmutableTask"] and core["initialCompleted"] and core["followUpEnterSubmittedOnce"], "initial/follow-up chain incomplete")
    require(core["followUpNewRevision"] and core["oldTokenRejectedBeforeExporter"], "revision/token boundary failed")
    require(core["confirmAcceptedOnce"] and core["preConfirmAnkiAttempts"] == 0, "confirm boundary failed")
    require(core["ankiWorkerVersion"] == "1.4.1" and core["confirmAnkiAttempts"] == 1, "Anki candidate binding/attempt drift")
    expected_card_count = len(core["previewCards"])
    require(core["exporterInvocationCount"] == 1, "Anki exporter must be invoked exactly once")
    require(core["logicalExternalWriteCount"] == expected_card_count, "Anki logical committed card count does not equal the confirmed immutable draft card count")
    require(core["lowLevelMutatingApiActionCount"] == expected_card_count, "create-only Anki mutating API action count does not equal the confirmed immutable draft card count")
    require(core["operationLevelSideEffectCount"] == 1, "Anki operation-level side effect count drifted")
    require(core["externalStateKnown"] is True and core["exportStatus"] == "completed", "partial or unknown Anki export cannot pass")
    require(core["previewCards"] == core["notesInfoCards"] == core["cardsInfoCards"], "Core preview/readback card drift")
    require(all({"front", "back", "deckName", "noteId", "cardId"} <= set(card) for card in core["notesInfoCards"]), "per-card Front/Back/deck/note/card readback is incomplete")
    require(core["confirmReplayAttemptDelta"] == 0 and core["confirmReplayNewNoteDelta"] == 0, "confirm replay was not idempotent")
    require(core["source"] == "raw-events-results-artifacts-readback", "Core evidence inferred from logs")

    desktop = data["desktop"]
    require(desktop["evidenceClass"] == "sealed-desktop-candidate", "Desktop evidence is not sealed candidate-bound")
    require(desktop["enterSubmitCount"] == 1 and desktop["spinnerTerminates"], "Desktop submit/spinner invariant failed")
    require(desktop["typedFailure"] and desktop["typedTimeout"], "Desktop typed terminal failures missing")
    require(desktop["sharedPreviewComponent"] and desktop["multiCardFrontBackLiteral"], "unified multi-card preview missing")
    require(desktop["preConfirmWrites"] == desktop["cancelWrites"] == 0, "Desktop wrote before explicit confirm/cancel")
    require(desktop["doubleClickAdditionalCommands"] == desktop["doubleClickAdditionalWrites"] == 0, "Desktop double-click duplicate")
    require(desktop["source"] == "candidate-ui-automation-and-typed-port", "Desktop evidence inferred from logs")

    runtime = data["runtimeAdmission"]
    require(runtime["knowledgeClipperEdgeTtsEnvironmentRemoved"] is True, "developer TTS environment variable was not removed")
    require(runtime["globalPathDependency"] is False and runtime["developerInstallDependency"] is False, "candidate depends on developer machine state")
    require(runtime["packagedToolInsideCandidate"] is True and runtime["packagedToolHashVerified"] is True, "sealed TTS runtime tool is missing or unverified")
    require(runtime["authorityBoundRuntimeDescriptor"] is True, "runtime tool is not resolved through an authority/candidate descriptor")
    require(runtime["productDirectEnvironmentRequirement"] is False, "product still directly requires KNOWLEDGE_CLIPPER_EDGE_TTS")
    require(runtime["testsInjectDeveloperAbsolutePath"] is False, "tests still inject a developer-machine TTS path")
    require(runtime["actualDesktopCandidateButtonProcess"] is True, "runtime admission was not crossed from the actual Desktop process")
    require(runtime["desktopActionRoute"] == ["button-handler", "capture", "ReadToAnkiUseCase", "typed-gateway"], "Desktop action did not traverse the production button/capture/use-case/gateway route")
    require(runtime["directCoreIpcBypass"] is False and runtime["receiptReplay"] is False, "Desktop evidence bypassed the production route or replayed a receipt")
    require(runtime["actualActionCandidateRoot"] == identity["candidateRoot"], "Desktop action evidence is bound to a different candidate root")
    require(runtime["actualActionCandidateManifestSha256"] == identity["candidateManifestSha256"], "Desktop action evidence is bound to a different candidate manifest")
    require(runtime["reusedPriorCandidateEvidence"] is False, "Desktop action evidence reused a prior candidate")
    require(runtime["synthesisAdmissionPassed"] and runtime["playbackAdmissionPassed"], "synthesis/playback runtime admission failed")
    require(runtime["oneCardPreConfirmWrites"] == 0 and runtime["oneCardPostConfirmCompleted"], "one-card candidate process chain incomplete")
    require(runtime["bothPreConfirmWrites"] == 0 and runtime["bothPostConfirmCompleted"] and runtime["bothCardCount"] == 2, "both candidate process chain incomplete")

    regression = data["regression"]
    require(regression["languageModes"] == {"ask": True, "meaning-reading": True, "writing": True, "both": True}, "language regression incomplete")
    require(regression["contentFailureSpeechIndependent"] and regression["contentFailureTypedTerminal"] and regression["contentFailureNoPreviewExport"], "content failure regression incomplete")
    combinations = regression["componentOutcomeMatrix"]
    require(combinations["contentReadySpeechFailed"] == {"cardDraftReady": True, "previewVisible": True, "confirmExportAvailable": True, "playbackState": "failed", "typedPlaybackNotice": True}, "speech failure incorrectly suppressed a ready Content draft")
    require(combinations["contentFailedSpeechCompleted"] == {"cardDraftReady": False, "previewVisible": False, "confirmExportAvailable": False, "playbackState": "completed", "typedContentNotice": True}, "content failure incorrectly allowed preview/export")

    boundaries = data["boundaries"]
    require(boundaries["moduleSelfReportAccepted"] is False and boundaries["fakeOnlyAccepted"] is False and boundaries["logParsingAccepted"] is False and boundaries["sourceSchemaAccepted"] is False, "prohibited evidence class accepted")
    require(boundaries["remNoteRequired"] is False and boundaries["ocrRequired"] is False, "out-of-scope destination added")
    return {"status": "PASS-QUESTION-TO-ANKI-USER-HANDTEST", "candidateRoot": identity["candidateRoot"], "cardCount": len(core["previewCards"])}
