import hashlib, json, pathlib, sys

desktop=pathlib.Path(r'D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-rta-20260904T104202Z')
core=pathlib.Path(r'D:\Artifacts\KnowledgeClipper\read-to-anki\rta-observation-20260904T122256Z-33da404d')
staging=pathlib.Path(r'D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-rta-observation-20260904T124500Z')
output=pathlib.Path(r'D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\quality\desktop-rta-observation-20260904T124500Z.merge-verification.independent.json')
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def tree(root): return {p.relative_to(root).as_posix():{'bytes':p.stat().st_size,'sha256':sha(p)} for p in root.rglob('*') if p.is_file()}
expected=tree(desktop)
for key in list(expected):
    if key in {'candidate-file-manifest.v1.json','core-read-to-anki-candidate-manifest.json','core-read-to-anki-sdk-freeze-manifest.json','candidate-integration.json','KnowledgeClipper.Core.V3.Client.dll','KnowledgeClipper.Core.V3.Contracts.dll'} or key.startswith(('authority/','core-v3/')): del expected[key]
for folder in ('authority','core-v3','contracts'):
    for key,value in tree(core/folder).items(): expected[f'{folder}/{key}']=value
for source,target in [('sdk/KnowledgeClipper.Core.V3.Client.dll','KnowledgeClipper.Core.V3.Client.dll'),('sdk/KnowledgeClipper.Core.V3.Contracts.dll','KnowledgeClipper.Core.V3.Contracts.dll'),('candidate-manifest.json','core-observation-candidate-manifest.json'),('dependency-manifest.v2.json','core-observation-dependency-manifest.v2.json'),('sdk/sdk-binding.v1.json','core-observation-sdk-binding.v1.json')]:
    p=core/source;expected[target]={'bytes':p.stat().st_size,'sha256':sha(p)}
actual=tree(staging);actual.pop('candidate-integration.json',None)
missing=sorted(set(expected)-set(actual));extra=sorted(set(actual)-set(expected));mismatch=sorted(k for k in expected.keys()&actual.keys() if expected[k]!=actual[k])
integration=json.loads((staging/'candidate-integration.json').read_text('utf-8'))
integration_ok=integration['OrchestratorExecutable']=='core-v3/KnowledgeClipper.Core.V3.Host.exe' and integration['AuthorityCheckpointId']=='rta-observation-20260904T122256Z-33da404d' and integration['AuthorityTreeSha256']=='B9F4C0A59489A2E958919BB1BB7A903BE3D99F1841C840A277B3AC16B9573CE6'
result={'schemaVersion':'knowledge-clipper-desktop-staging-merge-verification.independent.v1','status':'PASS' if not missing and not extra and not mismatch and integration_ok else 'FAIL','files':len(actual)+1,'missing':missing,'extra':extra,'hashMismatch':mismatch,'integrationValid':integration_ok,'candidateManifestPresent':(staging/'candidate-file-manifest.v1.json').exists(),'actualExecuted':False,'externalCalls':0}
if output.exists(): raise SystemExit('append-only-output-exists')
output.parent.mkdir(parents=True,exist_ok=True);output.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n','utf-8');print(json.dumps(result,ensure_ascii=False));raise SystemExit(0 if result['status']=='PASS' else 20)
