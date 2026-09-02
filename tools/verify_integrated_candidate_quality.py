from __future__ import annotations

import argparse
import hashlib
import json
import os
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable


PROJECT_ROOT = Path(__file__).resolve().parents[1]
CANDIDATE_ID = "knowledge-clipper-integrated-v1.2.2-20260828T015302Z"
CANDIDATE_ROOT = PROJECT_ROOT / "releases" / CANDIDATE_ID / "win-x64"
DESKTOP_RECEIPT = (
    PROJECT_ROOT
    / "receipts"
    / "desktop-integrated-candidate-knowledge-clipper-integrated-v1.2.2-20260828T015302Z.json"
)
DESKTOP_RECEIPT_SHA256 = "D82BF27FA13A060A2729587A1B5EADC0D86C992D36218211EAA16148E7D83C2E"
DESKTOP_EVIDENCE_ROOT = PROJECT_ROOT / "canary" / "desktop-integrated" / CANDIDATE_ID
CANDIDATE_V123_ID = "knowledge-clipper-integrated-v1.2.3-20260828T022432Z"
CANDIDATE_V123_ROOT = PROJECT_ROOT / "releases" / CANDIDATE_V123_ID / "win-x64"
DESKTOP_V123_RECEIPT = (
    PROJECT_ROOT
    / "receipts"
    / "desktop-integrated-candidate-knowledge-clipper-integrated-v1.2.3-20260828T022432Z.json"
)
DESKTOP_V123_RECEIPT_SHA256 = "B04C8A6BE0FE5E905C5BD978598A9C558818E79FD1CB224683CCC583E21A07A6"
DESKTOP_V123_EVIDENCE_ROOT = PROJECT_ROOT / "canary" / "desktop-integrated" / CANDIDATE_V123_ID
AUTHORITY_SOURCE = (
    PROJECT_ROOT / "releases" / "knowledge-clipper-authority-v1.2.2-20260828T012500Z" / "win-x64"
)
AUTHORITY_CHECKPOINT = (
    PROJECT_ROOT / "contracts" / "authority" / "worker-registry" / "v1.2.2-20260828T012500Z" / "checkpoint.json"
)
AUTHORITY_CHECKPOINT_SHA256 = "7B2EFE4D6A144494B0C18F0B84150906BB0F229374DD82F6BB83C6136A2DBFA5"
AUTHORITY_TREE_SHA256 = "F15F21E5489B4002086B0664144606A7E2EF5BD40E1AA02AB90F34EC277C2503"
AUTHORITY_V123_SOURCE = (
    PROJECT_ROOT / "releases" / "knowledge-clipper-authority-v1.2.3-20260828T021300Z" / "win-x64"
)
AUTHORITY_V123_CHECKPOINT = (
    PROJECT_ROOT / "contracts" / "authority" / "worker-registry" / "v1.2.3-20260828T021300Z" / "checkpoint.json"
)
AUTHORITY_V123_CHECKPOINT_SHA256 = "3EFB89D6D642F8546ABD14C5A114DE00E14BB7173EF33F03D3FD06DDAA2ADB55"
AUTHORITY_V123_REGISTRY_SHA256 = "B695939D797DDF7924D048B3380885ECF06AB926A807E9381D531855363F885B"
AUTHORITY_V123_TREE_SHA256 = "7B27BACDB1DBB56991272972A5F9309B1D6F782787EB0B99EBD21248F7959574"
AUTHORITY_V123_RECEIPT = PROJECT_ROOT / "receipts" / "knowledge-clipper-authority-1.2.3-20260828T021600Z.json"
OCR_WRAPPER = (
    PROJECT_ROOT / "receipts" / "knowledge-clipper-authority-v122-image-content-auth-v122-20260828-095848-ea4aaaf4.json"
)
ANKI_BLOCKER = (
    PROJECT_ROOT
    / "workers"
    / "anki-exporter"
    / "canary"
    / "authority-v1.2.2-blocker"
    / "20260828T020731Z-d5d03873"
    / "receipt.json"
)
ANKI_V123_RECEIPT = (
    PROJECT_ROOT
    / "workers"
    / "anki-exporter"
    / "canary"
    / "authority-v1.2.3"
    / "20260828T022032Z-1f87f554"
    / "receipt.json"
)
ANKI_V123_RECEIPT_SHA256 = "D10AB2E32F7D0424015293F64377FE5615FC2FE02CC3D3D5AFFC8B965B3250EC"


class GateFailure(RuntimeError):
    pass


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def windows_path(path: Path) -> Path:
    resolved = path.resolve()
    if os.name == "nt" and not str(resolved).startswith("\\\\?\\"):
        return Path("\\\\?\\" + str(resolved))
    return resolved


def sha256_file(path: Path) -> str:
    if not windows_path(path).is_file():
        raise GateFailure(f"missing evidence: {path}")
    digest = hashlib.sha256()
    with windows_path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load_json(path: Path) -> dict[str, Any]:
    try:
        return json.loads(windows_path(path).read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        raise GateFailure(f"invalid JSON evidence: {path}: {exc}") from exc


def project_relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT_ROOT.resolve()).as_posix()


def inventory(root: Path, excluded: set[str] | None = None) -> dict[str, Any]:
    excluded = excluded or set()
    records: list[dict[str, Any]] = []
    rows: list[str] = []
    total = 0
    resolved = root.resolve()
    files = sorted(
        (Path(str(item).removeprefix("\\\\?\\")) for item in windows_path(root).rglob("*") if item.is_file()),
        key=lambda item: item.relative_to(resolved).as_posix(),
    )
    for path in files:
        relative = path.relative_to(resolved).as_posix()
        if relative in excluded:
            continue
        size = windows_path(path).stat().st_size
        digest = sha256_file(path)
        records.append({"path": relative, "bytes": size, "sha256": digest})
        rows.append(f"{relative}\t{size}\t{digest.lower()}")
        total += size
    return {
        "files": records,
        "fileCount": len(records),
        "totalBytes": total,
        "treeSha256": hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper(),
        "treeAlgorithm": "sha256(utf8 ordinal(relativePath + TAB + bytes + TAB + lowercaseSha256) joined by LF)",
    }


def assert_equal(actual: Any, expected: Any, label: str) -> None:
    if actual != expected:
        raise GateFailure(f"{label}: expected {expected!r}, got {actual!r}")


