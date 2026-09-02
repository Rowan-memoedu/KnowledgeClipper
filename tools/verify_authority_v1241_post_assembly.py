from __future__ import annotations

import hashlib
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[1]
STAMP = "20260902T042000Z"
RELEASE_ID = f"knowledge-clipper-authority-v1.2.41-{STAMP}"
RELEASE = PROJECT / f"releases/{RELEASE_ID}/win-x64"
AUTHORITY = PROJECT / f"contracts/authority/worker-registry/v1.2.41-{STAMP}"
CHECKPOINT = PROJECT / "contracts/core/v2.10/checkpoints/v2.10.6-20260902T041800Z/checkpoint.json"
CHECKPOINT_SHA256 = "20AF0975FF176B6D46249B98AC36D72C74C7A494965AF3A69DE4C7FF52235D79"
FORMAL = PROJECT / f"receipts/authority-assembly/{STAMP}/{RELEASE_ID}.receipt.json"
ADMISSION = PROJECT / "receipts/authority-admission/20260902T042200Z-v1.2.41-entrypoint-resolver-final-process"
PROCESS = ADMISSION / "core-process-named-pipe.receipt.json"
REGISTRY_VALIDATION = ADMISSION / "validate-registry.receipt.json"
OUTPUT = ADMISSION / "post-assembly-independent-authority-admission.receipt.json"
V1240_RELEASE = PROJECT / "releases/knowledge-clipper-authority-v1.2.40-20260902T012900Z/win-x64"
V1240_AUTHORITY = PROJECT / "contracts/authority/worker-registry/v1.2.40-20260902T012900Z"
V1240_QUALITY = PROJECT / "receipts/quality-integrated-candidate/20260902T040142Z-v1240-shared-protocol-no-go/candidate-stateful-no-go.receipt.json"
V1240_INVALIDATION = PROJECT / "receipts/authority-invalidation/20260902T042200Z/knowledge-clipper-authority-v1.2.40.multi-file-entrypoint-no-go.json"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def evidence(path: Path) -> dict[str, object]:
    require(path.is_file(), f"missing evidence: {path}")
    return {"path": relative(path), "bytes": path.stat().st_size, "sha256": sha256(path)}


