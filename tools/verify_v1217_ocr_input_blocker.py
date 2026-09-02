from __future__ import annotations

import argparse
import hashlib
import json
import re
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


PROJECT = Path(__file__).resolve().parents[1]
BLOCKER = PROJECT / "canary/sealed-ocr-human-resume-v1.2.17/20260831T065500Z/blocker-receipt.json"
EXPECTED_BLOCKER = "89B57FEEE9F75C02BEA48E4CCFF8226F7AB8832C1AA47D7D1FB6F8CB5DD3B4D8"
EXPECTED_REGISTRY = "7F2F9591C564D634AA6408D2B1C4C07D9876342C1B3FA11A99A23A882CF3EFA1"
EXPECTED_CHECKPOINT = "862ED5C41EC6BAA5F8B1446C9AA68DD131CEA7784D9EA8985CE54D42F2ACF95D"
EXPECTED_ORCHESTRATOR = "CEDC60ABFA34096B3458F22A94FCD3E939969E2858F6B06AE639DABFEE90DDED"


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def io_path(path: Path) -> Path:
    absolute = path.resolve(strict=False)
    text = str(absolute)
    if len(text) >= 248 and absolute.drive and not text.startswith("\\\\?\\"):
        return Path("\\\\?\\" + text)
    return absolute


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with io_path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load(path: Path) -> dict[str, Any]:
    return json.loads(io_path(path).read_text(encoding="utf-8-sig"))


