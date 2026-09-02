from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


PROJECT = Path(__file__).resolve().parents[1]
DOTNET = Path(r"D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe")
DESKTOP_PROJECT = PROJECT / "desktop/RemNoteQuickClipper.Desktop.csproj"
TEMP_ROOT = Path(r"D:\KnowledgeClipperData\desktop-playback-control-builds")
IPC_CONFIG = PROJECT / "contracts/core/v2.3/playback-control/orchestrator-ipc.v1.1.json"
STAGE_REQUEST = PROJECT / "contracts/core/v2.3/playback-control/schemas/stage-control-request.v1.schema.json"
STAGE_RECEIPT = PROJECT / "contracts/core/v2.3/playback-control/schemas/stage-control-receipt.v1.schema.json"
CORE_CHECKPOINT = PROJECT / "contracts/authority/core-workflows/v2.3.0-20260828T030800Z/checkpoint.json"
CORE_RECEIPT = PROJECT / "receipts/core-playback-control-v2.3.0-checkpoint-20260828T030800Z.json"
EXPECTED = {
    IPC_CONFIG: "849CB3A85DD8CE660B5A6924D804AEB59E254010F616BA54B5E694C263DB98E2",
    STAGE_REQUEST: "B717994985275623719C09C93D04A4F05A5B23BAE6AB240191E00898908182E8",
    STAGE_RECEIPT: "A6B7DAAC447D93EC96BF57220AC7F14A07749B6498963F029EF0BA54BE48BC3F",
    CORE_CHECKPOINT: "483385EF31F990FD67D6FFFC8A4B7ED9186912C1B417A3F1156D71FABEA4F26A",
    CORE_RECEIPT: "C8CC5CD07EAC9A9297DA509FEEE17D2FF616A09A660E9C1C052CCDE9D49D028E",
}


class CandidateError(RuntimeError):
    pass


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def files(root: Path) -> list[Path]:
    return sorted((path for path in root.rglob("*") if path.is_file()), key=lambda path: path.relative_to(root).as_posix())


def inventory(root: Path, exclude: set[str] | None = None) -> dict[str, Any]:
    exclude = exclude or set()
    items: list[dict[str, Any]] = []
    material: list[str] = []
    total = 0
    for path in files(root):
        relative = path.relative_to(root).as_posix()
        if relative in exclude:
            continue
        if Path(relative).name != relative:
            raise CandidateError(f"desktop publish unexpectedly contains a nested file: {relative}")
        size = path.stat().st_size
        digest = sha256(path).lower()
        items.append({"path": relative, "bytes": size, "sha256": digest})
        material.append(f"{relative}\n{digest}\n{size}\n")
        total += size
    return {
        "files": items,
        "fileCount": len(items),
        "totalBytes": total,
        "treeSha256": hashlib.sha256("".join(material).encode("utf-8")).hexdigest().upper(),
    }


def source_snapshot() -> list[dict[str, Any]]:
    paths = [
        DESKTOP_PROJECT,
        PROJECT / "desktop/Directory.Build.props",
        PROJECT / "appsettings.json",
        PROJECT / "src/Core.cs",
        PROJECT / "src/Program.cs",
        PROJECT / "src/SingleInstanceCoordinator.cs",
        PROJECT / "src/CandidateIntegration.cs",
        PROJECT / "src/StartupDiagnostics.cs",
        PROJECT / "src/DesktopUi.cs",
        PROJECT / "src/WindowsDesktopProviders.cs",
        PROJECT / "src/Compat/JavaScriptSerializer.Net8.cs",
    ]
    paths.extend((PROJECT / "src/RemNoteQuickClipper.Desktop.Core").glob("*.cs"))
    result = []
    for path in sorted(paths, key=lambda item: item.relative_to(PROJECT).as_posix()):
        result.append({"path": path.relative_to(PROJECT).as_posix(), "bytes": path.stat().st_size, "sha256": sha256(path)})
    return result


