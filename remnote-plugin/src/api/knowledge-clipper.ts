import { BuiltInPowerupCodes, type PluginRem, type ReactRNPlugin, type RichTextInterface } from '@remnote/plugin-sdk';
import { clearEmptyBackWithEditor } from './plain-structure-editor';

export const KNOWLEDGE_CLIPPER_CAPABILITY = 'knowledge-clipper.sdk.v1';
export const KNOWLEDGE_CLIPPER_MEDIA_CAPABILITY = 'knowledge-clipper.media-finalize.v1';
export const KNOWLEDGE_CLIPPER_SPACING_CAPABILITY = 'knowledge-clipper.batch-spacing.v1';
export const KNOWLEDGE_CLIPPER_SPACING_FINALIZE_CAPABILITY = 'knowledge-clipper.batch-spacing-finalize.v2';
export const KNOWLEDGE_CLIPPER_PLAIN_STRUCTURE_CAPABILITY = 'knowledge-clipper.plain-structure.v3';
export const KNOWLEDGE_CLIPPER_SCHEMA = 'knowledge-clipper.remnote.v1';

export interface ClipPart {
  kind: 'text' | 'image' | 'cloze' | 'latex';
  text?: string;
  uri?: string;
  sha256?: string;
  clozeId?: string | number;
  hint?: string;
  block?: boolean;
}
export interface ClipCard {
  key: string;
  kind: 'basic' | 'plain' | 'cloze';
  front: ClipPart[];
  back: ClipPart[][];
}
export interface ClipPlan {
  schemaVersion: typeof KNOWLEDGE_CLIPPER_SCHEMA;
  sourceId: string;
  rootTitle: string;
  rootRemId?: string;
  documentPath: string[];
  targetDocumentId?: string;
  cards: ClipCard[];
}
export interface ClipIdentity {
  rootRemId: string;
  documentRemId: string;
  cards: { key: string; remIds: string[]; childRemIds: string[]; cardIds: string[] }[];
}
type Readback = Record<string, any>;
interface PreparedImage { uri: string; sha256: string; richText: RichTextInterface[number] }
export function clipIdentity(result: Readback): ClipIdentity {
  return { rootRemId: result.rootRemId, documentRemId: result.documentRemId,
    cards: result.cards.map((card: Readback) => ({key: card.key, remIds: card.remIds,
      childRemIds: card.childRemIds, cardIds: card.cardIds})) };
}
/** Server hashes the exact image locators in this snapshot before authorizing a media-only update. */
export function clipNodeSnapshot(result: Readback): Readback[] {
  return structuredClone(result.cards.flatMap((card: Readback) => card.nodes.map((node: Readback) =>
    Object.fromEntries(['remId','parentId','text','backText','children','remType','isDocument','isCardItem',
      'isExtraCardDetail','practiceDirection','practiceEnabled','cardIds','cardTypes'].map(key => [key,node[key]])))));
}
interface StoredCard {
  fingerprint: string;
  state: 'writing' | 'complete';
  rootRemId: string;
  documentRemId: string;
  remIds: string[];
  childRemIds: string[];
}
interface StoredAttempt {
  fingerprint: string;
  phase: 'preparing' | 'failed_before_cards' | 'writing_content' | 'complete';
  error?: string;
  // Absent on legacy imports. Never add a separator while rereading an old source.
  batchSeparator?: { state: 'pending' | 'writing' | 'complete'; documentRemId: string; remId?: string; plainVersion?: 1 };
}

const markerKey = (sourceId: string, key: string) =>
  `knowledge-clipper.v1.${encodeURIComponent(JSON.stringify([sourceId, key]))}`;
const attemptKey = (sourceId: string) => `knowledge-clipper.attempt.v1.${encodeURIComponent(sourceId)}`;
const attemptFingerprint = (plan: ClipPlan) => JSON.stringify(plan);
const fingerprint = (plan: ClipPlan, card: ClipCard) => JSON.stringify({
  rootTitle: plan.rootTitle, rootRemId: plan.rootRemId ?? '', documentPath: plan.documentPath,
  ...(plan.targetDocumentId ? {targetDocumentId: plan.targetDocumentId} : {}), card,
});
const content = (parts: ClipPart[]) => parts.some(p => p.kind === 'image' || (p.text ?? '').trim());
const emptyRichText = (value: unknown): boolean => Array.isArray(value) && value.every(part => typeof part === 'string'
  ? part === '' : part && part.i === 'm' && part.text === '' && Object.keys(part).every(key => key === 'i' || key === 'text'));

/** Includes raw back-text presence: an absent back differs from an empty flashcard back. */
export function clipStructureSnapshot(result: Readback): Readback[] {
  const nodes = [...result.cards.flatMap((card:Readback) => card.nodes), ...(result.directoryNodes ?? []),
    ...(result.batchSeparator ? [result.batchSeparator.node] : [])];
  return structuredClone(nodes.map((node:Readback) => Object.fromEntries(
    ['remId','parentId','text','backText','rawBackText','backTextState','children','remType','rawRemType','isDocument',
      'isCardItem','isExtraCardDetail','isMultiLineCard','practiceDirection','practiceEnabled','cardIds','cardTypes'].map(key => [key,node[key]]))));
}

