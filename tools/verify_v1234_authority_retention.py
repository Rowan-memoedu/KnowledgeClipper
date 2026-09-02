from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import pathlib
import subprocess
import sys


PROJECT = pathlib.Path(__file__).resolve().parents[1]
AUTHORITY = PROJECT / "contracts/authority/worker-registry/v1.2.34-20260901T043500Z"
AUTHORITY_RELEASE = PROJECT / "releases/knowledge-clipper-authority-v1.2.34-20260901T043500Z/win-x64"
DESKTOP_CANDIDATE = PROJECT / "releases/kc-i-speech-settings-v1234-20260901T060000Z/win-x64"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def sha(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def evidence(path: pathlib.Path) -> dict[str, object]:
    require(path.is_file(), f"Missing evidence: {path}")
    return {
        "path": path.relative_to(PROJECT).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha(path),
    }


def read_json(path: pathlib.Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def tree(root: pathlib.Path, excluded: set[str] | None = None) -> dict[str, object]:
    excluded = excluded or set()
    rows: list[str] = []
    total = 0
    count = 0
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(root).as_posix()
        if relative in excluded:
            continue
        size = path.stat().st_size
        rows.append(f"{relative}\t{size}\t{sha(path).lower()}")
        total += size
        count += 1
    rows.sort()
    return {
        "fileCount": count,
        "totalBytes": total,
        "treeSha256": hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper(),
    }


def run(command: list[str], expected_exit: int = 0) -> subprocess.CompletedProcess[str]:
    completed = subprocess.run(command, cwd=PROJECT, text=True, capture_output=True, encoding="utf-8")
    require(completed.returncode == expected_exit,
            f"Unexpected exit {completed.returncode} for {command!r}: {completed.stdout}\n{completed.stderr}")
    return completed


def verify_frozen_v1() -> dict[str, object]:
    receipt_path = PROJECT / "receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json"
    require(sha(receipt_path) == "1CB738FE133F6645B9757E246E2FBC210785086342F70BAD32B5015BD19E1B1E",
            "Frozen v1 receipt drifted")
    receipt = read_json(receipt_path)
    entries = receipt.get("files") or receipt.get("tree", {}).get("files")
    require(isinstance(entries, list) and len(entries) == 21, "Frozen v1 receipt does not enumerate 21 files")
    mismatches = []
    for item in entries:
        path = pathlib.Path(item["path"])
        if not path.is_absolute():
            path = PROJECT / path
        bytes_match = "bytes" not in item or path.is_file() and path.stat().st_size == int(item["bytes"])
        if not path.is_file() or not bytes_match or sha(path) != item["sha256"].upper():
            mismatches.append(item["path"])
    require(not mismatches, f"Frozen v1 mismatch: {mismatches}")
    return {"receipt": evidence(receipt_path), "filesChecked": 21, "mismatches": 0}


def verify_authority() -> tuple[dict[str, object], dict[str, object]]:
    expected = {
        "checkpoint": "3748DA2DDCD4A3E5F65BA5E90AEBE470339A8D8AE91D458AD9B0A95DCD2F2918",
        "registry": "96966ADD1BE8B8CDA9D456DCBC3E2860B89F85C752330A0F38AF2A05C6AA71A5",
        "receipt": "047F33677EE2EA17309C34F022C37240745242F46B83CF7D1E723A2CCF47BFCD",
        "tree": "F73ED8E39B5120D04EF332671CD7DF4F1131C7715ECFE1081517604C2107EFDA",
    }
    checkpoint_path = AUTHORITY / "checkpoint.json"
    registry_path = AUTHORITY / "worker-registry.v1.json"
    authority_receipt_path = PROJECT / "receipts/knowledge-clipper-authority-1.2.34-20260901T043600Z.json"
    require(sha(checkpoint_path) == expected["checkpoint"], "Authority checkpoint drifted")
    require(sha(registry_path) == expected["registry"], "Authority registry drifted")
    require(sha(authority_receipt_path) == expected["receipt"], "Authority receipt drifted")
    release_tree = tree(AUTHORITY_RELEASE)
    require(release_tree == {"fileCount": 386, "totalBytes": 261873123, "treeSha256": expected["tree"]},
            f"Authority release tree drifted: {release_tree}")

    registry = read_json(registry_path)
    expected_workers = {
        "image.ocr.mineru": "1.2.4",
        "content.deepseek": "0.9.5",
        "speech.edge": "1.2.5",
        "export.remnote": "2.2.0",
        "export.anki": "1.3.2",
    }
    require(registry["registryVersion"] == "1.2.34" and len(registry["workers"]) == 5, "Authority registry identity drifted")
    describes = []
    for worker in registry["workers"]:
        worker_id = worker["workerId"]
        executable = AUTHORITY_RELEASE / pathlib.PurePosixPath(worker["entrypoint"]["path"])
        completed = run([str(executable), "describe", "--json"])
        descriptor = json.loads(completed.stdout)
        require(descriptor["workerId"] == worker_id, f"Worker identity drift: {worker_id}")
        worker_version = descriptor.get("workerVersion", descriptor.get("version"))
        require(worker_version == expected_workers[worker_id], f"Worker downgrade/drift: {worker_id}")
        describes.append({"workerId": worker_id, "version": worker_version, "entrypoint": evidence(executable)})

    orchestrator = AUTHORITY_RELEASE / "orchestrator/KnowledgeClipper.Orchestrator.exe"
    registry_validation = json.loads(run([
        str(orchestrator), "validate-registry", "--registry", str(registry_path), "--release-root", str(AUTHORITY_RELEASE)
    ]).stdout)
    require(registry_validation["status"] == "valid" and registry_validation["workers"] == 5,
            "Packaged registry validation failed")

    closure = json.loads(run([sys.executable, "tools/validate_release_schema_closure.py", "--release-root", str(AUTHORITY_RELEASE)]).stdout)
    require(closure["status"] == "passed" and closure["schemasChecked"] == 63 and closure["localRefsChecked"] == 188,
            "Portable schema closure drifted")

    return ({
        "version": "1.2.34",
        "checkpoint": evidence(checkpoint_path),
        "registry": evidence(registry_path),
        "authorityReceipt": evidence(authority_receipt_path),
        "release": release_tree,
        "workers": describes,
        "registryValidation": registry_validation,
        "portableSchemaClosure": closure,
    }, expected)


def verify_retention() -> dict[str, object]:
    receipt_path = PROJECT / "canary/state-aware-retention-production/20260901T042500Z/receipt.json"
    require(sha(receipt_path) == "88CF8FC4E39F2CC04E454DC4198B99A76F10E4D58DB4F8737E924E3055A8391F",
            "Retention production Canary receipt drifted")
    receipt = read_json(receipt_path)
    checked = []
    for key in ("orchestrator", "policy", "evaluate", "quarantine"):
        item = receipt[key]
        path = pathlib.Path(item["path"])
        require(path.is_file() and path.stat().st_size == int(item["bytes"]) and sha(path) == item["sha256"].upper(),
                f"Retention evidence drifted: {key}")
        checked.append(evidence(path))
    require(receipt["destructiveDeletes"] == 0 and receipt["automaticProductionInvocation"] is False and
            receipt["legacyJobStorePurgeInvoked"] is False, "Retention receipt safety boundary drifted")
    jobs = receipt_path.parent / "jobs"
    quarantined = pathlib.Path(receipt["quarantined"][0]["quarantineJobRoot"])
    protected = ["retention-evidence", "retention-pinned", "retention-active", "retention-waiting", "retention-unknown"]
    require(all((jobs / item).is_dir() for item in protected), "Protected isolated job was moved/deleted")
    require(quarantined.is_dir() and not (jobs / "retention-terminal-ephemeral").exists(), "Eligible task was not recoverably quarantined")

    program = (PROJECT / "orchestrator/Program.cs").read_text(encoding="utf-8")
    desktop_startup = (PROJECT / "src/Program.cs").read_text(encoding="utf-8")
    require("retention-evaluate" in program and "retention-quarantine" in program and "QuarantineEligibleAsync" in program,
            "Production retention entrypoints are missing")
    require("JobStore.PurgeExpired(" not in program and "JobStore.PurgeExpired(config, 30);" not in desktop_startup,
            "Legacy creation-time purge is production-wired")
    require("PurgeEligibleAsync" not in program, "Destructive state-aware delete is exposed by production Program")

    orchestrator = AUTHORITY_RELEASE / "orchestrator/KnowledgeClipper.Orchestrator.exe"
    valid_policy = PROJECT / "contracts/core/v2.7/retention/fixtures/retention-policy/valid/explicit-quarantine.json"
    invalid_policy = PROJECT / "contracts/core/v2.7/retention/fixtures/retention-policy/invalid/automatic-delete.json"
    unused_quarantine = PROJECT / "canary/quality-authority-v1.2.34/nonexistent-quarantine-probe"
    require(not unused_quarantine.exists(), "Read-only quarantine probe root unexpectedly exists")
    evaluated = json.loads(run([str(orchestrator), "retention-evaluate", "--jobs-root", str(jobs),
                                "--minimum-terminal-age-hours", "24"]).stdout)
    quarantined_noop = json.loads(run([str(orchestrator), "retention-quarantine", "--jobs-root", str(jobs),
                                      "--minimum-terminal-age-hours", "24", "--retention-policy-snapshot", str(valid_policy),
                                      "--quarantine-root", str(unused_quarantine)]).stdout)
    rejected = run([str(orchestrator), "retention-quarantine", "--jobs-root", str(jobs),
                    "--minimum-terminal-age-hours", "24", "--retention-policy-snapshot", str(invalid_policy),
                    "--quarantine-root", str(unused_quarantine)], expected_exit=1)
    rejected_json = json.loads(rejected.stderr)
    require(evaluated["automaticProductionInvocation"] is False and len(evaluated["evaluations"]) == 5 and
            not any(item["eligible"] for item in evaluated["evaluations"]), "Packaged evaluate did not preserve protected tasks")
    require(quarantined_noop["automaticProductionInvocation"] is False and quarantined_noop["quarantined"] == [] and
            not unused_quarantine.exists(), "Packaged explicit quarantine mutated protected tasks")
    require(rejected_json["status"] == "failed" and rejected_json["error"]["retryable"] is False,
            "Unsafe automatic policy did not fail closed")

    adapter_dll = PROJECT / "tests/CoreAdapter.IntegrationTests/bin/Release/net8.0/CoreAdapter.IntegrationTests.dll"
    adapter = json.loads(run([r"C:\Program Files\dotnet\dotnet.exe", str(adapter_dll), str(PROJECT)]).stdout)
    retention_group = next(item for item in adapter["results"] if item["name"] == "state-aware-retention")
    require(adapter["status"] == "passed" and len(adapter["results"]) == 15 and retention_group["status"] == "passed" and
            retention_group["legacyPurgeInvoked"] is False and retention_group["destructiveDeleteInvoked"] is False,
            "Fresh Core adapter retention regression failed")
    contracts = json.loads(run([sys.executable, "tools/validate_contracts.py"]).stdout)

    return {
        "ownerReceipt": evidence(receipt_path),
        "referencedEvidenceChecked": checked,
        "filesystem": {"quarantinedEligible": 1, "protectedPresent": protected, "escapeObserved": False},
        "productionEntrypoints": ["retention-evaluate", "retention-quarantine"],
        "automaticProductionInvocation": False,
        "legacyPurgeInvocations": 0,
        "destructiveDeletes": 0,
        "packagedReadOnlyEvaluate": {"status": "passed", "tasks": 5, "eligible": 0},
        "packagedExplicitQuarantineNoop": {"status": "passed", "quarantined": 0},
        "unsafeAutomaticPolicy": {"status": "rejected", "exitCode": 1},
        "freshCoreAdapter": {"status": adapter["status"], "groups": len(adapter["results"]), "retention": retention_group},
        "freshContracts": contracts,
    }


def verify_metadata_correction() -> dict[str, object]:
    correction_path = PROJECT / "canary/sealed-original-anki-v1.2.33/20260901T042100Z/metadata-correction-receipt.v1.json"
    original_path = correction_path.parent / "execution-receipt.json"
    require(sha(correction_path) == "427569000AF77F07510F339063CB87930F8C1370C2629FD969FE88190A0232DA",
            "Metadata correction receipt drifted")
    require(sha(original_path) == "6DCA852E4876B4C6342D8E7023E6DD0046926144FFF76513599B7AD2FC9A3426",
            "Original immutable execution receipt drifted")
    correction = read_json(correction_path)
    require(correction["status"] == "supersedes-metadata-only" and correction["businessEvidenceChanged"] is False and
            correction["externalWritesReplayed"] is False and correction["correction"]["correctValue"] == "1.2.33",
            "Metadata correction changed business evidence or claims a rerun")
    return {"correction": evidence(correction_path), "immutableOriginal": evidence(original_path),
            "businessEvidenceChanged": False, "externalWritesReplayed": False}


def verify_desktop_candidate() -> dict[str, object]:
    manifest_path = DESKTOP_CANDIDATE / "candidate-file-manifest.v1.json"
    owner_receipt_path = PROJECT / "receipts/desktop-integrated-speech-settings-v1.2.34-20260901T061000Z.json"
    require(sha(manifest_path) == "A55796C838A0157A71E64799A8948A748D649D708B7DFEB073C9023ABBED9918",
            "Desktop candidate manifest drifted")
    require(sha(owner_receipt_path) == "6A9529373AFBFEBFFAA613942AD0A8A9E4C4197E0F15D4AE66A39E9BF3C10EB9",
            "Desktop owner receipt drifted")
    manifest = read_json(manifest_path)
    payload_entries = manifest["payload"]["files"]
    require(len(payload_entries) == 1044, "Desktop manifest payload count drifted")
    mismatches = []
    for item in payload_entries:
        path = DESKTOP_CANDIDATE / pathlib.PurePosixPath(item["path"])
        if not path.is_file() or path.stat().st_size != int(item["bytes"]) or sha(path) != item["sha256"].upper():
            mismatches.append(item["path"])
    require(not mismatches, f"Desktop payload manifest mismatches: {mismatches[:5]}")
    payload_tree = tree(DESKTOP_CANDIDATE, {"candidate-file-manifest.v1.json"})
    sealed_tree = tree(DESKTOP_CANDIDATE)
    require(payload_tree == {"fileCount": 1044, "totalBytes": 431781954,
                             "treeSha256": "D05DAE5B277A9850BD11DA7F64FB47D18DC15652B5D1120F036283A638721203"},
            f"Desktop payload tree drifted: {payload_tree}")
    require(sealed_tree == {"fileCount": 1045, "totalBytes": 432003544,
                            "treeSha256": "928A0124342131205C76FAC25DF15243B5115FAA8758A8A97CA911C886090390"},
            f"Desktop sealed tree drifted: {sealed_tree}")
    embedded_tree = tree(DESKTOP_CANDIDATE / "authority/win-x64")
    require(embedded_tree == {"fileCount": 386, "totalBytes": 261873123,
                              "treeSha256": "F73ED8E39B5120D04EF332671CD7DF4F1131C7715ECFE1081517604C2107EFDA"},
            "Desktop embedded authority drifted")

    owner = read_json(owner_receipt_path)
    runtime_evidence = []
    for item in owner["automatedGates"].values():
        path = PROJECT / pathlib.PurePosixPath(item["path"])
        require(path.is_file() and path.stat().st_size == int(item["bytes"]) and sha(path) == item["sha256"].upper(),
                f"Desktop runtime evidence drifted: {path}")
        runtime_evidence.append(evidence(path))
    secret = read_json(PROJECT / pathlib.PurePosixPath(owner["automatedGates"]["secretScan"]["path"]))
    finding_count = secret.get("findingCount", secret.get("findings", secret.get("secretFindings")))
    require(finding_count == 0 and secret.get("secretValuesEmitted") is False,
            "Desktop candidate secret scan is nonzero or emitted a secret")
    require(owner["promotion"]["userHandTestEntrypoint"] is False and owner["promotion"]["finalUniqueEntrypoint"] is False,
            "Internal Desktop candidate was promoted by its owner receipt")
    return {
        "candidateId": manifest["candidateId"],
        "manifest": evidence(manifest_path),
        "ownerReceipt": evidence(owner_receipt_path),
        "payload": payload_tree,
        "sealed": sealed_tree,
        "manifestFilesChecked": len(payload_entries),
        "manifestMismatches": 0,
        "embeddedAuthority": embedded_tree,
        "runtimeEvidenceChecked": runtime_evidence,
        "secretFindings": 0,
        "userHandTestEntrypoint": False,
        "finalUniqueEntrypoint": False,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-root", required=True)
    args = parser.parse_args()
    output_root = PROJECT / pathlib.PurePosixPath(args.output_root)
    require(not output_root.exists(), f"Append-only output already exists: {output_root}")

    authority, _ = verify_authority()
    frozen = verify_frozen_v1()
    retention = verify_retention()
    metadata = verify_metadata_correction()
    desktop = verify_desktop_candidate()

    output_root.mkdir(parents=True)
    receipt_path = output_root / "quality-authority-retention-receipt.json"
    receipt = {
        "schemaVersion": "knowledge-clipper-quality-authority-retention.v1",
        "createdAt": dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-AUTHORITY-V1.2.34-RETENTION-EXPLICIT-QUARANTINE-DESKTOP-INTERNAL-CONDITIONAL",
        "authority": authority,
        "frozenV1": frozen,
        "retention": retention,
        "originalAnkiMetadataCorrection": metadata,
        "desktopInternalCandidate": desktop,
        "promotion": {
            "authorityAdmitted": True,
            "stateAwareRetentionProductionWiring": "PASS-EXPLICIT-QUARANTINE-AUTOMATIC-DISABLED",
            "desktopInternalCandidate": "PASS-AUTOMATED-NOT-USER-ENTRYPOINT",
            "signedProductionRelease": False,
        },
        "safety": {
            "externalServiceCalls": 0,
            "workerRuns": 0,
            "destinationWrites": 0,
            "physicalSpeakerInvocations": 0,
            "productionJobsRootUsed": False,
            "destructiveDeletes": 0,
        },
    }
    receipt_path.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": receipt["status"], "receipt": receipt_path.relative_to(PROJECT).as_posix(),
                      "sha256": sha(receipt_path)}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
