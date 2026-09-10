# KnowledgeClipper Core v3

Core v3 is the isolated production runtime used by the delivered Desktop read-to-Anki slice. It is not full feature parity with the legacy orchestrator or every worker present in the packaged registry. Production operation availability is determined by the candidate's bound capability catalog and `KernelComposition`, not by a worker's presence alone.

Dependency direction:

1. `Contracts` contains the current wire, command, query, event, artifact, and capability DTOs.
2. `Kernel` contains application behavior and the Transport, CompatibilityGateway, Operations, Artifacts, Capabilities, and WorkerRuntime boundaries.
3. `Client` is the typed .NET SDK consumed by the Desktop adapter for read-to-Anki, question, artifact, and playback operations.
4. `Host` is the isolated Core v3 production process selected by `candidate-integration.json`.
5. `tests` and `tools` are Core-owned qualification and checkpoint utilities.

Legacy IPC versions are interpreted only inside `Kernel/CompatibilityGateway`. Application and operation handlers consume only current internal DTOs.

Current delivered baseline (2026-09-09):

- Candidate: `D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-final-resize-speech-20260908T172000Z`. Its `candidate-integration.json` selects `core-v3/KnowledgeClipper.Core.V3.Host.exe` and `contracts/core/v3/capabilities/read-to-anki-capability-binding.v1.json`; `candidate-file-manifest.v1.json` binds the delivered files.
- Verified scope: `D:\Artifacts\KnowledgeClipper\read-to-anki\actual\desktop-playback-final-resize-speech-20260908T172600Z\actual-run.receipt.json` records the production Desktop Read/Stop, card confirmation, Anki readback, and replay flow. This is not verification of all project requirements or settings actions.
- The bound catalog includes question operations, but does not register OCR/human continuation, original-media export, or RemNote export. Their legacy/module implementations must not be described as available through this production slice without an actual route and corresponding evidence. Desktop settings also retain legacy hosts; inspect their entrypoints separately from the typed SDK.

Historical frozen contracts and authority artifacts remain compatibility evidence. Their version labels do not supersede the delivered candidate's runtime selection or expand its supported operations.
