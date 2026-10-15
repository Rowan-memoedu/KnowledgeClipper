// Keep socket handshakes and reconnect backoff outside the hidden index iframe.
// SDK calls stay in the index widget; responses are bound to their original socket.
export const MAX_RECONNECT_DELAY_MS=5000;
export function startSocketWorker(port:{postMessage:(value:unknown)=>void;onmessage:any}) {
 let socket:WebSocket|undefined,timer:ReturnType<typeof setTimeout>|undefined;
 let stopped=false,delay=1000,generation=0,hello:unknown;
 const post=(value:unknown)=>port.postMessage(value);
 const connect=()=>{
  if(stopped)return;
  const current=++generation,ws=socket=new WebSocket('ws://127.0.0.1:43112');
  post({type:'status',value:'connecting',generation:current});
  ws.onopen=()=>{if(stopped||ws!==socket)return;delay=1000;ws.send(JSON.stringify(hello));post({type:'status',value:'connected',generation:current});};
  ws.onmessage=event=>{
   if(ws!==socket||stopped)return;
   let message:any;try{message=JSON.parse(String(event.data));}catch{return;}
   if(message.type==='ping'){ws.send(JSON.stringify({type:'pong'}));return;}
   post({type:'message',message,generation:current});
  };
  ws.onerror=()=>ws.close();
  ws.onclose=()=>{if(ws!==socket)return;post({type:'status',value:'disconnected',generation:current});if(!stopped){timer=setTimeout(connect,delay);delay=Math.min(delay*2,MAX_RECONNECT_DELAY_MS);}};
 };
 port.onmessage=(event:MessageEvent)=>{
  const message=event.data;
  if(message.type==='start'&&!hello){hello=message.hello;connect();}
  else if(message.type==='send'&&message.generation===generation&&socket?.readyState===WebSocket.OPEN)socket.send(JSON.stringify(message.message));
  else if(message.type==='stop'){stopped=true;clearTimeout(timer);socket?.close();}
 };
}

// The bundle is also imported by isolated transport tests.
if(typeof (globalThis as any).importScripts==='function')startSocketWorker(globalThis as any);
