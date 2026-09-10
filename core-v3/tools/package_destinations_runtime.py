"""Assemble the accepted Core/SDK with existing module-overlay packaging; offline only."""
from __future__ import annotations

import copy
import hashlib
import json
import shutil
import subprocess
from datetime import datetime, timezone
from pathlib import Path
import sys

REPO = Path(r'D:\Projects\RemNote Quick Clipper')
BASE = Path(r'D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-whole-window-zoom-20260909-01')
OUT = Path(r'D:\Artifacts\KnowledgeClipper\remnote-destinations\formal-runtime-20260909-01')
ROOT = OUT / 'overlay'
SDK = OUT.parent / 'sdk-integration-20260909-04'
QUAL = OUT.parent / 'qualification-20260909-02'
REM = Path(r'D:\Artifacts\KnowledgeClipper\remnote-exporter\export.remnote-2.3.0-source-bound-20260908T185328Z')
CLI = Path(r'C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote MCP Integration\node_modules')
NODE = Path(r'D:\Artifacts\KnowledgeClipper\content-worker\releases\content.deepseek-0.12.0-win-x64-formal-20260908T054141Z\runtime\node-v24.18.0-win-x64\node.exe')

def sha(p):
    with Path(p).open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest().upper()

def read(p):
    return json.loads(Path(p).read_text(encoding='utf-8-sig'))

def write(p, value):
    p = Path(p)
    p.parent.mkdir(parents=True, exist_ok=True)
    with p.open('x', encoding='utf-8', newline='\n') as f:
        json.dump(value, f, indent=2, ensure_ascii=False)
        f.write('\n')

def check(ok, message):
    if not ok:
        raise ValueError(message)

def row(p, relative=None):
    p = Path(p)
    return dict(path=relative or str(p), bytes=p.stat().st_size, sha256=sha(p))

def inventory(root):
    return [row(p, p.relative_to(root).as_posix()) for p in sorted(root.rglob('*')) if p.is_file()]

def tree(rows):
    text = ''.join(f'{r["path"]}\t{r["bytes"]}\t{r["sha256"]}\n' for r in sorted(rows, key=lambda r:r['path']))
    return dict(files=len(rows), bytes=sum(r['bytes'] for r in rows), treeSha256=hashlib.sha256(text.encode()).hexdigest().upper())

def verify(root, record):
    p = root / record['path']
    check(p.resolve().is_relative_to(root.resolve()), 'unsafe path')
    check(p.stat().st_size == record['bytes'] and sha(p) == record['sha256'].upper(), 'hash drift: ' + str(p))

def copy_file(src, dest):
    check(not dest.exists(), 'output already exists: ' + str(dest))
    check(src.is_file() and not src.is_symlink(), 'nonregular source: ' + str(src))
    dest.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(src, dest)
    check(sha(src) == sha(dest), 'copy drift')

def schema_closure(root):
    sys.path.insert(0, str(REPO / 'tools'))
    from validate_release_schema_closure import iter_refs, resolve_pointer
    refs = []
    paths = [p for p in sorted(root.rglob('*.schema.json')) if '/node_modules/' not in p.as_posix() and '/edge-tts/' not in p.as_posix()]
    documents = {p: read(p) for p in paths}
    ids = {}
    for p, doc in documents.items():
        if '$id' in doc:
            ids.setdefault(doc['$id'], []).append(p)
    for p in paths:
        # Only schema-owned contracts/app payload; dependencies such as node_modules have their own schema dialects.
        rel = p.relative_to(root).as_posix()
        if '/node_modules/' in rel or '/edge-tts/' in rel:
            continue
        doc = documents[p]
        for ref in iter_refs(doc):
            if ref.startswith(('http:', 'https:')):
                continue
            part, _, fragment = ref.partition('#')
            if '://' in part:
                candidates = ids.get(part, [])
                check(bool(candidates), 'named schema missing: ' + part)
                check(len({sha(x) for x in candidates}) == 1, 'ambiguous named schema: ' + part)
                target = candidates[0]
            else:
                target = (p.parent / part).resolve() if part else p
            check(target.is_relative_to(root), 'schema ref escapes: ' + str(p))
            check(target.is_file(), 'missing schema ref: ' + str(target))
            resolve_pointer(read(target), fragment)
            refs.append(dict(schema=rel, reference=ref, resolvedPath=target.relative_to(root).as_posix(), sha256=sha(target)))
    return refs

