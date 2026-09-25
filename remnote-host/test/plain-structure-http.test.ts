import {describe,it,expect} from 'vitest';
import express from 'express';
import {createServer} from 'node:http';
import {registerKnowledgeClipperHttp,CLIPPER_PLAIN_STRUCTURE_CAPABILITY} from '../src/knowledge-clipper-http.js';

const plan={schemaVersion:'knowledge-clipper.remnote.v1',sourceId:'plain-structure',rootTitle:'零碎知识剪藏',documentPath:['摘录'],
  cards:[{key:'plain.0',kind:'plain',front:[{kind:'text',text:'public text'}],back:[]}]};
const headers={'Content-Type':'application/json','X-KnowledgeClipper-Protocol':'1'};
const node=(id:string,text:any[],document=false)=>({remId:id,parentId:'doc',text,backText:[],rawBackText:[],backTextState:'present',
  children:[],remType:0,rawRemType:null,isDocument:document,isCardItem:false,isExtraCardDetail:false,isMultiLineCard:false,
  practiceDirection:'forward',practiceEnabled:false,cardIds:[],cardTypes:[]});
const state=()=>({schemaVersion:plan.schemaVersion,sourceId:plan.sourceId,rootRemId:'root',documentRemId:'doc',
  cards:[{key:'plain.0',kind:'plain',remIds:['front'],childRemIds:[],cardIds:[],nodes:[node('front',[{i:'m',text:'public text'}])]}],
  directoryNodes:[{...node('root',['零碎知识剪藏'],true),parentId:null,children:['doc']},
    {...node('doc',['摘录'],true),parentId:'root',children:['front','blank']}],
  batchSeparator:{remId:'blank',previousRemId:'front',node:node('blank',[])},documentChildren:['front','blank']});
const expected={rootRemId:'root',documentRemId:'doc',cards:[{key:'plain.0',remIds:['front'],childRemIds:[],cardIds:[]}]};

async function fixture(){
  const calls:{action:string;payload:any}[]=[];let value:any=state(),capability=true;
  const transport={isConnected:()=>true,hasBridgeCapability:(name:string)=>name!==CLIPPER_PLAIN_STRUCTURE_CAPABILITY||capability,
    sendRequest:async(action:string,payload:any)=>{
      calls.push({action,payload});
      if(action==='knowledge_clipper_repair_plain_structure')for(const n of [...value.directoryNodes,value.batchSeparator.node])
        if(payload.targetRemIds.includes(n.remId)){n.rawBackText=null;n.backTextState='absent';n.practiceDirection='none';}
      return structuredClone(value);
    }};
  const app=express(),server=createServer(app);await new Promise<void>(done=>server.listen(0,'127.0.0.1',done));
  const port=(server.address() as any).port;registerKnowledgeClipperHttp(app,transport,port,[]);
  return {calls,get value(){return value;},capability:(enabled:boolean)=>{capability=enabled;},
    post:(route:string,body:unknown)=>fetch(`http://127.0.0.1:${port}/sdk/knowledge-clipper/${route}`,{method:'POST',headers,body:JSON.stringify(body)}),
    close:async()=>{server.closeAllConnections();await new Promise<void>(done=>server.close(()=>done()));}};
}
const request=(value:any)=>({plan,expected,targetRemIds:['root','doc','blank'],expectedStructureNodes:[...value.directoryNodes,value.batchSeparator.node]});

describe('explicit same-ID ordinary structure repair HTTP boundary',()=>{
  it('leaves raw empty-back and direction evidence untouched in pure readback',async()=>{
    const f=await fixture();try{
      const response=await f.post('readback',plan);expect(response.status).toBe(200);
      expect((await response.json()).batchSeparator.node).toMatchObject({backTextState:'present',rawBackText:[],practiceDirection:'forward'});
      expect(f.calls.map(c=>c.action)).toEqual(['knowledge_clipper_readback']);
    }finally{await f.close();}
  });
  it('pins all content and structure snapshots before the bounded repair action',async()=>{
    const f=await fixture();try{
      const response=await f.post('repair-plain-structure',request(f.value));expect(response.status).toBe(200);
      expect(f.calls.map(c=>c.action)).toEqual(['knowledge_clipper_readback','knowledge_clipper_repair_plain_structure']);
      expect(f.calls[1].payload.verifiedNodes).toHaveLength(4);
      expect(f.calls[1].payload.verifiedNodes[3]).toMatchObject({remId:'blank',rawBackText:[],backTextState:'present'});
      expect((await response.json()).batchSeparator.node).toMatchObject({remId:'blank',backTextState:'absent',practiceDirection:'none'});
    }finally{await f.close();}
  });
  for(const changed of ['identity','snapshot','duplicate','source','pending','missing-target'] as const)it(`refuses ${changed} before any repair action`,async()=>{
    const f=await fixture();try{
      const req=request(structuredClone(f.value));
      if(changed==='identity')f.value.cards[0].remIds=['another-front'];
      if(changed==='snapshot')f.value.directoryNodes[0].backText=['user answer'];
      if(changed==='duplicate')req.targetRemIds=['root','root','blank'];
      if(changed==='source')f.value.sourceId='other-source';
      if(changed==='pending')f.value.batchSeparatorPending=true;
      if(changed==='missing-target')req.targetRemIds=['missing'];
      expect((await f.post('repair-plain-structure',req)).status).toBe(409);
      expect(f.calls.map(c=>c.action)).toEqual(['knowledge_clipper_readback']);
    }finally{await f.close();}
  });
  it('requires the exact new capability and rejects unbounded options',async()=>{
    const f=await fixture();try{
      f.capability(false);expect((await f.post('repair-plain-structure',request(f.value))).status).toBe(503);
      f.capability(true);expect((await f.post('repair-plain-structure',{...request(f.value),createRem:true})).status).toBe(400);
      expect(f.calls).toHaveLength(0);
    }finally{await f.close();}
  });
});
