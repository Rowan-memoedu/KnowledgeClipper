# 内容清洗、结构保真与制卡回归（2026-08-26）

本报告只验收 `content.deepseek` 模块。两组结果均为从原始 fixture 创建新 job 后运行的**离线 replay**，不是本轮真实 DeepSeek API 输出；不涉及 OCR、桌面端、RemNote、Anki 或 exporter。

## 样例一：结构化正文、复制/分享控件和版权尾注

原始输入：`fixtures/offline-structured-cleaning/materials/source.md`

````markdown
# 一元一次方程示例

求解原则：等式两边同作相同变换。

1. 去括号
2. 移项并合并同类项
3. 把未知数的系数化为 1

```text
x + 2 = 5
x = 3
```

公式核验：$$x + 2 = 5$$

复制
分享
Copyright Example 2026
````

LearningBundle 清洗结果：

````markdown
# 一元一次方程示例

求解原则：等式两边同作相同变换。

1. 去括号
2. 移项并合并同类项
3. 把未知数的系数化为 1

```text
x + 2 = 5
x = 3
```

公式核验：$$x + 2 = 5$$
````

删除记录：

- `复制\n分享\n`：`sharing_prompt`，证据为正文末尾的独立复制与分享控件文字。
- `Copyright Example 2026\n`：`site_footer`，证据为正文末尾的统一版权尾注。
- 删除占比 `0.203`；两个片段都逐字定位。
- `cleaning.structure-fidelity=pass`：段落、列表换行、代码块、代码缩进、数学定界符与内部空白均保持。

LearningBundle 关键状态：

- `release_state=ready`
- `category_id=general_learning`
- `subtype=worked_procedure`
- `answer_state=provided`
- `source_segment_ids` 覆盖 5 个保留正文分段，不包含已清空的噪声分段。

CardBundle 最终卡片：

- 模板：`procedure`
- 正面（`generated`）：`解一元一次方程时，应遵守什么原则并按哪些步骤操作？`
- 背面（全部 `source`）：
  1. `求解原则：等式两边同作相同变换。`
  2. 原始三行有序步骤
  3. 原始 `text` 代码块
  4. `公式核验：$$x + 2 = 5$$`
- `answer_provenance=source`

最终 Canary：

- job：`canary/jobs/20260825T233348614Z-offline-structured-cleaning-replay-a07c9b59`
- receipt SHA-256：`2BF4DE520C46BE40758563A7F561E8E5CF865F74C2A8FA60E86012E8EC6BDE88`
- LearningBundle：12,310 bytes，SHA-256 `90A51E9D4F84E81E7BFBB83F7FAC51545276F3B8DF244F11E513F80AD380F841`
- CardBundle：2,562 bytes，SHA-256 `96C39E939F6D95A3685F185FDAF631DE12D57558CF8B63C45D927658C15CF5FF`
- common result：`completed`；module release：`ready`；Schema 校验：PASS。

## 样例二：表格和公式格式异常

原始输入：`fixtures/offline-format-anomaly/materials/source.md`

```markdown
计算记录：

| 步骤 | 结果
| 去括号 | x + 2 = 5

公式：$$x + = 5
```

LearningBundle 的 `cleaned_text` 与上述原文相同，没有尝试补表格、闭合公式或猜测缺失内容。异常记录：

- `broken_table / requires_human`：表格缺少表头分隔行且两行均缺少闭合列分隔符。
- `broken_formula / requires_human`：只有起始 `$$`，且表达式在加号后缺少操作数。

最终状态：

- common result：`completed`，因为 worker 正常完成并发布了可追溯结果。
- module `release_state=unresolved`
- `cleaning.state=unresolved`
- unit `unresolved_reasons=["format_anomaly_requires_human"]`
- 只发布 LearningBundle；不调用制卡阶段，不发布 CardBundle。

最终 Canary：

- job：`canary/jobs/20260825T233349648Z-offline-format-anomaly-replay-044e1628`
- receipt SHA-256：`400FC28A4842B1DCEAA7D8C352616FAB7ED195D72E53B2BAA7613F75363F2C1C`
- LearningBundle：8,138 bytes，SHA-256 `6A306DAFB355315AFBE032B47A66FBC73A548B7888B5B5371FBE79EE1902951C`
- common result：`completed`；module release：`unresolved`；Schema 校验：PASS。

## 反例与完整回归

新增门禁会拒绝以下行为：

- 保留字符相同但压平 Markdown 列表换行。
- 保留字符相同但破坏代码块换行或缩进。
- 把正文中的 `DNA 复制` 误判为控件。
- 把 `分享经济` 误判为分享栏。
- 把 `Copyright 法律保护作品表达` 误判为站点尾注。

噪声片段必须是独立行形态；仅出现关键词不足以删除。完整 `npm test`：27/27 PASS。两组最终 Canary 均通过公共 describe/request/result/artifact 与模块 content-input/LearningBundle/CardBundle JSON Schema 验证。

冻结依赖复算：stable checkpoint `contracts-v1-20260825T115938Z` 的 21 个文件全部匹配，0 mismatch；receipt SHA-256 仍为 `1CB738FE133F6645B9757E246E2FBC210785086342F70BAD32B5015BD19E1B1E`。
