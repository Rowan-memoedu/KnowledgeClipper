from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tools"))

import verify_additive_offline_backlog_gate as gate  # noqa: E402


class AdditiveOfflineBacklogGateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.fixture = gate.load_json(gate.DEFAULT_FIXTURE)
        cls.result = gate.evaluate(copy.deepcopy(cls.fixture))

    def test_fixture_schema_accepts_valid_and_rejects_pending_as_failed(self) -> None:
        result = gate.validate_fixture_set()
        self.assertEqual("PASS", result["status"])
        self.assertEqual(1, result["validAccepted"])
        self.assertEqual(1, result["invalidRejected"])

    def test_overall_is_offline_conditional_and_not_candidate_bound(self) -> None:
        self.assertEqual("CONDITIONAL-OFFLINE-GATE-READY-OWNER-EVIDENCE-PENDING", self.result["status"])
        self.assertEqual("PENDING-OWNER-RECEIPTS", self.result["candidateBinding"]["status"])
        self.assertIsNone(self.result["candidateBinding"]["candidateHash"])
        self.assertEqual({"B": "PENDING-UNCHANGED", "C": "PENDING-UNCHANGED"}, self.result["existingPathState"])

    def test_baseline_is_read_only_and_still_not_release_go(self) -> None:
        self.assertEqual("PASS-READ-ONLY-BASELINE", self.result["baseline"]["status"])
        self.assertFalse(self.result["baseline"]["releaseGo"])

    def test_authoritative_requirement_audit_keeps_incomplete_routes_open(self) -> None:
        result = self.result["requirementAudit"]
        self.assertEqual("PASS-READ-ONLY-AUTHORITATIVE-INCOMPLETE-BOUNDARIES", result["status"])
        self.assertFalse(result["narrowTestsMayPromote"])
        self.assertEqual(6, len(result["mustRemainIncomplete"]))

    def test_original_desired_trace_has_zero_ocr_and_current_core_is_unreceipted(self) -> None:
        result = self.result["originalMode"]
        self.assertEqual("PASS-DESIRED-ROUTE-SEMANTICS", result["offlineFixture"])
        self.assertEqual("IMPLEMENTED-PENDING-OWNER-RECEIPT", result["currentOwnerState"])
        self.assertEqual(1, result["imageOriginalPassthroughWorkerStarts"])
        self.assertEqual(0, result["ocrProviderCalls"])
        self.assertEqual(0, result["mineruProviderCalls"])

    def test_original_resolver_entry_rejection_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["originalMode"]["resolver"]["status"] = "rejected"
        with self.assertRaisesRegex(gate.GateFailure, "rejects at resolver entry"):
            gate.evaluate(fixture)

    def test_original_unsupported_terminal_cannot_be_succeeded(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["originalMode"]["terminal"] = {"status": "succeeded", "code": "none", "succeeded": True}
        with self.assertRaisesRegex(gate.GateFailure, "reported succeeded"):
            gate.evaluate(fixture)

    def test_image_partial_reuses_success_hash_and_retries_only_failure(self) -> None:
        result = self.result["imagePartialRetry"]
        self.assertEqual("PASS-OFFLINE-FAILED-ITEMS-ONLY", result["status"])
        self.assertEqual(["answer-1"], result["retriedItemIds"])
        self.assertEqual("A" * 64, result["reusedItemHashes"]["question-1"])
        self.assertTrue(result["partialBlocksDownstream"])

    def test_image_retry_of_successful_item_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["imagePartialRetry"]["retryAttempt"]["providerItemIds"].append("question-1")
        with self.assertRaisesRegex(gate.GateFailure, "failed-items-only"):
            gate.evaluate(fixture)

    def test_unknown_external_state_retry_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["imagePartialRetry"]["unknownExternalState"]["retryAttempts"] = 1
        with self.assertRaisesRegex(gate.GateFailure, "made retryable"):
            gate.evaluate(fixture)

    def test_image_122_candidate_is_module_pass_but_not_authority_or_continuation_pass(self) -> None:
        result = self.result["imageSettings"]
        self.assertEqual("PASS-IMAGE.OCR.MINERU-1.2.2-OFFLINE-CORE-ADMISSION-EVIDENCE", result["moduleStatus"])
        self.assertEqual("PENDING-NEW-AUTHORITY-CORE-CONTINUATION-DESKTOP-CANDIDATE", result["integrationStatus"])
        self.assertEqual("F0F8FA5D5BC3210D8CA01CEC8707B29891A8ECFFBE6B533982990BE15FC03B89", result["release"]["treeSha256"])
        self.assertEqual("PASS", result["providerSettings"]["formalWorkerRequestExactConsumption"])
        self.assertEqual({"original": "unsupported", "partialContinuation": "conditional", "retention": "unsupported"}, result["boundaries"])
        self.assertEqual(0, result["secretScan"]["findingCount"])

    def test_image_authority_or_retention_promotion_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["imageSettings"]["expected"]["workerVersion"] = "1.2.1"
        with self.assertRaisesRegex(gate.GateFailure, "identity drifted"):
            gate.evaluate(fixture)
        fixture = copy.deepcopy(self.fixture)
        fixture["imageSettings"]["expected"]["providerSettingsKeys"].remove("pollIntervalSeconds")
        with self.assertRaisesRegex(gate.GateFailure, "providerSettings descriptor coverage"):
            gate.evaluate(fixture)

    def test_settings_chain_is_complete_and_adapter_honest(self) -> None:
        result = self.result["settingsHonesty"]
        self.assertEqual("PASS-EXHAUSTIVE-CHAIN-ALGORITHM", result["offlineFixture"])
        self.assertEqual(0, result["plaintextSensitiveValueCount"])
        self.assertEqual("PENDING-CORE-AND-MODULE-RECEIPTS", result["ownerCoverage"])

    def test_editable_field_without_adapter_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["settingsHonesty"]["editable"][0]["adapterAvailable"] = False
        with self.assertRaisesRegex(gate.GateFailure, "lacks adapter"):
            gate.evaluate(fixture)

    def test_settings_value_chain_drift_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["settingsHonesty"]["editable"][0]["valueChain"]["workerRequest"] = "zh"
        with self.assertRaisesRegex(gate.GateFailure, "value drift"):
            gate.evaluate(fixture)

    def test_adapterless_field_must_be_read_only_conditional(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["settingsHonesty"]["conditional"][0]["editable"] = True
        with self.assertRaisesRegex(gate.GateFailure, "fake editable"):
            gate.evaluate(fixture)

    def test_speech_companion_closes_module_chain_but_not_core_desktop_integration(self) -> None:
        result = self.result["speechSettings"]
        self.assertEqual("PASS-SPEECH.EDGE-1.2.4-FORMAL-MODULE-RELEASE", result["moduleStatus"])
        self.assertEqual("PENDING-CORE-AUTHORITY-ADMISSION", result["integrationStatus"])
        self.assertEqual({"strategy": "override", "override": "en-US"}, result["chain"]["synthesisPayloadLanguage"])
        self.assertEqual("IMMUTABLE", result["chain"]["existingTasks"])
        self.assertEqual("EXPLICIT-ONLY-CONDITIONAL-NOT-RUN", result["speakerPreview"])
        self.assertEqual((7, 49, 5, 268), (result["formalRelease"]["files"], result["formalRelease"]["unitTests"], result["formalRelease"]["liveLanguages"], result["formalRelease"]["minimumAudioPathLength"]))
        self.assertEqual({"networkTtsCalls": 5, "physicalSpeakerCalls": 0, "externalProductionWrites": 0}, result["moduleHistoricalSideEffects"])
        self.assertEqual({"externalCalls": 0, "workerStarts": 0, "physicalSpeakerCalls": 0}, result["qualityInvocationSideEffects"])

    def test_speech_formal_tree_or_unit_count_drift_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["speechSettings"]["expected"]["formalTreeSha256"] = "0" * 64
        with self.assertRaisesRegex(gate.GateFailure, "formal release tree"):
            gate.evaluate(fixture)
        fixture = copy.deepcopy(self.fixture)
        fixture["speechSettings"]["expected"]["unitTests"] = 48
        with self.assertRaisesRegex(gate.GateFailure, "unit-test evidence"):
            gate.evaluate(fixture)

    def test_readable_index_distinguishes_historical_edge_calls_from_quality_invocation(self) -> None:
        index = gate.render_index(self.result)
        self.assertIn("Quality current invocation external/network TTS / speaker calls = `0/0`", index)
        self.assertIn("module historical Edge synth calls = `5`", index)

    def test_speech_language_override_or_preview_policy_drift_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["speechSettings"]["expected"]["languageOverrideCanonical"] = "zh-CN"
        with self.assertRaisesRegex(gate.GateFailure, "language strategy/override"):
            gate.evaluate(fixture)
        fixture = copy.deepcopy(self.fixture)
        fixture["speechSettings"]["expected"]["speakerPreviewInvocation"] = "automatic"
        with self.assertRaisesRegex(gate.GateFailure, "explicit-only/conditional"):
            gate.evaluate(fixture)

    def test_anki_module_release_and_probe_are_read_only_not_integrated(self) -> None:
        result = self.result["ankiSettings"]
        self.assertEqual("PASS-EXPORT.ANKI-1.2.1-RELEASE-AND-OFFLINE-PROBE", result["moduleStatus"])
        self.assertEqual("NO-GO-AUTHORITY-V1.2.11-BINDS-1.2.0-PENDING-APPEND-ONLY-1.2.1", result["integrationStatus"])
        self.assertEqual("E7EBA348C344865B0F403D34187831BABDEADC4818451084ACB86CC37A3E161D", result["release"]["treeSha256"])
        self.assertEqual((0, [], 0), (result["probe"]["realAnkiConnectCalls"], result["probe"]["mutatingActions"], result["probe"]["writes"]))
        self.assertTrue(result["probe"]["mayPromptUser"])
        self.assertFalse(result["probe"]["permissionPromptIsMutation"])
        self.assertEqual(("1.2.0", "1.2.1", False), (result["compatibility"]["authorityWorkerVersion"], result["compatibility"]["candidateWorkerVersion"], result["compatibility"]["authorityBindsCandidate"]))
        self.assertEqual("NO-GO-SUPPLEMENTAL-FAILED", result["compatibility"]["desktopProbeDispatch"])

    def test_anki_authority_candidate_version_drift_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["ankiSettings"]["expected"]["authorityWorkerVersion"] = "1.2.1"
        with self.assertRaisesRegex(gate.GateFailure, "version boundary drifted"):
            gate.evaluate(fixture)

    def test_anki_manifest_path_or_probe_policy_drift_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["ankiSettings"]["expected"]["manifestRelativePath"] = "release-manifest.v1.json"
        with self.assertRaisesRegex(gate.GateFailure, "worker-owned path"):
            gate.evaluate(fixture)
        fixture = copy.deepcopy(self.fixture)
        fixture["ankiSettings"]["expected"]["probeReadOnly"] = False
        with self.assertRaisesRegex(gate.GateFailure, "read-only loopback"):
            gate.evaluate(fixture)

    def test_language_three_strategies_have_readable_raw_to_card_mapping(self) -> None:
        result = self.result["languageCards"]
        self.assertEqual("PASS-OFFLINE-READABLE-COMPARISON", result["status"])
        self.assertEqual(["language_context"], [card["template"] for card in result["meaningReading"]["cards"]])
        self.assertEqual(["language_writing"], [card["template"] for card in result["writing"]["cards"]])
        self.assertEqual(["language_context", "language_writing"], [card["template"] for card in result["both"]["cards"]])

    def test_content_070_module_compatibility_is_pass_but_gui_and_core_remain_conditional(self) -> None:
        result = self.result["contentSettings"]
        self.assertEqual("PASS-CONTENT.DEEPSEEK-0.7.0-OFFLINE-COMPATIBILITY", result["moduleStatus"])
        self.assertEqual("CONDITIONAL-DESKTOP-RUNTIMEPOLICY-1.0-AND-CORE-SPEECH-V3-ASSERTION", result["integrationStatus"])
        self.assertEqual({"content": 74, "schemas": 21, "bindings": 7, "desktopSuite": "PASS"}, result["tests"])
        self.assertEqual("content-config-snapshot/1.0.0", result["desktopPublishedSnapshotSchema"])
        self.assertEqual("CONDITIONAL-NOT-CONNECTED", result["desktopGuiRuntimePolicyAuthoring"])
        self.assertEqual("SPEECH-V3-WORKERREQUEST-ASSERTION-NOT-CONTENT", result["externalCoreBlocker"])
        self.assertEqual(0, result["sideEffects"]["liveDeepSeekCalls"])

    def test_content_scenario_and_desktop_version_drift_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["contentSettings"]["expected"]["scenarioIds"].remove("details-unreliable")
        with self.assertRaisesRegex(gate.GateFailure, "scenario inventory"):
            gate.evaluate(fixture)
        fixture = copy.deepcopy(self.fixture)
        fixture["contentSettings"]["expected"]["desktopPublishedSnapshotSchema"] = "content-config-snapshot/1.1.0"
        with self.assertRaisesRegex(gate.GateFailure, "published schema conditional boundary"):
            gate.evaluate(fixture)

    def test_remnote_210_is_offline_module_pass_and_unknown_state_manual_only(self) -> None:
        result = self.result["remnoteSettings"]
        self.assertEqual("PASS-EXPORT.REMNOTE-2.1.0-OFFLINE-RELEASE", result["moduleStatus"])
        self.assertEqual("PENDING-CORE-AUTHORITY-AND-CANDIDATE-BINDING", result["integrationStatus"])
        self.assertEqual((19, 169192, 9), (result["release"]["payloadFiles"], result["release"]["payloadBytes"], result["release"]["schemasValidated"]))
        self.assertEqual({"automaticRetryAllowed": False, "retryPolicy": "manual-only"}, result["unknownExternalState"])
        self.assertEqual({"realRemNoteCalls": 0, "realMcpCalls": 0, "externalWrites": 0}, result["sideEffects"])

    def test_remnote_retry_or_tree_promotion_drift_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["remnoteSettings"]["expected"]["automaticRetryAllowed"] = True
        with self.assertRaisesRegex(gate.GateFailure, "automatically retryable"):
            gate.evaluate(fixture)
        fixture = copy.deepcopy(self.fixture)
        fixture["remnoteSettings"]["expected"]["payloadTreeSha256"] = "0" * 64
        with self.assertRaisesRegex(gate.GateFailure, "payload tree"):
            gate.evaluate(fixture)

    def test_language_writing_exact_replacement_drift_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["languageCards"]["writing"]["cards"][0]["front"] += " tentative"
        with self.assertRaisesRegex(gate.GateFailure, "exact unique replacement"):
            gate.evaluate(fixture)

    def test_language_both_order_drift_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["languageCards"]["both"]["cards"].reverse()
        with self.assertRaisesRegex(gate.GateFailure, "original card then writing"):
            gate.evaluate(fixture)

    def test_nonlanguage_and_ambiguous_negatives_emit_no_cards(self) -> None:
        negative = self.result["languageCards"]["negative"]
        self.assertEqual([], negative["nonLanguage"]["cards"])
        self.assertEqual(("waiting_input", "unresolved", []), (negative["ambiguous"]["status"], negative["ambiguous"]["resolution"], negative["ambiguous"]["cards"]))

    def test_desktop_floating_modes_bottom_input_and_settings_ids_are_stable(self) -> None:
        result = self.result["desktopAutomation"]
        self.assertEqual("PASS-SOURCE", result["floatingContextMode"])
        self.assertEqual("PASS-SOURCE", result["floatingOriginalMode"])
        self.assertEqual("PASS-SOURCE", result["bottomInlineInput"])
        self.assertGreaterEqual(result["settingsAutomationIds"]["count"], 66)
        self.assertEqual(result["settingsAutomationIds"]["count"], result["settingsAutomationIds"]["unique"])
        self.assertEqual("knowledge-clipper.desktop.", result["settingsAutomationIds"]["prefix"])
        self.assertEqual("PENDING-DESKTOP", result["ownerAutomationReceipt"])

    def test_retention_startup_is_fail_closed_and_probe_is_isolated(self) -> None:
        result = self.result["retentionSafety"]
        self.assertEqual("IMPLEMENTED-PENDING-ISOLATED-OWNER-STARTUP-RECEIPT", result["status"])
        self.assertFalse(result["legacyCallPresent"])
        self.assertTrue(result["legacyImplementationUnsafe"])
        self.assertTrue(result["safeStartupRetentionDisabled"])
        self.assertEqual(0, result["startupLegacyPurgeInvocations"])
        self.assertFalse(result["productionJobsRootUsed"])
        self.assertEqual(result["isolatedProbe"]["oldTasksChecked"], result["isolatedProbe"]["oldTasksPreserved"])

    def test_legacy_external_canaries_are_opt_in_and_absent_from_candidate(self) -> None:
        result = self.result["legacyCanarySafety"]
        self.assertEqual("PASS-DEFAULT-BUILD-OPT-IN-ONLY-AND-CANDIDATE-INVENTORY-CLEAN", result["status"])
        self.assertFalse(result["buildDefaultLegacyExternalCanaries"])
        self.assertEqual(0, result["legacyExecutablesRunByQuality"])
        self.assertEqual(0, result["candidateInventory"]["forbiddenHits"])
        self.assertGreater(result["candidateInventory"]["filesChecked"], 0)

    def test_legacy_canary_policy_cannot_enable_default_or_shrink_inventory(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["legacyCanarySafety"]["defaultBuildIncludesLegacyExternalCanaries"] = True
        with self.assertRaisesRegex(gate.GateFailure, "policy was weakened"):
            gate.evaluate(fixture)
        fixture = copy.deepcopy(self.fixture)
        fixture["legacyCanarySafety"]["forbiddenBasenames"] = ["SmokeTest.exe"]
        with self.assertRaisesRegex(gate.GateFailure, "forbidden inventory was weakened"):
            gate.evaluate(fixture)

    def test_retention_fixture_must_preserve_waiting_unknown_pinned_and_evidence(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["retentionSafety"]["oldTaskFixtures"] = [item for item in fixture["retentionSafety"]["oldTaskFixtures"] if item["state"] != "unknown_external_state"]
        with self.assertRaisesRegex(gate.GateFailure, "protected nonterminal state"):
            gate.evaluate(fixture)
        fixture = copy.deepcopy(self.fixture)
        fixture["retentionSafety"]["oldTaskFixtures"][0]["preserved"] = False
        with self.assertRaisesRegex(gate.GateFailure, "permits startup deletion"):
            gate.evaluate(fixture)

    def test_policy_weakening_or_owner_completion_fabrication_is_rejected(self) -> None:
        fixture = copy.deepcopy(self.fixture)
        fixture["policy"]["pendingIsFailure"] = True
        with self.assertRaisesRegex(gate.GateFailure, "policy weakened"):
            gate.evaluate(fixture)
        fixture = copy.deepcopy(self.fixture)
        fixture["ownerEvidence"]["core"] = "passed"
        with self.assertRaisesRegex(gate.GateFailure, "fabricated"):
            gate.evaluate(fixture)

    def test_secret_frozen_and_side_effect_boundaries_are_zero(self) -> None:
        self.assertEqual(0, self.result["secretScan"]["findingCount"])
        self.assertEqual(21, self.result["immutableBaselines"]["frozenV1"]["filesChecked"])
        self.assertEqual(0, self.result["immutableBaselines"]["frozenV1"]["mismatches"])
        self.assertTrue(all(value == 0 for value in self.result["qualitySideEffects"].values()))


if __name__ == "__main__":
    unittest.main()
