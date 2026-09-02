from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable

from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource


PROJECT_ROOT = Path(__file__).resolve().parents[1]
CONTRACTS_ROOT = PROJECT_ROOT / "contracts" / "v1"
SCHEMAS_ROOT = CONTRACTS_ROOT / "schemas"
AUTHORITY_REGISTRY = CONTRACTS_ROOT / "registry" / "worker-registry.v1.json"
CHECKPOINT_ID = "contracts-v1-20260825T115938Z"
CHECKPOINT_RECEIPT_RELATIVE = Path("receipts/contracts-v1-stable-checkpoint-20260825T115938Z.json")
CHECKPOINT_RECEIPT_SHA256 = "1CB738FE133F6645B9757E246E2FBC210785086342F70BAD32B5015BD19E1B1E"
IMAGE_ONBOARDING_RECEIPT_RELATIVE = Path("receipts/image-ocr-worker-cli-validation-20260825T160541Z.json")
IMAGE_ONBOARDING_RECEIPT_SHA256 = "7EC0FFA3C2165B5B063E67F6D8B878227743C9254C1376DE71925DBD0301B072"
IMAGE_SCENARIO_MATRIX_RECEIPT_RELATIVE = Path("receipts/image-ocr-offline-scenario-matrix-validation-20260825T234615Z.json")
IMAGE_SCENARIO_MATRIX_RECEIPT_SHA256 = "9C9839C7E9531BF9EBEBDD0B9F44F5CFD97B69DE05A8C4B293A3023705AB6C53"
IMAGE_TERMINAL_FAILURE_RECEIPT_RELATIVE = Path("receipts/image-ocr-terminal-failure-validation-20260826T014703Z.json")
IMAGE_TERMINAL_FAILURE_RECEIPT_SHA256 = "904C4898416A59B6BD3D2A5C1A276190ADA602EA95E0D85AEE8B91298E07C6A8"
IMAGE_UNKNOWN_EXTERNAL_RECEIPT_RELATIVE = Path("receipts/image-ocr-unknown-external-state-validation-20260826T015142Z.json")
IMAGE_UNKNOWN_EXTERNAL_RECEIPT_SHA256 = "4CE67AB765A7199FDA42B06079E31D6771A82EA5DA18157AD956BED46AB528E0"
IMAGE_IMMUTABLE_EVIDENCE_RELATIVE = Path("releases/1.0.0-rc.1-preseal.6/win-x64/workers/image-ocr/RemNoteQuickClipper.ImageOcr.Worker.exe")
IMAGE_BRAND_RECEIPT_RELATIVE = Path("receipts/knowledge-clipper-image-ocr-public-identity-audit-20260826T030424Z.json")
IMAGE_BRAND_RECEIPT_SHA256 = "64D4BD96577325518C47F361DE9E79FBDA04C2743CFFC8D98BA06183D9773864"
IMAGE_BRAND_VALIDATION_RECEIPT_RELATIVE = Path("receipts/knowledge-clipper-image-ocr-destination-neutral-validation-20260826T030450Z.json")
IMAGE_BRAND_VALIDATION_RECEIPT_SHA256 = "682E7156EA8F31E9B6331CDEB0CBC0AA2C263AD2ED4B8744992F28DFD5D0077A"
DESKTOP_CONTRACT_RECEIPT_RELATIVE = Path("contracts/desktop-automation/v1/contract-receipt.json")
DESKTOP_CONTRACT_RECEIPT_SHA256 = "A326242A31A55088EC747891BA7FFE553B1250B24281F72B8738A6506C97EF06"
DESKTOP_CONTRACT_TREE_SHA256 = "D4D0CCA8D1210A90F484A6D31643A8312F67AB6F1E53FC2367308AF009194A6E"
DESKTOP_CLICKABILITY_RECEIPT_RELATIVE = Path(
    "receipts/knowledge-clipper-window-controls-canary-knowledge-clipper-window-controls-20260826T030631Z.json"
)
DESKTOP_CLICKABILITY_RECEIPT_SHA256 = "B56892A671A4DB9471D37D0DDD9C2D07F698BAD403869F5A0F29B0B917299C75"
DESKTOP_RELEASE_EXE_SHA256 = "29320D1CCAA252057D011490866CC7533901740C5C6921359B7A2F9F58C4720C"
ANKI_BUSINESS_CANARY_RELATIVE = Path(
    "workers/anki-exporter/canary/r20260825T140547789736-3155faa5/canary-result.json"
)
ANKI_BUSINESS_CANARY_SHA256 = "AF4872C6E65E18479DAB566C68C91D8C2F9066793384EDE1B7E2475DCBC3CC22"
ANKI_IDENTITY_NOOP_RECEIPT_RELATIVE = Path(
    "workers/anki-exporter/canary/identity-noop-20260825T141249170427-0228c210/identity-noop-result.json"
)
ANKI_IDENTITY_NOOP_RECEIPT_SHA256 = "F9D6BBA18A6D69286EE62A6E667C3D04C718E380BFA93EEECF86D0FFD69D749B"
ANKI_IDENTITY_NOOP_RESULT_RELATIVE = Path(
    "workers/anki-exporter/canary/identity-noop-20260825T141249170427-0228c210/01-noop/runs/run-01-noop/stages/anki-export/attempts/attempt-1/result.json"
)
ANKI_IDENTITY_NOOP_RESULT_SHA256 = "9C689FB70F4461F6D393CF9AAAE861510F2E71BFDF99DCA60B767C40BDC0F0E9"
ANKI_EXE_VALIDATION_RECEIPT_RELATIVE = Path(
    "workers/anki/canary/xv-20260826T015251-1212/exe-validation-result.json"
)
ANKI_EXE_VALIDATION_RECEIPT_SHA256 = "EF696CCCD9467AB58E6B7600FDBE73460AF03F16DC2B33782573FC64E75D2D7F"
ANKI_EXE_SHA256 = "AC1F37AB4486B59BEF50A40214BA8D7C7D3B01BC569461C8539D926308E9BB96"
ANKI_EXE_BYTES = 10207461
ANKI_QUALITY_REFRESH_RECEIPT_RELATIVE = Path("receipts/anki-worker-quality-refresh-20260826T020000Z.json")
ANKI_QUALITY_REFRESH_RECEIPT_SHA256 = "12515B07704CF2BD67072A48E257BDD418D0C6F5410AB5382CE65CA8BCBA01C9"
ANKI_CONSUMER_RECEIPT_RELATIVE = Path("workers/anki-exporter/canary/ca/260826015306-877f/receipt.json")
ANKI_CONSUMER_RECEIPT_SHA256 = "F1D83AA80D868172F3D49F8E5749266F6132DE5F4DD5AE0DF6FDBB5E3204794A"
ANKI_IMPLEMENTATION_SHA256 = {
    "workers/anki-exporter/anki_exporter.py": "30083FDE0BD5D0B72039111313A9180E8B1F2DE98CC4CDF6DAAD439D61A9AC7E",
    "workers/anki-exporter/tests/test_anki_exporter.py": "E214686B2464CCF8D98A9A8B718EC600F6F33BE5819C076B61B6BD01C35ED110",
}
ANKI_BRAND_RECEIPT_RELATIVE = Path("receipts/anki-worker-knowledgeclipper-migration-20260826T030900Z.json")
ANKI_BRAND_RECEIPT_SHA256 = "933F7DDE470932D407F8655BC1FD9F9C828943AF6B2805FB9DEE43427ABC6F9A"
ANKI_CURRENT_ACCEPTANCE_RELATIVE = Path("workers/anki-exporter/canary/ca/260826042542-8b7b/receipt.json")
ANKI_CURRENT_ACCEPTANCE_SHA256 = "AEB7F9D59085182C57E09BB49CE02DADBD089B4580BB7F2CB48DF44C47C0C62F"
CONTENT_LANGUAGE_AUTHORITY_SHA256 = "6E7DDC2B71D7647BAB24B89A28EE288CC11CF7050D012D4C49ADAE63A3CB9E63"
ORCHESTRATOR_COMPLETION_RECEIPT_RELATIVE = Path("canary/orchestrator-v1.1/orchestrator-v1.1-20260826T015200Z/receipt.json")
ORCHESTRATOR_COMPLETION_RECEIPT_SHA256 = "7C0FC14064167BEDC29ED05215DF69E5B8A6F710D4E2DEC5DF3C7C2632D9727B"
ORCHESTRATOR_EXE_RELATIVE = Path("dist/orchestrator/win-x64/v1.1.0-canary.6/RemNoteQuickClipper.Orchestrator.exe")
ORCHESTRATOR_EXE_SHA256 = "C93E911B5B977F9543AC2ED5BCC82FCCCCD7B4CCB77A367019327834EF0F4802"
ORCHESTRATOR_BRAND_EXE_RELATIVE = Path("dist/orchestrator/win-x64/v2.0.0/KnowledgeClipper.Orchestrator.exe")
CONTENT_FINAL_RECEIPT_RELATIVE = Path("receipts/content-worker-v0.2.0-final-20260825T142504Z.json")
CONTENT_FINAL_RECEIPT_SHA256 = "BACA6E785646791262736EFBD39416CA801F41E70A340CB910FCBB92BEB1A94C"
CONTENT_LAUNCHER_SHA256 = "ED7C2387EE90705999961E8B843142B35160A18E61E7148F0CFFC3661423822E"
CONTENT_ITERATION_RECEIPT_RELATIVE = Path("receipts/content-worker-continuous-iteration-20260825T155736Z.json")
CONTENT_ITERATION_RECEIPT_SHA256 = "A7DF36B952B1BCFCE3A6082D2683E46F8F95B228EDE70E33243F7C076B82BC39"
CONTENT_LIVE_RECEIPT_RELATIVE = Path("receipts/content-worker-general-learning-live-20260825T160311Z.json")
CONTENT_LIVE_RECEIPT_SHA256 = "6365C706E8898D969582B056F21FDFF1B2374453655E91561AA6A4A383AA35E3"
CONTENT_CLEANING_LIVE_RECEIPT_RELATIVE = Path("receipts/content-worker-cleaning-live-regression-20260825T2342Z.json")
CONTENT_CLEANING_LIVE_RECEIPT_SHA256 = "9B1286ACEDE31389122ECECE3B537EA44D79CB841584DB611BE3D86E227D08F7"
CONTENT_QUALITY_REFRESH_RECEIPT_RELATIVE = Path("receipts/content-worker-quality-final-20260826T021700Z.json")
CONTENT_QUALITY_REFRESH_RECEIPT_SHA256 = "5A11CBBB82DB4E6D5E6DAD616A16785CBD32AD277FD440A18C51C11C4E9B3DB2"
CONTENT_IMPLEMENTATION_SHA256 = {
    "content-worker/src/gates.ts": "31A14C7F50794C60CFCFABE6A40CDFCE7F4C57B95A2DBAD1F79AEAE1E8215873",
    "content-worker/test/worker.test.ts": "ADC059E0B02DE68ED6ACEDE7011A27826D1C9F858905A15037560C5219EBF932",
    "content-worker/dist/src/gates.js": "895F48B45703FE3F22B844E9B70E4D57F3C1460258E070A69E759EE5EACFB933",
    "content-worker/dist/test/worker.test.js": "83A571338DA7D3B6F8668D6FD06B64C8C763804A243A759DB61E3EBC9FAD3105",
}
CONTENT_BRAND_RECEIPT_RELATIVE = Path("receipts/content-worker-knowledgeclipper-destination-neutral-20260826T030540Z.json")
CONTENT_BRAND_RECEIPT_SHA256 = "DC0D0D092156A3F638E159EBCD6F6DBC3F09F8DE86D63C83117D015DFF3EC424"
CONTENT_LANGUAGE_RECEIPT_RELATIVE = Path("receipts/content-worker-language-practice-v2-20260826T034541Z.json")
CONTENT_LANGUAGE_RECEIPT_SHA256 = "732F13D99EA576A130994DC510CDA220217FE480D26B1E36D78A0E6DD0FFE324"
CONTENT_V050_RECEIPT_RELATIVE = Path("receipts/content-worker-v0.5.0-authoritative-20260826T041900Z.json")
CONTENT_V050_RECEIPT_SHA256 = "3476492F2B6FF69014B9AFC854F25A671C1D96BC5720DBC43446BF28C9BF43A4"
CONTENT_V050_REGISTRY_ADMISSION_RELATIVE = Path(
    "canary/registry-admission/content-v0.5-20260826T052837Z/receipt.json"
)
CONTENT_V050_REGISTRY_ADMISSION_SHA256 = "ED733E4391661AA48105B83BEF365BAF180CA3DEA5AB1F7849CC6ADE3731636F"
REMNOTE_FINAL_RECEIPT_RELATIVE = Path("receipts/remnote-exporter-worker-v1-20260825T235334Z.json")
REMNOTE_FINAL_RECEIPT_SHA256 = "BDB4DE203920F27118CD26169ECC2EC46D57A955C72CC41A6BB3A7EC195C620D"
REMNOTE_SHARED_CORE_SHA256 = "615810E50F8BD360E268FDCBFD130BDC20B34F06C6220444FD2FCEB7B7BA0235"
REMNOTE_EXE_SHA256 = "45DE3A87501236ECAA0CFA13FC02A8F6B0C56A79996F1F37613610819F2708FF"
REMNOTE_EXE_BYTES = 72704
REMNOTE_CONSUMER_RECEIPT_RELATIVE = Path("canary/rwa/260826014615/receipt.json")
REMNOTE_CONSUMER_RECEIPT_SHA256 = "5F6D80503F3A0D5C2A5EFCDFB0D4FADD44BCD79AB0638E1C7862BFDEB732F7F6"
REMNOTE_BRAND_RECEIPT_RELATIVE = Path("receipts/remnote-exporter-worker-v1-20260826T030237Z.json")
REMNOTE_BRAND_RECEIPT_SHA256 = "50FF57CAA7665763C374AAF97D37DD63AA1AD420ABD600F200E6A798CCEFC307"
SPEECH_ONBOARDING_ROOT_RELATIVE = Path(
    "tests/RemNoteQuickClipper.Speech.Tests/registry-canary/r20260825-115811-291091"
)
SPEECH_ONBOARDING_RECEIPT_SHA256 = "0A5C393032C6EC2854BEB4489A1B882C81079D025EFF6195A5B098333E7FE537"
SPEECH_EXE_SHA256 = "32E65EDF5D23F87A60E5826278DE06F3B5EC8066006D243ECD58D3FD3A272470"
SPEECH_CORE_REUSE_RECEIPT_RELATIVE = Path(
    "tests/RemNoteQuickClipper.Speech.Tests/bin/core-reuse-compatibility/r0151115287/speech-core-reuse-compatibility-receipt.json"
)
SPEECH_CORE_REUSE_RECEIPT_SHA256 = "1ADEED64784C5F16F9F18128CF6579F06F2C881F5CC5A68D747DA8F97C71CA02"
CURRENT_PRESEAL_ROOT_RELATIVE = Path("releases/1.0.0-rc.1-preseal.10/win-x64")
CURRENT_PRESEAL_STATE_RECEIPT_RELATIVE = Path("receipts/release-preseal-increment-20260826T021900Z.json")
CURRENT_PRESEAL_STATE_RECEIPT_SHA256 = "221AB06E3F13D1CA6FFC6595956EF0BB089990B619FE3A118083495562ECD3E8"
CURRENT_PRESEAL_BLACK_BOX_RECEIPT_RELATIVE = Path("receipts/release-preseal-black-box-20260826T022100Z.json")
CURRENT_PRESEAL_BLACK_BOX_RECEIPT_SHA256 = "A353FB81DBBB1B3637665DE7B4DB0BFA26A2A9661A012DB4690CEE7B04140CFF"

