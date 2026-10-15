import {renderWidget,usePlugin,useRunAsync,useAPIEventListener,AppEvents,WidgetLocation} from '@remnote/plugin-sdk';
import React,{useEffect,useRef,useState} from 'react';
import mermaid from 'mermaid';
import {MERMAID_RECEIPT_PREFIX} from '../api/mermaid';

mermaid.initialize({startOnLoad:false,securityLevel:'strict',suppressErrorRendering:true,theme:'default'});
function MermaidWidget(){
 const plugin=usePlugin();const context=useRunAsync(()=>plugin.widget.getWidgetContext<WidgetLocation.UnderRemEditor>(),[]);
 const host=useRef<HTMLDivElement>(null);const [revision,update]=useState(0);const [error,setError]=useState('');
 useAPIEventListener(AppEvents.RemChanged,context?.remId,()=>update(value=>value+1));
 useEffect(()=>{
  let active=true;
  void(async()=>{
   const remId=context?.remId;if(!remId||!host.current)return;
   await plugin.storage.setSession(MERMAID_RECEIPT_PREFIX+'boot',{remId,at:Date.now(),secure:globalThis.isSecureContext});
   const rem=await plugin.rem.findOne(remId);if(!active)return;
   const sources=(rem?.text??[]).flatMap(part=>typeof part!=='string'&&part.i==='m'&&part.code&&part.language==='mermaid'?[part.text]:[]);
   const hashes=await Promise.all(sources.map(async source=>Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(source)))).map(n=>n.toString(16).padStart(2,'0')).join('')));
   await plugin.storage.setSession(MERMAID_RECEIPT_PREFIX+'boot',{remId,at:Date.now(),stage:'rendering',sourceCount:sources.length});
   try{
    const results=[];
    for(let i=0;i<sources.length;i++)results.push(await mermaid.render('kc_mermaid_'+remId.replace(/\W/g,'')+'_'+revision+'_'+i,sources[i]));
    if(!active||!host.current)return;
    host.current.innerHTML=results.map(r=>'<div class="diagram">'+r.svg+'</div>').join('');
    host.current.querySelectorAll('a').forEach(a=>{a.removeAttribute('href');a.removeAttribute('xlink:href');});
    setError('');
    if(!active||!host.current)return;
    const svgs=Array.from(host.current.querySelectorAll('svg'));
    await plugin.storage.setSession(MERMAID_RECEIPT_PREFIX+remId,{version:'11.16.1',remId,hashes,status:'rendered',at:Date.now(),
     diagrams:svgs.map(svg=>({svg:svg.outerHTML,width:svg.getBoundingClientRect().width,height:svg.getBoundingClientRect().height,
       nodeCount:svg.querySelectorAll('.node').length,text:svg.querySelectorAll('.nodeLabel').length?Array.from(svg.querySelectorAll('.nodeLabel')).map(n=>n.textContent).join('|'):Array.from(svg.querySelectorAll('text')).map(n=>n.textContent).join('|')}))});
   }catch(e){if(!active)return;host.current?.replaceChildren();const message='Mermaid 语法错误，源码已保留：'+String(e);setError(message);
    await plugin.storage.setSession(MERMAID_RECEIPT_PREFIX+remId,{remId,hashes,status:'error',at:Date.now(),error:message});}
  })().catch(e=>{if(active){const message='Mermaid 暂未显示，源码已保留：'+String(e);setError(message);void plugin.storage.setSession(MERMAID_RECEIPT_PREFIX+(context?.remId??'boot'),{remId:context?.remId,status:'error',error:message,at:Date.now()});}});
  return()=>{active=false;};
 },[context?.remId,revision]);
 return <div><style>{`body{margin:0;background:transparent}.diagram{overflow:auto;max-width:100%;margin:6px 0;background:#fff;color:#333;padding:8px;border-radius:4px}.diagram svg{max-width:100%;height:auto}`}</style>
  {error&&<div role="alert">{error}</div>}<div ref={host}/></div>;
}
renderWidget(MermaidWidget);
