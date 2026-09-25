import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';
import { createHash } from 'node:crypto';
import { request } from 'node:http';
import { mkdir, mkdtemp, writeFile, rm } from 'node:fs/promises';
import { join, resolve, relative, isAbsolute } from 'node:path';
import { pathToFileURL } from 'node:url';
import express from 'express';
import { createServer } from 'node:http';
import { registerKnowledgeClipperHttp } from '../src/knowledge-clipper-http.js';
import type { ClipperTransport as WebSocketServer } from '../src/transport.js';
import { CLIPPER_MEDIA_CAPABILITY, ClipPlanSchema, enrichClipperMedia } from '../src/knowledge-clipper-http.js';
import { HARD_MAX_INLINE_BYTES, HARD_MAX_MANAGED_IMAGE_HASH_BYTES, resolveManagedImage } from '../src/media.js';



const createMockLogger=()=>({});
async function getAvailablePort(){const server=createServer();await new Promise<void>(done=>server.listen(0,'127.0.0.1',done));const port=(server.address() as any).port;await new Promise<void>(done=>server.close(()=>done()));return port;}
class TestHttpServer {private server:any; constructor(private port:number,_host:string,private ws:any,_meta:any,_log:any,private roots:string[]=[]){ } async start(){const app=express();registerKnowledgeClipperHttp(app,this.ws,this.port,this.roots);this.server=createServer(app);await new Promise<void>(done=>this.server.listen(this.port,'127.0.0.1',done));} async stop(){this.server.closeAllConnections();await new Promise<void>(done=>this.server.close(()=>done()));}}
const plan=()=>({schemaVersion:'knowledge-clipper.remnote.v1',sourceId:'canary',rootTitle:'零碎知识剪藏',
  documentPath:['摘录'],cards:[{key:'plain.0',kind:'plain',front:[{kind:'text',text:'test\nsecond'}],back:[]}]});
const headers={'Content-Type':'application/json','X-KnowledgeClipper-Protocol':'1'};
describe('native KnowledgeClipper SDK HTTP endpoints',()=>{
  let server:TestHttpServer, port:number, ws:any;
  beforeEach(async()=>{
    port=await getAvailablePort();ws={isConnected:vi.fn(()=>true),hasBridgeCapability:vi.fn(()=>true),
      sendRequest:vi.fn(async(action:string)=>action==='knowledge_clipper_health'?{acceptWriteOperations:true}:{schemaVersion:'knowledge-clipper.remnote.v1',cards:[]})};
    server=new TestHttpServer(port,'127.0.0.1',ws as WebSocketServer,{name:'test',version:'0.17.0'},createMockLogger());
    await server.start();
  });
  afterEach(async()=>{await server.stop();});
  const post=(port:number,body:unknown,extra:Record<string,string>={})=>fetch(`http://127.0.0.1:${port}/sdk/knowledge-clipper/export`,
    {method:'POST',headers:{...headers,...extra},body:JSON.stringify(body)});
  it('uses the same WebSocket SDK bridge with no MCP initialization or tools/call',async()=>{
    const result=await post(port,plan());expect(result.status).toBe(200);
    expect(ws.sendRequest).toHaveBeenCalledWith('knowledge_clipper_export',plan(),120000);
    expect((await result.json() as any).mediaPending).toBe(false);
  });
  it('readback dispatches a distinct read-only action',async()=>{
    const result=await fetch(`http://127.0.0.1:${port}/sdk/knowledge-clipper/readback`,{method:'POST',headers,body:JSON.stringify(plan())});
    expect(result.status).toBe(200);
    expect(ws.sendRequest).toHaveBeenCalledWith('knowledge_clipper_readback',plan(),120000);
  });
  it('accepts 31 plain paragraphs and bounds plans at 1000 entries',async()=>{
    const p=plan();p.cards=Array.from({length:31},(_,i)=>({...p.cards[0],key:`plain.${i}`}));
    expect((await post(port,p)).status).toBe(200);
    expect(ws.sendRequest).toHaveBeenCalledWith('knowledge_clipper_export',p,120000);
    p.cards=Array.from({length:1000},(_,i)=>({...p.cards[0],key:`plain.${i}`}));
    expect(ClipPlanSchema.safeParse(p).success).toBe(true);
    p.cards.push({...p.cards[0],key:'plain.1000'});
    expect(ClipPlanSchema.safeParse(p).success).toBe(false);
  });
  it('reports actual bridge readiness without pretending an old plugin supports the feature',async()=>{
    ws.hasBridgeCapability.mockReturnValue(false);
    const status=await fetch(`http://127.0.0.1:${port}/sdk/knowledge-clipper/health`,{headers});
    expect(await status.json()).toMatchObject({connected:true,supported:false,acceptWriteOperations:true,transport:'sdk-http'});
    expect((await post(port,plan())).status).toBe(503);
  });
  it('rejects browser origins, spoofed hosts and missing native protocol header',async()=>{
    expect((await post(port,plan(),{Origin:'https://evil.example'})).status).toBe(403);
    expect((await post(port,plan(),{Origin:'null'})).status).toBe(403);
    const spoofedHostStatus=await new Promise<number|undefined>((resolve,reject)=>{
      const req=request({hostname:'127.0.0.1',port,path:'/sdk/knowledge-clipper/export',method:'POST',
        headers:{...headers,Host:`evil.example:${port}`}},res=>{res.resume();resolve(res.statusCode);});
      req.on('error',reject);req.end(JSON.stringify(plan()));
    });
    expect(spoofedHostStatus).toBe(403);
    expect((await fetch(`http://127.0.0.1:${port}/sdk/knowledge-clipper/health`)).status).toBe(403);
    expect(ws.sendRequest).not.toHaveBeenCalled();
  });
  it('rejects unknown fields, duplicate keys, remote image URLs and missing image hashes',async()=>{
    expect((await post(port,{...plan(),eval:'malicious'})).status).toBe(400);
    const dup=plan();dup.cards.push(dup.cards[0]);expect((await post(port,dup)).status).toBe(400);
    for(const part of [{kind:'image',uri:'https://example.com/a.png',sha256:'0'.repeat(64)}, {kind:'image',uri:'file:///D:/a.png'}]){
      const p:any=plan();p.cards[0].front=[part];expect((await post(port,p)).status).toBe(400);
    }
    expect(ws.sendRequest).not.toHaveBeenCalled();
  });
  it('propagates content drift/partial failures instead of falsely acknowledging import',async()=>{
    ws.sendRequest.mockRejectedValue(new Error('Recorded content differs'));
    const response=await post(port,plan());expect(response.status).toBe(409);
    expect(await response.json()).toMatchObject({error:'Recorded content differs'});
  });
  it('accepts literal text, native LaTeX and linked formula clozes',()=>{
    const p:any=plan();p.cards[0].kind='cloze';p.cards[0].front=[{kind:'latex',text:'x^2',block:false,clozeId:'1',hint:'square'},
      {kind:'text',text:':: >> are literal'}];
    expect(ClipPlanSchema.safeParse(p).success).toBe(true);
  });
});

