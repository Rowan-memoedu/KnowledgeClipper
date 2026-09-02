from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


PROJECT = Path(__file__).resolve().parents[1]
STAMP = "20260902T012900Z"
RELEASE_ID = f"knowledge-clipper-authority-v1.2.40-{STAMP}"
RELEASE = PROJECT / f"releases/{RELEASE_ID}/win-x64"
AUTHORITY = PROJECT / f"contracts/authority/worker-registry/v1.2.40-{STAMP}"
CHECKPOINT = PROJECT / "contracts/core/v2.10/checkpoints/v2.10.5-20260902T012600Z/checkpoint.json"
CHECKPOINT_SHA256 = "D313242ED851AD93AEFBE64BDDB7C6ADA767630936AA63ACE0ECB65718693FCD"
FORMAL = PROJECT / f"receipts/authority-assembly/{STAMP}/{RELEASE_ID}.receipt.json"
ADMISSION_ROOT = PROJECT / "receipts/authority-admission/20260902T013500Z-v1.2.40-final-process"
PROCESS = ADMISSION_ROOT / "core-process-named-pipe.receipt.json"
REGISTRY_VALIDATION = ADMISSION_ROOT / "validate-registry.retry1.json"
OUTPUT = ADMISSION_ROOT / "post-assembly-independent-authority-admission.receipt.json"
V1239_RELEASE = PROJECT / "releases/knowledge-clipper-authority-v1.2.39-20260901T130618Z/win-x64"
V1239_NO_GO = PROJECT / "receipts/quality-authority-admission/20260902T010057Z-v1.2.39-no-go/post-assembly-independent-authority-admission.receipt.json"
V1239_INVALIDATION = PROJECT / "receipts/authority-invalidation/20260902T012100Z/knowledge-clipper-authority-v1.2.39.runtime-no-go.json"


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def load(path: Path) -> dict[str, Any]:
    require(path.is_file(), f"missing input: {path}")
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def evidence(path: Path) -> dict[str, Any]:
    return {"path": relative(path), "bytes": path.stat().st_size, "sha256": sha256_file(path)}


def inventory(root: Path) -> dict[str, Any]:
    rows: list[dict[str, Any]] = []
    for path in root.rglob("*"):
        if path.is_file():
            rows.append({"path": path.relative_to(root).as_posix(), "bytes": path.stat().st_size, "sha256": sha256_file(path)})
    rows.sort(key=lambda item: item["path"])
    payload = "\n".join(f"{item['path']}\t{item['bytes']}\t{item['sha256'].lower()}" for item in rows).encode("utf-8")
    return {
        "root": str(root),
        "files": len(rows),
        "bytes": sum(item["bytes"] for item in rows),
        "treeSha256": hashlib.sha256(payload).hexdigest().upper(),
        "algorithm": "sort relative paths ordinal; path TAB bytes TAB lowercase SHA-256; LF; SHA-256 UTF-8",
    }


