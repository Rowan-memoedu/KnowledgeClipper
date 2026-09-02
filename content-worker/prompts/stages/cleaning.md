# 阶段：保守清洗

检查普通文本中的复制噪声和格式异常。清洗稿必须能由原文通过“删除明确噪声”得到。

允许删除：导航、控件文字、分享提示、广告、与正文无关的统一站点尾注、完全重复分段。`duplicate` 只能用于至少两个逐字相同的完整 segment；必须保留至少一个逐字相同的完整分段副本。短分段只是另一较长分段的子串不算重复，不能删除重复出现的词语、短句或全部副本。

“复制”“分享”“Copyright”“广告”“赞助”等词本身不是删除依据：只有它们构成独立行的控件标签、分享栏、站点尾注、广告标签，或带明确购买/优惠/扫码行动提示的推广行时才能删除。正文中的“DNA 复制”“分享经济”“copyright 法律概念”“广告传播研究”“赞助研究”等必须保留。

理由映射：仅含分享、收藏、点赞、转发、复制、下载等分享栏动作的独立行优先使用 `sharing_prompt`；登录、注册、展开、收起、按钮等界面控件使用 `control`。两类混合且仍是纯控件行时可使用 `control`，不得因此删除带完整语义的正文句。

禁止：改写、总结、翻译、纠错、补充、重排、猜测 OCR 缺字、修复公式内容。疑似 OCR 或语义性格式损坏只能报告 anomaly。

若输入含 `human_resolution`，其中 `protected_ranges` 是用户已明确接受或亲自修正的字符范围：不得删除、替换、移动或重新推断这些字符。人工决议只作为当前来源文本与保护边界，不得描述成模型自主校对。

删除片段不得包含 fenced/inline code 或数学内容（包括 `$...$`、`$$...$$`、`\\(...\\)`、`\\[...\\]`、`\\begin{...}`）；即使它与广告或控件同处一行，也只能报告异常或保留原文。

结构保真：不得改变保留内容的段落边界、换行、列表编号与缩进、代码块及其缩进、表格排布、数学定界符或内部空白。只可把输入的 CRLF/LF 视为等价，并可忽略文件末尾是否有换行；其余空白不得自行归一化。删除整段噪声后，相邻正文段落仍以一个空行连接。

返回：

```json
{
  "cleaned_text": "清洗后的全文",
  "removed_fragments": [
    {
      "segment_index": 0,
      "exact_text": "必须逐字出现在该分段中的文字",
      "occurrence": 0,
      "reason": "navigation|control|advertisement|sharing_prompt|site_footer|duplicate",
      "evidence": "简短判定依据"
    }
  ],
  "format_anomalies": [
    {
      "segment_index": 0,
      "kind": "suspected_ocr|broken_table|broken_formula|ambiguous_layout|other",
      "severity": "notice|requires_human",
      "evidence": "原文中的可核查迹象"
    }
  ]
}
```

没有明确噪声时，原样返回 cleaned_text，removed_fragments 为空。
