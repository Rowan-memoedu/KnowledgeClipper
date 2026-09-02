# 语言卡三选一质量样例

这是一份质量观察夹具，不是公共或业务 schema；模块最终实现与回执尚未绑定时，状态保持 `implementation-pending`。

输入可见文字：`他踌躇了片刻。`  
目标词：`踌躇`  
读音：`chóu chú`  
意思：`犹豫不决。`

## meaning-reading

- 正面：`他踌躇了片刻。`
- 背面：`踌躇；chóu chú；犹豫不决。`
- 要求：与既有卡正反面逐字等价。

## writing

- 正面：`他（chóu chú；犹豫不决。）了片刻。`
- 背面：`踌躇`
- 要求：正面逐字等于把来源中唯一的 `踌躇` 一次替换为全角 `（chóu chú；犹豫不决。）`；替换后正面不再含 `踌躇`；背面精确等于目标词。

## both

恰好两张：上述 meaning-reading 在前，writing 在后。

## Fail-closed

目标词出现两次、缺读音或缺意思时，不发布卡片；任务保持 `waiting_input`，内容保持 `unresolved`。候选验证仅允许 fake/offline/validate-only，不调用外部服务。

RemNote 与 Anki exporter 只能看到已投影卡片，不得看到 `choice`、`decisionKind`、`targetToken` 或 `binding`。
