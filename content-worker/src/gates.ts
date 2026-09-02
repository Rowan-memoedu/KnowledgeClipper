import type {
  CardCandidate,
  CardGenerationResponse,
  CategoryDefinition,
  CategoryRegistry,
  ClassifiedUnitCandidate,
  ClassificationResponse,
  CleaningResponse,
  FormatAnomaly,
  GateResult,
  OcrProofreadingResponse,
  OcrUnresolvedCandidate,
  ResolvedOcrCorrection,
  ResolvedRemoval,
  SourceSegment,
} from "./types.js";
import { nonWhitespace, occurrenceIndex, sha256 } from "./util.js";

export class ContentGateError extends Error {
  constructor(message: string, readonly gates: GateResult[]) {
    super(message);
  }
}

export interface CleaningGateOutput {
  response: CleaningResponse;
  removals: ResolvedRemoval[];
  activeSegmentIndexes: number[];
  gates: GateResult[];
}

export interface OcrProofreadingGateOutput {
  response: OcrProofreadingResponse;
  corrections: ResolvedOcrCorrection[];
  unresolved: OcrUnresolvedCandidate[];
  proofreadSegments: SourceSegment[];
  gates: GateResult[];
}

export interface OcrLexicalAuditCandidate {
  candidate_id: string;
  segment_index: number;
  start: number;
  end: number;
  exact_text: string;
}

export interface OcrLexicalAuditOutput {
  unresolved: OcrUnresolvedCandidate[];
  gates: GateResult[];
}

export function extractOcrLexicalAuditCandidates(segments: SourceSegment[]): OcrLexicalAuditCandidate[] {
  const candidates: OcrLexicalAuditCandidate[] = [];
  const wordLike = /[A-Za-z0-9]+(?:[-_][A-Za-z0-9]+)*/gu;
  for (const segment of segments) {
    for (const match of segment.text.matchAll(wordLike)) {
      const exactText = match[0];
      const start = match.index;
      if (exactText.length < 4 || exactText.length > 32 || !/[A-Za-z]/u.test(exactText)) continue;
      candidates.push({
        candidate_id: `lex-${sha256(`${segment.segment_id}\0${start}\0${exactText}`).slice(0, 16)}`,
        segment_index: segment.segment_index,
        start,
        end: start + exactText.length,
        exact_text: exactText,
      });
    }
  }
  return candidates;
}

export function validateOcrLexicalAudit(raw: unknown, candidates: OcrLexicalAuditCandidate[]): OcrLexicalAuditOutput {
  const response = asRecord(raw, "ocr lexical audit response");
  assertExactProperties(response, ["reviewed_tokens"], "ocr lexical audit response");
  const reviewed = requireArray(response.reviewed_tokens, "reviewed_tokens");
  if (reviewed.length !== candidates.length) throw new Error(`AI_CONTRACT ocr lexical audit coverage expected=${candidates.length} actual=${reviewed.length}`);
  const expected = new Map(candidates.map((candidate) => [candidate.candidate_id, candidate]));
  const seen = new Set<string>();
  const unresolved: OcrUnresolvedCandidate[] = [];
  for (const value of reviewed) {
    const entry = asRecord(value, "reviewed_tokens[]");
    assertExactProperties(entry, ["candidate_id", "segment_index", "start", "end", "exact_text", "verdict", "evidence"], "reviewed_tokens[]");
    const candidateId = requireStringValue(entry.candidate_id, "reviewed_tokens[].candidate_id");
    const candidate = expected.get(candidateId);
    if (!candidate || seen.has(candidateId)) throw new Error(`AI_CONTRACT ocr lexical audit candidate ${candidateId}`);
    seen.add(candidateId);
    const segmentIndex = requireInteger(entry.segment_index, "reviewed_tokens[].segment_index");
    const start = requireInteger(entry.start, "reviewed_tokens[].start");
    const end = requireInteger(entry.end, "reviewed_tokens[].end");
    const exactText = requireStringValue(entry.exact_text, "reviewed_tokens[].exact_text");
    if (segmentIndex !== candidate.segment_index || start !== candidate.start || end !== candidate.end || exactText !== candidate.exact_text) throw new Error(`AI_CONTRACT ocr lexical audit range ${candidateId}`);
    const verdict = enumValue(entry.verdict, ["ordinary", "contextual_identifier", "suspicious"] as const, "reviewed_tokens[].verdict");
    const evidence = requireText(entry.evidence, "reviewed_tokens[].evidence");
    if (verdict === "suspicious") unresolved.push({ segment_index: segmentIndex, start, end, exact_text: exactText, reason: "insufficient_context", evidence: `lexical_audit: ${evidence}` });
  }
  if (seen.size !== expected.size) throw new Error("AI_CONTRACT ocr lexical audit incomplete coverage");
  return {
    unresolved,
    gates: [{
      gate_id: "ocr-proofreading.lexical-audit",
      status: unresolved.length > 0 ? "notice" : "pass",
      evidence: unresolved.length > 0 ? `${candidates.length} 个词形逐项复核，${unresolved.length} 项需人工判断` : `${candidates.length} 个词形逐项复核，无漏报异常`,
    }],
  };
}

