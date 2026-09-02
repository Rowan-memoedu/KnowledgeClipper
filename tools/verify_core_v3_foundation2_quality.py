from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

from assemble_integrated_desktop_v1240_quality import sha256
from verify_settings_control_plane import scan_paths_for_secrets


PROJECT = Path(__file__).resolve().parents[1]
RELEASE = PROJECT / "core-v3/releases/20260902T060054Z-foundation.2"
CHECKPOINT = RELEASE / "checkpoint.json"
PACKET = RELEASE / "typed-sdk-packet.json"
INVENTORY = RELEASE / "inventory.json"
EXTERNAL_REF = PROJECT / "contracts/core/v3/checkpoints/20260902T060054Z-foundation.2.ref.json"
AUTHORITY_ANCHOR = PROJECT / "contracts/core/v2.10/checkpoints/v2.10.6-20260902T041800Z/checkpoint.json"
ANKI_FIXTURE = PROJECT / "canary/quality-integrated-v1241/20260902T045200Z-entrypoint-resolver-live-readonly-stateful-v2/isolated-real-store/localappdata/KnowledgeClipper/jobs/module-actions/settings-anki-probe-53b61a21382046a7bdaf3db1dce82f1e/package"
HISTORICAL_FIXTURE = PROJECT / "canary/backend-full-pipeline/20260826T061500Z/jobs/backend-remnote-20260826t061500z"

