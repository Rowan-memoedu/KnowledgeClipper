from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


class GateFailure(RuntimeError):
    pass


def require(value: bool, message: str) -> None:
    if not value:
        raise GateFailure(message)


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def evaluate(fixture: dict) -> dict:
    observed = fixture["observed"]
    require(observed["captureSucceeded"] and observed["submitSucceeded"], "failure happened before submitted task")
    require(observed["content"]["status"] == "failed", "content failure missing")
    require(observed["content"]["errorCode"] == "content.gate-failed/card.same-sides", "content failure identity drift")
    require(observed["ankiAttempts"] == 0 and observed["previewCreated"] is False, "failed content reached preview/export")
    missing = []
    for key in ("speechCompletedIndependently", "typedTerminalFailure", "spinnerTerminated"):
        if observed.get(key) is not True:
            missing.append(key)
    require(missing, "historical failed candidate must not be promoted")
    return {
        "schema": "kc.quality.content-failure-isolation/1",
        "createdAt": datetime.now(timezone.utc).isoformat(),
        "status": "NO-GO-REAL-USER-FAILURE",
        "candidate": fixture["candidate"],
        "taskId": fixture["taskId"],
        "observed": observed,
        "missingNextCandidateGates": missing,
        "nextCandidateRequirements": fixture["nextCandidateRequirements"],
        "supersedesStatusOnly": fixture["supersedesStatusOnly"],
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = evaluate(json.loads(args.fixture.read_text(encoding="utf-8-sig")))
    args.output.parent.mkdir(parents=True, exist_ok=False)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{sha(args.output)} {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
