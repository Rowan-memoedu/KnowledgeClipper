# OCR human-resolution v1 fixtures

These fixtures are synthetic, source-neutral and offline. Tests first run the existing `offline-ocr-ambiguous` input to create a frozen `ocr-proofread-bundle/1.0.0`, then construct a new immutable `standard-input/5.0.0` attempt.

- `accept-original.provider-replay.v1.json`: the user confirms the unresolved literal `MODL` is intended.
- `user-correction.provider-replay.v1.json`: the user explicitly inserts `E`, producing `MODEL`.
- `commands/*.json`: Core resume-command positive and negative payload examples. Core owns `targetToken`, task state, ancestry, destinations and new-attempt creation; Content owns only the module-local payload and v5 snapshot validation.

No fixture contains a production oracle, credential, destination field, RemNote/Anki call, or external connection.
