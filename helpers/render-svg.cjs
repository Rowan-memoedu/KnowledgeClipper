// Static SVG only. The caller validates XML and disallows external resources.
const { Resvg } = require('@resvg/resvg-js');
const chunks = [];
let length = 0;
process.stdin.on('data', chunk => {
  length += chunk.length;
  if (length > 2 * 1024 * 1024) { process.stderr.write('SVG exceeds 2 MiB'); process.exit(1); }
  chunks.push(chunk);
});
process.stdin.on('end', () => {
  try {
    const input = Buffer.concat(chunks);
    const options = { font: { loadSystemFonts: true } };
    const probe = new Resvg(input, options);
    const { width, height } = probe;
    if (!Number.isFinite(width) || !Number.isFinite(height) || width <= 0 || height <= 0)
      throw new Error('Invalid SVG size');
    const scale = Math.min(2, 4096 / Math.max(width, height), Math.sqrt(16000000 / (width * height)));
    const targetWidth = Math.floor(width * scale);
    if (targetWidth < 1 || height * scale < 1) throw new Error('Invalid SVG aspect ratio');
    const svg = new Resvg(input, { ...options, fitTo: { mode: 'width', value: targetWidth } });
    if (svg.imagesToResolve().length) throw new Error('External SVG resources are disabled');
    process.stdout.write(svg.render().asPng());
  } catch (error) { process.stderr.write('SVG render failed'); process.exitCode = 1; }
});
