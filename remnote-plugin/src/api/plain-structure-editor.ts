import type {ReactRNPlugin, PluginRem} from '@remnote/plugin-sdk';

type EditorState = {url:string; paneId:string; paneRemIds:string[]; focusedRemId?:string; portalId?:string; selection:any};
const same = (a:unknown,b:unknown) => JSON.stringify(a) === JSON.stringify(b);
const pause = () => new Promise<void>(resolve => setTimeout(resolve,50));
async function capture(plugin:ReactRNPlugin):Promise<EditorState> {
  return {url:await plugin.window.getURL(),paneId:await plugin.window.getFocusedPaneId(),
    paneRemIds:await plugin.window.getOpenPaneRemIds(),focusedRemId:(await plugin.focus.getFocusedRem())?._id,
    portalId:(await plugin.focus.getFocusedPortal())?._id,selection:await plugin.editor.getSelection()};
}
async function focused(plugin:ReactRNPlugin,id:string):Promise<void> {
  for(let i=0;i<100;i++) { if((await plugin.focus.getFocusedRem())?._id===id)return;await pause(); }
  throw new Error('Official editor did not focus the exact recorded Rem; no text was replaced');
}
async function restore(plugin:ReactRNPlugin,original:EditorState):Promise<void> {
  await plugin.window.setFocusedPaneId(original.paneId);
  if(original.focusedRemId) {
    const rem=await plugin.rem.findOne(original.focusedRemId);
    if(!rem)throw new Error('Original editor Rem disappeared; original source identity retained');
    await plugin.window.openRem(rem);
    await focused(plugin,rem._id);
  } else await plugin.window.setURL(original.url);
  if(original.selection?.type==='Text') {
    const range=original.selection.range;
    await plugin.editor.selectText(original.selection.isReverse?{start:range.end,end:range.start}:range);
  } else if(original.selection?.type==='Rem') {
    await plugin.editor.selectRem(original.selection.remIds,original.portalId);
  } else if(!original.focusedRemId) await plugin.editor.selectRem([],original.portalId);
  for(let i=0;i<100;i++) {
    if(same(await capture(plugin),original))return;
    await pause();
  }
  throw new Error('Official SDK could not restore the original editor page and selection; no additional changes were attempted');
}

/** The installed SDK rejects setBackText(undefined). Use only its official editor to remove an empty delimiter. */
export async function clearEmptyBackWithEditor(plugin:ReactRNPlugin,rem:PluginRem,verifyUnchanged:()=>Promise<unknown>):Promise<void> {
  const original=await capture(plugin);
  if(original.selection && !['Text','Rem'].includes(original.selection.type)) {
    throw new Error('Current reader selection cannot be restored with the official editor API');
  }
  if(original.selection?.type==='Text' && original.selection.remId!==original.focusedRemId) {
    throw new Error('Original editor focus and text selection disagree');
  }
  const front=structuredClone(rem.text??[]),back=rem.backText;
  if(!Array.isArray(back)||back.length!==0)throw new Error('Editor repair requires the exact empty back array');
  await verifyUnchanged();
  try {
    await plugin.window.openRem(rem);
    await focused(plugin,rem._id);
    await plugin.editor.selectText({start:0,end:0});
    const selection:any=await plugin.editor.getSelection();
    if(selection?.type!=='Text'||selection.remId!==rem._id||selection.range.start!==0||selection.range.end!==0) {
      throw new Error('Official editor selection is not the exact repair target; no text was replaced');
    }
    const currentText=await plugin.editor.getFocusedEditorText();
    const delimiters=(currentText??[]).filter(part=>typeof part==='object'&&part.i==='s');
    const text=(currentText??[]).filter(part=>typeof part!=='object'||part.i!=='s');
    if(delimiters.length!==1 || await plugin.richText.toString(text)!==await plugin.richText.toString(front)) {
      throw new Error('Official editor content differs from the recorded front and empty delimiter');
    }
    await verifyUnchanged();
    if((await plugin.focus.getFocusedRem())?._id!==rem._id)throw new Error('Editor focus changed before repair');
    await plugin.editor.setText(front);
    for(let i=0;i<160;i++) {
      const after=await plugin.rem.findOne(rem._id);
      if(after && after.backText==null && same(after.text??[],front))return;
      await pause();
    }
    throw new Error('Official editor did not remove the empty card back; original Rem ID retained');
  } finally { await restore(plugin,original); }
}
