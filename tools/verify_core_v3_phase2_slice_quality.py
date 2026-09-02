from __future__ import annotations

import argparse
import hashlib
import json
import re
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

from assemble_integrated_desktop_v1240_quality import sha256
from verify_settings_control_plane import scan_paths_for_secrets


PROJECT = Path(__file__).resolve().parents[1]
RELEASE = PROJECT / "core-v3/releases/20260902T110618Z-phase2-question-anki-slice.1"
CHECKPOINT = RELEASE / "checkpoint.json"
PACKET = RELEASE / "typed-sdk-slice-packet.json"
INVENTORY = RELEASE / "inventory.json"
AUTHORITY_CHECKPOINT = PROJECT / "contracts/core/v2.10/checkpoints/v2.10.6-20260902T041800Z/checkpoint.json"
AUTHORITY_REGISTRY = PROJECT / "contracts/authority/worker-registry/v1.2.41-20260902T042000Z/worker-registry.v1.json"
AUTHORITY_RELEASE = PROJECT / "releases/knowledge-clipper-authority-v1.2.41-20260902T042000Z/win-x64"
ANKI_FIXTURE = PROJECT / "canary/quality-integrated-v1241/20260902T045200Z-entrypoint-resolver-live-readonly-stateful-v2/isolated-real-store/localappdata/KnowledgeClipper/jobs/module-actions/settings-anki-probe-53b61a21382046a7bdaf3db1dce82f1e/package"
HISTORICAL_FIXTURE = PROJECT / "canary/backend-full-pipeline/20260826T061500Z/jobs/backend-remnote-20260826t061500z"

