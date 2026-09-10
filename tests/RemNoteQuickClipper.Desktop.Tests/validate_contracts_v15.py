import hashlib
import json
import pathlib
import sys

import jsonschema

ROOT = pathlib.Path(__file__).resolve().parents[2]
CURRENT = ROOT / "contracts" / "desktop-automation" / "v1.5"
PREVIOUS = ROOT / "contracts" / "desktop-automation" / "v1.4"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    prior = json.loads((PREVIOUS / "contract-receipt.json").read_text(encoding="utf-8"))
    contract = json.loads((CURRENT / "desktop-automation.contract.json").read_text(encoding="utf-8"))
    assert contract["contractVersion"] == "1.5.0"
    assert contract["extends"]["receiptSha256"] == sha(PREVIOUS / "contract-receipt.json")
    assert contract["extends"]["treeSha256"] == prior["treeSha256"]
    expected = {
        "settings.export.anki.deck-routes.edit": ("knowledge-clipper.desktop.settings.export.anki.deck-routes", "Edit", "ValuePattern.SetValue"),
        "settings.save": ("knowledge-clipper.desktop.settings.save", "Button", "InvokePattern.Invoke"),
        "flashcard-preview.confirm": ("knowledge-clipper.desktop.flashcard-preview.confirm", "Button", "InvokePattern.Invoke"),
    }
    actual = {item["semanticAction"]: (item["automationId"], item["controlType"], item["interaction"]) for item in contract["automationIds"]}
    assert actual == expected and contract["semanticActions"] == list(expected)

    source = (ROOT / "src" / "RemNoteQuickClipper.Desktop.Core" / "SettingsAutomationCatalog.cs").read_text(encoding="utf-8")
    preview = (ROOT / "src" / "FlashcardPreviewUi.cs").read_text(encoding="utf-8")
    settings_ui = (ROOT / "src" / "ProjectSettingsForm.cs").read_text(encoding="utf-8")
    for action, (automation_id, _, _) in expected.items():
        assert automation_id in source or automation_id in preview
    assert "SettingsAutomationCatalog.ExportAnkiDeckRoutes" in settings_ui
    assert "SettingsAutomationCatalog.Save" in settings_ui
    assert "FlashcardPreviewAutomation.Confirm" in preview

    validator = jsonschema.Draft202012Validator(json.loads((CURRENT / "semantic-action.schema.json").read_text(encoding="utf-8")))
    valid = list((CURRENT / "fixtures" / "semantic-actions" / "valid").glob("*.json"))
    invalid = list((CURRENT / "fixtures" / "semantic-actions" / "invalid").glob("*.json"))
    assert len(valid) == 3 and len(invalid) == 2
    for path in valid:
        validator.validate(json.loads(path.read_text(encoding="utf-8")))
    for path in invalid:
        try:
            validator.validate(json.loads(path.read_text(encoding="utf-8")))
        except jsonschema.ValidationError:
            continue
        raise AssertionError(f"invalid fixture accepted: {path}")

    receipt = json.loads((CURRENT / "contract-receipt.json").read_text(encoding="utf-8"))
    paths = sorted((p for p in CURRENT.rglob("*") if p.is_file() and p.name != "contract-receipt.json"), key=lambda p: p.relative_to(CURRENT).as_posix().encode())
    assert [item["path"] for item in receipt["files"]] == [p.relative_to(CURRENT).as_posix() for p in paths]
    lines = []
    for item, path in zip(receipt["files"], paths):
        digest = sha(path)
        assert item["bytes"] == path.stat().st_size and item["sha256"] == digest
        lines.append(f'{item["path"]}|{digest}\n')
    assert receipt["treeSha256"] == hashlib.sha256("".join(lines).encode()).hexdigest()
    print("PASS desktop automation v1.5 semanticActions=3 automationIds=3 valid=3 invalid=2 v1.4Immutable=True sourceParity=True")
    return 0


if __name__ == "__main__":
    sys.exit(main())
