import { readFile } from "node:fs/promises";
import path from "node:path";
import type { CategoryDefinition } from "./types.js";
import { packagePath, sha256 } from "./util.js";

export interface LoadedPrompt {
  system: string;
  files: Array<{ path: string; sha256: string }>;
  combinedSha256: string;
}

export async function loadPrompt(promptRoot: string, stage: "ocr_proofreading" | "ocr_lexical_audit" | "cleaning" | "classification" | "card_generation" | "language_writing" | "question_answer", category?: CategoryDefinition): Promise<LoadedPrompt> {
  const relativeFiles = ["shared/system.md"];
  if (stage === "ocr_proofreading") relativeFiles.push("stages/ocr-proofreading.md");
  if (stage === "ocr_lexical_audit") relativeFiles.push("stages/ocr-lexical-audit.md");
  if (stage === "cleaning") relativeFiles.push("stages/cleaning.md");
  if (stage === "classification") relativeFiles.push("stages/classification.md");
  if (stage === "card_generation") {
    if (!category) throw new Error("PROMPT_INVALID missing category");
    relativeFiles.push("shared/card-output-contract.md");
    relativeFiles.push(category.prompt_file);
  }
  if (stage === "language_writing") {
    if (!category) throw new Error("PROMPT_INVALID missing category");
    const writingPrompt = category.template_prompt_files?.language_writing;
    if (!writingPrompt) throw new Error(`PROMPT_INVALID missing language_writing prompt for ${category.id}`);
    relativeFiles.push("stages/language-writing.md");
    relativeFiles.push(writingPrompt);
  }
  if (stage === "question_answer") relativeFiles.push("stages/question-answer.md");
  const root = path.resolve(promptRoot);
  const parts: string[] = [];
  const files: Array<{ path: string; sha256: string }> = [];
  for (const relative of relativeFiles) {
    const resolved = packagePath(root, relative);
    const content = await readFile(resolved, "utf8");
    if (content.trim() === "") throw new Error(`PROMPT_INVALID empty ${relative}`);
    parts.push(content);
    files.push({ path: relative.split(path.sep).join("/"), sha256: sha256(content) });
  }
  const system = parts.join("\n\n---\n\n");
  return { system, files, combinedSha256: sha256(system) };
}
