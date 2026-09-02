from __future__ import annotations

import json
import os
import sys
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource


WORKER_ROOT = Path(__file__).resolve().parents[1]
PROJECT_ROOT = WORKER_ROOT.parent
SETTINGS_ROOT = PROJECT_ROOT / "contracts" / "core" / "v2.4" / "settings-control"


def load(path: Path) -> object:
    resolved = str(path.resolve())
    readable = Path("\\\\?\\" + resolved) if os.name == "nt" and not resolved.startswith("\\\\?\\") else Path(resolved)
    return json.loads(readable.read_text(encoding="utf-8"))


def registry() -> tuple[Registry, dict[Path, object]]:
    result = Registry()
    loaded: dict[Path, object] = {}
    paths = list((PROJECT_ROOT / "contracts").rglob("*.schema.json")) + list((WORKER_ROOT / "schemas").glob("*.schema.json"))
    for schema_path in sorted(set(paths)):
        schema = load(schema_path)
        Draft202012Validator.check_schema(schema)
        loaded[schema_path.resolve()] = schema
        result = result.with_resource(schema_path.resolve().as_uri(), Resource.from_contents(schema))
    return result, loaded


def check(schema_path: Path, document_path: Path, resources: Registry, loaded: dict[Path, object]) -> None:
    resolved = schema_path.resolve()
    validator = Draft202012Validator(
        loaded[resolved],
        registry=resources,
        _resolver=resources.resolver(resolved.as_uri()),
        format_checker=FormatChecker(),
    )
    errors = sorted(validator.iter_errors(load(document_path)), key=lambda item: list(item.path))
    if errors:
        raise AssertionError(f"{document_path}: {errors[0].json_path}: {errors[0].message}")


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("usage: validate_core_v3_desktop_compatibility.py <absolute-canary-root>")
    root = Path(sys.argv[1]).resolve()
    if not root.is_absolute() or not root.is_dir():
        raise AssertionError("absolute canary root required")
    resources, loaded = registry()
    workflow_schema = SETTINGS_ROOT / "schemas" / "workflow-settings.v3.schema.json"
    snapshot_schema = SETTINGS_ROOT / "schemas" / "task-settings-snapshot.v3.schema.json"
    content_schema = WORKER_ROOT / "schemas" / "content-config-snapshot.v1.1.schema.json"
    result = load(root / "compatibility-result.v1.json")
    fixtures = result["coreV3"]["fixtures"]
    expected = {"ask", "meaning-reading", "writing", "both", "non-language", "target-nonunique", "details-unreliable"}
    actual = {entry["id"] for entry in fixtures}
    if actual != expected:
        raise AssertionError(f"scenario coverage mismatch: {sorted(actual)}")
    count = 0
    for entry in fixtures:
        check(workflow_schema, Path(entry["workflowSettings"]["path"]), resources, loaded)
        check(snapshot_schema, Path(entry["taskSnapshot"]["path"]), resources, loaded)
        check(content_schema, Path(entry["contentConfigSnapshot"]["path"]), resources, loaded)
        snapshot = load(Path(entry["taskSnapshot"]["path"]))
        request = load(Path(entry["workerRequest"]["path"]))
        config_ref = next(ref for ref in request["inputRefs"] if ref["kind"] == "content-config-snapshot")
        content = snapshot["modules"]["content"]
        if content["configArtifactRef"] != config_ref["artifactRef"] or content["expectedSha256"].lower() != config_ref["expectedSha256"].lower() or content["settingsRevisionId"] != request["configSnapshot"]["contentSettingsRevisionId"]:
            raise AssertionError(f"binding mismatch: {entry['id']}")
        count += 3
    print(f"core-v3/content-v1.1 compatibility schemas {count}/{count} PASS; bindings {len(fixtures)}/{len(fixtures)} PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
