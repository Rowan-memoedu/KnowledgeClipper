from __future__ import annotations

import hashlib
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


PROJECT = Path(__file__).resolve().parents[1]
CANDIDATE = PROJECT / "releases/kc-integrated-v1240-quality-20260902T015721Z/win-x64"
ASSEMBLY = PROJECT / "receipts/quality-integrated-candidate/20260902T015721Z-v1240/assembly.receipt.json"
AUTHORITY = PROJECT / "receipts/quality-authority-admission/20260902T015305Z-v1.2.40-quality/post-assembly-independent-authority-admission.receipt.json"
STARTUP = PROJECT / "canary/quality-integrated-v1240/20260902T015856Z-candidate-gates-retry/startup.stdout.json"
INTEGRATION = PROJECT / "canary/quality-integrated-v1240/20260902T020116Z-integration-canary-quoted/integration.stdout.json"
FAILED_RUN = PROJECT / "canary/quality-integrated-v1240/20260902T020500Z-production-stateful-journeys"
TOOLING = PROJECT / "canary/quality-sealed-stateful-runner/20260902T021000Z-v1240-cleanup-regression/tooling-receipt.json"
OUTPUT = PROJECT / "receipts/quality-integrated-candidate/20260902T021100Z-v1240-stateful-no-go/candidate-stateful-no-go.receipt.json"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def evidence(path: Path) -> dict[str, Any]:
    return {"path": path.relative_to(PROJECT).as_posix(), "bytes": path.stat().st_size, "sha256": sha256(path)}


def inventory(root: Path, excluded: set[str] | None = None) -> dict[str, Any]:
    excluded = excluded or set()
    rows: list[dict[str, Any]] = []
    material: list[str] = []
    total = 0
    for path in sorted((p for p in root.rglob("*") if p.is_file()), key=lambda p: p.relative_to(root).as_posix()):
        rel = path.relative_to(root).as_posix()
        if rel in excluded:
            continue
        size = path.stat().st_size
        digest = sha256(path)
        total += size
        rows.append({"path": rel, "bytes": size, "sha256": digest})
        material.append(f"{rel}\t{size}\t{digest.lower()}")
    return {
        "fileCount": len(rows),
        "totalBytes": total,
        "treeSha256": hashlib.sha256("\n".join(material).encode("utf-8")).hexdigest().upper(),
        "treeAlgorithm": "sha256 utf8 ordinal(path TAB bytes TAB lowercase-sha256) joined with LF, no trailing LF",
        "files": rows,
    }


def candidate_processes() -> list[dict[str, Any]]:
    marker = "kc-integrated-v1240-quality-20260902T015721Z"
    command = (
        "$rows=@(Get-CimInstance Win32_Process | Where-Object { "
        f"$_.ExecutablePath -like '*{marker}*' }} | Select-Object ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine); "
        "$rows | ConvertTo-Json -Depth 3 -Compress"
    )
    completed = subprocess.run(["pwsh", "-NoLogo", "-NoProfile", "-Command", command], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30, check=False)
    if completed.returncode != 0:
        raise RuntimeError(f"process audit failed: {completed.stderr}")
    text = completed.stdout.strip()
    if not text:
        return []
    value = json.loads(text)
    return value if isinstance(value, list) else [value]


