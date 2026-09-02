from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tools"))

import verify_settings_control_plane as gate  # noqa: E402


class SettingsControlPlaneQualityTests(unittest.TestCase):
    def test_quality_adapter_schema_and_fixtures(self) -> None:
        result = gate.validate_fixture_contract()
        self.assertEqual("passed", result["status"])
        self.assertEqual(1, result["validAccepted"])
        self.assertEqual(2, result["invalidRejected"])

    def test_missing_owner_evidence_is_independent_no_go(self) -> None:
        fake_baseline = {
            "status": "passed",
            "frozenV1": {"filesChecked": 21, "mismatches": 0},
            "authorityV124": {"fileCount": 91, "totalBytes": 1, "treeSha256": "A" * 64},
            "integratedCandidateV124": {"fileCount": 347, "totalBytes": 1, "treeSha256": "B" * 64},
        }
        with tempfile.TemporaryDirectory() as temporary, mock.patch.object(gate, "verify_immutable_baselines", return_value=fake_baseline), mock.patch.object(
            gate, "scan_paths_for_secrets", return_value={"status": "passed", "filesChecked": 1, "findingCount": 0, "findings": []}
        ):
            result = gate.evaluate(Path(temporary) / "owner-evidence.json")
        self.assertEqual("NO-GO-AWAITING-OWNER-EVIDENCE", result["status"])
        self.assertEqual(
            {"NO-GO-OWNER-EVIDENCE-MISSING"},
            {item["status"] for item in result["modules"].values()},
        )
        self.assertEqual(0, result["sideEffects"]["externalServiceCalls"])

    def test_secret_scanner_rejects_secrets_but_allows_refs_and_target_token(self) -> None:
        safe = {
            "apiKey": "env:DEEPSEEK_API_KEY",
            "password": "<redacted>",
            "targetToken": "learning-token-is-not-a-credential",
        }
        self.assertEqual([], gate.scan_json_secrets(safe))
        self.assertEqual([], gate.scan_json_secrets({"schema": "task-settings-snapshot.v1", "path": "task-settings-override.json"}))
        findings = gate.scan_json_secrets(
            {
                "apiKey": "sk-1234567890abcdefghijklmnop",
                "authorization": "Bearer abcdefghijklmnopqrstuvwxyz123456",
                "value": gate.SECRET_SENTINEL,
            }
        )
        self.assertGreaterEqual(len(findings), 3)
        self.assertEqual([], gate.scan_json_secrets({"apiKey": {"type": "string", "default": None}}))

    def test_json_pointer_unescapes_tokens(self) -> None:
        document = {"a/b": {"~key": ["zero", "one"]}}
        self.assertEqual("one", gate.json_pointer(document, "/a~1b/~0key/1"))
        with self.assertRaises(gate.GateFailure):
            gate.json_pointer(document, "/missing")

    def test_snapshot_binding_freezes_active_and_retry_but_changes_new_task(self) -> None:
        with tempfile.TemporaryDirectory(dir=PROJECT_ROOT) as temporary:
            root = Path(temporary)
            old_root = gate.PROJECT_ROOT
            gate.PROJECT_ROOT = root
            try:
                documents = {
                    "settings": {"editable": {"value": 2}},
                    "taskSnapshot": {"editable": {"value": 2}},
                    "resolvedPlan": {"stages": [{"configSnapshot": {"value": 2}, "payload": {"settings": {"value": 2}}}]},
                    "workerRequest": {"configSnapshot": {"value": 2}, "payload": {"settings": {"value": 2}}},
                    "retryWorkerRequest": {"configSnapshot": {"value": 2}, "payload": {"settings": {"value": 2}}},
                    "activeTaskSnapshotAfterSettingsChange": {"editable": {"value": 2}},
                    "newTaskSnapshot": {"editable": {"value": 3}},
                }
                refs = {}
                for name, document in documents.items():
                    path = root / f"{name}.json"
                    path.write_text(json.dumps(document), encoding="utf-8")
                    refs[name] = {"path": path.name, "sha256": gate.sha256_file(path), "bytes": path.stat().st_size}
                binding = {
                    **refs,
                    "mappings": [
                        {
                            "settingPointer": "/editable/value",
                            "snapshotPointer": "/editable/value",
                            "planConfigPointer": "/stages/0/configSnapshot/value",
                            "planPayloadPointer": "/stages/0/payload/settings/value",
                            "requestConfigPointer": "/configSnapshot/value",
                            "requestPayloadPointer": "/payload/settings/value",
                        }
                    ],
                }
                result = gate.verify_snapshot_binding(binding)
                self.assertEqual("passed", result["status"])
                self.assertEqual(1, result["mappingsChecked"])
            finally:
                gate.PROJECT_ROOT = old_root

    def test_snapshot_binding_rejects_retry_drift(self) -> None:
        with tempfile.TemporaryDirectory(dir=PROJECT_ROOT) as temporary:
            root = Path(temporary)
            old_root = gate.PROJECT_ROOT
            gate.PROJECT_ROOT = root
            try:
                documents = {
                    "settings": {"value": 2},
                    "taskSnapshot": {"value": 2},
                    "resolvedPlan": {"config": {"value": 2}, "payload": {"value": 2}},
                    "workerRequest": {"config": {"value": 2}, "payload": {"value": 2}},
                    "retryWorkerRequest": {"config": {"value": 99}, "payload": {"value": 2}},
                    "activeTaskSnapshotAfterSettingsChange": {"value": 2},
                    "newTaskSnapshot": {"value": 3},
                }
                refs = {}
                for name, document in documents.items():
                    path = root / f"{name}.json"
                    path.write_text(json.dumps(document), encoding="utf-8")
                    refs[name] = {"path": path.name, "sha256": gate.sha256_file(path)}
                binding = {
                    **refs,
                    "mappings": [{
                        "settingPointer": "/value",
                        "snapshotPointer": "/value",
                        "planConfigPointer": "/config/value",
                        "planPayloadPointer": "/payload/value",
                        "requestConfigPointer": "/config/value",
                        "requestPayloadPointer": "/payload/value",
                    }],
                }
                with self.assertRaisesRegex(gate.GateFailure, "drifted"):
                    gate.verify_snapshot_binding(binding)
            finally:
                gate.PROJECT_ROOT = old_root

    def test_current_immutable_baselines_have_zero_drift(self) -> None:
        result = gate.verify_immutable_baselines()
        self.assertEqual(21, result["frozenV1"]["filesChecked"])
        self.assertEqual(0, result["frozenV1"]["mismatches"])
        self.assertEqual(91, result["authorityV124"]["fileCount"])
        self.assertEqual(347, result["integratedCandidateV124"]["fileCount"])

    def test_latest_append_only_receipt_is_self_consistent(self) -> None:
        roots = sorted((PROJECT_ROOT / "canary" / "settings-control-quality").glob("settings-control-quality-*") )
        self.assertTrue(roots)
        receipt_path = roots[-1] / "quality-receipt.json"
        index_path = roots[-1] / "readable-index.md"
        receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
        self.assertEqual("knowledge-clipper-settings-control-quality-receipt.v1", receipt["schemaVersion"])
        self.assertTrue(receipt["status"].startswith("NO-GO"))
        self.assertEqual(0, receipt["secretScan"]["findingCount"])
        self.assertEqual(0, receipt["immutableBaselines"]["frozenV1"]["mismatches"])
        self.assertIn("当前 NO-GO 边界", index_path.read_text(encoding="utf-8"))
        if receipt["ownerEvidence"]["status"] == "native-owner-evidence-loaded":
            self.assertEqual(0, receipt["ownerEvidence"]["secretScan"]["findingCount"])
            self.assertEqual(receipt["ownerEvidence"]["desktop"], receipt["modules"]["desktop"]["status"])
            for name in ("core", "content", "remnote", "speech", "imageOcr", "anki"):
                self.assertTrue(receipt["modules"][name]["status"].startswith("PASS"), name)
            self.assertEqual("NO-GO-pending-new-authority-checkpoint", receipt["modules"]["remnote"]["authorityAdmission"])

    def test_current_native_owner_evidence_is_independently_scoped(self) -> None:
        log = PROJECT_ROOT / "tests" / "Quality.Gates" / "test_settings_control_plane_quality.py"
        safe = {
            "imageOcr": {"status": "passed", "tests": 48, "log": gate.evidence(log), "logAbsolutePath": str(log)},
            "anki": {"status": "passed", "tests": 43, "log": gate.evidence(log), "logAbsolutePath": str(log)},
        }
        owner, modules, paths = gate.verify_native_owner_evidence(safe)
        self.assertEqual("native-owner-evidence-loaded", owner["status"])
        self.assertEqual("PASS-MODULE", modules["desktop"]["status"])
        for name in ("desktop", "core", "content", "remnote", "speech", "imageOcr", "anki"):
            self.assertTrue(modules[name]["status"].startswith("PASS"), (name, modules[name]))
        if modules["core"]["sourceAdvanceAfterCheckpoint"]:
            self.assertIsNotNone(modules["core"]["immutableAdmissionReceipt"])
            self.assertIn("SOURCE-ADVANCED-AFTER-CHECKPOINT", modules["core"]["status"])
        if modules["content"]["sourceAdvanceAfterReceipt"]:
            self.assertIsNotNone(modules["content"]["immutableAdmissionReceipt"])
            self.assertIn("SOURCE-ADVANCED-AFTER-RECEIPT", modules["content"]["status"])
        self.assertEqual("NO-GO-pending-new-authority-checkpoint", modules["remnote"]["authorityAdmission"])
        self.assertGreater(len(paths), 20)

    def test_runtime_secret_scan_excludes_only_declared_invalid_fixtures(self) -> None:
        with tempfile.TemporaryDirectory(dir=PROJECT_ROOT) as temporary:
            root = Path(temporary)
            invalid = root / "contracts" / "fixtures" / "workflow-settings" / "invalid"
            invalid.mkdir(parents=True)
            (invalid / "secret-negative.json").write_text(json.dumps({"apiKey": "sk-negativefixture123456789"}), encoding="utf-8")
            (root / "runtime.json").write_text(json.dumps({"apiKey": "env:DEEPSEEK_API_KEY"}), encoding="utf-8")
            result = gate.scan_runtime_tree_for_secrets(root)
            self.assertEqual(0, result["findingCount"])
            self.assertEqual(1, result["excludedNegativeFixtureCount"])
            (root / "leaked-runtime.json").write_text(json.dumps({"apiKey": "sk-runtimeleak1234567890"}), encoding="utf-8")
            self.assertGreater(gate.scan_runtime_tree_for_secrets(root)["findingCount"], 0)


if __name__ == "__main__":
    unittest.main()