def assert_inventory(actual: dict[str, Any], expected: dict[str, Any], label: str) -> None:
    for key in ("fileCount", "totalBytes", "treeSha256"):
        left = actual[key]
        right = expected[key]
        if key == "treeSha256":
            left, right = str(left).upper(), str(right).upper()
        assert_equal(left, right, f"{label}.{key}")
    expected_files = {item["path"]: item for item in expected.get("files", [])}
    actual_files = {item["path"]: item for item in actual.get("files", [])}
    assert_equal(set(actual_files), set(expected_files), f"{label}.paths")
    for relative, item in actual_files.items():
        wanted = expected_files[relative]
        assert_equal(item["bytes"], wanted["bytes"], f"{label}.{relative}.bytes")
        assert_equal(item["sha256"].upper(), str(wanted["sha256"]).upper(), f"{label}.{relative}.sha256")


def resolve_reference(path_text: str, base: Path) -> Path:
    relative = Path(path_text)
    if relative.is_absolute():
        raise GateFailure(f"absolute evidence reference is forbidden: {path_text}")
    result = (base / relative).resolve()
    if not result.is_relative_to(base.resolve()):
        raise GateFailure(f"evidence reference escapes its base: {path_text}")
    return result


def verify_reference(item: dict[str, Any], base: Path, label: str) -> Path:
    path = resolve_reference(str(item["path"]), base)
    digest = sha256_file(path)
    assert_equal(digest, str(item["sha256"]).upper(), f"{label}.sha256")
    if "bytes" in item:
        assert_equal(windows_path(path).stat().st_size, int(item["bytes"]), f"{label}.bytes")
    return path


def verify_log(root: Path, item: dict[str, Any], label: str) -> Path:
    path = resolve_reference(str(item["log"]), root)
    assert_equal(sha256_file(path), str(item["sha256"]).upper(), f"{label}.sha256")
    return path


def verify_desktop() -> dict[str, Any]:
    assert_equal(sha256_file(DESKTOP_RECEIPT), DESKTOP_RECEIPT_SHA256, "desktop receipt")
    receipt = load_json(DESKTOP_RECEIPT)
    manifest_path = CANDIDATE_ROOT / "candidate-file-manifest.v1.json"
    manifest = load_json(manifest_path)
    candidate_before = inventory(CANDIDATE_ROOT)
    assert_inventory(candidate_before, receipt["candidate"], "candidate")
    assert_equal(candidate_before["fileCount"], 332, "candidate file count")
    assert_equal(candidate_before["totalBytes"], 400644750, "candidate bytes")
    assert_equal(candidate_before["treeSha256"], "FA2045720A10FCCE389BF9755B92F55FD60664BD08DBE9DAF33931C1A1979CD5", "candidate tree")
    assert_equal(sha256_file(manifest_path), receipt["candidate"]["manifestSha256"].upper(), "candidate manifest")
    assert_equal(manifest["excludedControlFiles"], ["candidate-file-manifest.v1.json"], "manifest control exclusion")
    assert_inventory(inventory(CANDIDATE_ROOT, set(manifest["excludedControlFiles"])), manifest["payload"], "manifest payload")

    authority_source = inventory(AUTHORITY_SOURCE)
    authority_candidate = inventory(CANDIDATE_ROOT / "authority" / "win-x64")
    assert_inventory(authority_candidate, authority_source, "authority copy")
    assert_equal(authority_source["fileCount"], 79, "authority files")
    assert_equal(authority_source["totalBytes"], 248977927, "authority bytes")
    assert_equal(authority_source["treeSha256"], AUTHORITY_TREE_SHA256, "authority tree")
    assert_equal(sha256_file(AUTHORITY_CHECKPOINT), AUTHORITY_CHECKPOINT_SHA256, "authority checkpoint")

    startup_path = verify_log(DESKTOP_EVIDENCE_ROOT, receipt["automatedCanaries"]["safeStartup"], "safe startup log")
    single_path = verify_log(DESKTOP_EVIDENCE_ROOT, receipt["automatedCanaries"]["existingInstance"], "single instance log")
    integration_path = verify_log(DESKTOP_EVIDENCE_ROOT, receipt["automatedCanaries"]["builtInOrchestrator"], "integration log")
    for key in ("authorityRegistry", "desktopContracts", "desktopIntegration", "visual"):
        verify_log(DESKTOP_EVIDENCE_ROOT, receipt["automatedCanaries"][key], f"{key} log")
    startup = load_json(startup_path)
    single = load_json(single_path)
    integration = load_json(integration_path)
    assert_equal(startup["result"], "PASS", "startup result")
    assert_equal(Path(startup["baseDirectory"]).resolve(), CANDIDATE_ROOT.resolve(), "startup candidate root")
    assert_equal(startup["candidateOrchestratorPathsVerified"], True, "startup paths")
    assert_equal(startup["productionWorkDirectoryCreated"], False, "startup hidden production write")
    assert_equal((startup["namedPipeInvoked"], startup["externalConnections"]), (False, 0), "safe startup side effects")
    assert_equal(single["existingInstanceDetected"], True, "single instance detection")
    assert_equal(single["userFeedbackMode"], "activate-existing-window-and-message-box", "single instance feedback")
    assert_equal(integration["result"], "PASS", "orchestrator integration")
    assert_equal((integration["orchestratorStartedByDesktop"], integration["namedPipeInvoked"]), (True, True), "orchestrator/pipe")
    assert_equal(integration["action"], "task.status.query-recent", "safe pipe action")
    assert_equal(
        tuple(integration[key] for key in ("externalWorkerInvoked", "physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")),
        (False, False, False, False, False),
        "integration side effects",
    )
    orchestrator_pid = int(integration["orchestratorProcessId"])
    process_still_running = False
    try:
        os.kill(orchestrator_pid, 0)
        process_still_running = True
    except OSError:
        pass
    if process_still_running:
        raise GateFailure(f"canary-owned orchestrator process still runs: {orchestrator_pid}")

    windows_item = receipt["automatedCanaries"]["windowsUi"]
    windows_path_value = resolve_reference(windows_item["receipt"], PROJECT_ROOT)
    assert_equal(sha256_file(windows_path_value), windows_item["receiptSha256"].upper(), "Windows UI receipt")
    windows = load_json(windows_path_value)
    automated = windows["automatedWindowsUiCanary"]
    assert_equal((automated["status"], automated["assertions"]), ("PASS", 84), "Windows UI assertions")
    automated_log = resolve_reference(automated["log"], windows_path_value.parent)
    assert_equal(sha256_file(automated_log), automated["logSha256"].upper(), "Windows UI log")
    ctrl_c = windows["realWindowsCanaries"]["focusedTextBoxCtrlC"]
    ctrl_c_log = resolve_reference(ctrl_c["log"], windows_path_value.parent)
    assert_equal(sha256_file(ctrl_c_log), ctrl_c["logSha256"].upper(), "Ctrl+C log")
    assert_equal((ctrl_c["status"], ctrl_c["clipboardRestored"]), ("PASS", True), "Ctrl+C readback")
    assert_equal(windows["desktopToRealRemNoteE2E"]["status"], "NOT_RUN", "desktop to real export boundary")

    candidate_after = inventory(CANDIDATE_ROOT)
    assert_equal(candidate_after["treeSha256"], candidate_before["treeSha256"], "candidate changed during gate")
    return {
        "status": "PASS-AUTOMATED",
        "receipt": {"path": project_relative(DESKTOP_RECEIPT), "sha256": DESKTOP_RECEIPT_SHA256},
        "candidate": {
            "root": project_relative(CANDIDATE_ROOT),
            "files": candidate_before["fileCount"],
            "bytes": candidate_before["totalBytes"],
            "treeSha256": candidate_before["treeSha256"],
            "manifest": {"path": project_relative(manifest_path), "sha256": sha256_file(manifest_path)},
            "unchangedByGate": True,
        },
        "authority": {
            "status": "PASS-BYTE-EXACT",
            "checkpoint": {"path": project_relative(AUTHORITY_CHECKPOINT), "sha256": AUTHORITY_CHECKPOINT_SHA256},
            "files": authority_candidate["fileCount"],
            "bytes": authority_candidate["totalBytes"],
            "treeSha256": authority_candidate["treeSha256"],
            "mismatches": 0,
        },
        "startup": {
            "status": "PASS-SAFE",
            "externalConnections": 0,
            "productionWorkDirectoryCreated": False,
        },
        "singleInstance": {"status": "PASS-NOT-SILENT", "feedbackMode": single["userFeedbackMode"]},
        "orchestrator": {
            "status": "PASS",
            "namedPipeAction": integration["action"],
            "queryItemCount": integration["queryItemCount"],
            "externalWorkerInvoked": False,
            "processId": orchestrator_pid,
            "processStillRunningAtIndependentReadback": False,
        },
        "windowsUi": {
            "status": "PASS-AUTOMATED",
            "assertions": 84,
            "focusedTextBoxCtrlC": "PASS",
            "clipboardRestored": True,
            "receipt": {"path": project_relative(windows_path_value), "sha256": sha256_file(windows_path_value)},
        },
    }


