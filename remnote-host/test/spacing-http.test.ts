import {describe,it,expect} from 'vitest';
import express from 'express';
import {createServer} from 'node:http';
import {registerKnowledgeClipperHttp,CLIPPER_SPACING_FINALIZE_CAPABILITY} from '../src/knowledge-clipper-http.js';

const plan={schemaVersion:'knowledge-clipper.remnote.v1',sourceId:'spacing',rootTitle:'零碎知识剪藏',documentPath:['摘录'],
  cards:[{key:'plain.0',kind:'plain',front:[{kind:'text',text:'public text'}],back:[]}]};
const headers={'Content-Type':'application/json','X-KnowledgeClipper-Protocol':'1'};
const node=(id:string,text:any[])=>({remId:id,parentId:'doc',text,backText:[],children:[],remType:0,isDocument:false,
  isCardItem:false,isExtraCardDetail:false,practiceDirection:'none',practiceEnabled:false,cardIds:[],cardTypes:[]});
const state=()=>({schemaVersion:plan.schemaVersion,sourceId:plan.sourceId,rootRemId:'root',documentRemId:'doc',
  cards:[{key:'plain.0',kind:'plain',remIds:['front'],childRemIds:[],cardIds:[],nodes:[node('front',[{i:'m',text:'public text'}])]}],
  batchSeparator:{remId:'blank',previousRemId:'front',node:{...node('blank',[]),practiceEnabled:true}},
  documentChildren:['blank','front'],batchSeparatorPending:true,spacingRecovery:{eligible:true,kind:'finalize-batch-separator'}});
const expected={rootRemId:'root',documentRemId:'doc',cards:[{key:'plain.0',remIds:['front'],childRemIds:[],cardIds:[]}]};

async function fixture(){
  const calls:{action:string;payload:any}[]=[];let value:any=state(),capability=true;
  const transport={isConnected:()=>true,hasBridgeCapability:(name:string)=>name!==CLIPPER_SPACING_FINALIZE_CAPABILITY||capability,
    sendRequest:async(action:string,payload:any)=>{
      calls.push({action,payload});
      if(action==='knowledge_clipper_finalize_spacing'){
        value.batchSeparator.node.practiceEnabled=false;value.documentChildren=['front',value.batchSeparator.remId];
        delete value.batchSeparatorPending;delete value.spacingRecovery;
      }
      return structuredClone(value);
    }};
  const app=express(),server=createServer(app);await new Promise<void>(done=>server.listen(0,'127.0.0.1',done));
  const port=(server.address() as any).port;registerKnowledgeClipperHttp(app,transport,port,[]);
  return {calls,get value(){return value;},capability:(enabled:boolean)=>{capability=enabled;},
    post:(route:string,body:unknown)=>fetch(`http://127.0.0.1:${port}/sdk/knowledge-clipper/${route}`,{method:'POST',headers,body:JSON.stringify(body)}),
    close:async()=>{server.closeAllConnections();await new Promise<void>(done=>server.close(()=>done()));}};
}
describe('same-identity spacing recovery HTTP boundary',()=>{
  it('returns pending readback without any automatic mutation',async()=>{
    const f=await fixture();try{
      const response=await f.post('readback',plan);expect(response.status).toBe(200);
      expect(await response.json()).toMatchObject({batchSeparatorPending:true,batchSeparator:{remId:'blank'}});
      expect(f.calls.map(call=>call.action)).toEqual(['knowledge_clipper_readback']);
    }finally{await f.close();}
  });
  it('pins content and blank identities plus the complete raw snapshot before explicit finalization',async()=>{
    const f=await fixture();try{
      const response=await f.post('finalize-spacing',{plan,expected,separatorRemId:'blank'});
      expect(response.status).toBe(200);const result:any=await response.json();
      expect(result.batchSeparator.remId).toBe('blank');expect(result.batchSeparatorPending).toBeUndefined();
      expect(f.calls.map(call=>call.action)).toEqual(['knowledge_clipper_readback','knowledge_clipper_finalize_spacing']);
      const sent=f.calls[1].payload;expect(sent.expected).toEqual(expected);expect(sent.separatorRemId).toBe('blank');
      expect(sent.documentChildren).toEqual(['blank','front']);expect(sent.verifiedNodes).toHaveLength(2);
      expect(sent.verifiedNodes[1]).toMatchObject({remId:'blank',text:[],practiceEnabled:true});
    }finally{await f.close();}
  });
  for(const changed of ['source','card','blank','schema'] as const)it(`rejects changed ${changed} before the finalization action`,async()=>{
    const f=await fixture();try{
      if(changed==='source')f.value.sourceId='other';if(changed==='schema')f.value.schemaVersion='other';
      if(changed==='card')f.value.cards[0].remIds=['other'];if(changed==='blank')f.value.batchSeparator.remId='other';
      expect((await f.post('finalize-spacing',{plan,expected,separatorRemId:'blank'})).status).toBe(409);
      expect(f.calls.map(call=>call.action)).toEqual(['knowledge_clipper_readback']);
    }finally{await f.close();}
  });
  it('requires the updated plugin capability and rejects unbounded payloads',async()=>{
    const f=await fixture();try{
      f.capability(false);expect((await f.post('finalize-spacing',{plan,expected,separatorRemId:'blank'})).status).toBe(503);
      f.capability(true);expect((await f.post('finalize-spacing',{plan,expected,separatorRemId:'blank',createRem:true})).status).toBe(400);
      expect(f.calls).toHaveLength(0);
    }finally{await f.close();}
  });
});
