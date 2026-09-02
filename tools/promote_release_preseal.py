from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import uuid
from datetime import datetime, timezone
from pathlib import Path

from assemble_release_preseal import configure_candidate, inventory, sha256_file, write_json


PROJECT_ROOT = Path(__file__).resolve().parents[1]
RELEASES_ROOT = PROJECT_ROOT / "releases"
BASE_ROOT = RELEASES_ROOT / "1.0.0-rc.1-preseal.4/win-x64"
BASE_TREE_SHA256 = "62673E60E2BBA3325204378CB0731B71360A8FC9F59510D5C2EA1296B38EB358"
ORCHESTRATOR_ROOT = PROJECT_ROOT / "dist/orchestrator/win-x64/core-v1.1.0-reuse-20260825T160043Z"
ORCHESTRATOR_EXE_SHA256 = "3845B116F94ACF3C26F6C19EF212E4474FB38ADD28CA69955364AEE9C127E576"
ORCHESTRATOR_RECEIPT = PROJECT_ROOT / "receipts/orchestrator-quality-snapshot-20260825T160043Z.json"
ORCHESTRATOR_RECEIPT_SHA256 = "6071921222E9502665EA0ED47B97F134C9E1D9D4E4C718E228BF7DF5953003ED"
REMNOTE_EXE = PROJECT_ROOT / "workers/remnote/RemNoteQuickClipper.RemNoteExporter.Worker.exe"
REMNOTE_EXE_SHA256 = "0543F769FF1B809E528D74104070852BD5BBBDC0C5BDEA9BDE49C42B0E11936B"
REMNOTE_RECEIPT = PROJECT_ROOT / "receipts/remnote-exporter-quality-refresh-20260825T155708Z.json"
REMNOTE_RECEIPT_SHA256 = "3A811230009693A80BF31182FD92A34EFF5A8B9CBAEAE4C2248C501E578874C1"
SPEECH_REUSE_RECEIPT = PROJECT_ROOT / "receipts/speech-reuse-quality-regression-20260825T155708Z.json"
SPEECH_REUSE_RECEIPT_SHA256 = "E1BBEF70A9BD51221EA413BCE6D0CB78D77D430E05E360FD97C3A614109246F4"
CORE_ADAPTER = PROJECT_ROOT / "contracts/core-adapter/v1"
AUTHORITY_REGISTRY = PROJECT_ROOT / "contracts/v1/registry/worker-registry.v1.json"
AUTHORITY_REGISTRY_SHA256 = "ACB4BA01ADBC0A410AAE7050FEC04F527C47EE6D0A98F30B73E7AC2B0E35F6E1"


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def tree_snapshot(root: Path) -> tuple[list[dict[str, object]], str]:
    items = []
    lines = []
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        size = path.stat().st_size
        digest = sha256_file(path)
        items.append({"path": relative, "bytes": size, "sha256": digest})
        lines.append(f"{relative}\t{size}\t{digest}\n")
    return items, hashlib.sha256("".join(lines).encode("utf-8")).hexdigest().upper()


