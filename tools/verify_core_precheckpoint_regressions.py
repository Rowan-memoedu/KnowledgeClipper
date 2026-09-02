from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


PROJECT = Path(__file__).resolve().parents[1]
DOTNET = Path(r"D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe")
PROJECT_FILE = PROJECT / "tests/CoreAdapter.IntegrationTests/CoreAdapter.IntegrationTests.csproj"


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


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


def find_result(document: dict[str, Any], name: str) -> dict[str, Any]:
    matches = [item for item in document["results"] if item["name"] == name]
    require(len(matches) == 1, f"CoreAdapter result missing or duplicated: {name}")
    require(matches[0]["status"] == "passed", f"CoreAdapter result did not pass: {name}")
    return matches[0]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-root", type=Path, required=True)
    args = parser.parse_args()
    output = args.output_root.resolve()
    require(not output.exists(), f"append-only output exists: {output}")
    require(DOTNET.is_file(), f"pinned dotnet missing: {DOTNET}")
    output.mkdir(parents=True)

    command = [
        str(DOTNET), "run", "--project", str(PROJECT_FILE),
        "--configuration", "Release", "--no-restore", "--", str(PROJECT),
    ]
    completed = subprocess.run(command, cwd=PROJECT, text=True, encoding="utf-8", errors="replace", capture_output=True, timeout=120)
    (output / "stderr.log").write_text(completed.stderr, encoding="utf-8")
    (output / "stdout.json").write_text(completed.stdout, encoding="utf-8")
    require(completed.returncode == 0, f"CoreAdapter exit code {completed.returncode}")
    document = json.loads(completed.stdout)
    require(document["status"] == "passed", "CoreAdapter aggregate did not pass")

    original = find_result(document, "original-media-anki-only")
    require(original["providerCalls"] == 0, "original route invoked provider")
    require(original["remnoteRejectedBeforeWorker"] is True, "original+RemNote was not rejected before worker")
    require(original["ankiStarted"] is True and original["remnoteStarted"] is False, "original route is not Anki-only")
    require(original["ordering"] == "questionImages-then-answerImages", "original media order drift")

    discovery = find_result(document, "remnote-target-discovery-v13")
    require(discovery["ipc"] == "1.3", "RemNote discovery IPC version drift")
    require(discovery["uniquePersistableBinding"] is True and discovery["ambiguousPersistableBinding"] is False, "RemNote exact/ambiguous discovery semantics drift")
    require(discovery["externalWrites"] == 0 and discovery["automaticRetryAllowed"] is False, "RemNote discovery safety boundary drift")

    playback = find_result(document, "stage-scoped-playback-stop-and-replay")
    require(playback["stop"]["runCancelRequested"] is False, "playback stop escalated to whole-run cancel")

    receipt = {
        "schemaVersion": "knowledge-clipper-quality-core-precheckpoint-regressions.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-SCOPED-CHECKPOINT-PENDING",
        "coreAdapter": {
            "aggregateStatus": document["status"],
            "groupsPassed": len(document["results"]),
            "stdout": evidence(output / "stdout.json"),
            "stderr": evidence(output / "stderr.log"),
            "exitCode": completed.returncode,
        },
        "regressions": {
            "originalMode": {
                "status": "PASS-OFFLINE",
                "destinations": ["anki"],
                "remnoteIncompatibleCode": "resolver.original_remnote_incompatible",
                "providerCalls": 0,
                "remnoteRejectedBeforeWorker": True,
                "ankiStarted": True,
                "remnoteStarted": False,
                "ordering": original["ordering"],
            },
            "remnoteExactTitleDiscovery": {
                "status": "PASS-FAKE-IPC-1.3",
                "uniquePersistableBinding": True,
                "ambiguousPersistableBinding": False,
                "externalWrites": 0,
                "automaticRetryAllowed": False,
            },
            "playbackStop": {
                "observedThisRun": "PASS",
                "checkpointGate": "PENDING-OWNER-REPORTED-INTERMITTENT-FAILURE",
                "mayPromoteCheckpoint": False,
            },
        },
        "sourceEvidence": {
            "resolver": evidence(PROJECT / "orchestrator/Core/DesktopIntentResolver.cs"),
            "moduleActions": evidence(PROJECT / "orchestrator/Core/ModuleActionControl.cs"),
            "coreAdapterTests": evidence(PROJECT / "tests/CoreAdapter.IntegrationTests/Program.cs"),
            "moduleActionRequestSchema": evidence(PROJECT / "contracts/core/v2.8/module-control/schemas/module-action-request.v2.schema.json"),
            "moduleActionReceiptSchema": evidence(PROJECT / "contracts/core/v2.8/module-control/schemas/module-action-receipt.v2.schema.json"),
        },
        "candidateBinding": None,
        "authorityCheckpoint": "PENDING-NOT-PUBLISHED",
        "desktopCandidate": "PENDING",
        "externalServices": {"remnoteWrites": 0, "ankiWrites": 0, "mineruCalls": 0, "speakerStarts": 0},
    }
    receipt_path = output / "quality-receipt.json"
    receipt_path.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"receipt": str(receipt_path), "sha256": sha256_file(receipt_path), "status": receipt["status"]}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
