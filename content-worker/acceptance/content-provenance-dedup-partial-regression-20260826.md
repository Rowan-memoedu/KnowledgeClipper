# 内容溯源、重复清洗与局部格式异常回归

生成时间：2026-08-26 02:01 UTC  
Worker：`content.deepseek` / `0.2.0`  
本批外部 API 调用：0

## 本批结论

- 完整测试 41/41 PASS；包括 common `describe/run`、append-only attempt、清洗、分类、制卡、取消与 secret fail-closed。
- 六组标准 replay Canary 均由原始 fixture 通过公共 worker 入口重新生成，公共 envelope、Artifact manifest、LearningBundle/CardBundle schema 全部 PASS。
- `format_anomaly.requires_human` 只冻结引用异常 segment 的 unit；同一 item 的干净 unit 继续产卡，Bundle 状态为 `partial`。
- 重复清洗只能删除完整重复 segment，且至少保留一个完整副本；不能借 `duplicate` 删除重复词语或全部副本。
- 卡片门禁要求：generated block 与 `generated_content[].text` 完全相等且双向有用；source block 只能逐字引用来源，或仅增加成对非空强调；禁止模型伪称 `human_confirmed`、generated asset、正反面相同及完全重复卡片。
- 分类门禁要求 unit 内 segment 索引唯一，语言 `target_term` 必须逐字来自该 unit 来源。

## 可读输入输出

以下六组均为离线 replay fixture，不是本批真实 API 输出。

### 1. 英语词语学习

原始文本：

> The committee reached a tentative conclusion after reviewing the evidence.

LearningBundle：`language_en` / `unit-tentative` / `ready`；生成内容仅登记实际使用的完整背面：

> tentative /ˈtentətɪv/：暂定的；尚未最终确定的。

CardBundle：`language_context` / `ready`

- 正面（source）：`The committee reached a **tentative** conclusion after reviewing the evidence.`
- 背面（generated）：`tentative /ˈtentətɪv/：暂定的；尚未最终确定的。`
- `answer_provenance=not_applicable`

强调标记只有成对、非空且剥除后可逐字定位到原文时才被接受。

### 2. 普通问题无来源答案

原始文本：

> 为什么 0! 等于 1？

LearningBundle：`question_answer` / `answer_state=missing` / `ready`；生成答案：

> 因为空排列只有一种，且递推关系 n!=n×(n-1)! 在 n=1 时要求 0!=1。

CardBundle：`basic_qa` / `answer_provenance=generated`；正面逐字来自来源，背面与一个 `generated_content` 条目完全相等。

### 3. general_learning 扩展模板

原始文本 A：

> 负迁移是已掌握的旧知识干扰新知识学习；当两个情境表面相似但规则不同时，先比较适用条件可以减少误用。

输出卡：`concept_transfer`

- 正面（generated）：`遇到表面相似但规则不同的新情境时，如何减少负迁移？`
- 背面（source）：`当两个情境表面相似但规则不同时，先比较适用条件可以减少误用。`

原始文本 B：

> 解一元一次方程时，先去括号，再移项并合并同类项，最后把未知数的系数化为 1。每一步都要保持等式两边同作相同变换。

输出卡：`procedure`

- 正面（generated）：`解一元一次方程的操作顺序和不变原则是什么？`
- 背面（source）：完整保留来源步骤与原则。

两张卡均为 `answer_provenance=source`。

### 4. 复制/分享/版权噪声与结构保真

原始文本含 Markdown 标题、编号列表、代码块、双美元公式，末尾另含：

```text
复制
分享
Copyright Example 2026
```

清洗结果只删除上述两段噪声；保留标题、列表换行、代码块缩进与 `$$x + 2 = 5$$`。LearningBundle 记录两次精确删除及来源 span；CardBundle 的 `procedure` 背面仍含原则、列表、代码和公式四个 source block。

### 5. 完整重复 segment

原始文本：

```text
课程页眉

课程页眉

核心结论：重复清洗必须至少保留一个完整副本。
```

清洗结果：