def main() -> int:
    require(not OUTPUT.exists(), f"append-only output exists: {OUTPUT}")
    formal = load(FORMAL)
    checkpoint = load(CHECKPOINT)
    process = load(PROCESS)
    registry_validation = load(REGISTRY_VALIDATION)
    registry = load(AUTHORITY / "worker-registry.v1.json")
    dependency = load(AUTHORITY / "dependency-manifest.v1.json")
    runtime_manifest = load(AUTHORITY / "orchestrator-runtime-manifest.v1.json")
    assembly = load(AUTHORITY / "assembly-receipt.v1.json")
    path_gate = load(AUTHORITY / "assembly-path-gate.v1.json")
    v1239_no_go = load(V1239_NO_GO)
    v1239_invalidation = load(V1239_INVALIDATION)

    require(sha256_file(CHECKPOINT) == CHECKPOINT_SHA256, "checkpoint SHA drift")
    require(formal["status"] == "candidate-built" and formal["result"]["authorityTargetsCreated"] == 1 and formal["result"]["candidateTargetsCreated"] == 0, "formal V3 receipt drift")
    require(formal["v3Assembler"]["sha256Before"] == formal["v3Assembler"]["sha256After"] == "69D6F6A2E0EB911A1D5A02C25820E89C0AFCAB20F5AC52ABBAE2DC89C486FF37", "V3 assembler hash drift")
    inner = formal["result"]["innerReceipt"]
    require(inner["releaseId"] == RELEASE_ID and inner["coreContracts"]["checkpointSha256"] == CHECKPOINT_SHA256, "formal release/checkpoint identity drift")

    release_inventory = inventory(RELEASE)
    authority_inventory = inventory(AUTHORITY)
    require(release_inventory["files"] == inner["release"]["files"] and release_inventory["bytes"] == inner["release"]["bytes"] and release_inventory["treeSha256"] == inner["release"]["treeSha256"], "release tree drift")
    require(dependency["files"] == release_inventory["files"] and dependency["bytes"] == release_inventory["bytes"] and dependency["treeSha256"] == release_inventory["treeSha256"], "dependency manifest release tree drift")
    require(authority_inventory["files"] == 5, "authority file cardinality drift")
    require(registry["registryVersion"] == "1.2.40" and len(registry["workers"]) == 5, "registry identity/cardinality drift")
    require(registry_validation["status"] == "valid" and registry_validation["registryVersion"] == "1.2.40" and registry_validation["workers"] == 5, "final registry validation drift")
    require(assembly["status"] == "candidate-built-offline" and assembly["registryValidation"]["workers"] == 5, "assembly receipt drift")
    require(path_gate["status"] == "passed" and path_gate["pathCount"] == 5 and path_gate["allBytesAndHashesMatch"] is True, "assembly path gate drift")

    runtime_root = RELEASE / "orchestrator"
    runtime_inventory = inventory(runtime_root)
    require(runtime_manifest["component"]["version"] == "2.10.3.0" and runtime_manifest["checkpoint"]["sha256"] == CHECKPOINT_SHA256, "runtime manifest identity drift")
    require(runtime_inventory["files"] == runtime_manifest["fileCount"] and runtime_inventory["bytes"] == runtime_manifest["totalBytes"] and runtime_inventory["treeSha256"] == runtime_manifest["treeSha256"], "runtime tree drift")
    manifest_files = {item["path"]: item for item in runtime_manifest["files"]}
    for rel in ("KnowledgeClipper.Orchestrator.exe", "KnowledgeClipper.Orchestrator.dll"):
        path = runtime_root / rel
        require(manifest_files[rel]["bytes"] == path.stat().st_size and manifest_files[rel]["sha256"] == sha256_file(path), f"runtime manifest file drift: {rel}")

    final_exe = runtime_root / "KnowledgeClipper.Orchestrator.exe"
    require(process["status"] == "passed" and process["processBoundary"] == "published KnowledgeClipper.Orchestrator.exe serve", "post-assembly final process did not pass")
    require(Path(process["orchestratorRuntime"]["path"]).resolve() == final_exe.resolve(), "process did not execute final release EXE")
    require(process["orchestratorRuntime"]["sha256"].upper() == sha256_file(final_exe) == "DD84BEDDCE083D8F62284189408A97C0549B7332390DFF69B7E2B4586D75C82C", "tested final EXE SHA drift")
    matrix = process["finalProcessProtocolMatrix"]
    require([item["protocolVersion"] for item in matrix] == ["1.5", "1.6", "1.7"] and all(item["startup"] == item["typedAction"] == "passed" for item in matrix), "final process protocol matrix drift")
    require(process["probe"]["externalWrites"] == 0 and process["contentSettings"]["externalWrites"] == 0, "IPC 1.5/1.6 effect boundary drift")
    typed = process["typedPublicSettings"]
    require(typed["remnote"]["externalWrites"] == 0 and typed["speechVoices"]["settingsMutations"] == 0 and typed["speechVoices"]["physicalSpeakerPlayback"] is False, "IPC 1.7 read-only boundary drift")
    require(typed["speechPreview"]["authorizationDriftRejected"] is True and typed["speechPreview"]["fakeOffline"] is True and typed["speechPreview"]["physicalSpeakerInvocationsDuringCanary"] == 0, "IPC 1.7 preview boundary drift")

    packaged_checkpoint = RELEASE / relative(CHECKPOINT)
    require(packaged_checkpoint.is_file() and sha256_file(packaged_checkpoint) == CHECKPOINT_SHA256, "packaged checkpoint drift")
    binding_results = []
    for binding in checkpoint["fileBindings"]:
        source = PROJECT / binding["path"]
        require(source.is_file() and sha256_file(source) == binding["sha256"].upper(), f"source fileBinding drift: {binding['path']}")
        packaged = None
        if binding["path"].startswith("contracts/core/v2.10/"):
            packaged = RELEASE / binding["path"]
            require(packaged.is_file() and sha256_file(packaged) == binding["sha256"].upper(), f"packaged fileBinding drift: {binding['path']}")
        binding_results.append({"path": binding["path"], "source": "matched", "packaged": "matched" if packaged else "not-packaged-by-design"})

    frozen = load(PROJECT / checkpoint["verification"]["frozenV1"]["receipt"])
    require(frozen["status"] == "passed" and frozen["filesChecked"] == 21 and frozen["mismatches"] == 0, "frozen v1 drift")
    require(v1239_no_go["status"] == "failed-no-go" and sha256_file(V1239_NO_GO) == "2A9F1301BB18214CFB86EAE7DD02F845571A3E4BEE73E9A46ABC5FAD595EA0FB", "v1.2.39 Quality NO-GO drift")
    require(v1239_invalidation["status"] == "invalidated-no-go" and v1239_invalidation["immutableAuthorityPreserved"] is True and v1239_invalidation["immutableReleasePreserved"] is True, "v1.2.39 invalidation drift")
    v1239_inventory = inventory(V1239_RELEASE)
    require(v1239_inventory["files"] == 596 and v1239_inventory["bytes"] == 268681365 and v1239_inventory["treeSha256"] == "5F6F87792C67B30FC1BE321359E9E114252D06B48C3542E7628BE5C6752937EF", "v1.2.39 immutable release drift")

    result = {
        "schemaVersion": "knowledge-clipper-post-assembly-independent-authority-admission.v1",
        "generatedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "passed-core-authority-offline",
        "releaseId": RELEASE_ID,
        "decision": {"authority": "PASS-OFFLINE", "integratedCandidate": "NOT_RUN", "userHandTestReady": False},
        "checkpoint": evidence(CHECKPOINT),
        "formalAssembly": evidence(FORMAL),
        "release": release_inventory,
        "authority": authority_inventory,
        "runtime": {"inventory": runtime_inventory, "manifest": evidence(AUTHORITY / "orchestrator-runtime-manifest.v1.json"), "exe": evidence(final_exe), "dll": evidence(runtime_root / "KnowledgeClipper.Orchestrator.dll")},
        "finalProcess": {"receipt": evidence(PROCESS), "registryValidation": evidence(REGISTRY_VALIDATION), "protocols": ["1.5", "1.6", "1.7"], "matrix": matrix},
        "fileBindings": {"checked": len(binding_results), "matched": len(binding_results), "items": binding_results},
        "frozenV1": frozen,
        "v1239": {"qualityNoGo": evidence(V1239_NO_GO), "invalidation": evidence(V1239_INVALIDATION), "immutableRelease": v1239_inventory},
        "effectBoundary": {"workerCanary": "Fake/offline only", "externalApiCalls": 0, "remnoteWrites": 0, "ankiWrites": 0, "mineruCalls": 0, "physicalSpeakerInvocations": 0},
    }
    OUTPUT.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": result["status"], "receipt": evidence(OUTPUT)}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
