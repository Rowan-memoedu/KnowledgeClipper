import { createHash } from "node:crypto";
import { readFile, stat } from "node:fs/promises";
import path from "node:path";
import type { SourceSegment } from "./types.js";

export function sha256(value: string | Buffer): string {
  return createHash("sha256").update(value).digest("hex");
}

export function stableStringify(value: unknown, space = 0): string {
  const normalize = (current: unknown): unknown => {
    if (Array.isArray(current)) return current.map(normalize);
    if (current && typeof current === "object") {
      return Object.fromEntries(
        Object.entries(current as Record<string, unknown>)
          .sort(([left], [right]) => left.localeCompare(right))
          .map(([key, item]) => [key, normalize(item)]),
      );
    }
    return current;
  };
  return JSON.stringify(normalize(value), null, space);
}

export async function readJson<T>(filePath: string): Promise<T> {
  const raw = await readFile(filePath, "utf8");
  try {
    return JSON.parse(raw) as T;
  } catch (error) {
    throw new Error(`JSON_INVALID ${filePath}: ${(error as Error).message}`);
  }
}

export function requireString(value: unknown, name: string): asserts value is string {
  if (typeof value !== "string" || value.trim() === "") throw new Error(`INPUT_INVALID ${name}`);
}

export function packagePath(packageRoot: string, relativePath: string): string {
  requireString(relativePath, "relative path");
  if (path.isAbsolute(relativePath)) throw new Error(`INPUT_INVALID absolute path: ${relativePath}`);
  const resolvedRoot = path.resolve(packageRoot);
  const resolved = path.resolve(resolvedRoot, relativePath);
  const prefix = `${resolvedRoot}${path.sep}`;
  if (resolved !== resolvedRoot && !resolved.startsWith(prefix)) throw new Error(`INPUT_INVALID path traversal: ${relativePath}`);
  return resolved;
}

export async function fileDigest(filePath: string): Promise<{ sha256: string; size: number }> {
  const data = await readFile(filePath);
  const details = await stat(filePath);
  return { sha256: sha256(data), size: details.size };
}

export function segmentText(text: string, sourcePath: string, sourceKind: SourceSegment["source_kind"], turn?: { id: string; role: "user" | "assistant" }): SourceSegment[] {
  const boundaries: Array<{ start: number; end: number }> = [];
  const separator = /\r?\n[ \t]*\r?\n/g;
  let start = 0;
  for (const match of text.matchAll(separator)) {
    const index = match.index;
    if (index > start && text.slice(start, index).trim() !== "") boundaries.push({ start, end: index });
    start = index + match[0].length;
  }
  if (start < text.length && text.slice(start).trim() !== "") boundaries.push({ start, end: text.length });
  if (boundaries.length === 0 && text.trim() !== "") boundaries.push({ start: 0, end: text.length });
  return boundaries.map((boundary, index) => {
    const segmentTextValue = text.slice(boundary.start, boundary.end);
    const identity = `${sourcePath}\0${turn?.id ?? ""}\0${boundary.start}\0${boundary.end}\0${segmentTextValue}`;
    const result: SourceSegment = {
      segment_index: index,
      segment_id: `seg-${sha256(identity).slice(0, 16)}`,
      source_kind: sourceKind,
      source_path: sourcePath,
      start: boundary.start,
      end: boundary.end,
      text: segmentTextValue,
    };
    if (turn) {
      result.turn_id = turn.id;
      result.role = turn.role;
    }
    return result;
  });
}

export function nonWhitespace(value: string): string {
  return value.replace(/\s+/gu, "");
}

export function occurrenceIndex(haystack: string, needle: string, occurrence: number): number {
  if (needle === "" || occurrence < 0 || !Number.isInteger(occurrence)) return -1;
  let from = 0;
  let found = -1;
  for (let index = 0; index <= occurrence; index += 1) {
    found = haystack.indexOf(needle, from);
    if (found < 0) return -1;
    from = found + needle.length;
  }
  return found;
}
