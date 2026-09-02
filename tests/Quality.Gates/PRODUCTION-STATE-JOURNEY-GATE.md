# Production-state journey gate

This Quality-owned gate separates component evidence from candidate promotion.

Evidence levels, in ascending scope, are `unit-contract`, `synthetic-component`, `production-shape`, `sealed-candidate-process-stateful`, and `external-live`. External-live evidence is independent and does not replace the sealed candidate process-stateful layer. Only the latter may enable `userHandTestEntrypoint=true`.

The source and fixture baseline is read-only:

```powershell
python tools/verify_production_state_journeys.py
python -m unittest discover -s tests/Quality.Gates -p 'test_production_state_journey_gate.py' -v
```

An append-only baseline receipt is created with:

```powershell
python tools/verify_production_state_journeys.py --output-root canary/quality-production-state-journey/<new-id>
```

A future candidate is evaluated with:

```powershell
python tools/verify_production_state_journeys.py --candidate-evidence <candidate-stateful-evidence.json>
```

The candidate evidence must point to the exact sealed release tree, its manifest, the production `KnowledgeClipper.exe`, the embedded candidate Orchestrator, Named Pipe logs, a real isolated persistence store, the production unified artifact reader, process-cleanup evidence, and three append-only journey receipts. Fixture-only documents, injected hosts, shared test Core DLLs, manual display state, and synthetic UI logs cannot promote a candidate.
