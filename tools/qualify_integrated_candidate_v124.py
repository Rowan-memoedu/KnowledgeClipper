from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

import qualify_integrated_candidate_v123 as v123_gate
import verify_integrated_candidate_quality as common


PROJECT_ROOT = common.PROJECT_ROOT
CANDIDATE_ID = "kc-i-v124-20260828T034900Z"
CANDIDATE_ROOT = PROJECT_ROOT / "releases" / CANDIDATE_ID / "win-x64"
DESKTOP_RECEIPT = PROJECT_ROOT / "receipts" / "desktop-integrated-v1.2.4-playback-control-20260828T034900Z.json"
DESKTOP_RECEIPT_SHA256 = "70F6333307A2908C6FBFF5B4566D5BD27452407AF0D2C86B61D153CD9BA6CC4F"
AUTHORITY_ROOT = PROJECT_ROOT / "releases" / "knowledge-clipper-authority-v1.2.4-20260828T032500Z" / "win-x64"
AUTHORITY_CONTRACT_ROOT = PROJECT_ROOT / "contracts" / "authority" / "worker-registry" / "v1.2.4-20260828T032500Z"
AUTHORITY_CHECKPOINT = AUTHORITY_CONTRACT_ROOT / "checkpoint.json"
AUTHORITY_CHECKPOINT_SHA256 = "E7A49572E20CA7C7E116466CEAF57659E9AD7A36CA5EC2C583940997A367F122"
AUTHORITY_REGISTRY_SHA256 = "72A6C1BA7604FBC787B21380F57DA59F7465F71E392C2F32F56E03B782E539A4"
AUTHORITY_TREE_SHA256 = "6CA2D59E66DF9F15CC1BE46890ABDA04F5FAFB98507716D91E568818A37882CE"
AUTHORITY_RECEIPT = PROJECT_ROOT / "receipts" / "knowledge-clipper-authority-1.2.4-20260828T033500Z.json"
AUTHORITY_RECEIPT_SHA256 = "14899ACDA401F00A20DB3F08DFC3C5AC8D09D12B56318C794FDAD79EB401D3AF"
DIRECT_GATE_ROOT = PROJECT_ROOT / "canary" / "desktop-integrated-v1.2.4" / "desktop-integrated-v1.2.4-20260828T035500Z"
DIRECT_GATE = DIRECT_GATE_ROOT / "playback-control-direct-ipc-gate.json"
REMNOTE_RECEIPT = v123_gate.REMNOTE_RECEIPT
REMNOTE_RECEIPT_SHA256 = v123_gate.REMNOTE_RECEIPT_SHA256

EXPECTED_CANDIDATE = (347, 400692507, "7A1BFFA0EE583D5AD47BD5755C3E43180E2D8C01AD4310497329589851F603A9")
EXPECTED_AUTHORITY = (91, 249015447, AUTHORITY_TREE_SHA256)

GateFailure = common.GateFailure


def evidence(path: Path) -> dict[str, Any]:
    try:
        display = common.project_relative(path)
    except ValueError:
        display = str(path.resolve())
    return {
        "path": display,
        "bytes": common.windows_path(path).stat().st_size,
        "sha256": common.sha256_file(path),
    }


def project_path(value: str) -> Path:
    path = Path(value)
    return path if path.is_absolute() else PROJECT_ROOT / path


def verify_ref(item: dict[str, Any], label: str) -> Path:
    value = item.get("path") or item.get("receipt") or item.get("absolutePath")
    if not value:
        raise GateFailure(f"{label}: missing path")
    path = project_path(str(value))
    expected = item.get("sha256") or item.get("receiptSha256")
    if not expected:
        raise GateFailure(f"{label}: missing sha256")
    common.assert_equal(common.sha256_file(path), str(expected).upper(), f"{label}.sha256")
    if "bytes" in item:
        common.assert_equal(common.windows_path(path).stat().st_size, int(item["bytes"]), f"{label}.bytes")
    return path


