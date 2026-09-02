# Content 0.9.1 OCR 模型坐标恢复与人工续跑验收

## 失败基线：authority v1.2.18 / Content 0.9.0

原始 OCR：KNOWLEDGECLIPPER SYNTHETIC OCR CANAR Question: What is 19 + 23? Answer: 42 No personal information.

DeepSeek 候选：before=CANAR，after=CANARY，模型坐标=[34,39)。0.9.0 因 before-exact 直接 failed，未产生 decision token。

## 0.9.1 冻结 response replay

- exact before 在声明 segment 中唯一匹配，规范化为 [31,36)。
- 候选 disposition=unresolved_human_review_not_applied；原文仍为 CANAR。
- WorkerResult=completed，moduleReleaseState=unresolved；只发布 proofread + LearningBundle v2，不发布 CardBundle。
- DeepSeek 外部调用 0；OCR 重跑 0；Exporter 调用 0。

## 人工 user_correction

用户 diff：{"after":"Y","before":"","end":36,"operation":"insertion","segment_index":0,"start":36}

Resolved：KNOWLEDGECLIPPER SYNTHETIC OCR CANARY Question: What is 19 + 23? Answer: 42 No personal information.

Card Front：Question: What is 19 + 23?

Card Back：Answer: 42

该字符来自 actor=user / provenance=human_submitted，不包装为 AI 自主修复。

## 人工 accept_original

用户明确接受后保留 CANAR，diff=[]；才进入清洗、分类、制卡并发布 ready bundles。

## 边界

普通 text 仍执行 delete-only；坐标恢复仅作用于 ocr_text proofreading。Core 仍拥有 waiting_input、一次性 decision token、resume、新 attempt、ancestry 与 destinations；本模块未调用任何 exporter。
