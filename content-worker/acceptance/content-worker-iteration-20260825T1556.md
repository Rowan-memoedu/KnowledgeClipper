# 内容理解与制卡：持续开发迭代验收

时间：2026-08-25  
模块：`content.deepseek` `0.2.0`  
本轮外部 API 调用：0。所有新增 Canary 均为离线 replay；DeepSeek 失败测试使用本地 fetch stub。

## 1. general_learning 已通过标准 worker run

Canary：`20260825T155147040Z-offline-general-learning-replay-8c4442f3`  
状态：common result `completed`，模块 `release_state=ready`，发布 LearningBundle 和 CardBundle。

### concept_transfer

原始输入：

> 负迁移是已掌握的旧知识干扰新知识学习；当两个情境表面相似但规则不同时，先比较适用条件可以减少误用。

最终输出：

- LearningUnit：`category_id=general_learning`、`subtype=transfer_strategy`、
  `answer_state=provided`、`state=ready`。
- 生成内容：问题措辞“遇到表面相似但规则不同的新情境时，如何减少负迁移？”。
- Card：`template=concept_transfer`；正面 provenance=`generated`；背面
  “当两个情境表面相似但规则不同时，先比较适用条件可以减少误用。” provenance=`source`；
  `answer_provenance=source`。

### procedure

原始输入：

> 解一元一次方程时，先去括号，再移项并合并同类项，最后把未知数的系数化为 1。每一步都要保持等式两边同作相同变换。

最终输出：

- LearningUnit：`category_id=general_learning`、`subtype=worked_procedure`、
  `answer_state=provided`、`state=ready`。
- 生成内容：问题措辞“解一元一次方程的操作顺序和不变原则是什么？”。
- Card：`template=procedure`；正面 provenance=`generated`；完整步骤背面
  provenance=`source`；`answer_provenance=source`。

两项清理均保持原文，`removals=[]`、`format_anomalies=[]`；每个生成问题都逐字登记在
`generated_content`，每个答案都能逐字回溯到来源。

证据：

- `canary/jobs/20260825T155147040Z-offline-general-learning-replay-8c4442f3/canary-receipt.json`
- receipt SHA-256：`B06D2123CA997DD9B345FED1A454EE80AB12AB7ED3208B2626C8B44F3A6EF34E`

## 2. 高风险缺失答案保持 needs_research

Canary：`20260825T155259319Z-offline-needs-research-replay-2a298a63`

原始输入：

> 某种新保健品是否能替代处方降压药？

最终 LearningBundle：

```json
{
  "release_state": "unresolved",
  "unit": {
    "category_id": "question_answer",
    "subtype": "medical_question",
    "answer_state": "missing",
    "context_state": "missing",
    "risk_state": "high_stakes",
    "state": "unresolved",
    "generated_content": [],
    "unresolved_reasons": ["needs_research"]
  }
}
```

common result 仍是可追溯的 `completed`，但只发布 LearningBundle，并给出：

`module release_state=unresolved; unresolved is preserved for review`

没有调用 card-generation 阶段，没有 CardBundle，也没有自动生成医学答案。

证据：

- `canary/jobs/20260825T155259319Z-offline-needs-research-replay-2a298a63/canary-receipt.json`
- receipt SHA-256：`5EFB242580A4585B8E458CFB1C7EB96F27C92A992F9417CEC4210CD3A52AA951`

## 3. 人工判断由新 input_version 重跑闭环

两次运行使用相同原始文本：

> 在当前规则下，提交后是否还能修改？
>
> 提交后原则上不可修改；管理员确认录入错误时可以退回重填。

### Pending

- input_version：`1`
- `answer_state=ambiguous`
- `unresolved_reasons=[answer_scope_requires_human, answer_state_ambiguous]`
- `release_state=unresolved`
- 只发布 LearningBundle；CardBundle 不存在
- receipt：`9495060D4F2EC4BA76AB62E29CDF5EEBA040FE56AF2AC5B23AB55488CF90AD17`

Canary：`20260825T155457002Z-offline-human-review-pending-replay-22a4ef62`

### Confirmed rerun

- input_version：`2`
- 新 input 的 `human_overrides` 将该 unit 明确为 `answer_state=provided`，并提供来源中的精确答案
- override 重新通过分类契约和来源门禁：`classification.human-overrides=pass`
- `release_state=ready`
- 最终 Card：
  - front：原始问题，provenance=`source`
  - back：`提交后原则上不可修改；管理员确认录入错误时可以退回重填。`，provenance=`source`
  - `answer_provenance=source`
- receipt：`225E7B0E6AF851699E26B1F836B0190499336D2D5044D08682E1ECE1F914091F`

Canary：`20260825T155504718Z-offline-human-review-confirmed-replay-b215ea9f`

这证明人工复核不是手改生成 Bundle：它通过新版本输入，从原始素材重新执行 cleaning、classification 和 card generation。

## 4. DeepSeek 失败语义的离线确定性测试

测试文件：`test/provider.test.ts`。测试使用假 key 和本地 fetch stub，不连接网络。

| 输入情形 | 可观察结果 |
|---|---|
| 第一次 HTTP 503，第二次返回合法 JSON | 调用 2 次，结果 `attempts=2`、`provider=deepseek`、`mode=live` |
| HTTP 401 | 只调用 1 次，立即失败为 `AI_AUTH DeepSeek HTTP 401` |
| HTTP 200 但响应不是 JSON，最大重试 2 次 | 调用 2 次，最终失败为 `AI_CONTRACT exhausted retries` |

## 5. 回归结果

- `npm test`：22 tests，22 pass，0 fail。
- 新增标准 adapter 回归：general_learning 双卡、needs_research 无卡片、人工 pending→confirmed。
- 新增 provider 回归：503、401、非法 JSON。
- 没有修改冻结的 `contracts/v1`，也没有进入 OCR、TTS、RemNote、Anki 或桌面端范围。
