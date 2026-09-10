import { readFile } from "node:fs/promises";
import path from "node:path";
import type { CategoryRegistry, CommonArtifactManifest, ManifestFile, OcrArtifactProvenanceRef, OcrHumanResolutionDiff, OcrHumanResolutionInput, OcrProofreadBundle, OcrProofreadItem, OcrTextSource, SourceSegment, StandardInput, TaskManifest } from "./types.js";
import { fileDigest, packagePath, readJson, requireString, segmentText, sha256, stableStringify } from "./util.js";

export interface ValidatedOcrHumanResolution {
  input: OcrHumanResolutionInput;
  sourceBundle: OcrProofreadBundle;
  sourceItem: OcrProofreadItem;
  resolvedText: string;
  resolvedSegments: SourceSegment[];
  protectedRanges: Array<{ segment_index: number; start: number; end: number; exact_text: string }>;
}

export interface LoadedContracts {
  manifest: TaskManifest;
  input: StandardInput;
  packageRoot: string;
  manifestSha256: string;
  inputSha256: string;
  inputRelativePath: string;
  filesByPath: Map<string, ManifestFile>;
  ocrHumanResolutions: Map<string, ValidatedOcrHumanResolution>;
  commonInputManifest?: CommonArtifactManifest;
}

export async function loadContracts(manifestPath: string, inputPath: string, commonJobRoot?: string): Promise<LoadedContracts> {
  const manifestDocument = await readJson<TaskManifest | CommonArtifactManifest>(manifestPath);
  const input = await readJson<StandardInput>(inputPath);
  const commonManifest = "schemaVersion" in manifestDocument ? manifestDocument : undefined;
  const legacyManifest = "schema_version" in manifestDocument ? manifestDocument : undefined;
  const isCommonArtifact = commonManifest?.schemaVersion === "1.0.0";
  if (!isCommonArtifact && legacyManifest?.schema_version !== "task-manifest/1.0.0") throw new Error("UNSUPPORTED_SCHEMA input artifact manifest");
  if (isCommonArtifact && !commonJobRoot) throw new Error("INPUT_INVALID common artifact requires job root");
  const packageRoot = isCommonArtifact ? path.resolve(commonJobRoot!) : path.dirname(path.resolve(manifestPath));
  const manifest: TaskManifest = isCommonArtifact
    ? {
        schema_version: "task-manifest/1.0.0",
        package_id: commonManifest!.artifactId,
        package_version: commonManifest!.contractVersion,
        created_at: commonManifest!.createdAt,
        files: commonManifest!.files.map((file) => ({ path: file.path, sha256: file.sha256, size: file.bytes, media_type: file.mediaType })),
      }
    : legacyManifest!;
  if (!(["standard-input/1.0.0", "standard-input/2.0.0", "standard-input/3.0.0", "standard-input/4.0.0", "standard-input/5.0.0", "standard-input/6.0.0", "standard-input/7.0.0", "standard-input/8.0.0"] as string[]).includes(input.schema_version)) throw new Error(`UNSUPPORTED_SCHEMA ${input.schema_version}`);
  if ((input.schema_version === "standard-input/8.0.0") !== (input.language_practice_resume !== undefined)) throw new Error("INPUT_INVALID language_practice_resume requires standard-input/8.0.0");
  requireString(manifest.package_id, "manifest.package_id");
  requireString(input.package_id, "input.package_id");
  requireString(input.task_id, "input.task_id");
  requireString(input.input_version, "input.input_version");
  if (!isCommonArtifact && manifest.package_id !== input.package_id) throw new Error("INPUT_INVALID package_id mismatch");
  if (isCommonArtifact && commonManifest!.kind !== "content-input") throw new Error(`INPUT_INVALID artifact kind ${commonManifest!.kind}`);
  if (input.schema_version === "standard-input/3.0.0") {
    if (input.mode !== "question") throw new Error(`INPUT_INVALID question workflow mode ${String(input.mode)}`);
    validateQuestionWorkflowInput(input);
  } else {
    if (input.mode !== "card") throw new Error(`INPUT_INVALID unsupported mode ${String(input.mode)}`);
    if (input.questionWorkflow !== undefined) throw new Error("INPUT_INVALID questionWorkflow requires standard-input/3.0.0");
  }
  if (!Array.isArray(manifest.files) || manifest.files.length === 0) throw new Error("INPUT_INVALID manifest.files");
  if (!Array.isArray(input.items) || input.items.length === 0) throw new Error("INPUT_INVALID input.items");
  validateLanguagePracticeOperationInput(input);

  const filesByPath = new Map<string, ManifestFile>();
  for (const file of manifest.files) {
    requireString(file.path, "manifest.files[].path");
    if (filesByPath.has(file.path)) throw new Error(`INPUT_INVALID duplicate manifest path ${file.path}`);
    if (!/^[a-f0-9]{64}$/u.test(file.sha256)) throw new Error(`INPUT_INVALID sha256 ${file.path}`);
    if (!Number.isInteger(file.size) || file.size < 0) throw new Error(`INPUT_INVALID size ${file.path}`);
    filesByPath.set(file.path, file);
  }

  const inputRelative = path.relative(packageRoot, path.resolve(inputPath)).split(path.sep).join("/");
  const inputManifest = filesByPath.get(inputRelative);
  if (!inputManifest) throw new Error(`INPUT_INVALID input not declared in manifest: ${inputRelative}`);
  await verifyDeclaredFile(packageRoot, inputManifest);

  const seenItems = new Set<string>();
  const ocrHumanResolutions = new Map<string, ValidatedOcrHumanResolution>();
  for (const item of input.items) {
    requireString(item.item_id, "items[].item_id");
    if (seenItems.has(item.item_id)) throw new Error(`INPUT_INVALID duplicate item_id ${item.item_id}`);
    seenItems.add(item.item_id);
    validateLanguagePracticeInput(item);
    await validateLanguageSelection(item, input.schema_version, packageRoot, filesByPath);
    if (!item.source || !["text", "ocr_text", "conversation", "media_set", "question_workflow"].includes(item.source.kind)) throw new Error(`INPUT_INVALID source ${item.item_id}`);
    if (item.source.kind === "ocr_text" && !["standard-input/4.0.0", "standard-input/5.0.0"].includes(input.schema_version)) throw new Error(`INPUT_INVALID ocr_text requires standard-input/4.0.0 or standard-input/5.0.0 ${item.item_id}`);
    if (input.schema_version === "standard-input/5.0.0" && item.source.kind !== "ocr_text") throw new Error(`INPUT_INVALID standard-input/5.0.0 requires ocr_text ${item.item_id}`);
    if (item.ocr_human_resolution !== undefined && input.schema_version !== "standard-input/5.0.0") throw new Error(`INPUT_INVALID ocr_human_resolution requires standard-input/5.0.0 ${item.item_id}`);
    if (input.schema_version === "standard-input/5.0.0" && item.ocr_human_resolution === undefined) throw new Error(`INPUT_INVALID missing ocr_human_resolution ${item.item_id}`);
    const paths = item.source.kind === "media_set"
      ? item.source.assets.map((asset) => asset.material_path)
      : item.source.kind === "question_workflow"
        ? []
        : [item.source.material_path];
    if (item.source.kind !== "question_workflow" && paths.length === 0) throw new Error(`INPUT_INVALID no source material ${item.item_id}`);
    for (const sourcePath of paths) {
      const declared = filesByPath.get(sourcePath);
      if (!declared) throw new Error(`INPUT_INVALID undeclared material ${sourcePath}`);
      await verifyDeclaredFile(packageRoot, declared);
    }
    if (item.source.kind === "ocr_text") {
      await validateOcrProvenance(item.source, commonManifest, packageRoot, filesByPath);
      if (item.ocr_human_resolution) {
        ocrHumanResolutions.set(item.item_id, await validateOcrHumanResolution(item.item_id, item.source, item.ocr_human_resolution, commonManifest, packageRoot, filesByPath));
      }
    }
  }
  const cardStructureRef = input.questionWorkflow?.cardStructureRef;
  if (cardStructureRef) {
    const declared = filesByPath.get(cardStructureRef);
    if (!declared) throw new Error(`INPUT_INVALID undeclared cardStructureRef ${cardStructureRef}`);
    await verifyDeclaredFile(packageRoot, declared);
  }

  const [manifestBytes, inputBytes] = await Promise.all([readFile(manifestPath), readFile(inputPath)]);
  const manifestDigest = await fileDigest(manifestPath);
  const inputDigest = await fileDigest(inputPath);
  void manifestBytes;
  void inputBytes;
  return {
    manifest,
    input,
    packageRoot,
    manifestSha256: manifestDigest.sha256,
    inputSha256: inputDigest.sha256,
    inputRelativePath: inputRelative,
    filesByPath,
    ocrHumanResolutions,
    ...(commonManifest ? { commonInputManifest: commonManifest } : {}),
  };
}

