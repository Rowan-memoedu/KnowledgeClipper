import express, { type Express, type Request, type Response } from 'express';
import { createHash } from 'node:crypto';
import { z } from 'zod';
import { hashLocalImageFile, hashManagedImage, readLocalImageFile } from './media.js';
import type { ClipperTransport as WebSocketServer } from './transport.js';

export const CLIPPER_SDK_CAPABILITY = 'knowledge-clipper.sdk.v1';
export const CLIPPER_MEDIA_CAPABILITY = 'knowledge-clipper.media-finalize.v1';
export const CLIPPER_SPACING_FINALIZE_CAPABILITY = 'knowledge-clipper.batch-spacing-finalize.v2';
export const CLIPPER_PLAIN_STRUCTURE_CAPABILITY = 'knowledge-clipper.plain-structure.v3';
const Part = z.object({
  kind: z.enum(['text', 'image', 'cloze', 'latex']), text: z.string().max(200_000).optional(),
  uri: z.string().max(4000).optional(), sha256: z.string().regex(/^[0-9a-f]{64}$/i).optional(),
  clozeId: z.union([z.number().int().positive(), z.string().regex(/^[1-9][0-9]*$/)]).transform(String).optional(), hint: z.string().max(4000).optional(), block: z.boolean().optional(),
}).strict().superRefine((part, ctx) => {
  if (part.kind === 'image') {
    let allowed = false;
    try { const url = new URL(part.uri ?? ''); allowed = url.protocol === 'file:' && !url.host && /\.(png|jpe?g|gif|webp)$/i.test(url.pathname); } catch { /* invalid URI */ }
    if (!allowed || !part.sha256) ctx.addIssue({code:'custom',message:'Image requires local file URI and SHA-256'});
  } else if (part.text === undefined) ctx.addIssue({code:'custom',message:'Text is required'});
  if (part.kind === 'cloze' && !part.clozeId) ctx.addIssue({code:'custom',message:'Cloze group is required'});
});
const Card = z.object({ key:z.string().min(1).max(200), kind:z.enum(['basic','plain','cloze']),
  front:z.array(Part).min(1).max(500), back:z.array(z.array(Part).max(500)).max(200),
}).strict();
export const ClipPlanSchema = z.object({ schemaVersion:z.literal('knowledge-clipper.remnote.v1'),
  sourceId:z.string().min(1).max(200), rootTitle:z.literal('零碎知识剪藏'), rootRemId:z.string().min(1).max(200).optional(),
  documentPath:z.array(z.string().trim().min(1).max(200)).min(1).max(5),
  targetDocumentId:z.string().min(1).max(200).optional(), cards:z.array(Card).min(1).max(1000),
}).strict().superRefine((plan, ctx) => {
  if (new Set(plan.cards.map(c => c.key)).size !== plan.cards.length) ctx.addIssue({code:'custom',message:'Duplicate card keys'});
  if (plan.targetDocumentId && (plan.documentPath.length!==2 || plan.documentPath[0]!=='摘录'))
    ctx.addIssue({code:'custom',message:'Selected excerpt Document must be directly under 摘录'});
});

type Json = Record<string, any>;
type Plan = z.infer<typeof ClipPlanSchema>;
const Ids = z.array(z.string().min(1).max(200)).max(1000);
const IdentitySchema = z.object({rootRemId:z.string().min(1),documentRemId:z.string().min(1),
  cards:z.array(z.object({key:z.string().min(1),remIds:Ids,childRemIds:Ids,cardIds:Ids}).strict()).min(1).max(1000)}).strict();
const FinalizeSchema = z.object({plan:ClipPlanSchema,expected:IdentitySchema}).strict();
const FinalizeSpacingSchema = z.object({plan:ClipPlanSchema,expected:IdentitySchema,separatorRemId:z.string().min(1).max(200)}).strict();
const RepairPlainStructureSchema = z.object({plan:ClipPlanSchema,expected:IdentitySchema,
  targetRemIds:z.array(z.string().min(1).max(200)).min(1).max(7),
  expectedStructureNodes:z.array(z.record(z.string(),z.unknown())).min(1).max(7)}).strict();
