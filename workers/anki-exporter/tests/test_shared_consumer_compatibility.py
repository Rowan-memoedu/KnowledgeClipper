import copy
import sys
import unittest
from pathlib import Path


WORKER = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(WORKER))

import shared_consumer_compatibility as gate


class SharedConsumerCompatibilityTests(unittest.TestCase):
    def descriptor(self):
        return {
            "workerId": "export.anki",
            "workerVersion": "1.2.1",
            "outputKinds": ["anki-export-evidence", "anki-connection-status"],
        }

    def test_descriptor_requires_both_outputs(self):
        gate.require_descriptor(self.descriptor(), version="1.2.1")
        bad = self.descriptor()
        bad["outputKinds"] = ["anki-export-evidence"]
        with self.assertRaisesRegex(ValueError, "both Anki output kinds"):
            gate.require_descriptor(bad, version="1.2.1")

    def test_probe_must_be_explicit_and_non_mutating(self):
        descriptor = {"tests": {"probeConnection": {
            "operation": "probe",
            "effectClass": "provider-read-only",
            "externalMutation": False,
            "executionPolicy": "explicit-user-action",
        }}}
        gate.require_explicit_probe(descriptor)
        automatic = copy.deepcopy(descriptor)
        automatic["tests"]["probeConnection"]["executionPolicy"] = "automatic-safe"
        with self.assertRaisesRegex(ValueError, "explicit-user-action"):
            gate.require_explicit_probe(automatic)

    def test_config_reference_must_bind_manifest_sha(self):
        digest = "a" * 64
        reference = {"artifactRef": "anki.config.a", "kind": "anki-export-config", "expectedSha256": digest}
        artifact = {"artifactId": "anki.config.a", "kind": "anki-export-config"}
        gate.require_sha_bound_config(reference, artifact, digest.upper())
        with self.assertRaisesRegex(ValueError, "SHA-256 mismatch"):
            gate.require_sha_bound_config(reference, artifact, "b" * 64)

    def test_desktop_anki_source_must_not_directly_connect(self):
        self.assertEqual([], gate.find_direct_network_tokens([(Path("safe.cs"), "var AnkiEndpoint = value;")]))
        findings = gate.find_direct_network_tokens([(Path("bad.cs"), "// Anki\nvar client = new HttpClient();")])
        self.assertEqual("HttpClient", findings[0]["token"])

    def test_registry_requires_planned_entrypoint_and_outputs(self):
        registry = {"workers": [{
            "workerId": "export.anki",
            "entrypoint": {"path": "workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe"},
            "outputKinds": ["anki-export-evidence", "anki-connection-status"],
        }]}
        gate.require_registry_entry(registry)
        registry["workers"][0]["entrypoint"]["path"] = "workers/anki-exporter/worker.py"
        with self.assertRaisesRegex(ValueError, "entrypoint mismatch"):
            gate.require_registry_entry(registry)


if __name__ == "__main__":
    unittest.main()
