import {expect,it} from 'vitest';
import {WebSocket} from 'ws';
import {PluginTransport} from '../src/transport.js';
import express from 'express';
import {createServer} from 'node:http';
import {registerKnowledgeClipperHttp} from '../src/knowledge-clipper-http.js';
const hello={type:'hello',protocol:'knowledge-clipper.transport.v1',pluginId:'knowledge-clipper',capabilities:['knowledge-clipper.sdk.v1']};
it('waits for the original plugin handshake and schedules bounded navigation wakes on Node',async()=>{
 const host=new PluginTransport(),port=await host.start(0);
 const waiting=host.waitUntilConnected(1000);
 const ws=new WebSocket(`ws://127.0.0.1:${port}`);await new Promise(done=>ws.once('open',done));
 try{
  ws.send(JSON.stringify(hello));expect(await waiting).toBe(true);
  const reply=new Promise<any>(done=>ws.once('message',raw=>done(JSON.parse(String(raw)))));
  ws.send(JSON.stringify({type:'wait',id:'background-editor',milliseconds:20}));
  expect(await reply).toEqual({type:'wait-result',id:'background-editor'});
  const closed=new Promise(done=>ws.once('close',done));
  ws.send(JSON.stringify({type:'wait',id:'unbounded',milliseconds:999999}));await closed;
  expect(await host.waitUntilConnected(20)).toBe(false);
 }finally{ws.terminate();await host.stop();}
});
it('connection wait is cancelled when the host stops',async()=>{
 const host=new PluginTransport();await host.start(0);const waiting=host.waitUntilConnected(60000);await host.stop();expect(await waiting).toBe(false);
});
it('health recovers a reconnect race and distinguishes SDK nonresponse from disconnection or disabled writes',async()=>{
 let connected=false,fail=false;const actions:string[]=[];
 const transport={isConnected:()=>connected,hasBridgeCapability:()=>true,
  waitUntilConnected:async()=>{connected=true;return true;},
  sendRequest:async(action:string)=>{actions.push(action);if(fail)throw new Error('SDK response timed out');return {acceptWriteOperations:false};}};
 const app=express(),server=createServer(app);await new Promise<void>(done=>server.listen(0,'127.0.0.1',done));
 const port=(server.address() as any).port;registerKnowledgeClipperHttp(app,transport,port,[]);
 const health=()=>fetch(`http://127.0.0.1:${port}/sdk/knowledge-clipper/health`,{headers:{'X-KnowledgeClipper-Protocol':'1'}}).then(r=>r.json());
 try{
  expect(await health()).toMatchObject({connected:true,supported:true,sdkResponsive:true,acceptWriteOperations:false});
  fail=true;const result=await health();expect(result).toMatchObject({connected:true,supported:true,sdkResponsive:false,sdkError:'SDK response timed out'});
  expect(actions).toEqual(['knowledge_clipper_health','knowledge_clipper_health']);
 }finally{server.closeAllConnections();await new Promise<void>(done=>server.close(()=>done()));}
});
