import {build} from 'esbuild';
import path from 'node:path';
const output=path.resolve(process.env.KC_CHAT_OUTPUT||'D:/Artifacts/KnowledgeClipper/chat-view');
await build({entryPoints:['src/mermaid-runtime.ts'],outfile:path.join(output,'mermaid-runtime.js'),bundle:true,
  minify:true,platform:'browser',format:'iife',target:'es2020',legalComments:'eof'});
