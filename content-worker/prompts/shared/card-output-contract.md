# 卡片阶段统一输出合同

无论类别如何，必须返回下面形状的 JSON。字段名不得换成 `content`、`front`、`back` 等近义词，也不得省略 provenance。

```json
{
  "generated_content": [
    {
      "kind": "answer|example|explanation|question_wording|other",
      "text": "AI 新生成的完整文字"
    }
  ],
  "cards": [
    {
      "template": "类别允许的模板 ID",
      "front_blocks": [
        {
          "type": "markdown|text",
          "text": "完整 block 文字",
          "provenance": "source|generated|human_confirmed"
        },
        {
          "type": "asset_ref",
          "material_path": "输入中已声明的素材相对路径",
          "provenance": "source|human_confirmed"
        }
      ],
      "back_blocks": [],
      "answer_provenance": "source|generated|human_confirmed|not_applicable"
    }
  ],
  "unresolved_reasons": []
}
```

约束：

1. `front_blocks`、`back_blocks` 都至少一个 block。
2. `markdown`、`text` 使用 `text`；`asset_ref` 使用 `material_path`，两者不能互换。
3. provenance=source 的文字必须可在该单元的来源分段中逐字定位；仅增加成对、非空且正确闭合的 `**...**` 或 `__...__` 强调标记除外。未闭合、空强调或借强调符掩盖文字改写均不允许。
4. provenance=generated 的每个 block，其完整 `text` 必须与某一个 `generated_content[].text` 完全相等；不能只登记包含该 block 的更长文字，也不能登记其中一部分或近义改写。拼音、IPA、标点、括号和释义都属于完整文本。
5. 来源已有答案时 answer_provenance 只能是 source 或 human_confirmed。
6. 普通问题缺少答案且确实生成答案时，answer_provenance 必须是 generated。
7. `language_zh`、`language_en` 的释义卡不是题目答案，answer_provenance 必须是 not_applicable；生成的释义仍以 block provenance=generated 并逐字登记。
8. 无法满足合同时，cards 返回空数组并写明 unresolved_reasons；不得猜字段或输出半成品。
9. 当前输入契约没有卡片正文级的人工确认通道；模型不得输出 provenance=human_confirmed 或 answer_provenance=human_confirmed。该枚举仅为未来经过验证的人工覆盖保留。
10. answer_provenance=source 时背面至少一个 block 必须为 source；answer_provenance=generated 时背面至少一个 block 必须为 generated。asset_ref 只能引用已声明来源素材并标记 source。
11. 不得输出正反面 block 完全相同的卡片；同一学习单元内不得输出内容与 provenance 都完全相同的重复卡片。
12. `generated_content` 只登记实际出现在卡片 generated block 中的完整文字；不得夹带未被任何卡片引用的生成说明、草稿或备用答案。没有卡片时该数组必须为空。
13. 单个学习单元的输出状态必须二选一：有卡片时 `unresolved_reasons` 必须为 `[]`；没有卡片时必须给出至少一个明确未决原因。不得同时放行卡片又声称该单元未决。

最终一致性检查（必须在序列化 JSON 前执行）：

1. 遍历所有 `front_blocks` 和 `back_blocks`，收集每个 `provenance=generated` 的完整 `text`。
2. 对每个收集到的完整文本，在 `generated_content` 中登记一条完全相等的 `text`；生成的问题措辞使用 `kind=question_wording`。
3. 再反向检查 `generated_content`：每条登记都必须被某个 generated block 完整引用。
4. 只有在没有任何 generated block 时，`generated_content` 才能为 `[]`。不得先复制空默认数组后遗漏回填。
