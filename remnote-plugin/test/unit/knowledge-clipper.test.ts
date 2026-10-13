import { describe, expect, it, vi } from 'vitest';
describe('standalone plugin identity migration',()=>{
  it('blocks unknown source without explicit new-source authorization',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true);
    await expect(adapter.export(plan())).rejects.toThrow('Unregistered source');
    expect(sdk.nodes.size).toBe(0);expect(sdk.storage.size).toBe(0);
    const result:any=await adapter.export({plan:plan(),newSource:true});
    expect(result.cards).toHaveLength(1);
    const size=sdk.nodes.size;
    await adapter.export(plan());expect(sdk.nodes.size).toBe(size);
  });
  it('adopts old IDs into fresh plugin storage using real readback without node writes',async()=>{
    const sdk=fakeSdk(),old=new KnowledgeClipperAdapter(sdk.plugin,()=>true);
    const result:any=await old.export({plan:plan(),newSource:true});
    const nodeSnapshot=JSON.stringify([...sdk.nodes.values()]);
    sdk.storage.clear();
    const adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true);
    const adopted:any=await adapter.adopt({plan:plan(),expected:clipIdentity(result),verifiedNodes:clipNodeSnapshot(result)});
    expect(clipIdentity(adopted)).toEqual(clipIdentity(result));
    expect(JSON.stringify([...sdk.nodes.values()])).toBe(nodeSnapshot);
    await adapter.adopt({plan:plan(),expected:clipIdentity(result),verifiedNodes:clipNodeSnapshot(result)});
    expect(clipIdentity(await adapter.export(plan()) as any)).toEqual(clipIdentity(result));
  });
  it('rejects changed node snapshot without writing journal',async()=>{
    const sdk=fakeSdk(),old=new KnowledgeClipperAdapter(sdk.plugin,()=>true);
    const result:any=await old.export({plan:plan(),newSource:true});
    const verifiedNodes=clipNodeSnapshot(result);
    sdk.storage.clear();
    const front=sdk.nodes.get(result.cards[0].remIds[0])!;
    front.text[0].bold=true;
    const writes=sdk.writes;
    const adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true);
    await expect(adapter.adopt({plan:plan(),expected:clipIdentity(result),verifiedNodes})).rejects.toThrow('snapshot changed');
    expect(sdk.storage.size).toBe(0);expect(sdk.writes).toBe(writes);
  });
  it('rejects wrong root/card identity and missing evidence without writes',async()=>{
    const sdk=fakeSdk(),old=new KnowledgeClipperAdapter(sdk.plugin,()=>true);
    const result:any=await old.export({plan:plan(),newSource:true});
    sdk.storage.clear();const writes=sdk.writes;
    const adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true),expected=clipIdentity(result);
    expected.cards[0].cardIds=['wrong-card'];
    await expect(adapter.adopt({plan:plan(),expected,verifiedNodes:clipNodeSnapshot(result)})).rejects.toThrow('snapshot changed');
    await expect(adapter.adopt({plan:plan(),expected})).rejects.toThrow('Complete verified');
    expect(sdk.storage.size).toBe(0);expect(sdk.writes).toBe(writes);
  });
  it('rejects user-edited content and write-disabled migration',async()=>{
    const sdk=fakeSdk(),old=new KnowledgeClipperAdapter(sdk.plugin,()=>true);
    const result:any=await old.export({plan:plan(),newSource:true});
    sdk.storage.clear();
    const request={plan:plan(),expected:clipIdentity(result),verifiedNodes:clipNodeSnapshot(result)};
    await expect(new KnowledgeClipperAdapter(sdk.plugin,()=>false).adopt(request)).rejects.toThrow('disabled');
    sdk.nodes.get(result.cards[0].remIds[0])!.text=[{i:'m',text:'user edit'}];
    const writes=sdk.writes;
    await expect(new KnowledgeClipperAdapter(sdk.plugin,()=>true).adopt(request)).rejects.toThrow('content or location');
    expect(sdk.storage.size).toBe(0);expect(sdk.writes).toBe(writes);
  });
});
import { createHash, webcrypto } from 'node:crypto';
import { BuiltInPowerupCodes, type ReactRNPlugin } from '@remnote/plugin-sdk';
import { clipIdentity, clipNodeSnapshot, clipStructureSnapshot, KnowledgeClipperAdapter, partsToRichText, richTextToParts, type ClipPlan } from '../../src/api/knowledge-clipper';
import {verifyEmptyBackEditor} from '../../src/testing/plain-structure-canary';

describe('completed import navigation identity',()=>{
  it('allows read-only navigation after editor spacer cleanup while preserving strict import readback',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true);
    const imported:any=await adapter.export({plan:plan(),newSource:true});
    const document=sdk.nodes.get(imported.documentRemId)!;
    document.children=document.children.filter(id=>id!==imported.batchSeparator.remId);
    const writes=sdk.writes,storage=JSON.stringify([...sdk.storage]);
    expect(clipIdentity(await adapter.readNavigation(plan()) as any)).toEqual(clipIdentity(imported));
    await expect(adapter.readback(plan())).rejects.toThrow('separator or sibling order');
    expect(sdk.writes).toBe(writes);expect(JSON.stringify([...sdk.storage])).toBe(storage);
  });
  it('rejects incomplete journals and changed content without navigation or repairs',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true);
    await expect(adapter.readNavigation(plan())).rejects.toThrow('completed import journal');
    const imported:any=await adapter.export({plan:plan(),newSource:true});
    sdk.nodes.get(imported.cards[0].remIds[0])!.text=[{i:'m',text:'Changed public content'}];
    const writes=sdk.writes;
    await expect(adapter.readNavigation(plan())).rejects.toThrow('content or location');
    expect(sdk.writes).toBe(writes);
  });
});

