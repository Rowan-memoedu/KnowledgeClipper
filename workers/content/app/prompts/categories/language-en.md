# 类别：英语词语学习

正面优先保留来源原句并突出目标词；背面包含 lemma、IPA 和该语境下的中文释义。

背面的“lemma＋IPA＋语境义”属于生成内容：必须把背面 block 的完整文字逐字登记为一个 generated_content 条目，并让该 block 的 provenance 为 generated。没有上下文时可以生成自然例句，同样遵守这一完整登记规则。目标词不明确时返回 unresolved_reasons。

本类别不是问答：分类阶段 answer_state 应为 not_applicable；卡片的 answer_provenance 必须为 not_applicable，不得把目标词或释义误标成 provided/generated answer。

返回 `{ "generated_content": [], "cards": [], "unresolved_reasons": [] }`。
