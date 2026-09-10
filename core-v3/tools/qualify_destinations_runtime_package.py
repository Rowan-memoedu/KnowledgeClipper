"""Final file/dependency admission for the source-bound destinations runtime package."""
import json
import subprocess
from pathlib import Path
from package_destinations_runtime import ROOT, OUT, REPO, BASE, QUAL, REM, SDK, read, write, sha, row, tree, inventory, verify, check, schema_closure

def main():
    admission = read(OUT/'module-overlay-admission.json')
    for item in admission['moduleCopies']:
        verify(ROOT, dict(path=item['destination'], bytes=item['bytes'], sha256=item['sha256']))
    check(tree(inventory(ROOT)) == admission['payload'], 'full overlay inventory drift')
    read_only = read(OUT/'startup/receipt.json')
    check(read_only['status']=='PASS' and read_only['businessCommandsSubmitted']==0 and read_only['workerRunInvocations']==0, 'startup boundary')
    write(OUT/'startup/probe-runner-boundary.json', dict(initialRunner=str(OUT.parent/'tests-build-20260909-21/KnowledgeClipper.Core.V3.Phase2Tests.dll'), initialFailure='DirectoryNotFoundException: Could not locate the RemNote Quick Clipper repository root.', initialFailureBoundary='test runner FindRepositoryRoot before production Host invocation; no service or worker started', successfulRunner=row(REPO/'core-v3/tests/KnowledgeClipper.Core.V3.Phase2Tests/bin/Release/net8.0/KnowledgeClipper.Core.V3.Phase2Tests.dll'), mode='--candidate-observation-readonly', includedInProductionPayload=False))
    catalog = read(ROOT/'contracts/core/v3/capabilities/read-to-anki-capability-catalog.v1.json')
    bound = {o['workerBinding']['workerId']:o['workerBinding']['authority'] for o in catalog['operations'] if o['available'] and o['workerBinding']['mode']=='worker'}
    check(set(bound)=={'content.deepseek','speech.edge','export.anki','export.remnote'}, 'production module bindings')
    descriptions = []
    for worker, b in bound.items():
        exe = ROOT / b['releaseRoot'] / b['entrypointPath']
        check(sha(exe)==b['entrypointSha256'], 'admission entrypoint mismatch')
        run = subprocess.run([str(exe),'describe','--json'], cwd=OUT, capture_output=True, text=True, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
        check(run.returncode==0, 'describe failure: '+worker)
        d=json.loads(run.stdout)
        check(d['workerId']==worker and d['workerVersion']==b['exactWorkerVersion'] and d['contractVersion']==b['contractVersion'], 'describe identity')
        p=OUT/'describe'/(worker+'.json')
        write(p,d)
        descriptions.append(dict(worker=worker,version=d['workerVersion'],entrypoint=row(exe,exe.relative_to(ROOT).as_posix()),describe=row(p)))
    refs=schema_closure(ROOT)
    frozen=ROOT/'authority/win-x64/contracts/v1/schemas/common/artifact.schema.json'
    original=REPO/'contracts/v1/schemas/common/artifact.schema.json'
    check(sha(frozen)==sha(original), 'frozen common schema altered')
    # No production request is submitted: verify the already accepted public body schemas locally.
    import jsonschema
    shapes=[]
    for name in ('command','discovery','probe','snapshot','result'):
        p=ROOT/f'contracts/core/v3/operations/destinations/{name}.v1.schema.json'
        jsonschema.Draft202012Validator.check_schema(read(p))
        shapes.append(row(p,p.relative_to(ROOT).as_posix()))
    for id, body in [('remnote.discover-target','kc.remnote-discovery/1'),('remnote.probe','kc.remnote-probe/1')]:
        op=next(x for x in catalog['operations'] if x['operationId']==id)
        check(op['commandSchema']==body and op['authorizationPolicy']=='none' and op['effectClass']=='read-only-external', 'public action descriptor mismatch')
    config=read(ROOT/'authority/win-x64/workers/remnote/appsettings.json')
    check(config['RemnoteMcpUrl']=='http://127.0.0.1:43101/mcp', 'loopback MCP binding')
    config_files=[]
    for key in ('NodePath','RemnoteCliJsPath'):
        p=Path(config[key])
        check(p.is_absolute() and p.is_relative_to(ROOT) and p.is_file(), 'config dependency outside final root')
        config_files.append(row(p,p.relative_to(ROOT).as_posix()))
    check(sha(ROOT/'core-v3/KnowledgeClipper.Core.V3.Kernel.dll')=='E16A4E48AF48BF61C723C9DA8F7F25F8EFC582ECBDBFB12CF36A421EA9FF264F', 'production kernel differs from qualified source build')
    for r in read(OUT/'assembly-inputs.json')['production']:
        check(sha(r['path'])==r['sha256'], 'source changed during packaging')
    baseline_manifest=BASE/'candidate-file-manifest.v1.json'
    check(sha(baseline_manifest)==admission['baseline']['manifestSha256'], 'baseline manifest changed')
    for r in read(OUT/'inherited-files.json'):
        verify(BASE,r)
        verify(ROOT,r)
    current=inventory(ROOT)
    payload=tree(current)
    manifest=dict(schema='kc.read-to-anki-candidate-manifest/4',candidateId=OUT.name,runtime=dict(host='core-v3/KnowledgeClipper.Core.V3.Host.exe',selfContained=True),authority=dict(registryVersion='1.2.44',historicalCheckpointUnchanged=True,moduleOverlay=True),dependencyManifest='authority/dependency-manifest.v1.json',portableRecursiveSchemaValidation='passed',payload=payload,files=current)
    write(ROOT/'candidate-manifest.json',manifest)
    receipt=dict(schema='kc.read-to-anki-observation-matched-candidate-qualification/1',status='PASS-OFFLINE-PACKAGE',root=str(ROOT),manifest=row(ROOT/'candidate-manifest.json'),payload=payload,authorityTree=tree(inventory(ROOT/'authority/win-x64')),registry=row(ROOT/'authority/worker-registry.v1.json'),dependencyManifest=row(ROOT/'authority/dependency-manifest.v1.json'),overlayAdmission=row(OUT/'module-overlay-admission.json'),sourceBinding=row(OUT/'assembly-inputs.json'),qualification=row(QUAL/'receipt.json'),startup=row(OUT/'startup/receipt.json'),cliHelp=row(OUT/'cli-help.json'),describes=descriptions,publicCommandSchemas=shapes,recursiveSchemaReferences=len(refs),frozenCommonSchema=row(frozen,frozen.relative_to(ROOT).as_posix()),deploymentConfig=dict(files=config_files,absolutePackagePaths=True,arbitraryRelocationSupported=False,rebindAtFinalDesktopRootRequired=True,standardRunUsesSameAppsettingsWhenConfigSnapshotEmpty=True,externalOverridePermittedOnlyForSameFinalRoot=True),boundaries=dict(workerDescribeInvocations=8,businessWorkerRuns=0,publicWorkerActions=0,cliHelpInvocations=1,externalCalls=0,serviceCalls=0,externalWrites=0,physicalSpeaker=0,fullACRerun=False,liveD=False,testHostInProductionPayload=False,desktopIntegrationPending=True))
    receipt['packagingTools']=[row(Path(__file__)),row(REPO/'core-v3/tools/package_destinations_runtime.py')]
    receipt['supplementaryEvidence']=[row(OUT/'assembly-validator-correction.json'),row(OUT/'startup/probe-runner-boundary.json')]
    write(OUT/'qualification-receipt.json',receipt)
    print('STATUS PASS-OFFLINE-PACKAGE')
    print('ROOT '+str(ROOT))
    print('MANIFEST '+sha(ROOT/'candidate-manifest.json'))
    print('RECEIPT '+sha(OUT/'qualification-receipt.json'))

if __name__=='__main__':
    main()
