from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator

import verify_settings_control_plane as secret_quality


PROJECT_ROOT = Path(__file__).resolve().parents[1]
FIXTURE_ROOT = PROJECT_ROOT / "tests" / "Quality.Gates" / "fixtures" / "v1214-desktop-candidate-addendum"
FIXTURE_SCHEMA = FIXTURE_ROOT / "v1214-desktop-candidate-addendum.schema.json"
DEFAULT_FIXTURE = FIXTURE_ROOT / "valid" / "final-candidate.json"


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def load_json(path: Path) -> dict[str, Any]:
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        raise GateFailure(f"invalid JSON: {path}: {exc}") from exc


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def project_path(relative: str, *, file: bool = False, directory: bool = False) -> Path:
    path = (PROJECT_ROOT / relative).resolve()
    require(path.is_relative_to(PROJECT_ROOT.resolve()), f"out-of-project path: {relative}")
    if file:
        require(path.is_file(), f"missing file: {relative}")
    if directory:
        require(path.is_dir(), f"missing directory: {relative}")
    return path


def evidence(path: Path) -> dict[str, Any]:
    return {
        "path": path.resolve().relative_to(PROJECT_ROOT.resolve()).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha256(path),
    }


def inventory(root: Path, excluded: set[str] | None = None) -> dict[str, Any]:
    excluded = excluded or set()
    files: list[dict[str, Any]] = []
    rows: list[str] = []
    total = 0
    for path in sorted((item for item in root.rglob("*") if item.is_file()), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        if relative in excluded:
            continue
        size = path.stat().st_size
        digest = sha256(path)
        files.append({"path": relative, "bytes": size, "sha256": digest})
        rows.append(f"{relative}\t{size}\t{digest.lower()}")
        total += size
    return {
        "fileCount": len(files),
        "totalBytes": total,
        "treeSha256": hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper(),
        "files": files,
    }


def verify_fixture_document(config: dict[str, Any]) -> None:
    schema = load_json(FIXTURE_SCHEMA)
    Draft202012Validator.check_schema(schema)
    errors = sorted(Draft202012Validator(schema).iter_errors(config), key=lambda error: list(error.path))
    require(not errors, f"fixture rejected: {errors[0].message if errors else 'unknown'}")
    require(config["candidate"]["root"].endswith("kc-i-crq-v1214-20260830T105500Z/win-x64"), "fixture is not bound to the final v1.2.14 candidate")
    require(config["manualBoundaryPolicy"] == "hand-test-not-release-failure", "physical boundary policy drifted")
    require(config["externalRerun"] is False, "external rerun is forbidden for this addendum")


def validate_fixtures() -> dict[str, Any]:
    schema = load_json(FIXTURE_SCHEMA)
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema)
    valid = sorted((FIXTURE_ROOT / "valid").glob("*.json"))
    invalid = sorted((FIXTURE_ROOT / "invalid").glob("*.json"))
    require(valid and invalid, "fixture set is incomplete")
    for path in valid:
        value = load_json(path)
        require(not list(validator.iter_errors(value)), f"valid fixture rejected: {path.name}")
        verify_fixture_document(value)
    for path in invalid:
        value = load_json(path)
        schema_rejected = bool(list(validator.iter_errors(value)))
        semantic_rejected = False
        if not schema_rejected:
            try:
                verify_fixture_document(value)
            except GateFailure:
                semantic_rejected = True
        require(schema_rejected or semantic_rejected, f"invalid fixture accepted: {path.name}")
    return {
        "status": "PASS",
        "validAccepted": len(valid),
        "invalidRejected": len(invalid),
        "schema": evidence(FIXTURE_SCHEMA),
    }