def verify_desktop_v123(previous_desktop: dict[str, Any]) -> dict[str, Any]:
    assert_equal(sha256_file(DESKTOP_V123_RECEIPT), DESKTOP_V123_RECEIPT_SHA256, "v1.2.3 desktop receipt")
    receipt = load_json(DESKTOP_V123_RECEIPT)
    manifest_path = CANDIDATE_V123_ROOT / "candidate-file-manifest.v1.json"
    manifest = load_json(manifest_path)
    candidate_before = inventory(CANDIDATE_V123_ROOT)
    assert_inventory(candidate_before, receipt["candidate"], "v1.2.3 candidate")
    assert_equal(
        (candidate_before["fileCount"], candidate_before["totalBytes"], candidate_before["treeSha256"]),
        (331, 400642976, "B5437F88D6A6B77BD194ED85A7ECEE57AA88ACAA7A17F75123CD753BDDFD21F7"),
        "v1.2.3 candidate aggregate",
    )
    assert_equal(sha256_file(manifest_path), "DEBD4EDA85DD3B8EB9E6DB61FCD6C6832C449C765C657C9868BDAC31A2BCEF3B", "v1.2.3 manifest")
    assert_equal(manifest["excludedControlFiles"], ["candidate-file-manifest.v1.json"], "v1.2.3 manifest exclusion")
    assert_inventory(inventory(CANDIDATE_V123_ROOT, set(manifest["excludedControlFiles"])), manifest["payload"], "v1.2.3 manifest payload")

    authority_candidate = inventory(CANDIDATE_V123_ROOT / "authority" / "win-x64")
    authority_source = inventory(AUTHORITY_V123_SOURCE)
    assert_inventory(authority_candidate, authority_source, "v1.2.3 authority copy")
    assert_equal(
        (authority_candidate["fileCount"], authority_candidate["totalBytes"], authority_candidate["treeSha256"]),
        (79, 248982362, AUTHORITY_V123_TREE_SHA256),
        "v1.2.3 embedded authority",
    )

    startup_path = verify_log(DESKTOP_V123_EVIDENCE_ROOT, receipt["automatedCanaries"]["safeStartup"], "v1.2.3 startup")
    single_path = verify_log(DESKTOP_V123_EVIDENCE_ROOT, receipt["automatedCanaries"]["existingInstance"], "v1.2.3 single instance")
    integration_path = verify_log(DESKTOP_V123_EVIDENCE_ROOT, receipt["automatedCanaries"]["builtInOrchestrator"], "v1.2.3 integration")
    for key in ("authorityRegistry", "manifestTree", "desktopPayloadEquality"):
        verify_log(DESKTOP_V123_EVIDENCE_ROOT, receipt["automatedCanaries"][key], f"v1.2.3 {key}")
    startup = load_json(startup_path)
    single = load_json(single_path)
    integration = load_json(integration_path)
    assert_equal((startup["result"], Path(startup["baseDirectory"]).resolve()), ("PASS", CANDIDATE_V123_ROOT.resolve()), "v1.2.3 startup root")
    assert_equal((startup["productName"], startup["entrypoint"]), ("零碎知识剪藏", "KnowledgeClipper.exe"), "v1.2.3 product identity")
    assert_equal((startup["candidateOrchestratorPathsVerified"], startup["productionWorkDirectoryCreated"], startup["externalConnections"]), (True, False, 0), "v1.2.3 startup safety")
    assert_equal((single["existingInstanceDetected"], single["userFeedbackMode"]), (True, "activate-existing-window-and-message-box"), "v1.2.3 visible singleton")
    assert_equal((integration["result"], integration["orchestratorStartedByDesktop"], integration["namedPipeInvoked"]), ("PASS", True, True), "v1.2.3 integration")
    assert_equal((integration["action"], integration["queryItemCount"], integration["externalWorkerInvoked"]), ("task.status.query-recent", 0, False), "v1.2.3 safe query")
    assert_equal(
        tuple(integration[key] for key in ("physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")),
        (False, False, False, False),
        "v1.2.3 integration side effects",
    )
    orchestrator_pid = int(integration["orchestratorProcessId"])
    try:
        os.kill(orchestrator_pid, 0)
    except OSError:
        pass
    else:
        raise GateFailure(f"v1.2.3 canary-owned orchestrator process still runs: {orchestrator_pid}")

    equality = load_json(DESKTOP_V123_EVIDENCE_ROOT / receipt["automatedCanaries"]["desktopPayloadEquality"]["log"])
    assert_equal((equality["fileCount"], equality["changedFiles"]), (243, 0), "v1.2.3 desktop byte equality")
    assert_equal(receipt["desktop"]["uiRetestRequired"], False, "v1.2.3 UI retest boundary")
    prior_ui = resolve_reference(receipt["desktop"]["priorWindowsUiReceipt"], PROJECT_ROOT)
    assert_equal(sha256_file(prior_ui), receipt["desktop"]["priorWindowsUiReceiptSha256"].upper(), "v1.2.3 prior Windows UI receipt")
    assert_equal(sha256_file(prior_ui), previous_desktop["windowsUi"]["receipt"]["sha256"], "v1.2.3 inherited UI evidence")
    assert_equal(sha256_file(CANDIDATE_V123_ROOT / "KnowledgeClipper.exe"), "29320D1CCAA252057D011490866CC7533901740C5C6921359B7A2F9F58C4720C", "v1.2.3 desktop EXE")
    assert_equal(sha256_file(CANDIDATE_V123_ROOT / "KnowledgeClipper.dll"), "E0593F6A556B606F88BD32DA017A1B5427E41E9AA46F816C291BE95F66B8F78B", "v1.2.3 desktop DLL")

    candidate_after = inventory(CANDIDATE_V123_ROOT)
    assert_equal(candidate_after["treeSha256"], candidate_before["treeSha256"], "v1.2.3 candidate changed during gate")
    return {
        "status": "PASS-AUTOMATED-AUTHORITY-REBASE",
        "receipt": {"path": project_relative(DESKTOP_V123_RECEIPT), "sha256": DESKTOP_V123_RECEIPT_SHA256},
        "candidate": {
            "root": project_relative(CANDIDATE_V123_ROOT),
            "files": 331,
            "bytes": 400642976,
            "treeSha256": candidate_before["treeSha256"],
            "manifest": {"path": project_relative(manifest_path), "sha256": sha256_file(manifest_path)},
            "unchangedByGate": True,
        },
        "authority": {"status": "PASS-BYTE-EXACT", "files": 79, "bytes": 248982362, "treeSha256": AUTHORITY_V123_TREE_SHA256, "mismatches": 0},
        "desktopPayload": {"status": "PASS-BYTE-EXACT-V122", "files": 243, "changedFiles": 0, "exeSha256": "29320D1CCAA252057D011490866CC7533901740C5C6921359B7A2F9F58C4720C", "dllSha256": "E0593F6A556B606F88BD32DA017A1B5427E41E9AA46F816C291BE95F66B8F78B"},
        "startup": {"status": "PASS-SAFE", "externalConnections": 0, "productionWorkDirectoryCreated": False},
        "singleInstance": {"status": "PASS-NOT-SILENT", "feedbackMode": single["userFeedbackMode"]},
        "orchestrator": {"status": "PASS", "namedPipeAction": integration["action"], "queryItemCount": 0, "externalWorkerInvoked": False, "processId": orchestrator_pid, "processStillRunningAtIndependentReadback": False},
        "windowsUi": {"status": "PASS-INHERITED-BYTE-EXACT-DESKTOP", "assertions": 84, "manualUiRerunRequired": False, "receipt": previous_desktop["windowsUi"]["receipt"]},
    }


