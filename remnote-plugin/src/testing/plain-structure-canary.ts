import type {ReactRNPlugin} from '@remnote/plugin-sdk';
import {KnowledgeClipperAdapter,clipIdentity,validateClipPlan} from '../api/knowledge-clipper';
import {clearEmptyBackWithEditor} from '../api/plain-structure-editor';

/** Explicit public fixture preparation, separate from every production import and repair path. */
export async function verifyEmptyBackEditor(plugin:ReactRNPlugin,adapter:KnowledgeClipperAdapter,payload:any):Promise<unknown> {
  const plan=validateClipPlan(payload?.plan),match=/^knowledgeclipper\.plain-structure-canary\.([0-9a-f]{32})$/.exec(plan.sourceId);
  if(!match||plan.documentPath.length!==3||plan.documentPath[0]!=='验收'||plan.documentPath[1]!==`普通节点-${match[1].slice(0,10)}`||
      plan.documentPath[2]!=='新增目录'||!plan.cards.every(card=>card.front.some(part=>part.text?.includes(`KnowledgeClipper public plain-structure test ${match[1]}`)))) {
    throw new Error('Only the isolated public ordinary-structure test fixture can prepare an empty back');
  }
  const key=`knowledge-clipper.public-editor-canary.${plan.sourceId}`;
  let record:any=await plugin.storage.getSynced(key);
  if(!record) {
    const before:any=await adapter.readback(plan);
    if(JSON.stringify(clipIdentity(before))!==JSON.stringify(payload.expected)||before.batchSeparator?.remId!==payload.separatorRemId) {
      throw new Error('Public test fixture identity differs');
    }
    record={plan,before};await plugin.storage.setSynced(key,record);
  }
  if(JSON.stringify(record.plan)!==JSON.stringify(plan)||JSON.stringify(clipIdentity(record.before))!==JSON.stringify(payload.expected)||
      record.before.batchSeparator.remId!==payload.separatorRemId)throw new Error('Public test fixture journal differs');
  const original=record.before.batchSeparator.node;
  const verify=async()=>{
    const blank=await plugin.rem.findOne(original.remId),directory=await plugin.rem.findOne(original.parentId);
    if(!blank||!directory||blank.parent!==original.parentId||JSON.stringify(blank.text??[])!==JSON.stringify(original.text)||
        (blank.backText!=null&&(!Array.isArray(blank.backText)||blank.backText.length))||(blank.children??[]).length||
        (blank.type??0)!==0||await blank.isDocument()||await blank.isCardItem()||(await blank.getCards()).length||
        JSON.stringify(directory.children??[])!==JSON.stringify(record.before.documentChildren))throw new Error('Public blank or sibling state changed');
    return blank;
  };
  const blank=await verify();
  if(blank.backText==null)await blank.setBackText([]);
  await clearEmptyBackWithEditor(plugin,await verify(),verify);
  const after:any=await adapter.readback(plan);
  if(JSON.stringify(clipIdentity(after))!==JSON.stringify(payload.expected)||after.batchSeparator.remId!==payload.separatorRemId) {
    throw new Error('Public test fixture identity changed during editor verification');
  }
  return {editorRestored:true,preparedExistingEmptyBack:true,result:after};
}
