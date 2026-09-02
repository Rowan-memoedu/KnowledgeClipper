from __future__ import annotations

import json
import os
import subprocess
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator, FormatChecker


PROJECT_ROOT = Path(__file__).resolve().parents[1]
SCHEMA_PATH = PROJECT_ROOT / "tests/Quality.Gates/schemas/product-identity.schema.json"
DISPLAY_NAME = "零碎知识剪藏"
ASCII_ROOT = "KnowledgeClipper"
DESKTOP_ENTRYPOINT = "desktop/KnowledgeClipper.exe"
CORE_COMPONENTS = {"desktop", "orchestrator.core", "image.ocr.mineru", "content.deepseek", "speech.edge"}
EXPORTERS = {"export.remnote", "export.anki"}
CORE_ENTRYPOINTS = {
    "desktop": "desktop/KnowledgeClipper.exe",
    "orchestrator.core": "orchestrator/KnowledgeClipper.Orchestrator.exe",
    "image.ocr.mineru": "workers/image-ocr/KnowledgeClipper.ImageOcr.Worker.exe",
    "content.deepseek": "workers/content/KnowledgeClipper.Content.Worker.exe",
    "speech.edge": "workers/speech/KnowledgeClipper.Speech.Worker.exe",
}


class BrandGateFailure(RuntimeError):
    pass


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def validate_identity_document(document: Any) -> None:
    schema = load_json(SCHEMA_PATH)
    Draft202012Validator.check_schema(schema)
    errors = sorted(
        Draft202012Validator(schema, format_checker=FormatChecker()).iter_errors(document),
        key=lambda error: list(error.absolute_path),
    )
    if errors:
        location = "/".join(str(part) for part in errors[0].absolute_path) or "$"
        raise BrandGateFailure(f"product identity violation at {location}: {errors[0].message}")
    component_ids = [item["componentId"] for item in document["coreIdentities"]]
    if set(component_ids) != CORE_COMPONENTS or len(component_ids) != len(set(component_ids)):
        raise BrandGateFailure("product identity must contain each neutral core component exactly once")


def _version_info(executable: Path) -> dict[str, str]:
    command = (
        "$v=(Get-Item -LiteralPath $env:KNOWLEDGE_CLIPPER_METADATA_PATH).VersionInfo;"
        "[pscustomobject]@{ProductName=$v.ProductName;InternalName=$v.InternalName;"
        "OriginalFilename=$v.OriginalFilename;FileDescription=$v.FileDescription}|ConvertTo-Json -Compress"
    )
    completed = subprocess.run(
        ["pwsh", "-NoLogo", "-NoProfile", "-Command", command],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=15, check=False,
        env={**os.environ, "KNOWLEDGE_CLIPPER_METADATA_PATH": str(executable)},
    )
    if completed.returncode != 0:
        raise BrandGateFailure(f"cannot read desktop file metadata: {completed.stderr}")
    return json.loads(completed.stdout)


