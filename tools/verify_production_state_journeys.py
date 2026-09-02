from __future__ import annotations

import argparse
import copy
import glob
import hashlib
import json
import os
import re
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


PROJECT = Path(__file__).resolve().parents[1]
FIXTURES = PROJECT / "tests/Quality.Gates/fixtures/production-state-journeys"
DESKTOP_PROJECT = PROJECT / "desktop/RemNoteQuickClipper.Desktop.csproj"
V1237_QUALITY = PROJECT / "canary/quality-desktop-v1.2.37/20260901T083000Z-independent-final/quality-candidate-receipt.json"
V1237_DESKTOP = PROJECT / "receipts/desktop-integrated-module-actions-v1.2.37-20260901T080500Z.json"
V1237_WINDOWS_UI = PROJECT / "canary/desktop-windows/desktop-v1237-module-actions-20260901T074500Z/windows-ui-automation.log"

EXPECTED_V1237 = {
    "quality": "1EA9F57A231A07331F3757F81F7D6BAFD7CBEF9AA5F7CD95B49D9979B7B58334",
    "desktop": "BBE56793488B0A1C022364E53204EF3E251F78FDD59ADF35FA4B2166A9926F3C",
    "windowsUi": "2E25F3DD31BBD767D835E66B88EBA4978C98AC83A447FCA85DD07D2C958AEB8A",
}


class GateFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise GateFailure(message)


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest().upper()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def rel(path: Path) -> str:
    return path.resolve().relative_to(PROJECT.resolve()).as_posix()


def evidence(path: Path) -> dict[str, Any]:
    return {"path": rel(path), "bytes": path.stat().st_size, "sha256": sha256_file(path)}


def verify_evidence(reference: dict[str, Any]) -> dict[str, Any]:
    require(isinstance(reference, dict) and isinstance(reference.get("path"), str), "evidence reference path missing")
    path = contained(PROJECT, reference["path"])
    require(path.is_file(), f"evidence file missing: {reference['path']}")
    observed = evidence(path)
    require(observed["sha256"] == str(reference.get("sha256", "")).upper(), f"evidence SHA drift: {reference['path']}")
    require(observed["bytes"] == int(reference.get("bytes", -1)), f"evidence bytes drift: {reference['path']}")
    return observed


def tree_summary(root: Path) -> dict[str, Any]:
    items = []
    native_root = ("\\\\?\\" + str(root.resolve())) if os.name == "nt" else str(root.resolve())
    for directory, _, names in os.walk(native_root):
        for name in names:
            native_path = Path(directory) / name
            display_path = Path(str(native_path).removeprefix("\\\\?\\"))
            items.append({"path": display_path.relative_to(root.resolve()).as_posix(), "bytes": native_path.stat().st_size, "sha256": sha256_file(native_path)})
    items.sort(key=lambda item: item["path"])
    material = "\n".join(f"{item['path']}\t{item['bytes']}\t{item['sha256'].lower()}" for item in items)
    return {"fileCount": len(items), "totalBytes": sum(item["bytes"] for item in items), "treeSha256": sha256_bytes(material.encode("utf-8")), "treeHashAlgorithm": "path-tab-bytes-tab-sha256-lower-lf"}


def source_evidence(path: Path, line: int, text: str) -> dict[str, Any]:
    return {"path": rel(path), "line": line, "text": text.strip()[:240], "fileSha256": sha256_file(path)}


def occurrences(path: Path, pattern: str) -> list[dict[str, Any]]:
    regex = re.compile(pattern)
    return [source_evidence(path, index, line) for index, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1) if regex.search(line)]


def braced_block(path: Path, marker: str) -> tuple[str, int]:
    text = path.read_text(encoding="utf-8-sig")
    start = text.find(marker)
    require(start >= 0, f"source marker missing: {path}: {marker}")
    brace = text.find("{", start)
    require(brace >= 0, f"source block brace missing: {path}: {marker}")
    depth = 0
    for index in range(brace, len(text)):
        if text[index] == "{":
            depth += 1
        elif text[index] == "}":
            depth -= 1
            if depth == 0:
                return text[start:index + 1], text[:start].count("\n") + 1
    raise GateFailure(f"source block did not close: {path}: {marker}")


