from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


PROJECT = Path(__file__).resolve().parents[1]
AGGREGATE = PROJECT / "canary/core-v1216-followup/20260831T060801Z/aggregate-receipt.json"
AGGREGATE_READABLE = AGGREGATE.with_name("READABLE.md")
RELEASE = PROJECT / "releases/knowledge-clipper-authority-v1.2.16-20260830T125200Z/win-x64"
CHECKPOINT = PROJECT / "contracts/authority/worker-registry/v1.2.16-20260830T125200Z/checkpoint.json"
REGISTRY = CHECKPOINT.with_name("worker-registry.v1.json")
DEPENDENCY = CHECKPOINT.with_name("dependency-manifest.v1.json")
PORTABLE_RECEIPT = CHECKPOINT.with_name("portable-schema-closure-gate.v1.json")
STABLE_RECEIPT = PROJECT / "receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json"
SCHEMA_TOOL = PROJECT / "tools/validate_release_schema_closure.py"

EXPECTED = {
    "aggregate": "AE116A79CE322AEAD32B9FA180E9CBAA4BEDC70A02EE20A54560B510539CF719",
    "aggregateReadable": "65C9B95892C71294AEC201F87A3D0572C261F84F7DADE98C354998599ECF23F6",
    "releaseFiles": 551,
    "releaseBytes": 267713338,
    "releaseTree": "D4223A17AAF308A0A30214526B368E3411D5254831EF96EC820BE268A77B43EF",
    "registry": "42250CC251EEA1BEBE2CB4E072E19C73F7CF25417010B5C33C922755EA46E757",
    "checkpoint": "48D6E25E967F73F1D0489DF1A6BA8C8F93212B5FAD1FAD832C5C09A3D58AC046",
    "coreCheckpoint": "B6909AFC256F299E6D9B3128C58B90B99988E412D4481C47AC2671B42440E9E1",
    "dependency": "DF9EF8E4B52C09B99DDE58337B05C7E586098E7D7EE4F3226E91C65AFE137049",
    "portable": "88A17A8B3334C6715342897428DC732689E0F11F2516F7F65D16B2FDF576F470",
    "artifact": "14AAA3BA918B201280DF462677C39A373ACDF421C8DA06C1D4A7B6D619DA0E91",
    "ankiCorrection": "FC05C2F3937F1B594C8E65D92D51CBA94B1D61F3C6A89B57D71E2722FE2DD94C",
    "moduleActions": "AF411B807C299C192454D8C8B041AC799F81BE19BA8EAFC8C919BECC4918929E",
    "ocrBlocker": "7D840723707CD50C95CB7512B8FCC232C0467A44D1DF9A08FC4903E5556A224C",
}


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def io_path(path: Path) -> Path:
    absolute = path.resolve(strict=False)
    text = str(absolute)
    if text.startswith("\\\\?\\"):
        return absolute
    if len(text) >= 248 and absolute.drive:
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


def verify_evidence_ref(value: dict[str, Any], label: str) -> Path:
    path = Path(value["path"])
    if not path.is_absolute():
        path = PROJECT / path
    require(io_path(path).is_file(), f"{label}: referenced file missing: {path}")
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


def run_json(command: list[str], cwd: Path) -> tuple[dict[str, Any], str, str]:
    completed = subprocess.run(command, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60, check=False)
    require(completed.returncode == 0, f"command failed ({completed.returncode}): {command}: {completed.stderr}")
    require(completed.stdout.strip(), f"command returned no JSON: {command}")
    return json.loads(completed.stdout), completed.stdout, completed.stderr


