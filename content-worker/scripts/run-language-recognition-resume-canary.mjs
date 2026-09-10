import assert from 'node:assert/strict';
import { mkdir, readFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { stage, run, output, read, save, workerRoot } from '../dist/test/language-resume-fixture.js';
import { sha256 } from '../dist/src/util.js';

const exe=process.argv[2];
const root=path.join('D:/Artifacts/KnowledgeClipper/content-worker/canary',`language-recognition-resume-${new Date().toISOString().replace(/[-:.]/g,'')}`);
await mkdir(root,{recursive:true});
const cases=await read(path.join(workerRoot,'fixtures/language-recognition-resume/cases.json'));
const evidence=[];
const digest=async file=>({path:file,sha256:sha256(await readFile(file))});
async function execute(s){
  if(!exe)return run(s);
  const p=spawnSync(exe,['run','--request',s.requestPath],{cwd:'D:/Artifacts',encoding:'utf8',windowsHide:true,timeout:30000});
  await save(path.join(s.job,s.request.attemptPath,'process.txt'),`${p.stdout}\n${p.stderr}`);
  assert.equal(p.status,0,p.stderr);
  return {result:await read(path.join(s.job,s.request.resultPath))};
}
for(const c of cases){
  const first=await stage(c,undefined,c.category==='general_learning'?input=>{input.languagePracticeMode='both';}:undefined,root);
  const initial=await execute(first);
  assert.equal(initial.result.status,'completed',JSON.stringify(initial.result));
  const stages=[{stage:first,result:initial.result}];
  if(c.category.startsWith('language_')){
    assert.equal(initial.result.outputs.some(r=>r.kind==='card-bundle'),false);
    for(const mode of ['meaning-reading','writing','both']){
      const next=await stage(c,{stage:first,result:initial.result,mode});
      const r=await execute(next); assert.equal(r.result.status,'completed',JSON.stringify(r.result));
      const learning=await output(next,r.result,'learning-bundle');
      assert.ok(learning.ai_trace.every(t=>!['classification','cleaning'].includes(t.stage)));
      if(c.unresolved)assert.equal(r.result.outputs.some(o=>o.kind==='card-bundle'),false);
      else assert.equal(r.result.payload.moduleReleaseState,'ready',JSON.stringify(r.result));
      stages.push({stage:next,result:r.result});
    }
  }
  for(const {stage:s,result:r} of stages){
    const learning=await output(s,r,'learning-bundle');
    const cards=r.outputs.some(o=>o.kind==='card-bundle')?await output(s,r,'card-bundle'):null;
    evidence.push({case:c.id,mode:s.input.human_overrides?.languagePracticeMode??'recognize',source:c.source,selected:c.selected,providerMode:'strict-fake-replay',request:await digest(s.requestPath),input:await digest(path.join(s.job,s.inputPath)),result:await digest(path.join(s.job,s.request.resultPath)),outputs:r.outputs,release:r.payload.moduleReleaseState,stages:learning.ai_trace.map(t=>t.stage),cards:cards?.cards.map(c=>({template:c.template,front:c.front_blocks.map(b=>b.text).join(''),back:c.back_blocks.map(b=>b.text).join('')}))??[],gates:learning.items.flatMap(i=>i.gates),cleaned:learning.items.map(i=>i.cleaning.cleaned_text)});
  }
}
await save(path.join(root,'receipt.json'),{schemaVersion:'content-language-recognition-resume-canary/1.0.0',workerVersion:'0.12.0',entrypoint:exe??'production runCommonRequest with captured Fake provider requests',...(exe?{executable:await digest(exe)}:{}),liveCalls:0,exporterCalls:0,results:evidence});
await save(path.join(root,'readable.md'),'# 识别→选择→制卡\n\n全部为严格 Fake/replay。真实请求构造覆盖另见模块测试记录；这里验证正式入口消费。\n\n'+evidence.map(e=>`## ${e.case} / ${e.mode}\n\n原文：${e.source}\n\n选区：${e.selected}\n\n清洗：${e.cleaned.join('\n')}\n\n状态：${e.release}；实际 provider stages：${e.stages.join(', ')||'无'}\n\n${e.cards.map(c=>`${c.template}：${c.front} → ${c.back}`).join('\n\n')||'未发布卡片'}`).join('\n\n'));
console.log(JSON.stringify(await digest(path.join(root,'receipt.json'))));
