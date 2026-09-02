#!/usr/bin/env python3
"""Finalize the append-only v1.2.33 Desktop hand-test candidate."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib
import subprocess

from assemble_integrated_desktop_candidate import inventory
from finalize_integrated_desktop_closure_v1229 import (
    assert_inventory,
    evidence,
    load,
    relative,
    sha256,
    write_once,
)


EXPECTED = {
    "candidateId": "kc-i-desktop-closure-v1233-20260901T041500Z",
    "manifest": "0A9F0AC487D5894EF49CAE6D71082C54E10E15A4F50DA5D46A9165B84053D3E2",
    "payloadFiles": 1040,
    "payloadBytes": 431690688,
    "payloadTree": "CEFCF3018DB4D08834FE1B1B6810B55DE43D394D58B8126E2AC5E745E35DA02F",
    "sealedFiles": 1041,
    "sealedBytes": 431911296,
    "sealedTree": "533A9646F20D3661A3669F522AFA3673319C0DF9E9318D35FF98111BFA862C7E",
    "desktopReceipt": "D36E41B022BA5A36321957219438B0EC353EA6FBC8F9BAEFD4EF866E6CE9A553",
    "desktopFiles": 645,
    "desktopBytes": 169722730,
    "desktopTree": "2D70625091CC0F47D28DECEF0966EC2B5567999EBB296B6E43A14D3F80C5522F",
    "authorityFiles": 382,
    "authorityBytes": 261815132,
    "authorityTree": "4DBA693C35F2DFB36B4808672976B1AA1B56CC95446AE5D289E8FCD52D08675F",
    "checkpoint": "83F7A5D86F22E665EDA4C346150DF3A9EE4235F0B8E1CFEFF513997DA81AE13D",
    "registry": "82C168A61EB2261F25CB85EC0B9D4A32C96CF4AA6D5B3A17FD5891EAD7BB7F6C",
    "dependency": "7B398C12A9391D510FD4AAD143942BF284E0D3A8C56816C9A27A713ABF66AD8C",
    "authorityReceipt": "FA7B440F1359C801050B925986C546E2A8A5BD070C314D0A56D6AE5A0F4EC49F",
    "coreCheckpoint": "784C0B5B214A0F69690137697FA1B3A51A09B5ED2906B36B927C6E4C908A01D0",
    "portableClosure": "DE8E6A3FA0A2E1324338DF547368D651F7C796AD0061EA0911A0D587201FE6E1",
    "orchestrator": "A7BBA2667F1D1EE5D51AE8CD6B5E20054A32EB87B8AB0526946B6AD92D4E35B0",
    "ankiVersion": "1.3.2",
    "ankiExecutable": "473007F52678A5233AE6023D52BC0C6D3F28452C00244DEDAFB88131714346F3",
    "acceptOriginalAggregate": "67FF2AA88332FD93FD8C0BED58AE981F738D3DE7CDC14B6639FBED7DE72496D1",
    "submitCorrectionAggregate": "594F152202CC47F49E5F35CC722653A35B55747B202F5D42BE02E1AB4EF27866",
    "qualityAuthority": "71D597DB5880875FB522D44B4F608449835482618CDEF1B3E0ADB1EC3ADA8175",
    "qualityCorrection": "AC4BF069FC282F01378AAEFFED443042557F9961C5BC2D1B8AEC6FBEF04F39E8",
    "windowsReceipt": "1A5C442C9FB6778CA8F36C7DB600991E966E0F97594130608DA1D538BFAEF27B",
    "windowsLog": "D340C19E4BC226E29A31A11D976147384E24E42334C7997C2ABFA6B4112FA06C",
}


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
    if manifest["candidateId"] != EXPECTED["candidateId"] or sha256(manifest_path) != EXPECTED["manifest"]:
        raise AssertionError("candidate identity drift")
    payload = inventory(candidate, {manifest_path.name})
    sealed = inventory(candidate)
    assert_inventory(payload, manifest["payload"], "candidate.payload")
    if (payload["fileCount"], payload["totalBytes"], payload["treeSha256"]) != (
        EXPECTED["payloadFiles"], EXPECTED["payloadBytes"], EXPECTED["payloadTree"]
    ) or (sealed["fileCount"], sealed["totalBytes"], sealed["treeSha256"]) != (
        EXPECTED["sealedFiles"], EXPECTED["sealedBytes"], EXPECTED["sealedTree"]
    ):
        raise AssertionError("candidate aggregate drift")

    desktop_source = project / "dist/desktop-win-x64/knowledge-clipper-desktop-contract-closure-20260901T021758Z"
    desktop_receipt_path = desktop_source / "desktop-build-receipt.v1.json"
    desktop_receipt = load(desktop_receipt_path)
    if sha256(desktop_receipt_path) != EXPECTED["desktopReceipt"] or (
        desktop_receipt["fileCount"], desktop_receipt["totalBytes"], desktop_receipt["treeSha256"].upper()
    ) != (EXPECTED["desktopFiles"], EXPECTED["desktopBytes"], EXPECTED["desktopTree"]):
        raise AssertionError("Desktop source build drift")
    changed: list[str] = []
    for item in desktop_receipt["files"]:
        source = desktop_source / item["path"]
        target = candidate / item["path"]
        if not target.is_file() or source.stat().st_size != target.stat().st_size or sha256(source) != sha256(target) or sha256(target) != str(item["sha256"]).upper():
            changed.append(item["path"])
    if changed or sha256(candidate / "desktop-build-receipt.v1.json") != EXPECTED["desktopReceipt"]:
        raise AssertionError(f"Desktop payload equality failed: {changed[:5]}")

    authority_source = project / "releases/knowledge-clipper-authority-v1.2.33-20260901T041000Z/win-x64"
    authority_embedded = candidate / "authority/win-x64"
    source_inventory = inventory(authority_source)
    embedded_inventory = inventory(authority_embedded)
    assert_inventory(embedded_inventory, source_inventory, "authority-copy")
    if (source_inventory["fileCount"], source_inventory["totalBytes"], source_inventory["treeSha256"]) != (
        EXPECTED["authorityFiles"], EXPECTED["authorityBytes"], EXPECTED["authorityTree"]
    ):
        raise AssertionError("authority aggregate drift")

    authority_meta = project / "contracts/authority/worker-registry/v1.2.33-20260901T041000Z"
    authority_receipt = project / "receipts/knowledge-clipper-authority-1.2.33-20260901T041100Z.json"
    core_checkpoint = project / "contracts/core/v2.7/checkpoints/v2.7.19-20260901T040900Z/checkpoint.json"
    hash_inputs = {
        authority_meta / "checkpoint.json": EXPECTED["checkpoint"],
        authority_meta / "worker-registry.v1.json": EXPECTED["registry"],
        authority_meta / "dependency-manifest.v1.json": EXPECTED["dependency"],
        authority_meta / "portable-schema-closure-gate.v1.json": EXPECTED["portableClosure"],
        authority_receipt: EXPECTED["authorityReceipt"],
        core_checkpoint: EXPECTED["coreCheckpoint"],
    }
    for path, wanted in hash_inputs.items():
        if sha256(path) != wanted:
            raise AssertionError(f"authority/Core evidence drift: {path}")
    packaged_orchestrator = authority_embedded / "orchestrator/KnowledgeClipper.Orchestrator.exe"
    packaged_anki = authority_embedded / "workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe"
    if sha256(packaged_orchestrator) != EXPECTED["orchestrator"]:
        raise AssertionError("packaged Orchestrator SHA drift")
    anki_describe_run = subprocess.run(
        [str(packaged_anki), "describe", "--json"],
        cwd=packaged_anki.parent,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=30,
        check=False,
    )
    if anki_describe_run.returncode != 0 or not anki_describe_run.stdout.strip():
        raise AssertionError("packaged Anki describe failed")
    packaged_anki_describe = json.loads(anki_describe_run.stdout)
    if sha256(packaged_anki) != EXPECTED["ankiExecutable"] or packaged_anki_describe["workerVersion"] != EXPECTED["ankiVersion"]:
        raise AssertionError("packaged Anki 1.3.2 identity drift")
    anki_describe_path = evidence_root / "runtime/anki-describe.json"
    write_once(anki_describe_path, json.dumps(packaged_anki_describe, ensure_ascii=False, indent=2) + "\n")
    integration = load(candidate / "candidate-integration.json")
    if integration["AuthorityTreeSha256"] != EXPECTED["authorityTree"] or integration["AuthorityCheckpointId"] != "knowledge-clipper-authority-1.2.33-20260901T041100Z":
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
        startup["result"] == "PASS" and startup["packageTreeVerified"] and startup["candidateOrchestratorPathsVerified"] and startup["externalConnections"] == 0
        and registry["status"] == "valid" and registry["registryVersion"] == "1.2.33" and registry["workers"] == 5
        and query["result"] == "PASS" and query["action"] == "task.status.query-recent" and query["namedPipeInvoked"]
        and not any(query[key] for key in ("externalWorkerInvoked", "physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked"))
        and authority_closure["status"] == "passed" and authority_closure["schemasChecked"] == 62 and authority_closure["localRefsChecked"] == 188
        and desktop_closure["status"] == "passed" and desktop_closure["schemasChecked"] == 25 and desktop_closure["validFixturesAccepted"] == 53 and desktop_closure["invalidFixturesRejected"] == 14
        and secret["status"] == "passed" and secret["findingCount"] == 0
    ):
        raise AssertionError("candidate-bound automated gate failed")

    windows_receipt_path = project / "canary/desktop-windows/desktop-closure-v1229-20260901T024500Z/receipt.json"
    windows_log_path = windows_receipt_path.parent / "windows-ui-automation.log"
    if sha256(windows_receipt_path) != EXPECTED["windowsReceipt"] or sha256(windows_log_path) != EXPECTED["windowsLog"]:
        raise AssertionError("current Desktop UI evidence drift")
    windows = load(windows_receipt_path)
    if windows["automatedWindowsUiCanary"]["assertions"] != 187:
        raise AssertionError("Windows UI assertion drift")

    accept_original = project / "canary/sealed-live-ocr-anki-v1.2.29/20260901T025000Z/aggregate-receipt.json"
    submit_correction = project / "canary/sealed-live-ocr-anki-v1.2.33/20260901T041300Z/aggregate-receipt.v1.json"
    quality_authority = project / "canary/quality-authority-v1.2.33/20260901T033159Z/quality-authority-receipt.json"
    quality_correction = project / "canary/quality-authority-v1.2.33/20260901T033159Z/quality-sealed-submit-correction-increment.json"
    if sha256(accept_original) != EXPECTED["acceptOriginalAggregate"] or sha256(submit_correction) != EXPECTED["submitCorrectionAggregate"]:
        raise AssertionError("sealed OCR branch aggregate drift")
    if sha256(quality_authority) != EXPECTED["qualityAuthority"] or sha256(quality_correction) != EXPECTED["qualityCorrection"]:
        raise AssertionError("Quality authority/correction receipt drift")
    correction = load(submit_correction)
    if not (
        correction["status"] == "passed" and correction["classification"] == "sealed-orchestrator-process-e2e"
        and correction["task"]["humanAction"] == "submit_correction" and correction["task"]["ocrRerun"] is False
        and correction["attempts"]["imageOcr"] == 1 and correction["attempts"]["content"]["beforeResolution"] == 1
        and correction["attempts"]["content"]["afterResolution"] == 2 and correction["attempts"]["anki"] == 1
        and correction["attempts"]["replay"]["newAttempts"] == 0 and correction["attempts"]["replay"]["newNotes"] == 0
        and correction["destinations"]["anki"]["writes"] == 1 and correction["destinations"]["anki"]["exactReadback"] is True
        and correction["destinations"]["anki"]["front"] == "What is 19 + 23?" and correction["destinations"]["anki"]["back"] == "42"
        and correction["destinations"]["remnote"]["writes"] == 0 and correction["destinations"]["speaker"]["invocations"] == 0
    ):
        raise AssertionError("submit_correction sealed semantics drift")

    checklist_path = evidence_root / "USER-HAND-TEST.md"
    checklist = """# 零碎知识剪藏 v1.2.33 集中手测清单

