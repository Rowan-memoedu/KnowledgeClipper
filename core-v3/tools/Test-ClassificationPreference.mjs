import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';

const [app, producerPath, receiptPath] = process.argv.slice(2);
assert(app && producerPath && receiptPath);
const json = async p => JSON.parse(await readFile(p, 'utf8'));
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const { runWorker } = await import(pathToFileURL(path.join(app, 'dist/src/worker.js')));
const producer = await json(producerPath);
const cases = [];
for (const sample of producer.cases) {
  const language = sample.choice === 'ask';
  const calls = [];
  const provider = { call: async request => {
    calls.push(request.stage);
    let response;
    if (request.stage === 'cleaning') response = { cleaned_text: sample.completeSource, removed_fragments: [], format_anomalies: [] };
    else if (request.stage === 'classification') response = { units: [{ unit_id: 'fixture-unit', source_segment_indexes: [0],
      category_id: language ? 'language_zh' : 'general_learning', subtype: language ? 'word' : 'concept',
      answer_state: language ? 'not_applicable' : 'provided', context_state: 'provided', risk_state: 'ordinary', unresolved_reasons: [],
      ...(language ? { target_term: sample.selected } : { supplied_answer: sample.completeSource }) }] };
    else if (request.stage === 'card_generation') {
      assert(!language, 'Language ask must return a decision before generating cards');
      assert.deepEqual(request.payload.requested_templates, ['basic_qa'], 'Both must not inject language templates into a non-language category');
      response = { generated_content: [{ kind: 'question_wording', text: 'Recall the source statement.' }], unresolved_reasons: [], cards: [{
        template: 'basic_qa', answer_provenance: 'source', front_blocks: [{ type: 'text', text: 'Recall the source statement.', provenance: 'generated' }],
        back_blocks: [{ type: 'text', text: sample.completeSource, provenance: 'source' }] }] };
    } else throw new Error(`Unexpected offline provider stage ${request.stage}`);
    return { response, mode: 'replay', provider: 'strict-offline-fixture', model: 'none', attempts: 1 };
  } };
  const outputDir = path.join(path.dirname(producerPath), 'classification-consumer', sample.id);
  await runWorker({ manifestPath: path.join(sample.jobRoot, 'inputs/content/artifact.json'), inputPath: sample.inputPath,
    jobRoot: sample.jobRoot, outputDir, attemptId: `offline-${sample.id}`, operation: 'understand-and-card',
    registryPath: path.join(app, 'config/categories.v2.json'), promptRoot: path.join(app, 'prompts'), languagePracticeDefault: 'ask' }, provider);
  const learningPath = path.join(outputDir, 'learning-bundle.schema-v1.json');
  const learning = await json(learningPath);
  assert.equal(learning.release_state, language ? 'unresolved' : 'ready');
  if (language) {
    assert.deepEqual(calls, ['cleaning', 'classification']);
    const decision = await json(path.join(outputDir, 'language-practice-decision.schema-v2.json'));
    assert(decision.decisions[0].reasons.includes('language_practice_mode_required'));
    assert.equal(decision.schema_version, 'language-practice-decision/2.0.0');
  } else {
    const cards = await json(path.join(outputDir, 'card-bundle.schema-v1.json'));
    assert.deepEqual(cards.cards.map(x => x.template), ['basic_qa']);
  }
  cases.push({ id: sample.id, scriptedCategory: language ? 'language_zh' : 'general_learning', calls, moduleReleaseState: learning.release_state,
    learningPath, learningSha256: sha(await readFile(learningPath)), classificationIsFixtureNotLiveFact: true });
}
await writeFile(receiptPath, JSON.stringify({ status: 'PASS', cases, producerPath, producerSha256: sha(await readFile(producerPath)),
  consumerWorkerPath: path.join(app, 'dist/src/worker.js'), consumerWorkerSha256: sha(await readFile(path.join(app, 'dist/src/worker.js'))),
  realProviderCalls: 0, externalWrites: 0, actualRerun: false, scope: 'offline consumer routing, not real language classification' }, null, 2), { flag: 'wx' });
console.log('PASS classification first: non-language Both -> basic_qa; language ask -> real decision artifact; no provider calls');
