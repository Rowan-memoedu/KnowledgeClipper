# 摘录制卡提示词接口

语言与摘录是独立入口。原文卡和手动模式不调用 AI；这里仅定义用户主动选择的摘录提示词模式。当前 `excerpt-modes.json` 为空，提示词正文与效果测试由后续发布补齐。

每次调用只返回一张卡，JSON 字段由工程维护的 `excerpt-card.schema.json` 校验：

| 字段 | 类型 | 约定 |
|---|---|---|
| `front` | string | 正面，不能是空白；使用纯文本，程序会转义 HTML |
| `back` | string | 反面，可以为空；挖空卡中表示额外说明，答案由 Anki 还原 |
| `cardType` | string | `basic` 或 `cloze` |

不返回数组、Markdown 代码围栏或其他字段。`cloze` 的 `front` 至少包含一处 `{{c1::文字}}`，所有空使用 `c1`，因为同一模式每次只生成一张卡；可用 `{{c1::文字::提示}}`，不支持嵌套空。`basic` 的反面原样独立显示。

输入 JSON 为 `selectedText`（不可变的原始摘录）、`contextText`（可为空）、`sourceTitle`。每个模式都收到原始摘录，不会收到其他模式生成的卡片，不使用分类提示词。

在 `excerpt-modes.json` 的 `modes` 数组中登记一个模式，例如以下配置结构；示例文件名并不代表该提示词已发布：

```json
{
  "id": "excerpt-summary",
  "name": "摘要制卡",
  "enabled": true,
  "description": "根据摘录生成一张摘要卡",
  "promptFile": "excerpt-summary.md",
  "schemaFile": "excerpt-card.schema.json",
  "outputKind": "excerpt",
  "requiresContext": false,
  "ankiDeckPath": "摘录::摘要"
}
```

`id` 必须唯一，`excerpt-manual` 留给内置手动模式。提示词路径必须位于配置目录内，禁止绝对路径或 `..`。`ankiDeckPath` 可省略，继承设置中的“摘录子牌组”；填入时是根牌组下的相对路径。

设置 → Anki 导入 → 摘录提示词目录，可指定 `D:\Projects\RemNote Quick Clipper\modes`，在摘录页点击“刷新模式”即可载入发布内容，无需重启。留空时使用程序旁的 `modes`。应用数据目录 `D:\AppData\KnowledgeClipper\modes` 中的同名文件优先。

可以同时勾选多个模式，每个模式每次各生成一张独立卡，原文卡一直保留。重复主动制卡会得到新草稿；导入则复用已确认的卡片身份，逐卡或批量跳过已导入项。某个模式失败时，其他成功卡保留，可只勾选失败模式重试。

普通摘录提示词注册表当前仍为空。OCR 清洗、原图解答和内容审核资源已单独发布，见 [图片流程字段](image-output-fields.md)；它们不是返回 `front/back/cardType` 的摘录模式，不能直接加入本注册表。图片消费流程由工程继续完成。
