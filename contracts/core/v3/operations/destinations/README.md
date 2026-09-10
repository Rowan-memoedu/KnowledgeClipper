# Confirmed card destinations — additive Core payloads

The wire command/event/artifact envelopes and operation IDs are unchanged. New SDK destination methods use `kc.destination-cards-command/1`; old SDK payload schema IDs and behavior remain unchanged. Old closed question body schemas are not relabelled to include new fields.

`ReadCardsAsync` and `StartDestinationQuestionAsync` freeze explicit zero/one/two unique destinations. An empty array is intentional, not legacy Anki fallback. Question follow-up/make-cards inherit the original snapshot. Confirmation binds the same draft token/revision/hash plus exact destination snapshot; each destination requires its own scope/target grant. No Content call is made by confirmation.

`GetFrozenDestinationsAsync(OperationId, SnapshotToken)` restores typed target identity, route key, configuration handle and verified historical RemNote binding from durable operation ownership and SHA-verified artifacts, never from current settings. It grants no write permission and proves no current service health.

Discovery/probe use the module's existing read-only public commands. Discovery persistence requires unique exact document title/type/id readback. Anki requires a frozen config artifact; RemNote-only and empty selections never create fake Anki configuration. RemNote cards consume `card.bundle` using payload v2 `route.kind=card-export`; dates are absent. Learning/date routing and original media are out of this increment.

Each destination has a durable started claim and terminal evidence. Recovered started-without-terminal is unknown with null write count. Earlier preview terminal cannot complete a later confirmation claim. No automatic retries of succeeded/unknown destinations. Aggregate partial/unknown is never green. RemNote worker receipts count logical documents separately from unknown low-level MCP mutation count.

Current qualification remains offline: handler/SDK tests, actual test-process crash recovery, request-package checks and exact release `describe`. These do not authorize live discovery/export or promotion of a sealed Desktop candidate.