def main():
    check((ROOT / 'core-v3/KnowledgeClipper.Core.V3.Host.exe').is_file(), 'publish Host first')
    check(not (OUT / 'assembly-inputs.json').exists(), 'append-only assembly already started')
    baseline_manifest = BASE / 'candidate-file-manifest.v1.json'
    check(sha(baseline_manifest) == '6BAC404621750CC9D7049736BED23274AAB1DB88012EE7B5AC898107C0DE0561', 'baseline identity')
    check(sha(QUAL / 'source-binding.json') == '19FC026E4A0C097801F9EB31FF9E1A6F266F1CDBC80F9887A2CD84CC4A3F59C8', 'qualification source identity')
    check(sha(REM / 'candidate-manifest.json') == 'EEC36A16527A6C734DBE375860769A458348E3796F8A432994356C89B70032D9', 'RemNote identity')
    check(sha(NODE) == '9A4EB5F1C29C6A2E93852EAD46B999E284A6A5CA8BAB4D4E241D587D025A52DE', 'Node identity')
    production = [r for r in read(QUAL / 'source-binding.json')['files'] if '\\core-v3\\src\\' in r['path'] or '\\contracts\\' in r['path']]
    for r in production:
        check(sha(r['path']) == r['sha256'], 'production source drift: ' + r['path'])
    for r in read(REM / 'candidate-manifest.json')['files']:
        verify(REM, r)
    check(sha(ROOT / 'core-v3/KnowledgeClipper.Core.V3.Contracts.dll') == sha(SDK / 'KnowledgeClipper.Core.V3.Contracts.dll') == '69894606F2F3A5C92F100EBFF6FED5A388F9DB1F222E1A954FBA80873155FFA0', 'SDK/runtime Contracts mismatch')
    check(sha(SDK / 'KnowledgeClipper.Core.V3.Client.dll') == '271A32528D779C5C052FB98C74FBE1FEC5CEAB2229086BFB90F8BEA811936591', 'SDK04 identity')
    write(OUT / 'assembly-inputs.json', dict(baseline=row(baseline_manifest), qualification=row(QUAL/'receipt.json'), sourceBinding=row(QUAL/'source-binding.json'), production=production, remnote=row(REM/'candidate-manifest.json'), sdk=inventory(SDK), generator=row(__file__)))
    print('PHASE inherit sealed dependency inputs', flush=True)
    excluded = {'authority/dependency-manifest.v1.json', 'contracts/core/v3/capabilities/read-to-anki-capability-catalog.v1.json', 'contracts/core/v3/capabilities/read-to-anki-capability-binding.v1.json'}
    replacement = {Path(r['path']).relative_to(REPO).as_posix(): Path(r['path']) for r in production if '\\contracts\\' in r['path']}
    inherited = []
    for r in read(baseline_manifest)['files']:
        rel = r['path']
        if not rel.startswith(('authority/', 'contracts/')) or rel in excluded or rel in replacement or rel.startswith('authority/win-x64/workers/remnote/'):
            continue
        verify(BASE, r)
        copy_file(BASE/rel, ROOT/rel)
        inherited.append(r)
    for rel, src in replacement.items():
        copy_file(src, ROOT/rel)
    # Resolve each newly introduced public schema dependency from the identical source path.
    sys.path.insert(0, str(REPO / 'tools'))
    from validate_release_schema_closure import iter_refs
    pending = [ROOT/rel for rel in replacement if rel.endswith('.schema.json')]
    seen = set()
    while pending:
        p = pending.pop()
        if p in seen:
            continue
        seen.add(p)
        for ref in iter_refs(read(p)):
            if ref.startswith(('http:', 'https:', '#')):
                continue
            target = (p.parent / ref.split('#')[0]).resolve()
            check(target.is_relative_to(ROOT), 'schema dependency escape')
            if not target.exists():
                copy_file(REPO/target.relative_to(ROOT), target)
            pending.append(target)
    for src in SDK.iterdir():
        if src.is_file():
            copy_file(src, ROOT/'sdk'/src.name)
    rem_dest = ROOT/'authority/win-x64/workers/remnote'
    for r in read(REM/'candidate-manifest.json')['files']:
        rel = r['path']
        if rel == 'win-x64/KnowledgeClipper.RemNoteExporter.Worker.exe' or rel.startswith('win-x64/contracts/'):
            copy_file(REM/rel, rem_dest/Path(rel).relative_to('win-x64'))
    print('PHASE copy Node and full CLI dependency tree; no user configuration', flush=True)
    node_dest = rem_dest/'runtime/node.exe'
    copy_file(NODE, node_dest)
    cli_target = rem_dest/'runtime/remnote-cli/node_modules'
    cli_rows = inventory(CLI)
    for r in cli_rows:
        check(Path(r['path']).name.lower() not in {'.env', '.npmrc', 'credentials.json', 'cookies.json'}, 'private config cannot be packaged')
        copy_file(CLI/r['path'], cli_target/r['path'])
    cli_entry = cli_target/'remnote-mcp-server/dist/remnote-cli/index.js'
    check(sha(cli_entry) == '0C67E1CBADB83F80B0F947DC74BBAE47DEF8AAD87B7C98BBA444C081D61EF571', 'CLI entry drift')
    check(read(cli_target/'remnote-mcp-server/package.json')['version'] == '0.17.0', 'CLI version')
    config = dict(NodePath=str(node_dest), RemnoteCliJsPath=str(cli_entry), RemnoteMcpUrl='http://127.0.0.1:43101/mcp')
    write(rem_dest/'appsettings.json', config)
    # Smoke imports and command registration only: --help never sends an MCP request.
    run = subprocess.run([str(node_dest), str(cli_entry), '--help'], cwd=cli_entry.parent, capture_output=True, text=True, timeout=30, creationflags=subprocess.CREATE_NO_WINDOW)
    write(OUT/'cli-help.json', dict(exitCode=run.returncode, stdout=run.stdout, stderr=run.stderr, node=row(node_dest), cli=row(cli_entry), serviceCalls=0))
    check(run.returncode == 0 and 'Usage:' in run.stdout, 'packaged CLI help failed')
    print('PHASE bind public catalog and module entrypoint', flush=True)
    catalog_rel = 'contracts/core/v3/capabilities/read-to-anki-capability-catalog.v1.json'
    binding_rel = 'contracts/core/v3/capabilities/read-to-anki-capability-binding.v1.json'
    catalog = read(BASE/catalog_rel)
    remexe = rem_dest/'KnowledgeClipper.RemNoteExporter.Worker.exe'
    for operation, schema, kind in [('remnote.discover-target', 'kc.remnote-discovery/1', 'remnote-discovery'), ('remnote.probe', 'kc.remnote-probe/1', 'remnote-probe')]:
        op = copy.deepcopy(next(o for o in catalog['operations'] if o['operationId']=='anki.probe'))
        op.update(operationId=operation, commandSchema=schema, resultArtifactKind=kind, timeoutMilliseconds=90000)
        op['uiAffordance'].update(label=operation, visible=True)
        op['workerBinding'].update(workerId='export.remnote', versionRange='=2.3.0')
        op['workerBinding']['authority'].update(entrypointPath='workers/remnote/KnowledgeClipper.RemNoteExporter.Worker.exe', entrypointSha256=sha(remexe), exactWorkerVersion='2.3.0')
        catalog['operations'].append(op)
    # Existing legacy command schemas remain admitted; new neutral body is a handler-owned additive branch.
    write(ROOT/catalog_rel, catalog)
    binding = read(BASE/binding_rel)
    binding['catalogSha256'] = sha(ROOT/catalog_rel)
    write(ROOT/binding_rel, binding)
    finish_metadata(inherited, cli_rows)

