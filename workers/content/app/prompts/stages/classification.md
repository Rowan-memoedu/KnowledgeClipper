# 阶段：学习单元切分与分类

根据输入中的开放类别注册表，把清洗后的有效分段组织成一个或多个学习单元。每个非空、未删除的分段至少属于一个单元。

覆盖是硬约束：输出前先列出输入提供的全部 `segment_index`，再核对它们都至少出现在一个 unit 的 `source_segment_indexes` 中。标题、说明、示例代码、公式核验等辅助分段不能因为不是独立答案而遗漏，应挂到语义相关的学习单元；不得只选择你认为最核心的几个分段。

不得创造注册表之外的 category_id。类别不够用时选最接近的现有类别，并在 unresolved_reasons 中明确指出需要扩充注册表；不要把它当作低价值内容。

返回：

```json
{
  "units": [
    {
      "unit_id": "本次输入内稳定且唯一的短 ID",
      "source_segment_indexes": [0],
      "category_id": "开放类别注册表中的 ID",
      "subtype": "可扩展的细分类名称",
      "answer_state": "provided|missing|ambiguous|not_applicable",
      "context_state": "provided|missing|ambiguous|not_applicable",
      "risk_state": "ordinary|verification_required|high_stakes",
      "question": "如适用，保持来源措辞的问题",
      "supplied_answer": "如适用，来自来源的答案",
      "target_term": "如适用，语言学习目标",
      "unresolved_reasons": []
    }
  ]
}
```

置信度数字不能代替证据或 unresolved 状态。

每个 unit 必须始终输出 `unit_id`、`source_segment_indexes`、`category_id`、`subtype`、`answer_state`、`context_state`、`risk_state`、`unresolved_reasons`。即使没有未决原因，`unresolved_reasons` 也必须显式为 `[]`，不得省略；只有 `question`、`supplied_answer`、`target_term` 是按适用性省略的可选字段。

语义约束：

- `hints` 是上游已知约束；除非与来源直接冲突，不得擅自反转。
- `language_zh`、`language_en` 是词语学习，不是题目问答；其 `answer_state` 必须为 `not_applicable`，不得把 `target_term` 同时当作 `supplied_answer`。
- 每个 unit 的 `source_segment_indexes` 不得重复同一个索引。`target_term` 也是来源抽取字段，必须可在该单元来源分段中逐字连续定位；词形还原、拼音、IPA 与释义留给后续制卡阶段生成。
- `question` 是来源抽取字段，不是让你生成卡片题目：只有来源分段中存在可逐字连续定位的显式问题时才填写；来源只有概念、结论或步骤时必须省略 `question`。需要把陈述转成问题的工作留给后续制卡阶段，并在那里登记为 `generated_content`。
- 只有来源真的给出答案或待学习的结论/步骤时才填写 `supplied_answer`。
- `answer_state=provided` 时，`supplied_answer` 必须是非空且可在该单元来源分段中逐字连续定位的文本；不得返回空字符串、摘要或改写。一般学习内容即使没有显式问句，只要来源已给出待学习的结论或步骤，也应把对应的来源原句登记为 `supplied_answer`。
- `answer_state=missing` 时不得填写 `supplied_answer`。不适用的可选字段应省略，不要用空字符串占位。
