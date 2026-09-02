# 零碎知识剪藏

`KnowledgeClipper` 是 Windows 上的置顶知识采集与处理工具。它从选区、剪贴板、截图或文字提问取得输入，经可组合的图像、内容、问答、制卡与语音模块处理，再按任务选择导出到 RemNote、Anki 等并列目标端。RemNote 不是产品身份，也不拥有上游流程。

## 交互模型

- 悬浮窗保留「上下文模式」「原图模式」两个模式按钮，以及「摘录」「朗读」「截图」三个操作按钮；底部文字框可直接输入问题。
- 状态、绿色勾和重播入口只占用悬浮窗底部状态条。只有内容审查异常、需要人工取舍时才打开确认窗口。
- 「摘录」先尝试复制当前选区；若没有得到有效文本，就处理最新剪贴板。普通文本与 OCR 文本都必须经过内容审查和清洗。
- 「朗读」提交内容处理任务，并由 `speech.edge` 使用 Edge 自然语音播放。自然结束后可直接从头重播；停止播放只取消当前 `speech.playback` attempt，不取消并行内容或导出阶段。
- 「截图」支持连续多图，并显式维护有序的题目图片组和答案图片组；答案组允许为空。
- 文字提问支持悬浮窗输入框和右键「提问…」入口。当前公共问答工作流只接受文本回合；图片问答仍是独立的待接能力。
- 识别为语言学习内容时，任务可选择「练习含义与读音」「练习书写」「双向提取练习」。每次操作的选择已有工作流；把默认策略纳入项目设置仍在做真实 adapter 接线。

## 图像模式

- `OCR`：明确授权把图片上传到 MinerU；输出结构化文本后再进入内容审查。
- `原图`：绝不调用 MinerU/OCR，保留不可变原图 artifact。图像 worker 的直通能力已存在，但 Core 到内容/导出端的完整媒体路线尚未全部接通；界面必须显示 conditional，不能把它报告为全链成功。
- 同一卡任一图片失败时整卡为 `partial`，禁止自动下游。worker 已能复用成功项并只重试失败项；Core 的自动后继 attempt 仍在接线。

## 设置控制面

设置窗口按采集、图像/OCR、内容与制卡、语音、导出、存储、开发诊断分区，并区分常用、进阶和只读诊断项。

- 工作流方案和模块设置保存到 `%LOCALAPPDATA%\KnowledgeClipper`。当前主要文件包括 `settings.json`、`workflow-settings.v2.json` 及其备份。
- DeepSeek 与 MinerU 凭据由 Windows DPAPI `CurrentUser` 保护后写入 `credentials.json`/`.bak`；任务包、manifest、settings snapshot、日志和回执不得包含明文凭据。启动内置 Orchestrator 时只通过进程环境注入。
- 设置优先级为：任务级覆盖 > 当前工作流方案 > 模块默认值。新任务会冻结 SHA-256 绑定的 settings snapshot；运行中的任务、重试和回放继续使用原 snapshot。
- authority checkpoint、registry、worker 入口、版本与哈希只读，不能通过普通设置改写。
- 只有存在真实 descriptor、校验器和 Core 映射的字段才允许编辑。缺少 adapter 的 provider 测试、retention、部分 retry/timeout、媒体导出等能力必须保持只读或 conditional。
- RemNote 与 Anki 是平级、可选的 destination，可为空、单选或在兼容 profile 下双选。启用目标不等于 worker 在 registry 中可用；每次任务仍需精确配置和 SHA 绑定。

## 当前集成基线

截至 2026-08-28，最新自动化基线为：

- authority `v1.2.11`，5 个 worker 的 registry admission 通过；
- Desktop DPAPI 集成候选：`releases/kc-i-dpapi-v1211-20260828T183000Z/win-x64`；
- 质量状态：`CONDITIONAL-V1.2.11-CANDIDATE-BOUND-A-D-DPAPI-PASS-B-C-PENDING`；
- 上述候选的 `userHandTestEntrypoint=false`，不能因为文件存在就当作正式用户测试入口。

已完成的真实隔离路径包括 Content→Anki，以及 Content→Anki→Speech 合成→物理播放→从头重播。真实 RemNote 路径和 MinerU 路径仍分别受下述本机前置约束。

## 真实外部测试边界

- RemNote 只通过本地 MCP/Automation Bridge 访问。Canary 只能写已授权的隔离根，禁止写正式「剪藏」目标。
- 当前 Bridge `status` 可显示 connected/write enabled，但隔离根 `read_note` 仍可能在约 15 秒超时；这种状态是 degraded/unresolved，不能自动重写或重试 `unknown_external_state`。
- Anki Canary 只能使用隔离 deck，不能修改用户现有牌组。
- MinerU Token 只从受保护凭据经当前子进程环境注入；禁止从 RemNote 查找 Token，也禁止把 Token 写入配置、任务包或回执。
- 物理扬声器、真实截图拖拽、外部应用选区、跨屏混合 DPI 和外部写入均是独立 Canary 边界，不能由单元测试替代。

## 保留与日志

- `retentionClass` 已进入任务契约，但状态感知的统一 RetentionService 尚未完成。正式实现必须保护 active、waiting_input、unknown_external_state、pinned 和 evidence 任务。
- 在统一服务完成前，不能承诺“自动保留 30 天后安全删除”，也不能仅按目录创建时间递归清理共享 jobs 根。
- 日志与诊断默认不得记录正文、剪贴板内容、API Key、Token 或未脱敏绝对私人路径。

## 构建与验收

各模块独立构建、独立运行契约夹具和 Canary；随后才由 authority release、Desktop candidate 和 Quality gate 做精确哈希绑定。模块 PASS 不等于完整 E2E 或可交付安装包。

根目录旧 `SmokeTest.exe` 路线会尝试外部服务和 RemNote 写入，不属于当前安全验收入口，不得在日常启动或正式「剪藏」目标上运行。请使用各模块 README、append-only receipt 和当前候选绑定脚本中声明的入口。

候选必须整目录使用，不能单独移动 EXE。尚无 sealed installer 或代码签名；只有 Quality 回执明确标记 `userHandTestEntrypoint=true` 后，才进入用户手动体验阶段。