def inspect_architecture() -> dict[str, Any]:
    violations: list[dict[str, Any]] = []
    project_text = DESKTOP_PROJECT.read_text(encoding="utf-8-sig")
    project_refs = occurrences(DESKTOP_PROJECT, r"ProjectReference.+Orchestrator")
    for item in project_refs:
        violations.append({"code": "desktop-ui-references-orchestrator-project", "evidence": item})

    desktop_module = PROJECT / "src/DesktopModuleSettings.cs"
    for symbol, code in (("OrchestratorEngine", "desktop-ui-instantiates-orchestrator-engine"), ("RegistryLoader", "desktop-ui-instantiates-registry-loader")):
        for item in occurrences(desktop_module, rf"new\s+{symbol}\s*\("):
            violations.append({"code": code, "evidence": item})

    compiled_sources = [PROJECT / "src/DesktopModuleSettings.cs", PROJECT / "src/RemNoteQuickClipper.Desktop.Core/QuestionAnswerArtifactReader.cs", PROJECT / "src/RemNoteQuickClipper.Desktop.Core/OcrHumanResolutionArtifactReader.cs"]
    product_sources: list[Path] = []
    for include in re.findall(r'<Compile\s+Include="([^"]+\.cs)"', project_text):
        pattern = str(DESKTOP_PROJECT.parent / include.replace("\\", os.sep))
        product_sources.extend(Path(value).resolve() for value in glob.glob(pattern))
    product_sources = sorted(set(product_sources))
    verified_reader_definitions = []
    for path in product_sources:
        verified_reader_definitions.extend(occurrences(path, r"\b(class|interface)\s+I?VerifiedArtifactReader\b"))
    if not verified_reader_definitions:
        violations.append({"code": "unified-verified-artifact-reader-missing", "evidence": evidence(DESKTOP_PROJECT)})
    for path in compiled_sources:
        if "VerifiedArtifactReader" not in path.read_text(encoding="utf-8-sig"):
            violations.append({"code": "production-artifact-consumer-not-unified", "consumer": rel(path), "evidence": evidence(path)})

    question_form = PROJECT / "src/DesktopUi.cs"
    question_block, question_line = braced_block(question_form, "internal sealed class QuestionForm")
    for match in re.finditer(r"TaskStatusQueryRecent", question_block):
        line = question_line + question_block[:match.start()].count("\n")
        source_line = question_form.read_text(encoding="utf-8-sig").splitlines()[line - 1]
        violations.append({"code": "active-question-session-uses-query-recent", "evidence": source_evidence(question_form, line, source_line)})
    for match in re.finditer(r"AcceptButton\s*=\s*submit", question_block):
        line = question_line + question_block[:match.start()].count("\n")
        source_line = question_form.read_text(encoding="utf-8-sig").splitlines()[line - 1]
        violations.append({"code": "question-enter-fixed-to-initial-submit", "evidence": source_evidence(question_form, line, source_line)})

    controller = PROJECT / "src/RemNoteQuickClipper.Desktop.Core/DesktopController.cs"
    open_block, open_line = braced_block(controller, "private void OpenQuestion()")
    for match in re.finditer(r"QueryRecentTask\s*\(", open_block):
        line = open_line + open_block[:match.start()].count("\n")
        source_line = controller.read_text(encoding="utf-8-sig").splitlines()[line - 1]
        violations.append({"code": "question-open-queries-global-recent-task", "evidence": source_evidence(controller, line, source_line)})

    promotion_tool = PROJECT / "tools/verify_v1237_desktop_candidate.py"
    promotion_text = promotion_tool.read_text(encoding="utf-8-sig")
    if "userHandTestEntrypoint\": True" in promotion_text and "sealedCandidateProcessStateful" not in promotion_text:
        line = next(index for index, value in enumerate(promotion_text.splitlines(), 1) if '"userHandTestEntrypoint": True' in value)
        violations.append({"code": "synthetic-evidence-promoted-to-user-handtest", "evidence": source_evidence(promotion_tool, line, promotion_text.splitlines()[line - 1])})

    return {
        "status": "passed" if not violations else "failed",
        "rulesEvaluated": [
            "desktop-ui-no-orchestrator-reference-or-instantiation",
            "production-artifacts-use-unified-verified-reader",
            "active-question-session-does-not-query-global-recent",
            "native-enter-routes-by-question-session-state",
            "synthetic-display-result-cannot-promote-candidate",
        ],
        "desktopProject": evidence(DESKTOP_PROJECT),
        "projectReferencesOrchestrator": len(project_refs),
        "verifiedArtifactReaderDefinitions": len(verified_reader_definitions),
        "violations": violations,
        "sourceOnly": True,
        "productionFilesModified": 0,
    }


