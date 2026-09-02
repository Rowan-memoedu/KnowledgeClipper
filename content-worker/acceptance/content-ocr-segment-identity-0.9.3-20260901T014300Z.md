# Content 0.9.3 OCR segment 身份隔离验收

## 失败复现

- v1.2.26 / Content 0.9.2：active=pseg-377bd9a8e9d7ded9；audit=pseg-377bd9a8e9d7ded9；collision=pseg-377bd9a8e9d7ded9。
- Core：binding.ocr_proofread_segment_invalid — OCR proofread segment collides with a source segment: pseg-377bd9a8e9d7ded9。Anki worker 未启动，目的端写入 0。

## 0.9.3 三空间规则

- seg-*：原始 OCR；pseg-*：不可变校对审计；rseg-*：自动校对后的正式内容；hseg-*：人工决议后的正式内容。
- unit/card 仅引用 rseg/hseg；source_segments 与 proofreading.proofread_segments 必须无交集。
- Content producer 在发布前拒绝旧 missing-ref 与旧 collision，两类错误不再依赖 Core 才发现。

## 自动 OCR frozen-response replay

- active=rseg-1a10a22d876a63cf；audit=pseg-377bd9a8e9d7ded9；unit/card refs=rseg-1a10a22d876a63cf。
- 卡片：Front=What is 19 + 23?；Back=42。

## 人工续跑

- accept_original：active=hseg-681019c38e07d45e；audit=pseg-d46220ad54e66e4c；原字符未改。
- user_correction：active=hseg-150de16d9b0760aa；audit=pseg-d46220ad54e66e4c；用户 diff={"after":"Y","before":"","end":36,"operation":"insertion","segment_index":0,"start":36}。

所有新 attempt 均 completed；Card manifest 精确绑定同 attempt Learning artifactRef/path/SHA。provider=replay；DeepSeek/MinerU/exporter/目的端写入均为 0。