EXPECTED_WORKERS: dict[str, dict[str, Any]] = {
    "image.ocr.mineru": {
        "entrypoint": "workers/image-ocr/RemNoteQuickClipper.ImageOcr.Worker.exe",
        "profiles": ["parity-remnote", "cards-remnote", "cards-anki"],
    },
    "content.deepseek": {
        "entrypoint": "workers/content/RemNoteQuickClipper.Content.Worker.exe",
        "profiles": ["parity-remnote", "cards-remnote", "cards-anki"],
    },
    "speech.edge": {
        "entrypoint": "workers/speech/RemNoteQuickClipper.Speech.Worker.exe",
        "profiles": ["parity-remnote", "cards-remnote", "cards-anki"],
    },
    "export.remnote": {
        "entrypoint": "workers/remnote/RemNoteQuickClipper.RemNoteExporter.Worker.exe",
        "profiles": ["parity-remnote", "cards-remnote"],
    },
    "export.anki": {
        "entrypoint": "workers/anki/RemNoteQuickClipper.AnkiExporter.Worker.exe",
        "profiles": ["cards-anki"],
    },
}

# Historical authority/checkpoint verification keeps using EXPECTED_WORKERS above.
# Every new KnowledgeClipper authority checkpoint and release root must use these neutral entrypoints.
KNOWLEDGE_CLIPPER_EXPECTED_WORKERS: dict[str, dict[str, Any]] = {
    "image.ocr.mineru": {
        "entrypoint": "workers/image-ocr/KnowledgeClipper.ImageOcr.Worker.exe",
        "profiles": ["parity-remnote", "cards-remnote", "cards-anki"],
    },
    "content.deepseek": {
        "entrypoint": "workers/content/KnowledgeClipper.Content.Worker.exe",
        "profiles": ["parity-remnote", "cards-remnote", "cards-anki"],
    },
    "speech.edge": {
        "entrypoint": "workers/speech/KnowledgeClipper.Speech.Worker.exe",
        "profiles": ["parity-remnote", "cards-remnote", "cards-anki"],
    },
    "export.remnote": {
        "entrypoint": "workers/remnote/KnowledgeClipper.RemNoteExporter.Worker.exe",
        "profiles": ["parity-remnote", "cards-remnote"],
    },
    "export.anki": {
        "entrypoint": "workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe",
        "profiles": ["cards-anki"],
    },
}


class GateFailure(RuntimeError):
    pass


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def windows_long_path(path: Path) -> Path:
    resolved = path.resolve()
    if len(str(resolved)) >= 260 and not str(resolved).startswith("\\\\?\\"):
        return Path("\\\\?\\" + str(resolved))
    return resolved


def content_asset_snapshot(project_root: Path) -> tuple[int, str]:
    content_root = project_root / "content-worker"
    roots = ["dist", "prompts", "config", "schemas"]
    items: list[tuple[str, int, str]] = []
    for name in roots:
        root = content_root / name
        if not root.is_dir():
            raise GateFailure(f"content runtime asset root is missing: {root}")
        for path in root.rglob("*"):
            if path.is_file():
                items.append((path.relative_to(content_root).as_posix(), path.stat().st_size, sha256_file(path)))
    package = content_root / "package.json"
    if not package.is_file():
        raise GateFailure("content package.json is missing")
    items.append(("package.json", package.stat().st_size, sha256_file(package)))
    items.sort(key=lambda item: item[0])
    lines = [f"{path}\t{size}\t{digest}\n" for path, size, digest in items]
    return len(items), hashlib.sha256("".join(lines).encode("utf-8")).hexdigest().upper()


def normalized_relative(path: Path, root: Path) -> str:
    return path.resolve().relative_to(root.resolve()).as_posix()


@dataclass(frozen=True)
class SchemaSet:
    registry: Registry
    schemas: dict[Path, Any]

    def validator(self, path: Path) -> Draft202012Validator:
        resolved = path.resolve()
        return Draft202012Validator(
            self.schemas[resolved],
            registry=self.registry,
            _resolver=self.registry.resolver(resolved.as_uri()),
            format_checker=FormatChecker(),
        )


def load_schemas() -> SchemaSet:
    registry = Registry()
    schemas: dict[Path, Any] = {}
    for path in sorted(SCHEMAS_ROOT.rglob("*.schema.json")):
        resolved = path.resolve()
        schema = load_json(resolved)
        Draft202012Validator.check_schema(schema)
        schemas[resolved] = schema
        registry = registry.with_resource(resolved.as_uri(), Resource.from_contents(schema))
    return SchemaSet(registry, schemas)


def verify_stable_checkpoint(project_root: Path = PROJECT_ROOT) -> dict[str, Any]:
    receipt_path = project_root / CHECKPOINT_RECEIPT_RELATIVE
    if not receipt_path.is_file():
        raise GateFailure(f"stable checkpoint receipt missing: {receipt_path}")
    actual_receipt_hash = sha256_file(receipt_path)
    if actual_receipt_hash != CHECKPOINT_RECEIPT_SHA256:
        raise GateFailure(
            f"stable checkpoint receipt hash mismatch: expected={CHECKPOINT_RECEIPT_SHA256} actual={actual_receipt_hash}"
        )
    receipt = load_json(receipt_path)
    if receipt.get("checkpointId") != CHECKPOINT_ID or receipt.get("status") != "stable":
        raise GateFailure("stable checkpoint identity/status mismatch")
    mismatches: list[str] = []
    for item in receipt.get("files", []):
        path = Path(item["path"])
        if not path.is_file():
            mismatches.append(f"missing:{path}")
            continue
        actual = sha256_file(path)
        if actual.casefold() != item["sha256"].casefold():
            mismatches.append(f"hash:{path}")
    if mismatches:
        raise GateFailure("stable checkpoint file mismatch: " + ", ".join(mismatches))
    return {
        "checkpointId": CHECKPOINT_ID,
        "receiptSha256": actual_receipt_hash,
        "filesChecked": len(receipt["files"]),
        "mismatches": 0,
    }


def assert_canonical_contract_evidence(evidence_path: Path, project_root: Path = PROJECT_ROOT) -> dict[str, str]:
    checkpoint = load_json(project_root / CHECKPOINT_RECEIPT_RELATIVE)
    resolved = evidence_path.resolve()
    canonical = {
        Path(item["path"]).resolve(): item["sha256"].upper()
        for item in checkpoint["files"]
        if Path(item["path"]).resolve().is_relative_to((project_root / "contracts" / "v1").resolve())
    }
    if resolved not in canonical:
        raise GateFailure(
            "contract evidence is not in the stable contracts/v1 checkpoint; module-local adapters and drafts are not authoritative: "
            + str(evidence_path)
        )
    actual = sha256_file(resolved)
    if actual != canonical[resolved]:
        raise GateFailure(f"canonical contract evidence hash mismatch: {evidence_path}")
    return {"path": normalized_relative(resolved, project_root), "sha256": actual}


def validate_describe(executable: Path, arguments: Iterable[str], expected_entry: dict[str, Any]) -> dict[str, Any]:
    command = [str(executable), *arguments, "describe", "--json"]
    try:
        completed = subprocess.run(command, capture_output=True, text=True, timeout=10, check=False)
    except subprocess.TimeoutExpired as exc:
        raise GateFailure(f"worker describe timed out: {expected_entry['workerId']}") from exc
    if completed.returncode != 0:
        raise GateFailure(
            f"worker describe failed: {expected_entry['workerId']} exit={completed.returncode} stderr={completed.stderr[:500]}"
        )
    try:
        description = json.loads(completed.stdout)
    except json.JSONDecodeError as exc:
        raise GateFailure(f"worker describe is not one JSON document: {expected_entry['workerId']}") from exc
    schemas = load_schemas()
    errors = list(schemas.validator(SCHEMAS_ROOT / "common" / "worker-describe.schema.json").iter_errors(description))
    if errors:
        raise GateFailure(f"worker describe schema violation: {expected_entry['workerId']}: {errors[0].message}")
    comparisons = {
        "workerId": description["workerId"] == expected_entry["workerId"],
        "stages": set(description["stages"]) == set(expected_entry["stages"]),
        "inputKinds": set(description["inputKinds"]) == set(expected_entry["inputKinds"]),
        "outputKinds": set(description["outputKinds"]) == set(expected_entry["outputKinds"]),
        "sideEffectLevel": description["sideEffectLevel"] == expected_entry["sideEffectLevel"],
        "idempotencyLevel": description["idempotencyLevel"] == expected_entry["idempotencyLevel"],
    }
    failed = [name for name, passed in comparisons.items() if not passed]
    if failed:
        raise GateFailure(f"worker describe/registry mismatch for {expected_entry['workerId']}: {', '.join(failed)}")
    return description


def assess_authority_registry(
    registry_path: Path = AUTHORITY_REGISTRY,
    release_root: Path | None = None,
    run_describe: bool = True,
    expected_workers: dict[str, dict[str, Any]] | None = None,
) -> dict[str, Any]:
    release_root = release_root or PROJECT_ROOT
    expected_workers = expected_workers or EXPECTED_WORKERS
    document = load_json(registry_path)
    schemas = load_schemas()
    errors = list(schemas.validator(SCHEMAS_ROOT / "common" / "worker-registry.schema.json").iter_errors(document))
    if errors:
        raise GateFailure(f"authority registry schema violation: {errors[0].message}")
    ids = [item["workerId"] for item in document["workers"]]
    if len(ids) != len(set(ids)):
        raise GateFailure("authority registry contains duplicate worker IDs")
    checked: list[dict[str, Any]] = []
    for entry in document["workers"]:
        expected = expected_workers.get(entry["workerId"])
        if expected is None:
            raise GateFailure(f"unapproved worker ID in authority registry: {entry['workerId']}")
        if entry["entrypoint"]["path"] != expected["entrypoint"]:
            raise GateFailure(f"authority entrypoint drift for {entry['workerId']}")
        if entry["requiredForProfiles"] != expected["profiles"]:
            raise GateFailure(f"requiredForProfiles drift for {entry['workerId']}")
        executable = release_root / Path(entry["entrypoint"]["path"])
        if not executable.is_file():
            raise GateFailure(f"registered worker executable missing: {entry['workerId']}: {executable}")
        description = validate_describe(executable, entry["entrypoint"]["args"], entry) if run_describe else None
        checked.append(
            {
                "workerId": entry["workerId"],
                "entrypoint": entry["entrypoint"]["path"],
                "sha256": sha256_file(executable),
                "describePassed": description is not None if run_describe else None,
            }
        )
    missing_by_profile = {
        profile: sorted(
            worker_id
            for worker_id, expected in expected_workers.items()
            if profile in expected["profiles"] and worker_id not in ids
        )
        for profile in document["profiles"]
    }
    return {
        "authorityRegistryEntries": len(ids),
        "state": "empty" if not ids else "populated",
        "checkedWorkers": checked,
        "profileReady": {profile: not missing for profile, missing in missing_by_profile.items()},
        "missingRequiredWorkers": missing_by_profile,
    }


def assess_module_onboarding(
    worker_id: str,
    executable: Path,
    registry_fixture: Path,
    receipt_path: Path,
    expected_executable_hash: str,
) -> dict[str, Any]:
    if not executable.is_file():
        raise GateFailure(f"module worker executable missing: {executable}")
    actual_hash = sha256_file(executable)
    if actual_hash != expected_executable_hash.upper():
        raise GateFailure(f"module worker executable hash mismatch: {worker_id}")
    receipt = load_json(receipt_path)
    if receipt.get("status") != "passed" or receipt.get("workerExeSha256", "").upper() != actual_hash:
        raise GateFailure(f"module onboarding receipt does not bind the executable: {worker_id}")
    registry = load_json(registry_fixture)
    entries = [item for item in registry["workers"] if item["workerId"] == worker_id]
    if len(entries) != 1:
        raise GateFailure(f"module onboarding registry must contain exactly one {worker_id} entry")
    entry = entries[0]
    description = validate_describe(executable, entry["entrypoint"]["args"], entry)
    authority_ids = {item["workerId"] for item in load_json(AUTHORITY_REGISTRY)["workers"]}
    return {
        "workerId": worker_id,
        "moduleOnboarding": "passed",
        "onboardingState": "eligible-but-not-authority-published",
        "workerVersion": description["workerVersion"],
        "executableSha256": actual_hash,
        "authorityPublished": worker_id in authority_ids,
        "releaseCandidateEligible": False,
        "reason": "module canary is not authority registry publication or final release-layout evidence",
    }


def assess_speech_onboarding(project_root: Path = PROJECT_ROOT) -> dict[str, Any]:
    root = project_root / SPEECH_ONBOARDING_ROOT_RELATIVE
    receipt_path = root / "registry-canary-receipt.json"
    if not receipt_path.is_file() or sha256_file(receipt_path) != SPEECH_ONBOARDING_RECEIPT_SHA256:
        raise GateFailure("speech onboarding receipt is missing or hash-mismatched")
    result = assess_module_onboarding(
        "speech.edge",
        root / "release/workers/RemNoteQuickClipper.Speech.Worker.exe",
        root / "worker-registry.json",
        receipt_path,
        SPEECH_EXE_SHA256,
    )
    reuse_path = project_root / SPEECH_CORE_REUSE_RECEIPT_RELATIVE
    if not reuse_path.is_file() or sha256_file(reuse_path) != SPEECH_CORE_REUSE_RECEIPT_SHA256:
        raise GateFailure("speech Core reuse compatibility receipt is missing or hash-mismatched")
    reuse = load_json(reuse_path)
    checks = reuse.get("checks", {})
    if (
        reuse.get("schemaVersion") != "speech-core-reuse-compatibility.v1"
        or reuse.get("status") != "passed"
        or checks.get("synthesisNewAttemptOnReuse") is not True
        or checks.get("synthesisCacheHitTransition") != [False, True]
        or checks.get("reuseAncestryPreserved") is not True
        or checks.get("operationIdentityCurrent") is not True
        or checks.get("immutableAudioReferencePreserved") is not True
        or checks.get("artifactProducerPreserved") is not True
        or checks.get("corruptedArtifactCandidateReused") is not False
        or checks.get("corruptedArtifactTriggeredFreshSynthesis") is not True
        or checks.get("malformedResultSkipped") is not True
        or checks.get("forgedIdentitySkipped") is not True
        or checks.get("validFallbackReuseStartedWorker") is not False
        or checks.get("playbackAttempts") != 2
        or checks.get("playbackWorkerStarts") != 2
        or checks.get("playbackResultReuse") is not False
        or checks.get("replayFromStart") is not True
    ):
        raise GateFailure("speech Core reuse compatibility receipt violates the quality gate")
    for item in reuse.get("inputs", []):
        path = reuse_path.parent / item["path"]
        if (
            not path.is_file()
            or path.stat().st_size != item.get("bytes")
            or sha256_file(path) != item.get("sha256", "").upper()
        ):
            raise GateFailure(f"speech Core reuse input mismatch: {path}")
    result["onboardingState"] = "eligible-but-not-authority-published"
    planned_entrypoint = Path(EXPECTED_WORKERS["speech.edge"]["entrypoint"])
    result["moduleSourceLayoutReady"] = (project_root / planned_entrypoint).is_file()
    result["currentPresealLayoutReady"] = (
        project_root / CURRENT_PRESEAL_ROOT_RELATIVE / planned_entrypoint
    ).is_file()
    result["finalReleaseLayoutReady"] = result["moduleSourceLayoutReady"] or result["currentPresealLayoutReady"]
    result["coreReuseCompatibility"] = "passed"
    result["coreReuseCompatibilityReceiptSha256"] = SPEECH_CORE_REUSE_RECEIPT_SHA256
    return result


def _validate_worker_describe(
    executable: Path,
    expected_worker_id: str,
    environment: dict[str, str] | None = None,
) -> dict[str, Any]:
    completed = subprocess.run(
        [str(executable), "describe", "--json"],
        capture_output=True,
        text=True,
        timeout=10,
        check=False,
        env=environment,
    )
    if completed.returncode != 0:
        raise GateFailure(f"worker describe failed during quality onboarding gate: {expected_worker_id}")
    try:
        description = json.loads(completed.stdout)
    except json.JSONDecodeError as exc:
        raise GateFailure(f"worker describe did not return one JSON document: {expected_worker_id}") from exc
    schemas = load_schemas()
    errors = list(
        schemas.validator(SCHEMAS_ROOT / "common" / "worker-describe.schema.json").iter_errors(description)
    )
    if errors or description.get("workerId") != expected_worker_id:
        message = errors[0].message if errors else "worker ID mismatch"
        raise GateFailure(f"worker describe contract mismatch for {expected_worker_id}: {message}")
    return description


