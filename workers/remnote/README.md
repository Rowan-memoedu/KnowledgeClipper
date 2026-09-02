# RemNote Exporter

> Acceptance entrypoint note: `tests/RemNoteQuickClipper.RemNoteExporter.Tests/run-v124-authority-offline-live-canary.ps1` is a preserved, unexecuted, parse-failed v1.2.4 draft. It is retired and excluded from build, acceptance, release, and Core admission. The active Fake MCP/offline acceptance entrypoint is `run-v2-offline-canary.ps1`.

本模块是“零碎知识剪藏”（ASCII 根名 `KnowledgeClipper`）的可拔插末端导出目标，稳定业务身份为 `export.remnote`。它与 `export.anki` 并列；RemNote 不是产品主体，也不是上游内容模型的默认归宿。

## 边界

- 权威入口：`KnowledgeClipper.RemNoteExporter.Worker.exe describe --json` 与 `run --request <absolute-request.json>`。
- 输入仅为稳定、目标中立的 `learning.bundle` / `card.bundle` Artifact 引用。LearningBundle/CardBundle 不携带根 Rem ID、document 标题、sink、MCP URL 或其他 RemNote 路由字段。
- `content-to-remnote.v1` 只在 Core 已选择 `export.remnote` 后执行一次末端、显式、可失败的投影；源 Artifact 不被修改。无法无损投影时在发布派生 Artifact 前失败。
- RemNote 特征只允许存在于本 worker 的目标配置、`remnote.write-receipt` / `remnote.import-package`、本地 MCP gateway 和模块专用测试证据中。
- 本 worker 不调用 OCR、内容理解、TTS 或 Anki Exporter；移除 `export.remnote` 不改变上游 bundle，也不要求 Anki Exporter 协调。

`remnote-export-task.v1` 继续作为只读兼容合同：支持 `mcp-direct` 与 `markdown-zip`，不做日期节点。additive `remnote-export-task.v2` 明确拆分两条末端路线：摘录使用 `clip-daily-route`，在目标根下按 `Asia/Shanghai` 的 `yyyy-MM-dd` 当日节点写入；制卡使用 `card-export`，禁止日期路由。原图产品路线已锁定为仅导出 Anki，RemNote 对 original-media 为 `by-design-unsupported`；任何误路由都必须在外部写入前返回 `destination.original_media_unsupported`，不得丢图降级。历史 additive v3 media 合同只作为兼容与失败封闭证据保留，不再是 RemNote 产品 backlog。真实 RemNote 写入必须另行授权；默认验收使用状态化 Fake RemNote。

Core 只把项目级选择快照为 `WorkerRequest.payload`：v2 日期字段全部位于 `payload.route`（`kind`、`timeZone`、`localDate`、`dateFormat`）。`configSnapshot` 只承载部署路径/MCP URL，不承载或覆盖业务路由。模块设置控制面可用 `settings describe --json`、`settings companion --json` 与 `settings adapt --input <absolute-settings.json> --json`；它不接收 RemNote secret。

## 只读健康与对账

Worker 2.1.0 新增 `probe --request <absolute-probe-request.json> --json`。它只持有 `status`、`search`、`list`、`read` 四种 gateway 能力，单次工具调用固定超时 15 秒，结果固定声明 `externalWriteCount=0`。`status.connected=true` 只代表 Bridge 握手；后续任一工具请求超时必须输出 `state=degraded`、`admission=blocked`，不能输出绿色 healthy。

Worker 2.2.0 另提供供 Core/Desktop 展示的脱敏公共入口：`settings companion --version 2 --json` 与 `probe-public --request <absolute-probe-request.json> --json`。公共结果包含 worker/version、连接与 `acceptWriteOperations`、typed root binding、固定路线能力和原图能力缺口；不包含 Node 路径、CLI 路径、原始 MCP URL、stderr 或知识内容。目标根设置只对下一份不可变任务 snapshot 生效，不修改运行中任务。

