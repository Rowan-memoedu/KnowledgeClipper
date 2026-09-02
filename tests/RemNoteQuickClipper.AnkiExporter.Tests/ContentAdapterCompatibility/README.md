# content-to-anki.v1 consumer acceptance

This acceptance belongs to `export.anki`. It invokes the repository's real
`StageInputResolver`/`content-to-anki.v1` adapter and the planned
`export.anki` 1.1.4 `KnowledgeClipper.AnkiExporter.Worker.exe` in `validate`
mode. It never connects to Anki. The existing test directory retains
`RemNoteQuickClipper` only as an explicit legacy compatibility path, not as an
authoritative product identity.

Inputs are the immutable baseline CardBundle and LearningBundle under
`content-worker/fixtures/projection-v1`, the fixed Content language authority
`20260826T041418150Z-offline-language-practice-v2-replay-e7deb47e`, and the
current Core mapping snapshot. The standard no-argument script validates the
authority receipt, artifact manifests, and CardBundle/LearningBundle payload
SHA-256 values before projection; it has no optional fixture override.
Because the real CardBundle deliberately contains one deferred-media card, the
suite uses three views without modifying the source fixture:

- a generated non-media view (only the `media_qa` card removed) must project,
  preserve field text, map every route/note type, produce worker-compatible
  `content_hash` values, retain ancestry, and pass Exporter validate-only;
- the unmodified full CardBundle must reject `media_qa` with
  `binding.anki_media_deferred` before publishing an adapter artifact;
- a generated view that maps the media card through `basic_qa` while retaining
  its `asset_ref` blocks must reject the asset itself with the same code and no
  adapter artifact.

Run from the project root:

```powershell
pwsh -File tests/RemNoteQuickClipper.AnkiExporter.Tests/run-content-adapter-compatibility.ps1
```

Every run writes a unique append-only receipt and a `readable-samples.md`
generated from that run's actual source bundle, projected bundle, mapping
snapshot, and Worker Result under `workers/anki-exporter/canary/ca/`.
