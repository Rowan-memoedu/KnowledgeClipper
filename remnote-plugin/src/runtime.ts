import {WidgetLocation,type ReactRNPlugin} from '@remnote/plugin-sdk';
import {MAX_RECONNECT_DELAY_MS} from './transport-worker';
import {ImportNavigation,NAVIGATION_CAPABILITY} from './api/import-navigation';
import {MERMAID_CAPABILITY,MERMAID_RECEIPT_PREFIX} from './api/mermaid';
import {verifyEmptyBackEditor} from './testing/plain-structure-canary';
import {KnowledgeClipperAdapter, KNOWLEDGE_CLIPPER_CAPABILITY, KNOWLEDGE_CLIPPER_MEDIA_CAPABILITY, KNOWLEDGE_CLIPPER_SPACING_CAPABILITY, KNOWLEDGE_CLIPPER_SPACING_FINALIZE_CAPABILITY, KNOWLEDGE_CLIPPER_PLAIN_STRUCTURE_CAPABILITY} from './api/knowledge-clipper';
export const CAPABILITIES=[KNOWLEDGE_CLIPPER_CAPABILITY,KNOWLEDGE_CLIPPER_MEDIA_CAPABILITY,KNOWLEDGE_CLIPPER_SPACING_CAPABILITY,KNOWLEDGE_CLIPPER_SPACING_FINALIZE_CAPABILITY,KNOWLEDGE_CLIPPER_PLAIN_STRUCTURE_CAPABILITY,'knowledge-clipper.identity-adopt.v1','knowledge-clipper.excerpt-documents.v1','knowledge-clipper.question-composition.v1'];
CAPABILITIES.push(NAVIGATION_CAPABILITY);
CAPABILITIES.push('knowledge-clipper.background-transport.v1');
CAPABILITIES.push('knowledge-clipper.question-daily-documents.v1');
CAPABILITIES.push(MERMAID_CAPABILITY);
export const STATUS_KEY='knowledge-clipper.connection-status';
export const WRITE_SETTING='knowledge-clipper-enable-import';
export const WS_URL='ws://127.0.0.1:43112';
export function createDispatcher(plugin:ReactRNPlugin,wait?:()=>Promise<void>) {
 let writable=false;
 const adapter=new KnowledgeClipperAdapter(plugin,()=>writable);
 const navigation=new ImportNavigation(plugin,adapter,wait);
 return async (action:string,payload:unknown) => {
  writable=await plugin.settings.getSetting<boolean>(WRITE_SETTING)===true;
  switch(action) {
   case 'knowledge_clipper_health':return {connected:true,supported:true,acceptWriteOperations:writable,capabilities:CAPABILITIES,pluginId:'knowledge-clipper',version:'1.0.2',reconnectMaxDelayMs:MAX_RECONNECT_DELAY_MS,mediaFinalizeSupported:true,identityAdoptSupported:true,batchSpacingSupported:true,spacingFinalizeSupported:true,plainStructureSupported:true};
   case 'knowledge_clipper_excerpt_documents':return adapter.listExcerptDocuments(payload as {rootRemId?:string});
   case 'knowledge_clipper_reveal':return navigation.reveal(payload);
   case 'knowledge_clipper_reveal_state':return navigation.state();
   case 'knowledge_clipper_export':return adapter.export(payload);
   case 'knowledge_clipper_readback':return adapter.readback(payload);
   case 'knowledge_clipper_mermaid_render_state':{
    const readback=await adapter.readNavigation(payload) as {cards:{nodes:{remId:string}[]}[]};
    const ids=readback.cards.flatMap(card=>card.nodes.map(node=>node.remId));
    return {receipts:(await Promise.all(ids.map(id=>plugin.storage.getSession(MERMAID_RECEIPT_PREFIX+id)))).filter(Boolean),
     widgetBoot:await plugin.storage.getSession(MERMAID_RECEIPT_PREFIX+'boot'),
     bindings:await Promise.all(ids.map(async remId=>({remId,widgets:await plugin.widget.getWidgetsAtLocation(WidgetLocation.UnderRemEditor,remId)})))};
   }
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
 const worker=new Worker(new URL('transport-worker.js',plugin.rootURL??globalThis.location.href),{name:'KnowledgeClipper transport'});
 let stopped=false,generation=0,connected=false,nextWait=0;
 const waits=new Map<string,{resolve:()=>void;reject:(error:Error)=>void}>();
 const rejectWaits=()=>{for(const pending of waits.values())pending.reject(new Error('Navigation connection interrupted; imported content is unchanged'));waits.clear();};
 const send=(message:unknown,current=generation)=>worker.postMessage({type:'send',generation:current,message});
 const dispatch=createDispatcher(plugin,()=>new Promise<void>((resolve,reject)=>{
  if(!connected){reject(new Error('Navigation connection is unavailable'));return;}
  const id='navigation-wait-'+(++nextWait);waits.set(id,{resolve,reject});
  // Node schedules this wakeup. Hidden-page timer clamping cannot consume the
  // navigation deadline before the editor has had a chance to settle.
  send({type:'wait',id,milliseconds:75});
 }));
 const status=(value:string)=>{void plugin.storage.setSession(STATUS_KEY,value).catch(()=>undefined);};
 worker.onmessage=async event=>{
   if(stopped)return;
   const envelope=event.data;
   if(envelope.type==='status'){
    generation=envelope.generation;connected=envelope.value==='connected';
    if(!connected)rejectWaits();
    status(connected?'已连接':envelope.value==='connecting'?'正在连接剪藏服务':'未连接，自动重试中');return;
   }
   if(envelope.type!=='message'||envelope.generation!==generation)return;
   const message=envelope.message,current=generation;
   if(message.type==='wait-result'){const pending=waits.get(message.id);waits.delete(message.id);pending?.resolve();return;}
   if(typeof message.id!=='string'||typeof message.action!=='string')return;
   try{const result=await dispatch(message.action,message.payload);if(!stopped)send({id:message.id,result},current);}
   catch(error){if(!stopped)send({id:message.id,error:error instanceof Error?error.message:String(error)},current);}
 };
 worker.onerror=()=>{connected=false;rejectWaits();status('后台连接组件加载失败，请重新加载剪藏插件');};
 worker.postMessage({type:'start',hello:{type:'hello',protocol:'knowledge-clipper.transport.v1',pluginId:'knowledge-clipper',version:'1.0.2',capabilities:CAPABILITIES}});
 return ()=>{stopped=true;connected=false;rejectWaits();worker.postMessage({type:'stop'});worker.terminate();status('已停用');};
}
