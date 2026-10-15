import { readFileSync, readdirSync, existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';

const lock=JSON.parse(readFileSync('package-lock.json','utf8'));
const sections=['KnowledgeClipper local chat view: third-party package notices.\nPackage versions are pinned in package-lock.json.'];
for(const [path,item] of Object.entries(lock.packages).sort(([a],[b])=>a.localeCompare(b))) {
  if(!path||item.dev||!existsSync(path))continue;
  const pkg=JSON.parse(readFileSync(resolve(path,'package.json'),'utf8'));
  const files=readdirSync(path).filter(name=>/^(license|licence|notice|copying)(\.|$)/i.test(name));
  sections.push(`\n--- ${pkg.name} ${pkg.version} (${pkg.license||item.license||'see package'}) ---\n`+
    files.map(file=>file+'\n'+readFileSync(resolve(path,file),'utf8')).join('\n'));
}
const output=resolve(process.env.KC_CHAT_OUTPUT||'D:/Artifacts/KnowledgeClipper/chat-view');
mkdirSync(output,{recursive:true});writeFileSync(resolve(output,'THIRD-PARTY-NOTICES.txt'),sections.join('\n'));
console.log(`Included notices for ${sections.length-1} runtime dependency packages.`);
