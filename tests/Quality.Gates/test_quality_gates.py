from __future__ import annotations

import copy
import hashlib
import json
import sys
import tempfile
import unittest
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tools"))

import quality_gates as gates  # noqa: E402


class QualityGateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.schemas = gates.load_schemas()
        cls.receipt_validator = cls.schemas.validator(
            gates.SCHEMAS_ROOT / "quality" / "verification-receipt.schema.json"
        )

    def test_verification_receipt_fixtures(self) -> None:
        root = gates.CONTRACTS_ROOT / "fixtures" / "quality" / "verification-receipt"
        valid = sorted((root / "valid").glob("*.json"))
        invalid = sorted((root / "invalid").glob("*.json"))
        self.assertEqual(2, len(valid))
        self.assertEqual(3, len(invalid))
        for path in valid:
            self.assertEqual([], list(self.receipt_validator.iter_errors(gates.load_json(path))), path.name)
        for path in invalid:
            self.assertNotEqual([], list(self.receipt_validator.iter_errors(gates.load_json(path))), path.name)

    def test_stable_checkpoint_and_canonical_evidence_gate(self) -> None:
        result = gates.verify_stable_checkpoint()
        self.assertEqual(21, result["filesChecked"])
        canonical = gates.assert_canonical_contract_evidence(
            PROJECT_ROOT / "contracts/v1/schemas/common/worker-result.schema.json"
        )
        self.assertEqual(
            "contracts/v1/schemas/common/worker-result.schema.json",
            canonical["path"],
        )
        with self.assertRaisesRegex(gates.GateFailure, "module-local adapters"):
            gates.assert_canonical_contract_evidence(
                PROJECT_ROOT / "workers/anki-exporter/fixtures/legacy-anki-adapter-manifest.v0.schema.json"
            )

    def test_empty_authority_registry_is_valid_but_no_profile_is_ready(self) -> None:
        state = gates.assess_authority_registry(run_describe=False)
        self.assertEqual("empty", state["state"])
        self.assertEqual(0, state["authorityRegistryEntries"])
        self.assertTrue(all(not ready for ready in state["profileReady"].values()))
        self.assertNotIn("export.anki", state["missingRequiredWorkers"]["parity-remnote"])
        self.assertNotIn("export.remnote", state["missingRequiredWorkers"]["cards-anki"])

    def test_speech_module_onboarding_pass_does_not_publish_authority(self) -> None:
        result = gates.assess_speech_onboarding(PROJECT_ROOT)
        self.assertEqual("passed", result["moduleOnboarding"])
        self.assertEqual("eligible-but-not-authority-published", result["onboardingState"])
        self.assertFalse(result["authorityPublished"])
        self.assertFalse(result["releaseCandidateEligible"])
        self.assertTrue(result["currentPresealLayoutReady"])
        self.assertTrue(result["finalReleaseLayoutReady"])
        self.assertEqual("passed", result["coreReuseCompatibility"])
        self.assertEqual(
            "1ADEED64784C5F16F9F18128CF6579F06F2C881F5CC5A68D747DA8F97C71CA02",
            result["coreReuseCompatibilityReceiptSha256"],
        )

    def test_image_ocr_onboarding_pass_does_not_publish_authority(self) -> None:
        result = gates.assess_image_onboarding(PROJECT_ROOT)
        self.assertEqual("passed", result["moduleOnboarding"])
        self.assertEqual(
            "3487C4D0FF8E5C5CA81ED2D2F85D957720DCAFE93F848337EE53EAAD4EC01BAD",
            result["executableSha256"],
        )
        self.assertEqual(10235516, result["executableBytes"])
        self.assertEqual("neutral-KnowledgeClipper-entrypoint-verified", result["workingEntrypointState"])
        self.assertEqual("passed-KnowledgeClipper", result["brandIdentity"])
        self.assertFalse(result["authorityPublished"])
        self.assertFalse(result["releaseCandidateEligible"])
        self.assertEqual("passed", result["offlineScenarioMatrix"])
        self.assertEqual(39, result["latestTestsPassed"])
        self.assertEqual("passed-fail-closed", result["unknownExternalState"])
        self.assertFalse(result["realMineruRerun"])

    def test_content_language_extension_pass_remains_integration_pending(self) -> None:
        result = gates.assess_content_onboarding(PROJECT_ROOT)
        self.assertEqual("passed-scoped-question-language-extension", result["moduleOnboarding"])
        self.assertEqual(
            "eligible-content-only-registry-admission-passed-five-worker-authority-pending",
            result["onboardingState"],
        )
        self.assertEqual("0.5.2", result["workerVersion"])
        self.assertEqual(58, result["latestTestsPassed"])
        self.assertEqual(
            ["understand-and-card", "question.answer", "question.make-cards"],
            result["operations"],
        )
        self.assertEqual(
            ["learning-bundle", "card-bundle", "language-practice-decision", "question.answer"],
            result["outputKinds"],
        )
        self.assertFalse(result["authorityDescriptorRefreshRequired"])
        self.assertEqual("passed-conditional", result["contentOnlyRegistryAdmission"])
        self.assertEqual(
            "ED733E4391661AA48105B83BEF365BAF180CA3DEA5AB1F7849CC6ADE3731636F",
            result["contentOnlyRegistryAdmissionReceiptSha256"],
        )
        self.assertEqual(713, result["contentOnlyRegistryReleaseRoot"]["files"])
        self.assertEqual("NO-GO", result["fiveWorkerAuthorityRegistry"])
        self.assertEqual(4, len(result["offlineCanaries"]))
        self.assertTrue(all(item["status"] == "completed" for item in result["offlineCanaries"]))
        self.assertTrue(result["terminalLanguageWritingNewlineRemoved"])
        self.assertFalse(result["frozenDescribeSchemaCarriesOperations"])
        self.assertEqual("passed-KnowledgeClipper-destination-neutral", result["brandIdentity"])
        self.assertFalse(result["authorityPublished"])
        self.assertFalse(result["releaseCandidateEligible"])

    def test_remnote_onboarding_pass_uses_final_deterministic_binary(self) -> None:
        result = gates.assess_remnote_onboarding(PROJECT_ROOT)
        self.assertEqual("passed", result["moduleOnboarding"])
        self.assertEqual("eligible-but-not-authority-published", result["onboardingState"])
        self.assertEqual(
            "FCB81BD1C8E44B3B5B9B95E3EFC22C281A8E59E1E74F5598C66B9E0B241F1A16",
            result["executableSha256"],
        )
        self.assertEqual(73728, result["executableBytes"])
        self.assertEqual(11, result["contentConsumerChecksPassed"])
        self.assertEqual(
            "48C3F329CD7FC7C8CCBD0EE249D79DB0BBEEC77D9123DC35D2261FF48EAA0851",
            result["contentConsumerReceiptSha256"],
        )
        self.assertEqual("retained-reference-not-rerun", result["liveRemNoteEvidence"])
        self.assertFalse(result["authorityPublished"])

    def test_self_contained_orchestrator_completion_receipt(self) -> None:
        result = gates.assess_orchestrator_completion(PROJECT_ROOT)
        self.assertEqual("passed-scope-production-no-go", result["status"])
        self.assertTrue(result["selfContained"])
        self.assertEqual(
            "C93E911B5B977F9543AC2ED5BCC82FCCCCD7B4CCB77A367019327834EF0F4802",
            result["executableSha256"],
        )
        self.assertEqual("1.1.0.0", result["version"])
        self.assertEqual(23, result["contractTestGroups"])
        self.assertEqual("NO-GO", result["productionDecision"])
        self.assertFalse(result["candidateBound"])

    def test_desktop_contract_pass_does_not_replace_real_windows_canary(self) -> None:
        result = gates.assess_desktop_contract(PROJECT_ROOT)
        self.assertEqual("passed", result["status"])
        self.assertEqual(11, result["filesChecked"])
        self.assertEqual(
            "D4D0CCA8D1210A90F484A6D31643A8312F67AB6F1E53FC2367308AF009194A6E",
            result["treeSha256"],
        )
        self.assertEqual("pending", result["realWindowsCanary"])
        self.assertEqual("pending", result["productionOrchestratorProcessCanary"])

    def test_clickable_desktop_automated_scope_does_not_replace_physical_canary(self) -> None:
        result = gates.assess_desktop_clickability(PROJECT_ROOT)
        self.assertEqual("passed-scoped-user-boundaries-pending", result["status"])
        self.assertEqual(
            "29320D1CCAA252057D011490866CC7533901740C5C6921359B7A2F9F58C4720C",
            result["executableSha256"],
        )
        self.assertEqual(243, result["packageTopLevelFilesChecked"])
        self.assertEqual(17, result["semanticHarnessResponses"])
        self.assertEqual(54, result["windowsUiAssertions"])
        self.assertEqual(7, result["visualPreviews"])
        self.assertEqual(0, result["startupDiagnosticHiddenWrites"])
        self.assertEqual("pending", result["physicalUserExperience"])
        self.assertFalse(result["candidateBound"])
        self.assertFalse(result["currentPresealApplicationDllMatches"])

    def test_current_preseal_is_exact_unsealed_and_non_authority(self) -> None:
        result = gates.assess_current_preseal(PROJECT_ROOT)
        self.assertEqual("1.0.0-rc.1-preseal.10", result["releaseVersion"])
        self.assertEqual("unsealed-no-go", result["status"])
        self.assertEqual(4994, result["files"])
        self.assertEqual(791324353, result["bytes"])
        self.assertEqual(
            "D0CE5FB36765A45D49BE5C83F6E4223C102C9EB13D37EDE079AEDCB97D00A6BC",
            result["treeSha256"],
        )
        self.assertEqual(0, result["authorityRegistryEntries"])
        self.assertEqual(5, result["registryCandidateWorkers"])
        self.assertFalse(result["registryCandidateAuthority"])
        self.assertEqual(0, result["hiddenWrites"])
        self.assertEqual("passed-no-side-effects", result["desktopStartupDiagnostic"])
        self.assertTrue(result["desktopPackageReceiptVerified"])
        self.assertIsNone(result["releaseManifest"])
        self.assertIsNone(result["candidateHash"])

    def test_anki_current_authority_acceptance_passes_but_candidate_remains_locked(self) -> None:
        result = gates.assess_anki_boundary(PROJECT_ROOT)
        self.assertEqual("passed-scoped-current-content-acceptance", result["moduleOnboarding"])
        self.assertEqual("eligible-but-neutral-authority-checkpoint-pending", result["onboardingState"])
        self.assertEqual("1.1.4", result["workerVersion"])
        self.assertEqual(
            "850BACAB0F3C86D84AE3B3A415AE780C079C5F98475E76D9C9F3E2D25DDBA0DB",
            result["executableSha256"],
        )
        self.assertEqual(10209227, result["executableBytes"])
        self.assertEqual(4, result["consumerAcceptanceChecksPassed"])
        self.assertTrue(result["validateOnly"])
        self.assertFalse(result["externalAnkiCalled"])
        self.assertFalse(result["authorityPublished"])
        self.assertFalse(result["releaseCandidateEligible"])

    def test_candidate_hash_binds_every_receipt_and_mutation_invalidates_all(self) -> None:
        with tempfile.TemporaryDirectory(prefix="KnowledgeClipper.CandidateGate.") as temporary:
            root = Path(temporary)
            manifest = root / "releases/1.0.0/win-x64/release-manifest.v1.json"
            manifest.parent.mkdir(parents=True)
            manifest.write_text('{"schemaVersion":"1.0.0","files":[]}', encoding="utf-8")
            candidate_hash = hashlib.sha256(manifest.read_bytes()).hexdigest().upper()
            base = gates.load_json(
                gates.CONTRACTS_ROOT
                / "fixtures/quality/verification-receipt/valid/worker-canary.json"
            )
            receipt_specs = [
                ("contract-fixtures", "contracts-v1"),
                ("desktop-canary", "desktop"),
                ("worker-canary", "speech.edge"),
                ("end-to-end", "desktop-to-remnote"),
            ]
            receipt_paths: list[Path] = []
            for index, (kind, component_id) in enumerate(receipt_specs):
                document = copy.deepcopy(base)
                document["receiptId"] = f"receipt-{index}"
                document["verificationKind"] = kind
                document["candidate"]["candidateHash"] = candidate_hash
                document["subject"]["componentId"] = component_id
                if kind == "end-to-end":
                    document["privacy"] = {
                        "syntheticDataOnly": True,
                        "productionContentObserved": False,
                        "secretsRedacted": True,
                    }
                path = root / f"receipt-{index}.json"
                path.write_text(json.dumps(document), encoding="utf-8")
                receipt_paths.append(path)
            passed = gates.verify_candidate_receipts(
                root, manifest, receipt_paths, "parity-remnote", ["speech.edge"]
            )
            self.assertEqual(candidate_hash, passed["candidateHash"])
            manifest.write_text('{"schemaVersion":"1.0.0","files":[],"rebuilt":true}', encoding="utf-8")
            with self.assertRaisesRegex(gates.GateFailure, "candidateHash mismatch"):
                gates.verify_candidate_receipts(
                    root, manifest, receipt_paths, "parity-remnote", ["speech.edge"]
                )

    def test_current_candidate_gate_remains_pending(self) -> None:
        status = gates.current_status()
        self.assertEqual("pending", status["candidateGate"]["status"])
        self.assertEqual("NO-GO", status["candidateGate"]["decision"])
        self.assertEqual("brand-migration-in-progress", status["workerOnboarding"]["state"])
        self.assertEqual(4, len(status["workerOnboarding"]["brandReadyWorkerIds"]))
        self.assertEqual("fixture-ready-implementation-pending", status["languageChoiceCardGate"]["status"])
        self.assertEqual(
            "observed-neutral-binary-awaiting-module-receipt",
            status["orchestratorBrandMigration"]["status"],
        )
        self.assertEqual(
            [
                "KnowledgeClipper-orchestrator-and-authority-descriptor-migrations-incomplete",
                "new-neutral-five-worker-authority-registry-checkpoint-missing",
                "desktop-inline-textbox-windows-canary-and-ui-package-pending",
                "desktop-additive-contract-common-checkpoint-reference-missing",
                "release-manifest-missing",
                "desktop-physical-windows-boundaries-incomplete",
                "candidate-bound-worker-canaries-missing",
                "desktop-to-real-remnote-e2e-receipt-missing",
                "signed-release-packaging-missing",
            ],
            status["candidateGate"]["reasons"],
        )


if __name__ == "__main__":
    unittest.main(verbosity=2)
