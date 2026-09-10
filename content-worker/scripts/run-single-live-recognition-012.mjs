import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import assert from 'node:assert/strict';

const release='D:/Artifacts/KnowledgeClipper/content-worker/releases/content.deepseek-0.12.0-win-x64-formal-20260908T054141Z';
const app=path.join(release,'app');
const exe=path.join(release,'KnowledgeClipper.Content.Worker.exe');
const {stableStringify}=await import(pathToFileURL(path.join(app,'dist/src/util.js')));
const hash=b=>createHash('sha256').update(b).digest('hex');
const actual='D:/AppData/KnowledgeClipper/actual-desktop-rta-20260908T050622Z/KnowledgeClipper/core-v3/worker-process-v3/jobs/task-8f2a939491087e89ba0319a1';
const stamp=new Date().toISOString().replace(/[-:.]/g,'');
const root=`D:/Artifacts/KnowledgeClipper/content-worker/canary/single-live-recognition-${stamp}`;
const job=path.join(root,'job');
const save=async (p,data)=>{await mkdir(path.dirname(p),{recursive:true});await writeFile(p,typeof data==='string'||Buffer.isBuffer(data)?data:stableStringify(data,2)+'\n',{flag:'wx'});};
const evidence=async p=>({path:p,sha256:hash(await readFile(p))});
const rawInputPath=path.join(actual,'inputs/content/standard-input.v1.json');
const original=JSON.parse(await readFile(rawInputPath,'utf8'));
const input=structuredClone(original);
input.package_id=`single-live-${stamp}`;input.task_id=`task-single-live-${stamp}`;
delete input.languagePracticeMode;delete input.human_overrides;
for(const item of input.items){delete item.hints;delete item.human_overrides;}
const files=[];
async function declared(relative,bytes){await save(path.join(job,relative),bytes);files.push({path:relative,sha256:hash(bytes),bytes:Buffer.byteLength(bytes),mediaType:relative.endsWith('.txt')?'text/plain':'application/json'});}
await declared('inputs/content/input.v6.json',stableStringify(input,2)+'\n');
for(const item of input.items){
  await declared(item.source.material_path,await readFile(path.join(actual,item.source.material_path)));
  for(const context of item.language_selection.context_segments)await declared(context.material_path,await readFile(path.join(actual,context.material_path)));
}
const source=await readFile(path.join(job,input.items[0].language_selection.context_segments[0].material_path),'utf8');
assert.equal(source,'A deterministic external sentence with deliberate context.');
assert.equal(input.items[0].language_selection.selected_text,'deliberate');
const manifest={schemaVersion:'1.0.0',artifactId:`input-single-live-${stamp}`,kind:'content-input',contractVersion:'1.0.0',mediaType:'application/json',createdAt:new Date().toISOString(),files,producer:{stageId:'content-module-canary',workerId:'canary.fixture',workerVersion:'1.0.0',attemptId:`capture-${stamp}`},inputRefs:[],dataClass:'personal',metadata:{entrypoint:'inputs/content/input.v6.json',syntheticNonPrivate:true}};
await save(path.join(job,'inputs/content/artifact.json'),manifest);
const ap='runs/run-1/stages/content.understand-and-card/attempts/recognize-once';
const request={schemaVersion:'1.0.0',requestId:`request-${stamp}`,operationId:`operation-${stamp}`,taskId:input.task_id,runId:'run-1',stageId:'content.understand-and-card',attemptId:'recognize-once',workerId:'content.deepseek',operation:'understand-and-card',idempotencyKey:hash(stamp),attemptPath:ap,outputPath:`${ap}/outputs`,progressPath:`${ap}/progress.ndjson`,resultPath:`${ap}/result.json`,cancelSignal:{schemaVersion:'1.0.0',path:`${ap}/cancel.json`,gracePeriodMs:2000},inputRefs:[{artifactRef:manifest.artifactId,kind:'content-input',contractVersion:'1.0.0',manifestPath:'inputs/content/artifact.json',expectedSha256:hash(await readFile(path.join(job,'inputs/content/artifact.json')))}],configSnapshot:{destinationNeutral:true,promptSet:'open-md-v3',categoryRegistry:'category-registry/2.0.0'},payload:{schemaVersion:'content-run/1.0.0',contentInputPath:'inputs/content/input.v6.json',providerMode:'live'}};
const requestPath=path.join(job,ap,'request.json');await save(requestPath,request);
await save(path.join(root,'invocation-policy.json'),{workerInvocations:1,maxProviderAttemptsPerStage:1,mode:'ask via omitted choice, built-in runtime has no fallback choice',preselectedCategory:false,source:await evidence(rawInputPath),sourceText:source,selected:'deliberate',providerMode:'live',exporterCalls:0});
console.log(JSON.stringify({event:'single-live-started',jobRoot:job,requestPath}));
const processResult=await new Promise((resolve,reject)=>{
  const child=spawn(exe,['run','--request',requestPath],{cwd:'D:/Artifacts',windowsHide:true,env:{...process.env,AI_PRODUCER_MAX_RETRIES:'1'}});
  let stdout='',stderr='';child.stdout.on('data',b=>{stdout+=b;process.stdout.write(b);});child.stderr.on('data',b=>{stderr+=b;});child.once('error',reject);child.once('close',code=>resolve({code,stdout,stderr}));
});
await save(path.join(root,'stdout.txt'),processResult.stdout);await save(path.join(root,'stderr.txt'),processResult.stderr);
const resultPath=path.join(job,request.resultPath);const result=JSON.parse(await readFile(resultPath,'utf8'));
const lp=path.join(job,request.outputPath,'learning-bundle.schema-v1.json');
let learning;try{learning=JSON.parse(await readFile(lp,'utf8'));}catch{}
const trace=learning?.ai_trace??[];
const ct=trace.find(t=>t.stage==='classification');
let classificationPayload,requestHashMatches;
if(ct){
  classificationPayload={item_id:input.items[0].item_id,cleaned_text:trace.find(t=>t.stage==='cleaning').response.cleaned_text,segments:learning.items[0].source_segments,category_registry:JSON.parse(await readFile(path.join(app,'config/categories.v2.json'),'utf8')),hints:{},human_overrides:{},language_selection:input.items[0].language_selection};
  requestHashMatches=hash(stableStringify({stage:ct.stage,callKey:ct.call_key,prompt_sha256:ct.prompt_sha256,payload:classificationPayload}))===ct.request_sha256;
  await save(path.join(root,'actual-classification-request.reconstructed.json'),{verification:'Reconstructed using sealed production request builder; equality checked against recorded request SHA',matches:requestHashMatches,recordedSha256:ct.request_sha256,payload:classificationPayload});
}
const bundles=[];
for(const ref of result.outputs??[]){const mp=path.join(job,ref.manifestPath);const m=JSON.parse(await readFile(mp,'utf8'));bundles.push({kind:ref.kind,manifest:await evidence(mp),payload:await evidence(path.join(job,m.files[0].path)),data:JSON.parse(await readFile(path.join(job,m.files[0].path),'utf8'))});}
const cards=bundles.find(b=>b.kind==='card-bundle')?.data.cards??[];
const decisions=bundles.find(b=>b.kind==='language-practice-decision')?.data.decisions??[];
const receipt={schemaVersion:'content-single-live-recognition/1.0.0',workerVersion:'0.12.0',workerInvocations:1,source,selected:'deliberate',category:ct?.response.units?.map(u=>u.category_id)??[],workerStatus:result.status,releaseState:result.payload?.moduleReleaseState,error:result.error,cardCount:cards.length,decisions,classificationRequestHasBoundSelection:requestHashMatches??false,stages:trace.map(t=>({stage:t.stage,mode:t.mode,provider:t.provider,model:t.model,attempts:t.attempts,promptSha256:t.prompt_sha256,requestSha256:t.request_sha256,responseSha256:t.response_sha256})),automaticReruns:0,exporterCalls:0,executable:await evidence(exe),releaseManifest:await evidence(path.join(release,'release-manifest.v1.json')),request:await evidence(requestPath),result:await evidence(resultPath),...(learning?{learning:await evidence(lp)}:{}),bundles:bundles.map(({data,...b})=>b),scope:'One real Content recognition only; not Desktop Actual or destination evidence'};
await save(path.join(root,'receipt.json'),receipt);
await save(path.join(root,'readable.md'),`# 单次真实内容识别\n\n原文：${source}\n\n选区：deliberate\n\n清洗：${learning?.items[0]?.cleaning?.cleaned_text??'未完成'}\n\n真实分类：${receipt.category.join(', ')||'未完成'}\n\nWorker：${result.status}；模块：${receipt.releaseState}；卡数：${cards.length}\n\n决策：${JSON.stringify(decisions,null,2)}\n\n实际阶段：${receipt.stages.map(t=>`${t.stage} (${t.model}, attempts=${t.attempts})`).join(' → ')}\n\n分类请求选区绑定SHA复算：${requestHashMatches??'未到分类'}\n\n无目的端调用；不替代Desktop Actual。\n`);
console.log(JSON.stringify({receipt:await evidence(path.join(root,'receipt.json')),category:receipt.category,status:result.status,cardCount:cards.length,decisionCount:decisions.length,classificationRequestHasBoundSelection:receipt.classificationRequestHasBoundSelection,stages:receipt.stages}));