def relative(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def evidence(path: Path) -> dict[str, Any]:
    return {"path": relative(path), "bytes": io_path(path).stat().st_size, "sha256": sha256_file(path)}


def verify_ref(value: dict[str, Any], label: str) -> Path:
    path = Path(value["path"])
    if not path.is_absolute():
        path = PROJECT / path
    require(io_path(path).is_file(), f"{label}: missing: {path}")
    require(sha256_file(path) == str(value["sha256"]).upper(), f"{label}: SHA drift: {path}")
    require(io_path(path).stat().st_size == int(value["bytes"]), f"{label}: bytes drift: {path}")
    return path


def find_values(value: Any, key: str) -> list[Any]:
    found: list[Any] = []
    if isinstance(value, dict):
        for name, child in value.items():
            if name == key:
                found.append(child)
            found.extend(find_values(child, key))
    elif isinstance(value, list):
        for child in value:
            found.extend(find_values(child, key))
    return found


def evaluate() -> dict[str, Any]:
    require(sha256_file(BLOCKER) == EXPECTED_BLOCKER, "blocker receipt SHA drift")
    data = load(BLOCKER)
    require(data["status"] == "blocked-input-mismatch", "blocker status drift")
    require(data["failClosedReason"]["code"] == "ocr.resume_source_not_unresolved", "blocker code drift")
    image_receipt_path = verify_ref(data["source"]["imageReceipt"], "Image receipt")
    worker_result_path = verify_ref(data["source"]["workerResult"], "worker result")
    handoff_path = verify_ref(data["source"]["contentHandoff"], "Content handoff")
    markdown_paths = [verify_ref(item, f"markdown[{index}]") for index, item in enumerate(data["source"]["markdown"])]
    require(len(markdown_paths) == 4, "expected four published Markdown files")
    registry_path = verify_ref(data["authority"]["registry"], "registry")
    checkpoint_path = verify_ref(data["authority"]["checkpoint"], "checkpoint")
    orchestrator_path = verify_ref(data["authority"]["orchestrator"], "Orchestrator")
    require(sha256_file(registry_path) == EXPECTED_REGISTRY, "registry identity drift")
    require(sha256_file(checkpoint_path) == EXPECTED_CHECKPOINT, "checkpoint identity drift")
    require(sha256_file(orchestrator_path) == EXPECTED_ORCHESTRATOR, "Orchestrator identity drift")
    image_receipt = load(image_receipt_path)
    worker_result = load(worker_result_path)
    handoff = load(handoff_path)
    require(worker_result["status"] == "completed", "source worker result not completed")
    review_values = find_values(image_receipt, "humanReviewRecommended") + find_values(worker_result, "humanReviewRecommended") + find_values(handoff, "humanReviewRecommended")
    require(review_values and all(value is False for value in review_values), f"human review signal drift: {review_values}")
    standalone_canar = 0
    canary_counts = []
    markdown_evidence = []
    for path in markdown_paths:
        text = io_path(path).read_text(encoding="utf-8-sig")
        standalone_canar += len(re.findall(r"(?<![A-Za-z])CANAR(?![A-Za-z])", text))
        canary_count = len(re.findall(r"(?<![A-Za-z])CANARY(?![A-Za-z])", text))
        canary_counts.append(canary_count)
        markdown_evidence.append({**evidence(path), "standaloneCANAR": 0, "CANARY": canary_count})
    require(standalone_canar == 0, "published Markdown contains standalone CANAR")
    require(all(count >= 1 for count in canary_counts), f"not every Markdown contains CANARY: {canary_counts}")
    observed = data["observed"]
    require(observed == {
        "sourceStatus": "completed",
        "humanReviewRecommended": False,
        "markdownItems": 4,
        "standaloneCanarOccurrences": 0,
        "allMarkdownAlreadyContainsCanary": True,
        "correctionDiffAvailable": False,
        "expectedInitialProjection": "ready-not-unresolved",
    }, "observed input semantics drift")
    effects = data["effects"]
    require(all(value == 0 for value in effects.values()), f"blocker caused side effects: {effects}")
    require(data["failClosedReason"]["prohibitedWorkaround"] == "Do not edit immutable OCR artifacts, fabricate an unresolved result, or submit an empty/non-evidenced correction.", "fail-closed workaround boundary drift")
    return {
        "schemaVersion": "knowledge-clipper-quality-v1217-ocr-input-blocker.v1",
        "createdAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "PASS-INPUT-SEMANTICS-BLOCKER-V1.2.17",
        "authorityBound": True,
        "blocker": evidence(BLOCKER),
        "source": {
            "imageReceipt": evidence(image_receipt_path),
            "workerResult": evidence(worker_result_path),
            "contentHandoff": evidence(handoff_path),
            "markdown": markdown_evidence,
        },
        "observed": {
            "workerStatus": "completed",
            "humanReviewRecommended": False,
            "markdownFiles": 4,
            "standaloneCANAR": standalone_canar,
            "CANARYPerFile": canary_counts,
            "truthfulProjection": "ready-not-unresolved",
        },
        "decision": {
            "status": "blocked-input-mismatch",
            "code": "ocr.resume_source_not_unresolved",
            "unresolvedProjectionAllowed": False,
            "waitingInputAllowed": False,
            "correctionSubmissionAllowed": False,
            "immutableArtifactsEdited": False,
        },
        "productGapUpdate": {
            "ocrHumanResumeImplementation": "CONDITIONAL-PREVIOUS-GAP-UNCHANGED",
            "providedLiveInput": "REJECTED-AS-ALREADY-RESOLVED",
            "requiredNextInput": data["requiredNextInput"],
        },
        "effects": effects,
        "promotion": {"authorityV1217": "UNCHANGED-PASS-OFFLINE", "ocrLiveHumanResume": "NOT-QUALIFIED-INPUT-MISMATCH", "release": "NO-GO-CONDITIONAL"},
    }


def render(result: dict[str, Any]) -> str:
    return "\n".join([
        "# v1.2.17 OCR human-resume input semantics quality gate",
        "",
        f"- Status: `{result['status']}`",
        "- The delegated Image 1.2.4 immutable package is already resolved: four published Markdown files each contain standalone `CANARY`; standalone `CANAR` count is 0; `humanReviewRecommended=false`.",
        "- Truthful projection is `ready-not-unresolved`. It must not be converted to `unresolved`, `waiting_input`, or a fabricated `CANAR -> CANARY` correction.",
        "- Orchestrator, MinerU, DeepSeek, Anki, RemNote and speaker starts/calls/writes all remained 0.",
        "",
        "## Current boundary",
        "",
        "- This is a PASS for the input-semantic fail-closed gate, not a PASS for OCR human-resume E2E.",
        "- OCR human-resume remains conditional until an actual immutable unresolved package is supplied, or the acceptance target is corrected.",
    ]) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--emit-root")
    args = parser.parse_args()
    result = evaluate()
    artifacts: dict[str, Any] = {}
    if args.emit_root:
        root = Path(args.emit_root).resolve()
        require(root.is_relative_to(PROJECT.resolve()), "output root escaped project")
        require(not root.exists(), "append-only output root exists")
        root.mkdir(parents=True)
        receipt = root / "quality-ocr-input-blocker-receipt.json"
        readable = root / "READABLE.md"
        receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="")
        readable.write_text(render(result), encoding="utf-8", newline="")
        artifacts = {"root": relative(root), "receipt": evidence(receipt), "readable": evidence(readable)}
    print(json.dumps({"result": result, "artifacts": artifacts}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
