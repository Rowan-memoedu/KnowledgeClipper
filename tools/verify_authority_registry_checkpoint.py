from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
from pathlib import Path
from typing import Any


PROJECT_ROOT = Path(__file__).resolve().parents[1]
OLD_AUTHORITY_SHA256 = "ACB4BA01ADBC0A410AAE7050FEC04F527C47EE6D0A98F30B73E7AC2B0E35F6E1"


def fail(message: str) -> None:
    raise RuntimeError(message)


def load_json(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        fail(f"JSON document is not an object: {path}")
    return value


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def tree_snapshot(root: Path) -> tuple[int, int, str]:
    lines: list[str] = []
    files = 0
    total_bytes = 0
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        size = path.stat().st_size
        digest = sha256_file(path)
        files += 1
        total_bytes += size
        lines.append(f"{relative}\t{size}\t{digest}\n")
    return files, total_bytes, hashlib.sha256("".join(lines).encode("utf-8")).hexdigest().upper()


def verify_file(root: Path, item: dict[str, Any], context: str) -> None:
    path = (root / str(item["path"])).resolve()
    if not path.is_file():
        fail(f"{context} is missing: {path}")
    if path.stat().st_size != int(item["bytes"]):
        fail(f"{context} byte count mismatch: {path}")
    if sha256_file(path) != str(item["sha256"]).upper():
        fail(f"{context} SHA-256 mismatch: {path}")


def run_json(command: list[str]) -> dict[str, Any]:
    completed = subprocess.run(
        command,
        cwd=str(PROJECT_ROOT),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=20,
        check=False,
    )
    if completed.returncode != 0:
        fail(f"command failed ({completed.returncode}): {command}: {completed.stderr.strip() or completed.stdout.strip()}")
    try:
        value = json.loads(completed.stdout.lstrip("\ufeff"))
    except json.JSONDecodeError as exception:
        fail(f"command did not emit one JSON object: {command}: {exception}")
    if not isinstance(value, dict):
        fail(f"command output is not a JSON object: {command}")
    return value


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--checkpoint-root", required=True)
    parser.add_argument("--release-root", required=True)
    parser.add_argument("--orchestrator", required=True)
    args = parser.parse_args()

    checkpoint_root = Path(args.checkpoint_root).resolve()
    release_root = Path(args.release_root).resolve()
    orchestrator = Path(args.orchestrator).resolve()
    registry_path = checkpoint_root / "worker-registry.v1.json"
    dependency_path = checkpoint_root / "dependency-manifest.v1.json"
    registry = load_json(registry_path)
    dependencies = load_json(dependency_path)

    old_authority = PROJECT_ROOT / "contracts/v1/registry/worker-registry.v1.json"
    if sha256_file(old_authority) != OLD_AUTHORITY_SHA256:
        fail("frozen empty authority registry changed")
    stable = load_json(PROJECT_ROOT / "receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json")
    stable_mismatches = 0
    for item in stable["files"]:
        path = Path(str(item["path"]))
        if sha256_file(path) != str(item["sha256"]).upper():
            stable_mismatches += 1
    if stable_mismatches:
        fail(f"frozen stable checkpoint has {stable_mismatches} mismatches")

    candidate_path = release_root / "registry-candidate/v1/worker-registry.v1.json"
    if registry_path.read_bytes() != candidate_path.read_bytes():
        fail("authority registry is not byte-identical to the admitted release candidate")

    admission = run_json([
        str(orchestrator),
        "validate-registry",
        "--registry",
        str(registry_path),
        "--release-root",
        str(release_root),
    ])
    if admission.get("status") != "valid" or admission.get("workers") != 5:
        fail(f"production RegistryLoader admission failed: {admission}")

    verify_file(release_root, dependencies["dispatcher"], "dispatcher")
    verify_file(release_root, dependencies["runtimeConfiguration"], "runtime configuration")
    release_files, release_bytes, release_tree = tree_snapshot(release_root)
    if release_tree != str(dependencies["release"]["treeSha256"]).upper():
        fail("release tree hash mismatch")

    registry_workers = {str(item["workerId"]): item for item in registry["workers"]}
    dependency_workers = {str(item["workerId"]): item for item in dependencies["workers"]}
    if set(registry_workers) != set(dependency_workers) or len(registry_workers) != 5:
        fail("registry and dependency worker sets differ")

    live_describes = 0
    local_dependencies = 0
    tree_dependencies = 0
    for worker_id, registered in registry_workers.items():
        declared = dependency_workers[worker_id]
        if registered["stages"] != declared["operations"]:
            fail(f"operation set mismatch: {worker_id}")
        if registered["inputKinds"] != declared["inputKinds"] or registered["outputKinds"] != declared["outputKinds"]:
            fail(f"artifact kind set mismatch: {worker_id}")
        if registered["entrypoint"]["path"] != declared["entrypoint"]["path"]:
            fail(f"entrypoint path mismatch: {worker_id}")
        verify_file(release_root, declared["entrypoint"], f"worker entrypoint {worker_id}")
        verify_file(checkpoint_root, declared["describe"], f"describe snapshot {worker_id}")
        snapshot = load_json(checkpoint_root / str(declared["describe"]["path"]))
        executable = release_root / str(declared["entrypoint"]["path"])
        live = run_json([str(executable), *registered["entrypoint"]["args"], "describe", "--json"])
        if live != snapshot:
            fail(f"live describe differs from checkpoint snapshot: {worker_id}")
        live_describes += 1
        for dependency in declared["localDependencies"]:
            if dependency.get("kind") == "tree":
                tree_root = release_root / str(dependency["path"])
                files, total_bytes, digest = tree_snapshot(tree_root)
                if files != int(dependency["files"]) or total_bytes != int(dependency["bytes"]) or digest != str(dependency["sha256"]).upper():
                    fail(f"tree dependency mismatch: {worker_id}: {tree_root}")
                tree_dependencies += 1
            else:
                verify_file(release_root, dependency, f"local dependency {worker_id}")
                local_dependencies += 1

    result = {
        "schemaVersion": "authority-worker-registry-verification.v1",
        "status": "passed",
        "checkpointRoot": checkpoint_root.relative_to(PROJECT_ROOT).as_posix(),
        "registry": {
            "schemaVersion": registry["schemaVersion"],
            "registryVersion": registry["registryVersion"],
            "sha256": sha256_file(registry_path),
            "workers": len(registry_workers),
            "candidateByteIdentical": True,
        },
        "release": {
            "root": release_root.relative_to(PROJECT_ROOT).as_posix(),
            "files": release_files,
            "bytes": release_bytes,
            "treeSha256": release_tree,
        },
        "admission": admission,
        "liveDescribes": live_describes,
        "localFileDependencies": local_dependencies,
        "treeDependencies": tree_dependencies,
        "stableFilesChecked": len(stable["files"]),
        "stableMismatches": stable_mismatches,
        "oldEmptyAuthoritySha256": sha256_file(old_authority),
        "externalProductionApisInvoked": False,
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