def verify_aggregate() -> dict[str, Any]:
    require(sha256_file(AGGREGATE) == EXPECTED["aggregate"], "aggregate receipt SHA drift")
    require(sha256_file(AGGREGATE_READABLE) == EXPECTED["aggregateReadable"], "aggregate readable SHA drift")
    data = load(AGGREGATE)
    require(data["status"] == "conditional", "aggregate must remain conditional")
    require(set(data["completed"]) == {"originalToAnki", "speechCatalogPublicDispatch", "speechPreviewAuthorizationGate", "remnotePublicProbe", "releasePortableSchemaClosure"}, "completed partition drift")
    require(set(data["conditional"]) == {"remnotePublicProbe", "existingOcrHumanResume"}, "conditional partition keys drift")
    require(data["conditional"]["remnotePublicProbe"] is None, "superseded RemNote conditional was not cleared")
    require(data["newAuthorityIssued"] is False, "follow-up unexpectedly claims a new authority")
    referenced = []
    for name, item in data["authority"].items():
        if isinstance(item, dict) and "path" in item and "sha256" in item:
            referenced.append(evidence(verify_evidence_ref(item, f"authority.{name}")))
    for group in ("completed", "conditional"):
        for name, item in data[group].items():
            if isinstance(item, dict) and isinstance(item.get("evidence"), dict):
                referenced.append(evidence(verify_evidence_ref(item["evidence"], f"{group}.{name}.evidence")))
    raw_readable = AGGREGATE_READABLE.read_bytes()
    return {
        "status": "passed",
        "aggregate": evidence(AGGREGATE),
        "readable": evidence(AGGREGATE_READABLE),
        "completedPartition": sorted(data["completed"]),
        "conditionalPartition": sorted(name for name, item in data["conditional"].items() if item is not None),
        "referencedEvidenceChecked": len(referenced),
        "sourceReadableControlCharacters": raw_readable.count(b"\x08"),
    }


def verify_authority(run_binaries: bool) -> tuple[dict[str, Any], dict[str, str]]:
    require(sha256_file(CHECKPOINT) == EXPECTED["checkpoint"], "authority checkpoint SHA drift")
    require(sha256_file(REGISTRY) == EXPECTED["registry"], "registry SHA drift")
    require(sha256_file(DEPENDENCY) == EXPECTED["dependency"], "dependency manifest SHA drift")
    require(sha256_file(PORTABLE_RECEIPT) == EXPECTED["portable"], "portable gate receipt SHA drift")
    checkpoint = load(CHECKPOINT)
    require(checkpoint["admissionStatus"] == "authority-admitted", "authority not admitted")
    require(checkpoint["frozenV1"]["files"] == 21 and checkpoint["frozenV1"]["mismatches"] == 0, "checkpoint frozen v1 drift")
    require(checkpoint["registry"]["registryVersion"] == "1.2.16" and checkpoint["registry"]["workers"] == 5, "registry identity drift")
    require(checkpoint["core"]["checkpointSha256"] == EXPECTED["coreCheckpoint"], "Core checkpoint SHA drift")
    rows = inventory(RELEASE)
    require(len(rows) == EXPECTED["releaseFiles"], "authority release file count drift")
    require(sum(row["bytes"] for row in rows) == EXPECTED["releaseBytes"], "authority release bytes drift")
    require(tree_sha(rows) == EXPECTED["releaseTree"], "authority release tree drift")
    runtime: dict[str, str] = {}
    schema_result, schema_stdout, schema_stderr = run_json(["python", str(SCHEMA_TOOL), "--release-root", str(RELEASE)], PROJECT)
    require(schema_result["status"] == "passed", "portable schema closure rerun failed")
    require(schema_result["schemasChecked"] == 62 and schema_result["localRefsChecked"] == 188 and schema_result["failures"] == [], "portable schema counts drift")
    runtime["portable-schema-closure.stdout.json"] = schema_stdout
    runtime["portable-schema-closure.stderr.log"] = schema_stderr
    artifact = RELEASE / "contracts/v1/schemas/common/artifact.schema.json"
    source_artifact = PROJECT / "contracts/v1/schemas/common/artifact.schema.json"
    require(sha256_file(artifact) == EXPECTED["artifact"] == sha256_file(source_artifact), "packaged frozen artifact schema modified")
    stable = load(STABLE_RECEIPT)
    require(len(stable["files"]) == 21, "stable v1 receipt file count drift")
    mismatches = []
    for item in stable["files"]:
        path = Path(item["path"])
        if not path.is_file() or sha256_file(path) != item["sha256"].upper():
            mismatches.append(str(path))
    require(not mismatches, f"frozen v1 drift: {mismatches}")
    registry_validation: dict[str, Any] | None = None
    descriptors: list[dict[str, Any]] = []
    if run_binaries:
        orchestrator = RELEASE / "orchestrator/KnowledgeClipper.Orchestrator.exe"
        registry_validation, stdout, stderr = run_json([str(orchestrator), "validate-registry", "--registry", str(REGISTRY), "--release-root", str(RELEASE)], RELEASE)
        require(registry_validation["status"] == "valid" and registry_validation["workers"] == 5, "runtime registry validation failed")
        runtime["validate-registry.stdout.json"] = stdout
        runtime["validate-registry.stderr.log"] = stderr
        for worker in load(REGISTRY)["workers"]:
            executable = RELEASE / worker["entrypoint"]["path"]
            descriptor, _, stderr = run_json([str(executable), "describe", "--json"], RELEASE)
            require(descriptor["workerId"] == worker["workerId"], f"worker describe identity drift: {worker['workerId']}")
            descriptors.append({"workerId": descriptor["workerId"], "workerVersion": descriptor["workerVersion"], "entrypoint": worker["entrypoint"]["path"], "sha256": sha256_file(executable), "stderr": stderr.strip()})
    return {
        "status": "passed",
        "version": "1.2.16",
        "checkpoint": evidence(CHECKPOINT),
        "registry": evidence(REGISTRY),
        "dependencyManifest": evidence(DEPENDENCY),
        "release": {"root": relative(RELEASE), "files": len(rows), "bytes": sum(row["bytes"] for row in rows), "treeSha256": tree_sha(rows)},
        "portableSchemaClosure": {"status": "passed", "schemasChecked": 62, "localRefsChecked": 188, "failures": 0, "receipt": evidence(PORTABLE_RECEIPT)},
        "frozenV1": {"files": 21, "mismatches": 0, "artifactSchema": evidence(artifact), "sourceHashEqual": True},
        "registryRuntime": registry_validation,
        "workers": descriptors,
    }, runtime


