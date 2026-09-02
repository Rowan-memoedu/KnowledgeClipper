from __future__ import annotations

import argparse
import json
import os
from datetime import datetime, timezone
from pathlib import Path

from assemble_integrated_desktop_v1240_quality import (
    AssemblyError,
    copy_once,
    copy_tree,
    inventory,
    load,
    sha256,
    verify_desktop_build,
)


PROJECT = Path(__file__).resolve().parents[1]
AUTHORITY_ID = "knowledge-clipper-authority-v1.2.41-20260902T042000Z"
AUTHORITY_TREE = "7FB6EC31A29C7A4CB912AFA27D8A06D3B5F7205BABDBABE21DF72EA0E913A3B4"
AUTHORITY_FILES = 595
AUTHORITY_BYTES = 268_726_758
CHECKPOINT_SHA = "20AF0975FF176B6D46249B98AC36D72C74C7A494965AF3A69DE4C7FF52235D79"
REGISTRY_SHA = "FBF1A3E30462A7C05269E47D510153A0165D502E204ABB50936E332BA07129A7"
FORMAL_SHA = "BC004DF05061B16E846E3038556B172C7886FA257B7F3C192E933394864B98BE"
ADMISSION_SHA = "118342DE7BD3BF9F943C04055436CD33D60C1E5C9FBA672D16C6B82250F599E8"
INVALIDATION_SHA = "528105FB7C7A37FB647828250466E07324D30663229AA3BF0145E54874C6C3F5"
MIGRATION_SHA = "CE9482FAD6B963E0E62D679D6A469F659730FD6A745201125B15E28EB01B1D94"
PRODUCTION_ROUTES_SHA = "E2489EA20359813C08EA47E19C5EE577F14A42A45DCB3418A9C97310F6DDACA7"
SHARED_PROTOCOL_SHA = "B338A7C7950A97E4DF9E832E9D587620D66218381AB760E24FD08C396D11B7C5"