const ExportSchema = z.object({plan:ClipPlanSchema,newSource:z.literal(true).optional()}).strict();
const AdoptSchema = z.object({plan:ClipPlanSchema,expected:IdentitySchema,verifiedNodes:z.array(z.record(z.string(),z.unknown())).min(1).max(201000)}).strict();
const identity = (result: Json) => ({rootRemId:result.rootRemId,documentRemId:result.documentRemId,
  cards:result.cards.map((card:Json)=>({key:card.key,remIds:card.remIds,childRemIds:card.childRemIds,cardIds:card.cardIds}))});
const snapshot = (result:Json) => result.cards.flatMap((card:Json)=>card.nodes.map((node:Json)=>
  Object.fromEntries(['remId','parentId','text','backText','children','remType','isDocument','isCardItem',
    'isExtraCardDetail','practiceDirection','practiceEnabled','cardIds','cardTypes'].map(key=>[key,node[key]]))));
const structureSnapshot = (result:Json) => [...result.cards.flatMap((card:Json)=>card.nodes),...(result.directoryNodes??[]),
  ...(result.batchSeparator?[result.batchSeparator.node]:[])].map((node:Json)=>Object.fromEntries(
    ['remId','parentId','text','backText','rawBackText','backTextState','children','remType','rawRemType','isDocument',
      'isCardItem','isExtraCardDetail','isMultiLineCard','practiceDirection','practiceEnabled','cardIds','cardTypes'].map(key=>[key,node[key]])));
const hasContent = (row:Plan['cards'][number]['front'])=>row.some(p=>p.kind==='image'||p.text?.trim());

async function verifySourceImage(part: Plan['cards'][number]['front'][number]): Promise<void> {
  const actual = await hashLocalImageFile(part.uri!);
  if (actual.sha256.toLowerCase() !== part.sha256!.toLowerCase()) throw new Error('Source image SHA-256 changed');
}

async function prepareImages(ws:WebSocketServer,parts:Plan['cards'][number]['front'],roots:string[]):Promise<Json[]> {
  const prepared:Json[]=[];
  const unique=new Map<string,Plan['cards'][number]['front'][number]>();
  for(const part of parts.filter(p=>p.kind==='image')) {
    const previous=unique.get(part.uri!);
    if(previous && previous.sha256!.toLowerCase()!==part.sha256!.toLowerCase()) throw new Error('Conflicting hashes for one source image');
    unique.set(part.uri!,part);
  }
  // Each frame contains one image; do not collect all Base64 bytes into one WebSocket request.
  for(const part of unique.values()) {
    const file=await readLocalImageFile(part.uri!);
    const sha256=createHash('sha256').update(file.data).digest('hex');
    if(sha256!==part.sha256!.toLowerCase()) throw new Error('Source image SHA-256 changed');
    const image=await ws.sendRequest('knowledge_clipper_prepare_image',
      {uri:part.uri,sha256,dataUri:`data:${file.mimeType};base64,${file.data.toString('base64')}`},120_000) as Json;
    if(image.uri!==part.uri || image.sha256?.toLowerCase()!==sha256 || image.richText?.i!=='i' ||
        typeof image.richText.url!=='string' || !image.richText.url.startsWith('%LOCAL_FILE%')) {
      throw new Error('Official SDK image upload did not return managed media');
    }
    const actual=await hashManagedImage({remId:'prepared',mediaId:'prepared',kind:'image',field:'text',elementIndex:0,
      imageIndex:0,source:'remnote_managed_local',localToken:decodeURIComponent(image.richText.url.slice('%LOCAL_FILE%'.length))},roots);
    if(actual.sha256!==sha256) throw new Error('Uploaded image SHA-256 differs');
    prepared.push(image);
  }
  return prepared;
}

