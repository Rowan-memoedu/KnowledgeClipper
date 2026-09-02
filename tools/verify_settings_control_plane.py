from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

from jsonschema import Draft202012Validator, FormatChecker

import qualify_integrated_candidate_v124 as v124
import quality_gates
import verify_integrated_candidate_quality as common


PROJECT_ROOT = Path(__file__).resolve().parents[1]
ADAPTER_SCHEMA = PROJECT_ROOT / "tests" / "Quality.Gates" / "fixtures" / "settings-control" / "schemas" / "owner-evidence.schema.json"
FIXTURE_ROOT = ADAPTER_SCHEMA.parents[1]
DEFAULT_OWNER_EVIDENCE = PROJECT_ROOT / "receipts" / "settings-control-owner-evidence-current.json"
CORE_CHECKPOINT = PROJECT_ROOT / "contracts" / "core" / "v2.4" / "settings-control" / "checkpoints" / "v2.4.1-20260828T064200Z" / "checkpoint.json"
CORE_CHECKPOINT_SHA256 = "120C6AC4C50671AACF9AFB13A52BBDCCAB7C5CB977D31872DAA81D24A3996C3C"
CORE_RECEIPT_SHA256 = "43980EDCBE9A86A64AED150D500CF464B35FA375CCED359532EE6C2064F7C5DB"
CONTENT_RECEIPT = PROJECT_ROOT / "content-worker" / "receipts" / "content-settings-adapter-v1" / "20260828T062914525Z" / "receipt.v1.json"
CONTENT_RECEIPT_SHA256 = "74A02FF2DB148F2EC8D4A8EF61F5F2C05839EEBC2D4F22987101C5045BAAE198"
REMNOTE_RECEIPT = PROJECT_ROOT / "canary" / "remnote-exporter-v2-offline" / "20260828T143719" / "receipt.json"
REMNOTE_RECEIPT_SHA256 = "18BEF9D8C5A201828E7E799B3D0F2A5845422AF39B993307698E54B095A588A8"
SPEECH_RECEIPT = PROJECT_ROOT / "tests" / "RemNoteQuickClipper.Speech.Tests" / "bin" / "out" / "r061648c2ee" / "unit-test-receipt.json"
SPEECH_RECEIPT_SHA256 = "6181DB5BC4F1B6BD5F8518F7565677BDE0CA704907669D8356D17EFA84B13979"
IMAGE_BUILD_RECEIPT = PROJECT_ROOT / "workers" / "image-ocr" / "candidates" / "1.2.0" / "build-receipt.json"
IMAGE_BUILD_RECEIPT_SHA256 = "6F0ED9859869966F69F04BBF09743FB54EF96C18D6A7F73A237F24E4BBE7EDC5"
ANKI_RECEIPT = PROJECT_ROOT / "workers" / "anki-exporter" / "canary" / "settings-adapter-probe" / "20260828T062757Z-44953fb1" / "receipt.json"
ANKI_RECEIPT_SHA256 = "FBA432B68B2E63D274734E4F96A49CBCEDD7D64378296FCB2C80423E6469CB1F"
DESKTOP_SETTINGS_RECEIPT = PROJECT_ROOT / "receipts" / "desktop-settings-control-v2.4.1-knowledge-clipper-settings-control-v2.4.1-20260828T072000Z.json"
DESKTOP_SETTINGS_RECEIPT_SHA256 = "D3DF645D896F2071F298B82DF516414EFF1A61D5B406E4461CEE6CB8DA54C45A"
DESKTOP_SETTINGS_ROOT = PROJECT_ROOT / "dist" / "desktop-win-x64" / "knowledge-clipper-settings-control-v2.4.1-20260828T072000Z"
DESKTOP_SETTINGS_EXPECTED = (483, 168567677, "042013156E922A7D0E5B653F2B5D26AC37C1E03F59F1F0672D95F2D26868D815")
DESKTOP_WINDOWS_RECEIPT = PROJECT_ROOT / "canary" / "desktop-windows" / "20260828T071546Z" / "receipt.json"
DESKTOP_WINDOWS_RECEIPT_SHA256 = "861AF4F5FA7ECC9EC1036CC8DB42E077DCAD23B1953FCD8350A38FC8DC475614"
PRIOR_CORE_ADMISSION_RECEIPT = PROJECT_ROOT / "canary" / "settings-control-quality" / "settings-control-native-20260828T070356Z" / "quality-receipt.json"
PRIOR_CORE_ADMISSION_RECEIPT_SHA256 = "5A6BACFDD064A2AB244D8E968E6DB3C6DEC3127F295F067D62B933ACED2573C8"

SECRET_SENTINEL = "KC_QUALITY_TEST_SECRET_DO_NOT_PERSIST"
SECRET_KEY = re.compile(r"^(?:api[_-]?key|password|client[_-]?secret|access[_-]?token|refresh[_-]?token|authorization)$", re.I)
SECRET_TEXT = (
    re.compile(r"(?<![A-Za-z0-9])sk-[A-Za-z0-9_-]{16,}"),
    re.compile(r"Bearer\s+[A-Za-z0-9._~+/-]{20,}", re.I),
)
SAFE_SECRET_VALUES = {"", "redacted", "***", "<redacted>", None}
TEXT_SUFFIXES = {".json", ".jsonl", ".md", ".txt", ".log", ".yaml", ".yml", ".toml", ".xml", ".config"}


class GateFailure(RuntimeError):
    pass


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def sha256_file(path: Path) -> str:
    return common.sha256_file(path)


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def canonical_json_sha256(value: Any) -> str:
    payload = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(payload).hexdigest().upper()


def project_relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT_ROOT.resolve()).as_posix()


def display_path(path: Path) -> str:
    try:
        return project_relative(path)
    except ValueError:
        return str(path.resolve())


def json_pointer(document: Any, pointer: str) -> Any:
    if pointer == "":
        return document
    if not pointer.startswith("/"):
        raise GateFailure(f"invalid JSON pointer: {pointer}")
    current = document
    for token in pointer[1:].split("/"):
        token = token.replace("~1", "/").replace("~0", "~")
        try:
            current = current[int(token)] if isinstance(current, list) else current[token]
        except (KeyError, IndexError, ValueError, TypeError) as exc:
            raise GateFailure(f"JSON pointer not found: {pointer}") from exc
    return current


def resolve_file_ref(item: dict[str, Any], label: str) -> Path:
    relative = Path(str(item["path"]))
    if relative.is_absolute():
        raise GateFailure(f"{label}: absolute path is forbidden")
    path = (PROJECT_ROOT / relative).resolve()
    if not path.is_relative_to(PROJECT_ROOT.resolve()):
        raise GateFailure(f"{label}: path escapes project root")
    if not path.is_file():
        raise GateFailure(f"{label}: missing file: {relative.as_posix()}")
    actual_hash = sha256_file(path)
    if actual_hash != str(item["sha256"]).upper():
        raise GateFailure(f"{label}: sha256 mismatch")
    if "bytes" in item and path.stat().st_size != int(item["bytes"]):
        raise GateFailure(f"{label}: bytes mismatch")
    return path