def verify_manifest(candidate_root: Path, config: dict[str, Any], full: dict[str, Any]) -> dict[str, Any]:
    manifest_path = candidate_root / "candidate-file-manifest.v1.json"
    require(sha256(manifest_path) == config["manifestSha256"].upper(), "candidate manifest SHA-256 mismatch")
    manifest = load_json(manifest_path)
    require(manifest["candidateId"] == config["id"], "candidate manifest identity mismatch")
    require(manifest["entrypoint"] == "KnowledgeClipper.exe", "candidate entrypoint identity mismatch")
    require(manifest["excludedControlFiles"] == ["candidate-file-manifest.v1.json"], "candidate control-file exclusion drifted")

    excluded = set(manifest["excludedControlFiles"])
    actual_payload = inventory(candidate_root, excluded)
    expected_payload = manifest["payload"]
    require(
        (actual_payload["fileCount"], actual_payload["totalBytes"], actual_payload["treeSha256"])
        == (expected_payload["fileCount"], expected_payload["totalBytes"], expected_payload["treeSha256"].upper()),
        "candidate payload aggregate mismatch",
    )
    actual_files = {item["path"]: item for item in actual_payload["files"]}
    expected_files = {item["path"]: item for item in expected_payload["files"]}
    require(set(actual_files) == set(expected_files), "candidate manifest path set mismatch")
    for relative, actual in actual_files.items():
        expected = expected_files[relative]
        require(actual["bytes"] == expected["bytes"] and actual["sha256"] == expected["sha256"].upper(), f"candidate manifest file drift: {relative}")
    require(
        (full["fileCount"], full["totalBytes"], full["treeSha256"])
        == (config["sealedFileCount"], config["sealedTotalBytes"], config["sealedTreeSha256"].upper()),
        "sealed candidate inventory mismatch",
    )
    return {
        "manifest": evidence(manifest_path),
        "payload": {key: actual_payload[key] for key in ("fileCount", "totalBytes", "treeSha256")},
        "document": manifest,
    }


def verify_desktop(candidate_root: Path, config: dict[str, Any], manifest: dict[str, Any], full_files: dict[str, dict[str, Any]]) -> dict[str, Any]:
    build_path = candidate_root / "desktop-build-receipt.v1.json"
    build = load_json(build_path)
    require(sha256(build_path) == config["buildReceiptSha256"].upper(), "Desktop build receipt SHA-256 mismatch")
    require(build["receiptExcludedFromTreeHash"] is True, "Desktop receipt exclusion contract drifted")
    require(build["selfContained"] is True and build["runtimeIdentifier"] == "win-x64", "Desktop self-contained runtime mismatch")
    require(len(build["files"]) == config["fileCount"], "Desktop recursive receipt file count mismatch")
    rows: list[str] = []
    total = 0
    for item in sorted(build["files"], key=lambda value: value["path"]):
        actual = full_files.get(item["path"])
        require(actual is not None, f"Desktop receipt path missing from candidate: {item['path']}")
        require(actual["bytes"] == item["bytes"] and actual["sha256"] == item["sha256"].upper(), f"Desktop recursive receipt drift: {item['path']}")
        rows.append(f"{item['path']}\n{actual['sha256'].lower()}\n{actual['bytes']}\n")
        total += actual["bytes"]
    tree = hashlib.sha256("".join(rows).encode("utf-8")).hexdigest().upper()
    require(
        (len(rows), total, tree) == (config["fileCount"], config["totalBytes"], config["treeSha256"].upper()),
        "Desktop recursive receipt aggregate mismatch",
    )
    require((build["fileCount"], build["totalBytes"], build["treeSha256"].upper()) == (len(rows), total, tree), "Desktop build receipt summary mismatch")
    require(manifest["desktop"]["buildReceiptSha256"].upper() == sha256(build_path), "manifest/Desktop receipt binding mismatch")
    require(
        (manifest["desktop"]["fileCount"], manifest["desktop"]["totalBytes"], manifest["desktop"]["treeSha256"].upper())
        == (len(rows), total, tree),
        "manifest/Desktop payload binding mismatch",
    )
    entrypoint = candidate_root / "KnowledgeClipper.exe"
    dll = candidate_root / "KnowledgeClipper.dll"
    require(sha256(entrypoint) == build["executableSha256"].upper(), "Desktop EXE/build receipt mismatch")
    require(sha256(dll) == build["applicationDllSha256"].upper(), "Desktop DLL/build receipt mismatch")
    return {
        "status": "PASS-544-FILES-RECURSIVE",
        "buildReceipt": evidence(build_path),
        "fileCount": len(rows),
        "totalBytes": total,
        "treeSha256": tree,
        "entrypoint": evidence(entrypoint),
        "applicationDll": evidence(dll),
    }