def verify_anki() -> dict[str, Any]:
    correction = Path(load(AGGREGATE)["completed"]["originalToAnki"]["evidence"]["path"])
    require(sha256_file(correction) == EXPECTED["ankiCorrection"], "Anki correction SHA drift")
    data = load(correction)
    verify_evidence_ref(data["originalReceipt"], "Anki original receipt")
    operations_path = verify_evidence_ref(data["operations"]["evidence"], "Anki operations")
    operations = [json.loads(line) for line in io_path(operations_path).read_text(encoding="utf-8").splitlines() if line.strip()]
    require(len(operations) == 23 and all(item["status"] == "SUCCEEDED" for item in operations), "Anki operation ledger drift")
    mutating = Counter(item["action"] for item in operations if item["action"] in {"createModel", "storeMediaFile", "addNote"})
    require(mutating == Counter({"storeMediaFile": 3, "createModel": 1, "addNote": 1}), f"Anki mutation correction drift: {mutating}")
    require(data["authority"]["version"] == "1.2.16" and data["authority"]["releaseTreeSha256"] == EXPECTED["releaseTree"], "Anki correction authority binding drift")
    readback = data["exactReadback"]
    require(readback["noteIds"] == [1788094533713] and readback["cardIds"] == [1788094533714], "Anki exact IDs drift")
    require(readback["mediaOrderPassed"] is True and readback["replayNewAttempts"] == 0 and readback["replayNewNotes"] == 0, "Anki order/replay drift")
    return {
        "status": "passed",
        "evidence": evidence(correction),
        "authorityVersion": "1.2.16",
        "noteId": 1788094533713,
        "cardId": 1788094533714,
        "operations": len(operations),
        "mutatingActions": dict(mutating),
        "mediaOrderPassed": True,
        "replayNewAttempts": 0,
        "replayNewNotes": 0,
        "qualityBoundary": "append-only metadata correction over a prior real isolated Canary; no new external action in this quality run",
    }


