# content-to-remnote.v1 consumer acceptance

This test belongs to the optional terminal target `export.remnote` in “零碎知识剪藏” (`KnowledgeClipper`). It does not implement the Core adapter and never calls RemNote. RemNote and Anki are peer export targets; neither is part of the upstream LearningBundle/CardBundle identity.

Accepted projections are intentionally narrower than the content-worker schemas:

- `learning-bundle` requires `schema_version`, `bundle_id`, `release_state=ready`, and ordered `items[]` with `item_id`, `state=ready`, and `cleaning.cleaned_text`.
- `card-bundle` requires `schema_version`, `bundle_id`, `release_state=ready`, and ordered `cards[]` with `card_id`, `front_blocks[]`, and `back_blocks[]`.
- Output IDs preserve the source `bundle_id`, `item_id`, and `card_id` exactly. Array order is preserved.
- RemNote cards are deterministically `cardType=basic`, `direction=forward`; front/back must equal the one source text/markdown block byte-for-byte after JSON decoding.
- Empty text, unsafe IDs, controls/newlines, reserved RemNote card delimiters, asset blocks, or multiple blocks cannot be represented losslessly by Exporter v1 and must fail before a projected artifact is published.
- A manifest whose kind alone is changed while retaining the content-worker payload must be rejected by the real Exporter entrypoint.
- When Core supplies `documentTitlePrefix + taskId`, the standard Exporter entrypoint replaces only that exact transient suffix with the input `bundleId`; the same bundle therefore routes to the same direct child document across task IDs. Explicit stable titles remain unchanged.
- The immutable source bundle payload and manifest must remain byte-identical after terminal projection and export.
- The content-worker fixture and schema keys must not contain RemNote/Anki routing fields. Target names inside user-authored text are content, not routing metadata.
- The canonical worker assembly is `KnowledgeClipper.RemNoteExporter.Worker.exe`; any `RemNoteQuickClipper.*` executable is tested only as a legacy compatibility alias.

The language extension gate is schema-driven and pins the immutable real Content writing/both authority Canary. It reconstructs the sentence body from ordered `source_segment_ids` using Content's locked rule: remove only the segment set's outer terminal CR/LF while preserving internal newlines and every other whitespace character. It then requires exactly one ordinal occurrence of `target_term` and reads pronunciation/meaning from either the reliable LearningBundle language-writing trace or the human-confirmed standard input whose SHA-256 is recorded in that LearningBundle. The front must equal the exact one-time replacement `（pronunciation；meaning）` with no remaining target term; the back must equal the original target term exactly. A zero/multiple occurrence or missing lossless evidence is rejected. `both` must contain exactly two distinct cards ordered `language_context`, then `language_writing`. The additive Core v2.1 mapping is exercised as ordinary basic/forward projection with byte-exact card order/text, no template/category/choice/token/target-term/mode leakage, and a successful offline Exporter ZIP. The Exporter performs no trim and never interprets upstream classification or choice.

Run from the project root:

```powershell
pwsh -File tests/RemNoteQuickClipper.RemNoteExporter.Tests/run-content-adapter-compatibility.ps1
```

The runner uses immutable real content-worker Canary payloads and writes an append-only receipt below `canary/rwa` (the short path also exercises the .NET Framework worker below Windows `MAX_PATH`).

The independent stateful Fake RemNote Canary exercises the same target policy through two complete `mcp-direct` standard-worker runs. It expects `created`, then `already_applied`, with one document whose title contains the bundle ID rather than either task ID:

```powershell
pwsh -File tests/RemNoteQuickClipper.RemNoteExporter.Tests/run-target-idempotency-canary.ps1
```
