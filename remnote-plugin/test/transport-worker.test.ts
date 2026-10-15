import {afterEach,describe,expect,it,vi} from 'vitest';
import {startSocketWorker,MAX_RECONNECT_DELAY_MS} from '../src/transport-worker';
afterEach(()=>{vi.useRealTimers();vi.unstubAllGlobals();});
describe('background socket worker',()=>{
 it('keeps reconnect attempts within the read-only health grace period after a long outage',async()=>{
  vi.useFakeTimers();const attempts:number[]=[];
  class Socket {static OPEN=1;readyState=3;onclose:any;onopen:any;onmessage:any;onerror:any;constructor(){attempts.push(Date.now());queueMicrotask(()=>this.onclose?.());}close(){this.onclose?.();}}
  vi.stubGlobal('WebSocket',Socket);
  const port={postMessage:vi.fn(),onmessage:undefined as any};startSocketWorker(port);
  port.onmessage({data:{type:'start',hello:{type:'hello'}}});
  await vi.advanceTimersByTimeAsync(65000);
  const gaps=attempts.slice(1).map((time,i)=>time-attempts[i]);
  expect(gaps.length).toBeGreaterThan(10);expect(Math.max(...gaps)).toBeLessThanOrEqual(MAX_RECONNECT_DELAY_MS);expect(MAX_RECONNECT_DELAY_MS).toBeLessThan(10000);
  port.onmessage({data:{type:'stop'}});
 });
 it('handles handshakes, pings and reconnection without relying on the page event loop',async()=>{
  vi.useFakeTimers();const sockets:any[]=[];
  class Socket {static OPEN=1;readyState=1;onopen:any;onclose:any;onmessage:any;onerror:any;send=vi.fn();constructor(public url:string){sockets.push(this);}close(){this.readyState=3;this.onclose?.();}}
  vi.stubGlobal('WebSocket',Socket);
  const port={postMessage:vi.fn(),onmessage:undefined as any};startSocketWorker(port);
  const receive=(data:any)=>port.onmessage({data});
  receive({type:'start',hello:{type:'hello',pluginId:'knowledge-clipper'}});
  expect(sockets[0].url).toBe('ws://127.0.0.1:43112');sockets[0].onopen();
  expect(JSON.parse(sockets[0].send.mock.calls[0][0]).type).toBe('hello');
  sockets[0].onmessage({data:'{"type":"ping"}'});expect(JSON.parse(sockets[0].send.mock.calls[1][0])).toEqual({type:'pong'});
  // Timers being delayed for minutes must not make the client close a healthy
  // loopback connection. The host owns native WebSocket ping/pong liveness.
  await vi.advanceTimersByTimeAsync(360000);expect(sockets[0].readyState).toBe(1);
  sockets[0].close();await vi.advanceTimersByTimeAsync(1000);expect(sockets).toHaveLength(2);sockets[1].onopen();
  receive({type:'send',generation:1,message:{id:'old',result:'must not cross sockets'}});
  expect(sockets[1].send).toHaveBeenCalledTimes(1);
  receive({type:'send',generation:2,message:{type:'wait',id:'tick',milliseconds:75}});
  expect(JSON.parse(sockets[1].send.mock.calls[1][0]).type).toBe('wait');
  sockets[1].onmessage({data:'{"type":"wait-result","id":"tick"}'});
  expect(port.postMessage).toHaveBeenLastCalledWith({type:'message',generation:2,message:{type:'wait-result',id:'tick'}});
  receive({type:'stop'});await vi.advanceTimersByTimeAsync(120000);expect(sockets).toHaveLength(2);
 });
});