def verify_desktop_equality(candidate_root: Path, config: dict[str, Any], full_files: dict[str, dict[str, Any]]) -> dict[str, Any]:
    equality_path = project_path(config["equalityEvidence"], file=True)
    equality = load_json(equality_path)
    require(equality["status"] == "PASS" and equality["changedFiles"] == 0, "Desktop equality evidence is not zero-drift")
    require(equality["currentCandidate"] == config["candidateId"], "Desktop equality evidence candidate mismatch")
    require(len(equality["files"]) == config["fileCount"], "Desktop equality file count mismatch")
    for item in equality["files"]:
        actual = full_files.get(item["path"])
        require(actual is not None and actual["bytes"] == item["bytes"] and actual["sha256"] == item["sha256"].upper(), f"Desktop equality file drift: {item['path']}")
    require(equality["buildReceiptSha256"].upper() == config["buildReceiptSha256"].upper(), "Desktop equality/build receipt mismatch")
    return {"status": "PASS-ZERO-DRIFT", "changedFiles": 0, "fileCount": len(equality["files"]), "evidence": evidence(equality_path)}


def verify_authority(candidate_root: Path, config: dict[str, Any], manifest: dict[str, Any]) -> dict[str, Any]:
    source_root = project_path(config["sourceReleaseRoot"], directory=True)
    embedded_root = candidate_root / "authority" / "win-x64"
    source = inventory(source_root)
    embedded = inventory(embedded_root)
    expected = (config["releaseFileCount"], config["releaseTotalBytes"], config["releaseTreeSha256"].upper())
    require((source["fileCount"], source["totalBytes"], source["treeSha256"]) == expected, "source authority inventory mismatch")
    require((embedded["fileCount"], embedded["totalBytes"], embedded["treeSha256"]) == expected, "embedded authority inventory mismatch")
    source_files = {item["path"]: item for item in source["files"]}
    embedded_files = {item["path"]: item for item in embedded["files"]}
    require(source_files == embedded_files, "embedded authority is not byte-for-byte equal to source authority")

    checkpoint_path = project_path(config["checkpointPath"], file=True)
    registry_path = project_path(config["registryPath"], file=True)
    require(sha256(checkpoint_path) == config["checkpointSha256"].upper(), "authority checkpoint SHA-256 mismatch")
    require(sha256(registry_path) == config["registrySha256"].upper(), "authority registry SHA-256 mismatch")
    embedded_checkpoint = candidate_root / "authority" / "metadata" / "checkpoint.json"
    embedded_registry = candidate_root / "authority" / "metadata" / "worker-registry.v1.json"
    require(sha256(embedded_checkpoint) == sha256(checkpoint_path), "embedded authority checkpoint mismatch")
    require(sha256(embedded_registry) == sha256(registry_path), "embedded authority registry mismatch")
    checkpoint = load_json(checkpoint_path)
    registry = load_json(registry_path)
    require(checkpoint["admissionStatus"] == "authority-admitted" and checkpoint["registry"]["registryVersion"] == "1.2.14", "authority checkpoint is not admitted v1.2.14")
    require(registry["registryVersion"] == "1.2.14" and len(registry["workers"]) == 5, "authority registry is not v1.2.14/5 workers")
    require(checkpoint["frozenV1"]["mismatches"] == 0, "frozen v1 drift detected")
    require(
        (manifest["authority"]["registryVersion"], manifest["authority"]["checkpointSha256"].upper(), manifest["authority"]["registrySha256"].upper(), manifest["authority"]["releaseTreeSha256"].upper())
        == ("1.2.14", sha256(checkpoint_path), sha256(registry_path), embedded["treeSha256"]),
        "candidate manifest authority binding mismatch",
    )
    return {
        "status": "PASS-AUTHORITY-V1.2.14-EXACT",
        "version": "1.2.14",
        "checkpoint": evidence(checkpoint_path),
        "registry": evidence(registry_path),
        "workers": sorted(item["workerId"] for item in registry["workers"]),
        "frozenV1Mismatches": 0,
        "sourceReleaseRoot": config["sourceReleaseRoot"],
        "embeddedReleaseRoot": embedded_root.relative_to(PROJECT_ROOT).as_posix(),
        "release": {"fileCount": embedded["fileCount"], "totalBytes": embedded["totalBytes"], "treeSha256": embedded["treeSha256"]},
        "filesCompared": len(embedded_files),
        "changedFiles": 0,
    }