export function validateOcrProofreading(raw: unknown, segments: SourceSegment[]): OcrProofreadingGateOutput {
  const gates: GateResult[] = [];
  const candidate = asRecord(raw, "ocr proofreading response");
  const proofreadText = requireText(candidate.proofread_text, "proofread_text");
  const correctionCandidates = requireArray(candidate.corrections, "corrections");
  const unresolvedCandidates = requireArray(candidate.unresolved, "unresolved");
  const rangesBySegment = new Map<number, Array<{ start: number; end: number; insertion: boolean }>>();
  const corrections: ResolvedOcrCorrection[] = [];
  const coordinateUnresolved: OcrUnresolvedCandidate[] = [];
  let coordinateIssueCount = 0;
  let unresolvedCoordinateIssueCount = 0;

  for (const value of correctionCandidates) {
    const correction = asRecord(value, "ocr correction");
    const segmentIndex = requireInteger(correction.segment_index, "corrections[].segment_index");
    const operation = enumValue(correction.operation, ["substitution", "insertion", "deletion"] as const, "corrections[].operation");
    const start = requireInteger(correction.start, "corrections[].start");
    const end = requireInteger(correction.end, "corrections[].end");
    const before = requireStringValue(correction.before, "corrections[].before");
    const after = requireStringValue(correction.after, "corrections[].after");
    const confidence = requireConfidence(correction.confidence, "corrections[].confidence");
    const evidenceType = enumValue(correction.evidence_type, ["lexical_context", "cross_segment_consistency", "document_context"] as const, "corrections[].evidence_type");
    const evidence = requireText(correction.evidence, "corrections[].evidence");
    if (end < start) fail(gates, "ocr-proofreading.range", `反向范围 segment=${segmentIndex} [${start},${end})`);
    if (operation === "insertion" && (start !== end || before !== "" || after === "")) fail(gates, "ocr-proofreading.operation", "insertion 必须是零长度来源范围且 after 非空");
    if (operation === "deletion" && (start === end || before === "" || after !== "")) fail(gates, "ocr-proofreading.operation", "deletion 必须删除非空来源范围且 after 为空");
    if (operation === "substitution" && (start === end || before === "" || after === "" || before === after)) fail(gates, "ocr-proofreading.operation", "substitution 必须替换不同的非空文字");
    if (/\s/u.test(before) || /\s/u.test(after)) fail(gates, "ocr-proofreading.structure", "OCR 校对不得插入、删除或替换空白字符");
    const editDistance = levenshtein([...before], [...after]);
    if (editDistance < 1 || editDistance > 2) fail(gates, "ocr-proofreading.minimal-edit", `单项纠错编辑距离必须为 1..2，实际=${editDistance}`);
    if (confidence < 0.9) fail(gates, "ocr-proofreading.confidence", `置信度低于自动放行阈值：${confidence}`);
    const segment = segments[segmentIndex];
    const claimedRangeExact = segment !== undefined && end <= segment.text.length && segment.text.slice(start, end) === before;
    if (!claimedRangeExact) {
      coordinateIssueCount += 1;
      coordinateUnresolved.push(...makeCoordinateUnresolved({
        segmentIndex,
        operation,
        start,
        end,
        before,
        after,
        confidence,
        evidenceType,
        evidence,
      }, segments));
      continue;
    }
    if (isProtectedRange(segment.text, start, end)) fail(gates, "ocr-proofreading.protected-content", "OCR 校对不得修改代码或数学内容");
    const ranges = rangesBySegment.get(segmentIndex) ?? [];
    const insertion = start === end;
    if (ranges.some((range) => rangesOverlap(start, end, insertion, range.start, range.end, range.insertion))) fail(gates, "ocr-proofreading.overlap", `纠错范围重叠，segment=${segmentIndex}`);
    ranges.push({ start, end, insertion });
    rangesBySegment.set(segmentIndex, ranges);
    corrections.push({
      segment_index: segmentIndex,
      operation,
      start,
      end,
      before,
      after,
      confidence,
      evidence_type: evidenceType,
      evidence,
      source_start: segment.start + start,
      source_end: segment.start + end,
      edit_distance: editDistance,
    });
  }

  const modelUnresolved = unresolvedCandidates.flatMap((value) => {
    const entry = asRecord(value, "ocr unresolved");
    const segmentIndex = requireInteger(entry.segment_index, "unresolved[].segment_index");
    const segment = segments[segmentIndex];
    const start = requireInteger(entry.start, "unresolved[].start");
    const end = requireInteger(entry.end, "unresolved[].end");
    const exactText = requireText(entry.exact_text, "unresolved[].exact_text");
    const reason = enumValue(entry.reason, ["ambiguous_text", "insufficient_context", "protected_content", "structural_ambiguity"] as const, "unresolved[].reason");
    const evidence = requireText(entry.evidence, "unresolved[].evidence");
    const claimedRangeExact = segment !== undefined && end > start && end <= segment.text.length && segment.text.slice(start, end) === exactText;
    const normalized = claimedRangeExact
      ? [{ segment_index: segmentIndex, start, end, exact_text: exactText, reason, evidence }]
      : makeUnresolvedCoordinateCandidates({ segmentIndex, start, end, exactText, reason, evidence }, segments);
    if (!claimedRangeExact) unresolvedCoordinateIssueCount += 1;
    const correctedRanges = rangesBySegment.get(segmentIndex) ?? [];
    if (normalized.some((candidate) => correctedRanges.some((range) => rangesOverlap(candidate.start, candidate.end, false, range.start, range.end, range.insertion)))) fail(gates, "ocr-proofreading.mixed-decision", "同一来源范围不能同时自动纠错和标记 unresolved");
    return normalized;
  });
  const unresolved = deduplicateOcrUnresolved([...coordinateUnresolved, ...modelUnresolved]);

  const correctedTexts = segments.map((segment) => {
    const entries = corrections.filter((entry) => entry.segment_index === segment.segment_index).sort((left, right) => right.start - left.start || right.end - left.end);
    let text = segment.text;
    for (const entry of entries) text = `${text.slice(0, entry.start)}${entry.after}${text.slice(entry.end)}`;
    return text;
  });
  const expectedText = correctedTexts.map((value) => normalizeLineEndings(value).replace(/\n+$/u, "")).join("\n\n");
  const actualText = normalizeLineEndings(proofreadText).replace(/\n+$/u, "");
  if (coordinateIssueCount === 0 && expectedText !== actualText) fail(gates, "ocr-proofreading.diff-replay", "proofread_text 不能由已登记最小 diff 精确重放得到");
  const releasedProofreadText = coordinateIssueCount > 0 ? expectedText : proofreadText;
  const totalSourceLength = Math.max(1, segments.reduce((sum, segment) => sum + segment.text.length, 0));
  const changedCharacters = corrections.reduce((sum, entry) => sum + Math.max([...entry.before].length, [...entry.after].length), 0);
  if (changedCharacters / totalSourceLength > 0.05) fail(gates, "ocr-proofreading.edit-ratio", `纠错比例过高：${(changedCharacters / totalSourceLength).toFixed(3)}`);

  let cursor = 0;
  const proofreadSegments = correctedTexts.map((text, index) => {
    const source = segments[index]!;
    const start = cursor;
    const end = start + text.length;
    cursor = end + 2;
    return {
      ...source,
      segment_id: `pseg-${sha256(`${source.segment_id}\0${text}`).slice(0, 16)}`,
      source_kind: "ocr_text" as const,
      start,
      end,
      text,
    };
  });
  gates.push({ gate_id: "ocr-proofreading.provenance-scope", status: "pass", evidence: "仅 ocr_text 且上游 provenance 已在输入加载阶段验真时可调用该门禁" });
  gates.push({
    gate_id: "ocr-proofreading.coordinate-localization",
    status: coordinateIssueCount === 0 ? "pass" : "notice",
    evidence: coordinateIssueCount === 0
      ? "所有已采纳纠错均在模型声明范围逐字匹配"
      : `${coordinateIssueCount} 个模型候选坐标不可信；按声明 segment 内 exact before 重新定位并保留为 unresolved，未应用候选改写`,
  });
  gates.push({
    gate_id: "ocr-proofreading.unresolved-coordinate-localization",
    status: unresolvedCoordinateIssueCount === 0 ? "pass" : "notice",
    evidence: unresolvedCoordinateIssueCount === 0
      ? "所有模型 unresolved 范围均逐字匹配"
      : `${unresolvedCoordinateIssueCount} 个模型 unresolved 坐标不可信；按声明 segment 内 exact_text 重新定位并继续 fail-closed`,
  });
  gates.push({ gate_id: "ocr-proofreading.minimal-diff", status: "pass", evidence: `${corrections.length} 项纠错均可逐字定位、编辑距离不超过 2、总变化比例 ${(changedCharacters / totalSourceLength).toFixed(3)}` });
  gates.push({ gate_id: "ocr-proofreading.protected-content", status: "pass", evidence: "未修改代码、公式或空白结构" });
  gates.push({ gate_id: "ocr-proofreading.release", status: unresolved.length === 0 ? "pass" : "notice", evidence: unresolved.length === 0 ? "无歧义项" : `${unresolved.length} 项保留原文并 fail-closed` });
  return {
    response: { proofread_text: releasedProofreadText, corrections, unresolved },
    corrections,
    unresolved,
    proofreadSegments,
    gates,
  };
}

