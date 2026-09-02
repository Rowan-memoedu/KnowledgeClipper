const args = process.argv.slice(2);
const commands = new Set(['status', 'search', 'list-children', 'read', 'create']);
const command = args.find(value => commands.has(value));
const root = { remId: 'canary-root-id', title: '零碎知识剪藏 Canary', headline: '零碎知识剪藏 Canary', remType: 'document', parentRemId: null, contentStructured: [] };

if (command === 'status') {
  process.stdout.write(JSON.stringify({ connected: true, acceptWriteOperations: true, serverVersion: 'fake-0.17.0', pluginVersion: 'fake-0.17.0' }));
} else if (command === 'search') {
  process.stdout.write(JSON.stringify({ results: [root], hasMore: false, nextCursor: null }));
} else if (command === 'list-children') {
  process.stdout.write(JSON.stringify({ children: [], hasMore: false, nextCursor: null }));
} else if (command === 'read') {
  process.stdout.write(JSON.stringify(root));
} else if (command === 'create') {
  process.stderr.write('WRITE_FORBIDDEN_IN_PUBLIC_PROBE');
  process.exitCode = 77;
} else {
  process.stderr.write('unsupported fake command');
  process.exitCode = 2;
}
