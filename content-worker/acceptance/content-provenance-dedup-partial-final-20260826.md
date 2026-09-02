# 内容清洗与制卡最终增量验收

生成时间：2026-08-26 02:10 UTC  
Worker：`content.deepseek` / `0.2.0`  
本批外部 API 调用：0

本报告及其最终回执 supersede 02:01 UTC 的中间检查点；旧报告和旧回执只作为 append-only 历史保留，不再代表当前代码哈希。冻结 `contracts/v1` 未修改。

## 最终结果

- `npm test`：44/44 PASS，build PASS。
- 六组 replay Canary 均从各自原始 fixture 通过标准 `describe/run` worker 入口重新生成；六组公共 request/result/Artifact 和 LearningBundle/CardBundle schema 验证全部 PASS。
- projection fixture：PASS，仍覆盖 4 类别、5 模板、3 block 类型。
- stable checkpoint：21 files checked、0 mismatch；receipt SHA-256 `1CB738FE133F6645B9757E246E2FBC210785086342F70BAD32B5015BD19E1B1E`。

## 本轮实现的门禁

### 保守清洗

- 只能删除可逐字定位且形态可独立核验的导航、控件、分享栏、广告、站点尾注和整段重复。
- `duplicate` 要求至少两个完整 segment 逐字相同，只能删除整段，并至少保留一个完整副本；“课程页眉”只是“课程页眉扩展”的子串时会显式失败。
- 不得删除 fenced/inline code、`$...$`、`$$...$$`、`\(...\)`、`\[...\]` 或 `\begin{...}` 数学内容。
- “DNA 复制”“分享经济”“Copyright 法律保护”“版权所有人依法享有复制权”“广告是一种商业传播形式”“赞助研究需要披露利益冲突”等领域正文均受反例保护。
- 广告只有独立标签，或同时具有推广标识与明确购买、优惠、扫码 CTA 时才允许自动删除；模糊内容默认保留。

### 分类、格式异常与人工状态

- unit 内 `source_segment_indexes` 必须唯一；语言 `target_term` 必须逐字来自该 unit 的来源分段。
- `requires_human` 格式异常只冻结引用异常 segment 的 unit；不再冻结同一 item 的无关干净 unit。
- 破损内容不改写、不猜修；进入 `unresolved` 并保留异常种类、证据和 segment 引用。

### 卡片内容与 provenance

- 每个 generated block 的完整文字必须与一个 `generated_content[].text` 完全相等；反向也要求每个生成条目实际被某个 generated block 使用。
- source block 必须逐字位于来源中；唯一例外是增加成对、非空、正确闭合的 `**...**` / `__...__` 强调。
- 当前模型输出不能声明 `human_confirmed`；asset 只能是已声明来源且 provenance 为 source。
- `answer_provenance=source/generated` 必须由背面同 provenance block 支持。
- 禁止正反面完全相同、同 unit 完全重复卡片，以及“已放行 cards + 非空 unresolved_reasons”的混合状态。

## 可观察样例

所有样例均为离线 replay；本轮没有调用 DeepSeek。

### 结构清洗

原始尾部：

```text
复制
分享
Copyright Example 2026
```

结果只删除上述控件/尾注，Markdown 标题、编号列表、代码块缩进和 `$$x + 2 = 5$$` 全部保留。最终 `procedure` 卡背面仍含原则、列表、代码与公式四个 source block。

### 完整重复

原始：

```text
课程页眉

课程页眉

核心结论：重复清洗必须至少保留一个完整副本。
```

结果保留一份“课程页眉”和核心结论；只记录一次整段 `duplicate` 删除。最终 basic_qa 背面为 source：

> 核心结论：重复清洗必须至少保留一个完整副本。

### 局部格式异常

原始：

```text
地球绕太阳公转。

公式草稿：$x + = 5$
```

LearningBundle 与 CardBundle 均为 `partial`：

- `unit-clean-fact` ready，卡片为“地球绕什么天体公转？”→“地球绕太阳公转。”
- `unit-broken-formula` unresolved，理由 `format_anomaly_requires_human`；CardBundle 的 `withheld_units` 只列该 unit。
- 破损公式原样保留，证据为“公式在加号后缺少操作数”。

### 英语、无答案与 general_learning

- English：来源句突出 `**tentative**`；生成背面 `tentative /ˈtentətɪv/：暂定的；尚未最终确定的。`，`answer_provenance=not_applicable`。
- 普通无答案：来源问题“为什么 0! 等于 1？”；生成答案完整登记，`answer_provenance=generated`。
- general_learning：一张 `concept_transfer` 与一张 `procedure`；问题措辞为 generated，答案/步骤逐字来自 source。

## 最终 append-only Canary

| fixture | job | 状态 | receipt SHA-256 |
|---|---|---|---|
| English | `20260826T020922330Z-offline-language-en-replay-2fed3bc9` | ready | `87D554ABFB9639074ABBB1D00BBB2CEE87A72834FBE85DF2915B8FF0DACA5FAF` |
| 无答案 | `20260826T020923413Z-offline-no-answer-replay-cf7e905f` | ready | `9B06C2F2C0232C29C07999674B030D280C0FE6FD3D53BD758409B5F1642DD5C1` |
| general_learning | `20260826T020924499Z-offline-general-learning-replay-ad050731` | ready | `C41F0B7BC938BD6551588C93CA0F030FAF5CE34A8C5702C5E07489851AD22F35` |
| 结构清洗 | `20260826T020925584Z-offline-structured-cleaning-replay-31eb0a16` | ready | `6F45F27BF027CBA59D9D77190AA74C17D2D32CC5F3027D7431A21203A59F41EC` |
| 重复清洗 | `20260826T020926658Z-offline-duplicate-cleaning-replay-19107c6d` | ready | `947F60CB563AA8AF9BE693941A391F413BA330AD7EE30A504367C49B7EF55959` |
| 局部异常 | `20260826T020927755Z-offline-partial-format-anomaly-replay-b191a743` | partial | `FACEB993C97013B70DAB8C0409A8D8DDF457588A7F470E98955EB091DB7CFE91` |

## 真实 DeepSeek 证据边界

本轮未再次调用 DeepSeek。此前不可变真实 API job 仍保留：

- `20260826T014730219Z-offline-no-answer-live-0a78331f`
- `20260826T014734652Z-offline-general-learning-live-d8b40913`
- `20260826T014742460Z-offline-language-en-live-a0479adf`

它们证明真实模型能产出既定合约形状；上表六个 replay job 是绑定当前最终代码与开放 Prompt 的确定性证据。

## 剩余真实边界

本模块范围内已确认、无需外部授权的清洗/理解/制卡 backlog 已完成。仍待总控或下游的是 authority registry、新版本 release checkpoint、candidate sealing、exporter、桌面端与 RemNote/Anki E2E；这些不扩入本模块。
