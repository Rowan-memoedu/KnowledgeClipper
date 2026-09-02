# Desktop v3 Application boundary (Phase 1)

This directory is the transport-neutral Desktop Application layer. It owns normalized commands, use cases, reducers, state, and capture-session identity. It does not own WinForms rendering, Named Pipe framing, authority discovery, schema locations, worker identities, or artifact paths.

## Dependency direction

```text
WinForms / UI Automation / keyboard
              |
              v
UnifiedApplicationCommandBinding
              |
              v
Application use cases -> DesktopStateStore/reducers
              |
              v
normalized ports (8) <- future generated Core SDK adapter
                           ^
                           |
              one NamedPipeCoreGateway only

Legacy WinForms -> LegacyDesktopAdapter -> existing v2 hosts/readers
```

`LegacyDesktopAdapter` is outside this directory. It is a temporary compatibility boundary for the existing UI and must not be referenced by the v3 Application layer. Existing `DesktopAnkiProbeHost`, `DesktopContentSettingsHost`, file readers, and versioned Named Pipe code remain behind it until the generated Core SDK is available.

## Owned source

- `ApplicationContracts.cs`: normalized DTOs with no IPC/schema/authority/path identity.
- `ApplicationPorts.cs`: task command/query, module action, stage control, artifact, settings, credential, and window activation ports.
- `DesktopStateStore.cs`: immutable feature state and reducers.
- `UseCases.cs`: module action, settings, and question-session orchestration.
- `QuestionObserver.cs`: Application-owned typed query observation with bounded backoff, timeout, cancellation, and terminal failure reduction.
- `CommandBinding.cs`: one command path for pointer, keyboard, and UI Automation.
- `CaptureSession.cs`: freezes foreground HWND, focused element/runtime ID, selection, and clipboard sequence before UI interaction.
- `DesktopCompositionRoot.cs`: the only v3 Application composition root.

## Required generated Core SDK surface

The Desktop adapter requires normalized methods equivalent to:

1. submit initial/follow-up question and make cards;
2. query recent task and question projection;
3. execute admitted module action;
4. request playback stage control;
5. read and publish typed artifacts by opaque `ArtifactHandle`;
6. load/save workflow settings snapshots;
7. describe/store local credential metadata/value without task leakage.

The generated SDK owns protocol selection, schema validation, operation/subject/idempotency fields, target tokens, authority/checkpoint binding, command roots, artifact paths, hashes, and durable receipts. Desktop implements exactly one `NamedPipeCoreGateway` over the public typed SDK façade from the immutable `20260902T060054Z-foundation.2` snapshot. It does not reference the SDK's internal protocol types, add a `RunV7`, or add an IPC-version branch. Anki/question remain typed unavailable in this foundation catalog and therefore fail closed without a legacy fallback.

## Phase 1 acceptance

`tests/KnowledgeClipper.Desktop.Application.Tests` uses fake normalized gateways to prove Anki probe, RemNote discovery, initial question, follow-up, make-cards, settings, artifact answer display, unified invocation sources, and frozen capture context. It also scans the boundary for legacy/transport dependencies.
