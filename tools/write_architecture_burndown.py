from __future__ import annotations

import argparse
import json
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import verify_production_state_journeys as journey_gate


PROJECT = Path(__file__).resolve().parents[1]
DEFAULT_BASELINE = PROJECT / "canary/quality-production-state-journey/20260901T090757Z-phase1-5-final/quality-receipt.json"
DEFAULT_BASELINE_SHA256 = "0BD731667436271B8CAB223E79BCBDD9D6005DCC6C75701B73CB2D3EA7A3CFE7"


def counts(violations: list[dict[str, Any]]) -> Counter[str]:
    return Counter(item["code"] for item in violations)


def compare(baseline: dict[str, Any], current: dict[str, Any]) -> dict[str, Any]:
    before = counts(baseline["architecture"]["violations"])
    after = counts(current["violations"])
    codes = sorted(set(before) | set(after))
    rows = []
    for code in codes:
        rows.append({
            "code": code,
            "baselineCount": before[code],
            "currentCount": after[code],
            "resolvedCount": max(0, before[code] - after[code]),
            "introducedCount": max(0, after[code] - before[code]),
        })
    remaining = sum(after.values())
    introduced = sum(item["introducedCount"] for item in rows)
    status = "PASS-ARCHITECTURE-SOURCE-GATE" if remaining == 0 else "NO-GO-ARCHITECTURE-VIOLATIONS-REMAIN"
    return {
        "status": status,
        "baselineViolationCount": sum(before.values()),
        "currentViolationCount": remaining,
        "resolvedViolationCount": sum(item["resolvedCount"] for item in rows),
        "introducedViolationCount": introduced,
        "rows": rows,
        "promotionAllowed": remaining == 0,
        "positiveInvariants": {
            "rulesEvaluated": current["rulesEvaluated"],
            "verifiedArtifactReaderDefinitions": current["verifiedArtifactReaderDefinitions"],
            "allProductionArtifactConsumersUnified": not any(item["code"] in {"unified-verified-artifact-reader-missing", "production-artifact-consumer-not-unified"} for item in current["violations"]),
            "fileAppearanceAloneCountsAsFix": False,
        },
    }


def run(baseline_path: Path) -> dict[str, Any]:
    journey_gate.require(baseline_path.is_file(), "architecture baseline missing")
    if baseline_path.resolve() == DEFAULT_BASELINE.resolve():
        journey_gate.require(journey_gate.sha256_file(baseline_path) == DEFAULT_BASELINE_SHA256, "architecture baseline SHA drift")
    baseline = journey_gate.load(baseline_path)
    current = journey_gate.inspect_architecture()
    delta = compare(baseline, current)
    return {
        "schemaVersion": "knowledge-clipper-architecture-burndown.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "baseline": journey_gate.evidence(baseline_path),
        "currentSource": current,
        "delta": delta,
        "status": delta["status"],
        "candidateBound": False,
        "sideEffects": {"productionWrites": 0, "candidateRuns": 0, "externalCalls": 0},
    }


def write(output_root: Path, baseline_path: Path) -> dict[str, Any]:
    journey_gate.require(not output_root.exists(), f"append-only output exists: {output_root}")
    output_root.mkdir(parents=True)
    result = run(baseline_path)
    receipt = output_root / "architecture-burndown-receipt.json"
    receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return {"status": result["status"], "receipt": journey_gate.evidence(receipt), "remaining": result["delta"]["currentViolationCount"], "resolved": result["delta"]["resolvedViolationCount"], "introduced": result["delta"]["introducedViolationCount"]}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--baseline", type=Path, default=DEFAULT_BASELINE)
    parser.add_argument("--output-root", type=Path)
    args = parser.parse_args()
    result = write(args.output_root.resolve(), args.baseline.resolve()) if args.output_root else run(args.baseline.resolve())
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
