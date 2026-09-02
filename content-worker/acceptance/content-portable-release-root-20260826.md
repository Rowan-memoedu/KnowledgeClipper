# Content v0.5.0 portable release-root acceptance

## Outcome

The canonical content entrypoint is now `workers/content/KnowledgeClipper.Content.Worker.exe`. The complete movable unit is the `workers/content` directory, not the launcher file alone.

The launcher resolves paths from `AppContext.BaseDirectory`:

- module runtime: `app`;
- bundled Node: `runtime/node-v24.18.0-win-x64/node.exe`;
- environment overrides remain optional compatibility controls;
- source-tree and historical release-root discovery remain fallback only.

Legacy `RemNoteQuickClipper.Content.Worker.*` files remain present as compatibility artifacts but are explicitly excluded from the new authority manifest.

## Canonical tree

```text
workers/content/
├── KnowledgeClipper.Content.Worker.exe
├── release-manifest.v1.json
├── app/
│   ├── package.json
│   ├── config/categories.v2.json
│   ├── dist/src/*.js
│   └── prompts/**/*.md
└── runtime/node-v24.18.0-win-x64/node.exe
```

The launcher is a self-contained .NET 8 single-file executable. The manifest binds 27 payload files totaling 160,184,502 bytes with SHA-256; independent recomputation found 0 mismatches.

Entrypoint:

- bytes: 67,490,988;
- SHA-256: `F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8`.

Release manifest SHA-256: `033CB2B0F9FD4A842DFAB347EACDDBDDBD11F7E5A4146B83A931556441F1BD6D`.

## Black-box validation

The canonical EXE was invoked from `C:\Windows` with:

- `PATH` empty;
- `KNOWLEDGE_CLIPPER_CONTENT_WORKER_ROOT` absent;
- `KNOWLEDGE_CLIPPER_NODE_PATH` absent;
- legacy `RQC_NODE_PATH` absent.

`describe --json` exited 0 and passed frozen `worker-describe.schema.json`:

- `workerId=content.deepseek`;
- `workerVersion=0.5.0`;
- `stages=[content.understand-and-card]`;
- output kinds include `question.answer` and `language-practice-decision`.

Frozen worker-describe v1 has `additionalProperties=false` and no `operations` field. The three supported operations are therefore bound without contract drift in `release-manifest.v1.json`: `understand-and-card`, `question.answer`, `question.make-cards`.

An absolute request copied from the original `offline-question-answer-v3` input was then executed from the same hostile working-directory/environment setup. It exited 0 with `completed`, publishing LearningBundle + `question.answer`. No result or output was copied from the earlier Canary.

Canary receipt:

`content-worker/canary/release-root/20260826T0532Z-content-v0.5.0-portable/canary-receipt.json`

SHA-256: `D878E699767993BC3106D5372964BF1C959ED21C4FE6E56315229425E9BAC672`

## Gates

- `npm test`: 58/58 PASS.
- .NET build/publish: 0 warnings, 0 errors.
- descriptor/common request/common result/common artifacts/module input/module outputs: PASS.
- frozen common checkpoint: 21 files checked, 0 mismatch.
- DeepSeek/API/exporter/Desktop calls: 0.

Registry publication and whole-product release assembly remain controller-owned. The module now supplies a hash-bound, neutral canonical tree that those layers can consume without the legacy entrypoint.
