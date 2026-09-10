from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


EXPECTED = {
    "manifest": "ED77F1940C87E0181770073E837265B1DA80C53253EE976834F3FADB85683BB8",
    "receipt": "975B11001E04017A74C788B78C86E1DCE89E75847A2E3D7FDA666A7C2DE10F79",
    "sdkManifest": "08338D0079BF9DB389264099DE42B5F80A7941EA94B360B3CA960006C7A0ACB3",
    "client": "B3C94D66D123DFC7A4615592B876C37B4285E010D782899147820D3F8358B007",
}


class GateFailure(RuntimeError):
    pass


def require(value: bool, message: str) -> None:
    if not value:
        raise GateFailure(message)


def sha(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def evidence(path: Path) -> dict[str, Any]:
    return {"path": str(path.resolve()), "bytes": path.stat().st_size, "sha256": sha(path)}


def verify_files(root: Path, manifest: dict[str, Any]) -> tuple[int, int]:
    mismatches = []
    for item in manifest["files"]:
        path = root / item["path"]
        if not path.is_file() or path.stat().st_size != item["bytes"] or sha(path) != item["sha256"].upper():
            mismatches.append(item["path"])
    require(not mismatches, f"manifest payload drift: {mismatches[:5]}")
    return len(manifest["files"]), sum(item["bytes"] for item in manifest["files"])


def verify_semantics(receipt: dict[str, Any]) -> dict[str, Any]:
    require(receipt["status"] == "PASS", "source receipt not PASS")
    require(receipt["externalWorkersExecuted"] is False and receipt["workerDescribeOnly"] is True, "external worker boundary drift")
    require(receipt["speakerPlayback"] is False, "speaker boundary drift")
    failure = receipt["contentFailure"]
    result = failure["result"]
    require(result["status"] == "partial", "typed result is not partial")
    require(result["pipeline"] == "partially_failed", "pipeline is not partially_failed")
    require(result["attentionRequired"] is True, "attentionRequired missing")
    require(result["cardGeneration"] == {"status": "failed", "reasonCode": "content.gate-failed"}, "typed content failure drift")
    require(result["cardDraft"] is None and result["expectedCardCount"] == result["actualCardCount"] == 0, "failed content produced preview cards")
    require(result["ankiExternalWriteCount"] == 0 and failure["ankiAttempts"] == 0, "failed content reached Anki")
    kinds = [item["kind"] for item in failure["artifacts"]]
    require(kinds.count("speech-synthesis") == 1 and kinds.count("speech-playback") == 1, "speech artifacts missing or duplicated")
    require(not any("card" in kind or "anki-readback" == kind for kind in kinds), "preview/export artifact leaked")
    require(failure["speechPlaybackAttempts"] == 1 and result["playback"] == "idle_replayable", "speech did not independently finish once")
    return {"result": result, "artifactKinds": kinds, "playbackAttempts": failure["speechPlaybackAttempts"], "ankiAttempts": failure["ankiAttempts"]}


def evaluate(candidate: Path, sdk: Path) -> dict[str, Any]:
    candidate, sdk = candidate.resolve(), sdk.resolve()
    manifest_path = candidate / "candidate-manifest.json"
    receipt_path = candidate / "canary/fake-content-failure-and-question/receipt.json"
    sdk_manifest_path = sdk / "freeze-manifest.json"
    require(sha(manifest_path) == EXPECTED["manifest"], "candidate manifest SHA drift")
    require(sha(receipt_path) == EXPECTED["receipt"], "Fake receipt SHA drift")
    require(sha(sdk_manifest_path) == EXPECTED["sdkManifest"], "SDK manifest SHA drift")
    manifest = load(manifest_path)
    count, total = verify_files(candidate, manifest)
    client = candidate / "core-v3/KnowledgeClipper.Core.V3.Client.dll"
    require(sha(client) == EXPECTED["client"], "Client DLL drift")
    sdk_manifest = load(sdk_manifest_path)
    sdk_count, sdk_total = verify_files(sdk, sdk_manifest)
    source = sdk_manifest["source"]
    require(source["candidate"] == str(candidate) and source["candidateManifestSha256"] == EXPECTED["manifest"], "SDK candidate binding drift")
    require(source["fakeReceiptSha256"] == EXPECTED["receipt"], "SDK receipt binding drift")
    contract = load(sdk / "sdk-contract.v1.json")
    require(contract["assembly"]["sha256"] == EXPECTED["client"], "SDK Client binding drift")
    semantics = verify_semantics(load(receipt_path))
    return {
        "schema": "kc.quality.content-failure-recovery-candidate/1",
        "createdAt": datetime.now(timezone.utc).isoformat(),
        "status": "PASS-SCOPED-OFFLINE-REGRESSION",
        "candidate": {"root": str(candidate), "manifest": evidence(manifest_path), "files": count, "bytes": total, "mismatches": 0, "client": evidence(client)},
        "sdk": {"root": str(sdk), "manifest": evidence(sdk_manifest_path), "files": sdk_count, "bytes": sdk_total, "mismatches": 0},
        "sourceReceipt": evidence(receipt_path),
        "semantics": semantics,
        "supersedesFailureCondition": "T122254Z missing speech/typed-terminal/spinner evidence",
        "boundary": {"fakeOnly": True, "desktopCandidateBound": False, "externalE2E": False},
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--sdk", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = evaluate(args.candidate, args.sdk)
    args.output.parent.mkdir(parents=True, exist_ok=False)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{sha(args.output)} {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
