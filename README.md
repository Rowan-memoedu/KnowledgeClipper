# 零碎知识剪藏

Windows 悬浮式知识采集工具。活动源码位于 `D:\Projects\RemNote Quick Clipper`，应用数据位于 `D:\AppData\KnowledgeClipper`，构建和验证产物位于 `D:\Artifacts\KnowledgeClipper`。

## 当前状态（2026-09-10）

**M1 主流程已接通，首声与窗口修复已通过用户复测；内容质量仍待最后回归，M2 尚未开始。**

现有朗读/摘录入口已直接连接 .NET 语言流程：取得一次材料，独立启动 TTS 和内容处理，显示可编辑预览，一次确认后通过 AnkiConnect 写入并核对实际卡片。新路径不启动旧 Core / Host / Worker 调度。

用户已通过现有朗读入口完成真实取词，来源上下文、预览和声音均出现。初次手测捕获后约 5.2 秒才有声音，且首次 UI Automation 初始化导致浮窗物理尺寸减半。现已提前启动独立语音、预热一个不含文本的助手并调整播放器缓冲；启动时固定 DPI、保存逻辑尺寸并保持浮窗完整位于屏幕内。13:10 的用户复测确认两项正常：点击至声音约 3.00 秒、捕获后约 2.58 秒，实际窗口仍为 696×424。内容质量的最后回归仍由独立提示词 Thread 完成；M1 通过后才接入全局选词悬浮点和设置开关。

## 构建与使用

使用当前构建脚本，不再运行旧 .NET Framework 的整批 Canary 构建：

~~~powershell
pwsh -NoLogo -NoProfile -File 'D:\Projects\RemNote Quick Clipper\build.ps1' -Start
~~~

脚本使用已安装的 .NET SDK 8.0.424，缺少 selection-hook 依赖时从锁文件安装；可用 `-DotnetPath` 指定其他 .NET 8 SDK。编译缓存位于 `D:\Caches\KnowledgeClipper\build`，NuGet 缓存位于 `D:\Caches\NuGet\packages`。

当前可执行入口：[KnowledgeClipper.exe](D:/Artifacts/KnowledgeClipper/build/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)。保留整个输出目录；重新构建前退出正在使用该输出目录的进程。

1. 在普通应用选中文字，点击悬浮窗“朗读”或“摘录”。
2. 上下文开启时使用可靠的来源原文；提取失败时选择允许生成、补充原文或取消。补充上下文不会自动重播。
3. 在预览核对读音、语境义、句子及练习类型。可选理解与朗读、书写或两者；关闭窗口保留草稿和未确认的编辑。
4. 确认一次，等待 Anki 实际回读。Anki 不可用时草稿保留；不确定的写入先查询，不盲目再添加。
5. 右键“设置…”调整语音、内容 API、Anki 目标和已有提示词；“最近草稿 / 任务”可恢复本次材料。停止朗读不取消制卡，完整音频可直接重播。

当前本机配置仍指向隔离牌组 `KnowledgeClipper Canary::Architecture 20260910`，用于完成 M1 验收。专用笔记类型是一份共享字段配两个原生模板；已有用户模板不会被覆盖。

## 配置和模块

- [desktop](desktop/RemNoteQuickClipper.Desktop.csproj)：现有 WinForms 界面与直接流程连接。
- [workflow](workflow/KnowledgeClipper.Workflow.csproj)：材料、草稿、内容 API、独立语音及 Anki 接口。
- [modes](modes/modes.json)：模式识别说明、公共分类提示词、处理提示词与字段要求。每次操作保留生效快照，分类请求只发送候选说明。
- [helpers](helpers/package.json)：selection-hook 2.1.1；Python edge-tts 7.2.8 将音频块直接送给 ffplay。
- 获准生成例句时，程序只接受目标原样出现一次的结果，并确定 UTF-16 下标；来源上下文始终使用捕获时的确切位置。
- 本机设置和草稿在 `D:\AppData\KnowledgeClipper`，提示词覆盖文件在其 `modes` 子目录，完整任务音频在 `D:\Caches\KnowledgeClipper\audio`。
- API Key 使用设置中的安全输入框或既有环境配置；不进入仓库、提示词、命令行参数或回执。

