#!/usr/bin/env python3
"""Seal candidate-bound evidence for the v1.2.29 Desktop closure hand-test build."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import pathlib
from typing import Any

from assemble_integrated_desktop_candidate import inventory
from verify_settings_control_plane import scan_runtime_tree_for_secrets


EXPECTED = {
    "candidateId": "kc-i-desktop-closure-v1229-20260901T024000Z",
    "manifestSha256": "408B572239D3B434A8C9960C30D83CD48EEBDE882D055544DE3525087B14F905",
    "payloadFiles": 1223,
    "payloadBytes": 438011755,
    "payloadTree": "D9C8FE7658230E7982C452F395F33F6BF8B4673F278F9FB41513B7BAE92729CC",
    "sealedFiles": 1224,
    "sealedBytes": 438270816,
    "sealedTree": "0556ED90531DCE9238143D927F1576A47CD2A2DFC5971F1594215DB729CCE4D5",
    "desktopReceipt": "D36E41B022BA5A36321957219438B0EC353EA6FBC8F9BAEFD4EF866E6CE9A553",
    "desktopFiles": 645,
    "desktopBytes": 169722730,
    "desktopTree": "2D70625091CC0F47D28DECEF0966EC2B5567999EBB296B6E43A14D3F80C5522F",
    "authorityFiles": 565,
    "authorityBytes": 268135929,
    "authorityTree": "A88F266457E76F3122CC4D9FE68B0B08A97D9AD342D2184A7BEF0BAB2687C006",
    "authorityCheckpoint": "1F1439648D69FBE87758FF6762A38D58798059A6BA27FECE051DE5CB9F2972B3",
    "authorityRegistry": "584B76D471374D96A6732AFBF22D3FD2A08FFBB7DDEC92C5D7733983747A9735",
    "authorityDependency": "D69F2359C65637B00D4D305EC388E4BEF129B4252CEC57F41BF1904CF29088D1",
    "authorityReceipt": "B92A5163D76C4F5D99410158D2400D8AE7BEF415A8F7D0D6C77CA2B7B799C129",
    "coreCheckpoint": "779F9DCD4E3B13644C4C7FC1DA2A2ED8ECDB8B8759DE07AC42CA0EADF23B19D9",
    "portableClosure": "1ECF3421A4DD0A0225D1E727B7643DC618AE46ACB2144A7C424ED3948857B5E3",
    "sealedAggregate": "67FF2AA88332FD93FD8C0BED58AE981F738D3DE7CDC14B6639FBED7DE72496D1",
    "windowsReceipt": "1A5C442C9FB6778CA8F36C7DB600991E966E0F97594130608DA1D538BFAEF27B",
    "windowsLog": "D340C19E4BC226E29A31A11D976147384E24E42334C7997C2ABFA6B4112FA06C",
}


def sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def load(path: pathlib.Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def relative(path: pathlib.Path, root: pathlib.Path) -> str:
    return path.resolve().relative_to(root.resolve()).as_posix()


def evidence(path: pathlib.Path, root: pathlib.Path) -> dict[str, Any]:
    return {"path": relative(path, root), "bytes": path.stat().st_size, "sha256": sha256(path)}


def assert_inventory(actual: dict[str, Any], expected: dict[str, Any], label: str) -> None:
    for key in ("fileCount", "totalBytes", "treeSha256"):
        left = actual[key]
        right = expected[key]
        if key == "treeSha256":
            left, right = str(left).upper(), str(right).upper()
        if left != right:
            raise AssertionError(f"{label}.{key}: {left!r} != {right!r}")
    actual_files = {item["path"]: item for item in actual["files"]}
    expected_files = {item["path"]: item for item in expected["files"]}
    if set(actual_files) != set(expected_files):
        raise AssertionError(f"{label}.file-set mismatch")
    for name, item in actual_files.items():
        wanted = expected_files[name]
        if int(item["bytes"]) != int(wanted["bytes"]) or str(item["sha256"]).upper() != str(wanted["sha256"]).upper():
            raise AssertionError(f"{label}.file mismatch: {name}")


def write_once(path: pathlib.Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        stream.write(text)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project-root", required=True, type=pathlib.Path)
    parser.add_argument("--candidate-root", required=True, type=pathlib.Path)
    parser.add_argument("--evidence-root", required=True, type=pathlib.Path)
    parser.add_argument("--receipt", required=True, type=pathlib.Path)
    args = parser.parse_args()

    project = args.project_root.resolve()
    candidate = args.candidate_root.resolve()
    evidence_root = args.evidence_root.resolve()
    receipt_path = args.receipt.resolve()
    manifest_path = candidate / "candidate-file-manifest.v1.json"
    manifest = load(manifest_path)
    if manifest["candidateId"] != EXPECTED["candidateId"] or sha256(manifest_path) != EXPECTED["manifestSha256"]:
        raise AssertionError("candidate manifest identity drift")
    payload = inventory(candidate, {manifest_path.name})
    sealed = inventory(candidate)
    assert_inventory(payload, manifest["payload"], "candidate.payload")
    if (payload["fileCount"], payload["totalBytes"], payload["treeSha256"]) != (
        EXPECTED["payloadFiles"], EXPECTED["payloadBytes"], EXPECTED["payloadTree"]
    ):
        raise AssertionError("candidate payload aggregate drift")
    if (sealed["fileCount"], sealed["totalBytes"], sealed["treeSha256"]) != (
        EXPECTED["sealedFiles"], EXPECTED["sealedBytes"], EXPECTED["sealedTree"]
    ):
        raise AssertionError("candidate sealed aggregate drift")

    desktop_source = project / "dist/desktop-win-x64/knowledge-clipper-desktop-contract-closure-20260901T021758Z"
    desktop_receipt_path = desktop_source / "desktop-build-receipt.v1.json"
    desktop_receipt = load(desktop_receipt_path)
    if sha256(desktop_receipt_path) != EXPECTED["desktopReceipt"]:
        raise AssertionError("Desktop build receipt drift")
    if (desktop_receipt["fileCount"], desktop_receipt["totalBytes"], desktop_receipt["treeSha256"].upper()) != (
        EXPECTED["desktopFiles"], EXPECTED["desktopBytes"], EXPECTED["desktopTree"]
    ):
        raise AssertionError("Desktop build aggregate drift")
    desktop_changed: list[str] = []
    for item in desktop_receipt["files"]:
        name = item["path"]
        source = desktop_source / name
        target = candidate / name
        if (
            not source.is_file() or not target.is_file()
            or source.stat().st_size != int(item["bytes"])
            or target.stat().st_size != int(item["bytes"])
            or sha256(source) != str(item["sha256"]).upper()
            or sha256(target) != str(item["sha256"]).upper()
        ):
            desktop_changed.append(name)
    if desktop_changed or sha256(candidate / desktop_receipt_path.name) != EXPECTED["desktopReceipt"]:
        raise AssertionError(f"Desktop payload equality failed: {desktop_changed[:5]}")

    authority_source = project / "releases/knowledge-clipper-authority-v1.2.29-20260901T023500Z/win-x64"
    authority_embedded = candidate / "authority/win-x64"
    source_authority = inventory(authority_source)
    embedded_authority = inventory(authority_embedded)
    assert_inventory(embedded_authority, source_authority, "authority-copy")
    if (source_authority["fileCount"], source_authority["totalBytes"], source_authority["treeSha256"]) != (
        EXPECTED["authorityFiles"], EXPECTED["authorityBytes"], EXPECTED["authorityTree"]
    ):
        raise AssertionError("authority aggregate drift")

    authority_meta = project / "contracts/authority/worker-registry/v1.2.29-20260901T023500Z"
    authority_receipt = project / "receipts/knowledge-clipper-authority-1.2.29-20260901T024500Z.json"
    core_checkpoint = project / "contracts/core/v2.7/checkpoints/v2.7.16-20260901T023000Z/checkpoint.json"
    portable_source = authority_meta / "portable-schema-closure-gate.v1.json"
    sealed_aggregate = project / "canary/sealed-live-ocr-anki-v1.2.29/20260901T025000Z/aggregate-receipt.json"
    hashes = {
        authority_meta / "checkpoint.json": EXPECTED["authorityCheckpoint"],
        authority_meta / "worker-registry.v1.json": EXPECTED["authorityRegistry"],
        authority_meta / "dependency-manifest.v1.json": EXPECTED["authorityDependency"],
        authority_receipt: EXPECTED["authorityReceipt"],
        core_checkpoint: EXPECTED["coreCheckpoint"],
        portable_source: EXPECTED["portableClosure"],
        sealed_aggregate: EXPECTED["sealedAggregate"],
    }
    for path, wanted in hashes.items():
        if sha256(path) != wanted:
            raise AssertionError(f"authority/Core evidence drift: {path}")
    integration = load(candidate / "candidate-integration.json")
    if integration["AuthorityTreeSha256"] != EXPECTED["authorityTree"] or integration["AuthorityCheckpointId"] != "knowledge-clipper-authority-1.2.29-20260901T024500Z":
        raise AssertionError("candidate authority binding drift")

    runtime = evidence_root / "runtime"
    startup_path = runtime / "startup.stdout.json"
    registry_path = runtime / "registry.stdout.json"
    query_path = runtime / "integration-query.stdout.json"
    authority_closure_path = runtime / "authority-schema-closure.json"
    desktop_closure_path = runtime / "desktop-automation-closure.json"
    secret_path = runtime / "secret-scan.json"
    startup, registry, query = load(startup_path), load(registry_path), load(query_path)
    authority_closure, desktop_closure, secret = load(authority_closure_path), load(desktop_closure_path), load(secret_path)
    if not (
        startup["result"] == "PASS" and startup["packageTreeVerified"] is True
        and startup["candidateOrchestratorPathsVerified"] is True and startup["externalConnections"] == 0
        and registry["status"] == "valid" and registry["registryVersion"] == "1.2.29" and registry["workers"] == 5
        and query["result"] == "PASS" and query["action"] == "task.status.query-recent" and query["namedPipeInvoked"] is True
        and not any(query[key] for key in ("externalWorkerInvoked", "physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked"))
        and authority_closure["status"] == "passed" and authority_closure["schemasChecked"] == 62 and authority_closure["localRefsChecked"] == 188
        and desktop_closure["status"] == "passed" and desktop_closure["schemasChecked"] == 25
        and desktop_closure["validFixturesAccepted"] == 53 and desktop_closure["invalidFixturesRejected"] == 14
        and secret["status"] == "passed" and secret["findingCount"] == 0
    ):
        raise AssertionError("candidate runtime/closure/safety gate failed")

    windows_receipt_path = project / "canary/desktop-windows/desktop-closure-v1229-20260901T024500Z/receipt.json"
    windows_log_path = windows_receipt_path.parent / "windows-ui-automation.log"
    if sha256(windows_receipt_path) != EXPECTED["windowsReceipt"] or sha256(windows_log_path) != EXPECTED["windowsLog"]:
        raise AssertionError("Windows UI receipt drift")
    windows_receipt = load(windows_receipt_path)
    windows_log = windows_log_path.read_text(encoding="utf-8")
    required_feedback = [
        "PASS settings opens modelessly and never disables floating-window dragging or actions",
        "PASS a real corner drag resizes the floating client area even while the modeless settings window is open",
        "PASS explicit Anki probe dispatches once through the injected Core host and projects a redacted status plus receipt",
        "PASS Anki read-only probe preserves the concrete worker reasonCode and failing step instead of collapsing them to a generic popup",
        "PASS RemNote UI dispatches the saved active-profile target through the public read-only Core probe and ignores unsaved advanced text",
        "PASS inline Enter gives immediate processing feedback and automatically opens the answer/follow-up/make-cards window",
        "PASS Enter emits exactly one initial question, keeps the accepted question session associated and releases other desktop actions after the answer window is locally closed",
    ]
    if windows_receipt["automatedWindowsUiCanary"]["assertions"] != 187 or any(line not in windows_log for line in required_feedback):
        raise AssertionError("user-feedback Windows UI regression failed")
    sealed_live = load(sealed_aggregate)
    if not (
        sealed_live["status"] == "passed"
        and sealed_live["originalTask"]["statusBeforeHumanDecision"] == "waiting_input"
        and sealed_live["originalTask"]["sideEffects"]["mineruSubmissions"] == 1
        and sealed_live["originalTask"]["sideEffects"]["ankiWrites"] == 0
        and sealed_live["humanContinuation"]["decision"] == "accept_original"
        and sealed_live["humanContinuation"]["ocrRerun"] is False
        and sealed_live["humanContinuation"]["sideEffects"]["ankiWrites"] == 1
        and sealed_live["humanContinuation"]["ankiExactReadback"]["exactReadback"] is True
        and sealed_live["humanContinuation"]["replay"]["newAttempts"] == 0
        and sealed_live["humanContinuation"]["replay"]["newNotes"] == 0
    ):
        raise AssertionError("sealed backend aggregate semantic drift")

    fresh_secret = scan_runtime_tree_for_secrets(candidate)
    if fresh_secret["status"] != "passed" or fresh_secret["findingCount"] != 0:
        raise AssertionError("fresh secret scan failed")

    checklist_path = evidence_root / "USER-HAND-TEST.md"
    checklist = """# 零碎知识剪藏 v1.2.29 集中手测清单