def verify_ocr_content() -> dict[str, Any]:
    wrapper = load_json(OCR_WRAPPER)
    assert_equal(wrapper["status"], "passed", "OCR wrapper status")
    canary_receipt_path = verify_reference(wrapper["canary"]["receipt"], PROJECT_ROOT, "OCR canary receipt")
    readable_path = verify_reference(wrapper["canary"]["readable"], PROJECT_ROOT, "OCR readable")
    receipt = load_json(canary_receipt_path)
    canary_root = canary_receipt_path.parent
    assert_equal(receipt["binding"]["checkpoint"]["sha256"].upper(), AUTHORITY_CHECKPOINT_SHA256, "OCR checkpoint binding")
    assert_equal(receipt["binding"]["release"]["treeSha256"].upper(), AUTHORITY_TREE_SHA256, "OCR authority tree binding")
    assert_equal(receipt["binding"]["release"]["unchanged"], True, "OCR authority release unchanged")
    assert_equal((receipt["ocr"]["status"], receipt["ocr"]["provider"], receipt["ocr"]["realProviderCall"]), ("completed", "mineru", True), "OCR stage")
    assert_equal((receipt["content"]["status"], receipt["content"]["provider"], receipt["content"]["realProviderCall"]), ("completed", "deepseek", True), "content stage")
    assert_equal((receipt["content"]["cardBundle"]["cards"], receipt["destinationWrites"]), (2, {"remnote": False, "anki": False, "exportStagesPresent": False}), "OCR output boundary")
    assert_equal((receipt["privacy"]["syntheticOnly"], receipt["privacy"]["credentialLeakDetected"], receipt["privacy"]["credentialsPersisted"]), (True, False, False), "OCR privacy")

    verify_reference(receipt["binding"]["checkpoint"], PROJECT_ROOT, "OCR checkpoint")
    verify_reference(receipt["binding"]["registry"], PROJECT_ROOT, "OCR registry")
    verify_reference(receipt["binding"]["dependencyManifest"], PROJECT_ROOT, "OCR dependency manifest")
    capture_path = verify_reference(receipt["input"]["capture"], canary_root, "OCR capture")
    # The module receipt records capture/stage references from the Canary root,
    # but records the four source images from the immutable job root. Derive
    # that base from the verified capture path instead of guessing from names.
    job_root = capture_path.parent.parent
    for group in ("questionImages", "answerImages"):
        for index, item in enumerate(receipt["input"][group]):
            verify_reference(item, job_root, f"OCR {group}[{index}]")
    for worker_name in ("image", "content"):
        worker = receipt["workers"][worker_name]
        source_entry = verify_reference(worker["entrypoint"], PROJECT_ROOT, f"{worker_name} authority entrypoint")
        candidate_entry = CANDIDATE_ROOT / "authority" / "win-x64" / source_entry.relative_to(AUTHORITY_SOURCE)
        assert_equal(sha256_file(candidate_entry), worker["entrypoint"]["sha256"].upper(), f"{worker_name} candidate entrypoint")
        verify_reference(worker["descriptorSnapshot"], PROJECT_ROOT, f"{worker_name} descriptor")
    verify_reference(receipt["workers"]["image"]["request"], canary_root, "OCR request")
    verify_reference(receipt["workers"]["image"]["result"], canary_root, "OCR result")
    for attempt in receipt["workers"]["content"]["attempts"]:
        verify_reference(attempt["request"], canary_root, f"content {attempt['attemptId']} request")
        verify_reference(attempt["result"], canary_root, f"content {attempt['attemptId']} result")
    for item in receipt["ocr"]["rawZips"]:
        verify_reference(item, canary_root, "MinerU raw ZIP")
    for section in ("artifact", "entrypoint", "material"):
        verify_reference(receipt["continuation"][section], canary_root, f"continuation {section}")
    for bundle in ("learningBundle", "cardBundle"):
        verify_reference(receipt["content"][bundle]["manifest"], canary_root, f"{bundle} manifest")
        verify_reference(receipt["content"][bundle]["payload"], canary_root, f"{bundle} payload")
    assert_equal(sha256_file(readable_path), receipt["readable"]["sha256"].upper(), "OCR readable inner hash")

    return {
        "status": "PASS-EXACT-AUTHORITY",
        "wrapper": {"path": project_relative(OCR_WRAPPER), "sha256": sha256_file(OCR_WRAPPER)},
        "receipt": {"path": project_relative(canary_receipt_path), "sha256": sha256_file(canary_receipt_path)},
        "readable": {"path": project_relative(readable_path), "sha256": sha256_file(readable_path)},
        "authorityTreeSha256": AUTHORITY_TREE_SHA256,
        "input": {
            "dataClass": receipt["input"]["dataClass"],
            "questionImages": 2,
            "answerImages": 2,
            "referenceBases": {
                "captureAndStages": "canaryRoot",
                "sourceImages": "jobRootDerivedFromCapture",
            },
        },
        "observed": {
            "ocrItems": len(receipt["ocr"]["items"]),
            "ocrOutputs": receipt["ocr"]["commonOutputs"],
            "mineruRawZips": receipt["ocr"]["rawZipCount"],
            "contentAttempts": len(receipt["workers"]["content"]["attempts"]),
            "cards": receipt["content"]["cardBundle"]["cards"],
        },
        "externalCallsAlreadyPerformedByModuleEvidence": {"mineru": True, "deepseek": True},
        "externalCallsPerformedByThisQualityGate": 0,
        "destinationWrites": receipt["destinationWrites"],
    }


