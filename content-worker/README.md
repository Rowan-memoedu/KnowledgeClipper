# 零碎知识剪藏：内容理解与制卡 Worker

ASCII 产品根名为 `KnowledgeClipper`。独立 worker `content.deepseek` 只理解普通文本/对话或传递已配对媒体引用，生成版本化 `LearningBundle` 与 `CardBundle`。它不执行捕获、OCR、TTS、UI 或任何下游目标导出；所有目标适配器均在本模块之后并列消费其结果。

当前开发候选 npm 包名为 `knowledge-clipper-content-worker`，模块版本为 `0.7.0`。公共业务 `workerId=content.deepseek` 保持稳定；既有 `0.5.x` 与 `0.6.x` authority/release 不改写。

`0.6.0` 增加模块级轻量设置 adapter、只读内置 Prompt 的用户副本/校验/diff，以及可选不可变 `content-config-snapshot/1.0.0` 运行绑定。控制面接线边界见 `docs/CONTENT-SETTINGS-ADAPTER-V1.md`。

`0.6.1` 修复卡片 Prompt 末尾空 `generated_content` 示例与严格门禁的冲突：保持 gate 不变，要求每个 generated block 在序列化前与 `generated_content` 完整文本一一对应。

`0.7.0` 新增独立 `content-companion-descriptor/1.0.0` 与 `content-companion-settings/1.0.0`，并通过 append-only `content-config-snapshot/1.1.0` 将语言默认选择、Prompt/profile 身份和受限 DeepSeek model/maxRetries 绑定到正式 WorkerRequest。`interaction.promptAfterRecognition` 由 Core/UI 所有，descriptor 明确标为 conditional，worker 不伪消费。

`LearningBundle` / `CardBundle` 的当前权威字段语义见 `docs/DESTINATION-NEUTRAL-ARTIFACT-SEMANTICS.md`。历史 projection v1 说明只作为已回执固定的 downstream compatibility record 保留，不再代表产品或内容 artifact 权威。

## 公共入口与 authority

公共入口严格使用 stable checkpoint `contracts-v1-20260825T115938Z`：

```powershell
node .\dist\src\cli.js describe --json
node .\dist\src\cli.js run --request <absolute-existing-request.json>
```

`run` 只接受 `contracts/v1/schemas/common/worker-request.schema.json`。输入由 `inputRefs` 引用一个公共 `content-input` Artifact；`payload.contentInputPath` 指向该 Artifact 中的模块局部 `standard-input/1.0.0`、`standard-input/2.0.0` 或 `standard-input/3.0.0` JSON。成功产物通过公共 Artifact manifest 发布为 `learning-bundle`、`card-bundle`、`language-practice-decision` 或 `question.answer`，终态写入公共 `worker-result`。公共 v1 文件只读，不在本目录复制或改写。

`schemas/content-input.v1.schema.json`、`content-input.v2.schema.json`、`content-input.v3.schema.json`、`learning-bundle.v1.schema.json`、`card-bundle.v1.schema.json`、`language-practice-decision.v1.schema.json` 与 `question-answer.v1.schema.json` 只定义模块 Artifact 内部文件，不是另一套 common schema。早期 TaskManifest 已降级为 `schemas/legacy-task-manifest.v0.schema.json`，正式 `describe/run` 运行时不读取；旧 fixture 仅用于迁移回归。

输出 Artifact metadata 使用产品标识 `KnowledgeClipper` / “零碎知识剪藏”，并显式声明 `destinationNeutral=true`。LearningBundle/CardBundle 不含目标应用、目标目录、牌组、文档、标签映射或写入动作；下游目标配置不得反向进入内容理解 Prompt 或 Bundle。

## 开放扩展点

- `prompts/**/*.md`：系统、阶段和类别 Prompt 均为开放 Markdown；运行时记录文件 SHA-256。
- `config/categories.v2.json`：当前开放类别注册表；v1 保持历史回归。新增唯一类别 ID、说明、Prompt 文件、模板及可选模板级 Prompt 即可扩充。
- `unresolved` 永久保持可追溯状态；人工判断通过新版本 content-input 的 `human_overrides` 回流并从原始输入重跑。

语言练习 v2 规范见 `docs/LANGUAGE-PRACTICE-V2.md`。现有 `language_context` Prompt 与语义保持为 `meaning-reading`；新 `language_writing` 使用独立开放 Prompt。操作级 `languagePracticeMode` 一次作用于全部语言 unit；缺少 mode 时发布 LearningBundle + decision artifact 且不发布 CardBundle，Core 再以新 input version 写入 `human_overrides.languagePracticeMode` 创建新 attempt。

