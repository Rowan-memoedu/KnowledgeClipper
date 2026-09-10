# Read / Clip sequence test runner

This is a test-only option, not a Core command or product contract.

Add `--sequence-matrix --capture-fixture <absolute queue JSON path>` to the existing
authorized Actual invocation. Desktop remains the sole Actual launcher. One
Desktop process executes, in order:

1. Read / meaning-reading / one card.
2. Read / writing / one card.
3. Clip / both / two cards, no synthesis or playback.
4. Read / both / two cards.

The existing Desktop capture seam consumes exactly four explicitly supplied
inputs in `desktop-test-capture-queue.v1`: `{schemaVersion, inputs}`, each input
containing only `selectedText`, `prefix`, and `suffix`. No mode, classification,
answer, or result may be injected. The queue file is immutable for the run.
Its seam does not prove physical capture selection.

The runner sets and verifies custom Anki routes once, toggles context once, and
uses a fresh typed discovery boundary for every capture/action. It requires each
item's expected template/card count, exact source hashes, exact authorized deck,
preview equality, confirmation, independent notesInfo/cardsInfo readback, and
its own UI-restored event before the next capture. Clip expects NotRequested and
no audio artifacts; the shared UI log's READ prefix is not an audio assertion.

Any error stops the sequence. Prior committed output is retained. No automatic
retry, second Desktop process, extra confirm click, or replay is performed.
After the final item, only read-only discovery/projection and worker-request /
result ledger checks run. Expected totals are four exporter invocations, six
logical cards, three synthesis and three playback invocations; low-level API
mutation counts are not inferred from these totals.

The default invocation without `--sequence-matrix` retains the single Read/Both
path. Core, Content, authority, and the typed SDK remain unchanged.
