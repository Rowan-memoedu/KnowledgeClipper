# Settings v2 → task snapshot → resolved plan → WorkerRequest

`knowledge-clipper-workflow-settings.v2` is additive. Existing v1 profiles and v1 task snapshots remain readable and unchanged.

## Image/OCR

```json
{
  "imageOcr": {
    "schemaVersion": "image-ocr-settings.v1",
    "providerSettings": {
      "modelVersion": "pipeline",
      "language": "en",
      "enableTable": false,
      "enableFormula": true,
      "timeoutSeconds": 120,
      "pollIntervalSeconds": 3
    }
  }
}
```

The values are frozen at `taskSettingsSnapshot.modules.imageOcr.providerSettings`, copied to `image-ocr.configSnapshot.providerSettings`, then copied unchanged to the Image/OCR `WorkerRequest.configSnapshot.providerSettings`. A raw MinerU token is never accepted; process-only credential injection remains outside the task package.

## Content resources

```json
{
  "content": {
    "configArtifactRef": "content-config-001",
    "expectedSha256": "<64 hex>",
    "settingsRevisionId": "revision-001"
  }
}
```

Core requires the exact `content-config-snapshot` artifact in `task.manifest.inputRefs`, verifies its manifest hash and entrypoint revision, freezes the reference in `taskSettingsSnapshot.modules.content`, and resolves:

- `PipelineStage.inputRefs += content-config-snapshot`
- `PipelineStage.configSnapshot.contentSettingsRevisionId = revision-001`
- `PipelineStage.payload.contentConfigPath = <artifact entrypoint>`

`StageInputResolver` preserves the config artifact as a direct Content `WorkerRequest.inputRef` while separately deriving the ordinary `content-input` artifact. Content 0.6.0 therefore receives both immutable inputs without treating configuration as source text.

## Existing routes

- RemNote learning: payload v2 `clip-daily-route`, with the Asia/Shanghai `localDate` frozen once.
- RemNote cards: payload v2 `{ "kind": "card-export" }`, with no date fields.
- Anki cards: one SHA-bound `anki-export-config` input plus `configSnapshot.ankiConfigArtifactId`.
- Speech: frozen voice/rate/volume/pitch/auto-play values reach synthesize/playback requests; `autoPlay=false` omits playback.
