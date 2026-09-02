from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import Any


PROJECT_ROOT = Path(__file__).resolve().parents[1]
FIXTURE_PATH = PROJECT_ROOT / "tests/Quality.Gates/fixtures/question-answer-additive-boundary/question-answer-observations.v1.json"
CORE_RESOLVER_PATH = PROJECT_ROOT / "orchestrator/Core/DesktopIntentResolver.cs"
CONTENT_RECEIPT_PATH = PROJECT_ROOT / "receipts/content-worker-v0.5.0-authoritative-20260826T041900Z.json"
CONTENT_RECEIPT_SHA256 = "3476492F2B6FF69014B9AFC854F25A671C1D96BC5720DBC43446BF28C9BF43A4"
DESKTOP_MODULE_RECEIPT_PATH = PROJECT_ROOT / "receipts/knowledge-clipper-question-workflow-v22-canary-knowledge-clipper-question-workflow-v22-20260826T042021Z.json"
DESKTOP_MODULE_RECEIPT_SHA256 = "AC513DE82F9C55FF4C87DCA0B84BC9153A4B4F637766D8C8189BB5617C04C978"
DESKTOP_WINDOWS_RECEIPT_PATH = PROJECT_ROOT / "canary/desktop-windows/20260826T042430Z/receipt.json"
DESKTOP_WINDOWS_RECEIPT_SHA256 = "8B9AB3B849AF86D7AF34A1FD3FB04D18E8737FF97F55B82E23D977DB5E1BCFAF"
DESKTOP_ADDITIVE_CONTRACT_RECEIPT_PATH = PROJECT_ROOT / "contracts/desktop-automation/v1.1/contract-receipt.json"
DESKTOP_ADDITIVE_CONTRACT_RECEIPT_SHA256 = "B0D491B99A37315583EFC27A22592C5AF13AE150BD9F10F96BDB7A3D1D353953"
REMNOTE_MODULE_ACCEPTANCE_PATH = PROJECT_ROOT / "receipts/remnote-exporter-real-language-both-core-acceptance-20260826T041817Z.json"
REMNOTE_MODULE_ACCEPTANCE_SHA256 = "21B15845D0DCCF09998E48854FBED72509137D6883A9E3C055EB02370CB5132F"
REMNOTE_ACCEPTANCE_PATH = PROJECT_ROOT / "canary/rwa/260826041745/receipt.json"
REMNOTE_ACCEPTANCE_SHA256 = "C4DB779F4C5DB86F0AF236B5220A9527C7A581343F2E83E0778F49885DD2BDF3"
ANKI_ACCEPTANCE_PATH = PROJECT_ROOT / "workers/anki-exporter/canary/ca/260826042542-8b7b/receipt.json"
ANKI_ACCEPTANCE_SHA256 = "AEB7F9D59085182C57E09BB49CE02DADBD089B4580BB7F2CB48DF44C47C0C62F"
CORE_QUESTION_RECEIPT_PATH = PROJECT_ROOT / "canary/core-question-workflow/core-question-workflow-20260826T042400Z/receipt.json"
CORE_QUESTION_RECEIPT_SHA256 = "D1E2896F103ECBEBFE8C4FD644844918CBEE1E3FF6BAC4EE8C55857BFD990C61"
CORE_LANGUAGE_RECEIPT_PATH = PROJECT_ROOT / "canary/core-language-practice/core-language-practice-20260826T042400Z/receipt.json"
CORE_LANGUAGE_RECEIPT_SHA256 = "535252F6811C622BB894E23F84EB1CC2D124ADB6FBFBB545CD5FA67EC2F47485"
CORE_WORKFLOWS_CHECKPOINT_PATH = PROJECT_ROOT / "contracts/authority/core-workflows/v2.2.0-20260826T052243Z/checkpoint.json"
CORE_WORKFLOWS_CHECKPOINT_SHA256 = "E4CF8DE7F0AF507E7564F0F8093283C32B77A282B1DFFF6F0E213673CCE5CE09"
CORE_WORKFLOWS_RECEIPT_PATH = PROJECT_ROOT / "receipts/core-workflows-v2.2.0-checkpoint-20260826T052243Z.json"
CORE_WORKFLOWS_RECEIPT_SHA256 = "C3E29A64FE544B72496A02FAA90EED672B9B53C412C0552E230366A9C7812E31"