def _assess_content_brand_migration(project_root: Path) -> dict[str, Any]:
    receipt_path = project_root / CONTENT_BRAND_RECEIPT_RELATIVE
    if sha256_file(receipt_path) != CONTENT_BRAND_RECEIPT_SHA256:
        raise GateFailure("KnowledgeClipper content migration receipt is hash-mismatched")
    receipt = load_json(receipt_path)
    product = receipt.get("product", {})
    worker = receipt.get("worker", {})
    authority = receipt.get("authority", {})
    executable_record = authority.get("executable", {})
    tests = receipt.get("tests", {})
    stable = receipt.get("stableContracts", {})
    executable = project_root / executable_record.get("path", "")
    if (
        receipt.get("status") != "passed"
        or product.get("productId") != "KnowledgeClipper"
        or product.get("productDisplayName") != "零碎知识剪藏"
        or product.get("destinationNeutral") is not True
        or worker.get("workerId") != "content.deepseek"
        or worker.get("version") != "0.3.0"
        or worker.get("npmPackage") != "knowledge-clipper-content-worker"
        or authority.get("assemblyName") != "KnowledgeClipper.Content.Worker"
        or authority.get("rootNamespace") != "KnowledgeClipper.Content.Worker"
        or authority.get("describe", {}).get("destinationNameHits") != 0
        or tests.get("nodeTests") != {"passed": 46, "failed": 0}
        or tests.get("projectionValidator", {}).get("externalApiCalls") != 0
        or tests.get("dotnetAuthorityBuild") != {"warnings": 0, "errors": 0}
        or tests.get("authoritativeSurfaceScan", {}).get("forbiddenDestinationNameHits") != 0
        or stable.get("checkpointId") != CHECKPOINT_ID
        or stable.get("receiptSha256", "").upper() != CHECKPOINT_RECEIPT_SHA256
        or stable.get("filesChecked") != 21
        or stable.get("mismatches") != 0
        or stable.get("modified") is not False
        or receipt.get("externalApiCalls") != 0
        or not executable.is_file()
        or executable.stat().st_size != executable_record.get("bytes")
        or sha256_file(executable) != executable_record.get("sha256", "").upper()
    ):
        raise GateFailure("KnowledgeClipper content migration receipt violates the quality gate")
    description = _validate_worker_describe(
        executable,
        "content.deepseek",
        {**os.environ, "KNOWLEDGE_CLIPPER_CONTENT_WORKER_ROOT": str(project_root / "content-worker")},
    )
    drifted_files: list[str] = []
    for item in receipt.get("files", []):
        path = project_root / item["path"]
        if not path.is_file() or sha256_file(path) != item.get("sha256", "").upper():
            drifted_files.append(item["path"])
    if drifted_files or description.get("workerVersion") != worker.get("version"):
        return {
            "workerId": "content.deepseek",
            "workerVersion": description["workerVersion"],
            "historicalBrandReceiptSha256": CONTENT_BRAND_RECEIPT_SHA256,
            "executableSha256": executable_record["sha256"].upper(),
            "executableBytes": executable_record["bytes"],
            "moduleOnboarding": "prior-brand-pass-current-extension-unverified",
            "onboardingState": "extension-implementation-pending",
            "brandIdentity": "passed-KnowledgeClipper-destination-neutral",
            "authorityPublished": False,
            "releaseCandidateEligible": False,
            "driftedFiles": drifted_files,
            "descriptorVersionDrift": description.get("workerVersion") != worker.get("version"),
            "externalProviderCalls": 0,
            "reason": "language-choice extension changed implementation after the latest module receipt; a new append-only receipt is required",
        }
    canary_states: list[tuple[str, str]] = []
    for item in receipt.get("offlineCanaries", []):
        path = project_root / item["receiptPath"]
        if not path.is_file() or sha256_file(path) != item.get("receiptSha256", "").upper():
            raise GateFailure(f"KnowledgeClipper content Canary mismatch: {path}")
        canary_states.append((item.get("status"), item.get("moduleReleaseState")))
    if canary_states != [("completed", "ready"), ("completed", "partial")]:
        raise GateFailure("KnowledgeClipper content Canary state matrix mismatch")
    authority_ids = {item["workerId"] for item in load_json(AUTHORITY_REGISTRY)["workers"]}
    planned = project_root / KNOWLEDGE_CLIPPER_EXPECTED_WORKERS["content.deepseek"]["entrypoint"]
    return {
        "workerId": "content.deepseek",
        "workerVersion": description["workerVersion"],
        "receiptSha256": CONTENT_BRAND_RECEIPT_SHA256,
        "executableSha256": executable_record["sha256"].upper(),
        "executableBytes": executable_record["bytes"],
        "moduleEntrypoint": executable_record["path"],
        "plannedReleaseEntrypoint": KNOWLEDGE_CLIPPER_EXPECTED_WORKERS["content.deepseek"]["entrypoint"],
        "plannedReleaseEntrypointPresent": planned.is_file(),
        "moduleDescribeDependency": "KNOWLEDGE_CLIPPER_CONTENT_WORKER_ROOT",
        "moduleOnboarding": "passed",
        "onboardingState": "eligible-but-not-authority-published",
        "brandIdentity": "passed-KnowledgeClipper-destination-neutral",
        "authorityPublished": "content.deepseek" in authority_ids,
        "releaseCandidateEligible": False,
        "latestTestsPassed": 46,
        "offlineCanaries": {"ready": "passed", "partial": "passed"},
        "externalProviderCalls": 0,
    }


def _assess_content_language_extension(project_root: Path) -> dict[str, Any]:
    receipt_path = project_root / CONTENT_LANGUAGE_RECEIPT_RELATIVE
    if not receipt_path.is_file() or sha256_file(receipt_path) != CONTENT_LANGUAGE_RECEIPT_SHA256:
        raise GateFailure("content language-practice receipt is missing or hash-mismatched")
    receipt = load_json(receipt_path)
    execution = receipt.get("execution", {})
    worker = receipt.get("worker", {})
    contract = receipt.get("languagePracticeContract", {})
    regression = receipt.get("regressionCorrection", {}).get("final", {})
    launcher = receipt.get("launcher", {})
    stable = receipt.get("stableContracts", {})
    executable = project_root / launcher.get("path", "")
    if (
        receipt.get("status") != "passed"
        or execution.get("mode") != "offline-replay-only"
        or execution.get("externalApiCalls") != 0
        or execution.get("deepSeekCalled") is not False
        or worker.get("workerId") != "content.deepseek"
        or worker.get("workerVersion") != "0.4.0"
        or worker.get("npmPackage") != "knowledge-clipper-content-worker"
        or contract.get("modes") != ["meaning-reading", "writing", "both"]
        or contract.get("bothOrder") != ["language_context", "language_writing"]
        or "unique target_term" not in contract.get("writingFront", "")
        or "fullwidth （pronunciation；contextual meaning）" not in contract.get("writingFront", "")
        or contract.get("writingBack") != "exact source target_term"
        or regression != {"tests": 51, "passed": 51, "failed": 0}
        or launcher.get("build") != {"warnings": 0, "errors": 0}
        or not executable.is_file()
        or executable.stat().st_size != launcher.get("bytes")
        or sha256_file(executable) != launcher.get("sha256", "").upper()
        or stable.get("checkpointId") != CHECKPOINT_ID
        or stable.get("receiptSha256", "").upper() != CHECKPOINT_RECEIPT_SHA256
        or stable.get("filesChecked") != 21
        or stable.get("mismatches") != 0
        or stable.get("modified") is not False
    ):
        raise GateFailure("content language-practice receipt violates the quality gate")
    description = _validate_worker_describe(
        executable,
        "content.deepseek",
        {**os.environ, "KNOWLEDGE_CLIPPER_CONTENT_WORKER_ROOT": str(project_root / "content-worker")},
    )
    if description.get("workerVersion") != "0.4.0" or description.get("outputKinds") != [
        "learning-bundle",
        "card-bundle",
        "language-practice-decision",
    ]:
        raise GateFailure("content language-practice describe evidence mismatch")

    hash_records: list[dict[str, Any]] = []
    hash_records.extend(receipt.get("schemas", []))
    hash_records.extend(receipt.get("openRegistryAndPrompts", []))
    hash_records.extend(receipt.get("meaningReadingByteEquality", []))
    for fixture in receipt.get("fixtures", []):
        hash_records.append({"path": fixture.get("inputPath"), "sha256": fixture.get("inputSha256")})
        if fixture.get("replayPath"):
            hash_records.append({"path": fixture.get("replayPath"), "sha256": fixture.get("replaySha256")})
    # Deep attempt artifact paths can exceed legacy Windows MAX_PATH. Their hashes are
    # transitively bound by the three shorter, immutable Canary receipts verified below.
    hash_records.append({
        "path": receipt.get("otherValidation", {}).get("acceptancePath"),
        "sha256": receipt.get("otherValidation", {}).get("acceptanceSha256"),
    })
    for item in hash_records:
        path = project_root / str(item.get("path", ""))
        if not path.is_file() or sha256_file(path) != str(item.get("sha256", "")).upper():
            raise GateFailure(f"content language-practice evidence drifted: {item.get('path')}")
    for canary in receipt.get("offlineCanaries", []):
        path = project_root / canary.get("receiptPath", "")
        if (
            canary.get("validation") != "passed"
            or not path.is_file()
            or sha256_file(path) != canary.get("receiptSha256", "").upper()
        ):
            raise GateFailure(f"content language-practice Canary mismatch: {canary.get('fixture')}")
    authority_ids = {item["workerId"] for item in load_json(AUTHORITY_REGISTRY)["workers"]}
    return {
        "workerId": "content.deepseek",
        "workerVersion": "0.4.0",
        "receiptSha256": CONTENT_LANGUAGE_RECEIPT_SHA256,
        "executableSha256": launcher["sha256"].upper(),
        "executableBytes": launcher["bytes"],
        "moduleOnboarding": "passed-scoped-language-extension",
        "onboardingState": "eligible-but-cross-module-integration-pending",
        "brandIdentity": "passed-KnowledgeClipper-destination-neutral",
        "authorityPublished": "content.deepseek" in authority_ids,
        "releaseCandidateEligible": False,
        "latestTestsPassed": 51,
        "offlineCanariesPassed": len(receipt.get("offlineCanaries", [])),
        "choiceModes": contract["modes"],
        "bothOrder": contract["bothOrder"],
        "exactWritingReplacement": "passed",
        "externalProviderCalls": 0,
        "remainingIntegrationBoundary": receipt.get("remainingIntegrationBoundary", []),
    }


