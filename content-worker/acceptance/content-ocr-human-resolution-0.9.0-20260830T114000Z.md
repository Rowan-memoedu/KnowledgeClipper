# Content 0.9.0 OCR 人工决议续跑验收

## 原始 waiting 状态

- 0.8.0 request：`C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\canary\image-ocr-v1.2.2-live-single-20260830T091817Z-668752c2\jobs\task-image-ocr-v1.2.2-live-single-20260830T091817Z-668752c2\runs\run-content-ocr-proofread-20260830T095600Z\stages\content.understand-and-card\attempts\attempt-ocr-proofread-0.8.0-0001\request.json`
- 0.8.0 result：completed + moduleReleaseState=unresolved；CardBundle=false。
- 原始 OCR SHA-256：`1B43E9F82B60C3CA998A587FD5AC577AAA98E69C51E4A238347C27579649ACAA`，本轮前后未改写。
- Core 应继续投影为 waiting_input；没有人工命令时 Content 不会自动猜字。

原始 / frozen 文本：

> KNOWLEDGECLIPPER SYNTHETIC OCR CANAR Question: What is 19 + 23? Answer: 42 No personal information.

## accept_original（严格 replay）

用户动作：accept_original；diff=[]；最终文本仍为：

> KNOWLEDGECLIPPER SYNTHETIC OCR CANAR Question: What is 19 + 23? Answer: 42 No personal information.

卡片：Front = Question: What is 19 + 23?；Back = Answer: 42。

## user_correction（严格 replay）

用户明确把 CANAR 补为 CANARY；登记 diff：`{"after":"Y","before":"","end":36,"operation":"insertion","segment_index":0,"start":36}`。

> KNOWLEDGECLIPPER SYNTHETIC OCR CANARY Question: What is 19 + 23? Answer: 42 No personal information.

卡片：Front = Question: What is 19 + 23?；Back = Answer: 42。

## user_correction（真实 DeepSeek）

人工证据：actor=user，provenance=human_submitted；不是 AI 自主纠错。

清洗后：KNOWLEDGECLIPPER SYNTHETIC OCR CANARY Question: What is 19 + 23? Answer: 42 No personal information.

分类：question_answer/basic_qa。

卡片：Front = What is 19 + 23?；Back = 42。

DeepSeek stages：cleaning:deepseek-chat:attempts=1；classification:deepseek-chat:attempts=1；card_generation:deepseek-chat:attempts=1。OCR stage=0，MinerU 重跑=0，exporter calls=0。

## 接线边界

- Content 已完成 module-local command/schema、v5 SHA-bound input、人工 diff 验真、受保护字符门禁及 destination-neutral bundles。
- Core 仍需把 task.provide_input、一次性 targetToken、waiting_input、ancestry、原 destinations 与新 attempt 正式接入 authority；默认 rerunOcr=false。
- Desktop 仍需展示 before/candidate/diff/reason 并提交明确动作。Content 不修改 Core/Desktop，也未进行任何目的端写入。
- GAP-OCR-HUMAN-CONTINUE 的模块侧 admission 已具备，但产品级 exporter E2E 仍由 Core/Desktop/Exporter 闭环，不能由本回执外推为全产品完成。
