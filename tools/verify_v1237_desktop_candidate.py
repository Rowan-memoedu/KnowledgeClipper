from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from verify_settings_control_plane import scan_runtime_tree_for_secrets


PROJECT = Path(__file__).resolve().parents[1]
CANDIDATE = PROJECT / "releases/kc-i-module-actions-v1237-20260901T075500Z/win-x64"
DESKTOP_SOURCE = PROJECT / "dist/desktop-win-x64/knowledge-clipper-module-actions-v1237-20260901T075000Z"
DESKTOP_RECEIPT = PROJECT / "receipts/desktop-integrated-module-actions-v1.2.37-20260901T080500Z.json"
AUTHORITY_SOURCE = PROJECT / "releases/knowledge-clipper-authority-v1.2.37-20260901T063000Z/win-x64"
AUTHORITY_META = PROJECT / "contracts/authority/worker-registry/v1.2.37-20260901T063000Z"
AUTHORITY_RECEIPT = PROJECT / "receipts/knowledge-clipper-authority-1.2.37-20260901T063200Z.json"
CORE_CHECKPOINT = PROJECT / "contracts/core/v2.9/checkpoints/v2.9.4-20260901T062900Z/checkpoint.json"
AUTHORITY_QUALITY = PROJECT / "canary/quality-authority-v1.2.37/20260901T064000Z-independent/quality-receipt.json"
HISTORICAL_PLAYBACK = PROJECT / "canary/quality-next-desktop-regressions/20260901T052000Z-core-precheckpoint/quality-receipt.json"
PRODUCER_GATES = PROJECT / "canary/desktop-integrated/kc-i-module-actions-v1237-20260901T075500Z/candidate-gates-20260901T080000Z"
WINDOWS_UI_LOG = PROJECT / "canary/desktop-windows/desktop-v1237-module-actions-20260901T074500Z/windows-ui-automation.log"
HAND_TEST = PROJECT / "canary/desktop-integrated/kc-i-module-actions-v1237-20260901T075500Z/USER-HAND-TEST.md"