def _assess_content_v050_extension(project_root: Path) -> dict[str, Any]:
    receipt_path = project_root / CONTENT_V050_RECEIPT_RELATIVE
    if not receipt_path.is_file() or sha256_file(receipt_path) != CONTENT_V050_RECEIPT_SHA256:
        raise GateFailure("authoritative Content 0.5.0 receipt is missing or hash-mismatched")
    receipt = load_json(receipt_path)
    worker = receipt.get("worker", {})
    regression = receipt.get("regression", {})
    launcher = receipt.get("launcher", {})
    stable = receipt.get("stableCheckpoint", {})
    expected_operations = ["understand-and-card", "question.answer", "question.make-cards"]
    expected_output_kinds = [
        "learning-bundle",
        "card-bundle",
        "language-practice-decision",
        "question.answer",
    ]
    executable = project_root / launcher.get("path", "")
    if (
        receipt.get("status") != "completed"
        or receipt.get("authority") != "module-v0.5.0-current"
        or receipt.get("productId") != "KnowledgeClipper"
        or receipt.get("productDisplayName") != "零碎知识剪藏"
        or receipt.get("destinationNeutral") is not True
        or worker.get("workerId") != "content.deepseek"
        or worker.get("workerVersion") != "0.5.0"
        or worker.get("operations") != expected_operations
        or worker.get("outputKinds") != expected_output_kinds
        or regression.get("exitCode") != 0
        or regression.get("tests") != 58
        or regression.get("passed") != 58
        or regression.get("failed") != 0
        or regression.get("questionFixturesChecked") != 8
        or regression.get("questionFixtureResult") != "PASS"
        or launcher.get("build") != "0 warnings, 0 errors"
        or not executable.is_file()
        or executable.stat().st_size != launcher.get("bytes")
        or sha256_file(executable) != launcher.get("sha256", "").upper()
        or stable.get("checkpointId") != CHECKPOINT_ID
        or stable.get("sha256", "").upper() != CHECKPOINT_RECEIPT_SHA256
        or stable.get("filesChecked") != 21
        or stable.get("mismatches") != 0
        or receipt.get("externalCalls") != {"deepSeek": 0, "exporters": 0, "desktop": 0}
    ):
        raise GateFailure("authoritative Content 0.5.0 receipt violates the quality gate")

    for contract in ("questionInput", "questionAnswer", "openPrompt"):
        record = receipt.get("moduleContracts", {}).get(contract, {})
        path = project_root / record.get("path", "")
        if not path.is_file() or sha256_file(path) != record.get("sha256", "").upper():
            raise GateFailure(f"Content 0.5.0 contract evidence drifted: {contract}")
    readable = receipt.get("readableEvidence", {})
    readable_path = project_root / readable.get("path", "")
    language_samples = readable.get("languageWriting", [])
    if (
        not readable_path.is_file()
        or readable_path.stat().st_size != readable.get("bytes")
        or sha256_file(readable_path) != readable.get("sha256", "").upper()
        or len(language_samples) != 2
        or any(item.get("frontEndsWithCrOrLf") is not False for item in language_samples)
    ):
        raise GateFailure("Content 0.5.0 readable/newline evidence drifted")

    canary_summary: list[dict[str, Any]] = []
    for item in receipt.get("independentOfflineCanaries", []):
        path = project_root / item.get("path", "")
        if (
            item.get("status") != "completed"
            or not windows_long_path(path).is_file()
            or sha256_file(windows_long_path(path)) != item.get("sha256", "").upper()
        ):
            raise GateFailure(f"Content 0.5.0 Canary evidence drifted: {item.get('fixture')}")
        canary = load_json(windows_long_path(path))
        actual_kinds = [output.get("kind") for output in canary.get("outputs", [])]
        if canary.get("status") != "completed" or actual_kinds != item.get("outputs"):
            raise GateFailure(f"Content 0.5.0 Canary output mismatch: {item.get('fixture')}")
        canary_summary.append({"fixture": item["fixture"], "outputs": actual_kinds, "status": "completed"})
    if len(canary_summary) != 4:
        raise GateFailure("Content 0.5.0 requires exactly four independently bound offline Canaries")

    description = _validate_worker_describe(
        executable,
        "content.deepseek",
        {**os.environ, "KNOWLEDGE_CLIPPER_CONTENT_WORKER_ROOT": str(project_root / "content-worker")},
    )
    if (
        description.get("workerVersion") != "0.5.2"
        or description.get("outputKinds") != expected_output_kinds
    ):
        raise GateFailure("Content 0.5.0 describe evidence mismatch")

    admission_path = project_root / CONTENT_V050_REGISTRY_ADMISSION_RELATIVE
    if (
        not admission_path.is_file()
        or sha256_file(admission_path) != CONTENT_V050_REGISTRY_ADMISSION_SHA256
    ):
        raise GateFailure("Content 0.5.0 registry admission receipt is missing or hash-mismatched")
    admission = load_json(admission_path)
    candidate = admission.get("candidateRegistry", {})
    release = admission.get("releaseRoot", {})
    stale = admission.get("staleRegistryNegative", {})
    release_root = Path(release.get("path", ""))
    candidate_registry = Path(candidate.get("path", ""))
    stale_registry = Path(stale.get("path", ""))
    if (
        admission.get("status") != "conditional"
        or admission.get("worker")
        != {
            "workerId": "content.deepseek",
            "workerVersion": "0.5.0",
            "operations": expected_operations,
            "inputKinds": ["content-input"],
            "outputKinds": expected_output_kinds,
        }
        or candidate.get("authority") is not False
        or candidate.get("registryVersion") != "1.2.0"
        or admission.get("frozenBoundaries")
        != {"contractsV1Modified": False, "oldAuthorityV1_1Modified": False}
        or admission.get("readiness", {}).get("contentOnlyRegistryAdmission") != "GO"
        or admission.get("readiness", {}).get("newFiveWorkerAuthorityRegistry") != "NO-GO"
        or admission.get("externalProductionApisInvoked") is not False
    ):
        raise GateFailure("Content 0.5.0 registry admission violates the scoped boundary")
    for path, record, label in (
        (candidate_registry, candidate, "candidate registry"),
        (stale_registry, stale, "stale registry"),
    ):
        if (
            not path.is_file()
            or path.stat().st_size != record.get("bytes")
            or sha256_file(path) != record.get("sha256", "").upper()
        ):
            raise GateFailure(f"Content 0.5.0 {label} drifted")
    for item in admission.get("keyFiles", []):
        path = admission_path.parent / item.get("path", "")
        if (
            not path.is_file()
            or path.stat().st_size != item.get("bytes")
            or sha256_file(path) != item.get("sha256", "").upper()
        ):
            raise GateFailure(f"Content 0.5.0 admission key file drifted: {item.get('path')}")
    files = sum(1 for path in release_root.rglob("*") if path.is_file())
    total_bytes = sum(path.stat().st_size for path in release_root.rglob("*") if path.is_file())
    if (
        files != release.get("files")
        or total_bytes != release.get("bytes")
    ):
        raise GateFailure("Content 0.5.0 admission release-root inventory drifted")
    registry_gate = assess_authority_registry(
        candidate_registry,
        release_root,
        run_describe=True,
        expected_workers=KNOWLEDGE_CLIPPER_EXPECTED_WORKERS,
    )
    checked = registry_gate.get("checkedWorkers", [])
    if (
        admission.get("describe")
        != {
            "command": "release-root/workers/content/KnowledgeClipper.Content.Worker.exe describe --json",
            "exitCode": 0,
            "exactRegistrySetsMatch": True,
        }
        or admission.get("registryLoaderAdmission", {}).get("exitCode") != 0
        or admission.get("registryLoaderAdmission", {}).get("status") != "valid"
        or admission.get("registryLoaderAdmission", {}).get("workers") != 1
        or registry_gate.get("authorityRegistryEntries") != 1
        or len(checked) != 1
        or checked[0].get("workerId") != "content.deepseek"
        or checked[0].get("describePassed") is not True
        or stale.get("exitCode") != 1
        or stale.get("failClosed") is not True
        or stale.get("error") != "Worker describe outputKinds mismatch: content.deepseek"
    ):
        raise GateFailure("Content 0.5.0 RegistryLoader admission did not independently fail closed")
    authority_ids = {item["workerId"] for item in load_json(AUTHORITY_REGISTRY)["workers"]}
    return {
        "workerId": "content.deepseek",
        "workerVersion": description["workerVersion"],
        "receiptSha256": CONTENT_V050_RECEIPT_SHA256,
        "executableSha256": launcher["sha256"].upper(),
        "executableBytes": launcher["bytes"],
        "moduleOnboarding": "passed-scoped-question-language-extension",
        "onboardingState": "eligible-content-only-registry-admission-passed-five-worker-authority-pending",
        "brandIdentity": "passed-KnowledgeClipper-destination-neutral",
        "authorityPublished": "content.deepseek" in authority_ids,
        "authorityDescriptorRefreshRequired": False,
        "contentOnlyRegistryAdmission": "passed-conditional",
        "contentOnlyRegistryAdmissionReceiptSha256": CONTENT_V050_REGISTRY_ADMISSION_SHA256,
        "contentOnlyRegistryReleaseRoot": {
            "files": files,
            "bytes": total_bytes,
            "receiptBoundTreeSha256": release.get("treeSha256", "").upper(),
            "independentlyRecomputed": ["files", "bytes", "keyFiles", "describe", "registryLoader"],
        },
        "fiveWorkerAuthorityRegistry": "NO-GO",
        "releaseCandidateEligible": False,
        "latestTestsPassed": 58,
        "questionFixturesPassed": 8,
        "offlineCanaries": canary_summary,
        "operations": expected_operations,
        "operationCoverageSource": "authoritative-module-receipt-and-four-bound-canaries",
        "frozenDescribeSchemaCarriesOperations": False,
        "outputKinds": expected_output_kinds,
        "terminalLanguageWritingNewlineRemoved": True,
        "externalProviderCalls": 0,
        "remainingIntegrationBoundary": receipt.get("remainingControllerBoundaries", []),
    }


def assess_content_onboarding(project_root: Path = PROJECT_ROOT) -> dict[str, Any]:
    v050_receipt = project_root / CONTENT_V050_RECEIPT_RELATIVE
    if v050_receipt.is_file():
        return _assess_content_v050_extension(project_root)
    language_receipt = project_root / CONTENT_LANGUAGE_RECEIPT_RELATIVE
    if language_receipt.is_file():
        return _assess_content_language_extension(project_root)
    brand_receipt = project_root / CONTENT_BRAND_RECEIPT_RELATIVE
    if brand_receipt.is_file():
        return _assess_content_brand_migration(project_root)
    receipt_path = project_root / CONTENT_FINAL_RECEIPT_RELATIVE
    if not receipt_path.is_file() or sha256_file(receipt_path) != CONTENT_FINAL_RECEIPT_SHA256:
        raise GateFailure("content worker final receipt is missing or hash-mismatched")
    brand_project = project_root / "content-worker/launcher/KnowledgeClipper.Content.Worker.csproj"
    brand_executable = project_root / "workers/content/KnowledgeClipper.Content.Worker.exe"
    if brand_project.is_file() and not brand_executable.is_file():
        stable = verify_stable_checkpoint(project_root)
        return {
            "workerId": "content.deepseek",
            "workerVersion": "0.2.0",
            "historicalReceiptSha256": CONTENT_FINAL_RECEIPT_SHA256,
            "historicalQualityRefreshReceiptSha256": CONTENT_QUALITY_REFRESH_RECEIPT_SHA256,
            "moduleOnboarding": "historical-pass-current-brand-migration-unverified",
            "onboardingState": "brand-migration-pending",
            "authorityPublished": False,
            "releaseCandidateEligible": False,
            "brandIdentity": "pending-final-neutral-executable-and-receipt",
            "neutralProjectPresent": True,
            "neutralEntrypointPresent": False,
            "stableContracts": stable,
            "externalProviderCalls": 0,
            "reason": "current content source changed for KnowledgeClipper migration after the last final receipt; a new append-only module receipt is required",
        }
    receipt = load_json(receipt_path)
    checkpoint = receipt.get("validation", {}).get("stableCheckpoint", {})
    launcher = receipt.get("launcher", {})
    if (
        receipt.get("status") != "ready-for-controller-integration"
        or receipt.get("worker", {}).get("workerId") != "content.deepseek"
        or checkpoint.get("checkpointId") != CHECKPOINT_ID
        or checkpoint.get("receiptSha256", "").upper() != CHECKPOINT_RECEIPT_SHA256
        or checkpoint.get("filesRecomputed") != 21
        or checkpoint.get("mismatchCount") != 0
        or receipt.get("validation", {}).get("tests", {}).get("passed") != 16
        or receipt.get("validation", {}).get("tests", {}).get("failed") != 0
        or launcher.get("sha256", "").upper() != CONTENT_LAUNCHER_SHA256
    ):
        raise GateFailure("content worker final receipt violates the onboarding contract")
    executable = project_root / EXPECTED_WORKERS["content.deepseek"]["entrypoint"]
    if (
        not executable.is_file()
        or executable.stat().st_size != launcher.get("bytes")
        or sha256_file(executable) != CONTENT_LAUNCHER_SHA256
    ):
        raise GateFailure("content worker launcher is missing or hash-mismatched")
    description = _validate_worker_describe(executable, "content.deepseek")
    iteration_path = project_root / CONTENT_ITERATION_RECEIPT_RELATIVE
    live_path = project_root / CONTENT_LIVE_RECEIPT_RELATIVE
    cleaning_live_path = project_root / CONTENT_CLEANING_LIVE_RECEIPT_RELATIVE
    quality_refresh_path = project_root / CONTENT_QUALITY_REFRESH_RECEIPT_RELATIVE
    if (
        not iteration_path.is_file()
        or sha256_file(iteration_path) != CONTENT_ITERATION_RECEIPT_SHA256
        or not live_path.is_file()
        or sha256_file(live_path) != CONTENT_LIVE_RECEIPT_SHA256
        or not cleaning_live_path.is_file()
        or sha256_file(cleaning_live_path) != CONTENT_CLEANING_LIVE_RECEIPT_SHA256
        or not quality_refresh_path.is_file()
        or sha256_file(quality_refresh_path) != CONTENT_QUALITY_REFRESH_RECEIPT_SHA256
    ):
        raise GateFailure("content worker iteration evidence is missing or hash-mismatched")
    iteration = load_json(iteration_path)
    live = load_json(live_path)
    cleaning_live = load_json(cleaning_live_path)
    quality_refresh = load_json(quality_refresh_path)
    iteration_checkpoint = iteration.get("stableCheckpointRecalculation", {})
    live_checkpoint = live.get("stableCheckpointRecalculation", {})
    if (
        iteration.get("status") != "pass"
        or iteration.get("worker", {}).get("id") != "content.deepseek"
        or iteration.get("tests", {}).get("passed") != 22
        or iteration.get("tests", {}).get("failed") != 0
        or iteration_checkpoint.get("filesChecked") != 21
        or iteration_checkpoint.get("mismatches") != 0
        or live.get("status") != "pass"
        or live.get("worker", {}).get("id") != "content.deepseek"
        or live.get("regressionBeforeRerun", {}).get("passed") != 22
        or live.get("regressionBeforeRerun", {}).get("failed") != 0
        or live.get("finalAttempt", {}).get("status") != "completed"
        or live.get("finalAttempt", {}).get("moduleReleaseState") != "ready"
        or live_checkpoint.get("filesChecked") != 21
        or live_checkpoint.get("mismatches") != 0
    ):
        raise GateFailure("content worker latest iteration evidence violates the quality gate")
    implementation = {item.get("path"): item for item in quality_refresh.get("implementation", [])}
    cleaning_checkpoint = cleaning_live.get("stableCheckpoint", {})
    canary_states = [
        (item.get("mode"), item.get("commonStatus"), item.get("moduleReleaseState"))
        for item in cleaning_live.get("finalCanaries", [])
    ]
    if (
        cleaning_live.get("status") != "passed"
        or cleaning_live.get("worker", {}).get("workerId") != "content.deepseek"
        or cleaning_live.get("regression", {}).get("passed") != 28
        or cleaning_live.get("regression", {}).get("failed") != 0
        or cleaning_checkpoint.get("checkpointId") != CHECKPOINT_ID
        or cleaning_checkpoint.get("receiptSha256", "").upper() != CHECKPOINT_RECEIPT_SHA256
        or cleaning_checkpoint.get("filesChecked") != 21
        or cleaning_checkpoint.get("mismatchCount") != 0
        or cleaning_live.get("providerRunSummary", {}).get("secretMaterialRecorded") is not False
        or len(cleaning_live.get("finalCanaries", [])) != 4
        or ("live", "completed", "ready") not in canary_states
        or ("live", "completed", "unresolved") not in canary_states
        or ("replay", "completed", "ready") not in canary_states
        or ("replay", "completed", "unresolved") not in canary_states
    ):
        raise GateFailure("content worker cleaning evidence violates the quality gate")
    quality_checkpoint = quality_refresh.get("stableContracts", {})
    runtime_assets = quality_refresh.get("packagedRuntimeAssets", {})
    runtime_files, runtime_tree = content_asset_snapshot(project_root)
    if (
        quality_refresh.get("status") != "passed-scoped-module-final-not-candidate-bound"
        or quality_refresh.get("worker", {}).get("workerId") != "content.deepseek"
        or quality_refresh.get("worker", {}).get("moduleLauncherSha256", "").upper() != CONTENT_LAUNCHER_SHA256
        or quality_refresh.get("regression", {}).get("passed") != 44
        or quality_refresh.get("regression", {}).get("failed") != 0
        or quality_refresh.get("regression", {}).get("externalProviderCalls") != 0
        or quality_checkpoint.get("checkpointId") != CHECKPOINT_ID
        or quality_checkpoint.get("receiptSha256", "").upper() != CHECKPOINT_RECEIPT_SHA256
        or quality_checkpoint.get("filesChecked") != 21
        or quality_checkpoint.get("mismatches") != 0
        or quality_checkpoint.get("modified") is not False
        or runtime_assets.get("files") != runtime_files
        or runtime_assets.get("treeSha256", "").upper() != runtime_tree
        or quality_refresh.get("releaseBoundary", {}).get("moduleTaskComplete") is not True
    ):
        raise GateFailure("content worker quality refresh violates the quality gate")
    for path, expected_hash in CONTENT_IMPLEMENTATION_SHA256.items():
        item = implementation.get(path)
        source = project_root / path
        if (
            item is None
            or not source.is_file()
            or source.stat().st_size != item.get("bytes")
            or item.get("sha256", "").upper() != expected_hash
            or sha256_file(source) != expected_hash
        ):
            raise GateFailure(f"content worker implementation drift: {path}")
    authority_ids = {item["workerId"] for item in load_json(AUTHORITY_REGISTRY)["workers"]}
    return {
        "workerId": "content.deepseek",
        "workerVersion": description["workerVersion"],
        "receiptSha256": CONTENT_FINAL_RECEIPT_SHA256,
        "executableSha256": CONTENT_LAUNCHER_SHA256,
        "executableBytes": executable.stat().st_size,
        "moduleOnboarding": "passed",
        "onboardingState": "eligible-but-not-authority-published",
        "authorityPublished": "content.deepseek" in authority_ids,
        "releaseCandidateEligible": False,
        "latestIteration": "passed",
        "latestTestsPassed": 44,
        "moduleTaskComplete": True,
        "generalLearningLiveCanary": "completed-ready-historical",
        "cleaningLiveCanary": "ready-and-unresolved-boundaries-passed",
        "latestIterationReceiptSha256": CONTENT_ITERATION_RECEIPT_SHA256,
        "latestLiveReceiptSha256": CONTENT_LIVE_RECEIPT_SHA256,
        "latestCleaningReceiptSha256": CONTENT_CLEANING_LIVE_RECEIPT_SHA256,
        "latestQualityRefreshReceiptSha256": CONTENT_QUALITY_REFRESH_RECEIPT_SHA256,
        "runtimeAssetTreeSha256": runtime_tree,
    }


