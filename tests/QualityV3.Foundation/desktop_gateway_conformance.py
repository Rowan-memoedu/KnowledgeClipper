from __future__ import annotations

import argparse
import hashlib
import json
import re
from datetime import datetime, timezone
from pathlib import Path


class ConformanceError(RuntimeError):
    pass


def require(condition: bool, code: str) -> None:
    if not condition:
        raise ConformanceError(code)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def file_ref(project: Path, path: Path) -> dict:
    return {
        "path": path.relative_to(project).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha256_file(path),
    }


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def desktop_tree(entries: list[dict]) -> str:
    payload = b"".join(
        item["path"].encode("utf-8")
        + b"\0"
        + str(item["bytes"]).encode("ascii")
        + b"\0"
        + item["sha256"].lower().encode("ascii")
        + b"\n"
        for item in sorted(entries, key=lambda item: item["path"])
    )
    return hashlib.sha256(payload).hexdigest().upper()


def require_successful_build(path: Path, code: str) -> None:
    text = path.read_text(encoding="utf-8")
    require("已成功生成" in text or "Build succeeded" in text, code + ".success")
    require("0 个错误" in text or "0 Error(s)" in text, code + ".errors")


def main() -> int:
    parser = argparse.ArgumentParser(description="Independent Desktop v3 Core Gateway cross-conformance")
    parser.add_argument("--project-root", type=Path, required=True)
    parser.add_argument("--run-root", type=Path, required=True)
    parser.add_argument("--snapshot-recompute-root", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()

    project = args.project_root.resolve()
    run = args.run_root.resolve()
    snapshot_recompute = args.snapshot_recompute_root.resolve()
    output = args.output_root.resolve()
    allowed_run = (project / "canary/quality-v3-desktop-gateway-runs").resolve()
    allowed_snapshot_recompute = (project / "canary/quality-v3-desktop-gateway-snapshot-recompute").resolve()
    allowed_output = (project / "canary/quality-v3-desktop-gateway-conformance").resolve()
    require(run.is_relative_to(allowed_run), "gateway.run-root-location")
    require(snapshot_recompute.is_relative_to(allowed_snapshot_recompute), "gateway.snapshot-recompute-root-location")
    require(output.is_relative_to(allowed_output) and not output.exists(), "gateway.output-not-append-only")

    expected_delta = {
        "desktop/RemNoteQuickClipper.Desktop.csproj": (6514, "093390E82CB4B842FA7BD8E805225B81CF16E0FC4042E9B732638DA834C45C2D"),
        "desktop/RemNoteQuickClipper.Desktop.WindowsCanary.csproj": (4767, "E31046E26AB27C3789C97D735DDC12B46C8F4698FE8D78BC45DA41D412BBE97B"),
        "src/KnowledgeClipper.Desktop.Adapters/NamedPipeCoreGateway.cs": (17165, "F5E670B55DC754B15D7860FBB66BFA45CB0F7B3DA002B3C11E0D7A59DE41BDBD"),
        "src/KnowledgeClipper.Desktop.Application/ApplicationContracts.cs": (2020, "8C0C048862C139F2D55EA251AF2463135C9F04CA9780E06563FDD0A683CED941"),
        "src/KnowledgeClipper.Desktop.Application/README.md": (3337, "6633B694E4DB0AF5A08A6C351DF91A18E0384259820F3222AB7B101C74EA3132"),
        "src/KnowledgeClipper.Desktop.Application/UseCases.cs": (7677, "FAE2365196ED4D987CFCD77579606C312E35B212BD0287BE802AFBCE76A00997"),
        "tests/KnowledgeClipper.Desktop.CoreGateway.Tests/KnowledgeClipper.Desktop.CoreGateway.Tests.csproj": (1207, "0FB477AEFCA80904C3B0390EA190A70564038A460D695F51BADE97DE68134557"),
        "tests/KnowledgeClipper.Desktop.CoreGateway.Tests/Program.cs": (15250, "7DEF1009C9F39D25A43AD3D25B47B05D77F55B066F5A8FFEA919D9B3C26773EA"),
        "tests/KnowledgeClipper.Desktop.CoreGateway.Tests/README.md": (1212, "97A2AA87FECF6DD8847AF7858414F37D28DF4EFC9532B9AFBAAA5382C4ADF8C2"),
    }
    manifest_path = project / "tests/KnowledgeClipper.Desktop.CoreGateway.Tests/gateway-delta-manifest.v1.json"
    require(sha256_file(manifest_path) == "F849572A2F242ED153AB4BC861981A5313A6036690ABE397F317F6FEDBEC8446", "gateway.manifest-identity")
    manifest = load_json(manifest_path)
    declared_snapshot = manifest["sourceSnapshot"]
    require(manifest["schemaVersion"] == "knowledge-clipper-desktop-gateway-delta-manifest.v1", "gateway.manifest-schema")
    require(manifest["workspaceRoot"] == "." and declared_snapshot["files"] == 9 and declared_snapshot["bytes"] == 59149, "gateway.manifest-boundary")
    require(declared_snapshot["algorithm"] == "SHA-256 over UTF-8 records sorted by StringComparer.Ordinal path; each record is path NUL decimal-bytes NUL lowercase-sha256 LF; final record includes LF", "gateway.manifest-algorithm")
    require(declared_snapshot["treeSha256"].upper() == "87F4044341FFE8020F9727E98156DE44CB09094E3A3F10E8C901625F7818EBCE", "gateway.manifest-tree")
    require(manifest["supersedes"]["declaredTreeSha256"].upper() == "4420B555F7588620B0A990E6BEE677B2291E57735C356BCB9A0ED2BB2D6965BA", "gateway.superseded-tree-identity")
    require(manifest["supersedes"]["status"] == "evidence-drift-superseded", "gateway.superseded-tree-status")
    declared_entries = declared_snapshot["entries"]
    require([item["path"] for item in declared_entries] == sorted(expected_delta), "gateway.manifest-ordinal-order")
    require(set(item["path"] for item in declared_entries) == set(expected_delta), "gateway.manifest-file-set")
    for item in declared_entries:
        expected_bytes, expected_sha = expected_delta[item["path"]]
        require(item["bytes"] == expected_bytes and item["sha256"].upper() == expected_sha, "gateway.manifest-entry:" + item["path"])

    delta_refs = []
    for relative, (expected_bytes, expected_sha) in expected_delta.items():
        path = project / relative
        require(path.is_file(), "gateway.delta-missing:" + relative)
        actual = file_ref(project, path)
        require(actual["bytes"] == expected_bytes and actual["sha256"] == expected_sha, "gateway.delta-drift:" + relative)
        delta_refs.append(actual)
    require(sum(item["bytes"] for item in delta_refs) == 59149, "gateway.delta-byte-count")
    independently_calculated_tree = desktop_tree(delta_refs)
    require(independently_calculated_tree == "87F4044341FFE8020F9727E98156DE44CB09094E3A3F10E8C901625F7818EBCE", "gateway.delta-tree")
    require(independently_calculated_tree == declared_snapshot["treeSha256"].upper(), "gateway.delta-manifest-tree-binding")
    official_snapshot_path = snapshot_recompute / "official-source-snapshot.json"
    require(official_snapshot_path.is_file(), "gateway.official-snapshot-missing")
    official_snapshot = load_json(official_snapshot_path)
    require(official_snapshot == declared_snapshot, "gateway.official-snapshot-manifest-drift")

    gateway_path = project / "src/KnowledgeClipper.Desktop.Adapters/NamedPipeCoreGateway.cs"
    gateway = gateway_path.read_text(encoding="utf-8")
    source_files = [path for path in (project / "src").rglob("*.cs") if "bin" not in path.parts and "obj" not in path.parts]
    implementations = [path for path in source_files if re.search(r"\bclass\s+NamedPipeCoreGateway\b", path.read_text(encoding="utf-8"))]
    require(implementations == [gateway_path], "gateway.unique-implementation")
    forbidden_gateway = (
        "KnowledgeClipper.Core.V3.Contracts", "CoreV3ProtocolClient", "CommandEnvelope", "QueryEnvelope",
        "WireRequestEnvelope", "KernelSchemas", "protocolVersion", "schemaVersion", "idempotencyKey",
        "authorityReleaseRoot", "artifactPath", "NamedPipeCommonIpcClient", "DesktopAnkiProbeHost",
        "DesktopContentSettingsHost", "FileQuestionAnswerArtifactReader", "ProjectReference",
    )
    require(all(value.lower() not in gateway.lower() for value in forbidden_gateway), "gateway.protocol-or-legacy-leak")
    require("using KnowledgeClipper.Core.V3.Client;" in gateway and "CoreV3Client" in gateway and "ISdkDomainFacade" in gateway, "gateway.typed-facade-binding")
    for anchor in (
        "client.QuestionInitialAsync", "client.QuestionFollowUpAsync", "client.QuestionMakeCardsAsync", "client.AnkiProbeAsync",
        "client.PublishTextArtifactAsync", "client.ReadArtifactAsync", "core-v3.typed-facade-unavailable",
        "core-v3.stage-control-unavailable", "question-answer-artifact-unavailable",
    ):
        require(anchor in gateway, "gateway.behavior-anchor:" + anchor)

    contracts = (project / "src/KnowledgeClipper.Desktop.Application/ApplicationContracts.cs").read_text(encoding="utf-8")
    use_cases = (project / "src/KnowledgeClipper.Desktop.Application/UseCases.cs").read_text(encoding="utf-8")
    require("MakeCardsSubmission(string TaskId, string CardStructureId, ArtifactHandle Answer)" in contracts, "gateway.answer-handle-contract")
    require("new MakeCardsSubmission(current.TaskId, cardStructureId, current.Answer)" in use_cases, "gateway.answer-handle-flow")

    release = project / "core-v3/releases/20260902T060054Z-foundation.2"
    packet = release / "typed-sdk-packet.json"
    client = release / "artifacts/net8.0/KnowledgeClipper.Core.V3.Client.dll"
    runtime_contracts = release / "artifacts/net8.0/KnowledgeClipper.Core.V3.Contracts.dll"
    require(sha256_file(packet) == "88076433AF2E4BC2C2A3FE3A5F96E22CD1461A71DAA7A58B0081D7E914A6A425", "gateway.packet-identity")
    require(sha256_file(client) == "7C191017324F6464D0A8328CF5506F1A6960BC0148B25FAC4F5BD1D3B20F5D8B", "gateway.client-identity")
    require(sha256_file(runtime_contracts) == "831DBE54E8202A513F94E8ECCB419D8EB5847E587F981DB30DE585FA74E78DFC", "gateway.contracts-identity")
    catalog = load_json(release / "snapshot/contracts/core/v3/capabilities/capability-catalog.v1.json")
    unavailable = sorted(item["operationId"] for item in catalog["operations"] if not item["available"])
    require(unavailable == ["anki.probe", "question.follow-up", "question.initial", "question.make-cards"], "gateway.foundation-unavailable-catalog")

    project_paths = [
        project / "desktop/RemNoteQuickClipper.Desktop.csproj",
        project / "desktop/RemNoteQuickClipper.Desktop.WindowsCanary.csproj",
        project / "tests/KnowledgeClipper.Desktop.CoreGateway.Tests/KnowledgeClipper.Desktop.CoreGateway.Tests.csproj",
        project / "tests/QualityV3.Foundation/DesktopGatewayConformance.Tests/QualityV3.DesktopGatewayConformance.Tests.csproj",
    ]
    release_fragment = "core-v3\\releases\\20260902T060054Z-foundation.2"
    for path in project_paths:
        text = path.read_text(encoding="utf-8")
        lowered = text.lower()
        require("<projectreference" not in lowered and "core-v3\\src" not in lowered and "bin\\release" not in lowered, "gateway.mutable-core-reference:" + path.name)
        require(release_fragment.lower() in lowered, "gateway.release-reference-missing:" + path.name)
        require("<reference include=\"knowledgeclipper.core.v3.client\"" in lowered, "gateway.client-reference-missing:" + path.name)
        require("<reference include=\"knowledgeclipper.core.v3.contracts\"" not in lowered, "gateway.contracts-public-reference:" + path.name)
        require("<content include=" in lowered and "knowledgeclipper.core.v3.contracts.dll" in lowered, "gateway.contracts-runtime-copy-missing:" + path.name)

    production_sources = [project / "src/Program.cs", project / "src/DesktopUi.cs", project / "src/ProjectSettingsForm.cs"]
    require(all("new NamedPipeCoreGateway" not in path.read_text(encoding="utf-8") for path in production_sources), "gateway.production-view-switched")
    require("LegacyDesktopAdapter.CreateBoundClipperDependencies" in (project / "src/Program.cs").read_text(encoding="utf-8"), "gateway.production-legacy-wiring")

    logs = {
        "sdk": run / "dotnet-info.log",
        "applicationBuild": run / "application-tests-build.log",
        "applicationRun": run / "application-tests-run.log",
        "ownerGatewayBuild": run / "gateway-tests-build.log",
        "ownerGatewayRun": run / "gateway-tests-run.log",
        "qualityGatewayBuild": run / "quality-gateway-tests-build.log",
        "qualityGatewayRun": run / "quality-gateway-tests-run.log",
        "productionBuild": run / "production-isolated-build.log",
        "canaryBuild": run / "windows-canary-isolated-build.log",
    }
    require(all(path.is_file() for path in logs.values()), "gateway.evidence-log-missing")
    for key in ("applicationBuild", "ownerGatewayBuild", "qualityGatewayBuild", "productionBuild", "canaryBuild"):
        require_successful_build(logs[key], "gateway." + key)
    application_run = logs["applicationRun"].read_text(encoding="utf-8")
    owner_run = logs["ownerGatewayRun"].read_text(encoding="utf-8")
    quality_run = logs["qualityGatewayRun"].read_text(encoding="utf-8")
    require(len(re.findall(r"(?m)^PASS ", application_run)) == 22 and "DESKTOP_V3_APPLICATION_TESTS PASS assertions=22" in application_run, "gateway.application-tests")
    require(len(re.findall(r"(?m)^PASS ", owner_run)) == 15 and "DESKTOP_CORE_GATEWAY_TESTS PASS assertions=15" in owner_run, "gateway.owner-tests")
    require(len(re.findall(r"(?m)^PASS ", quality_run)) == 13 and "QUALITY_DESKTOP_GATEWAY_CONFORMANCE PASS assertions=13" in quality_run, "gateway.quality-tests")
    sdk_info = logs["sdk"].read_text(encoding="utf-8")
    require("8.0.424" in sdk_info and "RemNoteQuickClipperRuntime" in sdk_info, "gateway.sdk-identity")

    production_outputs = sorted((run / "production-isolated-output").rglob("KnowledgeClipper.dll"))
    canary_outputs = sorted((run / "windows-canary-isolated-output").rglob("KnowledgeClipper.Desktop.WindowsCanary.dll"))
    require(len(production_outputs) == 1 and len(canary_outputs) == 1, "gateway.isolated-build-output")
    require(all(path.resolve().is_relative_to(run) for path in production_outputs + canary_outputs), "gateway.isolated-build-boundary")

    foundation_receipt_path = project / "canary/quality-v3-core-foundation2-conformance/20260902T060911Z/core-v3-foundation2-conformance.receipt.json"
    foundation_checkpoint_path = project / "receipts/quality-v3-core-foundation2-conformance/20260902T060957Z/quality-core-foundation2-conformance-checkpoint.json"
    require(sha256_file(foundation_receipt_path) == "1891510109BFD982BFA30A727D7C25312DC1BA08CE1F0E01735D459FCAD93AAF", "gateway.foundation-receipt")
    require(sha256_file(foundation_checkpoint_path) == "96BEBE7D0846CB27F3E4915C331E14BE55A2ADAA264A2ADF898C18326EB598EC", "gateway.foundation-checkpoint")
    foundation_receipt = load_json(foundation_receipt_path)
    require(foundation_receipt["status"] == "PASS-FOUNDATION2-CONFORMANCE" and foundation_receipt["foundationBoundary"]["verticalSliceCompleted"] is False, "gateway.foundation-boundary")
    require(foundation_receipt["consumerGates"]["contractsExportedTypeCount"] == 0 and foundation_receipt["consumerGates"]["clientPublicSignatureContractLeaks"] == 0, "gateway.public-surface-proof")

    quality_files = [
        project / "tests/QualityV3.Foundation/desktop_gateway_conformance.py",
        project / "tests/QualityV3.Foundation/DesktopGatewayConformance.Tests/QualityV3.DesktopGatewayConformance.Tests.csproj",
        project / "tests/QualityV3.Foundation/DesktopGatewayConformance.Tests/Program.cs",
    ]
    iteration_history = run / "verifier-iteration-history.md"
    require(iteration_history.is_file(), "gateway.verifier-iteration-history")
    prior_receipt_paths = [
        project / "canary/quality-v3-desktop-gateway-conformance/20260902T070923Z/desktop-v3-core-gateway-cross-conformance.receipt.json",
        project / "canary/quality-v3-desktop-gateway-conformance/20260902T071247Z/desktop-v3-core-gateway-cross-conformance.receipt.json",
    ]
    prior_checkpoint_paths = [
        project / "receipts/quality-v3-desktop-gateway-conformance/20260902T070945Z/quality-desktop-gateway-cross-conformance-checkpoint.json",
        project / "receipts/quality-v3-desktop-gateway-conformance/20260902T071306Z/quality-desktop-gateway-cross-conformance-checkpoint.json",
    ]
    expected_prior_hashes = {
        prior_receipt_paths[0]: "8CA2E475D2A2E64FD0486A812A3398FF40B3100C4F7F393AF4A062899BC754A6",
        prior_receipt_paths[1]: "7CE0E04259FCE3149C07B6683A22BD6277BA692253CF35CCE470670779664449",
        prior_checkpoint_paths[0]: "5C98866591C185ED2F712812CF2E5E1DFADB6569550C5B58A6BA72C9A0B33A85",
        prior_checkpoint_paths[1]: "8F14A3DB7EBF4DDFC7204ED12C064F02DC561FAAF932D5700AA9ED4463AEAF26",
    }
    require(all(sha256_file(path) == expected for path, expected in expected_prior_hashes.items()), "gateway.prior-quality-evidence-drift")
    result = {
        "schemaVersion": "quality-v3-desktop-gateway-cross-conformance.v2",
        "createdAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-GATEWAY-CROSS-CONFORMANCE",
        "gateAuthority": "SUPERSEDING",
        "delta": {
            "fileCount": len(delta_refs), "bytes": sum(item["bytes"] for item in delta_refs),
            "algorithm": declared_snapshot["algorithm"],
            "treeSha256": independently_calculated_tree, "files": delta_refs,
            "manifest": file_ref(project, manifest_path),
            "officialScript": file_ref(project, project / "tools/Get-DesktopSourceSnapshot.ps1"),
            "officialRecomputation": file_ref(project, official_snapshot_path),
            "manifestSelfBinding": "PASS", "independentRecomputation": "PASS",
        },
        "supersedes": {
            "status": "evidence-drift-superseded",
            "declaredTreeSha256": "4420B555F7588620B0A990E6BEE677B2291E57735C356BCB9A0ED2BB2D6965BA",
            "reason": manifest["supersedes"]["reason"],
            "priorQualityReceipts": [file_ref(project, path) for path in prior_receipt_paths],
            "priorQualityCheckpoints": [file_ref(project, path) for path in prior_checkpoint_paths],
        },
        "foundation2": {
            "status": foundation_receipt["status"], "verticalSliceCompleted": False,
            "conformanceReceipt": file_ref(project, foundation_receipt_path),
            "conformanceCheckpoint": file_ref(project, foundation_checkpoint_path),
            "packet": file_ref(project, packet), "clientAssembly": file_ref(project, client),
            "contractsRuntimeAssembly": file_ref(project, runtime_contracts),
        },
        "gatewayBoundary": {
            "implementationCount": 1, "implementation": file_ref(project, gateway_path),
            "typedClientFacadeOnly": True, "protocolDtoLeaks": 0, "legacyFallbackPaths": 0,
            "contractsCompileReference": False, "contractsPrivateRuntimeCopy": True,
            "unavailableOperations": unavailable,
        },
        "tests": {
            "application": {"status": "PASS", "assertions": 22, "build": file_ref(project, logs["applicationBuild"]), "run": file_ref(project, logs["applicationRun"])},
            "ownerGateway": {"status": "PASS", "assertions": 15, "build": file_ref(project, logs["ownerGatewayBuild"]), "run": file_ref(project, logs["ownerGatewayRun"])},
            "qualityIndependentGateway": {"status": "PASS", "assertions": 13, "build": file_ref(project, logs["qualityGatewayBuild"]), "run": file_ref(project, logs["qualityGatewayRun"])},
        },
        "builds": {
            "production": {"status": "PASS", "log": file_ref(project, logs["productionBuild"]), "assembly": file_ref(project, production_outputs[0])},
            "windowsCanary": {"status": "PASS", "log": file_ref(project, logs["canaryBuild"]), "assembly": file_ref(project, canary_outputs[0])},
            "sdk": file_ref(project, logs["sdk"]),
        },
        "productionBoundary": {
            "viewProductionWiring": "NOT-SWITCHED", "currentAdapter": "LegacyDesktopAdapter",
            "gatewayCompiledButNotInstantiated": True, "featureReadiness": "NO-GO",
            "nextGate": "Core Anki/question vertical slice",
        },
        "qualityVerifier": {"files": [file_ref(project, path) for path in quality_files]},
        "verifierIterationHistory": file_ref(project, iteration_history),
        "externalServicesInvoked": False,
        "candidatePromotion": "NO-GO",
    }
    output.mkdir(parents=True)
    receipt = output / "desktop-v3-core-gateway-cross-conformance.receipt.json"
    receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "status": result["status"], "receipt": receipt.relative_to(project).as_posix(),
        "receiptSha256": sha256_file(receipt), "viewProductionWiring": "NOT-SWITCHED",
        "featureReadiness": "NO-GO", "candidatePromotion": "NO-GO",
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
