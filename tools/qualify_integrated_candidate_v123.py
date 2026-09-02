from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

import verify_integrated_candidate_quality as base_gate


PROJECT_ROOT = base_gate.PROJECT_ROOT
CANDIDATE_ROOT = base_gate.CANDIDATE_V123_ROOT
CANDIDATE_ID = base_gate.CANDIDATE_V123_ID
CONTENT_ROOT = PROJECT_ROOT / "canary" / "authority-content-v123-candidate" / "20260828T024813481Z"
CONTENT_RECEIPT = CONTENT_ROOT / "authority-content-v123-receipt.json"
CONTENT_RECEIPT_SHA256 = "406BA9249902E6E695489F4CDD12EE1A6A413A38F9382FF5D6680D904C7DB6B8"
CONTENT_VERIFICATION = CONTENT_ROOT / "verification-receipt.json"
CONTENT_VERIFICATION_SHA256 = "95D905BC4E7894935AFA894720ACAC91853D5C03CD3DFFF0064E27CEA7DC7D57"
SPEECH_ROOT = PROJECT_ROOT / "tests" / "RemNoteQuickClipper.Speech.Tests" / "candidate-bound-v123" / "r20260828-024551-7a05a5"
SPEECH_BACKEND = SPEECH_ROOT / "speech-v123-candidate-bound-backend-receipt.json"
SPEECH_BACKEND_SHA256 = "147FB07FC0A3BE432D3870145897EC83096C5D133BBFA1CBAE0C2E39A67F5BE3"
SPEECH_VALIDATION = SPEECH_ROOT / "speech-v123-candidate-bound-validation-receipt.json"
SPEECH_VALIDATION_SHA256 = "BFE6C57CE0EFCB03F2364A51C4063B8BC5A11A02F90B4DD3FF3DC88ECA44DC30"
SPEECH_CONTRACT = SPEECH_ROOT / "speech-v123-candidate-bound-contract-validation-receipt.json"
SPEECH_CONTRACT_SHA256 = "8789D99575C1F26E4C4BA402F7424C251765FD02F804377549BE6F6E9BAEA2DD"
REMNOTE_RECEIPT = PROJECT_ROOT / "receipts" / "remnote-exporter-authority-v1.2.3-candidate-bound-partial-20260828T024628Z.json"
REMNOTE_RECEIPT_SHA256 = "4B6D4649F25DCE31078C0900E295A93F516A3E23A48DC93C50CF6BF943AC2479"
BASE_QUALITY_RECEIPT = PROJECT_ROOT / "canary" / "integrated-candidate-quality" / "integrated-v123-20260828T023641Z" / "quality-receipt.json"
BASE_QUALITY_RECEIPT_SHA256 = "F306CE20069D040C403280381F4885F166DD94210CE9F6F6B595F9E4A9234F07"


GateFailure = base_gate.GateFailure


def _path(item: dict[str, Any]) -> Path:
    value = item.get("absolutePath") or item.get("path")
    if not isinstance(value, str) or not value:
        raise GateFailure("evidence reference is missing path")
    candidate = Path(value)
    return candidate if candidate.is_absolute() else PROJECT_ROOT / candidate


def verify_item(item: dict[str, Any], label: str) -> Path:
    path = _path(item)
    actual = base_gate.sha256_file(path)
    base_gate.assert_equal(actual, str(item["sha256"]).upper(), f"{label}.sha256")
    if "bytes" in item:
        base_gate.assert_equal(base_gate.windows_path(path).stat().st_size, int(item["bytes"]), f"{label}.bytes")
    return path


def evidence_record(path: Path) -> dict[str, Any]:
    try:
        display = base_gate.project_relative(path)
    except ValueError:
        display = str(path.resolve())
    return {"path": display, "bytes": base_gate.windows_path(path).stat().st_size, "sha256": base_gate.sha256_file(path)}


