#!/usr/bin/env node
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";

const args = new Map();
for (let index = 2; index < process.argv.length; index += 2) {
  args.set(process.argv[index], process.argv[index + 1]);
}

const phase = args.get("--phase");
const bundlePath = args.get("--answer-bundle");
const outputRoot = args.get("--output-root");
const followupQuestion = args.get("--followup-question");
if (!bundlePath || !outputRoot || (phase !== "followup" && phase !== "make-cards")) {
  throw new Error("usage: --phase followup|make-cards --answer-bundle <absolute> --output-root <absolute> [--followup-question <text>]");
}
if (!path.isAbsolute(bundlePath) || !path.isAbsolute(outputRoot)) throw new Error("paths must be absolute");
if (phase === "followup" && !followupQuestion?.trim()) throw new Error("followup requires a non-empty --followup-question");

const bundle = JSON.parse(await readFile(bundlePath, "utf8"));
if (bundle.schema_version !== "question-answer/1.0.0" || !Array.isArray(bundle.turns) || bundle.turns.length === 0) {
  throw new Error("invalid question answer bundle");
}

const questionTurns = bundle.turns.map((turn, index) => ({
  turnId: turn.turn_id,
  turnIndex: turn.turn_index,
  question: turn.question,
  ...(index > 0 ? { parentAnswerId: bundle.turns[index - 1].answer_id } : {}),
}));
const answerAncestry = bundle.turns.map((turn, index) => ({
  answerId: turn.answer_id,
  artifactRef: turn.artifact_ref,
  turnId: turn.turn_id,
  turnIndex: turn.turn_index,
  questionSha256: turn.question_sha256,
  answerText: turn.answer_text,
  answerTextSha256: turn.answer_text_sha256,
  ...(index > 0 ? { parentAnswerId: bundle.turns[index - 1].answer_id } : {}),
}));

if (phase === "followup") {
  const turnIndex = questionTurns.length;
  questionTurns.push({
    turnId: `turn-regression-${turnIndex}`,
    turnIndex,
    question: followupQuestion.trim(),
    parentAnswerId: bundle.turns.at(-1).answer_id,
  });
}

const inputVersion = String(Number.parseInt(bundle.input_version, 10) + 1);
const input = {
  schema_version: "standard-input/3.0.0",
  package_id: bundle.package_id,
  task_id: bundle.task_id,
  input_version: inputVersion,
  mode: "question",
  questionWorkflow: {
    schemaVersion: "question-workflow-input/1.0.0",
    phase: phase === "followup" ? "answer" : "make-cards",
    questionTurns,
    answerAncestry,
  },
  items: [{
    item_id: "item-regression-question",
    source: { kind: "question_workflow" },
    hints: { requested_templates: ["basic_qa"] },
  }],
};

const inputDirectory = path.join(outputRoot, "inputs");
await mkdir(inputDirectory, { recursive: true });
const outputPath = path.join(inputDirectory, "standard-input.v3.json");
await writeFile(outputPath, `${JSON.stringify(input, null, 2)}\n`, { flag: "wx" });
process.stdout.write(`${JSON.stringify({ outputPath, phase: input.questionWorkflow.phase, inputVersion, turnCount: questionTurns.length, ancestryCount: answerAncestry.length })}\n`);
