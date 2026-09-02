from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from assemble_release_preseal import configure_candidate, sha256_file


PROJECT_ROOT = Path(__file__).resolve().parents[1]
RELEASES_ROOT = PROJECT_ROOT / "releases"
BASE_ROOT = RELEASES_ROOT / "1.0.0-rc.1-preseal.5/win-x64"
BASE_TREE_SHA256 = "57847E6D2796CD64F25F23F7F47D3BC4FF8142C464A653A5E82254A19EDCAAFD"
BASE_FILES = 4989
BASE_BYTES = 791136775
STABLE_RECEIPT = PROJECT_ROOT / "receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json"
STABLE_RECEIPT_SHA256 = "1CB738FE133F6645B9757E246E2FBC210785086342F70BAD32B5015BD19E1B1E"
AUTHORITY_REGISTRY = PROJECT_ROOT / "contracts/v1/registry/worker-registry.v1.json"
AUTHORITY_REGISTRY_SHA256 = "ACB4BA01ADBC0A410AAE7050FEC04F527C47EE6D0A98F30B73E7AC2B0E35F6E1"

DESKTOP_ROOT = PROJECT_ROOT / "dist/desktop-win-x64/desktop-system-screenshot-20260826T015611Z"
DESKTOP_RECEIPT = DESKTOP_ROOT / "desktop-build-receipt.v1.json"
DESKTOP_RECEIPT_SHA256 = "EE25DBD89538C55F03056E9CC0E802E5E3AE5127BAC3D38F8F2CFAA0E2227ECD"
DESKTOP_EXE_SHA256 = "D6A478C72462BB86916F3C5899DB0DBED13F56FD0A7D154A778A08F265108772"
DESKTOP_SYSTEM_SCREENSHOT_RECEIPT = PROJECT_ROOT / "receipts/desktop-system-screenshot-canary-desktop-system-screenshot-20260826T015611Z.json"
DESKTOP_SYSTEM_SCREENSHOT_RECEIPT_SHA256 = "5E77D0A210EC42D238CCF202F7B3025A2C92AD2FE16E842BFAC438C572B6A3DB"

ORCHESTRATOR_EXE = PROJECT_ROOT / "dist/orchestrator/win-x64/v1.1.0-canary.6/RemNoteQuickClipper.Orchestrator.exe"
ORCHESTRATOR_EXE_SHA256 = "C93E911B5B977F9543AC2ED5BCC82FCCCCD7B4CCB77A367019327834EF0F4802"
ORCHESTRATOR_CHECKPOINT = PROJECT_ROOT / "canary/orchestrator-v1.1/orchestrator-v1.1-20260826T015200Z/receipt.json"
ORCHESTRATOR_CHECKPOINT_SHA256 = "7C0FC14064167BEDC29ED05215DF69E5B8A6F710D4E2DEC5DF3C7C2632D9727B"

IMAGE_EXE = PROJECT_ROOT / "workers/image-ocr/RemNoteQuickClipper.ImageOcr.Worker.exe"
IMAGE_EXE_SHA256 = "12798E6898817F05B87369583A9894239FD1446D482E099980131BDD2F7DB0A0"
IMAGE_RECEIPT = PROJECT_ROOT / "receipts/image-ocr-worker-cli-validation-20260825T160541Z.json"
IMAGE_RECEIPT_SHA256 = "7EC0FFA3C2165B5B063E67F6D8B878227743C9254C1376DE71925DBD0301B072"
IMAGE_TERMINAL_RECEIPT = PROJECT_ROOT / "receipts/image-ocr-terminal-failure-validation-20260826T014703Z.json"
IMAGE_TERMINAL_RECEIPT_SHA256 = "904C4898416A59B6BD3D2A5C1A276190ADA602EA95E0D85AEE8B91298E07C6A8"
IMAGE_UNKNOWN_RECEIPT = PROJECT_ROOT / "receipts/image-ocr-unknown-external-state-validation-20260826T015142Z.json"
IMAGE_UNKNOWN_RECEIPT_SHA256 = "4CE67AB765A7199FDA42B06079E31D6771A82EA5DA18157AD956BED46AB528E0"

