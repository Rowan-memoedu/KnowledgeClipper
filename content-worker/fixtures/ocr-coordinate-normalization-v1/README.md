# OCR coordinate normalization fixtures

这些 fixture 只验证 `ocr_text` 的模型坐标边界，不参与普通文本清洗。

- `cases.json`：来源中立的唯一错位、多重匹配、无匹配、恶意越界四类候选。
- `v1218-frozen-evidence.json`：从 authority v1.2.18 失败 attempt 逐字段冻结的非私密 OCR 文本与 DeepSeek `ocr_proofreading` response。`sourceText` 的 SHA-256 必须为 `1B43E9F82B60C3CA998A587FD5AC577AAA98E69C51E4A238347C27579649ACAA`。

坐标恢复只决定如何发布人工复核候选，绝不自动应用模型建议；原 OCR 文本保持不变。
