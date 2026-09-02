# Core real-runtime unknown-state gate

This is an additional mandatory gate for the next immutable packet that contains `WorkerProcessRuntime`. It is separate from the historical five-gate Phase 2 packet runner because that frozen packet did not contain a real process runtime.

The gate must use only packet-bound source, binaries, authority trees, and fixtures. A Quality-owned fake process invoker may emulate worker termination and write `result.json`; no real Anki, DeepSeek, RemNote, MinerU, speaker, or user process may be invoked.

The executable test must cover all cases in `gate-contract.v1.json`:

1. An Anki export worker result with status `unknown_external_state` becomes the durable Core event `operation.unknown-external-state` and a typed `Degraded` projection. It must never be converted by a generic catch into `operation.failed` / `operation_failed`.
2. An Anki export process that was dispatched and then crashes, times out, is cancelled, or loses `result.json` has the same unknown-state outcome.
3. Restart, exact replay, and a new write command cause zero further process invocations, artifacts, or external-write attempts. Exact replay preserves the original operation id; a new write remains blocked until an explicit reconcile transition has occurred.
4. The equivalent process/result failures for read-only Anki probe and content work become ordinary `operation.failed`, not unknown external state. A read-only question session is explicitly aborted/reopened and remains reusable.
5. The runtime and Kernel share one canonical typed unknown-state exception or result channel; source-level duplicate exception types are a failure even if their names match.
6. The published production Host executable and its argument parser must reject `authority-bound-fake-test-only`. Fake runtime construction may exist only in a test assembly or through direct test composition that is unreachable from the production Host. The candidate inventory must not contain a TestHost executable or a production-Host Fake switch.
7. Side-effect accounting must report `confirmedWrites`, `unknownWriteAttempts`, and `dispatchAttempts` separately. A lost response after dispatch may have `confirmedWrites = 0`, but must also report unknown state and `possibleWrite = true`; it must never be summarized as `externalWrites = 0` or otherwise treated as proof that no write occurred.
8. Anki readiness is scoped to the requested action. When the selected Canary route, required models, and permission are ready but optional routes are missing, a worker-wide `needs_initialization` result must project as `readyForRequestedAction = true`, `partially_ready`, with typed `nonBlockingIssues`. Desktop must consume only that typed SDK projection, must not read worker JSON or report a global failure, and must not infer roles or readiness through `Sequence`, `Single`, or array position.

The final test receipt must report each scenario separately, include before/after invocation and write counters, and preserve the exact terminal event and typed projection state. Any missing scenario is `FAIL`, never conditional PASS.