CONTENT_RECEIPTS = {
    PROJECT_ROOT / "receipts/content-worker-continuous-iteration-20260825T155736Z.json":
        "A7DF36B952B1BCFCE3A6082D2683E46F8F95B228EDE70E33243F7C076B82BC39",
    PROJECT_ROOT / "receipts/content-worker-general-learning-live-20260825T160311Z.json":
        "6365C706E8898D969582B056F21FDFF1B2374453655E91561AA6A4A383AA35E3",
    PROJECT_ROOT / "receipts/content-worker-cleaning-live-regression-20260825T2342Z.json":
        "9B1286ACEDE31389122ECECE3B537EA44D79CB841584DB611BE3D86E227D08F7",
    PROJECT_ROOT / "receipts/content-worker-quality-refresh-20260826T014604Z.json":
        "6B97C14FA13F9B69CF795CB67DAD29DAFB3DE8D92BB09DC657C1204C449418E6",
    PROJECT_ROOT / "receipts/content-worker-quality-refresh-20260826T020300Z.json":
        "CDB592E602F5CF7918150E8AEC5B4D62BEE6031AF4369D84C98208067C94C00B",
    PROJECT_ROOT / "receipts/content-worker-provenance-dedup-partial-20260826T020120Z.json":
        "A9F8B28A8933EBA0F7F082BD28C995B8C897A076A7CED7941C1F738D0B5CC9DE",
    PROJECT_ROOT / "receipts/content-worker-quality-refresh-20260826T021100Z.json":
        "9A7092D251971BD7BB9BDAA9FB2CFFA220403B786135BC13D13A396BF7993834",
    PROJECT_ROOT / "receipts/content-worker-provenance-dedup-partial-final-20260826T021101Z.json":
        "DED40EC662755049730A62BABC1A5E649C4277F76B911F6027B8D5D7B262A039",
    PROJECT_ROOT / "receipts/content-worker-quality-final-20260826T021700Z.json":
        "5A11CBBB82DB4E6D5E6DAD616A16785CBD32AD277FD440A18C51C11C4E9B3DB2",
}
CONTENT_QUALITY_RECEIPT = PROJECT_ROOT / "receipts/content-worker-quality-final-20260826T021700Z.json"
CONTENT_QUALITY_RECEIPT_SHA256 = "5A11CBBB82DB4E6D5E6DAD616A16785CBD32AD277FD440A18C51C11C4E9B3DB2"
CONTENT_INCREMENT_RECEIPT_SHA256 = "DED40EC662755049730A62BABC1A5E649C4277F76B911F6027B8D5D7B262A039"
CONTENT_ASSETS = [
    PROJECT_ROOT / "content-worker/dist",
    PROJECT_ROOT / "content-worker/prompts",
    PROJECT_ROOT / "content-worker/config",
    PROJECT_ROOT / "content-worker/schemas",
]
CONTENT_PACKAGE = PROJECT_ROOT / "content-worker/package.json"

ANKI_EXE = PROJECT_ROOT / "workers/anki/RemNoteQuickClipper.AnkiExporter.Worker.exe"
ANKI_EXE_SHA256 = "AC1F37AB4486B59BEF50A40214BA8D7C7D3B01BC569461C8539D926308E9BB96"
ANKI_RECEIPT = PROJECT_ROOT / "workers/anki/canary/xv-20260826T015251-1212/exe-validation-result.json"
ANKI_RECEIPT_SHA256 = "EF696CCCD9467AB58E6B7600FDBE73460AF03F16DC2B33782573FC64E75D2D7F"
ANKI_QUALITY_RECEIPT = PROJECT_ROOT / "receipts/anki-worker-quality-refresh-20260826T020000Z.json"
ANKI_QUALITY_RECEIPT_SHA256 = "12515B07704CF2BD67072A48E257BDD418D0C6F5410AB5382CE65CA8BCBA01C9"
ANKI_CONSUMER_RECEIPT = PROJECT_ROOT / "workers/anki-exporter/canary/ca/260826015306-877f/receipt.json"
ANKI_CONSUMER_RECEIPT_SHA256 = "F1D83AA80D868172F3D49F8E5749266F6132DE5F4DD5AE0DF6FDBB5E3204794A"
REMNOTE_EXE = PROJECT_ROOT / "workers/remnote/RemNoteQuickClipper.RemNoteExporter.Worker.exe"
REMNOTE_EXE_SHA256 = "45DE3A87501236ECAA0CFA13FC02A8F6B0C56A79996F1F37613610819F2708FF"
REMNOTE_RECEIPT = PROJECT_ROOT / "receipts/remnote-exporter-worker-v1-20260825T235334Z.json"
REMNOTE_RECEIPT_SHA256 = "BDB4DE203920F27118CD26169ECC2EC46D57A955C72CC41A6BB3A7EC195C620D"