def verify_content() -> dict[str, Any]:
    base_gate.assert_equal(base_gate.sha256_file(CONTENT_RECEIPT), CONTENT_RECEIPT_SHA256, "content receipt")
    base_gate.assert_equal(base_gate.sha256_file(CONTENT_VERIFICATION), CONTENT_VERIFICATION_SHA256, "content verification")
    receipt = base_gate.load_json(CONTENT_RECEIPT)
    verification = base_gate.load_json(CONTENT_VERIFICATION)
    base_gate.assert_equal(receipt["status"], "passed", "content status")
    authority = receipt["authority"]
    base_gate.assert_equal(authority["checkpoint"]["sha256"].upper(), base_gate.AUTHORITY_V123_CHECKPOINT_SHA256, "content checkpoint")
    base_gate.assert_equal(authority["registry"]["sha256"].upper(), base_gate.AUTHORITY_V123_REGISTRY_SHA256, "content registry")
    base_gate.assert_equal(
        (authority["worker"]["workerId"], authority["worker"]["workerVersion"], authority["worker"]["executable"]["sha256"].upper()),
        ("content.deepseek", "0.5.2", "F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8"),
        "content worker",
    )
    for key in ("checkpoint", "registry"):
        verify_item(authority[key], f"content {key}")
    verify_item(authority["worker"]["executable"], "content executable")

    question = receipt["questionWorkflow"]
    base_gate.assert_equal(question["states"], ["waiting_input", "waiting_input", "succeeded"], "question states")
    base_gate.assert_equal((question["orderedTurns"], question["firstAnswerPreserved"], question["targetTokenRotated"]), (2, True, True), "question ancestry")
    base_gate.assert_equal([item["operation"] for item in question["attempts"]], ["question.answer", "question.answer", "question.make-cards"], "question operations")
    verify_item(question["receipt"], "question receipt")
    live_calls = 0
    for index, attempt in enumerate(question["attempts"]):
        verify_item(attempt["request"], f"question attempt {index} request")
        verify_item(attempt["result"], f"question attempt {index} result")
        traces = attempt["providerTrace"]
        base_gate.assert_equal(all(item["mode"] == "live" and item["provider"] == "deepseek" for item in traces), True, f"question attempt {index} provider")
        live_calls += sum(int(item["attempts"]) for item in traces)
    base_gate.assert_equal((live_calls, len(question["finalCards"]), question["exporterResults"]), (3, 1, 0), "question output")

    modes = receipt["languagePractice"]
    base_gate.assert_equal([item["mode"] for item in modes], ["meaning-reading", "writing", "both"], "language modes")
    base_gate.assert_equal([len(item["cards"]) for item in modes], [1, 1, 4], "language card counts")
    for mode in modes:
        verify_item(mode["receipt"], f"language {mode['mode']} receipt")
        verify_item(mode["request"], f"language {mode['mode']} request")
        verify_item(mode["result"], f"language {mode['mode']} result")
        base_gate.assert_equal(all(trace["mode"] == "replay" for trace in mode["providerEvidence"]), True, f"language {mode['mode']} provider")
        templates = [card["template"] for card in mode["cards"]]
        if mode["mode"] == "meaning-reading":
            base_gate.assert_equal(templates, ["language_context"], "meaning-reading templates")
        elif mode["mode"] == "writing":
            base_gate.assert_equal(templates, ["language_writing"], "writing templates")
        else:
            base_gate.assert_equal(templates, ["language_context", "language_writing", "language_context", "language_writing"], "both templates")
        for card in mode["cards"]:
            if card["template"] == "language_writing":
                if card["back"] in card["front"]:
                    raise GateFailure(f"writing front leaks target term: {card['back']}")
                if "（" not in card["front"] or "；" not in card["front"] or "）" not in card["front"]:
                    raise GateFailure("writing front lacks full-width replacement")
            if any(key in card for key in ("choice", "decisionKind", "targetToken", "binding")):
                raise GateFailure("decision metadata leaked into content card")

    verify_item(receipt["readable"], "content readable")
    base_gate.assert_equal((verification["status"], verification["tests"]["contentWorker"]["tests"], verification["tests"]["contentWorker"]["pass"]), ("passed", 59, 59), "content verification tests")
    verify_item(verification["candidate"]["receipt"], "content verification receipt binding")
    verify_item(verification["candidate"]["readable"], "content verification readable binding")
    for key in ("contentWorker", "coreQuestionContract"):
        verify_item(verification["tests"][key]["log"], f"content verification {key}")
    verify_item(verification["tests"]["descriptor"]["artifact"], "content descriptor")
    base_gate.assert_equal(verification["verifiedClaims"]["exporterCalls"], 0, "content exporter calls")
    return {
        "status": "PASS-EXACT-AUTHORITY",
        "receipt": evidence_record(CONTENT_RECEIPT),
        "verification": evidence_record(CONTENT_VERIFICATION),
        "readable": evidence_record(_path(receipt["readable"])),
        "worker": {"id": "content.deepseek", "version": "0.5.2", "sha256": authority["worker"]["executable"]["sha256"].upper()},
        "question": {"provider": "deepseek", "historicalRealCalls": live_calls, "states": question["states"], "finalCards": question["finalCards"]},
        "languagePractice": [{"mode": item["mode"], "sourceTexts": item["sourceTexts"], "cards": item["cards"]} for item in modes],
        "tests": {"passed": 59, "failed": 0},
        "exporterCalls": 0,
        "externalCallsPerformedByThisQualityGate": 0,
    }


