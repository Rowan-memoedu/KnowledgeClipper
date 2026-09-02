#!/usr/bin/env python3
"""Build an append-only formal win-x64 release for export.anki."""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Any


WORKER_ROOT = Path(__file__).resolve().parent
PROJECT_ROOT = WORKER_ROOT.parents[1]
sys.path.insert(0, str(WORKER_ROOT))

import anki_exporter as exporter


CONTRACT_FILES = (
    "anki-destination-settings.v1.schema.json",
    "anki-connection-status.v1.schema.json",
    "anki-destination-settings-descriptor.v1.json",
    "anki-destination-settings-descriptor.v1.schema.json",
    "anki-destination-settings-descriptor.v1.2.json",
    "anki-destination-settings-descriptor.v1.2.schema.json",
    "anki-destination-settings-descriptor.v1.3.json",
    "anki-destination-settings-descriptor.v1.3.schema.json",
    "anki-managed-initialization-status.v1.schema.json",
    "anki-export-config.v2.schema.json",
    "CORE-ORIGINAL-MEDIA-PROJECTION.md",
)
SCHEMA_FILES = ("card-bundle.v1.schema.json", "card-bundle.v1.1.schema.json")
CONFIG_FILES = ("anki-export.v1.example.json", "anki-export.v2.example.json")


def project_relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT_ROOT.resolve()).as_posix()


def tree(root: Path, *, exclude: set[str] | None = None) -> dict[str, Any]:
    excluded = exclude or set()
    rows: list[dict[str, Any]] = []
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        if relative in excluded:
            continue
        rows.append({
            "path": relative,
            "bytes": exporter.path_size(path),
            "sha256": exporter.sha256_file(path).upper(),
        })
    digest_input = "\n".join(
        f"{item['path']}\t{item['bytes']}\t{item['sha256'].lower()}" for item in rows
    ).encode("utf-8")
    return {
        "files": len(rows),
        "bytes": sum(item["bytes"] for item in rows),
        "treeSha256": exporter.sha256_bytes(digest_input).upper(),
        "entries": rows,
    }