export function validateClipPlan(value: unknown): ClipPlan {
  const p = value as ClipPlan;
  if (!p || p.schemaVersion !== KNOWLEDGE_CLIPPER_SCHEMA || typeof p.sourceId !== 'string' ||
      !p.sourceId.trim() || p.sourceId.length > 200 || p.rootTitle !== '零碎知识剪藏' ||
      !Array.isArray(p.documentPath) || !p.documentPath.length || p.documentPath.length > 5 ||
      p.documentPath.some(x => typeof x !== 'string' || !x.trim() || x.length > 200) ||
      (p.targetDocumentId !== undefined && (typeof p.targetDocumentId !== 'string' || !p.targetDocumentId ||
        p.targetDocumentId.length > 200 || p.documentPath.length !== 2 || p.documentPath[0] !== '摘录')) ||
      !Array.isArray(p.cards) || !p.cards.length || p.cards.length > 1000) {
    throw new Error('Invalid KnowledgeClipper export plan');
  }
  const keys = new Set<string>();
  for (const card of p.cards) {
    if (typeof card.key !== 'string' || !card.key || card.key.length > 200 || keys.has(card.key) ||
        !['plain', 'basic', 'cloze'].includes(card.kind) || !Array.isArray(card.front) ||
        !Array.isArray(card.back) || card.back.some(row => !Array.isArray(row))) {
      throw new Error('Invalid or duplicate card key');
    }
    keys.add(card.key);
    for (const part of [...card.front, ...card.back.flat()]) {
      if (!part || !['text', 'image', 'cloze', 'latex'].includes(part.kind)) throw new Error('Invalid rich part');
      if (part.kind === 'image') {
        const url = new URL(part.uri ?? '');
        if (url.protocol !== 'file:' || url.host || !/\.(png|jpe?g|gif|webp)$/i.test(url.pathname) ||
            !/^[0-9a-f]{64}$/i.test(part.sha256 ?? '')) throw new Error('Image requires a local file URI and SHA-256');
      } else if (typeof part.text !== 'string') throw new Error('Rich part text is required');
      if ((part.kind === 'cloze' || part.clozeId) && (!/^[1-9][0-9]*$/.test(String(part.clozeId ?? '')) || card.kind !== 'cloze')) {
        throw new Error('Cloze IDs must be positive decimal group numbers on a cloze card');
      }
    }
    if (!content(card.front) || (card.kind === 'basic' && !card.back.some(content)) ||
        (card.kind === 'plain' && card.back.some(content)) ||
        (card.kind === 'cloze' && !card.front.some(p => p.clozeId)) ||
        card.back.flat().some(p => p.clozeId)) throw new Error('Invalid card contents');
  }
  return p;
}

/** Direct rich text avoids treating source punctuation as Markdown card commands. */
export function partsToRichText(parts: ClipPart[]): RichTextInterface {
  return parts.map(part => {
    if (part.kind === 'image') return { i: 'i' as const, url: part.uri! };
    if (part.kind === 'latex') return { i: 'x' as const, text: part.text!, block: part.block ?? false,
      ...(part.clozeId ? { cId: `kc_${part.clozeId}` } : {}),
      ...(part.hint ? { 'cloze-hint': part.hint } : {}) };
    if (part.kind === 'cloze') return {
      i: 'm' as const, text: part.text!, cId: `kc_${part.clozeId}`,
      ...(part.hint ? { 'cloze-hint': part.hint } : {}),
    };
    return { i: 'm' as const, text: part.text! };
  });
}

/** Derive the semantic response only from the SDK readback, never from the plan. */
export function richTextToParts(value: RichTextInterface | undefined): ClipPart[] {
  const parts: ClipPart[] = [];
  for (const raw of value ?? []) {
    let part: ClipPart;
    if (typeof raw === 'string') part = { kind: 'text', text: raw };
    else if (raw.i === 'i') part = { kind: 'image', uri: raw.url };
    else if (raw.i === 'x') part = { kind: 'latex', text: raw.text, block: raw.block ?? false,
      ...(raw.cId ? { clozeId: raw.cId.replace(/^kc_/, '') } : {}),
      ...((raw as unknown as Record<string, unknown>)['cloze-hint'] ? { hint: String((raw as unknown as Record<string, unknown>)['cloze-hint']) } : {}) };
    else if (raw.i === 'm') part = raw.cId
      ? { kind: 'cloze', text: raw.text, clozeId: raw.cId.replace(/^kc_/, ''), ...(raw['cloze-hint'] ? { hint: raw['cloze-hint'] } : {}) }
      : { kind: 'text', text: raw.text };
    else throw new Error(`Unexpected rich text element ${raw.i}; refusing lossy readback`);
    const previous = parts[parts.length - 1];
    if (previous?.kind === 'text' && part.kind === 'text') previous.text += part.text!;
    else parts.push(part);
  }
  return parts;
}

function semantic(parts: ClipPart[]): string {
  // Media URLs become RemNote-managed URLs asynchronously. Their bytes are checked by the HTTP layer.
  return JSON.stringify(richTextToParts(partsToRichText(parts)).map(part => part.kind === 'image'
    ? { kind: 'image' } : part));
}

export class KnowledgeClipperAdapter {
  private queue: Promise<unknown> = Promise.resolve();
  constructor(private readonly plugin: ReactRNPlugin, private readonly canWrite: () => boolean, private readonly requireNewSource = true) {}

  async listExcerptDocuments(value?: {rootRemId?:string}): Promise<unknown> {
    const root = value?.rootRemId ? await this.plugin.rem.findOne(value.rootRemId)
      : await this.plugin.rem.findByName(['零碎知识剪藏'], null);
    if (!root || root.parent !== null || !await root.isDocument() ||
        await this.plugin.richText.toString(root.text ?? []) !== '零碎知识剪藏') throw new Error('剪藏根 Document 不存在或身份不一致');
    const excerpt = await this.plugin.rem.findByName(['摘录'], root._id);
    if (!excerpt || excerpt.parent !== root._id || !await excerpt.isDocument()) throw new Error('摘录 Document 不存在');
    const documents: {id:string;title:string}[] = [];
    for (const id of excerpt.children ?? []) {
      const child = await this.plugin.rem.findOne(id);
      if (!child || child.parent !== excerpt._id || !await child.isDocument()) continue;
      const title = await this.plugin.richText.toString(child.text ?? []);
      if (title) documents.push({id:child._id,title});
    }
    return {rootId:root._id,excerptId:excerpt._id,documents};
  }

  export(value: unknown): Promise<unknown> {
    const request = value as {plan?: ClipPlan; preparedImages?: PreparedImage[]; prepareOnly?: boolean; newSource?: boolean};
    const plan = validateClipPlan(request?.plan ?? value);
    const work = this.queue.then(() => this.exportLocked(plan, request.preparedImages ?? [], request.prepareOnly === true, request.newSource === true));
    this.queue = work.catch(() => undefined);
    return work;
  }

