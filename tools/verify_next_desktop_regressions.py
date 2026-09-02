from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


PROJECT = Path(__file__).resolve().parents[1]
DEFAULT_FIXTURE = PROJECT / "tests/Quality.Gates/fixtures/next-desktop-regressions/valid/waiting-next-candidate.json"
INVALID_FIXTURE = PROJECT / "tests/Quality.Gates/fixtures/next-desktop-regressions/invalid/remnote-original-media-reintroduced.json"


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def evidence(path: Path) -> dict[str, Any]:
    return {
        "path": path.resolve().relative_to(PROJECT.resolve()).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha256_file(path),
    }


def verify_evidence(reference: dict[str, Any]) -> dict[str, Any]:
    path = PROJECT / reference["path"]
    require(path.is_file(), f"evidence missing: {reference['path']}")
    observed = evidence(path)
    require(observed["sha256"] == reference["sha256"].upper(), f"evidence SHA drift: {reference['path']}")
    return observed


def evaluate(fixture: dict[str, Any]) -> dict[str, Any]:
    require(fixture.get("schemaVersion") == "knowledge-clipper-next-desktop-regression-gate.v1", "fixture schemaVersion drift")
    policy = fixture["productPolicy"]
    original = policy["originalMode"]
    remnote_media = original["remnoteOriginalMedia"]
    require(original["allowedDestinations"] == ["anki"], "original mode must be Anki-only")
    require(remnote_media["classification"] == "BY-DESIGN-UNSUPPORTED-NOT-A-GAP", "RemNote original-media was reintroduced as a product gap")
    require(remnote_media["reasonCode"] == "mcp.media_write_unavailable", "RemNote original-media reason code drift")
    require(remnote_media["releaseBlocker"] is False, "RemNote original-media was reintroduced as a release blocker")
    require(remnote_media["candidateGateRequired"] is False, "RemNote original-media was reintroduced as a candidate gate")
    remnote_text = policy["remnoteTextAndCards"]
    require(remnote_text == {"exactTitleDiscoveryRequired": True, "readOnlyProbeRequired": True}, "RemNote text/card discovery requirement drift")

    required = fixture["requiredRegressions"]
    require(set(required) == {"ankiReadonlyProbe", "remnoteExactTitleDiscovery", "questionFollowUpContinuation"}, "next candidate regression set drift")
    module_evidence = {name: verify_evidence(value) for name, value in fixture["moduleEvidence"].items()}
    candidate = fixture.get("nextCandidateEvidence")
    if candidate is None:
        expected = "WAITING-NEXT-AUTHORITY-DESKTOP-CANDIDATE"
        require(all(status == expected for status in required.values()), "missing next candidate was promoted")
        return {
            "schemaVersion": "knowledge-clipper-next-desktop-regression-quality.v1",
            "status": "WAITING-NEXT-AUTHORITY-DESKTOP-EVIDENCE",
            "policy": {
                "originalModeDestinations": ["anki"],
                "remnoteOriginalMedia": "BY-DESIGN-UNSUPPORTED-NOT-A-GAP-NONBLOCKING",
                "remnoteTextAndCards": "EXACT-TITLE-DISCOVERY-AND-READONLY-PROBE-REQUIRED",
            },
            "moduleEvidence": module_evidence,
            "candidateBinding": None,
            "regressions": {
                "ankiReadonlyProbe": {"moduleStatus": "PASS", "candidateStatus": expected},
                "remnoteExactTitleDiscovery": {"moduleStatus": "WAITING-CORE-ADMISSION", "candidateStatus": expected},
                "questionFollowUpContinuation": {"moduleStatus": "PASS", "candidateStatus": expected},
            },
            "releaseDecision": "NOT-EVALUATED-NO-NEXT-CANDIDATE",
        }

    authority = verify_evidence(candidate["authorityCheckpoint"])
    manifest = verify_evidence(candidate["candidateManifest"])
    regressions = candidate["regressions"]
    require(regressions["ankiReadonlyProbe"] == {"status": "passed", "v1V2PlanConfigMatched": True, "localPackageAndConnectionErrorsDistinct": True}, "Anki probe candidate regression failed")
    require(regressions["remnoteExactTitleDiscovery"] == {"status": "passed", "exactTitleMatched": True, "readOnlyProbe": True, "originalMediaEvaluated": False}, "RemNote exact-title candidate regression failed")
    require(regressions["questionFollowUpContinuation"] == {"status": "passed", "canonicalOrdinalArtifact": True, "taskGlobalManifest": True, "commandOwnedPayload": True}, "question follow-up candidate regression failed")
    return {
        "schemaVersion": "knowledge-clipper-next-desktop-regression-quality.v1",
        "status": "PASS-CANDIDATE-BOUND-THREE-REGRESSIONS",
        "policy": {
            "originalModeDestinations": ["anki"],
            "remnoteOriginalMedia": "BY-DESIGN-UNSUPPORTED-NOT-A-GAP-NONBLOCKING",
            "remnoteTextAndCards": "EXACT-TITLE-DISCOVERY-AND-READONLY-PROBE-REQUIRED",
        },
        "moduleEvidence": module_evidence,
        "candidateBinding": {"authorityCheckpoint": authority, "candidateManifest": manifest},
        "regressions": regressions,
        "releaseDecision": "SCOPED-REGRESSIONS-PASS-NOT-A-FULL-RELEASE-DECISION",
    }


def validate_fixtures() -> dict[str, Any]:
    evaluate(load(DEFAULT_FIXTURE))
    rejected = False
    try:
        evaluate(load(INVALID_FIXTURE))
    except GateFailure:
        rejected = True
    require(rejected, "invalid RemNote original-media blocker fixture was accepted")
    return {"status": "PASS", "validAccepted": 1, "invalidRejected": 1}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--fixture", type=Path, default=DEFAULT_FIXTURE)
    parser.add_argument("--output-root", type=Path)
    args = parser.parse_args()
    result = evaluate(load(args.fixture))
    result["fixtureValidation"] = validate_fixtures()
    result["createdAt"] = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    if args.output_root:
        root = args.output_root.resolve()
        require(not root.exists(), f"append-only output exists: {root}")
        root.mkdir(parents=True)
        receipt = root / "quality-receipt.json"
        receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"receipt": str(receipt), "sha256": sha256_file(receipt), "status": result["status"]}, ensure_ascii=False))
    else:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