def verify_speech() -> dict[str, Any]:
    expected = ((SPEECH_BACKEND, SPEECH_BACKEND_SHA256), (SPEECH_VALIDATION, SPEECH_VALIDATION_SHA256), (SPEECH_CONTRACT, SPEECH_CONTRACT_SHA256))
    for path, digest in expected:
        base_gate.assert_equal(base_gate.sha256_file(path), digest, f"speech {path.name}")
    backend = base_gate.load_json(SPEECH_BACKEND)
    validation = base_gate.load_json(SPEECH_VALIDATION)
    contract = base_gate.load_json(SPEECH_CONTRACT)
    base_gate.assert_equal((backend["status"], validation["status"], contract["status"]), ("passed", "passed", "passed"), "speech statuses")
    base_gate.assert_equal(backend["authority"]["checkpoint"]["sha256"], base_gate.AUTHORITY_V123_CHECKPOINT_SHA256, "speech checkpoint")
    base_gate.assert_equal(backend["authority"]["release"]["treeSha256"], base_gate.AUTHORITY_V123_TREE_SHA256, "speech authority tree")
    base_gate.assert_equal((backend["worker"]["workerId"], backend["worker"]["workerVersion"], backend["worker"]["sha256"]), ("speech.edge", "1.2.0", "51FF5937C297DAD562AB781D6114374649E1A965EDBB43D575264BEED3DFB19C"), "speech worker")
    for item, label in ((backend["authority"]["checkpoint"], "speech checkpoint"), (backend["worker"], "speech worker"), (backend["runtime"]["edgeTts"], "edge-tts runtime"), (backend["runtime"]["fakePlayer"], "speech fake player")):
        verify_item(item, label)
    for key, item in backend["evidence"].items():
        verify_item(item, f"speech evidence {key}")
    base_gate.assert_equal((validation["realEdgeSynthesisCalls"], validation["fiveLanguageAuto"], validation["fiveLanguageOverride"], validation["playbackAttempts"]), (14, 5, 5, 6), "speech observed counts")
    base_gate.assert_equal((backend["assertions"]["physicalSpeakerPlayback"], backend["assertions"]["externalProductionWrites"], backend["assertions"]["playbackResultReuse"]), (False, 0, False), "speech side effects")
    base_gate.assert_equal((contract["totalRequests"], contract["totalResults"], contract["totalArtifacts"], contract["physicalSpeakerPlayback"]), (20, 20, 18, False), "speech contract totals")
    return {
        "status": "PASS-EXACT-AUTHORITY-NO-SPEAKER",
        "backendReceipt": evidence_record(SPEECH_BACKEND),
        "validationReceipt": evidence_record(SPEECH_VALIDATION),
        "contractReceipt": evidence_record(SPEECH_CONTRACT),
        "readable": evidence_record(_path(backend["evidence"]["readable"])),
        "worker": {"id": "speech.edge", "version": "1.2.0", "sha256": backend["worker"]["sha256"]},
        "observed": {"languages": backend["assertions"]["languages"], "realEdgeSynthesisCalls": 14, "auto": 5, "override": 5, "playbackAttempts": 6, "contractArtifacts": 18},
        "physicalSpeakerPlayback": False,
        "externalProductionWrites": 0,
        "externalCallsPerformedByThisQualityGate": 0,
    }


