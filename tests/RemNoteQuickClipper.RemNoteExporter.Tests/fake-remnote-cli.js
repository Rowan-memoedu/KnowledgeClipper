const args = process.argv.slice(2);
const commands = new Set(['status', 'read', 'list-children', 'create']);
const command = args.find(value => commands.has(value));

if (command === 'status') {
  process.stdout.write(JSON.stringify({ connected: true, acceptWriteOperations: true }));
} else if (command === 'read') {
  process.stdout.write(JSON.stringify({
    remId: 'root-rem-id',
    title: '剪藏',
    headline: '剪藏',
    remType: 'document',
    parentRemId: null,
    contentStructured: []
  }));
} else if (command === 'list-children') {
  process.stdout.write(JSON.stringify({ children: [], hasMore: false, nextCursor: null }));
} else if (command === 'create') {
  process.stderr.write('simulated connection loss after possible commit');
  process.exitCode = 7;
} else {
  process.stderr.write('unsupported fake command');
  process.exitCode = 2;
}
