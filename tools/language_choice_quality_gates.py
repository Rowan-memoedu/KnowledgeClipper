from __future__ import annotations

import json
import hashlib
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator


PROJECT_ROOT = Path(__file__).resolve().parents[1]
FIXTURE_PATH = PROJECT_ROOT / "tests/Quality.Gates/fixtures/language-choice-card-behavior/language-choice-cases.v1.json"
READABLE_PATH = PROJECT_ROOT / "tests/Quality.Gates/fixtures/language-choice-card-behavior/readable-samples.md"
DECISION_STATE_FIXTURE = PROJECT_ROOT / "tests/Quality.Gates/fixtures/language-choice-card-behavior/desktop-state-decision-kind.observation.json"
FROZEN_DESKTOP_STATE_SCHEMA = PROJECT_ROOT / "contracts/desktop-automation/v1/semantic-state.schema.json"
EXPECTED_OPTIONS = ["meaning-reading", "writing", "both"]
FORBIDDEN_EXPORTER_FIELDS = ["choice", "decisionKind", "targetToken", "binding"]
CONTENT_LANGUAGE_AUTHORITY_RECEIPT = PROJECT_ROOT / "content-worker/canary/jobs/20260826T041418150Z-offline-language-practice-v2-replay-e7deb47e/canary-receipt.json"
CONTENT_LANGUAGE_AUTHORITY_SHA256 = "6E7DDC2B71D7647BAB24B89A28EE288CC11CF7050D012D4C49ADAE63A3CB9E63"
REMNOTE_ACCEPTANCE_RECEIPT = PROJECT_ROOT / "canary/rwa/260826041745/receipt.json"
REMNOTE_ACCEPTANCE_SHA256 = "C4DB779F4C5DB86F0AF236B5220A9527C7A581343F2E83E0778F49885DD2BDF3"
REMNOTE_MODULE_RECEIPT = PROJECT_ROOT / "receipts/remnote-exporter-real-language-both-core-acceptance-20260826T041817Z.json"
REMNOTE_MODULE_SHA256 = "21B15845D0DCCF09998E48854FBED72509137D6883A9E3C055EB02370CB5132F"
ANKI_ACCEPTANCE_RECEIPT = PROJECT_ROOT / "workers/anki-exporter/canary/ca/260826042542-8b7b/receipt.json"
ANKI_ACCEPTANCE_SHA256 = "AEB7F9D59085182C57E09BB49CE02DADBD089B4580BB7F2CB48DF44C47C0C62F"


class LanguageChoiceGateFailure(RuntimeError):
    pass


def load_fixture(path: Path = FIXTURE_PATH) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def windows_long_path(path: Path) -> Path:
    resolved = path.resolve()
    if len(str(resolved)) >= 260 and not str(resolved).startswith("\\\\?\\"):
        return Path("\\\\?\\" + str(resolved))
    return resolved