function fakeSdk() {
  const nodes = new Map<string, FakeRem>();
  const storage = new Map<string, unknown>();
  let writes = 0;
  let failAt = 0;
  let failDocumentAt = 0;
  let failEnableAt = 0;
  const parsed: string[] = [];
  let parseResult: any[] | undefined;
  let afterSetText: ((id:string)=>void) | undefined;
  let afterSetParent: ((id:string)=>void) | undefined;
  let editorId:string|undefined,editorUrl='original',editorSelection:any;
  class FakeRem {
    _id = `rem-${nodes.size + 1}`;
    parent: string | null = null;
    children: string[] = [];
    text: any[] = [];
    backText: any[] | undefined | null = undefined;
    type: number | undefined = undefined;
    document = false;
    item = false;
    enabled = true;
    direction = 'forward';
    powerups = new Set<string>();
    tags: string[] = [];
    aliases: string[] = [];
    table = false;
    constructor() { nodes.set(this._id, this); }
    async setText(v: any[]) { this.text = structuredClone(v); writes++; afterSetText?.(this._id); }
    async setBackText(v: any[] | undefined) { if(!Array.isArray(v))throw new Error('backText parameter: Required');this.backText = structuredClone(v); writes++; }
    async setParent(v: string | null, position = 0) {
      if(this.parent){const prior=nodes.get(this.parent)!;prior.children=prior.children.filter(id=>id!==this._id);}
      this.parent = v;if(v)nodes.get(v)!.children.splice(position,0,this._id);writes++;afterSetParent?.(this._id);
    }
    async setType(v: number | string) { if(v!==''&&v!=='DEFAULT_TYPE'&&v!==1&&v!==2)throw new Error('type parameter: Expected default type string, 1 or 2');this.type = typeof v==='string'?0:v; writes++; }
    async setIsDocument(v: boolean) { if(this._id===`rem-${failDocumentAt}`)throw new Error('directory setup failed');this.document = v; writes++; }
    async isDocument() { return this.document; }
    async setIsCardItem(v: boolean) { this.item = v; writes++; }
    async isCardItem() { return this.item || this.powerups.has(BuiltInPowerupCodes.MultiLineCard); }
    async setEnablePractice(v: boolean) { if(this._id===`rem-${failEnableAt}`)throw new Error('practice setup interrupted');this.enabled = v; writes++; }
    async getEnablePractice() { return this.enabled; }
    async getPracticeDirection() { return this.direction; }
    async setPracticeDirection(v: string) { this.direction = v; writes++; }
    async addPowerup(v: string) { this.powerups.add(v); writes++; }
    async hasPowerup(v: string) { return this.powerups.has(v); }
    async getTagRems() { return [...this.tags, ...this.powerups]; }
    async getAliases() { return this.aliases; }
    async isTable() { return this.table; }
    async getCards() {
      if (!this.enabled) return [];
      const groups = [...new Set(this.text.map(x => x.cId).filter(Boolean))];
      if(groups.length)return groups.map(c => ({_id:`${this._id}:${c}`,type:{clozeId:c}}));
      return this.children.some(id => nodes.get(id)!.item || nodes.get(id)!.powerups.has(BuiltInPowerupCodes.MultiLineCard))
        ? [{_id:`${this._id}:forward`,type:'forward'}] : [];
    }
  }
  const plugin = {
    window:{getURL:async()=>editorUrl,getFocusedPaneId:async()=>'pane',getOpenPaneRemIds:async()=>[editorUrl],
      setFocusedPaneId:async()=>{},setURL:async(url:string)=>{editorUrl=url;editorId=undefined;editorSelection=undefined;},
      openRem:async(rem:FakeRem)=>{editorId=rem._id;editorUrl=rem._id;}},
    focus:{getFocusedRem:async()=>editorId?nodes.get(editorId):undefined,getFocusedPortal:async()=>undefined},
    editor:{getSelection:async()=>editorSelection,selectText:async(range:any)=>{editorSelection={type:'Text',remId:editorId,
      range:{start:Math.min(range.start,range.end),end:Math.max(range.start,range.end)},isReverse:range.start>range.end,richText:[]};},
      selectRem:async(ids:string[])=>{editorSelection=ids.length?{type:'Rem',remIds:ids}:undefined;},
      getFocusedEditorText:async()=>{const rem=nodes.get(editorId!)!;return [...rem.text,...(rem.backText!=null?[{i:'s'}]:[])];},
      setText:async(front:any[])=>{const rem=nodes.get(editorId!)!;rem.text=structuredClone(front);rem.backText=undefined;writes++;}},
    rem: {createRem:async()=>{if(failAt && nodes.size + 1 === failAt)throw new Error('write failed');return new FakeRem();},
      findOne:async(id:string)=>nodes.get(id),findByName:async(name:string[],parent:string|null)=>
        [...nodes.values()].find(n=>n.parent===parent && n.text.map(x=>typeof x==='string'?x:x.text??'').join('')===name.join(''))},
    richText:{toString:async(t:any[])=>t.map(x=>typeof x==='string'?x:x.text??'').join(''),
      parseFromMarkdown:async(markdown:string)=>{parsed.push(markdown);return parseResult??[{i:'i',url:`%LOCAL_FILE%parsed-${parsed.length}.png`,title:'official title',width:160,height:96}];}},
    storage:{getSynced:async(k:string)=>structuredClone(storage.get(k)),setSynced:async(k:string,v:unknown)=>{storage.set(k,structuredClone(v));writes++;}},
  } as unknown as ReactRNPlugin;
  return {plugin,nodes,storage,parsed,get writes(){return writes;},failAt:(n:number)=>{failAt=n;},failDocumentAt:(n:number)=>{failDocumentAt=n;},failEnableAt:(n:number)=>{failEnableAt=n;},
    parseResult:(value:any[])=>{parseResult=value;},afterSetText:(value:(id:string)=>void)=>{afterSetText=value;},afterSetParent:(value:(id:string)=>void)=>{afterSetParent=value;}};
}
const text = (value:string) => ({kind:'text' as const,text:value});
const plan = (): ClipPlan => ({schemaVersion:'knowledge-clipper.remnote.v1',sourceId:'source-1',rootTitle:'零碎知识剪藏',
  documentPath:['摘录','手动'],cards:[{key:'card.0',kind:'basic',front:[text('first\nsecond :: literal >>')],
    back:[[text('answer one')],[text('answer two\nsoft newline')]]}]});

