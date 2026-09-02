from __future__ import annotations

import argparse
import json
import os
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import verify_production_state_journeys as gate
from verify_settings_control_plane import scan_runtime_tree_for_secrets


PROJECT = Path(__file__).resolve().parents[1]
CANDIDATE = PROJECT / "releases/kc-i-v1240-r-20260902T023513Z/win-x64"
ASSEMBLY = PROJECT / "receipts/quality-integrated-candidate/20260902T023513Z-v1240-production-routes/assembly.receipt.json"
DESKTOP = PROJECT / "receipts/desktop-production-routes/20260902T023600Z/desktop-production-routes.receipt.json"
SOURCE_GATE = PROJECT / "canary/quality-architecture-burndown/20260902T023513Z-v1240-production-routes-source-gate/architecture-burndown-receipt.json"
TOOLING = PROJECT / "canary/quality-sealed-stateful-runner/20260902T112200Z-v1240-collect-all-offline-boundary/tooling-receipt.json"
STATEFUL = PROJECT / "canary/quality-integrated-v1240/20260902T112300Z-production-routes-offline-isolated-full-matrix/candidate-stateful-evidence.json"
INVALIDATION = PROJECT / "receipts/quality-integrated-candidate/20260902T111700Z-v1240-offline-policy-invalidation/external-policy-invalidation.receipt.json"
HISTORICAL_NO_GO = PROJECT / "receipts/quality-integrated-candidate/20260902T021100Z-v1240-stateful-no-go/candidate-stateful-no-go.receipt.json"


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def long_files(root: Path, filename: str) -> list[Path]:
    native_root = ("\\\\?\\" + str(root.resolve())) if os.name == "nt" else str(root.resolve())
    result: list[Path] = []
    for directory, _, names in os.walk(native_root):
        for name in names:
            if name == filename:
                result.append(Path(directory) / name)
    return result


def candidate_processes() -> list[dict[str, Any]]:
    marker = "kc-i-v1240-r-20260902T023513Z"
    command = (
        "$rows=@(Get-CimInstance Win32_Process | Where-Object { "
        f"$_.ExecutablePath -like '*{marker}*' }} | Select-Object ProcessId,ParentProcessId,Name,ExecutablePath); "
        "$rows | ConvertTo-Json -Depth 3 -Compress"
    )
    completed = subprocess.run(["pwsh", "-NoLogo", "-NoProfile", "-Command", command], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30, check=False)
    gate.require(completed.returncode == 0, "candidate process audit failed")
    text = completed.stdout.strip()
    if not text:
        return []
    value = json.loads(text)
    return value if isinstance(value, list) else [value]


