from __future__ import annotations

import argparse
import json
import os
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from assemble_release_preseal import inventory, sha256_file
from quality_gates import (
    CHECKPOINT_RECEIPT_RELATIVE,
    CHECKPOINT_RECEIPT_SHA256,
    PROJECT_ROOT,
    SCHEMAS_ROOT,
    load_json,
    load_schemas,
)


WORKER_IDS = {
    "image.ocr.mineru",
    "content.deepseek",
    "speech.edge",
    "export.remnote",
    "export.anki",
}


def fail(message: str) -> None:
    raise RuntimeError(message)


def resolve(path: Path) -> Path:
    return path.resolve() if path.is_absolute() else (PROJECT_ROOT / path).resolve()


def relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT_ROOT.resolve()).as_posix()


def run_json(command: list[str], timeout: int = 20) -> dict[str, Any]:
    completed = subprocess.run(
        command,
        cwd=str(PROJECT_ROOT),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
        check=False,
        env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"},
    )
    if completed.returncode != 0:
        fail(f"command failed ({completed.returncode}): {command}: {completed.stderr.strip() or completed.stdout.strip()}")
    try:
        value = json.loads(completed.stdout.lstrip("\ufeff"))
    except json.JSONDecodeError as exc:
        fail(f"command did not emit exactly one JSON document: {command}: {exc}")
    if not isinstance(value, dict):
        fail(f"command JSON is not an object: {command}")
    return value


def run_text(command: list[str], timeout: int = 20) -> str:
    completed = subprocess.run(
        command,
        cwd=str(PROJECT_ROOT),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
        check=False,
        env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"},
    )
    output = (completed.stdout + "\n" + completed.stderr).strip()
    if completed.returncode != 0:
        fail(f"dependency probe failed ({completed.returncode}): {command}: {output}")
    return output.splitlines()[0].strip() if output else ""


def verify_stable_candidate_copy(root: Path) -> dict[str, Any]:
    checkpoint_path = PROJECT_ROOT / CHECKPOINT_RECEIPT_RELATIVE
    if sha256_file(checkpoint_path) != CHECKPOINT_RECEIPT_SHA256:
        fail("stable checkpoint receipt changed")
    checkpoint = load_json(checkpoint_path)
    source_root = (PROJECT_ROOT / "contracts/v1").resolve()
    candidate_root = root / "contracts/v1"
    mismatches: list[str] = []
    for item in checkpoint["files"]:
        source = Path(item["path"]).resolve()
        try:
            suffix = source.relative_to(source_root)
        except ValueError as exc:
            raise RuntimeError(f"checkpoint file is outside contracts/v1: {source}") from exc
        candidate = candidate_root / suffix
        if not candidate.is_file() or sha256_file(candidate) != item["sha256"].upper():
            mismatches.append(suffix.as_posix())
    if mismatches:
        fail("candidate stable contract copy mismatch: " + ", ".join(mismatches))
    return {"filesChecked": len(checkpoint["files"]), "mismatches": 0}


def verify_descriptions(root: Path, state: dict[str, Any]) -> list[dict[str, Any]]:
    schemas = load_schemas()
    describe_validator = schemas.validator(SCHEMAS_ROOT / "common/worker-describe.schema.json")
    results: list[dict[str, Any]] = []
    seen_workers: set[str] = set()
    for item in state["entrypoints"]:
        path = root / item["path"]
        if not path.is_file():
            fail(f"entrypoint missing: {item['path']}")
        actual_hash = sha256_file(path)
        if path.stat().st_size != item["bytes"] or actual_hash != item["sha256"].upper():
            fail(f"entrypoint hash/bytes mismatch: {item['path']}")
        component_id = item["componentId"]
        if component_id == "desktop":
            results.append({"componentId": component_id, "path": item["path"], "sha256": actual_hash, "describe": "not-applicable"})
            continue
        if component_id == "orchestrator.core":
            description = run_json([str(path), "describe"])
            if description.get("componentId") != component_id or description.get("version") != item["version"]:
                fail("orchestrator describe identity/version mismatch")
        elif component_id in WORKER_IDS:
            description = run_json([str(path), "describe", "--json"])
            errors = list(describe_validator.iter_errors(description))
            if errors or description.get("workerId") != component_id or description.get("workerVersion") != item["version"]:
                detail = errors[0].message if errors else "identity/version mismatch"
                fail(f"worker describe mismatch for {component_id}: {detail}")
            seen_workers.add(component_id)
        else:
            fail(f"unexpected component in state receipt: {component_id}")
        results.append(
            {
                "componentId": component_id,
                "path": item["path"],
                "sha256": actual_hash,
                "describe": "passed",
                "observedVersion": description.get("workerVersion", description.get("version")),
            }
        )
    if seen_workers != WORKER_IDS:
        fail(f"release-root worker set mismatch: expected={sorted(WORKER_IDS)} actual={sorted(seen_workers)}")
    return results


