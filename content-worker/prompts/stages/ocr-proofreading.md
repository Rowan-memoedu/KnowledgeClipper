# 阶段：OCR 保守校对

本阶段只会接收已由 worker 验真为 `ocr_text` 的文字及其不可变 OCR provenance。你的任务是发现有充分上下文证据的 OCR 字符错误，并登记可精确重放的最小 diff；不要做普通文本润色、总结、翻译或噪声清理。

自动纠错必须同时满足：

- 来源中当前写法在局部词法、同文档重复或完整语境中明显不成立，而纠正后有唯一、高可信解释；
- 每项只使用 substitution、insertion 或 deletion，编辑距离不超过 2；
- `start`/`end` 是相对该 segment 文本的零基 UTF-16 半开区间，`before` 必须与该范围逐字相等；
- 不修改任何空格、换行、段落、列表、表格或其他布局；
- 不修改代码、公式、标识符、数字答案或缺少充分证据的专名；
- `confidence` 仅在至少 0.90 时才可放入 corrections，并说明证据类型和可核查证据。

以下情况不得猜测：存在多个合理候选、需要外部事实才能判断、疑似代码/公式、结构边界不明。把原文范围逐字登记到 `unresolved`，`proofread_text` 保持该处不变。不要为了生成卡片而强行消除歧义。

本阶段不删除标题、版权、分享控件、广告或其他噪声；它们由后续普通 cleaning 阶段按 delete-only 规则处理。

返回严格 JSON：

```json
{
  "proofread_text": "仅应用 corrections 后的全文；其余字符和结构不变",
  "corrections": [
    {
      "segment_index": 0,
      "operation": "substitution|insertion|deletion",
      "start": 0,
      "end": 1,
      "before": "来源范围原文；insertion 时为空字符串",
      "after": "替换文字；deletion 时为空字符串",
      "confidence": 0.97,
      "evidence_type": "lexical_context|cross_segment_consistency|document_context",
      "evidence": "不依赖隐藏知识、可由输入核查的唯一性依据"
    }
  ],
  "unresolved": [
    {
      "segment_index": 0,
      "start": 0,
      "end": 1,
      "exact_text": "逐字原文",
      "reason": "ambiguous_text|insufficient_context|protected_content|structural_ambiguity",
      "evidence": "不能安全自动纠错的原因"
    }
  ]
}
```

没有错误或歧义时原样返回全文，两个数组均为空。
