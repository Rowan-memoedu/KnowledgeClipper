from __future__ import annotations

from typing import Any


class GateFailure(RuntimeError):
    pass


def require(value: bool, message: str) -> None:
    if not value:
        raise GateFailure(message)


def evaluate(data: dict[str, Any]) -> dict[str, Any]:
    modes = data["modes"]
    require(set(modes) == {"default", "meaning-reading", "writing", "both"}, "language mode matrix incomplete")
    expected_counts = {"meaning-reading": 1, "writing": 1, "both": 2}
    require(modes["default"]["choiceOptions"] == ["meaning-reading", "writing", "both"], "default choices/order drift")
    for mode, count in expected_counts.items():
        item = modes[mode]
        require(len(item["previewCards"]) == count, f"{mode} preview card count drift")
        require(item["preConfirmAnkiAttempts"] == 0, f"{mode} exported before confirmation")
        require(item["confirmAnkiAttempts"] == 1 and item["ankiWorkerVersion"] == "1.4.1", f"{mode} Anki binding drift")
        require(item["previewCards"] == item["notesInfoCards"] == item["cardsInfoCards"], f"{mode} preview/readback drift")
        require(all(item[key] is True for key in ("restartStable", "staleRejected", "doubleConfirmRejected", "cancelZeroDestination")), f"{mode} action invariant missing")
    require(modes["both"]["previewCards"][0]["strategy"] == "meaning-reading", "both first card order drift")
    require(modes["both"]["previewCards"][1]["strategy"] == "writing", "both second card order drift")
    external = data["externalEvidence"]
    require(external == {"realContentDeepSeek": True, "physicalSpeech": True, "isolatedAnki": True}, "required external candidate evidence missing")
    require(data["candidateBound"] is True, "matrix is not bound to one candidate")
    return {"status": "PASS-USER-HANDTEST-LANGUAGE-MATRIX", "modes": sorted(modes), "candidate": data["candidate"]}
