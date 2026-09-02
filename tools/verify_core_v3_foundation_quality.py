from __future__ import annotations

import argparse
import hashlib
import json
import re
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

from jsonschema import Draft202012Validator

from assemble_integrated_desktop_v1240_quality import inventory, sha256
from verify_settings_control_plane import scan_paths_for_secrets


PROJECT = Path(__file__).resolve().parents[1]
CHECKPOINT = PROJECT / "contracts/core/v3/checkpoints/20260902T054850Z-foundation/checkpoint.json"
PACKET = CHECKPOINT.parent / "typed-sdk-packet.json"
BUILD_RECEIPT = PROJECT / "core-v3/receipts/20260902T054850Z-formal-release-build.receipt.json"
TEST_RECEIPT = PROJECT / "core-v3/receipts/20260902T054850Z-formal-foundation-tests.receipt.json"
INDEPENDENT_TEST_RECEIPT = PROJECT / "canary/quality-core-v3-foundation/20260902T055257Z-independent/foundation-tests.rerun.receipt.json"
AUTHORITY_CHECKPOINT = PROJECT / "contracts/core/v2.10/checkpoints/v2.10.6-20260902T041800Z/checkpoint.json"
AUTHORITY_RELEASE = PROJECT / "releases/knowledge-clipper-authority-v1.2.41-20260902T042000Z/win-x64"
INTEGRATED_CANDIDATE = PROJECT / "releases/kc-v1241-ui-proto-q-20260902T043500Z/win-x64"