def assess_remnote_onboarding(project_root: Path = PROJECT_ROOT) -> dict[str, Any]:
    receipt_path = project_root / REMNOTE_BRAND_RECEIPT_RELATIVE
    if not receipt_path.is_file() or sha256_file(receipt_path) != REMNOTE_BRAND_RECEIPT_SHA256:
        raise GateFailure("KnowledgeClipper RemNote exporter migration receipt is missing or hash-mismatched")
    receipt = load_json(receipt_path)
    checkpoint = receipt.get("stableContracts", {})
    identity = receipt.get("productIdentity", {})
    binary = receipt.get("canonicalAssembly", {})
    legacy = receipt.get("legacyCompatibilityAlias", {})
    offline = receipt.get("offlineEvidence", {})
    verification = offline.get("contentAdapter", {})
    if (
        receipt.get("status") != "module_passed_external_release_boundaries_pending"
        or receipt.get("receiptType") != "knowledge-clipper-remnote-exporter-boundary-migration"
        or identity.get("displayName") != "零碎知识剪藏"
        or identity.get("asciiRoot") != "KnowledgeClipper"
        or identity.get("workerId") != "export.remnote"
        or checkpoint.get("checkpointId") != CHECKPOINT_ID
        or checkpoint.get("filesChecked") != 21
        or checkpoint.get("mismatches") != 0
        or checkpoint.get("modified") is not False
        or binary.get("path") != "workers/remnote/KnowledgeClipper.RemNoteExporter.Worker.exe"
        or binary.get("assemblyName") != "KnowledgeClipper.RemNoteExporter.Worker"
        or legacy.get("authority") is not False
        or legacy.get("bytesEqualCanonical") is not True
        or legacy.get("sha256", "").upper() != binary.get("sha256", "").upper()
        or offline.get("unitTests") != "26 passed, 0 failed"
        or verification.get("checks") != "11 passed, 0 failed"
        or verification.get("warningsAsErrors") is not True
        or receipt.get("realRemNoteCalled") is not False
    ):
        raise GateFailure("KnowledgeClipper RemNote exporter receipt violates the onboarding contract")
    executable = project_root / binary["path"]
    # The canonical module path has advanced to export.remnote 1.0.1. Keep
    # this historical migration receipt reproducible by resolving its exact
    # binary from the immutable v1.2.1 authority release.
    if executable.is_file() and sha256_file(executable) != binary.get("sha256", "").upper():
        executable = (
            project_root
            / "releases/knowledge-clipper-authority-v1.2.1-20260826T061500Z/win-x64"
            / binary["path"]
        )
    if (
        not executable.is_file()
        or executable.stat().st_size != binary.get("bytes")
        or sha256_file(executable) != binary.get("sha256", "").upper()
    ):
        raise GateFailure("KnowledgeClipper RemNote exporter executable is missing or hash-mismatched")
    description = _validate_worker_describe(executable, "export.remnote")
    consumer_path = project_root / verification.get("receipt", "")
    if not consumer_path.is_file() or sha256_file(consumer_path) != verification.get("receiptSha256", "").upper():
        raise GateFailure("RemNote content consumer receipt is missing or hash-mismatched")
    consumer = load_json(consumer_path)
    consumer_checks = consumer.get("checks", [])
    if (
        consumer.get("suite") != "export.remnote-content-to-remnote.v1-consumer-acceptance"
        or consumer.get("status") != "passed"
        or len(consumer_checks) != 11
        or any(item.get("status") != "passed" for item in consumer_checks)
    ):
        raise GateFailure("RemNote content consumer receipt violates the quality gate")
    for section in ("targetIdempotency", "commonContract"):
        item = offline.get(section, {})
        path = project_root / item.get("receipt", "")
        if not path.is_file() or sha256_file(path) != item.get("receiptSha256", "").upper():
            raise GateFailure(f"RemNote exporter {section} evidence is missing or hash-mismatched")
    authority_ids = {item["workerId"] for item in load_json(AUTHORITY_REGISTRY)["workers"]}
    return {
        "workerId": "export.remnote",
        "workerVersion": description["workerVersion"],
        "receiptSha256": REMNOTE_BRAND_RECEIPT_SHA256,
        "executableSha256": binary["sha256"].upper(),
        "executableBytes": binary["bytes"],
        "authoritativeEntrypoint": binary["path"],
        "moduleOnboarding": "passed",
        "onboardingState": "eligible-but-not-authority-published",
        "brandIdentity": "passed-KnowledgeClipper-export-boundary",
        "authorityPublished": "export.remnote" in authority_ids,
        "releaseCandidateEligible": False,
        "liveRemNoteEvidence": "retained-reference-not-rerun",
        "contentConsumerReceiptSha256": verification["receiptSha256"].upper(),
        "contentConsumerChecksPassed": 11,
        "legacyAliasAuthoritative": False,
    }


def assess_orchestrator_completion(project_root: Path = PROJECT_ROOT) -> dict[str, Any]:
    receipt_path = project_root / ORCHESTRATOR_COMPLETION_RECEIPT_RELATIVE
    if not receipt_path.is_file() or sha256_file(receipt_path) != ORCHESTRATOR_COMPLETION_RECEIPT_SHA256:
        raise GateFailure("orchestrator completion receipt is missing or hash-mismatched")
    receipt = load_json(receipt_path)
    release = receipt.get("release", {})
    implemented = receipt.get("implemented", {})
    validation = receipt.get("verification", {})
    checkpoint = receipt.get("immutableBoundaries", {}).get("frozenContracts", {})
    authority = receipt.get("immutableBoundaries", {}).get("authorityRegistry", {})
    executable = project_root / ORCHESTRATOR_EXE_RELATIVE
    if (
        receipt.get("status") != "passed-scope-production-no-go"
        or release.get("componentId") != "orchestrator.core"
        or release.get("version") != "1.1.0.0"
        or release.get("selfContained") is not True
        or release.get("singleFile") is not True
        or checkpoint.get("files") != 21
        or checkpoint.get("mismatchCount") != 0
        or authority.get("workers") != 0
        or authority.get("modified") is not False
        or validation.get("releaseBuild", {}).get("warnings") != 0
        or validation.get("releaseBuild", {}).get("errors") != 0
        or validation.get("orchestratorContractTests", {}).get("groups") != 23
        or validation.get("orchestratorContractTests", {}).get("status") != "passed"
        or validation.get("remNoteSharedConsumerAcceptance", {}).get("sha256", "").upper()
        != REMNOTE_CONSUMER_RECEIPT_SHA256
        or validation.get("speechReuseCompatibility", {}).get("sha256", "").upper()
        != SPEECH_CORE_REUSE_RECEIPT_SHA256
        or implemented.get("nullableRegistryRecovery", {}).get("registryMissingBranchNullSafe") is not True
        or implemented.get("reuseIsolation", {}).get("speechSynthesisCacheHit") is not True
        or implemented.get("reuseIsolation", {}).get("speechPlaybackResultReuse") is not False
        or release.get("sha256", "").upper() != ORCHESTRATOR_EXE_SHA256
        or not executable.is_file()
        or executable.stat().st_size != release.get("bytes")
        or sha256_file(executable) != ORCHESTRATOR_EXE_SHA256
        or receipt.get("productionReadiness", {}).get("status") != "NO-GO"
        or receipt.get("externalProductionApisInvoked") is not False
    ):
        raise GateFailure("orchestrator completion evidence violates the quality gate")
    completed = subprocess.run(
        [str(executable), "describe"], capture_output=True, text=True, timeout=10, check=False
    )
    if completed.returncode != 0:
        raise GateFailure("self-contained orchestrator describe failed")
    description = json.loads(completed.stdout)
    if description.get("componentId") != "orchestrator.core" or description.get("schemaVersion") != "1.0.0":
        raise GateFailure("self-contained orchestrator describe identity mismatch")
    return {
        "componentId": "orchestrator.core",
        "status": "passed-scope-production-no-go",
        "selfContained": True,
        "receiptSha256": ORCHESTRATOR_COMPLETION_RECEIPT_SHA256,
        "executableSha256": ORCHESTRATOR_EXE_SHA256,
        "executableBytes": executable.stat().st_size,
        "version": "1.1.0.0",
        "contractTestGroups": 23,
        "authorityRegistryEntries": 0,
        "productionDecision": "NO-GO",
        "candidateBound": False,
    }


def observe_orchestrator_brand_migration(project_root: Path = PROJECT_ROOT) -> dict[str, Any]:
    executable = project_root / ORCHESTRATOR_BRAND_EXE_RELATIVE
    if not executable.is_file():
        return {
            "status": "pending-neutral-binary",
            "candidateEligible": False,
            "externalProductionApisInvoked": False,
        }
    command = (
        "$v=(Get-Item -LiteralPath $env:KNOWLEDGE_CLIPPER_METADATA_PATH).VersionInfo;"
        "[pscustomobject]@{ProductName=$v.ProductName;OriginalFilename=$v.OriginalFilename;"
        "InternalName=$v.InternalName;FileDescription=$v.FileDescription}|ConvertTo-Json -Compress"
    )
    completed = subprocess.run(
        ["pwsh", "-NoLogo", "-NoProfile", "-Command", command],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=15,
        check=False,
        env={**os.environ, "KNOWLEDGE_CLIPPER_METADATA_PATH": str(executable)},
    )
    if completed.returncode != 0:
        raise GateFailure("cannot read KnowledgeClipper orchestrator file metadata")
    metadata = json.loads(completed.stdout)
    if (
        metadata.get("ProductName") != "零碎知识剪藏"
        or not str(metadata.get("OriginalFilename", "")).startswith("KnowledgeClipper.Orchestrator")
        or "remnote" in " ".join(str(value or "") for value in metadata.values()).lower()
    ):
        raise GateFailure("KnowledgeClipper orchestrator metadata is not destination-neutral")
    return {
        "status": "observed-neutral-binary-awaiting-module-receipt",
        "path": ORCHESTRATOR_BRAND_EXE_RELATIVE.as_posix(),
        "bytes": executable.stat().st_size,
        "sha256": sha256_file(executable),
        "fileMetadata": metadata,
        "moduleReceipt": None,
        "candidateEligible": False,
        "externalProductionApisInvoked": False,
    }


def assess_expected_worker(
    worker_id: str,
    release_root: Path,
    onboarding_receipt: Path | None = None,
) -> dict[str, Any]:
    expected = EXPECTED_WORKERS[worker_id]
    executable = release_root / expected["entrypoint"]
    reasons: list[str] = []
    if not executable.is_file():
        reasons.append("final-entrypoint-missing")
    if onboarding_receipt is None or not onboarding_receipt.is_file():
        reasons.append("module-onboarding-receipt-missing")
    authority_ids = {item["workerId"] for item in load_json(AUTHORITY_REGISTRY)["workers"]}
    if worker_id not in authority_ids:
        reasons.append("not-in-authority-registry")
    return {
        "workerId": worker_id,
        "finalEntrypoint": expected["entrypoint"],
        "finalEntrypointPresent": executable.is_file(),
        "registryOnboarding": "passed" if not reasons else "pending",
        "reasons": reasons,
    }