EXPECTED = {
    "checkpoint": "42E1C4CFFF3C23B147DC85D94E774D55BEFACD861B07B19316B4B94E6755F130",
    "packet": "F7F3E2D1DA3C174099CC185239678230D1C5CFF9EAB53EA4CC52C94E18CABCDB",
    "inventory": "46702E738284374ED768294CB6876418AF51931EE15E2B3F719CCDDFE117873A",
    "inventoryTree": "A6F7F230B0D1960A0653E2A307118FF4C9C89615CD43830D9413C29B57328FCA",
    "snapshotTree": "BC7568DEFF658C4FC5E310085F2767FAC8DE5E56855965B5903F3BF160EF1C26",
    "predecessor": "D789C855FE1F3264939E82A5661172523FFAD479218F927A8C2B44F7AD872F7A",
    "authorityCheckpoint": "20AF0975FF176B6D46249B98AC36D72C74C7A494965AF3A69DE4C7FF52235D79",
    "authorityRegistry": "FBF1A3E30462A7C05269E47D510153A0165D502E204ABB50936E332BA07129A7",
    "contentExe": "F6EC35798F8948727B8664517FDBB096DB8A94C8DD079F612B05BAC4EA72D3B8",
    "ankiExe": "B1F68947D8500766A8C63D30719BC849444F2533C2DB48DDC1065E2230C9751C",
    "catalog": "24408E53B85C09C28F785715E440984091BF0BB6A358A64564161EF38055C7C0",
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


def tree_evidence(path: Path) -> dict[str, Any]:
    value = tree((item for item in path.rglob("*") if item.is_file()), path)
    return {"path": project_relative(path), "files": value["files"], "bytes": value["bytes"], "treeSha256": value["treeSha256"]}


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
    require(sha256(PACKET) == EXPECTED["packet"], "typed SDK slice packet hash drift")
    require(sha256(INVENTORY) == EXPECTED["inventory"], "inventory hash drift")
    checkpoint = load(CHECKPOINT)
    packet = load(PACKET)
    inventory = load(INVENTORY)
    require(checkpoint["schema"] == "kc.core-v3-phase2-slice-checkpoint/1", "checkpoint schema drift")
    require(checkpoint["status"] == packet["status"] == "PASS-not-promoted", "test-slice status drift")
    require(packet["schema"] == "kc.core-v3-typed-sdk-slice-packet/1" and packet["boundary"] == "phase2-question-anki-test-only-slice", "packet identity drift")
    require(checkpoint["authorityPromoted"] is checkpoint["productionEnabled"] is checkpoint["candidateAssembled"] is checkpoint["realExternalServicesCalled"] is False, "promotion/external boundary drift")
    require(packet["predecessor"]["checkpointSha256"] == EXPECTED["predecessor"] and packet["predecessor"]["preserved"] is True, "foundation.2 predecessor drift")
    resolve_release_ref(checkpoint["inventory"], "checkpoint.inventory")
    resolve_release_ref(checkpoint["typedSdkSlicePacket"], "checkpoint.typedSdkSlicePacket")

    inventory_scope = [
        item for item in RELEASE.rglob("*")
        if item.is_file() and release_relative(item) not in {"inventory.json", "checkpoint.json"}
    ]
    computed = tree(inventory_scope, RELEASE)
    require(len(inventory["entries"]) == len({item["path"] for item in inventory["entries"]}) == 82, "inventory count/duplicate drift")
    require(inventory["entries"] == computed["entries"], "inventory entries differ from immutable release bytes")
    require(computed["treeSha256"] == inventory["treeSha256"] == checkpoint["inventoryTreeSha256"] == EXPECTED["inventoryTree"], "inventory tree drift")
    snapshot = tree((item for item in (RELEASE / "snapshot").rglob("*") if item.is_file()), RELEASE)
    require(snapshot["treeSha256"] == checkpoint["sourceAndContractSnapshotTreeSha256"] == packet["sourceAndContractSnapshotTreeSha256"] == EXPECTED["snapshotTree"], "snapshot tree drift")

    all_files = [item for item in RELEASE.rglob("*") if item.is_file()]
    readonly = [item for item in all_files if getattr(item.stat(), "st_file_attributes", 0) & 0x1]
    require(len(all_files) == 84 and len(readonly) == len(all_files), "release file count/read-only seal drift")
    for index, item in enumerate([packet["publicAssembly"], *packet["runtimeDependencies"], *packet["evidence"]]):
        resolve_release_ref(item, f"packet.refs[{index}]")
    for label in ("publicProject", "capabilityBinding", "capabilityCatalog"):
        raw = Path(packet[label])
        require(not raw.is_absolute() and ".." not in raw.parts and (RELEASE / raw).is_file(), f"packet {label} escaped/missing")
    mutable = [
        value for value in (packet["publicProject"], packet["capabilityBinding"], packet["capabilityCatalog"])
        if re.search(r"(?:^|[\\/])(?:bin|obj)(?:[\\/]|$)", value, re.IGNORECASE)
    ]
    require(not mutable, f"packet contains mutable source references: {mutable}")
    return {
        "status": "passed",
        "checkpoint": evidence(CHECKPOINT),
        "typedSdkSlicePacket": evidence(PACKET),
        "inventory": evidence(INVENTORY),
        "inventoryScope": {key: value for key, value in computed.items() if key != "entries"},
        "snapshot": {key: value for key, value in snapshot.items() if key != "entries"},
        "actualReleaseFiles": len(all_files),
        "readOnlyFiles": len(readonly),
        "mutablePacketReferences": mutable,
    }


def verify_authority() -> dict[str, Any]:
    copied_checkpoint = RELEASE / "evidence/authority/core-v2.10.6-checkpoint.json"
    copied_registry = RELEASE / "evidence/authority/worker-registry.v1.json"
    frozen_path = RELEASE / "evidence/authority/frozen-worker-dependencies.json"
    require(sha256(AUTHORITY_CHECKPOINT) == sha256(copied_checkpoint) == EXPECTED["authorityCheckpoint"], "authority checkpoint drift")
    require(sha256(AUTHORITY_REGISTRY) == sha256(copied_registry) == EXPECTED["authorityRegistry"], "authority registry drift")
    frozen = load(frozen_path)
    require(frozen["authorityVersion"] == "1.2.41" and frozen["admissionAction"] == "describe --json only", "frozen authority identity drift")
    expected_workers = {
        "content.deepseek": ("0.9.5", "workers/content/KnowledgeClipper.Content.Worker.exe", EXPECTED["contentExe"]),
        "export.anki": ("1.4.0", "workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe", EXPECTED["ankiExe"]),
    }
    worker_evidence = []
    for worker in frozen["workers"]:
        worker_id = worker["workerId"]
        require(worker_id in expected_workers, f"unexpected frozen worker {worker_id}")
        version, relative, expected_hash = expected_workers[worker_id]
        actual = AUTHORITY_RELEASE / relative
        require(worker["workerVersion"] == version and worker["contractVersion"] == "1.0.0", f"{worker_id} version/contract drift")
        require(sha256(actual) == worker["entrypoint"]["sha256"] == expected_hash, f"{worker_id} executable hash drift")
        require(actual.stat().st_size == worker["entrypoint"]["bytes"], f"{worker_id} executable bytes drift")
        worker_evidence.append({"workerId": worker_id, "version": version, "entrypoint": evidence(actual)})
    require({item["workerId"] for item in frozen["workers"]} == set(expected_workers), "frozen worker set drift")
    return {
        "status": "passed",
        "authorityVersion": "1.2.41",
        "checkpoint": evidence(AUTHORITY_CHECKPOINT),
        "registry": evidence(AUTHORITY_REGISTRY),
        "releaseCopiesExact": True,
        "workers": worker_evidence,
        "admission": "describe-only-during-independent-test",
    }


def verify_test_receipt(receipt: dict[str, Any], expected_count: int, label: str) -> None:
    tests = receipt.get("tests") or []
    require(receipt.get("status") == "PASS" and len(tests) == expected_count and all(item.get("status") == "PASS" for item in tests), f"{label} test gate failed")


def verify_execution(quality_root: Path) -> dict[str, Any]:
    owner_phase_path = RELEASE / "evidence/phase2-tests.receipt.json"
    owner_foundation_path = RELEASE / "evidence/foundation-regression.receipt.json"
    owner_build_path = RELEASE / "evidence/phase2-release-build.receipt.json"
    independent_phase_path = quality_root / "receipts/phase2-tests.receipt.json"
    independent_foundation_path = quality_root / "receipts/foundation-regression.receipt.json"
    owner_phase = load(owner_phase_path)
    owner_foundation = load(owner_foundation_path)
    owner_build = load(owner_build_path)
    independent_phase = load(independent_phase_path)
    independent_foundation = load(independent_foundation_path)
    verify_test_receipt(owner_phase, 8, "owner Phase2")
    verify_test_receipt(independent_phase, 8, "independent Phase2")
    verify_test_receipt(owner_foundation, 30, "owner Foundation")
    verify_test_receipt(independent_foundation, 30, "independent Foundation")
    require(owner_build["status"] == "PASS" and owner_build["exitCode"] == owner_build["warningCount"] == owner_build["errorCount"] == 0, "owner build gate failed")
    build_log = (quality_root / "build.log").read_text(encoding="utf-8-sig")
    require("0 个警告" in build_log and "0 个错误" in build_log, "independent build was not 0 warning / 0 error")
    for label, receipt in (("owner", owner_phase), ("independent", independent_phase)):
        boundaries = receipt["boundaries"]
        require(boundaries["executionMode"] == "authority-bound-independent-fake-proxies", f"{label} fake mode drift")
        require(all(boundaries[key] is False for key in ("legacyOrchestratorInvoked", "realAnkiCalled", "realDeepSeekCalled", "remNoteCalled", "minerUCalled", "speakerCalled", "frozenAuthorityModified", "foundation2Modified", "finalCandidateAssembled")), f"{label} external/mutation boundary drift")
        value = receipt["evidence"]
        require(value["catalogSha256"] == EXPECTED["catalog"], f"{label} catalog binding drift")
        before, replay = value["metricsBeforeReplay"], value["metricsAfterReplay"]
        require(before == replay, f"{label} replay changed metrics")
        require(before == {"contentAttempts": 3, "ankiProbeAttempts": 1, "ankiExportAttempts": 1, "ankiExternalWriteCount": 1, "ankiReadbackCount": 1, "remNoteCalls": 0, "minerUCalls": 0, "speakerCalls": 0, "mode": "authority-bound-independent-fake-proxies"}, f"{label} exact fake metrics drift")
        require(value["replayWorkerAttemptDelta"] == value["replayExternalWriteDelta"] == 0 and value["concurrentSuccessfulWriters"] == 1, f"{label} replay/concurrency invariant drift")
        require(value["ankiExportEvidence"]["exactReadback"] is True and value["ankiExportEvidence"]["externalWriteCount"] == 1, f"{label} fake Anki evidence drift")
    expected_names = {
        "production-host-fails-closed-on-test-slice",
        "slow-worker-fast-accept-and-reconnect-observation",
        "question-session-crash-recovery",
        "authority-entrypoint-hash-drift-rejected",
        "authority-exact-worker-version-drift-rejected",
        "authority-contract-version-drift-rejected",
    }
    require(expected_names.issubset({item["name"] for item in independent_phase["tests"]}), "independent Phase2 invariant coverage drift")
    return {
        "status": "passed",
        "ownerBuild": evidence(owner_build_path),
        "ownerPhase2": evidence(owner_phase_path),
        "ownerFoundation": evidence(owner_foundation_path),
        "independentBuildLog": evidence(quality_root / "build.log"),
        "independentPhase2": evidence(independent_phase_path),
        "independentFoundation": evidence(independent_foundation_path),
        "buildWarnings": 0,
        "buildErrors": 0,
        "phase2Tests": "8/8",
        "foundationTests": "30/30",
        "fakeMetrics": independent_phase["evidence"]["metricsBeforeReplay"],
        "replayWorkerAttemptDelta": 0,
        "replayExternalWriteDelta": 0,
        "concurrentSuccessfulWriters": 1,
        "productionHost": "FAIL-CLOSED-REAL-WORKER-PROCESS-RUNTIME-NOT-IMPLEMENTED",
        "externalReadOnlyReplayDependencies": [tree_evidence(ANKI_FIXTURE), tree_evidence(HISTORICAL_FIXTURE)],
        "testReplaySelfContainment": "CONDITIONAL-AUTHORITY-WORKERS-AND-TWO-HISTORICAL-FIXTURES-NOT-PACKAGED",
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
    require(quality_root.is_relative_to((PROJECT / "canary/quality-core-v3-phase2").resolve()), "quality root escaped")
    require(output_root.is_relative_to((PROJECT / "receipts/quality-core-v3-phase2").resolve()) and not output_root.exists(), "output root must be new append-only Quality path")
    release = verify_release()
    authority = verify_authority()
    execution = verify_execution(quality_root)
    secret_scan = scan_paths_for_secrets([item for item in RELEASE.rglob("*") if item.is_file()])
    require(secret_scan["status"] == "passed" and secret_scan["findingCount"] == 0, "release secret scan failed")
    status = "PASS-CORE-V3-PHASE2-TEST-SLICE-CONDITIONAL-PRODUCTION-RUNTIME-UNIMPLEMENTED"
    receipt = {
        "schemaVersion": "knowledge-clipper-core-v3-phase2-slice-independent-quality.v1",
        "generatedAt": datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
        "status": status,
        "release": release,
        "authority": authority,
        "execution": execution,
        "secretScan": secret_scan,
        "decision": {
            "immutableReleaseInventory": "PASS-INDEPENDENT",
            "snapshotAndReadOnlySeal": "PASS-INDEPENDENT",
            "authorityBindingAndDescribeAdmission": "PASS-INDEPENDENT",
            "namedTypedProjections": "PASS-INDEPENDENT",
            "fastAcceptanceSlowBackgroundReconnect": "PASS-INDEPENDENT-FAKE",
            "stableArtifactIdentityReplayConcurrencyCrashRecovery": "PASS-INDEPENDENT-FAKE",
            "foundation2Regression": "PASS-30-OF-30",
            "productionWorkerProcessRuntime": "NOT-IMPLEMENTED-FAIL-CLOSED",
            "authorityPromotion": "NOT-RUN",
            "candidateAssembly": "NOT-RUN",
            "realExternalServices": "ZERO-CALLS",
            "legacyOrchestrator": "NOT-INVOKED",
        },
    }
    receipt_path = output_root / "quality-receipt.json"
    readable_path = output_root / "READABLE.md"
    write_once(receipt_path, json.dumps(receipt, ensure_ascii=False, indent=2) + "\n")
    readable = f"""# Core v3 Phase2 question→Anki test slice 独立质量门禁

- 结论：**{status}**
- 封存树：84 文件全部 ReadOnly；inventory 82/82，tree `{EXPECTED['inventoryTree']}`；snapshot tree `{EXPECTED['snapshotTree']}`。
- Checkpoint `{EXPECTED['checkpoint']}`；typed SDK slice packet `{EXPECTED['packet']}`；inventory `{EXPECTED['inventory']}`。
- Authority 1.2.41：checkpoint/registry 及 content.deepseek 0.9.5、export.anki 1.4.0 入口哈希精确绑定；独立测试仅执行 describe admission，不执行真实 worker 业务。
- 隔离 snapshot Release build：0 warning / 0 error；Phase2 8/8 PASS；Foundation 30/30 PASS。
- 可读 fake 输入→输出：`Why does the derivative of x^2 equal 2x?` → initial answer → `Explain with a geometric intuition.` → final answer → 1 张 fake Anki card，exactReadback=true。
- 精确计数：content=3、probe=1、export=1、fake Anki write=1、readback=1；重放 attempt/write 增量 0；并发 writer=1；RemNote/MinerU/speaker=0。
- 生产模式：由于 real `WorkerProcessRuntime` 未实现，Host 显式 fail-closed；test-only fake 必须显式 `authority-bound-fake-test-only`。
- 真实边界：未 promotion、未组 candidate、未调用外部服务；测试重放仍依赖封存树外的 authority worker 与两组历史只读 fixture，因此“测试完全自包含”为 conditional。
"""
    write_once(readable_path, readable)
    print(json.dumps({"status": status, "receipt": evidence(receipt_path), "readable": evidence(readable_path), "phase2": "8/8", "foundation": "30/30", "releaseFiles": 84}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