def resolve_native_ref(item: dict[str, Any], label: str, base: Path = PROJECT_ROOT) -> Path:
    raw = Path(str(item["path"]))
    path = raw.resolve() if raw.is_absolute() else (base / raw).resolve()
    if not path.is_relative_to(PROJECT_ROOT.resolve()) or not path.is_file():
        raise GateFailure(f"{label}: missing or out-of-project file: {raw}")
    expected = str(item.get("sha256", "")).upper()
    if expected and sha256_file(path) != expected:
        raise GateFailure(f"{label}: sha256 mismatch")
    if "bytes" in item and path.stat().st_size != int(item["bytes"]):
        raise GateFailure(f"{label}: bytes mismatch")
    return path


def walk_file_refs(value: Any, prefix: str = "evidence") -> Iterable[tuple[str, dict[str, Any]]]:
    if isinstance(value, dict):
        if set(value).issuperset({"path", "sha256"}) and isinstance(value.get("path"), str):
            yield prefix, value
        else:
            for key, child in value.items():
                yield from walk_file_refs(child, f"{prefix}.{key}")
    elif isinstance(value, list):
        for index, child in enumerate(value):
            yield from walk_file_refs(child, f"{prefix}[{index}]")


def validate_fixture_contract() -> dict[str, Any]:
    schema = load_json(ADAPTER_SCHEMA)
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema, format_checker=FormatChecker())
    valid_files = sorted((FIXTURE_ROOT / "valid").glob("*.json"))
    invalid_files = sorted((FIXTURE_ROOT / "invalid").glob("*.json"))
    if not valid_files or not invalid_files:
        raise GateFailure("settings-control adapter fixtures are incomplete")
    for path in valid_files:
        errors = list(validator.iter_errors(load_json(path)))
        if errors:
            raise GateFailure(f"valid adapter fixture rejected: {path.name}: {errors[0].message}")
    for path in invalid_files:
        if not list(validator.iter_errors(load_json(path))):
            raise GateFailure(f"invalid adapter fixture accepted: {path.name}")
    return {
        "status": "passed",
        "schema": {"path": project_relative(ADAPTER_SCHEMA), "sha256": sha256_file(ADAPTER_SCHEMA)},
        "validAccepted": len(valid_files),
        "invalidRejected": len(invalid_files),
    }


def _secret_value_is_safe(value: Any) -> bool:
    if value is None or isinstance(value, (dict, list)):
        return True
    if isinstance(value, str):
        if value in SAFE_SECRET_VALUES:
            return True
        lowered = value.casefold()
        return lowered.startswith(("env:", "environment:", "credential-ref:", "secret-ref:"))
    return False


def scan_json_secrets(value: Any, path: str = "$") -> list[str]:
    findings: list[str] = []
    if isinstance(value, dict):
        for key, child in value.items():
            child_path = f"{path}.{key}"
            if SECRET_KEY.match(str(key)) and not _secret_value_is_safe(child):
                findings.append(f"secret-value:{child_path}")
            findings.extend(scan_json_secrets(child, child_path))
    elif isinstance(value, list):
        for index, child in enumerate(value):
            findings.extend(scan_json_secrets(child, f"{path}[{index}]") )
    elif isinstance(value, str):
        if SECRET_SENTINEL in value:
            findings.append(f"sentinel:{path}")
        for pattern in SECRET_TEXT:
            if pattern.search(value):
                findings.append(f"secret-pattern:{path}")
    return findings


def scan_file_secrets(path: Path) -> list[str]:
    if path.suffix.casefold() not in TEXT_SUFFIXES or path.stat().st_size > 8 * 1024 * 1024:
        return []
    text = path.read_text(encoding="utf-8-sig", errors="replace")
    findings: list[str] = []
    if SECRET_SENTINEL in text:
        findings.append("sentinel")
    for pattern in SECRET_TEXT:
        if pattern.search(text):
            findings.append("secret-pattern")
    if path.suffix.casefold() in {".json", ".jsonl"}:
        try:
            if path.suffix.casefold() == ".jsonl":
                for index, line in enumerate(text.splitlines()):
                    if line.strip():
                        findings.extend(f"line-{index + 1}:{item}" for item in scan_json_secrets(json.loads(line)))
            else:
                findings.extend(scan_json_secrets(json.loads(text)))
        except json.JSONDecodeError:
            findings.append("invalid-json-secret-scan")
    return sorted(set(findings))


def scan_paths_for_secrets(paths: Iterable[Path]) -> dict[str, Any]:
    findings: list[dict[str, str]] = []
    checked = 0
    unique: set[Path] = set()
    for root in paths:
        if root.is_dir():
            candidates = (path for path in root.rglob("*") if path.is_file())
        else:
            candidates = (root,)
        for path in candidates:
            resolved = path.resolve()
            if resolved in unique:
                continue
            unique.add(resolved)
            checked += 1
            for finding in scan_file_secrets(path):
                findings.append({"path": display_path(path), "finding": finding})
    return {"status": "passed" if not findings else "failed", "filesChecked": checked, "findingCount": len(findings), "findings": findings}


def scan_runtime_tree_for_secrets(root: Path) -> dict[str, Any]:
    candidates: list[Path] = []
    excluded_negative_fixtures: list[str] = []
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(root).as_posix()
        parts = [part.casefold() for part in Path(relative).parts]
        if "fixtures" in parts and "invalid" in parts:
            excluded_negative_fixtures.append(relative)
            continue
        candidates.append(path)
    result = scan_paths_for_secrets(candidates)
    result["excludedNegativeFixtureCount"] = len(excluded_negative_fixtures)
    result["excludedNegativeFixtures"] = sorted(excluded_negative_fixtures)
    return result


def verify_schema_fixture_set(value: dict[str, Any], label: str) -> dict[str, Any]:
    schema_path = resolve_file_ref(value["schema"], f"{label}.schema")
    schema = load_json(schema_path)
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema, format_checker=FormatChecker())
    for index, item in enumerate(value["valid"]):
        path = resolve_file_ref(item, f"{label}.valid[{index}]")
        errors = list(validator.iter_errors(load_json(path)))
        if errors:
            raise GateFailure(f"{label}: valid fixture rejected: {path.name}: {errors[0].message}")
    for index, item in enumerate(value["invalid"]):
        path = resolve_file_ref(item, f"{label}.invalid[{index}]")
        if not list(validator.iter_errors(load_json(path))):
            raise GateFailure(f"{label}: invalid fixture accepted: {path.name}")
    return {"validAccepted": len(value["valid"]), "invalidRejected": len(value["invalid"])}


