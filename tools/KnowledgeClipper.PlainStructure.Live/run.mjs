import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash, randomUUID } from 'node:crypto';
import assert from 'node:assert/strict';

const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, value, index, all) => {
  if (value.startsWith('--')) pairs.push([value.slice(2), all[index + 1]]);
  return pairs;
}, []));
const mode = args.mode ?? 'prepare';
const runRoot = path.resolve(args['run-root'] ?? 'D:/Caches/KnowledgeClipper/plain-structure-live-20260919');
const endpoint = new URL(args.endpoint ?? 'http://127.0.0.1:43111/sdk/knowledge-clipper/');
assert.equal(endpoint.hostname, '127.0.0.1', 'Only the standalone loopback API is allowed');
assert.equal(endpoint.port, '43111', 'Do not route through the retired MCP host');
assert.ok(runRoot.toLowerCase().startsWith('d:\\'), 'Keep live fixtures on D:');
const fields = ['remId','parentId','text','backText','rawBackText','backTextState','children','remType','rawRemType',
  'isDocument','isCardItem','isExtraCardDetail','isMultiLineCard','practiceDirection','practiceEnabled','cardIds','cardTypes'];
const oldFields = ['remId','parentId','text','backText','children','remType','isDocument','isCardItem',
  'isExtraCardDetail','practiceDirection','practiceEnabled','cardIds','cardTypes'];
const pick = (node, keys = fields) => Object.fromEntries(keys.map(key => [key, node[key]]));
const identity = value => ({rootRemId:value.rootRemId, documentRemId:value.documentRemId,
  cards:value.cards.map(card => ({key:card.key, remIds:card.remIds, childRemIds:card.childRemIds, cardIds:card.cardIds}))});
const contentSnapshot = value => value.cards.flatMap(card => card.nodes.map(node => pick(node, oldFields)));
const allNodes = value => [...value.cards.flatMap(card => card.nodes), ...value.directoryNodes, value.batchSeparator.node];
const json = value => JSON.stringify(value, null, 2) + '\n';
const readJson = async file => JSON.parse((await fs.readFile(file, 'utf8')).replace(/^\uFEFF/, ''));
const writeJson = async (file, value) => fs.writeFile(file, json(value));
const stripNull = value => Array.isArray(value) ? value.map(stripNull) : value && typeof value === 'object'
  ? Object.fromEntries(Object.entries(value).filter(([, entry]) => entry !== null).map(([key, entry]) => [key, stripNull(entry)])) : value;
