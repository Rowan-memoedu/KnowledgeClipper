import type { JsonObject } from "./types.js";
import { sha256, stableStringify } from "./util.js";

export interface OcrHumanResolutionCommand {
  schemaVersion: "knowledge-clipper-ocr-human-resolution-command/1.0.0";
  decisionKind: "ocr-human-resolution";
  targetToken: string;
  action: "accept_original" | "user_correction";
  reason: string;
  correction?: {
    correctedText: string;
    correctedTextSha256: string;
    diffs: Array<{
      segmentIndex: number;
      operation: "substitution" | "insertion" | "deletion";
      start: number;
      end: number;
      before: string;
      after: string;
    }>;
  };
}

export function validateOcrHumanResolutionCommand(raw: unknown): OcrHumanResolutionCommand {
  const command = record(raw, "command") as JsonObject;
  exactKeys(command, ["schemaVersion", "decisionKind", "targetToken", "action", "reason", ...(command.correction === undefined ? [] : ["correction"])], "command");
  if (command.schemaVersion !== "knowledge-clipper-ocr-human-resolution-command/1.0.0" || command.decisionKind !== "ocr-human-resolution") throw new Error("OCR_RESOLUTION_COMMAND_INVALID identity");
  const targetToken = text(command.targetToken, "targetToken");
  if (!/^[a-f0-9]{64}$/u.test(targetToken)) throw new Error("OCR_RESOLUTION_COMMAND_INVALID targetToken");
  const reason = text(command.reason, "reason");
  if (reason.trim() === "") throw new Error("OCR_RESOLUTION_COMMAND_INVALID reason");
  if (command.action !== "accept_original" && command.action !== "user_correction") throw new Error("OCR_RESOLUTION_COMMAND_INVALID action");
  if (command.action === "accept_original") {
    if (command.correction !== undefined) throw new Error("OCR_RESOLUTION_COMMAND_INVALID accept_original correction");
    return { schemaVersion: command.schemaVersion, decisionKind: command.decisionKind, targetToken, action: command.action, reason };
  }
  const correction = record(command.correction, "correction");
  exactKeys(correction, ["correctedText", "correctedTextSha256", "diffs"], "correction");
  const correctedText = text(correction.correctedText, "correctedText", true);
  const correctedTextSha256 = text(correction.correctedTextSha256, "correctedTextSha256");
  if (sha256(correctedText) !== correctedTextSha256) throw new Error("OCR_RESOLUTION_COMMAND_INVALID correctedTextSha256");
  if (!Array.isArray(correction.diffs) || correction.diffs.length === 0) throw new Error("OCR_RESOLUTION_COMMAND_INVALID diffs");
  const diffs = correction.diffs.map((rawDiff) => {
    const diff = record(rawDiff, "diff");
    exactKeys(diff, ["segmentIndex", "operation", "start", "end", "before", "after"], "diff");
    const segmentIndex = integer(diff.segmentIndex, "segmentIndex");
    const start = integer(diff.start, "start");
    const end = integer(diff.end, "end");
    const before = text(diff.before, "before", true);
    const after = text(diff.after, "after", true);
    const operation = diffOperation(diff.operation);
    return { segmentIndex, operation, start, end, before, after };
  });
  return {
    schemaVersion: command.schemaVersion,
    decisionKind: command.decisionKind,
    targetToken,
    action: command.action,
    reason,
    correction: { correctedText, correctedTextSha256, diffs },
  };
}

function diffOperation(value: unknown): "substitution" | "insertion" | "deletion" {
  if (value === "substitution" || value === "insertion" || value === "deletion") return value;
  throw new Error("OCR_RESOLUTION_COMMAND_INVALID diff.operation");
}

function record(value: unknown, where: string): JsonObject {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error(`OCR_RESOLUTION_COMMAND_INVALID ${where}`);
  return value as JsonObject;
}

function exactKeys(value: JsonObject, allowed: string[], where: string): void {
  if (stableStringify(Object.keys(value).sort()) !== stableStringify([...allowed].sort())) throw new Error(`OCR_RESOLUTION_COMMAND_INVALID ${where}.properties`);
}

function text(value: unknown, where: string, allowEmpty = false): string {
  if (typeof value !== "string" || (!allowEmpty && value === "")) throw new Error(`OCR_RESOLUTION_COMMAND_INVALID ${where}`);
  return value;
}

function integer(value: unknown, where: string): number {
  if (!Number.isInteger(value) || (value as number) < 0) throw new Error(`OCR_RESOLUTION_COMMAND_INVALID ${where}`);
  return value as number;
}
