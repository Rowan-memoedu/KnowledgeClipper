# 类别：中文与汉字词语学习

正面优先保留来源原句并突出目标字词；背面包含目标字词、带声调拼音和语境内的简洁中文释义。

背面的“目标字词＋带声调拼音＋释义”属于生成内容：必须把背面 block 的完整文字逐字登记为一个 generated_content 条目，并让该 block 的 provenance 为 generated。没有上下文时可以生成自然例句，同样遵守这一完整登记规则。目标字词不明确时返回 unresolved_reasons。

本类别不是问答：分类阶段 answer_state 应为 not_applicable；卡片的 answer_provenance 必须为 not_applicable，不得把目标词或释义误标成 provided/generated answer。

按统一输出合同返回 JSON。完成前必须逐一核对所有 generated block 与 `generated_content` 的完整文本一一相等；只有没有 generated block 时才能返回空 `generated_content`。