显式文字提问工作流见 `docs/QUESTION-WORKFLOW-V3.md`。`question.answer` 只发布 LearningBundle + `question.answer`，`question.make-cards` 只在完整持久化问答链上发布 LearningBundle + CardBundle；旧普通 question→card 路径保持兼容。开放答案 Prompt 位于 `prompts/stages/question-answer.md`。

清洗门禁只允许逐字定位后删除噪声，不允许改写原文；内部段落、列表、代码、公式、换行与缩进必须保真。来源块、生成块、媒体引用分别校验 provenance；普通缺失答案可生成并标记 `generated`，高风险缺失答案进入 `needs_research`，多轮追问必须保留所有参与 turn 的 segment 引用。

## 追加式运行

- Request 指定的 `resultPath` 与 `outputPath` 必须尚不存在。
- Bundle、Artifact manifest 与 result 都按 attempt 追加；同一 request 重跑会拒绝覆盖。
- `user_stop`、`superseded`、`task_cancel` 映射 `cancelled`；`timeout` 映射可重试 `failed`；失败与取消不发布 output refs。
- DeepSeek 配置依次从当前进程、Windows 用户环境变量、`AI_PRODUCER_CONFIG_FILE` 或 `Documents\New project\DeepSeek API 配置.md` 发现，密钥不进入 Bundle、日志或回执。

## 构建、回归与 Canary

```powershell
npm test
npm run test:question-fixtures
npm run worker:describe
npm run canary:offline
npm run canary:english:offline
npm run canary:language-decision:offline
npm run canary:language-practice:offline
npm run canary:language-mixed-partial:offline
npm run canary:language-whitespace:offline
npm run canary:question-answer:offline
npm run canary:question-followup:offline
npm run canary:question-cards:offline
npm run canary:no-answer:offline
npm run canary:multiturn:offline
npm run canary:general:offline
npm run canary:structured-cleaning:offline
npm run canary:format-anomaly:offline
npm run canary:duplicate-cleaning:offline
npm run canary:partial-format:offline
npm run canary:needs-research:offline
npm run canary:human-pending:offline
npm run canary:human-confirmed:offline
npm run canary:english:live
npm run canary:general:live
```

Canary 每次在 `canary/jobs/<唯一 run>` 创建不可变 job；不会覆盖旧 attempt。`live-language-zh` 的既有通过回执保留，不因本次 common adapter 接入而重跑。

## 启动器迁移与 legacy compatibility

新权威程序集为 `launcher/KnowledgeClipper.Content.Worker.csproj`，输出 `KnowledgeClipper.Content.Worker.exe`；Node 路径覆盖变量为 `KNOWLEDGE_CLIPPER_NODE_PATH`。

`launcher/RemNoteQuickClipper.Content.Worker.csproj` 与 `RQC_NODE_PATH` 仅为历史 release 工具和已安装入口保留的 legacy compatibility alias，不是新产品身份。新 registry/release checkpoint 应迁移到 `KnowledgeClipper.Content.Worker.exe`；冻结契约、旧回执和旧 release 不改写。

模块级可读样例见 `acceptance/content-readable-samples-v1-20260825.md`，其中明确区分真实 DeepSeek API、离线 replay 与静态 projection 证据。

后续持续开发的 general_learning、needs_research、人工判断重跑和 provider 失败语义可观察结果见 `acceptance/content-worker-iteration-20260825T1556.md`。

general_learning 的真实 DeepSeek 失败→通用 Prompt 修订→原始输入重跑通过证据见 `acceptance/general-learning-live-canary-20260826.md`。

普通文本复制/版权噪声、结构保真、格式异常人工门禁与最终卡片的可读输入输出见 `acceptance/content-cleaning-structure-regression-20260826.md`。

绑定当前最终 Prompt 的真实 DeepSeek + 离线重跑、失败迭代和可读前后对照见 `acceptance/content-cleaning-live-regression-20260826.md`。

来源/生成内容溯源收紧、完整重复清洗、局部格式异常部分发布及六组最终 replay 输入输出见 `acceptance/content-provenance-dedup-partial-regression-20260826.md`。

上述检查点后的误删反例、混合状态门禁与绑定最终代码的 44/44 回归见 `acceptance/content-provenance-dedup-partial-final-20260826.md`；该报告 supersede 前一份检查点报告作为当前验收入口。
