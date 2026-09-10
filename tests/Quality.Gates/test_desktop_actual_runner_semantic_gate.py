import hashlib
import json
import os
import subprocess
import unittest
from pathlib import Path


PROJECT = Path(r"D:\Projects\RemNote Quick Clipper")
FIXTURE = PROJECT / "tests/Quality.Gates/fixtures/desktop-actual-runner-semantic/cases.v1.json"
RUNNER_RELEASE = Path(os.environ.get(
    "KC_ACTUAL_RUNNER_RELEASE",
    r"D:\Artifacts\KnowledgeClipper\read-to-anki-runner\actual-desktop-flow-runner-v5-20260904T160533Z",
))
MANIFEST = RUNNER_RELEASE / "runner-release-manifest.v5.json"
LEGACY_CANDIDATE_VERIFIER = PROJECT / "tools/verify_desktop_read_to_anki_candidate.py"
DESKTOP_PACKAGER = PROJECT / "tools/New-DesktopReadToAnkiCandidate.ps1"


def sha(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest().upper()


class DesktopActualRunnerSemanticGate(unittest.TestCase):
    def test_quality_fixture_covers_locked_semantics(self):
        document = json.loads(FIXTURE.read_text(encoding="utf-8"))
        cases = {item["id"]: item for item in document["cases"]}
        self.assertEqual(
            set(cases),
            {
                "no-card-resolution",
                "card-resolution-required",
                "delayed-completion",
                "stale-command-terminal",
                "count-correct-content-wrong",
                "wrong-destination-before-confirm",
            },
        )
        self.assertEqual(cases["no-card-resolution"]["expected"]["resolutionActions"], 0)
        self.assertEqual(cases["card-resolution-required"]["expected"]["resolutionActions"], 1)
        self.assertFalse(cases["stale-command-terminal"]["expected"]["accepted"])
        self.assertFalse(cases["count-correct-content-wrong"]["expected"]["accepted"])
        self.assertEqual(cases["wrong-destination-before-confirm"]["expected"]["confirmActions"], 0)
        self.assertEqual(cases["wrong-destination-before-confirm"]["expected"]["ankiWrites"], 0)

    def test_release_manifest_is_complete_self_contained_closure(self):
        manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
        self.assertEqual(manifest["runtime"]["model"], "self-contained")
        self.assertEqual(manifest["runtime"]["rid"], "win-x64")
        self.assertFalse(manifest["runtime"]["singleFile"])
        rows = manifest["files"]
        self.assertEqual(len(rows), manifest["payload"]["files"])
        self.assertEqual(sum(row["bytes"] for row in rows), manifest["payload"]["bytes"])
        for row in rows:
            path = RUNNER_RELEASE / Path(row["path"])
            self.assertTrue(path.is_file(), row["path"])
            self.assertEqual(path.stat().st_size, row["bytes"], row["path"])
            self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest().upper(), row["sha256"], row["path"])

    def test_runner_black_box_declares_action_bound_semantic_observation(self):
        manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
        runner = RUNNER_RELEASE / manifest["entrypoint"]
        result = subprocess.run([str(runner), "--describe"], cwd=runner.parent, capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        descriptor = json.loads(result.stdout)
        required = {
            "BeginReadToAnkiDiscoveryAsync",
            "WaitForAcceptedReadToAnkiAsync",
            "WaitReadAloudCardsActionAsync",
            "ResolveCardResolutionAsync",
            "ConfirmFlashcardDraftAsync",
        }
        self.assertTrue(required.issubset(set(descriptor["sdkSequence"])))
        self.assertEqual(descriptor.get("semanticFixtureVersion"), "desktop-actual-runner-semantic-cases.v1")
        self.assertEqual(descriptor.get("ankiReadback"), "exact-note-card-front-back-deck-ids")
        self.assertEqual(descriptor.get("postConfirmPlaybackWait"), "typed-until-idle-replayable")

    def test_runner_self_test_proves_all_locked_semantic_cases(self):
        manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
        runner = RUNNER_RELEASE / manifest["entrypoint"]
        result = subprocess.run([str(runner), "--self-test"], cwd=runner.parent, capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        receipt = json.loads(result.stdout.strip().splitlines()[-1])
        self.assertEqual(receipt.get("semanticCases"), 6)
        self.assertEqual(receipt.get("semanticAcceptedCases"), 3)
        self.assertEqual(receipt.get("staleCommandRejected"), True)
        self.assertEqual(receipt.get("contentMismatchRejected"), True)
        self.assertEqual(receipt.get("wrongDestinationRejectedBeforeConfirm"), True)
        self.assertEqual(receipt.get("negativeAnkiWrites"), 0)
        self.assertEqual(receipt.get("delayedPlaybackCompletionAfterCards"), True)

    def test_current_actual_readback_cannot_be_satisfied_by_historical_core_evidence(self):
        source = LEGACY_CANDIDATE_VERIFIER.read_text(encoding="utf-8")
        historical = '"anki-confirmed-semantic-readback", "status": "PASS-CORE-REAL-ALREADY-CURRENT"'
        if historical in source:
            self.assertIn('"historicalDependencyOnly": True', source)
            self.assertIn('"currentActualSemanticReadback": "REQUIRED"', source)
        self.assertIn("actualActionCommandId", source)
        self.assertIn("previewFrontBackSha256", source)
        self.assertIn("ankiNoteIds", source)
        self.assertIn("ankiCardIds", source)
        self.assertIn("exactFrontBackDeckReadback", source)

    def test_desktop_packager_consumes_v5_release_manifest_recursively(self):
        source = DESKTOP_PACKAGER.read_text(encoding="utf-8-sig")
        self.assertIn("RunnerReleaseRoot", source)
        self.assertIn("runner-release-manifest.v5.json", source)
        self.assertIn("knowledge-clipper-actual-runner-release.v5", source)
        self.assertIn("$runnerReleaseManifest.files", source)
        self.assertIn("runnerReleaseManifest", source)
        self.assertNotIn("runner-manifest.v4.json", source)


if __name__ == "__main__":
    unittest.main()
