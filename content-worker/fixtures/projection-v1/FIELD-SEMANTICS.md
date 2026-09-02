# Content projection fixture v1

This directory is the content worker's self-contained compatibility fixture for
`content-to-remnote.v1` and `content-to-anki.v1`. It does not redefine the
common contracts and does not authorize either exporter to infer missing
configuration.

## Authoritative module schemas

- `../../schemas/learning-bundle.v1.schema.json`
  - `$id`: `learning-bundle/1.0.0`
  - instance: `learning-bundle.schema-v1.json`
- `../../schemas/card-bundle.v1.schema.json`
  - `$id`: `card-bundle/1.0.0`
  - instance: `card-bundle.schema-v1.json`

The JSON Schemas freeze the top-level content-module artifact envelope. Their
nested objects are intentionally open in v1. The companion validator
`../../test/validate_projection_fixtures.py` applies the current runtime types
from `../../src/types.ts` plus cross-file provenance rules; exporter code should
not mistake the shallow nested JSON Schema for permission to accept arbitrary
nested fields.

## Fixture coverage

The LearningBundle contains six ready items:

1. Chinese contextual vocabulary (`language_zh`), with `question` and
   `supplied_answer` omitted.
2. English contextual vocabulary (`language_en`), with the same optional fields
   omitted.
3. A question whose source answer is missing (`question_answer`), with
   `supplied_answer` and `target_term` omitted and generated answer provenance
   made explicit.
4. A question/answer media pair, with empty source segments and generated
   content, and with optional `cleaned_text`, `question`, `supplied_answer`, and
   `target_term` omitted.
5. A `general_learning` transfer-strategy unit using `concept_transfer`; its
   answer is an exact source span and only the recall question wording is
   registered as generated content.
6. A `general_learning` worked-procedure unit using `procedure`, with the same
   source-answer/generated-question boundary.

The CardBundle covers every template currently allowed by the open category
registry and every block kind currently emitted by the worker:

- `language_context`: `markdown` source front plus generated `text` back;
- `basic_qa`: source `text` front plus generated `text` back;
- `media_qa`: source `asset_ref` front and back.
- `concept_transfer`: generated recall wording plus a source-traceable answer;
- `procedure`: generated recall wording plus a source-traceable procedure.

The coverage check derives the category and template sets from
`../../config/categories.v1.json`; it does not keep a second hard-coded list.

It also deliberately contains empty `withheld_units`, an empty
`source_segment_ids` list on the media card, and no empty-string substitute for
any omitted optional field.

## Field semantics

### Lossless projection

The following values can be carried into a downstream intermediate record
without interpretation or generation:

- Bundle lineage: `package_id`, `task_id`, `attempt_id`, bundle IDs, and the
  LearningBundle path/SHA-256 pair.
- Card identity and grouping: `card_id`, `item_id`, `unit_id`,
  `variant_group_id`.
- Content classification: `category_id`, `subtype`, `template`.
- Ordered front/back blocks, including `block_id`, `type`, `text` or
  `material_path`, and `provenance`.
- `answer_provenance` and `source_segment_ids`.
- Learning source references, exact source segments, cleaning state, unit
  states, answer/context/risk states, generated content, unresolved reasons,
  withheld units, and gates. These remain audit/provenance data even when an
  exporter does not render them.

`ai_trace` is an audit record, not card content. `withheld_units` describes
content that must not be silently exported as cards.

### Required downstream configuration

The content artifacts intentionally do not decide destination behavior.

For RemNote, the downstream profile must provide at least:

- destination knowledge base/document/parent routing;
- the mapping from `template` to RemNote card direction/type and any power-up;
- block rendering and media insertion policy;
- optional tags and document organization policy.

For Anki, the downstream profile must provide at least:

- `deck_route_key` resolution;
- `note_type_key` and the mapping from ordered blocks to named note fields;
- card revision/update policy and optional tags;
- stable media filename/ID policy and content-hash calculation;
- active Anki/AnkiConnect target settings.

These values cannot be recovered from LearningBundle/CardBundle without making
a product decision. They must never be synthesized from category names or
machine-local defaults.

### Explicit failure conditions

A strict converter must fail the affected conversion instead of inventing data
when any of the following occurs:

- the LearningBundle file is missing, its SHA-256 differs, or bundle lineage
  (`package_id`, `task_id`, `attempt_id`) does not match;
- a card references a missing item, unit, or source segment, or duplicates any
  card/block/item/unit/segment identifier;
- `category_id` or `subtype` differs between a card and its referenced unit;
- a template, block type, block provenance, answer provenance, state, or enum
  value is unknown;
- a `markdown`/`text` block has no non-empty `text`, an `asset_ref` block has no
  non-empty `material_path`, or a block carries the wrong alternative field;
- a card has no front or no back;
- a source block cannot be traced to its referenced source segment (presentation
  markers such as `**` and `__` may be stripped for the trace check);
- generated block text is not present exactly in the referenced unit's
  `generated_content`;
- an asset path is absent from the LearningItem source references, the file is
  missing, or its content hash differs;
- a unit/card is unresolved or withheld, but export is attempted as if ready;
- required RemNote routing/template/direction configuration or required Anki
  deck/note-type/field/media configuration is absent.

An empty array means a known empty collection. An omitted optional field means
“not supplied/not applicable under the other state fields”; it must not be
converted to an empty string or guessed value. A malformed required value is an
error, not equivalent to omission.

## Validation

From the content worker directory, run:

```powershell
python .\test\validate_projection_fixtures.py
```

The validator first applies both existing JSON Schemas and then applies strict
nested, referential, source-hash, source-span, and provenance validation.