def find_driver_receipt(stateful: dict[str, Any], journey: str) -> tuple[Path, dict[str, Any]]:
    reference = stateful["failures"][journey]["driverReceipt"]
    gate.verify_evidence(reference)
    path = gate.contained(PROJECT, reference["path"])
    return path, load(path)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()
    output_root = args.output_root.resolve()
    gate.require(output_root.is_relative_to((PROJECT / "receipts/quality-integrated-candidate").resolve()), "quality output escaped append-only receipt root")
    gate.require(not output_root.exists(), "append-only quality output exists")

    assembly = load(ASSEMBLY)
    desktop = load(DESKTOP)
    source_gate = load(SOURCE_GATE)
    tooling = load(TOOLING)
    stateful = load(STATEFUL)
    invalidation = load(INVALIDATION)
    historical_no_go = load(HISTORICAL_NO_GO)
    sealed = gate.tree_summary(CANDIDATE)
    manifest_path = CANDIDATE / "candidate-file-manifest.v1.json"

    gate.require(assembly["candidateRoot"] == CANDIDATE.relative_to(PROJECT).as_posix(), "assembly candidate root drift")
    gate.require(assembly["sealed"]["fileCount"] == sealed["fileCount"] and assembly["sealed"]["totalBytes"] == sealed["totalBytes"] and assembly["sealed"]["treeSha256"] == sealed["treeSha256"], "sealed candidate tree drift")
    gate.require(assembly["manifest"]["sha256"] == gate.sha256_file(manifest_path), "candidate manifest drift")
    gate.require(desktop["status"] == "PASS-DESKTOP-PRODUCTION-VIEW-ROUTES" and desktop["productionBehavior"]["--open-settings"] and desktop["productionBehavior"]["--open-question"], "Desktop production route receipt is not passed")
    gate.require(source_gate["status"] == "PASS-ARCHITECTURE-SOURCE-GATE" and source_gate["delta"]["promotionAllowed"] is True, "architecture source gate drift")
    gate.require(tooling["status"] == "PASS-TOOLING-READY-FOR-CANDIDATE-SPEC-NOT-EXECUTED" and tooling["tests"]["total"] == 48, "runner tooling qualification drift")
    gate.require(stateful["candidate"]["sealed"] == sealed and stateful["candidate"]["manifest"]["sha256"] == assembly["manifest"]["sha256"], "stateful evidence bound a different candidate")
    gate.require(stateful["offlineBoundary"]["status"] == "passed" and stateful["offlineBoundary"]["realExternalProviderCompletionCount"] == 0 and stateful["offlineBoundary"]["credentialSentinelPersistenceCount"] == 0, "offline stateful rerun crossed the external boundary")
    gate.require(set(stateful["failures"]) == {"anki", "remnote", "question"} and stateful["promotion"]["userHandTestEntrypoint"] is False, "stateful NO-GO partition drift")
    gate.require(invalidation["status"] == "invalidated-never-promotion-evidence" and invalidation["scope"]["candidateInvalidated"] is False, "external-policy invalidation drift")
    gate.require(historical_no_go["status"] == "NO-GO-CANDIDATE-STATEFUL-JOURNEYS", "historical NO-GO evidence drift")

    driver_receipts = {}
    for journey in ("anki", "remnote", "question"):
        path, receipt = find_driver_receipt(stateful, journey)
        gate.require(receipt["status"] == "failed" and receipt["injectedHost"] is False and receipt["sharedTestCoreDll"] is False, f"{journey} driver boundary drift")
        driver_receipts[journey] = {"evidence": gate.evidence(path), "failure": receipt["failure"], "observed": receipt.get("failureObserved")}
    gate.require("worker-owned loopback read-only probe" in driver_receipts["anki"]["observed"], "Anki static capability observation drift")
    gate.require("未绑定" in driver_receipts["remnote"]["observed"], "RemNote unbound observation drift")

    isolated = STATEFUL.parent / "isolated-real-store"
    remnote_results = []
    content_results = []
    for path in long_files(isolated, "result.json"):
        try:
            document = json.loads(path.read_text(encoding="utf-8-sig"))
        except (json.JSONDecodeError, OSError):
            continue
        if document.get("schemaVersion") == "remnote-target-discovery-result.v1" and document.get("worker", {}).get("id") == "export.remnote":
            remnote_results.append(document)
        if document.get("worker", {}).get("id") == "content.deepseek":
            content_results.append(document)
    gate.require(len(remnote_results) == 1 and remnote_results[0]["state"] == "blocked" and remnote_results[0]["externalCallCount"] == 1 and remnote_results[0]["externalWriteCount"] == 0, "RemNote closed-loopback fail-closed evidence drift")
    gate.require(content_results and all(item["status"] == "failed" and item["error"]["code"] == "content.provider-failed" for item in content_results), "Question offline Content failure evidence drift")

    secret_scan = scan_runtime_tree_for_secrets(CANDIDATE)
    gate.require(secret_scan["status"] == "passed" and secret_scan["findingCount"] == 0, "candidate secret scan failed")
    processes = candidate_processes()
    gate.require(not processes, "owned candidate process remains after stateful gate")

    decision = {
        "status": "NO-GO-CANDIDATE-STATEFUL-PRODUCTION-ROUTES",
        "userHandTestEntrypoint": False,
        "candidateSealed": True,
        "candidatePromoted": False,
        "externalRealChainClaimed": False,
    }
    receipt = {
        "schemaVersion": "knowledge-clipper-quality-v1240-production-routes-stateful.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "decision": decision,
        "candidate": {"root": CANDIDATE.relative_to(PROJECT).as_posix(), "manifest": gate.evidence(manifest_path), "sealed": sealed, "assembly": gate.evidence(ASSEMBLY)},
        "desktopProductionRoutes": gate.evidence(DESKTOP),
        "sourceGate": gate.evidence(SOURCE_GATE),
        "tooling": {"receipt": gate.evidence(TOOLING), "tests": 48},
        "stateful": {"aggregate": gate.evidence(STATEFUL), "drivers": driver_receipts},
        "offlineBoundary": stateful["offlineBoundary"],
        "observations": {
            "anki": "Invoke reached the production button, but status remained the static capability string and no probe task was created.",
            "remnote": "Discovery reached the production module action, failed closed against the local denied endpoint, stayed unbound, and wrote nothing.",
            "question": f"The production question task was created; {len(content_results)} packaged Content attempts failed against the local denied endpoint, and the UI did not expose a usable terminal answer status.",
        },
        "evidenceInvalidation": gate.evidence(INVALIDATION),
        "historicalNoGoPreserved": gate.evidence(HISTORICAL_NO_GO),
        "safety": {
            "candidateSecretScan": secret_scan,
            "realExternalProviderCompletions": 0,
            "externalWrites": 0,
            "physicalSpeakerInvocations": 0,
            "candidateProcessesRemaining": 0,
            "preexistingProcessesTouched": 0,
        },
        "remainingGates": [
            "Anki probe production click must create a public Core task and project a typed read-only result.",
            "RemNote discovery/save/reopen must be qualified by the separately authorized real chain; this offline gate proves only fail-closed behavior.",
            "Question terminal failure/waiting/answer state must be readable through the production UI before follow-up and make-cards can be candidate-bound.",
            "External destination, physical speaker, installer/signing, and physical desktop capture/DPI boundaries remain outside this audit.",
        ],
    }

    output_root.mkdir(parents=True)
    receipt_path = output_root / "quality-receipt.json"
    readable_path = output_root / "INPUT-OUTPUT-INDEX.md"
    receipt_path.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    readable = f"""# KnowledgeClipper v1.2.40 production-routes candidate quality

Status: **{decision['status']}**  
Candidate: `{receipt['candidate']['root']}`  
Manifest: `{receipt['candidate']['manifest']['sha256']}`  
Tree: `{sealed['fileCount']} files / {sealed['totalBytes']} bytes / {sealed['treeSha256']}`

| Input/action | Candidate process output | Quality result |
|---|---|---|
| Settings -> Anki read-only probe | Button invoked; status stayed `available · worker-owned loopback read-only probe`; no task | NO-GO: production action is not wired to a typed Core result |
| RemNote root title `剪藏` -> discover | Module action ran against closed loopback, `state=blocked`, calls=1, writes=0; target stayed unbound | CONDITIONAL: fail-closed proved, real service success not claimed |
| Question `什么是不可变任务包？` -> Enter | Immutable task created; {len(content_results)} Content attempts failed against closed loopback; no live provider completion; UI terminal status was not usable | NO-GO: UI/Core terminal projection is incomplete |

Offline integrity: real external provider completions=0; external-origin references=0; credential sentinel persistence=0; candidate secret findings=0. The earlier evidence that completed a live DeepSeek call under an offline claim is preserved and explicitly invalidated by `{gate.evidence(INVALIDATION)['sha256']}`.

`KnowledgeClipper.exe` is not promoted as a user hand-test entrypoint by this receipt. Real external chains remain owned by their separately authorized Core runs.
"""
    readable_path.write_text(readable, encoding="utf-8")
    print(json.dumps({"status": decision["status"], "receipt": gate.evidence(receipt_path), "readable": gate.evidence(readable_path)}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