def verify_module_actions() -> dict[str, Any]:
    path = Path(load(AGGREGATE)["completed"]["speechCatalogPublicDispatch"]["evidence"]["path"])
    require(sha256_file(path) == EXPECTED["moduleActions"], "module-action receipt SHA drift")
    data = load(path)
    require(data["status"] == "passed" and data["authority"]["version"] == "1.2.16", "module-action receipt partition drift")
    speech = data["speechVoices"]
    result_path = verify_evidence_ref(speech["result"], "speech result")
    snapshot_path = verify_evidence_ref(speech["catalogSnapshotFile"], "speech snapshot")
    require(sha256_file(result_path) == sha256_file(snapshot_path), "speech replay snapshot content drift")
    result = load(result_path)
    require(result["catalogRevision"] == "edge-tts-20260830T000000Z-d803ac397064", "speech catalog revision drift")
    require(result["catalogSha256"] == "d803ac397064db09081d09277a43369efe5ce1b676aa7f892ec1e62e40d3c4a2", "speech semantic hash drift")
    require(result["settingsMutationCount"] == 0 and result["physicalSpeakerPlayback"] is False, "speech catalog caused mutation/audio")
    preview = data["speechPreview"]
    require(preview["explicitAuthorization"] is False and preview["workerInvocations"] == 0 and preview["physicalSpeakerCalls"] == 0, "unauthorized preview did not fail closed")
    remnote = data["remnoteProbe"]
    remnote_path = verify_evidence_ref(remnote["result"], "RemNote probe result")
    direct = load(remnote_path)
    require(direct["state"] == "healthy" and direct["admission"] == "ready" and direct["reasonCodes"] == [] and direct["externalWriteCount"] == 0, "RemNote healthy result drift")
    require(remnote["publicDispatchStatus"] == "completed" and remnote["accepted"] is True and remnote["connected"] is True and remnote["targetMatched"] is True, "RemNote public projection drift")
    return {
        "status": "passed",
        "evidence": evidence(path),
        "speechCatalog": {"status": "passed", "revision": result["catalogRevision"], "semanticSha256": result["catalogSha256"], "replayStable": True, "settingsWrites": 0, "speakerCalls": 0},
        "speechPreviewUnauthorized": {"status": "passed-negative-gate", "workerInvocations": 0, "speakerCalls": 0},
        "remnotePublicProbe": {"status": "passed", "publicStatus": "completed", "directContractState": "healthy", "admission": "ready", "connected": True, "targetMatched": True, "reasonCodes": [], "writes": 0, "replayStable": True},
    }


def verify_ocr_blocker() -> dict[str, Any]:
    path = Path(load(AGGREGATE)["conditional"]["existingOcrHumanResume"]["evidence"]["path"])
    require(sha256_file(path) == EXPECTED["ocrBlocker"], "OCR blocker SHA drift")
    data = load(path)
    require(data["status"] == "conditional-blocked" and data["blockerCode"] == "core.precomputed_ocr_import_not_admitted", "OCR blocker classification drift")
    source_result = verify_evidence_ref(data["sourceEvidence"]["workerResult"], "OCR source worker result")
    source = load(source_result)
    output_kinds = sorted({item["kind"] for item in source["outputs"]})
    require(output_kinds == ["image-ocr.markdown", "image-ocr.structured"], f"OCR source kinds drift: {output_kinds}")
    require(data["mismatch"]["missingOutputKinds"] == ["image.processing.provenance", "image.original"], "OCR missing-output classification drift")
    execution = data["execution"]
    require(all(execution[key] == 0 for key in ("newWorkerAttempts", "mineruRerun", "mineruProviderCalls", "contentCalls", "exporterAttempts", "ankiWrites", "remnoteWrites", "speakerCalls")), "OCR blocker caused side effects")
    return {
        "status": "conditional-blocked",
        "evidence": evidence(path),
        "blockerCode": data["blockerCode"],
        "sourceWorker": "image.ocr.mineru@1.2.2",
        "admittedWorker": "image.ocr.mineru@1.2.3",
        "observedOutputKinds": output_kinds,
        "missingOutputKinds": data["mismatch"]["missingOutputKinds"],
        "precomputedImportContract": None,
        "mineruRerun": 0,
        "downstreamCalls": 0,
        "destinationWrites": 0,
    }