def validate_remnote_exporter_preparation() -> dict[str, Any]:
    if not REMNOTE_MODULE_RECEIPT.is_file() or sha256_file(REMNOTE_MODULE_RECEIPT) != REMNOTE_MODULE_SHA256:
        raise LanguageChoiceGateFailure("current RemNote exporter module receipt is missing or hash-mismatched")
    if not REMNOTE_ACCEPTANCE_RECEIPT.is_file() or sha256_file(REMNOTE_ACCEPTANCE_RECEIPT) != REMNOTE_ACCEPTANCE_SHA256:
        raise LanguageChoiceGateFailure("current RemNote exporter acceptance receipt is missing or hash-mismatched")
    module = load_fixture(REMNOTE_MODULE_RECEIPT)
    receipt = load_fixture(REMNOTE_ACCEPTANCE_RECEIPT)
    evidence = receipt.get("evidence", {})
    authority = evidence.get("realLanguageAuthorityReceipt", {})
    real_source = evidence.get("realLanguageSource", {})
    language_extension = evidence.get("languageExtension", {})
    authority_path = PROJECT_ROOT / authority.get("path", "")
    result_path = PROJECT_ROOT / evidence.get("cardExporterResult", "")
    module_consumer = module.get("consumerAcceptance", {})
    module_effects = module.get("externalEffects", {})
    if (
        module.get("status") != "passed"
        or module.get("module") != "export.remnote"
        or module_consumer.get("receiptSha256", "").upper() != REMNOTE_ACCEPTANCE_SHA256
        or module_consumer.get("checksPassed") != 14
        or module_consumer.get("checksFailed") != 0
        or module.get("authorityInput", {}).get("receiptSha256", "").upper() != CONTENT_LANGUAGE_AUTHORITY_SHA256
        or module_effects != {"realRemNoteCalled": False, "productionApiCalled": False, "offlineOnly": True}
        or receipt.get("suite") != "export.remnote-content-to-remnote.v1-consumer-acceptance"
        or receipt.get("status") != "passed"
        or len(receipt.get("checks", [])) != 14
        or any(item.get("status") != "passed" for item in receipt.get("checks", []))
        or real_source.get("writingCards") != 2
        or real_source.get("bothGroups") != 2
        or real_source.get("sourceCardOrder")
        != ["language_context", "language_writing", "language_context", "language_writing"]
        or real_source.get("terminalContainerNewline")
        != "removed by Content before card construction; no Exporter trimming"
        or any(front.endswith(("\r", "\n")) for front in real_source.get("writingFronts", []))
        or language_extension.get("state") != "accepted"
        or authority.get("sha256", "").upper() != CONTENT_LANGUAGE_AUTHORITY_SHA256
        or not authority_path.is_file()
        or sha256_file(authority_path) != CONTENT_LANGUAGE_AUTHORITY_SHA256
        or not result_path.is_file()
    ):
        raise LanguageChoiceGateFailure("current RemNote exporter acceptance violates the locked language boundary")
    result = load_fixture(result_path)
    if (
        result.get("status") != "completed"
        or result.get("worker", {}).get("id") != "export.remnote"
        or result.get("payload", {}).get("sink") != "markdown-zip"
        or result.get("payload", {}).get("outcome") != "prepared_for_manual_import"
        or result.get("payload", {}).get("readbackVerified") is not False
        or result.get("sideEffects") != []
    ):
        raise LanguageChoiceGateFailure("current RemNote exporter result is not a zero-write manual-import preparation")
    for source in receipt.get("sourceFixtures", []):
        if not windows_long_path(PROJECT_ROOT / source).is_file():
            raise LanguageChoiceGateFailure(f"RemNote acceptance source is missing: {source}")
    return {
        "status": "current-content-language-acceptance-passed",
        "receiptSha256": REMNOTE_MODULE_SHA256,
        "consumerReceiptSha256": REMNOTE_ACCEPTANCE_SHA256,
        "contentAuthorityReceiptSha256": CONTENT_LANGUAGE_AUTHORITY_SHA256,
        "consumerChecksPassed": 14,
        "realWritingCardsAccepted": 2,
        "exactWritingReplacement": "passed",
        "bothOrder": real_source["sourceCardOrder"],
        "decisionFieldsLeakedToExporter": False,
        "terminalNewlineRemovedByContent": True,
        "currentLanguageMappingAccepted": True,
        "manualImportPackagePrepared": True,
        "realRemNoteCalled": False,
    }