def verify_candidate_and_desktop() -> dict[str, Any]:
    common.assert_equal(common.sha256_file(DESKTOP_RECEIPT), DESKTOP_RECEIPT_SHA256, "desktop receipt")
    receipt = common.load_json(DESKTOP_RECEIPT)
    common.assert_equal(receipt["status"], "PASS-INTEGRATED-V1.2.4-PLAYBACK-CONTROL", "desktop status")
    common.assert_equal(receipt["candidateId"], CANDIDATE_ID, "candidate id")

    before = common.inventory(CANDIDATE_ROOT)
    common.assert_inventory(before, receipt["candidate"], "candidate receipt inventory")
    common.assert_equal(
        (before["fileCount"], before["totalBytes"], before["treeSha256"]),
        EXPECTED_CANDIDATE,
        "candidate exact tree",
    )

    manifest_path = verify_ref(receipt["candidate"]["manifest"], "candidate file manifest")
    manifest = common.load_json(manifest_path)
    common.assert_equal(manifest["excludedControlFiles"], ["candidate-file-manifest.v1.json"], "candidate manifest exclusion")
    common.assert_inventory(common.inventory(CANDIDATE_ROOT, set(manifest["excludedControlFiles"])), manifest["payload"], "candidate payload")
    common.assert_equal(common.sha256_file(CANDIDATE_ROOT / "KnowledgeClipper.exe"), receipt["entrypointSha256"], "entrypoint")

    verify_ref(receipt["desktop"]["moduleReceipt"], "desktop module receipt")
    verify_ref(receipt["desktop"]["priorWindowsUiReceipt"], "prior Windows UI receipt")
    build_receipt = CANDIDATE_ROOT / "desktop-build-receipt.v1.json"
    common.assert_equal(common.sha256_file(build_receipt), receipt["desktop"]["buildReceiptSha256"], "desktop build receipt")
    common.assert_equal(receipt["desktop"]["changedFilesSinceModuleCandidate"], 0, "desktop payload drift")
    common.assert_equal(receipt["desktop"]["uiRetestRequired"], False, "UI retest boundary")

    canary_records: dict[str, dict[str, Any]] = {}
    for name, item in receipt["automatedCanaries"].items():
        path = verify_ref(item, f"desktop canary {name}")
        canary_records[name] = evidence(path)

    startup = json.loads(project_path(receipt["automatedCanaries"]["safeStartup"]["path"]).read_text(encoding="utf-8-sig"))
    single = json.loads(project_path(receipt["automatedCanaries"]["singleInstance"]["path"]).read_text(encoding="utf-8-sig"))
    integration = json.loads(project_path(receipt["automatedCanaries"]["builtInOrchestrator"]["path"]).read_text(encoding="utf-8-sig"))
    common.assert_equal(startup["result"], "PASS", "safe startup")
    common.assert_equal(single["existingInstanceDetected"], True, "single instance detection")
    common.assert_equal(receipt["automatedCanaries"]["singleInstance"]["existingProcessPreserved"], True, "old process preserved")
    common.assert_equal(integration["result"], "PASS", "built-in orchestrator")
    common.assert_equal(integration["orchestratorStartedByDesktop"], True, "orchestrator started by desktop")
    common.assert_equal(integration["namedPipeInvoked"], True, "named pipe invoked")
    common.assert_equal(receipt["automatedCanaries"]["builtInOrchestrator"]["processStoppedAfterCanary"], True, "canary process cleanup")

    expected_safety = {
        "externalWorkersInvoked": False,
        "physicalSpeakerInvoked": False,
        "remNoteInvoked": False,
        "ankiInvoked": False,
        "mineruInvoked": False,
        "externalProductionApisInvoked": False,
        "preExistingDesktopProcessTerminated": False,
    }
    common.assert_equal(receipt["safety"], expected_safety, "desktop safety")
    return {
        "receipt": receipt,
        "inventory": before,
        "candidateFileManifest": evidence(manifest_path),
        "buildReceipt": evidence(build_receipt),
        "canaries": canary_records,
        "oldProcess": {
            "processId": receipt["automatedCanaries"]["singleInstance"]["existingProcessId"],
            "preservedByCanary": True,
            "feedbackMode": receipt["automatedCanaries"]["singleInstance"]["feedbackMode"],
        },
    }