def contained(root: Path, relative: str) -> Path:
    require(relative and not Path(relative).is_absolute(), f"fixture path is not relative: {relative}")
    resolved = (root / relative.replace("/", os.sep)).resolve()
    require(resolved == root.resolve() or root.resolve() in resolved.parents, f"fixture path escapes root: {relative}")
    return resolved


def verify_anki_artifact(root: Path) -> dict[str, Any]:
    manifest_path = root / "artifact.json"
    manifest = load(manifest_path)
    require(manifest.get("schemaVersion") == "1.0.0", "Anki artifact schemaVersion drift")
    files = manifest.get("files")
    require(isinstance(files, list) and len(files) >= 4, "Anki production-shape fixture must preserve the formal multi-file artifact")
    paths = [item.get("path") for item in files]
    require(all(isinstance(value, str) and value for value in paths), "Anki artifact file path missing")
    require(len(paths) == len(set(paths)), "Anki artifact has duplicate file paths")
    entrypoint = (manifest.get("metadata") or {}).get("entrypoint")
    require(isinstance(entrypoint, str) and entrypoint, "Anki artifact metadata.entrypoint missing")
    require(paths.count(entrypoint) == 1, "Anki artifact metadata.entrypoint must identify exactly one declared file")
    checked = []
    for item in files:
        path = contained(root, item["path"])
        require(path.is_file(), f"Anki artifact file missing: {item['path']}")
        data = path.read_bytes()
        require(len(data) == item.get("bytes"), f"Anki artifact bytes drift: {item['path']}")
        require(sha256_bytes(data) == str(item.get("sha256", "")).upper(), f"Anki artifact SHA drift: {item['path']}")
        checked.append({"path": item["path"], "bytes": len(data), "sha256": sha256_bytes(data)})
    payload_path = contained(root, entrypoint)
    payload = load(payload_path)
    require(payload.get("schemaVersion") == "anki-connection-status.v1", "Anki entrypoint is not connection status")
    require(payload.get("operation") == "probe" and payload.get("effectClass") == "provider-read-only", "Anki probe boundary drift")
    require(payload.get("externalMutation") is False, "Anki read-only probe claims mutation")
    return {"status": "passed", "filesChecked": len(checked), "entrypoint": entrypoint, "sidecarsAccepted": len(checked) - 1, "externalMutation": False}


def verify_remnote_journey(data: dict[str, Any]) -> dict[str, Any]:
    require(data.get("schemaVersion") == "knowledge-clipper-remnote-stateful-journey.v1", "RemNote journey schema drift")
    steps = data.get("steps") or []
    by_name = {item.get("name"): item for item in steps}
    required = ["initial", "discovery", "probe-same-window", "save", "reopen-after-restart", "probe-after-restart", "next-task-snapshot"]
    require(list(by_name) == required, "RemNote journey order or cardinality drift")
    initial = by_name["initial"]
    require(initial["profile"]["enabled"] is False and initial["profile"]["rootRemId"] is None and initial["profile"]["rootTitle"], "RemNote initial disabled/rootTitle-only state missing")
    discovery = by_name["discovery"]
    require(discovery["bindingState"] == "verified_pending" and discovery["exactReadback"] is True and discovery["writes"] == 0, "RemNote discovery is not verified_pending read-only")
    pending = discovery["target"]
    same_window = by_name["probe-same-window"]
    require(same_window["target"] == pending and same_window["displayedTarget"] == pending, "RemNote same-window probe contradicts the displayed pending binding")
    require(same_window["writes"] == 0, "RemNote same-window probe wrote externally")
    saved = by_name["save"]
    require(saved["persistedTarget"] == pending and saved["revision"] != initial["revision"], "RemNote save did not persist a new revision")
    reopened = by_name["reopen-after-restart"]
    require(reopened["persistedTarget"] == pending and reopened["activeProfileTarget"] == pending, "RemNote restart lost the saved active profile binding")
    after = by_name["probe-after-restart"]
    require(after["target"] == pending and after["writes"] == 0, "RemNote post-restart probe target drift")
    snapshot = by_name["next-task-snapshot"]
    require(snapshot["target"] == pending and snapshot["settingsRevision"] == saved["revision"], "RemNote next task snapshot does not bind the saved revision")
    cancel = data.get("cancelScenario") or {}
    require(cancel.get("persistedAfterCancel") == cancel.get("persistedBeforeCancel"), "RemNote cancel mutated persisted settings")
    ambiguous = data.get("ambiguousScenario") or {}
    require(ambiguous.get("persistedAfterAmbiguous") == ambiguous.get("persistedBeforeAmbiguous") and ambiguous.get("admission") == "blocked", "RemNote ambiguous discovery overwrote persisted settings")
    return {"status": "passed", "steps": len(steps), "binding": pending, "restartVerified": True, "nextTaskSnapshotVerified": True, "writes": 0}


