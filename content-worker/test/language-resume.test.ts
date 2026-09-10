import test from "node:test";
import assert from "node:assert/strict";
import path from "node:path";
import { read, run, stage, output, responses, workerRoot, type Case } from "./language-resume-fixture.js";
import { sha256, stableStringify } from "../src/util.js";
import type { CardBundle, LanguagePracticeDecisionBundle, LearningBundle } from "../src/types.js";
import { loadContracts } from "../src/contracts.js";
import { loadLanguageResume } from "../src/language-resume.js";
import { loadBuiltInRuntimeContentResources } from "../src/settings-adapter.js";

const cases=await read<Case[]>(path.join(workerRoot,"fixtures/language-recognition-resume/cases.json"));
for(const c of cases.slice(0,2)) for(const mode of ["meaning-reading","writing","both"] as const){
  test(`${c.id}: production classification receives selection; frozen decision resumes ${mode} without reclassification`,async()=>{
    const first=await stage(c); const recognized=await run(first);
    assert.equal(recognized.result.status,"completed",JSON.stringify(recognized.result));
    assert.deepEqual(recognized.calls.map(r=>r.stage),["cleaning","classification"]);
    const actualRequest=recognized.calls[1]!;
    assert.deepEqual(actualRequest.payload.language_selection,first.input.items[0]!.language_selection);
    assert.equal(actualRequest.payload.languagePracticeMode,undefined);
    assert.equal(recognized.result.outputs.some(r=>r.kind==="card-bundle"),false);
    const decision=await output<LanguagePracticeDecisionBundle>(first,recognized.result,"language-practice-decision");
    assert.equal(decision.schema_version,"language-practice-decision/2.0.0");
    assert.ok(decision.decisions[0]!.reasons.includes("language_practice_mode_required"));
    const next=await stage(c,{stage:first,result:recognized.result,mode}); const completed=await run(next);
    assert.equal(completed.result.status,"completed",JSON.stringify(completed.result));
    assert.equal(completed.result.payload?.moduleReleaseState,"ready",JSON.stringify(completed.result));
    assert.ok(completed.calls.every(r=>r.stage!=="classification"&&r.stage!=="cleaning"));
    const cards=await output<CardBundle>(next,completed.result,"card-bundle");
    assert.deepEqual(cards.cards.map(c=>c.template),mode==="both"?["language_context","language_writing"]:[mode==="writing"?"language_writing":"language_context"]);
    const writing=cards.cards.find(c=>c.template==="language_writing");
    if(writing){assert.equal(writing.front_blocks[0]!.text,c.source.replace(c.selected,`（${c.pronunciation}；${c.meaning}）`));assert.equal(writing.back_blocks[0]!.text,c.selected);}
    const learning=await output<LearningBundle>(next,completed.result,"learning-bundle");
    assert.ok(learning.items[0]!.gates.some(g=>g.gate_id==="classification.frozen-resume"));
  });
}