def validate_anki_exporter_preparation() -> dict[str, Any]:
    if not ANKI_ACCEPTANCE_RECEIPT.is_file() or sha256_file(ANKI_ACCEPTANCE_RECEIPT) != ANKI_ACCEPTANCE_SHA256:
        raise LanguageChoiceGateFailure("current Anki exporter acceptance receipt is missing or hash-mismatched")
    receipt = load_fixture(ANKI_ACCEPTANCE_RECEIPT)
    worker = receipt.get("worker", {})
    extension = receipt.get("languageWritingExtension", {})
    evidence = receipt.get("evidence", {}).get("languageWritingExtension", {})
    executable = PROJECT_ROOT / worker.get("path", "")
    # This acceptance is immutable v1.1.4 evidence. The canonical module path
    # now contains the additive v1.1.5 fix, so bind the old receipt to the
    # byte-identical v1.2.2 authority copy instead of a mutable module path.
    if executable.is_file() and sha256_file(executable) != worker.get("sha256", "").upper():
        executable = (
            PROJECT_ROOT
            / "releases/knowledge-clipper-authority-v1.2.2-20260828T012500Z/win-x64"
            / worker.get("path", "")
        )
    result_path = PROJECT_ROOT / evidence.get("exporterResult", "")
    if (
        receipt.get("suite") != "export.anki-content-to-anki.v1-consumer-acceptance"
        or receipt.get("status") != "passed"
        or worker.get("workerId") != "export.anki"
        or worker.get("workerVersion") != "1.1.4"
        or extension.get("status") != "passed"
        or extension.get("writingCards") != 2
        or extension.get("bothGroups") != 2
        or extension.get("choiceOwnedByExporter") is not False
        or extension.get("inputOrderRequired") is not True
        or extension.get("externalAnki") != "not contacted"
        or receipt.get("contentAuthority", {}).get("receiptSha256", "").upper() != CONTENT_LANGUAGE_AUTHORITY_SHA256
        or receipt.get("contentAuthority", {}).get("verified") is not True
        or receipt.get("external_api_calls") != 0
        or len(receipt.get("checks", [])) != 4
        or any(item.get("status") != "passed" for item in receipt.get("checks", []))
        or evidence.get("inputOrderPreserved") is not True
        or evidence.get("choiceAndTokenLeakage") is not False
        or evidence.get("externalAnkiCalls") != 0
        or evidence.get("sourceCardBundleSha256", "").upper() != "249EC0213C5BFC4E7253DC2FA5AEB6727AD52DB1449F73D74BB47045BC4E4016"
        or not executable.is_file()
        or sha256_file(executable) != worker.get("sha256", "").upper()
        or not result_path.is_file()
        or sha256_file(result_path) != evidence.get("exporterResultSha256", "").upper()
    ):
        raise LanguageChoiceGateFailure("current Anki exporter acceptance violates the locked language boundary")
    for item in receipt.get("sourceFixtures", []):
        path = PROJECT_ROOT / item.get("path", "")
        if not windows_long_path(path).is_file() or sha256_file(windows_long_path(path)) != item.get("sha256", "").upper():
            raise LanguageChoiceGateFailure(f"Anki acceptance source is missing or hash-mismatched: {item.get('path')}")
    result = load_fixture(result_path)
    if result.get("status") != "completed" or result.get("payload", {}).get("validateOnly") is not True:
        raise LanguageChoiceGateFailure("current Anki exporter result is not validate-only")
    return {
        "status": "current-content-language-acceptance-passed",
        "receiptSha256": ANKI_ACCEPTANCE_SHA256,
        "contentAuthorityReceiptSha256": CONTENT_LANGUAGE_AUTHORITY_SHA256,
        "consumerChecksPassed": 4,
        "choiceOwnedByExporter": False,
        "bothInputOrderPreserved": True,
        "exactWritingReplacement": "passed",
        "validateOnly": True,
        "realAnkiCalled": False,
    }


def verify_additive_contract_receipt(contract_root: Path, receipt: dict[str, Any]) -> str:
    files = receipt.get("files", [])
    if receipt.get("receiptExcludedFromTreeHash") is not True or not isinstance(files, list) or not files:
        raise LanguageChoiceGateFailure("additive desktop contract receipt has no immutable file inventory")
    digest = hashlib.sha256()
    seen: set[str] = set()
    for item in sorted(files, key=lambda entry: entry.get("path", "").encode("utf-8")):
        relative = item.get("path", "")
        if not relative or relative in seen or Path(relative).is_absolute() or ".." in Path(relative).parts:
            raise LanguageChoiceGateFailure("additive desktop contract receipt contains an unsafe or duplicate path")
        seen.add(relative)
        path = contract_root / relative
        expected_sha = item.get("sha256", "").lower()
        if (
            not path.is_file()
            or path.stat().st_size != item.get("bytes")
            or sha256_file(path).lower() != expected_sha
        ):
            raise LanguageChoiceGateFailure(f"additive desktop contract file mismatch: {relative}")
        digest.update(f"{relative}|{expected_sha}\n".encode("utf-8"))
    tree_sha = digest.hexdigest().lower()
    if tree_sha != receipt.get("treeSha256", "").lower():
        raise LanguageChoiceGateFailure("additive desktop contract tree hash mismatch")
    return tree_sha.upper()


