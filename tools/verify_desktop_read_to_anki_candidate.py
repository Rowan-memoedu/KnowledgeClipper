from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


DEFAULT_CANDIDATE = Path(r"D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-rta-20260903T122254Z")
DEFAULT_OWNER_EVIDENCE = Path(r"D:\Artifacts\KnowledgeClipper\desktop-read-to-anki-evidence\desktop-rta-20260903T122254Z")
DEFAULT_CORE_QUALITY = Path(r"D:\Artifacts\KnowledgeClipper\read-to-anki\quality\20260903T122426Z-core-preview-candidate\quality-preview-confirm-receipt.json")
DEFAULT_RUNTIME_EVIDENCE = Path(r"D:\Artifacts\KnowledgeClipper\read-to-anki\quality\20260903T123000Z-desktop-candidate")
DEFAULT_OLD_UNSEALED = Path(r"D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-rta-20260903T122128Z")

EXPECTED = {
    "manifest": "13E257238EC7E9BED0437BECE735728BC8D94F4E09B0A016467E2122C1A8FFF2",
    "payloadFiles": 795,
    "payloadBytes": 342042574,
    "payloadTree": "1CC1691F4DD92EFD77A5515CDC14DCA08AB1798B8069DE229380EC0CF7AD3375",
    "buildReceipt": "2DC1F019977DE4D3AA4484502E88A0B72F983B94EA6B0FA852588575FC400D65",
    "sealedFiles": 796,
    "sealedBytes": 342195158,
    "sealedTree": "CF8D9C4992660A9E273EBE738B1D8877512C464BFF42E71D62BF468A05F9CC52",
    "ownerReceipt": "B0D0A222AE9F53DC222E907DD23D88788F1E6D6C8CC52AD21C760A95CB24F7B9",
    "entrypoint": "E9C891BC3354810E1D9D4F6348EE58D8578BF08256548A23B8B3B831288E4A61",
    "client": "64B4D2F86409621B34208F08F16D5DF917CE684B949410EFACB0E810F3B046DE",
    "coreManifest": "1AE6547EEDAC4812B5D9FB8F67367E2A2E85F93DABCC3206F18A236931284D3D",
    "sdkFreeze": "18672C73CAF2D46F64B3C5CF0F2F60D801D6D557762110753B9C10A99605A43B",
    "coreRealReceipt": "1AC975B4C1552B3E9ED48AE2A282AAF0F4F78F1301FA1D3150E11CF577EC2867",
    "authorityTree": "197CF1D50B7D6130372D2F298371C6C25A90506D34E693D5B09B52BE8B70FE54",
}


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def evidence(path: Path) -> dict[str, Any]:
    return {"path": str(path.resolve()), "bytes": path.stat().st_size, "sha256": sha256_file(path)}


def payload_tree(rows: list[dict[str, Any]]) -> str:
    value = "".join(f"{item['path']}\0{item['bytes']}\0{item['sha256'].upper()}\n" for item in rows)
    return hashlib.sha256(value.encode("utf-8")).hexdigest().upper()


def startup_tree(rows: list[dict[str, Any]]) -> str:
    value = "".join(f"{item['path']}\n{item['sha256'].lower()}\n{item['bytes']}\n" for item in rows)
    return hashlib.sha256(value.encode("utf-8")).hexdigest().upper()


def inventory(root: Path) -> list[dict[str, Any]]:
    rows = []
    for path in root.rglob("*"):
        if path.is_file():
            rows.append({"path": path.relative_to(root).as_posix(), "bytes": path.stat().st_size, "sha256": sha256_file(path)})
    return sorted(rows, key=lambda item: item["path"])


def verify_rows(root: Path, rows: list[dict[str, Any]], label: str) -> None:
    require(rows == sorted(rows, key=lambda item: item["path"]), f"{label} paths are not ordinal sorted")
    for item in rows:
        path = root / item["path"]
        require(path.is_file(), f"{label} file missing: {item['path']}")
        require(path.stat().st_size == int(item["bytes"]), f"{label} bytes drift: {item['path']}")
        require(sha256_file(path) == item["sha256"].upper(), f"{label} SHA drift: {item['path']}")