def verify_question_journey(data: dict[str, Any]) -> dict[str, Any]:
    require(data.get("schemaVersion") == "knowledge-clipper-question-native-keyboard-journey.v1", "Question journey schema drift")
    happy = data.get("happyPath") or {}
    require(happy.get("process") == "KnowledgeClipper.exe" and happy.get("transport") == "NamedPipe", "Question journey is not production-process shaped")
    require(happy.get("inputMethod") == "native-keyboard", "Question journey bypasses native keyboard routing")
    actions = happy.get("actions") or []
    names = [item.get("action") for item in actions]
    require(names == ["question.submit", "answer.ready", "question.follow-up", "answer.ready", "question.make-cards"], "Question sequential action order drift")
    require(sum(name == "question.submit" for name in names) == 1 and sum(name == "question.follow-up" for name in names) == 1, "Question journey must emit exactly one submit and one follow-up")
    task_ids = {item.get("taskId") for item in actions if item.get("taskId")}
    require(len(task_ids) == 1, "Question follow-up created or switched the task root")
    require(all(item.get("queryRecent") is not True for item in actions), "Active QuestionSession used query_recent")
    unresolved = data.get("unresolvedPath") or {}
    unresolved_actions = unresolved.get("actions") or []
    require(sum(item.get("action") == "question.submit" for item in unresolved_actions) == 1 and sum(item.get("action") == "question.follow-up" for item in unresolved_actions) == 1, "Unresolved question path lost initial/follow-up cardinality")
    terminal = unresolved.get("terminal") or {}
    require(terminal.get("state") == "attention_required" and terminal.get("loading") is False, "Unresolved question did not terminate as attention_required")
    require(terminal.get("reasonCode"), "Unresolved question lost its reasonCode")
    return {"status": "passed", "submitCount": 1, "followUpCount": 1, "taskId": next(iter(task_ids)), "unresolvedTerminal": "attention_required"}


