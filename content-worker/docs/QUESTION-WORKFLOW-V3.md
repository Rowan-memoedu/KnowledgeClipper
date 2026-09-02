# Explicit question workflow v3

## Module contracts

- Input: `schemas/content-input.v3.schema.json`, `$id=standard-input/3.0.0`.
- Answer artifact: `schemas/question-answer.v1.schema.json`, `$id=question-answer/1.0.0`.
- Open Prompt: `prompts/stages/question-answer.md`.
- Common WorkerRequest, WorkerResult and Artifact remain frozen v1 authority; Core owns outer waiting/input state.

`question_workflow` is an additive source discriminant. Existing v1/v2 text, conversation and media behavior is unchanged.

## Operations

### `question.answer`

The input contains contiguous ordered `questionTurns` and the persisted answer prefix in `answerAncestry`. The worker verifies turn indexes, parent answer links, question hashes, answer hashes and artifact references before invoking the configured provider. A successful attempt appends exactly one generated answer and publishes:

1. `learning-bundle` for source/provenance audit;
2. destination-neutral `question.answer` containing the complete ordered answer chain.

It never publishes a CardBundle. UI wrappers, copyright/copy prompts, non-plain answer content or an unsafe/unanswerable provider decision remain unresolved and publish LearningBundle only.

### `question.make-cards`

The input must contain one persisted answer for every ordered question turn. Optional `cardStructureRef` must be a path declared by the common input Artifact with matching hash. The worker uses the complete chain and existing card quality/provenance gates, then publishes LearningBundle + CardBundle. It does not publish a new answer artifact.

## Fail-closed boundaries

- empty/blank question;
- non-contiguous or duplicate turn index;
- missing/extra ancestry, broken parent link or mismatched question/answer hash;
- undeclared or hash-mismatched `cardStructureRef`;
- provider output containing UI/copyright noise or no safe plain-text answer.

Structural JSON Schema validation cannot express every cross-record ancestry invariant. Those constraints are enforced by the runtime contract loader and covered by the negative fixtures listed below.

## Replay fixtures

- completed: `offline-question-answer-v3`, `offline-question-followup-v3`, `offline-question-make-cards-v3`;
- structural reject: `offline-question-empty-v3`;
- semantic runtime reject: `offline-question-ancestry-missing-v3`, `offline-question-turn-order-invalid-v3`;
- completed unresolved: `offline-question-answer-noise-v3`, `offline-question-answer-unresolved-v3`.

Run `npm test` for runtime behavior and `npm run test:question-fixtures` for the explicit eight-fixture schema expectation matrix.