def desktop_additive_contract_status() -> dict[str, Any]:
    frozen_schema = load_fixture(FROZEN_DESKTOP_STATE_SCHEMA)
    observation = load_fixture(DECISION_STATE_FIXTURE)
    frozen_errors = list(Draft202012Validator(frozen_schema).iter_errors(observation))
    if not any(list(error.absolute_path) == ["recentTask"] for error in frozen_errors):
        raise LanguageChoiceGateFailure("frozen desktop-state.v1 no longer rejects decisionKind")
    candidates: list[dict[str, Any]] = []
    for contract_root in sorted((PROJECT_ROOT / "contracts/desktop-automation").iterdir()):
        if not contract_root.is_dir() or contract_root.name == "v1":
            continue
        contract_path = contract_root / "desktop-automation.contract.json"
        receipt_path = contract_root / "contract-receipt.json"
        if not contract_path.is_file() or not receipt_path.is_file():
            continue
        contract_document = load_fixture(contract_path)
        schema_name = contract_document.get("schemas", {}).get("semanticState")
        if not isinstance(schema_name, str) or not schema_name:
            continue
        schema_path = contract_root / schema_name
        if not schema_path.is_file() or schema_path.parent != contract_root:
            continue
        schema = load_fixture(schema_path)
        recent_properties = schema.get("properties", {}).get("recentTask", {}).get("properties", {})
        if "decisionKind" not in recent_properties:
            continue
        validation_errors = list(Draft202012Validator(schema).iter_errors(observation))
        tree_sha = verify_additive_contract_receipt(contract_root, load_fixture(receipt_path))
        common_checkpoint = contract_document.get("commonCheckpoint", {})
        common_checkpoint_valid = (
            common_checkpoint.get("checkpointId") == "contracts-v1-20260825T115938Z"
            and common_checkpoint.get("receiptSha256", "").upper()
            == "1CB738FE133F6645B9757E246E2FBC210785086342F70BAD32B5015BD19E1B1E"
        )
        candidates.append({
            "schema": schema_path.relative_to(PROJECT_ROOT).as_posix(),
            "automationContractPresent": True,
            "contractReceiptPresent": True,
            "contractTreeSha256": tree_sha,
            "observationValidUnderNewSchema": not validation_errors,
            "commonCheckpointReferenceValid": common_checkpoint_valid,
        })
    ready = [
        item for item in candidates
        if item["observationValidUnderNewSchema"] and item["commonCheckpointReferenceValid"]
    ]
    return {
        "status": "additive-contract-ready" if len(ready) == 1 else "awaiting-additive-desktop-contract",
        "frozenV1UnchangedAndRejectsDecisionKind": True,
        "newSchemaValidationRequired": True,
        "candidates": candidates,
    }