截图、原图、OCR、问答和 RemNote 尚未迁入新主流程；相关旧源码与入口保留，当前构建未验收它们的可用性。旧候选、SDK 和历史报告不作为新流程的运行前提或验收证据。

## 已核验的边界

| 检查 | 当前证据 |
|---|---|
| 中文、英文、混合内容 | 共用内容 CLI 已有真实 API 输出；最新提示词的拼音、提示泄露及生成例句回归仍在进行 |
| 语音 | 最新预热后的首次调用至实际输出约 3.94 秒，首块约 2.80 秒；同轮后续播放约 2.48 秒，重播约 0.33 秒。首次首块后的约 1.14 秒等待仍是体验限制 |
| 语音设置与状态 | 速度、音量、音色实际生效；新预热实现的停止约 78 毫秒，停止和退出能回收本次播放器与空助手 |
| Anki | 隔离牌组真实双卡与单卡；字段、卡面、模板、来源及非暂停/非埋藏状态回读；真实写后丢响应查询恢复且未再次添加 |
| 预览与确认 | 长内容、最小窗口下确认可达、编辑持久化、冻结确认；原失败草稿正常重试后产生 2 张实际卡片，重复确认不再调用导入器 |
| 捕获局部 | 多格式剪贴板保护、并发复制、辅助进程故障、超过 200k 字符后的真实 UIA 邻接范围 |
| 真实朗读入口 | 修复后用户复测通过；点击至实际输出 3.00 秒、预览 3.54 秒，预览早于播放结束；真实来源材料和两条分支均保留在本次草稿中 |
| 浮窗与 DPI | 当前实际 192 DPI 下首次 UIA、状态、预览开关尺寸稳定；手动缩放、逻辑尺寸保存恢复、启动后完全可见均已检查 |
| 仍未通过 | 最新内容质量，以及全部 M2 选词悬浮点行为 |

证据集中在 [validation](D:/Artifacts/KnowledgeClipper/validation)：[语音](D:/Artifacts/KnowledgeClipper/validation/speech.json)、[最终播放状态](D:/Artifacts/KnowledgeClipper/validation/speech-state.json)、[Anki 真实丢响应恢复](D:/Artifacts/KnowledgeClipper/validation/anki-mixed-writing-lost-response.json)、[预览确认](D:/Artifacts/KnowledgeClipper/validation/m1-preview-confirmation.json)、[窗口缩小复现](D:/Artifacts/KnowledgeClipper/validation/window-size-before.json)。首次取词自动检查未取得前台、静音、Anki 模板大小写及 HTTP chunked 兼容失败均保留；它们不覆盖后续真实结果。

本次修复的相关结果：[首次语音](D:/Artifacts/KnowledgeClipper/validation/speech-prepared-first.json)、[预热与生命周期](D:/Artifacts/KnowledgeClipper/validation/speech-latency.json)、[窗口尺寸及可见范围](D:/Artifacts/KnowledgeClipper/validation/window-size-visible-check.txt)、[预览 DPI](D:/Artifacts/KnowledgeClipper/validation/preview-dpi-check.txt)。普通接口回归 34 项通过，预览局部 6 项通过；固定响应检查仅证明结构与状态。

用户复测对应草稿为 `b0ccda88c648434b921be4632bd80796`，运行数据留在本机应用目录。人工验证由工程先准备测试条件并确认悬浮窗可见，再通过默认提问框通知和收集结果，不额外询问是否准备好。独立内容线程目前停在原生消息工具的批准项，最后语义回归尚未完成；不能据此宣布 M1 全部通过。

常规接口检查使用 [Workflow.Tests](tests/KnowledgeClipper.Workflow.Tests/KnowledgeClipper.Workflow.Tests.csproj)；内容、语音、Anki 和捕获各有薄 CLI，均调用生产实现。真实写入和播放检查只针对明确的隔离输入与目标，不作为每次构建的自动步骤。没有新变化或未解决问题时，不重跑整批外部检查。

实施依据：[架构交接文件](D:/Artifacts/KnowledgeClipper/architecture/knowledge-clipper-architecture-handover.md)。原有改动已保存在 Git 基线 `ede7fbe`；历史版本由 Git 保留，不复制版本目录或整批旧报告。
