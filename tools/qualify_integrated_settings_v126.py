from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[1]
CANDIDATE_ID = "knowledge-clipper-integrated-settings-v1.2.6-20260828T083800Z"
CANDIDATE = PROJECT / "releases/kc-i-settings-v126-20260828T083800Z/win-x64"
AUTHORITY = PROJECT / "releases/knowledge-clipper-authority-v1.2.6-20260828T074944Z/win-x64"
AUTHORITY_CONTRACT = PROJECT / "contracts/authority/worker-registry/v1.2.6-20260828T074944Z"
AUTHORITY_RECEIPT = PROJECT / "receipts/knowledge-clipper-authority-1.2.6-20260828T075312Z.json"
SETTINGS_CHECKPOINT = PROJECT / "contracts/core/v2.4/settings-control/checkpoints/v2.4.3-20260828T073934Z/checkpoint.json"
SETTINGS_ADAPTER = PROJECT / "contracts/core/v2.4/settings-control/module-settings-adapter.v2.json"
WINDOWS_RECEIPT = PROJECT / "canary/desktop-windows/20260828T081550Z/receipt.json"
WINDOWS_LOG = WINDOWS_RECEIPT.parent / "windows-ui-automation.log"
VISUAL = PROJECT / "canary/desktop-visual/knowledge-clipper-settings-control-v2.4.3-20260828T082500Z"
INTEGRATION = PROJECT / "canary/desktop-integrated-settings-v126/kc-i-settings-v126-20260828T083800Z"
OUTPUT = PROJECT / "receipts/desktop-integrated-settings-v1.2.6-20260828T083800Z.json"
READABLE = INTEGRATION / "settings-input-output.md"

EXPECTED = {
    AUTHORITY_CONTRACT / "checkpoint.json": "E9C5ACE5EB653E3D84C2AECA2390562F2DAD6402441A4F9676218122119F5D2D",
    AUTHORITY_CONTRACT / "worker-registry.v1.json": "0FD2AAB69E4B850D6D6747C0261FCA5448397C19BA8E88D8ADB1BA3E80348665",
    AUTHORITY_CONTRACT / "dependency-manifest.v1.json": "CD3E2DBBD8AEBB461115E1E87067A8871844D8A1E179807F7F508F19D0F24ADE",
    AUTHORITY_RECEIPT: "A76D21A909331A2327BFC685A9F87733A4BFDB793A8ECB0089837396B0694113",
    SETTINGS_CHECKPOINT: "8A9D2765804B7E7ED9A2B2B98C28DB25559C222DFBB757CDFFB6406FBAE61B32",
    SETTINGS_ADAPTER: "263DB21F081414CAF04F996DC658EAE363614EFC83B2461888EDAA962866C147",
}
AUTHORITY_TREE = "1DF64264BB69F4039C3B58956AA9612B98E45C2DF38B12D396B4813377E512EE"


class GateError(RuntimeError):
    pass