describe('ordinary blank sibling between new import batches',()=>{
  it('models SDK default prepend and rejects the numeric readback type as a setter argument',async()=>{
    const sdk=fakeSdk(),parent=await sdk.plugin.rem.createRem(),first=await sdk.plugin.rem.createRem(),second=await sdk.plugin.rem.createRem();
    await first!.setParent(parent!._id);await second!.setParent(parent!._id);
    expect(sdk.nodes.get(parent!._id)!.children).toEqual([second!._id,first!._id]);
    await expect(first!.setType(0)).rejects.toThrow('Expected default type string');
  });
  it('appends exactly one empty non-card sibling after all converted source paragraphs',async()=>{
    const sdk=fakeSdk(),p=plan();
    p.cards=Array.from({length:3},(_,i)=>({key:`paragraph.${i}`,kind:'plain' as const,front:[text(`Paragraph ${i+1}`)],back:[]}));
    const adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true),first:any=await adapter.export({plan:p,newSource:true});
    const ids=first.cards.flatMap((card:any)=>card.remIds),blank=first.batchSeparator;
    expect(first.documentChildren).toEqual([...ids,blank.remId]);
    expect(blank).toMatchObject({previousRemId:ids[2],node:{remId:blank.remId,parentId:first.documentRemId,text:[],backText:[],
      children:[],remType:0,isDocument:false,isCardItem:false,isExtraCardDetail:false,practiceEnabled:false,cardIds:[],cardTypes:[]}});
    expect(first.cards).toHaveLength(3);
    const size=sdk.nodes.size,writes=sdk.writes;
    const again:any=await new KnowledgeClipperAdapter(sdk.plugin,()=>true).export(p);
    expect(again.batchSeparator).toEqual(blank);expect(sdk.nodes.size).toBe(size);expect(sdk.writes).toBe(writes);
    expect((await adapter.readback(p) as any).batchSeparator).toEqual(blank);expect(sdk.writes).toBe(writes);
  });
  it('keeps earlier spacing stable when another batch is appended in the same directory',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true),p=plan();
    const first:any=await adapter.export({plan:p,newSource:true});
    const second:any=await adapter.export({plan:{...p,sourceId:'source-2'},newSource:true});
    expect(second.documentChildren).toEqual([first.cards[0].remIds[0],first.batchSeparator.remId,second.cards[0].remIds[0],second.batchSeparator.remId]);
    expect((await adapter.readback(p) as any).batchSeparator).toEqual(first.batchSeparator);
    expect(first.cards[0].childRemIds).not.toContain(first.batchSeparator.remId);
    expect(second.cards[0].cardIds).toHaveLength(1);
  });
  it('serializes different sources through the single adapter queue so shared-directory batches cannot interleave',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true),p=plan();
    const [first,second]:any[]=await Promise.all([adapter.export({plan:p,newSource:true}),adapter.export({plan:{...p,sourceId:'parallel-source'},newSource:true})]);
    expect(second.documentChildren).toEqual([first.cards[0].remIds[0],first.batchSeparator.remId,second.cards[0].remIds[0],second.batchSeparator.remId]);
    expect(sdk.nodes.get(first.cards[0].remIds[0])!.children).toEqual(first.cards[0].childRemIds);
    expect(sdk.nodes.get(second.cards[0].remIds[0])!.children).toEqual(second.cards[0].childRemIds);
  });
  it('reuses a daily leaf and appends complete batches while leaving undated content and other days untouched',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true);
    const old={...plan(),sourceId:'undated-source',documentPath:['摘录']};
    const legacy:any=await adapter.export({plan:old,newSource:true});
    const previousContent=clipNodeSnapshot(legacy),previousBlank=structuredClone(legacy.batchSeparator);
    const day={...plan(),sourceId:'daily-first',documentPath:['摘录','9.21']};
    const next={...day,sourceId:'daily-second'};
    const [first,second]:any[]=await Promise.all([adapter.export({plan:day,newSource:true}),adapter.export({plan:next,newSource:true})]);
    expect(first.documentRemId).toBe(second.documentRemId);
    expect(second.documentChildren).toEqual([first.cards[0].remIds[0],first.batchSeparator.remId,second.cards[0].remIds[0],second.batchSeparator.remId]);
    const leaf=sdk.nodes.get(first.documentRemId)!;
    expect(leaf.text).toEqual(['9.21']);expect(leaf.parent).toBe(legacy.documentRemId);
    expect(leaf.document).toBe(true);expect(leaf.backText).toBeUndefined();expect(leaf.direction).toBe('none');
    expect(await leaf.isCardItem()).toBe(false);expect(await leaf.getCards()).toEqual([]);
    const other:any=await adapter.export({plan:{...day,sourceId:'next-day',documentPath:['摘录','9.22']},newSource:true});
    expect(other.documentRemId).not.toBe(first.documentRemId);
    expect((await adapter.readback(day) as any).documentChildren).toEqual(second.documentChildren);
    const older:any=await adapter.readback(old);
    expect(clipNodeSnapshot(older)).toEqual(previousContent);expect(older.batchSeparator).toEqual(previousBlank);
    const writes=sdk.writes,count=sdk.nodes.size;
    expect(clipIdentity(await adapter.export(day) as any)).toEqual(clipIdentity(first));
    expect(sdk.writes).toBe(writes);expect(sdk.nodes.size).toBe(count);
  });
  it('refuses changing the date on a recorded source without moving or recreating its original batch',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true);
    const p={...plan(),documentPath:['摘录','9.20']};
    const original:any=await adapter.export({plan:p,newSource:true});
    const writes=sdk.writes,count=sdk.nodes.size;
    await expect(adapter.readback({...p,documentPath:['摘录','9.21']})).rejects.toThrow();
    await expect(adapter.export({...p,documentPath:['摘录','9.21']})).rejects.toThrow();
    expect(clipIdentity(await adapter.readback(p) as any)).toEqual(clipIdentity(original));
    expect(sdk.writes).toBe(writes);expect(sdk.nodes.size).toBe(count);
  });
  it('does not backfill legacy journals or create blanks during readback',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true),p=plan();
    const result:any=await adapter.export({plan:p,newSource:true});
    const key=[...sdk.storage.keys()].find(key=>key.startsWith('knowledge-clipper.attempt.'))!;
    const record=sdk.storage.get(key) as any;delete record.batchSeparator;sdk.storage.set(key,record);
    sdk.nodes.delete(result.batchSeparator.remId);
    sdk.nodes.get(result.documentRemId)!.children=sdk.nodes.get(result.documentRemId)!.children.filter(id=>id!==result.batchSeparator.remId);
    const count=sdk.nodes.size,writes=sdk.writes;
    expect(await adapter.readback(p)).not.toHaveProperty('batchSeparator');
    expect(await adapter.export(p)).not.toHaveProperty('batchSeparator');
    expect(sdk.nodes.size).toBe(count);expect(sdk.writes).toBe(writes);
  });
  it('preserves an existing spacing identity during an idempotent journal adoption',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true),p=plan();
    const result:any=await adapter.export({plan:p,newSource:true});
    const count=sdk.nodes.size;
    const adopted:any=await adapter.adopt({plan:p,expected:clipIdentity(result),verifiedNodes:clipNodeSnapshot(result)});
    expect(adopted.batchSeparator).toEqual(result.batchSeparator);expect(sdk.nodes.size).toBe(count);
    const blank=sdk.nodes.get(result.batchSeparator.remId)!;blank.text=['user-added content'];
    const writes=sdk.writes;
    await expect(adapter.adopt({plan:p,expected:clipIdentity(result),verifiedNodes:clipNodeSnapshot(result)})).rejects.toThrow('separator');
    expect(sdk.writes).toBe(writes);
  });
  for (const mutation of ['text','back','children','parent','document','type','item','extra','practice','order','missing'] as const) {
    it(`rejects a ${mutation} change to the blank without rewriting content or adding a replacement`,async()=>{
      const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true),p=plan();
      const result:any=await adapter.export({plan:p,newSource:true}),blank=sdk.nodes.get(result.batchSeparator.remId)!;
      if(mutation==='text')blank.text=['new user content'];
      if(mutation==='back')blank.backText=['new user answer'];
      if(mutation==='children')blank.children=['new-child'];
      if(mutation==='parent')blank.parent=result.cards[0].remIds[0];
      if(mutation==='document')blank.document=true;
      if(mutation==='type')blank.type=1;
      if(mutation==='item')blank.item=true;
      if(mutation==='extra')blank.powerups.add(BuiltInPowerupCodes.ExtraCardDetail);
      if(mutation==='practice')blank.enabled=true;
      if(mutation==='order')sdk.nodes.get(result.documentRemId)!.children.reverse();
      if(mutation==='missing')sdk.nodes.delete(blank._id);
      const count=sdk.nodes.size,writes=sdk.writes;
      await expect(adapter.readback(p)).rejects.toThrow();
      await expect(adapter.export(p)).rejects.toThrow();
      expect(sdk.nodes.size).toBe(count);expect(sdk.writes).toBe(writes);
    });
  }
  it('does not treat completed cards as a completed import after an ambiguous separator write',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true),p=plan();
    sdk.failAt(7); // 3 documents, 1 front, 2 answers, then the blank.
    await expect(adapter.export({plan:p,newSource:true})).rejects.toThrow('write failed');
    const attempt=[...sdk.storage.entries()].find(([key])=>key.startsWith('knowledge-clipper.attempt.'))![1] as any;
    expect(attempt).toMatchObject({phase:'writing_content',batchSeparator:{state:'writing'}});
    const count=sdk.nodes.size,writes=sdk.writes;sdk.failAt(0);
    await expect(adapter.readback(p)).rejects.toThrow('separator is incomplete');
    await expect(adapter.export(p)).rejects.toThrow('separator is incomplete');
    expect(sdk.nodes.size).toBe(count);expect(sdk.writes).toBe(writes);
  });
});