interface CoordinateRecoveryCandidate {
  segmentIndex: number;
  operation: "substitution" | "insertion" | "deletion";
  start: number;
  end: number;
  before: string;
  after: string;
  confidence: number;
  evidenceType: "lexical_context" | "cross_segment_consistency" | "document_context";
  evidence: string;
}

interface UnresolvedCoordinateCandidate {
  segmentIndex: number;
  start: number;
  end: number;
  exactText: string;
  reason: OcrUnresolvedCandidate["reason"];
  evidence: string;
}

function makeUnresolvedCoordinateCandidates(candidate: UnresolvedCoordinateCandidate, segments: SourceSegment[]): OcrUnresolvedCandidate[] {
  const declaredSegment = segments[candidate.segmentIndex];
  if (!declaredSegment) {
    return segments.map((segment) => ({
      segment_index: segment.segment_index,
      start: 0,
      end: segment.text.length,
      exact_text: segment.text,
      reason: "structural_ambiguity" as const,
      evidence: unresolvedCoordinateEvidence(candidate, "invalid_declared_segment", []),
    }));
  }
  const matches = allLiteralOccurrences(declaredSegment.text, candidate.exactText);
  if (matches.length === 1) {
    const start = matches[0]!;
    const end = start + candidate.exactText.length;
    return [{
      segment_index: candidate.segmentIndex,
      start,
      end,
      exact_text: candidate.exactText,
      reason: candidate.reason,
      evidence: unresolvedCoordinateEvidence(candidate, "unique_relocated", [{ start, end }]),
    }];
  }
  if (matches.length > 1) {
    const ranges = matches.map((start) => ({ start, end: start + candidate.exactText.length }));
    return ranges.map((range) => ({
      segment_index: candidate.segmentIndex,
      start: range.start,
      end: range.end,
      exact_text: candidate.exactText,
      reason: "ambiguous_text" as const,
      evidence: unresolvedCoordinateEvidence(candidate, "multiple_matches", ranges),
    }));
  }
  return [{
    segment_index: candidate.segmentIndex,
    start: 0,
    end: declaredSegment.text.length,
    exact_text: declaredSegment.text,
    reason: "insufficient_context" as const,
    evidence: unresolvedCoordinateEvidence(candidate, "no_match", []),
  }];
}

function unresolvedCoordinateEvidence(candidate: UnresolvedCoordinateCandidate, localization: string, matches: Array<{ start: number; end: number }>): string {
  return `model_unresolved_coordinate=${JSON.stringify({
    localization,
    declared_segment_index: candidate.segmentIndex,
    model_range: { start: candidate.start, end: candidate.end },
    normalized_ranges: matches,
    exact_text: candidate.exactText,
    reason: candidate.reason,
    model_evidence: candidate.evidence,
    disposition: "unresolved_human_review_not_applied",
  })}`;
}