def verify_remnote_blocker() -> dict[str, Any]:
    base_gate.assert_equal(base_gate.sha256_file(REMNOTE_RECEIPT), REMNOTE_RECEIPT_SHA256, "RemNote partial receipt")
    receipt = base_gate.load_json(REMNOTE_RECEIPT)
    base_gate.assert_equal(receipt["status"], "blocked_external_bridge", "RemNote current status")
    binding = receipt["candidateBinding"]
    base_gate.assert_equal(binding["checkpoint"]["sha256"], base_gate.AUTHORITY_V123_CHECKPOINT_SHA256, "RemNote checkpoint")
    base_gate.assert_equal(binding["registry"]["sha256"], base_gate.AUTHORITY_V123_REGISTRY_SHA256, "RemNote registry")
    base_gate.assert_equal(binding["release"]["treeSha256"], base_gate.AUTHORITY_V123_TREE_SHA256, "RemNote authority tree")
    for key in ("checkpoint", "registry", "dependencyManifest", "authorityReceipt", "orchestrator", "exporter"):
        verify_item(binding[key], f"RemNote {key}")
    execution = receipt["execution"]
    plan_path = _path(execution["plan"])
    plan_actual_sha256 = base_gate.sha256_file(plan_path)
    plan_expected_sha256 = execution["plan"]["sha256"].upper()
    plan_evidence_integrity = {
        "status": "FAILED-HASH-DRIFT" if plan_actual_sha256 != plan_expected_sha256 else "PASS",
        "path": base_gate.project_relative(plan_path),
        "expectedSha256": plan_expected_sha256,
        "actualSha256": plan_actual_sha256,
        "bytes": base_gate.windows_path(plan_path).stat().st_size,
    }
    content = receipt["content"]
    base_gate.assert_equal(content["status"], "completed", "RemNote content status")
    for key in ("learningBundle", "cardBundle"):
        verify_item(content[key], f"RemNote {key}")
    verify_item(content["remNoteProjection"], "RemNote projection")
    attempt = receipt["remNoteAttempt"]
    verify_item({"path": attempt["requestPath"], "sha256": attempt["requestSha256"]}, "RemNote request")
    verify_item({"path": attempt["resultPath"], "sha256": attempt["resultSha256"]}, "RemNote result")
    base_gate.assert_equal((attempt["status"], attempt["sideEffects"], attempt["outputs"], attempt["readbackVerified"], attempt["documentRemId"]), ("failed", [], [], False, None), "RemNote blocked attempt")
    root = receipt["rootScope"]
    base_gate.assert_equal((root["formalClippingDocumentTargeted"], root["deletionsOrCleanupPerformed"], root["currentAttemptReportedSideEffects"], root["currentIndependentRootReadback"]), (False, False, 0, "blocked_request_timeout"), "RemNote root boundary")
    verify_item(root["lastVerifiedBaselineReceipt"], "RemNote historical baseline")
    acceptance = receipt["acceptance"]
    base_gate.assert_equal((acceptance["remNoteMutation"], acceptance["remNoteReadback"], acceptance["duplicateReplay"], acceptance["overall"]), ("NOT_PROVEN_NO_SIDE_EFFECT_REPORTED", "BLOCKED", "NOT_RUN", "NO_GO_UNTIL_BRIDGE_READ_RECOVERS"), "RemNote acceptance")
    return {
        "status": "blocked_external_bridge",
        "receipt": evidence_record(REMNOTE_RECEIPT),
        "worker": {"id": "export.remnote", "version": "1.0.1", "sha256": binding["exporter"]["sha256"]},
        "input": execution["originalInput"],
        "projectedCard": content["cardBundle"]["cards"],
        "attempt": {"status": "failed", "error": attempt["error"], "outputs": 0, "reportedSideEffects": 0, "readbackVerified": False, "duplicateReplay": "NOT_RUN"},
        "evidenceIntegrity": {"plan": plan_evidence_integrity, "otherReferencedArtifacts": "PASS"},
        "scope": {"isolatedRoot": root["authorizedRootTitle"], "formalClippingDocumentTargeted": False, "deletions": 0},
        "qualityAction": "READ-ONLY-NO-RETRY",
        "externalCallsPerformedByThisQualityGate": 0,
    }


