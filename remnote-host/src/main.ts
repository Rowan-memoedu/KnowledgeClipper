import express from 'express';
import { createServer } from 'node:http';
import { resolve } from 'node:path';
import { readFile } from 'node:fs/promises';
import { PluginTransport } from './transport.js';
import { registerKnowledgeClipperHttp } from './knowledge-clipper-http.js';

const configPath=process.argv[2];
if(!configPath)throw new Error('Usage: node dist/main.js <config.json>');
const config=JSON.parse(await readFile(configPath,'utf8'));
if(!Array.isArray(config.mediaRoots)||!config.mediaRoots.length||!config.pluginDirectory)throw new Error('mediaRoots and pluginDirectory required');
const transport=new PluginTransport();
await transport.start(43112);
const app=express();app.disable('x-powered-by');
registerKnowledgeClipperHttp(app,transport,43111,config.mediaRoots);
const api=createServer(app);
const staticApp=express();staticApp.disable('x-powered-by');
staticApp.use((req,res,next)=>{res.setHeader('Access-Control-Allow-Origin','*');res.setHeader('Access-Control-Allow-Headers','baggage, sentry-trace');res.setHeader('Access-Control-Allow-Methods','GET, HEAD, OPTIONS');res.setHeader('Cache-Control','no-store');if(req.method==='OPTIONS'){res.sendStatus(204);return;}next();});
staticApp.use(express.static(resolve(config.pluginDirectory),{dotfiles:'deny'}));
const staticServer=createServer(staticApp);
try{await Promise.all([new Promise<void>((done,fail)=>{api.once('error',fail);api.listen(43111,'127.0.0.1',done);}),new Promise<void>((done,fail)=>{staticServer.once('error',fail);staticServer.listen(43113,'127.0.0.1',done);})]);}
catch(error){api.close();staticServer.close();await transport.stop();throw error;}
console.log(JSON.stringify({event:'ready',service:'knowledge-clipper-remnote-host',http:43111,websocket:43112,plugin:43113}));
const stop=async()=>{api.close();staticServer.close();await transport.stop();process.exit(0);};
process.on('SIGINT',stop);process.on('SIGTERM',stop);