def verify_candidate_promotion(data: dict[str, Any], allow_fixture: bool = False) -> dict[str, Any]:
    require(data.get("schemaVersion") == "knowledge-clipper-candidate-stateful-evidence.v1", "candidate evidence schema drift")
    promotion = data.get("promotion") or {}
    if promotion.get("userHandTestEntrypoint") is not True:
        return {"status": "not-promoted", "userHandTestEntrypoint": False}
    require(data.get("evidenceLevel") == "sealed-candidate-process-stateful", "user hand-test promotion lacks sealed candidate process-stateful evidence")
    candidate = data.get("candidate") or {}
    runtime = data.get("runtime") or {}
    require(runtime.get("desktopProcess") == "KnowledgeClipper.exe", "candidate did not start the production Desktop process")
    require(str(runtime.get("orchestratorProcess", "")).startswith("authority/"), "candidate did not use its embedded Orchestrator")
    require(runtime.get("transport") == "NamedPipe" and runtime.get("persistenceStore") == "isolated-real", "candidate did not use Named Pipe and a real isolated persistence store")
    require(runtime.get("injectedHost") is False and runtime.get("sharedTestCoreDll") is False and runtime.get("manualDisplayState") is False, "candidate used injected/test-only runtime state")
    require(runtime.get("artifactReader") == "production-unified", "candidate did not use the production unified artifact reader")
    journeys = data.get("journeys") or {}
    require(set(journeys) == {"anki", "remnote", "question"} and all(value.get("status") == "passed" for value in journeys.values()), "candidate stateful journey set is incomplete")
    if data.get("fixtureOnly") is True:
        require(allow_fixture, "fixture-only evidence cannot promote a candidate")
        require(candidate.get("sealedTreeSha256") and len(candidate["sealedTreeSha256"]) == 64 and candidate.get("manifestSha256") and len(candidate["manifestSha256"]) == 64, "candidate fixture identity is incomplete")
        return {"status": "fixture-contract-passed-not-promotable", "userHandTestEntrypoint": False, "evidenceLevel": data["evidenceLevel"], "journeys": sorted(journeys)}
    source_gate = verify_evidence(data.get("sourceGate") or {})
    source_receipt = load(PROJECT / source_gate["path"])
    require(source_receipt.get("status") == "PASS-ARCHITECTURE-SOURCE-GATE" and source_receipt.get("delta", {}).get("promotionAllowed") is True and source_receipt.get("delta", {}).get("currentViolationCount") == 0, "candidate promotion source gate is not clean")
    root_value = candidate.get("root")
    require(isinstance(root_value, str) and root_value.startswith("releases/"), "candidate root is not an append-only release root")
    root = contained(PROJECT, root_value)
    require(root.is_dir(), "candidate root is missing")
    manifest = verify_evidence(candidate.get("manifest") or {})
    require((PROJECT / manifest["path"]).resolve().parent == root.resolve(), "candidate manifest is not inside the sealed root")
    require((root / runtime["desktopProcess"]).is_file(), "production Desktop entrypoint is missing from candidate")
    require((root / runtime["orchestratorProcess"]).is_file(), "embedded Orchestrator entrypoint is missing from candidate")
    declared_tree = candidate.get("sealed") or {}
    observed_tree = tree_summary(root)
    require(declared_tree == observed_tree, "candidate sealed tree drift")
    runtime_evidence = runtime.get("evidence") or {}
    require(set(runtime_evidence) == {"startup", "namedPipe", "persistence", "artifactParser", "actionLog", "processCleanup"}, "candidate runtime evidence set incomplete")
    checked_runtime = {name: verify_evidence(reference) for name, reference in runtime_evidence.items()}
    checked_journeys = {}
    for name, journey in journeys.items():
        checked_journeys[name] = verify_evidence(journey.get("receipt") or {})
    return {"status": "passed", "userHandTestEntrypoint": True, "evidenceLevel": data["evidenceLevel"], "sourceGate": source_gate, "candidateTree": observed_tree, "runtimeEvidence": checked_runtime, "journeyEvidence": checked_journeys}


def validate_fixtures() -> dict[str, Any]:
    accepted = {
        "anki": verify_anki_artifact(FIXTURES / "anki/valid"),
        "remnote": verify_remnote_journey(load(FIXTURES / "remnote/valid/stateful.json")),
        "question": verify_question_journey(load(FIXTURES / "question/valid/native-keyboard.json")),
        "candidate": verify_candidate_promotion(load(FIXTURES / "candidate/valid/sealed-process-stateful.json"), allow_fixture=True),
    }
    invalid = [
        (verify_remnote_journey, load(FIXTURES / "remnote/invalid/split-form-and-persisted-state.json")),
        (verify_question_journey, load(FIXTURES / "question/invalid/double-submit-stuck-loading.json")),
        (verify_candidate_promotion, load(FIXTURES / "candidate/invalid/synthetic-component-promoted.json")),
        (verify_candidate_promotion, load(FIXTURES / "candidate/invalid/shared-test-core-dll.json")),
    ]
    rejected = 0
    for verifier, value in invalid:
        try:
            verifier(value)
        except GateFailure:
            rejected += 1
    require(rejected == len(invalid), "one or more invalid production-state fixtures were accepted")
    levels = load(FIXTURES / "evidence-levels.v1.json")
    require([item["id"] for item in levels["levels"]] == ["unit-contract", "synthetic-component", "production-shape", "sealed-candidate-process-stateful", "external-live"], "evidence level order drift")
    require([item["id"] for item in levels["levels"] if item["mayEnableUserHandTestEntrypoint"]] == ["sealed-candidate-process-stateful"], "promotion authority leaked to a lower or external-only evidence level")
    return {"status": "passed", "validAccepted": len(accepted), "invalidRejected": rejected, "accepted": accepted, "levels": levels}


