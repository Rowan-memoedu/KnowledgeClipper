from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator, FormatChecker, RefResolver

from verify_settings_control_plane import scan_runtime_tree_for_secrets


PROJECT = Path(__file__).resolve().parents[1]
DEFAULT_CANDIDATE = PROJECT / "releases/kc-i-ocr-v1215-20260830T121500Z/win-x64"
DEFAULT_RECEIPT = PROJECT / "receipts/desktop-ocr-human-resolution-integrated-v1.2.15-20260830T121900Z.json"
AUTHORITY_SOURCE = PROJECT / "releases/knowledge-clipper-authority-v1.2.15-20260830T121200Z/win-x64"
COMMON_ARTIFACT_SCHEMA = PROJECT / "contracts/v1/schemas/common/artifact.schema.json"

EXPECTED = {
    "candidateFiles": 1105,
    "candidateBytes": 437389476,
    "candidateTree": "46CB26033A40FF3A15875EF155CB16D53F0BF092E2577E40B43AA176F6B92EFA",
    "manifest": "D3843FDED77D58AC3376F25398C312203E3B3812B749BEABAD2744817A81D098",
    "entrypoint": "29320D1CCAA252057D011490866CC7533901740C5C6921359B7A2F9F58C4720C",
    "desktopDll": "711575C645629019DAD182F02988378996C57F9B7109630CE50B175716DE3623",
    "desktopReceipt": "C08B82882305A902D3ED4347F03804591B12A6A5F95BD1D8BCBED9C025A03168",
    "authorityFiles": 538,
    "authorityBytes": 267590016,
    "authorityTree": "E5067C1B93A47B5650A2B1A049F974A73F66E31C3A5CEFC5DBD4D33B623190D4",
    "authorityCheckpoint": "B5F1EF63EF812D86C360D0AFB46ECDFB220C96C24CD6CF2252E18411D82084A1",
    "authorityRegistry": "0EA0131DDA8A13667FBD9F758E05C27C8C8ACA7ED982939CCBE6E042D8A35830",
    "coreCheckpoint": "F913D27758E0AD20859B7C0C2B5B8756B0D8487E346FE0FD9732D3D480C34D65",
}


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def project_path(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def evidence(path: Path) -> dict[str, Any]:
    return {"path": project_path(path), "bytes": path.stat().st_size, "sha256": sha256_file(path)}


def inventory(root: Path, *, exclude: set[str] | None = None) -> list[dict[str, Any]]:
    excluded = exclude or set()
    rows: list[dict[str, Any]] = []
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(root).as_posix()
        if relative in excluded:
            continue
        rows.append({"path": relative, "bytes": path.stat().st_size, "sha256": sha256_file(path)})
    return sorted(rows, key=lambda item: item["path"])


def tab_tree(rows: list[dict[str, Any]]) -> str:
    material = "\n".join(f"{row['path']}\t{row['bytes']}\t{row['sha256'].lower()}" for row in rows)
    return hashlib.sha256(material.encode("utf-8")).hexdigest().upper()


def desktop_tree(rows: list[dict[str, Any]]) -> str:
    material = "".join(f"{row['path']}\n{row['sha256'].lower()}\n{row['bytes']}\n" for row in rows)
    return hashlib.sha256(material.encode("utf-8")).hexdigest().upper()


def assert_manifest(candidate: Path) -> dict[str, Any]:
    manifest_path = candidate / "candidate-file-manifest.v1.json"
    require(sha256_file(manifest_path) == EXPECTED["manifest"], "candidate manifest SHA drift")
    manifest = load(manifest_path)
    declared = manifest["payload"]["files"]
    observed = inventory(candidate, exclude={manifest_path.name})
    require(len(declared) == len(observed), "manifest file count drift")
    for expected, actual in zip(declared, observed, strict=True):
        require(expected["path"] == actual["path"], f"manifest path drift: {actual['path']}")
        require(int(expected["bytes"]) == actual["bytes"], f"manifest bytes drift: {actual['path']}")
        require(str(expected["sha256"]).upper() == actual["sha256"], f"manifest SHA drift: {actual['path']}")
    require(sum(row["bytes"] for row in observed) == int(manifest["payload"]["totalBytes"]), "manifest total bytes drift")
    require(tab_tree(observed) == str(manifest["payload"]["treeSha256"]).upper(), "manifest payload tree drift")
    sealed = inventory(candidate)
    require(len(sealed) == EXPECTED["candidateFiles"], "sealed file count drift")
    require(sum(row["bytes"] for row in sealed) == EXPECTED["candidateBytes"], "sealed total bytes drift")
    require(tab_tree(sealed) == EXPECTED["candidateTree"], "sealed tree drift")
    return {
        "status": "passed",
        "manifest": evidence(manifest_path),
        "payload": {"files": len(observed), "bytes": sum(row["bytes"] for row in observed), "treeSha256": tab_tree(observed)},
        "sealed": {"files": len(sealed), "bytes": sum(row["bytes"] for row in sealed), "treeSha256": tab_tree(sealed)},
    }


def assert_desktop_build(candidate: Path) -> dict[str, Any]:
    receipt_path = candidate / "desktop-build-receipt.v1.json"
    receipt = load(receipt_path)
    rows = inventory(candidate, exclude={"candidate-file-manifest.v1.json", "candidate-integration.json", "README-候选说明.txt", "desktop-build-receipt.v1.json"})
    rows = [row for row in rows if not row["path"].startswith("authority/")]
    declared = receipt["files"]
    require(len(declared) == len(rows) == int(receipt["fileCount"]), "desktop build file count drift")
    for expected, actual in zip(declared, rows, strict=True):
        require(expected["path"] == actual["path"], f"desktop build path drift: {actual['path']}")
        require(int(expected["bytes"]) == actual["bytes"], f"desktop build bytes drift: {actual['path']}")
        require(str(expected["sha256"]).upper() == actual["sha256"], f"desktop build SHA drift: {actual['path']}")
    require(sum(row["bytes"] for row in rows) == int(receipt["totalBytes"]), "desktop build total bytes drift")
    require(desktop_tree(rows) == str(receipt["treeSha256"]).upper(), "desktop build tree drift")
    return {"status": "passed", "receipt": evidence(receipt_path), "files": len(rows), "bytes": sum(row["bytes"] for row in rows), "treeSha256": desktop_tree(rows)}


def assert_authority(candidate: Path) -> dict[str, Any]:
    embedded = candidate / "authority/win-x64"
    source_rows = inventory(AUTHORITY_SOURCE)
    embedded_rows = inventory(embedded)
    require(source_rows == embedded_rows, "embedded authority differs from admitted release")
    require(len(embedded_rows) == EXPECTED["authorityFiles"], "authority file count drift")
    require(sum(row["bytes"] for row in embedded_rows) == EXPECTED["authorityBytes"], "authority total bytes drift")
    require(tab_tree(embedded_rows) == EXPECTED["authorityTree"], "authority tree drift")
    checkpoint_path = PROJECT / "contracts/authority/worker-registry/v1.2.15-20260830T121200Z/checkpoint.json"
    registry_source = PROJECT / "contracts/authority/worker-registry/v1.2.15-20260830T121200Z/worker-registry.v1.json"
    core_checkpoint = PROJECT / "contracts/core/v2.6/checkpoints/v2.6.0-20260830T120900Z/checkpoint.json"
    require(sha256_file(checkpoint_path) == EXPECTED["authorityCheckpoint"], "authority checkpoint SHA drift")
    require(sha256_file(registry_source) == EXPECTED["authorityRegistry"], "authority registry SHA drift")
    require(sha256_file(core_checkpoint) == EXPECTED["coreCheckpoint"], "Core checkpoint SHA drift")
    embedded_registry = candidate / "authority/metadata/worker-registry.v1.json"
    require(sha256_file(embedded_registry) == EXPECTED["authorityRegistry"], "embedded registry differs from authority")
    config = load(candidate / "candidate-integration.json")
    require(config["ReleaseRoot"] == "authority/win-x64", "candidate release root is not embedded authority")
    require(config["Registry"] == "authority/metadata/worker-registry.v1.json", "candidate registry path drift")
    require(config["OrchestratorExecutable"] == "authority/win-x64/orchestrator/KnowledgeClipper.Orchestrator.exe", "candidate Orchestrator path drift")
    require(config["AuthorityTreeSha256"].upper() == EXPECTED["authorityTree"], "candidate authority tree binding drift")
    runtime_core = embedded / "orchestrator/KnowledgeClipper.Orchestrator.dll"
    return {
        "status": "passed",
        "version": "1.2.15",
        "files": len(embedded_rows),
        "bytes": sum(row["bytes"] for row in embedded_rows),
        "treeSha256": tab_tree(embedded_rows),
        "checkpoint": evidence(checkpoint_path),
        "registry": evidence(registry_source),
        "coreCheckpoint": evidence(core_checkpoint),
        "runtimeCoreDll": evidence(runtime_core),
        "integrationConfig": evidence(candidate / "candidate-integration.json"),
    }


def assert_desktop_receipt(receipt_path: Path) -> dict[str, Any]:
    require(sha256_file(receipt_path) == EXPECTED["desktopReceipt"], "Desktop final receipt SHA drift")
    receipt = load(receipt_path)
    checked: list[dict[str, Any]] = []

    def walk(value: Any) -> None:
        if isinstance(value, dict):
            if isinstance(value.get("path"), str) and isinstance(value.get("sha256"), str):
                path = PROJECT / value["path"]
                require(path.is_file(), f"referenced evidence missing: {value['path']}")
                require(sha256_file(path) == value["sha256"].upper(), f"referenced evidence SHA drift: {value['path']}")
                if "bytes" in value:
                    require(path.stat().st_size == int(value["bytes"]), f"referenced evidence bytes drift: {value['path']}")
                checked.append(evidence(path))
            for child in value.values():
                walk(child)
        elif isinstance(value, list):
            for child in value:
                walk(child)

    walk(receipt)
    require(receipt["userHandTestEntrypoint"] is True, "Desktop did not mark hand-test entrypoint")
    require(receipt["qualified"]["ocrRerun"] is False, "OCR human action would rerun OCR")
    require(receipt["qualified"]["ankiOriginalMedia"] == "ordered-original-images.v1 supported", "Anki original media capability missing")
    require(receipt["qualified"]["remnoteOriginalMedia"] == "unsupported:mcp.media_write_unavailable", "RemNote media boundary drift")
    return {"status": "passed", "receipt": evidence(receipt_path), "referencedEvidenceChecked": len(checked)}


def schema_fixture_gate(candidate: Path) -> dict[str, Any]:
    core = candidate / "authority/win-x64/contracts/core/v2.6"
    desktop = candidate / "contracts/desktop-automation/v1.3"
    sets = [
        (core / "ocr-human-resolution/schemas/ocr-human-resolution-action.v1.schema.json", core / "ocr-human-resolution/fixtures/action"),
        (core / "ocr-human-resolution/schemas/task-query.v2.3.schema.json", core / "ocr-human-resolution/fixtures/task-query"),
        (core / "ocr-human-resolution/schemas/task-query-result.v2.3.schema.json", core / "ocr-human-resolution/fixtures/task-query-result"),
        (core / "original-media/schemas/media-bundle.v1.schema.json", core / "original-media/fixtures/media-bundle"),
        (core / "original-media/schemas/ordered-original-images-adapter.v1.schema.json", core / "original-media/fixtures/ordered-original-images-adapter"),
        (desktop / "semantic-action.schema.json", desktop / "fixtures/semantic-actions"),
    ]
    valid = 0
    invalid = 0
    external_ref_substitution = 0
    for schema_path, fixture_root in sets:
        schema = load(schema_path)
        store: dict[str, Any] = {}
        missing_common = (schema_path.parent / "../../../../v1/schemas/common/artifact.schema.json").resolve()
        if "$ref" in schema_path.read_text(encoding="utf-8") and not missing_common.exists():
            require(COMMON_ARTIFACT_SCHEMA.is_file(), "stable common artifact schema missing from source tree")
            store[missing_common.as_uri()] = load(COMMON_ARTIFACT_SCHEMA)
            external_ref_substitution += 1
        resolver = RefResolver(base_uri=schema_path.as_uri(), referrer=schema, store=store)
        validator = Draft202012Validator(schema, resolver=resolver, format_checker=FormatChecker())
        for fixture in sorted((fixture_root / "valid").glob("*.json")):
            errors = list(validator.iter_errors(load(fixture)))
            require(not errors, f"valid fixture rejected: {fixture}: {errors[0].message if errors else ''}")
            valid += 1
        for fixture in sorted((fixture_root / "invalid").glob("*.json")):
            require(bool(list(validator.iter_errors(load(fixture)))), f"invalid fixture accepted: {fixture}")
            invalid += 1
    missing_ref = candidate / "authority/win-x64/contracts/v1/schemas/common/artifact.schema.json"
    require(not missing_ref.exists(), "expected portability observation changed; review candidate contract packaging")
    return {
        "status": "passed-with-portability-conditional",
        "validAccepted": valid,
        "invalidRejected": invalid,
        "sourceStableCommonSchemaSubstitutions": external_ref_substitution,
        "candidateSelfContainedCommonSchema": False,
        "missingCandidatePath": missing_ref.relative_to(candidate).as_posix(),
        "boundary": "runtime/UI hand-test unaffected; portable recursive schema validation requires source-tree stable common schema",
    }


def automation_gate(candidate: Path) -> dict[str, Any]:
    contract_path = candidate / "contracts/desktop-automation/v1.3/desktop-automation.contract.json"
    contract = load(contract_path)
    actions = set(contract["semanticActions"])
    expected_actions = {
        "ocr-human-resolution.accept-original",
        "ocr-human-resolution.submit-correction",
        "ocr-human-resolution.cancel",
    }
    require(expected_actions <= actions, "OCR semantic actions missing")
    ids = {item["automationId"] for item in contract["automationIds"]}
    expected_ids = {
        "knowledge-clipper.desktop.ocr-resolution.window",
        "knowledge-clipper.desktop.ocr-resolution.original-text",
        "knowledge-clipper.desktop.ocr-resolution.ai-candidate",
        "knowledge-clipper.desktop.ocr-resolution.markdown",
        "knowledge-clipper.desktop.ocr-resolution.structured",
        "knowledge-clipper.desktop.ocr-resolution.difference",
        "knowledge-clipper.desktop.ocr-resolution.unresolved-reason",
        "knowledge-clipper.desktop.ocr-resolution.edit-text",
        "knowledge-clipper.desktop.ocr-resolution.validation",
        "knowledge-clipper.desktop.ocr-resolution.use-original",
        "knowledge-clipper.desktop.ocr-resolution.use-edited",
        "knowledge-clipper.desktop.ocr-resolution.cancel",
    }
    require(expected_ids <= ids, "OCR attention automation IDs missing")
    require("task-query v2.3" in contract["projectionPolicy"] and "waiting_input" in contract["projectionPolicy"], "OCR attention query contract drift")
    require("task.provide_input" in contract["submissionPolicy"], "OCR commands bypass public task input")
    require("never calls Content, Image/OCR or exporters directly" in contract["submissionPolicy"], "Desktop can bypass Core")
    return {"status": "passed", "contract": evidence(contract_path), "actions": sorted(expected_actions), "automationIds": len(expected_ids)}


def product_capability_gate(candidate: Path) -> dict[str, Any]:
    fixture_path = candidate / "authority/win-x64/contracts/core/v2.6/original-media/fixtures/ordered-original-images-adapter/valid/anki-with-remnote-conditional.json"
    fixture = load(fixture_path)
    require(fixture["sourceMode"] == "original" and fixture["ocrInvoked"] is False, "original route invokes OCR")
    require(fixture["projection"] == "ordered-original-images.v1", "Anki original-media projection drift")
    require(fixture["ordering"] == "questionImages-then-answerImages", "original image order drift")
    unsupported = fixture["unsupportedDestinations"]
    require(unsupported == [{"destination": "remnote", "code": "mcp.media_write_unavailable", "workerInvocationAllowed": False}], "RemNote media boundary drift")
    return {"status": "passed", "fixture": evidence(fixture_path), "anki": "capability-present", "remnote": "unsupported:mcp.media_write_unavailable"}


def safety_gate(candidate: Path) -> dict[str, Any]:
    scan = scan_runtime_tree_for_secrets(candidate)
    require(scan["status"] == "passed" and scan["findingCount"] == 0, "candidate secret scan failed")
    forbidden = [path.relative_to(candidate).as_posix() for path in candidate.rglob("*") if path.is_file() and path.name.casefold() in {"smoketest.exe", "ttscanary.exe"}]
    require(not forbidden, f"legacy external canaries packaged: {forbidden}")
    credentials = [path.relative_to(candidate).as_posix() for path in candidate.rglob("*") if path.is_file() and path.name.casefold() in {"credentials.json", "credentials.json.bak", ".env"}]
    require(not credentials, f"candidate carries user credential files: {credentials}")
    return {"status": "passed", "secretScan": scan, "credentialFiles": credentials, "legacyExternalCanaries": forbidden}


def ui_and_handtest_gate(receipt_path: Path) -> dict[str, Any]:
    receipt = load(receipt_path)
    ui_path = PROJECT / receipt["desktop"]["windowsUi"]["path"]
    ui = load(ui_path)
    automated = ui["automatedWindowsUiCanary"]
    require(automated["status"] == "PASS" and int(automated["assertions"]) == 173, "Windows UI automated evidence drift")
    require(ui["realWindowsCanaries"]["crossMonitorDpiDrag"]["status"] == "PENDING", "mixed-DPI boundary not preserved")
    checklist_path = PROJECT / receipt["desktop"]["userHandTestChecklist"]["path"]
    checklist = checklist_path.read_text(encoding="utf-8")
    for phrase in ["KnowledgeClipper.exe", "OCR", "使用原文继续", "编辑后继续", "取消", "ordered-original-images.v1", "mcp.media_write_unavailable", "DPI"]:
        require(phrase in checklist, f"hand-test checklist missing: {phrase}")
    return {"status": "passed", "windowsUi": evidence(ui_path), "assertions": 173, "handTestChecklist": evidence(checklist_path), "physicalBoundariesPreserved": True}


def run_json(command: list[str], cwd: Path) -> tuple[dict[str, Any], str, str]:
    completed = subprocess.run(command, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45, check=False)
    require(completed.returncode == 0, f"command failed ({completed.returncode}): {command}: {completed.stderr}")
    output = completed.stdout.strip()
    require(output, f"command produced no JSON: {command}")
    return json.loads(output), completed.stdout, completed.stderr


def runtime_gate(candidate: Path) -> tuple[dict[str, Any], dict[str, str]]:
    startup, startup_stdout, startup_stderr = run_json([str(candidate / "KnowledgeClipper.exe"), "--startup-diagnostic"], candidate)
    require(startup["result"] == "PASS", "startup diagnostic failed")
    require(startup["externalConnections"] == 0 and startup["namedPipeInvoked"] is False, "startup performed integration/external work")
    authority = candidate / "authority/win-x64"
    registry_path = candidate / "authority/metadata/worker-registry.v1.json"
    registry, registry_stdout, registry_stderr = run_json([
        str(authority / "orchestrator/KnowledgeClipper.Orchestrator.exe"), "validate-registry",
        "--registry", str(registry_path), "--release-root", str(authority),
    ], authority)
    require(registry["status"] == "valid" and int(registry["workers"]) == 5, "runtime registry validation failed")
    descriptors: list[dict[str, Any]] = []
    for worker in load(registry_path)["workers"]:
        executable = authority / worker["entrypoint"]["path"]
        descriptor, _, stderr = run_json([str(executable), "describe", "--json"], authority)
        require(descriptor["workerId"] == worker["workerId"], f"worker identity drift: {worker['workerId']}")
        descriptors.append({"workerId": descriptor["workerId"], "workerVersion": descriptor["workerVersion"], "entrypoint": worker["entrypoint"]["path"], "sha256": sha256_file(executable), "stderr": stderr.strip()})
    return {
        "status": "passed",
        "startup": startup,
        "registry": registry,
        "workers": descriptors,
        "externalCalls": 0,
        "workerRunCommands": 0,
    }, {
        "startup.stdout.json": startup_stdout,
        "startup.stderr.log": startup_stderr,
        "registry.stdout.json": registry_stdout,
        "registry.stderr.log": registry_stderr,
    }


def evaluate(candidate: Path, receipt_path: Path, run_binaries: bool) -> tuple[dict[str, Any], dict[str, str]]:
    require(candidate.is_dir(), "candidate root missing")
    require(receipt_path.is_file(), "Desktop final receipt missing")
    require(sha256_file(candidate / "KnowledgeClipper.exe") == EXPECTED["entrypoint"], "entrypoint SHA drift")
    require(sha256_file(candidate / "KnowledgeClipper.dll") == EXPECTED["desktopDll"], "Desktop DLL SHA drift")
    checks: dict[str, Any] = {
        "manifestAndTree": assert_manifest(candidate),
        "desktopBuild": assert_desktop_build(candidate),
        "authorityAndCoreBinding": assert_authority(candidate),
        "desktopReceiptRecursion": assert_desktop_receipt(receipt_path),
        "schemaFixtures": schema_fixture_gate(candidate),
        "ocrAttentionAutomation": automation_gate(candidate),
        "originalMediaDestinations": product_capability_gate(candidate),
        "candidateSafety": safety_gate(candidate),
        "uiAndHandtest": ui_and_handtest_gate(receipt_path),
    }
    runtime_artifacts: dict[str, str] = {}
    if run_binaries:
        checks["safeRuntime"] , runtime_artifacts = runtime_gate(candidate)
    status = "PASS-CANDIDATE-BOUND-USER-HANDTEST-CONDITIONAL-RELEASE"
    result = {
        "schemaVersion": "knowledge-clipper-quality-desktop-v1215-candidate-bound.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": status,
        "candidateBound": True,
        "candidate": {"root": project_path(candidate), "entrypoint": evidence(candidate / "KnowledgeClipper.exe"), "manifest": evidence(candidate / "candidate-file-manifest.v1.json"), "sealedTreeSha256": EXPECTED["candidateTree"]},
        "userHandTestEntrypoint": True,
        "checks": checks,
        "productGaps": {
            "ocrHumanContinuation": "candidate-bound-automated-pass; real external OCR-to-human-to-destination chain owned by Core qualification",
            "originalToAnki": "candidate-bound-capability-pass; real destination chain not run by this audit",
            "originalToRemNote": "unsupported:mcp.media_write_unavailable",
            "portableRecursiveSchemaValidation": "conditional:candidate omits contracts/v1 referenced by Core task-query-result schema",
            "speechVoiceCatalogAndPreviewCoreAction": "conditional-not-wired",
            "remnoteReadonlyProbeCoreDispatch": "conditional-module-cli-admitted",
            "stateAwareRetention": "production automatic invocation disabled",
            "desktopPhysicalContextAndMixedDpi": "user-handtest-boundary",
        },
        "promotion": {
            "userHandTest": "GO",
            "externalE2E": "NOT_RUN-IN-THIS-AUDIT",
            "release": "NO-GO-CONDITIONAL",
        },
        "sideEffects": {"externalServices": 0, "workerRuns": 0, "remnoteWrites": 0, "ankiWrites": 0, "mineruCalls": 0, "speakerInvocations": 0},
    }
    return result, runtime_artifacts


def render(result: dict[str, Any]) -> str:
    return "\n".join([
        "# KnowledgeClipper v1.2.15 Desktop candidate-bound quality gate",
        "",
        f"- Status: `{result['status']}`",
        f"- Candidate: `{result['candidate']['root']}`",
        f"- Sealed tree: `{result['candidate']['sealedTreeSha256']}`",
        "- User hand-test: `GO`; preserve the complete directory and launch `KnowledgeClipper.exe`.",
        "- External chain: `NOT_RUN-IN-THIS-AUDIT`; no RemNote/Anki/MinerU/DeepSeek/speaker action was invoked.",
        "",
        "## Verified input -> visible behavior",
        "",
        "- Core `waiting_input + attentionRequired + ocr-human-resolution` -> Desktop attention window -> original/candidate/Markdown/structured/diff/reason -> accept original / submit correction / cancel through public `task.provide_input`.",
        "- Original images -> `ordered-original-images.v1` -> Anki capability present, OCR calls = 0.",
        "- Original images -> RemNote -> explicit `unsupported:mcp.media_write_unavailable`; not reported as succeeded.",
        "",
        "## Real remaining boundaries",
        "",
        "- Core-owned real OCR -> human decision -> destination continuation is not part of this Desktop audit.",
        "- Physical external-app focus/selection, real image clipboard, drag screenshot and mixed-DPI cross-screen behavior remain on the hand-test checklist.",
        "- The candidate does not carry the frozen common `contracts/v1` file referenced by the Core task-query-result schema; runtime is unaffected, but portable recursive schema validation remains conditional for release promotion.",
        "- Speech catalog/explicit preview Core action, RemNote public probe dispatch and final state-aware retention integration remain conditional as listed in the authority product-gap matrix.",
    ]) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", default=str(DEFAULT_CANDIDATE))
    parser.add_argument("--desktop-receipt", default=str(DEFAULT_RECEIPT))
    parser.add_argument("--run-binaries", action="store_true")
    parser.add_argument("--emit-root")
    args = parser.parse_args()
    candidate = Path(args.candidate).resolve()
    receipt_path = Path(args.desktop_receipt).resolve()
    result, runtime_artifacts = evaluate(candidate, receipt_path, args.run_binaries)
    artifacts: dict[str, Any] = {}
    if args.emit_root:
        root = Path(args.emit_root).resolve()
        require(root.is_relative_to(PROJECT.resolve()), "output root escaped project")
        require(not root.exists(), "append-only output root exists")
        root.mkdir(parents=True)
        runtime_evidence: dict[str, Any] = {}
        for name, content in runtime_artifacts.items():
            path = root / name
            path.write_text(content if content.endswith("\n") or not content else content + "\n", encoding="utf-8")
            runtime_evidence[name] = evidence(path)
        result["runtimeEvidence"] = runtime_evidence
        receipt = root / "quality-candidate-receipt.json"
        readable = root / "READABLE.md"
        receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        readable.write_text(render(result), encoding="utf-8")
        artifacts = {"root": project_path(root), "receipt": evidence(receipt), "readable": evidence(readable)}
    print(json.dumps({"result": result, "artifacts": artifacts}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
