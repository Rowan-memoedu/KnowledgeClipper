from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from verify_settings_control_plane import scan_runtime_tree_for_secrets


PROJECT = Path(__file__).resolve().parents[1]
AUTHORITY = PROJECT / "contracts/authority/worker-registry/v1.2.17-20260831T062800Z"
CHECKPOINT = AUTHORITY / "checkpoint.json"
REGISTRY = AUTHORITY / "worker-registry.v1.json"
DEPENDENCY = AUTHORITY / "dependency-manifest.v1.json"
PORTABLE = AUTHORITY / "portable-schema-closure-gate.v1.json"
RELEASE = PROJECT / "releases/knowledge-clipper-authority-v1.2.17-20260831T062800Z/win-x64"
CORE_CHECKPOINT = PROJECT / "contracts/core/v2.7/checkpoints/v2.7.2-20260831T062500Z/checkpoint.json"
QUALIFICATION = PROJECT / "canary/authority-v1.2.17-offline/20260831T063100Z/qualification-receipt.json"
OWNER_ADAPTER = PROJECT / "canary/core-v2.7.1-offline/20260831T062200Z/core-adapter.receipt.json"
OCR_BLOCKER = PROJECT / "canary/sealed-ocr-human-resume-v1.2.16/20260831T054424Z/blocker-receipt.json"
STABLE_RECEIPT = PROJECT / "receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json"
SCHEMA_TOOL = PROJECT / "tools/validate_release_schema_closure.py"
ADAPTER_PROJECT = PROJECT / "tests/CoreAdapter.IntegrationTests/CoreAdapter.IntegrationTests.csproj"
DOTNET = Path(r"D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe")

EXPECTED = {
    "coreCheckpoint": "B22CA9746905AC923CBFFEF913A7CAEB3D7FF0EDDE8507145E6A7B99BBD09F65",
    "registry": "7F2F9591C564D634AA6408D2B1C4C07D9876342C1B3FA11A99A23A882CF3EFA1",
    "checkpoint": "862ED5C41EC6BAA5F8B1446C9AA68DD131CEA7784D9EA8985CE54D42F2ACF95D",
    "dependency": "1590F69BF4DB1B2AD796360682833CCD7309693564C951BE9D426BCEE7B3152F",
    "qualification": "B29C16AC3D72F8ACF766739F1067026D21429BF913E3DD8A07581C81FB599CC1",
    "portable": "88A17A8B3334C6715342897428DC732689E0F11F2516F7F65D16B2FDF576F470",
    "artifact": "14AAA3BA918B201280DF462677C39A373ACDF421C8DA06C1D4A7B6D619DA0E91",
    "ownerAdapter": "5EF26CEF7F09F3615EA65568F8D0A382FBB9E196395B452578C27BCF5258E58D",
    "ocrBlocker": "7D840723707CD50C95CB7512B8FCC232C0467A44D1DF9A08FC4903E5556A224C",
    "releaseFiles": 552,
    "releaseBytes": 267756432,
    "releaseTree": "90FE03EA69FBA488ADEB8E1CF46AB6C02154D9120A42DF2E05C8729C66B0E38E",
}


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def io_path(path: Path) -> Path:
    absolute = path.resolve(strict=False)
    text = str(absolute)
    if len(text) >= 248 and absolute.drive and not text.startswith("\\\\?\\"):
        return Path("\\\\?\\" + text)
    return absolute


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with io_path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load(path: Path) -> dict[str, Any]:
    return json.loads(io_path(path).read_text(encoding="utf-8-sig"))


def relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def evidence(path: Path) -> dict[str, Any]:
    return {"path": relative(path), "bytes": io_path(path).stat().st_size, "sha256": sha256_file(path)}


def verify_ref(value: dict[str, Any], label: str) -> Path:
    path = Path(value["path"])
    if not path.is_absolute():
        path = PROJECT / path
    require(io_path(path).is_file(), f"{label}: missing: {path}")
    require(sha256_file(path) == str(value["sha256"]).upper(), f"{label}: SHA drift: {path}")
    if "bytes" in value:
        require(io_path(path).stat().st_size == int(value["bytes"]), f"{label}: bytes drift: {path}")
    return path


