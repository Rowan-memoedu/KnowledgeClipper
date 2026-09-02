# general_learning 真实 DeepSeek Canary

模块：`content.deepseek` `0.2.0`  
模型：`deepseek-chat`  
范围：两个非敏感 general_learning 文本；不涉及其他模块。

## 第一次运行：门禁正确拒绝部分候选

Canary：`20260825T155848020Z-offline-general-learning-live-e3f9f93c`  
common result：`partial`  
模块状态：`partial`

失败 item：`item-concept-transfer`

- DeepSeek 分类输出 `answer_state=provided`，但 `supplied_answer` 是空字符串。
- 内容门禁拒绝：`FAILED_CONTENT_GATE classification.source-answer: unit=unit-concept-transfer 的 provided answer 不是来源原文`。
- 另一项 procedure 独立完成；失败项没有被伪装为 ready。

这次产物保持原样作为失败证据，没有人工修改。

## 通用修订

只修改开放 Markdown Prompt，不加入当前样例的答案：

- classification Prompt 明确：`answer_state=provided` 必须同时返回非空、可在来源中逐字连续定位的 `supplied_answer`；不适用字段省略而不是空字符串。
- general_learning Prompt 明确 `concept_transfer` / `procedure` 的模板职责：生成的回忆问题登记到 `generated_content`，答案优先保持来源原句，procedure 不把同一陈述原样放在正反两面。

离线回归随后为 22/22 PASS，再从原始 fixture 新建 attempt。

## 第二次运行：完整通过

Canary：`20260825T155958870Z-offline-general-learning-live-c698175f`  
common result：`completed`  
模块状态：`ready`  
DeepSeek live provider evidence：6 条  
LearningBundle SHA-256：`165CC8A5A33D0F332967BAE63BF2C5D641592C3FA534C00ACB9ABED4FCF21F09`  
CardBundle SHA-256：`481F5716E3B6C75B1E484752B5E9122A16251B0A6ED9839A69311970216E0BCA`

### concept_transfer

原始文本：

> 负迁移是已掌握的旧知识干扰新知识学习；当两个情境表面相似但规则不同时，先比较适用条件可以减少误用。

真实 LearningUnit：

```json
{
  "category_id": "general_learning",
  "subtype": "concept_transfer",
  "answer_state": "provided",
  "supplied_answer": "负迁移是已掌握的旧知识干扰新知识学习；当两个情境表面相似但规则不同时，先比较适用条件可以减少误用。",
  "generated_content": [
    {
      "kind": "question_wording",
      "text": "当两个情境表面相似但规则不同时，如何减少负迁移的误用？"
    }
  ],
  "state": "ready"
}
```

真实 Card：

- template：`concept_transfer`
- front：`当两个情境表面相似但规则不同时，如何减少负迁移的误用？`，provenance=`generated`
- back：完整原始文本，provenance=`source`
- answer_provenance：`source`

### procedure

原始文本：

> 解一元一次方程时，先去括号，再移项并合并同类项，最后把未知数的系数化为 1。每一步都要保持等式两边同作相同变换。

真实 LearningUnit：

```json
{
  "category_id": "general_learning",
  "subtype": "procedure",
  "answer_state": "provided",
  "supplied_answer": "解一元一次方程时，先去括号，再移项并合并同类项，最后把未知数的系数化为 1。每一步都要保持等式两边同作相同变换。",
  "generated_content": [
    {
      "kind": "question_wording",
      "text": "解一元一次方程的一般步骤是什么？每一步要注意什么？"
    }
  ],
  "state": "ready"
}
```

真实 Card：

- template：`procedure`
- front：`解一元一次方程的一般步骤是什么？每一步要注意什么？`，provenance=`generated`
- back：完整原始步骤，provenance=`source`
- answer_provenance：`source`

两个 item 的 cleaning 都保持原文，`removals=[]`、`format_anomalies=[]`；来源与生成 provenance 门禁均为 pass。

## 证据

- 失败回执：`canary/jobs/20260825T155848020Z-offline-general-learning-live-e3f9f93c/canary-receipt.json`
- 失败回执 SHA-256：`A88A95AC25B9516189961B5D885A5342DBEE7287B24509834A54B9F4AE225CC2`
- 成功回执：`canary/jobs/20260825T155958870Z-offline-general-learning-live-c698175f/canary-receipt.json`
- 成功回执 SHA-256：`E2F82F0CC967A0B1E11CD76151C8A4127E51897D1EF40723070076AB80B0D784`
