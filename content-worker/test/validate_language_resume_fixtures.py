"""Validate the generated recognition/resume fixture packages against module schemas."""
import json
import sys
from pathlib import Path
from jsonschema import Draft202012Validator, RefResolver

schemas = Path(__file__).resolve().parents[1] / 'schemas'
store = {}
for p in schemas.glob('*.schema.json'):
    doc = json.loads(p.read_text(encoding='utf-8-sig'))
    identifier = doc.pop('$id', None)  # Module IDs are labels, resolve file references only locally.
    store[p.name] = doc
    store[p.as_uri()] = doc
    if identifier:
        store[identifier] = doc
        store['standard-input/' + p.name] = doc
        store['language-practice-resume/' + p.name] = doc

root = Path(sys.argv[1]).resolve()
count = 0
for p in root.rglob('*.json'):
    doc = json.loads(p.read_text(encoding='utf-8-sig'))
    if not isinstance(doc, dict):
        continue
    version = doc.get('schema_version', '')
    if version not in ('standard-input/6.0.0', 'standard-input/8.0.0', 'learning-bundle/1.0.0', 'card-bundle/1.0.0', 'language-practice-decision/2.0.0'):
        continue
    schema = store[version]
    resolver = RefResolver(base_uri=schemas.as_uri()+'/', referrer=schema, store=store)
    Draft202012Validator(schema, resolver=resolver).validate(doc)
    count += 1
assert count > 0
print(json.dumps({'status': 'PASS', 'validated': count, 'root': str(root)}))
