# 零碎知识剪藏

Windows 悬浮式知识采集工具：划词朗读、图文提问、多轮问答与组合制卡，统一导出到 Anki 或 RemNote。活动源码位于本仓库；用户草稿、凭据、模型回合和学习库数据不进入 Git。

## 当前交付状态

2026-10-16 核对的现役桌面包为 `D:/Artifacts/KnowledgeClipper/performance-20261016/candidate/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64`。实际进程、桌面快捷方式和 `Personal Tool - KnowledgeClipper` 计划任务应指向此包。RemNote 专用服务使用 `performance-20261016/remnote`，HTTP/WS/安装服务分别为 43111/43112/43113。路径是本机部署事实，其他机器按其安装配置核对。

- [性能优化与完整验收](D:/Artifacts/KnowledgeClipper/performance-20261016/validation.md)：功能不变；优化历史读取/排版、旧制卡记录重开和双端导入；19 组业务回归、18 类界面检查及双端真实导入/原生显示通过，切换前后 1,121 份既有数据与配置字节未变。
- [最新批注书面化修复与真实验收](D:/Artifacts/KnowledgeClipper/annotation-formalize-fix-20261015/validation.md)：缺失历史来源不再使辅助操作在发送前报错；保留保存的正文、双面批注及原来源编号。
- [Mermaid 双端原生显示验收](D:/Artifacts/KnowledgeClipper/mermaid-chain-20261015/validation.md)：聊天、预览、RemNote widget 与 Anki 离线矢量图链路共用源码。
- [问答日期目录与后台定位验收](D:/Artifacts/KnowledgeClipper/question-dated-import-20261015/validation.md)：保留类型精确绑定、当日目录、普通摘录顺序及空行规则，冻结旧路径不迁移。
- [全部发布与验收历史](docs/release-history.md)：只用于追溯，不作为当前运行路径或跨任务授权。

当前包包含各轮已经验收的聊天、历史、批注、撤销/重做、补充要求和双端导出修改。源码提交、远端集成、已部署和真实验收分别留痕；此前功能整合见 [收尾记录](D:/Artifacts/KnowledgeClipper/closeout-20261016/closeout.md)，本次性能版本以顶部验收及部署回执为准。

## 使用与设置

- 文字浮点“读 / 卡 / 摘 / 问”，图片浮点另有“图”；快捷模式与普通模式共用原稿、导入身份及失败反馈。
- 图文输入进入同一问答页。普通“卡”按问题类型使用 Codex 默认模型/推理；最右“问”按配置的 API 快捷问模型发送一次，不自动导出。
- 多轮消息可选入卡片正反面，保留原始 Markdown、公式、图片与 Mermaid。双面正文和批注支持独立或全部书面化、图转文字、补充图片、预览、撤销及重做。
- 问答历史按会话归组；导出子卡保留各自冻结身份。旧记录重开不自动生成或导入。
- Anki 背面保留完整正面及分割线，精确旧托管模板可升级，自定义模板不覆盖；RemNote 按类型/模式与日期组织，核验后后台打开页面并选中正文。
- 设置中的模型、语音、上下文和导出目标通过应用界面维护；内置规则在 `modes`，个人覆盖和运行数据在 `D:/AppData/KnowledgeClipper`。

先看[现行完整流程](docs/workflow-overview.md)，了解从采集、问答、制卡到双端导入、历史与恢复的全链路。完整用法、设置文件、连接恢复、导出合同见 [问答与组合制卡](docs/chat-workflow.md)。RemNote 产品集成使用[专用 host](remnote-host/README.md)及[独立插件](remnote-plugin/README.md)，不依赖 MCP。

## 构建与验证

需要 Windows、.NET 8 SDK、Node/npm、WebView2 Runtime。当前本机 .NET 位于 `D:/Applications/RemNoteQuickClipperRuntime/dotnet-sdk-8.0.424/dotnet.exe`。

```powershell
pwsh -NoProfile -File ./build.ps1 -BuildOutput D:/Artifacts/KnowledgeClipper/build -BuildCache D:/Caches/KnowledgeClipper/build
```

构建包含离线前端、Mermaid 组件及依赖许可；禁止覆盖正在运行的输出。输出入口是 `<BuildOutput>/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe`。Codex 登录运行时由 `tools/Install-CodexChatRuntime.ps1` 安装；凭据使用原生登录，不写入仓库。

测试先构建项目，再运行准确 DLL；Windows 长任务使用既有超时包装。常规回归在 `tests`，前端真实验收使用 `tools/Invoke-IsolatedDesktopTest.ps1` 的隔离桌面。host/plugin 各自运行 `npm test`；插件另运行 `npm run typecheck`。真实模型、Anki 与 RemNote 验收仅使用独立公开样本，保留原身份与失败回执，不以删除回执重跑未知请求。

## 数据与恢复边界

- 用户数据位于 `D:/AppData/KnowledgeClipper`，源码、缓存和产物分别位于 `D:/Projects`、`D:/Caches`、`D:/Artifacts`；误放的测试回执可恢复归档到 `D:/Quarantine`。
- `Unknown` 必须先核对原身份；导入不确定时不能重新添加或改为新来源。已经确认的目标、内容与日期不随设置或重试漂移。
- 更新沿用原计划任务、快捷方式与服务端口，切换前检查活动请求并保存基线、任务和回滚入口。当前正在使用的应用重启遵循用户的会话授权。
- 回滚只恢复已核对的程序/任务配置；保留新草稿、媒体及 journal，不删除远端卡片、不用旧版重发新结构。
- 旧包、失败证据和回滚材料保留用于复核；Git 只保存源码、提示词、依赖锁、测试和文档。

工程约束见 [AGENTS.md](AGENTS.md)；第三方许可在 [licenses](licenses/) 及构建生成的 notices 中。
