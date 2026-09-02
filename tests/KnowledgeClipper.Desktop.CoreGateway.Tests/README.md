# Desktop v3 Core Gateway replay

Run from the repository root:

```powershell
& 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe' run `
  --project 'tests\KnowledgeClipper.Desktop.CoreGateway.Tests\KnowledgeClipper.Desktop.CoreGateway.Tests.csproj' `
  --configuration Release
```

Expected terminal line:

```text
DESKTOP_CORE_GATEWAY_TESTS PASS assertions=15
```

The test binds only `core-v3/releases/20260902T060054Z-foundation.2/artifacts/net8.0/KnowledgeClipper.Core.V3.Client.dll`. `KnowledgeClipper.Core.V3.Contracts.dll` is copied only as a private runtime dependency and exports zero public types.

It verifies:

- current foundation Anki/question capability-unavailable notices fail closed with no legacy fallback;
- a fake typed Desktop journey covers initial question, follow-up, make-cards, answer artifact read, and Anki probe;
- exactly one `NamedPipeCoreGateway` exists;
- Gateway source has no low-level protocol DTO, schema/version, authority, artifact-path, mutable Core, or legacy host dependency;
- Client/Contracts/packet hashes match the immutable SDK release;
- Desktop project files contain no mutable `core-v3/src`, `bin/Release`, or Core `ProjectReference`.