  private async imageRichText(dataUri: string): Promise<RichTextInterface[number]> {
    // Match the official parseFromMarkdown -> setText path used by the bridge's image-capable updateNote.
    // Only the image is parsed: source text must not become Markdown commands or separate Rems.
    // The native parser uploads data URIs; parsing a file URI alone does not enqueue a transfer.
    const rich = await this.plugin.richText.parseFromMarkdown(`![KnowledgeClipper](${dataUri})`);
    const meaningful = rich.filter(part => typeof part === 'string' ? part.trim() :
      part.i !== 'm' || part.text.trim());
    if (meaningful.length !== 1 || typeof meaningful[0] === 'string' || meaningful[0].i !== 'i' ||
        !meaningful[0].url.startsWith('%LOCAL_FILE%')) {
      throw new Error('Official Markdown image parser did not return exactly one image');
    }
    return meaningful[0];
  }

  async prepareImage(value: unknown): Promise<PreparedImage> {
    if (!this.canWrite()) throw new Error('Write operations are disabled in the RemNote bridge');
    const image = value as {uri:string;sha256:string;dataUri:string};
    if (!image || !/^file:\/\//.test(image.uri) || !/^[0-9a-f]{64}$/i.test(image.sha256) ||
        typeof image.dataUri !== 'string' || image.dataUri.length > 45_000_000 ||
        !/^data:image\/(png|jpeg|gif|webp);base64,[A-Za-z0-9+/]+={0,2}$/.test(image.dataUri)) {
      throw new Error('Invalid prepared image payload');
    }
    const binary = atob(image.dataUri.slice(image.dataUri.indexOf(',') + 1));
    const bytes = Uint8Array.from(binary, char => char.charCodeAt(0));
    const digest = await crypto.subtle.digest('SHA-256', bytes);
    const sha256 = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2,'0')).join('');
    if (sha256 !== image.sha256.toLowerCase()) throw new Error('Prepared image bytes do not match SHA-256');
    return {uri:image.uri,sha256:image.sha256,richText:await this.imageRichText(image.dataUri)};
  }

  private validatePreparedImages(prepared: PreparedImage[], allowed: ClipPart[]): void {
    if (!Array.isArray(prepared) || new Set(prepared.map(image => image.uri)).size !== prepared.length) {
      throw new Error('Duplicate prepared image input');
    }
    for (const image of prepared) {
      const source = allowed.find(part => part.kind === 'image' && part.uri === image.uri &&
        part.sha256?.toLowerCase() === image.sha256?.toLowerCase());
      if (!source) throw new Error('Unexpected prepared image input');
      this.preparedImage(source, prepared);
    }
  }

  private preparedImage(part: ClipPart, prepared: PreparedImage[]): RichTextInterface[number] {
    const matches = prepared.filter(image => image.uri === part.uri && image.sha256.toLowerCase() === part.sha256!.toLowerCase());
    const raw = matches[0]?.richText;
    if (matches.length !== 1 || !raw || typeof raw === 'string' || raw.i !== 'i' || !raw.url.startsWith('%LOCAL_FILE%')) {
      throw new Error('Verified official image preparation is required');
    }
    return raw;
  }

  private partsToSdkRichText(parts: ClipPart[], prepared: PreparedImage[]): RichTextInterface {
    const result = partsToRichText(parts);
    for (let i = 0; i < parts.length; i++) if (parts[i].kind === 'image') {
      result[i] = this.preparedImage(parts[i], prepared);
    }
    return result;
  }

  finalizeMedia(value: unknown): Promise<unknown> {
    const request = value as {plan: ClipPlan; expected: ClipIdentity; verifiedNodes: Readback[]; preparedImages?: PreparedImage[]};
    const plan = validateClipPlan(request?.plan);
    if (!request.expected || !Array.isArray(request.verifiedNodes)) throw new Error('Verified media snapshot is required');
    const work = this.queue.then(() => this.finalizeMediaLocked(plan, request.expected, request.verifiedNodes, request.preparedImages ?? []));
    this.queue = work.catch(() => undefined);
    return work;
  }

  private async finalizeMediaLocked(plan: ClipPlan, expected: ClipIdentity, verifiedNodes: Readback[], prepared: PreparedImage[]): Promise<unknown> {
    if (!this.canWrite()) throw new Error('Write operations are disabled in the RemNote bridge');
    let current = await this.readbackLocked(plan) as Readback;
    const assertUnchanged = (value: Readback) => {
      if (JSON.stringify(clipIdentity(value)) !== JSON.stringify(expected) ||
          JSON.stringify(clipNodeSnapshot(value)) !== JSON.stringify(verifiedNodes)) {
        throw new Error('Media recovery snapshot changed; no replacement was created');
      }
    };
    assertUnchanged(current);
    const pendingParts: ClipPart[] = [];
    for (let ci = 0; ci < plan.cards.length; ci++) {
      const rows = [plan.cards[ci].front, ...plan.cards[ci].back.filter(content)];
      rows.forEach((row,ri) => {
        const images = (current.cards[ci].nodes[ri].text as RichTextInterface).filter(part => typeof part !== 'string' && part.i === 'i') as {i:'i';url:string}[];
        row.filter(part => part.kind === 'image').forEach((part,ii) => { if (images[ii]?.url === part.uri) pendingParts.push(part); });
      });
    }
    this.validatePreparedImages(prepared, pendingParts);
    const edits: {nodeIndex: number; replacements: {elementIndex: number; uri: string; parsed: RichTextInterface[number]}[]}[] = [];
    let nodeIndex = 0;
    for (let ci = 0; ci < plan.cards.length; ci++) {
      const rows = [plan.cards[ci].front, ...plan.cards[ci].back.filter(content)];
      for (let ri = 0; ri < rows.length; ri++, nodeIndex++) {
        const node = current.cards[ci].nodes[ri], expectedImages = rows[ri].filter(part => part.kind === 'image');
        const images = (node.text as RichTextInterface).map((part, elementIndex) => ({part,elementIndex}))
          .filter(item => typeof item.part !== 'string' && item.part.i === 'i');
        if (images.length !== expectedImages.length) throw new Error('Image positions changed');
        const replacements = [];
        for (let ii = 0; ii < images.length; ii++) {
          const image = images[ii].part as {i:'i';url:string}, source = expectedImages[ii];
          if (image.url.startsWith('%LOCAL_FILE%')) continue; // Exact bytes were verified against this snapshot by HTTP.
          if (image.url !== source.uri) throw new Error('Original image URI changed; recovery refused');
          replacements.push({elementIndex:images[ii].elementIndex,uri:image.url,parsed:this.preparedImage(source, prepared)});
        }
        if (replacements.length) edits.push({nodeIndex,replacements});
      }
    }
    // Uploading was asynchronous. Re-read everything after it and immediately before each setText.
    for (const edit of edits) {
      current = await this.readbackLocked(plan) as Readback;
      assertUnchanged(current);
      const nodes = clipNodeSnapshot(current), node = nodes[edit.nodeIndex];
      const next = structuredClone(node.text) as RichTextInterface;
      for (const replacement of edit.replacements) {
        const old = next[replacement.elementIndex];
        if (typeof old === 'string' || old.i !== 'i' || old.url !== replacement.uri) throw new Error('Image changed before recovery');
        next[replacement.elementIndex] = {...old,url:(replacement.parsed as {i:'i';url:string}).url};
      }
      const target = await this.requiredRem(node.remId);
      if (JSON.stringify(target.text ?? []) !== JSON.stringify(node.text)) throw new Error('Image content changed before setText');
      await target.setText(next);
      // Only our just-updated node is allowed to advance the snapshot. No identity or content node is created.
      const after = await this.nodeReadback(node.remId);
      verifiedNodes[edit.nodeIndex] = clipNodeSnapshot({cards:[{nodes:[after]}]})[0];
    }
    return this.readbackLocked(plan);
  }

  private async requiredRem(id: string): Promise<PluginRem> {
    const rem = await this.plugin.rem.findOne(id);
    if (!rem) throw new Error(`Recorded Rem is missing: ${id}; no replacement was created`);
    return rem;
  }

  private async appendTo(rem: PluginRem, parentId: string): Promise<void> {
    // The SDK documents an omitted position as insertion at the front.
    await rem.setParent(parentId, (await this.requiredRem(parentId)).children?.length ?? 0);
  }

  private async configurePlainStructure(rem: PluginRem): Promise<void> {
    // Do not manufacture an empty back. The installed SDK rejects setBackText(undefined).
    if (rem.backText != null) throw new Error('New ordinary structure unexpectedly has a card back');
    await rem.setEnablePractice(false);
    await rem.setPracticeDirection('none');
    if (!this.plainStructureComplete(await this.nodeReadback(rem._id))) {
      throw new Error('Official SDK did not clear the plain structure card properties');
    }
  }

  private plainStructureComplete(node: Readback): boolean {
    return ['absent','null'].includes(node.backTextState) && node.practiceDirection === 'none' && node.practiceEnabled === false &&
      node.remType === 0 && !node.isCardItem && !node.isExtraCardDetail && !node.isMultiLineCard &&
      node.cardIds.length === 0 && node.cardTypes.length === 0;
  }

  private async document(title: string, parentId: string | null, suppliedId?: string): Promise<PluginRem> {
    let rem = suppliedId ? await this.requiredRem(suppliedId) : await this.plugin.rem.findByName([title], parentId);
    if (rem) {
      if (rem.parent !== parentId || await this.plugin.richText.toString(rem.text ?? []) !== title) {
        throw new Error(`Document location or type differs: ${title}`);
      }
      if (!await rem.isDocument()) {
        const literalTitle = (rem.text ?? []).every(part => typeof part === 'string' ||
          (part.i === 'm' && Object.keys(part).every(key => key === 'i' || key === 'text')));
        if ((rem.type ?? 0) !== 0 || !literalTitle || (rem.backText?.length ?? 0) || (rem.children?.length ?? 0) ||
            (await rem.getCards()).length || await rem.isCardItem() || (await rem.getTagRems()).length ||
            (await rem.getAliases()).length || await rem.isTable()) {
          throw new Error(`Document location or type differs: ${title}; existing nonempty Rem was not changed`);
        }
        // A directory preparation can fail after setText/setParent. Complete only an exact empty ordinary Rem.
        await this.configurePlainStructure(rem);
        await rem.setIsDocument(true);
        return this.requiredRem(rem._id);
      }
      return rem;
    }
    rem = await this.plugin.rem.createRem();
    if (!rem) throw new Error('SDK could not create a directory Rem');
    await rem.setText([title]);
    await rem.setParent(parentId);
    await this.configurePlainStructure(rem);
    await rem.setIsDocument(true);
    return this.requiredRem(rem._id);
  }

  private async exportLocked(plan: ClipPlan, prepared: PreparedImage[], prepareOnly: boolean, newSource: boolean): Promise<unknown> {
    if (!this.canWrite()) throw new Error('Write operations are disabled in the RemNote bridge');
    // Check every existing entry before any new mutation, including partial previous attempts.
    const existing = await Promise.all(plan.cards.map(c => this.plugin.storage.getSynced<StoredCard>(markerKey(plan.sourceId, c.key))));
    const previousAttempt = await this.plugin.storage.getSynced<StoredAttempt>(attemptKey(plan.sourceId));
    if (this.requireNewSource && !previousAttempt && !existing.some(Boolean) && !newSource) {
      throw new Error('Unregistered source requires explicit newSource or verified identity adoption; no card was created');
    }
    if (previousAttempt && previousAttempt.fingerprint !== attemptFingerprint(plan)) {
      throw new Error('Card plan changed for existing source');
    }
    if (existing.some(Boolean) && !existing.every(Boolean)) {
      throw new Error('Previous export is incomplete; inspect recorded IDs before retrying');
    }
    for (let i = 0; i < existing.length; i++) {
      const record = existing[i];
      if (record) await this.validateRecorded(plan, plan.cards[i], record);
    }
    if (existing.every(Boolean)) return this.readbackLocked(plan, new Set(plan.cards.map(c => c.key)));
    if (previousAttempt && previousAttempt.phase !== 'failed_before_cards') {
      throw new Error('Previous export stage is uncertain; inspect recorded IDs before retrying');
    }
    if (prepareOnly) {
      const error = 'Image preparation is pending; card content has not started';
      await this.plugin.storage.setSynced(attemptKey(plan.sourceId),
        {fingerprint:attemptFingerprint(plan),phase:'failed_before_cards',error});
      return {schemaVersion:KNOWLEDGE_CLIPPER_SCHEMA,sourceId:plan.sourceId,notStarted:true,requiresImages:true,error};
    }
    this.validatePreparedImages(prepared, plan.cards.flatMap(card => [...card.front,...card.back.flat()]));
    for (const card of plan.cards) for (const part of [...card.front,...card.back.flat()]) {
      if (part.kind === 'image') this.preparedImage(part, prepared);
    }
    const attempt: StoredAttempt = {fingerprint:attemptFingerprint(plan),phase:'preparing'};
    const savePhase = (phase: StoredAttempt['phase'], error?: string) => {
      attempt.phase = phase;
      if (error) attempt.error = error; else delete attempt.error;
      return this.plugin.storage.setSynced(attemptKey(plan.sourceId), attempt);
    };
    await savePhase('preparing');
    let root: PluginRem, directory: PluginRem;
    try {
      root = await this.document(plan.rootTitle, null, plan.rootRemId);
      directory = root;
      for (let i = 0; i < plan.documentPath.length; i++)
        directory = await this.document(plan.documentPath[i]!, directory._id,
          i === plan.documentPath.length - 1 ? plan.targetDocumentId : undefined);
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      await savePhase('failed_before_cards', message);
      return { schemaVersion: KNOWLEDGE_CLIPPER_SCHEMA, sourceId: plan.sourceId, notStarted: true, error: message };
    }
    // Persist this before any per-card reservation or content create, so absent storage is never proof of no write.
    attempt.batchSeparator = {state:'pending',documentRemId:directory._id,plainVersion:1};
    await savePhase('writing_content');
    for (let i = 0; i < plan.cards.length; i++) {
      if (existing[i]) continue;
      const card = plan.cards[i];
      const key = markerKey(plan.sourceId, card.key);
      const record: StoredCard = { fingerprint: fingerprint(plan, card), state: 'writing', rootRemId: root._id,
        documentRemId: directory._id, remIds: [], childRemIds: [] };
      // Reserve before create: interrupted/ambiguous writes never silently create duplicate cards.
      await this.plugin.storage.setSynced(key, record);
      const rem = await this.plugin.rem.createRem();
      if (!rem) throw new Error(`SDK could not create card ${card.key}`);
      record.remIds.push(rem._id);
      await this.plugin.storage.setSynced(key, record);
      await rem.setText(this.partsToSdkRichText(card.front, prepared));
      await this.appendTo(rem, directory._id);
      await rem.setIsDocument(false);
      if (card.kind === 'basic' || card.kind === 'cloze') {
        // MultiLineCard marks an answer item for the parent, not a card front.
        // Marking this front would turn its containing directory into a card.
        for (const row of card.back.filter(content)) {
          const child = await this.plugin.rem.createRem();
          if (!child) throw new Error('SDK could not create an answer item');
          record.childRemIds.push(child._id);
          await this.plugin.storage.setSynced(key, record);
          await child.setText(this.partsToSdkRichText(row, prepared));
          await this.appendTo(child, rem._id);
          await child.setIsDocument(false);
          if (card.kind === 'basic') await child.setIsCardItem(true);
          else await child.addPowerup(BuiltInPowerupCodes.ExtraCardDetail);
        }
        if (card.kind === 'basic') await rem.setPracticeDirection('forward');
        await rem.setEnablePractice(true);
      } else if (card.kind === 'plain') {
        await rem.setEnablePractice(false);
      }
      record.state = 'complete';
      await this.plugin.storage.setSynced(key, record);
    }
    // One blank sibling follows the entire source, not each converted paragraph or answer item.
    // Record both intent and identity before mutations; an ambiguous interruption must not create a second blank.
    attempt.batchSeparator.state = 'writing';
    await savePhase('writing_content');
    const separator = await this.plugin.rem.createRem();
    if (!separator) throw new Error('SDK could not create the batch separator');
    attempt.batchSeparator.remId = separator._id;
    await savePhase('writing_content');
    await separator.setText([]);
    await this.appendTo(separator, directory._id);
    await separator.setIsDocument(false);
    await separator.setIsCardItem(false);
    await this.configurePlainStructure(separator);
    attempt.batchSeparator.state = 'complete';
    await savePhase('complete');
    return this.readbackLocked(plan, new Set());
  }

  private async validateRecorded(plan: ClipPlan, card: ClipCard, record: StoredCard): Promise<void> {
    if (record.fingerprint !== fingerprint(plan, card)) throw new Error(`Card plan changed for existing key ${card.key}`);
    if (record.state !== 'complete') throw new Error(`Previous write is incomplete for ${card.key}; inspect recorded IDs before retrying`);
    await this.requiredRem(record.rootRemId);
    await this.requiredRem(record.documentRemId);
    for (const id of [...record.remIds, ...record.childRemIds]) await this.requiredRem(id);
    await this.cardReadback(card, record, true);
  }

  private async nodeReadback(id: string): Promise<Record<string, unknown>> {
    const rem = await this.requiredRem(id);
    const cards = await rem.getCards();
    return { remId: rem._id, parentId: rem.parent, text: rem.text ?? [], backText: rem.backText ?? [],
      rawBackText: rem.backText ?? null, backTextState: rem.backText === undefined ? 'absent' : rem.backText === null ? 'null' : 'present',
      children: rem.children ?? [], remType: rem.type ?? 0, rawRemType: rem.type ?? null, isDocument: await rem.isDocument(),
      isCardItem: await rem.isCardItem(), isExtraCardDetail: await rem.hasPowerup(BuiltInPowerupCodes.ExtraCardDetail),
      isMultiLineCard: await rem.hasPowerup(BuiltInPowerupCodes.MultiLineCard),
      practiceDirection: await rem.getPracticeDirection(),
      practiceEnabled: await rem.getEnablePractice(),
      cardIds: cards.map(c => c._id), cardTypes: cards.map(c => c.type),
      parts: richTextToParts(rem.text), backParts: richTextToParts(rem.backText) };
  }

  private async separatorReadback(attempt: StoredAttempt, directory: PluginRem,
      contentIds: string[]): Promise<Record<string, unknown>> {
    const record = attempt.batchSeparator!;
    if (!record.remId || record.state === 'pending' || record.state !== 'complete' && attempt.phase === 'complete') {
      throw new Error('Recorded batch separator is incomplete; inspect recorded IDs before retrying');
    }
    const pending = record.state === 'writing';
    const node = await this.nodeReadback(record.remId);
    const children = directory.children ?? [];
    const expectedOrder = [...contentIds, record.remId];
    const start = children.indexOf(contentIds[0]);
    const belongs = node.parentId === directory._id;
    const batchIds = new Set(expectedOrder);
    const positions = children.flatMap((id,index) => batchIds.has(id) ? [index] : []);
    const pendingOrderValid = positions.length === contentIds.length + (belongs ? 1 : 0) &&
      positions[positions.length-1] - positions[0] + 1 === positions.length &&
      contentIds.every(id => children.indexOf(id) >= 0 && children.indexOf(id) === children.lastIndexOf(id)) &&
      (belongs ? children.indexOf(record.remId) === children.lastIndexOf(record.remId) : !children.includes(record.remId));
    const completeOrderValid = start >= 0 && expectedOrder.every((id,index) => children[start+index] === id && children.indexOf(id) === children.lastIndexOf(id));
    if (record.documentRemId !== directory._id || !(belongs || pending && node.parentId === null) || contentIds.includes(record.remId) ||
        !emptyRichText(node.text) || !emptyRichText(node.backText) || node.remType !== 0 || node.isDocument || node.isCardItem ||
        node.isExtraCardDetail || typeof node.practiceEnabled !== 'boolean' || !pending && node.practiceEnabled !== false || (node.children as unknown[]).length ||
        (node.cardIds as unknown[]).length || (node.cardTypes as unknown[]).length || !(pending ? pendingOrderValid : completeOrderValid) ||
        !pending && record.plainVersion === 1 && !this.plainStructureComplete(node)) {
      throw new Error('Recorded batch separator or sibling order differs; no replacement was created');
    }
    return {remId:record.remId,previousRemId:contentIds[contentIds.length-1],node};
  }

  /** Finish only a known incomplete batch using its exact observed identities and raw snapshot. */
  finalizeSpacing(value: unknown): Promise<unknown> {
    const request = value as {plan:ClipPlan;expected:ClipIdentity;separatorRemId:string;verifiedNodes:Readback[];documentChildren:string[]};
    const plan = validateClipPlan(request?.plan);
    if (!request.expected || !request.separatorRemId || !Array.isArray(request.verifiedNodes) || !Array.isArray(request.documentChildren)) {
      throw new Error('Verified spacing identity and node snapshot required');
    }
    const work = this.queue.then(async () => {
      if (!this.canWrite()) throw new Error('KnowledgeClipper writes are disabled');
      const snapshot = (read:Readback) => [...clipNodeSnapshot(read), ...clipNodeSnapshot({cards:[{nodes:[read.batchSeparator.node]}]})];
      let nodes = structuredClone(request.verifiedNodes), children = [...request.documentChildren];
      const readUnchanged = async () => {
        const current = await this.readbackLocked(plan) as Readback;
        if (JSON.stringify(clipIdentity(current)) !== JSON.stringify(request.expected) ||
            current.batchSeparator?.remId !== request.separatorRemId || JSON.stringify(snapshot(current)) !== JSON.stringify(nodes) ||
            JSON.stringify(current.documentChildren) !== JSON.stringify(children)) {
          throw new Error('Spacing recovery snapshot changed; no replacement was created');
        }
        return current;
      };
      let current = await readUnchanged();
      if (!current.batchSeparatorPending) return current;
      const attempt = await this.plugin.storage.getSynced<StoredAttempt>(attemptKey(plan.sourceId));
      if (!attempt || attempt.phase === 'complete' || attempt.batchSeparator?.state !== 'writing' ||
          attempt.batchSeparator.remId !== request.separatorRemId || attempt.batchSeparator.documentRemId !== current.documentRemId) {
        throw new Error('Only an existing incomplete separator identity may be finalized');
      }
      const desired = [...current.cards.flatMap((card:Readback) => card.remIds),request.separatorRemId] as string[];
      const start = Math.min(...desired.map(id => children.indexOf(id)).filter(index => index >= 0));
      for (let index=0;index<desired.length;index++) {
        if (children[start+index] === desired[index]) continue;
        current = await readUnchanged();
        const rem = await this.requiredRem(desired[index]);
        await rem.setParent(current.documentRemId,start+index);
        children = children.filter(id => id !== rem._id);children.splice(start+index,0,rem._id);
        // Parent is the only changed raw field, and only the recorded blank may have been unattached.
        nodes = nodes.map(node => node.remId === rem._id ? {...node,parentId:current.documentRemId} : node);
      }
      current = await readUnchanged();
      if (current.batchSeparator.node.practiceEnabled !== false) {
        await (await this.requiredRem(request.separatorRemId)).setEnablePractice(false);
        nodes[nodes.length-1] = {...nodes[nodes.length-1],practiceEnabled:false,practiceDirection:'none'};
        // SDK practice direction may remain unchanged for plain Rems; read its exact resulting value only.
        const after = await this.nodeReadback(request.separatorRemId);
        nodes[nodes.length-1].practiceDirection = after.practiceDirection;
      }
      // A newly created plain separator may have stopped after any of the official property setters.
      if (attempt.batchSeparator.plainVersion === 1) {
        for (const operation of ['direction','back'] as const) {
          current = await readUnchanged();
          const blank = await this.requiredRem(request.separatorRemId);
          if (operation === 'direction' && current.batchSeparator.node.practiceDirection !== 'none') {
            await blank.setPracticeDirection('none');
            nodes[nodes.length-1] = {...nodes[nodes.length-1],practiceDirection:'none'};
          }
          if (operation === 'back' && current.batchSeparator.node.backTextState === 'present') {
            throw new Error('New separator unexpectedly has a card back; original identity retained');
          }
        }
      }
      await readUnchanged();
      attempt.batchSeparator.state = 'complete';attempt.phase = 'complete';delete attempt.error;
      await this.plugin.storage.setSynced(attemptKey(plan.sourceId),attempt);
      return this.readbackLocked(plan);
    });
    this.queue = work.catch(() => undefined);
    return work;
  }

  /** Explicitly clear only empty card properties on exact recorded directories and/or the existing separator. */
  repairPlainStructure(value: unknown): Promise<unknown> {
    const request = value as {plan:ClipPlan;expected:ClipIdentity;targetRemIds:string[];verifiedNodes:Readback[]};
    const plan = validateClipPlan(request?.plan);
    if (!request.expected || !Array.isArray(request.targetRemIds) || !request.targetRemIds.length || request.targetRemIds.length > 7 ||
        new Set(request.targetRemIds).size !== request.targetRemIds.length || !Array.isArray(request.verifiedNodes)) {
      throw new Error('Exact existing structure identities and snapshots required');
    }
    const work = this.queue.then(async () => {
      if (!this.canWrite()) throw new Error('KnowledgeClipper writes are disabled');
      let expectedNodes = structuredClone(request.verifiedNodes);
      const readUnchanged = async () => {
        const read = await this.readbackLocked(plan) as Readback;
        if (JSON.stringify(clipIdentity(read)) !== JSON.stringify(request.expected) ||
            JSON.stringify(clipStructureSnapshot(read)) !== JSON.stringify(expectedNodes)) {
          throw new Error('Plain structure snapshot changed; no content or identity was replaced');
        }
        return read;
      };
      const initial = await readUnchanged();
      if (initial.batchSeparatorPending) throw new Error('Finish the existing incomplete batch before plain structure repair');
      const allowed = new Set([...(initial.directoryNodes ?? []).map((node:Readback) => node.remId),
        ...(initial.batchSeparator ? [initial.batchSeparator.remId] : [])]);
      for (const id of request.targetRemIds) {
        if (!allowed.has(id)) throw new Error('Plain structure repair is limited to recorded directories and separator');
        const node = expectedNodes.find(node => node.remId === id)!;
        if (!emptyRichText(node.backText) || node.remType !== 0 || node.isCardItem || node.isExtraCardDetail || node.isMultiLineCard ||
            node.cardIds.length || node.cardTypes.length || typeof node.practiceEnabled !== 'boolean' ||
            !['none','forward','backward','both'].includes(node.practiceDirection) ||
            id === initial.batchSeparator?.remId && (!emptyRichText(node.text) || node.children.length)) {
          throw new Error('Structure has user content or card properties that cannot be removed safely');
        }
      }
      for (const id of request.targetRemIds) {
        for (const operation of ['practice','direction','back'] as const) {
          await readUnchanged();
          const before = expectedNodes.find(node => node.remId === id)!;
          const rem = await this.requiredRem(id);
          if (operation === 'practice' && before.practiceEnabled !== false) await rem.setEnablePractice(false);
          else if (operation === 'direction' && before.practiceDirection !== 'none') await rem.setPracticeDirection('none');
          else if (operation === 'back' && before.backTextState === 'present') await clearEmptyBackWithEditor(this.plugin,rem,readUnchanged);
          else continue;
          const after = (await this.nodeReadback(id)) as Readback;
          const allowedFields = operation === 'back' ? ['backText','rawBackText','backTextState'] :
            operation === 'direction' ? ['practiceDirection'] : ['practiceEnabled'];
          const advanced = {...before,...Object.fromEntries(allowedFields.map(field => [field,after[field]]))};
          expectedNodes = expectedNodes.map(node => node.remId === id ? advanced : node);
          // The full next read detects unexpected side effects and user edits before any further mutation.
          await readUnchanged();
        }
      }
      const completed = await readUnchanged();
      for (const id of request.targetRemIds) if (!this.plainStructureComplete(expectedNodes.find(node => node.remId === id)!)) {
        throw new Error('Official SDK has not completed plain structure settings; original identities retained');
      }
      return completed;
    });
    this.queue = work.catch(() => undefined);
    return work;
  }

  private async cardReadback(card: ClipCard, record: StoredCard, reused: boolean): Promise<Record<string, unknown>> {
    const nodes = await Promise.all([...record.remIds, ...record.childRemIds].map(id => this.nodeReadback(id)));
    const frontNode = nodes[0];
    const front = frontNode.parts as ClipPart[];
    const back = nodes.slice(1).map(n => n.parts as ClipPart[]);
    if (frontNode.parentId !== record.documentRemId || nodes.some(n => n.isDocument || n.remType !== 0) ||
        frontNode.practiceEnabled !== (card.kind !== 'plain') ||
        (card.kind === 'basic' && frontNode.practiceDirection !== 'forward') ||
        semantic(front) !== semantic(card.front) ||
        JSON.stringify(frontNode.children) !== JSON.stringify(record.childRemIds) ||
        nodes.slice(1).some(n => n.parentId !== record.remIds[0] ||
          (card.kind === 'basic' ? !n.isCardItem : !n.isExtraCardDetail)) ||
        JSON.stringify(back.map(semantic)) !== JSON.stringify(card.back.filter(content).map(semantic))) {
      throw new Error(`Recorded content or location differs for ${card.key}; no replacement was created`);
    }
    return { key: card.key, kind: card.kind, reused, remIds: record.remIds, childRemIds: record.childRemIds,
      cardIds: nodes.flatMap(n => n.cardIds as string[]), front, back, nodes };
  }


  /** Cross-plugin migration adopts exact existing IDs only; it never creates or edits Rems. */
  adopt(value: unknown): Promise<unknown> {
    const request = value as {plan: ClipPlan; expected: ClipIdentity; verifiedNodes: Readback[]};
    const plan = validateClipPlan(request?.plan);
    const work = this.queue.then(async () => {
      if (!this.canWrite()) throw new Error('KnowledgeClipper writes are disabled');
      const expected = request.expected;
      if (!expected || typeof expected.rootRemId !== 'string' || !expected.rootRemId ||
          typeof expected.documentRemId !== 'string' || !expected.documentRemId ||
          !Array.isArray(expected.cards) || expected.cards.length !== plan.cards.length ||
          !Array.isArray(request.verifiedNodes)) throw new Error('Complete verified identity and node snapshot required');
      if (plan.rootRemId && plan.rootRemId !== expected.rootRemId) throw new Error('Adoption root identity differs');
      const ids: string[] = [];
      const records = expected.cards.map((entry, index): StoredCard => {
        if (entry.key !== plan.cards[index].key || !Array.isArray(entry.remIds) || entry.remIds.length !== 1 ||
            !Array.isArray(entry.childRemIds) || !Array.isArray(entry.cardIds) ||
            [...entry.remIds, ...entry.childRemIds, ...entry.cardIds].some(id => typeof id !== 'string' || !id)) {
          throw new Error('Incomplete adoption card identity');
        }
        ids.push(...entry.remIds, ...entry.childRemIds);
        return {fingerprint:fingerprint(plan,plan.cards[index]),state:'complete',
          rootRemId:expected.rootRemId,documentRemId:expected.documentRemId,
          remIds:entry.remIds,childRemIds:entry.childRemIds};
      });
      if (new Set(ids).size !== ids.length || ids.includes(expected.rootRemId) || ids.includes(expected.documentRemId)) {
        throw new Error('Adoption contains duplicate or directory content IDs');
      }
      const current = await this.readbackLocked(plan, undefined, records) as Readback;
      if (JSON.stringify(clipIdentity(current)) !== JSON.stringify(expected) ||
          JSON.stringify(clipNodeSnapshot(current)) !== JSON.stringify(request.verifiedNodes)) {
        throw new Error('Adoption snapshot changed; no journal or content was written');
      }
      const previous = await this.plugin.storage.getSynced<StoredAttempt>(attemptKey(plan.sourceId));
      if (previous && previous.fingerprint !== attemptFingerprint(plan)) throw new Error('Existing source plan differs');
      if (previous?.batchSeparator && previous.batchSeparator.state !== 'complete') {
        throw new Error('Incomplete batch spacing must be finalized before identity adoption');
      }
      if (previous?.batchSeparator) await this.readbackLocked(plan);
      for (let i = 0; i < records.length; i++) {
        const existing = await this.plugin.storage.getSynced<StoredCard>(markerKey(plan.sourceId,plan.cards[i].key));
        if (existing && JSON.stringify(existing) !== JSON.stringify(records[i])) {
          throw new Error('Existing card journal differs; no adoption was performed');
        }
      }
      // Reserve before journal updates: a storage interruption remains unknown and never permits export.
      await this.plugin.storage.setSynced(attemptKey(plan.sourceId),
        {fingerprint:attemptFingerprint(plan),phase:'writing_content',...(previous?.batchSeparator ? {batchSeparator:previous.batchSeparator} : {})});
      for (let i=0; i<records.length; i++) await this.plugin.storage.setSynced(
        markerKey(plan.sourceId,plan.cards[i].key),records[i]);
      await this.plugin.storage.setSynced(attemptKey(plan.sourceId),
        {fingerprint:attemptFingerprint(plan),phase:'complete',...(previous?.batchSeparator ? {batchSeparator:previous.batchSeparator} : {})});
      return this.readbackLocked(plan);
    });
    this.queue = work.catch(() => undefined);
    return work;
  }

  readback(value: unknown): Promise<unknown> {
    const plan = validateClipPlan(value);
    const work = this.queue.then(() => this.readbackLocked(plan));
    this.queue = work.catch(() => undefined);
    return work;
  }

  private async readbackLocked(plan: ClipPlan, reused?: Set<string>, suppliedRecords?: StoredCard[]): Promise<unknown> {
    const records = suppliedRecords ?? await Promise.all(plan.cards.map(card => this.plugin.storage.getSynced<StoredCard>(markerKey(plan.sourceId, card.key))));
    const attempt = suppliedRecords ? undefined : await this.plugin.storage.getSynced<StoredAttempt>(attemptKey(plan.sourceId));
    if (attempt && attempt.fingerprint !== attemptFingerprint(plan)) throw new Error('Recorded export attempt differs');
    if (records.every(record => !record)) {
      if (attempt?.phase === 'failed_before_cards') {
        return { schemaVersion: KNOWLEDGE_CLIPPER_SCHEMA, sourceId: plan.sourceId, notStarted: true, ...(attempt.error ? {error:attempt.error} : {}) };
      }
      throw new Error('No confirmed pre-content failure record; import status remains unknown');
    }
    if (records.some(record => !record)) throw new Error('Recorded import is incomplete; no replacement was created');
    const results: Record<string, unknown>[] = [];
    let rootRemId = '', documentRemId = '';
    for (let i = 0; i < plan.cards.length; i++) {
      const card = plan.cards[i], record = records[i]!;
      if (record.fingerprint !== fingerprint(plan, card) || record.state !== 'complete') throw new Error(`Recorded import is changed or incomplete for ${card.key}`);
      rootRemId = record.rootRemId;
      documentRemId = record.documentRemId;
      results.push(await this.cardReadback(card, record, reused?.has(card.key) ?? true));
    }
    const root = await this.requiredRem(rootRemId);
    let parent = root;
    if (root.parent !== null || !await root.isDocument() || await this.plugin.richText.toString(root.text ?? []) !== plan.rootTitle) {
      throw new Error('Recorded root directory changed');
    }
    const directoryNodes = [await this.nodeReadback(rootRemId)];
    for (let i = 0; i < plan.documentPath.length; i++) {
      const title = plan.documentPath[i]!;
      const child = i === plan.documentPath.length - 1 && plan.targetDocumentId
        ? await this.plugin.rem.findOne(plan.targetDocumentId)
        : await this.plugin.rem.findByName([title], parent._id);
      if (!child || child.parent !== parent._id || !await child.isDocument() ||
          await this.plugin.richText.toString(child.text ?? []) !== title) throw new Error('Recorded mode directory changed');
      parent = child;
      directoryNodes.push(await this.nodeReadback(child._id));
    }
    if (parent._id !== documentRemId) throw new Error('Recorded mode directory moved');
    const batchSeparator = attempt?.batchSeparator ? await this.separatorReadback(attempt, parent,
      records.flatMap(record => record!.remIds)) : undefined;
    const batchSeparatorPending = attempt?.batchSeparator?.state === 'writing';
    return { schemaVersion: KNOWLEDGE_CLIPPER_SCHEMA, sourceId: plan.sourceId, rootRemId, documentRemId, cards: results, directoryNodes,
      ...(batchSeparator ? {batchSeparator,documentChildren:[...(parent.children ?? [])],...(batchSeparatorPending ? {
        batchSeparatorPending:true,spacingRecovery:{eligible:true,kind:'finalize-batch-separator'} } : {})} : {}) };
  }
}