class QuestionAnswerGateFailure(RuntimeError):
    pass


def load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def checkpoint_tree(root: Path) -> str:
    files = [path for path in root.rglob("*") if path.is_file()]
    rows = []
    for path in sorted(files, key=lambda item: str(item.relative_to(PROJECT_ROOT))):
        relative = str(path.relative_to(PROJECT_ROOT))
        rows.append(f"{relative}\t{path.stat().st_size}\t{sha256_file(path).lower()}")
    return hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper()


def validate_fixture(document: dict[str, Any]) -> dict[str, Any]:
    current = document.get("currentCoreFailure", {})
    required = document.get("requiredAdditiveEvidence", {})
    desktop = required.get("desktop", {})
    policy = document.get("acceptancePolicy", {})
    if (
        document.get("fixtureType") != "quality-observation-fixture-not-business-schema"
        or current != {
            "intent": "clip.question",
            "requestedOutput": "question.answer",
            "errorCode": "resolver.intent_unsupported",
        }
        or required.get("core") != ["question.answer", "question.follow-up", "question.make-cards"]
        or required.get("content") != ["question.answer"]
        or required.get("exporters") != ["export.remnote", "export.anki"]
        or desktop.get("semanticActions") != ["question.submit", "question.follow-up", "question.make-cards"]
        or desktop.get("answerRegion") != {"visible": True, "readOnly": True}
        or policy != {
            "frozenV1Modified": False,
            "newAdditiveContractsOnly": True,
            "candidateBlockedUntilAllNewReceiptsPass": True,
            "externalProductionApisInvoked": False,
        }
    ):
        raise QuestionAnswerGateFailure("question-answer additive quality observation drifted")
    return document


def historical_desktop_windows_ui_failure(document: dict[str, Any]) -> dict[str, Any]:
    evidence = document.get("desktopWindowsUiCanary", {})
    path = PROJECT_ROOT / evidence.get("path", "")
    if not path.is_file() or sha256_file(path) != evidence.get("sha256", "").upper():
        raise QuestionAnswerGateFailure("Desktop Windows UI failure evidence is missing or hash-mismatched")
    text = path.read_text(encoding="utf-8")
    if evidence.get("expectedFailure") not in text or "WINDOWS_UI_CANARY FAIL" not in text:
        raise QuestionAnswerGateFailure("Desktop Windows UI failure evidence no longer records the locked assertion")
    return {
        "status": "historical-failed-superseded-by-recovery",
        "receipt": None,
        "logPath": evidence["path"],
        "logSha256": evidence["sha256"],
        "failedAssertion": evidence["expectedFailure"],
        "repairReceiptRequired": False,
    }