const hash = value => createHash('sha256').update(value).digest('hex');
await fs.mkdir(runRoot, {recursive:true});
const attempt = path.join(runRoot, 'attempts', `${new Date().toISOString().replace(/[-:.]/g, '')}-${randomUUID().slice(0, 8)}`);
await fs.mkdir(attempt, {recursive:true});
let requestCount = 0;
async function request(route, body) {
  const index = String(++requestCount).padStart(2, '0');
  await writeJson(path.join(attempt, `${index}-${route}-request.json`), body ?? {method:'GET'});
  const response = await fetch(new URL(route, endpoint), {
    method: body === undefined ? 'GET' : 'POST',
    headers:{'X-KnowledgeClipper-Protocol':'1', 'Content-Type':'application/json'},
    ...(body === undefined ? {} : {body:JSON.stringify(body)}), signal:AbortSignal.timeout(180_000),
  });
  const raw = await response.text();
  await fs.writeFile(path.join(attempt, `${index}-${route}-response.json`), raw);
  assert.ok(response.ok, `${route}: HTTP ${response.status}: ${raw}`);
  return JSON.parse(raw);
}
function ordinary(node) {
  assert.ok(['absent','null'].includes(node.backTextState), `${node.remId}: optional card back must be absent or null`);
  assert.equal(node.rawBackText, null);
  assert.deepEqual(node.backText, []);
  assert.equal(node.practiceDirection, 'none', `${node.remId}: card direction must be none`);
  assert.equal(node.practiceEnabled, false);
  assert.equal(node.remType, 0);
  for (const key of ['isCardItem','isExtraCardDetail','isMultiLineCard']) assert.equal(node[key], false, `${node.remId}: ${key}`);
  assert.deepEqual(node.cardIds, []);
  assert.deepEqual(node.cardTypes, []);
}
function stableStructure(before, after, targetIds) {
  assert.deepEqual(identity(after), identity(before));
  assert.equal(after.batchSeparator.remId, before.batchSeparator.remId);
  assert.deepEqual(after.documentChildren, before.documentChildren);
  assert.deepEqual(contentSnapshot(after), contentSnapshot(before));
  const retained = fields.filter(key => !['backText','rawBackText','backTextState','practiceDirection','practiceEnabled'].includes(key));
  assert.deepEqual(allNodes(after).map(node => pick(node, targetIds.includes(node.remId) ? retained : fields)),
    allNodes(before).map(node => pick(node, targetIds.includes(node.remId) ? retained : fields)));
}
function validateCanary(value, manifest) {
  assert.equal(value.schemaVersion, manifest.plan.schemaVersion);
  assert.equal(value.sourceId, manifest.plan.sourceId);
  assert.ok(!value.batchSeparatorPending && !value.notStarted);
  assert.equal(value.cards.length, 2);
  for (let i = 0; i < value.cards.length; i++) {
    const actual = value.cards[i], planned = manifest.plan.cards[i];
    assert.equal(actual.key, planned.key);
    assert.equal(actual.kind, planned.kind);
    assert.equal(actual.remIds.length, 1);
    assert.deepEqual(actual.nodes[0].parts, planned.front);
    assert.equal(actual.nodes[0].parentId, value.documentRemId);
    assert.deepEqual(actual.nodes[0].children, actual.childRemIds);
    assert.equal(actual.nodes[0].isDocument, false);
    assert.equal(actual.nodes[0].isCardItem, false, 'A top-level front must not be an answer for its directory');
    assert.equal(actual.nodes[0].isMultiLineCard, false, 'A top-level front must not mark its directory as a multiline card');
    assert.equal(actual.childRemIds.length, planned.back.length);
    for (let j = 0; j < planned.back.length; j++) {
      assert.deepEqual(actual.nodes[j + 1].parts, planned.back[j]);
      assert.equal(actual.nodes[j + 1].parentId, actual.remIds[0]);
      assert.equal(actual.nodes[j + 1].isCardItem, true, 'Only answer children form the multiline card');
    }
    assert.equal(actual.cardIds.length, planned.kind === 'basic' ? 1 : 0);
    assert.equal(actual.nodes[0].practiceEnabled, planned.kind === 'basic');
  }
  assert.equal(value.cards[1].nodes[0].practiceDirection, 'forward');
  assert.deepEqual(value.cards[1].nodes[0].cardTypes, ['forward']);
  const blank = value.batchSeparator.node;
  ordinary(blank);
  assert.equal(blank.isDocument, false);
  assert.deepEqual(blank.text, []);
  assert.deepEqual(blank.children, []);
  assert.equal(blank.parentId, value.documentRemId);
  const topIds = value.cards.flatMap(card => card.remIds);
  assert.equal(value.batchSeparator.previousRemId, topIds.at(-1));
  const ordered = [...topIds, blank.remId];
  const index = value.documentChildren.indexOf(ordered[0]);
  assert.ok(index >= 0);
  assert.deepEqual(value.documentChildren.slice(index, index + ordered.length), ordered);
  for (const id of ordered) assert.equal(value.documentChildren.filter(value => value === id).length, 1);
  assert.equal(value.directoryNodes.length, manifest.plan.documentPath.length + 1);
  // The last two directory names are newly allocated by this frozen fixture.
  for (const directory of value.directoryNodes.slice(-2)) {
    ordinary(directory);
    assert.equal(directory.isDocument, true);
  }
  if (manifest.identity) assert.deepEqual(identity(value), manifest.identity);
  if (manifest.separatorRemId) assert.equal(blank.remId, manifest.separatorRemId);
}
async function health(requireWrite=true) {
  const value = await request('health');
  assert.equal(value.connected, true);
  if(requireWrite) {
    assert.equal(value.plainStructureSupported, true);
    assert.equal(value.acceptWriteOperations, true);
  }
  return value;
}
async function canary() {
  const file = path.join(runRoot, 'manifest.json');
  let manifest;
  try { manifest = await readJson(file); }
  catch (error) {
    if (error.code !== 'ENOENT' || mode !== 'prepare') throw error;
    const id = randomUUID().replaceAll('-', '');
    const marker = `KnowledgeClipper public plain-structure test ${id}`;
    manifest = {version:1, status:'prepared', writeAttempts:0, marker, plan:{schemaVersion:'knowledge-clipper.remnote.v1',
      sourceId:`knowledgeclipper.plain-structure-canary.${id}`, rootTitle:'零碎知识剪藏',
      documentPath:['验收', `普通节点-${id.slice(0, 10)}`, '新增目录'], cards:[
        {key:'plain.0', kind:'plain', front:[{kind:'text', text:`${marker}\nA triangle has three sides.`}], back:[]},
        {key:'basic.0', kind:'basic', front:[{kind:'text', text:`${marker}\nWhat is 2 + 3?`}],
          back:[[{kind:'text', text:'The sum is 5.'}], [{kind:'text', text:'Adding three to two gives five.'}]]},
      ]}};
    await fs.writeFile(file, json(manifest), {flag:'wx'});
  }
  if (mode === 'prepare') return {status:'prepared', manifest:file, sourceId:manifest.plan.sourceId};
  assert.ok(['run','readback','editor-canary'].includes(mode));
  await health();
  if(mode==='editor-canary') {
    assert.ok(manifest.identity&&manifest.separatorRemId,'The public fixture must already have persisted IDs');
    const probe=await request('test-plain-structure',{plan:manifest.plan,expected:manifest.identity,separatorRemId:manifest.separatorRemId});
    assert.equal(probe.editorRestored,true);
    validateCanary(probe.result,manifest);
    return {status:'editor-verified',sourceId:manifest.plan.sourceId,identity:manifest.identity,separatorRemId:manifest.separatorRemId,editorRestored:true};
  }
  let value;
  if (manifest.status === 'prepared') {
    assert.equal(mode, 'run', 'A prepared source has no live identity to read');
    manifest.status = 'unknown'; manifest.writeAttempts++;
    await writeJson(file, manifest); // Durable intent precedes the first and only new-source request.
    value = await request('export', {plan:manifest.plan, newSource:true});
  } else {
    value = await request('readback', manifest.plan);
    if (value.notStarted) {
      assert.equal(mode, 'run');
      assert.ok(!manifest.identity, 'Known content identities cannot become a new attempt');
      manifest.status = 'unknown'; manifest.writeAttempts++;
      await writeJson(file, manifest);
      value = await request('export', {plan:manifest.plan});
    }
  }
  if (!manifest.identity && value.cards?.length) {
    manifest.identity = identity(value);
    manifest.separatorRemId = value.batchSeparator?.remId;
    await writeJson(file, manifest); // Preserve identities even if later assertions fail.
  }
  validateCanary(value, manifest);
  const second = await request('readback', manifest.plan);
  validateCanary(second, manifest);
  assert.deepEqual(identity(second), identity(value));
  assert.equal(second.batchSeparator.remId, value.batchSeparator.remId);
  manifest.status = 'verified'; manifest.lastVerifiedAttempt = attempt;
  await writeJson(file, manifest);
  return {status:'verified', sourceId:manifest.plan.sourceId, identity:manifest.identity,
    separatorRemId:manifest.separatorRemId, newDirectoryRemIds:second.directoryNodes.slice(-2).map(node => node.remId),
    contentRemCount:4, reviewCardCount:1, writeAttempts:manifest.writeAttempts, manifest:file};
}
async function repair() {
  assert.ok(args.draft, 'A frozen existing draft path is required');
  const draftPath = path.resolve(args.draft);
  const originalBytes = await fs.readFile(draftPath);
  const draft = JSON.parse(originalBytes.toString('utf8').replace(/^\uFEFF/, ''));
  assert.ok(draft.confirmedRemNote && draft.remNoteResult?.evidence);
  const plan = stripNull(draft.confirmedRemNote), original = draft.remNoteResult.evidence;
  assert.equal(plan.sourceId, original.sourceId);
  assert.ok(original.batchSeparator?.remId, 'Repair requires the original recorded separator');
  const expected = identity(original);
  await health(mode!=='repair-preview');
  const before = await request('readback', plan);
  assert.deepEqual(identity(before), expected, 'Original content IDs must remain unchanged');
  assert.equal(before.batchSeparator.remId, original.batchSeparator.remId);
  assert.deepEqual(contentSnapshot(before), contentSnapshot(original), 'Original content or card settings changed');
  assert.ok(!before.batchSeparatorPending);
  const structureNodes = [...before.directoryNodes, before.batchSeparator.node];
  const targetNodes = structureNodes.filter(node => node.backTextState==='present');
  const alreadyWithoutBack = structureNodes.filter(node => node.backTextState!=='present').map(node => ({remId:node.remId,backTextState:node.backTextState,reason:'no existing card back to remove'}));
  const targetRemIds = targetNodes.map(node => node.remId);
  const prepared = {plan, expected, targetRemIds, expectedStructureNodes:targetNodes.map(node => pick(node))};
  await writeJson(path.join(attempt, 'repair-candidate.json'), prepared);
  await writeJson(path.join(attempt, 'original-draft-evidence.json'), {draftPath, sha256:hash(originalBytes),
    sourceId:plan.sourceId, expected, separatorRemId:before.batchSeparator.remId, targetRemIds});
  if (mode === 'repair-preview') return {status:'preview', sourceId:plan.sourceId, targetRemIds,alreadyWithoutBack};
  assert.equal(mode, 'repair');
  if(!targetRemIds.length)return {status:'already-without-card-back',sourceId:plan.sourceId,targetRemIds,alreadyWithoutBack};
  assert.equal(hash(await fs.readFile(draftPath)), hash(originalBytes), 'Saved draft changed before repair');
  const after = await request('repair-plain-structure', prepared);
  stableStructure(before, after, targetRemIds);
  for (const node of [...after.directoryNodes, after.batchSeparator.node].filter(node => targetRemIds.includes(node.remId))) ordinary(node);
  const reread = await request('readback', plan);
  stableStructure(before, reread, targetRemIds);
  for (const node of [...reread.directoryNodes, reread.batchSeparator.node].filter(node => targetRemIds.includes(node.remId))) ordinary(node);
  assert.equal(hash(await fs.readFile(draftPath)), hash(originalBytes), 'Saved draft was modified');
  return {status:'repaired-same-ids', sourceId:plan.sourceId, targetRemIds,alreadyWithoutBack, separatorRemId:after.batchSeparator.remId,
    identity:identity(after), contentRemCount:after.cards.reduce((sum, card) => sum + card.nodes.length, 0),
    reviewCardCount:after.cards.reduce((sum, card) => sum + card.cardIds.length, 0)};
}
try {
  const result = mode.startsWith('repair') ? await repair() : await canary();
  await writeJson(path.join(attempt, 'receipt.json'), {...result, attempt});
  console.log(json({...result, attempt}));
} catch (error) {
  await writeJson(path.join(attempt, 'failure.json'), {mode, error:String(error), stack:error.stack, attempt});
  console.error(json({status:'failed', mode, error:String(error), attempt}));
  process.exitCode = 1;
}
