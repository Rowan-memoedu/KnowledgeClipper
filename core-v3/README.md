# KnowledgeClipper Core v3 foundation

This is an additive major-line foundation. It does not participate in the v2.10.6 build or the v1.2.41 authority.

Dependency direction:

1. `Contracts` contains the current wire, command, query, event, artifact, and capability DTOs.
2. `Kernel` contains application behavior and the Transport, CompatibilityGateway, Operations, Artifacts, Capabilities, and WorkerRuntime boundaries.
3. `Client` is the typed .NET client intended for Desktop consumption after an explicit integration handoff.
4. `Host` is the isolated Core v3 process used by foundation canaries.
5. `tests` and `tools` are Core-owned qualification and checkpoint utilities.

Legacy IPC versions are interpreted only inside `Kernel/CompatibilityGateway`. Application and operation handlers consume only current internal DTOs.