REGISTRY_CANDIDATE = PROJECT_ROOT / "canary/registry-candidate/registry-candidate-20260825T161144Z/worker-registry.v1.json"
REGISTRY_CANDIDATE_SHA256 = "975B14F43D947C038E6B22F1DD3F43649E4C05D00FD22AB8042AD9F8D0D2EF7C"
REGISTRY_CANDIDATE_RECEIPT = PROJECT_ROOT / "canary/registry-candidate/registry-candidate-20260825T161144Z/receipt.json"
REGISTRY_CANDIDATE_RECEIPT_SHA256 = "3E818F227DF4B35D261CF3B99591EB48C6F28BD47432B1D2E5B11B40137CBB5D"


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def write_json_create_new(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        stream.write(json.dumps(value, ensure_ascii=False, indent=2) + "\n")


def tree_snapshot(root: Path) -> tuple[list[dict[str, object]], str]:
    items: list[dict[str, object]] = []
    lines: list[str] = []
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix()):
        rel = path.relative_to(root).as_posix()
        size = path.stat().st_size
        digest = sha256_file(path)
        items.append({"path": rel, "bytes": size, "sha256": digest})
        lines.append(f"{rel}\t{size}\t{digest}\n")
    return items, hashlib.sha256("".join(lines).encode("utf-8")).hexdigest().upper()


def desktop_receipt_tree(items: list[dict[str, object]]) -> str:
    canonical = "".join(
        f"{item['path']}\n{str(item['sha256']).lower()}\n{item['bytes']}\n"
        for item in sorted(items, key=lambda value: str(value["path"]))
    )
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest().upper()


def desktop_build_snapshot() -> list[dict[str, object]]:
    receipt = json.loads(DESKTOP_RECEIPT.read_text(encoding="utf-8"))
    items = receipt.get("files", [])
    if receipt.get("fileCount") != 243 or len(items) != 243:
        raise RuntimeError("desktop build receipt file count mismatch")
    for item in items:
        path = DESKTOP_ROOT / str(item["path"])
        if (
            not path.is_file()
            or path.stat().st_size != item["bytes"]
            or sha256_file(path) != str(item["sha256"]).upper()
        ):
            raise RuntimeError(f"desktop build file changed: {path}")
    if (
        sum(int(item["bytes"]) for item in items) != receipt.get("totalBytes")
        or desktop_receipt_tree(items) != str(receipt.get("treeSha256", "")).upper()
    ):
        raise RuntimeError("desktop build receipt inventory summary mismatch")
    return items