def run_checked(command: list[str], *, cwd: Path) -> subprocess.CompletedProcess[str]:
    process = subprocess.run(command, cwd=cwd, capture_output=True, text=True, encoding="utf-8", check=False)
    if process.returncode != 0:
        raise RuntimeError(
            f"Command failed with exit {process.returncode}: {command!r}\nstdout={process.stdout}\nstderr={process.stderr}"
        )
    return process


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--release-root", required=True)
    args = parser.parse_args()
    release_root = Path(args.release_root).resolve()
    if release_root.exists():
        raise RuntimeError(f"Append-only release root already exists: {release_root}")
    if release_root.name.lower() != "win-x64":
        raise RuntimeError("Formal release root must end in win-x64")

    target = release_root / "workers" / "anki"
    target.mkdir(parents=True, exist_ok=False)
    build = run_checked(
        [
            "pwsh", "-NoProfile", "-File", str(WORKER_ROOT / "build_exe.ps1"),
            "-OutputDirectory", str(target),
        ],
        cwd=PROJECT_ROOT,
    )
    exe = target / "KnowledgeClipper.AnkiExporter.Worker.exe"
    if not exe.is_file():
        raise RuntimeError(f"Build did not publish the worker EXE: {exe}")

    contracts_target = target / "contracts"
    contracts_target.mkdir()
    for name in CONTRACT_FILES:
        shutil.copy2(WORKER_ROOT / "contracts" / name, contracts_target / name)
    schemas_target = target / "schemas"
    schemas_target.mkdir()
    for name in SCHEMA_FILES:
        shutil.copy2(WORKER_ROOT / "schemas" / name, schemas_target / name)
    config_target = target / "config"
    config_target.mkdir()
    for name in CONFIG_FILES:
        shutil.copy2(WORKER_ROOT / "config" / name, config_target / name)

    described = run_checked([str(exe), "describe", "--json"], cwd=release_root)
    if described.stderr:
        raise RuntimeError(f"Release EXE describe wrote stderr: {described.stderr}")
    descriptor = json.loads(described.stdout)
    if descriptor.get("workerId") != "export.anki" or descriptor.get("workerVersion") != exporter.EXPORTER_VERSION:
        raise RuntimeError(f"Release EXE descriptor identity mismatch: {descriptor!r}")
    if "anki-connection-status" not in descriptor.get("outputKinds", []):
        raise RuntimeError("Release EXE descriptor omitted the read-only probe output kind")
    if "anki-managed-initialization-status" not in descriptor.get("outputKinds", []):
        raise RuntimeError("Release EXE descriptor omitted the explicit managed initialization output kind")
    describe_path = target / "export.anki.describe.json"
    exporter.write_json_atomic_once(describe_path, descriptor)

    manifest_relative = "workers/anki/release-manifest.v1.json"
    payload = tree(release_root, exclude={manifest_relative})
    manifest = {
        "schemaVersion": "export.anki.formal-release.v1",
        "releaseId": release_root.parent.name,
        "product": {"displayName": "零碎知识剪藏", "asciiRoot": "KnowledgeClipper"},
        "platform": "win-x64",
        "releaseRoot": project_relative(release_root),
        "worker": {
            "workerId": descriptor["workerId"],
            "workerVersion": descriptor["workerVersion"],
            "contractVersion": descriptor["contractVersion"],
            "entrypoint": "workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe",
            "outputKinds": descriptor["outputKinds"],
        },
        "settings": {
            "descriptor": "workers/anki/contracts/anki-destination-settings-descriptor.v1.3.json",
            "configArtifactKind": "anki-export-config",
            "configSchemaVersions": ["anki-export-config.v1", "anki-export-config.v2"],
            "configSnapshotPolicy": "artifact-identity-only",
            "instanceMode": "single-default",
            "registryEnablementSemantics": "availability-only",
        },
        "originalMedia": {
            "status": "ready",
            "activeCorePolicy": "ordered-original-images.v1",
            "destinationPolicy": "anki-only",
            "cardBundleSchema": "1.1",
            "requiresFormalCoreInputContract": True,
            "realAnkiCanary": "not-run-pending-core-admission",
        },
        "probe": {
            "operation": "probe",
            "outputKind": "anki-connection-status",
            "readOnly": True,
            "effectClass": "provider-read-only",
            "networkScope": "loopback-only",
            "externalMutation": False,
            "localArtifactWrite": True,
            "mayPromptUser": True,
            "statusRedaction": "sha256-profile-model-deck-identifiers",
        },
        "managedInitialization": {
            "operation": "initialize-managed",
            "outputKind": "anki-managed-initialization-status",
            "effectClass": "provider-write",
            "networkScope": "loopback-only",
            "externalMutation": True,
            "executionPolicy": "explicit-user-action",
            "createsOnlyMissingResources": True,
            "automaticRetryAfterUnknownState": False,
            "realAnkiCanary": "not-run-awaiting-user-ui-action",
        },
        "build": {
            "tool": "PyInstaller",
            "toolVersion": "6.21.0",
            "stdoutSha256": exporter.sha256_bytes(build.stdout.encode("utf-8")),
            "stderrSha256": exporter.sha256_bytes(build.stderr.encode("utf-8")),
        },
        "sourceBinding": {
            "ankiExporterSha256": exporter.sha256_file(WORKER_ROOT / "anki_exporter.py"),
            "settingsAdapterSha256": exporter.sha256_file(WORKER_ROOT / "anki_settings_adapter.py"),
            "versionMetadataSha256": exporter.sha256_file(WORKER_ROOT / "KnowledgeClipper.AnkiExporter.Worker.version.txt"),
        },
        "payload": payload,
        "promotedToCanonicalRegistry": False,
        "authorityTarget": None,
        "createdAt": exporter.utc_now(),
    }
    manifest_path = release_root / manifest_relative
    exporter.write_json_atomic_once(manifest_path, manifest)
    final_tree = tree(release_root)
    result = {
        "status": "passed",
        "releaseRoot": str(release_root),
        "releaseManifest": str(manifest_path),
        "releaseManifestSha256": exporter.sha256_file(manifest_path).upper(),
        "tree": {key: final_tree[key] for key in ("files", "bytes", "treeSha256")},
        "exe": {
            "path": str(exe),
            "bytes": exporter.path_size(exe),
            "sha256": exporter.sha256_file(exe).upper(),
        },
        "describe": {
            "path": str(describe_path),
            "sha256": exporter.sha256_file(describe_path).upper(),
            "workerId": descriptor["workerId"],
            "workerVersion": descriptor["workerVersion"],
        },
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
