# Desktop Automation Contract v1

`contracts/desktop-automation/v1/` is the authoritative desktop-owned contract surface for RemNote Quick Clipper desktop interaction and capture.

Version: `1.0.0`

This directory owns:

- stable desktop `automationId` values;
- stable semantic desktop actions and desktop state snapshots;
- deterministic capture, screenshot, and orchestrator fake scenarios;
- `capture-bundle.v1`, including ordered `questionImages[]` and `answerImages[]` artifact references.

It does not own or copy the common task manifest, task command, command receipt, task status, task query, task query result, IPC envelope, worker result, or `orchestrator-ipc.v1` contracts. The orchestrator is the only Named Pipe server and persistent-state owner. The desktop adapter maps semantic actions to immutable task packages and common command/query envelopes by reference to stable checkpoint `contracts-v1-20260825T115938Z`.

Functional acceptance must invoke semantic actions and assert semantic state or immutable capture bundles. Screen coordinates and preview screenshots are not functional substitutes. Visual snapshots remain a separate DPI and layout QA layer.

The semantic test port is test-only and uses injected providers. A separate integration fixture uses a uniquely named local fake pipe server to exercise the real desktop Named Pipe client; neither route invokes OCR, AI, TTS synthesis, RemNote, or Anki.

`capture-bundle.v1` cross-field rules are normative even where JSON Schema cannot express them: every artifact reference resolves to exactly one artifact, artifact references are unique, `questionImages[]` and `answerImages[]` are disjoint ordered arrays, every member is an image artifact, screenshot intent has at least one question image, and the answer array may be empty. `CaptureBundleValidator` is the desktop runtime enforcement for these rules.