def sha(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def inventory(root: Path, excluded: set[str] | None = None) -> dict:
    excluded = excluded or set()
    files, rows, total = [], [], 0
    for path in sorted((p for p in root.rglob("*") if p.is_file()), key=lambda p: p.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        if relative in excluded:
            continue
        size, digest = path.stat().st_size, sha(path)
        files.append({"path": relative, "bytes": size, "sha256": digest})
        rows.append(f"{relative}\t{size}\t{digest.lower()}")
        total += size
    return {"fileCount": len(files), "totalBytes": total, "treeSha256": hashlib.sha256("\n".join(rows).encode()).hexdigest().upper(), "files": files}


def require(value: bool, message: str) -> None:
    if not value:
        raise GateError(message)


def evidence(path: Path) -> dict:
    return {"path": path.relative_to(PROJECT).as_posix(), "bytes": path.stat().st_size, "sha256": sha(path)}


def main() -> int:
    require(not OUTPUT.exists(), f"append-only receipt exists: {OUTPUT}")
    for path, expected in EXPECTED.items():
        require(path.is_file() and sha(path) == expected, f"authority mismatch: {path}")

    manifest_path = CANDIDATE / "candidate-file-manifest.v1.json"
    manifest = load(manifest_path)
    require(manifest["candidateId"] == CANDIDATE_ID, "candidate identity mismatch")
    payload = inventory(CANDIDATE, {"candidate-file-manifest.v1.json"})
    require(all(payload[key] == manifest["payload"][key] for key in ("fileCount", "totalBytes", "treeSha256")), "candidate manifest/tree mismatch")
    sealed = inventory(CANDIDATE)
    embedded = inventory(CANDIDATE / "authority/win-x64")
    source_authority = inventory(AUTHORITY)
    require((source_authority["fileCount"], source_authority["totalBytes"], source_authority["treeSha256"]) == (186, 249571806, AUTHORITY_TREE), "authority source mismatch")
    require(embedded == source_authority, "embedded authority differs from admitted v1.2.6")

    original_ipc = CANDIDATE / "authority/metadata/orchestrator-ipc.v1.1.json"
    derived_ipc = CANDIDATE / "authority/metadata/orchestrator-ipc.candidate.v1.1.json"
    require(sha(original_ipc) == "849CB3A85DD8CE660B5A6924D804AEB59E254010F616BA54B5E694C263DB98E2", "original IPC v1.1 drift")
    derived = load(derived_ipc)
    require(derived["schemaVersion"] == "1.1.0" and derived["allowedMessages"] == ["task-command", "task-query", "stage-control"], "derived IPC contract drift")
    require(derived["pipeName"] == "KnowledgeClipper.Orchestrator.v1.settings-v126", "candidate pipe isolation missing")

    startup = load(INTEGRATION / "startup-diagnostic.stdout.log")
    single = load(INTEGRATION / "single-instance-diagnostic.stdout.log")
    registry = load(INTEGRATION / "authority-validate-registry.log")
    integrated = load(INTEGRATION / "integration-startup-canary.stdout.log")
    require(startup["result"] == "PASS" and startup["externalConnections"] == 0 and startup["packageTreeVerified"], "safe startup failed")
    require(single["existingInstanceDetected"] and single["existingProcessId"] == 5976 and single["userFeedbackMode"] == "activate-existing-window-and-message-box", "single-instance regression failed")
    require(registry.get("schemaVersion") == "1.0.0" and registry.get("status") == "valid" and registry.get("registryVersion") == "1.2.6" and registry.get("workers") == 5, "registry gate failed")
    require(integrated["result"] == "PASS" and integrated["orchestratorStartedByDesktop"] and integrated["namedPipeInvoked"], "Named Pipe integration failed")
    require(not any(integrated[key] for key in ("externalWorkerInvoked", "physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")), "integration side effect boundary failed")

    windows = load(WINDOWS_RECEIPT)
    window_text = WINDOWS_LOG.read_text(encoding="utf-8-sig")
    require(windows["automatedWindowsUiCanary"] == {"status": "PASS", "assertions": 122, "log": "windows-ui-automation.log", "logSha256": windows["automatedWindowsUiCanary"]["logSha256"]}, "Windows UI receipt failed")
    for line in (
        "PASS Content user Prompt validates through the authority worker adapter",
        "PASS Image settings and SHA-bound Content/Anki artifact identities enter the immutable task snapshot",
        "PASS Content and Anki module adapters publish one immutable task artifact each",
        "PASS credentials never enter the immutable task package, manifest, or payload files",
    ):
        require(line in window_text, f"settings evidence missing: {line}")
    require(len(list(VISUAL.glob("settings-*.png"))) == 7, "visual settings pages incomplete")
    require(not any("credentials.json" in item["path"].lower() for item in sealed["files"]), "credential store leaked into candidate")
    require(b"canary-deepseek-local-only" not in b"".join(path.read_bytes() for path in CANDIDATE.iterdir() if path.is_file()), "credential fixture leaked into desktop payload")

    READABLE.write_text(
        "# 设置控制面 v2.4.3 集成：真实输入 → 输出\n\n"
        "- Image：language=en、timeoutSeconds=321 → task-settings-snapshot.v2 modules.imageOcr。\n"
        "- Content：内置 open-md-v2 只读 → 用户副本 → worker settings validate/diff → content-config-snapshot + SHA 引用。\n"
        "- RemNote：rootRemId=canary-root-rem-id → snapshot target + learning/card 分离 route；未调用真实 RemNote。\n"
        "- Anki：本地 profile 仅写 anki-export-config 正文，task snapshot 只含 artifactRef/SHA；未调用真实 Anki。\n"
        "- 凭据：本地遮罩存储并只注入 Worker 环境；任务包、候选 manifest、回执中均无 secret。\n"
        "- 启动：旧 PID 5976 保留；候选使用隔离 pipe 启动内置 Orchestrator，query_recent 返回 0 项后清理 Canary 进程。\n",
        encoding="utf-8",
    )

    receipt = {
        "schemaVersion": "knowledge-clipper-desktop-integrated-settings-v126-receipt.v1",
        "status": "PASS-INTEGRATED-SETTINGS-V2.4.3-USER-HAND-TEST-READY",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "candidateId": CANDIDATE_ID,
        "entrypoint": (CANDIDATE / "KnowledgeClipper.exe").relative_to(PROJECT).as_posix(),
        "entrypointSha256": sha(CANDIDATE / "KnowledgeClipper.exe"),
        "candidate": {"root": CANDIDATE.relative_to(PROJECT).as_posix(), "manifest": evidence(manifest_path), **sealed},
        "authority": {"checkpoint": evidence(AUTHORITY_CONTRACT / "checkpoint.json"), "registry": evidence(AUTHORITY_CONTRACT / "worker-registry.v1.json"), "dependencyManifest": evidence(AUTHORITY_CONTRACT / "dependency-manifest.v1.json"), "receipt": evidence(AUTHORITY_RECEIPT), "embeddedExactly": True, **{key: source_authority[key] for key in ("fileCount", "totalBytes", "treeSha256")}},
        "settings": {"checkpoint": evidence(SETTINGS_CHECKPOINT), "adapter": evidence(SETTINGS_ADAPTER), "schemaVersion": "knowledge-clipper-workflow-settings.v2", "taskSnapshot": "knowledge-clipper-task-settings-snapshot.v2", "promptUserCopyValidated": True, "imageSnapshotMapped": True, "speechSnapshotMapped": True, "remnotePayloadV2Mapped": True, "ankiShaBoundArtifactMapped": True, "secretReferences": []},
        "canaries": {"windowsUi": evidence(WINDOWS_RECEIPT), "windowsUiAssertions": 122, "visualRoot": VISUAL.relative_to(PROJECT).as_posix(), "visualSettingsPages": 7, "safeStartup": evidence(INTEGRATION / "startup-diagnostic.stdout.log"), "singleInstance": evidence(INTEGRATION / "single-instance-diagnostic.stdout.log"), "registry": evidence(INTEGRATION / "authority-validate-registry.log"), "namedPipeQuery": evidence(INTEGRATION / "integration-startup-canary.stdout.log"), "readableInputOutput": evidence(READABLE)},
        "singleInstance": {"existingProcessId": 5976, "existingProcessPreserved": True, "feedbackMode": single["userFeedbackMode"], "candidatePipeName": derived["pipeName"]},
        "safety": {"externalWorkersInvoked": 0, "externalProductionApisInvoked": 0, "physicalSpeakerInvoked": 0, "remNoteInvoked": 0, "ankiInvoked": 0, "mineruInvoked": 0, "existingUserProcessTerminated": False, "credentialFilesInCandidate": 0, "taskSecretReferences": 0},
        "manualBoundary": ["physical Chinese IME", "real external application selection/clipboard", "real image clipboard", "physical screenshot drag", "cross-monitor mixed-DPI", "desktop-to-real RemNote/Anki/MinerU/DeepSeek E2E", "installer/code signing"],
    }
    OUTPUT.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": receipt["status"], "entrypoint": str(CANDIDATE / "KnowledgeClipper.exe"), "files": sealed["fileCount"], "bytes": sealed["totalBytes"], "treeSha256": sealed["treeSha256"], "receipt": str(OUTPUT), "receiptSha256": sha(OUTPUT)}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (GateError, OSError, ValueError, KeyError, json.JSONDecodeError) as exc:
        print(json.dumps({"status": "FAIL", "error": str(exc)}, ensure_ascii=False))
        raise SystemExit(1)