def verify_snapshot_binding(binding: dict[str, Any]) -> dict[str, Any]:
    paths = {key: resolve_file_ref(binding[key], f"core.snapshotBinding.{key}") for key in (
        "settings", "taskSnapshot", "resolvedPlan", "workerRequest", "retryWorkerRequest",
        "activeTaskSnapshotAfterSettingsChange", "newTaskSnapshot",
    )}
    documents = {key: load_json(path) for key, path in paths.items()}
    values: list[dict[str, Any]] = []
    for index, mapping in enumerate(binding["mappings"]):
        expected = json_pointer(documents["settings"], mapping["settingPointer"])
        observed = {
            "settings": expected,
            "taskSnapshot": json_pointer(documents["taskSnapshot"], mapping["snapshotPointer"]),
            "resolvedPlanConfig": json_pointer(documents["resolvedPlan"], mapping["planConfigPointer"]),
            "resolvedPlanPayload": json_pointer(documents["resolvedPlan"], mapping["planPayloadPointer"]),
            "workerRequestConfig": json_pointer(documents["workerRequest"], mapping["requestConfigPointer"]),
            "workerRequestPayload": json_pointer(documents["workerRequest"], mapping["requestPayloadPointer"]),
            "retryWorkerRequestConfig": json_pointer(documents["retryWorkerRequest"], mapping["requestConfigPointer"]),
            "retryWorkerRequestPayload": json_pointer(documents["retryWorkerRequest"], mapping["requestPayloadPointer"]),
            "activeTaskAfterChange": json_pointer(documents["activeTaskSnapshotAfterSettingsChange"], mapping["snapshotPointer"]),
        }
        if any(value != expected for value in observed.values()):
            raise GateFailure(f"core mapping[{index}] drifted: {observed!r}")
        new_value = json_pointer(documents["newTaskSnapshot"], mapping["snapshotPointer"])
        if new_value == expected:
            raise GateFailure(f"core mapping[{index}] new task did not bind changed settings")
        values.append({"mapping": index, "originalValueSha256": canonical_json_sha256(expected), "newValueSha256": canonical_json_sha256(new_value)})
    if canonical_json_sha256(documents["taskSnapshot"]) != canonical_json_sha256(documents["activeTaskSnapshotAfterSettingsChange"]):
        raise GateFailure("active task snapshot changed after settings modification")
    return {"status": "passed", "mappingsChecked": len(values), "values": values, "referencedPaths": [project_relative(path) for path in paths.values()]}


def verify_owner_module(name: str, module: dict[str, Any]) -> dict[str, Any]:
    if module["status"] == "no-go":
        return {"status": "NO-GO-OWNER-REPORTED", "evidenceChecked": 0}
    referenced = [resolve_file_ref(item, label) for label, item in walk_file_refs(module, f"modules.{name}")]
    result: dict[str, Any] = {"status": "PASS", "evidenceChecked": len(referenced)}
    if name == "desktop":
        result["settingsFixtures"] = verify_schema_fixture_set(module["settingsSchema"], "desktop.settingsSchema")
        result["profileFixtures"] = verify_schema_fixture_set(module["profileSchema"], "desktop.profileSchema")
        observations = module["observations"]
        booleans = [value for key, value in observations.items() if key not in {"guiPages", "secretLeaks"}]
        booleans = [value for value in booleans if isinstance(value, bool)]
        automation_ids = observations["automationIds"]
        if len(automation_ids) != len(set(automation_ids)) or not all(value is True for value in booleans) or observations["secretLeaks"] != 0:
            raise GateFailure("desktop settings observations are not all passing")
    elif name == "core":
        result["snapshotChain"] = verify_snapshot_binding(module["snapshotBinding"])
        if not all(module["observations"].values()):
            raise GateFailure("core settings observations are not all passing")
    elif name == "remnote":
        clipping = module["clippingDateRoute"]
        cards = module["cardsDateRoute"]
        if clipping["expected"] is not True or cards["expected"] is not False:
            raise GateFailure("RemNote date-route expectations are not clipping=true/cards=false")
        clipping_doc = load_json(resolve_file_ref(clipping["artifact"], "remnote.clippingDateRoute"))
        cards_doc = load_json(resolve_file_ref(cards["artifact"], "remnote.cardsDateRoute"))
        if json_pointer(clipping_doc, clipping["pointer"]) is not True or json_pointer(cards_doc, cards["pointer"]) is not False:
            raise GateFailure("RemNote date-route artifact values do not strictly separate clipping and cards")
    elif name == "anki" and (module["probeMode"] != "read-only" or module["mutatingActions"] != 0):
        raise GateFailure("Anki probe is not read-only")
    elif name == "imageOcr" and module["realUploads"] != 0:
        raise GateFailure("OCR quality probe performed a real upload")
    elif name == "speech" and module["physicalSpeakerInvocations"] != 0:
        raise GateFailure("speech quality probe invoked the physical speaker")
    result["referencedPaths"] = [project_relative(path) for path in referenced]
    return result