describe('pure readback and explicit same-ID spacing finalization',()=>{
  async function pending(){
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true),p=plan();
    p.cards=[{key:'plain.0',kind:'plain',front:[text('first paragraph')],back:[]},{key:'plain.1',kind:'plain',front:[text('second paragraph')],back:[]}];
    sdk.failEnableAt(6);
    await expect(adapter.export({plan:p,newSource:true})).rejects.toThrow('practice setup interrupted');
    sdk.failEnableAt(0);
    const raw:any=await adapter.readback(p);
    return {sdk,adapter,p,raw};
  }
  const request=(p:ClipPlan,raw:any)=>({plan:p,expected:clipIdentity(raw),separatorRemId:raw.batchSeparator.remId,
    verifiedNodes:[...clipNodeSnapshot(raw),...clipNodeSnapshot({cards:[{nodes:[raw.batchSeparator.node]}]})],documentChildren:raw.documentChildren});
  it('refuses adoption of an incomplete spaced batch without changing its recoverable journal',async()=>{
    const f=await pending(),count=f.sdk.nodes.size,writes=f.sdk.writes;
    const journal=JSON.stringify([...f.sdk.storage]);
    await expect(f.adapter.adopt({plan:f.p,expected:clipIdentity(f.raw),verifiedNodes:clipNodeSnapshot(f.raw)}))
      .rejects.toThrow('Incomplete batch spacing');
    expect(JSON.stringify([...f.sdk.storage])).toBe(journal);expect(f.sdk.writes).toBe(writes);expect(f.sdk.nodes.size).toBe(count);
    expect(await f.adapter.readback(f.p)).toEqual(f.raw);
    const recovered:any=await f.adapter.finalizeSpacing(request(f.p,f.raw));
    expect(recovered).not.toHaveProperty('batchSeparatorPending');expect(clipIdentity(recovered)).toEqual(clipIdentity(f.raw));
    expect(recovered.batchSeparator.remId).toBe(f.raw.batchSeparator.remId);expect(f.sdk.nodes.size).toBe(count);
  });
  it('returns incomplete spacing as pending evidence and finalizes the recorded blank without another create',async()=>{
    const f=await pending(),count=f.sdk.nodes.size,writes=f.sdk.writes;
    expect(f.raw).toMatchObject({batchSeparatorPending:true,spacingRecovery:{eligible:true,kind:'finalize-batch-separator'}});
    expect(f.raw.batchSeparator.node.practiceEnabled).toBe(true);
    expect(await f.adapter.readback(f.p)).toEqual(f.raw);expect(f.sdk.writes).toBe(writes);
    const result:any=await f.adapter.finalizeSpacing(request(f.p,f.raw));
    expect(result).not.toHaveProperty('batchSeparatorPending');expect(result.batchSeparator.node.practiceEnabled).toBe(false);
    expect(clipIdentity(result)).toEqual(clipIdentity(f.raw));expect(result.batchSeparator.remId).toBe(f.raw.batchSeparator.remId);expect(f.sdk.nodes.size).toBe(count);
    const after=f.sdk.writes;
    await f.adapter.finalizeSpacing(request(f.p,result));await f.adapter.readback(f.p);await f.adapter.export(f.p);
    expect(f.sdk.nodes.size).toBe(count);expect(f.sdk.writes).toBe(after);
  });
  it('restores only the contiguous incomplete batch order, retaining all neighboring Rems and original identities',async()=>{
    const f=await pending(),directory=f.sdk.nodes.get(f.raw.documentRemId)!;
    const before=await f.sdk.plugin.rem.createRem(),after=await f.sdk.plugin.rem.createRem();
    await before!.setText(['unrelated before']);await before!.setParent(directory._id,0);
    await after!.setText(['unrelated after']);await after!.setParent(directory._id,directory.children.length);
    const batch=f.raw.documentChildren as string[];
    directory.children=[before!._id,...[...batch].reverse(),after!._id];
    const raw:any=await f.adapter.readback(f.p),count=f.sdk.nodes.size;
    const result:any=await f.adapter.finalizeSpacing(request(f.p,raw));
    expect(result.documentChildren).toEqual([before!._id,...batch,after!._id]);
    expect(f.sdk.nodes.size).toBe(count);expect(clipIdentity(result)).toEqual(clipIdentity(raw));
    expect(before!.text).toEqual(['unrelated before']);expect(after!.text).toEqual(['unrelated after']);
  });
  it('can attach only a recorded empty orphan into its exact batch position',async()=>{
    const f=await pending(),blank=f.sdk.nodes.get(f.raw.batchSeparator.remId)!;
    await blank.setParent(null);
    const raw:any=await f.adapter.readback(f.p),count=f.sdk.nodes.size;
    const result:any=await f.adapter.finalizeSpacing(request(f.p,raw));
    expect(result.documentChildren).toEqual(f.raw.documentChildren);expect(blank.parent).toBe(result.documentRemId);expect(f.sdk.nodes.size).toBe(count);
  });
  for(const mutation of ['text','back','child','type','cross-directory','interleaved','identity','snapshot','completed-journal'] as const){
    it(`refuses ${mutation} without replacing or modifying recorded content`,async()=>{
      const f=await pending(),req=request(f.p,f.raw),blank=f.sdk.nodes.get(f.raw.batchSeparator.remId)!;
      if(mutation==='text')blank.text=['user edit'];
      if(mutation==='back')blank.backText=['user answer'];
      if(mutation==='child')blank.children=['child'];
      if(mutation==='type')blank.type=1;
      if(mutation==='cross-directory')blank.parent=f.raw.rootRemId;
      if(mutation==='interleaved')f.sdk.nodes.get(f.raw.documentRemId)!.children.splice(1,0,'user-node');
      if(mutation==='identity')req.separatorRemId='another-id';
      if(mutation==='snapshot')req.verifiedNodes[0].text[0].bold=true;
      if(mutation==='completed-journal'){
        const key=[...f.sdk.storage.keys()].find(key=>key.startsWith('knowledge-clipper.attempt.'))!;
        const attempt=f.sdk.storage.get(key) as any;attempt.phase='complete';f.sdk.storage.set(key,attempt);
      }
      const count=f.sdk.nodes.size,writes=f.sdk.writes;
      await expect(f.adapter.finalizeSpacing(req)).rejects.toThrow();
      expect(f.sdk.nodes.size).toBe(count);expect(f.sdk.writes).toBe(writes);
    });
  }
  it('detects an edit during reordering before another node mutation',async()=>{
    const f=await pending(),directory=f.sdk.nodes.get(f.raw.documentRemId)!;
    directory.children.reverse();const raw:any=await f.adapter.readback(f.p);
    const second=f.sdk.nodes.get(raw.cards[1].remIds[0])!;
    f.sdk.afterSetParent(()=>{second.text[0].b=true;});
    const count=f.sdk.nodes.size;
    await expect(f.adapter.finalizeSpacing(request(f.p,raw))).rejects.toThrow('snapshot changed');
    expect(second.text[0].b).toBe(true);expect(f.sdk.nodes.size).toBe(count);
  });
});