def verify_all() -> dict[str, Any]:
    base_gate.assert_equal(base_gate.sha256_file(BASE_QUALITY_RECEIPT), BASE_QUALITY_RECEIPT_SHA256, "base integrated quality receipt")
    base = base_gate.verify_all()
    content = verify_content()
    speech = verify_speech()
    remnote = verify_remnote_blocker()
    inventory_before = base_gate.inventory(CANDIDATE_ROOT)
    base_gate.assert_equal((inventory_before["fileCount"], inventory_before["totalBytes"], inventory_before["treeSha256"]), (331, 400642976, "B5437F88D6A6B77BD194ED85A7ECEE57AA88ACAA7A17F75123CD753BDDFD21F7"), "supplement candidate tree")
    inventory_after = base_gate.inventory(CANDIDATE_ROOT)
    base_gate.assert_equal(inventory_after["treeSha256"], inventory_before["treeSha256"], "candidate changed during supplement gate")
    return {
        "base": base,
        "content": content,
        "speech": speech,
        "remnote": remnote,
        "candidateInventory": inventory_before,
    }


def manifest_document(verified: dict[str, Any]) -> dict[str, Any]:
    base = verified["base"]
    evidence = [
        evidence_record(BASE_QUALITY_RECEIPT),
        evidence_record(CONTENT_RECEIPT),
        evidence_record(CONTENT_VERIFICATION),
        evidence_record(SPEECH_BACKEND),
        evidence_record(SPEECH_VALIDATION),
        evidence_record(SPEECH_CONTRACT),
        evidence_record(REMNOTE_RECEIPT),
    ]
    return {
        "schemaVersion": "knowledge-clipper-detached-release-manifest.v1",
        "manifestRole": "append-only-authoritative-candidate-identity",
        "product": {"displayName": "零碎知识剪藏", "asciiIdentity": "KnowledgeClipper", "entrypoint": "KnowledgeClipper.exe"},
        "candidate": {
            "candidateId": CANDIDATE_ID,
            "root": base_gate.project_relative(CANDIDATE_ROOT),
            "files": verified["candidateInventory"]["fileCount"],
            "bytes": verified["candidateInventory"]["totalBytes"],
            "treeSha256": verified["candidateInventory"]["treeSha256"],
            "candidateFileManifest": base["gates"]["desktop"]["candidate"]["manifest"],
            "entrypointSha256": base["gates"]["desktop"]["desktopPayload"]["exeSha256"],
        },
        "authority": {
            "checkpoint": base["authority"]["checkpoint"],
            "registry": base["authority"]["registry"],
            "release": base["authority"]["release"],
        },
        "qualification": {
            "desktop": base["gates"]["desktop"]["status"],
            "ocrToDeepSeek": base["gates"]["ocrToDeepSeek"]["forwardBindingToV123"]["status"],
            "anki": base["gates"]["anki"]["status"],
            "content": verified["content"]["status"],
            "speech": verified["speech"]["status"],
            "remnote": verified["remnote"]["status"],
            "releaseDecision": "NO-GO",
        },
        "evidence": sorted(evidence, key=lambda item: item["path"]),
        "candidateFiles": verified["candidateInventory"]["files"],
        "boundaries": [
            "export.remnote candidate-bound attempt is blocked_external_bridge; mutation/readback/duplicate replay are not proven",
            "the RemNote receipt's resolved plan reference has SHA-256 drift and is not fully reproducible",
            "desktop-to-real-export E2E remains deferred",
            "physical speaker playback and public Core playback.stop end-to-end remain deferred",
            "formal clipping RemNote and daily Anki targets were not touched",
            "installer and code signing are absent",
            "physical external-app selection, screenshot drag, and mixed-DPI checks remain user/environment boundaries",
        ],
    }