def evidence(path: Path) -> dict[str, Any]:
    return {"path": project_relative(path), "bytes": path.stat().st_size, "sha256": sha256_file(path)}


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def verify_desktop_native() -> tuple[dict[str, Any], list[Path]]:
    require(sha256_file(DESKTOP_SETTINGS_RECEIPT) == DESKTOP_SETTINGS_RECEIPT_SHA256, "Desktop settings receipt hash mismatch")
    receipt = load_json(DESKTOP_SETTINGS_RECEIPT)
    require(receipt["status"] == "PASS-MODULE-USER-HAND-TEST-READY", "Desktop settings module status mismatch")
    require(receipt["productName"] == "零碎知识剪藏" and receipt["buildId"] == DESKTOP_SETTINGS_ROOT.name, "Desktop product/build identity mismatch")
    package = receipt["package"]
    actual_inventory = common.inventory(DESKTOP_SETTINGS_ROOT)
    common.assert_inventory(actual_inventory, package, "desktop settings candidate")
    require((actual_inventory["fileCount"], actual_inventory["totalBytes"], actual_inventory["treeSha256"]) == DESKTOP_SETTINGS_EXPECTED, "Desktop settings candidate expected inventory mismatch")
    entrypoint = PROJECT_ROOT / receipt["entrypoint"]
    require(entrypoint.is_file() and sha256_file(entrypoint) == str(receipt["entrypointSha256"]).upper(), "Desktop settings entrypoint mismatch")
    build_receipt = DESKTOP_SETTINGS_ROOT / "desktop-build-receipt.v1.json"
    require(sha256_file(build_receipt) == str(package["buildReceiptSha256"]).upper(), "Desktop build receipt mismatch")
    core = receipt["coreSettingsAuthority"]
    require(str(core["checkpointSha256"]).upper() == CORE_CHECKPOINT_SHA256 and core["boundFileCount"] == 21 and str(core["boundTreeSha256"]).upper() == "91513A5AEFC9AFA719B3C7E824A169976E59217F4DE2F2E4AD7490001E646763", "Desktop Core settings checkpoint binding mismatch")
    automated = receipt["automatedEvidence"]
    semantic_log = PROJECT_ROOT / automated["desktopSemantic"]["log"]
    require(sha256_file(semantic_log) == str(automated["desktopSemantic"]["sha256"]).upper(), "Desktop semantic log mismatch")
    semantic_text = semantic_log.read_text(encoding="utf-8-sig", errors="replace")
    require("neutralAutomationIds=29" in semantic_text, "Desktop automation ID catalog evidence missing")
    require(sha256_file(DESKTOP_WINDOWS_RECEIPT) == DESKTOP_WINDOWS_RECEIPT_SHA256, "Desktop Windows receipt hash mismatch")
    windows = load_json(DESKTOP_WINDOWS_RECEIPT)
    ui = windows["automatedWindowsUiCanary"]
    require(ui["status"] == "PASS" and ui["assertions"] == 117, "Desktop Windows UI is not 117 assertions PASS")
    windows_log = DESKTOP_WINDOWS_RECEIPT.parent / ui["log"]
    require(sha256_file(windows_log) == str(ui["logSha256"]).upper(), "Desktop Windows UI log mismatch")
    windows_text = windows_log.read_text(encoding="utf-8-sig", errors="replace")
    required_windows_assertions = (
        "credential controls are editable, masked, and isolated from workflow profile fields",
        "settings footer keeps both save and cancel controls visible",
        "settings save persists supported values for the next desktop operation",
        "settings cancel persists neither ordinary values nor credentials",
        "credentials never enter the immutable task package, manifest, or payload files",
        "stable submit/cancel automation IDs",
    )
    require(all(text in windows_text for text in required_windows_assertions), "Desktop settings UI assertion coverage missing")
    ctrl_c = windows["realWindowsCanaries"]["focusedTextBoxCtrlC"]
    require(ctrl_c["status"] == "PASS" and ctrl_c["clipboardRestored"] is True, "Desktop Ctrl+C/clipboard restoration failed")
    require(windows["desktopToRealRemNoteE2E"]["status"] == "NOT_RUN" and windows["desktopToRealRemNoteE2E"]["remNoteInvoked"] is False, "Desktop receipt overclaims real RemNote E2E")
    settings = receipt["settingsEvidence"]
    require(settings["tiers"] == ["常用", "进阶", "开发 / 诊断"], "Desktop settings tiers mismatch")
    require(len(settings["pages"]) == 7 and settings["backupAndCorruptFallback"] is True, "Desktop pages or backup/corrupt fallback evidence missing")
    require(settings["credentialsMaskedAndLocal"] is True and settings["credentialsExcludedFromTaskPackages"] is True and settings["secretReferencesEmpty"] is True, "Desktop credential isolation evidence failed")
    require(settings["manifestV1Preserved"] is True and settings["manifestV2Appended"] is True and settings["settingsSnapshotShaBound"] is True, "Desktop manifest/snapshot binding evidence failed")
    require(any("unsupported/read-only" in item for item in receipt["conditionalAndNoGo"]), "Desktop unsupported/read-only boundary missing")
    settings_form_source = PROJECT_ROOT / "src" / "ProjectSettingsForm.cs"
    settings_form_text = settings_form_source.read_text(encoding="utf-8-sig", errors="replace")
    require("apply on orchestrator restart; excluded from task packages and receipts" in settings_form_text, "Desktop restart/credential exclusion hint missing")
    safety = receipt["safety"]
    require(safety["externalProductionApisInvoked"] == 0 and safety["physicalSpeakerInvoked"] == 0 and not any(safety[key] for key in ("remNoteInvoked", "ankiInvoked", "mineruInvoked", "existingUserProcessTerminated")), "Desktop external side-effect boundary failed")
    regression_root = semantic_log.parent
    paths = [
        DESKTOP_SETTINGS_RECEIPT,
        build_receipt,
        entrypoint,
        semantic_log,
        DESKTOP_WINDOWS_RECEIPT,
        windows_log,
        regression_root / "windows-ui-canary-final.log",
        regression_root / "startup-diagnostic.json",
        regression_root / "brand-surface-canary.log",
        regression_root / "visual-preview-canary.log",
        settings_form_source,
    ]
    for path in paths:
        require(path.is_file(), f"Desktop evidence missing: {path}")
    require(sha256_file(regression_root / "windows-ui-canary-final.log") == str(automated["windowsUi"]["logSha256"]).upper(), "Desktop final Windows log mismatch")
    require(sha256_file(regression_root / "startup-diagnostic.json") == str(automated["startup"]["sha256"]).upper(), "Desktop startup evidence mismatch")
    require(sha256_file(regression_root / "brand-surface-canary.log") == str(automated["brand"]["sha256"]).upper(), "Desktop brand evidence mismatch")
    require(sha256_file(regression_root / "visual-preview-canary.log") == str(automated["visual"]["logSha256"]).upper(), "Desktop visual evidence mismatch")
    runtime_secret_scan = scan_runtime_tree_for_secrets(DESKTOP_SETTINGS_ROOT)
    require(runtime_secret_scan["findingCount"] == 0, "Desktop candidate runtime secret scan found leakage")
    return ({
        "status": "PASS-MODULE",
        "receipt": evidence(DESKTOP_SETTINGS_RECEIPT),
        "candidate": {key: actual_inventory[key] for key in ("fileCount", "totalBytes", "treeSha256")},
        "entrypoint": evidence(entrypoint),
        "windowsUi": {"receipt": evidence(DESKTOP_WINDOWS_RECEIPT), "assertions": 117},
        "settingsPages": settings["pages"],
        "settingsTiers": settings["tiers"],
        "automationIds": {"neutralCatalogCount": 29, "submitCancelStable": True},
        "saveCancel": "passed",
        "restartAndConditionalHints": "passed-and-source-hash-bound",
        "credentials": "masked-local-excluded-from-task-packages",
        "runtimeSecretScan": runtime_secret_scan,
        "externalProductionApisInvoked": 0,
    }, paths)