async function validateLanguageSelection(item: StandardInput["items"][number], schemaVersion: StandardInput["schema_version"], packageRoot: string, filesByPath: Map<string, ManifestFile>): Promise<void> {
  const selection = item.language_selection;
  if (!selection) {
    if (["standard-input/6.0.0", "standard-input/7.0.0", "standard-input/8.0.0"].includes(schemaVersion)) throw new Error(`INPUT_INVALID missing language_selection ${item.item_id}`);
    return;
  }
  if (!["standard-input/6.0.0", "standard-input/7.0.0", "standard-input/8.0.0"].includes(schemaVersion) || item.source.kind !== "text") throw new Error(`INPUT_INVALID language_selection requires standard-input/6.0.0+ text ${item.item_id}`);
  assertExactKeys(selection, ["schema_version", "selected_text", "selected_text_sha256", "target_text", "target_text_sha256", "context_enabled", "context_segments"], "language_selection");
  if (selection.schema_version !== "language-selection/1.0.0" || typeof selection.context_enabled !== "boolean") throw new Error("INPUT_INVALID language_selection identity");
  requireString(selection.selected_text, "language_selection.selected_text");
  if (selection.selected_text.trim() === "" || sha256(selection.selected_text) !== selection.selected_text_sha256) throw new Error("INPUT_TAMPERED language_selection.selected_text");
  requireString(selection.target_text, "language_selection.target_text");
  if (selection.target_text.trim() === "" || sha256(selection.target_text) !== selection.target_text_sha256 || !selection.selected_text.includes(selection.target_text)) throw new Error("INPUT_TAMPERED language_selection.target_text");
  const selected = filesByPath.get(item.source.material_path);
  if (!selected) throw new Error(`INPUT_INVALID undeclared material ${item.source.material_path}`);
  await verifyDeclaredFile(packageRoot, selected);
  const selectedText = await readFile(packagePath(packageRoot, item.source.material_path), "utf8");
  if (selectedText.replace(/(?:\r\n|\r|\n)+$/u, "") !== selection.selected_text) throw new Error("INPUT_TAMPERED language_selection selected material");
  if (!Array.isArray(selection.context_segments)) throw new Error("INPUT_INVALID language_selection.context_segments");
  if (selection.context_enabled && selection.context_segments.length === 0) throw new Error("INPUT_INVALID language_selection missing context");
  for (const [index, segment] of selection.context_segments.entries()) {
    assertExactKeys(segment, ["segment_id", "order", "material_path", "sha256"], `language_selection.context_segments[${index}]`);
    requireIdentifier(segment.segment_id, "language_selection.context_segments[].segment_id");
    if (segment.order !== index) throw new Error("INPUT_INVALID language_selection context order");
    const declaration = filesByPath.get(segment.material_path);
    if (!declaration || declaration.sha256 !== segment.sha256) throw new Error("INPUT_TAMPERED language_selection context binding");
    await verifyDeclaredFile(packageRoot, declaration);
  }
  const resolution = item.human_overrides?.languageTargetResolution;
  if (resolution) {
    assertExactKeys(resolution, ["schema_version", "decision_id", "target_token", "action", "selected_text", "selected_text_sha256", "confirmed", ...(resolution.candidateOccurrence ? ["candidateOccurrence"] : [])], "human_overrides.languageTargetResolution");
    if (!["language-target-resolution/1.0.0", "language-target-resolution/2.0.0"].includes(resolution.schema_version) || resolution.confirmed !== true || !["accept_candidate", "user_correction"].includes(resolution.action)) throw new Error("INPUT_INVALID languageTargetResolution");
    if (schemaVersion === "standard-input/7.0.0" && resolution.schema_version !== "language-target-resolution/2.0.0") throw new Error("INPUT_INVALID standard-input/7.0.0 requires language-target-resolution/2.0.0");
    if (sha256(resolution.selected_text) !== resolution.selected_text_sha256) throw new Error("INPUT_TAMPERED languageTargetResolution selected_text");
    const expectedToken = sha256(stableStringify({ item_id: item.item_id, selected_text_sha256: selection.selected_text_sha256, target_text_sha256: selection.target_text_sha256, context_enabled: selection.context_enabled, context_segment_sha256s: selection.context_segments.map((entry) => entry.sha256) }));
    if (resolution.target_token !== expectedToken) throw new Error("INPUT_STALE languageTargetResolution target_token");
    if (resolution.action === "accept_candidate") {
      if (resolution.selected_text !== selection.target_text) throw new Error("INPUT_TAMPERED accept_candidate selected_text");
      if (schemaVersion === "standard-input/7.0.0" && !resolution.candidateOccurrence) throw new Error("INPUT_INVALID accept_candidate candidateOccurrence required");
      if (resolution.candidateOccurrence) await validateCandidateOccurrence(resolution.candidateOccurrence, selection, packageRoot);
    } else if (resolution.candidateOccurrence) {
      throw new Error("INPUT_INVALID user_correction forbids candidateOccurrence");
    }
  }
}

