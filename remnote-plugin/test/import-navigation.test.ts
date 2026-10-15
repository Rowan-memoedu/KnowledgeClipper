import {describe,it,expect,vi} from 'vitest';
import type {ReactRNPlugin} from '@remnote/plugin-sdk';
import {ImportNavigation} from '../src/api/import-navigation';
import {clipIdentity,type KnowledgeClipperAdapter} from '../src/api/knowledge-clipper';

function fixture(kind='plain',count=1) {
  let page='previous',selected:string[]=[],focused:string|undefined;
  const calls:string[]=[];
  const ids=Array.from({length:count},(_,i)=>`front-${i}`);
  const plan={schemaVersion:'knowledge-clipper.remnote.v1',sourceId:'fixture',rootTitle:'零碎知识剪藏',documentPath:['摘录','10.13'],
    cards:ids.map((key)=>({key,kind,front:kind==='cloze'?[{kind:'cloze',text:'public',clozeId:'1'}]:[{kind:'text',text:'public'}],back:kind==='basic'?[[{kind:'text',text:'answer'}]]:[]}))};
  const current:any={rootRemId:'root',documentRemId:'document',cards:ids.map(key=>({key,remIds:[key],childRemIds:kind==='basic'?['answer-'+key]:[],cardIds:kind==='plain'?[]:['card-'+key]})),batchSeparator:{remId:'blank'}};
  const document={_id:'document',isDocument:async()=>true,openRemAsPage:async()=>{calls.push('page');page='document';}};
  const nodes:any=new Map(ids.map(id=>[id,{_id:id,parent:'document',openRemInContext:async()=>{focused=id;calls.push('context:'+id);}}]));
  const plugin:any={rem:{findOne:async(id:string)=>id==='document'?document:nodes.get(id)},
    window:{getFocusedPaneId:async()=>'pane',getOpenPaneRemId:async()=>page,
      getCurrentWindowTree:async()=>({paneId:'pane',remId:page}),
      setRemWindowTree:vi.fn(async(tree:string)=>{page=tree;calls.push('pane');}),setFocusedPaneId:vi.fn(async()=>{})},
    focus:{getFocusedRem:async()=>focused?{_id:focused}:undefined},
    editor:{getSelectedRem:async()=>({type:'Rem',remIds:selected}),selectRem:vi.fn(async(remIds:string[])=>{selected=remIds;calls.push('select');})}};
  const adapter={readNavigation:vi.fn(async()=>current)} as unknown as KnowledgeClipperAdapter;
  return {plan,current,plugin,nodes,calls,adapter,nav:new ImportNavigation(plugin as ReactRNPlugin,adapter),request:()=>({plan,expected:clipIdentity(current)})};
}
describe('verified import navigation',()=>{
  for(const kind of ['plain','basic','cloze'])for(const count of [1,3])it(`${kind} ${count}: selects only imported fronts, preserving document and order`,async()=>{
    const f=fixture(kind,count),result:any=await f.nav.reveal(f.request());
    expect(result.revealed).toBe(true);expect(result.documentRemId).toBe('document');
    expect(result.remIds).toEqual([...f.nodes.keys()]);expect(result.remIds).not.toContain('blank');
    expect(f.plugin.editor.selectRem).toHaveBeenCalledWith([...f.nodes.keys()],'document');
    expect(f.calls).toEqual(['pane','select']);
  });
  it('image content uses the same frozen node identity without uploading or rewriting',async()=>{
    const f=fixture();f.plan.cards[0].front=[{kind:'image',uri:'file:///D:/public.png',sha256:'a'.repeat(64)}] as any;
    expect((await f.nav.reveal(f.request()) as any).remIds).toEqual(['front-0']);
  });
  for(const state of ['mediaPending','batchSeparatorPending','notStarted'])it(`rejects ${state} before changing the editor`,async()=>{
    const f=fixture();f.current[state]=true;
    await expect(f.nav.reveal(f.request())).rejects.toThrow('not verified');expect(f.calls).toEqual([]);
  });
  it('rejects mismatched receipt, moved nodes, and missing document before navigation',async()=>{
    const f=fixture();const request=f.request();request.expected.documentRemId='other';
    await expect(f.nav.reveal(request)).rejects.toThrow('not verified');
    f.nodes.get('front-0').parent='other';await expect(f.nav.reveal(f.request())).rejects.toThrow('moved');
    expect(f.calls).toEqual([]);
  });
  it('read-only state never calls navigation',async()=>{
    const f=fixture();expect((await f.nav.state()).documentRemId).toBe('previous');expect(f.calls).toEqual([]);
  });
  it('a later request supersedes an older read without selecting an old block',async()=>{
    const f=fixture();let resume:()=>void=()=>{};
    const gate=new Promise<void>(done=>{resume=done;});
    vi.mocked(f.adapter.readNavigation).mockImplementationOnce(async()=>{await gate;return f.current;});
    const old=f.nav.reveal(f.request());await Promise.resolve();await Promise.resolve();
    const latest=f.nav.reveal(f.request());resume();
    expect(await old).toEqual({revealed:false,superseded:true});expect((await latest as any).revealed).toBe(true);
    expect(f.calls.filter(c=>c==='select')).toHaveLength(1);
  });
  it('does not claim success when SDK resolves without retaining selection',async()=>{
    vi.useFakeTimers();
    try{const f=fixture();f.plugin.editor.selectRem=async()=>{};
      const result=expect(f.nav.reveal(f.request())).rejects.toThrow('retain');
      await vi.runAllTimersAsync();await result;
    }finally{vi.useRealTimers();}
  });
  it('waits for the real editor tree when navigation resolves before selection can apply',async()=>{
    const f=fixture();f.plugin.editor.selectRem.mockImplementationOnce(async()=>{});
    expect((await f.nav.reveal(f.request()) as any).revealed).toBe(true);
    expect(f.plugin.editor.selectRem).toHaveBeenCalledTimes(2);
    expect(f.plugin.window.setFocusedPaneId).toHaveBeenCalledTimes(3);
  });
  it('preserves other panes and focuses the replaced pane after SDK regenerates pane IDs',async()=>{
    const f=fixture();let tree:any={direction:'row',splitPercentage:37,first:{paneId:'other',remId:'keep'},second:{paneId:'pane',remId:'previous'}};
    let focused='pane';
    f.plugin.window.getCurrentWindowTree=async()=>tree;
    f.plugin.window.getFocusedPaneId=async()=>focused;
    f.plugin.window.getOpenPaneRemId=async(id:string)=>id==='new-right'?'document':'previous';
    f.plugin.window.setFocusedPaneId=async(id:string)=>{focused=id;};
    f.plugin.window.setRemWindowTree=vi.fn(async()=>{tree={...tree,first:{paneId:'new-left',remId:'keep'},second:{paneId:'new-right',remId:'document'}};});
    expect((await f.nav.reveal(f.request()) as any).revealed).toBe(true);
    expect(f.plugin.window.setRemWindowTree).toHaveBeenCalledWith({direction:'row',splitPercentage:37,first:'keep',second:'document'});
    expect(focused).toBe('new-right');
  });
  it('refuses to replace a pane when the current focused pane is not in the layout',async()=>{
    const f=fixture();f.plugin.window.getFocusedPaneId=async()=>'missing';
    await expect(f.nav.reveal(f.request())).rejects.toThrow('focused pane');
    expect(f.plugin.window.setRemWindowTree).not.toHaveBeenCalled();expect(f.calls).toEqual([]);
  });
  it('selects an already open document without rebuilding panes or opening a native window',async()=>{
    const f=fixture();f.plugin.window.getOpenPaneRemId=async()=>'document';
    expect((await f.nav.reveal(f.request()) as any).revealed).toBe(true);
    expect(f.plugin.window.setRemWindowTree).not.toHaveBeenCalled();expect(f.calls).toEqual(['select']);
  });
  it('settles editor readiness using host wakeups even when all hidden-page timers are suspended',async()=>{
    const f=fixture();let mounted=false;
    const select=f.plugin.editor.selectRem.getMockImplementation();
    f.plugin.editor.selectRem.mockImplementation(async(ids:string[])=>{if(mounted)await select(ids);});
    const wake=vi.fn(async()=>{mounted=true;});
    const nav=new ImportNavigation(f.plugin,f.adapter,wake);
    vi.stubGlobal('setTimeout',()=>{throw new Error('Background page timers are suspended');});
    try{expect((await nav.reveal(f.request()) as any).revealed).toBe(true);expect(wake).toHaveBeenCalled();}
    finally{vi.unstubAllGlobals();}
  });
});