def verify_core_native() -> tuple[dict[str, Any], list[Path]]:
    require(sha256_file(CORE_CHECKPOINT) == CORE_CHECKPOINT_SHA256, "Core v2.4.1 checkpoint hash mismatch")
    checkpoint = load_json(CORE_CHECKPOINT)
    require(checkpoint.get("checkpointVersion") == "2.4.1", "Core checkpoint version mismatch")
    require(checkpoint.get("status") == "passed-with-conditional-runtime-dependency", "Core checkpoint status mismatch")
    tree = checkpoint["tree"]
    rows: list[str] = []
    total = 0
    current_source_drift: list[dict[str, Any]] = []
    paths: list[Path] = [CORE_CHECKPOINT]
    for index, item in enumerate(sorted(tree["files"], key=lambda value: value["path"])):
        path = (PROJECT_ROOT / item["path"]).resolve()
        require(path.is_relative_to(PROJECT_ROOT.resolve()) and path.is_file(), f"core.tree.files[{index}]: missing file")
        paths.append(path)
        actual_hash = sha256_file(path)
        if actual_hash != str(item["sha256"]).upper() or path.stat().st_size != int(item["bytes"]):
            current_source_drift.append({
                "path": item["path"],
                "checkpointSha256": str(item["sha256"]).upper(),
                "currentSha256": actual_hash,
                "checkpointBytes": int(item["bytes"]),
                "currentBytes": path.stat().st_size,
            })
        total += int(item["bytes"])
        rows.append(f"{item['path']}\0{str(item['sha256']).lower()}\0{int(item['bytes'])}\n")
    actual_tree = hashlib.sha256("".join(rows).encode("utf-8")).hexdigest().upper()
    require(len(tree["files"]) == tree["fileCount"] == 21, "Core settings tree file count mismatch")
    require(total == tree["totalBytes"], "Core settings tree byte count mismatch")
    require(actual_tree == str(tree["treeSha256"]).upper(), "Core settings tree hash mismatch")
    if current_source_drift:
        require(sha256_file(PRIOR_CORE_ADMISSION_RECEIPT) == PRIOR_CORE_ADMISSION_RECEIPT_SHA256, "prior Core admission receipt hash mismatch")
        prior = load_json(PRIOR_CORE_ADMISSION_RECEIPT)
        prior_core = prior["modules"]["core"]
        require(str(prior_core["status"]).startswith("PASS") and prior_core["checkpoint"]["sha256"] == CORE_CHECKPOINT_SHA256, "prior Core admission identity mismatch")
        require(prior_core["tree"] == {"fileCount": 21, "totalBytes": tree["totalBytes"], "treeSha256": actual_tree}, "prior Core admission tree mismatch")
        paths.append(PRIOR_CORE_ADMISSION_RECEIPT)
    canary_path = PROJECT_ROOT / checkpoint["validation"]["settingsFakeWorkerCanary"]["receipt"]
    require(sha256_file(canary_path) == CORE_RECEIPT_SHA256, "Core settings canary receipt hash mismatch")
    canary = load_json(canary_path)
    store = canary["storeEvidence"]
    flow = canary["flowEvidence"]
    route = canary["routeEvidence"]
    require(canary["status"] == "passed" and canary["externalCalls"] == 0 and canary["physicalSpeakerInvocations"] == 0, "Core settings canary side-effect/status mismatch")
    require(store["backupCreated"] is True and store["corruptFallback"] is True, "Core corrupt fallback/.bak evidence missing")
    require(all(flow[key] is True for key in ("stageConfigBound", "stagePayloadBound", "workerRequestBound", "activeTaskFrozen", "replayUsesOriginalSnapshot", "newTaskUsesNewSnapshot")), "Core snapshot-to-plan/request or active retry freeze evidence failed")
    learning = next(item for item in route["routes"] if item["name"] == "remnote-learning")
    cards = next(item for item in route["routes"] if item["name"] == "remnote-cards")
    require(learning["route"]["kind"] == "clip-daily-route" and cards["route"] == {"kind": "card-export"}, "Core RemNote learning/card date-route separation failed")
    require(route["learningAnki"] == "conditional-unsupported" and route["questionExport"] == "unsupported", "Core unsupported/conditional state drifted")
    return ({
        "status": "PASS-SCOPED-REMNOTE-V2-AUTHORITY-CONDITIONAL" + ("-SOURCE-ADVANCED-AFTER-CHECKPOINT" if current_source_drift else ""),
        "checkpoint": evidence(CORE_CHECKPOINT),
        "canaryReceipt": evidence(canary_path),
        "tree": {"fileCount": 21, "totalBytes": total, "treeSha256": actual_tree},
        "immutableAdmissionReceipt": evidence(PRIOR_CORE_ADMISSION_RECEIPT) if current_source_drift else None,
        "sourceAdvanceAfterCheckpoint": current_source_drift,
        "snapshotChain": {key: flow[key] for key in ("stageConfigBound", "stagePayloadBound", "workerRequestBound", "activeTaskFrozen", "replayUsesOriginalSnapshot", "newTaskUsesNewSnapshot")},
        "dateRouting": {"clipping": learning["route"], "cards": cards["route"]},
        "externalCalls": 0,
    }, paths)


def verify_content_native() -> tuple[dict[str, Any], list[Path]]:
    require(sha256_file(CONTENT_RECEIPT) == CONTENT_RECEIPT_SHA256, "Content settings receipt hash mismatch")
    receipt = load_json(CONTENT_RECEIPT)
    require(receipt["status"] == "PASS" and receipt["tests"] == {**receipt["tests"], "total": 65, "pass": 65, "fail": 0}, "Content settings tests are not 65/65")
    require(receipt["externalConnections"] == 0 and receipt["credentialsRecorded"] is False, "Content external/credential boundary failed")
    paths = [CONTENT_RECEIPT]
    for label, item in (
        ("content.tests.log", receipt["tests"]["log"]),
        ("content.schemas.log", receipt["schemas"]["log"]),
        ("content.descriptor", receipt["descriptor"]),
        ("content.settingsDescriptor", receipt["settingsDescriptor"]),
        ("content.matrixCanary", receipt["matrixCanary"]),
    ):
        paths.append(resolve_native_ref(item, label))
    content_root = PROJECT_ROOT / "content-worker"
    current_source_drift: list[dict[str, Any]] = []
    for index, item in enumerate(receipt["sourceEvidence"]):
        path = (content_root / item["path"]).resolve()
        require(path.is_relative_to(content_root.resolve()) and path.is_file(), f"content.sourceEvidence[{index}]: missing file")
        paths.append(path)
        actual_hash = sha256_file(path)
        actual_bytes = path.stat().st_size
        if actual_hash != str(item["sha256"]).upper() or actual_bytes != int(item["bytes"]):
            current_source_drift.append({
                "path": f"content-worker/{item['path']}",
                "receiptSha256": str(item["sha256"]).upper(),
                "currentSha256": actual_hash,
                "receiptBytes": int(item["bytes"]),
                "currentBytes": actual_bytes,
            })
    if current_source_drift:
        require(sha256_file(PRIOR_CORE_ADMISSION_RECEIPT) == PRIOR_CORE_ADMISSION_RECEIPT_SHA256, "prior Content admission receipt hash mismatch")
        prior = load_json(PRIOR_CORE_ADMISSION_RECEIPT)
        prior_content = prior["modules"]["content"]
        require(str(prior_content["status"]).startswith("PASS"), "prior Content admission status mismatch")
        require(prior_content["receipt"]["sha256"] == CONTENT_RECEIPT_SHA256, "prior Content admission identity mismatch")
        paths.append(PRIOR_CORE_ADMISSION_RECEIPT)
    return ({
        "status": "PASS-SCOPED-CORE-BINDING-CONDITIONAL" + ("-SOURCE-ADVANCED-AFTER-RECEIPT" if current_source_drift else ""),
        "receipt": evidence(CONTENT_RECEIPT),
        "immutableAdmissionReceipt": evidence(PRIOR_CORE_ADMISSION_RECEIPT) if current_source_drift else None,
        "sourceAdvanceAfterReceipt": current_source_drift,
        "tests": "65/65",
        "schemas": "2/2",
        "externalConnections": 0,
        "credentialsRecorded": False,
        "remainingBoundary": receipt["remainingIntegrationBoundary"],
    }, paths)