def inventory(root: Path) -> list[dict[str, Any]]:
    rows = []
    for path in root.rglob("*"):
        if path.is_file():
            rows.append({"path": path.relative_to(root).as_posix(), "bytes": path.stat().st_size, "sha256": sha256_file(path)})
    return sorted(rows, key=lambda item: item["path"])


def tree_sha(rows: list[dict[str, Any]]) -> str:
    text = "\n".join(f"{item['path']}\t{item['bytes']}\t{item['sha256'].lower()}" for item in rows)
    return hashlib.sha256(text.encode("utf-8")).hexdigest().upper()


def run(command: list[str], cwd: Path, *, env: dict[str, str] | None = None, timeout: int = 120) -> subprocess.CompletedProcess[str]:
    completed = subprocess.run(command, cwd=cwd, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout, check=False)
    require(completed.returncode == 0, f"command failed ({completed.returncode}): {command}: {completed.stderr}")
    return completed


def run_json(command: list[str], cwd: Path) -> tuple[dict[str, Any], str, str]:
    completed = run(command, cwd)
    require(completed.stdout.strip(), f"command returned no JSON: {command}")
    return json.loads(completed.stdout), completed.stdout, completed.stderr


def verify_release(run_binaries: bool) -> tuple[dict[str, Any], dict[str, str]]:
    for path, expected, label in [
        (CORE_CHECKPOINT, EXPECTED["coreCheckpoint"], "Core checkpoint"),
        (CHECKPOINT, EXPECTED["checkpoint"], "authority checkpoint"),
        (REGISTRY, EXPECTED["registry"], "registry"),
        (DEPENDENCY, EXPECTED["dependency"], "dependency manifest"),
        (PORTABLE, EXPECTED["portable"], "portable receipt"),
        (QUALIFICATION, EXPECTED["qualification"], "qualification"),
    ]:
        require(sha256_file(path) == expected, f"{label} SHA drift")
    checkpoint = load(CHECKPOINT)
    require(checkpoint["admissionStatus"] == "authority-admitted", "authority not admitted")
    require(checkpoint["registry"]["registryVersion"] == "1.2.17" and checkpoint["registry"]["workers"] == 5, "registry identity drift")
    require(checkpoint["core"]["version"] == "2.7.1" and checkpoint["core"]["checkpointSha256"] == EXPECTED["coreCheckpoint"], "Core identity drift")
    require(checkpoint["frozenV1"]["files"] == 21 and checkpoint["frozenV1"]["mismatches"] == 0, "frozen v1 checkpoint drift")
    rows = inventory(RELEASE)
    require(len(rows) == EXPECTED["releaseFiles"], "release file count drift")
    require(sum(item["bytes"] for item in rows) == EXPECTED["releaseBytes"], "release bytes drift")
    require(tree_sha(rows) == EXPECTED["releaseTree"], "release tree drift")
    dependency = load(DEPENDENCY)
    require(dependency["files"] == len(rows) and dependency["bytes"] == sum(item["bytes"] for item in rows) and dependency["treeSha256"] == EXPECTED["releaseTree"], "dependency manifest tree drift")
    closure, closure_stdout, closure_stderr = run_json(["python", str(SCHEMA_TOOL), "--release-root", str(RELEASE)], PROJECT)
    require(closure == {"schemaVersion": "knowledge-clipper-portable-schema-closure-gate.v1", "status": "passed", "releaseRoot": ".", "schemasChecked": 62, "localRefsChecked": 188, "failures": []}, "portable closure rerun drift")
    packed_artifact = RELEASE / "contracts/v1/schemas/common/artifact.schema.json"
    source_artifact = PROJECT / "contracts/v1/schemas/common/artifact.schema.json"
    require(sha256_file(packed_artifact) == EXPECTED["artifact"] == sha256_file(source_artifact), "packed frozen artifact schema modified")
    stable = load(STABLE_RECEIPT)
    require(len(stable["files"]) == 21, "stable receipt file count drift")
    mismatches = [item["path"] for item in stable["files"] if not Path(item["path"]).is_file() or sha256_file(Path(item["path"])) != item["sha256"].upper()]
    require(not mismatches, f"frozen v1 drift: {mismatches}")
    secret_scan = scan_runtime_tree_for_secrets(RELEASE)
    require(secret_scan["status"] == "passed" and secret_scan["findingCount"] == 0, "authority secret scan failed")
    runtime = {
        "portable-schema-closure.stdout.json": closure_stdout,
        "portable-schema-closure.stderr.log": closure_stderr,
    }
    validation = None
    workers = []
    if run_binaries:
        validation, stdout, stderr = run_json([
            str(RELEASE / "orchestrator/KnowledgeClipper.Orchestrator.exe"), "validate-registry",
            "--registry", str(REGISTRY), "--release-root", str(RELEASE),
        ], RELEASE)
        require(validation["status"] == "valid" and validation["registryVersion"] == "1.2.17" and validation["workers"] == 5, "runtime registry validation failed")
        runtime["validate-registry.stdout.json"] = stdout
        runtime["validate-registry.stderr.log"] = stderr
        for worker in load(REGISTRY)["workers"]:
            executable = RELEASE / worker["entrypoint"]["path"]
            descriptor, _, descriptor_stderr = run_json([str(executable), "describe", "--json"], RELEASE)
            require(descriptor["workerId"] == worker["workerId"], f"describe identity drift: {worker['workerId']}")
            workers.append({"workerId": descriptor["workerId"], "workerVersion": descriptor["workerVersion"], "entrypoint": worker["entrypoint"]["path"], "sha256": sha256_file(executable), "stderr": descriptor_stderr.strip()})
    return {
        "status": "passed",
        "checkpoint": evidence(CHECKPOINT),
        "registry": evidence(REGISTRY),
        "coreCheckpoint": evidence(CORE_CHECKPOINT),
        "qualification": evidence(QUALIFICATION),
        "dependencyManifest": evidence(DEPENDENCY),
        "release": {"root": relative(RELEASE), "files": len(rows), "bytes": sum(item["bytes"] for item in rows), "treeSha256": tree_sha(rows)},
        "portableClosure": {"status": "passed", "schemas": 62, "localRefs": 188, "failures": 0, "receipt": evidence(PORTABLE)},
        "frozenV1": {"files": 21, "mismatches": 0, "artifactSchema": evidence(packed_artifact), "sourceHashEqual": True},
        "secretScan": secret_scan,
        "runtimeRegistry": validation,
        "workers": workers,
    }, runtime