Worker 2.3.0 additive 增加 `settings companion --version 3 --json` 与 `discover-target-public --request <absolute-discovery-request.json> --json`，解决用户不应手填 Rem ID 的设置流程。discovery request 只接受 `configured-destination` 下的 `rootTitle + expectedType=document + match=exact + uniqueness=required`，不接受 UI 提供的 `rootRemId`；worker 经 status/search/list/read 唯一发现后，才返回 `selection.resolvedRootRemId` 与 exact readback。只有 `state=healthy`、`admission=ready`、`resolution=unique`、`exactReadback=true`、`persistableBinding=true` 同时成立时，Core/Desktop 才可把该 binding 保存为下一任务设置。0 匹配、重名、15 秒超时、连接或权限异常均失败封闭，`externalWriteCount=0` 且禁止自动重试。既有 `probe-public` 继续负责已保存 ID 的后续核验。

`rootRemId` 是目标 authority；`rootTitle` 只是对该 ID 的 exact readback 期望。search 仅作诊断，最终必须 read 指定 ID 并核对实际标题与 document 类型，UI 文本不能替代回读绑定。`authorizationScope=isolated-canary` 时，标题“剪藏”会在任何外部调用前被拒绝；正式根不得用于 Canary。

`mode=reconcile` 还会先校验不可变文件及 SHA-256，形成 `settingsSnapshot → payload.v2 → WorkerRequest → WorkerResult → RemNote readback` 证据链。`unknown_external_state` 必须绑定 `retryable=false` 且没有 outputs；probe 始终输出 `automaticRetryAllowed=false`，只允许人工判断。

语言卡扩展同样只发生在末端投影：Exporter 不读取 `category_id`、`template`、`choice` 或 `target_term`，只接收 Core 已投影完成的独立 basic/forward 卡。`language_writing` 的正面必须把原句中唯一一次 `target_term` 逐字替换为全角 `（读音；意思）`，替换后正面不得再含 `target_term`；背面才逐字等于原 `target_term`。`both` 必须作为两张具有不同 cardId 的独立卡，按 `language_context`、`language_writing` 的输入顺序交付；Exporter 不合并、不重排、不自行判断类别或 choice。目标词缺失或出现多次、读音/意思无法从 LearningBundle 或其 SHA-256 绑定的人工确认输入无损验证时必须拒绝。真实 Content writing/both authority Canary 与 Core `profile-resolver.v2.1` 映射已通过离线消费者验收；Exporter 未执行 trim，且投影中不携带 template/category/choice/token/target-term/mode 控制字段。

## 身份迁移

- 新权威程序集与文件：`KnowledgeClipper.RemNoteExporter.Worker.exe`。
- 稳定 workerId 保持 `export.remnote`，公共 operation 保持 `remnote.export`。
- `RemNoteQuickClipper.RemNoteExporter.Worker.exe` 和 `RemNoteExporter.exe` 只作为明确的 legacy compatibility alias 保留，不得写入新的权威 registry checkpoint。

## 离线验收

```powershell
pwsh -File .\build-exporter.ps1
pwsh -File .\tests\RemNoteQuickClipper.RemNoteExporter.Tests\run-v2.3-offline-canary.ps1
python .\tests\RemNoteQuickClipper.RemNoteExporter.Tests\validate-additive-v2.py
KnowledgeClipper.RemNoteExporter.Worker.exe settings companion --json
KnowledgeClipper.RemNoteExporter.Worker.exe settings companion --version 3 --json
KnowledgeClipper.RemNoteExporter.Worker.exe discover-target-public --request C:\absolute\discovery-request.json --json
KnowledgeClipper.RemNoteExporter.Worker.exe probe --request C:\absolute\probe-request.json --json
pwsh -File .\tests\RemNoteQuickClipper.RemNoteExporter.Tests\run-content-adapter-compatibility.ps1
pwsh -File .\tests\RemNoteQuickClipper.RemNoteExporter.Tests\run-target-idempotency-canary.ps1
pwsh -File .\tests\RemNoteQuickClipper.RemNoteExporter.Tests\run-contract-canary.ps1
```
