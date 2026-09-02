from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
from pathlib import Path
from typing import Any, Iterable


TREE_ALGORITHM = "sha256 utf8 ordinal(path TAB bytes TAB lowercase-sha256) joined with LF, no trailing LF"
HEX_64 = re.compile(r"^[0-9A-Fa-f]{64}$")
HEX_40 = re.compile(r"^[0-9A-Fa-f]{40}$")


class GateError(RuntimeError):
    def __init__(self, code: str, detail: str | None = None):
        super().__init__(code if detail is None else f"{code}: {detail}")
        self.code = code
        self.detail = detail


def require(condition: bool, code: str, detail: str | None = None) -> None:
    if not condition:
        raise GateError(code, detail)


def load_json(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    require(isinstance(value, dict), "json.root-not-object", str(path))
    return value


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def canonical_relative(value: str) -> str:
    require(isinstance(value, str) and value != "", "path.missing")
    require("\\" not in value and not value.startswith("/") and ":" not in value, "path.noncanonical", value)
    parts = value.split("/")
    require(all(part not in {"", ".", ".."} for part in parts), "path.noncanonical", value)
    return value


def contained(root: Path, relative: str) -> Path:
    relative = canonical_relative(relative)
    root = root.resolve()
    path = (root / relative).resolve()
    require(path.is_relative_to(root), "path.escape", relative)
    return path


def inventory(root: Path, excluded: Iterable[str] = ()) -> dict[str, Any]:
    excluded_set = set(excluded)
    records: list[dict[str, Any]] = []
    rows: list[str] = []
    total = 0
    for path in sorted((p for p in root.rglob("*") if p.is_file()), key=lambda p: p.relative_to(root).as_posix()):
        relative = path.relative_to(root).as_posix()
        if relative in excluded_set:
            continue
        size = path.stat().st_size
        digest = sha256_file(path)
        total += size
        records.append({"path": relative, "bytes": size, "sha256": digest})
        rows.append(f"{relative}\t{size}\t{digest.lower()}")
    return {
        "algorithm": TREE_ALGORITHM,
        "fileCount": len(records),
        "totalBytes": total,
        "sha256": hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest().upper(),
        "files": records,
    }


def validate_build_manifest(candidate_root: Path, manifest_path: Path, expected_commit: str, expected_source_snapshot_sha256: str) -> dict[str, Any]:
    root = candidate_root.resolve()
    manifest_path = manifest_path.resolve()
    require(root.is_dir(), "build.root-missing", str(root))
    require(manifest_path.is_file() and manifest_path.parent == root, "build.manifest-not-root-owned")
    data = load_json(manifest_path)
    require(data.get("schemaVersion") == "quality-v3-canonical-build-manifest.v1", "build.schema")
    require(isinstance(data.get("buildId"), str) and data["buildId"], "build.id")

    source = data.get("source") or {}
    require(bool(HEX_40.fullmatch(str(source.get("repositoryCommit", "")))), "build.source-commit")
    require(bool(HEX_64.fullmatch(str(source.get("sourceSnapshotSha256", "")))), "build.source-snapshot")
    require(bool(HEX_40.fullmatch(expected_commit)), "build.expected-source-commit")
    require(bool(HEX_64.fullmatch(expected_source_snapshot_sha256)), "build.expected-source-snapshot")
    require(source.get("repositoryCommit") == expected_commit, "build.source-commit-mismatch")
    require(str(source.get("sourceSnapshotSha256", "")).upper() == expected_source_snapshot_sha256.upper(), "build.source-snapshot-mismatch")
    require(source.get("dirty") is False, "build.source-dirty")
    require(data.get("selfContained") is True and data.get("runtimeIdentifier") == "win-x64", "build.not-self-contained")

    startup = data.get("startupSelfVerification") or {}
    require(startup.get("required") is True, "build.startup-self-verification-missing")
    require(startup.get("mode") == "full-manifest-before-service-start", "build.startup-self-verification-mode")
    require(startup.get("failureMode") == "fail-closed", "build.startup-self-verification-not-fail-closed")

    contracts = data.get("contracts") or {}
    require(set(contracts) == {"coreIpc", "moduleAction", "artifact", "capabilityCatalog"}, "build.contract-set")
    for name, contract in contracts.items():
        require(isinstance(contract, dict) and isinstance(contract.get("version"), str) and contract["version"], "build.contract-version", name)
        canonical_relative(str(contract.get("path", "")))

    declared = data.get("files") or []
    require(isinstance(declared, list) and declared, "build.files")
    normalized: list[dict[str, Any]] = []
    seen: set[str] = set()
    for item in declared:
        require(isinstance(item, dict), "build.file-record")
        relative = canonical_relative(str(item.get("path", "")))
        require(relative not in seen, "build.file-duplicate", relative)
        seen.add(relative)
        require(isinstance(item.get("bytes"), int) and item["bytes"] >= 0, "build.file-bytes", relative)
        require(bool(HEX_64.fullmatch(str(item.get("sha256", "")))), "build.file-sha", relative)
        normalized.append({"path": relative, "bytes": item["bytes"], "sha256": str(item["sha256"]).upper()})
    normalized.sort(key=lambda item: item["path"])

    entrypoints = data.get("entrypoints") or {}
    required_entrypoints = [entrypoints.get("desktop"), entrypoints.get("core")]
    workers = entrypoints.get("workers") or []
    require(isinstance(workers, list) and workers, "build.worker-entrypoints")
    worker_ids: set[str] = set()
    for worker in workers:
        require(isinstance(worker, dict) and worker.get("id") not in worker_ids, "build.worker-id")
        worker_ids.add(worker.get("id"))
        required_entrypoints.append(worker.get("path"))
    required_paths = [canonical_relative(str(value or "")) for value in required_entrypoints]
    required_paths += [canonical_relative(str(value["path"])) for value in contracts.values()]
    for relative in required_paths:
        require(relative in seen, "build.required-file-not-declared", relative)

    observed = inventory(root, {manifest_path.name})
    require(observed["files"] == normalized, "build.file-inventory-drift")
    tree = data.get("tree") or {}
    require(tree.get("algorithm") == TREE_ALGORITHM, "build.tree-algorithm")
    require(tree.get("fileCount") == observed["fileCount"], "build.tree-file-count")
    require(tree.get("totalBytes") == observed["totalBytes"], "build.tree-byte-count")
    require(str(tree.get("sha256", "")).upper() == observed["sha256"], "build.tree-sha")
    return {
        "status": "PASS-CANONICAL-BUILD-MANIFEST",
        "buildId": data["buildId"],
        "repositoryCommit": source["repositoryCommit"],
        "fileCount": observed["fileCount"],
        "totalBytes": observed["totalBytes"],
        "treeSha256": observed["sha256"],
        "entrypoints": {"desktop": entrypoints["desktop"], "core": entrypoints["core"], "workers": len(workers)},
    }


def validate_ui_action_evidence(data: dict[str, Any]) -> dict[str, Any]:
    require(data.get("schemaVersion") == "quality-v3-ui-action-evidence.v1", "ui.schema")
    trigger = data.get("trigger") or {}
    require(trigger.get("driver") == "Windows.UIAutomation", "ui.driver")
    require(trigger.get("discovered") is True and trigger.get("invokePattern") is True and trigger.get("invoked") is True, "ui.invoke")
    require(isinstance(trigger.get("automationId"), str) and trigger["automationId"], "ui.automation-id")
    action_id = trigger.get("actionId")
    require(isinstance(action_id, str) and action_id, "ui.action-id-missing")
    forbidden = json.dumps(data, ensure_ascii=False).lower()
    for token in ("assert-contains", "displaytext", "failureobserved", "statussubstring", "renderedtext"):
        require(token not in forbidden, "ui.display-text-contract", token)
    events = data.get("typedEvents") or []
    require(isinstance(events, list) and events, "ui.typed-events-missing")
    correlated = [event for event in events if event.get("actionId") == action_id]
    require(correlated, "ui.invoke-not-correlated-to-command")
    event_types = [event.get("eventType") for event in correlated]
    require("command.accepted" in event_types, "ui.command-not-accepted")
    require(any(value in {"command.completed", "command.attention-required", "command.failed"} for value in event_types), "ui.command-no-terminal")
    return {"status": "PASS-UI-TRIGGER-TYPED-STATE", "actionId": action_id, "events": len(correlated)}


def validate_side_effect_audit(data: dict[str, Any], evidence_root: Path) -> dict[str, Any]:
    require(data.get("schemaVersion") == "quality-v3-side-effect-audit.v1", "effects.schema")
    provider = data.get("provider")
    require(provider in {"anki", "remnote", "deepseek", "mineru"}, "effects.provider")
    mode = data.get("mode")
    require(mode in {"disabled", "live-read-only", "live-bounded-write", "live-compute"}, "effects.mode")
    require(data.get("auditor") == "independent-proxy-call-log", "effects.not-independent")
    allowlist = data.get("allowlist") or []
    declared_observed = data.get("observedCalls") or []
    require(isinstance(allowlist, list) and len(set(allowlist)) == len(allowlist), "effects.allowlist")
    require(isinstance(declared_observed, list), "effects.calls")
    call_log = contained(evidence_root, str(data.get("callLog", "")))
    require(call_log.is_file(), "effects.call-log-missing")
    require(sha256_file(call_log) == str(data.get("callLogSha256", "")).upper(), "effects.call-log-sha")
    observed: list[dict[str, Any]] = []
    for number, line in enumerate(call_log.read_text(encoding="utf-8-sig").splitlines(), 1):
        if not line.strip():
            continue
        value = json.loads(line)
        require(isinstance(value, dict), "effects.call-log-record", str(number))
        observed.append(value)
    require(observed == declared_observed, "effects.call-log-content")
    observed_actions = [call.get("action") for call in observed]
    require(all(isinstance(action, str) and action in allowlist for action in observed_actions), "effects.action-not-allowlisted")
    require(data.get("observedCallCount") == len(observed), "effects.call-count")
    writes = [call for call in observed if call.get("effect") == "write"]
    require(data.get("observedWriteCount") == len(writes), "effects.write-count")
    if mode in {"disabled", "live-read-only", "live-compute"}:
        require(not writes and data.get("observedWriteCount") == 0, "effects.write-in-nonwrite-mode")
    if mode == "disabled":
        require(not observed, "effects.disabled-provider-called")
    if mode == "live-bounded-write":
        require(data.get("destinationIsolation") == "ephemeral-quality-owned", "effects.write-destination-not-isolated")
        require(isinstance(data.get("expectedWriteCount"), int) and len(writes) == data["expectedWriteCount"], "effects.bounded-write-count")
    return {"status": "PASS-SIDE-EFFECT-AUDIT", "provider": provider, "calls": len(observed), "writes": len(writes)}


def validate_runner_plan(data: dict[str, Any]) -> dict[str, Any]:
    require(data.get("schemaVersion") == "quality-v3-private-runner-plan.v1", "runner.schema")
    run_id = data.get("runId")
    require(isinstance(run_id, str) and re.fullmatch(r"[A-Za-z0-9._-]{12,96}", run_id), "runner.id")
    snapshot = data.get("runnerSnapshot") or {}
    snapshot_root = canonical_relative(str(snapshot.get("root", "")))
    require(snapshot_root.startswith("canary/quality-v3-runner-snapshots/"), "runner.snapshot-not-private")
    require("/tools/" not in f"/{snapshot_root}/" and not snapshot_root.startswith("tools/"), "runner.shared-tools-forbidden")
    require(bool(HEX_64.fullmatch(str(snapshot.get("treeSha256", "")))), "runner.snapshot-tree")
    require(bool(HEX_64.fullmatch(str(snapshot.get("manifestSha256", "")))), "runner.snapshot-manifest")

    isolation = data.get("isolation") or {}
    pipe_name = isolation.get("pipeName")
    require(isinstance(pipe_name, str) and pipe_name == f"KnowledgeClipper.QualityV3.{run_id}", "runner.pipe-not-unique")
    roots = [isolation.get(name) for name in ("settingsRoot", "jobsRoot", "tempRoot")]
    require(all(isinstance(value, str) and run_id in value for value in roots), "runner.root-not-run-owned")
    require(len(set(roots)) == 3, "runner.roots-not-distinct")
    require(isolation.get("ownedProcessTreeOnly") is True and isolation.get("preexistingProcessesTouched") == 0, "runner.process-ownership")
    require(data.get("commonPreflight", {}).get("failureScope") == "global", "runner.preflight-scope")
    require(data.get("aggregation", {}).get("businessJourneyMode") == "independent-collect-all", "runner.cross-journey-fail-fast")

    emitted = set(data.get("promotionContract", {}).get("emittedEvidenceKeys") or [])
    accepted = set(data.get("promotionContract", {}).get("acceptedEvidenceKeys") or [])
    require(emitted == accepted, "runner.promotion-key-contradiction")
    journeys = data.get("journeys") or []
    require(isinstance(journeys, list) and journeys, "runner.journeys")
    ids: set[str] = set()
    for journey in journeys:
        journey_id = journey.get("id")
        require(isinstance(journey_id, str) and journey_id not in ids, "runner.journey-id")
        ids.add(journey_id)
        require(journey.get("failureScope") == "journey-only", "runner.cross-journey-fail-fast", journey_id)
        validate_ui_contract(journey.get("uiContract") or {})
        policies = journey.get("sideEffectPolicies") or []
        require(isinstance(policies, list) and policies, "runner.side-effect-policy", journey_id)
        for policy in policies:
            require(policy.get("auditor") == "independent-proxy-call-log", "runner.side-effect-auditor", journey_id)
    return {"status": "PASS-PRIVATE-RUNNER-PLAN", "runId": run_id, "journeys": sorted(ids)}


def validate_ui_contract(data: dict[str, Any]) -> None:
    require(data.get("assertions") == ["discoverable", "invoke-pattern", "action-id-correlated"], "ui.contract-scope")
    forbidden = json.dumps(data, ensure_ascii=False).lower()
    for token in ("display", "contains", "substring", "rendered", "status-text"):
        require(token not in forbidden, "ui.display-text-contract", token)


def verify_artifact_manifest(manifest_path: Path, artifact_root: Path) -> dict[str, Any]:
    data = load_json(manifest_path)
    require(data.get("schemaVersion") == "quality-v3-artifact-manifest.v1", "artifact.schema")
    require(data.get("consumerMode") == "metadata-entrypoint-all-files-hashed", "artifact.legacy-single-file-consumer")
    files = data.get("files") or []
    require(isinstance(files, list) and files, "artifact.files")
    seen: set[str] = set()
    for item in files:
        relative = canonical_relative(str(item.get("path", "")))
        require(relative not in seen, "artifact.duplicate-file", relative)
        seen.add(relative)
        path = contained(artifact_root, relative)
        require(path.is_file(), "artifact.file-missing", relative)
        require(path.stat().st_size == item.get("bytes"), "artifact.file-bytes", relative)
        require(sha256_file(path) == str(item.get("sha256", "")).upper(), "artifact.file-sha", relative)
    entrypoint = canonical_relative(str((data.get("metadata") or {}).get("entrypoint", "")))
    require(entrypoint in seen, "artifact.entrypoint-undeclared")
    return {"status": "PASS-ARTIFACT-MANIFEST", "files": len(files), "entrypoint": entrypoint}


def validate_anki_probe_journey(evidence_path: Path) -> dict[str, Any]:
    data = load_json(evidence_path)
    require(data.get("schemaVersion") == "quality-v3-anki-probe-journey.v1", "anki.schema")
    ui = validate_ui_action_evidence(data.get("uiAction") or {})
    projection = data.get("typedProjection") or {}
    require(projection.get("actionId") == ui["actionId"] and projection.get("operation") == "anki.probe", "anki.projection-correlation")
    require(projection.get("terminalStatus") in {"ready", "needs_initialization", "service_unreachable"}, "anki.terminal")
    require(projection.get("externalWriteCount") == 0, "anki.write")
    artifact_ref = data.get("artifact") or {}
    artifact_manifest = contained(evidence_path.parent, str(artifact_ref.get("manifest", "")))
    artifact_root = contained(evidence_path.parent, str(artifact_ref.get("root", "")))
    artifact = verify_artifact_manifest(artifact_manifest, artifact_root)
    require(artifact["files"] >= 4, "anki.multifile-artifact-not-exercised")
    effect_results = [validate_side_effect_audit(value, evidence_path.parent) for value in data.get("sideEffectAudits") or []]
    require({value["provider"] for value in effect_results} == {"anki", "remnote", "deepseek", "mineru"}, "anki.effect-provider-set")
    anki = next(value for value in effect_results if value["provider"] == "anki")
    require(anki["writes"] == 0 and anki["calls"] > 0, "anki.readonly-proxy-not-exercised")
    require(all(value["calls"] == 0 for value in effect_results if value["provider"] != "anki"), "anki.unexpected-provider-call")
    return {"status": "PASS-ANKI-PROBE-GATE", "actionId": ui["actionId"], "artifactFiles": artifact["files"], "ankiCalls": anki["calls"], "ankiWrites": 0}


def validate_question_to_anki_journey(evidence_path: Path) -> dict[str, Any]:
    data = load_json(evidence_path)
    require(data.get("schemaVersion") == "quality-v3-question-to-anki-journey.v1", "question-anki.schema")
    ui = validate_ui_action_evidence(data.get("uiAction") or {})
    chain = data.get("typedChain") or []
    require([item.get("eventType") for item in chain] == ["question.accepted", "answer.ready", "cards.created", "anki.export.completed"], "question-anki.typed-chain")
    require(all(item.get("rootActionId") == ui["actionId"] for item in chain), "question-anki.action-correlation")
    require(len({item.get("taskId") for item in chain}) == 1, "question-anki.task-root-drift")
    effects = [validate_side_effect_audit(value, evidence_path.parent) for value in data.get("sideEffectAudits") or []]
    by_provider = {value["provider"]: value for value in effects}
    require(set(by_provider) == {"anki", "remnote", "deepseek", "mineru"}, "question-anki.effect-provider-set")
    require(by_provider["deepseek"]["calls"] == 1 and by_provider["deepseek"]["writes"] == 0, "question-anki.deepseek-call-count")
    require(by_provider["anki"]["writes"] == 1, "question-anki.anki-write-count")
    require(by_provider["remnote"]["calls"] == 0 and by_provider["mineru"]["calls"] == 0, "question-anki.unexpected-provider-call")
    export = data.get("exportReadback") or {}
    require(export.get("destinationIsolation") == "ephemeral-quality-owned", "question-anki.destination-not-isolated")
    require(export.get("createdNoteCount") == 1 and export.get("readBackCount") == 1, "question-anki.readback")
    require(bool(HEX_64.fullmatch(str(export.get("createdNoteIdentitySha256", "")))), "question-anki.note-identity")
    return {"status": "PASS-QUESTION-TO-ANKI-GATE", "actionId": ui["actionId"], "deepseekCalls": 1, "ankiWrites": 1, "readBack": 1}


def cli() -> int:
    parser = argparse.ArgumentParser(description="Independent Quality v3 foundation gates")
    sub = parser.add_subparsers(dest="command", required=True)
    build = sub.add_parser("build-manifest")
    build.add_argument("--candidate-root", type=Path, required=True)
    build.add_argument("--manifest", type=Path, required=True)
    build.add_argument("--expected-commit", required=True)
    build.add_argument("--expected-source-snapshot-sha256", required=True)
    for name in ("runner-plan", "ui-evidence", "side-effects", "anki-probe", "question-to-anki"):
        item = sub.add_parser(name)
        item.add_argument("--input", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "build-manifest":
        result = validate_build_manifest(args.candidate_root, args.manifest, args.expected_commit, args.expected_source_snapshot_sha256)
    elif args.command == "runner-plan":
        result = validate_runner_plan(load_json(args.input))
    elif args.command == "ui-evidence":
        result = validate_ui_action_evidence(load_json(args.input))
    elif args.command == "side-effects":
        result = validate_side_effect_audit(load_json(args.input), args.input.resolve().parent)
    elif args.command == "anki-probe":
        result = validate_anki_probe_journey(args.input)
    else:
        result = validate_question_to_anki_journey(args.input)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(cli())
