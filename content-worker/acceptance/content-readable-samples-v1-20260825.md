# 内容理解与制卡：模块级可读验收样例

生成时间：2026-08-25  
模块：`content.deepseek` / content worker `0.2.0`  
边界：只展示原始内容到 LearningBundle/CardBundle；不涉及 RemNote、Anki、桌面端或导出效果。

## 证据类型

- **真实 API**：产物中的 `ai_trace.mode=live`、`provider=deepseek`、
  `model=deepseek-chat`。本报告只回读已完成 Canary，没有重新请求 DeepSeek。
- **离线 replay fixture**：`ai_trace.mode=replay`、`provider=recorded`、
  `model=fixture`；响应来自冻结的 `provider-replay.v1.json`。
- **离线 projection fixture**：为跨模块投影准备的人工可核验固定样例；没有模型调用，
  其 source/generated provenance 由严格 validator 校验。

## 一览

| 类别 | 样例来源 | 最终状态 | 清理 | 卡片模板 |
|---|---|---|---|---|
| `language_zh` | 真实 DeepSeek API | `ready` | 原文保留，无删除 | `language_context` |
| `language_en` | 真实 DeepSeek API | `ready` | 原文保留，无删除 | `language_context` |
| `question_answer` | 离线 replay fixture | `ready` | 一个样例删除站点尾注；另一个原文保留 | `basic_qa` |
| `general_learning` | 离线 projection fixture | `ready` | 原文保留，无删除 | `concept_transfer`、`procedure` |

## 1. 中文词语学习 `language_zh`

**证据类型：真实 DeepSeek API。**

原始文本：

> 他阅读完补充材料后，对这个结论仍然心存疑窦。

LearningBundle 关键结果：

```json
{
  "release_state": "ready",
  "cleaning": {
    "state": "ready",
    "cleaned_text": "他阅读完补充材料后，对这个结论仍然心存疑窦。\n",
    "removals": [],
    "format_anomalies": []
  },
  "unit_id": "unit-zh-yidou",
  "category_id": "language_zh",
  "subtype": "词语学习",
  "answer_state": "not_applicable",
  "target_term": "疑窦",
  "generated_content": [
    { "kind": "explanation", "text": "疑窦（yí dòu）：指令人怀疑的地方；疑心。" }
  ],
  "state": "ready",
  "unresolved_reasons": []
}
```

CardBundle 最终卡片：

```json
{
  "template": "language_context",
  "front": {
    "type": "markdown",
    "text": "他阅读完补充材料后，对这个结论仍然心存**疑窦**。",
    "provenance": "source"
  },
  "back": {
    "type": "markdown",
    "text": "疑窦（yí dòu）：指令人怀疑的地方；疑心。",
    "provenance": "generated"
  },
  "answer_provenance": "not_applicable"
}
```

清理/拒绝说明：该成功样例没有删除片段或格式异常；来源块和生成块均通过
`card.provenance`。同一真实 API Canary 的一次早期失败见“门禁拒绝样例”。

证据：

- 输入：`fixtures/live-language-zh/materials/clip.md`
- LearningBundle：`canary/runs/attempt-2026-08-25T120148066Z-0c3df455/stages/content-understanding/attempt-2026-08-25T120148066Z-0c3df455/learning-bundle.schema-v1.json`
- CardBundle：`canary/runs/attempt-2026-08-25T120148066Z-0c3df455/stages/content-understanding/attempt-2026-08-25T120148066Z-0c3df455/card-bundle.schema-v1.json`

## 2. 英文词语学习 `language_en`

**证据类型：真实 DeepSeek API；正式 common worker Canary，状态 `completed`。**

原始文本：

> The committee reached a tentative conclusion after reviewing the evidence.

LearningBundle 关键结果：

```json
{
  "release_state": "ready",
  "cleaning": {
    "state": "ready",
    "cleaned_text": "The committee reached a tentative conclusion after reviewing the evidence.\n",
    "removals": [],
    "format_anomalies": []
  },
  "unit_id": "unit-language-en-tentative",
  "category_id": "language_en",
  "subtype": "language_context",
  "answer_state": "not_applicable",
  "target_term": "tentative",
  "generated_content": [
    { "kind": "explanation", "text": "tentative /ˈtentətɪv/ adj. 暂定的，试探性的" }
  ],
  "state": "ready",
  "unresolved_reasons": []
}
```

CardBundle 最终卡片：

