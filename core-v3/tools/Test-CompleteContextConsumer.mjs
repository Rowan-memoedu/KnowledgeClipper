import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';

// Offline only: exercise the immutable consumer's actual private source loader
// and exported validators, without calling its worker/provider entrypoint.
const [app, producerPath, receiptPath] = process.argv.slice(2);
assert(app && producerPath && receiptPath, 'app producer-receipt output-receipt required');
const digest = value => createHash('sha256').update(value).digest('hex');
const json = async p => JSON.parse(await readFile(p, 'utf8'));
const util = await import(pathToFileURL(path.join(app, 'dist/src/util.js')));
const { loadContracts } = await import(pathToFileURL(path.join(app, 'dist/src/contracts.js')));
const { validateClassification } = await import(pathToFileURL(path.join(app, 'dist/src/gates.js')));
const workerSource = await readFile(path.join(app, 'dist/src/worker.js'), 'utf8');
const start = workerSource.indexOf('async function loadTextSource(');
const end = workerSource.indexOf('async function callStage(', start);
assert(start >= 0 && end > start);
const loaderSource = workerSource.slice(start, end);
const loadTextSource = new Function('readFile', 'packagePath', 'segmentText', 'readJson',
  `${loaderSource}\nreturn loadTextSource;`)(readFile, util.packagePath, util.segmentText, json);
const producer = await json(producerPath);
assert.equal(producer.status, 'PASS');
const cases = [];
const registry = { categories: [{ id: 'general_learning' }] };
const classify = (segments, answer) => ({ units: [{ unit_id: 'unit-1', source_segment_indexes: segments.map(x => x.segment_index),
  category_id: 'general_learning', subtype: 'basic_qa', answer_state: 'provided', context_state: 'not_applicable',
  risk_state: 'ordinary', supplied_answer: answer, unresolved_reasons: [] }] });
for (const sample of producer.cases) {
  const request = await json(sample.workerRequestPath);
  const manifest = path.join(sample.jobRoot, request.inputRefs[0].manifestPath);
  assert.equal(digest(await readFile(manifest)), request.inputRefs[0].expectedSha256);
  const contracts = await loadContracts(manifest, sample.inputPath, sample.jobRoot);
  const input = await json(sample.inputPath);
  const source = await loadTextSource(input.items[0], contracts);
  assert.equal(source.text, sample.prefix + sample.selected + sample.suffix);
  assert.equal(digest(source.text), sample.completeSourceSha256);
  assert(source.text.includes(input.items[0].language_selection.target_text));
  assert.equal(input.languagePracticeMode, sample.choice === 'ask' ? undefined : sample.choice);
  assert.equal(input.items[0].hints, undefined);
  assert.equal(input.items[0].human_overrides, undefined);
  const active = source.segments.map(x => x.segment_index);
  validateClassification(classify(source.segments, source.text), source.segments, active, registry);
  assert.throws(() => validateClassification(classify(source.segments, `${source.text} invented answer`), source.segments, active, registry), /classification.source-answer/);
  cases.push({ id: sample.id, sourceText: source.text, sourceSha256: digest(source.text), sourceLoaderPassed: true,
    contractPassed: true, preferenceNotClassification: true, exactProvidedAnswerPassed: true, wrongProvidedAnswerRejected: true });
}
const original = producer.cases.find(x => x.id === 'original-failure');
assert(original);
const oldSource = original.prefix + '\n\n' + original.suffix;
const oldSegments = util.segmentText(oldSource, 'old-context.txt', 'text');
assert.throws(() => validateClassification(classify(oldSegments, 'A deterministic external sentence with context.'),
  oldSegments, oldSegments.map(x => x.segment_index), registry), /classification.source-answer/);
const bindings = [];
for (const relative of ['dist/src/contracts.js', 'dist/src/gates.js', 'dist/src/worker.js', 'dist/src/util.js']) {
  const bytes = await readFile(path.join(app, relative)); bindings.push({ path: path.join(app, relative), bytes: bytes.length, sha256: digest(bytes) });
}
await writeFile(receiptPath, JSON.stringify({ status: 'PASS', createdAt: new Date().toISOString(), producerReceipt: { path: producerPath,
  sha256: digest(await readFile(producerPath)) }, consumerFiles: bindings, sourceLoaderSha256: digest(loaderSource), cases,
  originalFailureStillRejected: true, gateChanged: false, promptChanged: false, workerInvocations: 0, externalCalls: 0 }, null, 2), { flag: 'wx' });
console.log(`PASS sealed Content loader/contracts/gates; ${cases.length} complete sources; preference not classification; wrong answers rejected; externalCalls=0`);