def verify_anki_blocker() -> dict[str, Any]:
    receipt = load_json(ANKI_BLOCKER)
    assert_equal(receipt["status"], "blocked_authority_checkpoint", "Anki exact authority status")
    requested = receipt["requestedAuthority"]
    assert_equal(requested["checkpointSha256"].upper(), AUTHORITY_CHECKPOINT_SHA256, "Anki checkpoint binding")
    assert_equal(requested["treeSha256"].upper(), AUTHORITY_TREE_SHA256, "Anki authority tree binding")
    assert_equal((requested["files"], requested["bytes"]), (79, 248977927), "Anki authority aggregate")
    exact = receipt["exactCandidateExecution"]
    assert_equal(exact["candidateBoundPass"], False, "Anki candidate-bound result")
    assert_equal((exact["contentFullRun"]["status"], exact["contentFullRun"]["worker"]), ("completed", {"id": "content.deepseek", "version": "0.5.2"}), "Anki content stage")
    assert_equal((exact["anki"]["status"], exact["anki"]["worker"]), ("failed", {"id": "export.anki", "version": "1.1.4"}), "Anki export stage")
    assert_equal(exact["anki"]["error"]["code"], "ANCESTRY_PATH_INVALID", "Anki failure code")
    assert_equal(exact["anki"]["sideEffectState"], "none", "Anki side effects")
    assert_equal((receipt["apiCalls"]["mutating"], receipt["destinationReadback"]["newNotes"], receipt["destinationReadback"]["deleted"], receipt["reconciliationHistory"]["finalReadbackUnchanged"]), (0, 0, 0, True), "Anki read-only boundary")
    verify_reference({"path": requested["checkpointPath"], "sha256": requested["checkpointSha256"]}, PROJECT_ROOT, "Anki checkpoint")
    verify_reference({"path": exact["contentFullRun"]["resultPath"], "sha256": exact["contentFullRun"]["resultSha256"]}, PROJECT_ROOT, "Anki content result")
    verify_reference({"path": exact["anki"]["resultPath"], "sha256": exact["anki"]["resultSha256"]}, PROJECT_ROOT, "Anki failed result")
    readable_path = verify_reference(receipt["readableEvidence"], PROJECT_ROOT, "Anki readable blocker")
    source_anki = AUTHORITY_SOURCE / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
    candidate_anki = CANDIDATE_ROOT / "authority" / "win-x64" / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
    assert_equal(sha256_file(source_anki), requested["anki114Sha256"].upper(), "Anki source binary")
    assert_equal(sha256_file(candidate_anki), requested["anki114Sha256"].upper(), "Anki candidate binary")

    return {
        "status": "NO-GO-EXACT-AUTHORITY",
        "receipt": {"path": project_relative(ANKI_BLOCKER), "sha256": sha256_file(ANKI_BLOCKER)},
        "readable": {"path": project_relative(readable_path), "sha256": sha256_file(readable_path)},
        "authorityTreeSha256": AUTHORITY_TREE_SHA256,
        "worker": {"id": "export.anki", "version": "1.1.4", "sha256": requested["anki114Sha256"].upper()},
        "input": {"contentWorker": "content.deepseek/0.5.2", "contentStatus": "completed", "cards": 4},
        "output": {
            "status": "failed",
            "errorCode": "ANCESTRY_PATH_INVALID",
            "sideEffectState": "none",
            "candidateBoundPass": False,
        },
        "existingIsolatedDestinationReadback": {
            "deck": receipt["destinationReadback"]["deck"],
            "notes": receipt["destinationReadback"]["notes"],
            "cards": receipt["destinationReadback"]["cards"],
            "newNotes": 0,
            "deleted": 0,
            "mutatingCalls": 0,
            "readOnlyCallsAlreadyPerformedByModuleEvidence": receipt["apiCalls"]["ankiConnectReadOnly"],
        },
        "externalCallsPerformedByThisQualityGate": 0,
        "moduleFixNotAuthorityBound": {
            "version": receipt["moduleFix"]["workerVersion"],
            "validateOnlyStatus": "PASS",
            "mustNotSubstituteForExactAuthority": True,
        },
        "requiredResolution": receipt["blocker"]["requiredResolution"],
    }


