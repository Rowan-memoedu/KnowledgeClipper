# 阶段：语言书写线索审查

只审查并结构化输出目标词的读音与当前语境义，不生成卡片，不改写原文。

返回严格 JSON：

```json
{
  "pronunciation": {
    "value": "可靠时填写读音",
    "reliability": "reliable|missing|unreliable",
    "evidence": "简短依据"
  },
  "meaning": {
    "value": "可靠时填写当前语境义",
    "reliability": "reliable|missing|unreliable",
    "evidence": "简短依据"
  },
  "unresolved_reasons": []
}
```

- 只有确有把握时才使用 `reliable`；不得为了生成卡片猜测读音或释义。
- `missing` 或 `unreliable` 时可以省略 `value`，并必须在 `unresolved_reasons` 中说明。
- 不得返回卡片、改写后的例句、目标路由或其他额外字段。