describe('explicit SDK image upload and media finalization',()=>{
  const base='D:/Caches/RemNoteMCP/tests';
  let root:string,server:TestHttpServer,port:number,ws:any,p:any,state:any,uploaded:number;
  const route=(name:string,body:unknown)=>fetch(`http://127.0.0.1:${port}/sdk/knowledge-clipper/${name}`,
    {method:'POST',headers,body:JSON.stringify(body)});
  const expected=()=>({rootRemId:state.rootRemId,documentRemId:state.documentRemId,cards:state.cards.map((card:any)=>
    ({key:card.key,remIds:card.remIds,childRemIds:card.childRemIds,cardIds:card.cardIds}))});
  beforeEach(async()=>{
    await mkdir(base,{recursive:true});root=await mkdtemp(join(base,'sdk-finalize-'));uploaded=0;
    const images=[];
    for(let i=0;i<4;i++){
      const bytes=Buffer.from(`89504e470d0a1a0a0102030${i}`,'hex'),path=join(root,`图 (${i}) #.png`);
      await writeFile(path,bytes);images.push({kind:'image',uri:pathToFileURL(path).href,sha256:createHash('sha256').update(bytes).digest('hex')});
    }
    p={...plan(),cards:[{key:'images',kind:'basic',front:[{kind:'text',text:'question\nline'},...images.slice(0,2)],
      back:[[{kind:'text',text:'answer'},images[2]],[images[3]]]}]};
    const ids=['front','back-1','back-2'];
    const nodes=[p.cards[0].front,...p.cards[0].back].map((row:any[],i:number)=>({remId:ids[i],parentId:i?'front':'directory',
      text:row.map(part=>part.kind==='image'?{i:'i',url:part.uri}:{i:'m',text:part.text}),backText:[],children:i?[]:ids.slice(1),
      remType:0,rawRemType:null,isDocument:false,isCardItem:true,isExtraCardDetail:false,
      practiceDirection:'forward',practiceEnabled:true,cardIds:i?[]:['forward-card'],cardTypes:i?[]:['forward'],
      parts:row.map(({sha256,...part})=>({...part})),backParts:[]}));
    state={schemaVersion:p.schemaVersion,sourceId:p.sourceId,rootRemId:'root',documentRemId:'directory',
      cards:[{key:'images',kind:'basic',reused:true,remIds:['front'],childRemIds:ids.slice(1),cardIds:['forward-card'],nodes}]};
    ws={isConnected:vi.fn(()=>true),hasBridgeCapability:vi.fn(()=>true),sendRequest:vi.fn(async(action:string,payload:any)=>{
      if(action==='knowledge_clipper_health')return {acceptWriteOperations:true};
      if(action==='knowledge_clipper_readback')return structuredClone(state);
      if(action==='knowledge_clipper_prepare_image'){
        uploaded++;const bytes=Buffer.from(payload.dataUri.split(',')[1],'base64');
        await writeFile(join(root,`uploaded-${uploaded}.png`),bytes);
        return {uri:payload.uri,sha256:payload.sha256,richText:{i:'i',url:`%LOCAL_FILE%uploaded-${uploaded}.png`,title:'official'}};
      }
      if(action==='knowledge_clipper_finalize_media'){
        for(const node of state.cards[0].nodes) for(const part of node.text) if(part.i==='i') {
          const prepared=payload.preparedImages.find((image:any)=>image.uri===part.url);
          if(prepared)part.url=prepared.richText.url;
        }
        for(const node of state.cards[0].nodes)node.parts=node.text.map((part:any)=>part.i==='i'?{kind:'image',uri:part.url}:{kind:'text',text:part.text});
        return structuredClone(state);
      }
      throw new Error(`Unexpected action ${action}`);
    })};
    port=await getAvailablePort();server=new TestHttpServer(port,'127.0.0.1',ws,{name:'test',version:'0.17.0'},createMockLogger(),[root]);
    await server.start();
  });
  afterEach(async()=>{
    await server.stop();const target=resolve(root),rel=relative(resolve(base),target);
    if(!rel||rel.startsWith('..')||isAbsolute(rel))throw new Error('Unsafe test cleanup target');
    await rm(target,{recursive:true,force:true});
  });
  it('keeps readback pure and uploads one verified image per frame before an explicit same-ID finalization',async()=>{
    const read=await route('readback',p),data:any=await read.json();
    expect(data.mediaRecovery).toEqual({eligible:true,kind:'finalize-local-images',pendingImages:4});
    expect(uploaded).toBe(0);expect(ws.sendRequest.mock.calls.map((call:any)=>call[0])).toEqual(['knowledge_clipper_readback']);
    const ids=expected(),response=await route('finalize-media',{plan:p,expected:ids});
    expect(response.status).toBe(200);const result:any=await response.json();
    expect(result.mediaPending).toBe(false);expect(result.mediaRecovery.eligible).toBe(false);expect(uploaded).toBe(4);
    expect(expected()).toEqual(ids);
    const request=ws.sendRequest.mock.calls.find((call:any)=>call[0]==='knowledge_clipper_finalize_media')[1];
    expect(request.plan).toEqual(p);expect(request.plan.cards[0].front[1].uri).toMatch(/^file:/);
    expect(request.verifiedNodes[0].text[1].url).toBe(p.cards[0].front[1].uri);
    expect(request.preparedImages).toHaveLength(4);expect(request.preparedImages[0]).not.toHaveProperty('dataUri');
  });
  it('refuses replaced and swapped source image URIs without any upload or setText action',async()=>{
    for(const mutation of ['replace','swap']){
      const original=structuredClone(state.cards[0].nodes[0].text);
      if(mutation==='replace')state.cards[0].nodes[0].text[1].url=p.cards[0].back[0][1].uri;
      else [state.cards[0].nodes[0].text[1],state.cards[0].nodes[0].text[2]]=[state.cards[0].nodes[0].text[2],state.cards[0].nodes[0].text[1]];
      expect((await (await route('readback',p)).json() as any).mediaRecovery.eligible).toBe(false);
      expect((await route('finalize-media',{plan:p,expected:expected()})).status).toBe(409);
      expect(uploaded).toBe(0);state.cards[0].nodes[0].text=original;
    }
  });
  it('refuses changed source bytes, unsupported images and wrong managed bytes before any upload',async()=>{
    const part=p.cards[0].front[1];
    await writeFile(new URL(part.uri),Buffer.from('89504e470d0a1a0a9999','hex'));
    expect((await route('finalize-media',{plan:p,expected:expected()})).status).toBe(409);expect(uploaded).toBe(0);
    state.cards[0].nodes[0].text[1].url='%LOCAL_FILE%wrong.png';await writeFile(join(root,'wrong.png'),Buffer.from('89504e470d0a1a0a8888','hex'));
    const read:any=await (await route('readback',p)).json();expect(read.mediaRecovery.eligible).toBe(false);
    expect(read.mediaRecovery.error).toContain('Managed image SHA-256 differs');
    expect((await route('finalize-media',{plan:p,expected:expected()})).status).toBe(409);expect(uploaded).toBe(0);
  });
  it('skips already verified images during partial and repeated finalization',async()=>{
    for(let i=1;i<=2;i++){
      const bytes=Buffer.from(`89504e470d0a1a0a0102030${i-1}`,'hex');await writeFile(join(root,`existing-${i}.png`),bytes);
      state.cards[0].nodes[0].text[i].url=`%LOCAL_FILE%existing-${i}.png`;
    }
    const ids=expected(),read:any=await (await route('readback',p)).json();expect(read.mediaRecovery.pendingImages).toBe(2);
    expect((await route('finalize-media',{plan:p,expected:ids})).status).toBe(200);expect(uploaded).toBe(2);
    expect((await route('finalize-media',{plan:p,expected:ids})).status).toBe(200);expect(uploaded).toBe(2);expect(expected()).toEqual(ids);
  });
  it('rejects stale identities and altered flashcard shapes and does not accept caller-supplied image payloads',async()=>{
    expect((await route('finalize-media',{plan:p,expected:{...expected(),rootRemId:'other'}})).status).toBe(409);
    expect((await route('finalize-media',{plan:p,expected:expected(),preparedImages:[]})).status).toBe(400);
    for(const mutation of ['backText','cardType','extraCard','duplicateId','children']){
      const before=structuredClone(state),front=state.cards[0].nodes[0];
      if(mutation==='backText')front.backText=['user answer'];
      if(mutation==='cardType')front.cardTypes=['backward'];
      if(mutation==='extraCard')state.cards[0].nodes[1].cardIds=['extra'];
      if(mutation==='duplicateId')state.cards[0].childRemIds[0]='front';
      if(mutation==='children')front.children=[];
      expect((await route('finalize-media',{plan:p,expected:expected()})).status).toBe(409);state=before;
    }
    expect(uploaded).toBe(0);
  });
  it('requires the new capability for both recovery and fresh image writes while keeping readback read-only',async()=>{
    ws.hasBridgeCapability.mockImplementation((cap:string)=>cap!==CLIPPER_MEDIA_CAPABILITY);
    expect((await route('export',p)).status).toBe(503);expect(ws.sendRequest).not.toHaveBeenCalled();
    expect((await route('finalize-media',{plan:p,expected:expected()})).status).toBe(503);
    const read:any=await (await route('readback',p)).json();expect(read.mediaRecovery.eligible).toBe(false);
    const health:any=await (await fetch(`http://127.0.0.1:${port}/sdk/knowledge-clipper/health`,{headers})).json();
    expect(health).toMatchObject({supported:true,mediaFinalizeSupported:false});expect(uploaded).toBe(0);
  });
  it('prepares all image bytes before a fresh export and preserves the frozen public plan',async()=>{
    const original=ws.sendRequest.getMockImplementation();
    ws.sendRequest.mockImplementation(async(action:string,payload:any)=>{
      if(action==='knowledge_clipper_export')return payload.prepareOnly
        ? {schemaVersion:p.schemaVersion,sourceId:p.sourceId,notStarted:true,requiresImages:true}
        : original('knowledge_clipper_finalize_media',{...payload});
      return original(action,payload);
    });
    const response=await route('export',p);expect(response.status).toBe(200);expect((await response.json() as any).mediaPending).toBe(false);
    expect(uploaded).toBe(4);
    const exports=ws.sendRequest.mock.calls.filter((call:any)=>call[0]==='knowledge_clipper_export');
    expect(exports).toHaveLength(2);expect(exports[1][1].plan).toEqual(p);
    expect(exports[1][1].preparedImages).toHaveLength(4);
  });
  it('does not upload again for an existing export and reports positive pre-content failure when upload fails',async()=>{
    const original=ws.sendRequest.getMockImplementation();
    ws.sendRequest.mockImplementation(async(action:string,payload:any)=>action==='knowledge_clipper_export'?structuredClone(state):original(action,payload));
    expect((await route('export',p)).status).toBe(200);expect(uploaded).toBe(0);
    const checkpoint={schemaVersion:p.schemaVersion,sourceId:p.sourceId,notStarted:true,requiresImages:true};
    ws.sendRequest.mockImplementation(async(action:string)=>{
      if(action==='knowledge_clipper_export'||action==='knowledge_clipper_readback')return checkpoint;
      throw new Error('SDK upload failed');
    });
    const failed=await route('export',p);expect(failed.status).toBe(200);
    expect(await failed.json()).toMatchObject({schemaVersion:p.schemaVersion,sourceId:p.sourceId,notStarted:true,error:'SDK upload failed'});
  });
});

