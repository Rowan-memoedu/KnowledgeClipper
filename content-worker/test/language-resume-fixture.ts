import { mkdir, mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { runCommonRequest } from "../src/common-adapter.js";
import { ReplayProvider, type ProviderRequest } from "../src/provider.js";
import type { CommonArtifactManifest, CommonWorkerRequest, CommonWorkerResult, LanguagePracticeMode, StandardInput } from "../src/types.js";
import { sha256, stableStringify } from "../src/util.js";

export const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
export interface Case { id: string; source: string; selected: string; category: string; pronunciation?: string; meaning?: string; unresolved?: string[] }
export const read = async <T>(file: string): Promise<T> => JSON.parse(await readFile(file, "utf8")) as T;
export const save = async (file: string, data: unknown): Promise<void> => { await mkdir(path.dirname(file), {recursive:true}); await writeFile(file, typeof data === "string" ? data : `${stableStringify(data,2)}\n`, {flag:"wx"}); };
export interface Stage { job: string; requestPath: string; request: CommonWorkerRequest; input: StandardInput; manifest: CommonArtifactManifest; inputPath: string }

export function responses(c: Case): Record<string, unknown> {
  const language=c.category.startsWith("language_");
  const back=language?`${c.selected} ${c.pronunciation}：${c.meaning}。`:c.source;
  return {
    "cleaning:item-source": {cleaned_text:c.source,removed_fragments:[],format_anomalies:[]},
    "classification:item-source": {units:[{unit_id:"unit-1",source_segment_indexes:[0],category_id:c.category,subtype:language?"contextual_vocabulary":"concept",answer_state:language?"not_applicable":"provided",context_state:"provided",risk_state:"ordinary",...(language?{target_term:c.selected}:{supplied_answer:c.source}),unresolved_reasons:c.unresolved??[]}]},
    "card_generation:item-source:unit-1": {generated_content:language?[{kind:"explanation",text:back}]:[{kind:"question",text:"What does the source state?"}],cards:[{template:language?"language_context":"basic_qa",front_blocks:[{type:"text",text:language?c.source:"What does the source state?",provenance:language?"source":"generated"}],back_blocks:[{type:"text",text:back,provenance:language?"generated":"source"}],answer_provenance:language?"not_applicable":"source"}],unresolved_reasons:[]},
    "language_writing:item-source:unit-1": {pronunciation:{value:c.pronunciation??"",reliability:"reliable",evidence:"Strict Fake fixture, not a live lexical assertion"},meaning:{value:c.meaning??"",reliability:"reliable",evidence:"Strict Fake fixture"},unresolved_reasons:[]},
  };
}

export async function stage(c: Case, parent?: { stage: Stage; result: CommonWorkerResult; mode: LanguagePracticeMode }, mutate?: (input: StandardInput) => void, root?: string, suppliedResponses?: Record<string, unknown>): Promise<Stage> {
  const job=parent?.stage.job??await mkdtemp(path.join(root??tmpdir(),"content-recognition-"));
  const attemptId=parent?`choose-${parent.mode}`:"recognize";
  const inputPath=`inputs/${attemptId}/input.json`, manifestPath=`inputs/${attemptId}/artifact.json`, replayPath=`inputs/${attemptId}/replay.json`;
  let input: StandardInput;
  const declarations: CommonArtifactManifest["files"]=[];
  const refs: CommonArtifactManifest["inputRefs"]=[];
  async function declare(relative: string) {const b=await readFile(path.join(job,relative)); declarations.push({path:relative,sha256:sha256(b),bytes:b.length,mediaType:relative.endsWith("txt")?"text/plain":"application/json"});}
  if(parent){
    input=structuredClone(parent.stage.input);
    input.schema_version="standard-input/8.0.0"; input.input_version="2";
    input.human_overrides={languagePracticeMode:parent.mode}; input.languagePracticeMode=parent.mode;
    const extended=async (ref: CommonArtifactManifest["inputRefs"][number])=>{
      const manifest=await read<CommonArtifactManifest>(path.join(job,ref.manifestPath));
      const file=manifest.files.find(f=>f.path===manifest.metadata?.entrypoint)??manifest.files[0]!;
      refs.push(ref); await declare(file.path);
      return {...ref,payload_path:file.path,payload_sha256:file.sha256};
    };
    input.language_practice_resume={schema_version:"language-practice-resume/1.0.0",input_ref:await extended(parent.stage.request.inputRefs[0]!),learning_ref:await extended(parent.result.outputs.find(r=>r.kind==="learning-bundle")!),decision_ref:await extended(parent.result.outputs.find(r=>r.kind==="language-practice-decision")!)};
  } else {
    await save(path.join(job,"materials/selected.txt"),c.selected); await save(path.join(job,"materials/context.txt"),c.source);
    input={schema_version:"standard-input/6.0.0",package_id:`package-${c.id}`,task_id:`task-${c.id}`,input_version:"1",mode:"card",items:[{item_id:"item-source",source:{kind:"text",material_path:"materials/selected.txt"},language_selection:{schema_version:"language-selection/1.0.0",selected_text:c.selected,selected_text_sha256:sha256(c.selected),target_text:c.selected,target_text_sha256:sha256(c.selected),context_enabled:true,context_segments:[{segment_id:"complete-context",order:0,material_path:"materials/context.txt",sha256:sha256(c.source)}]}}]};
  }
  mutate?.(input);
  await declare("materials/selected.txt"); await declare("materials/context.txt");
  await save(path.join(job,inputPath),input); await declare(inputPath);
  const replay=suppliedResponses??responses(c);
  if(parent){delete replay["cleaning:item-source"];delete replay["classification:item-source"];}
  await save(path.join(job,replayPath),{schema_version:"provider-replay/1.0.0",responses:replay}); await declare(replayPath);
  const manifest: CommonArtifactManifest={schemaVersion:"1.0.0",artifactId:`input-${attemptId}-${c.id}`,kind:"content-input",contractVersion:"1.0.0",mediaType:"application/json",files:declarations,createdAt:"2026-09-08T00:00:00.000Z",producer:{stageId:"offline-capture",workerId:"test.fixture",workerVersion:"1.0.0",attemptId:`capture-${attemptId}`},inputRefs:refs,dataClass:"personal",metadata:{entrypoint:inputPath}};
  await save(path.join(job,manifestPath),manifest);
  const ap=`runs/run-1/stages/content.understand-and-card/attempts/${attemptId}`;
  const request: CommonWorkerRequest={schemaVersion:"1.0.0",requestId:`request-${attemptId}`,operationId:`operation-${c.id}`,taskId:input.task_id,runId:"run-1",stageId:"content.understand-and-card",attemptId,workerId:"content.deepseek",operation:"understand-and-card",idempotencyKey:sha256(`${job}:${attemptId}`),attemptPath:ap,outputPath:`${ap}/outputs`,progressPath:`${ap}/progress.ndjson`,resultPath:`${ap}/result.json`,cancelSignal:{schemaVersion:"1.0.0",path:`${ap}/cancel.json`,gracePeriodMs:2000},inputRefs:[{artifactRef:manifest.artifactId,kind:manifest.kind,contractVersion:"1.0.0",manifestPath,expectedSha256:sha256(await readFile(path.join(job,manifestPath)))}],configSnapshot:{destinationNeutral:true},payload:{schemaVersion:"content-run/1.0.0",contentInputPath:inputPath,providerMode:"replay",replayPath}};
  const requestPath=path.join(job,ap,"request.json"); await save(requestPath,request);
  return {job,requestPath,request,input,manifest,inputPath};
}

/** Calls the production common loader and worker; captures actual constructed provider requests. */
export async function run(s: Stage): Promise<{result: CommonWorkerResult; calls: ProviderRequest[]}> {
  const calls: ProviderRequest[]=[];
  const call=ReplayProvider.prototype.call;
  ReplayProvider.prototype.call=async function(request){const {signal, ...record}=request;void signal;calls.push(structuredClone(record));return call.call(this,request);};
  try {const result=await runCommonRequest(s.requestPath,workerRoot); await save(path.join(s.job,s.request.attemptPath,"provider-requests.fake.json"),calls);return {result,calls};}
  finally {ReplayProvider.prototype.call=call;}
}
export async function output<T>(s: Stage, result: CommonWorkerResult, kind: string): Promise<T> {
  const ref=result.outputs.find(r=>r.kind===kind); if(!ref)throw new Error(`Missing ${kind}: ${JSON.stringify(result)}`);
  const manifest=await read<CommonArtifactManifest>(path.join(s.job,ref.manifestPath));
  return read<T>(path.join(s.job,manifest.files[0]!.path));
}
