# Core v3 contracts

This directory is an additive major-line contract root. It is not part of the v1.2.41 authority.

- `protocol/` freezes the current command, query, event, and wire envelopes.
- `artifacts/` freezes the v2 artifact envelope and reference.
- `capabilities/` contains the authority-bound admitted catalog and its schema.
- `compatibility/` documents the only boundary allowed to interpret legacy IPC.
- `operations/` contains operation-specific command contracts. Transport versions do not select operations.
