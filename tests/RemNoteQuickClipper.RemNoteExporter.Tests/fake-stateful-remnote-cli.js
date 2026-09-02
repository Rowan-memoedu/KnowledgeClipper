const fs = require('fs');

const args = process.argv.slice(2);
const commands = new Set(['status', 'read', 'list-children', 'create']);
const command = args.find(value => commands.has(value));
const statePath = process.env.REMNOTE_EXPORTER_FAKE_STATE;

if (!statePath) fail('REMNOTE_EXPORTER_FAKE_STATE is required');

function fail(message, code = 2) {
  process.stderr.write(message);
  process.exit(code);
}

function option(name) {
  const index = args.indexOf(name);
  return index >= 0 && index + 1 < args.length ? args[index + 1] : null;
}

function load() {
  if (!fs.existsSync(statePath)) return { createCount: 0, documents: [] };
  return JSON.parse(fs.readFileSync(statePath, 'utf8'));
}

function save(state) {
  fs.writeFileSync(statePath, JSON.stringify(state), { encoding: 'utf8', flag: 'wx' });
}

function replace(state) {
  const temporary = statePath + '.tmp-' + process.pid;
  fs.writeFileSync(temporary, JSON.stringify(state), 'utf8');
  fs.renameSync(temporary, statePath);
}

function parseChildren(markdown) {
  return markdown.split(/\r?\n/).filter(Boolean).map((line, index) => {
    const headline = line.startsWith('- ') ? line.slice(2) : line;
    let cardDirection = null;
    if (headline.includes(' >> ')) cardDirection = 'forward';
    else if (headline.includes(' << ')) cardDirection = 'reverse';
    else if (headline.includes(' <> ')) cardDirection = 'bidirectional';
    return { remId: `fake-child-${index + 1}`, headline, title: headline, remType: 'rem', cardDirection };
  });
}

if (command === 'status') {
  process.stdout.write(JSON.stringify({ connected: true, acceptWriteOperations: true }));
} else if (command === 'read') {
  const id = args[args.indexOf('read') + 1];
  if (id === 'root-rem-id') {
    process.stdout.write(JSON.stringify({ remId: id, title: '剪藏', headline: '剪藏', remType: 'document', parentRemId: null, contentStructured: [] }));
  } else {
    const document = load().documents.find(item => item.remId === id);
    if (!document) fail('fake document not found', 3);
    process.stdout.write(JSON.stringify({ ...document, headline: document.title, remType: 'document', contentStructured: document.children }));
  }
} else if (command === 'list-children') {
  const state = load();
  process.stdout.write(JSON.stringify({
    children: state.documents.map(({ remId, title, parentRemId }) => ({ remId, title, headline: title, remType: 'document', parentRemId })),
    hasMore: false,
    nextCursor: null
  }));
} else if (command === 'create') {
  const title = option('--title');
  const contentFile = option('--content-file');
  const parentRemId = option('--parent-id');
  if (!title || !contentFile || !parentRemId) fail('missing create argument');
  const state = load();
  state.createCount += 1;
  const remId = `fake-document-${state.createCount}`;
  state.documents.push({ remId, title, parentRemId, children: parseChildren(fs.readFileSync(contentFile, 'utf8')) });
  if (fs.existsSync(statePath)) replace(state); else save(state);
  process.stdout.write(JSON.stringify({ remIds: [remId] }));
} else {
  fail('unsupported fake command');
}
