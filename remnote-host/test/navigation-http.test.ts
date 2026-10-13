import {it,expect} from 'vitest';
import express from 'express';
import {createServer} from 'node:http';
import {registerKnowledgeClipperHttp} from '../src/knowledge-clipper-http.js';

it('navigation has explicit identity, capability and browser boundaries; inspection never exports',async()=>{
  const calls:string[]=[];let supported=true;
  const transport={isConnected:()=>true,hasBridgeCapability:()=>supported,sendRequest:async(action:string)=>{calls.push(action);return {revealed:true};}};
  const app=express(),server=createServer(app);await new Promise<void>(done=>server.listen(0,'127.0.0.1',done));
  const port=(server.address() as any).port;registerKnowledgeClipperHttp(app,transport,port,[]);
  const root=`http://127.0.0.1:${port}/sdk/knowledge-clipper/`;
  const headers={'Content-Type':'application/json','X-KnowledgeClipper-Protocol':'1'};
  const body={plan:{schemaVersion:'knowledge-clipper.remnote.v1',sourceId:'navigation',rootTitle:'零碎知识剪藏',documentPath:['摘录'],cards:[{key:'one',kind:'plain',front:[{kind:'text',text:'public'}],back:[]}]},
    expected:{rootRemId:'root',documentRemId:'doc',cards:[{key:'one',remIds:['front'],childRemIds:[],cardIds:[]}]}};
  const post=(value:unknown,extra={})=>fetch(root+'reveal',{method:'POST',headers:{...headers,...extra},body:JSON.stringify(value)});
  try{
    expect((await post(body)).status).toBe(200);expect(calls).toEqual(['knowledge_clipper_reveal']);
    expect((await fetch(root+'reveal-state',{headers})).status).toBe(200);expect(calls.at(-1)).toBe('knowledge_clipper_reveal_state');
    expect((await post({plan:body.plan})).status).toBe(400);
    expect((await post(body,{Origin:'https://example.org'})).status).toBe(403);
    supported=false;expect((await post(body)).status).toBe(503);expect(calls).toHaveLength(2);
  }finally{await new Promise<void>(done=>server.close(()=>done()));}
});