function makeCoordinateUnresolved(candidate: CoordinateRecoveryCandidate, segments: SourceSegment[]): OcrUnresolvedCandidate[] {
  const declaredSegment = segments[candidate.segmentIndex];
  if (!declaredSegment) {
    return segments.map((segment) => ({
      segment_index: segment.segment_index,
      start: 0,
      end: segment.text.length,
      exact_text: segment.text,
      reason: "structural_ambiguity" as const,
      evidence: coordinateRecoveryEvidence(candidate, "invalid_declared_segment", []),
    }));
  }
  const matches = candidate.before === "" ? [] : allLiteralOccurrences(declaredSegment.text, candidate.before);
  if (matches.length === 1) {
    const start = matches[0]!;
    const end = start + candidate.before.length;
    return [{
      segment_index: candidate.segmentIndex,
      start,
      end,
      exact_text: candidate.before,
      reason: isProtectedRange(declaredSegment.text, start, end) ? "protected_content" : "structural_ambiguity",
      evidence: coordinateRecoveryEvidence(candidate, "unique_relocated", [{ start, end }]),
    }];
  }
  if (matches.length > 1) {
    const ranges = matches.map((start) => ({ start, end: start + candidate.before.length }));
    return ranges.map((range) => ({
      segment_index: candidate.segmentIndex,
      start: range.start,
      end: range.end,
      exact_text: candidate.before,
      reason: "ambiguous_text" as const,
      evidence: coordinateRecoveryEvidence(candidate, "multiple_matches", ranges),
    }));
  }
  return [{
    segment_index: candidate.segmentIndex,
    start: 0,
    end: declaredSegment.text.length,
    exact_text: declaredSegment.text,
    reason: candidate.before === "" ? "ambiguous_text" : "insufficient_context",
    evidence: coordinateRecoveryEvidence(candidate, candidate.before === "" ? "empty_before_unlocatable" : "no_match", []),
  }];
}

function coordinateRecoveryEvidence(candidate: CoordinateRecoveryCandidate, localization: string, matches: Array<{ start: number; end: number }>): string {
  return `model_coordinate_candidate=${JSON.stringify({
    localization,
    declared_segment_index: candidate.segmentIndex,
    model_range: { start: candidate.start, end: candidate.end },
    normalized_ranges: matches,
    proposed_diff: { operation: candidate.operation, before: candidate.before, after: candidate.after },
    confidence: candidate.confidence,
    evidence_type: candidate.evidenceType,
    model_evidence: candidate.evidence,
    disposition: "unresolved_human_review_not_applied",
  })}`;
}

function allLiteralOccurrences(source: string, needle: string): number[] {
  if (needle === "") return [];
  const result: number[] = [];
  let from = 0;
  while (from <= source.length - needle.length) {
    const index = source.indexOf(needle, from);
    if (index < 0) break;
    result.push(index);
    from = index + 1;
  }
  return result;
}