def verify_owner_receipt_and_logs(config: dict[str, Any], candidate_root: Path, candidate_manifest_sha: str, full: dict[str, Any]) -> dict[str, Any]:
    receipt_path = project_path(config["ownerReceiptPath"], file=True)
    require(sha256(receipt_path) == config["ownerReceiptSha256"].upper(), "Desktop owner receipt SHA-256 mismatch")
    receipt = load_json(receipt_path)
    require(receipt["candidateId"] == config["candidateId"] and receipt["candidate"]["root"] == config["candidateRoot"], "Desktop owner receipt candidate mismatch")
    require(receipt["candidate"]["manifest"]["sha256"].upper() == candidate_manifest_sha, "Desktop owner receipt manifest mismatch")
    require(receipt["candidate"]["sealed"]["fileCount"] == full["fileCount"] and receipt["candidate"]["sealed"]["totalBytes"] == full["totalBytes"] and receipt["candidate"]["sealed"]["treeSha256"].upper() == full["treeSha256"], "Desktop owner receipt sealed inventory mismatch")

    checked: dict[str, Any] = {}
    for name in ("safeStartup", "singleInstance", "registry5Of5", "embeddedOrchestratorNamedPipeQuery", "manifestTree", "desktopPayloadEquality"):
        ref = receipt["automatedEvidence"][name]
        path = project_path(ref["path"], file=True)
        require(path.stat().st_size == ref["bytes"] and sha256(path) == ref["sha256"].upper(), f"Desktop evidence drift: {name}")
        checked[name] = evidence(path)

    startup = load_json(project_path(receipt["automatedEvidence"]["safeStartup"]["path"], file=True))
    single = load_json(project_path(receipt["automatedEvidence"]["singleInstance"]["path"], file=True))
    registry = load_json(project_path(receipt["automatedEvidence"]["registry5Of5"]["path"], file=True))
    query = load_json(project_path(receipt["automatedEvidence"]["embeddedOrchestratorNamedPipeQuery"]["path"], file=True))
    require(startup["result"] == "PASS" and startup["packageTreeVerified"] is True and startup["candidateOrchestratorPathsVerified"] is True, "safe startup log failed")
    require(startup["externalConnections"] == 0 and startup["namedPipeInvoked"] is False and startup["productionWorkDirectoryCreated"] is False, "safe startup crossed side-effect boundary")
    require(single["userFeedbackMode"] == "activate-existing-window-and-message-box", "single-instance behavior is configured as silent")
    require(single["existingInstanceDetected"] is False and single["existingProcessId"] is None, "single-instance diagnostic unexpectedly attached to an existing process")
    require(registry["status"] == "valid" and registry["registryVersion"] == "1.2.14" and registry["workers"] == 5, "registry validation log failed")
    require(query["result"] == "PASS" and query["orchestratorStartedByDesktop"] is True and query["namedPipeInvoked"] is True and query["action"] == "task.status.query-recent", "candidate-specific Named Pipe query failed")
    require(not any(query[key] for key in ("externalWorkerInvoked", "physicalSpeakerInvoked", "remNoteInvoked", "ankiInvoked", "mineruInvoked")), "candidate query crossed external boundary")
    return {
        "status": "PASS-INDEPENDENT-LOG-READBACK",
        "ownerReceipt": evidence(receipt_path),
        "ownerStatusObservedNotTrustedForPass": receipt["status"],
        "userHandTestEntrypoint": receipt["userHandTestEntrypoint"],
        "logs": checked,
        "startup": {"result": startup["result"], "externalConnections": startup["externalConnections"], "productionWorkDirectoryCreated": startup["productionWorkDirectoryCreated"]},
        "singleInstance": {"existingInstanceDetected": single["existingInstanceDetected"], "userFeedbackMode": single["userFeedbackMode"], "existingProcessId": single["existingProcessId"]},
        "query": {"result": query["result"], "action": query["action"], "namedPipeInvoked": query["namedPipeInvoked"], "externalWorkerInvoked": query["externalWorkerInvoked"]},
    }


