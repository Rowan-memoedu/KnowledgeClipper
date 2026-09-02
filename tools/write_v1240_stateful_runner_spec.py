from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[1]


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def evidence(path: Path) -> dict[str, object]:
    return {
        "path": path.resolve().relative_to(PROJECT.resolve()).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha256(path),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--template", required=True, type=Path)
    parser.add_argument("--candidate-root", required=True, type=Path)
    parser.add_argument("--source-gate", required=True, type=Path)
    parser.add_argument("--pipe-name", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    template = args.template.resolve()
    candidate = args.candidate_root.resolve()
    source_gate = args.source_gate.resolve()
    output = args.output.resolve()
    if output.exists():
        raise FileExistsError(f"append-only runner spec exists: {output}")
    if not candidate.is_relative_to((PROJECT / "releases").resolve()):
        raise ValueError("candidate root escaped releases")
    if not args.pipe_name.startswith("KnowledgeClipper.Quality.Stateful."):
        raise ValueError("pipe name is not Quality scoped")

    manifest = candidate / "candidate-file-manifest.v1.json"
    if not manifest.is_file() or not source_gate.is_file():
        raise FileNotFoundError("candidate manifest or source gate is missing")
    data = json.loads(template.read_text(encoding="utf-8-sig"))
    data["fixtureOnly"] = False
    data["candidate"] = {
        "root": candidate.relative_to(PROJECT).as_posix(),
        "manifest": evidence(manifest),
    }
    data["sourceGate"] = evidence(source_gate)
    data["isolation"]["pipeName"] = args.pipe_name
    for journey in data["journeys"].values():
        for phase in journey["phases"]:
            scenario_path = (PROJECT / phase["scenario"]["path"]).resolve()
            if not scenario_path.is_relative_to((PROJECT / "tests/Quality.Gates/fixtures").resolve()):
                raise ValueError("scenario path escaped Quality fixtures")
            phase["scenario"] = evidence(scenario_path)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": "written", "spec": evidence(output)}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