def current_desktop_module_evidence() -> dict[str, Any]:
    for path, expected in (
        (DESKTOP_MODULE_RECEIPT_PATH, DESKTOP_MODULE_RECEIPT_SHA256),
        (DESKTOP_WINDOWS_RECEIPT_PATH, DESKTOP_WINDOWS_RECEIPT_SHA256),
        (DESKTOP_ADDITIVE_CONTRACT_RECEIPT_PATH, DESKTOP_ADDITIVE_CONTRACT_RECEIPT_SHA256),
    ):
        if not path.is_file() or sha256_file(path) != expected:
            raise QuestionAnswerGateFailure(f"Desktop recovery evidence is missing or hash-mismatched: {path}")
    module = load_json(DESKTOP_MODULE_RECEIPT_PATH)
    windows = load_json(DESKTOP_WINDOWS_RECEIPT_PATH)
    automated = windows.get("automatedWindowsUiCanary", {})
    additive = module.get("additiveDesktopContract", {})
    startup = module.get("automatedEvidence", {}).get("startupDiagnostic", {})
    if (
        module.get("schemaVersion") != "desktop-ui-clickable-canary.v1"
        or module.get("productName") != "零碎知识剪藏"
        or not str(module.get("startEntrypoint", "")).endswith("/KnowledgeClipper.exe")
        or automated.get("status") != "PASS"
        or automated.get("assertions") != 72
        or automated.get("logSha256", "").upper()
        != "9BA607EA1C1342AB3C981DA9227A561122E0D71AA855383CA18F776F5CB5BF19"
        or additive.get("version") != "1.1.0"
        or additive.get("receiptSha256", "").upper() != DESKTOP_ADDITIVE_CONTRACT_RECEIPT_SHA256
        or additive.get("verified") is not True
        or module.get("questionWorkflowDependencies", {}).get("coreContractVersion") != "2.2.0"
        or module.get("questionWorkflowDependencies", {}).get("legacyDesktopQuestionActionUsed") is not False
        or startup.get("externalConnections") != 0
        or startup.get("namedPipeInvoked") is not False
        or startup.get("remNoteInvoked") is not False
        or startup.get("mineruInvoked") is not False
    ):
        raise QuestionAnswerGateFailure("Desktop recovery evidence violates its safe module boundary")
    return {
        "status": "desktop-v2.2-module-passed-production-core-final-pending",
        "moduleReceiptSha256": DESKTOP_MODULE_RECEIPT_SHA256,
        "windowsReceiptSha256": DESKTOP_WINDOWS_RECEIPT_SHA256,
        "additiveContractReceiptSha256": DESKTOP_ADDITIVE_CONTRACT_RECEIPT_SHA256,
        "assertionsPassed": 72,
        "readOnlyAnswerRegionObserved": True,
        "externalProductionApisInvoked": False,
        "candidateEligible": False,
    }


def current_content_evidence() -> dict[str, Any]:
    if not CONTENT_RECEIPT_PATH.is_file() or sha256_file(CONTENT_RECEIPT_PATH) != CONTENT_RECEIPT_SHA256:
        raise QuestionAnswerGateFailure("authoritative Content 0.5.0 receipt is missing or hash-mismatched")
    receipt = load_json(CONTENT_RECEIPT_PATH)
    worker = receipt.get("worker", {})
    regression = receipt.get("regression", {})
    if (
        receipt.get("status") != "completed"
        or worker.get("workerVersion") != "0.5.0"
        or worker.get("operations") != ["understand-and-card", "question.answer", "question.make-cards"]
        or worker.get("outputKinds")
        != ["learning-bundle", "card-bundle", "language-practice-decision", "question.answer"]
        or regression.get("passed") != 58
        or regression.get("failed") != 0
        or len(receipt.get("independentOfflineCanaries", [])) != 4
        or any(item.get("status") != "completed" for item in receipt.get("independentOfflineCanaries", []))
        or receipt.get("externalCalls") != {"deepSeek": 0, "exporters": 0, "desktop": 0}
    ):
        raise QuestionAnswerGateFailure("authoritative Content 0.5.0 question evidence violates its boundary")
    return {
        "status": "module-passed-controller-integration-pending",
        "receiptSha256": CONTENT_RECEIPT_SHA256,
        "testsPassed": 58,
        "offlineCanariesCompleted": 4,
        "externalProductionApisInvoked": False,
    }