```json
{
  "template": "language_context",
  "front": {
    "type": "markdown",
    "text": "The committee reached a **tentative** conclusion after reviewing the evidence.",
    "provenance": "source"
  },
  "back": {
    "type": "markdown",
    "text": "tentative /ˈtentətɪv/ adj. 暂定的，试探性的",
    "provenance": "generated"
  },
  "answer_provenance": "not_applicable"
}
```

清理/拒绝说明：该成功样例没有删除片段或格式异常，且生成释义逐字登记在
`generated_content`。正式 Canary 有 3 条 DeepSeek live provider evidence，最终
`moduleReleaseState=ready`。

证据根：

`canary/jobs/20260825T142100000Z-offline-language-en-live-2bf00b05/`

- 输入：`inputs/content/materials/clip.md`
- LearningBundle：`runs/run-17505aa51312b675761f/stages/content.understand-and-card/attempts/attempt-17505aa51312b675761f/outputs/learning-bundle.schema-v1.json`
- CardBundle：`runs/run-17505aa51312b675761f/stages/content.understand-and-card/attempts/attempt-17505aa51312b675761f/outputs/card-bundle.schema-v1.json`
- 回执：`canary-receipt.json`

## 3. 问答 `question_answer`

### 3.1 来源已有答案，并清除网页尾注

**证据类型：离线 replay fixture，不是真实 API。**

原始文本：

> 为什么天空在晴朗白天通常呈蓝色？
>
> 太阳光进入大气后，短波长蓝光被空气分子瑞利散射得更强，因此从各方向进入眼睛的蓝光较多。
>
> 分享 收藏  
> Copyright Example

清理结果：

```json
{
  "state": "ready",
  "cleaned_text": "为什么天空在晴朗白天通常呈蓝色？\n\n太阳光进入大气后，短波长蓝光被空气分子瑞利散射得更强，因此从各方向进入眼睛的蓝光较多。",
  "removals": [
    {
      "exact_text": "分享 收藏\nCopyright Example",
      "reason": "site_footer",
      "evidence": "统一分享控件与版权尾注"
    }
  ],
  "format_anomalies": []
}
```

删除门禁证据：删除文本可在来源中逐字定位；只删除、不改写正文；删除字符占比
`0.264`，低于门禁上限。

LearningBundle/CardBundle 关键结果：

```json
{
  "unit": {
    "unit_id": "unit-sky-blue",
    "category_id": "question_answer",
    "subtype": "factual_question",
    "answer_state": "provided",
    "question": "为什么天空在晴朗白天通常呈蓝色？",
    "supplied_answer": "太阳光进入大气后，短波长蓝光被空气分子瑞利散射得更强，因此从各方向进入眼睛的蓝光较多。",
    "generated_content": [],
    "state": "ready"
  },
  "card": {
    "template": "basic_qa",
    "front": { "text": "为什么天空在晴朗白天通常呈蓝色？", "provenance": "source" },
    "back": { "text": "太阳光进入大气后，短波长蓝光被空气分子瑞利散射得更强，因此从各方向进入眼睛的蓝光较多。", "provenance": "source" },
    "answer_provenance": "source"
  }
}
```

证据根：

`canary/jobs/20260825T141838065Z-offline-qa-replay-28f1ec17/`

### 3.2 来源没有答案

**证据类型：离线 replay fixture，不是真实 API。**

原始文本：

> 为什么 0! 等于 1？

关键结果：

```json
{
  "unit": {
    "unit_id": "unit-zero-factorial",
    "category_id": "question_answer",
    "subtype": "conceptual_question",
    "answer_state": "missing",
    "question": "为什么 0! 等于 1？",
    "generated_content": [
      { "kind": "answer", "text": "因为空排列只有一种，且递推关系 n!=n×(n-1)! 在 n=1 时要求 0!=1。" }
    ],
    "state": "ready"
  },
  "card": {
    "template": "basic_qa",
    "front": { "text": "为什么 0! 等于 1？", "provenance": "source" },
    "back": { "text": "因为空排列只有一种，且递推关系 n!=n×(n-1)! 在 n=1 时要求 0!=1。", "provenance": "generated" },
    "answer_provenance": "generated"
  }
}
```

清理/拒绝说明：原文未删除、无格式异常；因为 `answer_state=missing` 且
`risk_state=ordinary`，允许生成答案，但生成块必须完整登记在
`generated_content`，不能伪装成来源答案。

证据根：

`canary/jobs/20260825T141838031Z-offline-no-answer-replay-a4788467/`

## 4. 一般学习内容 `general_learning`

**证据类型：离线 projection fixture；不是真实 API，也不是 provider replay。**

### 4.1 概念迁移 `concept_transfer`

