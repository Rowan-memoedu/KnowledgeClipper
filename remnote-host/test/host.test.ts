import {describe,it,expect,vi} from 'vitest';
import express from 'express';
import {createServer} from 'node:http';
import {WebSocket} from 'ws';
import {PluginTransport} from '../src/transport.js';
import {registerKnowledgeClipperHttp} from '../src/knowledge-clipper-http.js';
const hello={type:'hello',protocol:'knowledge-clipper.transport.v1',pluginId:'knowledge-clipper',capabilities:['knowledge-clipper.sdk.v1']};
const plan={schemaVersion:'knowledge-clipper.remnote.v1',sourceId:'new',rootTitle:'零碎知识剪藏',documentPath:['摘录'],cards:[{key:'one',kind:'plain',front:[{kind:'text',text:'test'}],back:[]}]};
const headers={'Content-Type':'application/json','X-KnowledgeClipper-Protocol':'1'};
const pause=()=>new Promise(done=>setTimeout(done,25));
async function connect(port:number){const ws=new WebSocket(`ws://127.0.0.1:${port}`);await new Promise(done=>ws.once('open',done));return ws;}
describe('standalone SDK transport',()=>{
 it('keeps browser message-level liveness alive beyond its 90 second idle timeout',async()=>{
  vi.useFakeTimers({toFake:['setInterval','clearInterval']});
  const host=new PluginTransport();const port=await host.start(0);const ws=await connect(port);let ticks=0;
  ws.on('message',raw=>{const message=JSON.parse(raw.toString());if(message.type==='ping'){ticks++;ws.send(JSON.stringify({type:'pong'}));}});
  ws.send(JSON.stringify(hello));await pause();
  try{for(let tick=1;tick<=8;tick++){await vi.advanceTimersByTimeAsync(15000);await pause();expect(ticks).toBe(tick);expect(host.isConnected()).toBe(true);}}
  finally{ws.terminate();await host.stop();vi.useRealTimers();}
 });
 it('requires dedicated hello and dispatches only SDK actions',async()=>{
  const host=new PluginTransport();const port=await host.start(0);const ws=await connect(port);
  try{expect(host.isConnected()).toBe(false);ws.send(JSON.stringify(hello));await pause();expect(host.hasBridgeCapability('knowledge-clipper.sdk.v1')).toBe(true);
   ws.on('message',raw=>{const req=JSON.parse(raw.toString());ws.send(JSON.stringify({id:req.id,result:{action:req.action}}));});
   expect(await host.sendRequest('knowledge_clipper_health',{})).toEqual({action:'knowledge_clipper_health'});
   expect(await host.sendRequest('knowledge_clipper_excerpt_documents',{rootRemId:'test-root'})).toEqual({action:'knowledge_clipper_excerpt_documents'});
   await expect(host.sendRequest('tools/call',{})).rejects.toThrow('Unsupported');
  }finally{ws.terminate();await host.stop();}
 });
 it('rejects a second plugin without replacing active session',async()=>{
  const host=new PluginTransport();const port=await host.start(0);const a=await connect(port);a.send(JSON.stringify(hello));await pause();const b=await connect(port);
  try{const closed=new Promise(done=>b.once('close',done));b.send(JSON.stringify(hello));await closed;expect(host.isConnected()).toBe(true);}finally{a.terminate();b.terminate();await host.stop();}
 });
 it('does not retry ambiguous writes after disconnect or timeout',async()=>{
  const host=new PluginTransport();const port=await host.start(0);const ws=await connect(port);ws.send(JSON.stringify(hello));await pause();
  try{await expect(host.sendRequest('knowledge_clipper_export',{},10)).rejects.toThrow('unknown');const pending=host.sendRequest('knowledge_clipper_export',{});const rejected=expect(pending).rejects.toThrow('unknown');ws.close();await rejected;}finally{ws.terminate();await host.stop();}
 });
});
describe('standalone HTTP boundaries',()=>{
 it('keeps readback pure, newSource explicit, migration separate and browser calls denied',async()=>{
  const calls:{action:string,payload:any}[]=[];const transport={isConnected:()=>true,hasBridgeCapability:()=>true,sendRequest:async(action:string,payload:any)=>{calls.push({action,payload});return {cards:[]};}};
  const app=express();const server=createServer(app);await new Promise<void>(done=>server.listen(0,'127.0.0.1',done));const port=(server.address() as any).port;registerKnowledgeClipperHttp(app,transport,port,[]);
  const post=(path:string,body:unknown,extra={})=>fetch(`http://127.0.0.1:${port}/sdk/knowledge-clipper/${path}`,{method:'POST',headers:{...headers,...extra},body:JSON.stringify(body)});
  try{
   expect((await post('readback',plan)).status).toBe(200);expect(calls.pop()).toEqual({action:'knowledge_clipper_readback',payload:plan});
   expect((await post('export',plan)).status).toBe(200);expect(calls.pop()?.payload).toEqual(plan);
   expect((await post('export',{plan,newSource:true})).status).toBe(200);expect(calls.pop()?.payload).toEqual({plan,newSource:true});
   expect((await post('export',{plan,newSource:false})).status).toBe(400);
   expect((await post('export',plan,{Origin:'https://evil.example'})).status).toBe(403);
   expect((await post('export',plan,{'X-KnowledgeClipper-Protocol':''})).status).toBe(403);
   const adopt={plan,expected:{rootRemId:'root',documentRemId:'doc',cards:[{key:'one',remIds:['rem'],childRemIds:[],cardIds:[]}]},verifiedNodes:[{remId:'rem'}]};
   expect((await post('admin/adopt',adopt)).status).toBe(200);expect(calls.pop()).toEqual({action:'knowledge_clipper_adopt',payload:adopt});
   expect(calls).toHaveLength(0);
  }finally{server.closeAllConnections();await new Promise<void>(done=>server.close(()=>done()));}
 });
});
