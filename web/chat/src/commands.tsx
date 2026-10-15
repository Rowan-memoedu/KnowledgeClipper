import React, {useRef, useState, useSyncExternalStore} from 'react';
import {flushSync} from 'react-dom';

type Flight = {ids:Set<string>;release:()=>void};
type Pending = {command:string;cardId?:string;flight?:Flight};
const pending = new Map<string,Pending>();
const listeners = new Set<()=>void>();
const blocking = new Set(['send','compose','export','recover','recoverAuxiliary']);
let sequence = 0;
const viewId=crypto.randomUUID();
let clickFlight:Flight|undefined;
let snapshot = {busy:false,history:new Set<string>()};
const changed = () => {
  snapshot = {busy:[...pending.values()].some(p=>blocking.has(p.command)),
    history:new Set([...pending.values()].filter(p=>p.command==='undo'||p.command==='redo').map(p=>p.cardId!))};
  listeners.forEach(listener=>listener());
};
export const usePendingActions = () => useSyncExternalStore(
  listener=>{listeners.add(listener);return()=>listeners.delete(listener);},()=>snapshot);

// Paint the local feedback before the host can perform synchronous preparation.
// The fallback also dispatches if the view becomes hidden between click and paint.
const afterPaint = (action:()=>void) => {
  let done=false;
  const once=()=>{if(!done){done=true;action();}};
  window.setTimeout(once,50);
  requestAnimationFrame(()=>window.setTimeout(once,0));
};
let queued:(()=>void)[]=[];
const enqueue=(action:()=>void)=>{
  queued.push(action);if(queued.length>1)return;
  afterPaint(()=>{const batch=queued;queued=[];batch.forEach(run=>run());});
};
const finish = (requestId:string) => {
  const request=pending.get(requestId);if(!request)return;
  pending.delete(requestId);changed();
  if(request.flight){request.flight.ids.delete(requestId);if(!request.flight.ids.size)request.flight.release();}
};
export const receiveCommandAck = (value:unknown) => {
  const ack=value as {kind?:string;requestId?:string};
  if(ack?.kind==='commandAcknowledged'&&typeof ack.requestId==='string')finish(ack.requestId);
};
export function send(command:string,payload:object={}) {
  if(blocking.has(command)&&snapshot.busy)return;
  const history=command==='undo'||command==='redo';
  const cardId=(payload as {cardId?:string}).cardId;
  if(history&&cardId&&snapshot.history.has(cardId))return;
  if(!clickFlight&&!blocking.has(command)&&!history){
    const post=()=>window.chrome?.webview.postMessage({command,...payload});
    if(queued.length)enqueue(post);else post();return;
  }
  const requestId=`${viewId}:${++sequence}`;
  pending.set(requestId,{command,cardId,flight:clickFlight});
  clickFlight?.ids.add(requestId);changed();
  enqueue(()=>{
    try {
      if(!window.chrome?.webview){finish(requestId);return;}
      window.chrome.webview.postMessage({command,...payload,clientCommandId:requestId});
    } catch(error){finish(requestId);throw error;}
  });
}

// Every enabled button acknowledges its click locally, including commands which
// open a native dialog or only change the active card. Backend states cannot clear it.
export function ActionButton({onClick,disabled,...props}:React.ButtonHTMLAttributes<HTMLButtonElement>) {
  const [waiting,setWaiting]=useState(false);
  const locked=useRef(false);
  return <button {...props} disabled={disabled||waiting} aria-busy={waiting||props['aria-busy']||undefined}
    data-pending={waiting||undefined} onClick={event=>{
      if(disabled||locked.current)return;
      locked.current=true;flushSync(()=>setWaiting(true));
      const flight:Flight={ids:new Set(),release:()=>{locked.current=false;setWaiting(false);}};
      const previous=clickFlight;clickFlight=flight;
      try{onClick?.(event);}finally{clickFlight=previous;if(!flight.ids.size)afterPaint(flight.release);}
    }}/>;
}
