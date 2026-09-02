#!/usr/bin/env python3
"""Validate the frozen Desktop automation contract closure from a release root.

The validator deliberately accepts only a published root.  It never falls back to
the source tree, so a portable package cannot pass by borrowing missing files from
the checkout.
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
import os
import pathlib
import sys
from typing import Any, Iterable
from urllib.parse import unquote, urlsplit

from jsonschema import Draft202012Validator, FormatChecker, ValidationError
from referencing import Registry, Resource


VERSIONS = {
    "v1": {
        "receiptSha256": "a326242a31a55088ec747891ba7ffe553b1250b24281f72b8738a6506c97ef06",
        "treeSha256": "d4d0cca8d1210a90f484a6d31643a8312f67ab6f1e53fc2367308af009194a6e",
    },
    "v1.1": {
        "receiptSha256": "b0d491b99a37315583efc27a22592c5af13ae150bd9f10f96bdb7a3d1d353953",
        "treeSha256": "a05c8720c0ee8a493c403225957f497072e97fe36f836dc566c7d584d44c2276",
    },
    "v1.2": {
        "receiptSha256": "33a3526c367ba14f623888be8bbad1429c3f99dfbb9bac586d7794af2f3d371d",
        "treeSha256": "d105b10364e3015a85d4b6086a103e7094d7c3158e308280c3a31e06afb65637",
    },
    "v1.3": {
        "receiptSha256": "919aaf5b74712c6e1245769dbb035d81005ff8300c0bffe0c911c470b8d3087e",
        "treeSha256": "866e70dd7af5980012b817f22950dc2132914abcf39e63091ff2aac6181cd83a",
    },
}
COMMON_CHECKPOINT_SHA256 = "1cb738fe133f6645b9757e246e2fbc210785086342f70bad32b5015bd19e1b1e"


def sha256(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load(path: pathlib.Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def inside(path: pathlib.Path, root: pathlib.Path) -> bool:
    try:
        path.resolve().relative_to(root.resolve())
        return True
    except ValueError:
        return False


def require_file(path: pathlib.Path, root: pathlib.Path, label: str) -> pathlib.Path:
    resolved = path.resolve()
    if not inside(resolved, root):
        raise AssertionError(f"{label} escapes release root: {path}")
    if not resolved.is_file():
        raise AssertionError(f"{label} missing from release root: {resolved}")
    return resolved


def receipt_tree(items: Iterable[dict[str, Any]]) -> str:
    ordered = sorted(
        ((item["path"], item["sha256"].lower()) for item in items),
        key=lambda item: item[0].encode("utf-8"),
    )
    canonical = "".join(f"{path}|{digest}\n" for path, digest in ordered)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def verify_version_receipt(release_root: pathlib.Path, version: str) -> dict[str, Any]:
    contract_root = release_root / "contracts" / "desktop-automation" / version
    receipt_path = require_file(contract_root / "contract-receipt.json", release_root, f"{version} receipt")
    expected = VERSIONS[version]
    actual_receipt_sha = sha256(receipt_path)
    assert actual_receipt_sha == expected["receiptSha256"], (
        f"{version} receipt SHA drift: {actual_receipt_sha}"
    )
    receipt = load(receipt_path)
    expected_files = {
        path.relative_to(contract_root).as_posix()
        for path in contract_root.rglob("*")
        if path.is_file() and path.name != "contract-receipt.json"
    }
    listed_files = {item["path"] for item in receipt["files"]}
    assert listed_files == expected_files, (
        f"{version} receipt file set mismatch: missing={sorted(expected_files - listed_files)} "
        f"extra={sorted(listed_files - expected_files)}"
    )
    for item in receipt["files"]:
        path = require_file(contract_root / item["path"], release_root, f"{version} receipt item")
        assert path.stat().st_size == item["bytes"], f"{version} bytes mismatch: {item['path']}"
        assert sha256(path) == item["sha256"].lower(), f"{version} SHA mismatch: {item['path']}"
    tree = receipt_tree(receipt["files"])
    assert tree == receipt["treeSha256"] == expected["treeSha256"], f"{version} tree mismatch"
    return {
        "version": version,
        "receiptPath": receipt_path.relative_to(release_root).as_posix(),
        "receiptSha256": actual_receipt_sha,
        "treeSha256": tree,
        "files": len(receipt["files"]),
    }


def iter_refs(value: Any) -> Iterable[str]:
    if isinstance(value, dict):
        for key, child in value.items():
            if key == "$ref" and isinstance(child, str):
                yield child
            else:
                yield from iter_refs(child)
    elif isinstance(value, list):
        for child in value:
            yield from iter_refs(child)


def resolve_file_ref(schema_path: pathlib.Path, reference: str, release_root: pathlib.Path) -> pathlib.Path | None:
    if reference.startswith("#"):
        return None
    parsed = urlsplit(reference)
    if parsed.scheme:
        raise AssertionError(f"external schema reference is not portable/offline: {schema_path}: {reference}")
    relative = pathlib.Path(unquote(parsed.path.replace("/", os.sep)))
    return require_file(schema_path.parent / relative, release_root, f"$ref from {schema_path.name}")


def schema_registry(schema_paths: list[pathlib.Path]) -> tuple[Registry, dict[pathlib.Path, Any]]:
    registry = Registry()
    documents: dict[pathlib.Path, Any] = {}
    for path in schema_paths:
        document = load(path)
        document = copy.deepcopy(document)
        document["$id"] = path.resolve().as_uri()
        documents[path.resolve()] = document
        registry = registry.with_resource(path.resolve().as_uri(), Resource.from_contents(document))
    return registry, documents


def validate_fixture(validator: Draft202012Validator, path: pathlib.Path, valid: bool) -> None:
    instance = load(path)
    if valid:
        validator.validate(instance)
        return
    try:
        validator.validate(instance)
    except ValidationError:
        return
    raise AssertionError(f"invalid fixture accepted: {path}")


def validate_schema_and_fixtures(release_root: pathlib.Path) -> dict[str, int]:
    schema_paths = sorted(
        [
            *release_root.glob("contracts/desktop-automation/**/*.schema.json"),
            *release_root.glob("contracts/v1/schemas/**/*.schema.json"),
        ],
        key=lambda path: path.as_posix().encode("utf-8"),
    )
    assert schema_paths, "no packaged schemas found"
    referenced_files: set[pathlib.Path] = set()
    for path in schema_paths:
        document = load(path)
        Draft202012Validator.check_schema(document)
        for reference in iter_refs(document):
            target = resolve_file_ref(path, reference, release_root)
            if target:
                referenced_files.add(target)

    registry, documents = schema_registry(schema_paths)

    def validator_for(relative: str) -> Draft202012Validator:
        path = require_file(release_root / relative, release_root, "fixture schema")
        return Draft202012Validator(
            documents[path], registry=registry, format_checker=FormatChecker()
        )

    valid_count = 0
    invalid_count = 0

    v1 = release_root / "contracts" / "desktop-automation" / "v1"
    action_v1 = validator_for("contracts/desktop-automation/v1/semantic-action.schema.json")
    for line in (v1 / "fixtures" / "semantic-actions.valid.ndjson").read_text(encoding="utf-8").splitlines():
        if line.strip():
            action_v1.validate(json.loads(line))
            valid_count += 1
    capture = validator_for("contracts/desktop-automation/v1/capture-bundle.v1.schema.json")
    for name in ["capture-bundle.multi-image.valid.json", "capture-bundle.artifact-ref-128.valid.json"]:
        validate_fixture(capture, v1 / "fixtures" / name, True)
        valid_count += 1
    for name in ["capture-bundle.artifact-ref-129.invalid.json", "capture-bundle.answer-only.invalid.json"]:
        validate_fixture(capture, v1 / "fixtures" / name, False)
        invalid_count += 1

    v11 = release_root / "contracts" / "desktop-automation" / "v1.1"
    action_v11 = validator_for("contracts/desktop-automation/v1.1/semantic-action.schema.json")
    for line in (v11 / "fixtures" / "semantic-actions.valid.ndjson").read_text(encoding="utf-8").splitlines():
        if line.strip():
            action_v11.validate(json.loads(line))
            valid_count += 1
    invalid_v11 = [
        {"schemaVersion": "desktop-automation.v1", "requestId": "old", "action": "clip.capture"},
        {"schemaVersion": "desktop-automation.v1.1", "requestId": "missing", "action": "language-practice.select"},
        {"schemaVersion": "desktop-automation.v1.1", "requestId": "unknown", "action": "language-practice.select", "arguments": {"selection": "guessed"}},
        {"schemaVersion": "desktop-automation.v1.1", "requestId": "binding", "action": "language-practice.select", "arguments": {"selection": "both", "decisionTargetToken": "desktop-constructed"}},
    ]
    for instance in invalid_v11:
        try:
            action_v11.validate(instance)
        except ValidationError:
            invalid_count += 1
            continue
        raise AssertionError(f"v1.1 invalid inline fixture accepted: {instance}")

    v13 = release_root / "contracts" / "desktop-automation" / "v1.3" / "fixtures" / "semantic-actions"
    action_v13 = validator_for("contracts/desktop-automation/v1.3/semantic-action.schema.json")
    for path in sorted((v13 / "valid").glob("*.json")):
        validate_fixture(action_v13, path, True)
        valid_count += 1
    for path in sorted((v13 / "invalid").glob("*.json")):
        validate_fixture(action_v13, path, False)
        invalid_count += 1

    common = release_root / "contracts" / "v1" / "fixtures" / "common"
    for group in ["ipc-request", "ipc-response", "task-cancel-payload", "worker-cancel-signal"]:
        validator = validator_for(f"contracts/v1/schemas/common/{group}.schema.json")
        for path in sorted((common / group / "valid").glob("*.json")):
            validate_fixture(validator, path, True)
            valid_count += 1
        for path in sorted((common / group / "invalid").glob("*.json")):
            validate_fixture(validator, path, False)
            invalid_count += 1

    return {
        "schemasChecked": len(schema_paths),
        "referencedSchemaFilesPresent": len(referenced_files),
        "validFixturesAccepted": valid_count,
        "invalidFixturesRejected": invalid_count,
    }


def resolve_contract_reference(
    contract_path: pathlib.Path, relative: str, release_root: pathlib.Path, label: str
) -> pathlib.Path:
    return require_file(contract_path.parent / pathlib.Path(relative.replace("/", os.sep)), release_root, label)


def validate_contract_links(release_root: pathlib.Path) -> dict[str, Any]:
    linked: set[pathlib.Path] = set()
    contracts: dict[str, tuple[pathlib.Path, Any]] = {}
    for version in VERSIONS:
        path = require_file(
            release_root / "contracts" / "desktop-automation" / version / "desktop-automation.contract.json",
            release_root,
            f"{version} contract",
        )
        contracts[version] = (path, load(path))
    for version, (path, contract) in contracts.items():
        for name, relative in contract.get("schemas", {}).items():
            linked.add(resolve_contract_reference(path, relative, release_root, f"{version} schema {name}"))
        if version == "v1":
            for name, relative in contract["commonContractReferences"].items():
                linked.add(resolve_contract_reference(path, relative, release_root, f"v1 common {name}"))
            checkpoint_path = resolve_contract_reference(
                path, contract["commonCheckpoint"]["receipt"], release_root, "v1 common checkpoint"
            )
            linked.add(checkpoint_path)
            assert sha256(checkpoint_path) == contract["commonCheckpoint"]["receiptSha256"]
        if version == "v1.1":
            linked.add(resolve_contract_reference(path, contract["supersedesForNewCandidates"], release_root, "v1.1 supersedes"))
            linked.add(resolve_contract_reference(path, contract["legacyCompatibilityAliases"]["contract"], release_root, "v1.1 legacy contract"))
        if version in {"v1.2", "v1.3"}:
            parent_contract = resolve_contract_reference(path, contract["extends"]["contract"], release_root, f"{version} parent contract")
            parent_receipt = resolve_contract_reference(path, contract["extends"]["receipt"], release_root, f"{version} parent receipt")
            linked.update({parent_contract, parent_receipt})
            assert sha256(parent_receipt) == contract["extends"]["receiptSha256"]
            assert load(parent_receipt)["treeSha256"] == contract["extends"]["treeSha256"]
    return {"contractReferencedFilesPresent": len(linked)}


def validate_common_checkpoint(release_root: pathlib.Path) -> dict[str, Any]:
    receipt_path = require_file(
        release_root / "receipts" / "contracts-v1-stable-checkpoint-20260825T115938Z.json",
        release_root,
        "common checkpoint receipt",
    )
    assert sha256(receipt_path) == COMMON_CHECKPOINT_SHA256, "common checkpoint receipt SHA drift"
    receipt = load(receipt_path)
    assert receipt["checkpointId"] == "contracts-v1-20260825T115938Z"
    checked = 0
    for item in receipt["files"]:
        normalized = item["path"].replace("\\", "/")
        marker = "/contracts/"
        index = normalized.lower().find(marker)
        if index < 0:
            raise AssertionError(f"checkpoint item lacks contracts path: {item['path']}")
        relative = normalized[index + 1 :]
        packaged = require_file(release_root / pathlib.Path(relative), release_root, "common checkpoint file")
        assert sha256(packaged) == item["sha256"].lower(), f"common checkpoint SHA mismatch: {relative}"
        checked += 1
    return {
        "checkpointId": receipt["checkpointId"],
        "receiptSha256": sha256(receipt_path),
        "filesChecked": checked,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--release-root", required=True, type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path)
    args = parser.parse_args()
    release_root = args.release_root.resolve()
    if not release_root.is_dir():
        raise SystemExit(f"release root is not a directory: {release_root}")

    result: dict[str, Any] = {
        "schemaVersion": "knowledge-clipper-desktop-automation-release-closure.v1",
        "status": "passed",
        "releaseRoot": ".",
        "versions": [],
        "failures": [],
    }
    try:
        result["versions"] = [verify_version_receipt(release_root, version) for version in VERSIONS]
        result["commonCheckpoint"] = validate_common_checkpoint(release_root)
        result.update(validate_contract_links(release_root))
        result.update(validate_schema_and_fixtures(release_root))
    except Exception as exc:
        result["status"] = "failed"
        result["failures"].append(f"{type(exc).__name__}: {exc}")
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 1

    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