/** Pure read preflight: verifies every image position and both original and already-managed bytes. */
export async function inspectClipperMediaRecovery(result:Json,plan:Plan):Promise<number> {
  if (result.schemaVersion!==plan.schemaVersion || result.sourceId!==plan.sourceId ||
      !result.rootRemId || !result.documentRemId || result.cards?.length!==plan.cards.length) {
    throw new Error('Media recovery requires a complete matching readback');
  }
  let pendingImages=0;
  for (let ci=0;ci<plan.cards.length;ci++) {
    const card=plan.cards[ci],actual=result.cards[ci],rows=[card.front,...card.back.filter(hasContent)];
    if (actual.key!==card.key || actual.kind!==card.kind || actual.remIds?.length!==1 ||
        actual.nodes?.length!==rows.length || actual.childRemIds?.length!==rows.length-1) throw new Error('Media recovery card structure changed');
    const ids=[...actual.remIds,...actual.childRemIds];
    const front=actual.nodes[0];
    const groups=[...new Set(card.front.filter(part=>part.clozeId).map(part=>`kc_${part.clozeId}`))].sort();
    const validTypes=card.kind==='plain' ? front.cardTypes?.length===0 : card.kind==='basic'
      ? JSON.stringify(front.cardTypes)===JSON.stringify(['forward'])
      : JSON.stringify((front.cardTypes??[]).map((type:Json)=>type?.clozeId).sort())===JSON.stringify(groups);
    if(new Set(ids).size!==ids.length || ids.includes(result.rootRemId)||ids.includes(result.documentRemId)||
        JSON.stringify(front.cardIds)!==JSON.stringify(actual.cardIds)||!validTypes||
        new Set(actual.cardIds).size!==actual.cardIds.length ||
        JSON.stringify(front.children)!==JSON.stringify(actual.childRemIds)||
        front.parentId!==result.documentRemId || front.practiceEnabled!==(card.kind!=='plain') ||
        (card.kind==='basic'&&front.practiceDirection!=='forward') || front.isExtraCardDetail ||
        (card.kind!=='basic'&&front.isCardItem)) throw new Error('Media recovery flashcard shape changed');
    for(let ri=0;ri<rows.length;ri++) {
      const node=actual.nodes[ri],images=(node.text??[]).filter((part:Json)=>part&&typeof part==='object'&&part.i==='i');
      const expectedImages=rows[ri].filter(part=>part.kind==='image');
      if(node.remId!==ids[ri] || images.length!==expectedImages.length) throw new Error('Media recovery image positions changed');
      if(node.isDocument||node.remType!==0||(node.backText?.length??0)!==0||
          (ri>0 && (node.parentId!==ids[0]||node.children?.length||node.cardIds?.length||
            (card.kind==='basic'?!node.isCardItem:!node.isExtraCardDetail)))) throw new Error('Media recovery node shape changed');
      for(let ii=0;ii<images.length;ii++) {
        const image=images[ii],source=expectedImages[ii];
        if(typeof image.url==='string' && image.url.startsWith('%LOCAL_FILE%')) {
          const digest=node.media?.[ii];
          if(!digest?.sha256 || digest.uri!==image.url || digest.sha256.toLowerCase()!==source.sha256!.toLowerCase()) {
            throw new Error('Managed image SHA-256 differs; recovery refused');
          }
        } else {
          if(image.url!==source.uri) throw new Error('Original image URI changed; recovery refused');
          await verifySourceImage(source);
          pendingImages++;
        }
      }
    }
  }
  return pendingImages;
}

async function withMediaRecovery(result:Json,plan:Plan,roots:string[],supported:boolean):Promise<Json> {
  await enrichClipperMedia(result,roots);
  if(!plan.cards.some(card=>[...card.front,...card.back.flat()].some(part=>part.kind==='image'))) return result;
  try {
    if(!supported) throw new Error('Refresh the RemNote SDK plugin to enable image finalization');
    const pendingImages=await inspectClipperMediaRecovery(result,plan);
    result.mediaRecovery={eligible:pendingImages>0,kind:'finalize-local-images',pendingImages};
  } catch(error) {
    result.mediaRecovery={eligible:false,kind:'finalize-local-images',error:error instanceof Error?error.message:String(error)};
  }
  return result;
}

