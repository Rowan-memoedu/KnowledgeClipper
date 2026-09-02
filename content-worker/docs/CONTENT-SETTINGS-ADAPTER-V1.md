# Content 设置 Adapter v1

## 边界

- Adapter 版本：`content-settings-adapter/1.0.0`。
- 可变资源工件：`content-config-snapshot/1.0.0`，common artifact kind 为 `content-config-snapshot`。
- Additive 运行策略工件：`content-config-snapshot/1.1.0`。它沿用同一个 common artifact kind/contract，不修改冻结公共 schema；新增 `runtimePolicy`，只容纳 worker 实际消费且经 SHA 绑定的字段。
- 模块 companion：`content-companion-descriptor/1.0.0`；项目设置：`content-companion-settings/1.0.0`。
- 内置 `open-md-v2` Prompt 与 `category-registry/2.0.0` 始终只读。用户编辑流程必须先复制到新目录。
- API Key 等凭据只由控制面保存到本地秘密存储或用户环境，并以既有环境发现/进程注入交给 worker；不得写入草稿、任务包、日志、回执或 fixture。
- Content 不拥有日期、路由或目标端设置。

## CLI

```text
KnowledgeClipper.Content.Worker.exe settings describe --json
KnowledgeClipper.Content.Worker.exe settings companion --json
KnowledgeClipper.Content.Worker.exe settings profile validate --path <absolute-json> --json
KnowledgeClipper.Content.Worker.exe settings copy --target <absolute-directory> --json
KnowledgeClipper.Content.Worker.exe settings validate --root <absolute-directory> --json
KnowledgeClipper.Content.Worker.exe settings diff --root <absolute-directory> --json
```

`copy` 只写入一个尚不存在的目标目录，复制开放 Markdown Prompt、分类 registry 和不含凭据的 draft metadata。`validate` 与 `diff` 只读，返回每个文件的 SHA-256、bytes、资源组合 SHA、变更状态和警告。

## 运行时资源选择

旧请求未带配置工件时继续使用当前 release 内置资源。新请求可在 `inputRefs` 中加入一个 `content-config-snapshot` artifact，并在 `payload.contentConfigPath` 指向该工件声明的入口文件；`configSnapshot.contentSettingsRevisionId` 必须与入口的 `settingsRevisionId` 完全一致。

worker 会验证 artifact、入口、每个 Prompt、category registry 的路径、bytes、SHA 与组合 SHA，然后只从该不可变任务快照加载资源。发布后的 GUI 修改必须创建新 revision，只影响新任务；重试继续绑定原 revision。

`1.1.0` 的 `runtimePolicy.languagePracticeDefault` 支持 `ask|meaning-reading|writing|both`；`runtimePolicy.provider.model` 只接受 `deepseek-chat|deepseek-reasoner`，`maxRetries` 只接受 1–5。它们会进入 content-config 文件、common artifact 文件清单和 WorkerRequest 的 `inputRefs`/`contentSettingsRevisionId` 双重绑定，并在 WorkerResult/LearningBundle 回显 profile id/version/SHA。未知字段、越界值与任何凭据均拒绝。

`interaction.promptAfterRecognition` 能在 companion 项目设置中表示，但状态固定为 `conditional-core-adapter`：它由 Core/UI 决定是否弹出选择并在新 attempt 写入 `human_overrides.languagePracticeMode`，Content worker 不消费该字段，因此它不会被伪装进运行时 snapshot。没有 Core 接线时，worker 只执行 snapshot 中的默认选择；默认 `ask` 继续发布不可变 decision artifact。

## 现有参数映射

- DeepSeek provider 固定只读；`1.1.0` 可把 model 与最大重试次数绑定到任务快照，base URL 仍为控制面本地条件项且不得进入任务包。
- temperature、max tokens、请求超时和清洗门禁阈值当前只读或 unsupported。
- 分类类别人工覆盖、模板选择和问答/风险人工判断继续写入版本化 StandardInput。
- 语言制卡沿用 `meaning-reading`（含义与读音）、`writing`（练习书写）、`both`（先含义与读音、后练习书写，恰好两张）。缺少可靠 target term、读音或意思时保持 decision/unresolved。
- 非语言分类不触发语言三选一。书写卡只替换唯一 target term 为 `（读音；意思）`，背面保持 target term 原文；歧义、读音/释义不可靠时 fail-closed。

## 安全

Prompt 用户副本属于受信任的可执行配置。静态校验会拒绝路径穿越、空文件、超限文件、控制字符、明显凭据模式和引用缺失，并对典型指令优先级改写措辞发出人工审查警告；静态校验不能证明 Prompt 语义安全，发布前仍需 diff 审查和离线 replay。