def render(result: dict[str, Any]) -> str:
    return "\n".join([
        "# KnowledgeClipper v1.2.16 follow-up quality acceptance",
        "",
        f"- Status: `{result['status']}`",
        "- Scope: authority/module follow-up evidence; no Desktop v1.2.16 candidate was sealed or promoted by this gate.",
        "- Quality side effects: external service calls 0, worker run 0, destination writes 0, physical speaker 0.",
        "",
        "## Completed",
        "",
        "- Portable release schema closure: 551 files / 267,713,338 bytes / tree `D4223A17...B43EF`; 62 schemas / 188 local refs / 0 failures. The packaged artifact schema exactly equals frozen v1.",
        "- Original image -> isolated Anki: authority v1.2.16; note `1788094533713`, card `1788094533714`; media order PASS; replay created 0 attempts/notes. Correct mutation count: createModel 1 + storeMediaFile 3 + addNote 1.",
        "- Speech voice catalog public IPC: PASS; revision `edge-tts-20260830T000000Z-d803ac397064`; persisted snapshot and replay stable; settings writes 0, speaker 0.",
        "- Unauthorized speech preview: fail-closed; worker 0, speaker 0.",
        "- RemNote public read-only probe: PASS through sealed Orchestrator IPC 1.2; healthy/ready, target exact, replay stable, writes 0.",
        "",
        "## Conditional",
        "",
        "- Existing CANAR OCR resume: the real 1.2.2 result only contains Markdown + structured output; it lacks same-run `image.processing.provenance` + `image.original`. No precomputed import contract exists, so no ownership/run was fabricated and MinerU/Content/exporters/writes stayed 0.",
        "",
        "## Evidence hygiene",
        "",
        "- The superseded `060026Z` aggregate and `054134Z` module-action receipt remain immutable historical bridge-disconnected evidence; current status comes only from `060801Z` / `060620Z`.",
        "- The v1.2.16 authority closure fixes the authority release packaging gap. It does not rewrite or retroactively repair the historical v1.2.15 Desktop candidate.",
    ]) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--run-binaries", action="store_true", help="Run only portable schema closure, registry validation and worker describe; never worker run.")
    parser.add_argument("--emit-root")
    args = parser.parse_args()
    aggregate = verify_aggregate()
    authority, runtime = verify_authority(args.run_binaries)
    result = {
        "schemaVersion": "knowledge-clipper-quality-v1216-followup.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-SEGMENTED-AUTHORITY-V1.2.16-FOLLOWUP-CONDITIONAL",
        "authorityBound": True,
        "desktopCandidateBound": False,
        "checks": {
            "aggregate": aggregate,
            "authority": authority,
            "originalToAnki": verify_anki(),
            "moduleActions": verify_module_actions(),
            "existingOcrHumanResume": verify_ocr_blocker(),
        },
        "completed": ["releasePortableSchemaClosure", "originalToAnki", "speechCatalogPublicDispatch", "speechPreviewAuthorizationGate", "remnotePublicProbe"],
        "conditional": ["existingOcrHumanResume"],
        "productGapUpdate": {
            "portableRecursiveSchemaValidation": "CLOSED-AT-AUTHORITY-V1.2.16; historical v1.2.15 Desktop candidate unchanged",
            "originalToAnki": "PASS-SEALED-ISOLATED-V1.2.16",
            "speechVoiceCatalogPublicDispatch": "PASS-SEALED-IPC-V1.2.16",
            "speechPreview": "PASS-UNAUTHORIZED-NEGATIVE-GATE; physical explicit preview not run in this batch",
            "remnotePublicProbe": "PASS-SEALED-IPC-V1.2.16-READONLY-WRITES-0",
            "ocrHumanResumeFromExistingCanar": "CONDITIONAL-PRECOMPUTED-IMPORT-NOT-ADMITTED",
        },
        "promotion": {"authorityFollowup": "PASS-SEGMENTED", "desktopCandidate": "NOT-ISSUED-IN-THIS-FOLLOWUP", "release": "NO-GO-CONDITIONAL"},
        "qualitySideEffects": {"externalServices": 0, "workerRunCommands": 0, "mineruCalls": 0, "deepSeekCalls": 0, "ankiWrites": 0, "remnoteWrites": 0, "speakerCalls": 0},
    }
    artifacts: dict[str, Any] = {}
    if args.emit_root:
        root = Path(args.emit_root).resolve()
        require(root.is_relative_to(PROJECT.resolve()), "output root escaped project")
        require(not root.exists(), "append-only output root already exists")
        root.mkdir(parents=True)
        runtime_evidence = {}
        for name, content in runtime.items():
            path = root / name
            path.write_text(content if not content or content.endswith("\n") else content + "\n", encoding="utf-8")
            runtime_evidence[name] = evidence(path)
        result["runtimeEvidence"] = runtime_evidence
        receipt = root / "quality-followup-receipt.json"
        readable = root / "READABLE.md"
        receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        readable.write_text(render(result), encoding="utf-8")
        artifacts = {"root": relative(root), "receipt": evidence(receipt), "readable": evidence(readable)}
    print(json.dumps({"result": result, "artifacts": artifacts}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
