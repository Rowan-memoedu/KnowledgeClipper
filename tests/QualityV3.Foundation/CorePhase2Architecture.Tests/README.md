# Core Phase 2 architecture gates

Quality owns these five independent gates. They do not consume an owner test receipt as proof and must run only against the source snapshot and Host built from an immutable Core Phase 2 release packet.

1. Production Host defaults to a non-fake runtime. `AuthorityBoundFakeWorkerRuntime` is reachable only through the explicit `authority-bound-fake-test-only` injection switch; runtime metrics must distinguish production from fake test mode.
2. A fake Worker delayed beyond the SDK request timeout must not delay command acceptance. The typed call returns an operation id, and a newly constructed client observes the durable projection to completion.
3. Identical content submitted by different commands/sessions must have distinct artifact identities. Exact make-cards replay must preserve operation/artifact identity and create zero artifacts, Worker attempts, or Anki writes.
4. The test kills its own Host after a durable follow-up session claim and before Worker completion. Restart must project the interrupted operation to an explicit terminal failure and allow a new legal follow-up in the same session.
5. Public question projections expose the typed `AnswerArtifact`, `CardBundleArtifact`, and `DestinationEvidenceArtifact` fields for the answer, card-bundle, and destination-evidence roles instead of a generic artifact list. Accepted `OperationSubmission` and `QuestionOperationSubmission` values must not expose a generic `Artifacts` list. The Kernel maps by artifact kind and rejects missing or duplicate required kinds. A Client-DLL-only external consumer must compile against the three named roles, while a negative consumer must fail to compile when accessing raw reference, kind, or path data.

The test assembly name intentionally matches the existing Core Phase 2 friend-assembly boundary only to inspect durable event streams and fake metrics. Test source, assertions, execution, and receipts remain Quality-owned. Production workers are never invoked by the behavior gates; only the production Host startup and runtime-mode query are exercised.

Until an immutable Phase 2 packet is supplied, the status is `READY-AWAITING-PHASE2-PACKET`, not PASS.

The next packet that contains `WorkerProcessRuntime` has an additional mandatory gate in `../CoreRealRuntimeUnknownState.Tests/gate-contract.v1.json`. It is not retroactively counted as a sixth result for the already frozen Phase 2 packet.
