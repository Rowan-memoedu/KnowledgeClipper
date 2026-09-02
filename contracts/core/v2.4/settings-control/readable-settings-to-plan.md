# Settings control v1 readable mapping

Public versions:

- workflow profile: `knowledge-clipper-workflow-settings.v1`
- one-task override: `knowledge-clipper-task-settings-override.v1`
- immutable task snapshot: `knowledge-clipper-task-settings-snapshot.v1`
- module adapter descriptor: `knowledge-clipper-module-settings-adapter.v1`

Precedence is per field: `taskOverride > activeProfile > moduleDefault`. The task snapshot contains only modules used by that task. The snapshot is published as a `task-settings-snapshot` artifact, SHA-256-bound from `task.manifest.v2.inputRefs`, and never re-read from the mutable profile during resolve, retry, recovery, or replay.

## Example: cards to both destinations

Profile input:

```json
{
  "workflow": { "processingProfile": "cards", "destinations": ["remnote", "anki"] },
  "capture": { "contextEnabled": true, "contextGranularity": "paragraph", "imageHandling": "ocr" },
  "speech": { "ratePercent": 7, "autoPlay": true },
  "remnote": {
    "schemaVersion": "remnote-destination-settings.v1",
    "enabled": true,
    "target": { "rootRemId": "<exact 剪藏 Rem ID>", "rootTitle": "剪藏" },
    "advanced": { "sink": "markdown-zip", "clipRouting": "clip-daily-route", "cardRouting": "card-export", "timeZone": "Asia/Shanghai", "dateFormat": "yyyy-MM-dd" }
  }
}
```

Resolved plan effects:

- `routing.processingProfile=cards`
- exporters `export-remnote` and `anki-export` are parallel after content
- RemNote WorkerRequest payload uses schema `2.0.0` and exact route `{"kind":"card-export"}`; no date property exists
- Anki `configSnapshot.ankiConfigArtifactId` points to the task's SHA-bound `anki-export-config` artifact
- capture fields must equal `capture-bundle.modeSnapshot`; context text is materialized into the content input artifact

## Example: learning clipping to RemNote

At a task freeze instant of `2026-08-27T16:30:00Z`, Core resolves Asia/Shanghai local date `2026-08-28` exactly once:

```json
{
  "schemaVersion": "2.0.0",
  "documentTitle": "Knowledge Clipper · task-example",
  "sink": "markdown-zip",
  "target": { "rootRemId": "<exact 剪藏 Rem ID>", "rootTitle": "剪藏" },
  "route": {
    "kind": "clip-daily-route",
    "timeZone": "Asia/Shanghai",
    "localDate": "2026-08-28",
    "dateFormat": "yyyy-MM-dd"
  }
}
```

The frozen route is copied into `PipelineStage.payload` and then into `WorkerRequest.payload`; `configSnapshot` carries no business routing or date.

## Supported and fail-closed boundaries

- no destination, RemNote, Anki, and dual destination are supported route selections
- Anki is conditional: cards only, with exactly one SHA-bound `anki-export-config` task artifact
- RemNote is conditional: exact non-null root target and an authority package containing exporter v2 are required
- `imageHandling=original` is recorded and verified, but image-understanding downstream is unsupported and fails closed
- prompt/provider selection, retention, retry, timeout, and diagnostics editing have no stable public effect adapter and remain read-only `unsupported`
- profiles and task snapshots prohibit credentials; only worker-owned environment values or SHA-bound task artifact references are allowed