def assess_image_onboarding(project_root: Path = PROJECT_ROOT) -> dict[str, Any]:
    worker_id = "image.ocr.mineru"
    expected = EXPECTED_WORKERS[worker_id]
    receipt_path = project_root / IMAGE_ONBOARDING_RECEIPT_RELATIVE
    if not receipt_path.is_file() or sha256_file(receipt_path) != IMAGE_ONBOARDING_RECEIPT_SHA256:
        raise GateFailure("image OCR onboarding receipt is missing or hash-mismatched")
    receipt = load_json(receipt_path)
    if receipt.get("status") != "passed" or receipt.get("worker", {}).get("workerId") != worker_id:
        raise GateFailure("image OCR onboarding receipt identity/status mismatch")
    checkpoint = receipt.get("stableContractCheckpoint", {})
    if (
        checkpoint.get("checkpointId") != CHECKPOINT_ID
        or checkpoint.get("receiptSha256", "").upper() != CHECKPOINT_RECEIPT_SHA256
        or checkpoint.get("mismatches") != 0
    ):
        raise GateFailure("image OCR onboarding receipt is not bound to the stable checkpoint")
    artifact = receipt.get("artifact", {})
    if artifact.get("path") != expected["entrypoint"]:
        raise GateFailure("image OCR onboarding entrypoint does not match the planned release path")
    executable = project_root / IMAGE_IMMUTABLE_EVIDENCE_RELATIVE
    if not executable.is_file():
        raise GateFailure("immutable image OCR onboarding executable is missing")
    if executable.stat().st_size != artifact.get("bytes") or sha256_file(executable) != artifact.get("sha256", "").upper():
        raise GateFailure("image OCR onboarding receipt does not bind the executable bytes/hash")
    completed = subprocess.run(
        [str(executable), "describe", "--json"], capture_output=True, text=True, timeout=10, check=False
    )
    if completed.returncode != 0:
        raise GateFailure("image OCR describe failed during quality onboarding gate")
    try:
        description = json.loads(completed.stdout)
    except json.JSONDecodeError as exc:
        raise GateFailure("image OCR describe did not return one JSON document") from exc
    schemas = load_schemas()
    errors = list(
        schemas.validator(SCHEMAS_ROOT / "common" / "worker-describe.schema.json").iter_errors(description)
    )
    if errors or description.get("workerId") != worker_id:
        message = errors[0].message if errors else "worker ID mismatch"
        raise GateFailure(f"image OCR describe contract mismatch: {message}")
    authority_ids = {item["workerId"] for item in load_json(AUTHORITY_REGISTRY)["workers"]}
    working_executable = project_root / expected["entrypoint"]
    working_matches = (
        working_executable.is_file()
        and working_executable.stat().st_size == artifact.get("bytes")
        and sha256_file(working_executable) == artifact.get("sha256", "").upper()
    )
    scenario_path = project_root / IMAGE_SCENARIO_MATRIX_RECEIPT_RELATIVE
    if not scenario_path.is_file() or sha256_file(scenario_path) != IMAGE_SCENARIO_MATRIX_RECEIPT_SHA256:
        raise GateFailure("image OCR offline scenario matrix is missing or hash-mismatched")
    scenario = load_json(scenario_path)
    if (
        scenario.get("status") != "passed"
        or scenario.get("worker", {}).get("workerId") != worker_id
        or scenario.get("worker", {}).get("plannedEntrypointSha256", "").upper()
        != artifact.get("sha256", "").upper()
        or scenario.get("regression", {}).get("testsPassed") != 34
        or scenario.get("contractValidation", {}).get("authoritativeRegistryEntries") != 0
        or scenario.get("stableCheckpoint", {}).get("filesChecked") != 21
        or scenario.get("stableCheckpoint", {}).get("mismatches") != 0
        or scenario.get("historicalMineruCanary", {}).get("reuploaded") is not False
        or set(scenario.get("canaries", {})) != {"success", "partialRetry", "resume", "original"}
    ):
        raise GateFailure("image OCR offline scenario matrix violates the quality gate")
    terminal_path = project_root / IMAGE_TERMINAL_FAILURE_RECEIPT_RELATIVE
    unknown_path = project_root / IMAGE_UNKNOWN_EXTERNAL_RECEIPT_RELATIVE
    if (
        not terminal_path.is_file()
        or sha256_file(terminal_path) != IMAGE_TERMINAL_FAILURE_RECEIPT_SHA256
        or not unknown_path.is_file()
        or sha256_file(unknown_path) != IMAGE_UNKNOWN_EXTERNAL_RECEIPT_SHA256
    ):
        raise GateFailure("image OCR failure-state evidence is missing or hash-mismatched")
    terminal = load_json(terminal_path)
    unknown = load_json(unknown_path)
    provider_failed = terminal.get("terminalFailureCanaries", {}).get("providerFailed", {})
    tampered = terminal.get("terminalFailureCanaries", {}).get("tamperedInput", {})
    unknown_canary = unknown.get("unknownExternalStateCanary", {})
    if (
        terminal.get("status") != "passed"
        or provider_failed.get("workerStatus") != "failed"
        or provider_failed.get("commonOutputs") != 0
        or tampered.get("workerStatus") != "failed"
        or tampered.get("providerCalled") is not False
        or unknown.get("status") != "passed"
        or unknown_canary.get("workerStatus") != "unknown_external_state"
        or unknown_canary.get("retryable") is not False
        or unknown_canary.get("commonOutputs") != 0
        or unknown_canary.get("submissionPublished") is not False
        or unknown.get("regression", {}).get("testsPassed") != 37
        or unknown.get("privacy", {}).get("realMineruCalled") is not False
        or unknown.get("stableCheckpoint", {}).get("mismatches") != 0
    ):
        raise GateFailure("image OCR failure-state evidence violates the quality gate")
    brand_receipt_path = project_root / IMAGE_BRAND_RECEIPT_RELATIVE
    if not brand_receipt_path.is_file() or sha256_file(brand_receipt_path) != IMAGE_BRAND_RECEIPT_SHA256:
        raise GateFailure("KnowledgeClipper image OCR identity receipt is missing or hash-mismatched")
    brand_receipt = load_json(brand_receipt_path)
    brand_candidate = brand_receipt.get("candidate", {})
    brand_legacy = brand_receipt.get("legacyCompatibilityAlias", {})
    brand_executable = project_root / brand_candidate.get("path", "")
    if (
        brand_receipt.get("schemaVersion") != "knowledge-clipper-image-ocr-public-identity-audit.v1"
        or brand_receipt.get("status") != "passed"
        or brand_receipt.get("productDisplayName") != "零碎知识剪藏"
        or brand_receipt.get("assemblyRoot") != "KnowledgeClipper"
        or brand_candidate.get("path") != "workers/image-ocr/KnowledgeClipper.ImageOcr.Worker.exe"
        or brand_candidate.get("descriptor", {}).get("workerId") != worker_id
        or brand_legacy.get("candidate") is not False
        or brand_receipt.get("checks", {}).get("candidateFileNameDestinationNeutral") is not True
        or brand_receipt.get("checks", {}).get("descriptorDestinationNeutral") is not True
        or brand_receipt.get("sourceForbiddenDestinationMatches") != []
        or not brand_executable.is_file()
        or brand_executable.stat().st_size != brand_candidate.get("bytes")
        or sha256_file(brand_executable) != brand_candidate.get("sha256", "").upper()
    ):
        raise GateFailure("KnowledgeClipper image OCR identity receipt violates the quality gate")
    brand_description = _validate_worker_describe(brand_executable, worker_id)
    validation_path = project_root / IMAGE_BRAND_VALIDATION_RECEIPT_RELATIVE
    if not validation_path.is_file() or sha256_file(validation_path) != IMAGE_BRAND_VALIDATION_RECEIPT_SHA256:
        raise GateFailure("KnowledgeClipper image OCR destination-neutral validation is missing or hash-mismatched")
    validation = load_json(validation_path)
    validation_candidate = validation.get("candidate", {})
    validation_checkpoint = validation.get("commonV1", {}).get("stableCheckpoint", {})
    if (
        validation.get("status") != "passed"
        or validation.get("productIdentity", {}).get("displayName") != "零碎知识剪藏"
        or validation.get("productIdentity", {}).get("asciiRoot") != "KnowledgeClipper"
        or validation.get("productIdentity", {}).get("stableWorkerId") != worker_id
        or validation_candidate.get("path") != brand_candidate.get("path")
        or validation_candidate.get("bytes") != brand_candidate.get("bytes")
        or validation_candidate.get("sha256", "").upper() != brand_candidate.get("sha256", "").upper()
        or validation.get("publicIdentityAudit", {}).get("sha256", "").upper() != IMAGE_BRAND_RECEIPT_SHA256
        or validation.get("regression", {}).get("testsPassed") != 39
        or validation.get("regression", {}).get("exitCode") != 0
        or validation.get("liveCanaryPreparation", {}).get("executedRealMineru") is not False
        or validation.get("liveCanaryPreparation", {}).get("providerCalled") is not False
        or validation_checkpoint.get("checkpointId") != CHECKPOINT_ID
        or validation_checkpoint.get("receiptSha256", "").upper() != CHECKPOINT_RECEIPT_SHA256
        or validation_checkpoint.get("filesChecked") != 21
        or validation_checkpoint.get("mismatches") != 0
    ):
        raise GateFailure("KnowledgeClipper image OCR destination-neutral validation violates the quality gate")
    if len(validation.get("offlineCanaries", [])) != 7:
        raise GateFailure("KnowledgeClipper image OCR offline Canary matrix is incomplete")
    for item in validation["offlineCanaries"]:
        path = project_root / item["receiptPath"]
        if not path.is_file() or sha256_file(path) != item.get("receiptSha256", "").upper():
            raise GateFailure(f"KnowledgeClipper image OCR Canary mismatch: {path}")
    return {
        "workerId": worker_id,
        "moduleOnboarding": "passed",
        "onboardingState": "eligible-but-not-authority-published",
        "onboardingReceiptSha256": IMAGE_ONBOARDING_RECEIPT_SHA256,
        "workerVersion": brand_description["workerVersion"],
        "executableSha256": brand_candidate["sha256"].upper(),
        "executableBytes": brand_candidate["bytes"],
        "authoritativeEntrypoint": brand_candidate["path"],
        "onboardingEvidencePath": IMAGE_IMMUTABLE_EVIDENCE_RELATIVE.as_posix(),
        "workingEntrypointState": "neutral-KnowledgeClipper-entrypoint-verified",
        "historicalEntrypointMatchesOnboardingEvidence": working_matches,
        "brandIdentity": "passed-KnowledgeClipper",
        "brandIdentityReceiptSha256": IMAGE_BRAND_RECEIPT_SHA256,
        "brandValidationReceiptSha256": IMAGE_BRAND_VALIDATION_RECEIPT_SHA256,
        "offlineScenarioMatrix": "passed",
        "offlineScenarioMatrixReceiptSha256": IMAGE_SCENARIO_MATRIX_RECEIPT_SHA256,
        "terminalFailureReceiptSha256": IMAGE_TERMINAL_FAILURE_RECEIPT_SHA256,
        "unknownExternalStateReceiptSha256": IMAGE_UNKNOWN_EXTERNAL_RECEIPT_SHA256,
        "unknownExternalState": "passed-fail-closed",
        "realMineruRerun": False,
        "latestTestsPassed": 39,
        "authorityPublished": worker_id in authority_ids,
        "releaseCandidateEligible": False,
        "reason": "module onboarding evidence is not authority registry publication or candidate-bound Canary evidence",
    }


def assess_desktop_contract(project_root: Path = PROJECT_ROOT) -> dict[str, Any]:
    receipt_path = project_root / DESKTOP_CONTRACT_RECEIPT_RELATIVE
    if not receipt_path.is_file() or sha256_file(receipt_path) != DESKTOP_CONTRACT_RECEIPT_SHA256:
        raise GateFailure("desktop automation contract receipt is missing or hash-mismatched")
    receipt = load_json(receipt_path)
    contract_root = receipt_path.parent
    actual_paths = {
        path.relative_to(contract_root).as_posix()
        for path in contract_root.rglob("*")
        if path.is_file() and path.name != "contract-receipt.json"
    }
    receipt_paths = {item["path"] for item in receipt.get("files", [])}
    if len(receipt_paths) != 11 or receipt_paths != actual_paths:
        raise GateFailure("desktop automation contract receipt file set mismatch")
    canonical_lines: list[str] = []
    file_hashes: dict[str, str] = {}
    for item in sorted(receipt["files"], key=lambda value: value["path"].encode("utf-8")):
        path = contract_root / item["path"]
        actual_hash = sha256_file(path)
        if path.stat().st_size != item["bytes"] or actual_hash != item["sha256"].upper():
            raise GateFailure(f"desktop automation contract file mismatch: {item['path']}")
        file_hashes[item["path"]] = actual_hash
        canonical_lines.append(f"{item['path']}|{actual_hash.lower()}")
    tree_hash = hashlib.sha256(("\n".join(canonical_lines) + "\n").encode("utf-8")).hexdigest().upper()
    if tree_hash != DESKTOP_CONTRACT_TREE_SHA256 or tree_hash != receipt.get("treeSha256", "").upper():
        raise GateFailure("desktop automation contract tree hash mismatch")
    if file_hashes.get("capture-bundle.v1.schema.json") != "0971421423D0CB03B45B8A2F07C7461D1DC741EF19B58E7CD4A59B9827C3F979":
        raise GateFailure("desktop capture bundle schema hash mismatch")
    if file_hashes.get("desktop-automation.contract.json") != "789135BDD009BC12F6960BD62B4B58E5077463A11581EC94FF100E3DDEBDE8FE":
        raise GateFailure("desktop automation descriptor hash mismatch")
    return {
        "contractId": receipt["contractId"],
        "contractVersion": receipt["contractVersion"],
        "status": "passed",
        "receiptSha256": DESKTOP_CONTRACT_RECEIPT_SHA256,
        "treeSha256": tree_hash,
        "filesChecked": len(receipt_paths),
        "realWindowsCanary": "pending",
        "productionOrchestratorProcessCanary": "pending",
    }


def assess_desktop_clickability(project_root: Path = PROJECT_ROOT) -> dict[str, Any]:
    receipt_path = project_root / DESKTOP_CLICKABILITY_RECEIPT_RELATIVE
    if not receipt_path.is_file() or sha256_file(receipt_path) != DESKTOP_CLICKABILITY_RECEIPT_SHA256:
        raise GateFailure("desktop clickability receipt is missing or hash-mismatched")
    receipt = load_json(receipt_path)
    package = receipt.get("package", {})
    evidence = receipt.get("automatedEvidence", {})
    semantic = evidence.get("semanticContractRegression", {})
    windows_ui = evidence.get("windowsUiCanary", {})
    visual = evidence.get("visualPreviewCanary", {})
    brand = evidence.get("brandSurfaceCanary", {})
    startup = evidence.get("startupDiagnostic", {})
    boundaries = receipt.get("realWindowsBoundaries", {})
    build_receipt = project_root / package.get("buildReceiptPath", "")
    executable = project_root / receipt.get("startEntrypoint", "")
    application_dll = executable.with_suffix(".dll")
    if (
        receipt.get("schemaVersion") != "desktop-ui-clickable-canary.v1"
        or receipt.get("productName") != "零碎知识剪藏"
        or executable.name != "KnowledgeClipper.exe"
        or not build_receipt.is_file()
        or sha256_file(build_receipt) != package.get("buildReceiptSha256", "").upper()
        or not executable.is_file()
        or sha256_file(executable) != DESKTOP_RELEASE_EXE_SHA256
        or not application_dll.is_file()
        or sha256_file(application_dll) != package.get("applicationDllSha256", "").upper()
        or package.get("selfContained") is not True
        or package.get("runtimeIdentifier") != "win-x64"
        or package.get("fileCount") != 243
        or semantic.get("status") != "PASS"
        or semantic.get("semanticHarnessResponses") != 17
        or windows_ui.get("status") != "PASS"
        or windows_ui.get("assertions") != 54
        or visual.get("status") != "PASS"
        or len(visual.get("files", [])) != 7
        or brand.get("status") != "PASS"
        or brand.get("productName") != "零碎知识剪藏"
        or brand.get("assemblyName") != "KnowledgeClipper"
        or brand.get("entrypoint") != "KnowledgeClipper.exe"
        or startup.get("status") != "PASS"
        or startup.get("productionWorkDirectoryCreated") is not False
        or startup.get("externalConnections") != 0
        or startup.get("namedPipeInvoked") is not False
        or boundaries.get("desktopToRealRemNoteE2E") != "NOT_RUN"
        or boundaries.get("remNoteInvoked") is not False
        or boundaries.get("mineruInvoked") is not False
        or receipt.get("frozenDesktopContract", {}).get("changed") is not False
    ):
        raise GateFailure("desktop clickability receipt violates the scoped automated boundary")
    evidence_files = [semantic, windows_ui, visual, brand, startup]
    for item in evidence_files:
        evidence_path = project_root / item.get("log", item.get("report", ""))
        if not evidence_path.is_file() or sha256_file(evidence_path) != item.get("sha256", "").upper():
            raise GateFailure(f"desktop clickable evidence file mismatch: {evidence_path}")
    for item in visual.get("files", []):
        visual_path = project_root / item["path"]
        if (
            not visual_path.is_file()
            or visual_path.stat().st_size != item.get("bytes")
            or sha256_file(visual_path) != item.get("sha256", "").upper()
        ):
            raise GateFailure(f"desktop visual preview mismatch: {visual_path}")
    current_preseal_dll = project_root / CURRENT_PRESEAL_ROOT_RELATIVE / "desktop/RemNoteQuickClipper.dll"
    preseal_dll_matches = (
        current_preseal_dll.is_file()
        and sha256_file(current_preseal_dll) == package.get("applicationDllSha256", "").upper()
    )
    return {
        "status": "passed-scoped-user-boundaries-pending",
        "evidenceReleaseRoot": str(executable.parent.relative_to(project_root)).replace("\\", "/"),
        "receiptSha256": DESKTOP_CLICKABILITY_RECEIPT_SHA256,
        "executableSha256": DESKTOP_RELEASE_EXE_SHA256,
        "packageTopLevelFilesChecked": 243,
        "semanticHarnessResponses": 17,
        "windowsUiAssertions": 54,
        "visualPreviews": 7,
        "brandIdentity": "passed-KnowledgeClipper",
        "startupDiagnosticHiddenWrites": 0,
        "systemScreenshotAutomatedCanary": "passed",
        "physicalScreenshotDrag": "pending-user-experience",
        "physicalUserExperience": "pending",
        "candidateBound": False,
        "currentPresealApplicationDllMatches": preseal_dll_matches,
    }


