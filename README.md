# 零碎知识剪藏

Codex 问答已通过 WakeWire 托管原生连接；接口、部署和后续开发入口见 [WakeWire 接入说明](docs/wakewire.md)。原模型、图文消息、流式回复、停止、分叉、界面和导出合同保持不变。

Windows 悬浮式知识采集工具：划词朗读、图文提问、多轮问答与组合制卡，统一导出到 Anki 或 RemNote。活动源码位于本仓库；用户草稿、凭据、模型回合和学习库数据不进入 Git。

## 当前交付状态

2026-10-19 核对的现役桌面包为 `D:/Artifacts/KnowledgeClipper/image-link-20261019/release-final/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64`。实际进程、桌面快捷方式和 `Personal Tool - KnowledgeClipper` 计划任务均已指向此包。复制明确的图片链接可显示现有图片浮点，静态 SVG 在本地转换；普通网址与原生剪贴板图片沿用原规则。此前表格、预览定位、整合制卡和 Tab 功能保留。RemNote 专用服务仍使用 `performance-20261016/remnote`，HTTP/WS/安装服务分别为 43111/43112/43113。路径是本机部署事实，其他机器按其安装配置核对。

- [图片链接与普通复制回归](D:/Artifacts/KnowledgeClipper/image-link-20261019/validation.md)：172 项剪贴板/浮点检查、140 项实际桌面流程回归与用户 SVG 实际下载转换通过；1,288 份既有数据与配置原字节保留。
- [表格预览与双端导出](D:/Artifacts/KnowledgeClipper/tables-20261018/validation.md)：五种宽度无单元格重叠，双端真实导入及回读、Anki 原生复习器、300 项聊天检查、17 组业务回归和 28 项界面回归通过；1,206 份既有数据与配置原字节保留。
- [重复点击预览修复](D:/Artifacts/KnowledgeClipper/preview-repeat-20261018/validation.md)：内容不变时每次点击均重新定位，28 项界面检查通过；卡片数据及撤销历史保持。
- [整合对话重试与智能多卡验收](D:/Artifacts/KnowledgeClipper/chat-integrate-20261016/validation.md)：空卡整合全部对话，有选材或手工稿时聚焦当前内容并参考跨 Tab 上下文；自动判断张数，支持补充要求指定数量。293 项 Chat、19 项整合界面、27 项 Tab、25 项综合界面及 18 组业务回归通过；真实 API/Codex 和 RemNote/Anki 多卡导出、丢回执恢复通过，1,137 份既有数据与配置字节未变。
- [Tab 关闭、撤回和拖拽验收](D:/Artifacts/KnowledgeClipper/tab-controls-20261016/validation.md)：249 项 Chat、27 项专项界面、18 组业务回归及综合聊天界面通过；原生鼠标拖拽和 Ctrl+Z、连续十轮焦点检查、关闭后来源保留、在途关闭及重开恢复均验证。
- [同页分叉 Tab 与共享卡片验收](D:/Artifacts/KnowledgeClipper/chat-tabs-20261016/validation.md)：从消息分叉或新建空白 Tab，跨分支选材到同一卡片，输入和回复各归原对话；真实 Codex/API 分叉、双端四图导出与回执丢失恢复、专项及综合界面通过。
- [此前性能优化验收](D:/Artifacts/KnowledgeClipper/performance-20261016/validation.md)：优化历史读取/排版、旧制卡记录重开和双端导入；原功能与格式保持。
- [最新批注书面化修复与真实验收](D:/Artifacts/KnowledgeClipper/annotation-formalize-fix-20261015/validation.md)：缺失历史来源不再使辅助操作在发送前报错；保留保存的正文、双面批注及原来源编号。
- [Mermaid 双端原生显示验收](D:/Artifacts/KnowledgeClipper/mermaid-chain-20261015/validation.md)：聊天、预览、RemNote widget 与 Anki 离线矢量图链路共用源码。
- [问答日期目录与后台定位验收](D:/Artifacts/KnowledgeClipper/question-dated-import-20261015/validation.md)：保留类型精确绑定、当日目录、普通摘录顺序及空行规则，冻结旧路径不迁移。
- [全部发布与验收历史](docs/release-history.md)：只用于追溯，不作为当前运行路径或跨任务授权。

当前包包含各轮已经验收的聊天、同页分叉、Tab 关闭/恢复/排序、共享卡片、历史、批注、撤销/重做、补充要求和双端导出修改。源码、部署与验收分别留痕；本轮证据见[图片链接验收](D:/Artifacts/KnowledgeClipper/image-link-20261019/validation.md)，此前代码整合见 [收尾记录](D:/Artifacts/KnowledgeClipper/closeout-20261016/closeout.md)。

## 使用与设置

- 文字浮点“读 / 卡 / 摘 / 问”，图片浮点另有“图”；快捷模式与普通模式共用原稿、导入身份及失败反馈。
- 复制 PNG/JPEG/GIF/BMP/SVG 图片直链，下载完成后显示同一图片浮点；普通网址不探测，真实剪贴板图片始终优先。SVG 原文件与来源随图片操作保存在本地，进入既有流程的是 PNG；不改写剪贴板。
- 图文输入进入同一问答页。普通“卡”按问题类型使用 Codex 默认模型/推理；最右“问”按配置的 API 快捷问模型发送一次，不自动导出。
- 多轮消息可选入卡片正反面，保留原始 Markdown、公式、图片与 Mermaid。双面正文和批注支持独立或全部书面化、图转文字、补充图片、预览、撤销及重做。
- “整合对话”在卡面与选材均空时使用全部保留对话；有选材或手工内容时以其为重点，参考对话补足上下文。模型按知识目标判断张数，也可在补充要求中指定 1 至 20 张；停止或超时后 API 辅助请求可在原页面明确再次生成，旧输入和部分输出保留，Codex 不确定回合继续按原身份核对。
- 消息旁第五个按钮为“分叉”；原对话和分叉用左侧顶部 Tab 切换，末尾“＋”和右上“新建对话”均在同页创建空白 Tab。所有 Tab 共用右侧卡片，可跨分支选材；关闭重开恢复原状态。
- Tab 支持点 × 关闭和拖拽排序；在 Tab 栏按 Ctrl+Z 可逐次撤回关闭，也可点加号左侧的撤回按钮。关闭保留卡片来源，排序与关闭历史随草稿保存；输入框和卡片区继续使用各自原有撤销。
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
