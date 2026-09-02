from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any


PROJECT = Path(__file__).resolve().parents[1]
EXPECTED = {
    "coreCheckpoint": "FF766146F8D8EF67F98B75B3BA4345F98C50E5F6A5B9023CB2DA1BAC7A0C9179",
    "authorityCheckpoint": "6C9ABC96A05EFFE1E09FB7E3C78BF4596654BE61B05826E16E1FCABF54C89018",
    "authorityReceipt": "C7063AD026F4FCF4738A653C6C19271BC9C625F1AD80918C6E6C7A8098B1D20D",
    "registry": "1C2E21A92198C25C52DDD6B528F3A50A00E90B741557EC2C22E2FB3C9460EDCC",
    "dependencyManifest": "A2B589E7E3578E01881D43AFBE4D6DAF3AC00CD04ABD9250A4F5FDBDBFBABA6B",
    "runtimeGates": "9DA09E5AFA7DF47C66DD1337AC0BB4464E0C8C629CDC1A5B8014445A0716F307",
    "coreValidation": "022026C469C619592117C5A63412B903ADCF677E304A2E3445737CD5F8343904",
    "historicalPlayback": "6007DBE8EAB70DAFC5D8961A85F3A8D55979F3C949ABE39035EAD659ED959969",
    "desktopR14": "88AF0CEDFDA80A5843D6DF4DB32F3D09FF2F3AEB9794720E5B9296C0064B2C4F",
}

PATHS = {
    "coreCheckpoint": PROJECT / "contracts/core/v2.9/checkpoints/v2.9.4-20260901T062900Z/checkpoint.json",
    "authorityCheckpoint": PROJECT / "contracts/authority/worker-registry/v1.2.37-20260901T063000Z/checkpoint.json",
    "authorityReceipt": PROJECT / "receipts/knowledge-clipper-authority-1.2.37-20260901T063200Z.json",
    "registry": PROJECT / "contracts/authority/worker-registry/v1.2.37-20260901T063000Z/worker-registry.v1.json",
    "dependencyManifest": PROJECT / "contracts/authority/worker-registry/v1.2.37-20260901T063000Z/dependency-manifest.v1.json",
    "runtimeGates": PROJECT / "canary/core-authority-v1.2.37/20260901T063100Z/runtime-gates.json",
    "coreValidation": PROJECT / "canary/core-v2.9.0-offline/20260901T061500Z/core-authority-validation.receipt.json",
    "historicalPlayback": PROJECT / "canary/quality-next-desktop-regressions/20260901T052000Z-core-precheckpoint/quality-receipt.json",
    "desktopR14": PROJECT / "canary/quality-desktop-producer-fixes/20260901T050000Z-independent-r14-final/quality-receipt.json",
}

RELEASE = PROJECT / "releases/knowledge-clipper-authority-v1.2.37-20260901T063000Z/win-x64"
INVALIDATIONS = {
    "1.2.35": PROJECT / "contracts/authority/worker-registry/v1.2.35-20260901T062000Z/invalidation-receipt.v1.json",
    "1.2.36": PROJECT / "contracts/authority/worker-registry/v1.2.36-20260901T062600Z/invalidation-receipt.v1.json",
}


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def rel(path: Path) -> str:
    return path.relative_to(PROJECT).as_posix()


def evidence(path: Path) -> dict[str, Any]:
    return {"path": rel(path), "bytes": path.stat().st_size, "sha256": sha256(path)}


