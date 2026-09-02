from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import subprocess
import sys
from pathlib import Path


WORKER_ROOT = Path(__file__).resolve().parent
CANDIDATE = WORKER_ROOT / "KnowledgeClipper.ImageOcr.Worker.exe"
LEGACY_COMPATIBILITY_ALIAS = WORKER_ROOT / "RemNoteQuickClipper.ImageOcr.Worker.exe"
FORBIDDEN_DESTINATIONS = ("remnote", "anki")


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def describe(path: Path) -> dict[str, object]:
    completed = subprocess.run(
        [str(path), "describe", "--json"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=15,
        check=False,
    )
    if completed.returncode != 0 or completed.stderr or len(completed.stdout.splitlines()) != 1:
        raise RuntimeError(f"describe failed for {path.name}")
    value = json.loads(completed.stdout)
    if not isinstance(value, dict):
        raise RuntimeError(f"describe did not return an object for {path.name}")
    return value


def forbidden_matches(value: object) -> list[str]:
    serialized = json.dumps(value, ensure_ascii=False, sort_keys=True).lower()
    return [token for token in FORBIDDEN_DESTINATIONS if token in serialized]


def main() -> int:
    parser = argparse.ArgumentParser(description="Audit KnowledgeClipper image/OCR public identity")
    parser.add_argument("--output", type=Path, help="Optional create-once JSON report path")
    args = parser.parse_args()
    if not CANDIDATE.is_file():
        raise RuntimeError("KnowledgeClipper image/OCR candidate is missing")
    candidate_descriptor = describe(CANDIDATE)
    descriptor_matches = forbidden_matches(candidate_descriptor)
    source_text = (WORKER_ROOT / "image_ocr_worker.py").read_text(encoding="utf-8").lower()
    live_canary_text = (WORKER_ROOT / "run-live-canary.ps1").read_text(encoding="utf-8").lower()
    offline_canary_text = (WORKER_ROOT / "run-offline-canary.py").read_text(encoding="utf-8").lower()
    source_matches = [token for token in FORBIDDEN_DESTINATIONS if token in source_text]
    live_canary_matches = [token for token in FORBIDDEN_DESTINATIONS if token in live_canary_text]
    offline_canary_matches = [token for token in FORBIDDEN_DESTINATIONS if token in offline_canary_text]
    legacy_descriptor = describe(LEGACY_COMPATIBILITY_ALIAS) if LEGACY_COMPATIBILITY_ALIAS.is_file() else None
    checks = {
        "candidateFileName": CANDIDATE.name == "KnowledgeClipper.ImageOcr.Worker.exe",
        "candidateFileNameDestinationNeutral": not forbidden_matches(CANDIDATE.name),
        "descriptorWorkerIdStable": candidate_descriptor.get("workerId") == "image.ocr.mineru",
        "descriptorWorkerVersion": candidate_descriptor.get("workerVersion") == "1.1.0",
        "descriptorDestinationNeutral": not descriptor_matches,
        "sourceDiagnosticsDestinationNeutral": not source_matches,
        "liveCanaryDestinationNeutral": not live_canary_matches,
        "offlineCanaryDestinationNeutral": not offline_canary_matches,
        "legacyAliasExplicitAndSeparate": (
            legacy_descriptor is not None
            and LEGACY_COMPATIBILITY_ALIAS != CANDIDATE
            and legacy_descriptor.get("workerId") == "image.ocr.mineru"
        ),
    }
    passed = all(checks.values())
    report = {
        "schemaVersion": "knowledge-clipper-image-ocr-public-identity-audit.v1",
        "createdAt": dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "passed" if passed else "failed",
        "productDisplayName": "零碎知识剪藏",
        "assemblyRoot": "KnowledgeClipper",
        "candidate": {
            "path": "workers/image-ocr/KnowledgeClipper.ImageOcr.Worker.exe",
            "bytes": CANDIDATE.stat().st_size,
            "sha256": sha256_file(CANDIDATE),
            "descriptor": candidate_descriptor,
            "forbiddenDestinationMatches": descriptor_matches,
        },
        "legacyCompatibilityAlias": {
            "path": "workers/image-ocr/RemNoteQuickClipper.ImageOcr.Worker.exe",
            "bytes": LEGACY_COMPATIBILITY_ALIAS.stat().st_size if LEGACY_COMPATIBILITY_ALIAS.is_file() else None,
            "sha256": sha256_file(LEGACY_COMPATIBILITY_ALIAS) if LEGACY_COMPATIBILITY_ALIAS.is_file() else None,
            "descriptor": legacy_descriptor,
            "candidate": False,
        },
        "checks": checks,
        "sourceForbiddenDestinationMatches": source_matches,
        "liveCanaryForbiddenDestinationMatches": live_canary_matches,
        "offlineCanaryForbiddenDestinationMatches": offline_canary_matches,
    }
    serialized = json.dumps(report, ensure_ascii=False, sort_keys=True, indent=2) + "\n"
    if args.output is not None:
        output = args.output.resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        with output.open("xb") as handle:
            handle.write(serialized.encode("utf-8"))
    print(serialized, end="")
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
