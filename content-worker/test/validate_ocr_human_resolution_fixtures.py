import argparse
import hashlib
import json
from pathlib import Path

from jsonschema import Draft202012Validator


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def evidence(path: Path):
    payload = path.read_bytes()
    return {"path": str(path), "bytes": len(payload), "sha256": hashlib.sha256(payload).hexdigest().upper()}


parser = argparse.ArgumentParser()
parser.add_argument("--worker-root", required=True)
parser.add_argument("--live-run-root", required=True)
parser.add_argument("--receipt", required=True)
args = parser.parse_args()

worker_root = Path(args.worker_root).resolve()
live_run_root = Path("\\\\?\\" + str(Path(args.live_run_root).resolve()))
receipt_path = Path(args.receipt).resolve()

def exactly_one(pattern: str):
    matches = list(live_run_root.rglob(pattern))
    if len(matches) != 1:
        raise AssertionError(f"expected exactly one {pattern}, got {len(matches)}")
    return matches[0]


generated = [
    (worker_root / "schemas/content-input.v5.schema.json", exactly_one("content-input.v5.json")),
    (worker_root / "schemas/ocr-human-resolution-bundle.v1.schema.json", exactly_one("ocr-human-resolution-bundle.schema-v1.json")),
    (worker_root / "schemas/learning-bundle.v3.schema.json", exactly_one("learning-bundle.schema-v3.json")),
]
validated = []
for schema_path, document_path in generated:
    schema = load(schema_path)
    Draft202012Validator.check_schema(schema)
    Draft202012Validator(schema, format_checker=Draft202012Validator.FORMAT_CHECKER).validate(load(document_path))
    validated.append({"schema": evidence(schema_path), "document": evidence(document_path)})

command_schema_path = worker_root / "schemas/ocr-human-resolution-command.v1.schema.json"
command_schema = load(command_schema_path)
Draft202012Validator.check_schema(command_schema)
command_validator = Draft202012Validator(command_schema)
command_root = worker_root / "fixtures/ocr-human-resolution-v1/commands"
valid_commands = []
for name in ["accept-original.valid.json", "user-correction.valid.json"]:
    command_path = command_root / name
    command_validator.validate(load(command_path))
    valid_commands.append(evidence(command_path))
invalid_commands = []
for name in ["unknown-action.invalid.json", "correction-missing.invalid.json"]:
    command_path = command_root / name
    errors = list(command_validator.iter_errors(load(command_path)))
    if not errors:
        raise AssertionError(f"invalid command unexpectedly passed: {name}")
    invalid_commands.append({"fixture": evidence(command_path), "diagnosticCount": len(errors)})

receipt = {
    "schemaVersion": "content-ocr-human-resolution-schema-validation/1.0.0",
    "status": "PASS",
    "generatedArtifacts": validated,
    "commandSchema": evidence(command_schema_path),
    "validCommands": valid_commands,
    "invalidCommands": invalid_commands,
    "assertions": [
        "standard-input/5.0.0 validates the real human-corrected CANAR continuation",
        "ocr-human-resolution-bundle/1.0.0 and learning-bundle/3.0.0 validate",
        "accept_original and user_correction commands validate",
        "unknown action and missing correction commands fail closed",
    ],
}
receipt_path.parent.mkdir(parents=True, exist_ok=True)
with receipt_path.open("x", encoding="utf-8", newline="\n") as output:
    json.dump(receipt, output, ensure_ascii=False, indent=2)
    output.write("\n")
print(json.dumps({"status": "PASS", "receipt": evidence(receipt_path)}, ensure_ascii=False))
