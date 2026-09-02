#!/usr/bin/env node
import { createHash } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";

const args = new Map();
for (let index = 2; index < process.argv.length; index += 2) args.set(process.argv[index], process.argv[index + 1]);
const learningPath = path.resolve(args.get("--learning") ?? "");
const cardPath = path.resolve(args.get("--cards") ?? "");
const outputPath = path.resolve(args.get("--output") ?? "");
if (!path.isAbsolute(learningPath) || !path.isAbsolute(cardPath) || !path.isAbsolute(outputPath)) throw new Error("absolute --learning, --cards and --output are required");
const learningBytes = await readFile(learningPath);
const cardBytes = await readFile(cardPath);
const learning = JSON.parse(learningBytes.toString("utf8"));
const cards = JSON.parse(cardBytes.toString("utf8"));
if (learning.schema_version !== "learning-bundle/1.0.0" || learning.release_state !== "ready") throw new Error("LearningBundle is not ready v1");
if (cards.schema_version !== "card-bundle/1.0.0" || cards.release_state !== "ready") throw new Error("CardBundle is not ready v1");

const lines = [
  "# 内容清洗与审查后的可读结果",
  "",
  `- package_id: \`${learning.package_id}\``,
  `- LearningBundle: \`${sha256(learningBytes)}\``,
  `- CardBundle: \`${sha256(cardBytes)}\``,
  `- 状态: \`${learning.release_state}\``,
  "",
];
for (const item of learning.items) {
  lines.push(`## ${item.item_id}`, "", "### 保守清洗文本", "", item.cleaning.cleaned_text.replace(/\n$/u, ""), "", "### 清洗审查", "", `- 删除片段：${item.cleaning.removals.length === 0 ? "无" : item.cleaning.removals.map((entry) => `\`${entry.exact_text}\` (${entry.reason})`).join("；")}`, `- 格式异常：${item.cleaning.format_anomalies.length === 0 ? "无" : item.cleaning.format_anomalies.map((entry) => entry.evidence).join("；")}`, "", "### 识别出的学习单元", "");
  for (const unit of item.units) {
    lines.push(`#### ${unit.unit_id}`, "", `- 类别：\`${unit.category_id}/${unit.subtype}\``, `- 问题：${unit.question ?? "（无）"}`, `- 答案：${unit.supplied_answer ?? "（无可靠来源答案）"}`, `- 状态：\`${unit.state}\``, "");
  }
}
lines.push("## 最终卡片视图", "");
for (const card of cards.cards) {
  lines.push(`- ${blockText(card.front_blocks)} → ${blockText(card.back_blocks)}（\`${card.template}\`）`);
}
lines.push("");
await writeFile(outputPath, `${lines.join("\n")}\n`, { flag: "wx" });
process.stdout.write(`${JSON.stringify({ outputPath, sha256: sha256(await readFile(outputPath)), bytes: (await readFile(outputPath)).length, learningSha256: sha256(learningBytes), cardSha256: sha256(cardBytes) })}\n`);

function blockText(blocks) {
  return blocks.map((block) => block.text ?? block.asset_ref).join(" | ").replace(/\r?\n/gu, " ");
}

function sha256(value) {
  return createHash("sha256").update(value).digest("hex");
}
