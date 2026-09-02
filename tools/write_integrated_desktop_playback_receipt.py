from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


class ReceiptError(RuntimeError):
    pass


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ReceiptError(message)


def inventory(root: Path) -> dict[str, Any]:
    files: list[dict[str, Any]] = []
    rows: list[str] = []
    total = 0
    paths = sorted(
        (item for item in root.rglob("*") if item.is_file()),
        key=lambda item: item.relative_to(root).as_posix(),
    )
    for path in paths:
        relative = path.relative_to(root).as_posix()
        size = path.stat().st_size
        digest = sha256(path)
        files.append({"path": relative, "bytes": size, "sha256": digest})
        rows.append(f"{relative}\t{size}\t{digest.lower()}")
        total += size
    return {
        "files": files,
        "fileCount": len(files),
        "totalBytes": total,
        "treeSha256": hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper(),
        "treeAlgorithm": "sha256(utf8 ordinal(relativePath + TAB + bytes + TAB + lowercaseSha256) joined by LF)",
    }


def process_exists(pid: int) -> bool:
    completed = subprocess.run(
        ["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    return completed.returncode == 0 and f'"{pid}"' in completed.stdout


def evidence(path: Path, project: Path) -> dict[str, Any]:
    return {"path": path.resolve().relative_to(project).as_posix(), "sha256": sha256(path)}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--canary", type=Path, required=True)
    parser.add_argument("--desktop-module-receipt", type=Path, required=True)
    parser.add_argument("--production-pipe-receipt", type=Path, required=True)
    parser.add_argument("--prior-windows-ui-receipt", type=Path, required=True)
    parser.add_argument("--authority-runtime-gate", type=Path, required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    args = parser.parse_args()

    project = args.project.resolve()
    candidate = args.candidate.resolve()
    canary = args.canary.resolve()
    output = args.receipt.resolve()
    require(candidate.is_dir() and canary.is_dir(), "candidate or canary root is missing")
    require(not output.exists(), "append-only receipt already exists")

    manifest_path = candidate / "candidate-file-manifest.v1.json"
    manifest = load(manifest_path)
    candidate_inventory = inventory(candidate)
    manifest_gate_path = canary / "manifest-tree-gate.json"
    desktop_equality_path = canary / "desktop-payload-equality.json"
    startup_path = canary / "startup-diagnostic.stdout.log"
    single_path = canary / "single-instance-diagnostic.stdout.log"
    integration_path = canary / "integration-startup-canary.stdout.log"
    registry_gate_path = canary / "authority-validate-registry.log"
    direct_gate_path = canary / "playback-control-direct-ipc-gate.json"
    required_evidence = (
        manifest_gate_path,
        desktop_equality_path,
        startup_path,
        single_path,
        integration_path,
        registry_gate_path,
        direct_gate_path,
        args.desktop_module_receipt,
        args.production_pipe_receipt,
        args.prior_windows_ui_receipt,
        args.authority_runtime_gate,
    )
    for path in required_evidence:
        require(path.is_file(), f"required evidence is missing: {path}")

    manifest_gate = load(manifest_gate_path)
    desktop_equality = load(desktop_equality_path)
    startup = load(startup_path)
    single = load(single_path)
    integration = load(integration_path)
    registry_gate = load(registry_gate_path)
    direct_gate = load(direct_gate_path)
    desktop_module = load(args.desktop_module_receipt)
    production_pipe = load(args.production_pipe_receipt)
    integration_config = load(candidate / "candidate-integration.json")

    require(manifest.get("candidateId") == "kc-i-v124-20260828T034900Z", "candidate identity mismatch")
    require(
        manifest_gate.get("status") == "PASS"
        and manifest_gate.get("candidateFileCount") == candidate_inventory["fileCount"]
        and manifest_gate.get("candidateTotalBytes") == candidate_inventory["totalBytes"]
        and manifest_gate.get("candidateTreeSha256") == candidate_inventory["treeSha256"],
        "manifest/tree gate mismatch",
    )
    require(
        desktop_equality.get("status") == "PASS"
        and desktop_equality.get("fileCount") == 243
        and desktop_equality.get("changedFiles") == 0,
        "desktop payload equality gate mismatch",
    )
    require(
        desktop_module.get("status") == "PASS-DESKTOP-MODULE-INTEGRATED-AUTHORITY-PENDING"
        and desktop_module.get("playbackStop", {}).get("taskCancelMessagesObserved") == 0
        and desktop_module.get("playbackStop", {}).get("taskStageControlIntermediateRoutes") == 0,
        "desktop playback-control module receipt mismatch",
    )
    require(
        startup.get("result") == "PASS"
        and startup.get("externalConnections") == 0
        and startup.get("candidateOrchestratorPathsVerified") is True,
        "startup diagnostic mismatch",
    )
    require(
        single.get("existingInstanceDetected") is True
        and single.get("userFeedbackMode") == "activate-existing-window-and-message-box",
        "single-instance non-silent gate mismatch",
    )
    require(process_exists(int(single["existingProcessId"])), "pre-existing desktop process was not preserved")
    require(
        integration.get("result") == "PASS"
        and integration.get("action") == "task.status.query-recent"
        and integration.get("externalWorkerInvoked") is False
        and integration.get("physicalSpeakerInvoked") is False,
        "integrated startup/Named Pipe gate mismatch",
    )
    require(not process_exists(int(integration["orchestratorProcessId"])), "canary-owned orchestrator process still runs")
    require(
        registry_gate == {"schemaVersion": "1.0.0", "status": "valid", "registryVersion": "1.2.4", "workers": 5},
        "candidate registry result mismatch",
    )
    require(
        direct_gate.get("status") == "PASS"
        and direct_gate.get("protocol11", {}).get("receiptSchemaVersion") == "2.3.0"
        and direct_gate.get("protocol11", {}).get("accepted") is True
        and direct_gate.get("protocol11", {}).get("stateClaim") == "stop-requested"
        and direct_gate.get("durableEffects", {}).get("runCancelRequestedWritten") is False
        and direct_gate.get("physicalSpeakerInvocations") == 0
        and direct_gate.get("externalWrites") == 0,
        "candidate direct stage-control gate mismatch",
    )
    require(
        production_pipe.get("result") == "PASS"
        and production_pipe.get("action") == "task.status.query-recent"
        and production_pipe.get("externalOcrInvoked") is False
        and production_pipe.get("remNoteInvoked") is False,
        "desktop production Named Pipe receipt mismatch",
    )
    require(
        integration_config.get("IpcConfig") == "authority/metadata/orchestrator-ipc.v1.1.json"
        and integration_config.get("AuthorityCheckpointId") == "knowledge-clipper-authority-1.2.4-20260828T033500Z"
        and integration_config.get("AuthorityTreeSha256") == "6CA2D59E66DF9F15CC1BE46890ABDA04F5FAFB98507716D91E568818A37882CE",
        "candidate integration config mismatch",
    )

    metadata_hashes = {
        "checkpoint.json": "E7A49572E20CA7C7E116466CEAF57659E9AD7A36CA5EC2C583940997A367F122",
        "worker-registry.v1.json": "72A6C1BA7604FBC787B21380F57DA59F7465F71E392C2F32F56E03B782E539A4",
        "dependency-manifest.v1.json": "25DA7B8B27995AA1E9EDBD0282BEB191A04C47CD7D722F047379AD30B2683AF2",
        "knowledge-clipper-authority-1.2.4-20260828T033500Z.json": "14899ACDA401F00A20DB3F08DFC3C5AC8D09D12B56318C794FDAD79EB401D3AF",
        "orchestrator-ipc.v1.1.json": "849CB3A85DD8CE660B5A6924D804AEB59E254010F616BA54B5E694C263DB98E2",
        "schemas/ipc-request.v1.1.schema.json": "FF71DE29B9E832B302FB5A3CA8F8E16774B1E184868D9A7CAAFCDDDE29AF6731",
        "schemas/ipc-response.v1.1.schema.json": "F159A30F02EC356E318B9F6305F5F468613C014B6E0ED8CBB38AE57882A1A19F",
        "schemas/stage-control-request.v1.schema.json": "B717994985275623719C09C93D04A4F05A5B23BAE6AB240191E00898908182E8",
        "schemas/stage-control-receipt.v1.schema.json": "A6B7DAAC447D93EC96BF57220AC7F14A07749B6498963F029EF0BA54BE48BC3F",
    }
    for relative, expected in metadata_hashes.items():
        require(sha256(candidate / "authority/metadata" / relative) == expected, f"authority metadata mismatch: {relative}")
    require(
        sha256(candidate / "authority/win-x64/orchestrator/KnowledgeClipper.Orchestrator.exe")
        == "9C6FD23EE4CB83AECD6EB09E58DB1799408AB9C46FC05913C399A372BA9A1A53",
        "embedded orchestrator mismatch",
    )
    require(
        sha256(args.authority_runtime_gate) == "FF1434042E09E187E4D5D6628EFC7DE8ACB13316B7E8454E621B1AC0A3FA58B2",
        "authority runtime gate mismatch",
    )

    receipt = {
        "schemaVersion": "knowledge-clipper-integrated-desktop-playback-control-receipt.v1",
        "candidateId": manifest["candidateId"],
        "createdAt": datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z"),
        "status": "PASS-INTEGRATED-V1.2.4-PLAYBACK-CONTROL",
        "productName": "零碎知识剪藏",
        "entrypoint": (candidate / "KnowledgeClipper.exe").relative_to(project).as_posix(),
        "entrypointSha256": sha256(candidate / "KnowledgeClipper.exe"),
        "candidate": {
            "root": candidate.relative_to(project).as_posix(),
            "manifest": evidence(manifest_path, project),
            **candidate_inventory,
        },
        "desktop": {
            "moduleReceipt": evidence(args.desktop_module_receipt, project),
            "buildId": manifest["desktop"]["buildId"],
            "buildReceiptSha256": manifest["desktop"]["buildReceiptSha256"],
            "payloadFiles": manifest["desktop"]["fileCount"],
            "payloadBytes": manifest["desktop"]["totalBytes"],
            "payloadTreeSha256": manifest["desktop"]["treeSha256"],
            "changedFilesSinceModuleCandidate": 0,
            "priorWindowsUiReceipt": evidence(args.prior_windows_ui_receipt, project),
            "uiRetestRequired": False,
            "uiRetestReason": "All 243 receipt-bound desktop payload files are byte-for-byte identical; this successor changes only embedded authority and integration metadata.",
        },
        "authority": {
            "checkpointId": "knowledge-clipper-authority-1.2.4-20260828T033500Z",
            "releaseFiles": 91,
            "releaseBytes": 249015447,
            "releaseTreeSha256": "6CA2D59E66DF9F15CC1BE46890ABDA04F5FAFB98507716D91E568818A37882CE",
            "metadataSha256": metadata_hashes,
            "orchestratorSha256": "9C6FD23EE4CB83AECD6EB09E58DB1799408AB9C46FC05913C399A372BA9A1A53",
            "runtimeGate": evidence(args.authority_runtime_gate, project),
            "copiedExactly": True,
        },
        "playbackStop": {
            "transport": "direct-named-pipe",
            "protocolVersion": "1.1",
            "messageType": "stage-control",
            "requestPayload": {
                "operation": "speech.playback",
                "selector": "current-active",
                "reason": "user_stop",
            },
            "receiptMessageType": "stage-control-receipt",
            "receiptSchemaVersion": "2.3.0",
            "acceptedProjection": {"state": "stop-requested", "noticeCode": "playback.stop-requested", "claimsStopped": False},
            "taskCancelMessagesObserved": 0,
            "taskStageControlIntermediateRoutes": 0,
            "wholeRunCancelRequestedWritten": False,
        },
        "automatedCanaries": {
            "manifestTree": evidence(manifest_gate_path, project),
            "desktopPayloadEquality": evidence(desktop_equality_path, project),
            "safeStartup": evidence(startup_path, project),
            "singleInstance": {
                **evidence(single_path, project),
                "existingProcessId": single["existingProcessId"],
                "existingProcessPreserved": True,
                "feedbackMode": single["userFeedbackMode"],
            },
            "builtInOrchestrator": {
                **evidence(integration_path, project),
                "processId": integration["orchestratorProcessId"],
                "processStoppedAfterCanary": True,
                "queryAction": integration["action"],
            },
            "authorityRegistry": evidence(registry_gate_path, project),
            "directStageControl": evidence(direct_gate_path, project),
            "desktopProductionPipe": evidence(args.production_pipe_receipt, project),
        },
        "safety": {
            "externalWorkersInvoked": False,
            "physicalSpeakerInvoked": False,
            "remNoteInvoked": False,
            "ankiInvoked": False,
            "mineruInvoked": False,
            "externalProductionApisInvoked": False,
            "preExistingDesktopProcessTerminated": False,
        },
        "userHandTestNode": {
            "requiredForThisSuccessor": False,
            "reason": "The UI/EXE/DLL payload is unchanged from the already manually accepted desktop candidate; playback.stop integration is covered by deterministic IPC and process canaries.",
        },
        "noGoBoundaries": [
            "desktop-to-real RemNote/Anki export E2E not run",
            "physical speaker not invoked",
            "physical system screenshot selection and cross-monitor mixed-DPI drag remain user/environment checks",
            "real external application selection/clipboard remains a user experience check",
            "no sealed installer or code signing",
        ],
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(
        json.dumps(
            {
                "status": receipt["status"],
                "receipt": str(output),
                "receiptSha256": sha256(output),
                "candidateTreeSha256": candidate_inventory["treeSha256"],
                "candidateFiles": candidate_inventory["fileCount"],
                "candidateBytes": candidate_inventory["totalBytes"],
            },
            ensure_ascii=False,
        )
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ReceiptError, OSError, ValueError, KeyError, json.JSONDecodeError, subprocess.SubprocessError) as exc:
        print(json.dumps({"status": "FAIL", "error": str(exc)}, ensure_ascii=False))
        raise SystemExit(1)