def _assess_anki_brand_migration(project_root: Path) -> dict[str, Any]:
    migration_path = project_root / ANKI_BRAND_RECEIPT_RELATIVE
    if sha256_file(migration_path) != ANKI_BRAND_RECEIPT_SHA256:
        raise GateFailure("KnowledgeClipper Anki migration receipt is hash-mismatched")
    migration = load_json(migration_path)
    product = migration.get("product", {})
    worker = migration.get("worker", {})
    legacy = migration.get("legacyCompatibilityAlias", {})
    stable = migration.get("stableContracts", {})
    module = migration.get("moduleValidation", {})
    consumer_summary = migration.get("consumerAcceptance", {})
    executable = project_root / worker.get("authoritativeEntrypoint", "")
    if (
        migration.get("status") != "passed-scoped-onboarding-refresh-authority-checkpoint-pending"
        or product != {"displayName": "零碎知识剪藏", "asciiRoot": "KnowledgeClipper"}
        or worker.get("workerId") != "export.anki"
        or worker.get("workerVersion") != "1.1.3"
        or worker.get("authoritativeEntrypoint") != "workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe"
        or legacy.get("authorityForNewIdentity") is not False
        or legacy.get("byteIdenticalToAuthoritativeEntrypoint") is not False
        or legacy.get("matchesFrozenAuthorityDependencyManifest") is not True
        or legacy.get("writtenByNewBuildScript") is not False
        or stable.get("checkpointId") != CHECKPOINT_ID
        or stable.get("receiptSha256", "").upper() != CHECKPOINT_RECEIPT_SHA256
        or stable.get("modified") is not False
        or migration.get("regression", {}).get("passed") != 24
        or migration.get("regression", {}).get("failed") != 0
        or migration.get("regression", {}).get("realAnkiCalls") != 0
        or not executable.is_file()
    ):
        raise GateFailure("KnowledgeClipper Anki migration receipt violates the quality gate")
    description = _validate_worker_describe(executable, "export.anki")
    current_sha256 = sha256_file(executable)
    current_bytes = executable.stat().st_size
    current_matches_receipt = (
        current_bytes == worker.get("bytes")
        and current_sha256 == worker.get("sha256", "").upper()
        and description.get("workerVersion") == worker.get("workerVersion")
    )
    if not current_matches_receipt:
        authority_ids = {item["workerId"] for item in load_json(AUTHORITY_REGISTRY)["workers"]}
        observed_validations = sorted(
            (project_root / "workers/anki/canary").glob("*/exe-validation-result.json"),
            key=lambda path: path.stat().st_mtime_ns,
            reverse=True,
        )
        observed_validation = None
        if observed_validations:
            candidate = load_json(observed_validations[0])
            candidate_exe = candidate.get("exe", {})
            if (
                candidate.get("status") == "passed"
                and candidate.get("describe", {}).get("workerId") == "export.anki"
                and candidate.get("describe", {}).get("workerVersion") == description.get("workerVersion")
                and candidate_exe.get("bytes") == current_bytes
                and candidate_exe.get("sha256", "").upper() == current_sha256
            ):
                observed_validation = {
                    "path": normalized_relative(observed_validations[0], project_root),
                    "sha256": sha256_file(observed_validations[0]),
                    "status": "passed-unbound-module-evidence",
                }
        return {
            "workerId": "export.anki",
            "workerVersion": description["workerVersion"],
            "businessCanary": "passed-historical",
            "historicalBusinessCanaryWorkerId": "anki-exporter",
            "historicalBrandReceiptSha256": ANKI_BRAND_RECEIPT_SHA256,
            "executableSha256": current_sha256,
            "executableBytes": current_bytes,
            "authoritativeEntrypoint": worker["authoritativeEntrypoint"],
            "brandIdentity": "passed-KnowledgeClipper",
            "moduleOnboarding": "prior-pass-current-binary-unreceipted",
            "onboardingState": "module-receipt-pending",
            "currentValidationObserved": observed_validation,
            "authorityPublished": "export.anki" in authority_ids,
            "releaseCandidateEligible": False,
            "externalAnkiCalled": False,
            "reason": "the current executable no longer matches the latest append-only module receipt; observed validation cannot replace a final module receipt",
        }

    consumer_path = project_root / consumer_summary.get("path", "")
    if not consumer_path.is_file() or sha256_file(consumer_path) != consumer_summary.get("sha256", "").upper():
        raise GateFailure("KnowledgeClipper Anki consumer evidence is missing or hash-mismatched")
    consumer = load_json(consumer_path)
    if (
        consumer.get("status") != "passed"
        or consumer.get("product") != product
        or consumer.get("worker", {}).get("workerId") != "export.anki"
        or consumer.get("worker", {}).get("path") != worker.get("authoritativeEntrypoint")
        or consumer.get("worker", {}).get("sha256", "").upper() != worker.get("sha256", "").upper()
        or len(consumer.get("checks", [])) != 3
        or any(item.get("status") != "passed" for item in consumer.get("checks", []))
        or consumer.get("evidence", {}).get("success", {}).get("cards") != 5
        or consumer.get("policy", {}).get("externalAnki")
        != "not contacted; request operation=validate and fixture endpoint=127.0.0.1:65534"
    ):
        raise GateFailure("KnowledgeClipper Anki consumer receipt violates the quality gate")
    for item in consumer.get("sourceFixtures", []):
        source = project_root / item["path"]
        if not source.is_file() or sha256_file(source) != item.get("sha256", "").upper():
            raise GateFailure(f"KnowledgeClipper Anki consumer fixture mismatch: {source}")

    declared_module_path = project_root / module.get("path", "")
    matching_module_paths = [
        path for path in (project_root / "workers/anki/canary").glob("*/exe-validation-result.json")
        if sha256_file(path) == module.get("sha256", "").upper()
    ]
    module_path_valid = declared_module_path.is_file() and sha256_file(declared_module_path) == module.get("sha256", "").upper()
    authority_ids = {item["workerId"] for item in load_json(AUTHORITY_REGISTRY)["workers"]}
    return {
        "workerId": "export.anki",
        "workerVersion": description["workerVersion"],
        "businessCanary": "passed-historical",
        "businessCanarySha256": ANKI_BUSINESS_CANARY_SHA256,
        "historicalBusinessCanaryWorkerId": "anki-exporter",
        "executableSha256": worker["sha256"].upper(),
        "executableBytes": worker["bytes"],
        "authoritativeEntrypoint": worker["authoritativeEntrypoint"],
        "brandIdentity": "passed-KnowledgeClipper",
        "brandMigrationReceiptSha256": ANKI_BRAND_RECEIPT_SHA256,
        "moduleValidationDeclaredPathValid": module_path_valid,
        "moduleValidationMatchingPaths": [normalized_relative(path, project_root) for path in matching_module_paths],
        "consumerAcceptanceReceiptSha256": consumer_summary["sha256"].upper(),
        "consumerAcceptanceCards": 5,
        "latestTestsPassed": 24,
        "externalAnkiCalled": False,
        "moduleOnboarding": "passed" if module_path_valid else "pending-receipt-path-correction",
        "onboardingState": "eligible-but-not-authority-published" if module_path_valid else "evidence-path-defect",
        "authorityPublished": "export.anki" in authority_ids,
        "releaseCandidateEligible": False,
        "reason": (
            "module migration evidence is not authority publication"
            if module_path_valid
            else "module receipt binds a non-existent validation path; matching bytes exist but cannot replace the declared append-only evidence path"
        ),
    }


def _assess_anki_current_acceptance(project_root: Path) -> dict[str, Any]:
    receipt_path = project_root / ANKI_CURRENT_ACCEPTANCE_RELATIVE
    if not receipt_path.is_file() or sha256_file(receipt_path) != ANKI_CURRENT_ACCEPTANCE_SHA256:
        raise GateFailure("current Anki authority acceptance receipt is missing or hash-mismatched")
    receipt = load_json(receipt_path)
    worker = receipt.get("worker", {})
    authority = receipt.get("contentAuthority", {})
    extension = receipt.get("languageWritingExtension", {})
    evidence = receipt.get("evidence", {})
    language = evidence.get("languageWritingExtension", {})
    executable = project_root / worker.get("path", "")
    # The current module path is v1.1.5; this append-only v1.1.4 acceptance
    # remains verifiable against its byte-identical v1.2.2 authority copy.
    if executable.is_file() and sha256_file(executable) != worker.get("sha256", "").upper():
        executable = (
            project_root
            / "releases/knowledge-clipper-authority-v1.2.2-20260828T012500Z/win-x64"
            / worker.get("path", "")
        )
    if (
        receipt.get("suite") != "export.anki-content-to-anki.v1-consumer-acceptance"
        or receipt.get("status") != "passed"
        or receipt.get("product") != {"displayName": "零碎知识剪藏", "asciiRoot": "KnowledgeClipper"}
        or worker.get("workerId") != "export.anki"
        or worker.get("workerVersion") != "1.1.4"
        or worker.get("path") != "workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe"
        or not executable.is_file()
        or executable.stat().st_size != 10209227
        or sha256_file(executable) != worker.get("sha256", "").upper()
        or authority.get("receiptSha256", "").upper() != CONTENT_LANGUAGE_AUTHORITY_SHA256
        or authority.get("verified") is not True
        or extension.get("status") != "passed"
        or extension.get("writingCards") != 2
        or extension.get("bothGroups") != 2
        or extension.get("choiceOwnedByExporter") is not False
        or receipt.get("external_api_calls") != 0
        or len(receipt.get("checks", [])) != 4
        or any(item.get("status") != "passed" for item in receipt.get("checks", []))
        or language.get("inputOrderPreserved") is not True
        or language.get("choiceAndTokenLeakage") is not False
        or language.get("externalAnkiCalls") != 0
    ):
        raise GateFailure("current Anki authority acceptance violates the scoped quality boundary")
    description = _validate_worker_describe(executable, "export.anki")
    if description.get("workerVersion") != "1.1.4":
        raise GateFailure("current Anki authority acceptance descriptor mismatch")
    for item in receipt.get("sourceFixtures", []):
        source = windows_long_path(project_root / item.get("path", ""))
        if not source.is_file() or sha256_file(source) != item.get("sha256", "").upper():
            raise GateFailure(f"current Anki authority source drifted: {item.get('path')}")
    for result_record in (evidence.get("success", {}), language):
        result_path = windows_long_path(project_root / result_record.get("exporterResult", ""))
        if not result_path.is_file() or sha256_file(result_path) != result_record.get("exporterResultSha256", "").upper():
            raise GateFailure("current Anki validate-only result is missing or hash-mismatched")
        result = load_json(result_path)
        if result.get("status") != "completed" or result.get("payload", {}).get("validateOnly") is not True:
            raise GateFailure("current Anki acceptance result is not validate-only")
    stable = verify_stable_checkpoint(project_root)
    authority_ids = {item["workerId"] for item in load_json(AUTHORITY_REGISTRY)["workers"]}
    return {
        "workerId": "export.anki",
        "workerVersion": "1.1.4",
        "receiptSha256": ANKI_CURRENT_ACCEPTANCE_SHA256,
        "contentAuthorityReceiptSha256": CONTENT_LANGUAGE_AUTHORITY_SHA256,
        "executableSha256": worker["sha256"].upper(),
        "executableBytes": executable.stat().st_size,
        "authoritativeEntrypoint": worker["path"],
        "brandIdentity": "passed-KnowledgeClipper",
        "moduleOnboarding": "passed-scoped-current-content-acceptance",
        "onboardingState": "eligible-but-neutral-authority-checkpoint-pending",
        "consumerAcceptanceChecksPassed": 4,
        "writingCardsAccepted": 2,
        "bothGroupsAccepted": 2,
        "validateOnly": True,
        "stableContracts": stable,
        "authorityPublished": "export.anki" in authority_ids,
        "releaseCandidateEligible": False,
        "externalAnkiCalled": False,
        "reason": "current module acceptance is not a neutral authority checkpoint or candidate-bound chain receipt",
    }


def assess_anki_boundary(project_root: Path = PROJECT_ROOT) -> dict[str, Any]:
    current_acceptance = project_root / ANKI_CURRENT_ACCEPTANCE_RELATIVE
    if current_acceptance.is_file():
        return _assess_anki_current_acceptance(project_root)
    migration_path = project_root / ANKI_BRAND_RECEIPT_RELATIVE
    if migration_path.is_file():
        return _assess_anki_brand_migration(project_root)
    canary_path = project_root / ANKI_BUSINESS_CANARY_RELATIVE
    if not canary_path.is_file() or sha256_file(canary_path) != ANKI_BUSINESS_CANARY_SHA256:
        raise GateFailure("Anki business Canary is missing or hash-mismatched")
    canary = load_json(canary_path)
    checks = canary.get("checks", {})
    if canary.get("status") != "passed" or not checks or not all(checks.values()):
        raise GateFailure("Anki business Canary did not pass every recorded check")
    observed_worker_id = canary.get("worker", {}).get("id")
    noop_receipt_path = project_root / ANKI_IDENTITY_NOOP_RECEIPT_RELATIVE
    noop_result_path = project_root / ANKI_IDENTITY_NOOP_RESULT_RELATIVE
    exe_receipt_path = project_root / ANKI_EXE_VALIDATION_RECEIPT_RELATIVE
    quality_receipt_path = project_root / ANKI_QUALITY_REFRESH_RECEIPT_RELATIVE
    consumer_receipt_path = project_root / ANKI_CONSUMER_RECEIPT_RELATIVE
    if (
        not noop_receipt_path.is_file()
        or sha256_file(noop_receipt_path) != ANKI_IDENTITY_NOOP_RECEIPT_SHA256
        or not noop_result_path.is_file()
        or sha256_file(noop_result_path) != ANKI_IDENTITY_NOOP_RESULT_SHA256
        or not exe_receipt_path.is_file()
        or sha256_file(exe_receipt_path) != ANKI_EXE_VALIDATION_RECEIPT_SHA256
        or not quality_receipt_path.is_file()
        or sha256_file(quality_receipt_path) != ANKI_QUALITY_REFRESH_RECEIPT_SHA256
        or not consumer_receipt_path.is_file()
        or sha256_file(consumer_receipt_path) != ANKI_CONSUMER_RECEIPT_SHA256
    ):
        raise GateFailure("Anki onboarding evidence is missing or hash-mismatched")
    noop_receipt = load_json(noop_receipt_path)
    noop_result = load_json(noop_result_path)
    exe_receipt = load_json(exe_receipt_path)
    quality_receipt = load_json(quality_receipt_path)
    consumer_receipt = load_json(consumer_receipt_path)
    if (
        noop_receipt.get("status") != "passed"
        or not all(noop_receipt.get("checks", {}).values())
        or noop_receipt.get("worker", {}).get("id") != "export.anki"
        or noop_receipt.get("commonResultSha256", "").upper() != ANKI_IDENTITY_NOOP_RESULT_SHA256
        or noop_result.get("status") != "completed"
        or noop_result.get("worker", {}).get("id") != "export.anki"
    ):
        raise GateFailure("Anki identity no-op evidence violates the onboarding contract")
    final_executable = project_root / EXPECTED_WORKERS["export.anki"]["entrypoint"]
    if (
        exe_receipt.get("status") != "passed"
        or exe_receipt.get("checkpointId") != CHECKPOINT_ID
        or exe_receipt.get("exe", {}).get("path") != EXPECTED_WORKERS["export.anki"]["entrypoint"]
        or exe_receipt.get("exe", {}).get("bytes") != ANKI_EXE_BYTES
        or exe_receipt.get("exe", {}).get("sha256", "").upper() != ANKI_EXE_SHA256
        or exe_receipt.get("describe", {}).get("workerId") != "export.anki"
        or exe_receipt.get("describe", {}).get("schemaValid") is not True
        or exe_receipt.get("validateOnly", {}).get("status") != "completed"
        or exe_receipt.get("validateOnly", {}).get("fakeNotes") != 0
        or exe_receipt.get("fakeExport", {}).get("status") != "completed"
        or exe_receipt.get("fakeExport", {}).get("fakeNotes") != 1
        or exe_receipt.get("derivedValidateOnly", {}).get("status") != "completed"
        or exe_receipt.get("derivedValidateOnly", {}).get("fakeNotes") != 0
        or exe_receipt.get("derivedValidateOnly", {}).get("ancestryRootedInInitialTaskInputs") is not True
        or exe_receipt.get("derivedValidateOnly", {}).get("producerWorkerId") != "orchestrator.core-adapter"
        or exe_receipt.get("learningBundleValidateOnly", {}).get("status") != "completed"
        or exe_receipt.get("learningBundleValidateOnly", {}).get("fakeNotes") != 0
        or exe_receipt.get("nonCanonicalPathAttack", {}).get("status") != "failed"
        or exe_receipt.get("nonCanonicalPathAttack", {}).get("errorCode") != "PATH_NONCANONICAL"
        or exe_receipt.get("nonCanonicalPathAttack", {}).get("fakeAnkiCalls") != 0
        or exe_receipt.get("noteTypeConflict", {}).get("status") != "failed"
        or exe_receipt.get("noteTypeConflict", {}).get("mutatingActions") != 0
        or exe_receipt.get("noteTypeConflict", {}).get("globalRqcIdGuard") is not True
        or exe_receipt.get("endpointAuthorityAttack", {}).get("errorCode") != "CONFIG_INVALID"
        or exe_receipt.get("endpointAuthorityAttack", {}).get("fakeAnkiCalls") != 0
        or exe_receipt.get("malformedAnkiResponse", {}).get("errorCode") != "ANKI_RESPONSE_INVALID"
        or exe_receipt.get("mediaResponseLoss", {}).get("status") != "unknown_external_state"
        or exe_receipt.get("mediaResponseLoss", {}).get("retryable") is not False
        or exe_receipt.get("mediaResponseLoss", {}).get("outputs") != 0
        or exe_receipt.get("mediaExplicitReconcile", {}).get("status") != "completed"
        or exe_receipt.get("mediaExplicitReconcile", {}).get("mediaReadback") != "ALREADY_CURRENT"
    ):
        raise GateFailure("Anki executable validation receipt violates the onboarding contract")
    if (
        quality_receipt.get("status") != "passed-scoped-not-candidate-bound"
        or quality_receipt.get("worker", {}).get("workerVersion") != "1.1.2"
        or quality_receipt.get("worker", {}).get("sha256", "").upper() != ANKI_EXE_SHA256
        or quality_receipt.get("regression", {}).get("passed") != 24
        or quality_receipt.get("regression", {}).get("failed") != 0
        or quality_receipt.get("regression", {}).get("externalAnkiCalls") != 0
        or quality_receipt.get("consumerAcceptance", {}).get("sha256", "").upper()
        != ANKI_CONSUMER_RECEIPT_SHA256
        or quality_receipt.get("stableContracts", {}).get("mismatches") != 0
        or quality_receipt.get("releaseBoundary", {}).get("candidateHash") is not None
    ):
        raise GateFailure("Anki quality refresh receipt violates the quality gate")
    implementation = {item.get("path"): item for item in quality_receipt.get("implementation", [])}
    for relative, expected_hash in ANKI_IMPLEMENTATION_SHA256.items():
        item = implementation.get(relative, {})
        path = project_root / relative
        if (
            not path.is_file()
            or item.get("sha256", "").upper() != expected_hash
            or path.stat().st_size != item.get("bytes")
            or sha256_file(path) != expected_hash
        ):
            raise GateFailure(f"Anki quality implementation mismatch: {relative}")
    if (
        consumer_receipt.get("status") != "passed"
        or consumer_receipt.get("suite") != "export.anki-content-to-anki.v1-consumer-acceptance"
        or consumer_receipt.get("worker", {}).get("workerVersion") != "1.1.2"
        or consumer_receipt.get("worker", {}).get("sha256", "").upper() != ANKI_EXE_SHA256
        or len(consumer_receipt.get("checks", [])) != 3
        or any(item.get("status") != "passed" for item in consumer_receipt.get("checks", []))
        or consumer_receipt.get("evidence", {}).get("success", {}).get("cards") != 5
        or consumer_receipt.get("evidence", {}).get("mediaQaRejection", {}).get("code")
        != "binding.anki_media_deferred"
        or consumer_receipt.get("evidence", {}).get("assetRefRejection", {}).get("code")
        != "binding.anki_media_deferred"
        or consumer_receipt.get("policy", {}).get("externalAnki")
        != "not contacted; request operation=validate and fixture endpoint=127.0.0.1:65534"
    ):
        raise GateFailure("Anki consumer acceptance receipt violates the quality gate")
    for item in consumer_receipt.get("sourceFixtures", []):
        path = project_root / item["path"]
        if not path.is_file() or sha256_file(path) != item.get("sha256", "").upper():
            raise GateFailure(f"Anki consumer fixture mismatch: {path}")
    if (
        not final_executable.is_file()
        or final_executable.stat().st_size != ANKI_EXE_BYTES
        or sha256_file(final_executable) != ANKI_EXE_SHA256
    ):
        raise GateFailure("Anki planned executable is missing or hash-mismatched")
    completed = subprocess.run(
        [str(final_executable), "describe", "--json"], capture_output=True, text=True, timeout=10, check=False
    )
    if completed.returncode != 0:
        raise GateFailure("Anki executable describe failed during quality onboarding gate")
    try:
        description = json.loads(completed.stdout)
    except json.JSONDecodeError as exc:
        raise GateFailure("Anki executable describe did not return one JSON document") from exc
    schemas = load_schemas()
    describe_errors = list(
        schemas.validator(SCHEMAS_ROOT / "common" / "worker-describe.schema.json").iter_errors(description)
    )
    if (
        describe_errors
        or description.get("workerId") != "export.anki"
        or description.get("workerVersion") != "1.1.2"
    ):
        message = describe_errors[0].message if describe_errors else "worker ID mismatch"
        raise GateFailure(f"Anki executable describe contract mismatch: {message}")
    authority_ids = {item["workerId"] for item in load_json(AUTHORITY_REGISTRY)["workers"]}
    return {
        "workerId": "export.anki",
        "workerVersion": "1.1.2",
        "businessCanary": "passed",
        "businessCanarySha256": ANKI_BUSINESS_CANARY_SHA256,
        "historicalBusinessCanaryWorkerId": observed_worker_id,
        "identityNoopReceiptSha256": ANKI_IDENTITY_NOOP_RECEIPT_SHA256,
        "commonResultSha256": ANKI_IDENTITY_NOOP_RESULT_SHA256,
        "executableValidationReceiptSha256": ANKI_EXE_VALIDATION_RECEIPT_SHA256,
        "executableSha256": ANKI_EXE_SHA256,
        "executableBytes": ANKI_EXE_BYTES,
        "derivedInputAncestry": "passed",
        "learningBundleValidateOnly": "passed",
        "nonCanonicalPathRejected": "passed",
        "noteTypeConflictFailClosed": "passed",
        "endpointAuthorityFailClosed": "passed",
        "malformedResponseFailClosed": "passed",
        "unknownExternalState": "passed-nonretryable-no-outputs",
        "mediaExplicitReconciliation": "passed",
        "qualityRefreshReceiptSha256": ANKI_QUALITY_REFRESH_RECEIPT_SHA256,
        "latestTestsPassed": 24,
        "consumerAcceptanceReceiptSha256": ANKI_CONSUMER_RECEIPT_SHA256,
        "consumerAcceptanceCards": 5,
        "externalAnkiCalled": False,
        "moduleOnboarding": "passed",
        "onboardingState": "eligible-but-not-authority-published",
        "authorityPublished": "export.anki" in authority_ids,
        "releaseCandidateEligible": False,
        "reason": "module onboarding evidence is not authority registry publication or candidate-bound Canary evidence",
    }