def verify_v1237_history() -> dict[str, Any]:
    exact = {"quality": V1237_QUALITY, "desktop": V1237_DESKTOP, "windowsUi": V1237_WINDOWS_UI}
    observed = {}
    for name, path in exact.items():
        require(path.is_file(), f"v1.2.37 evidence missing: {path}")
        require(sha256_file(path) == EXPECTED_V1237[name], f"v1.2.37 evidence drift: {path}")
        observed[name] = evidence(path)
    quality = load(V1237_QUALITY)
    desktop = load(V1237_DESKTOP)
    require(quality.get("status") == "PASS-CANDIDATE-BOUND-USER-HANDTEST-GO", "v1.2.37 historical quality status was rewritten")
    require(desktop.get("status") == "PASS-CANDIDATE-BOUND-USER-HANDTEST-READY", "v1.2.37 historical Desktop status was rewritten")
    return {
        "status": "invalidated-for-user-handtest-only",
        "historicalEvidence": observed,
        "invalidatedClaims": ["PASS-CANDIDATE-BOUND-USER-HANDTEST-GO", "PASS-CANDIDATE-BOUND-USER-HANDTEST-READY", "userHandTestEntrypoint=true"],
        "preservedClaims": ["candidate-manifest-and-tree", "authority-binding", "startup", "single-instance", "named-pipe-query", "secret-scan", "synthetic-component-assertions"],
        "reasonCodes": ["production-artifact-consumer-uncovered", "settings-state-transition-uncovered", "native-keyboard-follow-up-uncovered", "sealed-candidate-process-stateful-evidence-missing"],
        "oldReceiptsModified": False,
    }


def inspect_local_failure_evidence() -> dict[str, Any]:
    result: dict[str, Any] = {"status": "optional-local-evidence-not-found", "externalCalls": 0, "writes": 0}
    local = Path(os.environ.get("LOCALAPPDATA", "")) / "KnowledgeClipper"
    log = local / "logs/desktop-ui.log"
    if log.is_file():
        lines = [line for line in log.read_text(encoding="utf-8-sig", errors="replace").splitlines() if line.startswith("2026-09-01T15:31:")]
        submit = sum("action=question.submit" in line for line in lines)
        follow = sum("action=question.follow-up" in line for line in lines)
        result = {"status": "observed", "desktopLog": {"path": "%LOCALAPPDATA%/KnowledgeClipper/logs/desktop-ui.log", "bytes": log.stat().st_size, "sha256": sha256_file(log)}, "window": "2026-09-01T15:31", "submitCount": submit, "followUpCount": follow, "externalCalls": 0, "writes": 0}
    manifests: list[Path] = []
    action_root = local / "module-actions"
    if action_root.is_dir():
        native_root = ("\\\\?\\" + str(action_root.resolve())) if os.name == "nt" else str(action_root.resolve())
        for directory, _, names in os.walk(native_root):
            if "anki-connection-status.artifact.json" in names:
                manifests.append(Path(directory) / "anki-connection-status.artifact.json")
        manifests.sort(key=lambda item: item.stat().st_mtime, reverse=True)
    for manifest_path in manifests:
        manifest = load(manifest_path)
        files = manifest.get("files") or []
        if len(files) >= 4 and (manifest.get("metadata") or {}).get("entrypoint"):
            display_path = Path(str(manifest_path).removeprefix("\\\\?\\"))
            result["ankiArtifact"] = {"path": "%LOCALAPPDATA%/KnowledgeClipper/" + display_path.relative_to(local).as_posix(), "bytes": manifest_path.stat().st_size, "sha256": sha256_file(manifest_path), "fileCount": len(files), "entrypoint": manifest["metadata"]["entrypoint"]}
            break
    return result


