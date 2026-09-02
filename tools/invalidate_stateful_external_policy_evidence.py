from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path

import run_sealed_candidate_stateful_journeys as runner
import verify_production_state_journeys as gate


PROJECT = Path(__file__).resolve().parents[1]


def run(evidence_path: Path, output_root: Path) -> dict:
    evidence_path = evidence_path.resolve()
    output_root = output_root.resolve()
    gate.require(evidence_path.is_file() and evidence_path.name == "candidate-stateful-evidence.json", "stateful evidence input missing")
    gate.require(not output_root.exists(), "append-only invalidation output exists")
    source = gate.load(evidence_path)
    gate.require(source.get("externalPolicy") and all(value is False for value in source["externalPolicy"].values()), "source evidence did not claim fully offline execution")
    observed = runner.inspect_offline_boundary(evidence_path.parent)
    gate.require(observed["status"] == "failed-external-policy-breach", "source evidence has no reproducible external-policy breach")
    candidate = source.get("candidate") or {}
    candidate_root = gate.contained(PROJECT, candidate.get("root", ""))
    gate.require(gate.tree_summary(candidate_root) == candidate.get("sealed"), "sealed candidate drifted while invalidating evidence")
    output_root.mkdir(parents=True)
    receipt = {
        "schemaVersion": "knowledge-clipper-stateful-evidence-invalidation.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "invalidated-never-promotion-evidence",
        "invalidates": gate.evidence(evidence_path),
        "candidate": {
            "root": candidate["root"],
            "manifest": candidate["manifest"],
            "sealed": candidate["sealed"],
            "mutated": False,
        },
        "reasonCodes": [
            "external-policy-breached",
            "content-provider-live-completion-under-offline-claim",
            "offline-process-environment-did-not-shadow-user-setting-fallback",
        ],
        "observedBreach": observed,
        "scope": {
            "candidateInvalidated": False,
            "evidenceInvalidated": True,
            "externalCallsDuringInvalidation": 0,
            "userProcessesTouched": 0,
        },
    }
    receipt_path = output_root / "external-policy-invalidation.receipt.json"
    receipt_path.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return {"status": receipt["status"], "receipt": gate.evidence(receipt_path)}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(run(args.evidence, args.output_root), ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