async function validateCandidateOccurrence(occurrence: { segmentId: string; order: number; start: number; end: number; exactText: string; sourceSha256: string }, selection: NonNullable<StandardInput["items"][number]["language_selection"]>, packageRoot: string): Promise<void> {
  assertExactKeys(occurrence, ["segmentId", "order", "start", "end", "exactText", "sourceSha256"], "languageTargetResolution.candidateOccurrence");
  const context = selection.context_segments[occurrence.order];
  if (!context || context.sha256 !== occurrence.sourceSha256) throw new Error("INPUT_STALE candidateOccurrence sourceSha256");
  const source = await readFile(packagePath(packageRoot, context.material_path), "utf8");
  const parts = segmentText(source, context.material_path, "text");
  const matches = parts.flatMap((part) => {
    const segmentId = `${context.segment_id}-${part.segment_id}`;
    const result: Array<{ segmentId: string; order: number; start: number; end: number; exactText: string; sourceSha256: string }> = [];
    let cursor = 0;
    while (true) {
      const start = part.text.indexOf(selection.target_text, cursor);
      if (start < 0) break;
      result.push({ segmentId, order: occurrence.order, start, end: start + selection.target_text.length, exactText: selection.target_text, sourceSha256: sha256(part.text) });
      cursor = start + selection.target_text.length;
    }
    return result;
  });
  const exactMatches = matches.filter((entry) => stableStringify(entry) === stableStringify(occurrence));
  if (exactMatches.length !== 1) throw new Error("INPUT_STALE candidateOccurrence coordinates");
}