def verify_safety(candidate_root: Path) -> dict[str, Any]:
    scan = secret_quality.scan_runtime_tree_for_secrets(candidate_root)
    require(scan["findingCount"] == 0, "candidate secret scan found plaintext material")
    credential_files = [
        path.relative_to(candidate_root).as_posix()
        for path in candidate_root.rglob("*")
        if path.is_file() and path.name.casefold() in {"credentials.json", "credentials.json.bak", ".env"}
    ]
    require(not credential_files, f"candidate contains credential store files: {credential_files}")
    legacy = [
        path.relative_to(candidate_root).as_posix()
        for path in candidate_root.rglob("*")
        if path.is_file() and path.name.casefold() in {"smoketest.exe", "ttscanary.exe"}
    ]
    require(not legacy, f"candidate contains legacy external canaries: {legacy}")
    return {
        "status": "PASS-ZERO-FINDINGS",
        "filesChecked": scan["filesChecked"],
        "findingCount": 0,
        "credentialFiles": [],
        "legacyExternalCanaries": [],
        "excludedNegativeFixtureCount": scan["excludedNegativeFixtureCount"],
        "excludedBoundary": "Only authority contract fixtures whose path contains both fixtures and invalid are excluded from plaintext-value scanning; their hashes remain covered by candidate and authority tree gates.",
    }


def evaluate(config: dict[str, Any]) -> dict[str, Any]:
    verify_fixture_document(config)
    candidate_cfg = config["candidate"]
    candidate_root = project_path(candidate_cfg["root"], directory=True)
    full = inventory(candidate_root)
    full_files = {item["path"]: item for item in full["files"]}
    manifest_result = verify_manifest(candidate_root, candidate_cfg, full)
    manifest = manifest_result.pop("document")
    desktop = verify_desktop(candidate_root, config["desktop"], manifest, full_files)
    equality = verify_desktop_equality(candidate_root, config["desktop"], full_files)
    authority = verify_authority(candidate_root, config["authority"], manifest)
    owner = verify_owner_receipt_and_logs(config["evidence"], candidate_root, manifest_result["manifest"]["sha256"], full)
    safety = verify_safety(candidate_root)

    base_path = project_path(config["baseQualityReceipt"]["path"], file=True)
    require(sha256(base_path) == config["baseQualityReceipt"]["sha256"].upper(), "base quality receipt SHA-256 mismatch")
    base = load_json(base_path)
    require(base["status"] == "PASS-SEALED-QUESTION-ANKI-DESKTOP-CANDIDATE-PENDING", "base quality receipt is not the expected pending checkpoint")
    require(owner["userHandTestEntrypoint"] is True, "Desktop owner did not expose this final candidate as the hand-test entrypoint")

    return {
        "schemaVersion": "knowledge-clipper-v1.2.14-final-candidate-bound-quality-addendum.v1",
        "status": "PASS-CANDIDATE-BOUND-AUTOMATED-HANDTEST-BOUNDARIES-REMAIN",
        "candidateBound": True,
        "candidateHash": manifest_result["manifest"]["sha256"],
        "candidate": {
            "id": candidate_cfg["id"],
            "root": candidate_cfg["root"],
            "manifest": manifest_result["manifest"],
            "payload": manifest_result["payload"],
            "sealed": {key: full[key] for key in ("fileCount", "totalBytes", "treeSha256")},
        },
        "desktop": {**desktop, "equality": equality},
        "authority": authority,
        "automatedEvidence": owner,
        "safety": safety,
        "baseQualityReceipt": evidence(base_path),
        "executionBoundary": {
            "externalRerun": False,
            "externalWorkerCalls": 0,
            "externalWrites": 0,
            "physicalSpeakerInvocations": 0,
            "candidateBinariesExecutedByThisGate": False,
            "evidenceMode": "read-only-hash-and-log-replay",
        },
        "promotionDecision": {
            "status": "PASS-FOR-USER-HANDTEST-ENTRYPOINT",
            "userHandTestEntrypoint": True,
            "releaseFailure": False,
            "manualBoundaries": [
                "Physical external-application foreground/focus and selection remain a user-environment hand-test boundary.",
                "Mixed-DPI and cross-monitor resize/capture remain a user-environment hand-test boundary.",
            ],
            "manualBoundaryPolicy": config["manualBoundaryPolicy"],
        },
    }


