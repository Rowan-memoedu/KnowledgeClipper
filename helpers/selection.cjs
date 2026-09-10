'use strict';
const SelectionHook = require('selection-hook');
const hook = new SelectionHook();
const emit = (event, data) => process.stdout.write(JSON.stringify({ event, data }) + '\n');
const once = process.argv.includes('--once');
const check = process.argv.includes('--check');
hook.on('error', () => emit('error', '取词组件出错'));
// The host owns clipboard fallback so every format and concurrent copy can be guarded.
if (!hook.start({ enableClipboard: false, selectionPassiveMode: once || check, enableMouseMoveEvent: false })) {
  emit('error', '无法启动取词组件'); process.exitCode = 1;
} else if (check) {
  // Read-only dependency probe: start the real native add-on without collecting
  // any selected text or enabling its clipboard fallback.
  emit('ready', { nativeStarted: true, clipboardFallback: false });
  hook.cleanup();
} else if (once) {
  emit('selection', hook.getCurrentSelection()); hook.cleanup();
} else {
  hook.on('text-selection', data => emit('selection', data));
  hook.on('mouse-down', data => emit('selection-changing', data));
  hook.on('mouse-wheel', data => emit('selection-changing', data));
  hook.on('key-up', data => {
    if (['Shift', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End'].includes(data.uniKey)) {
      emit('selection', hook.getCurrentSelection());
    } else if (['Escape', 'Enter', 'Backspace', 'Delete'].includes(data.uniKey)) emit('selection-changing', data);
  });
  emit('ready', null);
  process.stdin.resume();
  process.stdin.on('end', () => { hook.cleanup(); process.exit(0); });
}