def verify_inputs() -> tuple[list[dict[str, object]], str]:
    base_files, base_bytes, base_tree = inventory(BASE_ROOT)
    if base_tree != BASE_TREE_SHA256 or base_files != 4965 or base_bytes != 723240804:
        raise RuntimeError("immutable preseal.4 base tree changed")
    pins = {
        ORCHESTRATOR_ROOT / "RemNoteQuickClipper.Orchestrator.exe": ORCHESTRATOR_EXE_SHA256,
        ORCHESTRATOR_RECEIPT: ORCHESTRATOR_RECEIPT_SHA256,
        REMNOTE_EXE: REMNOTE_EXE_SHA256,
        REMNOTE_RECEIPT: REMNOTE_RECEIPT_SHA256,
        SPEECH_REUSE_RECEIPT: SPEECH_REUSE_RECEIPT_SHA256,
        AUTHORITY_REGISTRY: AUTHORITY_REGISTRY_SHA256,
    }
    for path, expected in pins.items():
        if not path.is_file() or sha256_file(path) != expected:
            raise RuntimeError(f"pinned promotion input changed: {path}")
    registry = json.loads(AUTHORITY_REGISTRY.read_text(encoding="utf-8"))
    if registry.get("workers") != []:
        raise RuntimeError("authority registry is no longer empty")
    core_items, core_tree = tree_snapshot(CORE_ADAPTER)
    orchestrator_receipt = json.loads(ORCHESTRATOR_RECEIPT.read_text(encoding="utf-8"))
    snapshotted_core = [
        item for item in orchestrator_receipt["sourceSnapshot"]["items"]
        if item["path"].startswith("contracts/core-adapter/v1/")
    ]
    expected_core = [
        {"path": item["path"].removeprefix("contracts/core-adapter/v1/"), "bytes": item["bytes"], "sha256": item["sha256"]}
        for item in snapshotted_core
    ]
    if core_items != expected_core:
        raise RuntimeError("current core-adapter contracts do not match the orchestrator quality snapshot")
    return core_items, core_tree


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument("--receipt", required=True, type=Path)
    args = parser.parse_args()

    from brand_identity_gates import block_legacy_release_builder
    block_legacy_release_builder("promote_release_preseal.py")

    target = RELEASES_ROOT / args.version / "win-x64"
    receipt_path = args.receipt if args.receipt.is_absolute() else PROJECT_ROOT / args.receipt
    if target.exists() or target.parent.exists() or receipt_path.exists():
        raise RuntimeError("append-only promotion target or receipt already exists")
    core_before, core_tree = verify_inputs()
    staging_parent = RELEASES_ROOT / f".promote-{uuid.uuid4().hex[:8]}"
    root = staging_parent / "win-x64"
    try:
        shutil.copytree(BASE_ROOT, root)
        shutil.copy2(REMNOTE_EXE, root / "workers/remnote/RemNoteQuickClipper.RemNoteExporter.Worker.exe")
        shutil.copytree(ORCHESTRATOR_ROOT, root / "orchestrator")
        shutil.copytree(CORE_ADAPTER, root / "contracts/core-adapter/v1")
        core_after, core_after_tree = tree_snapshot(CORE_ADAPTER)
        if core_before != core_after or core_tree != core_after_tree:
            raise RuntimeError("core-adapter contract tree changed during promotion")
        configure_candidate(root, target, args.version)
        write_json(root / "PRESEAL.v1.json", {
            "schemaVersion": "release-preseal.v1",
            "releaseVersion": args.version,
            "state": "unsealed-no-go-authority-and-physical-boundaries",
            "base": {
                "releaseRoot": "releases/1.0.0-rc.1-preseal.4/win-x64",
                "treeSha256": BASE_TREE_SHA256,
                "inheritedWorkerIds": ["image.ocr.mineru", "content.deepseek", "speech.edge", "export.anki"],
            },
            "orchestrator": {
                "path": "orchestrator/RemNoteQuickClipper.Orchestrator.exe",
                "sha256": ORCHESTRATOR_EXE_SHA256,
                "receiptSha256": ORCHESTRATOR_RECEIPT_SHA256,
            },
            "remnoteWorker": {
                "path": "workers/remnote/RemNoteQuickClipper.RemNoteExporter.Worker.exe",
                "sha256": REMNOTE_EXE_SHA256,
                "receiptSha256": REMNOTE_RECEIPT_SHA256,
            },
            "speechReuseRegression": {
                "status": "passed-source-and-self-contained-black-box",
                "receiptSha256": SPEECH_REUSE_RECEIPT_SHA256,
            },
            "coreAdapterContracts": {
                "path": "contracts/core-adapter/v1",
                "files": len(core_before),
                "treeSha256": core_tree,
            },
            "releaseManifest": None,
            "candidateHash": None,
            "authorityRegistry": "unchanged-empty-stable-v1",
            "pending": [
                "authority-registry-checkpoint",
                "physical-windows-boundaries",
                "candidate-bound-real-worker-canaries",
                "desktop-to-real-remnote-e2e",
                "signed-release-packaging",
            ],
            "externalProductionApisInvoked": False,
        })
        files, total_bytes, tree_hash = inventory(root)
        if (root / "release-manifest.v1.json").exists():
            raise RuntimeError("unsealed promotion unexpectedly contains a release manifest")
        os.replace(staging_parent, target.parent)
        receipt = {
            "schemaVersion": "release-preseal-promotion-receipt.v1",
            "createdAt": utc_now(),
            "releaseVersion": args.version,
            "releaseRoot": str(target),
            "status": "unsealed-no-go",
            "baseTreeSha256": BASE_TREE_SHA256,
            "inventory": {"files": files, "bytes": total_bytes, "presealTreeSha256": tree_hash},
            "orchestratorSha256": ORCHESTRATOR_EXE_SHA256,
            "remnoteWorkerSha256": REMNOTE_EXE_SHA256,
            "coreAdapterTreeSha256": core_tree,
            "releaseManifest": None,
            "candidateHash": None,
            "stableCheckpointModified": False,
            "authorityRegistryModified": False,
            "externalProductionApisInvoked": False,
        }
        write_json(receipt_path, receipt)
        print(json.dumps({
            "releaseRoot": str(target),
            "receipt": str(receipt_path),
            "receiptSha256": sha256_file(receipt_path),
            "inventory": receipt["inventory"],
            "coreAdapterFiles": len(core_before),
        }, ensure_ascii=False))
        return 0
    except Exception:
        shutil.rmtree(staging_parent, ignore_errors=True)
        raise


if __name__ == "__main__":
    raise SystemExit(main())