def verify_authority_v123() -> dict[str, Any]:
    assert_equal(sha256_file(AUTHORITY_V123_CHECKPOINT), AUTHORITY_V123_CHECKPOINT_SHA256, "v1.2.3 checkpoint")
    checkpoint = load_json(AUTHORITY_V123_CHECKPOINT)
    assert_equal(checkpoint["admissionStatus"], "authority-admitted", "v1.2.3 admission")
    assert_equal((checkpoint["registry"]["registryVersion"], checkpoint["registry"]["workers"]), ("1.2.3", 5), "v1.2.3 registry identity")
    assert_equal(checkpoint["registry"]["sha256"].upper(), AUTHORITY_V123_REGISTRY_SHA256, "v1.2.3 registry hash")
    registry_path = Path(checkpoint["registry"]["path"])
    assert_equal(sha256_file(registry_path), AUTHORITY_V123_REGISTRY_SHA256, "v1.2.3 registry file")
    assert_equal((checkpoint["frozenV1"]["files"], checkpoint["frozenV1"]["mismatches"]), (21, 0), "v1.2.3 frozen contracts")
    assert_equal(checkpoint["validation"], {"command": "KnowledgeClipper.Orchestrator.exe validate-registry", "exitCode": 0, "status": "valid", "workers": 5}, "v1.2.3 registry validation")

    current = inventory(AUTHORITY_V123_SOURCE)
    assert_equal((current["fileCount"], current["totalBytes"], current["treeSha256"]), (79, 248982362, AUTHORITY_V123_TREE_SHA256), "v1.2.3 release tree")
    baseline = inventory(AUTHORITY_SOURCE)
    baseline_map = {item["path"]: item for item in baseline["files"]}
    current_map = {item["path"]: item for item in current["files"]}
    differences = sorted(
        path
        for path in set(baseline_map) | set(current_map)
        if baseline_map.get(path) != current_map.get(path)
    )
    assert_equal(differences, ["workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe"], "v1.2.3 delta")
    workers = {item["workerId"]: item for item in checkpoint["workers"]}
    assert_equal(set(workers), {"image.ocr.mineru", "content.deepseek", "speech.edge", "export.remnote", "export.anki"}, "v1.2.3 workers")
    assert_equal((workers["export.anki"]["workerVersion"], workers["export.anki"]["sha256"]), ("1.1.5", "4BDD812370D4BA45B45D86DF76A42F90D1CAD9A08D018810C9385BD99BB39774"), "v1.2.3 Anki")
    for worker_id, item in workers.items():
        entrypoint = AUTHORITY_V123_SOURCE / item["entrypoint"]
        assert_equal(sha256_file(entrypoint), item["sha256"].upper(), f"v1.2.3 {worker_id} entrypoint")
        assert_equal(windows_path(entrypoint).stat().st_size, int(item["bytes"]), f"v1.2.3 {worker_id} bytes")
        describe = item["describeSnapshot"]
        assert_equal(sha256_file(Path(describe["path"])), describe["sha256"].upper(), f"v1.2.3 {worker_id} describe")

    receipt = load_json(AUTHORITY_V123_RECEIPT)
    assert_equal(receipt["admissionStatus"], "authority-admitted", "v1.2.3 authority receipt")
    assert_equal(receipt["checkpoint"]["sha256"].upper(), AUTHORITY_V123_CHECKPOINT_SHA256, "v1.2.3 receipt checkpoint")
    assert_equal(receipt["release"]["treeSha256"].upper(), AUTHORITY_V123_TREE_SHA256, "v1.2.3 receipt tree")
    return {
        "status": "PASS-AUTHORITY-ADMITTED",
        "checkpoint": {"path": project_relative(AUTHORITY_V123_CHECKPOINT), "sha256": AUTHORITY_V123_CHECKPOINT_SHA256},
        "registry": {"path": project_relative(registry_path), "sha256": AUTHORITY_V123_REGISTRY_SHA256, "version": "1.2.3", "workers": 5},
        "receipt": {"path": project_relative(AUTHORITY_V123_RECEIPT), "sha256": sha256_file(AUTHORITY_V123_RECEIPT)},
        "release": {"root": project_relative(AUTHORITY_V123_SOURCE), "files": 79, "bytes": 248982362, "treeSha256": AUTHORITY_V123_TREE_SHA256},
        "deltaFromV122": {"changedFiles": differences, "count": 1, "onlyAnki115": True},
        "integratedDesktopCandidate": "PASS",
        "exactAuthorityAnkiCanary": "PASS",
    }