def verify_runtime_dependencies(root: Path) -> dict[str, Any]:
    settings_path = root / "desktop/appsettings.json"
    settings = load_json(settings_path)
    properties = ["NodePath", "RemnoteCliJsPath", "PythonPath", "EdgeTtsPath", "FfplayPath"]
    resolved: dict[str, Path] = {}
    for name in properties:
        path = Path(settings.get(name, "")).resolve()
        if not path.is_file() or not path.is_relative_to(root):
            fail(f"configured runtime path is missing or outside release root: {name}: {path}")
        resolved[name] = path
    versions = {
        "node": run_text([str(resolved["NodePath"]), "--version"]),
        "pythonEdgeTts": run_text(
            [str(resolved["PythonPath"]), "-c", "import sys,edge_tts;print(sys.version.split()[0]+'+'+edge_tts.__version__)"]
        ),
        "edgeTts": run_text([str(resolved["EdgeTtsPath"]), "--version"]),
        "ffplay": run_text([str(resolved["FfplayPath"]), "-version"]),
        "remnoteCli": run_text([str(resolved["NodePath"]), str(resolved["RemnoteCliJsPath"]), "--help"]),
    }
    return {
        "configuredPathsChecked": len(properties),
        "configuredPathsExistingInsideReleaseRoot": len(resolved),
        "versions": versions,
    }


def verify_desktop_startup_diagnostic(root: Path) -> dict[str, Any]:
    desktop_root = (root / "desktop").resolve()
    executable = desktop_root / "RemNoteQuickClipper.exe"
    result = run_json([str(executable), "--startup-diagnostic"], timeout=30)
    required_files = result.get("requiredFiles", [])
    if (
        result.get("schemaVersion") != "desktop-startup-diagnostic.v1"
        or result.get("result") != "PASS"
        or Path(result.get("baseDirectory", "")).resolve() != desktop_root
        or result.get("processArchitecture") != "X64"
        or result.get("is64BitProcess") is not True
        or result.get("selfContainedHost") is not True
        or result.get("configParsed") is not True
        or result.get("workDirectoryExpanded") is not True
        or result.get("packageReceiptParsed") is not True
        or result.get("packageFilesVerified") is not True
        or result.get("packageTreeVerified") is not True
        or result.get("productionWorkDirectoryCreated") is not False
        or result.get("namedPipeInvoked") is not False
        or result.get("externalConnections") != 0
        or result.get("remNoteInvoked") is not False
        or result.get("mineruInvoked") is not False
        or result.get("failures") != []
        or len(required_files) != 7
        or not all(item.get("exists") is True and item.get("bytes", 0) > 0 for item in required_files)
    ):
        fail(f"desktop startup diagnostic violates the no-side-effect preseal boundary: {result}")
    return {
        "status": "passed",
        "processArchitecture": result["processArchitecture"],
        "selfContainedHost": True,
        "configParsed": True,
        "workDirectoryExpanded": True,
        "packageReceiptParsed": True,
        "packageFilesVerified": True,
        "packageTreeVerified": True,
        "productionWorkDirectoryCreated": False,
        "namedPipeInvoked": False,
        "externalConnections": 0,
        "remNoteInvoked": False,
        "mineruInvoked": False,
        "requiredFiles": len(required_files),
    }


