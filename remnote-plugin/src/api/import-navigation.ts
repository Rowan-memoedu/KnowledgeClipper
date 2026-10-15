import type {PaneRemWindowTree,ReactRNPlugin,RemIdWindowTree} from '@remnote/plugin-sdk';
import {clipIdentity, validateClipPlan, type KnowledgeClipperAdapter} from './knowledge-clipper';

export const NAVIGATION_CAPABILITY = 'knowledge-clipper.import-navigation.v1';
const same = (a:unknown,b:unknown) => JSON.stringify(a) === JSON.stringify(b);
const pause = () => new Promise<void>(resolve => setTimeout(resolve,75));
type PanePath = Array<'first'|'second'>;

function replacePane(tree:PaneRemWindowTree,paneId:string,documentId:string,path:PanePath=[]):{tree:RemIdWindowTree;target?:PanePath} {
  if('paneId' in tree) {
    if(tree.paneId===paneId)return {tree:documentId,target:path};
    if(!tree.remId)throw new Error('An existing pane cannot be preserved; navigation was skipped');
    return {tree:tree.remId};
  }
  const first=replacePane(tree.first,paneId,documentId,[...path,'first']);
  const second=replacePane(tree.second,paneId,documentId,[...path,'second']);
  return {tree:{...tree,first:first.tree,second:second.tree},target:first.target??second.target};
}

function paneAt(tree:PaneRemWindowTree,path:PanePath) {
  for(const side of path) {
    if('paneId' in tree)return undefined;
    tree=tree[side];
  }
  return 'paneId' in tree?tree:undefined;
}

/** Navigation never writes content or changes the import journal. */
export class ImportNavigation {
  private revision = 0;
  private tail:Promise<unknown> = Promise.resolve();
  private phase = 'idle';
  constructor(private plugin:ReactRNPlugin, private adapter:KnowledgeClipperAdapter,private wait:()=>Promise<void>=pause) {}
  async state() {
    const paneId = await this.plugin.window.getFocusedPaneId();
    const selection = await this.plugin.editor.getSelectedRem();
    return {paneId,documentRemId:await this.plugin.window.getOpenPaneRemId(paneId),
      remIds:selection?.remIds ?? [],focusedRemId:(await this.plugin.focus.getFocusedRem())?._id,navigationPhase:this.phase};
  }
  reveal(value:unknown):Promise<unknown> {
    const revision = ++this.revision;
    const task = this.tail.catch(()=>undefined).then(()=>this.run(value,revision));
    this.tail = task;
    return task;
  }
  private async run(value:unknown,revision:number) {
    const request = value as {plan:unknown;expected:unknown};
    const plan = validateClipPlan(request?.plan);
    this.phase='readback';
    const current:any = await this.adapter.readNavigation(plan);
    if(current.notStarted || current.mediaPending || current.batchSeparatorPending || !same(clipIdentity(current),request.expected))
      throw new Error('Imported node identity is not verified; navigation was skipped');
    const remIds:string[] = current.cards.flatMap((card:any)=>card.remIds);
    if(!remIds.length || new Set(remIds).size!==remIds.length || remIds.includes(current.batchSeparator?.remId))
      throw new Error('Imported content selection is invalid');
    this.phase='resolve-nodes';
    const document = await this.plugin.rem.findOne(current.documentRemId);
    const nodes = await Promise.all(remIds.map(id=>this.plugin.rem.findOne(id)));
    if(!document || !await document.isDocument() || nodes.some(rem=>!rem || rem.parent!==document._id))
      throw new Error('Imported nodes moved or disappeared; navigation was skipped');
    const superseded = () => revision!==this.revision;
    if(superseded())return {revealed:false,superseded:true};
    this.phase='open-document';
    // openRemAsPage/openRemInContext can restore the native application window.
    // Change the internal pane through the official Window API instead.
    const focusedPane=await this.plugin.window.getFocusedPaneId();
    let targetPath:PanePath|undefined;
    if(await this.plugin.window.getOpenPaneRemId(focusedPane)!==document._id) {
      const existing=await this.plugin.window.getCurrentWindowTree();
      const replacement=existing?replacePane(existing,focusedPane,document._id):{tree:document._id,target:[]};
      if(!replacement.target)throw new Error('The focused pane could not be identified; navigation was skipped');
      targetPath=replacement.target;
      await this.plugin.window.setRemWindowTree(replacement.tree);
    }
    this.phase='wait-document';
    const pageDeadline=Date.now()+5000;
    for(;;) {
      if(superseded())return {revealed:false,superseded:true};
      if(targetPath) {
        const tree=await this.plugin.window.getCurrentWindowTree();
        const target=tree&&paneAt(tree,targetPath);
        if(target?.remId===document._id)await this.plugin.window.setFocusedPaneId(target.paneId);
      }
      const pane = await this.plugin.window.getFocusedPaneId();
      if(await this.plugin.window.getOpenPaneRemId(pane)===document._id)break;
      if(Date.now()>=pageDeadline)throw new Error('RemNote did not open the imported document');
      await this.wait();
    }
    if(superseded())return {revealed:false,superseded:true};
    this.phase='verify-selection';
    const selectionDeadline=Date.now()+7000;
    for(;;) {
      if(superseded())return {revealed:false,superseded:true};
      const observed = await this.state();
      if(observed.documentRemId===document._id && same(observed.remIds,remIds)) {
        this.phase='revealed';
        return {revealed:true,sourceId:plan.sourceId,...observed,navigationPhase:this.phase};
      }
      if(Date.now()>=selectionDeadline)break;
      // SDK navigation resolves before the editor tree mounts. Selection can be a
      // no-op during that interval. The document is the ordinary editor's root
      // portal; naming it avoids depending on the previous page's text cursor.
      if(observed.documentRemId===document._id) {
        // Routing can remount the pane after the window-tree call has resolved.
        // Rebind to the live pane before selecting in its active editor.
        await this.plugin.window.setFocusedPaneId(observed.paneId);
        await this.plugin.editor.selectRem(remIds,document._id);
      }
      await this.wait();
    }
    throw new Error('RemNote did not retain the imported block selection');
  }
}