async function validateOcrHumanResolution(
  itemId: string,
  source: OcrTextSource,
  resolution: OcrHumanResolutionInput,
  inputManifest: CommonArtifactManifest | undefined,
  jobRoot: string,
  filesByPath: Map<string, ManifestFile>,
): Promise<ValidatedOcrHumanResolution> {
  if (!inputManifest) throw new Error("INPUT_INVALID ocr_human_resolution requires common content-input artifact");
  assertExactKeys(resolution, ["schema_version", "resolution_id", "action", "source_proofread_artifact", "source_item_id", "original_text_sha256", "proofread_text_sha256", "unresolved_candidates", "reason", ...(resolution.correction === undefined ? [] : ["correction"])], "ocr_human_resolution");
  if (resolution.schema_version !== "ocr-human-resolution-input/1.0.0") throw new Error("INPUT_INVALID ocr_human_resolution.schema_version");
  requireIdentifier(resolution.resolution_id, "ocr_human_resolution.resolution_id");
  if (!(["accept_original", "user_correction"] as string[]).includes(resolution.action)) throw new Error("INPUT_INVALID ocr_human_resolution.action");
  if (resolution.source_item_id !== itemId) throw new Error("INPUT_TAMPERED ocr_human_resolution.source_item_id");
  requireString(resolution.reason, "ocr_human_resolution.reason");
  if (resolution.reason.trim() === "") throw new Error("INPUT_INVALID ocr_human_resolution.reason");
  if (!/^[a-f0-9]{64}$/u.test(resolution.original_text_sha256) || !/^[a-f0-9]{64}$/u.test(resolution.proofread_text_sha256)) throw new Error("INPUT_INVALID ocr_human_resolution source sha256");
  if (!Array.isArray(resolution.unresolved_candidates) || resolution.unresolved_candidates.length === 0) throw new Error("INPUT_INVALID ocr_human_resolution.unresolved_candidates");
  const artifactRef = resolution.source_proofread_artifact;
  assertExactKeys(artifactRef, ["artifactRef", "kind", "contractVersion", "manifestPath", "expectedSha256", "payload_path", "payload_sha256"], "ocr_human_resolution.source_proofread_artifact");
  if (artifactRef.kind !== "ocr-proofread-bundle" || artifactRef.contractVersion !== "1.0.0") throw new Error("INPUT_INVALID ocr_human_resolution source artifact identity");
  const ancestor = inputManifest.inputRefs.find((entry) => entry.artifactRef === artifactRef.artifactRef);
  if (!ancestor || ancestor.kind !== artifactRef.kind || ancestor.contractVersion !== artifactRef.contractVersion || ancestor.manifestPath !== artifactRef.manifestPath || ancestor.expectedSha256.toLowerCase() !== artifactRef.expectedSha256.toLowerCase()) throw new Error("INPUT_TAMPERED ocr_human_resolution ancestry reference");
  const manifestPath = packagePath(jobRoot, artifactRef.manifestPath);
  const manifestDigest = await fileDigest(manifestPath);
  if (manifestDigest.sha256 !== artifactRef.expectedSha256.toLowerCase()) throw new Error("INPUT_TAMPERED ocr_human_resolution manifest sha256");
  const manifest = await readJson<CommonArtifactManifest>(manifestPath);
  if (manifest.schemaVersion !== "1.0.0" || manifest.artifactId !== artifactRef.artifactRef || manifest.kind !== "ocr-proofread-bundle" || manifest.contractVersion !== "1.0.0" || manifest.producer.workerId !== "content.deepseek") throw new Error("INPUT_TAMPERED ocr_human_resolution manifest identity");
  const payload = manifest.files.find((file) => file.path === artifactRef.payload_path);
  if (!payload || payload.sha256 !== artifactRef.payload_sha256.toLowerCase()) throw new Error("INPUT_TAMPERED ocr_human_resolution payload binding");
  const payloadDigest = await fileDigest(packagePath(jobRoot, artifactRef.payload_path));
  if (payloadDigest.sha256 !== payload.sha256 || payloadDigest.size !== payload.bytes) throw new Error("INPUT_TAMPERED ocr_human_resolution payload sha256");
  const bundle = await readJson<OcrProofreadBundle>(packagePath(jobRoot, artifactRef.payload_path));
  if (bundle.schema_version !== "ocr-proofread-bundle/1.0.0" || bundle.release_state !== "unresolved") throw new Error("INPUT_INVALID ocr_human_resolution source bundle state");
  const sourceItem = bundle.items.find((entry) => entry.item_id === itemId);
  if (!sourceItem || sourceItem.state !== "unresolved" || sourceItem.unresolved.length === 0) throw new Error("INPUT_INVALID ocr_human_resolution source item state");
  if (sourceItem.original_text_sha256 !== resolution.original_text_sha256 || sourceItem.proofread_text_sha256 !== resolution.proofread_text_sha256) throw new Error("INPUT_TAMPERED ocr_human_resolution source text sha256");
  const currentMaterial = filesByPath.get(source.material_path);
  if (!currentMaterial || currentMaterial.sha256 !== sourceItem.original_text_sha256) throw new Error("INPUT_TAMPERED ocr_human_resolution original OCR material");
  if (stableStringify(source.provenance) !== stableStringify(sourceItem.provenance)) throw new Error("INPUT_TAMPERED ocr_human_resolution OCR provenance");
  if (stableStringify(resolution.unresolved_candidates) !== stableStringify(sourceItem.unresolved)) throw new Error("INPUT_TAMPERED ocr_human_resolution candidates");

  const candidateRanges = sourceItem.unresolved.map((candidate) => mapCandidateRange(candidate, sourceItem));
  let resolvedSegments = sourceItem.proofread_segments.map((segment) => ({ ...segment }));
  let protectedRanges: ValidatedOcrHumanResolution["protectedRanges"] = [];
  if (resolution.action === "accept_original") {
    if (resolution.correction !== undefined) throw new Error("INPUT_INVALID accept_original forbids correction");
    protectedRanges = candidateRanges.map((candidate) => ({ segment_index: candidate.segment_index, start: candidate.start, end: candidate.end, exact_text: resolvedSegments[candidate.segment_index]!.text.slice(candidate.start, candidate.end) }));
  } else {
    if (!resolution.correction) throw new Error("INPUT_INVALID user_correction requires correction");
    assertExactKeys(resolution.correction, ["corrected_text", "corrected_text_sha256", "diffs"], "ocr_human_resolution.correction");
    requireString(resolution.correction.corrected_text, "ocr_human_resolution.correction.corrected_text");
    if (sha256(resolution.correction.corrected_text) !== resolution.correction.corrected_text_sha256) throw new Error("INPUT_TAMPERED ocr_human_resolution corrected_text_sha256");
    if (!Array.isArray(resolution.correction.diffs) || resolution.correction.diffs.length === 0) throw new Error("INPUT_INVALID user_correction requires diffs");
    ({ segments: resolvedSegments, protectedRanges } = applyHumanCorrectionDiffs(resolvedSegments, candidateRanges, resolution.correction.diffs));
    if (canonicalSegments(resolvedSegments) !== normalizeDocument(resolution.correction.corrected_text)) throw new Error(`INPUT_TAMPERED ocr_human_resolution diff replay expected=${sha256(normalizeDocument(resolution.correction.corrected_text))} actual=${sha256(canonicalSegments(resolvedSegments))}`);
  }
  resolvedSegments = identifyHumanResolvedSegments(resolvedSegments);
  const resolvedText = resolution.action === "accept_original" ? sourceItem.proofread_text : resolution.correction!.corrected_text;
  if (canonicalSegments(resolvedSegments) !== normalizeDocument(resolvedText)) throw new Error("INPUT_TAMPERED ocr_human_resolution resolved text");
  return { input: resolution, sourceBundle: bundle, sourceItem, resolvedText, resolvedSegments, protectedRanges };
}

