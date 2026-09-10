from __future__ import annotations

from typing import Any


class GateFailure(RuntimeError):
    pass


def require(value: bool, message: str) -> None:
    if not value:
        raise GateFailure(message)


def evaluate(data: dict[str, Any]) -> dict[str, Any]:
    require(data["schema"] == "kc.quality.content-context-resolution-evidence/1", "evidence schema drift")
    identity = data["identity"]
    require(identity["workerId"] == "content.deepseek" and identity["workerVersion"] == "0.10.2", "worker identity drift")
    require(identity["manifestVerified"] and identity["payloadMismatchCount"] == 0, "release payload not independently verified")

    input_contract = data["inputContract"]
    require(input_contract["schemaVersion"] == "standard-input/6.0.0", "input contract version drift")
    require(input_contract["selectedTextExact"] and input_contract["targetTextExact"], "selected/target identity is not exact")
    require(input_contract["contextSegmentsOrdered"] and input_contract["allIdentityHashesBound"], "context identity/order is not hash-bound")
    require(input_contract["speechUsesSelectedTextOnly"], "context leaked into Speech text")

    cards = data["cardinality"]
    require(cards == {"meaning-reading": 1, "writing": 1, "both": 2}, "language-card cardinality drift")
    require(data["bothOrder"] == ["language_context", "language_writing"], "both card order drift")

    unresolved = data["unresolved"]
    require(unresolved["workerResultStatus"] == "completed" and unresolved["moduleReleaseState"] == "unresolved", "unresolved transport semantics drift")
    require(unresolved["decisionKind"] == "language-practice-decision" and unresolved["decisionContractVersion"] == "2.0.0", "decision contract drift")
    require(unresolved["cardBundleCount"] == 0 and unresolved["coreExpectedProjection"] == "waiting_input", "unresolved path was treated as ready")

    human = data["humanResolution"]
    require(human["field"] == "human_overrides.languageTargetResolution", "human resolution field drift")
    require(human["newAttempt"] and human["targetTokenBindsSelectedAndContextHashes"], "human resolution is not immutable and hash-bound")
    require(human["staleTokenError"] == "INPUT_STALE", "stale token did not fail closed")

    require(data["sideEffects"] == {"deepSeekLive": 0, "exporterWrites": 0}, "offline evidence performed external work")
    return {"status": "PASS-CONTENT-MODULE-CONDITIONAL-CORE-DESKTOP", "workerVersion": identity["workerVersion"]}