def inventory(root: Path) -> tuple[dict[str, Any], dict[str, dict[str, Any]]]:
    files: dict[str, dict[str, Any]] = {}
    for path in sorted((p for p in root.rglob("*") if p.is_file()), key=lambda p: p.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        files[relative] = {"bytes": path.stat().st_size, "sha256": sha256(path)}
    material = "\n".join(f"{name}\t{item['bytes']}\t{item['sha256'].lower()}" for name, item in files.items())
    return {
        "fileCount": len(files),
        "totalBytes": sum(item["bytes"] for item in files.values()),
        "treeSha256": hashlib.sha256(material.encode("utf-8")).hexdigest().upper(),
        "treeAlgorithm": "sort relative paths ordinal; path TAB bytes TAB lowercase SHA-256; LF between rows; SHA-256 UTF-8",
    }, files


def verify(output: Path) -> dict[str, Any]:
    for name, path in PATHS.items():
        require(path.is_file(), f"missing evidence: {name}: {path}")
        require(sha256(path) == EXPECTED[name], f"evidence drift: {name}")

    checkpoint = load(PATHS["authorityCheckpoint"])
    authority_receipt = load(PATHS["authorityReceipt"])
    registry = load(PATHS["registry"])
    dependency = load(PATHS["dependencyManifest"])
    runtime = load(PATHS["runtimeGates"])
    core = load(PATHS["coreValidation"])

    observed, release_files = inventory(RELEASE)
    expected_release = (404, 262238747, "973E63B7F876A650D6EF4CFAF6D31F6386510BDE81FB2F096F83C403982CECCC")
    require(tuple(observed[k] for k in ("fileCount", "totalBytes", "treeSha256")) == expected_release, "release tree drift")
    require(checkpoint["admissionStatus"] == "authority-admitted", "checkpoint is not admitted")
    require(authority_receipt["admissionStatus"] == "authority-admitted", "authority receipt is not admitted")
    require(checkpoint["registry"]["registryVersion"] == "1.2.37", "checkpoint registry version drift")
    require(registry["registryVersion"] == "1.2.37" and len(registry["workers"]) == 5, "registry shape drift")
    require(checkpoint["registry"]["sha256"] == EXPECTED["registry"], "checkpoint registry hash drift")
    require(checkpoint["release"]["dependencyManifestSha256"] == EXPECTED["dependencyManifest"], "dependency hash binding drift")
    require((dependency["files"], dependency["bytes"], dependency["treeSha256"]) == expected_release, "dependency manifest release binding drift")
    require(dependency["legacyEntrypointsExcluded"] is True, "legacy entrypoints are not excluded")
    require(checkpoint["frozenV1"]["files"] == 21 and checkpoint["frozenV1"]["mismatches"] == 0, "frozen v1 drift")
    require(checkpoint["coreSettings"]["checkpointVersion"] == "2.9.4", "Core checkpoint version drift")
    require(checkpoint["coreSettings"]["sha256"] == EXPECTED["coreCheckpoint"], "Core checkpoint binding drift")

    checkpoint_workers = {worker["workerId"]: worker for worker in checkpoint["workers"]}
    registry_workers = {worker["workerId"]: worker for worker in registry["workers"]}
    expected_ids = {"image.ocr.mineru", "content.deepseek", "speech.edge", "export.remnote", "export.anki"}
    require(set(checkpoint_workers) == expected_ids == set(registry_workers), "worker IDs drift")
    worker_evidence: list[dict[str, Any]] = []
    for worker_id in sorted(expected_ids):
        cp_worker = checkpoint_workers[worker_id]
        reg_worker = registry_workers[worker_id]
        entrypoint = reg_worker["entrypoint"]["path"].replace("\\", "/")
        require(entrypoint == cp_worker["entrypoint"], f"entrypoint binding drift: {worker_id}")
        require(entrypoint in release_files, f"entrypoint missing from release: {worker_id}")
        require(release_files[entrypoint]["sha256"] == cp_worker["sha256"], f"entrypoint hash drift: {worker_id}")
        require(release_files[entrypoint]["bytes"] == cp_worker["bytes"], f"entrypoint bytes drift: {worker_id}")
        describe_path = Path(cp_worker["describeSnapshot"]["path"])
        require(describe_path.is_file(), f"describe snapshot missing: {worker_id}")
        require(sha256(describe_path) == cp_worker["describeSnapshot"]["sha256"], f"describe snapshot drift: {worker_id}")
        descriptor = load(describe_path)
        require(descriptor.get("workerId") == worker_id, f"describe identity drift: {worker_id}")
        worker_evidence.append({
            "workerId": worker_id,
            "workerVersion": cp_worker["workerVersion"],
            "entrypoint": entrypoint,
            "entrypointBytes": cp_worker["bytes"],
            "entrypointSha256": cp_worker["sha256"],
            "describeSnapshot": evidence(describe_path),
        })

    require(runtime["status"] == "passed", "runtime gates failed")
    require(runtime["validateRegistry"]["status"] == "valid" and runtime["validateRegistry"]["workers"] == 5, "runtime registry validation drift")
    require(runtime["protocol10"]["taskQuery"]["status"] == "passed" and runtime["protocol10"]["taskCommand"]["status"] == "passed", "protocol 1.0 gate failed")
    require(runtime["protocol11"]["stageControl"]["status"] == "passed", "protocol 1.1 stage control failed")
    require(runtime["workerRunInvocations"] == runtime["externalWrites"] == runtime["physicalSpeakerInvocations"] == 0, "runtime gate caused forbidden effects")

    require(core["status"] == "passed", "Core validation failed")
    require(core["contracts"]["schemasChecked"] == 86 and core["contracts"]["frozenContractsV1"] == "unchanged-by-this-release", "Core contract gate drift")
    require(core["coreAdapter"]["groups"] == 17 and core["orchestrator"]["tests"] == 23, "Core test counts drift")
    init = core["coreAdapter"]["ankiInitializeManaged"]
    require(init["unauthorizedWorkerCalls"] == 0 and init["realAnkiConnectCalls"] == 0, "Anki initialize isolation failed")
    require(init["firstRun"]["modelsCreated"] == 1 and init["firstRun"]["decksCreated"] == 1 and init["firstRun"]["exactReadback"] is True, "Anki initialize first-run semantics drift")
    require(init["explicitReplay"]["modelsCreated"] == 0 and init["explicitReplay"]["decksCreated"] == 0 and init["explicitReplay"]["status"] == "already_current", "Anki initialize replay is not idempotent")
    require(init["unknownExternalStateAutomaticRetries"] == 0, "unknown external state retried")
    require(core["safety"]["externalWrites"] == core["safety"]["physicalSpeakerInvocations"] == 0, "Core validation caused forbidden effects")

    historical = load(PATHS["historicalPlayback"])
    desktop_r14 = load(PATHS["desktopR14"])
    require(historical.get("status") != "PASS-AUTHORITY-ADMITTED-INDEPENDENT", "historical playback evidence was rewritten as current pass")
    require(desktop_r14.get("status") is not None, "Desktop r14 evidence unreadable")
    invalidations = []
    for version, path in INVALIDATIONS.items():
        item = load(path)
        require(item["registryVersion"] == version, f"invalidation version drift: {version}")
        require(item["status"] == "invalidated-never-authority-admitted", f"invalid version accepted: {version}")
        require(item["authorityCheckpointIssued"] is False and item["authorityReceiptIssued"] is False, f"invalid version claims authority: {version}")
        invalidations.append({"version": version, "decision": "REJECT-AS-AUTHORITY", "receipt": evidence(path)})

    receipt = {
        "schemaVersion": "knowledge-clipper-quality-authority-admission.v1",
        "status": "PASS-AUTHORITY-ADMITTED-INDEPENDENT",
        "scope": "authority-v1.2.37-read-only-admission",
        "candidateBound": False,
        "authority": {
            "version": "1.2.37",
            "checkpoint": evidence(PATHS["authorityCheckpoint"]),
            "authorityReceipt": evidence(PATHS["authorityReceipt"]),
            "registry": evidence(PATHS["registry"]),
            "dependencyManifest": evidence(PATHS["dependencyManifest"]),
            "releaseRoot": rel(RELEASE),
            **observed,
            "workers": worker_evidence,
            "frozenV1": {"files": 21, "mismatches": 0, "receiptSha256": checkpoint["frozenV1"]["sha256"]},
        },
        "core": {
            "checkpoint": evidence(PATHS["coreCheckpoint"]),
            "validation": evidence(PATHS["coreValidation"]),
            "coreAdapterGroups": 17,
            "orchestratorTests": 23,
            "ankiInitializeManagedV14": "PASS-FAKE-OFFLINE-IDEMPOTENT",
            "originalMediaPolicy": {
                "anki": "required-and-covered-offline",
                "remnote": "BY-DESIGN-UNSUPPORTED-NONBLOCKING",
            },
        },
        "runtime": {
            "receipt": evidence(PATHS["runtimeGates"]),
            "registry": "valid-5-of-5",
            "protocol10": "passed",
            "protocol11StageControl": "passed",
            "workerRunInvocations": 0,
            "externalWrites": 0,
            "physicalSpeakerInvocations": 0,
        },
        "history": {
            "playbackFailureEvidence": evidence(PATHS["historicalPlayback"]),
            "desktopR14Evidence": evidence(PATHS["desktopR14"]),
            "retained": True,
            "currentPassDoesNotEraseHistory": True,
            "interpretation": "v1.2.37 validates the current Core/runtime state; the earlier playback failure remains immutable regression history.",
        },
        "rejectedAuthorityCandidates": invalidations,
        "releaseDecision": {
            "authorityAdmission": "PASS",
            "integratedDesktopCandidate": "PENDING-NEW-CANDIDATE-BINDING",
            "releaseManifestCandidateHash": "PENDING",
            "signingInstaller": "PENDING",
            "userHandTestEntrypoint": False,
        },
        "safety": {
            "readOnly": True,
            "workersExecuted": 0,
            "externalServicesCalled": 0,
            "externalWrites": 0,
            "physicalSpeakerInvocations": 0,
        },
    }
    output.parent.mkdir(parents=True, exist_ok=False)
    output.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return receipt


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = verify(args.output.resolve())
    print(json.dumps({"status": result["status"], "receipt": str(args.output.resolve()), "sha256": sha256(args.output.resolve())}, ensure_ascii=False))


if __name__ == "__main__":
    main()