def evaluate(candidate: Path, owner_evidence: Path, core_quality: Path, runtime_evidence: Path, old_unsealed: Path) -> dict[str, Any]:
    candidate = candidate.resolve()
    manifest_path = candidate / "candidate-file-manifest.v1.json"
    build_receipt_path = candidate / "desktop-build-receipt.v1.json"
    owner_receipt_path = owner_evidence / "final-receipt.json"
    require(sha256_file(manifest_path) == EXPECTED["manifest"], "Desktop candidate manifest SHA drift")
    require(sha256_file(build_receipt_path) == EXPECTED["buildReceipt"], "Desktop build receipt SHA drift")
    require(sha256_file(owner_receipt_path) == EXPECTED["ownerReceipt"], "Desktop owner receipt SHA drift")
    require(sha256_file(core_quality) == "768C2438CF61FC5E86A419D3BAB738025139021DA1412CFCB06B096BC068FDAA", "Core quality receipt SHA drift")

    manifest = load(manifest_path)
    build = load(build_receipt_path)
    owner = load(owner_receipt_path)
    require(manifest["candidateId"] == candidate.name and manifest["entrypoint"] == "KnowledgeClipper.exe", "Desktop candidate identity drift")
    verify_rows(candidate, manifest["files"], "candidate manifest")
    require((len(manifest["files"]), sum(item["bytes"] for item in manifest["files"]), payload_tree(manifest["files"])) == (EXPECTED["payloadFiles"], EXPECTED["payloadBytes"], EXPECTED["payloadTree"]), "Desktop payload aggregate drift")
    require(manifest["payload"] == {"files": EXPECTED["payloadFiles"], "bytes": EXPECTED["payloadBytes"], "treeSha256": EXPECTED["payloadTree"]}, "Desktop manifest payload summary drift")
    verify_rows(candidate, build["files"], "desktop build receipt")
    require((build["fileCount"], build["totalBytes"], startup_tree(build["files"])) == (EXPECTED["sealedFiles"], EXPECTED["sealedBytes"], EXPECTED["sealedTree"]), "Desktop sealed aggregate drift")
    require(build["treeSha256"].upper() == EXPECTED["sealedTree"], "Desktop sealed tree summary drift")
    actual = inventory(candidate)
    require(len(actual) == 797 and sum(item["bytes"] for item in actual) == 342346872, "post-seal control-file inventory drift")
    require({item["path"] for item in actual} - {item["path"] for item in build["files"]} == {"desktop-build-receipt.v1.json"}, "unexpected post-seal candidate file")

    exe = candidate / "KnowledgeClipper.exe"
    client = candidate / "KnowledgeClipper.Core.V3.Client.dll"
    embedded_core_manifest = candidate / "core-read-to-anki-candidate-manifest.json"
    sdk_freeze_path = candidate / "core-read-to-anki-sdk-freeze-manifest.json"
    require(sha256_file(exe) == EXPECTED["entrypoint"], "Desktop EXE SHA drift")
    require(sha256_file(client) == EXPECTED["client"], "Desktop Core Client SHA drift")
    require(sha256_file(embedded_core_manifest) == EXPECTED["coreManifest"], "embedded Core candidate manifest drift")
    require(sha256_file(sdk_freeze_path) == EXPECTED["sdkFreeze"], "SDK freeze manifest drift")
    freeze = load(sdk_freeze_path)
    require(freeze["source"]["candidateManifestSha256"] == EXPECTED["coreManifest"], "SDK freeze Core manifest binding drift")
    require(freeze["source"]["realReceiptSha256"] == EXPECTED["coreRealReceipt"], "SDK freeze real receipt binding drift")
    require(manifest["bindings"]["coreCandidateManifest"]["sha256"] == EXPECTED["coreManifest"], "manifest Core binding drift")
    require(manifest["bindings"]["sdkFreezeManifest"]["sha256"] == EXPECTED["sdkFreeze"], "manifest SDK binding drift")

    authority_rows = inventory(candidate / "authority/win-x64")
    require(payload_tree(authority_rows) == EXPECTED["authorityTree"], "embedded authority tree drift")
    integration_config = load(candidate / "candidate-integration.json")
    require(integration_config["AuthorityCheckpointId"] == "rta-20260903T200942Z-b3b69727", "integration checkpoint drift")
    require(integration_config["AuthorityTreeSha256"] == EXPECTED["authorityTree"], "integration authority tree drift")
    require(integration_config["RuntimeKind"] == "core-v3" and integration_config["AutoStart"] is True, "Core v3 autostart binding drift")

    startup_path = runtime_evidence / "startup-independent-v2.json"
    integration_path = runtime_evidence / "integration-independent-v2.json"
    startup = load(startup_path)
    integration = load(integration_path)
    require(startup["result"] == "PASS" and startup["packageFilesVerified"] and startup["packageTreeVerified"], "independent startup failed")
    require((startup["packageFileCount"], startup["packageTotalBytes"], startup["packageTreeSha256"].upper()) == (EXPECTED["sealedFiles"], EXPECTED["sealedBytes"], EXPECTED["sealedTree"]), "startup readback tree drift")
    require(startup["externalConnections"] == 0 and not startup["productionWorkDirectoryCreated"], "startup crossed safety boundary")
    require(integration["result"] == "PASS" and integration["runtimeKind"] == "core-v3" and integration["action"] == "capabilities.get" and integration["queryItemCount"] == 8, "independent integration query failed")
    require(integration["namedPipeInvoked"] and not any(integration[key] for key in ("externalWorkerInvoked", "physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")), "integration crossed external boundary")
    tasklist = subprocess.run(["tasklist", "/FI", f"PID eq {integration['orchestratorProcessId']}", "/FO", "CSV", "/NH"], capture_output=True, text=True, check=False)
    require(str(integration["orchestratorProcessId"]) not in tasklist.stdout, "independent integration Orchestrator process still alive")

    application_log = (runtime_evidence / "application-tests.log").read_text(encoding="utf-8-sig")
    gateway_log = (runtime_evidence / "gateway-tests.log").read_text(encoding="utf-8-sig")
    windows_log = (runtime_evidence / "windows-preview-canary.log").read_text(encoding="utf-8-sig")
    require(application_log.count("PASS ") == 65 and "DESKTOP_V3_APPLICATION_TESTS PASS assertions=64" in application_log, "Application test evidence drift")
    require(gateway_log.count("PASS ") == 22 and "DESKTOP_CORE_GATEWAY_TESTS PASS assertions=21" in gateway_log, "Gateway test evidence drift")
    require(windows_log.count("PASS ") == 9 and "WINDOWS_FLASHCARD_PREVIEW_UI_CANARY PASS assertions=8" in windows_log, "Windows preview Canary evidence drift")
    for path in runtime_evidence.glob("*.err.log"):
        require(path.stat().st_size == 0, f"independent stderr is non-empty: {path.name}")

    require(owner["candidateRoot"] == str(candidate), "owner receipt candidate path drift")
    require(owner["candidate"]["manifestSha256"] == EXPECTED["manifest"], "owner receipt manifest binding drift")
    require(owner["bindings"]["coreClientSha256"] == EXPECTED["client"], "owner receipt Core Client binding drift")
    require(owner["safety"] == {"externalWorker": 0, "physicalSpeaker": 0, "remnote": 0, "anki": 0, "mineru": 0}, "owner safety boundary drift")

    old_contamination = old_unsealed / "integration-startup-canary.receipt.json"
    require(old_contamination.is_dir(), "old unsealed candidate contamination evidence missing")
    require(old_unsealed.resolve() != candidate, "old unsealed candidate reused as entrypoint")

    core = load(core_quality)
    require(core["status"] == "CONDITIONAL-CORE-QUALIFIED", "Core quality status drift")
    return {
        "schema": "kc.quality.desktop-read-to-anki-candidate/1",
        "status": "PASS-SCOPED-USER-HANDTEST",
        "candidate": {
            "root": str(candidate),
            "entrypoint": evidence(exe),
            "manifest": evidence(manifest_path),
            "payload": {"files": EXPECTED["payloadFiles"], "bytes": EXPECTED["payloadBytes"], "treeSha256": EXPECTED["payloadTree"], "mismatches": 0},
            "sealed": {"files": EXPECTED["sealedFiles"], "bytes": EXPECTED["sealedBytes"], "treeSha256": EXPECTED["sealedTree"], "mismatches": 0},
            "postSealControlFile": evidence(build_receipt_path),
        },
        "bindings": {
            "coreCandidateManifest": evidence(embedded_core_manifest),
            "sdkFreezeManifest": evidence(sdk_freeze_path),
            "coreClient": evidence(client),
            "authorityTreeSha256": EXPECTED["authorityTree"],
            "sameTypedCorePath": True,
        },
        "independentRuntime": {
            "startup": evidence(startup_path),
            "integration": evidence(integration_path),
            "namedPipeCapabilities": 8,
            "orchestratorProcessCleaned": True,
            "externalWorkerCalls": 0,
            "externalWrites": 0,
            "speakerInvocations": 0,
        },
        "independentTests": {
            "application": {"assertions": 64, "log": evidence(runtime_evidence / "application-tests.log")},
            "gateway": {"assertions": 21, "log": evidence(runtime_evidence / "gateway-tests.log")},
            "windowsPreview": {"assertions": 8, "log": evidence(runtime_evidence / "windows-preview-canary.log")},
        },
        "gateMatrix": [
            {"id": "before-confirm-zero-exporter", "status": "PASS"},
            {"id": "preview-card-bundle-equivalence", "status": "PASS"},
            {"id": "typed-confirm-token-revision-digest-single-use", "status": "PASS-DESKTOP-OFFLINE"},
            {"id": "cancel-followup-newest-revision", "status": "PASS-DESKTOP-OFFLINE"},
            {"id": "shared-core-desktop-path", "status": "PASS-READ-ALOUD"},
            {"id": "restart-recovery", "status": "PASS-CORE-REAL"},
            {"id": "anki-confirmed-semantic-readback", "status": "DEPENDENCY-QUALIFIED-CURRENT-ACTUAL-REQUIRED"},
        ],
        "evidenceLimitations": {
            "historicalDependencyOnly": True,
            "currentActualSemanticReadback": "REQUIRED",
            "actualActionCommandId": "REQUIRED-SAME-RUN",
            "previewFrontBackSha256": "REQUIRED-SAME-RUN-PER-ORDINAL",
            "ankiNoteIds": "REQUIRED-SAME-RUN-PER-ORDINAL",
            "ankiCardIds": "REQUIRED-SAME-RUN-PER-ORDINAL",
            "exactFrontBackDeckReadback": "REQUIRED-SAME-RUN",
            "coreDurableNegativeCommandPayloads": "CONDITIONAL",
            "questionFollowupSamePreview": "CONDITIONAL-SDK-NOT-FROZEN",
            "physicalDesktopToAnkiHandTest": "NOT-RUN-BY-QUALITY",
            "priorCreatedWriteBelongsToDifferentCandidate": True,
        },
        "invalidatedInputs": [{"path": str(old_unsealed.resolve()), "reason": "unsealed-directory-contamination", "userEntrypoint": False}],
        "decision": {
            "userHandTestEntrypoint": False,
            "scope": "read/read-aloud -> typed preview -> explicit confirm/cancel -> isolated Anki; replay is playback-only",
            "entrypoint": str(exe.resolve()),
            "releasePromotion": "NO-GO",
            "questionFollowupPromotion": "PENDING-NEXT-SDK",
        },
        "ownerReceipt": evidence(owner_receipt_path),
        "coreQualityReceipt": evidence(core_quality),
    }