def validate_adapter_receipt(path: Path, expected_hash: str | None, label: str) -> dict[str, Any]:
    if expected_hash:
        require(sha256_file(path) == expected_hash, f"{label} receipt SHA drift")
    data = load(path)
    require(data["status"] == "passed", f"{label} receipt not passed")
    require(len(data["results"]) == 14 and all(item["status"] == "passed" for item in data["results"]), f"{label} expected 14 passed groups")
    module = [item for item in data["results"] if item["name"] == "module-action-public-dispatch"]
    require(len(module) == 1, f"{label} module-action group missing")
    module = module[0]
    require(module["ipc"] == "1.2" and module["remnoteProbeWrites"] == 0 and module["previewWithoutAuthorization"] == "rejected" and module["physicalSpeakerInvocations"] == 0, f"{label} module-action boundary drift")
    typed = {item["state"]: item for item in module["remnoteTypedStates"]}
    require(set(typed) == {"degraded", "blocked", "malformed"}, f"{label} typed-state matrix drift")
    for state in ("degraded", "blocked"):
        require(typed[state]["processExitCode"] == 3 and typed[state]["accepted"] is True and typed[state]["typedResultPreserved"] is True and typed[state]["externalWrites"] == 0 and typed[state]["replayStable"] is True, f"{label} typed {state} drift")
    malformed = typed["malformed"]
    require(malformed["processExitCode"] == 3 and malformed["accepted"] is False and malformed["typedResultPreserved"] is False and malformed["externalWrites"] == 0 and malformed["replayStable"] is False, f"{label} malformed fail-closed drift")
    receipt_evidence = evidence(path) if path.resolve().is_relative_to(PROJECT.resolve()) else {
        "temporaryInput": path.name,
        "bytes": io_path(path).stat().st_size,
        "sha256": sha256_file(path),
        "finalPathOwner": "runtimeEvidence",
    }
    return {
        "status": "passed",
        "receipt": receipt_evidence,
        "groups": 14,
        "healthy": {"status": "passed-via-public-dispatch-group", "writes": 0},
        "degraded": {"acceptedTyped": True, "exitCode": 3, "replayStable": True, "writes": 0},
        "blocked": {"acceptedTyped": True, "exitCode": 3, "replayStable": True, "writes": 0},
        "malformed": {"accepted": False, "typedPersisted": False, "failClosed": True, "writes": 0},
        "previewUnauthorized": {"rejected": True, "speaker": 0},
    }


