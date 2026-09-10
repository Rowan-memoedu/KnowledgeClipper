'use strict';
const SelectionHook = require('selection-hook');
const hook = new SelectionHook();
const emit = (event, data) => process.stdout.write(JSON.stringify({ event, data }) + '\n');
const once = process.argv.includes('--once');
const check = process.argv.includes('--check');
hook.on('error', () => emit('error', '取词组件出错'));
// The host owns clipboard fallback so every format and concurrent copy can be guarded.
// The .NET observer owns input epochs. Native automatic retrieval suppresses input
// events while UIA is busy, so it cannot be the authority for snapshot validity.
if (!hook.start({ enableClipboard: false, selectionPassiveMode: true, enableMouseMoveEvent: false })) {
  emit('error', '无法启动取词组件'); process.exitCode = 1;
} else if (check) {
  // Read-only dependency probe: start the real native add-on without collecting
  // any selected text or enabling its clipboard fallback.
  emit('ready', { nativeStarted: true, clipboardFallback: false });
  hook.cleanup();
} else if (once) {
  emit('selection', hook.getCurrentSelection()); hook.cleanup();
} else {
  emit('error', '请通过 --once 按需取词，或通过 --check 检查组件。');
  hook.cleanup(); process.exitCode = 1;
}
