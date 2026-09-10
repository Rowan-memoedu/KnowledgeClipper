from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path
from typing import Any


SHA256_RE = re.compile(r"^[0-9A-F]{64}$")
CANONICAL_ALGORITHM = (
    "sha256(utf8 ordinal(path TAB int64(bytes) TAB uppercase(sha256) LF))"
)
LEGACY_ALGORITHM = (
    "sha256(utf8 manifest-order(path TAB int64(bytes) TAB uppercase(sha256) LF))"
)
V2_SCHEMA = "kc.read-to-anki-candidate-manifest/2"
V3_SCHEMA = "kc.read-to-anki-candidate-manifest/3"
FIXTURE_SCHEMA = "kc.canonical-tree-hash-fixture/1"


def ordinal_utf16_key(value: str) -> bytes:
    return value.encode("utf-16-be", errors="surrogatepass")


def normalize(records: list[dict[str, Any]], *, ordinal_sort: bool) -> list[dict[str, Any]]:
    normalized: list[dict[str, Any]] = []
    paths: set[str] = set()
    for record in records:
        path = record.get("path")
        size = record.get("bytes")
        sha256 = record.get("sha256")
        if not isinstance(path, str) or not path.strip():
            raise ValueError("tree record path must be a non-empty string")
        if path in paths:
            raise ValueError(f"duplicate tree record path: {path}")
        paths.add(path)
        if isinstance(size, bool) or not isinstance(size, int) or size < 0:
            raise ValueError(f"tree record bytes must be a non-negative Int64: {path}")
        if size > 9223372036854775807:
            raise ValueError(f"tree record bytes exceeds Int64: {path}")
        if not isinstance(sha256, str):
            raise ValueError(f"tree record sha256 must be a string: {path}")
        sha256 = sha256.upper()
        if not SHA256_RE.fullmatch(sha256):
            raise ValueError(f"tree record sha256 is invalid: {path}")
        normalized.append({"path": path, "bytes": size, "sha256": sha256})
    if ordinal_sort:
        return sorted(normalized, key=lambda record: ordinal_utf16_key(record["path"]))
    return normalized


def calculate(records: list[dict[str, Any]], *, ordinal_sort: bool) -> dict[str, Any]:
    normalized = normalize(records, ordinal_sort=ordinal_sort)
    material = "".join(
        f'{record["path"]}\t{record["bytes"]}\t{record["sha256"]}\n'
        for record in normalized
    ).encode("utf-8")
    return {
        "algorithm": CANONICAL_ALGORITHM if ordinal_sort else LEGACY_ALGORITHM,
        "files": len(normalized),
        "bytes": sum(record["bytes"] for record in normalized),
        "treeSha256": hashlib.sha256(material).hexdigest().upper(),
    }


def require_declared_int(value: Any, field: str, *, legacy_float: bool = False) -> int:
    if isinstance(value, bool):
        raise ValueError(f"{field} must be an Int64")
    if isinstance(value, int):
        result = value
    elif legacy_float and isinstance(value, float) and value.is_integer():
        result = int(value)
    else:
        raise ValueError(f"{field} must be an Int64")
    if result < 0 or result > 9223372036854775807:
        raise ValueError(f"{field} is outside Int64 range")
    return result


def require_declared_sha(value: Any, field: str) -> str:
    if not isinstance(value, str):
        raise ValueError(f"{field} must be a SHA-256 string")
    result = value.upper()
    if not SHA256_RE.fullmatch(result):
        raise ValueError(f"{field} is not a SHA-256 value")
    return result


def validate_document(document: Any, expected_tree: str | None) -> dict[str, Any]:
    if not isinstance(document, dict):
        raise ValueError("input must be a schema-versioned manifest object")
    schema = document.get("schema")
    records = document.get("files")
    if not isinstance(records, list):
        raise ValueError("manifest files must be an array")

    if schema == V2_SCHEMA:
        if "treeAlgorithm" in document or "payload" in document:
            raise ValueError("v2 legacy manifest has an unknown algorithm declaration")
        if expected_tree is None:
            raise ValueError("v2 legacy manifest requires --expected-tree from its historical receipt")
        declared = document.get("stagingSource")
        if not isinstance(declared, dict):
            raise ValueError("v2 legacy manifest stagingSource is missing")
        calculated = calculate(records, ordinal_sort=False)
        declared_files = require_declared_int(declared.get("files"), "stagingSource.files")
        declared_bytes = require_declared_int(
            declared.get("bytes"), "stagingSource.bytes", legacy_float=True
        )
        declared_tree = require_declared_sha(expected_tree, "expected-tree")
        if declared_files != calculated["files"]:
            raise ValueError("v2 declared file count mismatch")
        if declared_bytes != calculated["bytes"]:
            raise ValueError("v2 declared total bytes mismatch")
        if declared_tree != calculated["treeSha256"]:
            raise ValueError("v2 historical tree mismatch")
        return {
            "status": "PASS",
            "schema": schema,
            "dispatch": "legacy-v2-manifest-order",
            "declaredValuesMatched": True,
            **calculated,
        }

    if schema in {V3_SCHEMA, FIXTURE_SCHEMA}:
        if expected_tree is not None:
            raise ValueError("canonical manifests must use their embedded declared tree")
        declared = document.get("payload")
        if not isinstance(declared, dict):
            raise ValueError("canonical manifest payload declaration is missing")
        if declared.get("treeAlgorithm") != CANONICAL_ALGORITHM:
            raise ValueError("unknown schema/treeAlgorithm combination")
        calculated = calculate(records, ordinal_sort=True)
        declared_files = require_declared_int(declared.get("files"), "payload.files")
        declared_bytes = require_declared_int(declared.get("bytes"), "payload.bytes")
        declared_tree = require_declared_sha(declared.get("treeSha256"), "payload.treeSha256")
        if declared_files != calculated["files"]:
            raise ValueError("canonical declared file count mismatch")
        if declared_bytes != calculated["bytes"]:
            raise ValueError("canonical declared total bytes mismatch")
        if declared_tree != calculated["treeSha256"]:
            raise ValueError("canonical declared tree mismatch")
        return {
            "status": "PASS",
            "schema": schema,
            "dispatch": "canonical-v3-ordinal",
            "declaredValuesMatched": True,
            **calculated,
        }

    raise ValueError("unknown manifest schema/treeAlgorithm combination")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--expected-tree")
    args = parser.parse_args()
    try:
        document = json.loads(args.input.read_text(encoding="utf-8"))
        result = validate_document(document, args.expected_tree)
        print(json.dumps(result, ensure_ascii=False, separators=(",", ":")))
        return 0
    except (OSError, ValueError, TypeError, json.JSONDecodeError) as error:
        print(
            json.dumps(
                {"status": "FAIL", "error": str(error)},
                ensure_ascii=False,
                separators=(",", ":"),
            ),
            file=sys.stderr,
        )
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
