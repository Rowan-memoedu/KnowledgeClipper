import {ImportNavigation,NAVIGATION_CAPABILITY} from './api/import-navigation';
import type {ReactRNPlugin} from '@remnote/plugin-sdk';
import {verifyEmptyBackEditor} from './testing/plain-structure-canary';
import {KnowledgeClipperAdapter, KNOWLEDGE_CLIPPER_CAPABILITY, KNOWLEDGE_CLIPPER_MEDIA_CAPABILITY, KNOWLEDGE_CLIPPER_SPACING_CAPABILITY, KNOWLEDGE_CLIPPER_SPACING_FINALIZE_CAPABILITY, KNOWLEDGE_CLIPPER_PLAIN_STRUCTURE_CAPABILITY} from './api/knowledge-clipper';
export const CAPABILITIES=[KNOWLEDGE_CLIPPER_CAPABILITY,KNOWLEDGE_CLIPPER_MEDIA_CAPABILITY,KNOWLEDGE_CLIPPER_SPACING_CAPABILITY,KNOWLEDGE_CLIPPER_SPACING_FINALIZE_CAPABILITY,KNOWLEDGE_CLIPPER_PLAIN_STRUCTURE_CAPABILITY,'knowledge-clipper.identity-adopt.v1','knowledge-clipper.excerpt-documents.v1'];
CAPABILITIES.push(NAVIGATION_CAPABILITY);
export const STATUS_KEY='knowledge-clipper.connection-status';
export const WRITE_SETTING='knowledge-clipper-enable-import';
export const WS_URL='ws://127.0.0.1:43112';
export function createDispatcher(plugin:ReactRNPlugin) {
 let writable=false;
 const adapter=new KnowledgeClipperAdapter(plugin,()=>writable);
 const navigation=new ImportNavigation(plugin,adapter);
 return async (action:string,payload:unknown) => {
  writable=await plugin.settings.getSetting<boolean>(WRITE_SETTING)===true;
  switch(action) {
   case 'knowledge_clipper_health':return {connected:true,supported:true,acceptWriteOperations:writable,capabilities:CAPABILITIES,pluginId:'knowledge-clipper',version:'1.0.0',mediaFinalizeSupported:true,identityAdoptSupported:true,batchSpacingSupported:true,spacingFinalizeSupported:true,plainStructureSupported:true};
   case 'knowledge_clipper_excerpt_documents':return adapter.listExcerptDocuments(payload as {rootRemId?:string});
   case 'knowledge_clipper_reveal':return navigation.reveal(payload);
   case 'knowledge_clipper_reveal_state':return navigation.state();
   case 'knowledge_clipper_export':return adapter.export(payload);
   case 'knowledge_clipper_readback':return adapter.readback(payload);
   case 'knowledge_clipper_prepare_image':return adapter.prepareImage(payload);
   case 'knowledge_clipper_finalize_media':return adapter.finalizeMedia(payload);
   case 'knowledge_clipper_finalize_spacing':return adapter.finalizeSpacing(payload);
   case 'knowledge_clipper_repair_plain_structure':return adapter.repairPlainStructure(payload);
   case 'knowledge_clipper_test_plain_structure':if(!writable)throw new Error('KnowledgeClipper writes are disabled');return verifyEmptyBackEditor(plugin,adapter,payload);
   case 'knowledge_clipper_adopt':return adapter.adopt(payload);
   default:throw new Error('Unsupported KnowledgeClipper action');
  }
 };
}
export function startRuntime(plugin:ReactRNPlugin) {
 const dispatch=createDispatcher(plugin);
 let socket:WebSocket|undefined, timer:ReturnType<typeof setTimeout>|undefined, stopped=false, delay=1000, lastSeen=Date.now();
 const status=(value:string)=>{void plugin.storage.setSession(STATUS_KEY,value).catch(()=>undefined);};
 const connect=()=>{
  if(stopped)return;
  status('正在连接剪藏服务');
  const ws=socket=new WebSocket(WS_URL);
  ws.onopen=()=>{delay=1000;lastSeen=Date.now();status('已连接');ws.send(JSON.stringify({type:'hello',protocol:'knowledge-clipper.transport.v1',pluginId:'knowledge-clipper',version:'1.0.0',capabilities:CAPABILITIES}));};
  ws.onmessage=async event=>{
   lastSeen=Date.now();
   let message:any;
   try{message=JSON.parse(String(event.data));}catch{return;}
   if(message.type==='ping'){ws.send(JSON.stringify({type:'pong'}));return;}
   if(typeof message.id!=='string'||typeof message.action!=='string')return;
   try{const result=await dispatch(message.action,message.payload);if(ws.readyState===WebSocket.OPEN)ws.send(JSON.stringify({id:message.id,result}));}
   catch(error){if(ws.readyState===WebSocket.OPEN)ws.send(JSON.stringify({id:message.id,error:error instanceof Error?error.message:String(error)}));}
  };
  ws.onerror=()=>ws.close();
  ws.onclose=()=>{status('未连接，自动重试中');if(!stopped){timer=setTimeout(connect,delay);delay=Math.min(delay*2,30000);}};
 };
 const heartbeat=setInterval(()=>{if(socket?.readyState===WebSocket.OPEN && Date.now()-lastSeen>90000)socket.close();},30000);
 connect();
 return ()=>{stopped=true;clearTimeout(timer);clearInterval(heartbeat);socket?.close();status('已停用');};
}