function mapCandidateRange(candidate: OcrProofreadItem["unresolved"][number], item: OcrProofreadItem): { segment_index: number; start: number; end: number } {
  const corrections = item.corrections.filter((entry) => entry.segment_index === candidate.segment_index).sort((left, right) => left.start - right.start);
  const mapOffset = (offset: number): number => corrections.reduce((mapped, entry) => entry.end <= offset ? mapped + entry.after.length - entry.before.length : mapped, offset);
  const start = mapOffset(candidate.start);
  const end = mapOffset(candidate.end);
  const segment = item.proofread_segments[candidate.segment_index];
  if (!segment || segment.text.slice(start, end) !== candidate.exact_text) throw new Error("INPUT_TAMPERED ocr_human_resolution candidate mapping");
  return { segment_index: candidate.segment_index, start, end };
}

function applyHumanCorrectionDiffs(segments: SourceSegment[], candidates: Array<{ segment_index: number; start: number; end: number }>, diffs: OcrHumanResolutionDiff[]): { segments: SourceSegment[]; protectedRanges: ValidatedOcrHumanResolution["protectedRanges"] } {
  const grouped = new Map<number, OcrHumanResolutionDiff[]>();
  for (const diff of diffs) {
    assertExactKeys(diff, ["segment_index", "operation", "start", "end", "before", "after"], "ocr_human_resolution.correction.diffs[]");
    if (!Number.isInteger(diff.segment_index) || !Number.isInteger(diff.start) || !Number.isInteger(diff.end)) throw new Error("INPUT_INVALID ocr_human_resolution diff integer");
    if (!(["substitution", "insertion", "deletion"] as string[]).includes(diff.operation)) throw new Error("INPUT_INVALID ocr_human_resolution diff operation");
    const segment = segments[diff.segment_index];
    if (!segment || diff.start < 0 || diff.end < diff.start || diff.end > segment.text.length || segment.text.slice(diff.start, diff.end) !== diff.before) throw new Error("INPUT_TAMPERED ocr_human_resolution diff before-exact");
    if (diff.operation === "insertion" && (diff.start !== diff.end || diff.before !== "" || diff.after === "")) throw new Error("INPUT_INVALID ocr_human_resolution insertion");
    if (diff.operation === "substitution" && (diff.end <= diff.start || diff.before === "" || diff.after === "" || diff.before === diff.after)) throw new Error("INPUT_INVALID ocr_human_resolution substitution");
    if (diff.operation === "deletion" && (diff.end <= diff.start || diff.before === "" || diff.after !== "")) throw new Error("INPUT_INVALID ocr_human_resolution deletion");
    const withinCandidate = candidates.some((candidate) => candidate.segment_index === diff.segment_index && (diff.start === diff.end
      ? diff.start >= candidate.start && diff.start <= candidate.end
      : diff.start >= candidate.start && diff.end <= candidate.end));
    if (!withinCandidate) throw new Error("INPUT_INVALID ocr_human_resolution diff outside unresolved candidate");
    const entries = grouped.get(diff.segment_index) ?? [];
    if (entries.some((entry) => diff.start < entry.end && diff.end > entry.start || (diff.start === diff.end && entry.start === entry.end && diff.start === entry.start))) throw new Error("INPUT_INVALID ocr_human_resolution overlapping diffs");
    entries.push(diff);
    grouped.set(diff.segment_index, entries);
  }
  const protectedRanges: ValidatedOcrHumanResolution["protectedRanges"] = [];
  const resolved = segments.map((segment) => {
    const entries = [...(grouped.get(segment.segment_index) ?? [])].sort((left, right) => left.start - right.start || left.end - right.end);
    let value = "";
    let cursor = 0;
    for (const entry of entries) {
      value += segment.text.slice(cursor, entry.start);
      const start = value.length;
      value += entry.after;
      if (entry.after !== "") protectedRanges.push({ segment_index: segment.segment_index, start, end: start + entry.after.length, exact_text: entry.after });
      cursor = entry.end;
    }
    value += segment.text.slice(cursor);
    return { ...segment, text: value };
  });
  let cursor = 0;
  for (const segment of resolved) {
    segment.start = cursor;
    segment.end = cursor + segment.text.length;
    cursor = segment.end + 2;
  }
  return { segments: resolved, protectedRanges };
}

