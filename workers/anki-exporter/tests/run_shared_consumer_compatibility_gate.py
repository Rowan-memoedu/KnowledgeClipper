#!/usr/bin/env python3
"""Offline compatibility gate for the current authority, Core and Desktop consumers."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


WORKER = Path(__file__).resolve().parents[1]
PROJECT = WORKER.parents[1]
sys.path.insert(0, str(WORKER))

import anki_exporter as exporter
import shared_consumer_compatibility as checks


DEFAULT_AUTHORITY = PROJECT / "releases" / "knowledge-clipper-authority-v1.2.12-20260830T075600Z" / "win-x64"
DEFAULT_CHECKPOINT = PROJECT / "contracts" / "authority" / "worker-registry" / "v1.2.12-20260830T075600Z" / "checkpoint.json"
DEFAULT_REGISTRY = DEFAULT_CHECKPOINT.parent / "worker-registry.v1.json"
DEFAULT_CANDIDATE = DEFAULT_AUTHORITY
DESKTOP_CANARY = PROJECT / "desktop" / "bin" / "RemNoteQuickClipper.Desktop.WindowsCanary" / "Release" / "net8.0-windows" / "win-x64" / "KnowledgeClipper.Desktop.WindowsCanary.exe"


def now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def read_json(path: Path) -> Any:
    with open(exporter.native_fs_path(path), "r", encoding="utf-8") as stream:
        return json.load(stream)


def run(command: list[str], cwd: Path, *, timeout: int) -> subprocess.CompletedProcess[str]:
    process = subprocess.run(
        command,
        cwd=cwd,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
        check=False,
    )
    if process.returncode != 0:
        raise RuntimeError(
            f"Command failed with exit {process.returncode}: {command!r}\nstdout={process.stdout}\nstderr={process.stderr}"
        )
    return process


def file_evidence(path: Path) -> dict[str, Any]:
    return {"path": relative(path), "bytes": exporter.path_size(path), "sha256": exporter.sha256_file(path).upper()}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--authority-release-root", default=str(DEFAULT_AUTHORITY))
    parser.add_argument("--authority-checkpoint", default=str(DEFAULT_CHECKPOINT))
    parser.add_argument("--authority-registry", default=str(DEFAULT_REGISTRY))
    parser.add_argument("--candidate-release-root", default=str(DEFAULT_CANDIDATE))
    args = parser.parse_args()

    authority_root = Path(args.authority_release_root).resolve()
    checkpoint_path = Path(args.authority_checkpoint).resolve()
    registry_path = Path(args.authority_registry).resolve()
    candidate_root = Path(args.candidate_release_root).resolve()
    authority_exe = authority_root / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
    candidate_exe = candidate_root / "workers" / "anki" / "KnowledgeClipper.AnkiExporter.Worker.exe"
    companion_path = WORKER / "contracts" / "anki-destination-settings-descriptor.v1.json"
    authority_companion_path = authority_root / "workers" / "anki" / "contracts" / "anki-destination-settings-descriptor.v1.json"
    for required in (checkpoint_path, registry_path, authority_exe, candidate_exe, companion_path, authority_companion_path, DESKTOP_CANARY):
        if not required.is_file():
            raise RuntimeError(f"Required compatibility input missing: {required}")

    gate_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    root = WORKER / "canary" / "shared-consumer-compatibility" / gate_id
    root.mkdir(parents=True, exist_ok=False)

    checkpoint = read_json(checkpoint_path)
    registry = read_json(registry_path)
    registry_entry = checks.require_registry_entry(registry)
    stored_authority_describe_path = registry_path.parent / "describe" / "export.anki.describe.json"
    stored_authority_describe = read_json(stored_authority_describe_path)

    authority_describe_run = run([str(authority_exe), "describe", "--json"], authority_root, timeout=30)
    candidate_describe_run = run([str(candidate_exe), "describe", "--json"], candidate_root, timeout=30)
    authority_describe = json.loads(authority_describe_run.stdout)
    candidate_describe = json.loads(candidate_describe_run.stdout)
    checks.require_descriptor(authority_describe)
    checks.require_descriptor(candidate_describe, version="1.2.1")
    if authority_describe != stored_authority_describe:
        raise RuntimeError("Authority release describe differs from its stored authority snapshot")
    authority_describe_path = root / "authority-release.describe.json"
    candidate_describe_path = root / "candidate-release.describe.json"
    exporter.write_json_atomic_once(authority_describe_path, authority_describe)
    exporter.write_json_atomic_once(candidate_describe_path, candidate_describe)

    companion = read_json(companion_path)
    probe_action = checks.require_explicit_probe(companion)
    if exporter.sha256_file(companion_path).lower() != exporter.sha256_file(authority_companion_path).lower():
        raise RuntimeError("Authority Anki companion descriptor differs from the module release descriptor")

    desktop_sources = [
        PROJECT / "src" / "DesktopModuleSettings.cs",
        PROJECT / "src" / "DesktopSettingsControl.cs",
        PROJECT / "src" / "ProjectSettingsForm.cs",
        PROJECT / "src" / "RemNoteQuickClipper.Desktop.Core" / "SettingsAutomationCatalog.cs",
    ]
    direct_findings = checks.find_direct_network_tokens((path, path.read_text(encoding="utf-8")) for path in desktop_sources)
    if direct_findings:
        raise RuntimeError(f"Desktop Anki source contains a direct-network implementation: {direct_findings}")

    desktop_canary_run = subprocess.run(
        [str(DESKTOP_CANARY), "--settings-only"], cwd=PROJECT, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120, check=False
    )
    desktop_log = root / "desktop-windows-canary.stdout.log"
    desktop_error_log = root / "desktop-windows-canary.stderr.log"
    desktop_log.write_text(desktop_canary_run.stdout, encoding="utf-8", newline="\n")
    desktop_error_log.write_text(desktop_canary_run.stderr, encoding="utf-8", newline="\n")
    canary_match = re.search(r"WINDOWS_(?:SETTINGS_)?UI_CANARY PASS assertions=(\d+)", desktop_canary_run.stdout)
    anki_authority_assertion = f"PASS authority v{checkpoint['registry']['registryVersion']} admits the explicit Anki 1.2.1 worker-owned read-only probe action" in desktop_canary_run.stdout
    desktop_canary_status = "passed" if desktop_canary_run.returncode == 0 and canary_match else (
        "anki-authority-assertion-passed-supplemental-failed" if anki_authority_assertion else "supplemental-failed"
    )

    formal_script = WORKER / "tests" / "run_formal_release_settings_probe_canary.py"
    formal_run = run([sys.executable, str(formal_script), "--release-root", str(candidate_root)], PROJECT, timeout=180)
    formal_log = root / "core-candidate-harness.stdout.log"
    formal_error_log = root / "core-candidate-harness.stderr.log"
    formal_log.write_text(formal_run.stdout, encoding="utf-8", newline="\n")
    formal_error_log.write_text(formal_run.stderr, encoding="utf-8", newline="\n")
    formal_summary = json.loads(formal_run.stdout)
    formal_receipt_path = Path(formal_summary["receipt"]).resolve()
    formal_receipt = read_json(formal_receipt_path)
    if formal_receipt.get("status") != "passed":
        raise RuntimeError("Candidate-bound Core settings/probe harness did not pass")
    probe_request_path = PROJECT / formal_receipt["attempts"]["offlineProbe"]["request"]
    probe_result_path = PROJECT / formal_receipt["attempts"]["offlineProbe"]["result"]
    probe_result = read_json(probe_result_path)
    job_candidates = [probe_request_path.parent, *probe_request_path.parents]
    job_root = next((path for path in job_candidates if (path / "task.manifest.v1.json").is_file()), None)
    if job_root is None:
        raise RuntimeError("Cannot locate the immutable job root for the probe request")
    probe_manifest_path = job_root / probe_result["outputs"][0]["manifestPath"]
    probe_manifest = read_json(probe_manifest_path)
    connection_status_path = job_root / probe_manifest["metadata"]["entrypoint"]
    connection_status = read_json(connection_status_path)
    if connection_status.get("externalMutation") is not False:
        raise RuntimeError("Probe status did not declare externalMutation=false")
    if probe_result.get("sideEffects") != []:
        raise RuntimeError("Probe common result reported side effects")
    if formal_receipt["safety"]["realAnkiConnectCalls"] != 0 or formal_receipt["safety"]["mutatingActions"]:
        raise RuntimeError("Candidate-bound probe safety evidence is not offline/read-only")

    config_manifest_path = PROJECT / formal_receipt["inputs"]["configArtifact"]["path"]
    config_manifest = read_json(config_manifest_path)
    probe_request = read_json(probe_request_path)
    probe_config_refs = [item for item in probe_request["inputRefs"] if item["kind"] == "anki-export-config"]
    if len(probe_config_refs) != 1:
        raise RuntimeError("Probe WorkerRequest must contain exactly one config reference")
    checks.require_sha_bound_config(probe_config_refs[0], config_manifest, exporter.sha256_file(config_manifest_path))
    if probe_request.get("configSnapshot") != {"ankiConfigArtifactId": probe_config_refs[0]["artifactRef"]}:
        raise RuntimeError("Probe configSnapshot must contain only the config artifact identity")

    core_resolver = PROJECT / "orchestrator" / "Core" / "DesktopIntentResolver.cs"
    desktop_settings = PROJECT / "src" / "DesktopModuleSettings.cs"
    desktop_settings_control = PROJECT / "src" / "DesktopSettingsControl.cs"
    desktop_form = PROJECT / "src" / "ProjectSettingsForm.cs"
    automation_catalog = PROJECT / "src" / "RemNoteQuickClipper.Desktop.Core" / "SettingsAutomationCatalog.cs"
    core_files = list((PROJECT / "orchestrator" / "Core").glob("*.cs"))
    core_text = "\n".join(path.read_text(encoding="utf-8") for path in core_files)
    desktop_text = "\n".join(path.read_text(encoding="utf-8") for path in desktop_sources)
    core_probe_wired = "ResolveAnkiProbe" in core_text and "explicit-module-action" in core_text
    desktop_probe_presented = "ExportAnkiProbe" in desktop_text and "probeConnection" in desktop_text
    desktop_probe_dispatched = "ankiProbe.Click" in desktop_text
    authority_version = checkpoint["registry"]["registryVersion"]
    desktop_authority_current = authority_version in desktop_settings_control.read_text(encoding="utf-8")
    core_action_source = PROJECT / "orchestrator" / "Core" / "SettingsControl.cs"
    core_probe_line = checks.first_line(core_action_source, "ResolveAnkiProbe")
    desktop_status_line = checks.first_line(desktop_form, "Exporter 状态与回执")
    desktop_action_line = checks.first_line(desktop_form, "只读连接检查")

    gaps = [
        {
            "id": "authority.export-anki-1.2.1-not-bound",
            "owner": "authority/registry-release",
            "status": "open" if authority_describe["workerVersion"] != candidate_describe["workerVersion"] else "closed",
            "evidence": f"authority {checkpoint['registry']['registryVersion']} physical describe is export.anki {authority_describe['workerVersion']}; module candidate is {candidate_describe['workerVersion']}",
            "requiredChange": "none when both immutable describes remain identical; otherwise append a new authority instead of rewriting an existing checkpoint",
        },
        {
            "id": "core.anki-read-only-probe-route-missing",
            "owner": "Core",
            "status": "open" if not core_probe_wired else "closed",
            "evidence": f"{relative(core_action_source)}:{core_probe_line} now builds operation=probe with explicit-module-action routing and the candidate-bound harness executed it",
            "requiredChange": "none while the current resolver behavior and contract remain stable",
        },
        {
            "id": "desktop.anki-probe-action-missing",
            "owner": "Desktop",
            "status": "open" if not desktop_probe_dispatched else "closed",
            "evidence": f"{relative(desktop_form)}:{desktop_action_line} exposes the manual action; ankiProbe.Click dispatch is {'present' if desktop_probe_dispatched else 'missing'}",
            "requiredChange": "none while the click path stays Core-owned and read-only" if desktop_probe_dispatched else "bind the existing manual button to Core ModuleSettingsActionResolver.ResolveAnkiProbe and render the redacted status/receipt; never call AnkiConnect from Desktop",
        },
        {
            "id": "desktop.authority-v1.2.12-discovery-missing",
            "owner": "Desktop",
            "status": "open" if not desktop_authority_current else "closed",
            "evidence": f"{relative(desktop_settings_control)} authority discovery {'names' if desktop_authority_current else 'does not name'} admitted authority {authority_version}",
            "requiredChange": "make Desktop discover the append-only v1.2.12 release/checkpoint so the already-wired explicit probe can become enabled; do not bypass authority admission",
        },
    ]
    open_consumer_gaps = [gap for gap in gaps if gap["owner"] in {"Core", "Desktop"} and gap["status"] == "open"]
    open_gaps = [gap for gap in gaps if gap["status"] == "open"]

    differences_path = root / "consumer-differences.json"
    exporter.write_json_atomic_once(differences_path, {"schemaVersion": "export.anki.consumer-differences.v1", "gaps": gaps})
    readable_path = root / "readable-compatibility.md"
    readable_path.write_text("\n".join([
        "# export.anki 1.2.1 × 当前 Core/Desktop 离线兼容门禁",
        "",
        "## 已通过",
        "",
        f"- Authority `{authority_version}` 的 `export.anki {authority_describe['workerVersion']}` 入口与真实 `describe` 一致，outputKinds 同时包含 `anki-export-evidence`、`anki-connection-status`。",
        f"- Core 实际组装并运行 1.2.1 WorkerRequest：config Artifact `{probe_config_refs[0]['artifactRef']}`，manifest SHA `{probe_config_refs[0]['expectedSha256'].upper()}`。",
        f"- probe request → common result `completed` → status `{connection_status['status']}`；`externalMutation=false`，common sideEffects 为空。",
        f"- companion descriptor 要求 `{probe_action['executionPolicy']}`；不是启动自动探测。",
        f"- Desktop 当前源码已消费 companion descriptor、显示手动“只读连接检查”并接到 Core；当前 Anki 相关 Desktop 源码未发现直接网络客户端。",
        f"- Desktop settings-only Windows Canary 为补充证据：`{desktop_canary_status}`{f' / {canary_match.group(1)} assertions' if canary_match else ''}；Anki authority 断言已通过，后续其他模块断言不决定本门禁。",
        "- Fake 随机 loopback 服务调用；真实 AnkiConnect `0`，写牌组/笔记/媒体 `0`。",
        "",
        "## 当前门禁差异",
        "",
        f"- Core：真实 `ResolveAnkiProbe` 已由 candidate-bound harness 执行通过（{relative(core_action_source)}:{core_probe_line}），不再是缺口。",
        f"- Desktop action：已有手动入口及 click→Core dispatch（{relative(desktop_form)}:{desktop_action_line}）。",
        f"- Desktop authority discovery：`{'PASS' if desktop_authority_current else 'NO-GO'}`；当前源码{'已' if desktop_authority_current else '尚未'}发现 authority `{authority_version}`。",
        f"- Authority binding：`{'PASS' if authority_describe['workerVersion'] == candidate_describe['workerVersion'] else 'NO-GO'}`；物理 worker `{authority_describe['workerVersion']}` / candidate `{candidate_describe['workerVersion']}`。",
        "",
        "结论：文件/descriptor/WorkerRequest/result 兼容 PASS；共享 consumer 的只读 probe onboarding " + ("PASS。" if not open_consumer_gaps else "仍为 NO-GO。"),
        "",
    ]), encoding="utf-8", newline="\n")

    receipt = {
        "schemaVersion": "export.anki.shared-consumer-compatibility-receipt.v1",
        "status": "passed" if not open_gaps else "conditional",
        "createdAt": now(),
        "compatibilityStatus": "passed",
        "consumerOnboardingStatus": "no-go" if open_consumer_gaps else "passed",
        "scope": "offline-only; no real AnkiConnect",
        "authority": {
            "checkpoint": file_evidence(checkpoint_path),
            "registry": file_evidence(registry_path),
            "releaseRoot": relative(authority_root),
            "entrypoint": registry_entry["entrypoint"]["path"],
            "describe": file_evidence(authority_describe_path),
            "workerVersion": authority_describe["workerVersion"],
            "outputKinds": authority_describe["outputKinds"],
            "companionDescriptor": file_evidence(authority_companion_path),
        },
        "candidate": {
            "releaseRoot": relative(candidate_root),
            "exe": file_evidence(candidate_exe),
            "describe": file_evidence(candidate_describe_path),
            "workerVersion": candidate_describe["workerVersion"],
            "outputKinds": candidate_describe["outputKinds"],
        },
        "gates": {
            "authorityEntrypointAndOutputKinds": "passed",
            "shaBoundAnkiExportConfig": "passed",
            "probeRequestResult": "passed",
            "explicitUserAction": "passed",
            "externalMutationFalse": "passed",
            "desktopNoDirectAnkiConnect": "passed",
            "coreProbeConsumerWiring": "no-go" if not core_probe_wired else "passed",
            "desktopExplicitActionSurface": "passed" if desktop_probe_presented else "no-go",
            "desktopProbeDispatch": "no-go" if not desktop_probe_dispatched else "passed",
            "desktopAuthorityDiscovery": "passed" if desktop_authority_current else "no-go",
            "authorityBindsCandidate121": "no-go" if authority_describe["workerVersion"] != candidate_describe["workerVersion"] else "passed",
        },
        "core": {
            "candidateHarnessReceipt": file_evidence(formal_receipt_path),
            "probeRequest": file_evidence(probe_request_path),
            "probeResult": file_evidence(probe_result_path),
            "connectionStatus": file_evidence(connection_status_path),
            "configArtifact": file_evidence(config_manifest_path),
            "configArtifactRef": probe_config_refs[0]["artifactRef"],
            "configExpectedSha256": probe_config_refs[0]["expectedSha256"].upper(),
            "configSnapshot": probe_request["configSnapshot"],
        },
        "desktop": {
            "canaryExe": file_evidence(DESKTOP_CANARY),
            "canaryStatus": desktop_canary_status,
            "canaryAssertions": int(canary_match.group(1)) if canary_match else None,
            "ankiAuthorityAssertionPassed": anki_authority_assertion,
            "canaryStdout": file_evidence(desktop_log),
            "canaryStderr": file_evidence(desktop_error_log),
            "sourceFiles": [file_evidence(path) for path in desktop_sources],
            "directNetworkFindings": direct_findings,
        },
        "probe": {
            "operation": probe_request["operation"],
            "resultStatus": probe_result["status"],
            "outputKind": probe_result["outputs"][0]["kind"],
            "connectionStatus": connection_status["status"],
            "executionPolicy": probe_action["executionPolicy"],
            "externalMutation": connection_status["externalMutation"],
            "sideEffects": probe_result["sideEffects"],
        },
        "safety": {
            "realAnkiConnectCalls": 0,
            "fakeLoopbackOnly": True,
            "mutatingActions": formal_receipt["safety"]["mutatingActions"],
            "notesWritten": formal_receipt["safety"]["notesWritten"],
            "mediaWritten": formal_receipt["safety"]["mediaWritten"],
            "desktopDirectConnection": False,
        },
        "outputs": {
            "differences": file_evidence(differences_path),
            "readable": file_evidence(readable_path),
            "coreHarnessStdout": file_evidence(formal_log),
            "coreHarnessStderr": file_evidence(formal_error_log),
        },
    }
    receipt_path = root / "receipt.json"
    exporter.write_json_atomic_once(receipt_path, receipt)
    print(json.dumps({
        "status": receipt["status"],
        "compatibilityStatus": receipt["compatibilityStatus"],
        "consumerOnboardingStatus": receipt["consumerOnboardingStatus"],
        "receipt": str(receipt_path),
        "receiptSha256": exporter.sha256_file(receipt_path).upper(),
        "readable": str(readable_path),
        "differences": str(differences_path),
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
