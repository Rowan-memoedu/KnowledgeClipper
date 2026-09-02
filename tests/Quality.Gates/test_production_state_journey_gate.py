from __future__ import annotations

import copy
import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT / "tools"))

import verify_production_state_journeys as gate  # noqa: E402


class ProductionStateJourneyGateTests(unittest.TestCase):
    def test_architecture_gate_covers_desktop_orchestrator_dependency(self) -> None:
        result = gate.inspect_architecture()
        self.assertIn("desktop-ui-no-orchestrator-reference-or-instantiation", result["rulesEvaluated"])

    def test_architecture_gate_covers_unified_verified_artifact_reader(self) -> None:
        result = gate.inspect_architecture()
        self.assertIn("production-artifacts-use-unified-verified-reader", result["rulesEvaluated"])

    def test_architecture_gate_covers_active_question_session_query_scope(self) -> None:
        result = gate.inspect_architecture()
        self.assertIn("active-question-session-does-not-query-global-recent", result["rulesEvaluated"])

    def test_architecture_gate_covers_native_enter_routing(self) -> None:
        result = gate.inspect_architecture()
        self.assertIn("native-enter-routes-by-question-session-state", result["rulesEvaluated"])

    def test_architecture_gate_covers_synthetic_promotion(self) -> None:
        result = gate.inspect_architecture()
        self.assertIn("synthetic-display-result-cannot-promote-candidate", result["rulesEvaluated"])

    def test_formal_anki_four_file_artifact_is_accepted(self) -> None:
        result = gate.verify_anki_artifact(gate.FIXTURES / "anki/valid")
        self.assertEqual(4, result["filesChecked"])
        self.assertEqual(3, result["sidecarsAccepted"])
        self.assertEqual("outputs/connection-status.json", result["entrypoint"])

    def test_anki_file_order_is_not_semantic(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "anki"
            shutil.copytree(gate.FIXTURES / "anki/valid", root)
            manifest = gate.load(root / "artifact.json")
            manifest["files"].reverse()
            (root / "artifact.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            self.assertEqual("passed", gate.verify_anki_artifact(root)["status"])

    def test_anki_tampered_sidecar_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "anki"
            shutil.copytree(gate.FIXTURES / "anki/valid", root)
            with (root / "outputs/operations.ndjson").open("a", encoding="utf-8") as stream:
                stream.write("tampered\n")
            with self.assertRaisesRegex(gate.GateFailure, "bytes drift|SHA drift"):
                gate.verify_anki_artifact(root)

    def test_anki_missing_entrypoint_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "anki"
            shutil.copytree(gate.FIXTURES / "anki/valid", root)
            manifest = gate.load(root / "artifact.json")
            manifest["metadata"].pop("entrypoint")
            (root / "artifact.json").write_text(json.dumps(manifest), encoding="utf-8")
            with self.assertRaisesRegex(gate.GateFailure, "entrypoint missing"):
                gate.verify_anki_artifact(root)

    def test_anki_duplicate_entrypoint_file_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "anki"
            shutil.copytree(gate.FIXTURES / "anki/valid", root)
            manifest = gate.load(root / "artifact.json")
            manifest["files"].append(copy.deepcopy(manifest["files"][0]))
            (root / "artifact.json").write_text(json.dumps(manifest), encoding="utf-8")
            with self.assertRaisesRegex(gate.GateFailure, "duplicate"):
                gate.verify_anki_artifact(root)

    def test_remnote_full_stateful_journey_is_accepted(self) -> None:
        result = gate.verify_remnote_journey(gate.load(gate.FIXTURES / "remnote/valid/stateful.json"))
        self.assertTrue(result["restartVerified"])
        self.assertTrue(result["nextTaskSnapshotVerified"])

    def test_remnote_split_form_and_persisted_state_is_rejected(self) -> None:
        with self.assertRaisesRegex(gate.GateFailure, "contradicts"):
            gate.verify_remnote_journey(gate.load(gate.FIXTURES / "remnote/invalid/split-form-and-persisted-state.json"))

    def test_native_keyboard_question_sequence_is_accepted(self) -> None:
        result = gate.verify_question_journey(gate.load(gate.FIXTURES / "question/valid/native-keyboard.json"))
        self.assertEqual(1, result["submitCount"])
        self.assertEqual(1, result["followUpCount"])
        self.assertEqual("attention_required", result["unresolvedTerminal"])

    def test_double_submit_and_stuck_loading_are_rejected(self) -> None:
        with self.assertRaises(gate.GateFailure):
            gate.verify_question_journey(gate.load(gate.FIXTURES / "question/invalid/double-submit-stuck-loading.json"))

    def test_sealed_candidate_process_stateful_fixture_validates_but_cannot_promote(self) -> None:
        fixture = gate.load(gate.FIXTURES / "candidate/valid/sealed-process-stateful.json")
        result = gate.verify_candidate_promotion(fixture, allow_fixture=True)
        self.assertEqual("fixture-contract-passed-not-promotable", result["status"])
        self.assertFalse(result["userHandTestEntrypoint"])
        with self.assertRaisesRegex(gate.GateFailure, "fixture-only evidence"):
            gate.verify_candidate_promotion(fixture)

    def test_synthetic_component_evidence_cannot_promote(self) -> None:
        with self.assertRaisesRegex(gate.GateFailure, "sealed candidate process-stateful"):
            gate.verify_candidate_promotion(gate.load(gate.FIXTURES / "candidate/invalid/synthetic-component-promoted.json"))

    def test_shared_test_core_dll_cannot_promote(self) -> None:
        with self.assertRaisesRegex(gate.GateFailure, "injected/test-only"):
            gate.verify_candidate_promotion(gate.load(gate.FIXTURES / "candidate/invalid/shared-test-core-dll.json"))

    def test_current_baseline_is_no_go_and_invalidates_only_handtest_claims(self) -> None:
        result = gate.build_baseline()
        expected = "NO-GO-CURRENT-SOURCE-ARCHITECTURE" if result["architecture"]["violations"] else "WAITING-SEALED-CANDIDATE-PROCESS-STATEFUL"
        self.assertEqual(expected, result["status"])
        self.assertFalse(result["promotion"]["userHandTestEntrypoint"])
        self.assertIn("candidate-manifest-and-tree", result["v1237Invalidation"]["preservedClaims"])
        self.assertFalse(result["v1237Invalidation"]["oldReceiptsModified"])
        self.assertEqual(0, result["sideEffects"]["externalCalls"])


if __name__ == "__main__":
    unittest.main()