def rebind_desktop_build_receipt(staging_root: Path, release_version: str) -> None:
    desktop_root = staging_root / "desktop"
    receipt_path = desktop_root / "desktop-build-receipt.v1.json"
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    derived_from = sha256_file(receipt_path)
    files: list[dict[str, object]] = []
    receipt_paths = sorted(str(item["path"]) for item in receipt.get("files", []))
    if len(receipt_paths) != 243:
        raise RuntimeError(f"source desktop receipt file count changed: {len(receipt_paths)}")
    for relative in receipt_paths:
        path = desktop_root / relative
        if not path.is_file():
            raise RuntimeError(f"derived desktop receipt input missing: {path}")
        files.append({
            "path": relative,
            "bytes": path.stat().st_size,
            "sha256": sha256_file(path).lower(),
        })
    receipt["buildId"] = f"{release_version}-desktop-derived"
    receipt["createdAt"] = utc_now()
    receipt["treeSha256"] = desktop_receipt_tree(files).lower()
    receipt["fileCount"] = len(files)
    receipt["totalBytes"] = sum(int(item["bytes"]) for item in files)
    receipt["executableSha256"] = sha256_file(desktop_root / "RemNoteQuickClipper.exe").lower()
    receipt["applicationDllSha256"] = sha256_file(desktop_root / "RemNoteQuickClipper.dll").lower()
    receipt["files"] = files
    receipt["releaseDerivation"] = {
        "releaseVersion": release_version,
        "sourceBuildReceiptSha256": derived_from,
        "changedFile": "appsettings.json",
        "reason": "bind immutable versioned work directory to the staged preseal package",
    }
    receipt_path.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")


def release_inventory(root: Path, label: str) -> tuple[int, int, str]:
    files = sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix())
    lines: list[str] = []
    total_bytes = 0
    for index, path in enumerate(files, start=1):
        relative = path.relative_to(root).as_posix()
        size = path.stat().st_size
        digest = sha256_file(path)
        total_bytes += size
        lines.append(f"{relative}\t{size}\t{digest}\n")
        if index % 500 == 0:
            print(f"PRESEAL_PROGRESS phase={label} files={index}/{len(files)}", flush=True)
    tree_hash = hashlib.sha256("".join(lines).encode("utf-8")).hexdigest().upper()
    print(f"PRESEAL_PROGRESS phase={label} files={len(files)}/{len(files)} complete=true", flush=True)
    return len(files), total_bytes, tree_hash


def content_snapshot() -> tuple[list[dict[str, object]], str]:
    items: list[dict[str, object]] = []
    for root in CONTENT_ASSETS:
        if not root.is_dir():
            raise RuntimeError(f"content asset root missing: {root}")
        for path in root.rglob("*"):
            if path.is_file():
                items.append({
                    "path": path.relative_to(PROJECT_ROOT / "content-worker").as_posix(),
                    "bytes": path.stat().st_size,
                    "sha256": sha256_file(path),
                })
    items.append({
        "path": "package.json",
        "bytes": CONTENT_PACKAGE.stat().st_size,
        "sha256": sha256_file(CONTENT_PACKAGE),
    })
    items.sort(key=lambda item: str(item["path"]))
    lines = [f"{item['path']}\t{item['bytes']}\t{item['sha256']}\n" for item in items]
    return items, hashlib.sha256("".join(lines).encode("utf-8")).hexdigest().upper()


def content_quality_snapshot() -> list[dict[str, object]]:
    receipt = json.loads(CONTENT_QUALITY_RECEIPT.read_text(encoding="utf-8"))
    items: list[dict[str, object]] = []
    for expected in receipt.get("implementation", []):
        path = PROJECT_ROOT / str(expected["path"])
        if (
            not path.is_file()
            or path.stat().st_size != expected["bytes"]
            or sha256_file(path) != str(expected["sha256"]).upper()
        ):
            raise RuntimeError(f"content quality input changed: {path}")
        items.append({"path": expected["path"], "bytes": expected["bytes"], "sha256": str(expected["sha256"]).upper()})
    if not items:
        raise RuntimeError("content quality receipt has no implementation snapshot")
    return items


def anki_quality_snapshot() -> list[dict[str, object]]:
    receipt = json.loads(ANKI_QUALITY_RECEIPT.read_text(encoding="utf-8"))
    items: list[dict[str, object]] = []
    for expected in receipt.get("implementation", []):
        path = PROJECT_ROOT / str(expected["path"])
        if (
            not path.is_file()
            or path.stat().st_size != expected["bytes"]
            or sha256_file(path) != str(expected["sha256"]).upper()
        ):
            raise RuntimeError(f"Anki quality input changed: {path}")
        items.append({"path": expected["path"], "bytes": expected["bytes"], "sha256": str(expected["sha256"]).upper()})
    if not items:
        raise RuntimeError("Anki quality receipt has no implementation snapshot")
    return items