function identifyHumanResolvedSegments(segments: SourceSegment[]): SourceSegment[] {
  return segments.map((segment) => ({
    ...segment,
    segment_id: `hseg-${sha256(`${segment.segment_id}\0${segment.text}`).slice(0, 16)}`,
  }));
}

function canonicalSegments(segments: SourceSegment[]): string {
  return segments.map((segment) => normalizeDocument(segment.text)).filter((value) => value.trim() !== "").join("\n\n");
}

function normalizeDocument(value: string): string {
  return value.replace(/\r\n?/gu, "\n").replace(/\n+$/u, "");
}

function assertExactKeys(value: object, allowed: string[], where: string): void {
  const actual = Object.keys(value).sort();
  const expected = [...allowed].sort();
  if (stableStringify(actual) !== stableStringify(expected)) throw new Error(`INPUT_INVALID ${where} properties`);
}

async function validateOcrProvenance(
  source: OcrTextSource,
  inputManifest: CommonArtifactManifest | undefined,
  jobRoot: string,
  filesByPath: Map<string, ManifestFile>,
): Promise<void> {
  if (!inputManifest) throw new Error("INPUT_INVALID ocr_text requires common content-input artifact provenance");
  const provenance = source.provenance;
  if (!provenance || provenance.schema_version !== "ocr-provenance/1.0.0" || provenance.producer_worker_id !== "image.ocr.mineru") throw new Error("INPUT_INVALID ocr provenance identity");
  await validateOcrArtifactRef(provenance.markdown_artifact, "image-ocr.markdown", source.material_path, provenance.producer_worker_id, inputManifest, jobRoot, filesByPath);
  if (provenance.structured_artifact) {
    const structured = await validateOcrArtifactRef(provenance.structured_artifact, "image-ocr.structured", provenance.structured_artifact.payload_path, provenance.producer_worker_id, inputManifest, jobRoot, filesByPath);
    const structuredDocument = await readJson<{ markdownArtifactRef?: string }>(packagePath(jobRoot, provenance.structured_artifact.payload_path));
    if (structuredDocument.markdownArtifactRef !== provenance.markdown_artifact.artifactRef) throw new Error("INPUT_TAMPERED ocr structured markdownArtifactRef");
    void structured;
  }
}