def build_baseline() -> dict[str, Any]:
    architecture = inspect_architecture()
    fixtures = validate_fixtures()
    history = verify_v1237_history()
    quality_assets = {
        "gate": evidence(PROJECT / "tools/verify_production_state_journeys.py"),
        "tests": evidence(PROJECT / "tests/Quality.Gates/test_production_state_journey_gate.py"),
        "candidateEvidenceSchema": evidence(FIXTURES / "candidate-stateful-evidence.schema.json"),
        "instructions": evidence(PROJECT / "tests/Quality.Gates/PRODUCTION-STATE-JOURNEY-GATE.md"),
        "fixtures": tree_summary(FIXTURES),
    }
    return {
        "schemaVersion": "knowledge-clipper-production-state-journey-quality.v1",
        "status": "NO-GO-CURRENT-SOURCE-ARCHITECTURE" if architecture["violations"] else "WAITING-SEALED-CANDIDATE-PROCESS-STATEFUL",
        "candidateBound": False,
        "architecture": architecture,
        "productionShapeFixtures": fixtures,
        "qualityAssets": quality_assets,
        "v1237Invalidation": history,
        "localFailureEvidence": inspect_local_failure_evidence(),
        "promotion": {"userHandTestEntrypoint": False, "reason": "sealed candidate process-stateful evidence is absent"},
        "futureCandidateAdmission": {
            "requiredEvidenceLevel": "sealed-candidate-process-stateful",
            "requiredProcesses": ["KnowledgeClipper.exe", "candidate/authority/win-x64/orchestrator/KnowledgeClipper.Orchestrator.exe"],
            "requiredTransport": "NamedPipe",
            "requiredPersistenceStore": "isolated-real",
            "forbidden": ["injected-host", "shared-test-core-dll", "manual-display-state", "query-recent-for-active-question-session"],
            "requiredJourneys": ["anki-multifile-artifact-consumer", "remnote-discovery-persist-restart-snapshot", "native-keyboard-question-follow-up"],
        },
        "sideEffects": {"productionSourceWrites": 0, "workerRuns": 0, "externalCalls": 0, "destinationWrites": 0, "speakerInvocations": 0},
    }


def write_append_only(output_root: Path) -> dict[str, Any]:
    require(not output_root.exists(), f"append-only output exists: {output_root}")
    output_root.mkdir(parents=True)
    result = build_baseline()
    created = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    invalidation = {
        "schemaVersion": "knowledge-clipper-v1.2.37-user-handtest-invalidation.v1",
        "createdAt": created,
        **result["v1237Invalidation"],
        "supersededByGate": "knowledge-clipper-production-state-journey-quality.v1",
        "promotionAfterInvalidation": {"userHandTestEntrypoint": False, "formalRelease": "NO-GO-UNCHANGED"},
    }
    invalidation_path = output_root / "v1.2.37-user-handtest-invalidation.json"
    invalidation_path.write_text(json.dumps(invalidation, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    result["createdAt"] = created
    result["v1237InvalidationReceipt"] = evidence(invalidation_path)
    receipt_path = output_root / "quality-receipt.json"
    receipt_path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    readable_path = output_root / "READABLE.md"
    readable_path.write_text(
        "# Production-state journey gate baseline\n\n"
        f"- Status: `{result['status']}`\n"
        f"- Current architecture violations: `{len(result['architecture']['violations'])}`\n"
        "- v1.2.37 user-handtest GO/READY: `INVALIDATED`; packaging/startup/authority evidence remains valid.\n"
        "- Promotion floor: `sealed-candidate-process-stateful`. Synthetic component and production-shape fixtures cannot enable the hand-test entrypoint.\n"
        "- No production source, worker, candidate, frozen contract, or historical receipt was modified. External calls and writes: `0`.\n",
        encoding="utf-8",
    )
    return {"status": result["status"], "receipt": evidence(receipt_path), "invalidation": evidence(invalidation_path), "readable": evidence(readable_path)}


def main() -> int:
    parser = argparse.ArgumentParser(description="KnowledgeClipper production-state journey quality gate")
    parser.add_argument("--output-root", type=Path)
    parser.add_argument("--candidate-evidence", type=Path)
    args = parser.parse_args()
    if args.candidate_evidence:
        print(json.dumps(verify_candidate_promotion(load(args.candidate_evidence.resolve())), ensure_ascii=False, indent=2))
        return 0
    if args.output_root:
        print(json.dumps(write_append_only(args.output_root.resolve()), ensure_ascii=False, indent=2))
        return 0
    print(json.dumps(build_baseline(), ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
