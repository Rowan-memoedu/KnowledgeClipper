# Destination-neutral artifact semantics

This document is the current authority for `LearningBundle` and `CardBundle`
projection semantics in the content module. Both artifacts describe learning
content, provenance, release state, and card structure. They do not select,
configure, or encode a downstream destination.

## Lossless fields

A downstream consumer may carry these fields without interpretation or new
content generation:

- bundle lineage and hashes;
- card, item, unit, block, and variant identifiers;
- category, subtype, and template identifiers;
- ordered front/back blocks and their provenance;
- source references and exact source segments;
- cleaning, answer, context, risk, release, unresolved, and withheld states;
- audit gates and AI trace records.

`ai_trace` is audit evidence rather than renderable card content.
`withheld_units` must never be silently converted into released cards.

## Consumer-owned configuration

The consumer must supply every destination-specific choice, including routing,
container or collection identity, template-to-native-type mapping, block and
media rendering, update policy, tagging, and credentials or connection state.
None of these values may be guessed from a category, template, local machine
default, or artifact path.

## Explicit failure boundary

A strict consumer must fail the affected conversion when integrity or lineage
does not match; referenced items, units, segments, blocks, or assets are
missing; an enum or template is unknown; a block lacks its required content;
source/generated provenance cannot be proven; unresolved or withheld content
is treated as ready; or required consumer configuration is absent.

An empty array is a known empty collection. An omitted optional field means
that the value was not supplied or does not apply under the other state fields;
it must not be converted to an empty string or synthesized.

## Versioning note

The earlier `fixtures/projection-v1/FIELD-SEMANTICS.md` is retained unchanged as
a receipt-pinned legacy downstream compatibility record. It is not the current
product or content-artifact authority.