def verify_authority(receipt: dict[str, Any]) -> dict[str, Any]:
    common.assert_equal(common.sha256_file(AUTHORITY_CHECKPOINT), AUTHORITY_CHECKPOINT_SHA256, "authority checkpoint")
    checkpoint = common.load_json(AUTHORITY_CHECKPOINT)
    common.assert_equal((checkpoint["admissionStatus"], checkpoint["registry"]["registryVersion"], checkpoint["registry"]["workers"]), ("authority-admitted", "1.2.4", 5), "authority admission")
    common.assert_equal(checkpoint["registry"]["sha256"], AUTHORITY_REGISTRY_SHA256, "authority registry digest")
    common.assert_equal((checkpoint["frozenV1"]["files"], checkpoint["frozenV1"]["mismatches"]), (21, 0), "frozen v1")
    common.assert_equal((checkpoint["validation"]["status"], checkpoint["validation"]["workers"]), ("valid", 5), "authority validation")

    registry_path = project_path(checkpoint["registry"]["path"])
    dependency_path = project_path(checkpoint["release"]["dependencyManifest"])
    common.assert_equal(common.sha256_file(registry_path), AUTHORITY_REGISTRY_SHA256, "registry file")
    common.assert_equal(common.sha256_file(dependency_path), checkpoint["release"]["dependencyManifestSha256"], "dependency manifest")
    common.assert_equal(common.sha256_file(AUTHORITY_RECEIPT), AUTHORITY_RECEIPT_SHA256, "authority receipt")

    source_inventory = common.inventory(AUTHORITY_ROOT)
    embedded_inventory = common.inventory(CANDIDATE_ROOT / "authority" / "win-x64")
    common.assert_equal((source_inventory["fileCount"], source_inventory["totalBytes"], source_inventory["treeSha256"]), EXPECTED_AUTHORITY, "authority source")
    common.assert_inventory(embedded_inventory, source_inventory, "embedded authority exact copy")
    common.assert_equal(receipt["authority"]["copiedExactly"], True, "desktop authority copy claim")
    common.assert_equal((receipt["authority"]["releaseFiles"], receipt["authority"]["releaseBytes"], receipt["authority"]["releaseTreeSha256"]), EXPECTED_AUTHORITY, "desktop authority summary")

    metadata_root = CANDIDATE_ROOT / "authority" / "metadata"
    for relative, digest in receipt["authority"]["metadataSha256"].items():
        common.assert_equal(common.sha256_file(metadata_root / relative), digest, f"embedded authority metadata {relative}")

    worker_records: list[dict[str, Any]] = []
    for worker in checkpoint["workers"]:
        entrypoint = AUTHORITY_ROOT / worker["entrypoint"]
        common.assert_equal(common.windows_path(entrypoint).stat().st_size, int(worker["bytes"]), f"{worker['workerId']} bytes")
        common.assert_equal(common.sha256_file(entrypoint), worker["sha256"], f"{worker['workerId']} entrypoint")
        describe = project_path(worker["describeSnapshot"]["path"])
        common.assert_equal(common.sha256_file(describe), worker["describeSnapshot"]["sha256"], f"{worker['workerId']} describe")
        worker_records.append({"workerId": worker["workerId"], "workerVersion": worker["workerVersion"], "entrypoint": evidence(entrypoint), "describe": evidence(describe)})

    runtime_path = verify_ref(receipt["authority"]["runtimeGate"], "authority runtime gate")
    runtime = common.load_json(runtime_path)
    common.assert_equal((runtime["status"], runtime["registryVersion"]), ("passed", "1.2.4"), "runtime gate status")
    common.assert_equal(runtime["protocol11"]["stageControl"]["status"], "passed", "authority IPC 1.1")
    common.assert_equal((runtime["workerRunInvocations"], runtime["physicalSpeakerInvocations"], runtime["externalWrites"]), (0, 0, 0), "authority runtime side effects")
    return {
        "status": "PASS-AUTHORITY-ADMITTED",
        "checkpoint": evidence(AUTHORITY_CHECKPOINT),
        "registry": evidence(registry_path),
        "dependencyManifest": evidence(dependency_path),
        "authorityReceipt": evidence(AUTHORITY_RECEIPT),
        "release": source_inventory,
        "embeddedRelease": embedded_inventory,
        "runtimeGate": evidence(runtime_path),
        "workers": worker_records,
        "frozenV1": checkpoint["frozenV1"],
    }


