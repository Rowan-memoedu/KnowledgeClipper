# Content 0.9.2 卡片来源引用一致性验收

## 旧 0.9.1 失败事实

- Core 诊断：`binding.content_reference_missing` — Card references a missing source segment: pseg-d46220ad54e66e4c
- LearningBundle 活动 segment：`seg-18ffaad5e73747b1`
- CardBundle 引用：`pseg-d46220ad54e66e4c`
- 三个 Content artifact/manifest 均合法，但组合内部不一致；Core 在 Anki worker 启动前拒绝是正确行为。

## 0.9.2 修复与重跑

- ocr-proofread-bundle 继续保存原始 OCR segment；没有改写历史工件。
- accept_original 后，LearningBundle 顶层 source_segments 使用已接受的 pseg；user_correction 使用 hseg。
- Content 新增卡片来源引用门禁：任一 card source_segment_id 无法在同一 LearningBundle item 解析即 fail-closed。
- 新 LearningBundle 活动 segment：`pseg-d46220ad54e66e4c`
- 新 CardBundle 引用：`pseg-d46220ad54e66e4c`
- 卡片：Front = Question: What is 19 + 23?；Back = Answer: 42。
- 原 OCR / accept_original 最终文本：KNOWLEDGECLIPPER SYNTHETIC OCR CANAR Question: What is 19 + 23? Answer: 42 No personal information.
- provider=replay；OCR rerun=0；DeepSeek live calls=0；exporter calls=0。

## Core 精确消费规则

- CardBundle：`card-bundle-2eff909a0385e3c1881e6587`，manifest SHA-256 `13037692205ed5d495556296df05630aad7b40e96539a148fb8e86e8c2297995`。
- LearningBundle：`learning-bundle-8a56a3178be06cf6dd64f259`，manifest SHA-256 `9da633bbfba1c1fb3d894bb84dc7cb47fff40011f6372275b1e17d16156d2c7c`。
- 必须消费上述同一 Content attempt 的成对工件；ocr-human-resolution-bundle 仅为审计，不代替内容/卡片工件。