describe('ordinary structure without disabled flashcard dividers',()=>{
  async function legacy(){
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true),p=plan();
    const first:any=await adapter.export({plan:p,newSource:true});
    const key=[...sdk.storage.keys()].find(key=>key.startsWith('knowledge-clipper.attempt.'))!;
    const attempt=sdk.storage.get(key) as any;delete attempt.batchSeparator.plainVersion;sdk.storage.set(key,attempt);
    const targetRemIds=[...first.directoryNodes.map((node:any)=>node.remId),first.batchSeparator.remId];
    for(const id of targetRemIds){const rem=sdk.nodes.get(id)!;await rem.setBackText([]);await rem.setPracticeDirection('forward');}
    const raw:any=await adapter.readback(p);
    return {sdk,adapter,p,raw,targetRemIds};
  }
  const request=(f:any,raw=f.raw)=>({plan:f.p,expected:clipIdentity(raw),targetRemIds:f.targetRemIds,verifiedNodes:clipStructureSnapshot(raw)});
  it('creates directories and the blank with no back field, no direction, and no cards',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true),raw:any=await adapter.export({plan:plan(),newSource:true});
    for(const node of [...raw.directoryNodes,raw.batchSeparator.node]){
      expect(node).toMatchObject({backText:[],rawBackText:null,backTextState:'absent',practiceDirection:'none',practiceEnabled:false,
        remType:0,isCardItem:false,isExtraCardDetail:false,isMultiLineCard:false,cardIds:[],cardTypes:[]});
      expect(sdk.nodes.get(node.remId)!.backText).toBeUndefined();
    }
    expect(raw.cards[0].cardIds).toHaveLength(1);
  });
  it('keeps real back absence and empty-back states distinct and readback never repairs them',async()=>{
    const f=await legacy(),writes=f.sdk.writes;
    expect(f.raw.batchSeparator.node).toMatchObject({backTextState:'present',rawBackText:[],practiceEnabled:false,practiceDirection:'forward',cardIds:[]});
    expect(await f.adapter.readback(f.p)).toEqual(f.raw);expect(f.sdk.writes).toBe(writes);
  });
  it('preserves a real null back as absent content and never calls the unsupported undefined setter',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true),p=plan(),raw:any=await adapter.export({plan:p,newSource:true});
    sdk.nodes.get(raw.batchSeparator.remId)!.backText=null;
    const read:any=await adapter.readback(p);expect(read.batchSeparator.node).toMatchObject({rawBackText:null,backTextState:'null'});
    await expect(sdk.plugin.rem.findOne(raw.batchSeparator.remId).then(rem=>rem!.setBackText(undefined))).rejects.toThrow('Required');
  });
  it('repairs exact empty structure properties in place, keeps content and order, and is idempotent',async()=>{
    const f=await legacy(),count=f.sdk.nodes.size,content=clipNodeSnapshot(f.raw),children=[...f.raw.documentChildren];
    const result:any=await f.adapter.repairPlainStructure(request(f));
    expect(clipIdentity(result)).toEqual(clipIdentity(f.raw));expect(clipNodeSnapshot(result)).toEqual(content);
    expect(result.documentChildren).toEqual(children);expect(result.batchSeparator.remId).toBe(f.raw.batchSeparator.remId);
    for(const node of [...result.directoryNodes,result.batchSeparator.node])expect(node).toMatchObject({backTextState:'absent',practiceDirection:'none',practiceEnabled:false});
    expect(f.sdk.nodes.size).toBe(count);const writes=f.sdk.writes;
    await f.adapter.repairPlainStructure(request(f,result));expect(f.sdk.writes).toBe(writes);
  });
  for(const mutation of ['back','front','type','child','parent','order','snapshot','target-content','target-unrelated','source','title'] as const){
    it(`refuses a ${mutation} change before writing any structure property`,async()=>{
      const f=await legacy(),req=request(f),blank=f.sdk.nodes.get(f.raw.batchSeparator.remId)!,directory=f.sdk.nodes.get(f.raw.documentRemId)!;
      if(mutation==='back')directory.backText=['user answer'];
      if(mutation==='front')f.sdk.nodes.get(f.raw.cards[0].remIds[0])!.text[0].bold=true;
      if(mutation==='type')directory.type=1;
      if(mutation==='child')blank.children=['user child'];
      if(mutation==='parent')blank.parent=f.raw.rootRemId;
      if(mutation==='order')directory.children.reverse();
      if(mutation==='snapshot')req.verifiedNodes[0].text[0].bold=true;
      if(mutation==='target-content')req.targetRemIds=[f.raw.cards[0].remIds[0]];
      if(mutation==='target-unrelated')req.targetRemIds=['unrelated-id'];
      if(mutation==='source')req.expected.documentRemId='other-directory';
      if(mutation==='title')directory.text=['user title'];
      const writes=f.sdk.writes,count=f.sdk.nodes.size;
      await expect(f.adapter.repairPlainStructure(req)).rejects.toThrow();
      expect(f.sdk.writes).toBe(writes);expect(f.sdk.nodes.size).toBe(count);
    });
  }
  it('refuses real card roles even with a matching current raw snapshot',async()=>{
    const f=await legacy();f.sdk.nodes.get(f.raw.documentRemId)!.powerups.add(BuiltInPowerupCodes.MultiLineCard);
    const raw:any=await f.adapter.readback(f.p),writes=f.sdk.writes;
    await expect(f.adapter.repairPlainStructure(request(f,raw))).rejects.toThrow('card properties');expect(f.sdk.writes).toBe(writes);
  });
  it('refuses a directory answer even with a matching current raw snapshot',async()=>{
    const f=await legacy();f.sdk.nodes.get(f.raw.documentRemId)!.backText=['user answer'];
    const raw:any=await f.adapter.readback(f.p),writes=f.sdk.writes;
    await expect(f.adapter.repairPlainStructure(request(f,raw))).rejects.toThrow('user content');expect(f.sdk.writes).toBe(writes);
  });
  it('does not weaken a newly completed plain separator when its divider is changed later',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true),p=plan(),raw:any=await adapter.export({plan:p,newSource:true});
    sdk.nodes.get(raw.batchSeparator.remId)!.backText=[];const writes=sdk.writes;
    await expect(adapter.readback(p)).rejects.toThrow('separator');expect(sdk.writes).toBe(writes);
  });
});

describe('isolated public editor fixture driver',()=>{
  const publicPlan=()=>{const p=plan(),id='c5e6b690611b4b018437a45e79413ee8';
    p.sourceId=`knowledgeclipper.plain-structure-canary.${id}`;p.documentPath=['验收',`普通节点-${id.slice(0,10)}`,'新增目录'];
    p.cards[0].front=[text(`KnowledgeClipper public plain-structure test ${id}\nPublic question.`)];return p;};
  it('does not accept an ordinary source as public fixture preparation',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true),writes=sdk.writes;
    await expect(verifyEmptyBackEditor(sdk.plugin,adapter,{plan:plan()})).rejects.toThrow('Only the isolated public');
    expect(sdk.writes).toBe(writes);
  });
  it('prepares an empty back on the existing public separator, removes it via editor, and preserves all IDs',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true),p=publicPlan(),raw:any=await adapter.export({plan:p,newSource:true});
    const count=sdk.nodes.size;
    const result:any=await verifyEmptyBackEditor(sdk.plugin,adapter,{plan:p,expected:clipIdentity(raw),separatorRemId:raw.batchSeparator.remId});
    expect(result.editorRestored).toBe(true);expect(clipIdentity(result.result)).toEqual(clipIdentity(raw));
    expect(clipNodeSnapshot(result.result)).toEqual(clipNodeSnapshot(raw));expect(sdk.nodes.size).toBe(count);
    expect(await sdk.plugin.window.getURL()).toBe('original');expect(await sdk.plugin.editor.getSelection()).toBeUndefined();
  });
  it('refuses unexpected focused editor text and retains the same public IDs for a later safe retry',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true),p=publicPlan(),raw:any=await adapter.export({plan:p,newSource:true});
    const request={plan:p,expected:clipIdentity(raw),separatorRemId:raw.batchSeparator.remId},count=sdk.nodes.size;
    const read=sdk.plugin.editor.getFocusedEditorText;sdk.plugin.editor.getFocusedEditorText=async()=>['unexpected user text'];
    await expect(verifyEmptyBackEditor(sdk.plugin,adapter,request)).rejects.toThrow('editor content differs');
    expect(await sdk.plugin.window.getURL()).toBe('original');expect(sdk.nodes.get(raw.batchSeparator.remId)!.backText).toEqual([]);
    sdk.plugin.editor.getFocusedEditorText=read;
    const result:any=await verifyEmptyBackEditor(sdk.plugin,adapter,request);
    expect(clipIdentity(result.result)).toEqual(clipIdentity(raw));expect(sdk.nodes.size).toBe(count);
  });
});

