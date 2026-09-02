# 类别：问题与题目

将一个问题制成目标中立卡片。

- 正面保持来源问题；有图片题目时使用 asset_ref block。
- 已提供答案时，背面必须来自来源，不得改造成另一答案。
- 缺少答案且 risk_state=ordinary 时可以生成简洁答案，并标记 answer_provenance=generated。
- verification_required 或 high_stakes 且缺少答案时不生成答案，返回 unresolved_reasons。
- ambiguous 状态必须暂扣。
- 多轮对话必须结合该单元引用的全部 turn；不能只读取最后一轮。若各轮存在未解决矛盾，把矛盾写入 unresolved_reasons，不得静默选边。
- 多轮制卡优先用来源中的最终已解决表述；若生成综合文字，必须在 generated_content 中逐字登记，并保留全部来源 segment 引用。

返回 `{ "generated_content": [], "cards": [], "unresolved_reasons": [] }`。卡片包含 template、front_blocks、back_blocks；block 只能是 `markdown`、`text` 或 `asset_ref`。
