# Language practice v2 模块验收

状态：PASS  
Worker：`content.deepseek` 0.4.0  
执行方式：仅离线 replay fixture；本批次真实外部 API 调用为 0。

## 冻结的模块语义

- `languagePracticeMode` 的允许值为 `meaning-reading`、`writing`、`both`。
- 操作级选择一次应用于同一输入内的全部语言 unit；item 级 `human_overrides` 可承载已确认的 unit 书写细节。
- 未选择 mode 时，WorkerResult 仍为 `completed`，发布不可变 `learning-bundle` 与 `language-practice-decision`，不得发布 `card-bundle`。
- Core 读取 decision 工件后，在新输入版本中写入 `human_overrides.languagePracticeMode`，并创建新 attempt；旧 attempt 与工件不覆盖。
- `meaning-reading` 继续使用既有 `language_context` Prompt 和卡片语义；两个旧 Prompt 文件及共享卡片契约均保持原字节哈希。
- `writing` 正面只把原文中唯一的 `target_term` 出现替换为全角 `（读音；意思）`，背面为原始 `target_term`。
- `both` 对每个安全 unit 恰好生成两张卡，顺序固定为 `language_context`、`language_writing`。
- target 不唯一、读音或释义缺失/不可靠时，不猜测；该 unit 进入 decision/unresolved。其他安全 unit 可形成真实 partial CardBundle。

## 样例 A：未选择 mode

Fixture：`fixtures/offline-language-decision-v2`

原文：

> The committee reached a tentative conclusion after reviewing the evidence.

输入关键字段：`category_id=language_en`，`target_term=tentative`，未提供 `languagePracticeMode`。

输出：

- LearningBundle：清洗后原文逐字保留；分类为 `language_en`；unit 为 `unresolved`。
- LanguagePracticeDecision：`state=requires_human`，reason=`language_practice_mode_required`，请求 `human_overrides.languagePracticeMode`，允许三种 mode。
- CardBundle：不存在，这是预期行为。

可核验工件：

- `canary/jobs/20260826T033640587Z-offline-language-decision-v2-replay-ae4c9d60/canary-receipt.json`
- 对应 attempt 中的 `outputs/language-practice-decision.schema-v1.json`
- 对应 attempt 中的 `outputs/language-practice-decision.artifact.v1.json`

## 样例 B：操作级 both，中英文一起应用

Fixture：`fixtures/offline-language-practice-v2`  
操作级 override：`human_overrides.languagePracticeMode=both`

中文原文：

> 他阅读完补充材料后，对这个结论仍然心存疑窦。

中文卡片，固定顺序：

1. `language_context`
   - 正面：`他阅读完补充材料后，对这个结论仍然心存**疑窦**。`
   - 背面：`疑窦 yí dòu：疑问；疑惑。`
2. `language_writing`
   - 正面：`他阅读完补充材料后，对这个结论仍然心存（yí dòu；疑问或疑惑）。`
   - 背面：`疑窦`

英文原文：

> The committee reached a tentative conclusion after reviewing the evidence.

英文卡片，固定顺序：

1. `language_context`
   - 正面：`The committee reached a **tentative** conclusion after reviewing the evidence.`
   - 背面：`tentative /ˈtentətɪv/：暂定的；尚未最终确定的。`
2. `language_writing`
   - 正面：`The committee reached a （/ˈtentətɪv/；暂定的） conclusion after reviewing the evidence.`
   - 背面：`tentative`

结果：4 张卡，`release_state=ready`。中文 writing 细节来自已验证的人类 override；英文 writing 细节来自离线 replay 模型输出。

可核验回执：`canary/jobs/20260826T033641648Z-offline-language-practice-v2-replay-5a41e77b/canary-receipt.json`

## 样例 C：混合语言局部 unresolved

Fixture：`fixtures/offline-language-mixed-partial-v2`  
操作级 choice：`languagePracticeMode=writing`

安全中文原文：

> 他阅读完补充材料后，对这个结论仍然心存疑窦。

已发布中文 writing 卡：

- 正面：`他阅读完补充材料后，对这个结论仍然心存（yí dòu；疑问或疑惑）。`
- 背面：`疑窦`

不安全英文原文：

> Please review the draft before Friday.

英文 unit 的 target `draft` 只出现一次，但 fixture 把语境释义标为 `unreliable`。结果为：

- 英文 unit 保持 unresolved，reason=`language_writing_details_required`；未生成猜测卡。
- 中文安全卡照常发布。
- 同一 completed WorkerResult 同时发布 decision artifact 与 partial CardBundle，`release_state=partial`。

可核验回执：`canary/jobs/20260826T033642758Z-offline-language-mixed-partial-v2-replay-095b2a6c/canary-receipt.json`

## 验证结果

- `npm test`：51/51 PASS，0 fail。
- 旧英文含义—读音回归：输入显式指定 `meaning-reading`，生成期望未放宽、未改写。
- 无 mode 中英文语义：独立测试验证 decision artifact 存在且 CardBundle 缺失。
- `content-input/2.0.0`：6 个输入 fixture 通过 schema。
- `category-registry/2.0.0`：开放类别/模板注册表通过 schema。
- `language-practice-decision/1.0.0`：3 个真实离线 decision 工件通过 schema。
- 三个离线 Canary 的 common request/result/artifact 与模块工件验证全部 PASS。
- `KnowledgeClipper.Content.Worker` 与 legacy compatibility alias 构建均为 0 warning、0 error。
- stable common checkpoint：21 文件重新计算，0 mismatch；冻结 v1 未修改。

## 模块外边界

Core 负责把 decision 工件映射为公共 `waiting_input` 交互、保存新版本 human override 并启动新 attempt。Registry admission、release assembly、桌面交互、RemNote/Anki 导出和跨模块 E2E 均不属于本模块回执。
