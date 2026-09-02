from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[1]
CANDIDATE_ID = "kc-i-module-actions-v1237-20260901T075500Z"
CANDIDATE = PROJECT / "releases" / CANDIDATE_ID / "win-x64"
DESKTOP = PROJECT / "dist/desktop-win-x64/knowledge-clipper-module-actions-v1237-20260901T075000Z"
AUTHORITY = PROJECT / "releases/knowledge-clipper-authority-v1.2.37-20260901T063000Z/win-x64"
AUTHORITY_META = PROJECT / "contracts/authority/worker-registry/v1.2.37-20260901T063000Z"
AUTHORITY_RECEIPT = PROJECT / "receipts/knowledge-clipper-authority-1.2.37-20260901T063200Z.json"
CORE_CHECKPOINT = PROJECT / "contracts/core/v2.9/checkpoints/v2.9.4-20260901T062900Z/checkpoint.json"
QUALITY_RECEIPT = PROJECT / "canary/quality-authority-v1.2.37/20260901T064000Z-independent/quality-receipt.json"
HISTORICAL_PLAYBACK = PROJECT / "canary/quality-next-desktop-regressions/20260901T052000Z-core-precheckpoint/quality-receipt.json"
WINDOWS_LOG = PROJECT / "canary/desktop-windows/desktop-v1237-module-actions-20260901T074500Z/windows-ui-automation.log"
GATES = PROJECT / "canary/desktop-integrated" / CANDIDATE_ID / "candidate-gates-20260901T080000Z"
CHECKLIST = GATES.parent / "USER-HAND-TEST.md"
RECEIPT = PROJECT / "receipts/desktop-integrated-module-actions-v1.2.37-20260901T080500Z.json"