def verify_stage_control(receipt: dict[str, Any]) -> dict[str, Any]:
    playback = receipt["playbackStop"]
    common.assert_equal((playback["transport"], playback["protocolVersion"], playback["messageType"]), ("direct-named-pipe", "1.1", "stage-control"), "direct IPC request")
    common.assert_equal(playback["requestPayload"], {"operation": "speech.playback", "selector": "current-active", "reason": "user_stop"}, "stage control input")
    common.assert_equal((playback["receiptMessageType"], playback["receiptSchemaVersion"]), ("stage-control-receipt", "2.3.0"), "stage control output contract")
    common.assert_equal(playback["acceptedProjection"], {"state": "stop-requested", "noticeCode": "playback.stop-requested", "claimsStopped": False}, "stage control projection")
    common.assert_equal((playback["taskCancelMessagesObserved"], playback["taskStageControlIntermediateRoutes"], playback["wholeRunCancelRequestedWritten"]), (0, 0, False), "stage isolation")

    gate = common.load_json(DIRECT_GATE)
    common.assert_equal(gate["status"], "PASS", "direct IPC gate")
    common.assert_equal((gate["protocol10"]["taskQuery"], gate["protocol10"]["taskCommand"]), ("PASS", "PASS"), "IPC 1.0 compatibility")
    common.assert_equal((gate["protocol11"]["messageType"], gate["protocol11"]["accepted"], gate["protocol11"]["stateClaim"]), ("stage-control-receipt", True, "stop-requested"), "IPC 1.1 response")
    common.assert_equal((gate["durableEffects"]["runCancelRequestedWritten"], gate["durableEffects"]["workerResultWritten"]), (False, False), "stage durable isolation")
    common.assert_equal((gate["workerRunInvocations"], gate["physicalSpeakerInvocations"], gate["externalWrites"]), (0, 0, 0), "stage gate side effects")

    direct_root = DIRECT_GATE_ROOT / "playback-control-direct-ipc"
    response_path = direct_root / gate["stageControlResponse"]["path"]
    common.assert_equal(common.sha256_file(response_path), gate["stageControlResponse"]["sha256"], "stage response")
    response = common.load_json(response_path)
    common.assert_equal((response["messageType"], response["payload"]["accepted"]), ("stage-control-receipt", True), "stage response payload")

    durable: dict[str, Any] = {}
    for name in ("attemptCancelSignal", "stageControlEvent", "stageControlReceipt"):
        path = direct_root / gate["durableEffects"][name]
        durable[name] = evidence(path)
    run_root = direct_root / "jobs" / "task-authority-stage-control-probe" / "runs" / "run-probe"
    if (run_root / "cancel-requested.json").exists():
        raise GateFailure("whole-run cancel marker unexpectedly exists")
    if list(run_root.rglob("result.json")):
        raise GateFailure("worker result unexpectedly exists in direct stage-control fixture")
    return {
        "status": "PASS-DIRECT-IPC-1.1-STAGE-ISOLATED",
        "gate": evidence(DIRECT_GATE),
        "request": playback["requestPayload"],
        "response": playback["acceptedProjection"],
        "responseEvidence": evidence(response_path),
        "durableEffects": durable,
        "taskCancelMessagesObserved": 0,
        "wholeRunCancelRequestedWritten": False,
        "workerRunInvocations": 0,
        "physicalSpeakerInvocations": 0,
        "externalWrites": 0,
    }


def verify_remnote_boundary() -> dict[str, Any]:
    common.assert_equal(common.sha256_file(REMNOTE_RECEIPT), REMNOTE_RECEIPT_SHA256, "RemNote blocker receipt")
    result = v123_gate.verify_remnote_blocker()
    common.assert_equal(result["status"], "blocked_external_bridge", "RemNote boundary")
    common.assert_equal(result["qualityAction"], "READ-ONLY-NO-RETRY", "RemNote no retry")
    return result


def verify_all() -> dict[str, Any]:
    desktop = verify_candidate_and_desktop()
    authority = verify_authority(desktop["receipt"])
    stage = verify_stage_control(desktop["receipt"])
    remnote = verify_remnote_boundary()
    after = common.inventory(CANDIDATE_ROOT)
    common.assert_equal(after["treeSha256"], desktop["inventory"]["treeSha256"], "candidate changed during gate")
    return {"desktop": desktop, "authority": authority, "stageControl": stage, "remnote": remnote, "candidateInventory": after}