def readable_index(verified: dict[str, Any], candidate_hash: str, manifest_relative: str) -> str:
    content = verified["content"]
    speech = verified["speech"]
    remnote = verified["remnote"]
    question_card = content["question"]["finalCards"][0]
    language_lines: list[str] = []
    for mode in content["languagePractice"]:
        language_lines.append(f"- `{mode['mode']}` 输入：{' | '.join(text.rstrip() for text in mode['sourceTexts'])}")
        for card in mode["cards"]:
            language_lines.append(f"  - `{card['template']}`：{card['front']} → {card['back']}")
    language_text = "\n".join(language_lines)
    return f"""# 零碎知识剪藏 integrated v1.2.3 质量补充：输入 → 输出

## 候选身份

- 候选：`{base_gate.project_relative(CANDIDATE_ROOT)}`
- 固定文件树：331 files / 400,642,976 bytes / `B5437F88D6A6B77BD194ED85A7ECEE57AA88ACAA7A17F75123CD753BDDFD21F7`
- 分离式 release manifest：`{manifest_relative}`
- candidateHash（manifest SHA-256）：`{candidate_hash}`
- 该 manifest 位于新的只追加质量根中，没有向既有候选目录写入文件，也没有使既有桌面回执失效。

## Content：真实问答与三种语言卡

- 问答状态：`waiting_input → waiting_input → succeeded`；真实 DeepSeek 调用 3 次；本轮质量核验调用 0 次。
- 最终问答卡：{question_card['front']} → {question_card['back']}
{language_text}
- exporter 调用 0；Content 自测 59/59 PASS。
- 可读证据：`{content['readable']['path']}`。

## Speech：候选绑定合成与播放语义

- 输入覆盖：`zh-CN / en-US / es-ES / ja-JP / ko-KR`，auto 5 例、override 5 例。
- 输出：历史证据内真实 Edge 合成 14 次；20 requests / 20 results / 18 artifacts；6 个播放 attempt。
- 物理扬声器未调用；playback 每次新 attempt、结果不复用。Core 公共 `playback.stop` 完整链不属于本轮等待条件，继续延期。
- 可读证据：`{speech['readable']['path']}`。

## RemNote：当前真实阻断

- 原始输入：{remnote['input']}
- 投影卡：{remnote['projectedCard'][0]['front']} → {remnote['projectedCard'][0]['back']}
- 当前结果：`blocked_external_bridge`。export attempt failed，outputs=0，已报告 sideEffects=0，readbackVerified=false，duplicate replay=`NOT_RUN`。
- 证据完整性：resolved plan 回执记录 `{remnote['evidenceIntegrity']['plan']['expectedSha256']}`，当前实读 `{remnote['evidenceIntegrity']['plan']['actualSha256']}`；该漂移作为附加 NO-GO 保留，其他引用工件哈希通过。
- 仅隔离根“零碎知识剪藏 Canary”；未定位正式剪藏文档、未删除或清理。本质量轮只读吸收回执，没有重试外部写入。
- 回执：`{remnote['receipt']['path']}`。

## 结论与 NO-GO

- Desktop、OCR→DeepSeek、Anki、Content、Speech：候选范围 PASS。
- RemNote：`blocked_external_bridge`，不得宣称 PASS。
- candidateHash 已建立，但整体仍 NO-GO：RemNote mutation/readback/duplicate replay 未证明；desktop→real-export E2E 延后；installer/signing、物理扬声器、Core playback.stop 完整链、外部应用选择/截图拖拽/混合 DPI 均未完成。
"""


