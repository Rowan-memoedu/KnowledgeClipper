import {describe,it,expect,vi,afterEach} from 'vitest';
import type {ReactRNPlugin} from '@remnote/plugin-sdk';
import {createDispatcher,startRuntime,WRITE_SETTING} from '../src/runtime';
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
 it('dispatches SDK requests with no page timers and binds late replies to the original connection',async()=>{
  let worker:any;
  class WorkerMock {onmessage:any;onerror:any;postMessage=vi.fn();terminate=vi.fn();constructor(public url:URL){worker=this;}}
  vi.stubGlobal('Worker',WorkerMock);vi.stubGlobal('location',{href:'http://localhost:43113/index.html?widgetName=index'});
  vi.stubGlobal('setTimeout',()=>{throw new Error('hidden page timer must not be used');});
  vi.stubGlobal('setInterval',()=>{throw new Error('hidden page timer must not be used');});
  const plugin=sdk(),stop=startRuntime(plugin);expect(String(worker.url)).toBe('http://localhost:43113/transport-worker.js');
  expect(worker.postMessage.mock.calls[0][0].hello.protocol).toBe('knowledge-clipper.transport.v1');
  await worker.onmessage({data:{type:'status',value:'connected',generation:1}});
  let finish!:(value:boolean)=>void;
  vi.mocked(plugin.settings.getSetting).mockImplementationOnce(()=>new Promise(resolve=>{finish=resolve as any;}));
  const response=worker.onmessage({data:{type:'message',generation:1,message:{id:'health1',action:'knowledge_clipper_health',payload:{}}}});
  await worker.onmessage({data:{type:'status',value:'connected',generation:2}});finish(true);await response;
  expect(worker.postMessage.mock.calls.at(-1)[0]).toMatchObject({type:'send',generation:1,message:{id:'health1',result:{pluginId:'knowledge-clipper'}}});
  stop();expect(worker.terminate).toHaveBeenCalledOnce();
 });
});