def verify_remnote_native() -> tuple[dict[str, Any], list[Path]]:
    require(sha256_file(REMNOTE_RECEIPT) == REMNOTE_RECEIPT_SHA256, "RemNote v2 offline receipt hash mismatch")
    receipt = load_json(REMNOTE_RECEIPT)
    require(receipt["status"] == "passed" and receipt["realRemNoteCalled"] is False, "RemNote offline status/external-call mismatch")
    require(receipt["worker"]["workerVersion"] == "2.0.0", "RemNote v2 worker identity mismatch")
    mapping = receipt["workerRequestMapping"]
    require(mapping["clipRouteFields"] == ["payload.route.kind", "payload.route.timeZone", "payload.route.localDate", "payload.route.dateFormat"], "RemNote clipping date mapping drifted")
    require(mapping["cardRouteFields"] == ["payload.route.kind"] and mapping["configSnapshotDateRoutingFields"] == [], "RemNote card route contains date fields")
    require("card-no-date" in receipt["tests"]["fakeCoverage"], "RemNote card-no-date fixture missing")
    require(receipt["frozenV1"]["filesChecked"] == 21 and receipt["frozenV1"]["mismatchCount"] == 0, "RemNote frozen v1 evidence drifted")
    paths = [REMNOTE_RECEIPT]
    for group in ("candidateFiles", "evidenceFiles"):
        for index, item in enumerate(receipt[group]):
            paths.append(resolve_native_ref(item, f"remnote.{group}[{index}]"))
    return ({
        "status": "PASS-SCOPED-AUTHORITY-CONDITIONAL",
        "receipt": evidence(REMNOTE_RECEIPT),
        "workerVersion": "2.0.0",
        "realRemNoteCalled": False,
        "clippingDateRoute": "payload.route.kind+timeZone+localDate+dateFormat",
        "cardsDateRoute": "payload.route.kind-only",
        "authorityAdmission": "NO-GO-pending-new-authority-checkpoint",
        "remainingBoundary": receipt["remainingBoundary"],
    }, paths)


def verify_speech_native() -> tuple[dict[str, Any], list[Path]]:
    require(sha256_file(SPEECH_RECEIPT) == SPEECH_RECEIPT_SHA256, "Speech unit receipt hash mismatch")
    receipt = load_json(SPEECH_RECEIPT)
    tests = receipt["tests"]
    require(receipt["status"] == "passed" and receipt["failures"] == 0 and len(tests) == 42 and all(item["status"] == "passed" for item in tests), "Speech settings suite is not 42/42")
    names = {item["name"] for item in tests}
    require("settings adapter freezes five-language request and runtime snapshots" in names and "settings adapt CLI emits synthesis and playback worker snapshots" in names, "Speech settings adapter coverage missing")
    return ({"status": "PASS-SCOPED-OFFLINE", "receipt": evidence(SPEECH_RECEIPT), "tests": "42/42", "physicalSpeakerInvocationsByQualityRun": 0}, [SPEECH_RECEIPT])


def verify_image_native(safe_test: dict[str, Any] | None) -> tuple[dict[str, Any], list[Path]]:
    require(sha256_file(IMAGE_BUILD_RECEIPT) == IMAGE_BUILD_RECEIPT_SHA256, "Image settings build receipt hash mismatch")
    receipt = load_json(IMAGE_BUILD_RECEIPT)
    candidate = PROJECT_ROOT / receipt["candidatePath"]
    require(candidate.is_file() and candidate.stat().st_size == receipt["bytes"] and sha256_file(candidate) == str(receipt["sha256"]).upper(), "Image settings candidate mismatch")
    require(receipt["externalServiceCalled"] is False and receipt["registryOnboarding"].startswith("NO-GO"), "Image external/authority boundary failed")
    require(safe_test is not None and safe_test["status"] == "passed" and safe_test["tests"] == 48, "Image safe test refresh is not 48/48")
    paths = [IMAGE_BUILD_RECEIPT, candidate, Path(safe_test["logAbsolutePath"])]
    for path in (
        PROJECT_ROOT / "workers/image-ocr/schemas/image-ocr-settings.v1.schema.json",
        PROJECT_ROOT / "workers/image-ocr/schemas/image-ocr-settings-adapter-result.v1.schema.json",
        PROJECT_ROOT / "workers/image-ocr/fixtures/settings-original.v1.json",
        PROJECT_ROOT / "workers/image-ocr/fixtures/settings-ocr.v1.json",
    ):
        require(path.is_file(), f"Image settings evidence missing: {path}")
        paths.append(path)
    return ({"status": "PASS-SCOPED-NO-REAL-UPLOAD", "buildReceipt": evidence(IMAGE_BUILD_RECEIPT), "tests": "48/48", "testLog": safe_test["log"], "realUploads": 0, "authorityAdmission": receipt["registryOnboarding"]}, paths)


def verify_anki_native(safe_test: dict[str, Any] | None) -> tuple[dict[str, Any], list[Path]]:
    require(sha256_file(ANKI_RECEIPT) == ANKI_RECEIPT_SHA256, "Anki settings probe receipt hash mismatch")
    receipt = load_json(ANKI_RECEIPT)
    checks = receipt["checks"]
    require(receipt["status"] == "passed" and checks["probeReadOnly"] is True and checks["realAnkiConnectCalls"] == 0, "Anki probe is not read-only/offline")
    require(checks["fakeMutatingActions"] == [] and checks["fakeNotes"] == 0 and checks["fakeMedia"] == 0, "Anki probe mutated fake state")
    require(checks["settingsToShaBoundConfigArtifact"] is True and checks["enabledOnlyAffectsNewTaskDestinations"] is True, "Anki settings snapshot binding failed")
    require(safe_test is not None and safe_test["status"] == "passed" and safe_test["tests"] == 43, "Anki safe test refresh is not 43/43")
    paths = [ANKI_RECEIPT, Path(safe_test["logAbsolutePath"])]
    for group in ("inputs", "outputs"):
        for label, item in receipt[group].items():
            paths.append(resolve_native_ref(item, f"anki.{group}.{label}"))
    return ({"status": "PASS-SCOPED-READ-ONLY", "receipt": evidence(ANKI_RECEIPT), "tests": "43/43", "testLog": safe_test["log"], "realAnkiConnectCalls": 0, "mutatingActions": 0}, paths)


