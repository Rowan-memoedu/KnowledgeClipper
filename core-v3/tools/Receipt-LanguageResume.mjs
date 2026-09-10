import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';
const root = path.resolve(process.argv[2]);
const read = p => JSON.parse(fs.readFileSync(p, 'utf8'));
const sha = p => crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
const bound = p => ({path:p, bytes:fs.statSync(p).size, sha256:sha(p)});
const corePath=path.join(root,'core-resume-producer-receipt.json');
const core=read(corePath), producerPath=path.join(root,'producer-receipt.json');
const matrixPath=path.join(root,'final-sequence-matrix.json');
assert.equal(core.status,'PASS'); assert.equal(read(matrixPath).status,'PASS');
const firstReplayPath=path.join(path.dirname(core.initialRequestPath),'core-formal-provider-replay.json');
const nextReplayPath=path.join(path.dirname(core.nextRequestPath),'core-formal-provider-replay.json');
const first=read(firstReplayPath), next=read(nextReplayPath);
for(const replay of [first,next]){
  assert.equal(replay.status,'completed'); assert.equal(replay.fakeProviderBoundary,true);
  assert.equal(replay.realProviderCalls,0); assert.equal(replay.workerExeInvoked,false);
  for(const k of ['request','result','formalCommonAdapter']) assert.equal(sha(replay[k+'Path'] ?? replay[k]),replay[k+'Sha256']);
}
assert.equal(first.moduleReleaseState,'unresolved'); assert.equal(next.moduleReleaseState,'ready');
assert.deepEqual(first.calls.map(c=>c.stage),['cleaning','classification']);
assert.deepEqual(next.calls.map(c=>c.stage),['card_generation','language_writing']);
const initialRequest=read(core.initialRequestPath), nextRequest=read(core.nextRequestPath);
assert.equal(initialRequest.taskId,nextRequest.taskId);
assert.notEqual(initialRequest.runId,nextRequest.runId);assert.notEqual(initialRequest.attemptId,nextRequest.attemptId);
const originalCase=read(producerPath).cases.find(c=>c.id==='ask-before-classification');
assert.equal(sha(originalCase.inputPath),originalCase.inputSha256);
const original=read(originalCase.inputPath), resumed=read(core.nextInputPath);
assert.deepEqual(original.items,resumed.items);assert.equal(resumed.schema_version,'standard-input/8.0.0');
const results=[read(first.resultPath),read(next.resultPath)];
assert.equal(results[0].outputs.some(o=>o.kind==='card-bundle'),false);
const outputEvidence=[];let cards;
for(const result of results){
  assert.equal(result.worker.version,'0.12.0');
  for(const ref of result.outputs){
    const manifestPath=path.join(originalCase.jobRoot,ref.manifestPath);
    assert.equal(sha(manifestPath),ref.expectedSha256.toLowerCase());
    outputEvidence.push(bound(manifestPath));
    const manifest=read(manifestPath);
    for(const file of manifest.files){
      const payloadPath=path.join(originalCase.jobRoot,file.path);
      assert.equal(sha(payloadPath),file.sha256.toLowerCase());assert.equal(fs.statSync(payloadPath).size,file.bytes);
      outputEvidence.push(bound(payloadPath));
      if(ref.kind==='card-bundle' && file.path.endsWith('.json')) cards=read(payloadPath);
    }
  }
}
assert.equal(cards.cards.length,2);
const receipt={schema:'kc.core-language-recognition-resume-qualification/1',status:'PASS',createdAt:new Date().toISOString(),
  evidence:[producerPath,corePath,matrixPath,firstReplayPath,nextReplayPath,core.initialRequestPath,first.resultPath,core.nextRequestPath,next.resultPath,core.nextInputPath].map(bound),
  outputEvidence,source:originalCase.completeSource,selected:originalCase.selected,taskId:initialRequest.taskId,
  initial:{runId:initialRequest.runId,attemptId:initialRequest.attemptId,state:'unresolved',calls:first.calls.map(c=>c.stage),cardBundle:false},
  resume:{runId:nextRequest.runId,attemptId:nextRequest.attemptId,state:'ready',calls:next.calls.map(c=>c.stage),cardCount:2,cards:cards.cards},
  sourceItemsUnchanged:true,initialInputHashUnchanged:true,reclassificationCallsOnResume:0,negativeCases:core.rejected,
  intermediatePendingResolvedBy:bound(nextReplayPath),
  boundary:{classification:'strict-fake fixture, not live model quality',consumer:'formal Content 0.12.0 production common loader/runWorker',workerExeInvoked:false,realProviderCalls:0,exports:0,speaker:0,actualDesktopE2E:false}};
const output=path.join(root,'language-recognition-resume-qualification.json');
fs.writeFileSync(output,JSON.stringify(receipt,null,2)+'\n',{flag:'wx'});
console.log(JSON.stringify({status:'PASS',receipt:bound(output)}));