def verify_anki_v123() -> dict[str, Any]:
    assert_equal(sha256_file(ANKI_V123_RECEIPT), ANKI_V123_RECEIPT_SHA256, "v1.2.3 Anki receipt")
    receipt = load_json(ANKI_V123_RECEIPT)
    authority = receipt["authority"]
    assert_equal(receipt["status"], "passed", "v1.2.3 Anki status")
    assert_equal(authority["checkpoint"]["sha256"].upper(), AUTHORITY_V123_CHECKPOINT_SHA256, "v1.2.3 Anki checkpoint")
    assert_equal(authority["registry"]["sha256"].upper(), AUTHORITY_V123_REGISTRY_SHA256, "v1.2.3 Anki registry")
    assert_equal(authority["release"], {"bytes": 248982362, "files": 79, "root": "releases/knowledge-clipper-authority-v1.2.3-20260828T021300Z/win-x64", "treeSha256": AUTHORITY_V123_TREE_SHA256}, "v1.2.3 Anki release")
    assert_equal(authority["validation"], {"registryVersion": "1.2.3", "status": "valid", "workers": 5}, "v1.2.3 Anki registry validation")
    for name, item in authority["executables"].items():
        verify_reference(item, PROJECT_ROOT, f"v1.2.3 Anki {name} executable")
    verify_reference(authority["checkpoint"], PROJECT_ROOT, "v1.2.3 Anki checkpoint evidence")
    verify_reference(authority["registry"], PROJECT_ROOT, "v1.2.3 Anki registry evidence")
    verify_reference(authority["dependencyManifest"], PROJECT_ROOT, "v1.2.3 Anki dependency evidence")

    expected_checks = {
        "allDeckInventoryUnchanged",
        "authorityBound",
        "contentSemanticMatch",
        "coreOrderAndFieldsVerified",
        "exactCandidateProcesses",
        "isolatedDeckOnly",
        "noDeleteOrCleanup",
        "noMutatingAction",
        "readbackUnchanged",
        "zeroNewFourCurrent",
    }
    assert_equal(set(receipt["checks"]), expected_checks, "v1.2.3 Anki checks")
    assert_equal(all(receipt["checks"].values()), True, "v1.2.3 Anki check values")
    assert_equal(receipt["route"], "exact-candidate orchestrator -> content.deepseek 0.5.2 -> Core content-to-anki.v1 -> export.anki 1.1.5", "v1.2.3 Anki route")
    assert_equal(receipt["runs"]["fullAuthority"]["orchestratorStatus"], "succeeded", "v1.2.3 Anki orchestrator")
    assert_equal(receipt["runs"]["fullAuthority"]["anki"]["counts"], {"already_current": 4, "blocked_upstream": 0, "failed": 0, "succeeded": 0}, "v1.2.3 Anki outcomes")
    for key in ("contentPreflight",):
        verify_reference({"path": receipt["runs"][key]["resultPath"], "sha256": receipt["runs"][key]["resultSha256"]}, PROJECT_ROOT, f"v1.2.3 Anki {key}")
    full = receipt["runs"]["fullAuthority"]
    verify_reference({"path": full["contentResultPath"], "sha256": full["contentResultSha256"]}, PROJECT_ROOT, "v1.2.3 Anki content result")
    verify_reference({"path": full["anki"]["resultPath"], "sha256": full["anki"]["resultSha256"]}, PROJECT_ROOT, "v1.2.3 Anki result")
    verify_reference({"path": full["anki"]["moduleReceiptPath"], "sha256": full["anki"]["moduleReceiptSha256"]}, PROJECT_ROOT, "v1.2.3 Anki module receipt")
    readable = verify_reference(receipt["readableEvidence"], PROJECT_ROOT, "v1.2.3 Anki readable")
    for key in ("cardBundlePath", "learningBundlePath", "receiptPath"):
        sha_key = key.removesuffix("Path") + "Sha256"
        verify_reference({"path": receipt["source"][key], "sha256": receipt["source"][sha_key]}, PROJECT_ROOT, f"v1.2.3 Anki source {key}")

    destination = receipt["destination"]
    assert_equal((destination["deck"], destination["notesBefore"], destination["notesAfter"], destination["cardsBefore"], destination["cardsAfter"]), ("零碎知识剪藏 Canary", 4, 4, 4, 4), "v1.2.3 Anki isolated destination")
    assert_equal((destination["newNotes"], destination["deleted"], destination["existingCurrent"]), (0, 0, 4), "v1.2.3 Anki destination effects")
    assert_equal(destination["allDeckInventory"]["beforeSha256"], destination["allDeckInventory"]["afterSha256"], "v1.2.3 Anki all-deck inventory")
    observable_keys = ("ankiBack", "ankiFront", "cardId", "cardIds", "deck", "noteId", "template")
    before_observable = [{key: item[key] for key in observable_keys} for item in destination["before"]]
    after_observable = [{key: item[key] for key in observable_keys} for item in destination["after"]]
    assert_equal(before_observable, after_observable, "v1.2.3 Anki observable readback")
    assert_equal((receipt["apiCalls"]["mutatingActions"], receipt["apiCalls"]["contentProvider"]), ([], 0), "v1.2.3 Anki external boundary")

    cards = receipt["cardSemantics"]["cards"]
    assert_equal(receipt["cardSemantics"]["orderedTemplates"], ["language_context", "language_writing", "language_context", "language_writing"], "v1.2.3 Anki card order")
    assert_equal((receipt["cardSemantics"]["meaningReading"], receipt["cardSemantics"]["writing"], receipt["cardSemantics"]["bothGroups"]), (2, 2, 2), "v1.2.3 Anki card counts")
    for card in cards:
        if card["template"] == "language_writing":
            target = card["back"]
            if target in card["front"]:
                raise GateFailure(f"writing card still contains target term: {card['cardId']}")
        if any(field in card for field in ("choice", "decisionKind", "targetToken", "binding")):
            raise GateFailure(f"decision field leaked into Anki card: {card['cardId']}")

    return {
        "status": "PASS-EXACT-AUTHORITY-IDEMPOTENT",
        "receipt": {"path": project_relative(ANKI_V123_RECEIPT), "sha256": ANKI_V123_RECEIPT_SHA256},
        "readable": {"path": project_relative(readable), "sha256": sha256_file(readable)},
        "authorityTreeSha256": AUTHORITY_V123_TREE_SHA256,
        "route": receipt["route"],
        "input": {"providerMode": receipt["source"]["providerMode"], "providerExternalCalls": 0, "cards": 4},
        "output": {"orchestratorStatus": "succeeded", "alreadyCurrent": 4, "newNotes": 0, "deleted": 0},
        "destination": {"deck": destination["deck"], "notes": 4, "cards": 4, "allDeckInventoryUnchanged": True},
        "cards": [{"cardId": card["cardId"], "template": card["template"], "front": card["front"], "back": card["back"]} for card in cards],
        "externalCallsAlreadyPerformedByModuleEvidence": {"ankiConnectReadOnly": receipt["external_api_calls"]["ankiConnect"], "mutatingActions": 0},
        "externalCallsPerformedByThisQualityGate": 0,
    }


def verify_all() -> dict[str, Any]:
    historical_desktop = verify_desktop()
    desktop = verify_desktop_v123(historical_desktop)
    ocr = verify_ocr_content()
    historical_anki_blocker = verify_anki_blocker()
    authority = verify_authority_v123()
    anki = verify_anki_v123()
    assert_equal(authority["deltaFromV122"]["changedFiles"], ["workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe"], "OCR forward-binding delta")
    ocr["forwardBindingToV123"] = {
        "status": "PASS-BYTE-EXACT-UNCHANGED-WORKERS",
        "authorityTreeSha256": AUTHORITY_V123_TREE_SHA256,
        "reason": "v1.2.3 differs from v1.2.2 only in export.anki; image.ocr.mineru and content.deepseek trees are byte-identical",
    }
    release_manifest = CANDIDATE_V123_ROOT / "release-manifest.v1.json"
    if release_manifest.exists():
        raise GateFailure("unsealed candidate unexpectedly contains release-manifest.v1.json")
    return {
        "schemaVersion": "knowledge-clipper-integrated-candidate-quality-gate.v1",
        "candidateId": CANDIDATE_V123_ID,
        "createdAt": utc_now(),
        "status": "NO-GO-RELEASE-BOUNDARIES",
        "candidateScopedQualification": "PASS",
        "decision": "candidate-scoped-quality-pass-release-unsealed",
        "candidateBinding": {
            "candidateRoot": project_relative(CANDIDATE_V123_ROOT),
            "candidateTreeSha256": desktop["candidate"]["treeSha256"],
            "authorityTreeSha256": desktop["authority"]["treeSha256"],
            "releaseManifest": None,
            "candidateHash": None,
            "signed": False,
        },
        "authority": authority,
        "gates": {"desktop": desktop, "ocrToDeepSeek": ocr, "anki": anki},
        "historicalEvidence": {
            "integratedV122Desktop": historical_desktop,
            "v122AnkiBlocker": historical_anki_blocker,
        },
        "qualityExecution": {
            "mode": "read-only-existing-evidence",
            "externalCallsPerformed": 0,
            "candidateFilesModified": 0,
            "userManualUiRerun": False,
        },
        "remainingNoGo": [
            "release-manifest.v1.json and candidateHash are absent",
            "installer and code signing are absent",
            "desktop-to-real-export E2E was not run; formal clipping RemNote and daily Anki targets were not touched",
            "physical speaker playback was not run",
            "physical external-app selection, screenshot drag, and mixed-DPI checks remain user/environment boundaries",
        ],
    }