def verify_pins() -> tuple[list[dict[str, object]], str, list[dict[str, object]], list[dict[str, object]]]:
    base = release_inventory(BASE_ROOT, "verify-base")
    if base != (BASE_FILES, BASE_BYTES, BASE_TREE_SHA256):
        raise RuntimeError(f"immutable preseal.5 base changed: {base}")
    pins = {
        STABLE_RECEIPT: STABLE_RECEIPT_SHA256,
        AUTHORITY_REGISTRY: AUTHORITY_REGISTRY_SHA256,
        DESKTOP_RECEIPT: DESKTOP_RECEIPT_SHA256,
        DESKTOP_ROOT / "RemNoteQuickClipper.exe": DESKTOP_EXE_SHA256,
        DESKTOP_SYSTEM_SCREENSHOT_RECEIPT: DESKTOP_SYSTEM_SCREENSHOT_RECEIPT_SHA256,
        ORCHESTRATOR_EXE: ORCHESTRATOR_EXE_SHA256,
        ORCHESTRATOR_CHECKPOINT: ORCHESTRATOR_CHECKPOINT_SHA256,
        IMAGE_EXE: IMAGE_EXE_SHA256,
        IMAGE_RECEIPT: IMAGE_RECEIPT_SHA256,
        IMAGE_TERMINAL_RECEIPT: IMAGE_TERMINAL_RECEIPT_SHA256,
        IMAGE_UNKNOWN_RECEIPT: IMAGE_UNKNOWN_RECEIPT_SHA256,
        ANKI_EXE: ANKI_EXE_SHA256,
        ANKI_RECEIPT: ANKI_RECEIPT_SHA256,
        ANKI_QUALITY_RECEIPT: ANKI_QUALITY_RECEIPT_SHA256,
        ANKI_CONSUMER_RECEIPT: ANKI_CONSUMER_RECEIPT_SHA256,
        REMNOTE_EXE: REMNOTE_EXE_SHA256,
        REMNOTE_RECEIPT: REMNOTE_RECEIPT_SHA256,
        REGISTRY_CANDIDATE: REGISTRY_CANDIDATE_SHA256,
        REGISTRY_CANDIDATE_RECEIPT: REGISTRY_CANDIDATE_RECEIPT_SHA256,
        **CONTENT_RECEIPTS,
    }
    for path, expected in pins.items():
        if not path.is_file() or sha256_file(path) != expected:
            raise RuntimeError(f"pinned promotion input changed: {path}")
    if json.loads(AUTHORITY_REGISTRY.read_text(encoding="utf-8")).get("workers") != []:
        raise RuntimeError("authority registry is no longer empty")
    desktop_build_snapshot()
    content_items, content_tree = content_snapshot()
    return content_items, content_tree, content_quality_snapshot(), anki_quality_snapshot()


def safe_replace_tree(staging_root: Path, relative: str, source: Path) -> None:
    target = (staging_root / relative).resolve()
    if not target.is_relative_to(staging_root.resolve()):
        raise RuntimeError(f"replacement target escaped staging root: {target}")
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(source, target)


