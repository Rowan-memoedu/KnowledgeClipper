from __future__ import annotations
import argparse,json,subprocess
from datetime import datetime,timezone
from pathlib import Path
from typing import Any
import verify_desktop_read_to_anki_candidate as base

E={'manifest':'D0F4E3E2CD90D83407486C1457D2A0BD6DA4427CD18807FD54A4E5912F2AC172','owner':'DD676F06817B35E244852A822FE77EC00EC24066F8A8FECC83EBC6D13EB9BCA0','build':'C20C5E11EE77A20722B564D1D7D8FD56588352EBFC6761D903B73BEC260B8560','exe':'E9C891BC3354810E1D9D4F6348EE58D8578BF08256548A23B8B3B831288E4A61','core':'92AC56277BB06A06137B2FF22EEFCAAB578A0072CC06CDDDEF34350BC4C8BF67','sdk':'147C00CC33018EAA791C4D8E017FBE8F7EB900B2747720F8024514629D4F5A78','descriptor':'BEA27B4B6C3085B5D4FA3603F71FCD1ABB52D3DCCFB6A0C8BD1268D2843F5BE3','payloadTree':'DA860D93B20DB9353730A8AC9DEE5A27055CC96232CD28F7D01718C1D3A574B4','sealedTree':'BD211239F2B97F3D5882CDD066A7BAF08225FEA8A57DDF776BA2BD76BF4EB04F'}
class GateFailure(RuntimeError):pass
def req(v,m):
 if not v:raise GateFailure(m)
def sha(p):return base.sha256_file(Path(p))
def ev(p):return base.evidence(Path(p))
def load(p):return base.load(Path(p))
def evaluate(c:Path,e:Path)->dict[str,Any]:
 c,e=c.resolve(),e.resolve();mp=c/'candidate-file-manifest.v1.json';bp=c/'desktop-build-receipt.v1.json';op=e/'final-receipt.json';sp=e/'startup-diagnostic.json';ip=e/'integration-startup-canary.json'
 for p,k in ((mp,'manifest'),(bp,'build'),(op,'owner'),(c/'KnowledgeClipper.exe','exe'),(c/'core-read-to-anki-candidate-manifest.json','core'),(c/'core-read-to-anki-sdk-freeze-manifest.json','sdk'),(c/'authority/runtime-tools.v1.json','descriptor')):req(sha(p)==E[k],f'{k} SHA drift')
 m,b=load(mp),load(bp);base.verify_rows(c,m['files'],'payload');req((len(m['files']),sum(x['bytes'] for x in m['files']),base.payload_tree(m['files']))==(10747,844348725,E['payloadTree']),'payload aggregate drift');base.verify_rows(c,b['files'],'sealed');req((b['fileCount'],b['totalBytes'],base.startup_tree(b['files']))==(10748,846683850,E['sealedTree']),'sealed aggregate drift')
 actual=base.inventory(c);req((len(actual),sum(x['bytes'] for x in actual))==(10749,849017931),'post-seal inventory drift');req({x['path'] for x in actual}-{x['path'] for x in b['files']}=={'desktop-build-receipt.v1.json'},'unexpected post-seal files')
 d=load(c/'authority/runtime-tools.v1.json');tools={}
 for name,x in d['tools'].items():
  p=c/x['path'];req(p.resolve().is_relative_to(c) and p.stat().st_size==x['bytes'] and sha(p)==x['sha256'],'runtime tool drift');tools[name]=ev(p)
 s,i=load(sp),load(ip);req(s['result']=='PASS' and s['candidateRuntimeToolsVerified'] and s['packageTreeVerified'],'startup admission failed');req((s['packageFileCount'],s['packageTotalBytes'],s['packageTreeSha256'].upper())==(10748,846683850,E['sealedTree']),'startup tree readback drift');req(s['externalConnections']==0 and not s['productionWorkDirectoryCreated'],'startup side effect')
 req(i['result']=='PASS' and i['namedPipeInvoked'] and i['queryItemCount']==8,'IPC startup failed');req(not any(i[k] for k in ('externalWorkerInvoked','physicalSpeakerInvoked','remNoteInvoked','ankiInvoked','mineruInvoked')),'IPC crossed external boundary');task=subprocess.run(['tasklist','/FI',f"PID eq {i['orchestratorProcessId']}",'/FO','CSV','/NH'],capture_output=True,text=True);req(str(i['orchestratorProcessId']) not in task.stdout,'orchestrator process leaked')
 o=load(op);req(o['candidate']['manifestSha256']==E['manifest'] and o['core']['candidateManifestSha256']==E['core'] and o['core']['sdkFreezeManifestSha256']==E['sdk'] and o['core']['runtimeDescriptorSha256']==E['descriptor'],'owner recursive binding drift');req(o['safety']['externalServiceCalls']==o['safety']['physicalSpeakerCalls']==o['safety']['remnoteCalls']==o['safety']['mineruCalls']==0,'owner safety drift')
 return {'schema':'kc.quality.desktop-runtime-self-contained-candidate/1','verifiedAt':datetime.now(timezone.utc).isoformat(),'status':'CONDITIONAL-NO-GO-MISSING-ACTUAL-DESKTOP-ACTION','candidate':{'root':str(c),'entrypoint':ev(c/'KnowledgeClipper.exe'),'manifest':ev(mp),'payload':{'files':10747,'bytes':844348725,'treeSha256':E['payloadTree'],'mismatches':0},'sealed':{'files':10748,'bytes':846683850,'treeSha256':E['sealedTree'],'mismatches':0},'runtimeTools':tools},'bindings':{'core':ev(c/'core-read-to-anki-candidate-manifest.json'),'sdk':ev(c/'core-read-to-anki-sdk-freeze-manifest.json'),'descriptor':ev(c/'authority/runtime-tools.v1.json')},'verified':{'cleanEnvironmentStartup':True,'packagedRuntimeTools':True,'capabilitiesQuery':True,'processCleaned':True,'externalCalls':0},'missing':{'actualDesktopSemanticReadToAnkiAction':True,'synthesisAdmissionFromDesktopAction':True,'playbackAdmissionFromDesktopAction':True,'oneCardPrePostChain':True,'bothTwoCardPrePostChain':True},'decision':{'userHandTestGo':False,'reason':'startup/capabilities and offline UI tests do not exercise the real Desktop Read-to-Anki action through runtime admission'}}
def main():
 a=argparse.ArgumentParser();a.add_argument('--candidate',type=Path,required=True);a.add_argument('--evidence',type=Path,required=True);a.add_argument('--output',type=Path,required=True);x=a.parse_args();r=evaluate(x.candidate,x.evidence);x.output.parent.mkdir(parents=True,exist_ok=False);x.output.write_text(json.dumps(r,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print(f'{sha(x.output)} {x.output}')
if __name__=='__main__':main()
