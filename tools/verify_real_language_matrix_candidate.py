from __future__ import annotations

import argparse, hashlib, json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

EXPECTED={"manifest":"A3B2337479F3E893CF0DE62025589FB28D74D599CFE01BE01C4D123A762700FC","fake":"15A3D8AE9213325885B7A6D6238AF408B4022E6A399636786613ABD0443489F2","real":"3A026B50D4B15E1BEA28234C9817A8E31FE83649DCAB17353386F37250E19BE3","sdk":"7341C52DC7E3A8A55F402EF3D6D9FABE2577480D5619684036E5BAAAF7AF2D6D","dependency":"7FECAE910A64D5D58BA879B35B817872F9A1C628FB3A9DC4FE43AF6EF6EB8B8B","client":"84527AAEFB48D9DE3882482B772BC64CB194F7237BDC5C1D447049D659382E67"}
class GateFailure(RuntimeError): pass
def require(v:bool,m:str)->None:
    if not v: raise GateFailure(m)
def sha(p:Path)->str:
    h=hashlib.sha256()
    with p.open('rb') as f:
        for c in iter(lambda:f.read(1024*1024),b''): h.update(c)
    return h.hexdigest().upper()
def load(p:Path)->dict[str,Any]: return json.loads(p.read_text(encoding='utf-8-sig'))
def ev(p:Path)->dict[str,Any]: return {'path':str(p.resolve()),'bytes':p.stat().st_size,'sha256':sha(p)}
def verify_files(root:Path,m:dict[str,Any])->tuple[int,int]:
    bad=[]
    for x in m['files']:
        p=root/x['path']
        if not p.is_file() or p.stat().st_size!=x['bytes'] or sha(p)!=x['sha256'].upper(): bad.append(x['path'])
    require(not bad,f'payload drift: {bad[:5]}')
    return len(m['files']),sum(x['bytes'] for x in m['files'])
def card_view(cards:list[dict[str,Any]])->list[dict[str,str]]: return [{'front':x['front'],'back':x['back']} for x in cards]
def readback_file(root:Path,rel:str)->Path:
    matches=list(root.glob(f"worker-process-v3/jobs/*/{rel}"))
    require(len(matches)==1,f'readback resolution drift: {rel}')
    return matches[0]
def verify_scenario(item:dict[str,Any],runtime:Path,templates:list[str])->dict[str,Any]:
    cards=item['draft']['cards']; rb=item['semanticReadback']
    require(item['expectedTemplates']==templates and [x['template'] for x in cards]==templates,'template/count/order drift')
    require(card_view(cards)==card_view(rb['cards']),'preview/readback text drift')
    require(item['before']['ankiExternalWriteCount']==item['after']['ankiExternalWriteCount']-1 or item['after']['ankiReadbackCount']==item['before']['ankiReadbackCount']+1,'confirmed Anki effect/readback delta missing')
    require(item['after']['ankiExportAttempts']==item['before']['ankiExportAttempts']+1,'not exactly one confirmed Anki attempt')
    require(item['before']['remNoteCalls']==item['after']['remNoteCalls']==0 and item['before']['minerUCalls']==item['after']['minerUCalls']==0,'unexpected destination/provider calls')
    p=readback_file(runtime,rb['artifactPath']); require(sha(p)==rb['sha256'].upper(),'saved readback SHA drift')
    return {'mode':item['mode'],'viaAsk':item['viaAsk'],'templates':templates,'cards':card_view(cards),'readback':ev(p),'preConfirmExternalWriteDelta':0,'confirmAnkiAttemptDelta':1}
def evaluate(candidate:Path,sdk:Path)->dict[str,Any]:
    candidate,sdk=candidate.resolve(),sdk.resolve(); mp=candidate/'candidate-manifest.json'; fp=candidate/'fake-language-matrix-receipt.json'; rp=candidate/'real-language-matrix-receipt.v4.json'; dp=candidate/'authority/dependency-manifest.v1.json'; sm=sdk/'freeze-manifest.json'
    for p,k in ((mp,'manifest'),(fp,'fake'),(rp,'real'),(dp,'dependency'),(sm,'sdk')): require(sha(p)==EXPECTED[k],f'{k} SHA drift')
    m=load(mp); count,total=verify_files(candidate,m); client=candidate/'core-v3/KnowledgeClipper.Core.V3.Client.dll'; require(sha(client)==EXPECTED['client'],'Client drift')
    dep=load(dp); require(dep['portableRecursiveSchemaValidation']=='passed','portable closure failed')
    sf=load(sm); sc,st=verify_files(sdk,sf); require(sf['source']['candidate']==str(candidate) and sf['source']['candidateManifestSha256']==EXPECTED['manifest'] and sf['source']['realReceiptSha256']==EXPECTED['real'],'SDK binding drift')
    real=load(rp); require(real['status']=='PASS' and Path(real['candidate']).resolve()==candidate,'real receipt binding drift'); require(real['contentWorker']=='content.deepseek@0.9.5' and real['ankiWorker']=='export.anki@1.4.1','worker version drift'); require(real['physicalSpeakerPlaybackCount']==4,'physical playback count drift'); require(real['remnoteCalls']==real['ocrCalls']==0,'unexpected external path')
    runtime=Path(real['runtimeDataRoot']); require(runtime.is_dir(),'runtime root missing')
    rows={x['mode']:x for x in real['matrix']}; require(set(rows)=={'meaning-reading','writing','both'},'matrix incomplete')
    out=[verify_scenario(rows['meaning-reading'],runtime,['language_context']),verify_scenario(rows['writing'],runtime,['language_writing']),verify_scenario(rows['both'],runtime,['language_context','language_writing']),verify_scenario(real['ask'],runtime,['language_context'])]
    fake=load(fp); require(fake['status']=='PASS' and fake['externalWorkersExecuted'] is False and fake['speakerPlayback'] is False,'Fake boundary drift')
    require([len(x['draft']['cards']) for x in fake['languageMatrix']]==[1,1,2],'Fake matrix count drift')
    return {'schema':'kc.quality.real-language-matrix-candidate/1','createdAt':datetime.now(timezone.utc).isoformat(),'status':'PASS-CORE-CANDIDATE-BOUND-REAL-LANGUAGE-MATRIX','candidate':{'root':str(candidate),'manifest':ev(mp),'files':count,'bytes':total,'mismatches':0,'client':ev(client),'dependency':ev(dp)},'sdk':{'root':str(sdk),'manifest':ev(sm),'files':sc,'bytes':st,'mismatches':0},'receipts':{'fake':ev(fp),'real':ev(rp)},'matrix':out,'workers':{'content':'0.9.5','anki':'1.4.1','physicalPlaybackCount':4},'boundary':{'coreCandidateBound':True,'desktopCandidateBound':False,'userHandTestGo':False,'externalRerunByQuality':False}}
def main()->int:
    a=argparse.ArgumentParser(); a.add_argument('--candidate',type=Path,required=True); a.add_argument('--sdk',type=Path,required=True); a.add_argument('--output',type=Path,required=True); x=a.parse_args(); r=evaluate(x.candidate,x.sdk); x.output.parent.mkdir(parents=True,exist_ok=False); x.output.write_text(json.dumps(r,ensure_ascii=False,indent=2)+'\n',encoding='utf-8'); print(f'{sha(x.output)} {x.output}'); return 0
if __name__=='__main__': raise SystemExit(main())
