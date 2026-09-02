from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tools"))

import language_choice_quality_gates as gates  # noqa: E402


class LanguageChoiceQualityGateTests(unittest.TestCase):
    def test_locked_language_choice_fixture_is_ready_without_business_schema(self) -> None:
        result = gates.current_status()
        self.assertEqual("fixture-ready-implementation-pending", result["status"])
        self.assertEqual(6, result["cases"])
        self.assertEqual(["meaning-reading", "writing", "both"], result["choiceOptions"])
        self.assertFalse(result["businessSchemaCreated"])
        self.assertFalse(result["externalProductionApisInvoked"])
        self.assertEqual(["choice", "decisionKind", "targetToken", "binding"], result["forbiddenExporterFields"])
        self.assertEqual("current-content-language-acceptance-passed", result["remnoteExporterPreparation"]["status"])
        self.assertEqual(14, result["remnoteExporterPreparation"]["consumerChecksPassed"])
        self.assertEqual(2, result["remnoteExporterPreparation"]["realWritingCardsAccepted"])
        self.assertFalse(result["remnoteExporterPreparation"]["decisionFieldsLeakedToExporter"])
        self.assertTrue(result["remnoteExporterPreparation"]["currentLanguageMappingAccepted"])
        self.assertEqual("current-content-language-acceptance-passed", result["ankiExporterPreparation"]["status"])
        self.assertEqual(4, result["ankiExporterPreparation"]["consumerChecksPassed"])
        self.assertFalse(result["ankiExporterPreparation"]["choiceOwnedByExporter"])
        self.assertTrue(result["ankiExporterPreparation"]["validateOnly"])
        self.assertEqual("awaiting-additive-desktop-contract", result["desktopAdditiveContract"]["status"])
        self.assertFalse(result["desktopAdditiveContract"]["candidates"][0]["commonCheckpointReferenceValid"])
        self.assertTrue(result["desktopAdditiveContract"]["frozenV1UnchangedAndRejectsDecisionKind"])
        frozen_root = PROJECT_ROOT / "contracts/desktop-automation/v1"
        frozen_receipt = gates.load_fixture(frozen_root / "contract-receipt.json")
        self.assertEqual(
            "D4D0CCA8D1210A90F484A6D31643A8312F67AB6F1E53FC2367308AF009194A6E",
            gates.verify_additive_contract_receipt(frozen_root, frozen_receipt),
        )

    def test_card_equivalence_order_and_fail_closed_rules_reject_drift(self) -> None:
        base = gates.load_fixture()
        mutations = []
        wrong_options = copy.deepcopy(base)
        wrong_options["desktopIntegration"]["choicePopup"]["options"].reverse()
        mutations.append(wrong_options)
        writing_drift = copy.deepcopy(base)
        writing_drift["cases"][1]["cards"][0]["back"] = "踌躇 "
        mutations.append(writing_drift)
        writing_keeps_target = copy.deepcopy(base)
        writing_keeps_target["cases"][1]["cards"][0]["front"] = base["source"]["visibleText"]
        mutations.append(writing_keeps_target)
        both_order = copy.deepcopy(base)
        both_order["cases"][2]["cards"].reverse()
        mutations.append(both_order)
        unresolved_published = copy.deepcopy(base)
        unresolved_published["cases"][3]["cards"] = [base["cases"][0]["cards"][0]]
        mutations.append(unresolved_published)
        production_mode = copy.deepcopy(base)
        production_mode["candidateBoundPolicy"]["allowedModes"].append("live")
        mutations.append(production_mode)
        for field in ["choice", "decisionKind", "targetToken", "binding"]:
            exporter_leak = copy.deepcopy(base)
            exporter_leak["cases"][0]["cards"][0][field] = "must-not-reach-exporter"
            mutations.append(exporter_leak)
        for document in mutations:
            with self.assertRaises(gates.LanguageChoiceGateFailure):
                gates.validate_fixture(document)


if __name__ == "__main__":
    unittest.main(verbosity=2)
