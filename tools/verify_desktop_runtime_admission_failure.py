from __future__ import annotations
import argparse,hashlib,json
from datetime import datetime,timezone
from pathlib import Path
class GateFailure(RuntimeError): pass
def req(v,m):
    if not v: raise GateFailure(m)
def sha(p): return hashlib.sha256(Path(p).read_bytes()).hexdigest().upper()
def evaluate(fixture):
    s=fixture['screenshot']; old=fixture['invalidates']; obs=fixture['observed']; req(sha(s['path'])==s['sha256'] and Path(s['path']).stat().st_size==s['bytes'],'screenshot drift'); req(sha(old['path'])==old['sha256'],'invalidated receipt drift'); req(obs['buttonClicked'] and not obs['runtimeAdmissionPassed'],'real failure boundary drift'); req(obs['missingDependency']=='KNOWLEDGE_CLIPPER_EDGE_TTS' and 'existing absolute path' in obs['error'],'failure identity drift'); return {'schema':'kc.quality.desktop-runtime-admission-invalidation/1','createdAt':datetime.now(timezone.utc).isoformat(),'status':'NO-GO-REAL-USER-RUNTIME-ADMISSION-FAILURE','candidate':fixture['candidate'],'entrypoint':fixture['entrypoint'],'observed':obs,'screenshot':s,'invalidates':old,'userHandTestGo':False,'gateGap':'startup diagnostics did not exercise Read-to-Anki runtime admission with developer environment removed'}
def main():
 a=argparse.ArgumentParser(); a.add_argument('--fixture',type=Path,required=True); a.add_argument('--output',type=Path,required=True); x=a.parse_args(); r=evaluate(json.loads(x.fixture.read_text(encoding='utf-8'))); x.output.parent.mkdir(parents=True,exist_ok=False); x.output.write_text(json.dumps(r,ensure_ascii=False,indent=2)+'\n',encoding='utf-8'); print(f'{sha(x.output)} {x.output}')
if __name__=='__main__': main()
