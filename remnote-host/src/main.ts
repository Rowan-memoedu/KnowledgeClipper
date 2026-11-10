import express from 'express';
import { createServer } from 'node:http';
import { resolve } from 'node:path';
import { readFile,writeFile,mkdir } from 'node:fs/promises';
import {randomBytes} from 'node:crypto';
import { PluginTransport } from './transport.js';
import { registerKnowledgeClipperHttp } from './knowledge-clipper-http.js';

const configPath=process.argv[2];
if(!configPath)throw new Error('Usage: node dist/main.js <config.json>');
const config=JSON.parse(await readFile(configPath,'utf8'));
if(!Array.isArray(config.mediaRoots)||!config.mediaRoots.length||!config.pluginDirectory)throw new Error('mediaRoots and pluginDirectory required');
const tokenDirectory='D:/AppData/KnowledgeClipper';
await mkdir(tokenDirectory,{recursive:true});
const tokenPath=tokenDirectory+'/remnote-transport-token.json';
let transportToken:string;
try{transportToken=JSON.parse(await readFile(tokenPath,'utf8')).token;}
catch(error){if((error as NodeJS.ErrnoException).code!=='ENOENT')throw error;transportToken=randomBytes(32).toString('hex');await writeFile(tokenPath,JSON.stringify({token:transportToken}),{flag:'wx',mode:0o600});}
const transport=new PluginTransport(transportToken);
await transport.start(43112);
const app=express();app.disable('x-powered-by');
registerKnowledgeClipperHttp(app,transport,43111,config.mediaRoots);
const api=createServer(app);
const staticApp=express();staticApp.disable('x-powered-by');
staticApp.use((req,res,next)=>{res.setHeader('Access-Control-Allow-Origin','*');res.setHeader('Access-Control-Allow-Headers','baggage, sentry-trace');res.setHeader('Access-Control-Allow-Methods','GET, HEAD, OPTIONS');res.setHeader('Cache-Control','no-store');if(req.method==='OPTIONS'){res.sendStatus(204);return;}next();});
staticApp.get(['/','/index.html'],async(req,res)=>{
 res.removeHeader('Access-Control-Allow-Origin');
 res.setHeader('Content-Security-Policy','frame-ancestors app://js https://www.remnote.com https://remnote.com');
 let html=await readFile(resolve(config.pluginDirectory,'index.html'),'utf8');
 if(req.headers['sec-fetch-dest']==='iframe'&&req.headers['sec-fetch-mode']==='navigate'){
  const seed='<script>globalThis.__REMNOTE_TRANSPORT_TOKEN='+JSON.stringify(transportToken).replace(/</g,'\\u003c')+';</script>';
  const index=html.indexOf('<script');html=index>=0?html.slice(0,index)+seed+html.slice(index):html+seed;
 }
 res.type('html').send(html);
});
staticApp.use(express.static(resolve(config.pluginDirectory),{dotfiles:'deny'}));
const staticServer=createServer(staticApp);
const staticIPv6=createServer(staticApp);
try{await Promise.all([new Promise<void>((done,fail)=>{api.once('error',fail);api.listen(43111,'127.0.0.1',done);}),new Promise<void>((done,fail)=>{staticServer.once('error',fail);staticServer.listen(43113,'127.0.0.1',done);}),new Promise<void>((done,fail)=>{staticIPv6.once('error',fail);staticIPv6.listen({port:43113,host:'::1',ipv6Only:true},done);})]);}
catch(error){api.close();staticServer.close();staticIPv6.close();await transport.stop();throw error;}
console.log(JSON.stringify({event:'ready',service:'knowledge-clipper-remnote-host',http:43111,websocket:43112,plugin:43113}));
const stop=async()=>{api.close();staticServer.close();staticIPv6.close();await transport.stop();process.exit(0);};
process.on('SIGINT',stop);process.on('SIGTERM',stop);