def run_safe_module_tests(output_root: Path) -> dict[str, Any]:
    test_root = output_root / "module-tests"
    test_root.mkdir(parents=True, exist_ok=False)
    cases = {
        "imageOcr": (PROJECT_ROOT / "workers" / "image-ocr", 48),
        "anki": (PROJECT_ROOT / "workers" / "anki-exporter", 43),
    }
    results: dict[str, Any] = {}
    for name, (cwd, expected) in cases.items():
        completed = subprocess.run(
            [sys.executable, "-B", "-m", "unittest", "discover", "-s", "tests", "-v"],
            cwd=cwd,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=900,
            check=False,
        )
        log_path = test_root / f"{name}.log"
        log_text = f"COMMAND: {sys.executable} -B -m unittest discover -s tests -v\nCWD: {cwd}\nEXIT: {completed.returncode}\n\nSTDOUT\n{completed.stdout}\nSTDERR\n{completed.stderr}"
        log_path.write_text(log_text, encoding="utf-8")
        match = re.search(r"Ran\s+(\d+)\s+tests?", completed.stdout + completed.stderr)
        count = int(match.group(1)) if match else -1
        status = "passed" if completed.returncode == 0 and count == expected else "failed"
        results[name] = {"status": status, "tests": count, "expected": expected, "log": evidence(log_path), "logAbsolutePath": str(log_path.resolve())}
    return results


def verify_native_owner_evidence(safe_tests: dict[str, Any] | None) -> tuple[dict[str, Any], dict[str, Any], list[Path]]:
    modules: dict[str, Any] = {}
    paths: list[Path] = []
    verifiers = {
        "desktop": lambda: verify_desktop_native(),
        "core": lambda: verify_core_native(),
        "content": lambda: verify_content_native(),
        "remnote": lambda: verify_remnote_native(),
        "speech": lambda: verify_speech_native(),
        "imageOcr": lambda: verify_image_native((safe_tests or {}).get("imageOcr")),
        "anki": lambda: verify_anki_native((safe_tests or {}).get("anki")),
    }
    for name, verifier in verifiers.items():
        try:
            result, module_paths = verifier()
            modules[name] = result
            paths.extend(module_paths)
        except (GateFailure, OSError, json.JSONDecodeError) as exc:
            modules[name] = {"status": "NO-GO-QUALITY-GATE", "reason": str(exc)}
    owner = {
        "status": "native-owner-evidence-loaded",
        "coreCheckpoint": evidence(CORE_CHECKPOINT),
        "desktop": modules.get("desktop", {}).get("status", "NO-GO-OWNER-EVIDENCE-MISSING"),
        "remnoteAuthority": "conditional-pending-new-authority-checkpoint",
    }
    return owner, modules, paths


def verify_immutable_baselines() -> dict[str, Any]:
    stable = quality_gates.verify_stable_checkpoint(PROJECT_ROOT)
    if stable["filesChecked"] != 21 or stable["mismatches"] != 0:
        raise GateFailure("frozen v1 checkpoint drifted")
    if sha256_file(v124.AUTHORITY_CHECKPOINT) != v124.AUTHORITY_CHECKPOINT_SHA256:
        raise GateFailure("authority v1.2.4 checkpoint drifted")
    authority = common.inventory(v124.AUTHORITY_ROOT)
    if (authority["fileCount"], authority["totalBytes"], authority["treeSha256"]) != v124.EXPECTED_AUTHORITY:
        raise GateFailure("authority v1.2.4 release tree drifted")
    candidate = common.inventory(v124.CANDIDATE_ROOT)
    if (candidate["fileCount"], candidate["totalBytes"], candidate["treeSha256"]) != v124.EXPECTED_CANDIDATE:
        raise GateFailure("integrated v1.2.4 candidate drifted")
    if sha256_file(v124.DESKTOP_RECEIPT) != v124.DESKTOP_RECEIPT_SHA256:
        raise GateFailure("integrated v1.2.4 desktop receipt drifted")
    return {
        "status": "passed",
        "frozenV1": stable,
        "authorityV124": {"checkpointSha256": v124.AUTHORITY_CHECKPOINT_SHA256, **{key: authority[key] for key in ("fileCount", "totalBytes", "treeSha256")}},
        "integratedCandidateV124": {"desktopReceiptSha256": v124.DESKTOP_RECEIPT_SHA256, **{key: candidate[key] for key in ("fileCount", "totalBytes", "treeSha256")}},
    }


def evaluate(
    owner_evidence: Path = DEFAULT_OWNER_EVIDENCE,
    *,
    native_owners: bool = False,
    safe_tests: dict[str, Any] | None = None,
) -> dict[str, Any]:
    adapter = validate_fixture_contract()
    baselines = verify_immutable_baselines()
    candidate_secret_scan = scan_paths_for_secrets([v124.CANDIDATE_ROOT])
    if candidate_secret_scan["findingCount"]:
        raise GateFailure("secret scan found candidate leakage")
    module_names = ("desktop", "core", "content", "remnote", "anki", "imageOcr", "speech")
    owner_summary: dict[str, Any]
    if native_owners:
        owner_summary, modules, referenced_paths = verify_native_owner_evidence(safe_tests)
        persisted_paths = [
            path for path in referenced_paths
            if path.suffix.casefold() in TEXT_SUFFIXES
            and not any(part.casefold() in {"schemas", "fixtures", "src", "docs"} for part in path.parts)
        ]
        evidence_secret_scan = scan_paths_for_secrets(persisted_paths)
        owner_summary["secretScan"] = evidence_secret_scan
        if evidence_secret_scan["findingCount"]:
            modules["secretPersistence"] = {"status": "NO-GO-SECRET-LEAK", **evidence_secret_scan}
    elif not owner_evidence.is_file():
        modules = {name: {"status": "NO-GO-OWNER-EVIDENCE-MISSING"} for name in module_names}
        owner_summary = {"status": "missing", "expectedPath": display_path(owner_evidence)}
    else:
        schema = load_json(ADAPTER_SCHEMA)
        evidence_document = load_json(owner_evidence)
        errors = sorted(Draft202012Validator(schema, format_checker=FormatChecker()).iter_errors(evidence_document), key=lambda item: list(item.path))
        if errors:
            raise GateFailure(f"owner evidence adapter mismatch: {errors[0].message}")
        modules = {}
        for name in module_names:
            try:
                modules[name] = verify_owner_module(name, evidence_document["modules"][name])
            except GateFailure as exc:
                modules[name] = {"status": "NO-GO-QUALITY-GATE", "reason": str(exc)}
        referenced_paths: list[Path] = []
        for name in module_names:
            if evidence_document["modules"][name]["status"] != "passed":
                continue
            for label, item in walk_file_refs(evidence_document["modules"][name], f"modules.{name}"):
                try:
                    referenced_paths.append(resolve_file_ref(item, label))
                except GateFailure:
                    # The module-specific result already records this as NO-GO. Do not
                    # collapse independent module admission into a tool-level failure.
                    continue
        evidence_secret_scan = scan_paths_for_secrets([owner_evidence, *referenced_paths])
        if evidence_secret_scan["findingCount"]:
            modules["secretPersistence"] = {"status": "NO-GO-SECRET-LEAK", **evidence_secret_scan}
        owner_summary = {"status": "loaded", "path": project_relative(owner_evidence), "sha256": sha256_file(owner_evidence), "secretScan": evidence_secret_scan}
    all_pass = all(str(item.get("status", "")).startswith("PASS") for item in modules.values())
    desktop_ready = str(modules.get("desktop", {}).get("status", "")).startswith("PASS")
    remnote_authority_ready = modules.get("remnote", {}).get("authorityAdmission") != "NO-GO-pending-new-authority-checkpoint"
    if all_pass and desktop_ready and remnote_authority_ready:
        overall_status = "PASS"
    elif all_pass and desktop_ready and not remnote_authority_ready:
        overall_status = "NO-GO-REMNOTE-V2-AUTHORITY"
    elif native_owners and not desktop_ready:
        overall_status = "NO-GO-DESKTOP-EVIDENCE-AND-REMNOTE-V2-AUTHORITY"
    elif native_owners:
        overall_status = "NO-GO-OWNER-EVIDENCE-QUALITY-GATE"
    else:
        overall_status = "NO-GO-AWAITING-OWNER-EVIDENCE"
    implementation_paths = [
        Path(__file__).resolve(),
        ADAPTER_SCHEMA,
        PROJECT_ROOT / "tests" / "Quality.Gates" / "test_settings_control_plane_quality.py",
        *sorted((FIXTURE_ROOT / "valid").glob("*.json")),
        *sorted((FIXTURE_ROOT / "invalid").glob("*.json")),
    ]
    return {
        "schemaVersion": "knowledge-clipper-settings-control-quality-receipt.v1",
        "generatedAt": utc_now(),
        "status": overall_status,
        "scope": "quality-fixtures-and-owner-evidence-admission-only",
        "qualityInfrastructure": {
            **adapter,
            "implementation": [
                {"path": project_relative(path), "bytes": path.stat().st_size, "sha256": sha256_file(path)}
                for path in implementation_paths
            ],
        },
        "ownerEvidence": owner_summary,
        "modules": modules,
        "secretScan": candidate_secret_scan,
        "immutableBaselines": baselines,
        "sideEffects": {"externalServiceCalls": 0, "productionWorkerStarts": 0, "remnoteWrites": 0, "ankiWrites": 0, "ocrRealUploads": 0, "physicalSpeakerInvocations": 0},
        "release": {"sealed": False, "candidateModified": False, "authorityModified": False, "frozenV1Modified": False},
    }


