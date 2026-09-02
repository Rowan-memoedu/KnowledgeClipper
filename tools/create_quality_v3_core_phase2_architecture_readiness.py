from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


class ReadinessError(RuntimeError):
    pass


def require(condition: bool, code: str) -> None:
    if not condition:
        raise ReadinessError(code)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def file_ref(project: Path, path: Path) -> dict:
    return {"path": path.relative_to(project).as_posix(), "bytes": path.stat().st_size, "sha256": sha256_file(path)}


def main() -> int:
    parser = argparse.ArgumentParser(description="Record compile-only readiness for the independent Core Phase 2 architecture gates")
    parser.add_argument("--project-root", type=Path, required=True)
    parser.add_argument("--compile-log", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()
    project = args.project_root.resolve()
    compile_log = args.compile_log.resolve()
    output = args.output_root.resolve()
    allowed = (project / "receipts/quality-v3-core-phase2-architecture-readiness").resolve()
    require(output.is_relative_to(allowed) and not output.exists(), "phase2-readiness.output-not-append-only")
    log = compile_log.read_text(encoding="utf-8")
    require(("已成功生成" in log or "Build succeeded" in log) and ("0 个错误" in log or "0 Error(s)" in log), "phase2-readiness.compile")

    test_project = project / "tests/QualityV3.Foundation/CorePhase2Architecture.Tests/QualityV3.CorePhase2Architecture.Tests.csproj"
    test_source = project / "tests/QualityV3.Foundation/CorePhase2Architecture.Tests/Program.cs"
    readme = project / "tests/QualityV3.Foundation/CorePhase2Architecture.Tests/README.md"
    runner = project / "tests/QualityV3.Foundation/run_core_phase2_architecture_gate.py"
    generator = project / "tools/create_quality_v3_core_phase2_architecture_readiness.py"
    source = test_source.read_text(encoding="utf-8")
    gate_names = [
        "production-host-excludes-authority-bound-fake",
        "acceptance-is-decoupled-from-slow-worker-and-reconnect-observes",
        "artifact-identity-and-replay-side-effects-are-stable",
        "crash-after-session-claim-recovers-terminal-and-session",
        "public-output-roles-and-exactly-one-kind-cardinality",
    ]
    require(all(name in source for name in gate_names), "phase2-readiness.gate-definition")
    require("Phase2SnapshotRoot must point to an immutable Core v3 release snapshot" in test_project.read_text(encoding="utf-8"), "phase2-readiness.immutable-input-contract")
    runner_source = runner.read_text(encoding="utf-8")
    require("verify_release" in runner_source and "allFilesReadOnly" in runner_source and "ownerReceiptTrusted" in runner_source, "phase2-readiness.runner-boundary")

    result = {
        "schemaVersion": "quality-v3-core-phase2-architecture-readiness.v1",
        "createdAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "READY-AWAITING-PHASE2-PACKET",
        "gateCount": 5,
        "gates": gate_names,
        "compilePreflight": {"status": "PASS", "mutableSourceCompatibilityOnly": True, "log": file_ref(project, compile_log)},
        "formalExecution": {"executed": False},
        "formalPassGranted": False,
        "requiredInput": "immutable Core Phase 2 release packet with source snapshot and authority-bound capability catalog",
        "qualityFiles": [file_ref(project, path) for path in (
            test_project,
            test_source,
            readme,
            project / "tests/QualityV3.Foundation/CorePhase2OutputRoles.Consumer/QualityV3.CorePhase2OutputRoles.Consumer.csproj",
            project / "tests/QualityV3.Foundation/CorePhase2OutputRoles.Consumer/Program.cs",
            project / "tests/QualityV3.Foundation/CorePhase2OutputRoles.ForbiddenConsumer/QualityV3.CorePhase2OutputRoles.ForbiddenConsumer.csproj",
            project / "tests/QualityV3.Foundation/CorePhase2OutputRoles.ForbiddenConsumer/Program.cs",
            runner,
            generator,
        )],
        "ownerReceiptTrusted": False,
        "productFilesModifiedByQuality": False,
        "externalServicesInvoked": False,
        "candidatePromotion": "NO-GO",
    }
    output.mkdir(parents=True)
    receipt = output / "quality-core-phase2-architecture-readiness.receipt.json"
    receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "status": result["status"], "receipt": receipt.relative_to(project).as_posix(),
        "receiptSha256": sha256_file(receipt), "gateCount": 5,
        "formalPassGranted": False, "candidatePromotion": "NO-GO",
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
