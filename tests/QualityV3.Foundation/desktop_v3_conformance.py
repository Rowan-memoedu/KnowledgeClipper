from __future__ import annotations

import argparse
import hashlib
import json
import re
from datetime import datetime, timezone
from pathlib import Path


INPUT_FILES = [
    "src/KnowledgeClipper.Desktop.Application/ApplicationContracts.cs",
    "src/KnowledgeClipper.Desktop.Application/ApplicationPorts.cs",
    "src/KnowledgeClipper.Desktop.Application/CaptureSession.cs",
    "src/KnowledgeClipper.Desktop.Application/CommandBinding.cs",
    "src/KnowledgeClipper.Desktop.Application/DesktopCompositionRoot.cs",
    "src/KnowledgeClipper.Desktop.Application/DesktopStateStore.cs",
    "src/KnowledgeClipper.Desktop.Application/QuestionObserver.cs",
    "src/KnowledgeClipper.Desktop.Application/README.md",
    "src/KnowledgeClipper.Desktop.Application/UseCases.cs",
    "src/LegacyDesktopAdapter.cs",
    "src/LoopbackProviderOverrides.cs",
    "tests/KnowledgeClipper.Desktop.Application.Tests/KnowledgeClipper.Desktop.Application.Tests.csproj",
    "tests/KnowledgeClipper.Desktop.Application.Tests/Program.cs",
]


class ConformanceError(RuntimeError):
    pass


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def inventory(project: Path) -> tuple[list[dict], str, int]:
    records = []
    for relative in INPUT_FILES:
        path = project / relative
        if not path.is_file():
            raise ConformanceError("desktop.input-missing:" + relative)
        records.append({"path": relative, "bytes": path.stat().st_size, "sha256": sha256_file(path)})
    records.sort(key=lambda item: item["path"])
    rows = [f"{item['path']}\t{item['bytes']}\t{item['sha256'].lower()}" for item in records]
    tree = hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper()
    return records, tree, sum(item["bytes"] for item in records)