def main() -> int:
    if OUTPUT.exists():
        raise FileExistsError(f"append-only output exists: {OUTPUT}")
    assembly = load(ASSEMBLY)
    authority = load(AUTHORITY)
    manifest_path = CANDIDATE / "candidate-file-manifest.v1.json"
    manifest = load(manifest_path)
    startup = load(STARTUP)
    integration = load(INTEGRATION)
    tooling = load(TOOLING)
    sealed = inventory(CANDIDATE)
    payload = inventory(CANDIDATE, {"candidate-file-manifest.v1.json"})
    processes = candidate_processes()
    anki_phase = FAILED_RUN / "journeys/anki/01-probe"
    remnote_root = FAILED_RUN / "journeys/remnote"
    question_root = FAILED_RUN / "journeys/question"
    isolated = inventory(FAILED_RUN / "isolated-real-store")
    program_text = (PROJECT / "src/Program.cs").read_text(encoding="utf-8-sig")

    checks = {
        "candidateSealedTreeUnchanged": sealed["treeSha256"] == assembly["sealed"]["treeSha256"],
        "manifestPayloadMatchesCandidate": payload == manifest["payload"],
        "authorityStillAdmitted": authority["status"] == "passed-authority-offline",
        "startupDiagnosticPassed": startup["result"] == "PASS" and startup["candidateIntegrationParsed"] and startup["candidateOrchestratorPathsVerified"],
        "embeddedNamedPipeCanaryPassed": integration["result"] == "PASS" and integration["orchestratorStartedByDesktop"] and integration["namedPipeInvoked"],
        "toolingRegressionPassed": tooling["status"] == "PASS-TOOLING-READY-FOR-CANDIDATE-SPEC-NOT-EXECUTED" and tooling["tests"]["total"] == 37,
        "ankiPhaseStarted": anki_phase.is_dir() and (anki_phase / "desktop.stdout.log").is_file(),
        "ankiUiDriverDidNotComplete": not (anki_phase / "ui-driver-receipt.json").exists() and not (FAILED_RUN / "journeys/anki/journey-receipt.json").exists(),
        "laterJourneysNotRun": not remnote_root.exists() and not question_root.exists(),
        "noTypedActionOrWorkerArtifacts": not any(
            path.is_file()
            for path in (FAILED_RUN / "isolated-real-store/localappdata/KnowledgeClipper/jobs").rglob("*")
        ),
        "ownedCandidateProcessesRemaining": len(processes) == 0,
        "productionOpenRoutesAbsentFromCurrentProgram": "--open-settings" not in program_text and "--open-question" not in program_text,
    }
    if not all(checks.values()):
        raise RuntimeError(f"NO-GO evidence self-check failed: {[key for key, value in checks.items() if not value]}")

    result = {
        "schemaVersion": "knowledge-clipper-quality-integrated-candidate-stateful-no-go.v1",
        "generatedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "NO-GO-CANDIDATE-STATEFUL-JOURNEYS",
        "candidateId": manifest["candidateId"],
        "decision": {"authorityV1240": "PASS-OFFLINE", "integratedCandidate": "NO-GO", "userHandTestEntrypoint": False, "formalRelease": "NO-GO"},
        "failure": {
            "journey": "anki",
            "phase": "probe",
            "observed": "The real candidate Desktop process did not expose the production settings AutomationId before the 180-second fail-closed driver limit; the driver ended with subprocess.TimeoutExpired.",
            "expectedAutomationId": "knowledge-clipper.desktop.settings.export.anki.probe-readonly",
            "diagnosis": "The current production Program route recognizes diagnostic/canary arguments only; --open-settings and --open-question are absent, so the stateful runner cannot enter those production views without a test-only state injection.",
            "sequenceBoundary": "RemNote and Question journeys were not run after the first mandatory journey failed.",
        },
        "candidate": {"root": CANDIDATE.relative_to(PROJECT).as_posix(), "manifest": evidence(manifest_path), "sealed": {key: sealed[key] for key in ("fileCount", "totalBytes", "treeSha256")}, "assembly": evidence(ASSEMBLY)},
        "authorityAdmission": evidence(AUTHORITY),
        "startup": evidence(STARTUP),
        "integrationNamedPipe": evidence(INTEGRATION),
        "statefulAttempt": {"root": FAILED_RUN.relative_to(PROJECT).as_posix(), "ankiPhaseFiles": inventory(anki_phase), "isolatedStore": isolated, "remnoteRun": False, "questionRun": False},
        "runnerTooling": evidence(TOOLING),
        "cleanup": {"manualOwnedOrchestratorPid": 46500, "identityCheckedBeforeStop": True, "remainingCandidateProcesses": processes, "preexistingProcessesTouched": 0, "runnerProcessTreeCleanupRegressionAdded": True},
        "effectBoundary": {"externalApiCalls": 0, "externalWrites": 0, "remnoteWrites": 0, "ankiWrites": 0, "mineruCalls": 0, "physicalSpeakerInvocations": 0, "localIsolatedSettingsFiles": isolated["fileCount"]},
        "checks": checks,
    }
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": result["status"], "receipt": evidence(OUTPUT)}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