def finish_metadata(inherited, cli_rows):
    print('PHASE recursive schema closure and dependency manifest', flush=True)
    baseline_manifest = BASE/'candidate-file-manifest.v1.json'
    production = read(OUT/'assembly-inputs.json')['production']
    rem_dest = ROOT/'authority/win-x64/workers/remnote'
    remexe = rem_dest/'KnowledgeClipper.RemNoteExporter.Worker.exe'
    node_dest = rem_dest/'runtime/node.exe'
    refs = schema_closure(ROOT)
    frozen_rel = 'authority/win-x64/contracts/v1/schemas/common/artifact.schema.json'
    check((ROOT/frozen_rel).exists(), 'frozen common schema must be packaged')
    dep = read(BASE/'authority/dependency-manifest.v1.json')
    dep.update(generatedAt=datetime.now(timezone.utc).isoformat(), references=refs)
    dep['moduleInputs']['remnote'] = dict(worker='export.remnote@2.3.0', copiedPath='authority/win-x64/workers/remnote', entrypointSha256=sha(remexe), sourceManifest=row(REM/'candidate-manifest.json'), runtimeCli=tree(cli_rows), runtimeNode=row(node_dest, node_dest.relative_to(ROOT).as_posix()), configuration=dict(path='authority/win-x64/workers/remnote/appsettings.json', sha256=sha(rem_dest/'appsettings.json'), owner='final-package-deployment', sourceConfig=row(REM/'win-x64/appsettings.json'), changedFields=['NodePath','RemnoteCliJsPath'], requiresRebindAtNewFinalRoot=True, arbitraryRelocationSupported=False, standardRunDefaultAndPublicActionsShareAppsettings=True, workerRequestDeploymentOverrideKeys=['nodePath','remnoteCliJsPath','remnoteMcpUrl']))
    dep['core'] = dict(host=row(ROOT/'core-v3/KnowledgeClipper.Core.V3.Host.exe','core-v3/KnowledgeClipper.Core.V3.Host.exe'), kernel=row(ROOT/'core-v3/KnowledgeClipper.Core.V3.Kernel.dll','core-v3/KnowledgeClipper.Core.V3.Kernel.dll'), sdk=inventory(ROOT/'sdk'), sourceBinding=row(QUAL/'source-binding.json'), productionFiles=production, testHostIncluded=False)
    write(ROOT/'authority/dependency-manifest.v1.json', dep)
    write(OUT/'inherited-files.json', inherited)
    write(OUT/'cli-dependency-files.json', dict(sourceRoot=str(CLI), files=cli_rows, payload=tree(cli_rows), sourceOnlyNoRuntimeDependency=True))
    rows = inventory(ROOT)
    check(not any('Phase2Tests' in r['path'] or 'DestinationFakeServer' in r['path'] for r in rows), 'test host leaked into payload')
    write(OUT/'module-overlay-admission.json', dict(schema='kc.module-overlay-admission/1', overlayId=OUT.name, status='prepared-offline-startup-pending', baseline=dict(root=str(BASE), manifestSha256=sha(baseline_manifest), historicalCheckpointUnchanged=True, registryVersion='1.2.44'), metadataRoot=str(ROOT), moduleCopies=[dict(source=str(ROOT/r['path']),destination=r['path'],bytes=r['bytes'],sha256=r['sha256']) for r in rows], metadataCopies=[], sourceBinding=row(OUT/'assembly-inputs.json'), payload=tree(rows), assemblyRequirements=['Use a new Desktop candidate; never overwrite the baseline.', 'Replace core-v3, authority and sdk from this complete overlay; overlay contracts at the same relative paths.', 'Before sealing the new Desktop root, regenerate ONLY NodePath and RemnoteCliJsPath in workers/remnote/appsettings.json as absolute paths within that final root. Public discovery/probe and standard run must use those same paths. Do not use an old task configSnapshot deployment override.', 'Recompute dependency configuration SHA, authority tree, candidate-integration and final file manifest; do not claim unmodified appsettings.', 'Historical registry/checkpoint 1.2.44 remains provenance, not a promotion of this new module overlay.', 'No network/service/worker run/export/speaker qualification authorized in this package step.'], evidenceBoundary=dict(workerRuns=0, publicWorkerActions=0, externalCalls=0, destinationWrites=0, speaker=0, fullMatrixRerun=False, cliHelpInvocations=1, productionTestHostIncluded=False)))
    print('STATUS ASSEMBLED ' + str(OUT), flush=True)

