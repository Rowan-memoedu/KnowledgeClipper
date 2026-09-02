from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

from assemble_integrated_desktop_v1240_quality import sha256
from verify_settings_control_plane import scan_paths_for_secrets


PROJECT = Path(__file__).resolve().parents[1]
SDK_ROOT = PROJECT / "core-v3/releases/20260902T060054Z-foundation.2"
PACKET = SDK_ROOT / "typed-sdk-packet.json"
GATEWAY = PROJECT / "src/KnowledgeClipper.Desktop.Adapters/NamedPipeCoreGateway.cs"
DELTA_MANIFEST = PROJECT / "tests/KnowledgeClipper.Desktop.CoreGateway.Tests/gateway-delta-manifest.v1.json"
PREVIOUS_QUALITY = PROJECT / "receipts/quality-desktop-core-gateway/20260902T070856Z-independent/quality-receipt.json"
PROJECTS = [
    PROJECT / "desktop/RemNoteQuickClipper.Desktop.csproj",
    PROJECT / "desktop/RemNoteQuickClipper.Desktop.WindowsCanary.csproj",
    PROJECT / "tests/KnowledgeClipper.Desktop.CoreGateway.Tests/KnowledgeClipper.Desktop.CoreGateway.Tests.csproj",
]
DELTA_PATHS = [
    "desktop/RemNoteQuickClipper.Desktop.csproj",
    "desktop/RemNoteQuickClipper.Desktop.WindowsCanary.csproj",
    "src/KnowledgeClipper.Desktop.Adapters/NamedPipeCoreGateway.cs",
    "src/KnowledgeClipper.Desktop.Application/ApplicationContracts.cs",
    "src/KnowledgeClipper.Desktop.Application/README.md",
    "src/KnowledgeClipper.Desktop.Application/UseCases.cs",
    "tests/KnowledgeClipper.Desktop.CoreGateway.Tests/KnowledgeClipper.Desktop.CoreGateway.Tests.csproj",
    "tests/KnowledgeClipper.Desktop.CoreGateway.Tests/Program.cs",
    "tests/KnowledgeClipper.Desktop.CoreGateway.Tests/README.md",
]
EXPECTED = {
    "gateway": "F5E670B55DC754B15D7860FBB66BFA45CB0F7B3DA002B3C11E0D7A59DE41BDBD",
    "packet": "88076433AF2E4BC2C2A3FE3A5F96E22CD1461A71DAA7A58B0081D7E914A6A425",
    "client": "7C191017324F6464D0A8328CF5506F1A6960BC0148B25FAC4F5BD1D3B20F5D8B",
    "contracts": "831DBE54E8202A513F94E8ECCB419D8EB5847E587F981DB30DE585FA74E78DFC",
    "claimedDeltaTree": "4420B555F7588620B0A990E6BEE677B2291E57735C356BCB9A0ED2BB2D6965BA",
    "deltaManifest": "F849572A2F242ED153AB4BC861981A5313A6036690ABE397F317F6FEDBEC8446",
    "supersedingDeltaTree": "87F4044341FFE8020F9727E98156DE44CB09094E3A3F10E8C901625F7818EBCE",
    "previousQuality": "5950E014869971605E50BBA2F70C6279F5BA9DDBDD3977BC435F910D54EAADB8",
}


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def evidence(path: Path) -> dict[str, Any]:
    require(path.is_file(), f"missing evidence: {path}")
    return {
        "path": path.resolve().relative_to(PROJECT.resolve()).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha256(path),
    }