```text
课程页眉

核心结论：重复清洗必须至少保留一个完整副本。
```

删除理由为 `duplicate`，只删除 segment 0 的完整副本并保留 segment 1；最终生成一张 `basic_qa`，背面逐字引用核心结论。

### 6. 局部格式异常的部分发布

原始文本：

```text
地球绕太阳公转。

公式草稿：$x + = 5$
```

LearningBundle：`release_state=partial`

- `unit-clean-fact`：`ready`，生成问题“地球绕什么天体公转？”
- `unit-broken-formula`：`unresolved`，理由 `format_anomaly_requires_human`
- 清洗不改写错误公式；异常证据为“公式在加号后缺少操作数”。

CardBundle：`release_state=partial`

- 放行卡：正面（generated）“地球绕什么天体公转？”，背面（source）“地球绕太阳公转。”
- `withheld_units`：仅 `unit-broken-formula`

这证明人工判断状态按受影响 unit 隔离，不再把同一 item 的无关干净内容全部拦截。

## 最终 Canary 证据

| fixture | job | 状态 | receipt SHA-256 |
|---|---|---|---|
| English | `20260826T015914110Z-offline-language-en-replay-5a9423e6` | ready | `CA484AA8C5E6E21AC11FC5A8CA172BA9383767A343E34966C3C17F4963678E96` |
| 无答案 | `20260826T015915383Z-offline-no-answer-replay-0e857bc7` | ready | `39B69D97BB461EC0A3F1F49F05142E7B0B763ED00C66E1E7DFF16C3A06648E7F` |
| general_learning | `20260826T015916632Z-offline-general-learning-replay-8649a71c` | ready | `151097CCE055F9CBDF6620543D8AA016FB45FECDA5924BFF89EF8954FC678284` |
| 结构清洗 | `20260826T015917750Z-offline-structured-cleaning-replay-794a891f` | ready | `AF687DFB2AC3CE415C49B174F68B1924BEC42297ED6D8CA504C74A871871DF8F` |
| 重复清洗 | `20260826T015918865Z-offline-duplicate-cleaning-replay-88cec386` | ready | `BD3C56D89573C4A48B0C33053F7481E68BB8CE4A0D4ADE9750969E793063497D` |
| 局部异常 | `20260826T015919943Z-offline-partial-format-anomaly-replay-57d53de3` | partial | `F52125990044F97BA60E8AA2D0D519856E83F7CF4D87F8E58000102494D9AC6A` |

六个 job 均通过 `test/validate_canary.py`：describe=1、request=1、result=1、input Artifact=1、output Artifact=2、module bundle=2。

projection fixture 继续覆盖四个类别、五个模板和三种 block，`learning-bundle/1.0.0`、`card-bundle/1.0.0` 验证 PASS；本批未修改 projection fixture。

stable checkpoint `contracts-v1-20260825T115938Z` 复算 21 files checked、0 mismatch；receipt SHA-256 仍为 `1CB738FE133F6645B9757E246E2FBC210785086342F70BAD32B5015BD19E1B1E`。

## 真实 DeepSeek 证据边界

本批遵守要求，没有再次调用 DeepSeek。此前真实 API Canary 仍保留为独立、不可变证据：

- 无答案：`canary/jobs/20260826T014730219Z-offline-no-answer-live-0a78331f/`，ready。
- general_learning：`canary/jobs/20260826T014734652Z-offline-general-learning-live-d8b40913/`，两张卡 ready。
- English：`canary/jobs/20260826T014742460Z-offline-language-en-live-a0479adf/`，ready。

这些 live job 证明真实模型可生成合约形状；本报告六个最终 replay job 才是绑定本批新增门禁与 Prompt 的确定性回归证据。

## 仍属总控/下游的边界

- authority registry、新 release checkpoint 与候选发布不由本模块决定。
- exporter、RemNote/Anki 写入和桌面 E2E 不在本模块范围。
- `contracts/v1` 未修改；如未来需要不兼容 schema 变化，必须新版本并回总控裁决。