唯一入口：`releases/kc-i-desktop-closure-v1233-20260901T041500Z/win-x64/KnowledgeClipper.exe`

1. 从旧版托盘菜单退出旧实例，再启动上述入口；不得强制结束旧进程。
2. 设置保持打开时拖动/缩放悬浮窗，并测试托盘“恢复默认大小”。
3. 在浏览器或编辑器选中有标题和正文层级的内容，开启“上下文”，分别测试摘录和朗读，观察是否仍误报无上下文。
4. 保存 Anki 目标后测试普通摘录、底部输入框初问、答案、追问和制卡，以及含义读音/书写/双向三种语言策略。
5. 截图/继续必须调用系统截图；OCR 进入复核时分别测试“使用原文继续”“编辑后继续”“取消”。继续后只应生成一次后续写入，重播不重复写入。
6. Anki 原图路线可测试；RemNote 原图仍必须明确显示 unsupported。

自动候选门禁没有调用 RemNote、Anki、MinerU、DeepSeek 或扬声器。外部应用上下文、跨屏异 DPI 与真实目标写入属于本清单的人工观察边界。
"""
    write_once(checklist_path, checklist)

    result = {
        "schemaVersion": "knowledge-clipper-integrated-desktop-v1.2.33-receipt.v1",
        "createdAt": dt.datetime.now(dt.timezone.utc).isoformat(),
        "status": "PASS-CANDIDATE-BOUND-USER-HANDTEST-READY",
        "candidateId": EXPECTED["candidateId"],
        "productName": "零碎知识剪藏",
        "userHandTestEntrypoint": True,
        "finalUniqueEntrypoint": True,
        "entrypoint": relative(candidate / "KnowledgeClipper.exe", project),
        "entrypointSha256": sha256(candidate / "KnowledgeClipper.exe"),
        "applicationDllSha256": sha256(candidate / "KnowledgeClipper.dll"),
        "candidate": {"manifest": evidence(manifest_path, project), "payload": {"fileCount": payload["fileCount"], "totalBytes": payload["totalBytes"], "treeSha256": payload["treeSha256"]}, "sealed": {"fileCount": sealed["fileCount"], "totalBytes": sealed["totalBytes"], "treeSha256": sealed["treeSha256"]}},
        "desktopPayloadEquality": {"sourceBuild": relative(desktop_source, project), "filesCompared": EXPECTED["desktopFiles"], "changedFiles": 0, "treeSha256": EXPECTED["desktopTree"], "buildReceipt": evidence(desktop_receipt_path, project), "windowsUiAssertionsReusedBecausePayloadIdentical": 187},
        "authority": {"version": "1.2.33", "embeddedCopiedExactly": True, "fileCount": EXPECTED["authorityFiles"], "totalBytes": EXPECTED["authorityBytes"], "treeSha256": EXPECTED["authorityTree"], "checkpoint": evidence(authority_meta / "checkpoint.json", project), "registry": evidence(authority_meta / "worker-registry.v1.json", project), "dependencyManifest": evidence(authority_meta / "dependency-manifest.v1.json", project), "authorityReceipt": evidence(authority_receipt, project), "coreCheckpoint": evidence(core_checkpoint, project), "packagedOrchestrator": evidence(packaged_orchestrator, project), "packagedAnki": {"workerVersion": EXPECTED["ankiVersion"], **evidence(packaged_anki, project), "describe": evidence(anki_describe_path, project)}},
        "automatedGates": {"safeStartup": evidence(startup_path, project), "registry5Of5": evidence(registry_path, project), "namedPipeQueryRecent": evidence(query_path, project), "authorityPortableClosure": evidence(authority_closure_path, project), "desktopAutomationClosure": evidence(desktop_closure_path, project), "secretScan": evidence(secret_path, project), "windowsUi187": evidence(windows_receipt_path, project), "externalServicesInvoked": 0},
        "sealedBackendBranches": {"acceptOriginal": evidence(accept_original, project), "submitCorrection": evidence(submit_correction, project), "qualityAuthorityAudit": evidence(quality_authority, project), "qualitySubmitCorrectionAudit": evidence(quality_correction, project), "ocrRerunOnCorrection": 0, "ankiWritesAfterCorrection": 1, "exactReadback": True, "replayNewAttempts": 0, "replayNewNotes": 0, "scope": "sealed-authority-process-not-Desktop-UI-E2E"},
        "safety": {"workerRunsByThisCandidateGate": 0, "externalCallsByThisCandidateGate": 0, "destinationWritesByThisCandidateGate": 0, "physicalSpeakerInvocationsByThisCandidateGate": 0, "existingUserProcessTerminated": False, "productionLegacyRetentionDisabled": True, "secretFindings": 0},
        "manualBoundary": {"checklist": evidence(checklist_path, project), "externalApplicationContext": "manual", "mixedDpiCrossScreen": "manual", "realDestinationWrites": "manual-explicit-user-action", "remNoteOriginalMedia": "unsupported"},
    }
    write_once(receipt_path, json.dumps(result, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps({"status": result["status"], "entrypoint": result["entrypoint"], "receipt": evidence(receipt_path, project), "sealedTreeSha256": sealed["treeSha256"]}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
