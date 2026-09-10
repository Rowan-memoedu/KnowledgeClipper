"""Offline, final-root checks. Never submits worker actions or loads credentials."""
import hashlib
import json
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / 'core-v3/tools'))
from package_destinations_runtime import schema_closure


def read(p):
    return json.loads(p.read_text(encoding='utf-8-sig'))


def sha(p):
    with p.open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest().upper()


def require(value, reason):
    if not value:
        raise ValueError(reason)


def main(root, evidence):
    manifest = read(root / 'candidate-file-manifest.v1.json')
    index = {r['path']: r for r in manifest['files']}
    require(not manifest['safety']['userHandTestEntrypoint'], 'not admitted for hand-test')
    catalog = read(root / 'contracts/core/v3/capabilities/read-to-anki-capability-catalog.v1.json')
    bound = {o['workerBinding']['workerId']: o['workerBinding']['authority'] for o in catalog['operations']
             if o['available'] and o['workerBinding']['mode'] == 'worker'}
    require(set(bound) == {'content.deepseek', 'speech.edge', 'export.anki', 'export.remnote'}, 'worker catalog')
    identities = []
    for worker, binding in bound.items():
        p = root / binding['releaseRoot'] / binding['entrypointPath']
        require(p.resolve().is_relative_to(root) and p.is_file(), 'worker path')
        require(sha(p) == binding['entrypointSha256'], 'worker identity')
        identities.append(dict(worker=worker, version=binding['exactWorkerVersion'], sha256=sha(p)))
    for action, schema in [('remnote.discover-target', 'kc.remnote-discovery/1'), ('remnote.probe', 'kc.remnote-probe/1')]:
        op = next(x for x in catalog['operations'] if x['operationId'] == action)
        require(op['commandSchema'] == schema and op['authorizationPolicy'] == 'none'
                and op['effectClass'] == 'read-only-external', 'public action identity')
    print('CATALOG PASS workers=4 publicActions=2; invoked=0', flush=True)
    dependency = read(root / 'authority/dependency-manifest.v1.json')
    config_path = root / 'authority/win-x64/workers/remnote/appsettings.json'
    config = read(config_path)
    require(set(config) == {'NodePath', 'RemnoteCliJsPath', 'RemnoteMcpUrl'}, 'deployment config fields')
    require(config['RemnoteMcpUrl'] == 'http://127.0.0.1:43101/mcp', 'MCP URL')
    require(sha(config_path) == dependency['moduleInputs']['remnote']['configuration']['sha256'], 'deployment config SHA')
    for key in ['NodePath', 'RemnoteCliJsPath']:
        p = Path(config[key]).resolve()
        require(p.is_relative_to(root) and p.is_file(), 'final-root deployment dependency')
        require(sha(p) == index[p.relative_to(root).as_posix()]['sha256'], 'deployment dependency SHA')
    for name in ['KnowledgeClipper.Core.V3.Client.dll', 'KnowledgeClipper.Core.V3.Contracts.dll']:
        require(sha(root / name) == sha(root / 'sdk' / name), 'root SDK differs from formal SDK04')
    print('CONFIG SDK FINAL-ROOT PASS', flush=True)
    refs = schema_closure(root)
    for ref in refs:
        require(ref['resolvedPath'] in index and ref['sha256'] == index[ref['resolvedPath']]['sha256'], 'schema not manifest-bound')
    print('SCHEMA CLOSURE PASS refs=' + str(len(refs)), flush=True)
    # Scan shipped configuration, not user stores, and fail without emitting values.
    config_files = [p for p in root.rglob('appsettings*.json') if '/node_modules/' not in p.as_posix()]
    sensitive = {'apikey', 'apisecret', 'token', 'password', 'deepseekapikey', 'mineruapitoken', 'accesstoken'}
    def check_config(value):
        if isinstance(value, dict):
            for key, item in value.items():
                require(not (key.replace('_', '').lower() in sensitive and item not in (None, '', False)), 'nonempty secret in shipped config')
                check_config(item)
        elif isinstance(value, list):
            for item in value:
                check_config(item)
    for p in config_files:
        check_config(read(p))
    forbidden = [r for r in index if Path(r).name.lower() in ('credentials.json', 'credentials.json.bak', '.env', 'credentials.protected.json')]
    require(not forbidden, 'credential store included')
    result = dict(status='PASS-OFFLINE-FINAL-ROOT', candidate=str(root), manifestSha256=sha(root/'candidate-file-manifest.v1.json'),
                  catalogWorkers=identities, publicActionsInvoked=0, schemaReferences=len(refs), schemaSourceFallback=False,
                  configFilesScanned=len(config_files), nonemptySecretFields=0, credentialStoreFiles=0,
                  actualUserSecretsRead=False, liveProviderRuns=0, speaker=0,
                  remnoteFixedRoot=True, arbitraryRelocationSupported=False, userHandTestEntrypoint=False)
    with (evidence/'package-gates.json').open('x', encoding='utf-8') as f:
        json.dump(result, f, indent=2)
        f.write('\n')
    print('PACKAGE GATES PASS', flush=True)


if __name__ == '__main__':
    main(Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve())
