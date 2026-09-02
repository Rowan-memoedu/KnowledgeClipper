# Quality v3 foundation

This package is the independent Quality control plane for the v3 architecture. It does not import Desktop, Core, worker, candidate, or historical release code.

## Authority boundaries

- A build is identified only by `quality-v3-canonical-build-manifest.v1`. The gate re-hashes every payload file and rejects undeclared files, missing entrypoints, dirty source identity, and absent startup self-verification.
- Every E2E run uses a new snapshot produced below `canary/quality-v3-runner-snapshots/`. Runtime execution imports the snapshot copy, never mutable `tools/` code.
- UI Automation proves only discoverability, native invoke, and action-id correlation. Typed Core events, projections, receipts, and ArtifactRefs decide business success.
- Provider effects are accepted only from an independent proxy call log with an explicit per-journey allowlist. Worker-reported counters are non-authoritative.
- A common integrity/process preflight may fail fast globally. Anki, RemNote, Question, OCR, and speech journeys fail independently and are aggregated with collect-all semantics.

## Commands

```powershell
python tests/QualityV3.Foundation/test_quality_v3_foundation.py
python tests/QualityV3.Foundation/quality_v3_gate.py build-manifest --candidate-root <root> --manifest <root>/canonical-build-manifest.v1.json --expected-commit <quality-observed-commit> --expected-source-snapshot-sha256 <quality-observed-source-tree>
python tools/create_quality_v3_runner_snapshot.py --snapshot-root <new-canary-path> --snapshot-id <unique-id>
```

The Anki probe and question-to-Anki validators are prepared but use fixture evidence only until Core publishes the v3 contracts/SDK and Desktop publishes the Application fake journey.
