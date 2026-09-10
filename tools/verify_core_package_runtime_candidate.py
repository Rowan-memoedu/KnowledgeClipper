from __future__ import annotations
import argparse,hashlib,json
from datetime import datetime,timezone
from pathlib import Path
from typing import Any

E={'manifest':'92AC56277BB06A06137B2FF22EEFCAAB578A0072CC06CDDDEF34350BC4C8BF67','descriptor':'BEA27B4B6C3085B5D4FA3603F71FCD1ABB52D3DCCFB6A0C8BD1268D2843F5BE3','real':'C20AB0E2F6941E781FE9C96C4968E69833D71B396C1480594711C375E68C78E6','drift':'7724AA02782FD6821C77EBED0135F0516C4C0788BA2B72AB7703EE0317CB9E64','aggregate':'6E2559B5E398C781D9EEF14A6B261990C8D3479F92481CE31565746B794BC63E','sdk':'147C00CC33018EAA791C4D8E017FBE8F7EB900B2747720F8024514629D4F5A78'}
class GateFailure(RuntimeError):pass
def req(v,m):
 if not v:raise GateFailure(m)
def sha(p):
 h=hashlib.sha256()
 with Path(p).open('rb') as f:
  for c in iter(lambda:f.read(1024*1024),b''):h.update(c)
 return h.hexdigest().upper()
def load(p):return json.loads(Path(p).read_text(encoding='utf-8-sig'))
def ev(p):p=Path(p);return {'path':str(p.resolve()),'bytes':p.stat().st_size,'sha256':sha(p)}
def verify_manifest(root,m):
 bad=[]
 for x in m['files']:
  p=root/x['path']
  if not p.is_file() or p.stat().st_size!=x['bytes'] or sha(p)!=x['sha256'].upper():bad.append(x['path'])
 req(not bad,f'manifest drift:{bad[:5]}');return len(m['files']),sum(x['bytes'] for x in m['files'])
def readback(root,rel):
 xs=list(root.glob(f'worker-process-v3/jobs/*/{rel}'));req(len(xs)==1,f'readback resolution:{rel}');return xs[0]
def evaluate(c:Path,sdk:Path)->dict[str,Any]:
 c,sdk=c.resolve(),sdk.resolve();mp=c/'candidate-manifest.json';dp=c/'authority/runtime-tools.v1.json';rp=c/'real-language-matrix-no-env-receipt.v2.json';hp=c/'runtime-admission-self-test-receipt.json';ap=c/'runtime-self-contained-qualification-receipt.json';sm=sdk/'freeze-manifest.json'
 for p,k in ((mp,'manifest'),(dp,'descriptor'),(rp,'real'),(hp,'drift'),(ap,'aggregate'),(sm,'sdk')):req(sha(p)==E[k],f'{k} SHA drift')
 m=load(mp);fc,bt=verify_manifest(c,m);d=load(dp);tools={}
 for name,meta in d['tools'].items():
  p=c/meta['path'];req(p.is_file() and p.stat().st_size==meta['bytes'] and sha(p)==meta['sha256'],'runtime tool drift');req(p.resolve().is_relative_to(c),'runtime tool escapes candidate');tools[name]=ev(p)
 req(set(tools)=={'edgeTts','ffplay'},'runtime tool set drift')
 h=load(hp);req(h['status']=='PASS' and h['cases']==['candidate-relative-paths-and-hashes-pass','hash-drift-fails-before-operation'] and h['externalCalls']==0 and not h['physicalSpeakerPlayback'],'hash drift self-test boundary failed')
 r=load(rp);req(r['status']=='PASS' and Path(r['candidate']).resolve()==c,'real matrix candidate drift');req(r['runtimeAdmission']=={'descriptor':'authority/runtime-tools.v1.json','developerEnvironmentRemoved':True,'pathCleared':True},'clean environment admission drift');req(r['contentWorker']=='content.deepseek@0.9.5' and r['ankiWorker']=='export.anki@1.4.1' and r['physicalSpeakerPlaybackCount']==4,'worker/playback drift');req(r['remnoteCalls']==r['ocrCalls']==0,'unexpected path')
 runtime=Path(r['runtimeDataRoot']);rows={x['mode']:x for x in r['matrix']};req(set(rows)=={'meaning-reading','writing','both'},'matrix incomplete');expected={'meaning-reading':['language_context'],'writing':['language_writing'],'both':['language_context','language_writing']};summary=[]
 for mode,templates in expected.items():
  x=rows[mode];cards=x['draft']['cards'];rb=x['semanticReadback'];req([z['template'] for z in cards]==templates,'template order drift');req([{'front':z['front'],'back':z['back']} for z in cards]==[{'front':z['front'],'back':z['back']} for z in rb['cards']],'readback text drift');req(x['after']['ankiExportAttempts']==x['before']['ankiExportAttempts']+1 and x['before']['ankiExternalWriteCount']==x['after']['ankiExternalWriteCount'],'confirmed idempotent attempt drift');p=readback(runtime,rb['artifactPath']);req(sha(p)==rb['sha256'].upper(),'saved readback drift');summary.append({'mode':mode,'cards':len(cards),'readback':ev(p)})
 ask=r['ask'];req(ask['viaAsk'] and ask['expectedTemplates']==['language_context'],'ask drift')
 sf=load(sm);sc,sb=verify_manifest(sdk,sf);req(sf['source']['candidate']==str(c) and sf['source']['candidateManifestSha256']==E['manifest'] and sf['source']['realReceiptSha256']==E['real'],'SDK binding drift')
 a=load(ap);req(a['status']=='PASS' and a['boundaries']=={'userEnvironmentRequired':False,'pathFallback':False,'developerToolFallback':False,'remNoteCalls':0,'minerUCalls':0,'oldCandidate':'rta-20260903T211155Z-90c373ff invalid-for-user-handtest'},'aggregate boundary drift')
 return {'schema':'kc.quality.core-package-runtime-candidate/1','verifiedAt':datetime.now(timezone.utc).isoformat(),'status':'PASS-CORE-PACKAGE-RUNTIME-ADMISSION','candidate':{'root':str(c),'manifest':ev(mp),'files':fc,'bytes':bt,'mismatches':0,'runtimeDescriptor':ev(dp),'tools':tools},'sdk':{'root':str(sdk),'manifest':ev(sm),'files':sc,'bytes':sb,'mismatches':0},'receipts':{'real':ev(rp),'hashDrift':ev(hp),'aggregate':ev(ap)},'matrix':summary,'boundary':{'environmentRemoved':True,'pathCleared':True,'desktopCandidateBound':False,'questionCrashRecoveryPassed':False,'userHandTestGo':False,'qualityExternalRerun':False}}
def main():
 a=argparse.ArgumentParser();a.add_argument('--candidate',type=Path,required=True);a.add_argument('--sdk',type=Path,required=True);a.add_argument('--output',type=Path,required=True);x=a.parse_args();r=evaluate(x.candidate,x.sdk);x.output.parent.mkdir(parents=True,exist_ok=False);x.output.write_text(json.dumps(r,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print(f'{sha(x.output)} {x.output}')
if __name__=='__main__':main()