def validate_release_identity(release_root: Path) -> dict[str, Any]:
    release_root = release_root.resolve()
    identity_path = release_root / "product-identity.v1.json"
    start_path = release_root / "docs/START.md"
    if not identity_path.is_file() or not start_path.is_file():
        raise BrandGateFailure("new release must contain product-identity.v1.json and docs/START.md")
    identity = load_json(identity_path)
    validate_identity_document(identity)
    identities = {item["componentId"]: item for item in identity["coreIdentities"]}
    missing_entrypoints = [path for path in CORE_ENTRYPOINTS.values() if not (release_root / path).is_file()]
    if missing_entrypoints:
        raise BrandGateFailure("new neutral core entrypoints are missing: " + ", ".join(missing_entrypoints))
    desktop = release_root / DESKTOP_ENTRYPOINT
    if (release_root / "desktop/RemNoteQuickClipper.exe").exists():
        raise BrandGateFailure("legacy desktop executable cannot remain a primary desktop entrypoint")
    component_metadata: dict[str, dict[str, str]] = {}
    for component_id, relative in CORE_ENTRYPOINTS.items():
        metadata = _version_info(release_root / relative)
        component_metadata[component_id] = metadata
        combined = " ".join(str(value or "") for value in metadata.values()).lower()
        original = str(metadata.get("OriginalFilename") or "")
        expected_assembly = identities[component_id]["assemblyName"]
        if (original and Path(original).stem != expected_assembly) or "remnote" in combined:
            raise BrandGateFailure(f"{component_id} file metadata is not a neutral KnowledgeClipper identity")
    if (
        component_metadata["desktop"].get("ProductName") != DISPLAY_NAME
        or Path(str(component_metadata["desktop"].get("OriginalFilename") or "")).stem != "KnowledgeClipper"
    ):
        raise BrandGateFailure("desktop file metadata does not identify 零碎知识剪藏 / KnowledgeClipper")
    start_text = start_path.read_text(encoding="utf-8")
    if DISPLAY_NAME not in start_text or "export.remnote" not in start_text or "export.anki" not in start_text:
        raise BrandGateFailure("startup instructions must name the product and present both final exporters")
    forbidden: list[str] = []
    for path in release_root.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(release_root).as_posix()
        lower = relative.lower()
        if "remnotequickclipper" in lower and not lower.startswith("legacy/") and not lower.startswith("workers/remnote/"):
            forbidden.append(relative)
    if forbidden:
        raise BrandGateFailure("legacy RemNote-centric identity outside allowed compatibility/export boundary: " + ", ".join(forbidden[:10]))
    return {
        "status": "passed",
        "displayName": DISPLAY_NAME,
        "asciiRoot": ASCII_ROOT,
        "desktopEntrypoint": DESKTOP_ENTRYPOINT,
        "primaryProcess": identity["primaryProcess"],
        "coreComponents": sorted(CORE_COMPONENTS),
        "exporters": sorted(EXPORTERS),
        "legacyAliasesAuthoritative": False,
        "fileMetadata": component_metadata,
    }


def audit_current_legacy_release() -> dict[str, Any]:
    root = PROJECT_ROOT / "releases/1.0.0-rc.1-preseal.10/win-x64"
    authority = load_json(PROJECT_ROOT / "contracts/authority/worker-registry/v1.1.0-20260826T023054Z/worker-registry.v1.json")
    non_export_legacy_entries = [
        item["workerId"]
        for item in authority["workers"]
        if item["workerId"] not in EXPORTERS and "remnotequickclipper" in item["entrypoint"]["path"].lower()
    ]
    return {
        "schemaVersion": "knowledge-clipper-brand-audit.v1",
        "status": "migration-required-no-go",
        "releaseDecision": "NO-GO",
        "lockedIdentity": {"displayName": DISPLAY_NAME, "asciiRoot": ASCII_ROOT, "desktopEntrypoint": DESKTOP_ENTRYPOINT},
        "historicalRelease": {
            "root": root.relative_to(PROJECT_ROOT).as_posix(),
            "immutable": True,
            "legacyDesktopEntrypointPresent": (root / "desktop/RemNoteQuickClipper.exe").is_file(),
            "releaseManifest": None,
            "candidateHash": None,
        },
        "authorityRegistry": {
            "stableWorkerIdsPreserved": sorted(item["workerId"] for item in authority["workers"]),
            "nonExportLegacyEntrypointsRequiringNewCheckpoint": sorted(non_export_legacy_entries),
        },
        "pending": [
            "desktop-KnowledgeClipper.exe-and-file-metadata",
            "KnowledgeClipper-core-assembly-and-namespace-migration",
            "neutral-worker-entrypoints-and-new-authority-checkpoint",
            "neutral-installation-and-startup-instructions",
            "candidate-bound-five-worker-chain-after-brand-migration",
        ],
        "externalProductionApisInvoked": False,
    }


def block_legacy_release_builder(builder: str) -> None:
    raise BrandGateFailure(
        f"{builder} is disabled for new outputs because it emits legacy RemNoteQuickClipper authority identity; "
        "use the append-only KnowledgeClipper migration builder after module branding completes"
    )