def load(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def source_tree(paths: Iterable[Path]) -> dict[str, Any]:
    rows = [evidence(path) for path in paths]
    rows.sort(key=lambda item: item["path"])
    material = "".join(f"{item['path']}\0{item['bytes']}\0{item['sha256'].lower()}\n" for item in rows)
    return {
        "algorithm": "SHA-256 over UTF-8 records sorted by ordinal project-relative path; path NUL decimal-bytes NUL lowercase-sha256 LF; final LF included",
        "files": len(rows),
        "bytes": sum(item["bytes"] for item in rows),
        "treeSha256": hashlib.sha256(material.encode("utf-8")).hexdigest().upper(),
        "entries": rows,
    }


def check_build_log(path: Path, label: str) -> dict[str, Any]:
    text = path.read_text(encoding="utf-8-sig")
    require("0 个警告" in text and "0 个错误" in text, f"{label} was not 0 warning / 0 error")
    return evidence(path)


def evaluate(quality_root: Path, delta_recompute: Path) -> dict[str, Any]:
    require(sha256(GATEWAY) == EXPECTED["gateway"], "Gateway source hash drift")
    require(sha256(PACKET) == EXPECTED["packet"], "foundation.2 packet hash drift")
    client = SDK_ROOT / "artifacts/net8.0/KnowledgeClipper.Core.V3.Client.dll"
    contracts = SDK_ROOT / "artifacts/net8.0/KnowledgeClipper.Core.V3.Contracts.dll"
    require(sha256(client) == EXPECTED["client"] and sha256(contracts) == EXPECTED["contracts"], "immutable SDK artifact drift")

    gateway_text = GATEWAY.read_text(encoding="utf-8-sig")
    forbidden = [
        "KnowledgeClipper.Core.V3.Contracts", "CoreV3ProtocolClient", "CommandEnvelope", "QueryEnvelope",
        "WireRequestEnvelope", "KernelSchemas", "protocolVersion", "schemaVersion", "idempotencyKey",
        "authorityReleaseRoot", "artifactPath", "NamedPipeCommonIpcClient", "DesktopAnkiProbeHost",
        "DesktopContentSettingsHost", "FileQuestionAnswerArtifactReader",
    ]
    gateway_hits = [value for value in forbidden if value.casefold() in gateway_text.casefold()]
    require(not gateway_hits, f"Gateway low-level/legacy surface leak: {gateway_hits}")
    gateway_count = len(list((PROJECT / "src/KnowledgeClipper.Desktop.Adapters").glob("*NamedPipeCoreGateway*.cs")))
    require(gateway_count == 1, "Desktop does not have exactly one NamedPipeCoreGateway")

    project_results = []
    for path in PROJECTS:
        text = path.read_text(encoding="utf-8-sig")
        normalized = text.replace("/", "\\").casefold()
        counts = {
            "projectReference": text.casefold().count("<projectreference"),
            "mutableCoreSource": normalized.count("core-v3\\src"),
            "mutableBinRelease": normalized.count("bin\\release"),
            "immutableSdkRoot": normalized.count("core-v3\\releases\\20260902t060054z-foundation.2"),
        }
        require(counts["projectReference"] == counts["mutableCoreSource"] == counts["mutableBinRelease"] == 0, f"mutable Core binding in {path}")
        require(counts["immutableSdkRoot"] > 0, f"immutable SDK binding missing in {path}")
        project_results.append({**evidence(path), **counts})

    surface_path = quality_root / "assembly-surface.json"
    surface = load(surface_path)
    require(surface["contracts"]["sha256"] == EXPECTED["contracts"] and surface["contracts"]["exportedTypes"] == 0, "Contracts public surface gate failed")
    require(surface["client"]["sha256"] == EXPECTED["client"] and not surface["client"]["forbiddenPublicTypeHits"], "Client public surface gate failed")

    catalog = load(SDK_ROOT / "snapshot/contracts/core/v3/capabilities/capability-catalog.v1.json")
    unavailable = sorted(item["operationId"] for item in catalog["operations"] if item["available"] is False)
    require(unavailable == ["anki.probe", "question.follow-up", "question.initial", "question.make-cards"], "foundation unavailable catalog drift")

    gateway_log = (quality_root / "core-gateway-tests.log").read_text(encoding="utf-8-sig")
    application_log = (quality_root / "application-tests.log").read_text(encoding="utf-8-sig")
    require("DESKTOP_CORE_GATEWAY_TESTS PASS assertions=15" in gateway_log, "Gateway 15-test replay failed")
    require("DESKTOP_V3_APPLICATION_TESTS PASS assertions=22" in application_log, "Application 22-test replay failed")
    desktop_build = check_build_log(quality_root / "desktop-build.log", "production Desktop build")
    windows_build = check_build_log(quality_root / "windows-canary-build.log", "WindowsCanary build")

    delta = source_tree(PROJECT / path for path in DELTA_PATHS)
    require(delta["files"] == 9 and delta["bytes"] == 59149, "Gateway delta count/bytes drift")
    require(sha256(DELTA_MANIFEST) == EXPECTED["deltaManifest"], "superseding delta manifest hash drift")
    require(sha256(PREVIOUS_QUALITY) == EXPECTED["previousQuality"], "previous conditional Quality receipt drift")
    manifest = load(DELTA_MANIFEST)
    manifest_snapshot = manifest["sourceSnapshot"]
    require(manifest["schemaVersion"] == "knowledge-clipper-desktop-gateway-delta-manifest.v1", "delta manifest identity drift")
    require(manifest["supersedes"]["declaredTreeSha256"].upper() == EXPECTED["claimedDeltaTree"] and manifest["supersedes"]["status"] == "evidence-drift-superseded", "delta supersede chain drift")
    require(manifest_snapshot["files"] == delta["files"] and manifest_snapshot["bytes"] == delta["bytes"] and manifest_snapshot["treeSha256"].upper() == delta["treeSha256"] == EXPECTED["supersedingDeltaTree"], "delta manifest aggregate drift")
    normalized_manifest_entries = [{"path": item["path"], "bytes": item["bytes"], "sha256": item["sha256"].upper()} for item in manifest_snapshot["entries"]]
    require(normalized_manifest_entries == delta["entries"], "delta manifest file entries drift")
    recomputed = load(delta_recompute)
    require(recomputed["files"] == 9 and recomputed["bytes"] == 59149 and recomputed["treeSha256"].upper() == EXPECTED["supersedingDeltaTree"], "independent delta tool recompute drift")
    secret_scan = scan_paths_for_secrets([PROJECT / path for path in DELTA_PATHS] + PROJECTS + [PACKET, DELTA_MANIFEST])
    require(secret_scan["status"] == "passed" and secret_scan["findingCount"] == 0, "Gateway quality secret scan failed")
    return {
        "status": "PASS-DESKTOP-V3-GATEWAY-MODULE-CONDITIONAL",
        "gateway": evidence(GATEWAY),
        "immutableSdk": {"packet": evidence(PACKET), "client": evidence(client), "contracts": evidence(contracts)},
        "sourceBoundary": {"forbiddenHits": gateway_hits, "gatewayImplementationCount": gateway_count},
        "projectBindings": project_results,
        "assemblySurface": {"evidence": evidence(surface_path), "contractsExportedTypes": 0, "clientForbiddenPublicTypeHits": []},
        "capabilityCatalog": {"preparedUnavailable": unavailable, "failClosed": True, "fallbackInvocations": 0},
        "tests": {
            "gateway": {"assertions": 15, "status": "passed", "log": evidence(quality_root / "core-gateway-tests.log")},
            "application": {"assertions": 22, "status": "passed", "log": evidence(quality_root / "application-tests.log")},
            "productionDesktopBuild": desktop_build,
            "windowsCanaryBuild": windows_build,
        },
        "delta": {
            **delta,
            "manifest": evidence(DELTA_MANIFEST),
            "independentRecompute": evidence(delta_recompute),
            "status": "passed-superseding-manifest",
            "supersedesTreeSha256": EXPECTED["claimedDeltaTree"],
            "supersedesStatus": "evidence-drift-superseded",
            "supersedesQualityReceipt": evidence(PREVIOUS_QUALITY),
        },
        "secretScan": secret_scan,
        "boundaries": {
            "fakeJourneyOnly": True,
            "externalServicesCalled": False,
            "productionFeatureGo": False,
            "candidateAssembled": False,
            "authorityPromoted": False,
        },
    }


def write_once(path: Path, content: str) -> None:
    require(not path.exists(), f"append-only output exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--quality-root", required=True, type=Path)
    parser.add_argument("--delta-recompute", required=True, type=Path)
    parser.add_argument("--output-root", required=True, type=Path)
    args = parser.parse_args()
    quality_root = args.quality_root.resolve()
    delta_recompute = args.delta_recompute.resolve()
    output_root = args.output_root.resolve()
    require(quality_root.is_relative_to((PROJECT / "canary/quality-desktop-core-gateway").resolve()), "quality root escaped")
    require(delta_recompute.is_relative_to((PROJECT / "canary/quality-desktop-core-gateway").resolve()) and delta_recompute.is_file(), "delta recompute evidence escaped/missing")
    require(output_root.is_relative_to((PROJECT / "receipts/quality-desktop-core-gateway").resolve()) and not output_root.exists(), "output root must be new append-only Quality path")
    result = evaluate(quality_root, delta_recompute)
    receipt = {
        "schemaVersion": "knowledge-clipper-desktop-core-gateway-independent-quality.v1",
        "generatedAt": datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
        **result,
        "decision": {
            "typedGatewayFunctionalGate": "PASS-INDEPENDENT",
            "immutableSdkBinding": "PASS-INDEPENDENT",
            "lowLevelAndLegacySurface": "PASS-ZERO-HITS",
            "gatewayDeltaEvidence": "PASS-SUPERSEDING-MANIFEST",
            "ankiAndQuestionProduction": "PREPARED-UNAVAILABLE-FAIL-CLOSED",
            "candidatePromotion": "NOT_RUN",
        },
    }
    receipt_path = output_root / "quality-receipt.json"
    readable_path = output_root / "READABLE.md"
    write_once(receipt_path, json.dumps(receipt, ensure_ascii=False, indent=2) + "\n")
    readable = f"""# Desktop v3 immutable Core Gateway 独立门禁\n\n- 结论：**{receipt['status']}**\n- Gateway：`{EXPECTED['gateway']}`；foundation.2 packet/Client/Contracts 精确绑定 PASS。\n- 独立重跑：Gateway 15/15、Application 22/22；production Desktop 与 WindowsCanary build 均 0 warning / 0 error。\n- 三个 csproj：ProjectReference=0、core-v3/src=0、bin/Release=0；均绑定 immutable foundation.2。\n- Gateway 低层协议/authority/path/legacy host 命中 0；Contracts exported types=0；Client forbidden public types=0。\n- Delta：superseding manifest `{EXPECTED['deltaManifest']}` 已逐文件复算 9 files / 59,149 bytes / `{EXPECTED['supersedingDeltaTree']}` PASS；旧 `{EXPECTED['claimedDeltaTree']}` 与上一份 conditional Quality receipt保持不可变并显式 superseded。\n- Anki/question 仍 unavailable，fake journey 证明 fail-closed/no fallback，不代表生产功能 GO。\n- 未组装候选、未提升 authority、外部服务调用 0。\n"""
    write_once(readable_path, readable)
    print(json.dumps({"status": receipt["status"], "receipt": evidence(receipt_path), "readable": evidence(readable_path), "gatewayAssertions": 15, "applicationAssertions": 22, "delta": receipt["delta"]}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
