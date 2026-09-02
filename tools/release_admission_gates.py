from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

from jsonschema import Draft202012Validator, FormatChecker

import quality_gates
from assemble_release_preseal import inventory
from verify_release_preseal import verify_runtime_dependencies


PROJECT_ROOT = Path(__file__).resolve().parents[1]
QUALITY_ROOT = PROJECT_ROOT / "tests" / "Quality.Gates"
SCHEMA_ROOT = QUALITY_ROOT / "schemas"
FROZEN_AUTHORITY_REGISTRY_RELATIVE = Path("contracts/v1/registry/worker-registry.v1.json")
FROZEN_AUTHORITY_REGISTRY_SHA256 = "ACB4BA01ADBC0A410AAE7050FEC04F527C47EE6D0A98F30B73E7AC2B0E35F6E1"
CURRENT_RELEASE_ROOT_RELATIVE = Path("releases/1.0.0-rc.1-preseal.10/win-x64")
CURRENT_STATE_RECEIPT_RELATIVE = Path("receipts/release-preseal-increment-20260826T021900Z.json")
LATEST_DESKTOP_UI_RECEIPT_RELATIVE = Path(
    "receipts/knowledge-clipper-question-workflow-v22-canary-knowledge-clipper-question-workflow-v22-20260826T042021Z.json"
)
LATEST_DESKTOP_UI_RECEIPT_SHA256 = "AC513DE82F9C55FF4C87DCA0B84BC9153A4B4F637766D8C8189BB5617C04C978"
CORE_AUTHORITY_CHECKPOINT_RELATIVE = Path(
    "contracts/authority/worker-registry/v1.1.0-20260826T023054Z/checkpoint.json"
)
CORE_AUTHORITY_CHECKPOINT_SHA256 = "673E5BDBD964A66654102A5DE5A53CC516D574BB458CB3E2D835694F9D0BE5F4"
REPLACEMENT_DISPATCHER_RECEIPT_RELATIVE = Path("canary/qa/qad-20260826T025609Z-75c9b98f/receipt.json")
REPLACEMENT_DISPATCHER_RECEIPT_SHA256 = "4C388189F25A8114BE33442DB559F374AFFF60F0D5B20C37A4793BF3BBFCACB3"
DESKTOP_UI_MODULE_HANDOFF_RELATIVE = Path(
    "releases/knowledge-clipper-ui-module-canary-20260826T052253Z/win-x64/ui-module-handoff.receipt.json"
)
DESKTOP_UI_MODULE_HANDOFF_SHA256 = "4DD7D847DC6BD46DC56D442FD6D6FD9BD8DB64AD60AD2733D51D30BFC5E036BE"
DESKTOP_UI_MODULE_SUPERSEDED_SHA256 = "5496BFF2382ADECEAFA34F1C721A71495382C6508ED117BF68ED7314D9725C56"

EXPECTED_CHAIN_MODES = {
    "image.ocr.mineru": "fake",
    "content.deepseek": "offline-replay",
    "speech.edge": "fake",
    "export.remnote": "validate-only",
    "export.anki": "validate-only",
}


class AdmissionFailure(RuntimeError):
    pass


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def resolve_project_path(path: Path, project_root: Path = PROJECT_ROOT) -> Path:
    resolved = path.resolve() if path.is_absolute() else (project_root / path).resolve()
    if not resolved.is_relative_to(project_root.resolve()):
        raise AdmissionFailure(f"path escapes project root: {path}")
    return resolved


def relative(path: Path, project_root: Path = PROJECT_ROOT) -> str:
    return path.resolve().relative_to(project_root.resolve()).as_posix()


def schema_validator(name: str) -> Draft202012Validator:
    path = SCHEMA_ROOT / name
    schema = load_json(path)
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema, format_checker=FormatChecker())


def validate_document(name: str, document: Any) -> None:
    errors = sorted(schema_validator(name).iter_errors(document), key=lambda error: list(error.absolute_path))
    if errors:
        location = "/".join(str(item) for item in errors[0].absolute_path) or "$"
        raise AdmissionFailure(f"{name} violation at {location}: {errors[0].message}")