EXPECTED = {
    "checkpoint": "CE82E060BB422E8457D70B8E5750666DE200343351C315A9F11CD2AB42EDEEA2",
    "packet": "127B75F9FE8F4B29F93C908A7D093AE8A0D84B96AE0FC114B49BBFE35AF47E0C",
    "sourceTree": "B83912EE7DD11781F21A97B99713FB8BE0E708057F736B871F77B284D63754E2",
    "buildReceipt": "237A7B69CC0D5948007601A58A9757D0BB0920B98EF7D80C7371EAD6EC711E98",
    "testReceipt": "7A450B47833E061D3D6DC6FD86AA4DF82723321610C2CF98E1C7BB91C68D2C0B",
    "independentTestReceipt": "4D11C5A6847945C7C14B84A9DFDB77CACA1E236CE6A9649E9E6D05C7B7D6932F",
    "authorityCheckpoint": "20AF0975FF176B6D46249B98AC36D72C74C7A494965AF3A69DE4C7FF52235D79",
    "authorityTree": "7FB6EC31A29C7A4CB912AFA27D8A06D3B5F7205BABDBABE21DF72EA0E913A3B4",
    "candidateManifest": "F33C0C04ABBA2AA1854E47F3A9E2009A854DEA9C8F98AF2ECD46720B9F101918",
    "candidateTree": "A408DED6D1EBCE9BACC22E7BF0B9065B0842FB0F8575EE32EBD7E54F797DE9E0",
}


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def load(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def evidence(path: Path) -> dict[str, Any]:
    require(path.is_file(), f"missing evidence: {path}")
    return {"path": relative(path), "bytes": path.stat().st_size, "sha256": sha256(path)}


def resolve_ref(item: dict[str, Any], label: str) -> Path:
    raw = Path(str(item.get("path", "")))
    require(not raw.is_absolute() and ".." not in raw.parts, f"{label} is not project-relative")
    path = (PROJECT / raw).resolve()
    require(path.is_relative_to(PROJECT.resolve()) and path.is_file(), f"{label} is missing or escaped")
    require(path.stat().st_size == int(item["bytes"]), f"{label} bytes drift")
    require(sha256(path) == str(item["sha256"]).upper(), f"{label} sha256 drift")
    return path


def observe_ref(item: dict[str, Any], label: str) -> dict[str, Any]:
    raw = Path(str(item.get("path", "")))
    require(not raw.is_absolute() and ".." not in raw.parts, f"{label} is not project-relative")
    path = (PROJECT / raw).resolve()
    require(path.is_relative_to(PROJECT.resolve()), f"{label} escaped the project")
    expected = {"bytes": int(item["bytes"]), "sha256": str(item["sha256"]).upper()}
    if not path.is_file():
        return {"path": raw.as_posix(), "status": "missing", "expected": expected, "current": None}
    current = {"bytes": path.stat().st_size, "sha256": sha256(path)}
    return {
        "path": raw.as_posix(),
        "status": "unchanged" if current == expected else "drifted",
        "expected": expected,
        "current": current,
    }


def summarize_ref_observations(observations: Iterable[dict[str, Any]]) -> dict[str, int]:
    rows = list(observations)
    return {
        "total": len(rows),
        "unchanged": sum(item["status"] == "unchanged" for item in rows),
        "drifted": sum(item["status"] == "drifted" for item in rows),
        "missing": sum(item["status"] == "missing" for item in rows),
    }


def source_candidates() -> set[str]:
    paths: set[Path] = set()
    for base in (PROJECT / "core-v3/src", PROJECT / "core-v3/tests"):
        paths.update(
            path
            for path in base.rglob("*")
            if path.is_file() and not any(part in {"bin", "obj"} for part in path.relative_to(base).parts)
        )
    paths.update(path for path in (PROJECT / "core-v3/tools").rglob("*") if path.is_file())
    paths.add(PROJECT / "core-v3/README.md")
    contract_root = PROJECT / "contracts/core/v3"
    paths.update(
        path
        for path in contract_root.rglob("*")
        if path.is_file() and "checkpoints" not in path.relative_to(contract_root).parts
    )
    return {relative(path) for path in paths}


def tree_hash(entries: Iterable[dict[str, Any]], *, casefold_order: bool) -> str:
    rows = list(entries)
    key = (lambda item: item["path"].casefold()) if casefold_order else (lambda item: item["path"])
    rows.sort(key=key)
    material = "".join(f"{item['path']}\t{str(item['sha256']).upper()}\t{int(item['bytes'])}\n" for item in rows)
    return hashlib.sha256(material.encode("utf-8")).hexdigest().upper()


def verify_source_tree(checkpoint: dict[str, Any]) -> dict[str, Any]:
    declared = checkpoint.get("sourceTree") or []
    require(len(declared) == 45, "source tree file count drift")
    paths = [str(item.get("path", "")) for item in declared]
    require(len(paths) == len(set(paths)), "source tree contains duplicate paths")
    declared_casefold = tree_hash(declared, casefold_order=True)
    declared_case_sensitive = tree_hash(declared, casefold_order=False)
    require(declared_casefold == checkpoint["sourceTreeSha256"] == EXPECTED["sourceTree"], "declared source tree aggregate drift")
    require([item["path"] for item in declared] == [item["path"] for item in sorted(declared, key=lambda value: value["path"].casefold())], "source tree receipt order drift")

    current_scope = source_candidates()
    declared_scope = set(paths)
    observations = [observe_ref(item, f"sourceTree[{index}]") for index, item in enumerate(declared)]
    summary = summarize_ref_observations(observations)
    current_entries = [evidence(PROJECT / path) for path in sorted(current_scope, key=str.casefold)]
    current_casefold = tree_hash(current_entries, casefold_order=True)
    current_case_sensitive = tree_hash(current_entries, casefold_order=False)
    added = sorted(current_scope - declared_scope, key=str.casefold)
    missing_from_scope = sorted(declared_scope - current_scope, key=str.casefold)
    current_matches = summary["drifted"] == summary["missing"] == 0 and not added and not missing_from_scope
    return {
        "status": "passed-declared-checkpoint-current-source-drift" if not current_matches else "passed",
        "declaredCheckpoint": {
            "status": "passed",
            "files": len(declared),
            "bytes": sum(int(item["bytes"]) for item in declared),
            "treeSha256": declared_casefold,
            "orderingObserved": "Windows case-insensitive ordinal path order",
            "caseSensitiveOrderingTreeSha256": declared_case_sensitive,
            "portableOrderingEqual": declared_case_sensitive == declared_casefold,
        },
        "currentSourceSnapshot": {
            "status": "passed" if current_matches else "NO-GO-DRIFTED",
            "scopeFiles": len(current_scope),
            "scopeBytes": sum(int(item["bytes"]) for item in current_entries),
            "treeSha256": current_casefold,
            "caseSensitiveOrderingTreeSha256": current_case_sensitive,
            "comparison": summary,
            "addedPaths": added,
            "missingFromQualificationScope": missing_from_scope,
            "mismatches": [item for item in observations if item["status"] != "unchanged"],
        },
    }


def verify_schemas(paths: Iterable[Path]) -> dict[str, Any]:
    schemas = sorted((path for path in paths if path.name.endswith(".schema.json")), key=lambda path: relative(path).casefold())
    for path in schemas:
        Draft202012Validator.check_schema(load(path))
    catalog_path = PROJECT / "contracts/core/v3/capabilities/capability-catalog.v1.json"
    catalog_schema = load(PROJECT / "contracts/core/v3/capabilities/schemas/capability-catalog.v1.schema.json")
    catalog_errors = list(Draft202012Validator(catalog_schema).iter_errors(load(catalog_path)))
    require(not catalog_errors, f"capability catalog schema validation failed: {catalog_errors[0].message if catalog_errors else ''}")
    binding_path = PROJECT / "contracts/core/v3/capabilities/authority-bound-capability-catalog.ref.json"
    binding_schema = load(PROJECT / "contracts/core/v3/capabilities/schemas/capability-binding.v1.schema.json")
    binding_errors = list(Draft202012Validator(binding_schema).iter_errors(load(binding_path)))
    require(not binding_errors, f"capability binding schema validation failed: {binding_errors[0].message if binding_errors else ''}")
    return {"status": "passed", "schemasChecked": len(schemas), "catalog": evidence(catalog_path), "binding": evidence(binding_path)}


def verify_catalog() -> dict[str, Any]:
    catalog_path = PROJECT / "contracts/core/v3/capabilities/capability-catalog.v1.json"
    binding_path = PROJECT / "contracts/core/v3/capabilities/authority-bound-capability-catalog.ref.json"
    catalog = load(catalog_path)
    binding = load(binding_path)
    operations = catalog.get("operations") or []
    operation_ids = [item.get("operationId") for item in operations]
    require(len(operation_ids) == len(set(operation_ids)) == 6, "capability operation set drift")
    available = {item["operationId"] for item in operations if item.get("available") is True}
    unavailable = {item["operationId"] for item in operations if item.get("available") is False}
    require(available == {"content.settings.describe", "artifact.publish"}, "foundation exposed an unexpected available operation")
    require(unavailable == {"anki.probe", "question.initial", "question.follow-up", "question.make-cards"}, "foundation unavailable operation boundary drift")
    for item in operations:
        if item["operationId"] in unavailable:
            require(isinstance(item.get("availabilityReason"), str) and item["availabilityReason"], f"unavailable operation lacks reason: {item['operationId']}")
            require(item["uiAffordance"]["visible"] is False, f"unavailable operation is visible: {item['operationId']}")
    anchor = catalog["authorityAnchor"]
    require(anchor == binding["authorityAnchor"], "catalog/binding authority anchor mismatch")
    require(anchor["authorityVersion"] == "1.2.41" and anchor["checkpointSha256"] == EXPECTED["authorityCheckpoint"], "authority anchor drift")
    resolve_ref({"path": anchor["checkpointPath"], "bytes": AUTHORITY_CHECKPOINT.stat().st_size, "sha256": anchor["checkpointSha256"]}, "authorityAnchor")
    require(binding["catalogSha256"] == sha256(catalog_path), "capability binding catalog hash drift")
    return {"status": "passed", "available": sorted(available), "preparedUnavailable": sorted(unavailable), "authorityAnchor": anchor}


def verify_packet(checkpoint: dict[str, Any]) -> dict[str, Any]:
    require(sha256(PACKET) == EXPECTED["packet"], "typed SDK packet drift")
    packet = load(PACKET)
    require(packet["schema"] == "kc.core-v3-typed-sdk-packet/1" and packet["boundary"] == "foundation-only", "typed SDK packet identity drift")
    require(packet["sourceTreeSha256"] == EXPECTED["sourceTree"], "typed SDK packet source tree drift")
    refs = {
        "publicAssembly": packet["publicAssembly"],
        "contractAssembly": packet["contractAssembly"],
        "capabilityBinding": packet["capabilityBinding"],
        "capabilityCatalog": packet["capabilityCatalog"],
        "buildReceipt": packet["buildReceipt"],
        "testReceipt": packet["testReceipt"],
    }
    ref_observations = {name: observe_ref(item, f"packet.{name}") for name, item in refs.items()}
    ref_summary = summarize_ref_observations(ref_observations.values())
    public_source_observations = [observe_ref(item, f"packet.publicSources[{index}]") for index, item in enumerate(packet["publicSources"])]
    public_source_summary = summarize_ref_observations(public_source_observations)
    expected_sources = sorted(
        item["path"]
        for item in checkpoint["sourceTree"]
        if item["path"].startswith("core-v3/src/KnowledgeClipper.Core.V3.Client/")
    )
    require(sorted(item["path"] for item in packet["publicSources"]) == expected_sources, "typed SDK public source set drift")
    restrictions = set(packet["restrictions"])
    require("Anki and Question operations are prepared but unavailable in the foundation catalog." in restrictions, "packet availability restriction missing")
    require("No Desktop, worker, candidate, or authority migration is included." in restrictions, "packet migration restriction missing")
    existing_public_sources = [PROJECT / item["path"] for item in packet["publicSources"] if (PROJECT / item["path"]).is_file()]
    require(len(existing_public_sources) == len(packet["publicSources"]), "typed SDK public source is missing")
    public_text = "\n".join(path.read_text(encoding="utf-8") for path in existing_public_sources)
    forbidden = re.compile(r"public\s+[^\n]*(?:CommandEnvelope|QueryEnvelope|WireRequestEnvelope|ArtifactReference)")
    require(not forbidden.search(public_text), "low-level protocol or raw artifact reference leaked through public SDK source")
    require("internal ArtifactReference Reference" in public_text and "internal sealed class CoreV3ProtocolClient" in public_text, "SDK opacity implementation drift")
    current_sources_match = public_source_summary["drifted"] == public_source_summary["missing"] == 0
    current_refs_match = ref_summary["drifted"] == ref_summary["missing"] == 0
    return {
        "status": "passed-immutable-packet-current-references-drift" if not (current_sources_match and current_refs_match) else "passed",
        "packet": evidence(PACKET),
        "refs": {
            "status": "passed" if current_refs_match else "NO-GO-DRIFTED",
            "comparison": ref_summary,
            "observations": ref_observations,
        },
        "publicSources": {
            "status": "passed" if current_sources_match else "NO-GO-DRIFTED",
            "comparison": public_source_summary,
            "observations": public_source_observations,
        },
        "restrictions": sorted(restrictions),
    }


def verify_build_and_tests() -> dict[str, Any]:
    require(sha256(BUILD_RECEIPT) == EXPECTED["buildReceipt"], "build receipt drift")
    require(sha256(TEST_RECEIPT) == EXPECTED["testReceipt"], "test receipt drift")
    require(sha256(INDEPENDENT_TEST_RECEIPT) == EXPECTED["independentTestReceipt"], "independent test receipt drift")
    build = load(BUILD_RECEIPT)
    require(build["status"] == "PASS" and build["configuration"] == "Release" and build["exitCode"] == build["warningCount"] == build["errorCount"] == 0, "formal Release build receipt failed")
    artifact_observations = [observe_ref(item, f"build.artifacts[{index}]") for index, item in enumerate(build["artifacts"])]
    artifact_summary = summarize_ref_observations(artifact_observations)
    require(len(artifact_observations) == 5, "formal build artifact count drift")
    owner = load(TEST_RECEIPT)
    rerun = load(INDEPENDENT_TEST_RECEIPT)
    for label, receipt in (("owner", owner), ("independent", rerun)):
        tests = receipt.get("tests") or []
        require(receipt.get("status") == "PASS" and len(tests) == 29 and len({item["name"] for item in tests}) == 29, f"{label} test receipt count/status drift")
        require(all(item["status"] == "PASS" and item.get("error") is None for item in tests), f"{label} test receipt contains a failure")
        boundaries = receipt["boundaries"]
        require(all(boundaries[key] is False for key in ("externalServicesCalled", "desktopModified", "workersModified", "frozenAuthorityModified", "candidateAssembled")), f"{label} test boundary drift")
    require([item["name"] for item in owner["tests"]] == [item["name"] for item in rerun["tests"]], "independent test inventory differs from owner receipt")
    artifacts_match = artifact_summary["drifted"] == artifact_summary["missing"] == 0
    return {
        "status": "passed-historical-receipts-current-artifacts-drift" if not artifacts_match else "passed",
        "build": evidence(BUILD_RECEIPT),
        "currentArtifacts": {
            "status": "passed" if artifacts_match else "NO-GO-DRIFTED",
            "comparison": artifact_summary,
            "observations": artifact_observations,
        },
        "ownerTests": evidence(TEST_RECEIPT),
        "independentTests": evidence(INDEPENDENT_TEST_RECEIPT),
        "tests": 29,
        "warnings": 0,
        "errors": 0,
        "testToExecutedBinaryBinding": "NO-GO-MISSING-EXECUTED-BINARY-HASH" if not artifacts_match else "CURRENT-ARTIFACTS-MATCH-BUILD-RECEIPT",
    }


def verify_immutable_baselines() -> dict[str, Any]:
    require(sha256(AUTHORITY_CHECKPOINT) == EXPECTED["authorityCheckpoint"], "v1.2.41 authority checkpoint drift")
    authority = inventory(AUTHORITY_RELEASE, include_files=False)
    require((authority["fileCount"], authority["totalBytes"], authority["treeSha256"]) == (595, 268726758, EXPECTED["authorityTree"]), "v1.2.41 authority release drift")
    manifest = INTEGRATED_CANDIDATE / "candidate-file-manifest.v1.json"
    require(sha256(manifest) == EXPECTED["candidateManifest"], "v1.2.41 integrated candidate manifest drift")
    candidate = inventory(INTEGRATED_CANDIDATE, include_files=False)
    require(candidate["treeSha256"] == EXPECTED["candidateTree"], "v1.2.41 integrated candidate tree drift")
    return {"status": "passed", "authorityCheckpoint": evidence(AUTHORITY_CHECKPOINT), "authorityRelease": authority, "candidateManifest": evidence(manifest), "candidate": candidate}


def write_once(path: Path, content: str) -> None:
    require(not path.exists(), f"append-only output exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-root", required=True, type=Path)
    args = parser.parse_args()
    output_root = args.output_root.resolve()
    allowed = (PROJECT / "receipts/quality-core-v3-foundation").resolve()
    require(output_root.is_relative_to(allowed) and not output_root.exists(), "output root must be a new append-only Quality path")
    require(sha256(CHECKPOINT) == EXPECTED["checkpoint"], "checkpoint drift")
    checkpoint = load(CHECKPOINT)
    require(checkpoint["schema"] == "kc.core-v3-foundation-checkpoint/1" and checkpoint["status"] == "PASS", "checkpoint identity drift")
    require(checkpoint["authorityPromoted"] is checkpoint["candidateAssembled"] is checkpoint["externalServicesCalled"] is False, "checkpoint boundary drift")
    require(evidence(resolve_ref(checkpoint["typedSdkPacket"], "checkpoint.typedSdkPacket"))["sha256"] == EXPECTED["packet"], "checkpoint packet binding drift")
    require(evidence(resolve_ref(checkpoint["buildReceipt"], "checkpoint.buildReceipt"))["sha256"] == EXPECTED["buildReceipt"], "checkpoint build binding drift")
    require(evidence(resolve_ref(checkpoint["testReceipt"], "checkpoint.testReceipt"))["sha256"] == EXPECTED["testReceipt"], "checkpoint test binding drift")

    source_tree = verify_source_tree(checkpoint)
    source_paths = [PROJECT / item["path"] for item in checkpoint["sourceTree"]]
    schemas = verify_schemas(source_paths)
    catalog = verify_catalog()
    packet = verify_packet(checkpoint)
    execution = verify_build_and_tests()
    baselines = verify_immutable_baselines()
    secret_scan = scan_paths_for_secrets(source_paths + [CHECKPOINT, PACKET, BUILD_RECEIPT, TEST_RECEIPT, INDEPENDENT_TEST_RECEIPT])
    require(secret_scan["status"] == "passed" and secret_scan["findingCount"] == 0, "Core v3 foundation evidence secret scan failed")

    now = datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")
    source_reproducible = source_tree["currentSourceSnapshot"]["status"] == "passed" and packet["publicSources"]["status"] == "passed"
    binaries_reproducible = packet["refs"]["status"] == "passed" and execution["currentArtifacts"]["status"] == "passed"
    fully_reproducible = source_reproducible and binaries_reproducible
    receipt = {
        "schemaVersion": "knowledge-clipper-core-v3-foundation-independent-quality.v1",
        "generatedAt": now,
        "status": "CONDITIONAL-IMMUTABLE-RECEIPTS-PASS-CURRENT-SOURCE-AND-BINARY-NO-GO" if not fully_reproducible else "PASS-CORE-V3-FOUNDATION-INDEPENDENT-CONDITIONAL",
        "checkpoint": evidence(CHECKPOINT),
        "sourceTree": source_tree,
        "schemas": schemas,
        "capabilityCatalog": catalog,
        "typedSdk": packet,
        "execution": execution,
        "immutableBaselines": baselines,
        "secretScan": secret_scan,
        "decision": {
            "immutableFoundationCheckpoint": "PASS-INDEPENDENT",
            "typedSdkPacket": "PASS-INDEPENDENT",
            "currentBuiltAssemblies": "PASS" if binaries_reproducible else "NO-GO-DRIFTED",
            "currentSourceReproducibility": "PASS" if source_reproducible else "NO-GO-DRIFTED",
            "historicalIndependentTestRun": "PASS-29-OF-29-RECEIPT-EXACT-BUT-EXECUTED-BINARY-HASH-NOT-RECORDED",
            "authorityPromotion": "NOT_RUN",
            "integratedCandidate": "NOT_ASSEMBLED",
            "desktopMigration": "NOT_RUN",
            "workerMigration": "NOT_RUN",
            "ankiAndQuestion": "PREPARED-UNAVAILABLE",
            "externalServices": "ZERO-CALLS",
        },
        "diagnostics": {
            "sourceTreeOrderingPortable": source_tree["declaredCheckpoint"]["portableOrderingEqual"],
            "sourceTreeOrderingBoundary": "Current checkpoint is valid on the declared Windows ordering. A future cross-platform producer must specify and test the aggregate ordering algorithm explicitly.",
            "sourceSnapshotBoundary": "The immutable checkpoint, packet, and receipts remain hash-verifiable. Their source and build-artifact references point to mutable project paths and contain no archived snapshot; current drift blocks source/binary reproduction without invalidating the historical receipt bytes.",
        },
    }
    receipt_path = output_root / "quality-receipt.json"
    readable_path = output_root / "READABLE.md"
    write_once(receipt_path, json.dumps(receipt, ensure_ascii=False, indent=2) + "\n")
    declared_tree = source_tree["declaredCheckpoint"]
    current_tree = source_tree["currentSourceSnapshot"]
    artifact_state = execution["currentArtifacts"]
    readable = f"""# Core v3 Phase 1 foundation 独立质量门禁\n\n- 结论：**{receipt['status']}**\n- Checkpoint SHA-256：`{EXPECTED['checkpoint']}`\n- Typed SDK packet SHA-256：`{EXPECTED['packet']}`\n- Checkpoint 声明源树：{declared_tree['files']} files / {declared_tree['bytes']} bytes / `{declared_tree['treeSha256']}`，回执内部复算 PASS。\n- 当前源码快照：{current_tree['comparison']['unchanged']} unchanged / {current_tree['comparison']['drifted']} drifted / {current_tree['comparison']['missing']} missing / {len(current_tree['addedPaths'])} added，源码可复现 **NO-GO**。\n- 当前 build artifacts：{artifact_state['comparison']['unchanged']} unchanged / {artifact_state['comparison']['drifted']} drifted / {artifact_state['comparison']['missing']} missing，二进制复现 **NO-GO**。\n- 历史独立重跑：29/29 PASS；owner Release receipt 记录 0 warning / 0 error；外部服务 0。该测试 receipt 未记录 executed binary hash，不能把历史运行绑定到当前已变化 DLL。\n- 可用操作：`content.settings.describe`、`artifact.publish`。\n- 仅准备但不可用：`anki.probe`、`question.initial`、`question.follow-up`、`question.make-cards`。\n- 未执行：authority promotion、Desktop/worker migration、candidate assembly。\n- 诊断边界：checkpoint/packet/receipts 自身哈希不变，但其源码与 build artifact 引用的是可变路径，且未携带不可变快照；当前漂移阻断源码/二进制复现。\n- 排序边界：声明树按 Windows 大小写不敏感路径顺序复算一致；大小写敏感顺序会产生 `{declared_tree['caseSensitiveOrderingTreeSha256']}`，后续生成器应显式固定算法。\n"""
    write_once(readable_path, readable)
    print(json.dumps({"status": receipt["status"], "receipt": evidence(receipt_path), "readable": evidence(readable_path), "sourceTree": source_tree, "tests": execution["tests"], "authorityPromoted": False, "candidateAssembled": False}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