def write_outputs(result: dict[str, Any], output_root: Path) -> tuple[Path, Path]:
    require(not output_root.exists(), f"append-only output already exists: {output_root}")
    output_root.mkdir(parents=True)
    receipt = dict(result)
    receipt["createdAt"] = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    receipt_path = output_root / "v1214-final-candidate-bound-quality-receipt.json"
    receipt_path.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    readable_path = output_root / "READABLE.md"
    readable_path.write_text(
        "# KnowledgeClipper v1.2.14 final candidate-bound quality addendum\n\n"
        f"- Status: `{receipt['status']}`\n"
        f"- Candidate: `{receipt['candidate']['root']}`\n"
        f"- Candidate hash (manifest SHA-256): `{receipt['candidateHash']}`\n"
        f"- Sealed tree: {receipt['candidate']['sealed']['fileCount']} files / {receipt['candidate']['sealed']['totalBytes']} bytes / `{receipt['candidate']['sealed']['treeSha256']}`\n"
        f"- Desktop recursive payload: {receipt['desktop']['fileCount']} files / `{receipt['desktop']['treeSha256']}`; equality changed files = {receipt['desktop']['equality']['changedFiles']}\n"
        f"- Authority: v{receipt['authority']['version']}, {receipt['authority']['release']['fileCount']} files, source-to-embedded changed files = {receipt['authority']['changedFiles']}\n"
        f"- Secret scan: {receipt['safety']['filesChecked']} runtime files checked, findings = {receipt['safety']['findingCount']}; excluded invalid contract fixtures = {receipt['safety']['excludedNegativeFixtureCount']} and remain tree-hash covered.\n"
        "- Startup/query evidence: safe startup, non-silent single instance, registry 5/5, candidate-specific Named Pipe recent-task query all independently read back from hash-bound logs.\n"
        "- This gate executed no candidate binary, worker, external API, destination write, MinerU or speaker.\n"
        "- Physical external-app focus/selection and mixed-DPI/cross-monitor behavior remain hand-test boundaries; they are not classified as release failures.\n",
        encoding="utf-8",
    )
    return receipt_path, readable_path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--fixture", type=Path, default=DEFAULT_FIXTURE)
    parser.add_argument("--output-root", type=Path)
    parser.add_argument("--validate-fixtures", action="store_true")
    args = parser.parse_args()
    fixtures = validate_fixtures()
    if args.validate_fixtures:
        print(json.dumps(fixtures, ensure_ascii=False))
        return
    result = evaluate(load_json(args.fixture))
    output_root = args.output_root
    if output_root is None:
        stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        output_root = PROJECT_ROOT / "canary" / "v1214-candidate-qualification" / f"{stamp}-desktop-final-candidate-bound"
    elif not output_root.is_absolute():
        output_root = PROJECT_ROOT / output_root
    receipt, readable = write_outputs(result, output_root)
    print(json.dumps({
        "status": result["status"],
        "candidateHash": result["candidateHash"],
        "receipt": receipt.relative_to(PROJECT_ROOT).as_posix(),
        "receiptSha256": sha256(receipt),
        "readable": readable.relative_to(PROJECT_ROOT).as_posix(),
        "readableSha256": sha256(readable),
    }, ensure_ascii=False))


if __name__ == "__main__":
    main()