def readable_index(result: dict[str, Any], receipt_relative: str) -> str:
    ocr = result["gates"]["ocrToDeepSeek"]
    anki = result["gates"]["anki"]
    blocker = result["historicalEvidence"]["v122AnkiBlocker"]
    cards = "\n".join(
        f"  {index}. `{card['template']}`：{card['front']} → {card['back']}"
        for index, card in enumerate(anki["cards"], start=1)
    )
    return f"""# 零碎知识剪藏 integrated v1.2.3：原始输入 → 最终输出索引

## 候选绑定

- 候选目录：`{result['candidateBinding']['candidateRoot']}`
- 候选文件树：331 files / 400,642,976 bytes / `{result['candidateBinding']['candidateTreeSha256']}`。
- 候选 manifest：`DEBD4EDA85DD3B8EB9E6DB61FCD6C6832C449C765C657C9868BDAC31A2BCEF3B`。
- 内嵌 authority：79 files / 248,982,362 bytes / `{result['candidateBinding']['authorityTreeSha256']}`，与 v1.2.3 源逐文件一致；registry 5/5 valid。
- 机器可读质量回执：`{receipt_relative}`

## Desktop：候选入口 → 本地可观察结果

- 输入：`KnowledgeClipper.exe`、候选内 `candidate-integration.json`、内嵌 Orchestrator/registry/IPC。
- 输出：安全 startup PASS；未创建生产工作目录、外连 0；旧实例存在时明确激活并提示，不静默成功、不结束旧进程。
- Orchestrator/Named Pipe：桌面实际启动候选内 Orchestrator，执行只读 `task.status.query-recent`，返回 0 项；未启动 worker；Canary 进程随后已清理。
- Windows UI：桌面 243 个 payload 文件与 v1.2.2 逐文件相同，因此继承同一 84 条自动断言、focused TextBox Ctrl+C 与剪贴板恢复证据；UI 无变化，没有重复用户手测。

## 图片 → MinerU → DeepSeek（v1.2.2 实跑，字节级前向绑定 v1.2.3）

- 原始输入：2 张 question + 2 张 answer 合成图片，显式角色和顺序。
- OCR：4 items / 8 common outputs / 4 个原始 MinerU ZIP。
- Content：第一次 gate failure 被保留；第二次从同一不可变输入重跑 completed。
- 最终输出：2 张 `basic_qa` 卡：`WHAT IS 18 + 24?` → `18 + 24 = 42.`；`QUESTION 2: WHICH POLYGON HAS THREE SIDES?` → `ANSWER 2: A TRIANGLE HAS THREE SIDES.`。未进入导出阶段。
- 可读证据：`{ocr['readable']['path']}`；权威回执：`{ocr['wrapper']['path']}`。
- 绑定说明：v1.2.3 相对 v1.2.2 仅替换 `export.anki`，image/content 文件树未变；模块证据曾真实调用 MinerU 与 DeepSeek，本质量门禁外部调用 0。

## 文本/语言卡 → Anki（exact authority v1.2.3）

- 原始输入：冻结的 DeepSeek 语言卡结果回放，4 张卡；本轮 DeepSeek 外部调用 0。
- 卡片顺序与可读输出：
{cards}
- 最终读回：Orchestrator succeeded；`already_current=4`，新增 0、删除 0；隔离牌组前后均 4 notes / 4 cards，全牌组 inventory SHA 不变。
- AnkiConnect：53 次只读调用，`mutatingActions=[]`；可读证据：`{anki['readable']['path']}`；回执：`{anki['receipt']['path']}`。

## 历史阻断（保留，不覆盖）

- v1.2.2 / export.anki 1.1.4 曾在写入前因 `ANCESTRY_PATH_INVALID` NO-GO，副作用为 0；历史回执：`{blocker['receipt']['path']}`。
- v1.2.3 / export.anki 1.1.5 已用同类 ancestry 输入在精确 authority 下通过，因此历史阻断不再是当前候选缺口。

## 当前结论

- Desktop v1.2.3 自动候选门禁：PASS。
- OCR → DeepSeek：PASS（worker 位字节级前向绑定 v1.2.3）。
- Anki v1.2.3 exact-authority 隔离目标幂等路径：PASS。
- 候选范围质量：PASS；整体发布仍 `NO-GO-RELEASE-BOUNDARIES`。`release-manifest.v1.json`、candidateHash、installer、signing 为空；desktop→real-export E2E、正式剪藏 RemNote/日常 Anki、物理扬声器及物理外部应用/跨屏 DPI 均未成为发布证据。
"""


def write_once(path: Path, content: str) -> None:
    if path.exists():
        raise GateFailure(f"append-only output already exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=False)
    path.write_text(content, encoding="utf-8")


def emit(output_root: Path) -> dict[str, Any]:
    if output_root.exists():
        raise GateFailure(f"append-only output root already exists: {output_root}")
    result = verify_all()
    receipt_path = output_root / "quality-receipt.json"
    index_path = output_root / "input-output-index.md"
    receipt_relative = project_relative(receipt_path)
    index = readable_index(result, receipt_relative)
    output_root.mkdir(parents=True)
    index_path.write_text(index, encoding="utf-8")
    result["readableIndex"] = {
        "path": project_relative(index_path),
        "bytes": index_path.stat().st_size,
        "sha256": sha256_file(index_path),
    }
    receipt_path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return {
        "status": result["status"],
        "receipt": project_relative(receipt_path),
        "receiptSha256": sha256_file(receipt_path),
        "index": project_relative(index_path),
        "indexSha256": sha256_file(index_path),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("verify")
    emit_parser = subparsers.add_parser("emit")
    emit_parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == "verify":
            result = verify_all()
        else:
            output_root = args.output_root
            if not output_root.is_absolute():
                output_root = PROJECT_ROOT / output_root
            result = emit(output_root)
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    except (GateFailure, OSError, ValueError, KeyError, TypeError) as exc:
        print(json.dumps({"status": "failed", "error": str(exc)}, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