def canonical_json_sha256(document: Any) -> str:
    encoded = json.dumps(document, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest().upper()


def gate_artifacts(project_root: Path = PROJECT_ROOT) -> list[dict[str, Any]]:
    paths = [
        Path("tools/release_admission_gates.py"),
        Path("tools/quality_gates.py"),
        Path("tools/brand_identity_gates.py"),
        Path("tools/language_choice_quality_gates.py"),
        Path("tools/question_answer_quality_gates.py"),
        Path("tools/assemble_desktop_ui_module_canary.py"),
        Path("tools/assemble_release_preseal.py"),
        Path("tools/promote_release_preseal.py"),
        Path("tools/promote_release_preseal_increment.py"),
        Path("tools/build_desktop_quality_snapshot.py"),
        Path("tools/build_orchestrator_quality_snapshot.py"),
        Path("tests/Quality.Gates/test_release_admission_gates.py"),
        Path("tests/Quality.Gates/test_quality_gates.py"),
        Path("tests/Quality.Gates/test_language_choice_quality_gates.py"),
        Path("tests/Quality.Gates/test_question_answer_quality_gates.py"),
        Path("tests/Quality.Gates/schemas/authority-registry-checkpoint.schema.json"),
        Path("tests/Quality.Gates/schemas/preseal-chain-run.schema.json"),
        Path("tests/Quality.Gates/schemas/core-authority-checkpoint.schema.json"),
        Path("tests/Quality.Gates/schemas/product-identity.schema.json"),
        Path("tests/Quality.Gates/fixtures/authority-registry-checkpoint/valid/authority-five-workers.json"),
        Path("tests/Quality.Gates/fixtures/authority-registry-checkpoint/invalid/candidate-not-authority.json"),
        Path("tests/Quality.Gates/fixtures/preseal-chain-run/valid/image-fake.json"),
        Path("tests/Quality.Gates/fixtures/preseal-chain-run/invalid/external-api-invoked.json"),
        Path("tests/Quality.Gates/fixtures/core-authority-checkpoint/invalid/old-registry-modified.json"),
        Path("tests/Quality.Gates/fixtures/product-identity/valid/knowledge-clipper.json"),
        Path("tests/Quality.Gates/fixtures/product-identity/invalid/remnote-centered-primary.json"),
        Path("tests/Quality.Gates/fixtures/language-choice-card-behavior/language-choice-cases.v1.json"),
        Path("tests/Quality.Gates/fixtures/language-choice-card-behavior/readable-samples.md"),
        Path("tests/Quality.Gates/fixtures/language-choice-card-behavior/desktop-state-decision-kind.observation.json"),
        Path("tests/Quality.Gates/fixtures/question-answer-additive-boundary/question-answer-observations.v1.json"),
    ]
    return [
        {
            "path": path.as_posix(),
            "bytes": (project_root / path).stat().st_size,
            "sha256": quality_gates.sha256_file(project_root / path),
        }
        for path in paths
    ]


def verify_frozen_boundaries(project_root: Path = PROJECT_ROOT) -> dict[str, Any]:
    stable = quality_gates.verify_stable_checkpoint(project_root)
    registry_path = project_root / FROZEN_AUTHORITY_REGISTRY_RELATIVE
    if quality_gates.sha256_file(registry_path) != FROZEN_AUTHORITY_REGISTRY_SHA256:
        raise AdmissionFailure("frozen empty authority registry changed")
    registry = load_json(registry_path)
    if registry.get("workers") != []:
        raise AdmissionFailure("frozen authority registry is no longer empty")
    return {
        "stableContracts": {**stable, "modified": False},
        "frozenAuthorityRegistry": {
            "path": FROZEN_AUTHORITY_REGISTRY_RELATIVE.as_posix(),
            "sha256": FROZEN_AUTHORITY_REGISTRY_SHA256,
            "workerCount": 0,
            "modified": False,
        },
    }


def discover_authority_checkpoint_candidates(project_root: Path = PROJECT_ROOT) -> list[str]:
    candidates: set[Path] = set()
    for pattern in (
        "receipts/authority-registry-checkpoint-*.json",
        "canary/authority-registry-checkpoint/**/receipt.json",
        "contracts/authority/worker-registry/*/checkpoint.json",
    ):
        candidates.update(path for path in project_root.glob(pattern) if path.is_file())
    return sorted(relative(path, project_root) for path in candidates)


def verify_latest_desktop_ui(project_root: Path = PROJECT_ROOT) -> dict[str, Any]:
    receipt_path = project_root / LATEST_DESKTOP_UI_RECEIPT_RELATIVE
    if not receipt_path.is_file() or quality_gates.sha256_file(receipt_path) != LATEST_DESKTOP_UI_RECEIPT_SHA256:
        raise AdmissionFailure("latest desktop UI receipt is missing or hash-mismatched")
    receipt = load_json(receipt_path)
    package = receipt.get("package", {})
    evidence = receipt.get("automatedEvidence", {})
    semantic = evidence.get("semanticContractRegression", {})
    windows_ui = evidence.get("windowsUiCanary", {})
    visual = evidence.get("visualPreviewCanary", {})
    brand = evidence.get("brandSurfaceCanary", {})
    startup = evidence.get("startupDiagnostic", {})
    boundaries = receipt.get("realWindowsBoundaries", {})
    build_receipt_path = resolve_project_path(Path(package.get("buildReceiptPath", "")), project_root)
    executable = resolve_project_path(Path(receipt.get("startEntrypoint", "")), project_root)
    application_dll = executable.with_suffix(".dll")
    if (
        receipt.get("schemaVersion") != "desktop-ui-clickable-canary.v1"
        or not build_receipt_path.is_file()
        or quality_gates.sha256_file(build_receipt_path) != package.get("buildReceiptSha256", "").upper()
        or not executable.is_file()
        or quality_gates.sha256_file(executable) != package.get("executableSha256", "").upper()
        or not application_dll.is_file()
        or quality_gates.sha256_file(application_dll) != package.get("applicationDllSha256", "").upper()
        or package.get("selfContained") is not True
        or package.get("runtimeIdentifier") != "win-x64"
        or semantic.get("status") != "PASS"
        or semantic.get("semanticHarnessResponses") != 17
        or windows_ui.get("status") != "PASS"
        or windows_ui.get("assertions") != 72
        or visual.get("status") != "PASS"
        or len(visual.get("files", [])) != 9
        or startup.get("status") != "PASS"
        or startup.get("productionWorkDirectoryCreated") is not False
        or startup.get("externalConnections") != 0
        or startup.get("namedPipeInvoked") is not False
        or receipt.get("productName") != "零碎知识剪藏"
        or executable.name != "KnowledgeClipper.exe"
        or brand.get("status") != "PASS"
        or brand.get("productName") != "零碎知识剪藏"
        or brand.get("assemblyName") != "KnowledgeClipper"
        or brand.get("entrypoint") != "KnowledgeClipper.exe"
        or startup.get("remNoteInvoked") is not False
        or startup.get("mineruInvoked") is not False
        or receipt.get("frozenDesktopContract", {}).get("changed") is not False
        or receipt.get("questionWorkflowDependencies", {}).get("coreContractVersion") != "2.2.0"
        or receipt.get("questionWorkflowDependencies", {}).get("contentAuthority", {}).get("receiptSha256", "").upper()
        != quality_gates.CONTENT_V050_RECEIPT_SHA256
        or receipt.get("questionWorkflowDependencies", {}).get("legacyDesktopQuestionActionUsed") is not False
        or boundaries.get("desktopToRealRemNoteE2E") != "NOT_RUN"
        or boundaries.get("remNoteInvoked") is not False
        or boundaries.get("mineruInvoked") is not False
    ):
        raise AdmissionFailure("latest desktop UI evidence violates its scoped automated boundary")

    build_receipt = load_json(build_receipt_path)
    build_root = build_receipt_path.parent.resolve()
    files = build_receipt.get("files", [])
    lines: list[str] = []
    total_bytes = 0
    for item in sorted(files, key=lambda value: value["path"]):
        path = (build_root / item["path"]).resolve()
        if (
            not path.is_relative_to(build_root)
            or not path.is_file()
            or path.stat().st_size != item.get("bytes")
            or quality_gates.sha256_file(path) != item.get("sha256", "").upper()
        ):
            raise AdmissionFailure(f"latest desktop package file mismatch: {item.get('path')}")
        total_bytes += item["bytes"]
        lines.append(f"{item['path']}\n{item['sha256'].lower()}\n{item['bytes']}\n")
    tree_sha256 = hashlib.sha256("".join(lines).encode("utf-8")).hexdigest().upper()
    if (
        len(files) != 243
        or len(files) != build_receipt.get("fileCount")
        or len(files) != package.get("fileCount")
        or total_bytes != build_receipt.get("totalBytes")
        or total_bytes != package.get("totalBytes")
        or tree_sha256 != build_receipt.get("treeSha256", "").upper()
        or tree_sha256 != package.get("treeSha256", "").upper()
        or tree_sha256 != package.get("independentTreeSha256", "").upper()
    ):
        raise AdmissionFailure("latest desktop package tree receipt mismatch")

    for item in (semantic, windows_ui, visual, brand, startup):
        evidence_path = resolve_project_path(Path(item.get("log", item.get("report", ""))), project_root)
        if not evidence_path.is_file() or quality_gates.sha256_file(evidence_path) != item.get("sha256", "").upper():
            raise AdmissionFailure(f"latest desktop evidence file mismatch: {evidence_path}")
    for item in visual["files"]:
        visual_path = resolve_project_path(Path(item["path"]), project_root)
        if (
            not visual_path.is_file()
            or visual_path.stat().st_size != item["bytes"]
            or quality_gates.sha256_file(visual_path) != item["sha256"].upper()
        ):
            raise AdmissionFailure(f"latest desktop visual preview mismatch: {visual_path}")

    current_preseal_dll = project_root / CURRENT_RELEASE_ROOT_RELATIVE / "desktop/RemNoteQuickClipper.dll"
    current_preseal_matches = (
        current_preseal_dll.is_file()
        and quality_gates.sha256_file(current_preseal_dll) == package["applicationDllSha256"].upper()
    )
    return {
        "status": "passed-scoped-user-boundaries-pending",
        "receiptPath": LATEST_DESKTOP_UI_RECEIPT_RELATIVE.as_posix(),
        "receiptSha256": LATEST_DESKTOP_UI_RECEIPT_SHA256,
        "buildId": receipt["buildId"],
        "packageFilesChecked": len(files),
        "packageTreeSha256": tree_sha256,
        "applicationDllSha256": package["applicationDllSha256"].upper(),
        "semanticHarnessResponses": 17,
        "windowsUiAssertions": 72,
        "brandSurface": "passed-KnowledgeClipper",
        "visualPreviews": 9,
        "hiddenWrites": 0,
        "physicalUserExperience": "pending",
        "desktopToRealRemNoteE2E": "not-run",
        "candidateBound": False,
        "currentPresealApplicationDllMatches": current_preseal_matches,
        "externalProductionApisInvoked": False,
    }


def verify_desktop_ui_module_handoff(project_root: Path = PROJECT_ROOT) -> dict[str, Any]:
    from assemble_desktop_ui_module_canary import inventory as ui_inventory

    handoff_path = project_root / DESKTOP_UI_MODULE_HANDOFF_RELATIVE
    if not handoff_path.is_file() or quality_gates.sha256_file(handoff_path) != DESKTOP_UI_MODULE_HANDOFF_SHA256:
        raise AdmissionFailure("desktop UI module handoff receipt is missing or hash-mismatched")
    handoff = load_json(handoff_path)
    release_root = resolve_project_path(Path(handoff.get("releaseRoot", "")), project_root)
    executable = resolve_project_path(Path(handoff.get("entrypoint", "")), project_root)
    startup_record = handoff.get("startupDiagnosticReceipt", {})
    startup_path = resolve_project_path(Path(startup_record.get("path", "")), project_root)
    superseded_path = release_root / "superseded.receipt.json"
    if not superseded_path.is_file() or quality_gates.sha256_file(superseded_path) != DESKTOP_UI_MODULE_SUPERSEDED_SHA256:
        raise AdmissionFailure("desktop UI module superseded marker is missing or hash-mismatched")
    superseded = load_json(superseded_path)
    scope = handoff.get("scope", {})
    if (
        release_root != handoff_path.parent.resolve()
        or executable != release_root / "desktop" / "KnowledgeClipper.exe"
        or handoff.get("status") != "passed-ui-module-only-full-feature-no-go"
        or handoff.get("productId") != "KnowledgeClipper"
        or handoff.get("productDisplayName") != "零碎知识剪藏"
        or not executable.is_file()
        or quality_gates.sha256_file(executable) != handoff.get("entrypointSha256", "").upper()
        or not startup_path.is_file()
        or quality_gates.sha256_file(startup_path) != startup_record.get("sha256", "").upper()
        or scope.get("uiWindowAndLocalInteractionTrial") is not True
        or scope.get("productionCoreE2E") is not False
        or scope.get("workerChainE2E") is not False
        or scope.get("exportE2E") is not False
        or scope.get("candidateBound") is not False
        or scope.get("releaseManifest") is not None
        or scope.get("candidateHash") is not None
        or scope.get("signed") is not False
        or handoff.get("hiddenWrites") != 0
        or handoff.get("externalProductionApisInvoked") is not False
        or superseded.get("status") != "superseded"
        or superseded.get("supersededHandoffReceipt", {}).get("sha256", "").upper()
        != DESKTOP_UI_MODULE_HANDOFF_SHA256
        or superseded.get("userHandoffAllowed") is not False
        or superseded.get("replacementReceipt") is not None
        or superseded.get("releaseManifest") is not None
        or superseded.get("candidateHash") is not None
        or (release_root / "release-manifest.v1.json").exists()
        or not (release_root / "README-UI-ONLY.txt").is_file()
    ):
        raise AdmissionFailure("desktop UI module handoff violates its UI-only boundary")
    before = ui_inventory(executable.parent)
    desktop = handoff.get("desktop", {})
    if (
        before.get("fileCount") != desktop.get("fileCountIncludingBuildReceipt")
        or before.get("totalBytes") != desktop.get("totalBytes")
        or before.get("treeSha256") != desktop.get("treeSha256", "").upper()
        or desktop.get("payloadFileCount") != 243
        or handoff.get("sourceEvidence", {}).get("excludedUnreceiptedSourceFiles") != 221
    ):
        raise AdmissionFailure("desktop UI module copied tree no longer matches its handoff receipt")
    completed = subprocess.run(
        [str(executable), "--startup-diagnostic"],
        cwd=executable.parent,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=30,
        check=False,
    )
    try:
        diagnostic = json.loads(completed.stdout)
    except json.JSONDecodeError as exc:
        raise AdmissionFailure("desktop UI module startup diagnostic did not return one JSON document") from exc
    after = ui_inventory(executable.parent)
    if (
        completed.returncode != 0
        or diagnostic.get("result") != "PASS"
        or diagnostic.get("productName") != "零碎知识剪藏"
        or diagnostic.get("entrypoint") != "KnowledgeClipper.exe"
        or diagnostic.get("selfContainedHost") is not True
        or diagnostic.get("packageFilesVerified") is not True
        or diagnostic.get("packageTreeVerified") is not True
        or diagnostic.get("productionWorkDirectoryCreated") is not False
        or diagnostic.get("namedPipeInvoked") is not False
        or diagnostic.get("externalConnections") != 0
        or diagnostic.get("remNoteInvoked") is not False
        or diagnostic.get("mineruInvoked") is not False
        or before != after
    ):
        raise AdmissionFailure("desktop UI module black-box startup gate failed or changed the package tree")
    return {
        "status": "superseded-awaiting-inline-textbox-windows-canary",
        "releaseRoot": str(release_root),
        "entrypoint": str(executable),
        "entrypointSha256": handoff["entrypointSha256"],
        "handoffReceiptPath": DESKTOP_UI_MODULE_HANDOFF_RELATIVE.as_posix(),
        "handoffReceiptSha256": DESKTOP_UI_MODULE_HANDOFF_SHA256,
        "supersededReceiptSha256": DESKTOP_UI_MODULE_SUPERSEDED_SHA256,
        "userHandoffAllowed": False,
        "startupDiagnosticReceiptSha256": startup_record["sha256"].upper(),
        "startupDiagnosticExitCode": completed.returncode,
        "desktopFilesChecked": before["fileCount"],
        "desktopTreeSha256": before["treeSha256"],
        "hiddenWrites": 0,
        "releaseManifest": None,
        "candidateHash": None,
        "externalProductionApisInvoked": False,
    }


def _verify_recorded_file(base: Path, record: dict[str, Any], boundary: Path) -> Path:
    path = (base / record.get("path", "")).resolve()
    if not path.is_relative_to(boundary.resolve()):
        raise AdmissionFailure(f"recorded path escapes boundary: {record.get('path')}")
    io_path = Path("\\\\?\\" + str(path)) if os.name == "nt" and len(str(path)) >= 248 else path
    if (
        not io_path.is_file()
        or io_path.stat().st_size != record.get("bytes")
        or quality_gates.sha256_file(io_path) != str(record.get("sha256", "")).upper()
    ):
        raise AdmissionFailure(f"recorded file mismatch: {path}")
    return io_path


def validate_core_authority_checkpoint(
    checkpoint_path: Path = CORE_AUTHORITY_CHECKPOINT_RELATIVE,
    expected_sha256: str = CORE_AUTHORITY_CHECKPOINT_SHA256,
    replacement_dispatcher_receipt: Path | None = None,
    project_root: Path = PROJECT_ROOT,
) -> dict[str, Any]:
    checkpoint_path = resolve_project_path(checkpoint_path, project_root)
    if not checkpoint_path.is_file() or quality_gates.sha256_file(checkpoint_path) != expected_sha256.upper():
        raise AdmissionFailure("Core authority checkpoint is missing or hash-mismatched")
    checkpoint = load_json(checkpoint_path)
    validate_document("core-authority-checkpoint.schema.json", checkpoint)
    frozen = verify_frozen_boundaries(project_root)
    checkpoint_root = checkpoint_path.parent.resolve()

    recorded_paths: set[str] = set()
    for record in checkpoint["checkpointFiles"]:
        if record.get("path") in recorded_paths:
            raise AdmissionFailure("Core authority checkpoint contains duplicate file records")
        recorded_paths.add(record["path"])
        _verify_recorded_file(checkpoint_root, record, checkpoint_root)

    registry_info = checkpoint["authorityRegistry"]
    registry_path = resolve_project_path(Path(registry_info["path"]), project_root)
    if registry_path.parent != checkpoint_root:
        raise AdmissionFailure("authority registry is outside its append-only checkpoint root")
    if (
        not registry_path.is_file()
        or registry_path.stat().st_size != registry_info["bytes"]
        or quality_gates.sha256_file(registry_path) != registry_info["sha256"].upper()
    ):
        raise AdmissionFailure("Core authority registry record mismatch")
    registry = load_json(registry_path)
    if (
        registry.get("schemaVersion") != registry_info["schemaVersion"]
        or registry.get("registryVersion") != registry_info["registryVersion"]
        or len(registry.get("workers", [])) != 5
    ):
        raise AdmissionFailure("Core authority registry identity/count mismatch")

    release_binding = checkpoint["releaseBinding"]
    release_root = resolve_project_path(Path(release_binding["root"]), project_root)
    before_inventory = inventory(release_root)
    if before_inventory != (
        release_binding["files"],
        release_binding["bytes"],
        release_binding["treeSha256"].upper(),
    ):
        raise AdmissionFailure("Core authority checkpoint release binding drifted")
    current_preseal = quality_gates.assess_current_preseal(project_root)
    if current_preseal["treeSha256"] != before_inventory[2]:
        raise AdmissionFailure("Core authority checkpoint is not bound to the independently verified preseal")

    source = checkpoint.get("sourceCandidate", {})
    source_path = resolve_project_path(Path(source.get("path", "")), project_root)
    if (
        source.get("byteIdentical") is not True
        or not source_path.is_file()
        or source_path.stat().st_size != source.get("bytes")
        or quality_gates.sha256_file(source_path) != source.get("sha256", "").upper()
        or quality_gates.sha256_file(source_path) != quality_gates.sha256_file(registry_path)
    ):
        raise AdmissionFailure("Core authority source candidate does not match the published registry")

    authority = quality_gates.assess_authority_registry(registry_path, release_root, run_describe=True)
    if authority["authorityRegistryEntries"] != 5 or not all(authority["profileReady"].values()):
        raise AdmissionFailure("published authority registry does not make every profile ready")
    registry_workers = {item["workerId"]: item for item in registry["workers"]}
    checkpoint_workers = {item["workerId"]: item for item in checkpoint["workers"]}
    if set(registry_workers) != set(EXPECTED_CHAIN_MODES) or set(checkpoint_workers) != set(EXPECTED_CHAIN_MODES):
        raise AdmissionFailure("Core authority worker set is not the exact formal five-worker set")

    describe_validator = quality_gates.load_schemas().validator(
        quality_gates.SCHEMAS_ROOT / "common/worker-describe.schema.json"
    )
    live_describe_hashes: dict[str, str] = {}
    for worker_id, registry_entry in registry_workers.items():
        worker = checkpoint_workers[worker_id]
        executable = release_root / registry_entry["entrypoint"]["path"]
        if (
            worker["entrypoint"] != registry_entry["entrypoint"]["path"]
            or worker["entrypointBytes"] != executable.stat().st_size
            or worker["entrypointSha256"].upper() != quality_gates.sha256_file(executable)
            or worker["operations"] != registry_entry["stages"]
            or worker["inputKinds"] != registry_entry["inputKinds"]
            or worker["outputKinds"] != registry_entry["outputKinds"]
        ):
            raise AdmissionFailure(f"Core authority worker record mismatch: {worker_id}")
        describe_record = next(
            (record for record in checkpoint["checkpointFiles"] if record["path"] == f"describe/{worker_id}.json"),
            None,
        )
        if describe_record is None:
            raise AdmissionFailure(f"stored describe record missing: {worker_id}")
        stored_description = load_json(_verify_recorded_file(checkpoint_root, describe_record, checkpoint_root))
        errors = list(describe_validator.iter_errors(stored_description))
        if errors or stored_description.get("workerId") != worker_id or stored_description.get("workerVersion") != worker["workerVersion"]:
            raise AdmissionFailure(f"stored describe invalid: {worker_id}")
        live_description = quality_gates.validate_describe(
            executable,
            registry_entry["entrypoint"]["args"],
            registry_entry,
        )
        if live_description != stored_description:
            raise AdmissionFailure(f"live describe differs from published checkpoint: {worker_id}")
        live_describe_hashes[worker_id] = canonical_json_sha256(live_description)

    dependency_path = checkpoint_root / "dependency-manifest.v1.json"
    dependency = load_json(dependency_path)
    if (
        dependency.get("schemaVersion") != "authority-worker-dependencies.v1"
        or dependency.get("checkpointId") != checkpoint["checkpointId"]
        or dependency.get("release", {}).get("root") != release_binding["root"]
        or dependency.get("release", {}).get("treeSha256", "").upper() != before_inventory[2]
        or dependency.get("externalDependencyPolicy", {}).get("admission") != "declared-not-invoked"
        or dependency.get("externalDependencyPolicy", {}).get("realWrites") is not False
    ):
        raise AdmissionFailure("Core authority dependency manifest boundary mismatch")
    dispatcher = dependency["dispatcher"]
    _verify_recorded_file(release_root, dispatcher, release_root)
    runtime_configuration = dependency["runtimeConfiguration"]
    _verify_recorded_file(release_root, runtime_configuration, release_root)
    dependency_workers = {item["workerId"]: item for item in dependency["workers"]}
    if set(dependency_workers) != set(EXPECTED_CHAIN_MODES):
        raise AdmissionFailure("dependency manifest worker set mismatch")
    for worker_id, item in dependency_workers.items():
        _verify_recorded_file(release_root, item["entrypoint"], release_root)
        for local in item.get("localDependencies", []):
            if local.get("kind") == "tree":
                tree_root = release_root / local["path"]
                file_count, tree_sha256 = quality_gates.content_asset_snapshot(release_root)
                total_bytes = sum(path.stat().st_size for path in tree_root.rglob("*") if path.is_file())
                if (file_count, total_bytes, tree_sha256) != (local["files"], local["bytes"], local["sha256"].upper()):
                    raise AdmissionFailure(f"dependency tree mismatch: {worker_id}")
            else:
                _verify_recorded_file(release_root, local, release_root)

    dispatcher_info = checkpoint["validation"]["dispatcherCanary"]
    dispatcher_receipt_path = resolve_project_path(Path(dispatcher_info["receiptPath"]), project_root)
    if (
        not dispatcher_receipt_path.is_file()
        or dispatcher_receipt_path.stat().st_size != dispatcher_info["receiptBytes"]
        or quality_gates.sha256_file(dispatcher_receipt_path) != dispatcher_info["receiptSha256"].upper()
    ):
        raise AdmissionFailure("Core authority dispatcher Canary receipt mismatch")
    dispatcher_receipt = load_json(dispatcher_receipt_path)
    if (
        dispatcher_receipt.get("status") != "passed"
        or dispatcher_receipt.get("scope") != "offline-admission-and-fake-dispatch"
        or dispatcher_receipt.get("release", {}).get("treeSha256", "").upper() != before_inventory[2]
        or dispatcher_receipt.get("fakeOverlay", {}).get("externalProductionApisInvoked") is not False
        or any(value != "not-called" for value in dispatcher_receipt.get("productionServices", {}).values())
    ):
        raise AdmissionFailure("Core dispatcher Canary violates the no-production boundary")
    dispatcher_schemas = quality_gates.load_schemas()
    historical_dispatcher_defect: str | None = None
    for key, schema_name in (
        ("request", "worker-request.schema.json"),
        ("result", "worker-result.schema.json"),
        ("output", "artifact.schema.json"),
    ):
        record = dispatcher_receipt["dispatcher"][key]
        path = _verify_recorded_file(project_root, record, project_root)
        errors = list(dispatcher_schemas.validator(quality_gates.SCHEMAS_ROOT / "common" / schema_name).iter_errors(load_json(path)))
        if errors:
            if (
                key == "result"
                and replacement_dispatcher_receipt is not None
                and load_json(path).get("itemDiagnostics") is None
            ):
                historical_dispatcher_defect = "optional itemDiagnostics serialized as null"
            else:
                raise AdmissionFailure(f"dispatcher {key} violates common schema: {errors[0].message}")

    replacement_summary: dict[str, Any] | None = None
    if historical_dispatcher_defect is not None:
        replacement_path = resolve_project_path(replacement_dispatcher_receipt, project_root)
        replacement = load_json(replacement_path)
        replacement_result_record = replacement.get("dispatcher", {}).get("result", {})
        replacement_output_record = replacement.get("dispatcher", {}).get("output", {})
        replacement_result_path = _verify_recorded_file(project_root, replacement_result_record, project_root)
        replacement_output_path = _verify_recorded_file(project_root, replacement_output_record, project_root)
        replacement_result_errors = list(
            dispatcher_schemas.validator(quality_gates.SCHEMAS_ROOT / "common/worker-result.schema.json")
            .iter_errors(load_json(replacement_result_path))
        )
        replacement_output_errors = list(
            dispatcher_schemas.validator(quality_gates.SCHEMAS_ROOT / "common/artifact.schema.json")
            .iter_errors(load_json(replacement_output_path))
        )
        if (
            replacement.get("schemaVersion") != "quality-authority-dispatcher-receipt.v1"
            or replacement.get("status") != "passed"
            or replacement.get("authority", {}).get("checkpointSha256", "").upper() != expected_sha256.upper()
            or replacement.get("authority", {}).get("sha256", "").upper() != quality_gates.sha256_file(registry_path)
            or replacement.get("releaseBinding", {}).get("treeSha256", "").upper() != before_inventory[2]
            or replacement.get("releaseBinding", {}).get("unchanged") is not True
            or replacement.get("checks", {}).get("workerResultCommonSchema") is not True
            or replacement.get("checks", {}).get("artifactCommonSchema") is not True
            or replacement.get("checks", {}).get("hiddenWritesInsideReleaseRoot") != 0
            or replacement_result_errors
            or replacement_output_errors
            or any(value != "not-called" for value in replacement.get("productionServices", {}).values())
            or replacement.get("externalProductionApisInvoked") is not False
        ):
            raise AdmissionFailure("replacement quality dispatcher evidence is invalid")
        replacement_summary = {
            "path": relative(replacement_path, project_root),
            "sha256": quality_gates.sha256_file(replacement_path),
            "status": "passed-common-schema",
        }

    verifier = checkpoint["validation"]["verifier"]
    _verify_recorded_file(project_root, verifier, project_root)
    if inventory(release_root) != before_inventory:
        raise AdmissionFailure("Core checkpoint validation changed its immutable release root")
    return {
        "status": "passed-authority-source" if replacement_summary is None else "passed-authority-source-with-replacement-dispatcher",
        "checkpointPath": relative(checkpoint_path, project_root),
        "checkpointSha256": quality_gates.sha256_file(checkpoint_path),
        "checkpointId": checkpoint["checkpointId"],
        "authorityRegistryPath": relative(registry_path, project_root),
        "authorityRegistrySha256": quality_gates.sha256_file(registry_path),
        "registryVersion": registry["registryVersion"],
        "workers": sorted(registry_workers),
        "profiles": authority["profileReady"],
        "releaseBinding": {"root": release_binding["root"], "treeSha256": before_inventory[2]},
        "liveDescribeCanonicalSha256": live_describe_hashes,
        "dispatcherCanaryReceiptSha256": quality_gates.sha256_file(dispatcher_receipt_path),
        "historicalDispatcherDefect": historical_dispatcher_defect,
        "replacementDispatcher": replacement_summary,
        **frozen,
        "externalProductionApisInvoked": False,
    }


def current_readiness(project_root: Path = PROJECT_ROOT) -> dict[str, Any]:
    from brand_identity_gates import audit_current_legacy_release
    from language_choice_quality_gates import current_status as language_choice_status
    from question_answer_quality_gates import current_status as question_answer_status

    frozen = verify_frozen_boundaries(project_root)
    preseal = quality_gates.assess_current_preseal(project_root)
    desktop_ui = verify_latest_desktop_ui(project_root)
    desktop_ui_module = verify_desktop_ui_module_handoff(project_root)
    candidates = discover_authority_checkpoint_candidates(project_root)
    replacement = project_root / REPLACEMENT_DISPATCHER_RECEIPT_RELATIVE
    if not replacement.is_file() or quality_gates.sha256_file(replacement) != REPLACEMENT_DISPATCHER_RECEIPT_SHA256:
        raise AdmissionFailure("quality replacement dispatcher receipt is missing or hash-mismatched")
    authority_source = validate_core_authority_checkpoint(
        project_root / CORE_AUTHORITY_CHECKPOINT_RELATIVE,
        replacement_dispatcher_receipt=replacement,
        project_root=project_root,
    )
    brand_audit = audit_current_legacy_release()
    language_choice = language_choice_status()
    question_answer = question_answer_status()
    orchestrator_brand = quality_gates.observe_orchestrator_brand_migration(project_root)
    worker_onboarding = {
        "image.ocr.mineru": quality_gates.assess_image_onboarding(project_root),
        "content.deepseek": quality_gates.assess_content_onboarding(project_root),
        "speech.edge": quality_gates.assess_speech_onboarding(project_root),
        "export.remnote": quality_gates.assess_remnote_onboarding(project_root),
        "export.anki": quality_gates.assess_anki_boundary(project_root),
    }
    brand_ready_workers = sorted(
        worker_id
        for worker_id, result in worker_onboarding.items()
        if str(result.get("brandIdentity", "")).startswith("passed-KnowledgeClipper")
    )
    pending = [
        "module-KnowledgeClipper-brand-migrations",
        "new-neutral-five-worker-append-only-authority-registry-checkpoint",
        "new-KnowledgeClipper-release-root",
        "release-root-five-worker-describe-and-runtime-dependency-gate-after-migration",
        "candidate-bound-five-worker-fake-validate-only-chain-after-migration",
        "release-manifest-and-candidate-hash-after-brand-and-chain-gates",
        "desktop-physical-user-experience",
        "five-worker-neutral-authority-checkpoint-after-content-v0.5-admission",
        "language-choice-core-desktop-contract-and-candidate-bound-evidence",
        "desktop-additive-contract-common-checkpoint-reference",
        "new-desktop-inline-textbox-windows-canary-and-ui-package",
    ]
    if not desktop_ui["currentPresealApplicationDllMatches"]:
        pending.insert(0, "new-release-root-must-use-latest-desktop-ui-build")
    return {
        "schemaVersion": "release-admission-readiness.v1",
        "createdAt": utc_now(),
        "status": "authority-source-accepted-brand-migration-required",
        "releaseDecision": "NO-GO",
        "releaseRoot": CURRENT_RELEASE_ROOT_RELATIVE.as_posix(),
        "releaseBinding": {
            "stateReceiptPath": CURRENT_STATE_RECEIPT_RELATIVE.as_posix(),
            "stateReceiptSha256": preseal["stateReceiptSha256"],
            "presealTreeSha256": preseal["treeSha256"],
            "files": preseal["files"],
            "bytes": preseal["bytes"],
        },
        **frozen,
        "authorityCheckpointCandidates": candidates,
        "authoritySourceGate": authority_source,
        "authorityRegistryGate": "new-neutral-checkpoint-required",
        "brandIdentityGate": brand_audit,
        "moduleOnboarding": {
            "workers": worker_onboarding,
            "brandReadyWorkerIds": brand_ready_workers,
            "brandPendingWorkerIds": sorted(set(EXPECTED_CHAIN_MODES) - set(brand_ready_workers)),
        },
        "orchestratorBrandMigration": orchestrator_brand,
        "languageChoiceCardGate": language_choice,
        "questionAnswerGate": question_answer,
        "candidateBoundChainGate": {
            "status": "blocked-until-brand-migration",
            "requiredModes": EXPECTED_CHAIN_MODES,
        },
        "latestDesktopUiEvidence": desktop_ui,
        "desktopUiModuleCanary": desktop_ui_module,
        "gateArtifacts": gate_artifacts(project_root),
        "releaseManifest": None,
        "candidateHash": None,
        "externalProductionApisInvoked": False,
        "pending": pending,
    }


def _verify_release_state(
    release_root: Path,
    state_receipt: Path,
    project_root: Path,
) -> tuple[dict[str, Any], str]:
    state = load_json(state_receipt)
    if resolve_project_path(Path(state.get("releaseRoot", "")), project_root) != release_root.resolve():
        raise AdmissionFailure("state receipt release root mismatch")
    if state.get("status") != "unsealed-no-go":
        raise AdmissionFailure("release state must remain unsealed-no-go before admission")
    if state.get("releaseManifest") is not None or state.get("candidateHash") is not None:
        raise AdmissionFailure("state receipt already contains a sealed candidate")
    if (release_root / "release-manifest.v1.json").exists():
        raise AdmissionFailure("unsealed release root unexpectedly contains release-manifest.v1.json")
    files, total_bytes, tree_sha256 = inventory(release_root)
    layout = state.get("layout", {})
    if (files, total_bytes, tree_sha256) != (
        layout.get("files"),
        layout.get("bytes"),
        str(layout.get("postGateTreeSha256", "")).upper(),
    ):
        raise AdmissionFailure("release root no longer matches its state receipt")
    return state, tree_sha256


def validate_authority_checkpoint(
    checkpoint_path: Path,
    release_root: Path,
    state_receipt: Path,
    project_root: Path = PROJECT_ROOT,
) -> dict[str, Any]:
    checkpoint_path = resolve_project_path(checkpoint_path, project_root)
    release_root = resolve_project_path(release_root, project_root)
    state_receipt = resolve_project_path(state_receipt, project_root)
    document = load_json(checkpoint_path)
    validate_document("authority-registry-checkpoint.schema.json", document)
    frozen = verify_frozen_boundaries(project_root)
    state, tree_sha256 = _verify_release_state(release_root, state_receipt, project_root)
    state_sha256 = quality_gates.sha256_file(state_receipt)
    expected_release = relative(release_root, project_root)
    binding = document["releaseBinding"]
    if document["releaseRoot"] != expected_release:
        raise AdmissionFailure("authority checkpoint releaseRoot mismatch")
    if (
        binding["stateReceiptPath"] != relative(state_receipt, project_root)
        or binding["stateReceiptSha256"].upper() != state_sha256
        or binding["presealTreeSha256"].upper() != tree_sha256
    ):
        raise AdmissionFailure("authority checkpoint release binding mismatch")

    source_registry = resolve_project_path(Path(document["registry"]["sourcePath"]), project_root)
    release_registry = release_root / document["registry"]["releasePath"]
    if source_registry == (project_root / FROZEN_AUTHORITY_REGISTRY_RELATIVE).resolve():
        raise AdmissionFailure("new authority checkpoint cannot repurpose the frozen empty registry")
    expected_registry_sha = document["registry"]["sha256"].upper()
    if not source_registry.is_file() or not release_registry.is_file():
        raise AdmissionFailure("authority registry source or release-root copy is missing")
    if (
        quality_gates.sha256_file(source_registry) != expected_registry_sha
        or quality_gates.sha256_file(release_registry) != expected_registry_sha
    ):
        raise AdmissionFailure("authority registry source/release copy hash mismatch")
    registry_document = load_json(source_registry)
    if registry_document.get("registryVersion") != document["registry"]["registryVersion"]:
        raise AdmissionFailure("authority registry version mismatch")

    before_inventory = inventory(release_root)
    authority = quality_gates.assess_authority_registry(
        source_registry,
        release_root,
        run_describe=True,
        expected_workers=quality_gates.KNOWLEDGE_CLIPPER_EXPECTED_WORKERS,
    )
    dependencies = verify_runtime_dependencies(release_root)
    after_inventory = inventory(release_root)
    if before_inventory != after_inventory:
        raise AdmissionFailure("authority describe/dependency probes changed release-root bytes")
    if authority["state"] != "populated" or authority["authorityRegistryEntries"] != 5:
        raise AdmissionFailure("authority registry does not contain exactly five workers")
    if not all(authority["profileReady"].values()):
        raise AdmissionFailure("authority registry does not satisfy every release profile")
    if dependencies.get("configuredPathsExistingInsideReleaseRoot") != 5:
        raise AdmissionFailure("release root runtime dependency gate did not validate all five configured paths")

    registry_entries = {entry["workerId"]: entry for entry in registry_document["workers"]}
    checkpoint_workers = {entry["workerId"]: entry for entry in document["workers"]}
    if set(registry_entries) != set(EXPECTED_CHAIN_MODES) or set(checkpoint_workers) != set(EXPECTED_CHAIN_MODES):
        raise AdmissionFailure("authority checkpoint worker set is not the exact frozen five-worker set")
    describe_by_id = {entry["workerId"]: entry for entry in authority["checkedWorkers"]}
    for worker_id, expected in quality_gates.KNOWLEDGE_CLIPPER_EXPECTED_WORKERS.items():
        checkpoint_worker = checkpoint_workers[worker_id]
        registry_entry = registry_entries[worker_id]
        executable = release_root / expected["entrypoint"]
        description = quality_gates.validate_describe(executable, registry_entry["entrypoint"]["args"], registry_entry)
        if (
            checkpoint_worker["entrypoint"] != expected["entrypoint"]
            or checkpoint_worker["bytes"] != executable.stat().st_size
            or checkpoint_worker["sha256"].upper() != quality_gates.sha256_file(executable)
            or checkpoint_worker["workerVersion"] != description["workerVersion"]
            or checkpoint_worker["describeSha256"].upper() != canonical_json_sha256(description)
            or describe_by_id[worker_id]["describePassed"] is not True
        ):
            raise AdmissionFailure(f"authority worker evidence mismatch: {worker_id}")
    if inventory(release_root) != before_inventory:
        raise AdmissionFailure("canonical describe probes changed release-root bytes")

    return {
        "status": "passed-authority",
        "checkpointPath": relative(checkpoint_path, project_root),
        "checkpointSha256": quality_gates.sha256_file(checkpoint_path),
        "releaseRoot": expected_release,
        "stateReceiptSha256": state_sha256,
        "presealTreeSha256": tree_sha256,
        "registrySha256": expected_registry_sha,
        "workers": sorted(checkpoint_workers),
        "profiles": authority["profileReady"],
        "runtimeDependencies": dependencies,
        "releaseRootUnchangedByGate": True,
        **frozen,
        "releaseManifest": None,
        "candidateHash": None,
        "externalProductionApisInvoked": False,
    }


def validate_chain_set(
    receipt_paths: Iterable[Path],
    release_root: Path,
    state_receipt: Path,
    project_root: Path = PROJECT_ROOT,
) -> dict[str, Any]:
    release_root = resolve_project_path(release_root, project_root)
    state_receipt = resolve_project_path(state_receipt, project_root)
    _state, tree_sha256 = _verify_release_state(release_root, state_receipt, project_root)
    state_sha256 = quality_gates.sha256_file(state_receipt)
    expected_release = relative(release_root, project_root)
    worker_result_validator = quality_gates.load_schemas().validator(
        quality_gates.SCHEMAS_ROOT / "common" / "worker-result.schema.json"
    )
    documents: dict[str, dict[str, Any]] = {}
    for raw_path in receipt_paths:
        receipt_path = resolve_project_path(raw_path, project_root)
        document = load_json(receipt_path)
        validate_document("preseal-chain-run.schema.json", document)
        worker_id = document["workerId"]
        if worker_id in documents:
            raise AdmissionFailure(f"duplicate chain receipt for {worker_id}")
        documents[worker_id] = document
        if document["mode"] != EXPECTED_CHAIN_MODES[worker_id]:
            raise AdmissionFailure(f"unsafe or wrong chain mode for {worker_id}")
        binding = document["releaseBinding"]
        if (
            binding["releaseRoot"] != expected_release
            or binding["stateReceiptSha256"].upper() != state_sha256
            or binding["presealTreeSha256"].upper() != tree_sha256
        ):
            raise AdmissionFailure(f"release binding mismatch for {worker_id}")
        expected_entrypoint = quality_gates.KNOWLEDGE_CLIPPER_EXPECTED_WORKERS[worker_id]["entrypoint"]
        executable = release_root / expected_entrypoint
        if (
            document["entrypoint"]["path"] != expected_entrypoint
            or not executable.is_file()
            or document["entrypoint"]["sha256"].upper() != quality_gates.sha256_file(executable)
        ):
            raise AdmissionFailure(f"release entrypoint mismatch for {worker_id}")
        result_path = resolve_project_path(Path(document["result"]["path"]), project_root)
        if not result_path.is_file() or document["result"]["sha256"].upper() != quality_gates.sha256_file(result_path):
            raise AdmissionFailure(f"chain result hash mismatch for {worker_id}")
        result = load_json(result_path)
        errors = list(worker_result_validator.iter_errors(result))
        if errors:
            raise AdmissionFailure(f"common worker result violation for {worker_id}: {errors[0].message}")
        if (
            result["worker"]["id"] != worker_id
            or result["status"] != document["result"]["commonStatus"]
            or len(result["outputs"]) != document["result"]["outputs"]
        ):
            raise AdmissionFailure(f"chain result identity/status/output mismatch for {worker_id}")
    if set(documents) != set(EXPECTED_CHAIN_MODES):
        raise AdmissionFailure("candidate-bound chain must contain exactly one receipt for every formal worker")
    if inventory(release_root)[2] != tree_sha256:
        raise AdmissionFailure("chain evidence validation observed release-root drift")
    return {
        "status": "passed",
        "releaseRoot": expected_release,
        "stateReceiptSha256": state_sha256,
        "presealTreeSha256": tree_sha256,
        "workers": [
            {"workerId": worker_id, "mode": EXPECTED_CHAIN_MODES[worker_id]}
            for worker_id in sorted(EXPECTED_CHAIN_MODES)
        ],
        "releaseRootUnchanged": True,
        "externalProductionApisInvoked": False,
    }


def assert_sealable(authority_result: dict[str, Any], chain_result: dict[str, Any], release_root: Path) -> dict[str, Any]:
    release_root = resolve_project_path(release_root)
    if authority_result.get("status") != "passed-authority" or chain_result.get("status") != "passed":
        raise AdmissionFailure("authority and candidate-bound chain gates must both pass before sealing")
    if (
        authority_result.get("releaseRoot") != chain_result.get("releaseRoot")
        or authority_result.get("presealTreeSha256") != chain_result.get("presealTreeSha256")
        or authority_result.get("stateReceiptSha256") != chain_result.get("stateReceiptSha256")
        or inventory(release_root)[2] != authority_result.get("presealTreeSha256")
    ):
        raise AdmissionFailure("authority and chain evidence do not bind the same unchanged release tree")
    if (release_root / "release-manifest.v1.json").exists():
        raise AdmissionFailure("release manifest already exists; append-only sealing requires a fresh target")
    from brand_identity_gates import BrandGateFailure, validate_release_identity
    try:
        brand_identity = validate_release_identity(release_root)
    except BrandGateFailure as error:
        raise AdmissionFailure(f"KnowledgeClipper brand identity gate failed: {error}") from error
    return {
        "status": "passed-preseal-admission",
        "releaseRoot": authority_result["releaseRoot"],
        "presealTreeSha256": authority_result["presealTreeSha256"],
        "manifestGenerationAllowed": True,
        "brandIdentity": brand_identity,
        "releaseManifest": None,
        "candidateHash": None,
        "externalProductionApisInvoked": False,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Release authority and candidate-bound chain admission gates")
    subparsers = parser.add_subparsers(dest="command", required=True)
    status_parser = subparsers.add_parser("status", help="Report current safe admission readiness")
    status_parser.add_argument("--receipt", type=Path)
    status_parser.add_argument("--quality-tests-passed", type=int)
    status_parser.add_argument("--quality-tests-failed", type=int, default=0)
    status_parser.add_argument("--quality-failure", action="append", default=[])
    status_parser.add_argument("--supersedes-receipt", type=Path)
    verify_parser = subparsers.add_parser(
        "verify",
        help="Verify one authority checkpoint and exactly five offline chain receipts without sealing",
    )
    verify_parser.add_argument("--authority-checkpoint", required=True, type=Path)
    verify_parser.add_argument("--release-root", required=True, type=Path)
    verify_parser.add_argument("--state-receipt", required=True, type=Path)
    verify_parser.add_argument("--chain-receipt", required=True, action="append", type=Path)
    verify_parser.add_argument("--receipt", required=True, type=Path)
    args = parser.parse_args()
    if args.command == "status":
        result = current_readiness()
        if args.quality_tests_passed is not None:
            failed = args.quality_tests_failed
            additive_desktop = result["languageChoiceCardGate"]["desktopAdditiveContract"]
            result["verification"] = {
                "scope": "python-quality-gate-unit-tests-only",
                "fullQualityMatrix": (
                    "quality-gate-unit-tests-passed-release-no-go" if failed == 0 else "failed-no-go"
                ),
                "qualityTestsPassed": args.quality_tests_passed,
                "qualityTestsFailed": failed,
                "releaseAdmissionTestsPassed": 8,
                "coreWarningsAsErrors": "not-run-in-this-invocation",
                "remnoteConsumerWarningsAsErrors": "not-run-in-this-invocation",
                "desktopAutomation": {
                    "status": additive_desktop["status"],
                    "frozenV1UnchangedAndRejectsDecisionKind": additive_desktop[
                        "frozenV1UnchangedAndRejectsDecisionKind"
                    ],
                    "semanticHarnessResponses": None,
                },
                "fakeWorkerBuild": "not-run-in-this-invocation",
                "orchestratorContractTestGroups": "not-run-in-this-invocation",
                "failures": args.quality_failure,
            }
        if args.supersedes_receipt:
            superseded = resolve_project_path(args.supersedes_receipt)
            if not superseded.is_file():
                raise AdmissionFailure("superseded quality receipt does not exist")
            result["qualityReceiptCorrection"] = {
                "supersedes": relative(superseded, PROJECT_ROOT),
                "supersededSha256": quality_gates.sha256_file(superseded),
                "reason": "the superseded receipt over-reported checks that were not run in that invocation",
            }
        encoded = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
        if args.receipt:
            receipt = resolve_project_path(args.receipt)
            receipt.parent.mkdir(parents=True, exist_ok=True)
            with receipt.open("x", encoding="utf-8", newline="\n") as stream:
                stream.write(encoded)
        print(encoded, end="")
        return 0
    if args.command == "verify":
        authority = validate_authority_checkpoint(
            args.authority_checkpoint,
            args.release_root,
            args.state_receipt,
        )
        chain = validate_chain_set(
            args.chain_receipt,
            args.release_root,
            args.state_receipt,
        )
        admission = assert_sealable(authority, chain, args.release_root)
        result = {
            "schemaVersion": "release-preseal-admission.v1",
            "createdAt": utc_now(),
            **admission,
            "authority": authority,
            "candidateBoundOfflineChain": chain,
            "qualityGateArtifacts": gate_artifacts(),
        }
        encoded = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
        receipt_path = resolve_project_path(args.receipt)
        release_root = resolve_project_path(args.release_root)
        if receipt_path.is_relative_to(release_root):
            raise AdmissionFailure("admission receipt must stay outside the immutable release root")
        receipt_path.parent.mkdir(parents=True, exist_ok=True)
        with receipt_path.open("x", encoding="utf-8", newline="\n") as stream:
            stream.write(encoded)
        print(encoded, end="")
        return 0
    raise AdmissionFailure(f"unsupported command: {args.command}")


if __name__ == "__main__":
    raise SystemExit(main())
