import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';

// Import the exact formal release common adapter. The only substituted boundary
// is provider creation in this isolated test process. No package or module edits.
const [app, requestPath, category = 'language_zh'] = process.argv.slice(2);
const json = async p => JSON.parse(await readFile(p, 'utf8'));
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const request = await json(requestPath);
const root = requestPath.slice(0, -(request.attemptPath.replaceAll('/', path.sep).length + '/request.json'.length));
assert.equal(path.resolve(root, request.attemptPath, 'request.json'), path.resolve(requestPath));
const input = await json(path.join(root, request.payload.contentInputPath));
const item = input.items[0];
const selection = item.language_selection;
const source = await readFile(path.join(root, selection.context_segments[0].material_path), 'utf8');
const target = selection.target_text;
const resumed = input.schema_version === 'standard-input/8.0.0';
const calls = [];
const providerModule = await import(pathToFileURL(path.join(app, 'dist/src/provider.js')));
providerModule.DeepSeekProvider.create = async () => ({ call: async call => {
  const { signal, ...evidence } = call;
  calls.push(evidence);
  assert(!resumed || !['cleaning', 'classification'].includes(call.stage), 'Resume reclassified immutable source');
  let response;
  if (call.stage === 'cleaning') response = { cleaned_text: source, removed_fragments: [], format_anomalies: [] };
  else if (call.stage === 'classification') {
    assert.deepEqual(call.payload.language_selection, selection, 'Verified language selection was not forwarded to classification');
    response = { units: [{ unit_id: 'fixture-unit', source_segment_indexes: [0], category_id: category, subtype: 'word',
      answer_state: 'not_applicable', context_state: 'provided', risk_state: 'ordinary', target_term: target, unresolved_reasons: [] }] };
  } else if (call.stage === 'card_generation') {
    const back = `Fixture explanation of ${target}.`;
    assert.deepEqual(call.payload.requested_templates, ['language_context']);
    response = { generated_content: [{ kind: 'explanation', text: back }], unresolved_reasons: [], cards: [{ template: 'language_context', answer_provenance: 'not_applicable',
      front_blocks: [{ type: 'text', text: source, provenance: 'source' }], back_blocks: [{ type: 'text', text: back, provenance: 'generated' }] }] };
  } else if (call.stage === 'language_writing') response = {
    pronunciation: { value: 'fixture pronunciation', reliability: 'reliable', evidence: 'Explicit offline fixture, not a real pronunciation assertion' },
    meaning: { value: 'fixture meaning', reliability: 'reliable', evidence: 'Explicit offline fixture' }, unresolved_reasons: [] };
  else throw new Error(`Unexpected provider stage ${call.stage}`);
  return { response, mode: 'replay', provider: 'strict-offline-boundary', model: 'none', attempts: 1 };
} });
const { runCommonRequest } = await import(pathToFileURL(path.join(app, 'dist/src/common-adapter.js')));
const result = await runCommonRequest(requestPath, app);
const evidencePath = path.join(root, request.attemptPath, 'core-formal-provider-replay.json');
await writeFile(evidencePath, JSON.stringify({ status: result.status, moduleReleaseState: result.payload?.moduleReleaseState, requestPath,
  requestSha256: digest(await readFile(requestPath)), resultPath: path.join(root, request.resultPath), resultSha256: digest(await readFile(path.join(root, request.resultPath))),
  formalCommonAdapter: path.join(app, 'dist/src/common-adapter.js'), formalCommonAdapterSha256: digest(await readFile(path.join(app, 'dist/src/common-adapter.js'))),
  fakeProviderBoundary: true, realProviderCalls: 0, workerExeInvoked: false, classificationIsFixtureNotLiveFact: true, calls }, null, 2), { flag: 'wx' });
assert.equal(result.status, 'completed', JSON.stringify(result.error));
assert.equal(result.payload.moduleReleaseState, resumed ? 'ready' : 'unresolved');
assert.deepEqual(calls.map(x => x.stage), resumed ? ['card_generation', 'language_writing'] : ['cleaning', 'classification']);
console.log(`PASS formal common loader ${resumed ? 'resume' : 'recognize'}; live=0; evidence=${evidencePath}`);