/** Hash only bytes resolved from current SDK-managed media locators; never echo the caller's hash. */
export async function enrichClipperMedia(result: Json, roots: string[]): Promise<Json> {
  let mediaPending = false;
  for (const card of result.cards ?? []) {
    for (const node of card.nodes ?? []) {
      const media = [];
      let imageIndex = 0;
      for (let elementIndex = 0; elementIndex < (node.text ?? []).length; elementIndex++) {
        const image = node.text[elementIndex];
        if (!image || typeof image !== 'object' || image.i !== 'i') continue;
        const item: Json = {elementIndex,imageIndex,uri:image.url};
        try {
          if (typeof image.url !== 'string' || !image.url.startsWith('%LOCAL_FILE%')) throw new Error('Image is awaiting RemNote-managed storage');
          const resolved = await hashManagedImage({remId:node.remId,mediaId:`sdk:${node.remId}:${imageIndex}`,
            kind:'image',field:'text',elementIndex,imageIndex,source:'remnote_managed_local',
            localToken:decodeURIComponent(image.url.slice('%LOCAL_FILE%'.length))}, roots);
          item.sha256 = resolved.sha256;
          item.sizeBytes = resolved.sizeBytes;
          item.mimeType = resolved.mimeType;
        } catch (error) {
          mediaPending = true;
          item.error = error instanceof Error ? error.message : String(error);
        }
        const part = (node.parts ?? []).filter((p: Json) => p.kind === 'image')[imageIndex];
        if (part && item.sha256) { part.sha256 = item.sha256; part.sizeBytes = item.sizeBytes; }
        media.push(item);
        imageIndex++;
      }
      node.media = media;
    }
    // Rebuild from actual node readback after enriching it, not from request fields.
    card.front = card.nodes?.[0]?.parts ?? [];
    card.back = (card.nodes ?? []).slice(1).map((n: Json) => n.parts ?? []);
  }
  result.mediaPending = mediaPending;
  return result;
}