def emit(output_root: Path) -> dict[str, Any]:
    if output_root.exists():
        raise GateFailure(f"append-only output root already exists: {output_root}")
    verified = verify_all()
    output_root.mkdir(parents=True)
    manifest_path = output_root / "release-manifest.v1.json"
    manifest_path.write_text(json.dumps(manifest_document(verified), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    candidate_hash = base_gate.sha256_file(manifest_path)
    index_path = output_root / "input-output-index.md"
    index_path.write_text(readable_index(verified, candidate_hash, base_gate.project_relative(manifest_path)), encoding="utf-8")
    receipt = {
        "schemaVersion": "knowledge-clipper-integrated-v123-quality-supplement.v1",
        "generatedAt": base_gate.utc_now(),
        "status": "NO-GO-REMNOTE-BRIDGE-AND-RELEASE-BOUNDARIES",
        "candidateScopedQualification": "PASS-WITH-REMNOTE-BLOCKED",
        "candidateIdentity": {
            "mode": "detached-append-only-release-manifest",
            "manifest": evidence_record(manifest_path),
            "candidateHash": candidate_hash,
            "candidateRootUnchanged": True,
            "boundTreeSha256": verified["candidateInventory"]["treeSha256"],
        },
        "authority": verified["base"]["authority"],
        "gates": {
            "desktop": verified["base"]["gates"]["desktop"],
            "ocrToDeepSeek": verified["base"]["gates"]["ocrToDeepSeek"],
            "anki": verified["base"]["gates"]["anki"],
            "content": verified["content"],
            "speech": verified["speech"],
            "remnote": verified["remnote"],
        },
        "readableIndex": evidence_record(index_path),
        "qualityExecution": {"mode": "read-only-existing-evidence-plus-detached-identity", "externalCallsPerformed": 0, "candidateFilesModified": 0, "externalWritesRetried": False, "userManualUiRerun": False},
        "remainingNoGo": manifest_document(verified)["boundaries"],
    }
    receipt_path = output_root / "quality-supplement-receipt.json"
    receipt_path.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    after = base_gate.inventory(CANDIDATE_ROOT)
    base_gate.assert_equal(after["treeSha256"], verified["candidateInventory"]["treeSha256"], "candidate changed while emitting detached identity")
    return {
        "status": receipt["status"],
        "candidateHash": candidate_hash,
        "manifest": base_gate.project_relative(manifest_path),
        "manifestSha256": base_gate.sha256_file(manifest_path),
        "receipt": base_gate.project_relative(receipt_path),
        "receiptSha256": base_gate.sha256_file(receipt_path),
        "index": base_gate.project_relative(index_path),
        "indexSha256": base_gate.sha256_file(index_path),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("verify")
    emit_parser = subparsers.add_parser("emit")
    emit_parser.add_argument("--output-root", required=True, type=Path)
    args = parser.parse_args()
    try:
        if args.command == "verify":
            verified = verify_all()
            result = {
                "status": "verified",
                "content": verified["content"]["status"],
                "speech": verified["speech"]["status"],
                "remnote": verified["remnote"]["status"],
                "candidateTreeSha256": verified["candidateInventory"]["treeSha256"],
                "externalCallsPerformed": 0,
            }
        else:
            output_root = args.output_root if args.output_root.is_absolute() else PROJECT_ROOT / args.output_root
            result = emit(output_root)
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    except (GateFailure, OSError, ValueError, KeyError, TypeError) as exc:
        print(json.dumps({"status": "failed", "error": str(exc)}, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
