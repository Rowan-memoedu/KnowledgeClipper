from __future__ import annotations

import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT / "tools"))

import verify_core_authority_assembler as gate  # noqa: E402


class CoreAuthorityAssemblerGateTests(unittest.TestCase):
    def test_current_source_is_checkpoint_driven_and_safe(self) -> None:
        result = gate.inspect_source()
        self.assertTrue(result["sourceReady"])
        self.assertEqual([], result["missingParameters"])
        self.assertEqual([], result["staleLiterals"])
        self.assertTrue(all(result["invariants"].values()))

    def test_legacy_hardcoded_assembler_is_rejected(self) -> None:
        text = gate.DEFAULT_TOOL.read_text(encoding="utf-8-sig")
        text += "\n$coreContracts = 'contracts/core/v2.3/playback-control'\n$ipcProtocolVersion = '1.1'\n$receiptSchemaVersion = '2.3.0'\n"
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "legacy.ps1"
            path.write_text(text, encoding="utf-8")
            result = gate.inspect_source(path)
        self.assertFalse(result["sourceReady"])
        self.assertEqual({"hardcoded-core-v2.3", "hardcoded-ipc-1.1", "hardcoded-receipt-2.3.0"}, set(result["staleLiterals"]))

    def test_source_pass_does_not_authorize_assembly(self) -> None:
        result = gate.evaluate()
        self.assertEqual("CONDITIONAL-ASSEMBLER-SOURCE-READY-PREFLIGHT-PENDING", result["status"])
        self.assertFalse(result["releaseDecision"]["authorityAssemblyAllowed"])
        self.assertFalse(result["releaseDecision"]["candidateAssemblyAllowed"])

    def test_formal_qualification_is_independently_accepted(self) -> None:
        result = gate.evaluate(qualification_receipt=gate.DEFAULT_QUALIFICATION)
        self.assertEqual("PASS-ASSEMBLER-QUALIFIED-AUTHORITY-ASSEMBLY-NOT-RUN", result["status"])
        self.assertTrue(result["qualification"]["validated"])
        self.assertTrue(result["releaseDecision"]["authorityAssemblyAllowed"])
        self.assertFalse(result["releaseDecision"]["candidateAssemblyAllowed"])

    def test_qualification_cannot_hide_external_or_release_side_effects(self) -> None:
        receipt = gate.common.load(gate.DEFAULT_QUALIFICATION)
        receipt["qualification"]["externalServicesCalled"] = 1
        with tempfile.TemporaryDirectory(dir=PROJECT / "tests/Quality.Gates") as temp:
            path = Path(temp) / "drift.json"
            path.write_text(json.dumps(receipt), encoding="utf-8")
            with self.assertRaises(RuntimeError):
                gate.verify_qualification(path)

    def test_preflight_must_bind_exact_checkpoint_and_ipc(self) -> None:
        checkpoint = gate.common.load(gate.DEFAULT_CHECKPOINT)
        receipt = {
            "schemaVersion": "knowledge-clipper-core-authority-tool-preflight.v1",
            "status": "passed",
            "checkpointVersion": checkpoint["checkpointVersion"],
            "checkpointSha256": gate.common.sha256_file(gate.DEFAULT_CHECKPOINT),
            "boundFiles": len(checkpoint["fileBindings"]),
            "ipc": {
                "protocolVersion": checkpoint["authority"]["ipc"]["protocolVersion"],
                "schemaVersion": checkpoint["authority"]["ipc"]["schemaVersion"],
                "requestContract": checkpoint["authority"]["ipc"]["request"],
                "receiptContract": checkpoint["authority"]["ipc"]["receipt"],
            },
        }
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "preflight.json"
            path.write_text(json.dumps(receipt), encoding="utf-8")
            self.assertTrue(gate.verify_preflight(path)["validated"])
            drift = copy.deepcopy(receipt)
            drift["ipc"]["protocolVersion"] = "1.1"
            path.write_text(json.dumps(drift), encoding="utf-8")
            with self.assertRaises(RuntimeError):
                gate.verify_preflight(path)


if __name__ == "__main__":
    unittest.main()
