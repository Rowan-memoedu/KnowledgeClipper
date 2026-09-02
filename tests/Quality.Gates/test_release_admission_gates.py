from __future__ import annotations

import copy
import json
import subprocess
import sys
import tempfile
import unittest
import uuid
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "tools"))

import release_admission_gates as admission  # noqa: E402
import brand_identity_gates as branding  # noqa: E402
from assemble_release_preseal import inventory  # noqa: E402
from quality_gates import sha256_file  # noqa: E402


class ReleaseAdmissionGateTests(unittest.TestCase):
    def test_product_identity_fixtures_and_current_legacy_no_go(self) -> None:
        valid = branding.load_json(admission.QUALITY_ROOT / "fixtures/product-identity/valid/knowledge-clipper.json")
        branding.validate_identity_document(valid)
        invalid = branding.load_json(admission.QUALITY_ROOT / "fixtures/product-identity/invalid/remnote-centered-primary.json")
        with self.assertRaises(branding.BrandGateFailure):
            branding.validate_identity_document(invalid)
        audit = branding.audit_current_legacy_release()
        self.assertEqual("migration-required-no-go", audit["status"])
        self.assertEqual("KnowledgeClipper", audit["lockedIdentity"]["asciiRoot"])
        self.assertTrue(audit["historicalRelease"]["immutable"])
        self.assertIsNone(audit["historicalRelease"]["candidateHash"])

    def test_legacy_release_and_build_producers_are_hard_blocked(self) -> None:
        with self.assertRaisesRegex(branding.BrandGateFailure, "disabled for new outputs"):
            branding.block_legacy_release_builder("fixture-builder")
        for relative in (
            "tools/assemble_release_preseal.py",
            "tools/promote_release_preseal.py",
            "tools/promote_release_preseal_increment.py",
            "tools/build_desktop_quality_snapshot.py",
            "tools/build_orchestrator_quality_snapshot.py",
        ):
            source = (PROJECT_ROOT / relative).read_text(encoding="utf-8")
            self.assertIn("block_legacy_release_builder", source, relative)
            version = f"quality-brand-guard-{uuid.uuid4().hex}"
            with tempfile.TemporaryDirectory(prefix="quality-brand-receipt-") as temporary:
                receipt = Path(temporary) / "receipt.json"
                completed = subprocess.run(
                    [sys.executable, str(PROJECT_ROOT / relative), "--version", version, "--receipt", str(receipt)],
                    cwd=PROJECT_ROOT,
                    capture_output=True,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                    check=False,
                )
                self.assertNotEqual(0, completed.returncode, relative)
                self.assertIn("disabled for new outputs", completed.stderr, relative)
                self.assertFalse(receipt.exists(), relative)
                self.assertFalse((PROJECT_ROOT / "releases" / version).exists(), relative)

    def test_quality_owned_schema_fixtures_accept_valid_and_reject_non_authority(self) -> None:
        cases = [
            (
                "authority-registry-checkpoint.schema.json",
                "fixtures/authority-registry-checkpoint/valid/authority-five-workers.json",
                True,
            ),
            (
                "authority-registry-checkpoint.schema.json",
                "fixtures/authority-registry-checkpoint/invalid/candidate-not-authority.json",
                False,
            ),
            (
                "preseal-chain-run.schema.json",
                "fixtures/preseal-chain-run/valid/image-fake.json",
                True,
            ),
            (
                "preseal-chain-run.schema.json",
                "fixtures/preseal-chain-run/invalid/external-api-invoked.json",
                False,
            ),
            (
                "core-authority-checkpoint.schema.json",
                "fixtures/core-authority-checkpoint/invalid/old-registry-modified.json",
                False,
            ),
        ]
        for schema, relative, should_pass in cases:
            document = admission.load_json(admission.QUALITY_ROOT / relative)
            if should_pass:
                admission.validate_document(schema, document)
            else:
                with self.assertRaises(admission.AdmissionFailure):
                    admission.validate_document(schema, document)

    def test_current_state_is_ready_to_gate_but_not_ready_to_seal(self) -> None:
        result = admission.current_readiness(PROJECT_ROOT)
        self.assertEqual("authority-source-accepted-brand-migration-required", result["status"])
        self.assertEqual("NO-GO", result["releaseDecision"])
        self.assertEqual(21, result["stableContracts"]["filesChecked"])
        self.assertEqual(0, result["stableContracts"]["mismatches"])
        self.assertEqual(0, result["frozenAuthorityRegistry"]["workerCount"])
        self.assertFalse(result["frozenAuthorityRegistry"]["modified"])
        self.assertEqual(
            [
                "contracts/authority/worker-registry/v1.1.0-20260826T023054Z/checkpoint.json",
                "contracts/authority/worker-registry/v1.2.2-20260828T012500Z/checkpoint.json",
                "contracts/authority/worker-registry/v1.2.3-20260828T021300Z/checkpoint.json",
                "contracts/authority/worker-registry/v1.2.4-20260828T032500Z/checkpoint.json",
            ],
            result["authorityCheckpointCandidates"],
        )
        self.assertEqual("passed-authority-source-with-replacement-dispatcher", result["authoritySourceGate"]["status"])
        self.assertEqual("migration-required-no-go", result["brandIdentityGate"]["status"])
        self.assertEqual("new-neutral-checkpoint-required", result["authorityRegistryGate"])
        self.assertEqual("blocked-until-brand-migration", result["candidateBoundChainGate"]["status"])
        self.assertEqual("fixture-ready-implementation-pending", result["languageChoiceCardGate"]["status"])
        self.assertEqual(4, len(result["moduleOnboarding"]["brandReadyWorkerIds"]))
        self.assertEqual(["speech.edge"], result["moduleOnboarding"]["brandPendingWorkerIds"])
        self.assertEqual(
            "observed-neutral-binary-awaiting-module-receipt",
            result["orchestratorBrandMigration"]["status"],
        )
        self.assertEqual(
            "AC513DE82F9C55FF4C87DCA0B84BC9153A4B4F637766D8C8189BB5617C04C978",
            result["latestDesktopUiEvidence"]["receiptSha256"],
        )
        self.assertEqual(243, result["latestDesktopUiEvidence"]["packageFilesChecked"])
        self.assertEqual(72, result["latestDesktopUiEvidence"]["windowsUiAssertions"])
        self.assertEqual("passed-KnowledgeClipper", result["latestDesktopUiEvidence"]["brandSurface"])
        self.assertEqual("pending", result["latestDesktopUiEvidence"]["physicalUserExperience"])
        self.assertFalse(result["latestDesktopUiEvidence"]["candidateBound"])
        self.assertEqual(
            "superseded-awaiting-inline-textbox-windows-canary",
            result["desktopUiModuleCanary"]["status"],
        )
        self.assertEqual(244, result["desktopUiModuleCanary"]["desktopFilesChecked"])
        self.assertEqual(0, result["desktopUiModuleCanary"]["hiddenWrites"])
        self.assertFalse(result["desktopUiModuleCanary"]["userHandoffAllowed"])
        self.assertIsNone(result["desktopUiModuleCanary"]["candidateHash"])
        self.assertIsNone(result["releaseManifest"])
        self.assertIsNone(result["candidateHash"])
        self.assertFalse(result["externalProductionApisInvoked"])

    def test_candidate_registry_fixture_cannot_masquerade_as_authority_checkpoint(self) -> None:
        candidate = admission.load_json(
            admission.QUALITY_ROOT
            / "fixtures/authority-registry-checkpoint/invalid/candidate-not-authority.json"
        )
        self.assertFalse(candidate["registry"]["authority"])
        with self.assertRaisesRegex(admission.AdmissionFailure, "registry/authority"):
            admission.validate_document("authority-registry-checkpoint.schema.json", candidate)

    def test_exact_five_worker_offline_chain_is_accepted_and_wrong_mode_is_rejected(self) -> None:
        releases_root = PROJECT_ROOT / "releases"
        releases_root.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix=".quality-admission-", dir=releases_root) as temporary:
            temporary_root = Path(temporary)
            release_root = temporary_root / "win-x64"
            release_root.mkdir()
            result_template = admission.load_json(
                PROJECT_ROOT / "contracts/v1/fixtures/common/worker-result/valid/completed.json"
            )
            receipt_paths: list[Path] = []
            for index, (worker_id, mode) in enumerate(admission.EXPECTED_CHAIN_MODES.items(), start=1):
                entrypoint = admission.quality_gates.KNOWLEDGE_CLIPPER_EXPECTED_WORKERS[worker_id]["entrypoint"]
                executable = release_root / entrypoint
                executable.parent.mkdir(parents=True, exist_ok=True)
                executable.write_bytes(f"fixture-{worker_id}".encode("utf-8"))

                result = copy.deepcopy(result_template)
                result["worker"]["id"] = worker_id
                result["worker"]["version"] = "1.0.0"
                result["requestId"] = f"request-{index}"
                result["operationId"] = f"operation-{index}"
                result["taskId"] = f"task-{index}"
                result["runId"] = f"run-{index}"
                result["attemptId"] = f"attempt-{index}"
                result_path = temporary_root / f"result-{index}.json"
                result_path.write_text(json.dumps(result), encoding="utf-8")

                receipt_path = temporary_root / f"chain-{index}.json"
                receipt_paths.append(receipt_path)
                receipt_path.write_text("{}", encoding="utf-8")

            files, total_bytes, tree_sha256 = inventory(release_root)
            state = {
                "status": "unsealed-no-go",
                "releaseRoot": admission.relative(release_root),
                "layout": {"files": files, "bytes": total_bytes, "postGateTreeSha256": tree_sha256},
                "releaseManifest": None,
                "candidateHash": None,
            }
            state_path = temporary_root / "state.json"
            state_path.write_text(json.dumps(state), encoding="utf-8")
            state_sha256 = sha256_file(state_path)

            for index, (worker_id, mode) in enumerate(admission.EXPECTED_CHAIN_MODES.items(), start=1):
                entrypoint = admission.quality_gates.KNOWLEDGE_CLIPPER_EXPECTED_WORKERS[worker_id]["entrypoint"]
                executable = release_root / entrypoint
                result_path = temporary_root / f"result-{index}.json"
                receipt = {
                    "schemaVersion": "preseal-chain-run.v1",
                    "runId": f"chain-{index}",
                    "createdAt": "2026-08-26T03:00:00Z",
                    "status": "passed",
                    "workerId": worker_id,
                    "mode": mode,
                    "releaseBinding": {
                        "releaseRoot": admission.relative(release_root),
                        "stateReceiptSha256": state_sha256,
                        "presealTreeSha256": tree_sha256,
                    },
                    "entrypoint": {"path": entrypoint, "sha256": sha256_file(executable)},
                    "result": {
                        "path": admission.relative(result_path),
                        "sha256": sha256_file(result_path),
                        "commonStatus": "completed",
                        "outputs": 1,
                    },
                    "checks": {
                        "describePassed": True,
                        "requestSchemaValid": True,
                        "resultSchemaValid": True,
                        "noHiddenWrites": True,
                    },
                    "externalProductionApisInvoked": False,
                }
                receipt_paths[index - 1].write_text(json.dumps(receipt), encoding="utf-8")

            passed = admission.validate_chain_set(receipt_paths, release_root, state_path)
            self.assertEqual("passed", passed["status"])
            self.assertEqual(5, len(passed["workers"]))
            self.assertTrue(passed["releaseRootUnchanged"])
            self.assertFalse(passed["externalProductionApisInvoked"])

            wrong = admission.load_json(receipt_paths[0])
            wrong["mode"] = "validate-only"
            receipt_paths[0].write_text(json.dumps(wrong), encoding="utf-8")
            with self.assertRaisesRegex(admission.AdmissionFailure, "wrong chain mode"):
                admission.validate_chain_set(receipt_paths, release_root, state_path)

    def test_sealing_is_fail_closed_without_both_same_tree_passes(self) -> None:
        current_root = PROJECT_ROOT / admission.CURRENT_RELEASE_ROOT_RELATIVE
        with self.assertRaisesRegex(admission.AdmissionFailure, "must both pass"):
            admission.assert_sealable({"status": "passed-authority"}, {"status": "pending"}, current_root)
        with self.assertRaisesRegex(admission.AdmissionFailure, "same unchanged release tree"):
            admission.assert_sealable(
                {
                    "status": "passed-authority",
                    "releaseRoot": admission.CURRENT_RELEASE_ROOT_RELATIVE.as_posix(),
                    "presealTreeSha256": "1" * 64,
                    "stateReceiptSha256": "2" * 64,
                },
                {
                    "status": "passed",
                    "releaseRoot": admission.CURRENT_RELEASE_ROOT_RELATIVE.as_posix(),
                    "presealTreeSha256": "3" * 64,
                    "stateReceiptSha256": "2" * 64,
                },
                current_root,
            )

    def test_verify_cli_is_fail_closed_when_checkpoint_is_missing(self) -> None:
        completed = subprocess.run(
            [
                sys.executable,
                str(PROJECT_ROOT / "tools/release_admission_gates.py"),
                "verify",
                "--authority-checkpoint",
                "receipts/does-not-exist.json",
                "--release-root",
                admission.CURRENT_RELEASE_ROOT_RELATIVE.as_posix(),
                "--state-receipt",
                admission.CURRENT_STATE_RECEIPT_RELATIVE.as_posix(),
                "--chain-receipt",
                "receipts/does-not-exist-chain.json",
                "--receipt",
                "receipts/should-not-be-created.json",
            ],
            cwd=PROJECT_ROOT,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            check=False,
        )
        self.assertNotEqual(0, completed.returncode)
        self.assertFalse((PROJECT_ROOT / "receipts/should-not-be-created.json").exists())


if __name__ == "__main__":
    unittest.main(verbosity=2)