describe('SDK media readback evidence',()=>{
  it('hashes images above the MCP inline limit without returning their bytes or raising that limit',async()=>{
    const base='D:/Caches/RemNoteMCP/tests';await mkdir(base,{recursive:true});const root=await mkdtemp(join(base,'sdk-large-media-'));
    try{
      const bytes=Buffer.alloc(HARD_MAX_INLINE_BYTES+1024,0x35);
      Buffer.from('89504e470d0a1a0a','hex').copy(bytes);
      await writeFile(join(root,'large.png'),bytes);
      const locator={remId:'r',mediaId:'m',kind:'image',field:'text',elementIndex:0,imageIndex:0,source:'remnote_managed_local',localToken:'large.png'};
      await expect(resolveManagedImage(locator,[root],HARD_MAX_INLINE_BYTES)).rejects.toThrow('oversized');
      const value:any={cards:[{nodes:[{remId:'r',text:[{i:'i',url:'%LOCAL_FILE%large.png'}],parts:[{kind:'image',uri:'%LOCAL_FILE%large.png'}]}]}]};
      const result=await enrichClipperMedia(value,[root]);
      expect(result.mediaPending).toBe(false);
      expect(result.cards[0].nodes[0].media[0]).toMatchObject({sha256:createHash('sha256').update(bytes).digest('hex'),sizeBytes:bytes.length,mimeType:'image/png'});
      expect(result.cards[0].nodes[0].media[0]).not.toHaveProperty('data');
      const oversized=Buffer.alloc(HARD_MAX_MANAGED_IMAGE_HASH_BYTES+1);
      Buffer.from('89504e470d0a1a0a','hex').copy(oversized);
      await writeFile(join(root,'large.png'),oversized);
      const rejected=await enrichClipperMedia(value,[root]);
      expect(rejected.mediaPending).toBe(true);
      expect(rejected.cards[0].nodes[0].media[0].error).toContain('oversized');
    }finally{
      const target=resolve(root),rel=relative(resolve(base),target);
      if(!rel || rel.startsWith('..') || isAbsolute(rel))throw new Error('Unsafe test cleanup target');
      await rm(target,{recursive:true,force:true});
    }
  });
  it('hashes the current managed image bytes and preserves semantic order',async()=>{
    const base='D:/Caches/RemNoteMCP/tests';await mkdir(base,{recursive:true});const root=await mkdtemp(join(base,'sdk-media-'));
    try{
      const bytes=Buffer.from('89504e470d0a1a0a01020304','hex');await writeFile(join(root,'actual.png'),bytes);
      const value:any={cards:[{front:[{kind:'image',sha256:'fabricated'}],nodes:[{remId:'rem-1',
        text:[{i:'m',text:'front'},{i:'i',url:'%LOCAL_FILE%actual.png'}],parts:[{kind:'text',text:'front'},{kind:'image',uri:'%LOCAL_FILE%actual.png'}]}]}]};
      const result=await enrichClipperMedia(value,[root]);
      expect(result.mediaPending).toBe(false);
      expect(result.cards[0].front[1].sha256).toBe(createHash('sha256').update(bytes).digest('hex'));
      expect(result.cards[0].nodes[0].media[0].sizeBytes).toBe(bytes.length);
    }finally{
      const target=resolve(root),rel=relative(resolve(base),target);
      if(!rel || rel.startsWith('..') || isAbsolute(rel))throw new Error('Unsafe test cleanup target');
      await rm(target,{recursive:true,force:true});
    }
  });
  it('reports pending, missing and unsafe managed images without echoing the expected SHA',async()=>{
    for(const uri of ['file:///D:/original.png','%LOCAL_FILE%missing.png','%LOCAL_FILE%..%2Fprivate.png']){
      const result=await enrichClipperMedia({cards:[{nodes:[{remId:'r',text:[{i:'i',url:uri}],parts:[{kind:'image',uri}]}]}]},[]);
      expect(result.mediaPending).toBe(true);
      expect(result.cards[0].front[0].sha256).toBeUndefined();
      expect(result.cards[0].nodes[0].media[0].error).toBeTruthy();
    }
  });
});