async function validateOcrArtifactRef(
  provenanceRef: OcrArtifactProvenanceRef,
  expectedKind: "image-ocr.markdown" | "image-ocr.structured",
  expectedPayloadPath: string,
  producerWorkerId: string,
  inputManifest: CommonArtifactManifest,
  jobRoot: string,
  filesByPath: Map<string, ManifestFile>,
): Promise<CommonArtifactManifest> {
  if (!provenanceRef || typeof provenanceRef !== "object") throw new Error(`INPUT_INVALID ${expectedKind} provenance`);
  requireString(provenanceRef.artifactRef, `${expectedKind}.artifactRef`);
  requireString(provenanceRef.manifestPath, `${expectedKind}.manifestPath`);
  requireString(provenanceRef.expectedSha256, `${expectedKind}.expectedSha256`);
  requireString(provenanceRef.payload_path, `${expectedKind}.payload_path`);
  requireString(provenanceRef.payload_sha256, `${expectedKind}.payload_sha256`);
  if (provenanceRef.kind !== expectedKind || provenanceRef.contractVersion !== "1.0.0") throw new Error(`INPUT_INVALID ${expectedKind} artifact identity`);
  if (provenanceRef.payload_path !== expectedPayloadPath) throw new Error(`INPUT_INVALID ${expectedKind} payload path mismatch`);
  const nestedRef = inputManifest.inputRefs.find((entry) => entry.artifactRef === provenanceRef.artifactRef);
  if (!nestedRef
    || nestedRef.kind !== provenanceRef.kind
    || nestedRef.contractVersion !== provenanceRef.contractVersion
    || nestedRef.manifestPath !== provenanceRef.manifestPath
    || nestedRef.expectedSha256.toLowerCase() !== provenanceRef.expectedSha256.toLowerCase()) {
    throw new Error(`INPUT_TAMPERED ${expectedKind} ancestry reference`);
  }
  const manifestPath = packagePath(jobRoot, provenanceRef.manifestPath);
  const manifestDigest = await fileDigest(manifestPath);
  if (manifestDigest.sha256 !== provenanceRef.expectedSha256.toLowerCase()) throw new Error(`INPUT_TAMPERED ${expectedKind} manifest sha256`);
  const manifest = await readJson<CommonArtifactManifest>(manifestPath);
  if (manifest.schemaVersion !== "1.0.0" || manifest.artifactId !== provenanceRef.artifactRef || manifest.kind !== expectedKind || manifest.contractVersion !== "1.0.0") throw new Error(`INPUT_TAMPERED ${expectedKind} manifest identity`);
  if (manifest.producer.workerId !== producerWorkerId || !/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/u.test(manifest.producer.workerVersion)) throw new Error(`INPUT_TAMPERED ${expectedKind} producer`);
  const payload = manifest.files.find((file) => file.path === provenanceRef.payload_path);
  if (!payload || payload.sha256 !== provenanceRef.payload_sha256.toLowerCase()) throw new Error(`INPUT_TAMPERED ${expectedKind} payload binding`);
  const declared = filesByPath.get(provenanceRef.payload_path);
  if (!declared || declared.sha256 !== payload.sha256 || declared.size !== payload.bytes) throw new Error(`INPUT_TAMPERED ${expectedKind} content-input declaration`);
  await verifyDeclaredFile(jobRoot, declared);
  return manifest;
}

function validateQuestionWorkflowInput(input: StandardInput): void {
  const workflow = input.questionWorkflow;
  if (!workflow || workflow.schemaVersion !== "question-workflow-input/1.0.0") throw new Error("INPUT_INVALID questionWorkflow.schemaVersion");
  if (workflow.phase !== "answer" && workflow.phase !== "make-cards") throw new Error("INPUT_INVALID questionWorkflow.phase");
  if (input.items.length !== 1 || input.items[0]?.source.kind !== "question_workflow") throw new Error("INPUT_INVALID question workflow requires exactly one question_workflow item");
  if (!Array.isArray(workflow.questionTurns) || workflow.questionTurns.length === 0) throw new Error("INPUT_INVALID questionTurns");
  if (!Array.isArray(workflow.answerAncestry)) throw new Error("INPUT_INVALID answerAncestry");
  const expectedAnswers = workflow.phase === "answer" ? workflow.questionTurns.length - 1 : workflow.questionTurns.length;
  if (workflow.answerAncestry.length !== expectedAnswers) throw new Error(`INPUT_INVALID answerAncestry missing or extra: expected ${expectedAnswers}`);
  if (workflow.phase === "answer" && workflow.cardStructureRef !== undefined) throw new Error("INPUT_INVALID cardStructureRef is only valid for make-cards");
  const turnIds = new Set<string>();
  const answerIds = new Set<string>();
  for (let index = 0; index < workflow.questionTurns.length; index += 1) {
    const turn = workflow.questionTurns[index]!;
    requireString(turn.turnId, `questionTurns[${index}].turnId`);
    requireCleanQuestion(turn.question, `questionTurns[${index}].question`);
    if (turn.turnIndex !== index) throw new Error(`INPUT_INVALID questionTurns order at ${index}`);
    if (turnIds.has(turn.turnId)) throw new Error(`INPUT_INVALID duplicate question turn ${turn.turnId}`);
    turnIds.add(turn.turnId);
    const previousAnswer = index > 0 ? workflow.answerAncestry[index - 1] : undefined;
    if (index === 0 && turn.parentAnswerId !== undefined) throw new Error("INPUT_INVALID first question cannot have parentAnswerId");
    if (index > 0 && turn.parentAnswerId !== previousAnswer?.answerId) throw new Error(`INPUT_INVALID question ancestry at turn ${index}`);
    const answer = workflow.answerAncestry[index];
    if (!answer) continue;
    requireString(answer.answerId, `answerAncestry[${index}].answerId`);
    requireIdentifier(answer.artifactRef, `answerAncestry[${index}].artifactRef`);
    requireString(answer.turnId, `answerAncestry[${index}].turnId`);
    requireString(answer.answerText, `answerAncestry[${index}].answerText`);
    if (answerIds.has(answer.answerId)) throw new Error(`INPUT_INVALID duplicate answer ${answer.answerId}`);
    answerIds.add(answer.answerId);
    if (answer.turnId !== turn.turnId || answer.turnIndex !== index) throw new Error(`INPUT_INVALID answer ancestry turn mismatch at ${index}`);
    if (answer.questionSha256 !== sha256(turn.question)) throw new Error(`INPUT_TAMPERED questionSha256 at turn ${index}`);
    if (answer.answerTextSha256 !== sha256(answer.answerText)) throw new Error(`INPUT_TAMPERED answerTextSha256 at turn ${index}`);
    if (index === 0 && answer.parentAnswerId !== undefined) throw new Error("INPUT_INVALID first answer cannot have parentAnswerId");
    if (index > 0 && answer.parentAnswerId !== workflow.answerAncestry[index - 1]?.answerId) throw new Error(`INPUT_INVALID answer ancestry at turn ${index}`);
  }
}