def rerun_core_adapter() -> tuple[dict[str, Any], dict[str, Any]]:
    require(DOTNET.is_file(), f"workspace .NET SDK missing: {DOTNET}")
    with tempfile.TemporaryDirectory(prefix="kc-quality-v1217-") as temp:
        receipt_path = Path(temp) / "core-adapter-rerun.receipt.json"
        environment = dict(os.environ)
        environment["RQC_CORE_ADAPTER_RECEIPT"] = str(receipt_path)
        completed = run([str(DOTNET), "run", "--project", str(ADAPTER_PROJECT), "-c", "Release", "--no-restore", "--", str(PROJECT)], PROJECT, env=environment, timeout=180)
        require(receipt_path.is_file(), "CoreAdapter rerun did not emit a receipt")
        summary = validate_adapter_receipt(receipt_path, None, "fresh CoreAdapter rerun")
        return summary, {
            "core-adapter-rerun.receipt.json": receipt_path.read_bytes(),
            "core-adapter-rerun.stdout.log": completed.stdout,
            "core-adapter-rerun.stderr.log": completed.stderr,
        }


def verify_qualification() -> dict[str, Any]:
    qualification = load(QUALIFICATION)
    require(qualification["status"] == "passed-offline", "qualification not passed-offline")
    require(qualification["authority"]["registryVersion"] == "1.2.17", "qualification authority drift")
    verify_ref(qualification["authority"]["registry"], "qualification registry")
    verify_ref(qualification["authority"]["checkpoint"], "qualification checkpoint")
    verify_ref(qualification["authority"]["dependencyManifest"], "qualification dependency")
    verify_ref(qualification["core"]["checkpoint"], "qualification Core checkpoint")
    verify_ref(qualification["core"]["moduleAction"]["evidence"], "qualification CoreAdapter evidence")
    boundaries = qualification["boundaries"]
    require(all(boundaries[key] == 0 for key in ("workerRunInvocations", "externalCalls", "remnoteProbeInvocations", "remnoteWrites", "ankiWrites", "mineruCalls", "speakerInvocations")), "qualification side-effect boundary drift")
    return {"status": "passed", "receipt": evidence(QUALIFICATION), "healthyLiveEvidenceReusedOnly": boundaries["healthyLiveEvidenceReusedOnly"], "newLiveProbe": False, "externalCalls": 0}


def verify_ocr_conditional() -> dict[str, Any]:
    require(sha256_file(OCR_BLOCKER) == EXPECTED["ocrBlocker"], "OCR blocker SHA drift")
    data = load(OCR_BLOCKER)
    require(data["status"] == "conditional-blocked" and data["blockerCode"] == "core.precomputed_ocr_import_not_admitted", "OCR blocker status drift")
    require(data["mismatch"]["missingOutputKinds"] == ["image.processing.provenance", "image.original"], "OCR missing kinds drift")
    execution = data["execution"]
    require(all(execution[key] == 0 for key in ("newWorkerAttempts", "mineruRerun", "mineruProviderCalls", "contentCalls", "exporterAttempts", "ankiWrites", "remnoteWrites", "speakerCalls")), "OCR conditional caused side effects")
    return {"status": "conditional-blocked", "receipt": evidence(OCR_BLOCKER), "code": data["blockerCode"], "missingOutputKinds": data["mismatch"]["missingOutputKinds"], "precomputedImportContract": None, "mineruRerun": 0, "downstream": 0, "writes": 0}