原始文本：

> 负迁移是已掌握的旧知识干扰新知识学习；当两个情境表面相似但规则不同时，先比较适用条件可以减少误用。

关键结果：

```json
{
  "unit": {
    "unit_id": "unit-concept-transfer",
    "category_id": "general_learning",
    "subtype": "transfer_strategy",
    "answer_state": "provided",
    "supplied_answer": "当两个情境表面相似但规则不同时，先比较适用条件可以减少误用。",
    "generated_content": [
      { "kind": "question_wording", "text": "遇到表面相似但规则不同的新情境时，如何减少负迁移？" }
    ],
    "state": "ready"
  },
  "card": {
    "template": "concept_transfer",
    "front": { "text": "遇到表面相似但规则不同的新情境时，如何减少负迁移？", "provenance": "generated" },
    "back": { "text": "当两个情境表面相似但规则不同时，先比较适用条件可以减少误用。", "provenance": "source" },
    "answer_provenance": "source"
  }
}
```

### 4.2 步骤学习 `procedure`

原始文本：

> 解一元一次方程时，先去括号，再移项并合并同类项，最后把未知数的系数化为 1。每一步都要保持等式两边同作相同变换。

关键结果：

```json
{
  "unit": {
    "unit_id": "unit-procedure",
    "category_id": "general_learning",
    "subtype": "worked_procedure",
    "answer_state": "provided",
    "supplied_answer": "解一元一次方程时，先去括号，再移项并合并同类项，最后把未知数的系数化为 1。每一步都要保持等式两边同作相同变换。",
    "generated_content": [
      { "kind": "question_wording", "text": "解一元一次方程的操作顺序和不变原则是什么？" }
    ],
    "state": "ready"
  },
  "card": {
    "template": "procedure",
    "front": { "text": "解一元一次方程的操作顺序和不变原则是什么？", "provenance": "generated" },
    "back": { "text": "解一元一次方程时，先去括号，再移项并合并同类项，最后把未知数的系数化为 1。每一步都要保持等式两边同作相同变换。", "provenance": "source" },
    "answer_provenance": "source"
  }
}
```

两例的清理状态均为 `ready`，`removals=[]`、`format_anomalies=[]`。最终答案均为
来源中的精确文本；只生成了问题措辞，且逐字登记在 `generated_content`。

证据：

- 原文：`fixtures/projection-v1/materials/concept.md`、
  `fixtures/projection-v1/materials/procedure.md`
- LearningBundle：`fixtures/projection-v1/learning-bundle.schema-v1.json`
- CardBundle：`fixtures/projection-v1/card-bundle.schema-v1.json`

## 门禁拒绝样例

这些是已经发生并被正确拒绝的产物，不是当前成功结果。

### 真实 DeepSeek API：中文生成块登记不一致

- 原文仍是“他阅读完补充材料后，对这个结论仍然心存疑窦。”
- 模型在卡片背面输出：`疑窦（yí dòu）：指令人怀疑的地方；疑心。`
- 但 `generated_content` 只登记：`疑窦：指令人怀疑的地方；疑心。`
- 门禁拒绝理由：`FAILED_CONTENT_GATE card.generated-block: 生成 block 未在 generated_content 中登记`
- 结果：`release_state=failed`，没有 CardBundle，未静默放行。
- 证据：`canary/runs/attempt-2026-08-25T120054499Z-5a5fadcb/stages/content-understanding/attempt-2026-08-25T120054499Z-5a5fadcb/learning-bundle.schema-v1.json`

### 真实 DeepSeek API：英文答案 provenance 与来源状态冲突

- 门禁拒绝理由：`FAILED_CONTENT_GATE card.answer-provenance: 来源已有答案却使用 generated provenance`
- common result：`status=failed`、`code=content.gate-failed`、`outputs=[]`。
- 结果：未发布 LearningBundle/CardBundle；之后的新独立 Canary 才得到本报告中的英文成功结果。
- 证据根：`canary/jobs/20260825T141408287Z-offline-language-en-live-74641f41/`

## 验收判断

- 当前四个开放类别均已有可以直接阅读的“原文 → LearningBundle → CardBundle”样例。
- 真实 API 证据只覆盖中文、英文词语学习；问答与一般学习内容在本报告中明确是离线证据，不能冒充真实 DeepSeek 结果。
- 清理行为可观察到“原文不改写”和“逐字删除站点尾注”两种情况。
- 内容门禁确实会阻断生成内容登记不一致和 answer provenance 冲突，不会为了得到卡片而自动修补候选产物。