def entrypoint(component_id: str, version: str, root: Path, relative: str) -> dict[str, Any]:
    path = root / relative
    if not path.is_file():
        raise RuntimeError(f"promoted entrypoint missing: {relative}")
    return {
        "componentId": component_id,
        "version": version,
        "path": relative,
        "bytes": path.stat().st_size,
        "sha256": sha256_file(path),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Promote verified module increments into an append-only unsealed preseal")
    parser.add_argument("--version", required=True)
    parser.add_argument("--receipt", required=True, type=Path)
    args = parser.parse_args()

    from brand_identity_gates import block_legacy_release_builder
    block_legacy_release_builder("promote_release_preseal_increment.py")

    target = RELEASES_ROOT / args.version / "win-x64"
    receipt_path = args.receipt.resolve() if args.receipt.is_absolute() else (PROJECT_ROOT / args.receipt).resolve()
    if target.exists() or target.parent.exists() or receipt_path.exists():
        raise RuntimeError("append-only preseal target or receipt already exists")
    content_before, content_tree, quality_inputs_before, anki_inputs_before = verify_pins()
    staging_parent = RELEASES_ROOT / f".increment-{uuid.uuid4().hex[:8]}"
    root = staging_parent / "win-x64"
    try:
        print("PRESEAL_PROGRESS phase=copy-base started=true", flush=True)
        shutil.copytree(BASE_ROOT, root)
        print("PRESEAL_PROGRESS phase=copy-base complete=true", flush=True)
        safe_replace_tree(root, "desktop", DESKTOP_ROOT)
        safe_replace_tree(root, "orchestrator", ORCHESTRATOR_EXE.parent)
        shutil.copy2(IMAGE_EXE, root / "workers/image-ocr/RemNoteQuickClipper.ImageOcr.Worker.exe")
        shutil.copy2(ANKI_EXE, root / "workers/anki/RemNoteQuickClipper.AnkiExporter.Worker.exe")
        shutil.copy2(REMNOTE_EXE, root / "workers/remnote/RemNoteQuickClipper.RemNoteExporter.Worker.exe")
        for source in CONTENT_ASSETS:
            safe_replace_tree(root, f"content-worker/{source.name}", source)
        shutil.copy2(CONTENT_PACKAGE, root / "content-worker/package.json")
        candidate_registry_root = root / "registry-candidate/v1"
        candidate_registry_root.mkdir(parents=True)
        shutil.copy2(REGISTRY_CANDIDATE, candidate_registry_root / "worker-registry.v1.json")
        configure_candidate(root, target, args.version)
        rebind_desktop_build_receipt(root, args.version)
        content_after, content_after_tree = content_snapshot()
        if content_after != content_before or content_after_tree != content_tree:
            raise RuntimeError("content assets changed during preseal promotion")
        if content_quality_snapshot() != quality_inputs_before:
            raise RuntimeError("content source or regression inputs changed during preseal promotion")
        if anki_quality_snapshot() != anki_inputs_before:
            raise RuntimeError("Anki source or regression inputs changed during preseal promotion")
        desktop_build_snapshot()

        entrypoints = [
            entrypoint("desktop", "1.0.0", root, "desktop/RemNoteQuickClipper.exe"),
            entrypoint("orchestrator.core", "1.1.0.0", root, "orchestrator/RemNoteQuickClipper.Orchestrator.exe"),
            entrypoint("image.ocr.mineru", "1.0.0", root, "workers/image-ocr/RemNoteQuickClipper.ImageOcr.Worker.exe"),
            entrypoint("content.deepseek", "0.2.0", root, "workers/content/RemNoteQuickClipper.Content.Worker.exe"),
            entrypoint("speech.edge", "1.1.0", root, "workers/speech/RemNoteQuickClipper.Speech.Worker.exe"),
            entrypoint("export.remnote", "1.0.0", root, "workers/remnote/RemNoteQuickClipper.RemNoteExporter.Worker.exe"),
            entrypoint("export.anki", "1.1.2", root, "workers/anki/RemNoteQuickClipper.AnkiExporter.Worker.exe"),
        ]
        preseal = {
            "schemaVersion": "release-preseal.v1",
            "releaseVersion": args.version,
            "state": "unsealed-no-go",
            "base": {"releaseVersion": "1.0.0-rc.1-preseal.5", "treeSha256": BASE_TREE_SHA256},
            "absorbedEvidence": {
                "desktopQualitySnapshotReceiptSha256": DESKTOP_RECEIPT_SHA256,
                "desktopSystemScreenshotReceiptSha256": DESKTOP_SYSTEM_SCREENSHOT_RECEIPT_SHA256,
                "coreCheckpointReceiptSha256": ORCHESTRATOR_CHECKPOINT_SHA256,
                "imageOcrReceiptSha256": IMAGE_RECEIPT_SHA256,
                "imageOcrTerminalFailureReceiptSha256": IMAGE_TERMINAL_RECEIPT_SHA256,
                "imageOcrUnknownExternalStateReceiptSha256": IMAGE_UNKNOWN_RECEIPT_SHA256,
                "contentIterationReceiptSha256": CONTENT_RECEIPTS[next(iter(CONTENT_RECEIPTS))],
                "contentLiveReceiptSha256": list(CONTENT_RECEIPTS.values())[1],
                "contentCleaningReceiptSha256": list(CONTENT_RECEIPTS.values())[2],
                "contentQualityRefreshReceiptSha256": CONTENT_QUALITY_RECEIPT_SHA256,
                "contentProvenanceDedupPartialFinalReceiptSha256": CONTENT_INCREMENT_RECEIPT_SHA256,
                "contentModuleTaskComplete": True,
                "ankiReceiptSha256": ANKI_RECEIPT_SHA256,
                "ankiQualityRefreshReceiptSha256": ANKI_QUALITY_RECEIPT_SHA256,
                "ankiConsumerAcceptanceReceiptSha256": ANKI_CONSUMER_RECEIPT_SHA256,
                "remNoteReceiptSha256": REMNOTE_RECEIPT_SHA256,
                "registryCandidateReceiptSha256": REGISTRY_CANDIDATE_RECEIPT_SHA256,
                "contentAssetTreeSha256": content_tree,
            },
            "authorityRegistry": "unchanged-empty-stable-v1",
            "registryCandidate": {
                "path": "registry-candidate/v1/worker-registry.v1.json",
                "sha256": REGISTRY_CANDIDATE_SHA256,
                "authority": False,
            },
            "releaseManifest": None,
            "candidateHash": None,
            "pending": [
                "authority-registry-checkpoint",
                "release-manifest-and-candidateHash",
                "physical-windows-boundaries",
                "candidate-bound-real-worker-canaries",
                "desktop-to-real-remnote-e2e",
                "signed-release-packaging",
            ],
            "externalProductionApisInvoked": False,
        }
        preseal_path = root / "PRESEAL.v1.json"
        preseal_path.write_text(json.dumps(preseal, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
        files, total_bytes, tree_hash = release_inventory(root, "inventory-promoted")
        if (root / "release-manifest.v1.json").exists():
            raise RuntimeError("unsealed preseal unexpectedly contains release manifest")
        os.replace(staging_parent, target.parent)
        receipt = {
            "schemaVersion": "release-preseal-increment-receipt.v1",
            "createdAt": utc_now(),
            "releaseVersion": args.version,
            "releaseRoot": target.relative_to(PROJECT_ROOT).as_posix(),
            "status": "unsealed-no-go",
            "baseTreeSha256": BASE_TREE_SHA256,
            "layout": {"files": files, "bytes": total_bytes, "postGateTreeSha256": tree_hash},
            "entrypoints": entrypoints,
            "contentAssets": {"files": len(content_before), "treeSha256": content_tree},
            "registryCandidate": {"path": "registry-candidate/v1/worker-registry.v1.json", "sha256": REGISTRY_CANDIDATE_SHA256, "authority": False},
            "stableCheckpoint": {"filesChecked": 21, "mismatches": 0, "receiptSha256": STABLE_RECEIPT_SHA256, "modified": False},
            "authorityRegistry": {"entries": 0, "sha256": AUTHORITY_REGISTRY_SHA256, "modified": False},
            "releaseManifest": None,
            "candidateHash": None,
            "externalProductionApisInvoked": False,
        }
        write_json_create_new(receipt_path, receipt)
        print(json.dumps({
            "releaseRoot": str(target),
            "receipt": str(receipt_path),
            "receiptSha256": sha256_file(receipt_path),
            "layout": receipt["layout"],
            "entrypoints": entrypoints,
            "contentAssets": receipt["contentAssets"],
        }, ensure_ascii=False))
        return 0
    except Exception:
        if staging_parent.exists():
            shutil.rmtree(staging_parent, ignore_errors=True)
        raise


if __name__ == "__main__":
    raise SystemExit(main())