def verify_registry_candidate(root: Path, state: dict[str, Any]) -> dict[str, Any] | None:
    candidate = state.get("registryCandidate")
    if candidate is None:
        return None
    if candidate.get("authority") is not False:
        fail("preseal registry candidate is not explicitly marked non-authority")
    path = root / candidate["path"]
    if not path.is_file() or sha256_file(path) != candidate["sha256"].upper():
        fail("preseal registry candidate is missing or hash-mismatched")
    orchestrator = root / "orchestrator/RemNoteQuickClipper.Orchestrator.exe"
    result = run_json(
        [str(orchestrator), "validate-registry", "--registry", str(path), "--release-root", str(root)],
        timeout=60,
    )
    if result.get("status") != "valid" or result.get("workers") != 5:
        fail(f"preseal non-authority registry candidate gate failed: {result}")
    return {
        "path": candidate["path"],
        "sha256": sha256_file(path),
        "authority": False,
        "describeGate": result,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify an immutable unsealed RemNote Quick Clipper preseal root")
    parser.add_argument("--release-root", required=True, type=Path)
    parser.add_argument("--state-receipt", required=True, type=Path)
    parser.add_argument("--expected-state-receipt-sha256", required=True)
    parser.add_argument("--receipt", type=Path)
    args = parser.parse_args()

    root = resolve(args.release_root)
    state_path = resolve(args.state_receipt)
    if not root.is_dir() or not state_path.is_file():
        fail("release root or state receipt is missing")
    if sha256_file(state_path) != args.expected_state_receipt_sha256.upper():
        fail("state receipt hash mismatch")
    state = load_json(state_path)
    if resolve(Path(state["releaseRoot"])) != root:
        fail("state receipt release root mismatch")
    if state.get("releaseManifest") is not None or state.get("candidateHash") is not None:
        fail("preseal verifier refuses sealed candidates")
    if (root / "release-manifest.v1.json").exists():
        fail("unsealed preseal unexpectedly contains release-manifest.v1.json")

    before_files, before_bytes, before_tree = inventory(root)
    layout = state["layout"]
    if (before_files, before_bytes, before_tree) != (
        layout["files"],
        layout["bytes"],
        layout["postGateTreeSha256"].upper(),
    ):
        fail("release inventory does not match state receipt")
    stable = verify_stable_candidate_copy(root)
    authority = load_json(root / "contracts/v1/registry/worker-registry.v1.json")
    if authority.get("workers") != []:
        fail("authority registry is not empty")
    descriptions = verify_descriptions(root, state)
    registry_candidate = verify_registry_candidate(root, state)
    runtimes = verify_runtime_dependencies(root)
    desktop_startup = verify_desktop_startup_diagnostic(root)
    if runtimes.get("configuredPathsChecked") != 5 or runtimes.get("configuredPathsExistingInsideReleaseRoot") != 5:
        fail("runtime dependency gate did not verify all five configured paths")
    after_files, after_bytes, after_tree = inventory(root)
    if (after_files, after_bytes, after_tree) != (before_files, before_bytes, before_tree):
        fail("release-root probes changed the immutable tree")

    result = {
        "schemaVersion": "release-preseal-black-box-verification-receipt.v1",
        "createdAt": datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z"),
        "status": "passed-unsealed-no-go",
        "releaseRoot": relative(root),
        "stateReceipt": {"path": relative(state_path), "sha256": sha256_file(state_path)},
        "inventory": {"files": after_files, "bytes": after_bytes, "treeSha256": after_tree, "hiddenWrites": 0},
        "stableContracts": stable,
        "authorityRegistryEntries": 0,
        "registryCandidate": registry_candidate,
        "entrypoints": descriptions,
        "runtimeDependencies": runtimes,
        "desktopStartupDiagnostic": desktop_startup,
        "releaseManifest": None,
        "candidateHash": None,
        "externalProductionApisInvoked": False,
        "releaseDecision": "NO-GO",
    }
    encoded = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
    if args.receipt:
        receipt_path = resolve(args.receipt)
        if receipt_path.is_relative_to(root):
            fail("verification receipt must be outside the immutable release root")
        receipt_path.parent.mkdir(parents=True, exist_ok=True)
        with receipt_path.open("x", encoding="utf-8", newline="\n") as stream:
            stream.write(encoded)
    print(encoded, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