def current_exporter_evidence() -> dict[str, Any]:
    outputs: dict[str, Any] = {}
    for worker_id, path, expected, expected_suite, checks in (
        ("export.remnote", REMNOTE_ACCEPTANCE_PATH, REMNOTE_ACCEPTANCE_SHA256, "export.remnote-content-to-remnote.v1-consumer-acceptance", 14),
        ("export.anki", ANKI_ACCEPTANCE_PATH, ANKI_ACCEPTANCE_SHA256, "export.anki-content-to-anki.v1-consumer-acceptance", 4),
    ):
        if not path.is_file() or sha256_file(path) != expected:
            raise QuestionAnswerGateFailure(f"current {worker_id} acceptance is missing or hash-mismatched")
        receipt = load_json(path)
        if (
            receipt.get("suite") != expected_suite
            or receipt.get("status") != "passed"
            or len(receipt.get("checks", [])) != checks
            or any(item.get("status") != "passed" for item in receipt.get("checks", []))
        ):
            raise QuestionAnswerGateFailure(f"current {worker_id} acceptance did not pass its locked checks")
        outputs[worker_id] = {"status": "current-content-fixture-accepted", "receiptSha256": expected, "checksPassed": checks}
    if not REMNOTE_MODULE_ACCEPTANCE_PATH.is_file() or sha256_file(REMNOTE_MODULE_ACCEPTANCE_PATH) != REMNOTE_MODULE_ACCEPTANCE_SHA256:
        raise QuestionAnswerGateFailure("current export.remnote module acceptance is missing or hash-mismatched")
    remnote_module = load_json(REMNOTE_MODULE_ACCEPTANCE_PATH)
    if (
        remnote_module.get("status") != "passed"
        or remnote_module.get("consumerAcceptance", {}).get("receiptSha256", "").upper() != REMNOTE_ACCEPTANCE_SHA256
        or remnote_module.get("externalEffects")
        != {"realRemNoteCalled": False, "productionApiCalled": False, "offlineOnly": True}
    ):
        raise QuestionAnswerGateFailure("current export.remnote module acceptance violates its zero-write boundary")
    outputs["export.remnote"]["moduleReceiptSha256"] = REMNOTE_MODULE_ACCEPTANCE_SHA256
    return outputs