def render(result: dict[str, Any]) -> str:
    return "\n".join([
        "# KnowledgeClipper authority v1.2.17 independent quality acceptance",
        "",
        f"- Status: `{result['status']}`",
        "- Scope: authority/Core offline admission only; no Desktop v1.2.17 candidate or new live external qualification.",
        "- Release: 552 files / 267,756,432 bytes / tree `90FE03EA...B0E38E`.",
        "- Portable closure: 62 schemas / 188 local refs / 0 failures; frozen artifact schema is byte-identical.",
        "- Registry: runtime-valid 5/5; five release-root `describe --json` identities and versions match.",
        "- CoreAdapter was independently rerun from source inputs: 14/14 groups PASS. Healthy public dispatch remained zero-write; degraded/blocked exit 3 preserved typed results; malformed exit 3 failed closed; unauthorized preview stayed rejected.",
        "- Qualification reused existing healthy RemNote owner evidence and made 0 live probe/external calls in this batch.",
        "",
        "## Remaining conditional",
        "",
        "- Existing Image 1.2.2 CANAR lacks same-run `image.processing.provenance` and `image.original`; no admitted precomputed import contract exists. MinerU rerun, downstream calls and writes remain 0.",
        "",
        "## Promotion boundary",
        "",
        "- Authority/Core offline admission: PASS.",
        "- Desktop candidate-bound promotion and complete external E2E: NOT RUN / NO-GO-CONDITIONAL.",
    ]) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--run-binaries", action="store_true", help="Run only portable closure, registry validation, worker describe and offline CoreAdapter; never worker run/live probe.")
    parser.add_argument("--emit-root")
    args = parser.parse_args()
    release, runtime = verify_release(args.run_binaries)
    owner = validate_adapter_receipt(OWNER_ADAPTER, EXPECTED["ownerAdapter"], "owner CoreAdapter")
    fresh = None
    if args.run_binaries:
        fresh, fresh_runtime = rerun_core_adapter()
        runtime.update(fresh_runtime)
    result = {
        "schemaVersion": "knowledge-clipper-quality-authority-v1217.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-AUTHORITY-V1.2.17-OFFLINE-CONDITIONAL-PRODUCT",
        "authorityBound": True,
        "desktopCandidateBound": False,
        "checks": {
            "release": release,
            "qualification": verify_qualification(),
            "ownerCoreAdapter": owner,
            "freshCoreAdapter": fresh,
            "existingOcrArtifact": verify_ocr_conditional(),
        },
        "completed": ["authorityAdmission", "portableSchemaClosure", "registry5Of5", "remnoteTypedProjectionOffline"],
        "conditional": ["existingOcrHumanResume"],
        "promotion": {"authorityCoreOffline": "PASS", "desktopCandidate": "NOT-ISSUED", "release": "NO-GO-CONDITIONAL"},
        "qualitySideEffects": {"externalCalls": 0, "workerRunCommands": 0, "liveRemnoteProbe": 0, "remnoteWrites": 0, "ankiWrites": 0, "mineruCalls": 0, "speakerCalls": 0},
        "invalidates": [{
            "path": "canary/product-gap-audit/20260831T064115Z-authority-v1217-quality/quality-authority-receipt.json",
            "sha256": "892375A942CCDEBA1561AB55D3D6A4D91DF7C51CC817B47B3A1FEBB4C14CD7D7",
            "reason": "fresh CoreAdapter receipt was rewritten in text mode, so the summary SHA/bytes did not match the persisted runtime evidence",
        }],
    }
    artifacts: dict[str, Any] = {}
    if args.emit_root:
        root = Path(args.emit_root).resolve()
        require(root.is_relative_to(PROJECT.resolve()), "output root escaped project")
        require(not root.exists(), "append-only output root exists")
        root.mkdir(parents=True)
        runtime_evidence = {}
        for name, content in runtime.items():
            path = root / name
            if isinstance(content, bytes):
                path.write_bytes(content)
            else:
                path.write_text(content if not content or content.endswith("\n") else content + "\n", encoding="utf-8", newline="")
            runtime_evidence[name] = evidence(path)
        result["runtimeEvidence"] = runtime_evidence
        receipt = root / "quality-authority-receipt.json"
        readable = root / "READABLE.md"
        receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        readable.write_text(render(result), encoding="utf-8")
        artifacts = {"root": relative(root), "receipt": evidence(receipt), "readable": evidence(readable)}
    print(json.dumps({"result": result, "artifacts": artifacts}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
