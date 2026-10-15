const esbuild = require('esbuild');
const fs = require('node:fs/promises');
const path = require('node:path');
const { parseManifest } = require('@remnote/plugin-sdk/dist/lib');
async function main() {
 const manifest=parseManifest(JSON.parse(await fs.readFile(path.join(__dirname,'public','manifest.json'),'utf8')));
 if(!manifest.success) throw new Error('Invalid RemNote manifest: '+JSON.stringify(manifest.errors));
 const output=path.resolve(process.env.KC_PLUGIN_OUTPUT || 'D:/Artifacts/KnowledgeClipper/standalone-remnote/plugin');
 await fs.mkdir(output,{recursive:true});
 for (const name of ['index','status']) for (const sandbox of [false,true]) {
  await esbuild.build({entryPoints:[`src/widgets/${name}.tsx`],outfile:path.join(output,`${name}${sandbox?'-sandbox':''}.js`),bundle:true,minify:true,platform:'browser',format:'iife',target:'es2020',define:{'process.env.NODE_ENV':'"production"'},banner:sandbox?{}:{js:'const IMPORT_META=import.meta;'}});
 }
 for (const file of ['manifest.json','index.html']) await fs.copyFile(path.join('public',file),path.join(output,file));
 await esbuild.build({entryPoints:['src/transport-worker.ts'],outfile:path.join(output,'transport-worker.js'),bundle:true,minify:true,platform:'browser',format:'iife',target:'es2020'});
 console.log(`Plugin build complete: ${output}`);
}
main().catch(error=>{console.error(error);process.exitCode=1;});