def publish(target: Path) -> None:
    command = [
        str(DOTNET), "publish", str(DESKTOP_PROJECT), "-c", "Release", "-r", "win-x64",
        "--self-contained", "true", "--no-restore", "--nologo", "-p:DebugType=embedded",
        "-p:DebugSymbols=false", "--output", str(target),
    ]
    completed = subprocess.run(command, cwd=PROJECT, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if completed.returncode != 0:
        raise CandidateError(f"desktop publish failed\n{completed.stdout}\n{completed.stderr}")
    # Preserve the established, startup-diagnostic-compatible flat desktop
    # payload used by the user-tested candidates. The SDK emits optional
    # framework satellite-resource directories that were not part of those
    # candidates; remove them identically from both deterministic stagings.
    for child in target.iterdir():
        if child.is_dir():
            shutil.rmtree(child)


def run_startup(executable: Path) -> dict[str, Any]:
    completed = subprocess.run([str(executable), "--startup-diagnostic"], cwd=executable.parent, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30)
    if completed.returncode != 0:
        raise CandidateError(f"startup diagnostic failed: {completed.stderr[:500]}")
    value = json.loads(completed.stdout)
    if (
        value.get("result") != "PASS"
        or value.get("packageFilesVerified") is not True
        or value.get("packageTreeVerified") is not True
        or value.get("externalConnections") != 0
        or value.get("namedPipeInvoked") is not False
        or value.get("remNoteInvoked") is not False
        or value.get("mineruInvoked") is not False
    ):
        raise CandidateError("startup diagnostic violated the standalone zero-side-effect boundary")
    return value


def write_once(path: Path, value: dict[str, Any]) -> None:
    if path.exists():
        raise CandidateError(f"append-only output already exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate-id", required=True)
    parser.add_argument("--release-root", type=Path, required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--canary-root", type=Path, required=True)
    parser.add_argument("--production-pipe-receipt", type=Path, required=True)
    args = parser.parse_args()
    release_root = args.release_root.resolve()
    receipt_path = args.receipt.resolve()
    canary_root = args.canary_root.resolve()
    production_pipe_receipt = args.production_pipe_receipt.resolve()
    if not release_root.is_relative_to((PROJECT / "releases").resolve()):
        raise CandidateError("release root must stay below the project releases directory")
    if release_root.exists() or receipt_path.exists():
        raise CandidateError("append-only candidate or receipt already exists")
    for path, expected in EXPECTED.items():
        if not path.is_file() or sha256(path) != expected:
            raise CandidateError(f"authority hash mismatch: {path}")
    required_logs = [
        canary_root / "validate-contracts.log",
        canary_root / "desktop-build-and-test.log",
        canary_root / "desktop-net8-build.log",
        canary_root / "production-pipe-canary.log",
    ]
    for path in required_logs:
        if not path.is_file():
            raise CandidateError(f"required canary log missing: {path}")
    if not production_pipe_receipt.is_file():
        raise CandidateError("production Pipe Canary receipt is missing")
    test_text = (canary_root / "desktop-build-and-test.log").read_text(encoding="utf-8", errors="replace")
    build_text = (canary_root / "desktop-net8-build.log").read_text(encoding="utf-8", errors="replace")
    pipe_receipt = json.loads(production_pipe_receipt.read_text(encoding="utf-8"))
    if "PASS named-pipe-semantic-actions-direct-stage-control-no-task-cancel" not in test_text:
        raise CandidateError("desktop direct stage-control/no-task.cancel regression evidence missing")
    if "0 个警告" not in build_text or "0 个错误" not in build_text:
        raise CandidateError("desktop net8 clean-build evidence missing")
    if (
        pipe_receipt.get("result") != "PASS"
        or pipe_receipt.get("action") != "task.status.query-recent"
        or pipe_receipt.get("externalOcrInvoked") is not False
        or pipe_receipt.get("remNoteInvoked") is not False
    ):
        raise CandidateError("production v1.1 pipe query evidence missing")
    authoritative_source = PROJECT / "src/RemNoteQuickClipper.Desktop.Core"
    intermediate_hits = []
    for path in authoritative_source.glob("*.cs"):
        if "task.stage_control" in path.read_text(encoding="utf-8", errors="replace"):
            intermediate_hits.append(path.relative_to(PROJECT).as_posix())
    if intermediate_hits:
        raise CandidateError(f"forbidden task.stage_control route remains: {intermediate_hits}")

    before = source_snapshot()
    TEMP_ROOT.mkdir(parents=True, exist_ok=True)
    first = Path(tempfile.mkdtemp(prefix="desktop-playback-a-", dir=TEMP_ROOT))
    second = Path(tempfile.mkdtemp(prefix="desktop-playback-b-", dir=TEMP_ROOT))
    try:
        publish(first)
        publish(second)
        after = source_snapshot()
        if before != after:
            raise CandidateError("desktop source changed during deterministic publish")
        first_inventory = inventory(first)
        second_inventory = inventory(second)
        if first_inventory != second_inventory:
            raise CandidateError("two self-contained desktop publishes are not byte-identical")
        release_root.parent.mkdir(parents=True, exist_ok=True)
        shutil.copytree(first, release_root)
        copied = inventory(release_root)
        if copied != first_inventory:
            raise CandidateError("copied desktop candidate differs from the deterministic publish")
        build_receipt = {
            "schemaVersion": "desktop-build-receipt.v1",
            "buildId": args.candidate_id,
            "createdAt": utc_now(),
            "sdkPath": str(DOTNET),
            "sdkVersion": "8.0.424",
            "targetFramework": "net8.0-windows",
            "runtimeIdentifier": "win-x64",
            "selfContained": True,
            "receiptExcludedFromTreeHash": True,
            "treeHashAlgorithm": "sha256(utf8 concat ordinal(path + LF + sha256 + LF + bytes + LF))",
            "treeSha256": copied["treeSha256"].lower(),
            "fileCount": copied["fileCount"],
            "totalBytes": copied["totalBytes"],
            "executableSha256": sha256(release_root / "KnowledgeClipper.exe").lower(),
            "applicationDllSha256": sha256(release_root / "KnowledgeClipper.dll").lower(),
            "files": copied["files"],
        }
        write_once(release_root / "desktop-build-receipt.v1.json", build_receipt)
        startup = run_startup(release_root / "KnowledgeClipper.exe")
        sealed = inventory(release_root)
        authority = {path.relative_to(PROJECT).as_posix(): digest for path, digest in EXPECTED.items()}
        receipt = {
            "schemaVersion": "knowledge-clipper-desktop-playback-control-candidate-receipt.v1",
            "candidateId": args.candidate_id,
            "createdAt": utc_now(),
            "status": "PASS-DESKTOP-MODULE-INTEGRATED-AUTHORITY-PENDING",
            "entrypoint": str(release_root / "KnowledgeClipper.exe"),
            "entrypointSha256": sha256(release_root / "KnowledgeClipper.exe"),
            "candidate": {"root": str(release_root), **sealed},
            "desktopPayload": copied,
            "desktopBuildReceipt": {"path": str(release_root / "desktop-build-receipt.v1.json"), "sha256": sha256(release_root / "desktop-build-receipt.v1.json")},
            "sourceSnapshot": {"files": len(before), "changedDuringBuild": False, "items": before},
            "playbackStop": {
                "transport": "direct-named-pipe",
                "protocolVersion": "1.1",
                "messageType": "stage-control",
                "payload": {"operation": "speech.playback", "selector": "current-active", "reason": "user_stop"},
                "successProjection": {"state": "stop-requested", "noticeCode": "playback.stop-requested", "claimsStopped": False},
                "taskCancelMessagesObserved": 0,
                "taskStageControlIntermediateRoutes": 0,
            },
            "authorityBindings": authority,
            "automatedEvidence": {
                "startupDiagnostic": startup,
                "logs": [{"path": str(path), "sha256": sha256(path)} for path in required_logs],
                "productionPipeReceipt": {"path": str(production_pipe_receipt), "sha256": sha256(production_pipe_receipt), "observed": pipe_receipt},
            },
            "safety": {"externalWorkersInvoked": False, "physicalSpeakerInvoked": False, "remNoteInvoked": False, "ankiInvoked": False, "mineruInvoked": False},
            "integratedCandidate": {
                "ready": False,
                "blocker": "The currently published worker authority v1.2.3 release-root embeds an orchestrator that rejects IPC config v1.1; a new authority release-root containing Core v2.3 is required.",
                "probe": "canary/desktop-playback-control/authority-v123-stage-control-probe-20260828T032050Z",
            },
        }
        write_once(receipt_path, receipt)
        print(json.dumps({
            "status": receipt["status"], "entrypoint": receipt["entrypoint"],
            "candidateFiles": sealed["fileCount"], "candidateBytes": sealed["totalBytes"],
            "candidateTreeSha256": sealed["treeSha256"], "receipt": str(receipt_path),
            "receiptSha256": sha256(receipt_path),
        }, ensure_ascii=False))
        return 0
    finally:
        shutil.rmtree(first, ignore_errors=True)
        shutil.rmtree(second, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
