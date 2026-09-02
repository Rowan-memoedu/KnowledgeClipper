# Content 0.8.0 OCR 保守校对验收

## 结论

- 仅 `standard-input/4.0.0` 的 `ocr_text` 进入 OCR 校对阶段。
- 原始 OCR Markdown/structured artifact 只按 SHA-256 引用，从不改写。
- 自动纠错只允许可逐字定位、置信度至少 0.90、单项编辑距离不超过 2 的 substitution/insertion/deletion。
- 有多个合理候选时保留原文并输出 `unresolved`；不发布 CardBundle。
- 普通 `text` 继续使用 0.7 delete-only 语义和 LearningBundle v1。

## 离线缺字正例（strict replay fixture）

Before:

> Answer: PHOTOSYTHESIS.

After:

> Answer: PHOTOSYNTHESIS.

最小 diff：在已声明 segment 的字符位置 15 插入 `N`，confidence 0.98，edit distance 1。后续 delete-only cleaning 独立删除 copyright footer。

最终卡片：

- Front: What process converts light energy into chemical energy?
- Back: PHOTOSYNTHESIS.

## 离线歧义反例（strict replay fixture）

Before / After:

> Answer: MODL.

`MODL` 无唯一可证明修正，corrections 为空，状态为 `unresolved/insufficient_context`，无 CardBundle。

## 真实 DeepSeek 对现有 OCR artifact 的新 attempt

Provider: `deepseek-chat` live，1 次调用。未重跑 MinerU，未调用 RemNote/Anki。

Before:

> KNOWLEDGECLIPPER SYNTHETIC OCR CANAR Question: What is 19 + 23? Answer: 42 No personal information.

After:

> KNOWLEDGECLIPPER SYNTHETIC OCR CANAR Question: What is 19 + 23? Answer: 42 No personal information.

DeepSeek 判定 `CANAR` 可能是 `CANARY` 或 `CANARD`，缺少唯一上下文，因此保留原文并发布不可变 `ocr-proofread-bundle` + `learning-bundle/2.0.0`。WorkerResult 为 `completed`，模块状态为 `unresolved`，无 CardBundle；Core 应转为人工判断/waiting_input，不得送入 exporter。

## 不合格 0.8.1 证据边界

0.8.1 曾错误将 synthetic Canary 的 `content-consumer-evidence.v1.json#/inputReadableText` 当作生产校对证据，且 live 响应的字符坐标未通过 `before-exact` 门。该 release/attempt 只作失败取证，标记 `never-promoted/failed-canary`，不得进入 authority。