function deduplicateOcrUnresolved(values: OcrUnresolvedCandidate[]): OcrUnresolvedCandidate[] {
  const seen = new Set<string>();
  return values.filter((value) => {
    const key = `${value.segment_index}\0${value.start}\0${value.end}\0${value.exact_text}\0${value.reason}\0${value.evidence}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

export function validateCleaning(raw: unknown, segments: SourceSegment[]): CleaningGateOutput {
  const gates: GateResult[] = [];
  const candidate = asRecord(raw, "cleaning response");
  const cleanedText = requireText(candidate.cleaned_text, "cleaned_text");
  const removedCandidates = requireArray(candidate.removed_fragments, "removed_fragments");
  const anomalyCandidates = requireArray(candidate.format_anomalies, "format_anomalies");
  const removals: ResolvedRemoval[] = [];
  const rangesBySegment = new Map<number, Array<{ start: number; end: number }>>();
  const allSource = segments.map((segment) => segment.text).join("\n\n");
  const allowedReasons = new Set(["navigation", "control", "advertisement", "sharing_prompt", "site_footer", "duplicate"]);
  const protectedPattern = /```|`[^`\r\n]+`|\$\$|\$[^$\r\n]+\$|\\\[|\\\]|\\\(|\\\)|\\begin\{/u;
  let removedLength = 0;

  for (const value of removedCandidates) {
    const removal = asRecord(value, "removed fragment");
    const segmentIndex = requireInteger(removal.segment_index, "removed_fragments[].segment_index");
    const segment = segments[segmentIndex];
    if (!segment) fail(gates, "cleaning.removal.segment", `不存在的 segment_index=${segmentIndex}`);
    const exactText = requireText(removal.exact_text, "removed_fragments[].exact_text");
    const reason = requireText(removal.reason, "removed_fragments[].reason");
    const evidence = requireText(removal.evidence, "removed_fragments[].evidence");
    if (!allowedReasons.has(reason)) fail(gates, "cleaning.removal.reason", `不支持的噪声理由 ${reason}`);
    if (protectedPattern.test(exactText)) fail(gates, "cleaning.protected-content", `删除片段包含代码或数学定界符，segment=${segmentIndex}`);
    const occurrence = removal.occurrence === undefined ? 0 : requireInteger(removal.occurrence, "removed_fragments[].occurrence");
    const localStart = occurrenceIndex(segment.text, exactText, occurrence);
    if (localStart < 0) fail(gates, "cleaning.removal.exact", `删除片段无法逐字定位，segment=${segmentIndex}`);
    const localEnd = localStart + exactText.length;
    const ranges = rangesBySegment.get(segmentIndex) ?? [];
    if (ranges.some((range) => localStart < range.end && localEnd > range.start)) fail(gates, "cleaning.removal.overlap", `删除片段重叠，segment=${segmentIndex}`);
    ranges.push({ start: localStart, end: localEnd });
    rangesBySegment.set(segmentIndex, ranges);
    if (reason === "duplicate") {
      if (!isWholeSegmentRemoval(segment.text, localStart, localEnd)) fail(gates, "cleaning.duplicate-whole-segment", "duplicate 只能删除完整重复分段，不能删除词语或句内子串");
      const signature = segmentBody(segment.text);
      if (segments.filter((candidate) => segmentBody(candidate.text) === signature).length < 2) {
        fail(gates, "cleaning.duplicate", "标记为 duplicate 的完整分段没有另一个逐字相同的完整分段");
      }
    }
    if (reason !== "duplicate" && !looksLikeNoise(exactText, reason, segment.text, localStart, localEnd)) fail(gates, "cleaning.noise-evidence", `删除片段缺少可核验的独立噪声形态：${reason}`);
    removals.push({
      segment_index: segmentIndex,
      exact_text: exactText,
      occurrence,
      reason: reason as ResolvedRemoval["reason"],
      evidence,
      source_start: segment.start + localStart,
      source_end: segment.start + localEnd,
    });
    removedLength += exactText.length;
  }

  const expectedSegments = segments.map((segment) => {
    const ranges = [...(rangesBySegment.get(segment.segment_index) ?? [])].sort((left, right) => right.start - left.start);
    let value = segment.text;
    for (const range of ranges) value = `${value.slice(0, range.start)}${value.slice(range.end)}`;
    return value;
  });
  for (const removal of removals.filter((entry) => entry.reason === "duplicate")) {
    const signature = segmentBody(segments[removal.segment_index]?.text ?? "");
    if (!expectedSegments.some((candidate) => candidate.trim() !== "" && segmentBody(candidate) === signature)) {
      fail(gates, "cleaning.duplicate-preserve-one", "duplicate 删除必须至少保留一个逐字相同的完整分段副本");
    }
  }
  if (nonWhitespace(expectedSegments.join("\n\n")) !== nonWhitespace(cleanedText)) fail(gates, "cleaning.delete-only", "cleaned_text 含删除以外的非空白字符变化");
  const expectedStructuredText = canonicalStructuredText(expectedSegments);
  const actualStructuredText = normalizeLineEndings(cleanedText).replace(/\n+$/u, "");
  if (expectedStructuredText !== actualStructuredText) fail(gates, "cleaning.structure-fidelity", "cleaned_text 改变了保留内容的段落、换行、缩进或内部空白结构");
  const sourceLength = Math.max(1, allSource.length);
  if (removedLength / sourceLength > 0.35) fail(gates, "cleaning.removal-ratio", `删除比例过高：${(removedLength / sourceLength).toFixed(3)}`);
  if (cleanedText.trim() === "") fail(gates, "cleaning.nonempty", "清洗结果为空");

  const anomalies: FormatAnomaly[] = anomalyCandidates.map((value) => {
    const anomaly = asRecord(value, "format anomaly");
    const segmentIndex = requireInteger(anomaly.segment_index, "format_anomalies[].segment_index");
    if (!segments[segmentIndex]) fail(gates, "cleaning.anomaly.segment", `异常引用不存在的 segment=${segmentIndex}`);
    const kind = requireText(anomaly.kind, "format_anomalies[].kind") as FormatAnomaly["kind"];
    const severity = requireText(anomaly.severity, "format_anomalies[].severity") as FormatAnomaly["severity"];
    const evidence = requireText(anomaly.evidence, "format_anomalies[].evidence");
    if (!["suspected_ocr", "broken_table", "broken_formula", "ambiguous_layout", "other"].includes(kind)) fail(gates, "cleaning.anomaly.kind", `未知异常 ${kind}`);
    if (!["notice", "requires_human"].includes(severity)) fail(gates, "cleaning.anomaly.severity", `未知严重度 ${severity}`);
    return { segment_index: segmentIndex, kind, severity, evidence };
  });

  gates.push({ gate_id: "cleaning.delete-only", status: "pass", evidence: `${removals.length} 个删除操作均可逐字定位；非空白字符序列一致` });
  gates.push({ gate_id: "cleaning.structure-fidelity", status: "pass", evidence: "保留内容的段落、换行、缩进与内部空白结构一致" });
  gates.push({ gate_id: "cleaning.removal-ratio", status: "pass", evidence: `删除字符占比 ${(removedLength / sourceLength).toFixed(3)}` });
  if (anomalies.some((anomaly) => anomaly.severity === "requires_human")) gates.push({ gate_id: "cleaning.format-anomaly", status: "notice", evidence: "存在 requires_human 格式异常" });
  else gates.push({ gate_id: "cleaning.format-anomaly", status: "pass", evidence: `${anomalies.length} 个非阻断异常` });
  return {
    response: { cleaned_text: cleanedText, removed_fragments: removals, format_anomalies: anomalies },
    removals,
    activeSegmentIndexes: expectedSegments.map((value, index) => (value.trim() === "" ? -1 : index)).filter((index) => index >= 0),
    gates,
  };
}

function canonicalStructuredText(segments: string[]): string {
  return segments
    .map((value) => normalizeLineEndings(value).replace(/\n+$/u, ""))
    .filter((value) => value.trim() !== "")
    .join("\n\n");
}

function normalizeLineEndings(value: string): string {
  return value.replace(/\r\n?/gu, "\n");
}

function isWholeSegmentRemoval(segmentText: string, start: number, end: number): boolean {
  const contentEnd = segmentText.search(/[\r\n]+$/u);
  const effectiveEnd = contentEnd < 0 ? segmentText.length : contentEnd;
  return start === 0 && end >= effectiveEnd;
}

function segmentBody(value: string): string {
  return value.replace(/[\r\n]+$/u, "");
}

export interface ClassificationGateOutput {
  units: ClassifiedUnitCandidate[];
  gates: GateResult[];
}

export function validateClassification(raw: unknown, segments: SourceSegment[], activeIndexes: number[], registry: CategoryRegistry): ClassificationGateOutput {
  const gates: GateResult[] = [];
  const response = asRecord(raw, "classification response");
  const values = requireArray(response.units, "units");
  if (values.length === 0) fail(gates, "classification.nonempty", "没有学习单元");
  const categoryIds = new Set(registry.categories.map((category) => category.id));
  const unitIds = new Set<string>();
  const covered = new Set<number>();
  const units: ClassifiedUnitCandidate[] = values.map((value) => {
    const unit = asRecord(value, "classification unit");
    const unitId = requireText(unit.unit_id, "unit_id");
    if (unitIds.has(unitId)) fail(gates, "classification.unique-unit", `重复 unit_id=${unitId}`);
    unitIds.add(unitId);
    const sourceSegmentIndexes = requireArray(unit.source_segment_indexes, "source_segment_indexes").map((index) => requireInteger(index, "source_segment_indexes[]"));
    if (sourceSegmentIndexes.length === 0) fail(gates, "classification.source", `unit=${unitId} 没有来源分段`);
    if (new Set(sourceSegmentIndexes).size !== sourceSegmentIndexes.length) fail(gates, "classification.unique-source", `unit=${unitId} 重复引用同一来源分段`);
    for (const index of sourceSegmentIndexes) {
      if (!segments[index] || !activeIndexes.includes(index)) fail(gates, "classification.source", `unit=${unitId} 引用无效或已删除分段 ${index}`);
      covered.add(index);
    }
    const categoryId = requireText(unit.category_id, "category_id");
    if (!categoryIds.has(categoryId)) fail(gates, "classification.registered-category", `未注册类别 ${categoryId}`);
    const subtype = requireText(unit.subtype, "subtype");
    const answerState = enumValue(unit.answer_state, ["provided", "missing", "ambiguous", "not_applicable"], "answer_state");
    const contextState = enumValue(unit.context_state, ["provided", "missing", "ambiguous", "not_applicable"], "context_state");
    const riskState = enumValue(unit.risk_state, ["ordinary", "verification_required", "high_stakes"], "risk_state");
    const assignedSource = sourceSegmentIndexes.map((index) => segments[index]?.text ?? "").join("\n\n");
    const question = optionalText(unit.question, "question");
    const suppliedAnswer = optionalText(unit.supplied_answer, "supplied_answer");
    const targetTerm = optionalText(unit.target_term, "target_term");
    if ((categoryId === "language_zh" || categoryId === "language_en") && answerState !== "not_applicable") fail(gates, "classification.language-answer-state", `语言学习单元 ${unitId} 的 answer_state 必须为 not_applicable`);
    if (question && !assignedSource.includes(question)) fail(gates, "classification.source-question", `unit=${unitId} 的 question 不是来源原文`);
    if (answerState === "provided" && (!suppliedAnswer || !assignedSource.includes(suppliedAnswer))) fail(gates, "classification.source-answer", `unit=${unitId} 的 provided answer 不是来源原文`);
    if (answerState === "missing" && suppliedAnswer) fail(gates, "classification.answer-state", `unit=${unitId} missing 状态却包含 supplied_answer`);
    if (targetTerm && !assignedSource.includes(targetTerm)) fail(gates, "classification.source-target", `unit=${unitId} 的 target_term 不是来源原文`);
    const unresolvedReasons = requireArray(unit.unresolved_reasons, "unresolved_reasons").map((reason) => requireText(reason, "unresolved_reasons[]"));
    return {
      unit_id: unitId,
      source_segment_indexes: sourceSegmentIndexes,
      category_id: categoryId,
      subtype,
      answer_state: answerState,
      context_state: contextState,
      risk_state: riskState,
      ...(question ? { question } : {}),
      ...(suppliedAnswer ? { supplied_answer: suppliedAnswer } : {}),
      ...(targetTerm ? { target_term: targetTerm } : {}),
      unresolved_reasons: unresolvedReasons,
    };
  });
  const missingCoverage = activeIndexes.filter((index) => !covered.has(index));
  if (missingCoverage.length > 0) fail(gates, "classification.coverage", `未覆盖分段：${missingCoverage.join(",")}`);
  gates.push({ gate_id: "classification.registered-category", status: "pass", evidence: `${units.length} 个单元均使用开放注册表类别` });
  gates.push({ gate_id: "classification.coverage", status: "pass", evidence: `${activeIndexes.length} 个有效分段全部覆盖` });
  return { units, gates };
}

export interface CardGateOutput {
  response: CardGenerationResponse;
  cards: CardCandidate[];
  gates: GateResult[];
}

export function validateCards(raw: unknown, unit: ClassifiedUnitCandidate, category: CategoryDefinition, assignedSource: string, allowedAssets: Set<string>, options: { allowHumanConfirmed?: boolean } = {}): CardGateOutput {
  const gates: GateResult[] = [];
  const response = asRecord(raw, "card response");
  const generatedContent = requireArray(response.generated_content, "generated_content").map((value) => {
    const entry = asRecord(value, "generated content");
    return { kind: requireText(entry.kind, "generated_content[].kind"), text: requireText(entry.text, "generated_content[].text") };
  });
  const unresolvedReasons = requireArray(response.unresolved_reasons, "unresolved_reasons").map((value) => requireText(value, "unresolved_reasons[]"));
  const cardValues = requireArray(response.cards, "cards");
  if (unit.answer_state === "ambiguous" && cardValues.length > 0) fail(gates, "card.ambiguous", "答案状态 ambiguous 时不得放行卡片");
  if (unit.answer_state === "missing" && unit.risk_state !== "ordinary" && cardValues.length > 0) fail(gates, "card.research-boundary", `${unit.risk_state} 缺失答案不得自动制卡`);
  const generatedTexts = new Set(generatedContent.map((entry) => entry.text));
  const cards = cardValues.map((value) => {
    const card = asRecord(value, "card");
    const template = requireText(card.template, "card.template");
    if (!category.allowed_templates.includes(template)) fail(gates, "card.template", `类别 ${category.id} 不允许模板 ${template}`);
    const answerProvenance = enumValue(card.answer_provenance, ["source", "generated", "human_confirmed", "not_applicable"], "answer_provenance");
    if (answerProvenance === "human_confirmed" && !options.allowHumanConfirmed) fail(gates, "card.human-confirmed", "当前输入契约没有经过验证的人工确认卡片正文，模型不得声明 human_confirmed answer provenance");
    if (unit.answer_state === "provided" && !["source", "human_confirmed"].includes(answerProvenance)) fail(gates, "card.answer-provenance", "来源已有答案却使用 generated provenance");
    if (unit.answer_state === "missing" && unit.category_id === "question_answer" && answerProvenance !== "generated") fail(gates, "card.answer-provenance", "缺失答案的问答卡必须标记 generated");
    if ((unit.category_id === "language_zh" || unit.category_id === "language_en") && answerProvenance !== "not_applicable") fail(gates, "card.answer-provenance", "语言学习卡的 answer_provenance 必须为 not_applicable");
    const frontBlocks = validateBlocks(card.front_blocks, "front", assignedSource, generatedTexts, allowedAssets, gates, options);
    const backBlocks = validateBlocks(card.back_blocks, "back", assignedSource, generatedTexts, allowedAssets, gates, options);
    if (frontBlocks.length === 0 || backBlocks.length === 0) fail(gates, "card.nonempty", "卡片正反面均不能为空");
    if (JSON.stringify(frontBlocks) === JSON.stringify(backBlocks)) fail(gates, "card.same-sides", "卡片正反面完全相同，不能形成有效回忆线索");
    if (answerProvenance === "source" && !backBlocks.some((block) => block.provenance === "source")) fail(gates, "card.answer-block-provenance", "answer_provenance=source 时背面必须包含 source block");
    if (answerProvenance === "generated" && !backBlocks.some((block) => block.provenance === "generated")) fail(gates, "card.answer-block-provenance", "answer_provenance=generated 时背面必须包含 generated block");
    return { template, front_blocks: frontBlocks, back_blocks: backBlocks, answer_provenance: answerProvenance };
  });
  const cardSignatures = new Set<string>();
  const usedGeneratedTexts = new Set<string>();
  for (const card of cards) {
    const signature = JSON.stringify(card);
    if (cardSignatures.has(signature)) fail(gates, "card.duplicate", "同一学习单元返回了完全重复的卡片");
    cardSignatures.add(signature);
    for (const block of [...card.front_blocks, ...card.back_blocks]) {
      if (block.type !== "asset_ref" && block.provenance === "generated") usedGeneratedTexts.add(block.text);
    }
  }
  const unusedGenerated = generatedContent.filter((entry) => !usedGeneratedTexts.has(entry.text));
  if (unusedGenerated.length > 0) fail(gates, "card.unused-generated-content", "generated_content 含未被任何 generated block 完整引用的文字");
  if (cards.length > 0 && unresolvedReasons.length > 0) fail(gates, "card.mixed-state", "同一学习单元不能同时放行卡片并保留 unresolved_reasons");
  if (cards.length === 0 && unresolvedReasons.length === 0) fail(gates, "card.explicit-state", "没有卡片时必须给出 unresolved_reasons");
  gates.push({ gate_id: "card.schema", status: "pass", evidence: `${cards.length} 张卡片结构有效` });
  gates.push({ gate_id: "card.provenance", status: "pass", evidence: "来源块、生成块与素材引用均通过来源门禁" });
  return { response: { generated_content: generatedContent, cards, unresolved_reasons: unresolvedReasons }, cards, gates };
}

function validateBlocks(raw: unknown, side: string, assignedSource: string, generatedTexts: Set<string>, allowedAssets: Set<string>, gates: GateResult[], options: { allowHumanConfirmed?: boolean }) {
  return requireArray(raw, `${side}_blocks`).map((value) => {
    const block = asRecord(value, `${side} block`);
    const type = enumValue(block.type, ["markdown", "text", "asset_ref"], "block.type");
    const provenance = enumValue(block.provenance, ["source", "generated", "human_confirmed"], "block.provenance");
    if (provenance === "human_confirmed" && !options.allowHumanConfirmed) fail(gates, "card.human-confirmed", "当前输入契约没有经过验证的人工确认卡片正文，模型不得声明 human_confirmed block provenance");
    if (type === "asset_ref") {
      const materialPath = requireText(block.material_path, "block.material_path");
      if (!allowedAssets.has(materialPath)) fail(gates, "card.asset", `未声明素材引用 ${materialPath}`);
      if (provenance !== "source") fail(gates, "card.asset-provenance", "asset_ref 必须引用已声明来源素材，不得标记为 generated");
      return { type, material_path: materialPath, provenance };
    }
    const text = requireText(block.text, "block.text");
    if (provenance === "source" && !assignedSource.includes(text)) {
      const comparable = stripAddedEmphasis(text);
      if (comparable === undefined || comparable === text || !assignedSource.includes(comparable)) {
        fail(gates, "card.source-block", `来源 block 不可在分配原文中定位：${(comparable ?? text).slice(0, 60)}`);
      }
    }
    if (provenance === "generated" && !generatedTexts.has(text)) fail(gates, "card.generated-block", "生成 block 未作为完整且完全相等的 generated_content 条目登记");
    return { type, text, provenance };
  });
}

function looksLikeNoise(text: string, reason: string, source: string, start: number, end: number): boolean {
  const lineBoundedStart = start === 0 || source[start - 1] === "\n" || source[start - 1] === "\r";
  const lineBoundedEnd = end === source.length || text.endsWith("\n") || text.endsWith("\r") || source[end] === "\n" || source[end] === "\r";
  if (!lineBoundedStart || !lineBoundedEnd) return false;
  const lines = normalizeLineEndings(text).split("\n").map((line) => line.trim()).filter((line) => line !== "");
  if (lines.length === 0 || lines.length > 8) return false;
  const navigationLine = /^(?:首页|目录|导航|上一页|下一页|home|menu|navigation)(?:[\s|·•/、,，]+(?:首页|目录|导航|上一页|下一页|home|menu|navigation))*$/iu;
  const controlLine = /^(?:登录|注册|展开|收起|复制|按钮|点击登录|点击展开|login|sign\s*in|button|copy)(?:[\s|·•/、,，]+(?:登录|注册|展开|收起|复制|按钮|点击登录|点击展开|login|sign\s*in|button|copy))*$/iu;
  const sharingLine = /^(?:分享|收藏|点赞|转发|复制|下载|share|like|favorite|copy|download)(?:[\s|·•/、,，]+(?:分享|收藏|点赞|转发|复制|下载|share|like|favorite|copy|download))*$/iu;
  const footerLine = /^(?:copyright(?:\s+©)?\s+[a-z0-9][a-z0-9 .&_-]{0,60}|©\s*[a-z0-9][a-z0-9 .&_-]{0,60}|(?:[\p{L}\p{N} .&_-]{1,60}\s+)?版权所有[。.]*|.*(?:ICP备案|备案号).*|all rights reserved)$/iu;
  const advertisementLabel = /^(?:广告|推广|赞助|advertisement|advert|sponsored)$/iu;
  const advertisementCta = /(?:广告|推广|赞助|advertisement|advert|sponsored).*(?:立即购买|立即下单|点击购买|扫码|优惠|折扣|领券|buy\s*now|shop\s*now|learn\s*more)/iu;
  if (reason === "navigation") return lines.every((line) => navigationLine.test(line));
  if (reason === "control") return lines.every((line) => controlLine.test(line) || sharingLine.test(line));
  if (reason === "sharing_prompt") return lines.every((line) => sharingLine.test(line));
  if (reason === "site_footer") return lines.some((line) => footerLine.test(line)) && lines.every((line) => footerLine.test(line) || sharingLine.test(line));
  if (reason === "advertisement") return lines.every((line) => line.length <= 160 && (advertisementLabel.test(line) || advertisementCta.test(line)));
  return false;
}

function stripAddedEmphasis(value: string): string | undefined {
  let result = "";
  let cursor = 0;
  let changed = false;
  while (cursor < value.length) {
    const marker = value.startsWith("**", cursor) ? "**" : value.startsWith("__", cursor) ? "__" : undefined;
    if (!marker) {
      result += value[cursor];
      cursor += 1;
      continue;
    }
    const close = value.indexOf(marker, cursor + marker.length);
    if (close < 0) return undefined;
    const inner = value.slice(cursor + marker.length, close);
    if (inner.trim() === "" || inner.includes("**") || inner.includes("__")) return undefined;
    result += inner;
    cursor = close + marker.length;
    changed = true;
  }
  return changed ? result : value;
}

function asRecord(value: unknown, name: string): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error(`AI_CONTRACT ${name} must be an object`);
  return value as Record<string, unknown>;
}

function requireArray(value: unknown, name: string): unknown[] {
  if (!Array.isArray(value)) throw new Error(`AI_CONTRACT ${name} must be an array`);
  return value;
}

function requireText(value: unknown, name: string): string {
  if (typeof value !== "string" || value.trim() === "") throw new Error(`AI_CONTRACT ${name} must be non-empty text`);
  return value;
}

function requireStringValue(value: unknown, name: string): string {
  if (typeof value !== "string") throw new Error(`AI_CONTRACT ${name} must be text`);
  return value;
}

function requireConfidence(value: unknown, name: string): number {
  if (typeof value !== "number" || !Number.isFinite(value) || value < 0 || value > 1) throw new Error(`AI_CONTRACT ${name} must be between 0 and 1`);
  return value;
}

function optionalText(value: unknown, name: string): string | undefined {
  if (value === undefined || value === null || value === "") return undefined;
  return requireText(value, name);
}

function requireInteger(value: unknown, name: string): number {
  if (!Number.isInteger(value) || (value as number) < 0) throw new Error(`AI_CONTRACT ${name} must be a non-negative integer`);
  return value as number;
}

function enumValue<const T extends readonly string[]>(value: unknown, values: T, name: string): T[number] {
  if (typeof value !== "string" || !values.includes(value)) throw new Error(`AI_CONTRACT ${name} must be one of ${values.join(",")}`);
  return value as T[number];
}

function assertExactProperties(value: Record<string, unknown>, expected: string[], name: string): void {
  const actual = Object.keys(value).sort();
  const allowed = [...expected].sort();
  if (actual.length !== allowed.length || actual.some((key, index) => key !== allowed[index])) throw new Error(`AI_CONTRACT ${name} properties`);
}

function fail(gates: GateResult[], gateId: string, evidence: string): never {
  gates.push({ gate_id: gateId, status: "fail", evidence });
  throw new ContentGateError(`FAILED_CONTENT_GATE ${gateId}: ${evidence}`, gates);
}

function rangesOverlap(leftStart: number, leftEnd: number, leftInsertion: boolean, rightStart: number, rightEnd: number, rightInsertion: boolean): boolean {
  if (leftInsertion && rightInsertion) return leftStart === rightStart;
  if (leftInsertion) return leftStart >= rightStart && leftStart <= rightEnd;
  if (rightInsertion) return rightStart >= leftStart && rightStart <= leftEnd;
  return leftStart < rightEnd && leftEnd > rightStart;
}

function levenshtein(left: string[], right: string[]): number {
  const previous = Array.from({ length: right.length + 1 }, (_, index) => index);
  for (let leftIndex = 1; leftIndex <= left.length; leftIndex += 1) {
    const current = [leftIndex];
    for (let rightIndex = 1; rightIndex <= right.length; rightIndex += 1) {
      current[rightIndex] = Math.min(
        current[rightIndex - 1]! + 1,
        previous[rightIndex]! + 1,
        previous[rightIndex - 1]! + (left[leftIndex - 1] === right[rightIndex - 1] ? 0 : 1),
      );
    }
    for (let index = 0; index < current.length; index += 1) previous[index] = current[index]!;
  }
  return previous[right.length]!;
}

function isProtectedRange(text: string, start: number, end: number): boolean {
  const ranges: Array<{ start: number; end: number }> = [];
  const patterns = [
    /```[\s\S]*?```/gu,
    /`[^`\r\n]+`/gu,
    /\$\$[\s\S]*?\$\$/gu,
    /\$[^$\r\n]+\$/gu,
    /\\\[[\s\S]*?\\\]/gu,
    /\\\([^\r\n]*?\\\)/gu,
    /\\begin\{[^}]+\}[\s\S]*?\\end\{[^}]+\}/gu,
  ];
  for (const pattern of patterns) {
    for (const match of text.matchAll(pattern)) ranges.push({ start: match.index, end: match.index + match[0].length });
  }
  if (start === end) return ranges.some((range) => start > range.start && start < range.end);
  return ranges.some((range) => start < range.end && end > range.start);
}