def render_readable(receipt: dict[str, Any]) -> str:
    rows = "\n".join(f"| {name} | {item['status']} |" for name, item in receipt["modules"].items())
    baselines = receipt["immutableBaselines"]
    desktop_status = receipt["modules"].get("desktop", {}).get("status", "missing")
    boundary = (
        "Desktop 设置控制面模块证据已接纳；当前唯一 remaining NO-GO 是 RemNote exporter 2.0 尚未进入新的 authority checkpoint 与 integrated candidate。"
        if str(desktop_status).startswith("PASS")
        else "Desktop 最终 GUI 三级页、automationId、保存/取消与重启提示证据仍待 owner 回执；RemNote exporter 2.0 也尚未进入新的 authority checkpoint。"
    )
    return f"""# 零碎知识剪藏：设置控制面精简质量门禁

- 总状态：**{receipt['status']}**
- 范围：仅质量 schema、fixtures、证据 adapter、只读验证与只追加回执；未改产品行为。
- Owner 证据：{receipt['ownerEvidence']['status']}
- 外部调用/写入：0；OCR 真实上传：0；物理扬声器：0。
- 候选 secret scan：{receipt['secretScan']['filesChecked']} files，{receipt['secretScan']['findingCount']} findings。

| 模块 | 独立结果 |
|---|---|
{rows}

## 不可变基线

- frozen contracts/v1：{baselines['frozenV1']['filesChecked']} files / {baselines['frozenV1']['mismatches']} mismatch。
- authority v1.2.4：{baselines['authorityV124']['fileCount']} files / `{baselines['authorityV124']['treeSha256']}`。
- integrated v1.2.4：{baselines['integratedCandidateV124']['fileCount']} files / `{baselines['integratedCandidateV124']['treeSha256']}`。

## 当前 NO-GO 边界

{boundary} 不得把模块 PASS 包装成 integrated PASS 或发布 GO。
"""


def write_append_only(receipt: dict[str, Any], output_root: Path) -> dict[str, Any]:
    if output_root.exists() and any((output_root / name).exists() for name in ("quality-receipt.json", "readable-index.md")):
        raise GateFailure(f"append-only quality artifacts already exist: {output_root}")
    output_root.mkdir(parents=True, exist_ok=True)
    receipt_path = output_root / "quality-receipt.json"
    index_path = output_root / "readable-index.md"
    receipt_path.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    index_path.write_text(render_readable(receipt), encoding="utf-8")
    return {
        "root": project_relative(output_root),
        "receipt": {"path": project_relative(receipt_path), "bytes": receipt_path.stat().st_size, "sha256": sha256_file(receipt_path)},
        "readableIndex": {"path": project_relative(index_path), "bytes": index_path.stat().st_size, "sha256": sha256_file(index_path)},
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify the trimmed KnowledgeClipper settings control plane evidence.")
    parser.add_argument("--owner-evidence", type=Path, default=DEFAULT_OWNER_EVIDENCE)
    parser.add_argument("--emit-root", type=Path)
    parser.add_argument("--native-owners", action="store_true")
    parser.add_argument("--refresh-safe-tests", action="store_true")
    args = parser.parse_args()
    try:
        owner = args.owner_evidence if args.owner_evidence.is_absolute() else PROJECT_ROOT / args.owner_evidence
        root = args.emit_root if args.emit_root and args.emit_root.is_absolute() else (PROJECT_ROOT / args.emit_root if args.emit_root else None)
        if args.refresh_safe_tests and (not args.native_owners or root is None):
            raise GateFailure("--refresh-safe-tests requires --native-owners and --emit-root")
        if root is not None and root.exists():
            raise GateFailure(f"append-only output root already exists: {root}")
        safe_tests = run_safe_module_tests(root) if args.refresh_safe_tests and root is not None else None
        receipt = evaluate(owner, native_owners=args.native_owners, safe_tests=safe_tests)
        if safe_tests is not None:
            receipt["safeModuleTestRefresh"] = {
                name: {key: value for key, value in result.items() if key != "logAbsolutePath"}
                for name, result in safe_tests.items()
            }
        output: dict[str, Any] = {"result": receipt}
        if root is not None:
            output["artifacts"] = write_append_only(receipt, root)
            after = verify_immutable_baselines()
            if after != receipt["immutableBaselines"]:
                raise GateFailure("immutable baselines changed while emitting quality receipt")
        print(json.dumps(output, ensure_ascii=False, indent=2))
        return 0
    except (GateFailure, quality_gates.GateFailure, common.GateFailure) as exc:
        print(json.dumps({"status": "failed", "error": str(exc)}, ensure_ascii=False), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