def validate_verification_receipt(path: Path) -> dict[str, Any]:
    document = load_json(path)
    schemas = load_schemas()
    errors = list(
        schemas.validator(SCHEMAS_ROOT / "quality" / "verification-receipt.schema.json").iter_errors(document)
    )
    if errors:
        raise GateFailure(f"verification receipt schema violation: {path}: {errors[0].message}")
    if document["finishedAt"] < document["startedAt"]:
        raise GateFailure(f"verification receipt time order is invalid: {path}")
    return document


def verify_candidate_receipts(
    project_root: Path,
    manifest_path: Path,
    receipt_paths: Iterable[Path],
    profile: str,
    required_worker_ids: Iterable[str],
) -> dict[str, Any]:
    if not manifest_path.is_file():
        raise GateFailure(f"release candidate manifest missing: {manifest_path}")
    candidate_hash = sha256_file(manifest_path)
    manifest_relative = normalized_relative(manifest_path, project_root)
    receipts = [validate_verification_receipt(path) for path in receipt_paths]
    for receipt in receipts:
        if receipt["status"] != "passed":
            raise GateFailure(f"failed verification receipt cannot release: {receipt['receiptId']}")
        if receipt["profile"] != profile:
            raise GateFailure(f"verification receipt profile mismatch: {receipt['receiptId']}")
        if receipt["candidate"]["manifestPath"] != manifest_relative:
            raise GateFailure(f"verification receipt manifest path mismatch: {receipt['receiptId']}")
        if receipt["candidate"]["candidateHash"].upper() != candidate_hash:
            raise GateFailure(f"verification receipt candidateHash mismatch: {receipt['receiptId']}")
        checkpoint = receipt["contractCheckpoint"]
        if checkpoint["checkpointId"] != CHECKPOINT_ID or checkpoint["receiptSha256"].upper() != CHECKPOINT_RECEIPT_SHA256:
            raise GateFailure(f"verification receipt checkpoint mismatch: {receipt['receiptId']}")
    required_kinds = {"contract-fixtures", "desktop-canary", "end-to-end"}
    actual_kinds = {receipt["verificationKind"] for receipt in receipts}
    missing_kinds = required_kinds - actual_kinds
    if missing_kinds:
        raise GateFailure("candidate receipts missing verification kinds: " + ", ".join(sorted(missing_kinds)))
    worker_subjects = {
        receipt["subject"]["componentId"]
        for receipt in receipts
        if receipt["verificationKind"] == "worker-canary"
    }
    missing_workers = set(required_worker_ids) - worker_subjects
    if missing_workers:
        raise GateFailure("candidate receipts missing required worker canaries: " + ", ".join(sorted(missing_workers)))
    return {
        "candidateHash": candidate_hash,
        "profile": profile,
        "receiptsChecked": len(receipts),
        "requiredWorkerCanaries": sorted(required_worker_ids),
        "status": "passed",
    }


def assess_current_preseal(project_root: Path = PROJECT_ROOT) -> dict[str, Any]:
    root = project_root / CURRENT_PRESEAL_ROOT_RELATIVE
    state_path = project_root / CURRENT_PRESEAL_STATE_RECEIPT_RELATIVE
    black_box_path = project_root / CURRENT_PRESEAL_BLACK_BOX_RECEIPT_RELATIVE
    if (
        not state_path.is_file()
        or sha256_file(state_path) != CURRENT_PRESEAL_STATE_RECEIPT_SHA256
        or not black_box_path.is_file()
        or sha256_file(black_box_path) != CURRENT_PRESEAL_BLACK_BOX_RECEIPT_SHA256
    ):
        raise GateFailure("current preseal state or black-box receipt is missing or hash-mismatched")
    state = load_json(state_path)
    black_box = load_json(black_box_path)
    layout = state.get("layout", {})
    if (
        state.get("status") != "unsealed-no-go"
        or state.get("releaseManifest") is not None
        or state.get("candidateHash") is not None
        or black_box.get("status") != "passed-unsealed-no-go"
        or black_box.get("stateReceipt", {}).get("sha256") != CURRENT_PRESEAL_STATE_RECEIPT_SHA256
        or black_box.get("inventory", {}).get("hiddenWrites") != 0
        or black_box.get("stableContracts", {}).get("filesChecked") != 21
        or black_box.get("stableContracts", {}).get("mismatches") != 0
        or black_box.get("authorityRegistryEntries") != 0
        or black_box.get("registryCandidate", {}).get("authority") is not False
        or black_box.get("registryCandidate", {}).get("describeGate", {}).get("workers") != 5
        or black_box.get("runtimeDependencies", {}).get("configuredPathsExistingInsideReleaseRoot") != 5
        or black_box.get("desktopStartupDiagnostic", {}).get("status") != "passed"
        or black_box.get("desktopStartupDiagnostic", {}).get("packageReceiptParsed") is not True
        or black_box.get("desktopStartupDiagnostic", {}).get("packageFilesVerified") is not True
        or black_box.get("desktopStartupDiagnostic", {}).get("packageTreeVerified") is not True
        or black_box.get("desktopStartupDiagnostic", {}).get("requiredFiles") != 7
        or black_box.get("desktopStartupDiagnostic", {}).get("productionWorkDirectoryCreated") is not False
        or black_box.get("desktopStartupDiagnostic", {}).get("namedPipeInvoked") is not False
        or black_box.get("desktopStartupDiagnostic", {}).get("externalConnections") != 0
        or black_box.get("desktopStartupDiagnostic", {}).get("remNoteInvoked") is not False
        or black_box.get("desktopStartupDiagnostic", {}).get("mineruInvoked") is not False
        or black_box.get("releaseManifest") is not None
        or black_box.get("candidateHash") is not None
    ):
        raise GateFailure("current preseal evidence violates the unsealed NO-GO boundary")
    if not root.is_dir() or (root / "release-manifest.v1.json").exists():
        raise GateFailure("current preseal root is missing or unexpectedly sealed")
    lines: list[str] = []
    total_bytes = 0
    files = sorted((path for path in root.rglob("*") if path.is_file()), key=lambda path: path.relative_to(root).as_posix())
    for path in files:
        relative = path.relative_to(root).as_posix()
        size = path.stat().st_size
        digest = sha256_file(path)
        total_bytes += size
        lines.append(f"{relative}\t{size}\t{digest}\n")
    tree_hash = hashlib.sha256("".join(lines).encode("utf-8")).hexdigest().upper()
    if (
        len(files) != layout.get("files")
        or total_bytes != layout.get("bytes")
        or tree_hash != layout.get("postGateTreeSha256", "").upper()
        or tree_hash != black_box.get("inventory", {}).get("treeSha256", "").upper()
    ):
        raise GateFailure("current preseal tree no longer matches its state and black-box receipts")
    return {
        "releaseVersion": state["releaseVersion"],
        "status": "unsealed-no-go",
        "files": len(files),
        "bytes": total_bytes,
        "treeSha256": tree_hash,
        "stateReceiptSha256": CURRENT_PRESEAL_STATE_RECEIPT_SHA256,
        "blackBoxReceiptSha256": CURRENT_PRESEAL_BLACK_BOX_RECEIPT_SHA256,
        "authorityRegistryEntries": 0,
        "registryCandidateWorkers": 5,
        "registryCandidateAuthority": False,
        "hiddenWrites": 0,
        "desktopStartupDiagnostic": "passed-no-side-effects",
        "desktopPackageReceiptVerified": True,
        "releaseManifest": None,
        "candidateHash": None,
    }


def current_status(project_root: Path = PROJECT_ROOT) -> dict[str, Any]:
    from language_choice_quality_gates import current_status as language_choice_status

    authority = assess_authority_registry(project_root / "contracts/v1/registry/worker-registry.v1.json", project_root)
    image = assess_image_onboarding(project_root)
    workers = {
        "image.ocr.mineru": image,
        "content.deepseek": assess_content_onboarding(project_root),
        "speech.edge": assess_speech_onboarding(project_root),
        "export.remnote": assess_remnote_onboarding(project_root),
        "export.anki": assess_anki_boundary(project_root),
    }
    eligible = sorted(
        worker_id
        for worker_id, result in workers.items()
        if result.get("onboardingState") == "eligible-but-not-authority-published"
        and result.get("authorityPublished") is False
    )
    brand_ready = sorted(
        worker_id
        for worker_id, result in workers.items()
        if str(result.get("brandIdentity", "")).startswith("passed-KnowledgeClipper")
    )
    return {
        "schemaVersion": "1.0.0",
        "checkpoint": verify_stable_checkpoint(project_root),
        "orchestrator": assess_orchestrator_completion(project_root),
        "orchestratorBrandMigration": observe_orchestrator_brand_migration(project_root),
        "authorityRegistry": authority,
        "workerOnboarding": {
            "state": (
                "all-eligible-but-not-authority-published"
                if len(eligible) == 5 and len(brand_ready) == 5
                else "brand-migration-in-progress"
            ),
            "eligibleWorkerIds": eligible,
            "brandReadyWorkerIds": brand_ready,
            "workers": workers,
        },
        "imageOcr": image,
        "desktopContract": assess_desktop_contract(project_root),
        "desktopClickability": assess_desktop_clickability(project_root),
        "currentPreseal": assess_current_preseal(project_root),
        "languageChoiceCardGate": language_choice_status(),
        "anki": assess_anki_boundary(project_root),
        "candidateGate": {
            "status": "pending",
            "decision": "NO-GO",
            "reasons": [
                "KnowledgeClipper-orchestrator-and-authority-descriptor-migrations-incomplete",
                "new-neutral-five-worker-authority-registry-checkpoint-missing",
                "desktop-inline-textbox-windows-canary-and-ui-package-pending",
                "desktop-additive-contract-common-checkpoint-reference-missing",
                "release-manifest-missing",
                "desktop-physical-windows-boundaries-incomplete",
                "candidate-bound-worker-canaries-missing",
                "desktop-to-real-remnote-e2e-receipt-missing",
                "signed-release-packaging-missing",
            ],
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="KnowledgeClipper / 零碎知识剪藏 quality gates")
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("checkpoint")
    subparsers.add_parser("status")
    registry_parser = subparsers.add_parser("registry")
    registry_parser.add_argument("--registry", type=Path, default=AUTHORITY_REGISTRY)
    registry_parser.add_argument("--release-root", type=Path, default=PROJECT_ROOT)
    evidence_parser = subparsers.add_parser("contract-evidence")
    evidence_parser.add_argument("path", type=Path)
    args = parser.parse_args()
    try:
        if args.command == "checkpoint":
            output = verify_stable_checkpoint()
        elif args.command == "status":
            output = current_status()
        elif args.command == "registry":
            output = assess_authority_registry(args.registry, args.release_root)
        else:
            output = assert_canonical_contract_evidence(args.path)
        print(json.dumps(output, ensure_ascii=False, indent=2))
        return 0
    except GateFailure as exc:
        print(json.dumps({"schemaVersion": "1.0.0", "status": "failed", "error": str(exc)}, ensure_ascii=False), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