if __name__ == '__main__':
    if sys.argv[1:] == ['--finish-metadata']:
        # Resume only the unsealed metadata phase; all inherited/module files are reverified.
        check(not (OUT/'module-overlay-admission.json').exists(), 'already sealed assembly')
        check(read(OUT/'cli-help.json')['exitCode'] == 0, 'CLI startup missing')
        inherited = []
        source_contracts = {Path(r['path']).relative_to(REPO).as_posix() for r in read(OUT/'assembly-inputs.json')['production'] if '\\contracts\\' in r['path']}
        excluded = {'authority/dependency-manifest.v1.json', 'contracts/core/v3/capabilities/read-to-anki-capability-catalog.v1.json', 'contracts/core/v3/capabilities/read-to-anki-capability-binding.v1.json'}
        for r in read(BASE/'candidate-file-manifest.v1.json')['files']:
            if r['path'].startswith(('authority/','contracts/')) and r['path'] not in excluded|source_contracts and not r['path'].startswith('authority/win-x64/workers/remnote/'):
                verify(BASE,r)
                verify(ROOT,r)
                inherited.append(r)
        cli_rows = inventory(CLI)
        for r in cli_rows:
            verify(ROOT/'authority/win-x64/workers/remnote/runtime/remnote-cli/node_modules',r)
        write(OUT/'assembly-validator-correction.json', dict(priorPhase='recursive schema closure',priorError='knowledgeclipper named $ref interpreted as file path',fix='resolve exact package-local $id; reject missing or divergent duplicates; preserve all schema bytes',generator=row(__file__),productionChanged=False,externalCalls=0))
        finish_metadata(inherited, cli_rows)
    else:
        main()
