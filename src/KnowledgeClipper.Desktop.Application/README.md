# Desktop v3 Application boundary

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
normalized ports
              |
              v
one NamedPipeCoreGateway -> typed CoreV3Client SDK

Legacy WinForms -> LegacyDesktopAdapter -> existing v2 hosts/readers
```

[`LegacyDesktopAdapter`](../LegacyDesktopAdapter.cs) is outside this directory and must not be referenced by the v3 Application layer. Its `CreateReadToAnkiApplication` composition path now supplies one `NamedPipeCoreGateway` for the typed read/clip/question and draft ports, plus Windows capture and local support ports. The legacy controller path still exists for compatibility; it is not a fallback when a v3 command is unavailable. Existing settings hosts and file readers outside this layer must not be mistaken for Application-owned transport or business logic.

## Read-to-Anki vertical slice seam

`ReadToAnkiUseCases` owns only Desktop interaction semantics: one current-text capture, one immutable capture-text publication through `IReadToAnkiInputArtifactPort`, and one typed Core use-case submission. Playback and the background content/card/export pipeline are projected as independent typed state axes, so natural playback completion does not imply that the Anki write completed. Language practice resumes only after `WaitingUserChoice`; replay calls only the playback method.

`IReadToAnkiPort` is implemented by [`NamedPipeCoreGateway`](../KnowledgeClipper.Desktop.Adapters/NamedPipeCoreGateway.cs), outside this layer. Its `SdkDomainFacade.ReadToAnkiAsync` calls the public typed SDK's `ReadAloudCardsAsync` or `ClipCardsAsync` according to `includeSpeech`; the gateway also maps language choice, card resolution, draft confirmation/cancellation, replay, and typed projections. [`DesktopCompositionRoot`](DesktopCompositionRoot.cs) registers Clip and Read with `StartAsync(false)` and `StartAsync(true)` respectively, through the same command binding.

[`ReadPlaybackUseCases`](ReadPlaybackUseCases.cs) observes playback separately from the card pipeline. Stop uses the captured operation/action/attempt target through `IReadPlaybackControlPort` and [`PlaybackControlSdkBridge`](../KnowledgeClipper.Desktop.Adapters/PlaybackControlSdkBridge.cs); accepted stop is not proof of stopped playback. Only matching terminal evidence establishes stopped/naturally completed state. Replay does not resubmit capture, Content, or export. No IPC message, worker request, artifact path, authority lookup, log parsing, or legacy controller fallback is permitted in this Application layer.

## Question and card preview

`QuestionUseCases` and [`QuestionObserver`](QuestionObserver.cs) are bound to the same gateway. `SubmitQuestionAsync`, `MakeCardsAsync`, and `GetQuestionAsync` map initial question, follow-up, make-cards, answer artifacts, and draft projections to typed SDK operations. [`QuestionSessionSdkBridge`](../KnowledgeClipper.Desktop.Adapters/QuestionSessionSdkBridge.cs) owns the adapter-side session/configuration binding, not the Application layer.

[`FlashcardDraftPreviewUseCases`](FlashcardDraftPreview.cs) handles the shared draft interaction; composition connects question/read draft projections and confirmation observation. A visible draft or an accepted confirmation is not itself evidence of an Anki write. The current read/clip adapter uses `AnkiWriteAuthorizationBuilder.ForIsolatedCanaryDeck`; typed wiring is not a claim of unrestricted destination authorization or universal capability availability.

## Owned source

- `ApplicationContracts.cs`: normalized DTOs with no IPC/schema/authority/path identity.
- `ApplicationPorts.cs`: task command/query, module action, stage control, artifact, settings, credential, and window activation ports.
- `DesktopStateStore.cs`: immutable feature state and reducers.
- `UseCases.cs`: module action, settings, and question-session orchestration.
- `ReadToAnkiUseCases.cs` / `ReadPlaybackUseCases.cs`: read/clip submission, independent pipeline/playback observation, and targeted playback control.
- `FlashcardDraftPreview.cs`: draft projection, user interaction, and confirmation/cancellation state.
- `QuestionObserver.cs`: Application-owned typed query observation with bounded backoff, timeout, cancellation, and terminal failure reduction.
- `CommandBinding.cs`: one command path for pointer, keyboard, and UI Automation.
- `ViewBinding.cs`: normalized view-to-command binding; no alternate business dispatch path.
- `CaptureSession.cs`: freezes foreground HWND, focused element/runtime ID, selection, and clipboard sequence before UI interaction.
- `DesktopCompositionRoot.cs`: the only v3 Application composition root.

## Typed SDK boundary and remaining unavailable surfaces

The normalized port surface covers:

1. submit initial/follow-up question and make cards;
2. query recent task and question projection;
3. execute admitted module action;
4. request playback stage control;
5. read and publish typed artifacts by opaque `ArtifactHandle`;
6. load/save workflow settings snapshots;
7. describe/store local credential metadata/value without task leakage.

The Core SDK owns protocol selection, schema validation, operation/subject/idempotency fields, target tokens, authority/checkpoint binding, command roots, artifact paths, hashes, and durable receipts. Desktop uses one `NamedPipeCoreGateway` over the public typed SDK façade. It must not reference internal protocol types, add a `RunV7`, or add an IPC-version branch. Pointer, keyboard, and UI Automation continue through `UnifiedApplicationCommandBinding`; they must not acquire separate command implementations.

Not every normalized port has a production implementation. In the current gateway, `ExecuteAsync` dispatches only Anki probe with a published configuration artifact; `ObserveAnkiReadinessAsync` still returns `Unknown` / `core-v3.typed-readiness-unavailable`. Other module actions fail closed. The older task-id-only `RequestPlaybackStopAsync` remains unavailable; it is not the current target-bound read playback control path. Local settings/credential/window support is composed outside this directory. Missing capability/configuration or unsupported projections must remain explicit, without a legacy fallback or invented success.

### Historical foundation snapshot

`20260902T060054Z-foundation.2` was the Phase 1 SDK/catalog baseline, when the read adapter had not yet been bound and Anki/question capability availability was restricted. Those statements describe that historical snapshot only, not the current read/clip/question source wiring. A sealed candidate's actual SDK, catalog, and module bindings are determined by its own immutable manifest and admission evidence; this source README does not revise any historical candidate or checkpoint.

## Source-level acceptance coverage

[`tests/KnowledgeClipper.Desktop.Application.Tests`](../../tests/KnowledgeClipper.Desktop.Application.Tests) contains fake-port coverage for normalized workflows, command sources, capture context, and boundary dependencies, including [`PlaybackStopTests`](../../tests/KnowledgeClipper.Desktop.Application.Tests/PlaybackStopTests.cs) and [`ReplayIdentityTests`](../../tests/KnowledgeClipper.Desktop.Application.Tests/ReplayIdentityTests.cs). Phase 1 fake Anki/RemNote/module tests do not establish live capability availability. Source coverage, candidate-bound UI evidence, and real provider/destination evidence are separate acceptance layers; this documentation-only update reruns none of them.