def manifest_document(result: dict[str, Any]) -> dict[str, Any]:
    desktop = result["desktop"]
    refs = [
        evidence(DESKTOP_RECEIPT),
        desktop["candidateFileManifest"],
        desktop["buildReceipt"],
        evidence(project_path(desktop["receipt"]["desktop"]["moduleReceipt"]["path"])),
        evidence(project_path(desktop["receipt"]["desktop"]["priorWindowsUiReceipt"]["path"])),
        result["authority"]["checkpoint"],
        result["authority"]["registry"],
        result["authority"]["dependencyManifest"],
        result["authority"]["authorityReceipt"],
        result["authority"]["runtimeGate"],
        result["stageControl"]["gate"],
        result["stageControl"]["responseEvidence"],
        result["remnote"]["receipt"],
    ] + list(desktop["canaries"].values()) + list(result["stageControl"]["durableEffects"].values())
    unique = {item["path"]: item for item in refs}
    return {
        "schemaVersion": "knowledge-clipper-detached-release-manifest.v1",
        "manifestRole": "append-only-authoritative-candidate-identity",
        "product": {"displayName": "零碎知识剪藏", "asciiIdentity": "KnowledgeClipper", "entrypoint": "KnowledgeClipper.exe"},
        "candidate": {
            "candidateId": CANDIDATE_ID,
            "root": common.project_relative(CANDIDATE_ROOT),
            "fileCount": result["candidateInventory"]["fileCount"],
            "totalBytes": result["candidateInventory"]["totalBytes"],
            "treeSha256": result["candidateInventory"]["treeSha256"],
            "files": result["candidateInventory"]["files"],
            "sourceManifest": desktop["candidateFileManifest"],
        },
        "authority": {
            "status": result["authority"]["status"],
            "checkpoint": result["authority"]["checkpoint"],
            "registry": result["authority"]["registry"],
            "release": {key: result["authority"]["release"][key] for key in ("fileCount", "totalBytes", "treeSha256")},
            "workers": result["authority"]["workers"],
            "frozenV1": result["authority"]["frozenV1"],
        },
        "qualification": {
            "desktop": "PASS-INTEGRATED-V1.2.4-PLAYBACK-CONTROL",
            "stageControl": result["stageControl"]["status"],
            "remnote": "blocked_external_bridge",
            "releaseDecision": "NO-GO",
        },
        "evidence": [unique[key] for key in sorted(unique)],
        "boundaries": [
            "export.remnote remains blocked_external_bridge; no retry was performed",
            "desktop-to-real-export E2E is deferred",
            "physical speaker was not invoked",
            "real external-app selection and clipboard remain user/environment checks",
            "physical screenshot drag and cross-monitor mixed-DPI remain user/environment checks",
            "installer and code signing are absent",
        ],
    }


def readable_index(result: dict[str, Any], candidate_hash: str, manifest_path: Path) -> str:
    stage = result["stageControl"]
    old = result["desktop"]["oldProcess"]
    return f"""# 零碎知识剪藏 integrated v1.2.4：输入 → 输出与 NO-GO

## 候选身份

- 候选根：`{common.project_relative(CANDIDATE_ROOT)}`
- 文件树：347 files / 400,692,507 bytes / `7A1BFFA0EE583D5AD47BD5755C3E43180E2D8C01AD4310497329589851F603A9`
- authority v1.2.4：91 files / 249,015,447 bytes / `6CA2D59E66DF9F15CC1BE46890ABDA04F5FAFB98507716D91E568818A37882CE`
- detached release manifest：`{common.project_relative(manifest_path)}`
- candidateHash：`{candidate_hash}`
- 候选目录未写入或覆盖任何文件；candidateHash 是上述只追加 manifest 的 SHA-256。

## Playback stop：真实输入 → 可观察输出

- 输入：Named Pipe IPC `1.1`，`messageType=stage-control`，`operation=speech.playback`，`selector=current-active`，`reason=user_stop`。
- 输出：`stage-control-receipt` / schema `2.3.0`，`accepted=true`，`state=stop-requested`，`noticeCode=playback.stop-requested`。
- 语义边界：`claimsStopped=false`；只写 attempt cancel signal、stage-control event 与 control receipt。
- 隔离断言：`taskCancelMessagesObserved={stage['taskCancelMessagesObserved']}`，`wholeRunCancelRequestedWritten={str(stage['wholeRunCancelRequestedWritten']).lower()}`，worker result 未写入，worker/speaker/external writes 均为 0。
- 单实例：既有进程 PID `{old['processId']}` 在 Canary 中被保留，反馈方式 `{old['feedbackMode']}`；质量门禁没有关闭或重启它。

## RemNote 当前边界

- 状态：`blocked_external_bridge`；沿用只读历史回执，不重试外部写入，不提升为 PASS。
- 已报告 outputs=0、sideEffects=0、readbackVerified=false、duplicate replay=`NOT_RUN`。
- 这不阻止本轮桌面与 stage-control 候选级验收，但阻止把整体称为真实 RemNote E2E。

## 结论

- integrated v1.2.4 桌面、authority 精确拷贝、startup、单实例、内置 Orchestrator/Named Pipe、IPC 1.0 兼容与 IPC 1.1 stage-control：PASS。
- 整体发布：NO-GO。剩余边界为 RemNote Bridge、desktop→real-export E2E、物理扬声器、真实外部应用选择/剪贴板、物理截图拖拽与跨屏 mixed-DPI、installer/signing。
- 本质量轮外部 Worker、RemNote、Anki、MinerU、生产 API 和扬声器调用数均为 0。
"""


