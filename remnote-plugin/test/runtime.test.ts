import {describe,it,expect,vi,afterEach} from 'vitest';
import type {ReactRNPlugin} from '@remnote/plugin-sdk';
import {createDispatcher,startRuntime,WS_URL,WRITE_SETTING} from '../src/runtime';
afterEach(()=>{vi.unstubAllGlobals();vi.useRealTimers();});
function sdk(){return {settings:{getSetting:vi.fn(async()=>true)},storage:{setSession:vi.fn(async()=>undefined)}} as unknown as ReactRNPlugin;}
describe('dedicated plugin transport',()=>{
 it('advertises only clipper health and rejects MCP commands',async()=>{
  const plugin=sdk(),dispatch=createDispatcher(plugin);
  const health:any=await dispatch('knowledge_clipper_health',{});
  expect(health.pluginId).toBe('knowledge-clipper');expect(health.identityAdoptSupported).toBe(true);
  expect(plugin.settings.getSetting).toHaveBeenCalledWith(WRITE_SETTING);
  for(const action of ['create_note','get_status','enumerate_rems','execute_code']) await expect(dispatch(action,{})).rejects.toThrow('Unsupported');
 });
 it('reports disabled writes without blocking read-only health',async()=>{
  const plugin=sdk();vi.mocked(plugin.settings.getSetting).mockResolvedValue(false);
  expect((await createDispatcher(plugin)('knowledge_clipper_health',{}) as any).acceptWriteOperations).toBe(false);
 });
 it('uses dedicated endpoint, responds to health and reconnects until shutdown',async()=>{
  vi.useFakeTimers();
  const sockets:any[]=[];
  class Socket {static OPEN=1;readyState=1;onopen:any;onmessage:any;onclose:any;onerror:any;send=vi.fn();url:string;constructor(url:string){this.url=url;sockets.push(this);}close(){this.readyState=3;this.onclose?.();}}
  vi.stubGlobal('WebSocket',Socket);
  const stop=startRuntime(sdk());expect(sockets[0].url).toBe(WS_URL);
  sockets[0].onopen();expect(JSON.parse(sockets[0].send.mock.calls[0][0]).protocol).toBe('knowledge-clipper.transport.v1');
  await sockets[0].onmessage({data:JSON.stringify({id:'health1',action:'knowledge_clipper_health',payload:{}})});
  expect(JSON.parse(sockets[0].send.mock.calls[1][0]).result.pluginId).toBe('knowledge-clipper');
  await sockets[0].onmessage({data:'{"type":"ping"}'});expect(JSON.parse(sockets[0].send.mock.calls[2][0])).toEqual({type:'pong'});
  sockets[0].close();await vi.advanceTimersByTimeAsync(1000);expect(sockets).toHaveLength(2);
  stop();await vi.advanceTimersByTimeAsync(120000);expect(sockets).toHaveLength(2);
 });
});
