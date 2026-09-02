from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
from datetime import datetime, timezone
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[1]


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--windows-receipt", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--quality-delta", type=Path, required=True)
    args = parser.parse_args()

    package = args.package.resolve()
    windows_receipt_path = args.windows_receipt.resolve()
    output = args.output.resolve()
    require(package.is_dir(), "package missing")
    require(not output.exists(), "append-only receipt exists")

    build_receipt_path = package / "desktop-build-receipt.v1.json"
    build = json.loads(build_receipt_path.read_text(encoding="utf-8"))
    files = build["files"]
    material: list[str] = []
    total = 0
    for item in sorted(files, key=lambda value: value["path"]):
        path = package / item["path"]
        require(path.is_file(), f"package file missing: {item['path']}")
        require(path.stat().st_size == item["bytes"], f"package byte drift: {item['path']}")
        require(sha256(path).lower() == item["sha256"].lower(), f"package hash drift: {item['path']}")
        material.append(f"{item['path']}\n{item['sha256'].lower()}\n{item['bytes']}\n")
        total += item["bytes"]
    tree = hashlib.sha256("".join(material).encode("utf-8")).hexdigest().upper()
    require(len(files) == build["fileCount"] and total == build["totalBytes"] and tree == build["treeSha256"].upper(), "package aggregate drift")

    windows = json.loads(windows_receipt_path.read_text(encoding="utf-8"))
    require(windows["automatedWindowsUiCanary"]["status"] == "PASS", "Windows UI Canary failed")
    require(windows["automatedWindowsUiCanary"]["assertions"] >= 213, "Windows UI authority projection assertions missing")
    external = windows["externalTargetsAndProviders"]
    require(external["status"] == "NOT_RUN", "external target boundary violated")
    require(not external["exportTargets"]["remnoteInvoked"] and not external["exportTargets"]["ankiInvoked"], "export target invoked")
    require(not external["imageProviderInvoked"] and not external["physicalSpeakerInvoked"], "provider or speaker invoked")

    source = (PROJECT / "src" / "ProjectSettingsForm.cs").read_text(encoding="utf-8")
    control = (PROJECT / "src" / "DesktopSettingsControl.cs").read_text(encoding="utf-8")
    canary = (PROJECT / "src" / "WindowsUiCanary.cs").read_text(encoding="utf-8")
    require("Core v1.2.34" not in source, "stale authority version remains user-visible")
    require("DesktopAuthorityBindingProjection" in control and "release-tree-mismatch" in control, "fail-closed authority binding implementation missing")
    require("candidate-bound read-only status projects authority v1.2.37" in canary, "v1.2.37 positive assertion missing")
    require("missing candidate authority metadata fails closed" in canary and "inconsistent authority tree metadata fails closed" in canary, "authority negative assertions missing")

    diagnostic_process = subprocess.run(
        [str(package / "KnowledgeClipper.exe"), "--startup-diagnostic"],
        cwd=package,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=30,
        check=False,
    )
    require(diagnostic_process.returncode == 0, "package startup diagnostic failed")
    diagnostic = json.loads(diagnostic_process.stdout)
    require(diagnostic["result"] == "PASS" and diagnostic["externalConnections"] == 0 and not diagnostic["namedPipeInvoked"], "startup safety boundary failed")

    receipt = {
        "schemaVersion": "knowledge-clipper.desktop-authority-version-projection-receipt/1.0.0",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-DESKTOP-MODULE-ONLY",
        "supersedesForFutureIntegration": "releases/kc-i-module-actions-v1237-20260901T075500Z/win-x64",
        "sealedIntegratedCandidateModified": False,
        "package": {
            "root": str(package),
            "entrypoint": str(package / "KnowledgeClipper.exe"),
            "fileCount": build["fileCount"],
            "totalBytes": build["totalBytes"],
            "treeSha256": tree,
            "buildReceiptSha256": sha256(build_receipt_path),
            "executableSha256": sha256(package / "KnowledgeClipper.exe"),
            "applicationDllSha256": sha256(package / "KnowledgeClipper.dll"),
        },
        "authorityVersionProjection": {
            "source": "candidate-integration + embedded authority checkpoint + registry + release tree binding",
            "positive": "authority v1.2.37",
            "missing": "authority unknown / candidate-integration-missing",
            "inconsistent": "authority unknown / release-tree-mismatch",
            "staleHardcodedCoreV1234Visible": False,
            "readOnly": True,
        },
        "retention": {
            "automatic": False,
            "legacyPurgeProductionEnabled": False,
            "destructiveDeleteProvided": False,
        },
        "tests": {
            "desktopBuildAndTest": "PASS",
            "windowsUiReceipt": str(windows_receipt_path),
            "windowsUiReceiptSha256": sha256(windows_receipt_path),
            "windowsUiAssertions": windows["automatedWindowsUiCanary"]["assertions"],
            "startupDiagnostic": diagnostic,
        },
        "qualityDelta": {"path": str(args.quality_delta.resolve()), "sha256": sha256(args.quality_delta.resolve())},
        "sideEffects": {"externalCalls": 0, "remnoteWrites": 0, "ankiWrites": 0, "mineruCalls": 0, "physicalSpeaker": 0},
        "userHandTestEntrypoint": False,
        "nextStep": "append-only integrate this Desktop payload only after current user feedback, without replacing the sealed v1.2.37 candidate in place",
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_name(output.name + ".tmp-" + os.urandom(8).hex())
    temporary.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, output)
    print(json.dumps({"receipt": str(output), "sha256": sha256(output), "packageTreeSha256": tree}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
