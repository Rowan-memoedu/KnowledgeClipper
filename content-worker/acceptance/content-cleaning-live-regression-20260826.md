# 内容清洗与结构保真：真实 DeepSeek + 离线回归（2026-08-26）

本报告是 `content.deepseek` 当前 Prompt 集的最终模块证据，补充并取代早先仅含离线结果的 `content-cleaning-structure-regression-20260826.md` 作为最新验收入口。所有 job 均从原始 fixture 新建，不覆盖旧 attempt；没有人工修改任何生成 Bundle。

## 1. 结构化正文与复制/版权噪声

原始文本：

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

### 真实 DeepSeek API 输出

清洗后的 `cleaned_text`：

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

模型将末尾 `复制\n分享\nCopyright Example 2026\n` 作为一个 `site_footer` 逐字删除。本地门禁确认该片段由独立分享控件行与版权尾注行组成，允许合并删除；正文段落、列表换行、代码块、缩进、公式定界符和内部空白全部保持。

LearningBundle：

- `release_state=ready`
- `category_id=general_learning`
- 分类覆盖保留的 `segment_index 0..4`；标题、原则、步骤、代码和公式均未丢失。
- 清洗/分类/制卡共 3 次真实 `deepseek-chat` 调用，均记录 Prompt 和响应 SHA。

CardBundle：

- `template=procedure`
- 正面（`generated`）：`求解一元一次方程时，应遵循哪些步骤？`
- 背面（`source`）：

```text
1. 去括号
2. 移项并合并同类项
3. 把未知数的系数化为 1
```

这张 live 卡选择了精确的三步正文作为背面；原则、代码示例和公式核验仍完整保存在 LearningBundle 的来源分段与 `cleaned_text`，没有被清洗或分类丢弃。

最终 live job：

- `canary/jobs/20260825T233924768Z-offline-structured-cleaning-live-9d9f3b51`
- receipt：3,231 bytes；SHA-256 `2DD5BCF5FC50F655837FD0D7709644ED0FA57EA12606C65083A91FF075614F64`
- LearningBundle：11,283 bytes；SHA-256 `0C012BDFEC09A993AAB560B218B8E98970AA74669FF094454B66242375145CB7`
- CardBundle：1,951 bytes；SHA-256 `D78801EE9E9E9E77D67B97F81BF75D3AC500CA15806CEBA207F9FBD3B6ECA5BD`
- common status `completed`；module release `ready`；Schema PASS。

### 最终离线 replay 输出

同一原始输入的确定性 fixture 将噪声拆为 `sharing_prompt` 与 `site_footer` 两条删除记录；生成同为 `procedure` 的卡片，背面保留原则、三步列表、代码块和公式四个 `source` block。

- job：`canary/jobs/20260825T234005348Z-offline-structured-cleaning-replay-29cb1b23`
- receipt SHA-256：`78C48C4C13DB0943A7BD8668411B08691A9957663F404D26DBF6590E22772656`
- LearningBundle SHA-256：`DAF17EB67FA7D048BB9DB057D24180B44109C797F467E6C5D2FB62C42BAC9595`
- CardBundle SHA-256：`67EFC6EE319D37763F041B6BE1F5756C718FDAC35964FC0797AB6E9E6E44CF7F`

两种结果的删除分组和卡片详略不同，但都满足同一来源、结构和 provenance 门禁。

## 2. 表格与公式格式异常

原始文本：

```markdown
计算记录：

| 步骤 | 结果
| 去括号 | x + 2 = 5

公式：$$x + = 5
```

### 真实 DeepSeek API 输出

`cleaned_text` 与原文相同，模型没有补表格、闭合数学定界符或猜测缺失变量；报告：

- `broken_table / requires_human`
- `broken_formula / requires_human`

最终状态：common `completed`，module `unresolved`，`unresolved_reasons=["format_anomaly_requires_human"]`。只发布 LearningBundle，不进入制卡阶段，不发布 CardBundle。

最终 live job：

- `canary/jobs/20260825T233948830Z-offline-format-anomaly-live-fad3486a`
- receipt：2,574 bytes；SHA-256 `35BFBB8D2410AB8D94D461AE86FAA45E31702ADFDD9790770DDCA1DD4CE93FD3`
- LearningBundle：8,244 bytes；SHA-256 `23D490A0E95609478DC1182A00597E440154B48C81D0CAD6F25C556D4DC53734`
- Schema PASS。

最终 replay job：

- `canary/jobs/20260825T234005585Z-offline-format-anomaly-replay-b2a3001a`
- receipt SHA-256：`CAB5A5EA39E2EFABD6E7100F88AF18B14F698C3D9001F6F8FFE7216BD8279D77`
- LearningBundle SHA-256：`1A62BFE21CD778EE76ADF1931D8518243C0460EEC3B148FC9460B1A92988B3E1`
- 同样保持 `unresolved` 且无 CardBundle；Schema PASS。

## 3. 真实失败驱动的 Prompt 迭代

结构化 live 样例没有一次性伪装为通过：

1. 首轮分类生成了来源不存在的 `question`，被 `classification.source-question` 拒绝。
2. 修订后模型省略必填 `unresolved_reasons`，被严格 AI contract 拒绝。
3. 再修订后遗漏标题和公式分段，被 `classification.coverage` 拒绝。
4. 分类 Prompt 加入抽取/生成边界、必填字段清单和输出前覆盖集合核对后，从同一原始输入重跑通过。

失败 job 与输出均追加保留；未降低门禁，未硬编码当前样例答案，未手改生成 Bundle。

## 4. 最终验证

- `npm test`：28/28 PASS。
- 结构破坏反例：Markdown 列表压平、代码换行/缩进损坏均被拒绝。
- 防误删反例：`DNA 复制`、`分享经济`、`Copyright 法律保护作品表达` 均不可作为控件/尾注删除。
- 最终 2 个 live + 2 个 replay job：公共 describe/request/result/artifact 与模块 content-input/LearningBundle/CardBundle JSON Schema 全部 PASS。
- stable checkpoint `contracts-v1-20260825T115938Z`：21 files checked，0 mismatch；receipt SHA-256 仍为 `1CB738FE133F6645B9757E246E2FBC210785086342F70BAD32B5015BD19E1B1E`。