def load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_once(path: Path, value: object) -> None:
    require(not path.exists(), f"append-only target exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def inventory(root: Path) -> dict[str, object]:
    rows = []
    for path in root.rglob("*"):
        if path.is_file():
            rows.append((path.relative_to(root).as_posix(), path.stat().st_size, sha256(path)))
    rows.sort()
    payload = "\n".join(f"{name}\t{size}\t{digest.lower()}" for name, size, digest in rows).encode()
    return {"root": str(root), "fileCount": len(rows), "totalBytes": sum(row[1] for row in rows), "treeSha256": hashlib.sha256(payload).hexdigest().upper()}


def main() -> int:
    require(not OUTPUT.exists() and not V1240_INVALIDATION.exists() and not REGISTRY_VALIDATION.exists(), "append-only verification target exists")
    formal = load(FORMAL)
    checkpoint = load(CHECKPOINT)
    process = load(PROCESS)
    registry = load(AUTHORITY / "worker-registry.v1.json")
    dependency = load(AUTHORITY / "dependency-manifest.v1.json")
    runtime_manifest = load(AUTHORITY / "orchestrator-runtime-manifest.v1.json")
    assembly = load(AUTHORITY / "assembly-receipt.v1.json")
    path_gate = load(AUTHORITY / "assembly-path-gate.v1.json")

    require(sha256(CHECKPOINT) == CHECKPOINT_SHA256, "checkpoint drifted")
    require(formal["status"] == "candidate-built" and formal["result"]["authorityTargetsCreated"] == 1 and formal["result"]["candidateTargetsCreated"] == 0, "formal assembly receipt drifted")
    inner = formal["result"]["innerReceipt"]
    require(inner["releaseId"] == RELEASE_ID and inner["coreContracts"]["checkpointSha256"] == CHECKPOINT_SHA256, "release/checkpoint identity drifted")

    release_inventory = inventory(RELEASE)
    authority_inventory = inventory(AUTHORITY)
    require(release_inventory["fileCount"] == inner["release"]["files"] and release_inventory["totalBytes"] == inner["release"]["bytes"] and release_inventory["treeSha256"] == inner["release"]["treeSha256"], "release tree drifted")
    require(dependency["files"] == release_inventory["fileCount"] and dependency["bytes"] == release_inventory["totalBytes"] and dependency["treeSha256"] == release_inventory["treeSha256"], "dependency tree drifted")
    require(authority_inventory["fileCount"] == 5 and registry["registryVersion"] == "1.2.41" and len(registry["workers"]) == 5, "authority registry drifted")
    require(assembly["status"] == "candidate-built-offline" and path_gate["status"] == "passed" and path_gate["allBytesAndHashesMatch"] is True, "assembly path gate failed")

    final_exe = RELEASE / "orchestrator/KnowledgeClipper.Orchestrator.exe"
    completed = subprocess.run([str(final_exe), "validate-registry", "--registry", str(AUTHORITY / "worker-registry.v1.json"), "--release-root", str(RELEASE)], capture_output=True, text=True, check=True)
    registry_validation = json.loads(completed.stdout)
    require(registry_validation["status"] == "valid" and registry_validation["registryVersion"] == "1.2.41" and registry_validation["workers"] == 5, "final registry validation failed")
    write_once(REGISTRY_VALIDATION, registry_validation)

    runtime_root = RELEASE / "orchestrator"
    runtime_inventory = inventory(runtime_root)
    require(runtime_manifest["component"]["version"] == "2.10.6.0" and runtime_manifest["checkpoint"]["sha256"] == CHECKPOINT_SHA256, "runtime manifest identity drifted")
    require(runtime_inventory["fileCount"] == runtime_manifest["fileCount"] and runtime_inventory["totalBytes"] == runtime_manifest["totalBytes"] and runtime_inventory["treeSha256"] == runtime_manifest["treeSha256"], "runtime tree drifted")
    require(process["status"] == "passed" and process["processBoundary"] == "published KnowledgeClipper.Orchestrator.exe serve", "final release EXE process failed")
    require(Path(process["orchestratorRuntime"]["path"]).resolve() == final_exe.resolve() and process["orchestratorRuntime"]["sha256"].upper() == sha256(final_exe) == "C71AA9701C3D818BE9C37524E164443C7E369C5B9AA426D7A31AB22A8D613A61", "tested final EXE identity drifted")
    matrix = process["finalProcessProtocolMatrix"]
    require([item["protocolVersion"] for item in matrix] == ["1.5", "1.6", "1.7"] and all(item["startup"] == item["typedAction"] == "passed" for item in matrix), "final IPC matrix failed")
    require(process["probe"]["declaredFileCount"] == 4 and process["probe"]["externalWrites"] == 0, "final four-file Anki probe failed")
    require(process["contentSettings"]["externalWrites"] == 0 and process["typedPublicSettings"]["remnote"]["externalWrites"] == 0 and process["typedPublicSettings"]["speechVoices"]["settingsMutations"] == 0 and process["typedPublicSettings"]["speechPreview"]["physicalSpeakerInvocationsDuringCanary"] == 0, "effect boundary drifted")

    packaged_checkpoint = RELEASE / relative(CHECKPOINT)
    require(packaged_checkpoint.is_file() and sha256(packaged_checkpoint) == CHECKPOINT_SHA256, "packaged checkpoint drifted")
    binding_results = []
    for binding in checkpoint["fileBindings"]:
        source = PROJECT / binding["path"]
        require(source.is_file() and sha256(source) == binding["sha256"].upper(), f"source binding drifted: {binding['path']}")
        packaged = binding["path"].startswith("contracts/core/v2.10/")
        if packaged:
            target = RELEASE / binding["path"]
            require(target.is_file() and sha256(target) == binding["sha256"].upper(), f"packaged binding drifted: {binding['path']}")
        binding_results.append({"path": binding["path"], "source": "matched", "packaged": "matched" if packaged else "not-packaged-by-design"})

    v1240_release = inventory(V1240_RELEASE)
    v1240_authority = inventory(V1240_AUTHORITY)
    require(sha256(V1240_QUALITY) == "16A419037B1DB096D88DFD46B7CD1449B97A94E711C496B5A64399E6D3A6A51D", "v1.2.40 Quality NO-GO drifted")
    require(v1240_release["fileCount"] == 595 and v1240_release["totalBytes"] == 268725358 and v1240_release["treeSha256"] == "2892F056A612B4555F4E54C080BD6008411CDAA7E5A86FB0A2513FFF3306DF39", "v1.2.40 release mutated")
    require(v1240_authority["fileCount"] == 5 and v1240_authority["treeSha256"] == "36DD7AF3289B6137E8DB2DB31FB77336A59C80512651A197EF98A350053DFBF5", "v1.2.40 authority mutated")

    now = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    write_once(V1240_INVALIDATION, {
        "schemaVersion": "knowledge-clipper-authority-invalidation.v1", "createdAt": now, "status": "invalidated-no-go",
        "releaseId": "knowledge-clipper-authority-v1.2.40-20260902T012900Z", "reason": "Valid four-file anki-connection-status artifacts were rejected by a hardcoded single-payload assumption.",
        "qualityNoGo": evidence(V1240_QUALITY), "immutableAuthorityPreserved": True, "immutableReleasePreserved": True,
        "releaseInventory": v1240_release, "authorityInventory": v1240_authority, "supersededBy": RELEASE_ID,
    })

    result = {
        "schemaVersion": "knowledge-clipper-post-assembly-independent-authority-admission.v1", "generatedAt": now,
        "status": "passed-core-authority-offline", "releaseId": RELEASE_ID,
        "decision": {"authority": "PASS-OFFLINE", "integratedCandidate": "NOT_RUN", "userHandTestReady": False},
        "checkpoint": evidence(CHECKPOINT), "formalAssembly": evidence(FORMAL), "release": release_inventory, "authority": authority_inventory,
        "runtime": {"inventory": runtime_inventory, "manifest": evidence(AUTHORITY / "orchestrator-runtime-manifest.v1.json"), "exe": evidence(final_exe), "dll": evidence(runtime_root / "KnowledgeClipper.Orchestrator.dll")},
        "finalProcess": {"receipt": evidence(PROCESS), "registryValidation": evidence(REGISTRY_VALIDATION), "protocols": ["1.5", "1.6", "1.7"], "declaredAnkiFiles": 4},
        "fileBindings": {"checked": len(binding_results), "matched": len(binding_results), "items": binding_results},
        "v1240": {"qualityNoGo": evidence(V1240_QUALITY), "invalidation": evidence(V1240_INVALIDATION), "immutableRelease": v1240_release, "immutableAuthority": v1240_authority},
        "effectBoundary": {"workerCanary": "Fake/offline only", "externalApiCalls": 0, "remnoteWrites": 0, "ankiWrites": 0, "mineruCalls": 0, "physicalSpeakerInvocations": 0},
    }
    write_once(OUTPUT, result)
    print(json.dumps({"status": result["status"], "receipt": evidence(OUTPUT), "v1240Invalidation": evidence(V1240_INVALIDATION)}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