入口：`releases/kc-i-desktop-closure-v1229-20260901T024000Z/win-x64/KnowledgeClipper.exe`

1. 先从旧版托盘菜单退出旧实例，再双击上述入口；若旧实例仍在，确认出现明确提示而不是静默无响应。
2. 打开设置窗口后拖动并从四角缩放悬浮窗；确认设置保持打开、悬浮窗仍可移动，且可通过托盘“恢复默认大小”。
3. 在浏览器或编辑器选中包含标题、正文层级的文本，打开“上下文”，分别测试“摘录/朗读”；记录状态条结果以及是否仍误报“没有上下文”。
4. 设置中选择 Anki 目标；测试普通文本摘录、底部输入框初问/答案/追问/制卡，以及含义读音、书写、双向三种语言策略。
5. 点击截图/继续时确认调用系统截图；OCR 若进入人工复核，分别验证“使用原文继续”“编辑后继续”“取消”，并检查只有继续动作才进入后续 Anki 写入。
6. 原图到 Anki 可测试；RemNote 原图仍应明确显示 unsupported，不应静默丢图或伪装成功。

本轮自动门禁未调用 RemNote、Anki、MinerU、DeepSeek 或扬声器。外部应用真实上下文、跨屏异 DPI 和真实目标写入属于本清单的人工观察边界。
"""
    write_once(checklist_path, checklist)

    result = {
        "schemaVersion": "knowledge-clipper-integrated-desktop-v1.2.29-receipt.v1",
        "createdAt": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "PASS-CANDIDATE-BOUND-USER-HANDTEST-READY",
        "candidateId": EXPECTED["candidateId"],
        "productName": "零碎知识剪藏",
        "userHandTestEntrypoint": True,
        "entrypoint": relative(candidate / "KnowledgeClipper.exe", project),
        "entrypointSha256": sha256(candidate / "KnowledgeClipper.exe"),
        "applicationDllSha256": sha256(candidate / "KnowledgeClipper.dll"),
        "candidate": {
            "manifest": evidence(manifest_path, project),
            "payload": {"fileCount": payload["fileCount"], "totalBytes": payload["totalBytes"], "treeSha256": payload["treeSha256"]},
            "sealed": {"fileCount": sealed["fileCount"], "totalBytes": sealed["totalBytes"], "treeSha256": sealed["treeSha256"]},
        },
        "desktopPayloadEquality": {"sourceBuild": relative(desktop_source, project), "filesCompared": EXPECTED["desktopFiles"], "changedFiles": 0, "treeSha256": EXPECTED["desktopTree"], "buildReceipt": evidence(desktop_receipt_path, project)},
        "authority": {"version": "1.2.29", "embeddedCopiedExactly": True, "fileCount": EXPECTED["authorityFiles"], "totalBytes": EXPECTED["authorityBytes"], "treeSha256": EXPECTED["authorityTree"], "checkpoint": evidence(authority_meta / "checkpoint.json", project), "registry": evidence(authority_meta / "worker-registry.v1.json", project), "dependencyManifest": evidence(authority_meta / "dependency-manifest.v1.json", project), "authorityReceipt": evidence(authority_receipt, project), "coreCheckpoint": evidence(core_checkpoint, project)},
        "automatedGates": {
            "safeStartup": evidence(startup_path, project),
            "registry5Of5": evidence(registry_path, project),
            "namedPipeQueryRecent": evidence(query_path, project),
            "authorityPortableClosure": evidence(authority_closure_path, project),
            "desktopAutomationClosure": evidence(desktop_closure_path, project),
            "secretScan": evidence(secret_path, project),
            "windowsUi187": evidence(windows_receipt_path, project),
            "fourUserFeedbackFixes": "passed-current-source-current-build",
        },
        "backendQualification": {"scope": "sealed-authority-orchestrator-process-not-desktop-ui-e2e", "aggregate": evidence(sealed_aggregate, project), "waitingInputToAcceptedContinuation": True, "ocrRerun": 0, "ankiWritesAfterHumanDecision": 1, "exactReadback": True, "replayNewAttempts": 0, "replayNewNotes": 0},
        "safety": {"externalServicesInvokedByThisGate": 0, "workerRunsByThisGate": 0, "destinationWritesByThisGate": 0, "physicalSpeakerInvocationsByThisGate": 0, "existingUserProcessTerminated": False, "productionLegacyRetentionDisabled": True, "secretFindings": 0},
        "manualBoundary": {"checklist": evidence(checklist_path, project), "externalApplicationContext": "manual", "mixedDpiCrossScreen": "manual", "realDestinationWrites": "manual-explicit-user-action", "remNoteOriginalMedia": "unsupported"},
    }
    write_once(receipt_path, json.dumps(result, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps({"status": result["status"], "entrypoint": result["entrypoint"], "receipt": evidence(receipt_path, project), "sealedTreeSha256": sealed["treeSha256"]}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
