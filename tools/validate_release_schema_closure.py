#!/usr/bin/env python3
"""Validate that every local JSON Schema reference is portable within a release root."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
from urllib.parse import unquote


def iter_refs(value):
    if isinstance(value, dict):
        ref = value.get("$ref")
        if isinstance(ref, str):
            yield ref
        for child in value.values():
            yield from iter_refs(child)
    elif isinstance(value, list):
        for child in value:
            yield from iter_refs(child)


def resolve_pointer(document, fragment: str):
    if not fragment:
        return
    if not fragment.startswith("/"):
        raise ValueError(f"unsupported non-pointer fragment: #{fragment}")
    current = document
    for encoded in fragment[1:].split("/"):
        token = unquote(encoded).replace("~1", "/").replace("~0", "~")
        if isinstance(current, list):
            current = current[int(token)]
        elif isinstance(current, dict) and token in current:
            current = current[token]
        else:
            raise KeyError(f"missing JSON pointer token: {token}")


def native_path(path: Path) -> str:
    value = str(path)
    if os.name == "nt" and not value.startswith("\\\\?\\"):
        return "\\\\?\\" + value
    return value


def read_document(path: Path):
    with open(native_path(path), "r", encoding="utf-8-sig") as stream:
        return json.load(stream)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--release-root", required=True)
    parser.add_argument("--output")
    args = parser.parse_args()

    release_root = Path(args.release_root).resolve()
    contracts_root = (release_root / "contracts").resolve()
    schemas = sorted(contracts_root.rglob("*.schema.json"))
    failures = []
    refs_checked = 0
    documents = {}

    for schema_path in schemas:
        try:
            documents[schema_path] = read_document(schema_path)
        except Exception as exc:  # parse failure is part of the portable gate
            failures.append({"schema": schema_path.relative_to(release_root).as_posix(), "error": f"invalid-json: {exc}"})

    for schema_path, document in list(documents.items()):
        for ref in iter_refs(document):
            if ref.startswith(("http://", "https://")):
                continue
            refs_checked += 1
            file_part, _, fragment = ref.partition("#")
            target_path = schema_path if not file_part else (schema_path.parent / unquote(file_part)).resolve()
            try:
                target_path.relative_to(release_root)
            except ValueError:
                failures.append({"schema": schema_path.relative_to(release_root).as_posix(), "ref": ref, "error": "reference-escapes-release-root"})
                continue
            if not os.path.isfile(native_path(target_path)):
                failures.append({"schema": schema_path.relative_to(release_root).as_posix(), "ref": ref, "error": "referenced-file-missing"})
                continue
            try:
                target_document = documents.get(target_path)
                if target_document is None:
                    target_document = read_document(target_path)
                    documents[target_path] = target_document
                resolve_pointer(target_document, unquote(fragment))
            except Exception as exc:
                failures.append({"schema": schema_path.relative_to(release_root).as_posix(), "ref": ref, "error": f"fragment-invalid: {exc}"})

    receipt = {
        "schemaVersion": "knowledge-clipper-portable-schema-closure-gate.v1",
        "status": "passed" if not failures else "failed",
        "releaseRoot": ".",
        "schemasChecked": len(schemas),
        "localRefsChecked": refs_checked,
        "failures": failures,
    }
    if args.output:
        output = Path(args.output).resolve()
        if output.exists():
            raise FileExistsError(f"append-only output already exists: {output}")
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(receipt, ensure_ascii=False, separators=(",", ":")))
    return 0 if not failures else 1


if __name__ == "__main__":
    raise SystemExit(main())