def emit(output_root: Path) -> dict[str, Any]:
    if output_root.exists():
        raise GateFailure(f"append-only output root already exists: {output_root}")
    result = verify_all()
    output_root.mkdir(parents=True)
    manifest_path = output_root / "release-manifest.v1.json"
    manifest_path.write_text(json.dumps(manifest_document(result), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    candidate_hash = common.sha256_file(manifest_path)
    index_path = output_root / "input-output-index.md"
    index_path.write_text(readable_index(result, candidate_hash, manifest_path), encoding="utf-8")
    receipt = {
        "schemaVersion": "knowledge-clipper-integrated-v124-quality.v1",
        "generatedAt": common.utc_now(),
        "status": "NO-GO-EXTERNAL-AND-PHYSICAL-RELEASE-BOUNDARIES",
        "candidateScopedQualification": "PASS-INTEGRATED-V1.2.4-PLAYBACK-CONTROL",
        "candidateIdentity": {
            "mode": "detached-append-only-release-manifest",
            "manifest": evidence(manifest_path),
            "candidateHash": candidate_hash,
            "candidateRootUnchanged": True,
            "boundTreeSha256": result["candidateInventory"]["treeSha256"],
        },
        "authority": result["authority"],
        "gates": {"desktop": {"status": result["desktop"]["receipt"]["status"], "receipt": evidence(DESKTOP_RECEIPT)}, "stageControl": result["stageControl"], "remnote": result["remnote"]},
        "readableIndex": evidence(index_path),
        "qualityExecution": {
            "mode": "read-only-existing-evidence-plus-detached-identity",
            "externalWorkersStarted": 0,
            "physicalSpeakerInvocations": 0,
            "remNoteInvocations": 0,
            "ankiInvocations": 0,
            "mineruInvocations": 0,
            "productionApiCalls": 0,
            "candidateFilesModified": 0,
            "oldProcessTerminated": False,
        },
        "remainingNoGo": manifest_document(result)["boundaries"],
    }
    receipt_path = output_root / "quality-receipt.json"
    receipt_path.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    after = common.inventory(CANDIDATE_ROOT)
    common.assert_equal(after["treeSha256"], result["candidateInventory"]["treeSha256"], "candidate changed during emit")
    return {
        "status": receipt["status"],
        "candidateHash": candidate_hash,
        "manifest": evidence(manifest_path),
        "receipt": evidence(receipt_path),
        "index": evidence(index_path),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("verify")
    emit_parser = commands.add_parser("emit")
    emit_parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == "verify":
            result = verify_all()
            output = {"status": "verified", "candidate": EXPECTED_CANDIDATE, "authority": EXPECTED_AUTHORITY, "stageControl": result["stageControl"]["status"], "remnote": result["remnote"]["status"], "externalCallsPerformed": 0}
        else:
            root = args.output_root if args.output_root.is_absolute() else PROJECT_ROOT / args.output_root
            output = emit(root)
        print(json.dumps(output, ensure_ascii=False, indent=2))
        return 0
    except (GateFailure, OSError, ValueError, KeyError, TypeError) as exc:
        print(json.dumps({"status": "failed", "error": str(exc)}, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
