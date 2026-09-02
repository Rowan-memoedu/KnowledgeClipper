# Content worker launcher identity

The authoritative launcher project is `KnowledgeClipper.Content.Worker.csproj`; its assembly and namespace root are `KnowledgeClipper`.

`RemNoteQuickClipper.Content.Worker.csproj` is retained only as a legacy compatibility alias for frozen release tooling and already-installed entrypoint paths. New registry and release checkpoints must select `KnowledgeClipper.Content.Worker.exe`.

The authoritative Node override is `KNOWLEDGE_CLIPPER_NODE_PATH`. `RQC_NODE_PATH` is read only as a legacy compatibility fallback.

`KNOWLEDGE_CLIPPER_CONTENT_WORKER_ROOT` may point the launcher at an unpacked content-worker root for isolated validation. When it is absent, the launcher uses the standard release layout.

The canonical portable layout is rooted at `workers/content`: the launcher is at the root, module assets are under `app`, and Node 24.18.0 is under `runtime/node-v24.18.0-win-x64`. Path discovery is based on `AppContext.BaseDirectory`, never the caller's current working directory. Source-tree and historical release-root discovery remain compatibility fallbacks only.
