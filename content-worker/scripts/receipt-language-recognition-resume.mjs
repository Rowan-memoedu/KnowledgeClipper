import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';
import assert from 'node:assert/strict';
import { save } from '../dist/test/language-resume-fixture.js';
const worker=path.resolve(import.meta.dirname,'..');
const release='D:/Artifacts/KnowledgeClipper/content-worker/releases/content.deepseek-0.12.0-win-x64-formal-20260908T054141Z';
const canary='D:/Artifacts/KnowledgeClipper/content-worker/canary/language-recognition-resume-20260908T054217161Z';
const hash=b=>createHash('sha256').update(b).digest('hex');
const evidence=async p=>({path:p,bytes:(await readFile(p)).length,sha256:hash(await readFile(p))});
const manifest=JSON.parse(await readFile(path.join(release,'release-manifest.v1.json'),'utf8'));
for(const f of manifest.files){const b=await readFile(path.join(release,f.path));assert.equal(b.length,f.bytes);assert.equal(hash(b).toUpperCase(),f.sha256.toUpperCase());}
const tests='D:/Artifacts/KnowledgeClipper/content-worker/test-temp/recognition-resume-0.12.0-final-tests.txt';
const log=await readFile(tests,'utf8');assert.match(log,/tests 116/);assert.match(log,/pass 116/);assert.match(log,/fail 0/);
const r=JSON.parse(await readFile(path.join(canary,'receipt.json'),'utf8'));
for(const c of r.results){assert.ok(c.stages.every(s=>c.mode==='recognize'||!['classification','cleaning'].includes(s)));}
const packet={schemaVersion:'content-recognition-resume-admission/1.0.0',workerId:'content.deepseek',workerVersion:'0.12.0',status:'module-offline-qualified',tests:{pass:116,fail:0,log:await evidence(tests)},schemaValidation:{documents:36,status:'PASS',command:`python test/validate_language_resume_fixtures.py ${canary}`},liveCalls:0,exporterCalls:0,release:{root:release,manifest:await evidence(path.join(release,'release-manifest.v1.json')),treeSha256:manifest.payloadTreeSha256,exe:await evidence(path.join(release,'KnowledgeClipper.Content.Worker.exe'))},formalVerification:await evidence('D:/Artifacts/KnowledgeClipper/content-worker/qualification/content.deepseek-0.12.0-win-x64-formal-20260908T054141Z/20260908T054214874Z-settings-snapshot-replay/formal-release-offline-receipt.v1.json'),canary:await evidence(path.join(canary,'receipt.json')),readable:await evidence(path.join(canary,'readable.md')),moduleContracts:await Promise.all(['schemas/content-input.v8.schema.json','schemas/language-practice-resume.v1.schema.json','docs/LANGUAGE-RECOGNITION-RESUME-V1.md','fixtures/language-recognition-resume/cases.json','fixtures/language-recognition-resume/actual-t050622-classification.json'].map(p=>evidence(path.join(worker,p)))),integration:{operation:'understand-and-card',firstInput:'standard-input/6.0.0',resumeInput:'standard-input/8.0.0',resumeField:'language_practice_resume',refs:['input_ref','learning_ref','decision_ref'],choice:'human_overrides.languagePracticeMode',commonContractUnchanged:true,frozenAuthorityUnmodified:true,coreDesktopAdmission:'pending owner integration; no Actual run by Content'}};
const out=path.join(canary,'module-admission.json');await save(out,packet);console.log(JSON.stringify(await evidence(out)));