EXPECTED = {
    "checkpoint": "D789C855FE1F3264939E82A5661172523FFAD479218F927A8C2B44F7AD872F7A",
    "packet": "88076433AF2E4BC2C2A3FE3A5F96E22CD1461A71DAA7A58B0081D7E914A6A425",
    "inventory": "DAD749354BD9BF82D96FFCCAE6CADAB32A30424C0188D5AE2D26DE63C8BD3F55",
    "inventoryTree": "FC9957F17E7BD7795007AA5F904D5E00770988075ADD672ED3B00D69DE6B6ABE",
    "snapshotTree": "CBB61BB80E958341CC402617138FBD179D826427F76812C7996F30B8832D8519",
    "externalRef": "16C7033F07D8333B685AD181D48EB71D34D32E38E7AC8ABFC99AFC6E8BA7AF29",
    "authorityAnchor": "20AF0975FF176B6D46249B98AC36D72C74C7A494965AF3A69DE4C7FF52235D79",
    "predecessor": "CE82E060BB422E8457D70B8E5750666DE200343351C315A9F11CD2AB42EDEEA2",
}


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def load(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def project_relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def release_relative(path: Path) -> str:
    return path.resolve().relative_to(RELEASE.resolve()).as_posix()


def evidence(path: Path) -> dict[str, Any]:
    require(path.is_file(), f"missing evidence: {path}")
    return {"path": project_relative(path), "bytes": path.stat().st_size, "sha256": sha256(path)}


def tree(files: Iterable[Path], root: Path) -> dict[str, Any]:
    entries = [
        {"path": path.resolve().relative_to(root.resolve()).as_posix(), "bytes": path.stat().st_size, "sha256": sha256(path)}
        for path in files
    ]
    entries.sort(key=lambda item: item["path"])
    material = "".join(f"{item['path']}\t{item['sha256']}\t{item['bytes']}\n" for item in entries)
    return {
        "files": len(entries),
        "bytes": sum(item["bytes"] for item in entries),
        "treeSha256": hashlib.sha256(material.encode("utf-8")).hexdigest().upper(),
        "entries": entries,
    }


def resolve_release_ref(item: dict[str, Any], label: str) -> Path:
    raw = Path(str(item.get("path", "")))
    require(not raw.is_absolute() and ".." not in raw.parts, f"{label} is not release-relative")
    path = (RELEASE / raw).resolve()
    require(path.is_relative_to(RELEASE.resolve()) and path.is_file(), f"{label} missing or escaped")
    require(path.stat().st_size == int(item["bytes"]), f"{label} bytes drift")
    require(sha256(path) == str(item["sha256"]).upper(), f"{label} hash drift")
    return path


def verify_release() -> dict[str, Any]:
    require(sha256(CHECKPOINT) == EXPECTED["checkpoint"], "checkpoint hash drift")
    require(sha256(PACKET) == EXPECTED["packet"], "packet hash drift")
    require(sha256(INVENTORY) == EXPECTED["inventory"], "inventory hash drift")
    require(sha256(EXTERNAL_REF) == EXPECTED["externalRef"], "external checkpoint ref drift")
    checkpoint = load(CHECKPOINT)
    packet = load(PACKET)
    inventory = load(INVENTORY)
    external_ref = load(EXTERNAL_REF)
    require(checkpoint["schema"] == "kc.core-v3-foundation-checkpoint/2" and checkpoint["status"] == "PASS", "checkpoint identity drift")
    require(packet["schema"] == "kc.core-v3-typed-sdk-packet/2" and packet["boundary"] == "foundation-only", "packet identity drift")
    require(inventory["schema"] == "kc.core-v3-immutable-release-inventory/1", "inventory identity drift")
    require(checkpoint["authorityPromoted"] is checkpoint["candidateAssembled"] is checkpoint["externalServicesCalled"] is False, "checkpoint boundary drift")
    require(checkpoint["supersedes"]["status"] == packet["supersedes"]["status"] == external_ref["rejectedPredecessor"]["status"] == "rejected-never-promoted", "predecessor rejection drift")
    require(checkpoint["supersedes"]["checkpointSha256"] == EXPECTED["predecessor"], "predecessor hash drift")
    require(external_ref["checkpointSha256"] == EXPECTED["checkpoint"], "external ref/checkpoint binding drift")
    resolve_release_ref(checkpoint["inventory"], "checkpoint.inventory")
    resolve_release_ref(checkpoint["typedSdkPacket"], "checkpoint.typedSdkPacket")

    actual_scope = [
        path for path in RELEASE.rglob("*")
        if path.is_file() and release_relative(path) not in {"checkpoint.json", "inventory.json"}
    ]
    computed = tree(actual_scope, RELEASE)
    declared = inventory["entries"]
    require(len(declared) == len({item["path"] for item in declared}) == 61, "inventory count/duplicates drift")
    require(declared == computed["entries"], "inventory entries differ from release bytes")
    require(computed["treeSha256"] == inventory["treeSha256"] == checkpoint["inventoryTreeSha256"] == EXPECTED["inventoryTree"], "inventory tree drift")
    snapshot = tree([path for path in (RELEASE / "snapshot").rglob("*") if path.is_file()], RELEASE)
    require(snapshot["treeSha256"] == checkpoint["sourceAndContractSnapshotTreeSha256"] == packet["sourceAndContractSnapshotTreeSha256"] == EXPECTED["snapshotTree"], "snapshot tree drift")

    all_files = [path for path in RELEASE.rglob("*") if path.is_file()]
    readonly = [path for path in all_files if getattr(path.stat(), "st_file_attributes", 0) & 0x1]
    require(len(readonly) == len(all_files), "release contains a writable file")

    packet_refs = [packet["publicAssembly"], *packet["runtimeDependencies"], *packet["evidence"]]
    for index, item in enumerate(packet_refs):
        resolve_release_ref(item, f"packet.refs[{index}]")
    path_values = [packet["publicProject"], packet["contractProject"], packet["externalConsumerGate"]["positive"], packet["externalConsumerGate"]["negative"]]
    for index, raw_value in enumerate(path_values):
        raw = Path(raw_value)
        require(not raw.is_absolute() and ".." not in raw.parts, f"packet path[{index}] is mutable/escaped")
        require((RELEASE / raw).is_file(), f"packet path[{index}] missing")
    mutable_pattern = re.compile(r"(?:^|[\\/])(?:bin[\\/]Release|obj)(?:[\\/]|$)", re.IGNORECASE)
    mutable_refs = [value for value in path_values + [item["path"] for item in packet_refs] if mutable_pattern.search(value)]
    require(not mutable_refs, f"packet contains mutable build references: {mutable_refs}")
    return {
        "status": "passed",
        "checkpoint": evidence(CHECKPOINT),
        "packet": evidence(PACKET),
        "inventory": evidence(INVENTORY),
        "externalReference": evidence(EXTERNAL_REF),
        "inventoryScope": {key: value for key, value in computed.items() if key != "entries"},
        "snapshot": {key: value for key, value in snapshot.items() if key != "entries"},
        "actualReleaseFiles": len(all_files),
        "readOnlyFiles": len(readonly),
        "packetMutableBuildReferences": mutable_refs,
    }


def fixture_evidence(path: Path) -> dict[str, Any]:
    if path.is_file():
        return evidence(path)
    files = [item for item in path.rglob("*") if item.is_file()]
    value = tree(files, path)
    return {
        "path": project_relative(path),
        "files": value["files"],
        "bytes": value["bytes"],
        "treeSha256": value["treeSha256"],
    }


def verify_execution(quality_root: Path) -> dict[str, Any]:
    owner_build_path = RELEASE / "evidence/foundation2-release-build.receipt.json"
    owner_tests_path = RELEASE / "evidence/foundation2-tests.receipt.json"
    owner_build = load(owner_build_path)
    owner_tests = load(owner_tests_path)
    independent_path = quality_root / "independent-tests.receipt.json"
    independent = load(independent_path)
    for label, receipt in (("owner", owner_tests), ("independent", independent)):
        tests = receipt.get("tests") or []
        require(receipt.get("status") == "PASS" and len(tests) == 30 and all(item["status"] == "PASS" for item in tests), f"{label} 30-test gate failed")
        require(all(receipt["boundaries"][key] is False for key in ("externalServicesCalled", "desktopModified", "workersModified", "frozenAuthorityModified", "candidateAssembled")), f"{label} boundary drift")
    require(owner_build["status"] == "PASS" and owner_build["exitCode"] == owner_build["warningCount"] == owner_build["errorCount"] == 0, "owner Release build receipt failed")
    build_log = (quality_root / "build.log").read_text(encoding="utf-8-sig")
    require("0 个警告" in build_log and "0 个错误" in build_log, "independent staged build was not 0/0")
    positive_log = (quality_root / "positive-consumer.log").read_text(encoding="utf-8-sig")
    negative_log = (quality_root / "negative-consumer.log").read_text(encoding="utf-8-sig")
    require("SDK_POSITIVE_CONSUMER_OK" in positive_log, "positive external consumer did not run")
    forbidden_files = ["CommandEnvelopeAccess.cs", "QueryEnvelopeAccess.cs", "ArtifactReferenceAccess.cs", "KernelSchemasAccess.cs", "CoreV3ProtocolClientAccess.cs"]
    require(all(name in negative_log for name in forbidden_files), "negative consumer evidence incomplete")
    require("error CS0122" in negative_log and "error CS0234" in negative_log, "negative consumer did not fail on accessibility/surface")
    reflection = load(quality_root / "contracts-reflection.json")
    require(reflection["status"] == "PASS" and reflection["exportedTypes"] == 0, "Contracts exported types gate failed")
    reproducible = load(quality_root / "reproducible-build-comparison.json")
    require(len(reproducible) == 2 and all(item["Match"] is True for item in reproducible), "PathMap reproducible build mismatch")
    return {
        "status": "passed",
        "ownerBuild": evidence(owner_build_path),
        "ownerTests": evidence(owner_tests_path),
        "independentTests": evidence(independent_path),
        "tests": 30,
        "buildWarnings": 0,
        "buildErrors": 0,
        "positiveConsumer": evidence(quality_root / "positive-consumer.log"),
        "negativeConsumer": evidence(quality_root / "negative-consumer.log"),
        "forbiddenNegativeFiles": forbidden_files,
        "contractsExportedTypes": 0,
        "reflection": evidence(quality_root / "contracts-reflection.json"),
        "reproducibleBuild": evidence(quality_root / "reproducible-build-comparison.json"),
        "externalReadOnlyReplayDependencies": [fixture_evidence(AUTHORITY_ANCHOR), fixture_evidence(ANKI_FIXTURE), fixture_evidence(HISTORICAL_FIXTURE)],
        "testReplaySelfContainment": "CONDITIONAL-THREE-EXTERNAL-READONLY-FIXTURE-DEPENDENCIES",
    }


def write_once(path: Path, content: str) -> None:
    require(not path.exists(), f"append-only output exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--quality-root", required=True, type=Path)
    parser.add_argument("--output-root", required=True, type=Path)
    args = parser.parse_args()
    quality_root = args.quality_root.resolve()
    output_root = args.output_root.resolve()
    require(quality_root.is_relative_to((PROJECT / "canary/quality-core-v3-foundation2").resolve()), "quality root escaped")
    require(output_root.is_relative_to((PROJECT / "receipts/quality-core-v3-foundation2").resolve()) and not output_root.exists(), "output root must be new append-only Quality path")
    release = verify_release()
    execution = verify_execution(quality_root)
    release_files = [path for path in RELEASE.rglob("*") if path.is_file()]
    secret_scan = scan_paths_for_secrets(release_files + [EXTERNAL_REF])
    require(secret_scan["status"] == "passed" and secret_scan["findingCount"] == 0, "release evidence secret scan failed")
    status = "PASS-FOUNDATION2-IMMUTABLE-SDK-CONDITIONAL-TEST-REPLAY-DEPENDENCIES"
    receipt = {
        "schemaVersion": "knowledge-clipper-core-v3-foundation2-independent-quality.v1",
        "generatedAt": datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
        "status": status,
        "release": release,
        "execution": execution,
        "secretScan": secret_scan,
        "decision": {
            "immutableSdkPacket": "PASS-INDEPENDENT",
            "inventoryAndReadOnlySeal": "PASS-INDEPENDENT",
            "sourceAndContractSnapshot": "PASS-INDEPENDENT",
            "reproducibleClientAndContractsAssemblies": "PASS-WITH-EXPLICIT-PATHMAP",
            "externalConsumerBoundary": "PASS-INDEPENDENT",
            "testReplaySelfContainment": execution["testReplaySelfContainment"],
            "predecessor054850Z": "REJECTED-NEVER-PROMOTED",
            "authorityPromotion": "NOT_RUN",
            "candidateAssembly": "NOT_RUN",
            "desktopAndWorkers": "NOT_RUN",
            "ankiAndQuestion": "PREPARED-UNAVAILABLE",
            "externalServices": "ZERO-CALLS",
        },
    }
    receipt_path = output_root / "quality-receipt.json"
    readable_path = output_root / "READABLE.md"
    write_once(receipt_path, json.dumps(receipt, ensure_ascii=False, indent=2) + "\n")
    readable = f"""# Core v3 foundation.2 独立质量门禁\n\n- 结论：**{status}**\n- Checkpoint：`{EXPECTED['checkpoint']}`；packet：`{EXPECTED['packet']}`。\n- Inventory：61/61，tree `{EXPECTED['inventoryTree']}`；release 实际 63 文件且全部 ReadOnly。\n- Snapshot tree：`{EXPECTED['snapshotTree']}`；packet mutable `bin/Release` / `obj` 引用 0。\n- 隔离 snapshot Release build：0 warning / 0 error；30/30 PASS；外部服务 0。\n- 外部 consumer：正向编译运行 PASS；5 个低层类型负向编译拒绝 PASS；Contracts exported types=0。\n- Client/Contracts 在显式 PathMap 下从 snapshot 重建后与封存 DLL 逐字节哈希一致。\n- 真实边界：30 项测试重放还需要 release 未收录的 authority checkpoint 与两组历史 artifact fixtures；它们已逐文件绑定到本回执，因此 SDK release PASS，但“测试完全自包含”仍为 conditional。\n- 旧 054850Z：rejected-never-promoted；未执行 authority promotion、Desktop/worker migration 或 candidate assembly。\n"""
    write_once(readable_path, readable)
    print(json.dumps({"status": status, "receipt": evidence(receipt_path), "readable": evidence(readable_path), "tests": 30, "inventoryFiles": 61, "releaseFiles": release["actualReleaseFiles"]}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
