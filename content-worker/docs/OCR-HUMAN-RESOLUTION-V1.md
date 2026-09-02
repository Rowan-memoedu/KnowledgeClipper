# OCR 人工决议续跑 v1

## 边界

`content.deepseek 0.9.0` 只追加 Content 模块本地能力，不修改 frozen common WorkerRequest/Artifact v1，也不改写 `content.deepseek 0.8.0` 的 OCR 原文、`ocr-proofread-bundle/1.0.0` 或失败证据。

- Core 继续拥有 `waiting_input`、`targetToken`、`task.provide_input`、ancestry、原 destinations 与新 attempt 创建。
- Desktop 只收集明确的 `accept_original` 或 `user_correction`；不得提供猜测或 synthetic oracle。
- Content 验证 Core 生成的 `standard-input/5.0.0`，默认不重跑 OCR，随后执行标准 cleaning、classification、card generation。
- Content 只输出 destination-neutral 的 `ocr-human-resolution-bundle/1.0.0`、`learning-bundle/3.0.0` 与条件式 `card-bundle/1.0.0`，不调用任何 exporter。

## 两种动作

### accept_original

用户确认 frozen proofread 中保留的 unresolved 字面值就是预期文本。输入不得携带 correction；候选范围被标为用户明确字符，后续清洗不得删除或改写。

### user_correction

用户提交完整 corrected text、SHA-256 与逐项 diff。每个 diff 必须在 frozen unresolved candidate 范围内，且能从 frozen proofread 精确重放。输出记录 `actor=user`、`provenance=human_submitted`；不得标为 AI 自主校对。

## 失败语义

- 无人工动作：继续使用 `standard-input/4.0.0`，保持 `unresolved`；Core 投影为 waiting_input。
- artifact/manifest/payload/hash/candidate/diff 任一不匹配：`INPUT_TAMPERED` 或 `INPUT_INVALID`，不调用 provider。
- DeepSeek 清洗删除用户明确字符：`FAILED_CONTENT_GATE human-resolution.user-characters`。
- 重审后仍有格式、答案或内容歧义：LearningBundle 继续 `unresolved`，不伪造 CardBundle。
- 只有 LearningBundle ready 时 CardBundle 才可供 Core 继续原 destinations；目的端选择不进入 Content 输入或输出语义。

## Core resume adapter

1. `task.provide_input` 校验 `knowledge-clipper-ocr-human-resolution-command/1.0.0` 与一次性 `targetToken`。
2. 读取 waiting attempt 的 frozen `ocr-proofread-bundle`，把原 OCR provenance、artifact/payload SHA、候选和用户动作写入新的不可变 `standard-input/5.0.0`。
3. 新 content-input artifact 的 `inputRefs` 同时绑定原 OCR artifacts 与 frozen proofread artifact；保留 task ancestry 与原 destinations；`rerunOcr=false`。
4. 创建新 Content attempt，运行既有 `understand-and-card` operation。Content 不接受 resume 作为新的 common operation。

Schemas：

- `schemas/ocr-human-resolution-command.v1.schema.json`
- `schemas/content-input.v5.schema.json`
- `schemas/ocr-human-resolution-bundle.v1.schema.json`
- `schemas/learning-bundle.v3.schema.json`
