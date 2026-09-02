# Quality v3 foundation verification

The foundation test suite has four independent layers:

1. Canonical build manifest: re-hash the complete candidate payload, require exact entrypoints and contract files, and require fail-closed startup self-verification.
2. Private runner plan: require a per-run immutable snapshot, unique pipe/settings/jobs/temp roots, owned process-tree cleanup, global fail-fast only for common preflight, and independent collect-all business journeys.
3. UI and effects: correlate UIA invoke to typed command events; validate provider calls from an independent proxy log and explicit allowlists.
4. Vertical slices: validate a four-file metadata-entrypoint Anki read-only probe and a correlated question-to-Anki bounded-write journey.

Known regressions are permanent fixtures and must retain their exact reject codes:

| Fixture | Required rejection |
|---|---|
| UIA invoke without typed command | `ui.invoke-not-correlated-to-command` |
| Four-file artifact with exactly-one-file consumer | `artifact.legacy-single-file-consumer` |
| Rendered `writes=0` assertion | `ui.display-text-contract` |
| Runner emitter/verifier key mismatch | `runner.promotion-key-contradiction` |
| Candidate without startup self-verification | `build.startup-self-verification-missing` |

No fixture result is a claim about real Core, Desktop, Anki, RemNote, DeepSeek, or MinerU. Real conformance remains blocked until the v3 contracts/SDK and Desktop Application fake journey are supplied.