function requireCleanQuestion(value: unknown, name: string): asserts value is string {
  requireString(value, name);
  if (value.trim() === "") throw new Error(`INPUT_INVALID empty ${name}`);
  if (/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/u.test(value)) throw new Error(`INPUT_INVALID control character ${name}`);
}

function requireIdentifier(value: unknown, name: string): asserts value is string {
  requireString(value, name);
  if (value.length > 128 || !/^[A-Za-z0-9][A-Za-z0-9._-]*$/u.test(value)) throw new Error(`INPUT_INVALID ${name}`);
}

async function verifyDeclaredFile(packageRoot: string, declaration: ManifestFile): Promise<void> {
  const resolved = packagePath(packageRoot, declaration.path);
  const actual = await fileDigest(resolved);
  if (actual.sha256 !== declaration.sha256 || actual.size !== declaration.size) throw new Error(`INPUT_TAMPERED ${declaration.path}`);
}

export async function loadCategoryRegistry(registryPath: string): Promise<CategoryRegistry> {
  const registry = await readJson<CategoryRegistry>(registryPath);
  if (!(["category-registry/1.0.0", "category-registry/2.0.0"] as string[]).includes(registry.schema_version) || !Array.isArray(registry.categories) || registry.categories.length === 0) throw new Error("CATEGORY_REGISTRY_INVALID");
  const ids = new Set<string>();
  for (const category of registry.categories) {
    requireString(category.id, "category.id");
    requireString(category.description, "category.description");
    requireString(category.prompt_file, "category.prompt_file");
    if (ids.has(category.id)) throw new Error(`CATEGORY_REGISTRY_INVALID duplicate ${category.id}`);
    if (!Array.isArray(category.allowed_templates) || category.allowed_templates.length === 0) throw new Error(`CATEGORY_REGISTRY_INVALID templates ${category.id}`);
    if (category.template_prompt_files !== undefined) {
      if (!category.template_prompt_files || typeof category.template_prompt_files !== "object" || Array.isArray(category.template_prompt_files)) throw new Error(`CATEGORY_REGISTRY_INVALID template prompts ${category.id}`);
      for (const [template, promptFile] of Object.entries(category.template_prompt_files)) {
        requireString(template, `category.template_prompt_files key ${category.id}`);
        requireString(promptFile, `category.template_prompt_files.${template}`);
        if (!category.allowed_templates.includes(template)) throw new Error(`CATEGORY_REGISTRY_INVALID prompt template ${category.id}:${template}`);
      }
    }
    ids.add(category.id);
  }
  return registry;
}

function validateLanguagePracticeInput(item: StandardInput["items"][number]): void {
  const allowed = ["meaning-reading", "writing", "both"];
  const hintMode = item.hints?.languagePracticeMode;
  const overrideMode = item.human_overrides?.languagePracticeMode;
  if (hintMode !== undefined && !allowed.includes(hintMode)) throw new Error(`INPUT_INVALID languagePracticeMode ${item.item_id}`);
  if (overrideMode !== undefined && !allowed.includes(overrideMode)) throw new Error(`INPUT_INVALID human_overrides.languagePracticeMode ${item.item_id}`);
  validateLanguageWritingOverrides(item.human_overrides?.languageWriting, item.item_id);
}

function validateLanguagePracticeOperationInput(input: StandardInput): void {
  const allowed = ["meaning-reading", "writing", "both"];
  if (input.languagePracticeMode !== undefined && !allowed.includes(input.languagePracticeMode)) throw new Error("INPUT_INVALID languagePracticeMode");
  const overrideMode = input.human_overrides?.languagePracticeMode;
  if (overrideMode !== undefined && !allowed.includes(overrideMode)) throw new Error("INPUT_INVALID human_overrides.languagePracticeMode");
  validateLanguageWritingOverrides(input.human_overrides?.languageWriting, "operation");
}

function validateLanguageWritingOverrides(writing: unknown, scope: string): void {
  if (writing === undefined) return;
  if (!writing || typeof writing !== "object" || Array.isArray(writing) || !("units" in writing) || !(writing as { units?: unknown }).units || typeof (writing as { units?: unknown }).units !== "object" || Array.isArray((writing as { units?: unknown }).units)) throw new Error(`INPUT_INVALID human_overrides.languageWriting ${scope}`);
  for (const [unitId, detail] of Object.entries((writing as { units: Record<string, { pronunciation?: unknown; meaning?: unknown; confirmed?: unknown }> }).units)) {
    requireString(unitId, "human_overrides.languageWriting.units key");
    if (!detail || typeof detail !== "object" || detail.confirmed !== true) throw new Error(`INPUT_INVALID languageWriting confirmation ${scope}:${unitId}`);
    requireString(detail.pronunciation, `languageWriting.pronunciation ${scope}:${unitId}`);
    requireString(detail.meaning, `languageWriting.meaning ${scope}:${unitId}`);
  }
}