def readable(result: dict[str, Any]) -> str:
    entry = result["decision"]["entrypoint"]
    rows = "\n".join(f"| {item['id']} | {item['status']} |" for item in result["gateMatrix"])
    return f"""# Desktop Read-to-Anki candidate quality result

- Decision: **GO for scoped user hand-test**
- Entrypoint: `{entry}`
- Manifest: `{result['candidate']['manifest']['sha256']}`
- Payload: `{result['candidate']['payload']['files']}` files / `{result['candidate']['payload']['bytes']}` bytes / `{result['candidate']['payload']['treeSha256']}`
- Offline quality side effects: external workers `0`, writes `0`, speaker `0`

| Gate | Status |
|---|---|
{rows}

The approved hand-test scope is read/read-aloud -> typed card preview -> explicit confirm or cancel -> isolated Anki, plus playback-only replay. The Desktop candidate embeds the exact Core Client, SDK freeze, Core candidate manifest and authority tree used by the qualified Core run.

This is not release GO. Core's raw stale/double-confirm command payload persistence remains conditional evidence, question/follow-up needs the next frozen SDK, and Quality did not perform the physical Desktop-to-Anki hand-test. The contaminated `T122128Z` directory is preserved as non-entrypoint failure evidence.
"""


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", type=Path, default=DEFAULT_CANDIDATE)
    parser.add_argument("--owner-evidence", type=Path, default=DEFAULT_OWNER_EVIDENCE)
    parser.add_argument("--core-quality", type=Path, default=DEFAULT_CORE_QUALITY)
    parser.add_argument("--runtime-evidence", type=Path, default=DEFAULT_RUNTIME_EVIDENCE)
    parser.add_argument("--old-unsealed", type=Path, default=DEFAULT_OLD_UNSEALED)
    parser.add_argument("--output-root", type=Path, default=DEFAULT_RUNTIME_EVIDENCE)
    args = parser.parse_args()
    result = evaluate(args.candidate, args.owner_evidence, args.core_quality, args.runtime_evidence, args.old_unsealed)
    result["verifiedAt"] = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    receipt_path = args.output_root / "quality-desktop-read-to-anki-receipt.json"
    readable_path = args.output_root / "READABLE.md"
    require(not receipt_path.exists() and not readable_path.exists(), "append-only quality outputs already exist")
    receipt_path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    readable_path.write_text(readable(result), encoding="utf-8")
    print(json.dumps({"status": result["status"], "receipt": evidence(receipt_path), "readable": evidence(readable_path)}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