def validate_fixture(document: dict[str, Any]) -> dict[str, Any]:
    desktop = document.get("desktopIntegration", {})
    entry = desktop.get("visibleTextQuestionEntry", {})
    popup = desktop.get("choicePopup", {})
    if (
        document.get("fixtureType") != "quality-observation-fixture-not-business-schema"
        or entry.get("semanticActions") != ["question.open", "question.submit"]
        or entry.get("followUpCommandType") != "task.provide_input"
        or popup.get("trigger") != "language-detected"
        or popup.get("options") != EXPECTED_OPTIONS
    ):
        raise LanguageChoiceGateFailure("desktop entry or exact three-choice popup mapping drifted")

    source = document.get("source", {})
    visible = source.get("visibleText")
    term = source.get("targetTerm")
    pronunciation = source.get("pronunciation")
    meaning = source.get("meaning")
    if not all(isinstance(value, str) and value for value in (visible, term, pronunciation, meaning)):
        raise LanguageChoiceGateFailure("canonical readable source is incomplete")
    baseline = document.get("baselineExistingCard")
    expected_writing = {
        "variant": "writing",
        "front": visible.replace(term, f"（{pronunciation}；{meaning}）"),
        "back": term,
    }
    expected_meaning = {"variant": "meaning-reading", **baseline}
    cases = {item.get("id"): item for item in document.get("cases", [])}
    if len(cases) != 6:
        raise LanguageChoiceGateFailure("quality fixture must contain the exact six locked observations")

    meaning_case = cases["meaning-reading-byte-equivalent"]
    if meaning_case.get("cards") != [expected_meaning]:
        raise LanguageChoiceGateFailure("meaning-reading is not byte-equivalent to the existing card")
    writing_case = cases["writing-unique-replacement"]
    if (
        visible.count(term) != 1
        or writing_case.get("cards") != [expected_writing]
        or term in writing_case["cards"][0]["front"]
    ):
        raise LanguageChoiceGateFailure("writing card does not replace the unique target term exactly once")
    both_case = cases["both-original-first-exactly-two"]
    if both_case.get("cards") != [expected_meaning, expected_writing]:
        raise LanguageChoiceGateFailure("both must contain exactly two cards with the original card first")
    for item in (meaning_case, writing_case, both_case):
        if item.get("taskStatus") != "succeeded" or item.get("releaseState") != "ready":
            raise LanguageChoiceGateFailure("ready language choice case has an invalid terminal state")

    expected_unresolved = {
        "ambiguous-target-waits": "target_term_ambiguous",
        "missing-pronunciation-waits": "pronunciation_missing",
        "missing-meaning-waits": "meaning_missing",
    }
    for case_id, reason in expected_unresolved.items():
        item = cases[case_id]
        if (
            item.get("taskStatus") != "waiting_input"
            or item.get("releaseState") != "unresolved"
            or item.get("cards") != []
            or item.get("unresolvedReasons") != [reason]
        ):
            raise LanguageChoiceGateFailure(f"{case_id} does not fail closed")
    if cases["ambiguous-target-waits"].get("override", {}).get("visibleText", "").count(term) < 2:
        raise LanguageChoiceGateFailure("ambiguous target fixture does not contain multiple target occurrences")
    if cases["missing-pronunciation-waits"].get("override", {}).get("pronunciation", "present") is not None:
        raise LanguageChoiceGateFailure("missing pronunciation fixture is not actually missing pronunciation")
    if cases["missing-meaning-waits"].get("override", {}).get("meaning", "present") is not None:
        raise LanguageChoiceGateFailure("missing meaning fixture is not actually missing meaning")

    policy = document.get("candidateBoundPolicy", {})
    if policy.get("allowedModes") != ["fake", "offline", "validate-only"] or policy.get("externalProductionApisInvoked") is not False:
        raise LanguageChoiceGateFailure("candidate-bound policy permits production service access")
    exporter = document.get("exporterBoundary", {})
    if (
        exporter.get("forbiddenFields") != FORBIDDEN_EXPORTER_FIELDS
        or exporter.get("targets") != ["export.remnote", "export.anki"]
    ):
        raise LanguageChoiceGateFailure("terminal exporter boundary leaks decision-layer fields")
    serialized_cards = json.dumps(
        [card for item in cases.values() for card in item.get("cards", [])],
        ensure_ascii=False,
        separators=(",", ":"),
    )
    if any(f'"{field}"' in serialized_cards for field in FORBIDDEN_EXPORTER_FIELDS):
        raise LanguageChoiceGateFailure("projected cards expose decision-layer fields to exporters")
    if not READABLE_PATH.is_file():
        raise LanguageChoiceGateFailure("readable sample is missing")
    return {
        "status": "fixture-ready-implementation-pending",
        "cases": len(cases),
        "choiceOptions": EXPECTED_OPTIONS,
        "existingDesktopActionsReused": entry["semanticActions"],
        "existingProvideInputCommandReused": True,
        "forbiddenExporterFields": FORBIDDEN_EXPORTER_FIELDS,
        "businessSchemaCreated": False,
        "externalProductionApisInvoked": False,
    }


def current_status() -> dict[str, Any]:
    result = validate_fixture(load_fixture())
    result["remnoteExporterPreparation"] = validate_remnote_exporter_preparation()
    result["ankiExporterPreparation"] = validate_anki_exporter_preparation()
    result["desktopAdditiveContract"] = desktop_additive_contract_status()
    return result