EXPECTED = {
    "authorityTree": "973E63B7F876A650D6EF4CFAF6D31F6386510BDE81FB2F096F83C403982CECCC",
    "authorityCheckpoint": "6C9ABC96A05EFFE1E09FB7E3C78BF4596654BE61B05826E16E1FCABF54C89018",
    "authorityRegistry": "1C2E21A92198C25C52DDD6B528F3A50A00E90B741557EC2C22E2FB3C9460EDCC",
    "authorityReceipt": "C7063AD026F4FCF4738A653C6C19271BC9C625F1AD80918C6E6C7A8098B1D20D",
    "coreCheckpoint": "FF766146F8D8EF67F98B75B3BA4345F98C50E5F6A5B9023CB2DA1BAC7A0C9179",
    "qualityAuthority": "EA9130B02AAEC2F67E6913E46679CD9C12AE3AB8572E62A5D5A6DD7D2DC04797",
    "historicalPlayback": "6007DBE8EAB70DAFC5D8961A85F3A8D55979F3C949ABE39035EAD659ED959969",
    "desktopReceipt": "88AA3215112FF59B44D87721EF7AC16FC28EB7A64DEA401D120D9B7E38229291",
    "desktopTree": "AEA6BD97875E6009C753B19B3A41F8B0B78C54859493685697D90C4CDD45A20F",
    "entrypoint": "29320D1CCAA252057D011490866CC7533901740C5C6921359B7A2F9F58C4720C",
    "desktopDll": "3AB422A2B9CDA1672F5F8D1942D9B35C9178154F92A627A9C906F3E291B18DE6",
}


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def sha(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def inventory(root: Path, exclude: set[str] | None = None):
    excluded = exclude or set()
    rows = []
    for path in root.rglob("*"):
        if path.is_file():
            relative = path.relative_to(root).as_posix()
            if relative not in excluded:
                rows.append({"path": relative, "bytes": path.stat().st_size, "sha256": sha(path)})
    rows.sort(key=lambda row: row["path"])
    material = "\n".join(f"{row['path']}\t{row['bytes']}\t{row['sha256'].lower()}" for row in rows)
    return {"files": rows, "fileCount": len(rows), "totalBytes": sum(row["bytes"] for row in rows), "treeSha256": hashlib.sha256(material.encode()).hexdigest().upper()}


def rel(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def evidence(path: Path):
    return {"path": rel(path), "bytes": path.stat().st_size, "sha256": sha(path)}


def write_once(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        stream.write(text)


def main() -> int:
    required = [CANDIDATE, DESKTOP, AUTHORITY, AUTHORITY_META, AUTHORITY_RECEIPT, CORE_CHECKPOINT, QUALITY_RECEIPT, HISTORICAL_PLAYBACK, WINDOWS_LOG, GATES]
    for path in required:
        require(path.exists(), f"missing input: {path}")
    require(not RECEIPT.exists() and not CHECKLIST.exists(), "append-only final output exists")

    desktop_receipt_path = DESKTOP / "desktop-build-receipt.v1.json"
    desktop_receipt = load(desktop_receipt_path)
    require(sha(desktop_receipt_path) == EXPECTED["desktopReceipt"], "Desktop receipt drift")
    require((desktop_receipt["fileCount"], desktop_receipt["totalBytes"], desktop_receipt["treeSha256"].upper()) == (645, 169868871, EXPECTED["desktopTree"]), "Desktop aggregate drift")
    changed = []
    for row in desktop_receipt["files"]:
        source, target = DESKTOP / row["path"], CANDIDATE / row["path"]
        if not target.is_file() or source.stat().st_size != target.stat().st_size or sha(source) != sha(target) or sha(target) != str(row["sha256"]).upper():
            changed.append(row["path"])
    require(not changed and sha(CANDIDATE / "desktop-build-receipt.v1.json") == EXPECTED["desktopReceipt"], f"Desktop payload drift: {changed[:5]}")
    require(sha(CANDIDATE / "KnowledgeClipper.exe") == EXPECTED["entrypoint"] and sha(CANDIDATE / "KnowledgeClipper.dll") == EXPECTED["desktopDll"], "Desktop binary identity drift")

    manifest_path = CANDIDATE / "candidate-file-manifest.v1.json"
    manifest = load(manifest_path)
    payload = inventory(CANDIDATE, {manifest_path.name})
    sealed = inventory(CANDIDATE)
    declared = [{"path": row["path"], "bytes": int(row["bytes"]), "sha256": str(row["sha256"]).upper()} for row in manifest["payload"]["files"]]
    require(manifest["candidateId"] == CANDIDATE_ID and declared == payload["files"], "candidate manifest inventory drift")
    require((manifest["payload"]["fileCount"], manifest["payload"]["totalBytes"], manifest["payload"]["treeSha256"].upper()) == (payload["fileCount"], payload["totalBytes"], payload["treeSha256"]), "candidate manifest aggregate drift")

    authority_source, authority_embedded = inventory(AUTHORITY), inventory(CANDIDATE / "authority/win-x64")
    require(authority_source == authority_embedded, "embedded authority differs from v1.2.37")
    require((authority_source["fileCount"], authority_source["totalBytes"], authority_source["treeSha256"]) == (404, 262238747, EXPECTED["authorityTree"]), "authority aggregate drift")
    exact = {
        AUTHORITY_META / "checkpoint.json": EXPECTED["authorityCheckpoint"], AUTHORITY_META / "worker-registry.v1.json": EXPECTED["authorityRegistry"],
        AUTHORITY_RECEIPT: EXPECTED["authorityReceipt"], CORE_CHECKPOINT: EXPECTED["coreCheckpoint"], QUALITY_RECEIPT: EXPECTED["qualityAuthority"], HISTORICAL_PLAYBACK: EXPECTED["historicalPlayback"],
    }
    for path, expected in exact.items():
        require(sha(path) == expected, f"authority/Core/Quality evidence drift: {path}")
    integration = load(CANDIDATE / "candidate-integration.json")
    require(integration["AuthorityTreeSha256"] == EXPECTED["authorityTree"] and integration["AuthorityCheckpointId"] == "knowledge-clipper-authority-1.2.37-20260901T063200Z", "candidate authority binding drift")
    require(integration["AutoStart"] is True and integration["PipeName"] == "KnowledgeClipper.Integrated.v1237.T075500" and integration["IpcConfig"].endswith("v1_4_0.json"), "candidate IPC/autostart drift")
    ipc = load(CANDIDATE / integration["IpcConfig"])
    require(ipc["schemaVersion"] == "1.4.0" and ipc["pipeName"] == integration["PipeName"], "candidate IPC 1.4 config drift")

    startup = load(GATES / "startup.stdout.json")
    registry = load(GATES / "registry.stdout.json")
    query = load(GATES / "integration-query.stdout.json")
    single = load(GATES / "single-instance.stdout.json")
    closure = load(GATES / "authority-schema-closure.json")
    desktop_closure = load(GATES / "desktop-automation-closure.json")
    secret = load(GATES / "secret-scan.json")
    build_text = (GATES / "desktop-build-and-test.log").read_text(encoding="utf-8-sig")
    ui_text = WINDOWS_LOG.read_text(encoding="utf-8-sig")
    require(startup["result"] == "PASS" and startup["packageTreeVerified"] and startup["candidateOrchestratorPathsVerified"] and startup["externalConnections"] == 0, "safe startup gate failed")
    require(registry == {"schemaVersion": "1.0.0", "status": "valid", "registryVersion": "1.2.37", "workers": 5}, "registry gate failed")
    require(query["result"] == "PASS" and query["orchestratorStartedByDesktop"] and query["namedPipeInvoked"] and not any(query[key] for key in ("externalWorkerInvoked", "physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")), "Named Pipe query gate failed")
    require(single["existingInstanceDetected"] and single["userFeedbackMode"] == "activate-existing-window-and-message-box", "single-instance launch remains silent")
    require(closure["status"] == "passed" and closure["schemasChecked"] == 68 and closure["localRefsChecked"] == 196 and not closure["failures"], "portable recursive schema closure failed")
    require(desktop_closure["status"] == "passed" and desktop_closure["schemasChecked"] == 25 and desktop_closure["validFixturesAccepted"] == 53 and desktop_closure["invalidFixturesRejected"] == 14, "Desktop contract closure failed")
    require(secret["status"] == "passed" and secret["findingCount"] == 0, "candidate secret scan failed")
    require("PASS named-pipe-public-module-actions-v1.2-v1.3-v1.4" in build_text and "PASS desktop automation build and tests" in build_text, "Desktop semantic suite evidence missing")
    for marker in (
        "WINDOWS_UI_CANARY PASS assertions=210",
        "PASS settings opens modelessly and never disables floating-window dragging or actions",
        "PASS a real corner drag resizes the floating client area even while the modeless settings window is open",
        "PASS context mode consumes a reliable Windows text-pattern context projection",
        "PASS authority-admitted Anki initialization dispatches through public IPC 1.4",
        "PASS Desktop creates the canonical immutable package with exact SHA-bound config and explicit authorization",
        "PASS Desktop binds every authority v1.2.37 public action to the strict IPC 1.4 v3 envelope",
    ):
        require(marker in ui_text, marker)
    production = (PROJECT / "src/Program.cs").read_text(encoding="utf-8")
    policy = production[production.index("internal static class ProductionStartupPolicy"):production.index("internal enum ReviewChoice")]
    require("JobStore.PurgeExpired(" not in policy, "legacy retention purge is active")

    checklist = f"""# 零碎知识剪藏 v1.2.37 集中手测清单

唯一入口：`releases/{CANDIDATE_ID}/win-x64/KnowledgeClipper.exe`

1. 先从旧版托盘菜单选择“退出”，再双击唯一入口。若旧实例仍在，新候选必须唤醒旧窗口并给出提示，不会静默假装启动成功。
2. 打开设置窗口后拖动悬浮窗，再从四角调整大小；如尺寸异常，用托盘“恢复默认大小”。
3. 在浏览器或编辑器选中包含标题、正文层级的文本，开启悬浮窗“上下文”，测试摘录与朗读；记录实际上下文是否完整。
4. 底部输入框输入问题并按 Enter，查看答案；在答案窗口测试追问与制卡。
5. 导出页选择 Anki，先点“只读连接检查”。只有返回“需要初始化”时，“初始化受管资源”才会启用；该按钮是显式写操作，只创建缺失牌组/模型，不写卡、不删除、不修改已有资源。
6. 测试语言策略：含义与读音、书写、双向。原图模式固定只导出 Anki；RemNote 原图不作为检查项，也不显示为产品缺口。
7. 截图及“继续”应调用 Windows 系统截图；OCR 未决时测试“使用原文继续”“编辑后继续”“取消”。

自动门禁未调用真实 Anki、RemNote、MinerU、DeepSeek 或扬声器。外部应用上下文、中文 IME、真实目标写入、跨屏异 DPI 仍由本次人工体验观察。
"""
    write_once(CHECKLIST, checklist)

    result = {
        "schemaVersion": "knowledge-clipper-integrated-desktop-v1.2.37-receipt.v1",
        "createdAt": datetime.now(timezone.utc).isoformat(),
        "status": "PASS-CANDIDATE-BOUND-USER-HANDTEST-READY",
        "candidateId": CANDIDATE_ID,
        "productName": "零碎知识剪藏",
        "userHandTestEntrypoint": True,
        "entrypoint": rel(CANDIDATE / "KnowledgeClipper.exe"),
        "entrypointSha256": EXPECTED["entrypoint"],
        "applicationDllSha256": EXPECTED["desktopDll"],
        "candidate": {"manifest": evidence(manifest_path), "payload": {key: payload[key] for key in ("fileCount", "totalBytes", "treeSha256")}, "sealed": {key: sealed[key] for key in ("fileCount", "totalBytes", "treeSha256")}},
        "desktopPayload": {"source": rel(DESKTOP), "filesCompared": 645, "changedFiles": 0, "treeSha256": EXPECTED["desktopTree"], "buildReceipt": evidence(desktop_receipt_path)},
        "authority": {"version": "1.2.37", "embeddedCopiedExactly": True, "fileCount": 404, "totalBytes": 262238747, "treeSha256": EXPECTED["authorityTree"], "checkpoint": evidence(AUTHORITY_META / "checkpoint.json"), "registry": evidence(AUTHORITY_META / "worker-registry.v1.json"), "authorityReceipt": evidence(AUTHORITY_RECEIPT), "coreCheckpoint": evidence(CORE_CHECKPOINT), "qualityAdmission": evidence(QUALITY_RECEIPT)},
        "moduleActions": {"ipc": "1.4", "requestSchema": "knowledge-clipper-module-action.v3", "remNoteProbeAndDiscovery": "public-read-only", "speechDiscovery": "public-read-only", "speechPreview": "explicit-physical-audio-only", "ankiProbe": "read-only", "ankiInitializeManaged": "explicit-sha-bound-public-action", "automaticInitialization": False},
        "automatedGates": {"desktopBuildAndTest": evidence(GATES / "desktop-build-and-test.log"), "windowsUi": evidence(WINDOWS_LOG), "windowsUiAssertions": 210, "safeStartup": evidence(GATES / "startup.stdout.json"), "registry5Of5": evidence(GATES / "registry.stdout.json"), "namedPipeQuery": evidence(GATES / "integration-query.stdout.json"), "singleInstance": evidence(GATES / "single-instance.stdout.json"), "authorityRecursiveClosure": evidence(GATES / "authority-schema-closure.json"), "desktopAutomationClosure": evidence(GATES / "desktop-automation-closure.json"), "secretScan": evidence(GATES / "secret-scan.json")},
        "safety": {"externalServicesInvoked": 0, "externalWorkerRuns": 0, "destinationWrites": 0, "physicalSpeakerInvocations": 0, "existingUserProcessTerminated": False, "productionLegacyRetentionDisabled": True, "secretFindings": 0, "historicalPlaybackFailurePreserved": evidence(HISTORICAL_PLAYBACK)},
        "manualBoundary": {"checklist": evidence(CHECKLIST), "externalApplicationContext": "manual", "mixedDpiCrossScreen": "manual", "realDestinationWrites": "manual-explicit-user-action", "remNoteOriginalMedia": "BY-DESIGN-UNSUPPORTED-NOT-A-GAP-NONBLOCKING"},
    }
    write_once(RECEIPT, json.dumps(result, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps({"status": result["status"], "entrypoint": result["entrypoint"], "candidateTree": sealed["treeSha256"], "manifestSha256": sha(manifest_path), "receipt": rel(RECEIPT), "receiptSha256": sha(RECEIPT)}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