def bound(path: Path, expected_sha256: str) -> None:
    if not path.is_file() or sha256(path) != expected_sha256:
        raise AssemblyError(f"bound source drift: {path}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--desktop-root", required=True, type=Path)
    parser.add_argument("--candidate-root", required=True, type=Path)
    parser.add_argument("--candidate-id", required=True)
    parser.add_argument("--pipe-name", required=True)
    parser.add_argument("--output-receipt", required=True, type=Path)
    args = parser.parse_args()

    desktop = args.desktop_root.resolve()
    candidate = args.candidate_root.resolve()
    releases = (PROJECT / "releases").resolve()
    receipt_output = args.output_receipt.resolve()
    if not candidate.is_relative_to(releases) or candidate.exists():
        raise AssemblyError("candidate must be a new append-only directory below releases")
    if receipt_output.exists():
        raise AssemblyError("append-only assembly receipt exists")
    if not args.pipe_name.startswith("KnowledgeClipper.Quality.Stateful.") or any(char in args.pipe_name for char in "\\/"):
        raise AssemblyError("pipe name must be a unique Quality stateful identifier")

    desktop_build = verify_desktop_build(desktop)
    authority_source = PROJECT / "releases" / AUTHORITY_ID / "win-x64"
    authority_meta = PROJECT / "contracts/authority/worker-registry/v1.2.41-20260902T042000Z"
    checkpoint = PROJECT / "contracts/core/v2.10/checkpoints/v2.10.6-20260902T041800Z/checkpoint.json"
    formal = PROJECT / "receipts/authority-assembly/20260902T042000Z" / f"{AUTHORITY_ID}.receipt.json"
    admission = PROJECT / "receipts/authority-admission/20260902T042200Z-v1.2.41-entrypoint-resolver-final-process/post-assembly-independent-authority-admission.receipt.json"
    invalidation = PROJECT / "receipts/authority-invalidation/20260902T042200Z/knowledge-clipper-authority-v1.2.40.multi-file-entrypoint-no-go.json"
    migration = PROJECT / "receipts/desktop-application-migration/20260901T103629Z/desktop-application-migration.superseding-receipt.json"
    production_routes = PROJECT / "receipts/desktop-production-routes/20260902T023600Z/desktop-production-routes.receipt.json"
    shared_protocol = PROJECT / "receipts/desktop-shared-module-action-protocol/20260902T035124Z/desktop-shared-module-action-protocol.receipt.json"
    registry = authority_meta / "worker-registry.v1.json"
    for path, expected in (
        (checkpoint, CHECKPOINT_SHA),
        (formal, FORMAL_SHA),
        (admission, ADMISSION_SHA),
        (invalidation, INVALIDATION_SHA),
        (migration, MIGRATION_SHA),
        (production_routes, PRODUCTION_ROUTES_SHA),
        (shared_protocol, SHARED_PROTOCOL_SHA),
        (registry, REGISTRY_SHA),
    ):
        bound(path, expected)

    authority_inventory = inventory(authority_source, include_files=False)
    if (
        authority_inventory["fileCount"],
        authority_inventory["totalBytes"],
        authority_inventory["treeSha256"],
    ) != (AUTHORITY_FILES, AUTHORITY_BYTES, AUTHORITY_TREE):
        raise AssemblyError("Authority v1.2.41 release tree drift")
    registry_json = load(registry)
    if registry_json.get("registryVersion") != "1.2.41" or len(registry_json.get("workers", [])) != 5:
        raise AssemblyError("Authority v1.2.41 registry identity drift")

    if os.name == "nt":
        target_lengths = [
            (len(str(candidate / item["path"])), item["path"])
            for item in desktop_build["receipt"]["files"]
        ]
        target_lengths.extend(
            (
                len(str(candidate / "authority/win-x64" / path.relative_to(authority_source))),
                path.relative_to(authority_source).as_posix(),
            )
            for path in authority_source.rglob("*")
            if path.is_file()
        )
        longest_length, longest_relative = max(target_lengths)
        if longest_length >= 260:
            raise AssemblyError(
                f"candidate target exceeds legacy Windows MAX_PATH before write: length={longest_length} path={longest_relative}"
            )

    candidate.mkdir(parents=True)
    for item in desktop_build["receipt"]["files"]:
        copy_once(desktop / item["path"], candidate / item["path"])
    copy_once(desktop_build["receiptPath"], candidate / "desktop-build-receipt.v1.json")
    copy_tree(authority_source, candidate / "authority/win-x64")

    metadata = candidate / "authority/metadata"
    for source in sorted((p for p in authority_meta.iterdir() if p.is_file()), key=lambda p: p.name):
        copy_once(source, metadata / source.name)
    copy_once(checkpoint, metadata / "core-v2.10.6-checkpoint.json")
    copy_once(formal, metadata / "formal-authority-assembly.receipt.json")
    copy_once(admission, metadata / "post-assembly-independent-authority-admission.receipt.json")
    copy_once(invalidation, metadata / "v1.2.40-multi-file-entrypoint.invalidation.json")
    copy_once(migration, metadata / "desktop-application-migration.receipt.json")
    copy_once(production_routes, metadata / "desktop-production-routes.receipt.json")
    copy_once(shared_protocol, metadata / "desktop-shared-module-action-protocol.receipt.json")

    ipc_source = PROJECT / "contracts/core/v2.10/module-control/orchestrator-ipc.v1.7.json"
    ipc = load(ipc_source)
    ipc["pipeName"] = args.pipe_name
    ipc["endpoint"] = rf"\\.\pipe\{args.pipe_name}"
    ipc_root = metadata / "contracts/core/v2.10/module-control"
    ipc_root.mkdir(parents=True)
    ipc_path = ipc_root / "orchestrator-ipc.candidate.v1.7.json"
    ipc_path.write_text(json.dumps(ipc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    copy_tree(PROJECT / "contracts/core/v2.10/module-control/schemas", ipc_root / "schemas")
    copy_once(
        PROJECT / "contracts/core/v2.3/playback-control/schemas/stage-control-request.v1.schema.json",
        metadata / "contracts/core/v2.3/playback-control/schemas/stage-control-request.v1.schema.json",
    )

    integration = {
        "SchemaVersion": "knowledge-clipper-candidate-integration.v1",
        "AutoStart": True,
        "OrchestratorExecutable": "authority/win-x64/orchestrator/KnowledgeClipper.Orchestrator.exe",
        "IpcConfig": "authority/metadata/contracts/core/v2.10/module-control/orchestrator-ipc.candidate.v1.7.json",
        "Registry": "authority/metadata/worker-registry.v1.json",
        "ReleaseRoot": "authority/win-x64",
        "JobsRoot": "%LOCALAPPDATA%\\KnowledgeClipper\\jobs",
        "PipeName": args.pipe_name,
        "MaxActiveTasks": 4,
        "AuthorityCheckpointId": "knowledge-clipper-core-v2.10.6",
        "AuthorityTreeSha256": AUTHORITY_TREE,
    }
    (candidate / "candidate-integration.json").write_text(
        json.dumps(integration, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    (candidate / "README-候选说明.txt").write_text(
        "零碎知识剪藏 - v1.2.41 质量集成候选\n\n"
        "此目录从最新 Desktop 共享模块动作协议载荷与 Core v2.10.6 / Authority v1.2.41 原始输入只追加组装。\n"
        "请保留整个目录；启动入口为 KnowledgeClipper.exe。\n",
        encoding="utf-8",
    )

    copied_authority = inventory(candidate / "authority/win-x64", include_files=False)
    if copied_authority != authority_inventory:
        raise AssemblyError("copied Authority subtree drift")
    payload = inventory(candidate, {"candidate-file-manifest.v1.json"})
    manifest = {
        "schemaVersion": "knowledge-clipper-integrated-candidate-manifest.v2",
        "candidateId": args.candidate_id,
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "entrypoint": "KnowledgeClipper.exe",
        "desktop": {
            "buildId": desktop_build["receipt"]["buildId"],
            "buildReceiptSha256": sha256(desktop_build["receiptPath"]),
            "payloadTreeSha256": desktop_build["treeSha256"],
            "migrationReceiptSha256": MIGRATION_SHA,
            "productionRoutesReceiptSha256": PRODUCTION_ROUTES_SHA,
            "sharedModuleActionProtocolReceiptSha256": SHARED_PROTOCOL_SHA,
        },
        "core": {
            "checkpointVersion": "2.10.6",
            "checkpointSha256": CHECKPOINT_SHA,
            "ipcProtocolVersion": "1.7",
        },
        "authority": {
            "releaseId": AUTHORITY_ID,
            "registryVersion": "1.2.41",
            "registrySha256": REGISTRY_SHA,
            "formalReceiptSha256": FORMAL_SHA,
            "admissionReceiptSha256": ADMISSION_SHA,
            "supersededAuthorityInvalidationSha256": INVALIDATION_SHA,
            "releaseFiles": authority_inventory["fileCount"],
            "releaseBytes": authority_inventory["totalBytes"],
            "releaseTreeSha256": AUTHORITY_TREE,
        },
        "processBoundary": {
            "desktop": "KnowledgeClipper.exe",
            "orchestrator": "authority/win-x64/orchestrator/KnowledgeClipper.Orchestrator.exe",
            "transport": "windows-named-pipe",
            "pipeName": args.pipe_name,
        },
        "payload": payload,
        "excludedControlFiles": ["candidate-file-manifest.v1.json"],
    }
    manifest_path = candidate / "candidate-file-manifest.v1.json"
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    sealed = inventory(candidate, include_files=False)
    result = {
        "schemaVersion": "knowledge-clipper-quality-integrated-candidate-assembly.v1",
        "status": "assembled-sealed-not-promoted",
        "candidateId": args.candidate_id,
        "candidateRoot": candidate.relative_to(PROJECT).as_posix(),
        "manifest": {
            "path": manifest_path.relative_to(PROJECT).as_posix(),
            "bytes": manifest_path.stat().st_size,
            "sha256": sha256(manifest_path),
        },
        "sealed": sealed,
        "authority": authority_inventory,
        "supersedesAuthority": "knowledge-clipper-authority-v1.2.40-20260902T012900Z",
        "invalidation": {
            "path": invalidation.relative_to(PROJECT).as_posix(),
            "sha256": INVALIDATION_SHA,
        },
        "externalCalls": 0,
        "externalWrites": 0,
        "physicalSpeakerInvocations": 0,
        "userHandTestReady": False,
    }
    receipt_output.parent.mkdir(parents=True, exist_ok=True)
    receipt_output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(
        json.dumps(
            {
                "status": result["status"],
                "candidateRoot": result["candidateRoot"],
                "manifestSha256": result["manifest"]["sha256"],
                "sealedTreeSha256": sealed["treeSha256"],
                "receipt": str(receipt_output),
                "receiptSha256": sha256(receipt_output),
            },
            ensure_ascii=False,
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
