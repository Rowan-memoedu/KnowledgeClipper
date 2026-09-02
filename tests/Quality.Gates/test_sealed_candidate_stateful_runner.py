from __future__ import annotations

import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT / "tools"))

import run_sealed_candidate_stateful_journeys as runner  # noqa: E402
import verify_production_state_journeys as gate  # noqa: E402


FIXTURE = PROJECT / "tests/Quality.Gates/fixtures/sealed-candidate-stateful-runner/valid/future-runner.json"


class SealedCandidateStatefulRunnerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.fixture = gate.load(FIXTURE)

    def test_fixture_contract_validates_without_execution(self) -> None:
        result = runner.validate_spec(copy.deepcopy(self.fixture), allow_fixture=True)
        self.assertEqual("fixture-valid-not-executable", result["status"])
        self.assertEqual(0, result["externalCallsAllowed"])

    def test_fixture_cannot_be_executed_as_candidate_evidence(self) -> None:
        with self.assertRaisesRegex(gate.GateFailure, "fixture-only runner input cannot execute"):
            runner.validate_spec(copy.deepcopy(self.fixture))

    def test_external_service_enablement_is_rejected(self) -> None:
        value = copy.deepcopy(self.fixture)
        value["externalPolicy"]["anki"] = True
        with self.assertRaisesRegex(gate.GateFailure, "fully offline or the exact authorized live-read-only policy"):
            runner.validate_spec(value, allow_fixture=True)

    def test_exact_live_readonly_policy_is_accepted(self) -> None:
        value = copy.deepcopy(self.fixture)
        value["externalPolicy"] = {
            "remnote": {"mode": "live-read-only", "writesAllowed": 0, "stopExistingProcesses": False},
            "anki": {"mode": "live-read-only", "writesAllowed": 0, "stopExistingProcesses": False},
            "mineru": False,
            "deepseek": {"mode": "live", "writesAllowed": 0},
            "speaker": False,
        }
        result = runner.validate_spec(value, allow_fixture=True)
        self.assertEqual("authorized-live-read-only", result["externalMode"])

    def test_injected_host_marker_is_rejected(self) -> None:
        value = copy.deepcopy(self.fixture)
        value["injectedHost"] = True
        with self.assertRaisesRegex(gate.GateFailure, "forbidden test substitution"):
            runner.validate_spec(value, allow_fixture=True)

    def test_shared_test_core_path_is_rejected(self) -> None:
        value = copy.deepcopy(self.fixture)
        value["runtimeAssembly"] = "tests/PhaseB.Canary/KnowledgeClipper.Orchestrator.dll"
        with self.assertRaisesRegex(gate.GateFailure, "forbidden test substitution"):
            runner.validate_spec(value, allow_fixture=True)

    def test_unique_pipe_and_real_persistence_are_required(self) -> None:
        value = copy.deepcopy(self.fixture)
        value["isolation"]["pipeName"] = "KnowledgeClipper.Default"
        with self.assertRaisesRegex(gate.GateFailure, "uniquely Quality-scoped"):
            runner.validate_spec(value, allow_fixture=True)
        value = copy.deepcopy(self.fixture)
        value["isolation"]["persistenceMode"] = "memory"
        with self.assertRaisesRegex(gate.GateFailure, "real isolated persistence"):
            runner.validate_spec(value, allow_fixture=True)

    def test_process_environment_isolates_localappdata_and_explicit_bindings(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            isolated = Path(directory)
            with patch.dict(runner.os.environ, {"AI_PRODUCER_API_KEY": "must-not-inherit", "MINERU_API_TOKEN": "must-not-inherit"}):
                value = runner.process_env(copy.deepcopy(self.fixture), isolated)
            self.assertEqual(str((isolated / "localappdata").resolve()), value["LOCALAPPDATA"])
            self.assertEqual(str((isolated / "settings").resolve()), value["KNOWLEDGE_CLIPPER_SETTINGS_ROOT"])
            self.assertEqual(str((isolated / "jobs").resolve()), value["KNOWLEDGE_CLIPPER_JOBS_ROOT"])
            self.assertEqual("KnowledgeClipper.Quality.Stateful.fixture", value["KNOWLEDGE_CLIPPER_PIPE_NAME"])
            self.assertEqual(runner.OFFLINE_CREDENTIAL_SENTINEL, value["AI_PRODUCER_API_KEY"])
            self.assertEqual(runner.OFFLINE_CREDENTIAL_SENTINEL, value["MINERU_API_TOKEN"])
            self.assertEqual(runner.OFFLINE_LOOPBACK_BASE_URL, value["AI_PRODUCER_BASE_URL"])
            self.assertEqual("1", value["AI_PRODUCER_MAX_RETRIES"])
            self.assertEqual(str((isolated / "offline-no-user-config.md").resolve()), value["AI_PRODUCER_CONFIG_FILE"])
            self.assertNotIn("DEEPSEEK_API_KEY", value)
            self.assertNotIn("RQC_DEEPSEEK_API_KEY", value)
            self.assertEqual("http://127.0.0.1:1/mcp", value["KNOWLEDGE_CLIPPER_REMNOTE_MCP_URL"])
            self.assertEqual("http://127.0.0.1:1", value["KNOWLEDGE_CLIPPER_ANKI_ENDPOINT"])

    def test_offline_process_environment_cannot_fall_back_to_user_credentials(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            isolated = Path(directory)
            inherited = {
                "AI_PRODUCER_API_KEY": "real-user-key-must-be-shadowed",
                "AI_PRODUCER_BASE_URL": "https://api.deepseek.com",
                "AI_PRODUCER_CONFIG_FILE": "C:/Users/example/DeepSeek API 配置.md",
                "MINERU_API_TOKEN": "real-user-token-must-be-shadowed",
            }
            with patch.dict(runner.os.environ, inherited, clear=False):
                value = runner.process_env(copy.deepcopy(self.fixture), isolated)
            serialized = json.dumps(value, ensure_ascii=False)
            self.assertNotIn("real-user-key-must-be-shadowed", serialized)
            self.assertNotIn("real-user-token-must-be-shadowed", serialized)
            self.assertNotIn("api.deepseek.com", serialized)
            self.assertEqual(runner.OFFLINE_LOOPBACK_BASE_URL, value["AI_PRODUCER_BASE_URL"])

    def test_offline_boundary_rejects_live_provider_and_persisted_sentinel(self) -> None:
        with tempfile.TemporaryDirectory(dir=PROJECT) as directory:
            root = Path(directory)
            (root / "live.json").write_text(json.dumps({"provider": "deepseek", "mode": "live"}), encoding="utf-8")
            (root / "sentinel.log").write_text(runner.OFFLINE_CREDENTIAL_SENTINEL, encoding="utf-8")
            result = runner.inspect_offline_boundary(root)
            self.assertEqual("failed-external-policy-breach", result["status"])
            self.assertEqual(1, result["realExternalProviderCompletionCount"])
            self.assertEqual(1, result["credentialSentinelPersistenceCount"])

    def test_offline_boundary_accepts_failed_closed_loopback_without_secret_persistence(self) -> None:
        with tempfile.TemporaryDirectory(dir=PROJECT) as directory:
            root = Path(directory)
            (root / "result.json").write_text(json.dumps({"status": "failed", "endpoint": "http://127.0.0.1:1"}), encoding="utf-8")
            result = runner.inspect_offline_boundary(root)
            self.assertEqual("passed", result["status"])
            self.assertTrue(result["closedLoopbackDenyConfiguration"]["notARealExternalService"])

    def test_forced_cleanup_targets_the_owned_process_tree(self) -> None:
        with patch.object(runner.subprocess, "run") as mocked:
            mocked.return_value.returncode = 0
            self.assertTrue(runner.force_cleanup_owned_process_tree(4242))
            self.assertEqual(["taskkill.exe", "/PID", "4242", "/T", "/F"], mocked.call_args.args[0])

    def test_remnote_requires_a_real_restart_phase(self) -> None:
        value = copy.deepcopy(self.fixture)
        value["journeys"]["remnote"]["phases"][1]["restartDesktop"] = False
        with self.assertRaisesRegex(gate.GateFailure, "restart boundary"):
            runner.validate_spec(value, allow_fixture=True)

    def test_question_requires_exact_native_enter_counts(self) -> None:
        value = copy.deepcopy(self.fixture)
        value["journeys"]["question"]["expectedActionCounts"]["question.submit"] = 2
        with self.assertRaisesRegex(gate.GateFailure, "native Enter"):
            runner.validate_spec(value, allow_fixture=True)

    def test_question_scenario_sends_enter_exactly_twice(self) -> None:
        reference = self.fixture["journeys"]["question"]["phases"][0]["scenario"]
        result = runner.validate_scenario(reference, "question")
        self.assertEqual(8, result["steps"])

    def test_anki_requires_formal_multifile_artifact(self) -> None:
        value = copy.deepcopy(self.fixture)
        value["journeys"]["anki"]["productionArtifact"] = "single-file-fake"
        with self.assertRaisesRegex(gate.GateFailure, "formal read-only multi-file"):
            runner.validate_spec(value, allow_fixture=True)

    def test_native_driver_polls_assertions_until_their_deadline(self) -> None:
        driver = (PROJECT / "tools/Invoke-KnowledgeClipperStatefulUi.ps1").read_text(encoding="utf-8")
        self.assertGreaterEqual(driver.count("$assertDeadline"), 2)
        self.assertIn("Start-Sleep -Milliseconds 100", driver)

    def test_candidate_promotion_requires_a_zero_violation_source_gate(self) -> None:
        value = gate.load(PROJECT / "tests/Quality.Gates/fixtures/production-state-journeys/candidate/valid/sealed-process-stateful.json")
        value.pop("fixtureOnly")
        with self.assertRaisesRegex(gate.GateFailure, "evidence reference path missing"):
            gate.verify_candidate_promotion(value)

    def test_native_ui_driver_uses_bounded_process_scoped_tree_walk(self) -> None:
        driver_text = runner.DRIVER.read_text(encoding="utf-8-sig")
        self.assertNotIn("RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants", driver_text)
        self.assertIn("[System.Windows.Automation.TreeWalker]::ControlViewWalker", driver_text)
        self.assertIn("[int]$MaxNodesPerPass = 4096", driver_text)
        self.assertIn("[DateTime]::UtcNow -lt $deadline", driver_text)
        self.assertIn("$item.Current.ProcessId -eq $Id", driver_text)

    def test_native_ui_driver_reads_accessible_state_before_stable_id_name(self) -> None:
        driver_text = runner.DRIVER.read_text(encoding="utf-8-sig")
        self.assertIn("[System.Windows.Automation.InvokePattern]::Pattern", driver_text)
        self.assertIn("$pattern.Invoke()", driver_text)
        self.assertNotIn("$legacy.DoDefaultAction()", driver_text)
        self.assertIn("$Element.Current.HelpText", driver_text)
        self.assertLess(driver_text.index("$Element.Current.HelpText"), driver_text.index("$Element.Current.Name"))

    def test_production_routes_scenarios_open_the_export_page_before_hidden_controls(self) -> None:
        fixture = gate.load(PROJECT / "tests/Quality.Gates/fixtures/sealed-candidate-stateful-runner/valid/production-routes-runner.json")
        for journey in ("anki", "remnote"):
            for phase in fixture["journeys"][journey]["phases"]:
                scenario = gate.load(PROJECT / phase["scenario"]["path"])
                self.assertEqual("knowledge-clipper.desktop.settings.navigation", scenario["steps"][0]["automationId"])
                self.assertEqual("focus", scenario["steps"][0]["operation"])
                self.assertEqual("{END}{UP}", scenario["steps"][1]["keys"])

    def test_candidate_pipe_must_match_integration_and_ipc_contract(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            ipc = root / "authority/ipc.json"
            ipc.parent.mkdir(parents=True)
            ipc.write_text(json.dumps({"pipeName": "KnowledgeClipper.Quality.Stateful.bound"}), encoding="utf-8")
            (root / "candidate-integration.json").write_text(
                json.dumps({
                    "SchemaVersion": "knowledge-clipper-candidate-integration.v1",
                    "PipeName": "KnowledgeClipper.Quality.Stateful.bound",
                    "IpcConfig": "authority/ipc.json",
                }),
                encoding="utf-8",
            )
            self.assertEqual(
                "KnowledgeClipper.Quality.Stateful.bound",
                runner.validate_candidate_pipe_binding(root, "KnowledgeClipper.Quality.Stateful.bound")["pipeName"],
            )
            with self.assertRaisesRegex(gate.GateFailure, "does not match the sealed candidate integration"):
                runner.validate_candidate_pipe_binding(root, "KnowledgeClipper.Quality.Stateful.other")

    def test_runner_emits_phase_progress_for_timeout_observability(self) -> None:
        source = (PROJECT / "tools/run_sealed_candidate_stateful_journeys.py").read_text(encoding="utf-8")
        self.assertIn('"event": "phase-start"', source)
        self.assertIn('"event": "phase-passed"', source)
        self.assertIn('"event": "phase-failed"', source)
        self.assertGreaterEqual(source.count("flush=True"), 3)

    def test_runner_collects_all_journeys_after_an_independent_failure(self) -> None:
        source = (PROJECT / "tools/run_sealed_candidate_stateful_journeys.py").read_text(encoding="utf-8")
        self.assertNotIn("if phase_failures:\n            break", source)
        self.assertNotIn("not-run-after-prior-failure", source)


if __name__ == "__main__":
    unittest.main()