EXPECTED = {
    "desktopReceipt": "BBE56793488B0A1C022364E53204EF3E251F78FDD59ADF35FA4B2166A9926F3C",
    "manifest": "6A14128A4E927BC5C614F672B7F6B160A3232B7A96C9C4C2AEC28162386677E3",
    "payload": (1063, 432264615, "01A063733F489869390A470467359F3D9ECC1DEFAB8422D0553969401D635BF2"),
    "sealed": (1064, 432490902, "FA9D6CA33CB86F23537C326D0FF0CE1D24F8A5BF2221D16724FB52F138E94F90"),
    "entrypoint": "29320D1CCAA252057D011490866CC7533901740C5C6921359B7A2F9F58C4720C",
    "desktopDll": "3AB422A2B9CDA1672F5F8D1942D9B35C9178154F92A627A9C906F3E291B18DE6",
    "desktopBuildReceipt": "88AA3215112FF59B44D87721EF7AC16FC28EB7A64DEA401D120D9B7E38229291",
    "desktop": (645, 169868871, "AEA6BD97875E6009C753B19B3A41F8B0B78C54859493685697D90C4CDD45A20F"),
    "authority": (404, 262238747, "973E63B7F876A650D6EF4CFAF6D31F6386510BDE81FB2F096F83C403982CECCC"),
    "authorityCheckpoint": "6C9ABC96A05EFFE1E09FB7E3C78BF4596654BE61B05826E16E1FCABF54C89018",
    "authorityRegistry": "1C2E21A92198C25C52DDD6B528F3A50A00E90B741557EC2C22E2FB3C9460EDCC",
    "authorityReceipt": "C7063AD026F4FCF4738A653C6C19271BC9C625F1AD80918C6E6C7A8098B1D20D",
    "coreCheckpoint": "FF766146F8D8EF67F98B75B3BA4345F98C50E5F6A5B9023CB2DA1BAC7A0C9179",
    "authorityQuality": "EA9130B02AAEC2F67E6913E46679CD9C12AE3AB8572E62A5D5A6DD7D2DC04797",
    "historicalPlayback": "6007DBE8EAB70DAFC5D8961A85F3A8D55979F3C949ABE39035EAD659ED959969",
}


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def native(path: Path) -> str:
    value = str(path.resolve())
    if os.name == "nt" and not value.startswith("\\\\?\\"):
        return "\\\\?\\" + value
    return value


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with open(native(path), "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load(path: Path) -> dict[str, Any]:
    with open(native(path), "r", encoding="utf-8-sig") as stream:
        return json.load(stream)


def rel(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def ev(path: Path) -> dict[str, Any]:
    return {"path": rel(path), "bytes": os.stat(native(path)).st_size, "sha256": sha256(path)}


def rows(root: Path, exclude: set[str] | None = None) -> list[dict[str, Any]]:
    excluded = exclude or set()
    result = []
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(root).as_posix()
        if relative in excluded:
            continue
        result.append({"path": relative, "bytes": os.stat(native(path)).st_size, "sha256": sha256(path)})
    return sorted(result, key=lambda item: item["path"])


def aggregate(items: list[dict[str, Any]]) -> dict[str, Any]:
    material = "\n".join(f"{item['path']}\t{item['bytes']}\t{item['sha256'].lower()}" for item in items)
    return {
        "fileCount": len(items),
        "totalBytes": sum(item["bytes"] for item in items),
        "treeSha256": hashlib.sha256(material.encode("utf-8")).hexdigest().upper(),
    }


def desktop_build_tree(items: list[dict[str, Any]]) -> str:
    material = "".join(f"{item['path']}\n{item['sha256'].lower()}\n{item['bytes']}\n" for item in sorted(items, key=lambda value: value["path"]))
    return hashlib.sha256(material.encode("utf-8")).hexdigest().upper()


def verify_manifest() -> dict[str, Any]:
    manifest_path = CANDIDATE / "candidate-file-manifest.v1.json"
    require(sha256(manifest_path) == EXPECTED["manifest"], "manifest SHA drift")
    manifest = load(manifest_path)
    observed = rows(CANDIDATE, {manifest_path.name})
    sealed = rows(CANDIDATE)
    declared = [{"path": item["path"], "bytes": int(item["bytes"]), "sha256": str(item["sha256"]).upper()} for item in manifest["payload"]["files"]]
    require(declared == observed, "manifest inventory differs from candidate files")
    observed_aggregate = aggregate(observed)
    sealed_aggregate = aggregate(sealed)
    require(tuple(observed_aggregate[key] for key in ("fileCount", "totalBytes", "treeSha256")) == EXPECTED["payload"], "payload aggregate drift")
    require(tuple(sealed_aggregate[key] for key in ("fileCount", "totalBytes", "treeSha256")) == EXPECTED["sealed"], "sealed aggregate drift")
    require((manifest["payload"]["fileCount"], manifest["payload"]["totalBytes"], manifest["payload"]["treeSha256"].upper()) == EXPECTED["payload"], "manifest aggregate claim drift")
    require(manifest["candidateId"] == "kc-i-module-actions-v1237-20260901T075500Z", "candidate ID drift")
    require(manifest.get("candidateHash") is None, "hand-test candidate unexpectedly claims a release candidateHash")
    return {"status": "passed", "manifest": ev(manifest_path), "payload": observed_aggregate, "sealed": sealed_aggregate, "candidateHash": None}


def verify_desktop_equality() -> dict[str, Any]:
    build_receipt_path = DESKTOP_SOURCE / "desktop-build-receipt.v1.json"
    require(sha256(build_receipt_path) == EXPECTED["desktopBuildReceipt"], "Desktop build receipt drift")
    build = load(build_receipt_path)
    source = [{"path": item["path"], "bytes": int(item["bytes"]), "sha256": str(item["sha256"]).upper()} for item in build["files"]]
    require((len(source), sum(item["bytes"] for item in source), desktop_build_tree(source)) == EXPECTED["desktop"], "Desktop receipt inventory aggregate drift")
    require((build["fileCount"], build["totalBytes"], build["treeSha256"].upper()) == EXPECTED["desktop"], "Desktop build receipt aggregate drift")
    mismatches = []
    for item in source:
        source_path = DESKTOP_SOURCE / item["path"]
        target = CANDIDATE / item["path"]
        if not source_path.is_file() or os.stat(native(source_path)).st_size != item["bytes"] or sha256(source_path) != item["sha256"] or not target.is_file() or os.stat(native(target)).st_size != item["bytes"] or sha256(target) != item["sha256"]:
            mismatches.append(item["path"])
    require(not mismatches, f"candidate Desktop payload differs from source build: {mismatches[:5]}")
    require(sha256(CANDIDATE / "KnowledgeClipper.exe") == EXPECTED["entrypoint"], "Desktop entrypoint drift")
    require(sha256(CANDIDATE / "KnowledgeClipper.dll") == EXPECTED["desktopDll"], "Desktop DLL drift")
    actual_source_paths = {item["path"] for item in rows(DESKTOP_SOURCE)}
    declared_paths = {item["path"] for item in source}
    source_control_files = sorted(actual_source_paths - declared_paths)
    require(source_control_files == ["desktop-build-receipt.v1.json"], f"unexpected source files outside build inventory: {source_control_files}")
    return {"status": "passed", "source": rel(DESKTOP_SOURCE), "filesCompared": len(source), "changedFiles": 0, "treeSha256": desktop_build_tree(source), "treeAlgorithm": build["treeHashAlgorithm"], "sourceControlFilesExcluded": source_control_files, "buildReceipt": ev(build_receipt_path)}


def verify_authority() -> dict[str, Any]:
    source = rows(AUTHORITY_SOURCE)
    embedded = rows(CANDIDATE / "authority/win-x64")
    require(source == embedded, "embedded authority subtree differs from admitted v1.2.37")
    summary = aggregate(source)
    require(tuple(summary[key] for key in ("fileCount", "totalBytes", "treeSha256")) == EXPECTED["authority"], "authority aggregate drift")
    exact = {
        AUTHORITY_META / "checkpoint.json": EXPECTED["authorityCheckpoint"],
        AUTHORITY_META / "worker-registry.v1.json": EXPECTED["authorityRegistry"],
        AUTHORITY_RECEIPT: EXPECTED["authorityReceipt"],
        CORE_CHECKPOINT: EXPECTED["coreCheckpoint"],
        AUTHORITY_QUALITY: EXPECTED["authorityQuality"],
    }
    for path, expected in exact.items():
        require(sha256(path) == expected, f"authority/Core evidence drift: {path}")
    require(sha256(CANDIDATE / "authority/metadata/checkpoint.json") == EXPECTED["authorityCheckpoint"], "embedded authority checkpoint drift")
    require(sha256(CANDIDATE / "authority/metadata/worker-registry.v1.json") == EXPECTED["authorityRegistry"], "embedded authority registry drift")
    config = load(CANDIDATE / "candidate-integration.json")
    require(config["AuthorityCheckpointId"] == "knowledge-clipper-authority-1.2.37-20260901T063200Z", "candidate authority checkpoint binding drift")
    require(config["AuthorityTreeSha256"].upper() == EXPECTED["authority"][2], "candidate authority tree binding drift")
    require(config["ReleaseRoot"] == "authority/win-x64" and config["Registry"] == "authority/metadata/worker-registry.v1.json", "candidate authority paths drift")
    require(config["IpcConfig"].endswith("orchestrator-ipc.candidate.v1_4_0.json"), "candidate is not bound to IPC 1.4")
    ipc = load(CANDIDATE / config["IpcConfig"])
    require(ipc["schemaVersion"] == "1.4.0" and ipc["pipeName"] == config["PipeName"], "IPC 1.4 config drift")
    return {"status": "passed", "version": "1.2.37", **summary, "checkpoint": ev(AUTHORITY_META / "checkpoint.json"), "registry": ev(AUTHORITY_META / "worker-registry.v1.json"), "authorityReceipt": ev(AUTHORITY_RECEIPT), "coreCheckpoint": ev(CORE_CHECKPOINT), "priorIndependentAdmission": ev(AUTHORITY_QUALITY), "integrationConfig": ev(CANDIDATE / "candidate-integration.json")}


def verify_receipt_recursively() -> dict[str, Any]:
    require(sha256(DESKTOP_RECEIPT) == EXPECTED["desktopReceipt"], "Desktop final receipt SHA drift")
    receipt = load(DESKTOP_RECEIPT)
    checked: dict[str, dict[str, Any]] = {}

    def walk(value: Any) -> None:
        if isinstance(value, dict):
            if isinstance(value.get("path"), str) and isinstance(value.get("sha256"), str):
                path = PROJECT / value["path"]
                require(path.is_file(), f"referenced evidence missing: {value['path']}")
                require(sha256(path) == value["sha256"].upper(), f"referenced evidence SHA drift: {value['path']}")
                if "bytes" in value:
                    require(os.stat(native(path)).st_size == int(value["bytes"]), f"referenced evidence bytes drift: {value['path']}")
                checked[value["path"]] = ev(path)
            for child in value.values():
                walk(child)
        elif isinstance(value, list):
            for child in value:
                walk(child)

    walk(receipt)
    require(receipt["status"] == "PASS-CANDIDATE-BOUND-USER-HANDTEST-READY", "Desktop producer receipt status drift")
    require(receipt["authority"]["version"] == "1.2.37", "Desktop producer receipt authority drift")
    require(receipt["moduleActions"] == {
        "ipc": "1.4",
        "requestSchema": "knowledge-clipper-module-action.v3",
        "remNoteProbeAndDiscovery": "public-read-only",
        "speechDiscovery": "public-read-only",
        "speechPreview": "explicit-physical-audio-only",
        "ankiProbe": "read-only",
        "ankiInitializeManaged": "explicit-sha-bound-public-action",
        "automaticInitialization": False,
    }, "Desktop module-action summary drift")
    return {"status": "passed", "receipt": ev(DESKTOP_RECEIPT), "referencedEvidenceChecked": len(checked)}


def verify_semantics(fresh_test_log: Path) -> dict[str, Any]:
    producer_log = WINDOWS_UI_LOG.read_text(encoding="utf-8-sig")
    fresh = fresh_test_log.read_text(encoding="utf-8-sig")
    markers = [
        "WINDOWS_UI_CANARY PASS assertions=210",
        "PASS authority-admitted Anki initialization dispatches through public IPC 1.4",
        "PASS Desktop creates the canonical immutable package with exact SHA-bound config and explicit authorization",
        "PASS Anki managed initialization cannot run before a typed read-only probe reports needs_initialization",
        "PASS read-only Anki probe projects missing managed routes and never initializes implicitly",
        "PASS RemNote discovery sends only the exact-title selector through the public v2 action and never supplies a guessed ID",
        "PASS a unique exact-readback discovery updates only the pending form binding for the next-task save",
        "PASS original-image task manifest forces the exact sole destination [anki] and publishes the SHA-bound Anki config",
    ]
    for marker in markers:
        require(marker in producer_log or marker in fresh, f"semantic evidence missing: {marker}")
    require("PASS named-pipe-public-module-actions-v1.2-v1.3-v1.4" in fresh, "fresh IPC module-action test missing")
    require("PASS question-follow-up-cards" in fresh, "fresh question follow-up test group missing")
    test_source = (PROJECT / "tests/RemNoteQuickClipper.Desktop.Tests/Program.cs").read_text(encoding="utf-8-sig")
    require("follow-up publishes the canonical ordinal question.turn artifact identity" in test_source, "canonical follow-up assertion missing from tested source")
    require("PASS desktop automation build and tests" in fresh, "fresh Desktop test suite did not finish")
    checklist = HAND_TEST.read_text(encoding="utf-8-sig")
    for phrase in ("KnowledgeClipper.exe", "只读连接检查", "初始化受管资源", "原图模式固定只导出 Anki", "RemNote 原图不作为检查项", "使用原文继续", "编辑后继续", "跨屏异 DPI"):
        require(phrase in checklist, f"hand-test checklist missing: {phrase}")
    return {"status": "passed", "freshOfflineSuite": ev(fresh_test_log), "windowsUiEvidence": ev(WINDOWS_UI_LOG), "windowsUiAssertions": 210, "handTestChecklist": ev(HAND_TEST)}


def run_json(command: list[str], cwd: Path, timeout: int = 60) -> tuple[dict[str, Any], str, str]:
    completed = subprocess.run(command, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout, check=False)
    require(completed.returncode == 0, f"command failed ({completed.returncode}): {completed.stderr or completed.stdout}")
    require(completed.stdout.strip(), f"command returned no JSON: {command}")
    return json.loads(completed.stdout), completed.stdout, completed.stderr


def verify_runtime(output_root: Path) -> dict[str, Any]:
    runtime = output_root / "runtime"
    runtime.mkdir(parents=True, exist_ok=False)
    startup, out, err = run_json([str(CANDIDATE / "KnowledgeClipper.exe"), "--startup-diagnostic"], CANDIDATE)
    (runtime / "startup.stdout.json").write_text(out, encoding="utf-8")
    (runtime / "startup.stderr.log").write_text(err, encoding="utf-8")
    require(startup["result"] == "PASS" and startup["externalConnections"] == 0 and startup["namedPipeInvoked"] is False, "fresh startup diagnostic is not side-effect-free")
    authority = CANDIDATE / "authority/win-x64"
    registry, out, err = run_json([str(authority / "orchestrator/KnowledgeClipper.Orchestrator.exe"), "validate-registry", "--registry", str(CANDIDATE / "authority/metadata/worker-registry.v1.json"), "--release-root", str(authority)], authority)
    (runtime / "registry.stdout.json").write_text(out, encoding="utf-8")
    (runtime / "registry.stderr.log").write_text(err, encoding="utf-8")
    require(registry["status"] == "valid" and registry["registryVersion"] == "1.2.37" and registry["workers"] == 5, "fresh registry validation failed")
    single, out, err = run_json([str(CANDIDATE / "KnowledgeClipper.exe"), "--single-instance-diagnostic"], CANDIDATE)
    (runtime / "single-instance.stdout.json").write_text(out, encoding="utf-8")
    (runtime / "single-instance.stderr.log").write_text(err, encoding="utf-8")
    require(single["userFeedbackMode"] == "activate-existing-window-and-message-box", "single-instance feedback mode is silent")
    integration_jobs = output_root / "isolated-jobs"
    query, out, err = run_json([str(CANDIDATE / "KnowledgeClipper.exe"), "--integration-startup-canary", str(integration_jobs)], CANDIDATE, 90)
    (runtime / "integration-query.stdout.json").write_text(out, encoding="utf-8")
    (runtime / "integration-query.stderr.log").write_text(err, encoding="utf-8")
    require(query["result"] == "PASS" and query["orchestratorStartedByDesktop"] and query["namedPipeInvoked"], "fresh candidate Named Pipe query failed")
    require(not any(query[key] for key in ("externalWorkerInvoked", "physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")), "fresh query crossed a forbidden boundary")
    return {"status": "passed", "startup": ev(runtime / "startup.stdout.json"), "registry": ev(runtime / "registry.stdout.json"), "singleInstance": ev(runtime / "single-instance.stdout.json"), "namedPipeQuery": ev(runtime / "integration-query.stdout.json"), "externalWorkerRuns": 0, "externalServices": 0, "destinationWrites": 0, "speakerInvocations": 0}


def verify_closure_and_secrets(output_root: Path) -> dict[str, Any]:
    closure_path = output_root / "portable-schema-closure.json"
    completed = subprocess.run(["python", str(PROJECT / "tools/validate_release_schema_closure.py"), "--release-root", str(CANDIDATE / "authority/win-x64"), "--output", str(closure_path)], cwd=PROJECT, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=90, check=False)
    require(completed.returncode == 0, f"portable schema closure failed: {completed.stderr or completed.stdout}")
    closure = load(closure_path)
    require(closure["status"] == "passed" and closure["schemasChecked"] == 68 and closure["localRefsChecked"] == 196 and not closure["failures"], "portable schema closure drift")
    scan = scan_runtime_tree_for_secrets(CANDIDATE)
    require(scan["status"] == "passed" and scan["findingCount"] == 0, "fresh candidate secret scan failed")
    credential_files = [path.relative_to(CANDIDATE).as_posix() for path in CANDIDATE.rglob("*") if path.is_file() and path.name.casefold() in {"credentials.json", "credentials.json.bak", ".env"}]
    legacy_canaries = [path.relative_to(CANDIDATE).as_posix() for path in CANDIDATE.rglob("*") if path.is_file() and path.name.casefold() in {"smoketest.exe", "ttscanary.exe"}]
    require(not credential_files and not legacy_canaries, "candidate carries credentials or legacy external canaries")
    secret_path = output_root / "secret-scan.json"
    secret_path.write_text(json.dumps({"status": "passed", "findingCount": 0, "credentialFiles": credential_files, "legacyExternalCanaries": legacy_canaries, "scan": scan}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return {"status": "passed", "portableClosure": ev(closure_path), "schemasChecked": 68, "localRefsChecked": 196, "secretScan": ev(secret_path), "secretFindings": 0}


def verify_history() -> dict[str, Any]:
    require(sha256(HISTORICAL_PLAYBACK) == EXPECTED["historicalPlayback"], "historical playback evidence drift")
    data = load(HISTORICAL_PLAYBACK)
    require(data.get("status") != "PASS-CANDIDATE-BOUND-USER-HANDTEST-READY", "historical playback failure was rewritten as current pass")
    return {"status": "retained-immutable", "evidence": ev(HISTORICAL_PLAYBACK), "currentPassDoesNotEraseHistory": True}


def run_fresh_desktop_tests(output_root: Path) -> Path:
    log = output_root / "fresh-desktop-build-and-test.log"
    completed = subprocess.run(
        ["pwsh", "-NoLogo", "-NoProfile", "-File", str(PROJECT / "tests/RemNoteQuickClipper.Desktop.Tests/build-and-test.ps1")],
        cwd=PROJECT,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=3300,
        check=False,
    )
    log.write_text(completed.stdout + ("\n[stderr]\n" + completed.stderr if completed.stderr else ""), encoding="utf-8")
    require(completed.returncode == 0, f"fresh Desktop test suite failed: {completed.stderr or completed.stdout[-2000:]}")
    return log


def verify(fresh_test_log: Path | None, output_root: Path) -> dict[str, Any]:
    require(CANDIDATE.is_dir() and DESKTOP_RECEIPT.is_file(), "required candidate evidence missing")
    require(not output_root.exists(), "append-only output root already exists")
    output_root.mkdir(parents=True)
    if fresh_test_log is None:
        fresh_test_log = run_fresh_desktop_tests(output_root)
    require(fresh_test_log.is_file(), "fresh Desktop test evidence missing")
    checks = {
        "manifestAndSealedTree": verify_manifest(),
        "desktopPayloadEquality": verify_desktop_equality(),
        "authorityV1237ExactBinding": verify_authority(),
        "producerReceiptRecursiveEvidence": verify_receipt_recursively(),
        "freshOfflineDesktopSemantics": verify_semantics(fresh_test_log),
        "freshStartupRegistryIpc": verify_runtime(output_root),
        "freshPortableClosureAndSecrets": verify_closure_and_secrets(output_root),
        "historicalPlaybackEvidence": verify_history(),
    }
    result = {
        "schemaVersion": "knowledge-clipper-quality-desktop-v1.2.37-candidate-bound.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-CANDIDATE-BOUND-AUTOMATED-NOT-HANDTEST-PROMOTABLE",
        "candidateBound": True,
        "candidate": {"root": rel(CANDIDATE), "entrypoint": ev(CANDIDATE / "KnowledgeClipper.exe"), "desktopDll": ev(CANDIDATE / "KnowledgeClipper.dll"), "manifest": ev(CANDIDATE / "candidate-file-manifest.v1.json"), "sealed": checks["manifestAndSealedTree"]["sealed"], "candidateHash": None},
        "desktopFinalReceipt": ev(DESKTOP_RECEIPT),
        "authority": {"version": "1.2.37", "treeSha256": EXPECTED["authority"][2], "exactlyEmbedded": True},
        "capabilities": {
            "ankiReadOnlyProbe": "PASS-TYPED-STATUS-OFFLINE",
            "ankiInitializeManaged": "PASS-EXPLICIT-ONLY-IMMUTABLE-SHA-BOUND; AUTOMATIC-INITIALIZATION=0; REAL-WRITES=0",
            "remnoteExactTitleDiscovery": "PASS-EXACT-TITLE-ONLY; UNIQUE-EXACT-READBACK-REQUIRED-BEFORE-NEXT-TASK-SAVE",
            "questionFollowUp": "PASS-CANONICAL-QUESTION-TURN-CONTINUATION",
            "originalDestinations": ["anki"],
            "remnoteOriginalMedia": "BY-DESIGN-UNSUPPORTED-NONBLOCKING-NOT-A-GAP",
        },
        "checks": checks,
        "promotion": {"userHandTestEntrypoint": False, "userHandTest": "NO-GO-SEALED-PROCESS-STATEFUL-EVIDENCE-MISSING", "formalRelease": "NO-GO-NOT-SIGNED-NO-INSTALLER-NO-CANDIDATE-HASH", "candidateEntrypointObserved": rel(CANDIDATE / "KnowledgeClipper.exe")},
        "manualBoundaries": {"externalApplicationContext": "manual", "mixedDpiCrossScreen": "manual", "realDestinationWrites": "explicit-user-action-only", "resize": "existing-user-PASS-not-repeated"},
        "sideEffectsByThisAudit": {"externalWorkerRuns": 0, "externalServiceCalls": 0, "remnoteCalls": 0, "ankiCalls": 0, "mineruCalls": 0, "deepseekCalls": 0, "physicalSpeakerInvocations": 0, "destinationWrites": 0},
    }
    receipt = output_root / "quality-candidate-receipt.json"
    receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    readable = output_root / "READABLE.md"
    readable.write_text(
        "# v1.2.37 Desktop candidate-bound quality\n\n"
        f"- Result: `{result['status']}`\n"
        f"- Candidate entrypoint observed: `{result['promotion']['candidateEntrypointObserved']}`\n"
        "- User hand-test promotion: `NO-GO`; this verifier has component/automated evidence but no sealed-process stateful journey evidence.\n"
        f"- Sealed tree: `{EXPECTED['sealed'][2]}`\n"
        f"- Authority: v1.2.37 / `{EXPECTED['authority'][2]}`\n"
        "- Anki probe is read-only; managed initialization is explicit-only and produced zero real writes in this audit.\n"
        "- RemNote exact-title discovery never guesses an ID and saves only a unique exact-readback binding for the next task.\n"
        "- Original mode is Anki-only. RemNote original media is intentionally unsupported and nonblocking.\n"
        "- Historical playback failure remains immutable regression evidence; this PASS does not erase it.\n"
        "- External-app context and mixed-DPI remain hand-test boundaries. Formal release is not signed and has no installer/candidateHash.\n",
        encoding="utf-8",
    )
    return {"receipt": receipt, "readable": readable, "result": result}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--fresh-test-log", type=Path)
    parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()
    output = verify(args.fresh_test_log.resolve() if args.fresh_test_log else None, args.output_root.resolve())
    print(json.dumps({"status": output["result"]["status"], "receipt": ev(output["receipt"]), "readable": ev(output["readable"]), "candidateTreeSha256": EXPECTED["sealed"][2]}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
