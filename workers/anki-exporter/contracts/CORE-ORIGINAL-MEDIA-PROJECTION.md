# Core → Anki ordered original-media projection candidate

This is an additive `export.anki` consumer contract candidate. It does not modify common v1, CaptureBundle v1, or CardBundle 1.0, and it does not activate the existing `media_qa/asset_ref` route.

Core may produce CardBundle 1.1 only after it has verified the authoritative CaptureBundle and every referenced image Artifact. Projection is deterministic:

1. Preserve `questionImages[]` order, then preserve `answerImages[]` order. The two arrays remain disjoint.
2. Copy each verified image into the derived card Artifact as a hashed file. Each `media[]` row uses the original immutable bytes, SHA-256, byte count and media type.
3. Emit one `media_ref` per source entry. Question rows use `role=questionImages`, `field=question`, `placement=append`, and zero-based contiguous `ordinal`. Answer rows use `role=answerImages`, `field=answer`, `placement=append`, and their own zero-based contiguous `ordinal`.
4. Emit all question rows before all answer rows. Do not encode legacy `{{media:...}}` tokens and do not translate Content `asset_ref` blocks through this candidate.
5. Use `note_type_key=original_image_qa` with `anki-export-config.v2` and `media_projection=ordered-original-images.v1`. Text in `fields.question` or `fields.answer` is preserved verbatim; an empty answer and an empty `answerImages[]` are both valid.
6. Ordered refs, roles, ordinals, media kinds and media SHA-256 values participate in `content_hash`. Reordering any image therefore requires a new revision/hash and cannot be mistaken for an idempotent replay.

The worker renders each role into its mapped field with exact markup `<img src="rqc_<sha256>.<extension>">`, separating existing text and subsequent images with `<br>`. It stores media by content-addressed filename, reads bytes back, rejects same-name/different-content collisions, and applies the existing global `RQC_ID`, revision conflict, readback and `unknown_external_state` rules.

Until Core publishes and binds its formal input contract, active Core resolver policy remains `reject-media-deferred`; the worker candidate is qualified only through validate-only and Fake AnkiConnect.
