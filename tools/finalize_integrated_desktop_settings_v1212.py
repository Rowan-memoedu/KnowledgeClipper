from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[1]
CANDIDATE_ID = "kc-integrated-settings-v1212-20260830T183500Z"
CANDIDATE = PROJECT / "releases" / CANDIDATE_ID / "win-x64"
DESKTOP_BUILD = PROJECT / "dist" / "desktop-win-x64" / "knowledge-clipper-settings-v1212-20260830T183000Z"
INTEGRATION = PROJECT / "canary" / "desktop-integrated" / f"{CANDIDATE_ID}-qualified-20260830T184000Z"
INTEGRATION_UNIT = PROJECT / "canary" / "desktop-integrated" / "kc-integrated-settings-v1212-20260830T175000Z-safe-20260830T180000Z" / "desktop-integration-canary.log"
SETTINGS_UI = PROJECT / "canary" / "desktop-windows" / "settings-companions-v1212-20260830T172500Z" / "settings-ui-automation.log"
FULL_UI = PROJECT / "canary" / "desktop-windows" / "20260830T081155Z" / "windows-ui-automation.log"
FOCUSED_INPUT = PROJECT / "canary" / "desktop-windows" / "20260830T081155Z" / "ctrl-c-focused-selection.log"
VISUAL = PROJECT / "canary" / "desktop-visual" / "settings-companions-v1212-20260830T174000Z"
BUILD_LOG = INTEGRATION / "desktop-build-and-test-final.log"
OUTPUT = PROJECT / "receipts" / "desktop-integrated-settings-v1.2.12-20260830T184500Z.json"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


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
    files = []
    rows = []
    total = 0
    for path in sorted((p for p in root.rglob("*") if p.is_file()), key=lambda p: p.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        if relative in excluded:
            continue
        size = path.stat().st_size
        digest = sha(path)
        files.append({"path": relative, "bytes": size, "sha256": digest})
        rows.append(f"{relative}\t{size}\t{digest.lower()}")
        total += size
    return {
        "fileCount": len(files),
        "totalBytes": total,
        "treeSha256": hashlib.sha256("\n".join(rows).encode()).hexdigest().upper(),
        "treeAlgorithm": "sha256(utf8 ordinal(relativePath + TAB + bytes + TAB + lowercaseSha256) joined by LF)",
        "files": files,
    }


def evidence(path: Path) -> dict:
    return {"path": path.relative_to(PROJECT).as_posix(), "bytes": path.stat().st_size, "sha256": sha(path)}


def main() -> None:
    require(not OUTPUT.exists(), f"append-only receipt exists: {OUTPUT}")
    manifest_path = CANDIDATE / "candidate-file-manifest.v1.json"
    build_receipt_path = CANDIDATE / "desktop-build-receipt.v1.json"
    manifest = load(manifest_path)
    build_receipt = load(build_receipt_path)
    require(manifest["candidateId"] == CANDIDATE_ID, "candidate manifest identity mismatch")
    require(build_receipt["fileCount"] == 540, "recursive Desktop payload count mismatch")

    payload = inventory(CANDIDATE, {"candidate-file-manifest.v1.json"})
    require(payload["fileCount"] == manifest["payload"]["fileCount"], "manifest payload file count mismatch")
    require(payload["totalBytes"] == manifest["payload"]["totalBytes"], "manifest payload bytes mismatch")
    require(payload["treeSha256"] == manifest["payload"]["treeSha256"], "manifest payload tree mismatch")
    manifest_records = {item["path"]: item for item in manifest["payload"]["files"]}
    for item in payload["files"]:
        require(item == manifest_records.get(item["path"]), f"manifest payload entry mismatch: {item['path']}")
    sealed = inventory(CANDIDATE)
    require(sealed["fileCount"] == 838 and sealed["totalBytes"] == 429594994, "sealed candidate aggregate mismatch")
    require(sealed["treeSha256"] == "4F1021038635984154290467011FBB25AB9D88DD7A1A035F059CFF309231416A", "sealed candidate tree mismatch")

    authority = inventory(CANDIDATE / "authority" / "win-x64")
    require(authority["fileCount"] == 284 and authority["totalBytes"] == 260374870, "authority aggregate mismatch")
    require(authority["treeSha256"] == "31CA3150B1A2EF9F97DD67B3B5491E94C49275C23018BC75B8BFECACC504DF13", "authority tree mismatch")
    require(sha(CANDIDATE / "authority/metadata/checkpoint.json") == "552151B3EDBEED94FD1B4ECC7B49F78CD7503321119235BBF3ECAA2A0EEE8580", "authority checkpoint mismatch")
    require(sha(CANDIDATE / "authority/metadata/worker-registry.v1.json") == "FC105AB65FD80B72779D1601E2C4B6FAA7ECCAF5F5C1BADC774060D8F685495A", "authority registry mismatch")

    for relative in (
        "descriptors/speech-settings-companion.v1.json",
        "descriptors/content-companion-descriptor.v1.json",
        "descriptors/anki-destination-settings-descriptor.v1.json",
        "descriptors/image-ocr-capability-config-descriptor.v4.json",
        "contracts/core/v2.4/settings-control/module-settings-adapter.v3.json",
        "contracts/core/v2.5/checkpoints/v2.5.1-20260830T075400Z/checkpoint.json",
    ):
        require((CANDIDATE / relative).is_file(), f"recursive Desktop dependency missing: {relative}")
    require(sha(CANDIDATE / "contracts/core/v2.4/settings-control/module-settings-adapter.v3.json") == "C59C4234072FB40B538C4C3275B10AE827EA130AC70B5157F3140149408C16BC", "packaged adapter mismatch")
    require(sha(CANDIDATE / "contracts/core/v2.5/checkpoints/v2.5.1-20260830T075400Z/checkpoint.json") == "62996EF471F46470090BE40259BF95ACD84EB7640084A1FCD4EC85E680AEE4F3", "packaged Core checkpoint mismatch")

    startup = load(INTEGRATION / "startup-diagnostic.stdout.log")
    registry = load(INTEGRATION / "authority-validate-registry.log")
    pipe = load(INTEGRATION / "integration-startup-canary.stdout.log")
    single = load(INTEGRATION / "single-instance-diagnostic.stdout.log")
    require(startup["result"] == "PASS" and startup["packageFileCount"] == 540 and startup["packageTreeSha256"].upper() == build_receipt["treeSha256"].upper(), "safe startup/package receipt gate failed")
    require(startup["candidateOrchestratorPathsVerified"] and startup["externalConnections"] == 0 and not startup["namedPipeInvoked"], "safe startup side-effect boundary failed")
    require(registry["status"] == "valid" and registry["workers"] == 5 and registry["registryVersion"] == "1.2.12", "registry gate failed")
    require(pipe["result"] == "PASS" and pipe["namedPipeInvoked"] and pipe["action"] == "task.status.query-recent", "Named Pipe gate failed")
    require(not any(pipe[key] for key in ("externalWorkerInvoked", "physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")), "integrated safe canary crossed external boundary")
    require(single["userFeedbackMode"] == "activate-existing-window-and-message-box", "single-instance feedback contract mismatch")
    integration_unit_text = INTEGRATION_UNIT.read_text(encoding="utf-8")
    require("DESKTOP_INTEGRATION_CANARY PASS assertions=8" in integration_unit_text and "existing canonical mutex is detected" in integration_unit_text, "isolated single-instance collision regression failed")

    settings_text = SETTINGS_UI.read_text(encoding="utf-8")
    full_ui_text = FULL_UI.read_text(encoding="utf-8")
    focused_text = FOCUSED_INPUT.read_text(encoding="utf-8")
    build_text = BUILD_LOG.read_text(encoding="utf-8")
    require("WINDOWS_SETTINGS_UI_CANARY PASS assertions=97" in settings_text, "settings-only Windows UI gate failed")
    require("WINDOWS_UI_CANARY PASS assertions=152" in full_ui_text, "full Windows UI program gate failed")
    require("FAIL Canary window did not receive foreground focus" in focused_text, "focused input boundary must remain explicit")
    require("PASS desktop automation build and tests" in build_text, "desktop build-and-test gate failed")
    visual = inventory(VISUAL)
    visual_png_count = sum(1 for path in VISUAL.rglob("*.png") if path.is_file())
    require(visual_png_count == 16 and all((VISUAL / name).is_file() for name in ("settings-content.png", "settings-speech.png", "settings-export.png", "settings-ocr.png", "floating-idle.png")), "visual preview gate failed")

    forbidden_values = (b"canary-deepseek-local-only", b"canary-mineru-local-only")
    forbidden_files = []
    leaked_values = []
    for path in (p for p in CANDIDATE.rglob("*") if p.is_file()):
        if path.name.lower() in {"credentials.json", "credentials.json.bak"}:
            forbidden_files.append(path.relative_to(CANDIDATE).as_posix())
        data = path.read_bytes()
        if any(value in data for value in forbidden_values):
            leaked_values.append(path.relative_to(CANDIDATE).as_posix())
    require(not forbidden_files and not leaked_values, "candidate secret scan failed")

    companion_inputs = {
        "contentCompatibility": PROJECT / "content-worker/receipts/core-v3-desktop-compatibility/20260830T074659078Z/receipt.v1.json",
        "ankiPostAuthorityCompatibility": PROJECT / "workers/anki-exporter/canary/shared-consumer-compatibility/20260830T080701Z-fe8c2407/receipt.json",
        "imageAdmission": PROJECT / "workers/image-ocr/canary/core-admission-1.2.2-20260830T074207Z/core-admission-receipt.v1.json",
        "authorityOfflineQualification": PROJECT / "receipts/knowledge-clipper-authority-1.2.12-offline-qualification-20260830T080000Z.json",
    }
    expected = {
        "contentCompatibility": "BF933C31A6D0D16C51313CDD6B71D58EB493A11D23F5A01DCFA8A762FCF9513C",
        "ankiPostAuthorityCompatibility": "E8335BE40C626A4BECB04ABB76BEC7560A36A5855D053CA6990373984D4019A0",
        "imageAdmission": "8833618214090F8871A86F2693B74B3C996E7DCD0570441D2D17C0214228FBA7",
        "authorityOfflineQualification": "4E46CBE8532F05FBCE1BC0AE222A23390C8C4448720A9A4B53B28D2851F64B9E",
    }
    for name, path in companion_inputs.items():
        require(sha(path) == expected[name], f"companion evidence drift: {name}")

    receipt = {
        "schemaVersion": "knowledge-clipper-desktop-integrated-settings-module-checkpoint.v1",
        "status": "PASS-INTEGRATED-AUTOMATED-CONDITIONAL",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "candidateId": CANDIDATE_ID,
        "userHandTestEntrypoint": False,
        "candidate": {
            "root": CANDIDATE.relative_to(PROJECT).as_posix(),
            "entrypoint": (CANDIDATE / "KnowledgeClipper.exe").relative_to(PROJECT).as_posix(),
            "entrypointSha256": sha(CANDIDATE / "KnowledgeClipper.exe"),
            "applicationDllSha256": sha(CANDIDATE / "KnowledgeClipper.dll"),
            "manifest": evidence(manifest_path),
            "sealed": {key: sealed[key] for key in ("fileCount", "totalBytes", "treeSha256", "treeAlgorithm")},
        },
        "desktop": {
            "buildId": build_receipt["buildId"],
            "buildReceipt": evidence(build_receipt_path),
            "payload": {"fileCount": build_receipt["fileCount"], "totalBytes": build_receipt["totalBytes"], "treeSha256": build_receipt["treeSha256"].upper()},
            "recursiveDependenciesIncluded": True,
            "coreCheckpoint": {"version": "2.5.1", "sha256": "62996EF471F46470090BE40259BF95ACD84EB7640084A1FCD4EC85E680AEE4F3"},
            "settingsAdapter": {"version": "3", "sha256": "C59C4234072FB40B538C4C3275B10AE827EA130AC70B5157F3140149408C16BC"},
        },
        "authority": {
            "version": "1.2.12", "checkpointId": "knowledge-clipper-authority-1.2.12-20260830T075600Z",
            "checkpointSha256": "552151B3EDBEED94FD1B4ECC7B49F78CD7503321119235BBF3ECAA2A0EEE8580",
            "registrySha256": "FC105AB65FD80B72779D1601E2C4B6FAA7ECCAF5F5C1BADC774060D8F685495A",
            "releaseFiles": authority["fileCount"], "releaseBytes": authority["totalBytes"], "releaseTreeSha256": authority["treeSha256"],
            "workers": ["image.ocr.mineru@1.2.2", "content.deepseek@0.7.0", "speech.edge@1.2.4", "export.remnote@2.1.0", "export.anki@1.2.1"],
        },
        "automatedEvidence": {
            "desktopBuildAndTest": evidence(BUILD_LOG),
            "settingsWindowsUi": {"status": "PASS", "assertions": 97, **evidence(SETTINGS_UI)},
            "fullWindowsUiProgram": {"status": "PASS", "assertions": 152, **evidence(FULL_UI)},
            "focusedExternalInput": {"status": "PENDING-ENVIRONMENT-FOREGROUND-FAIL", **evidence(FOCUSED_INPUT)},
            "visualPreview": {"status": "PASS", "fileCount": visual["fileCount"], "treeSha256": visual["treeSha256"], "root": VISUAL.relative_to(PROJECT).as_posix()},
            "safeStartup": evidence(INTEGRATION / "startup-diagnostic.stdout.log"),
            "singleInstanceProbe": evidence(INTEGRATION / "single-instance-diagnostic.stdout.log"),
            "singleInstanceCollisionRegression": evidence(INTEGRATION_UNIT),
            "registry": evidence(INTEGRATION / "authority-validate-registry.log"),
            "namedPipeQuery": evidence(INTEGRATION / "integration-startup-canary.stdout.log"),
            "companionInputs": {name: evidence(path) for name, path in companion_inputs.items()},
        },
        "observedSettingsFlow": {
            "content": "GUI both/deepseek-reasoner/5 -> content-config-snapshot/1.1.0 runtimePolicy -> SHA-bound task snapshot",
            "speech": "GUI en-US/voices/rate -> read-aloud v3 snapshot; card task excludes absent speech stage; autoPlay fixed true",
            "anki": "visible explicit probe -> Core resolver injected fake completed/ready; post-authority compatibility confirms zero Desktop direct connection",
            "image": "descriptor v4 + three offline actionIds rendered; fake runner exact; live external-upload action not run",
            "credentials": "DPAPI CurrentUser local store; zero credential files or known plaintext values in candidate",
            "retention": "legacy creation-time deleter disabled at production startup; state-aware service remains unavailable",
        },
        "safety": {"externalWorkerRuns": 0, "externalProductionApis": 0, "physicalSpeaker": 0, "remNoteWrites": 0, "ankiConnectionsOrWrites": 0, "mineruUploads": 0, "existingUserProcessTerminated": False},
        "conditionalAndNoGo": [
            "Content promptAfterRecognition remains conditional-core-ui",
            "Speech preview and voice discovery remain conditional; physical audio was not run",
            "RemNote probe remains conditional; no real RemNote read/write was run",
            "state-aware retention service is unsupported; legacy deleter remains disabled",
            "original media destination remains terminal unsupported",
            "physical external-app Ctrl+C/selection focus and mixed-DPI multi-monitor interaction remain environment/user canaries",
            "Quality candidate-bound gate has not yet promoted this as a user hand-test entrypoint",
        ],
        "supersedes": [
            "releases/kc-integrated-settings-v1212-20260830T175000Z/win-x64 (desktop receipt omitted nested descriptors/contracts)",
            "releases/kc-integrated-settings-v1212-20260830T182000Z/win-x64 (startup diagnostic did not yet accept contained recursive receipt paths)",
        ],
    }
    OUTPUT.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": receipt["status"], "receipt": str(OUTPUT), "receiptSha256": sha(OUTPUT), "candidateTreeSha256": sealed["treeSha256"], "entrypointSha256": receipt["candidate"]["entrypointSha256"]}, ensure_ascii=False))


if __name__ == "__main__":
    main()
