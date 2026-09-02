# Language practice v2

## Frozen module contracts

- Input: `schemas/content-input.v2.schema.json`, `$id=standard-input/2.0.0`.
- Category registry: `config/categories.v2.json`, validated by `schemas/category-registry.v2.schema.json`.
- Decision output: `schemas/language-practice-decision.v1.schema.json`, `$id=language-practice-decision/1.0.0`.
- Existing `LearningBundle` and `CardBundle` remain at `1.0.0`; their destination-neutral semantics are unchanged.
- Common WorkerRequest, WorkerResult, and Artifact schemas remain the stable checkpoint v1 authority.

The v1 `language-en.md` and `language-zh.md` Prompt files remain byte-for-byte unchanged and define `meaning-reading` / `language_context`. Writing analysis is isolated in `stages/language-writing.md` plus the category-specific writing Prompt files.

## Operation-level choice

One choice applies to every language unit in the StandardInput:

```json
{
  "human_overrides": {
    "languagePracticeMode": "both"
  }
}
```

Allowed values are `meaning-reading`, `writing`, and `both`. A scoped item override remains available for exceptional replay compatibility, but the normal Core path writes the operation-level human override in a new `input_version` and starts a new append-only attempt.

## Two-stage behavior

1. If classification finds any language unit and no operation/item mode is present, the worker completes with immutable `learning-bundle` and `language-practice-decision` outputs. It does not publish a CardBundle.
2. A new input version supplies the selected mode. The next attempt runs from the original source and may publish the final CardBundle.

The decision is a real common artifact kind with its own manifest. Core owns any outer `waiting_input` workflow state; the content worker does not encode it in an ad-hoc payload.

## Card semantics

- `meaning-reading`: unchanged `language_context` card generation.
- `writing`: the worker requires exactly one literal `target_term` occurrence in the assigned source. It deterministically replaces that occurrence with `（实际读音；实际语境义）`; the back is the exact source `target_term`.
- `both`: exactly two cards per ready language unit, ordered `language_context` then `language_writing`.

AI writing analysis only supplies structured pronunciation/meaning evidence. It does not rewrite the source or author the card. If either field is missing or unreliable, or the target occurrence is not unique, the affected unit remains unresolved and gets a decision record. With a selected operation mode, other safe units may still publish a partial CardBundle; the unresolved unit is listed in `withheld_units` and is never represented as a successful card.

`LearningBundle.source_segments` 始终无损保存容器文本，包括终止 CR/LF。构造 `language_writing` front 时，只从该 unit 的 source segment 集合最外层剥离终止 CR/LF；不得 trim 尾空格、首部空白或内部换行。随后才校验正文中 `target_term` 恰好出现一次并替换。空白正文、多个 source segment 或正文中非唯一出现均进入 unresolved；back 仍为逐字 `target_term`。

Human-confirmed writing details use:

```json
{
  "human_overrides": {
    "languageWriting": {
      "units": {
        "unit-id": {
          "pronunciation": "confirmed pronunciation",
          "meaning": "confirmed contextual meaning",
          "confirmed": true
        }
      }
    }
  }
}
```

## Readable examples

Source:

> 他阅读完补充材料后，对这个结论仍然心存疑窦。

Writing card:

- Front: `他阅读完补充材料后，对这个结论仍然心存（yí dòu；疑问或疑惑）。`
- Back: `疑窦`

Source:

> The committee reached a tentative conclusion after reviewing the evidence.

Writing card:

- Front: `The committee reached a （/ˈtentətɪv/；暂定的） conclusion after reviewing the evidence.`
- Back: `tentative`