describe('KnowledgeClipper official SDK adapter',()=>{
  it('lists only immediate excerpt Documents and imports to the exact selected ID',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true,false);
    const otherRoot=await sdk.plugin.rem.createRem();await otherRoot!.setText(['零碎知识剪藏']);await otherRoot!.setIsDocument(true);
    const root=await sdk.plugin.rem.createRem(),excerpt=await sdk.plugin.rem.createRem();
    await root!.setText(['零碎知识剪藏']);await root!.setIsDocument(true);
    await excerpt!.setText(['摘录']);await excerpt!.setParent(root!._id);await excerpt!.setIsDocument(true);
    const selected=await sdk.plugin.rem.createRem(),duplicate=await sdk.plugin.rem.createRem(),ordinary=await sdk.plugin.rem.createRem();
    for(const rem of [selected!,duplicate!]){await rem.setText(['科研论文']);await rem.setParent(excerpt!._id);await rem.setIsDocument(true);}
    await ordinary!.setText(['普通笔记']);await ordinary!.setParent(excerpt!._id);
    const listed:any=await adapter.listExcerptDocuments({rootRemId:root!._id});
    expect(listed.documents.map((item:any)=>item.id).sort()).toEqual([selected!._id,duplicate!._id].sort());
    const p={...plan(),rootRemId:root!._id,documentPath:['摘录','科研论文'],targetDocumentId:selected!._id};
    const result:any=await adapter.export(p);
    expect(result.documentRemId).toBe(selected!._id);
    expect((await adapter.readback(p) as any).documentRemId).toBe(selected!._id);
    const other=await sdk.plugin.rem.createRem();await other!.setText(['其他']);await other!.setIsDocument(true);
    await selected!.setParent(other!._id);
    await expect(adapter.readback(p)).rejects.toThrow('directory changed');
  });
  it('keeps a multiline front in one text Rem, answer lines in multiline children, and only directories as documents',async()=>{
    const sdk=fakeSdk(), adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true, false);
    const result:any=await adapter.export(plan());
    const card=result.cards[0];
    expect(card.remIds).toHaveLength(1);
    expect(card.nodes[0].parts).toEqual(plan().cards[0].front);
    expect(card.nodes[0].children).toEqual(card.childRemIds);
    expect(card.nodes[0].isCardItem).toBe(false);
    expect(card.nodes[0].isMultiLineCard).toBe(false);
    expect(card.nodes.slice(1).map((n:any)=>n.isCardItem)).toEqual([true,true]);
    expect(card.nodes.every((n:any)=>n.remType===0 && !n.isDocument)).toBe(true);
    expect(card.cardIds).toHaveLength(1);
    expect(sdk.nodes.get(result.rootRemId)?.document).toBe(true);
    expect(sdk.nodes.get(result.documentRemId)?.document).toBe(true);
    const directory=sdk.nodes.get(result.documentRemId)!;
    expect(await Promise.all(directory.children.map(id=>sdk.nodes.get(id)!.isCardItem()))).toEqual([false,false]);
    directory.enabled=true;
    expect(await directory.getCards()).toEqual([]);
  });
  it('reads legacy basic fronts marked as answer items without changing their identity or role',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true,false);
    const first:any=await adapter.export(plan());
    const front=sdk.nodes.get(first.cards[0].remIds[0])!;
    await front.addPowerup(BuiltInPowerupCodes.MultiLineCard);
    const writes=sdk.writes;
    const read:any=await adapter.readback(plan());
    expect(clipIdentity(read)).toEqual(clipIdentity(first));
    expect(read.cards[0].nodes[0].isCardItem).toBe(true);
    expect(read.cards[0].nodes[0].isMultiLineCard).toBe(true);
    expect(sdk.writes).toBe(writes);
  });
  it('reuses persisted source/card identity across adapter instances and readback performs no writes',async()=>{
    const sdk=fakeSdk(), first:any=await new KnowledgeClipperAdapter(sdk.plugin,()=>true, false).export(plan());
    const count=sdk.nodes.size, writes=sdk.writes;
    const second:any=await new KnowledgeClipperAdapter(sdk.plugin,()=>true, false).readback(plan());
    expect(sdk.writes).toBe(writes);
    expect(second.cards[0].front).toEqual(first.cards[0].front);
    const third:any=await new KnowledgeClipperAdapter(sdk.plugin,()=>true, false).export(plan());
    expect(sdk.nodes.size).toBe(count);
    expect(third.cards[0].reused).toBe(true);
    expect(third.cards[0].cardIds).toEqual(first.cards[0].cardIds);
  });
  it('makes plain excerpts ordinary unpracticed Rems and preserves TeX line breaks',async()=>{
    const sdk=fakeSdk(), p=plan();p.cards=[{key:'plain.0',kind:'plain',front:[{kind:'latex',text:'a\\\\\nb',block:true}],back:[]}];
    const result:any=await new KnowledgeClipperAdapter(sdk.plugin,()=>true, false).export(p);
    expect(result.cards[0].nodes).toHaveLength(1);
    expect(result.cards[0].front).toEqual(p.cards[0].front);
    expect(result.cards[0].cardIds).toEqual([]);
  });
  it('imports a 31-paragraph ordinary excerpt without a 30-card cutoff',async()=>{
    const sdk=fakeSdk(),p=plan();
    p.cards=Array.from({length:31},(_,i)=>({key:`plain.${i}`,kind:'plain' as const,front:[text(`paragraph ${i+1}`)],back:[]}));
    const adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true, false),result:any=await adapter.export(p);
    expect(result.cards).toHaveLength(31);
    expect(result.cards.every((c:any)=>c.cardIds.length===0 && c.nodes.length===1)).toBe(true);
    expect(result.cards.map((c:any)=>c.front[0].text)).toEqual(p.cards.map(c=>c.front[0].text));
    p.cards=Array.from({length:1001},(_,i)=>({...p.cards[0],key:`plain.${i}`}));
    const count=sdk.nodes.size;
    expect(()=>adapter.export(p)).toThrow('Invalid KnowledgeClipper');
    expect(sdk.nodes.size).toBe(count);
  });
  it('keeps same-number text and formula clozes linked, each group one card, and extra answers as non-card details',async()=>{
    const sdk=fakeSdk(),p=plan();p.cards=[{key:'cloze',kind:'cloze',front:[
      {kind:'cloze',text:'one',clozeId:'1',hint:'hint'},text(' and '),
      {kind:'latex',text:'x^2',block:false,clozeId:'1'},text(' then '),{kind:'cloze',text:'two',clozeId:'2'}],
      back:[[text('extra answer')]]}];
    const result:any=await new KnowledgeClipperAdapter(sdk.plugin,()=>true, false).export(p);
    expect(result.cards[0].cardIds).toHaveLength(2);
    expect(result.cards[0].front).toEqual(p.cards[0].front);
    expect(result.cards[0].nodes[1].isExtraCardDetail).toBe(true);
    expect(result.cards[0].nodes[1].isCardItem).toBe(false);
  });
  it('creates cloze cards with no separate answer',async()=>{
    const sdk=fakeSdk(),p=plan();p.cards=[{key:'cloze',kind:'cloze',front:[{kind:'cloze',text:'one',clozeId:'1'}],back:[]}];
    const result:any=await new KnowledgeClipperAdapter(sdk.plugin,()=>true, false).export(p);
    expect(result.cards[0].cardIds).toHaveLength(1);
  });
  it('rejects disabled writes but permits readback',async()=>{
    const sdk=fakeSdk();await new KnowledgeClipperAdapter(sdk.plugin,()=>true, false).export(plan());
    const adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>false, false);
    await expect(adapter.export(plan())).rejects.toThrow('disabled');
    await expect(adapter.readback(plan())).resolves.toHaveProperty('cards');
  });
  it('rejects duplicate keys, unsafe images and invalid plain/basic cards before creating anything',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true, false);
    const p=plan();p.cards.push(p.cards[0]);expect(()=>adapter.export(p)).toThrow('duplicate');
    const bad=plan();bad.cards[0].front=[{kind:'image',uri:'https://example.com/private.png',sha256:'0'.repeat(64)}];
    expect(()=>adapter.export(bad)).toThrow('local file');
    const empty=plan();empty.cards[0].back=[];expect(()=>adapter.export(empty)).toThrow('contents');
    expect(sdk.nodes.size).toBe(0);
  });
  it('refuses changed plans and deleted/imported content rather than creating replacements',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true, false),r:any=await adapter.export(plan());
    const count=sdk.nodes.size,p=plan();p.cards[0].front=[text('changed')];
    await expect(adapter.export(p)).rejects.toThrow('plan changed');
    sdk.nodes.get(r.cards[0].remIds[0])!.text=['user edit'];
    await expect(adapter.export(plan())).rejects.toThrow('differs');
    expect(sdk.nodes.size).toBe(count);
    sdk.nodes.delete(r.cards[0].remIds[0]);
    await expect(adapter.export(plan())).rejects.toThrow('missing');
    expect(sdk.nodes.size).toBe(count-1);
  });
  it('persists interrupted writes and refuses ambiguous automatic recreation',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true, false);sdk.failAt(5);
    await expect(adapter.export(plan())).rejects.toThrow('write failed');
    const count=sdk.nodes.size;sdk.failAt(0);
    await expect(adapter.export(plan())).rejects.toThrow('incomplete');
    expect(sdk.nodes.size).toBe(count);
  });
  it('rejects a changed basic practice direction even when card identities remain unchanged',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true, false),r:any=await adapter.export(plan());
    const front=sdk.nodes.get(r.cards[0].remIds[0])!,count=sdk.nodes.size,writes=sdk.writes;
    for (const direction of ['backward','both','none']) {
      front.direction=direction;
      expect((await front.getCards()).map(c=>c._id)).toEqual(r.cards[0].cardIds);
      await expect(adapter.readback(plan())).rejects.toThrow('differs');
      await expect(adapter.export(plan())).rejects.toThrow('differs');
    }
    expect(sdk.nodes.size).toBe(count);
    expect(sdk.writes).toBe(writes);
  });
  it('rejects disabled practice for card fronts and enabled practice for plain excerpts',async()=>{
    for (const kind of ['basic','cloze','plain'] as const) {
      const sdk=fakeSdk(),p=plan();
      if (kind==='cloze') p.cards=[{key:'cloze',kind,front:[{kind:'cloze',text:'answer',clozeId:'1'}],back:[]}];
      if (kind==='plain') p.cards=[{key:'plain',kind,front:[text('ordinary text')],back:[]}];
      const adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true, false),r:any=await adapter.export(p);
      expect(r.cards[0].nodes[0].practiceEnabled).toBe(kind!=='plain');
      sdk.nodes.get(r.cards[0].remIds[0])!.enabled=kind==='plain';
      const writes=sdk.writes;
      await expect(adapter.readback(p)).rejects.toThrow('differs');
      await expect(adapter.export(p)).rejects.toThrow('differs');
      expect(sdk.writes).toBe(writes);
    }
  });
  it('serializes concurrent requests for the same key',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true, false);
    const [a,b,read]:any[]=await Promise.all([adapter.export(plan()),adapter.export(plan()),adapter.readback(plan())]);
    expect(a.cards[0].remIds).toEqual(b.cards[0].remIds);
    expect(b.cards[0].reused).toBe(true);
    expect(read.cards[0].remIds).toEqual(a.cards[0].remIds);
  });
  it('completes an interrupted empty directory and retries the same source after a positive pre-content failure checkpoint',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true, false);
    sdk.failDocumentAt(2);
    const first:any=await adapter.export(plan());
    expect(first).toMatchObject({schemaVersion:plan().schemaVersion,sourceId:plan().sourceId,notStarted:true,error:'directory setup failed'});
    const directory=sdk.nodes.get('rem-2')!;
    expect(directory.document).toBe(false);
    expect(directory.children).toEqual([]);
    const writes=sdk.writes;
    expect(await adapter.readback(plan())).toEqual(first);
    expect(sdk.writes).toBe(writes);
    sdk.failDocumentAt(0);
    const retried:any=await adapter.export(plan());
    expect(retried.cards).toHaveLength(1);
    expect(sdk.nodes.get('rem-2')?.document).toBe(true);
    expect(sdk.nodes.get('rem-2')?.enabled).toBe(false);
    expect([...sdk.nodes.values()].filter(n=>n.text.join('')==='摘录')).toHaveLength(1);
  });
  it('refuses to promote matching ordinary Rems with meaningful data',async()=>{
    for(const changed of ['back','children','cards','tags','aliases','table','type','format'] as const){
      const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true, false);
      sdk.failDocumentAt(2);await adapter.export(plan());sdk.failDocumentAt(0);
      const rem=sdk.nodes.get('rem-2')!;
      if(changed==='back')rem.backText=['meaningful answer'];
      if(changed==='children')rem.children=['child'];
      if(changed==='cards')rem.powerups.add(BuiltInPowerupCodes.MultiLineCard);
      if(changed==='tags')rem.tags=['tag'];
      if(changed==='aliases')rem.aliases=['alias'];
      if(changed==='table')rem.table=true;
      if(changed==='type')rem.type=1;
      if(changed==='format')rem.text=[{i:'m',text:'摘录',b:true}];
      const result:any=await adapter.export(plan());
      expect(result).toMatchObject({notStarted:true});
      expect(result.error).toContain('existing nonempty Rem');
      expect(rem.document).toBe(false);
      expect(sdk.nodes.size).toBe(2);
    }
  });
  it('does not infer notStarted from missing storage or from ambiguous preparation/content stages',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true, false);
    const writes=sdk.writes;
    await expect(adapter.readback(plan())).rejects.toThrow('status remains unknown');
    expect(sdk.writes).toBe(writes);
    sdk.failDocumentAt(2);await adapter.export(plan());
    const [key,record]=[...sdk.storage.entries()].find(([key])=>key.startsWith('knowledge-clipper.attempt.'))!;
    for(const phase of ['preparing','writing_content','complete']){
      sdk.storage.set(key,{...(record as any),phase});
      const before=sdk.writes;
      await expect(adapter.readback(plan())).rejects.toThrow('status remains unknown');
      await expect(adapter.export(plan())).rejects.toThrow('stage is uncertain');
      expect(sdk.writes).toBe(before);
    }
    sdk.storage.set(key,record);
    const changed=plan();changed.cards[0].front=[text('changed')];
    await expect(adapter.readback(changed)).rejects.toThrow('attempt differs');
  });
  it('rejects partial content reservations on readback and repeat export without creating replacements',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true, false),p=plan();
    p.cards.push({...p.cards[0],key:'card.1'});
    await adapter.export(p);
    const key=[...sdk.storage.keys()].find(key=>key.startsWith('knowledge-clipper.v1.'))!;
    sdk.storage.delete(key);
    const writes=sdk.writes,count=sdk.nodes.size;
    await expect(adapter.readback(p)).rejects.toThrow('incomplete');
    await expect(adapter.export(p)).rejects.toThrow('incomplete');
    expect(sdk.writes).toBe(writes);expect(sdk.nodes.size).toBe(count);
  });
  it('retains image order and decodes actual managed URLs without trusting expected hashes',()=>{
    const hash='0'.repeat(64);
    const raw=partsToRichText([{kind:'image',uri:'file:///D:/a.png',sha256:hash},text('\n'),{kind:'image',uri:'file:///D:/b.png',sha256:hash}]);
    (raw[0] as any).url='%LOCAL_FILE%actual-a.png';
    const decoded=richTextToParts(raw);
    expect(decoded.map(p=>p.uri)).toEqual(['%LOCAL_FILE%actual-a.png',undefined,'file:///D:/b.png']);
    expect(decoded[0].sha256).toBeUndefined();
  });
});