def current_core_boundary() -> dict[str, Any]:
    if not CORE_RESOLVER_PATH.is_file():
        raise QuestionAnswerGateFailure("Core DesktopIntentResolver is missing")
    text = CORE_RESOLVER_PATH.read_text(encoding="utf-8")
    unsupported = (
        'intent.Intent == "clip.question"' in text
        and '"resolver.intent_unsupported"' in text
        and "separate question-answer pipeline contract" in text
    )
    if unsupported:
        status = "resolver-intent-unsupported-no-go"
        contract_tests = None
    else:
        for path, expected in (
            (CORE_QUESTION_RECEIPT_PATH, CORE_QUESTION_RECEIPT_SHA256),
            (CORE_LANGUAGE_RECEIPT_PATH, CORE_LANGUAGE_RECEIPT_SHA256),
        ):
            if not path.is_file() or sha256_file(path) != expected:
                raise QuestionAnswerGateFailure(f"Core v2.2 contract-test evidence is missing or hash-mismatched: {path}")
        question = load_json(CORE_QUESTION_RECEIPT_PATH)
        language = load_json(CORE_LANGUAGE_RECEIPT_PATH)
        if (
            question.get("status") != "passed"
            or question.get("observed", {}).get("orderedTurns") != 2
            or question.get("observed", {}).get("exportersStarted") != 0
            or question.get("observed", {}).get("makeCardsOutputs") != ["learning-bundle", "card-bundle"]
            or language.get("status") != "passed"
            or any(item.get("status") != "passed" for item in language.get("tests", []))
        ):
            raise QuestionAnswerGateFailure("Core v2.2 contract-test evidence violates the locked offline boundary")
        for path, expected in (
            (CORE_WORKFLOWS_CHECKPOINT_PATH, CORE_WORKFLOWS_CHECKPOINT_SHA256),
            (CORE_WORKFLOWS_RECEIPT_PATH, CORE_WORKFLOWS_RECEIPT_SHA256),
        ):
            if not path.is_file() or sha256_file(path) != expected:
                raise QuestionAnswerGateFailure(f"Core workflow authority evidence is missing or hash-mismatched: {path}")
        checkpoint = load_json(CORE_WORKFLOWS_CHECKPOINT_PATH)
        checkpoint_receipt = load_json(CORE_WORKFLOWS_RECEIPT_PATH)
        trees = {item.get("root"): item for item in checkpoint.get("contractTrees", [])}
        v21_root = PROJECT_ROOT / "contracts/core/v2.1"
        v22_root = PROJECT_ROOT / "contracts/core/v2.2/question-workflow"
        if (
            checkpoint.get("status") != "stable"
            or checkpoint.get("checkpointId") != "core-workflows-v2.2.0-20260826T052243Z"
            or checkpoint.get("scope")
            != {"languagePractice": "v2.1", "questionWorkflow": "v2.2", "destinationNeutral": True, "externalProductionApisInvoked": False}
            or checkpoint.get("frozenV1", {}).get("filesChecked") != 21
            or checkpoint.get("frozenV1", {}).get("mismatches") != 0
            or checkpoint.get("frozenV1", {}).get("modified") is not False
            or trees.get("contracts/core/v2.1", {}).get("files") != 22
            or trees.get("contracts/core/v2.1", {}).get("treeSha256", "").upper() != checkpoint_tree(v21_root)
            or trees.get("contracts/core/v2.2/question-workflow", {}).get("files") != 14
            or trees.get("contracts/core/v2.2/question-workflow", {}).get("treeSha256", "").upper() != checkpoint_tree(v22_root)
            or checkpoint_receipt.get("status") != "passed"
            or checkpoint_receipt.get("checkpoint", {}).get("sha256", "").upper() != CORE_WORKFLOWS_CHECKPOINT_SHA256
            or checkpoint_receipt.get("contentCompatibility", {}).get("receiptSha256", "").upper() != CONTENT_RECEIPT_SHA256
            or checkpoint_receipt.get("externalCalls") != {"deepSeek": 0, "remNote": 0, "anki": 0, "minerU": 0}
        ):
            raise QuestionAnswerGateFailure("Core v2.2 authority checkpoint violates the locked quality boundary")
        source_evidence_drifted: list[str] = []
        for item in checkpoint.get("sourceEvidence", []):
            source = PROJECT_ROOT / item.get("path", "")
            if (
                not source.is_file()
                or source.stat().st_size != item.get("bytes")
                or sha256_file(source) != item.get("sha256", "").upper()
            ):
                source_evidence_drifted.append(item.get("path", ""))
        status = (
            "core-v2.2-authority-checkpoint-historical-source-advanced-v1.2.3-candidate-pending"
            if source_evidence_drifted
            else "core-v2.2-authority-checkpoint-passed-neutral-worker-authority-pending"
        )
        contract_tests = {
            "questionWorkflowReceiptSha256": CORE_QUESTION_RECEIPT_SHA256,
            "languagePracticeReceiptSha256": CORE_LANGUAGE_RECEIPT_SHA256,
            "coreWorkflowsCheckpointSha256": CORE_WORKFLOWS_CHECKPOINT_SHA256,
            "coreWorkflowsReceiptSha256": CORE_WORKFLOWS_RECEIPT_SHA256,
            "orchestratorContractGroupsPassed": 23,
            "externalProductionApisInvoked": False,
            "sourceEvidenceDrifted": source_evidence_drifted,
        }
    return {
        "status": status,
        "intent": "clip.question",
        "requiredOutput": "question.answer",
        "additiveCoreReceipt": CORE_WORKFLOWS_RECEIPT_SHA256 if not unsupported else None,
        "contractTests": contract_tests,
        "candidateEligible": False,
    }


def current_status() -> dict[str, Any]:
    document = validate_fixture(load_json(FIXTURE_PATH))
    return {
        "status": "blocked-awaiting-neutral-five-worker-authority-and-new-desktop-canary",
        "historicalDesktopWindowsUiFailure": historical_desktop_windows_ui_failure(document),
        "desktopWindowsUiCanary": current_desktop_module_evidence(),
        "coreQuestionBoundary": current_core_boundary(),
        "contentModuleEvidence": current_content_evidence(),
        "exporterAcceptanceEvidence": current_exporter_evidence(),
        "requiredAdditiveEvidence": document["requiredAdditiveEvidence"],
        "frozenV1Modified": False,
        "businessSchemaCreated": False,
        "candidateEligible": False,
        "externalProductionApisInvoked": False,
    }


if __name__ == "__main__":
    print(json.dumps(current_status(), ensure_ascii=False, indent=2))