test("same raw source without selection uses ordinary classification; default both does not force language",async()=>{
  const c={...cases[0]!,category:"general_learning"};
  const s=await stage(c,undefined,input=>{input.schema_version="standard-input/2.0.0";delete input.items[0]!.language_selection;input.items[0]!.source={kind:"text",material_path:"materials/context.txt"};input.languagePracticeMode="both";});
  const result=await run(s); assert.equal(result.result.status,"completed",JSON.stringify(result.result));
  assert.equal(result.calls[1]!.payload.language_selection,undefined);
  assert.deepEqual((await output<CardBundle>(s,result.result,"card-bundle")).cards.map(c=>c.template),["basic_qa"]);
});
test("nonlanguage selected material plus default both stays ordinary",async()=>{
  const s=await stage(cases[2]!,undefined,input=>{input.languagePracticeMode="both";});const r=await run(s);
  assert.equal(r.result.status,"completed");assert.equal(r.result.outputs.some(o=>o.kind==="language-practice-decision"),false);
  assert.deepEqual((await output<CardBundle>(s,r.result,"card-bundle")).cards.map(c=>c.template),["basic_qa"]);
});
test("ambiguous classification remains unresolved after selecting both",async()=>{
  const c=cases[3]!;const first=await stage(c);const recognized=await run(first);
  const next=await stage(c,{stage:first,result:recognized.result,mode:"both"});const r=await run(next);
  assert.equal(r.result.status,"completed");assert.equal(r.result.payload?.moduleReleaseState,"unresolved");assert.equal(r.calls.length,0);assert.equal(r.result.outputs.some(o=>o.kind==="card-bundle"),false);
});
test("Actual T050622 unaltered general response never dispatches conflicting language templates",async()=>{
  const raw=await read<{response:unknown;responseSha256:string}>(path.join(workerRoot,"fixtures/language-recognition-resume/actual-t050622-classification.json"));
  assert.equal(sha256(stableStringify(raw.response)),raw.responseSha256);
  const c=cases[0]!;const fixture=responses(c);fixture["classification:item-source"]=raw.response;
  const s=await stage(c,undefined,input=>{input.languagePracticeMode="both";input.human_overrides={languagePracticeMode:"both"};input.items[0]!.hints={requested_templates:["language_context","language_writing"]};},undefined,fixture);
  const r=await run(s);assert.equal(r.result.status,"completed");assert.equal(r.result.payload?.moduleReleaseState,"unresolved");assert.deepEqual(r.calls.map(r=>r.stage),["cleaning","classification"]);
  assert.ok((await output<LearningBundle>(s,r.result,"learning-bundle")).items[0]!.unresolved_reasons.includes("requested_template_category_conflict"));
});
test("frozen recognition does not bypass missing pronunciation gate",async()=>{
  const c=cases[0]!;const first=await stage(c);const recognized=await run(first);
  const replay=responses(c);replay["language_writing:item-source:unit-1"]={pronunciation:{value:"",reliability:"missing",evidence:"No reliable reading in offline fixture"},meaning:{value:"刻意的",reliability:"reliable",evidence:"Fake fixture"},unresolved_reasons:[]};
  const next=await stage(c,{stage:first,result:recognized.result,mode:"both"},undefined,undefined,replay);const r=await run(next);
  assert.equal(r.result.status,"completed");assert.equal(r.result.payload?.moduleReleaseState,"unresolved");assert.equal(r.result.outputs.some(o=>o.kind==="card-bundle"),false);
  assert.ok((await output<LanguagePracticeDecisionBundle>(next,r.result,"language-practice-decision")).decisions[0]!.reasons.includes("language_writing_details_required"));
});
test("resume rejects changed prompt settings before provider use",async()=>{
  const c=cases[0]!;const first=await stage(c);const recognized=await run(first);
  const next=await stage(c,{stage:first,result:recognized.result,mode:"both"});
  const contracts=await loadContracts(path.join(next.job,next.request.inputRefs[0]!.manifestPath),path.join(next.job,next.inputPath),next.job);
  const resources=await loadBuiltInRuntimeContentResources(workerRoot);
  await assert.rejects(loadLanguageResume(contracts,{manifestPath:path.join(next.job,next.request.inputRefs[0]!.manifestPath),inputPath:path.join(next.job,next.inputPath),outputDir:path.join(next.job,next.request.outputPath),attemptId:next.request.attemptId,promptRoot:resources.promptRoot,registryPath:resources.registryPath,promptSetSha256:"0".repeat(64)}),/settings changed/u);
});
for(const mutation of ["decision-sha","ancestry","selection","context-sha","target-sha","mode","unknown","version"]){
  test(`resume rejects ${mutation} before any provider call`,async()=>{
    const c=cases[0]!;const first=await stage(c);const recognized=await run(first);
    const next=await stage(c,{stage:first,result:recognized.result,mode:"both"},input=>{
      if(mutation==="decision-sha")input.language_practice_resume!.decision_ref.payload_sha256="0".repeat(64);
      if(mutation==="ancestry")input.language_practice_resume!.learning_ref.artifactRef="another-artifact";
      if(mutation==="selection")input.items[0]!.hints={category_id:"general_learning"};
      if(mutation==="context-sha")input.items[0]!.language_selection!.context_segments[0]!.sha256="0".repeat(64);
      if(mutation==="target-sha")input.items[0]!.language_selection!.target_text_sha256="0".repeat(64);
      if(mutation==="mode")input.languagePracticeMode="writing";
      if(mutation==="unknown")Object.assign(input.language_practice_resume!,{unknown:true});
      if(mutation==="version")input.input_version="1";
    });
    const r=await run(next);assert.equal(r.result.status,"failed",JSON.stringify(r.result));assert.equal(r.calls.length,0);assert.deepEqual(r.result.outputs,[]);
  });
}