describe('native SDK image preparation and same-ID media recovery',()=>{
  const image=(name:string)=>({kind:'image' as const,uri:`file:///D:/images/${name}.png`,sha256:'a'.repeat(64)});
  const imagePlan=():ClipPlan=>({...plan(),cards:[{key:'images',kind:'basic',front:[text('literal :: >>\n'),image('a'),image('b')],
    back:[[text('answer'),image('c')],[image('d')]]}]});
  const prepared=(p:ClipPlan)=>p.cards.flatMap(card=>[...card.front,...card.back.flat()]).filter(part=>part.kind==='image')
    .map(part=>({uri:part.uri!,sha256:part.sha256!,richText:{i:'i' as const,url:`%LOCAL_FILE%${part.uri!.split('/').pop()}`,width:160,title:'official title'}}));
  async function existing(){
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true, false),p=imagePlan();
    const result:any=await adapter.export({plan:p,preparedImages:prepared(p)});
    result.cards[0].nodes.forEach((node:any,ri:number)=>{
      const parts=[p.cards[0].front,...p.cards[0].back][ri];sdk.nodes.get(node.remId)!.text=partsToRichText(parts) as any[];
    });
    const raw:any=await adapter.readback(p);
    return {sdk,adapter,p,raw,request:{plan:p,expected:clipIdentity(raw),verifiedNodes:clipNodeSnapshot(raw),preparedImages:prepared(p)}};
  }
  it('uploads exactly one data image through the official parser and retains its complete metadata',async()=>{
    vi.stubGlobal('crypto',webcrypto);
    try{
      const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true, false),bytes=Buffer.from('89504e470d0a1a0a0102','hex');
      const input={uri:'file:///D:/images/%E4%B8%AD%20%28x%29%23.png',sha256:createHash('sha256').update(bytes).digest('hex'),dataUri:`data:image/png;base64,${bytes.toString('base64')}`};
      expect(await adapter.prepareImage(input)).toMatchObject({uri:input.uri,sha256:input.sha256,richText:{i:'i',url:'%LOCAL_FILE%parsed-1.png',width:160,height:96,title:'official title'}});
      expect(sdk.parsed).toEqual([`![KnowledgeClipper](${input.dataUri})`]);
      expect(sdk.nodes.size).toBe(0);
      await expect(adapter.prepareImage({...input,sha256:'f'.repeat(64)})).rejects.toThrow('bytes do not match');
      expect(sdk.parsed).toHaveLength(1);
      sdk.parseResult([{i:'i',url:input.dataUri}]);
      await expect(adapter.prepareImage(input)).rejects.toThrow('exactly one image');
      sdk.parseResult([{i:'i',url:'%LOCAL_FILE%a.png'},{i:'i',url:'%LOCAL_FILE%b.png'}]);
      await expect(adapter.prepareImage(input)).rejects.toThrow('exactly one image');
    }finally{vi.unstubAllGlobals();}
  });
  it('records a positive no-content checkpoint before uploading and rejects missing or extra prepared image inputs',async()=>{
    const sdk=fakeSdk(),adapter=new KnowledgeClipperAdapter(sdk.plugin,()=>true, false),p=imagePlan();
    expect(await adapter.export({plan:p,prepareOnly:true})).toMatchObject({notStarted:true,requiresImages:true});
    expect(sdk.nodes.size).toBe(0);
    expect(await adapter.readback(p)).toMatchObject({notStarted:true});
    await expect(adapter.export(p)).rejects.toThrow('preparation is required');
    const inputs=prepared(p);
    await expect(adapter.export({plan:p,preparedImages:[...inputs,inputs[0]]})).rejects.toThrow('Duplicate');
    await expect(adapter.export({plan:p,preparedImages:[...inputs,{...inputs[0],uri:'file:///D:/unexpected.png'}]})).rejects.toThrow('Unexpected');
    expect(sdk.nodes.size).toBe(0);
    const result:any=await adapter.export({plan:p,preparedImages:inputs});
    expect(result.cards[0].nodes[0].text[1]).toMatchObject(inputs[0].richText);
    expect(await adapter.export({plan:p,prepareOnly:true})).toHaveProperty('cards');
  });
  it('updates only original image slots, preserves raw text formatting, and creates no new Rems or cards',async()=>{
    const f=await existing(),front=f.sdk.nodes.get(f.raw.cards[0].remIds[0])!;
    front.text[0].b=true;front.text[1].width=222;front.text[1].title='user title';f.request.verifiedNodes=clipNodeSnapshot(await f.adapter.readback(f.p) as any);
    const count=f.sdk.nodes.size,ids=clipIdentity(f.raw),stored=JSON.stringify([...f.sdk.storage]);
    const result:any=await f.adapter.finalizeMedia(f.request);
    expect(clipIdentity(result)).toEqual(ids);expect(f.sdk.nodes.size).toBe(count);
    expect(front.text[0]).toEqual({i:'m',text:'literal :: >>\n',b:true});
    expect(front.text[1]).toEqual({i:'i',url:'%LOCAL_FILE%a.png',width:222,title:'user title'});
    expect(JSON.stringify([...f.sdk.storage])).toBe(stored);
    const writes=f.sdk.writes;
    await f.adapter.readback(f.p);expect(f.sdk.writes).toBe(writes);expect(f.sdk.parsed).toHaveLength(0);
    await f.adapter.finalizeMedia({plan:f.p,expected:ids,verifiedNodes:clipNodeSnapshot(result),preparedImages:[]});
    expect(f.sdk.writes).toBe(writes);expect(f.sdk.parsed).toHaveLength(0);
  });
  it('resumes partial image finalization on the same IDs and skips all already managed images',async()=>{
    const f=await existing(),front=f.sdk.nodes.get(f.raw.cards[0].remIds[0])!;
    front.text[1].url='%LOCAL_FILE%a.png';front.text[2].url='%LOCAL_FILE%b.png';
    const raw:any=await f.adapter.readback(f.p),writes=f.sdk.writes;
    const result:any=await f.adapter.finalizeMedia({...f.request,verifiedNodes:clipNodeSnapshot(raw),preparedImages:f.request.preparedImages.slice(2)});
    expect(clipIdentity(result)).toEqual(clipIdentity(f.raw));expect(f.sdk.writes-writes).toBe(2);
  });
  it('refuses replaced, swapped, missing or structurally edited images before setText',async()=>{
    for(const mutation of ['uri','swap','text','missing','ids'] as const){
      const f=await existing(),front=f.sdk.nodes.get(f.raw.cards[0].remIds[0])!;
      if(mutation==='uri')front.text[1].url='file:///D:/another.png';
      if(mutation==='swap')[front.text[1],front.text[2]]=[front.text[2],front.text[1]];
      if(mutation==='text')front.text[0].text='user text';
      if(mutation==='missing')f.sdk.nodes.delete(front._id);
      if(mutation==='ids')f.request.expected.cards[0].cardIds=['different'];
      const writes=f.sdk.writes,count=f.sdk.nodes.size;
      await expect(f.adapter.finalizeMedia(f.request)).rejects.toThrow();
      expect(f.sdk.writes).toBe(writes);expect(f.sdk.nodes.size).toBe(count);
    }
  });
  it('does not overwrite edits made between node updates and remains recoverable after a partial write',async()=>{
    const f=await existing(),frontId=f.raw.cards[0].remIds[0],child=f.sdk.nodes.get(f.raw.cards[0].childRemIds[0])!;
    f.sdk.afterSetText(id=>{if(id===frontId)child.text[0].b=true;});
    const count=f.sdk.nodes.size;
    await expect(f.adapter.finalizeMedia(f.request)).rejects.toThrow('snapshot changed');
    expect(child.text[0].b).toBe(true);expect(child.text[1].url).toBe('file:///D:/images/c.png');
    expect(f.sdk.nodes.size).toBe(count);
    f.sdk.afterSetText(()=>{});
    const now:any=await f.adapter.readback(f.p);
    await f.adapter.finalizeMedia({...f.request,verifiedNodes:clipNodeSnapshot(now),preparedImages:f.request.preparedImages.slice(2)});
    expect(child.text[0].b).toBe(true);expect(child.text[1].url).toBe('%LOCAL_FILE%c.png');
  });
});