def require(condition: bool, code: str) -> None:
    if not condition:
        raise ConformanceError(code)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def type_block(source: str, type_name: str) -> str:
    match = re.search(rf"\bclass\s+{re.escape(type_name)}\b[^{{]*{{", source)
    if not match:
        raise ConformanceError("desktop.type-missing:" + type_name)
    start = match.start()
    depth = 0
    for index in range(match.end() - 1, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[start:index + 1]
    raise ConformanceError("desktop.type-unbalanced:" + type_name)


def parse_test_log(project: Path, path: Path, marker: str, expected_assertions: int | None = None) -> dict:
    require(path.is_file(), "desktop.test-log-missing:" + path.name)
    content = read(path)
    matches = re.findall(re.escape(marker) + r" PASS assertions=(\d+)", content)
    require(len(matches) == 1, "desktop.test-log-terminal-marker:" + path.name)
    assertions = int(matches[0])
    if expected_assertions is not None:
        require(assertions == expected_assertions, "desktop.test-assertion-count:" + path.name)
    require(" FAIL " not in content, "desktop.test-log-failure:" + path.name)
    return {"path": path.relative_to(project).as_posix(), "bytes": path.stat().st_size, "sha256": sha256_file(path), "assertions": assertions}


def validate_ui_receipt(project: Path, receipt_path: Path, expected_sha: str) -> dict:
    require(receipt_path.is_file(), "desktop.ui-receipt-missing")
    actual_sha = sha256_file(receipt_path)
    require(actual_sha == expected_sha.upper(), "desktop.ui-receipt-sha")
    receipt = json.loads(read(receipt_path))
    referenced = []
    for item in (receipt["automatedWindowsUiCanary"], receipt["realWindowsCanaries"]["focusedTextBoxCtrlC"]):
        log = receipt_path.parent / item["log"]
        require(log.is_file(), "desktop.ui-log-missing:" + item["log"])
        actual = sha256_file(log)
        require(actual == item["logSha256"].upper(), "desktop.ui-log-sha:" + item["log"])
        referenced.append({"path": log.relative_to(project).as_posix(), "bytes": log.stat().st_size, "sha256": actual})
    physical = receipt["realWindowsCanaries"]
    unresolved = {name: value["status"] for name, value in physical.items() if value["status"] != "PASS"}
    return {
        "path": receipt_path.relative_to(project).as_posix(),
        "bytes": receipt_path.stat().st_size,
        "sha256": actual_sha,
        "referencedLogs": referenced,
        "automatedAssertionsReported": receipt["automatedWindowsUiCanary"]["assertions"],
        "unresolvedPhysicalChecks": unresolved,
    }


def validate_static_boundaries(project: Path) -> dict:
    application_root = project / "src/KnowledgeClipper.Desktop.Application"
    application_sources = {path.name: read(path) for path in sorted(application_root.glob("*.cs"))}
    combined = "\n".join(application_sources.values())
    standalone_forbidden = re.compile(r"\b(operation|schema|subject|idempotency|protocol|worker|authority)\b", re.IGNORECASE)
    identity_forbidden = re.compile(r"\b(CoreV3Client|RunV7|SchemaVersion|WorkerId|IdempotencyKey|ProtocolVersion|IpcVersion|ManifestPath|RawPath|ArtifactPath|FilePath)\b", re.IGNORECASE)
    require(standalone_forbidden.search(combined) is None, "desktop.application-transport-vocabulary")
    require(identity_forbidden.search(combined) is None, "desktop.application-transport-identity")
    require("System.IO" not in combined and "File." not in combined and "Path." not in combined, "desktop.application-raw-file-access")

    contracts = application_sources["ApplicationContracts.cs"]
    dto_parameters = re.findall(r"record\s+\w+\s*\(([^)]*)\)", contracts, flags=re.DOTALL)
    dto_text = "\n".join(dto_parameters)
    require(standalone_forbidden.search(dto_text) is None, "desktop.dto-transport-vocabulary")
    require(identity_forbidden.search(dto_text) is None, "desktop.dto-transport-identity")
    require(re.search(r"\b(?:Raw|Artifact|File|Manifest)?Path\b", dto_text, re.IGNORECASE) is None, "desktop.dto-raw-artifact-path")

    observer = application_sources["QuestionObserver.cs"]
    for anchor in (
        'QuestionReducers.Cancelled(value, "question-observation-cancelled")',
        'QuestionReducers.Failed(value, "question-observation-" + exception.GetType().Name)',
        'QuestionReducers.Failed(value, "question-answer-" + exception.GetType().Name)',
        'QuestionReducers.Failed(value, "question-observation-timeout")',
    ):
        require(anchor in observer, "desktop.observer-terminal-anchor")

    binding = application_sources["CommandBinding.cs"]
    composition = application_sources["DesktopCompositionRoot.cs"]
    require("enum InvocationSource { Pointer, Keyboard, UiAutomation }" in binding, "desktop.invocation-source-set")
    require("UnifiedApplicationCommandBinding" in binding, "desktop.unified-binding-missing")
    require("Commands = new UnifiedApplicationCommandBinding(new ApplicationCommandDispatcher(handlers));" in composition, "desktop.composition-binding")

    use_cases = application_sources["UseCases.cs"]
    require("Interlocked.CompareExchange(ref commandInFlight, 1, 0)" in use_cases, "desktop.question-concurrency-interlock")
    require('"question-command-in-progress"' in use_cases, "desktop.question-concurrency-notice")
    require("finally { Interlocked.Exchange(ref commandInFlight, 0); }" in use_cases, "desktop.question-concurrency-release")

    desktop_ui = type_block(read(project / "src/DesktopUi.cs"), "BoundClipperForm")
    settings_ui = type_block(read(project / "src/ProjectSettingsForm.cs"), "SettingsForm")
    constructor_pattern = re.compile(r"new\s+[A-Za-z0-9_.]*(?:Transport|Service|ArtifactReader)\b")
    require(constructor_pattern.search(desktop_ui) is None, "desktop.bound-form-constructs-infrastructure")
    require("UserSettings.Load(config)" not in desktop_ui and "CreateProductionController" not in desktop_ui and "NamedPipe" not in desktop_ui, "desktop.bound-form-owns-composition")
    require(constructor_pattern.search(settings_ui) is None, "desktop.settings-form-constructs-infrastructure")
    require("DesktopCredentialSettings.Load(" not in settings_ui and "DesktopModuleActionHost.Load(" not in settings_ui and "NamedPipe" not in settings_ui, "desktop.settings-form-owns-composition")

    legacy = read(project / "src/LegacyDesktopAdapter.cs")
    typed_gateway_present = re.search(r"\bclass\s+NamedPipeCoreGateway\b", combined + "\n" + legacy) is not None
    generic_client_present = "new NamedPipeCommonIpcClient" in legacy
    require(not typed_gateway_present, "desktop.gateway-unexpected-before-sdk")
    require(generic_client_present, "desktop.legacy-generic-client-observation")
    require("CoreV3Client" not in combined + "\n" + legacy and "RunV7" not in combined + "\n" + legacy, "desktop.generic-client-substitution")

    return {
        "applicationBoundary": "PASS",
        "observerTerminalAnchors": "PASS",
        "unifiedCommandBinding": "PASS",
        "duplicateCommandInterlock": "PASS",
        "formsReceiveInfrastructureDependencies": "PASS",
        "typedSdkAvailable": False,
        "namedPipeCoreGatewayPresent": False,
        "legacyGenericClientObserved": True,
        "gatewayStatus": "AWAITING_INPUT",
        "gatewayReason": "Core typed SDK is absent; legacy NamedPipeCommonIpcClient is observed but is not accepted as NamedPipeCoreGateway evidence.",
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Independent Desktop v3 Phase 1 conformance gate")
    parser.add_argument("--project-root", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    parser.add_argument("--expected-tree-sha256", required=True)
    parser.add_argument("--expected-input-bytes", type=int, required=True)
    parser.add_argument("--ui-receipt", type=Path, required=True)
    parser.add_argument("--expected-ui-receipt-sha256", required=True)
    parser.add_argument("--application-test-log", type=Path, required=True)
    parser.add_argument("--quality-test-log", type=Path, required=True)
    parser.add_argument("--sdk-info-log", type=Path, required=True)
    args = parser.parse_args()

    project = args.project_root.resolve()
    output = args.output_root.resolve()
    allowed = (project / "canary/quality-v3-desktop-conformance").resolve()
    require(output.is_relative_to(allowed) and not output.exists(), "desktop.output-not-append-only")

    records, tree, total_bytes = inventory(project)
    require(len(records) == 13, "desktop.input-count")
    require(total_bytes == args.expected_input_bytes, "desktop.input-bytes")
    require(tree == args.expected_tree_sha256.upper(), "desktop.input-tree")
    static = validate_static_boundaries(project)
    ui = validate_ui_receipt(project, args.ui_receipt.resolve(), args.expected_ui_receipt_sha256)
    application_test = parse_test_log(project, args.application_test_log.resolve(), "DESKTOP_V3_APPLICATION_TESTS", 22)
    quality_test = parse_test_log(project, args.quality_test_log.resolve(), "QUALITY_V3_DESKTOP_CONFORMANCE_TESTS")
    sdk_log = args.sdk_info_log.resolve()
    require(sdk_log.is_file(), "desktop.sdk-log-missing")
    sdk_text = read(sdk_log)
    require("8.0.424" in sdk_text and "D:\\Applications\\RemNoteQuickClipperRuntime\\dotnet-sdk-8.0.424" in sdk_text, "desktop.sdk-identity")
    sdk_evidence = {"path": sdk_log.relative_to(project).as_posix(), "bytes": sdk_log.stat().st_size, "sha256": sha256_file(sdk_log), "version": "8.0.424"}

    verifier_files = []
    for relative in (
        "tests/QualityV3.Foundation/desktop_v3_conformance.py",
        "tests/QualityV3.Foundation/DesktopConformance.Tests/QualityV3.DesktopConformance.Tests.csproj",
        "tests/QualityV3.Foundation/DesktopConformance.Tests/Program.cs",
    ):
        path = project / relative
        verifier_files.append({"path": relative, "bytes": path.stat().st_size, "sha256": sha256_file(path)})

    result = {
        "schemaVersion": "quality-v3-desktop-conformance.v1",
        "createdAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-PHASE-1-CONFORMANCE",
        "sourceInput": {
            "fileCount": len(records),
            "bytes": total_bytes,
            "treeAlgorithm": "sha256 utf8 ordinal(path TAB bytes TAB lowercase-sha256) joined with LF, no trailing LF",
            "treeSha256": tree,
            "files": records,
        },
        "checks": static,
        "tests": {"sdk": sdk_evidence, "applicationSuite": application_test, "independentQualitySuite": quality_test},
        "qualityVerifier": {"files": verifier_files},
        "windowsUiEvidence": ui,
        "externalServicesInvoked": False,
        "productSourceModifiedByQuality": False,
        "candidatePromotion": {
            "status": "NO-GO",
            "reasons": [
                "Core typed SDK and real NamedPipeCoreGateway are absent (AWAITING_INPUT).",
                "Windows UI evidence retains physical-system PENDING/PARTIAL checks.",
                "No canonical final candidate was assembled or exercised in this phase.",
            ],
        },
    }
    output.mkdir(parents=True)
    receipt = output / "desktop-v3-conformance.receipt.json"
    receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": result["status"], "receipt": receipt.relative_to(project).as_posix(), "receiptSha256": sha256_file(receipt), "candidatePromotion": "NO-GO"}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
