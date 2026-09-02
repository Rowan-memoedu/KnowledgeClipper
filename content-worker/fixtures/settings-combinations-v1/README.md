# 设置组合离线 fixture matrix

本 matrix 不复制或手改模型产物，而是引用四个现有原始 fixture，并由 `scripts/run-settings-combinations-canary.mjs` 每次从原始输入重新 staging、replay、生成 LearningBundle/CardBundle 与可读对照。

覆盖：

- 普通知识：结构与清洗噪声、分类、`basic_qa`。
- 语言含义与读音：`meaning-reading` → `language_context`。
- 语言书写：`writing` → `language_writing`。
- 双向提取：`both` → 每个安全 unit 恰好按 `language_context`、`language_writing` 顺序输出两张。

所有场景使用 provider replay，不访问外部 API，不含凭据、日期、路由或目标端字段。