export function registerKnowledgeClipperHttp(app: Express, ws: WebSocketServer, port: number, roots: string[]): void {
  const router = express.Router();
  router.use((req: Request, res: Response, next) => {
    const address = req.socket.remoteAddress ?? '';
    const host = req.headers.host?.toLowerCase();
    if (!['127.0.0.1','::1','::ffff:127.0.0.1'].includes(address) ||
        ![`127.0.0.1:${port}`,`localhost:${port}`,`[::1]:${port}`].includes(host ?? '') || req.headers.origin ||
        req.headers['x-knowledgeclipper-protocol'] !== '1') {
      res.status(403).json({error:'SDK endpoint accepts native loopback clients only'}); return;
    }
    next();
  });
  router.use(express.json({limit:'2mb'}));
  router.get('/health', async (_req, res) => {
    const connected = ws.isConnected();
    const supported = connected && ws.hasBridgeCapability(CLIPPER_SDK_CAPABILITY);
    const status = connected ? await ws.sendRequest('knowledge_clipper_health',{},5000).catch(() => ({})) : {};
    res.json({schemaVersion:'knowledge-clipper.remnote.v1',connected,supported,
      ...(status as Json),transport:'sdk-http',capability:CLIPPER_SDK_CAPABILITY,
      mediaFinalizeSupported:connected&&ws.hasBridgeCapability(CLIPPER_MEDIA_CAPABILITY),
      spacingFinalizeSupported:connected&&ws.hasBridgeCapability(CLIPPER_SPACING_FINALIZE_CAPABILITY),
      plainStructureSupported:connected&&ws.hasBridgeCapability(CLIPPER_PLAIN_STRUCTURE_CAPABILITY),
      identityAdoptSupported:connected&&ws.hasBridgeCapability('knowledge-clipper.identity-adopt.v1')});
  });
  router.get('/excerpt-documents', async (_req,res) => {
    const requestedRoot = _req.query.rootRemId;
    if(requestedRoot!==undefined && (typeof requestedRoot!=='string'||!requestedRoot||requestedRoot.length>200)){
      res.status(400).json({error:'Invalid excerpt root Document ID'});return;
    }
    if(!ws.isConnected()||!ws.hasBridgeCapability('knowledge-clipper.excerpt-documents.v1')){
      res.status(503).json({error:'Refresh the connected KnowledgeClipper plugin to list excerpt Documents'});return;
    }
    try{res.json(await ws.sendRequest('knowledge_clipper_excerpt_documents',requestedRoot?{rootRemId:requestedRoot}:{},15000));}
    catch(error){res.status(503).json({error:error instanceof Error?error.message:String(error)});}
  });
  for (const operation of ['export','readback'] as const) router.post(`/${operation}`, async (req, res) => {
    const envelope = operation==='export' && req.body?.plan ? ExportSchema.safeParse(req.body) : undefined;
    if(envelope && !envelope.success){res.status(400).json({error:'Invalid export envelope'});return;}
    const newSource=envelope?.success && envelope.data.newSource===true;
    const parsed = ClipPlanSchema.safeParse(envelope?.success ? envelope.data.plan : req.body);
    if (!parsed.success) { res.status(400).json({error:'Invalid export plan',details:parsed.error.issues}); return; }
    if (!ws.isConnected() || !ws.hasBridgeCapability(CLIPPER_SDK_CAPABILITY)) {
      res.status(503).json({error:'The connected RemNote SDK plugin does not provide knowledge-clipper.sdk.v1'}); return;
    }
    const images=parsed.data.cards.flatMap(card=>[...card.front,...card.back.flat()]).filter(part=>part.kind==='image');
    if(operation==='export'&&images.length&&!ws.hasBridgeCapability(CLIPPER_MEDIA_CAPABILITY)){
      res.status(503).json({error:'Refresh the connected RemNote SDK plugin before exporting images'});return;
    }
    try {
      if(operation==='export'&&images.length) {
        const before=await ws.sendRequest('knowledge_clipper_export',{plan:parsed.data,prepareOnly:true,...(newSource?{newSource:true}:{})},120_000) as Json;
        if(before.cards) {res.json(await withMediaRecovery(before,parsed.data,roots,true));return;}
        if(before.schemaVersion!==parsed.data.schemaVersion||before.sourceId!==parsed.data.sourceId||
            before.notStarted!==true||before.requiresImages!==true) throw new Error('Image export preparation was not acknowledged');
        for(const part of images) await verifySourceImage(part);
      }
      const payload=operation==='export'&&images.length
        ? {plan:parsed.data,preparedImages:await prepareImages(ws,images,roots),...(newSource?{newSource:true}:{})}
        : operation==='export' && newSource ? {plan:parsed.data,newSource:true} : parsed.data;
      const result = await ws.sendRequest(`knowledge_clipper_${operation}`,payload,120_000);
      res.json(await withMediaRecovery(result as Json,parsed.data,roots,ws.hasBridgeCapability(CLIPPER_MEDIA_CAPABILITY)));
    } catch (error) {
      if(operation==='export'&&images.length) {
        try {
          const state=await ws.sendRequest('knowledge_clipper_readback',parsed.data,120_000) as Json;
          if(state.schemaVersion===parsed.data.schemaVersion&&state.sourceId===parsed.data.sourceId&&state.notStarted===true) {
            res.json({...state,error:error instanceof Error?error.message:String(error)});return;
          }
        } catch { /* Ambiguous content writes retain the original failure and unknown state. */ }
      }
      res.status(409).json({error:error instanceof Error ? error.message : String(error),retryCreatesNoDuplicate:true});
    }
  });
  router.post('/finalize-media',async(req,res)=>{
    const parsed=FinalizeSchema.safeParse(req.body);
    if(!parsed.success){res.status(400).json({error:'Invalid media recovery request',details:parsed.error.issues});return;}
    if(!ws.isConnected()||!ws.hasBridgeCapability(CLIPPER_SDK_CAPABILITY)||!ws.hasBridgeCapability(CLIPPER_MEDIA_CAPABILITY)){
      res.status(503).json({error:'Refresh the connected RemNote SDK plugin to enable image finalization'});return;
    }
    try {
      const {plan,expected}=parsed.data;
      const before=await ws.sendRequest('knowledge_clipper_readback',plan,120_000) as Json;
      await enrichClipperMedia(before,roots);
      await inspectClipperMediaRecovery(before,plan);
      if(JSON.stringify(identity(before))!==JSON.stringify(expected)) throw new Error('Media recovery identity changed');
      const pending:Plan['cards'][number]['front']=[];
      for(let ci=0;ci<plan.cards.length;ci++) {
        const rows=[plan.cards[ci].front,...plan.cards[ci].back.filter(hasContent)];
        for(let ri=0;ri<rows.length;ri++) {
          const images=before.cards[ci].nodes[ri].text.filter((part:Json)=>part&&typeof part==='object'&&part.i==='i');
          rows[ri].filter(part=>part.kind==='image').forEach((part,ii)=>{if(images[ii].url===part.uri)pending.push(part);});
        }
      }
      const verifiedNodes=snapshot(before),preparedImages=await prepareImages(ws,pending,roots);
      const result=await ws.sendRequest('knowledge_clipper_finalize_media',{plan,expected,verifiedNodes,preparedImages},120_000);
      res.json(await withMediaRecovery(result as Json,plan,roots,true));
    } catch(error) {
      res.status(409).json({error:error instanceof Error?error.message:String(error),retryCreatesNoDuplicate:true});
    }
  });
  router.post('/finalize-spacing',async(req,res)=>{
    const parsed=FinalizeSpacingSchema.safeParse(req.body);
    if(!parsed.success){res.status(400).json({error:'Invalid batch spacing recovery request'});return;}
    if(!ws.isConnected()||!ws.hasBridgeCapability(CLIPPER_SDK_CAPABILITY)||!ws.hasBridgeCapability(CLIPPER_SPACING_FINALIZE_CAPABILITY)){
      res.status(503).json({error:'Refresh the connected RemNote SDK plugin to enable existing spacing finalization'});return;
    }
    try{
      const {plan,expected,separatorRemId}=parsed.data;
      const before=await ws.sendRequest('knowledge_clipper_readback',plan,120000) as Json;
      if(before.schemaVersion!==plan.schemaVersion||before.sourceId!==plan.sourceId||
          JSON.stringify(identity(before))!==JSON.stringify(expected)||before.batchSeparator?.remId!==separatorRemId||
          !before.batchSeparator.node||!Array.isArray(before.documentChildren))throw new Error('Spacing recovery identity changed');
      const verifiedNodes=[...snapshot(before),...snapshot({cards:[{nodes:[before.batchSeparator.node]}]})];
      const result=await ws.sendRequest('knowledge_clipper_finalize_spacing',
        {plan,expected,separatorRemId,verifiedNodes,documentChildren:before.documentChildren},120000) as Json;
      if(JSON.stringify(identity(result))!==JSON.stringify(expected)||result.batchSeparator?.remId!==separatorRemId||result.batchSeparatorPending)
        throw new Error('Spacing finalization did not preserve the completed original identity');
      res.json(await withMediaRecovery(result,plan,roots,ws.hasBridgeCapability(CLIPPER_MEDIA_CAPABILITY)));
    }catch(error){res.status(409).json({error:error instanceof Error?error.message:String(error),retryCreatesNoDuplicate:true});}
  });
  router.post('/repair-plain-structure',async(req,res)=>{
    const parsed=RepairPlainStructureSchema.safeParse(req.body);
    if(!parsed.success){res.status(400).json({error:'Invalid plain structure repair request'});return;}
    if(!ws.isConnected()||!ws.hasBridgeCapability(CLIPPER_SDK_CAPABILITY)||!ws.hasBridgeCapability(CLIPPER_PLAIN_STRUCTURE_CAPABILITY)){
      res.status(503).json({error:'Refresh the connected RemNote SDK plugin to enable plain structure verification'});return;
    }
    try{
      const {plan,expected,targetRemIds,expectedStructureNodes}=parsed.data;
      const before=await ws.sendRequest('knowledge_clipper_readback',plan,120000) as Json;
      const verifiedNodes=structureSnapshot(before);
      const targets=targetRemIds.map(id=>verifiedNodes.find(node=>node.remId===id));
      if(before.schemaVersion!==plan.schemaVersion||before.sourceId!==plan.sourceId||before.batchSeparatorPending||
          JSON.stringify(identity(before))!==JSON.stringify(expected)||new Set(targetRemIds).size!==targetRemIds.length||
          targets.some(node=>!node)||JSON.stringify(targets)!==JSON.stringify(expectedStructureNodes)){
        throw new Error('Plain structure identity or observed snapshot changed');
      }
      const result=await ws.sendRequest('knowledge_clipper_repair_plain_structure',{plan,expected,targetRemIds,verifiedNodes},120000) as Json;
      if(result.schemaVersion!==plan.schemaVersion||result.sourceId!==plan.sourceId||JSON.stringify(identity(result))!==JSON.stringify(expected)||
          result.batchSeparator?.remId!==before.batchSeparator?.remId)throw new Error('Plain structure repair changed source identity');
      res.json(await withMediaRecovery(result,plan,roots,ws.hasBridgeCapability(CLIPPER_MEDIA_CAPABILITY)));
    }catch(error){res.status(409).json({error:error instanceof Error?error.message:String(error),retryCreatesNoDuplicate:true});}
  });
  // Explicit public-fixture driver; ordinary export/readback/repair never dispatch this action.
  router.post('/test-plain-structure',async(req,res)=>{
    const parsed=FinalizeSpacingSchema.safeParse(req.body);
    if(!parsed.success||!/^knowledgeclipper\.plain-structure-canary\.[0-9a-f]{32}$/.test(parsed.data.plan.sourceId)){
      res.status(400).json({error:'Only a frozen public ordinary-structure fixture is accepted'});return;
    }
    if(!ws.isConnected()||!ws.hasBridgeCapability(CLIPPER_PLAIN_STRUCTURE_CAPABILITY)){
      res.status(503).json({error:'Refresh the dedicated SDK plugin for the public fixture test'});return;
    }
    try{res.json(await ws.sendRequest('knowledge_clipper_test_plain_structure',parsed.data,120000));}
    catch(error){res.status(409).json({error:error instanceof Error?error.message:String(error)});}
  });
  router.post('/admin/adopt',async(req,res)=>{
    const parsed=AdoptSchema.safeParse(req.body);
    if(!parsed.success){res.status(400).json({error:'Invalid identity adoption request'});return;}
    if(!ws.isConnected()||!ws.hasBridgeCapability('knowledge-clipper.identity-adopt.v1')){res.status(503).json({error:'Identity adoption is unavailable'});return;}
    try{
      const result=await ws.sendRequest('knowledge_clipper_adopt',parsed.data,120000) as Json;
      res.json(await withMediaRecovery(result,parsed.data.plan,roots,ws.hasBridgeCapability(CLIPPER_MEDIA_CAPABILITY)));
    }catch(error){res.status(409).json({error:error instanceof Error?error.message:String(error)});}
  });
  app.use('/sdk/knowledge-clipper',router);
}
