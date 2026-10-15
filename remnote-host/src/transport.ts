import { randomUUID } from 'node:crypto';
import { WebSocket, WebSocketServer } from 'ws';
import type { IncomingMessage } from 'node:http';

export interface ClipperTransport {
  isConnected(): boolean;
  hasBridgeCapability(capability: string): boolean;
  sendRequest(action: string, payload: unknown, timeout?: number): Promise<unknown>;
  waitUntilConnected?(timeout: number): Promise<boolean>;
}
const actions = new Set(['knowledge_clipper_health','knowledge_clipper_export','knowledge_clipper_readback',
  'knowledge_clipper_excerpt_documents','knowledge_clipper_reveal','knowledge_clipper_reveal_state',
  'knowledge_clipper_prepare_image','knowledge_clipper_finalize_media','knowledge_clipper_finalize_spacing',
  'knowledge_clipper_repair_plain_structure','knowledge_clipper_test_plain_structure','knowledge_clipper_adopt']);
export class PluginTransport implements ClipperTransport {
  private server?: WebSocketServer;
  private client?: WebSocket;
  private capabilities = new Set<string>();
  private heartbeat?: ReturnType<typeof setInterval>;
  private connectionWaiters=new Set<(connected:boolean)=>void>();
  private pending = new Map<string,{resolve:(value:unknown)=>void;reject:(error:Error)=>void;timer:ReturnType<typeof setTimeout>}>();
  async start(port: number): Promise<number> {
    const server=this.server=new WebSocketServer({host:'127.0.0.1',port,maxPayload:48*1024*1024,
      verifyClient:({req}:{req:IncomingMessage})=>!req.headers.origin || ['http://localhost:43113','http://127.0.0.1:43113'].includes(req.headers.origin)});
    const alive=new WeakSet<WebSocket>();
    this.heartbeat=setInterval(()=>{for(const socket of server.clients){if(!alive.has(socket)){socket.terminate();continue;}alive.delete(socket);socket.ping();if(socket===this.client && socket.readyState===WebSocket.OPEN)socket.send(JSON.stringify({type:'ping'}));}},15000);
    this.heartbeat.unref();
    server.on('connection',socket=>{
      const wakeups=new Map<string,ReturnType<typeof setTimeout>>();
      alive.add(socket);socket.on('pong',()=>alive.add(socket));
      let ready=false;
      const timer=setTimeout(()=>socket.close(1008,'Hello required'),5000);
      socket.on('message',raw=>{
        try {
          const message=JSON.parse(raw.toString());
          if(!ready){
            if(message.type!=='hello'||message.protocol!=='knowledge-clipper.transport.v1'||message.pluginId!=='knowledge-clipper'||!Array.isArray(message.capabilities)) throw new Error('Invalid hello');
            if(this.client && this.client.readyState===WebSocket.OPEN){socket.close(1008,'Plugin already connected');return;}
            clearTimeout(timer);ready=true;this.client=socket;this.capabilities=new Set(message.capabilities.filter((x:unknown)=>typeof x==='string'));
            for(const done of this.connectionWaiters)done(true);return;
          }
          if(message.type==='wait'){
            if(typeof message.id!=='string'||message.id.length>80||!Number.isInteger(message.milliseconds)||message.milliseconds<1||message.milliseconds>1000||wakeups.has(message.id)||wakeups.size>=4)throw new Error('Invalid navigation wait');
            wakeups.set(message.id,setTimeout(()=>{wakeups.delete(message.id);if(socket.readyState===WebSocket.OPEN)socket.send(JSON.stringify({type:'wait-result',id:message.id}));},message.milliseconds));return;
          }
          const pending=this.pending.get(message.id);if(!pending)return;
          clearTimeout(pending.timer);this.pending.delete(message.id);
          if(message.error!==undefined)pending.reject(new Error(typeof message.error==='string'?message.error:'SDK operation failed'));
          else if(Object.hasOwn(message,'result'))pending.resolve(message.result);
          else pending.reject(new Error('Malformed SDK response'));
        } catch {socket.close(1008,'Invalid transport message');}
      });
      socket.on('close',()=>{clearTimeout(timer);for(const pending of wakeups.values())clearTimeout(pending);wakeups.clear();if(this.client===socket){this.client=undefined;this.capabilities.clear();this.failPending('Plugin disconnected; write outcome may be unknown');}});
      socket.on('error',()=>socket.close());
    });
    await new Promise<void>((resolve,reject)=>{server.once('listening',resolve);server.once('error',reject);});
    return (server.address() as {port:number}).port;
  }
  isConnected(){return this.client?.readyState===WebSocket.OPEN;}
  async waitUntilConnected(timeout:number):Promise<boolean>{
    if(this.isConnected())return true;
    return new Promise(resolve=>{
      const done=(connected:boolean)=>{clearTimeout(timer);this.connectionWaiters.delete(done);resolve(connected);};
      const timer=setTimeout(()=>done(false),timeout);this.connectionWaiters.add(done);
    });
  }
  hasBridgeCapability(value:string){return this.isConnected()&&this.capabilities.has(value);}
  sendRequest(action:string,payload:unknown,timeout=120000):Promise<unknown>{
    if(!actions.has(action))return Promise.reject(new Error('Unsupported SDK action'));
    if(!this.isConnected())return Promise.reject(new Error('RemNote Clipper plugin is not connected'));
    const id=randomUUID();return new Promise((resolve,reject)=>{
      const timer=setTimeout(()=>{this.pending.delete(id);reject(new Error('SDK response timed out; write outcome may be unknown'));},timeout);
      this.pending.set(id,{resolve,reject,timer});
      this.client!.send(JSON.stringify({id,action,payload}),error=>{if(error){clearTimeout(timer);this.pending.delete(id);reject(error);}});
    });
  }
  private failPending(message:string){for(const pending of this.pending.values()){clearTimeout(pending.timer);pending.reject(new Error(message));}this.pending.clear();}
  async stop(){clearInterval(this.heartbeat);for(const done of this.connectionWaiters)done(false);this.failPending('Host stopping');this.client?.terminate();for(const client of this.server?.clients??[])client.terminate();if(this.server)await new Promise<void>(resolve=>this.server!.close(()=>resolve()));}
}
