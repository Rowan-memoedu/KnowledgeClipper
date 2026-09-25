# 浮点快捷入口真实验收

2026-09-19 单图原图快捷摘录使用新增的独立入口：`--prepare-image-excerpt`、`--live-image-excerpt`、`--readback-image-excerpt`。必须显式传最终包的 `--desktop`；这些入口使用不同 manifest schema，只运行 `ImageDesktopFlow.StartClipboardImage(png, "excerpt", true)`，不会进入下方旧四路线。旧四路线样本已退役，禁止再次导入。

本轮固定新目录为 `D:\Caches\KnowledgeClipper\image-excerpt-quick-live-20260919`，回执为 `D:\Artifacts\KnowledgeClipper\image-routing-refinement\validation\image-excerpt-quick-live.json`。公开 PNG 内容为 `PUBLIC SINGLE IMAGE EXCERPT / 3 x 4 = 12`，准备时冻结图片、模式文件和本地设置副本；只开启原图与快捷摘录，其余捕获监听和音频关闭。OCR 使用不存在的本地守卫路径、内容 API 使用封闭 loopback 地址，以阻止路由回归触发任何真实上游请求。正常原图快捷摘录不使用这两项设置。

先构建并准备，待当次 SDK 修复加载后才运行 live。失败保留全部原 job/session/draft/Rem 身份；不会重新调用入口制造第二条记录。后续 `--readback-image-excerpt` 只回读，同 ID 核验原节点、空背面、零复习卡、批次空行和历史状态，并根据 SDK 返回的托管图片 token，在 host 配置允许的目录中读取该 PNG 的实际字节，与固定原图逐字节比较。每次记录 HTTP 请求与全部预览窗口观察；成功期间必须没有预览。

```powershell
$dotnet = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
$desktop = 'D:\Artifacts\KnowledgeClipper\image-routing-refinement\release-final\RemNoteQuickClipper.Desktop\Release\net8.0-windows\win-x64\KnowledgeClipper.dll'
& $dotnet build '.\tests\KnowledgeClipper.FloatingEntry.Live\KnowledgeClipper.FloatingEntry.Live.csproj' -c Release --nologo `
  "-p:DesktopAssemblyPath=$desktop" `
  '-p:KnowledgeClipperBuildOutput=D:\Artifacts\KnowledgeClipper\image-excerpt-quick-live-final-20260919' `
  '-p:KnowledgeClipperBuildCache=D:\Caches\KnowledgeClipper\image-excerpt-quick-build-final-20260919'
& '.\tools\Invoke-IsolatedDesktopTest.ps1' -Executable $dotnet -TimeoutSeconds 900 -Arguments @(
  'D:\Artifacts\KnowledgeClipper\image-excerpt-quick-live-final-20260919\KnowledgeClipper.FloatingEntry.Live\Release\net8.0-windows\KnowledgeClipper.FloatingEntry.Live.dll',
  'D:\Projects\RemNote Quick Clipper',
  'D:\Caches\KnowledgeClipper\image-excerpt-quick-live-20260919',
  'D:\Artifacts\KnowledgeClipper\image-routing-refinement\validation\image-excerpt-quick-live.json',
  '--prepare-image-excerpt', '--desktop', $desktop
)
```

最终包实测已通过：[单图真实验收](D:/Artifacts/KnowledgeClipper/image-routing-refinement/validation/image-excerpt-quick-live.md)。首次真实调用只有一条原图导入，24 项通过、83 次窗口采样为零；新进程纯回读 26 项通过、11 次窗口采样为零，所有节点身份及托管 PNG 的 8425 字节不变，纯回读无写入。

该工具只运行于 `tools/Invoke-IsolatedDesktopTest.ps1` 创建的隔离 Windows 桌面。不会读取或改写系统剪贴板，不启动文字/图片监听，不修改用户设置，也不播放音频。

四条独立公开样本：`velocity` 完整句上下文（真实发布语言提示词、默认理解与朗读格式、Anki）、一个真实单轮数学问答（RemNote）、原文摘录（RemNote 普通节点）、脚本生成的单 PNG（RemNote 图片普通节点，无 OCR/答案）。整个成功路径持续检查 `Application.OpenForms` 与生产流程保存的预览集合；出现预览即失败。

源码构建不执行测试。`--prepare` 只冻结本机设置副本、提示词、公开输入和 PNG，不调用外部服务；`--live` 才实际调用配置中的 DeepSeek、Anki 与独立 RemNote SDK。首次入口调用生产 `StartMaterial(..., quick:true)`、`ExcerptDesktopFlow.CompleteQuickAsync`、`ImageDesktopFlow.StartClipboardImage(png, true)`；恢复只使用同一 manifest、原草稿、原图片任务和已确认身份。已生成问答直接恢复生产导入阶段，不再次调用模型。

建议固定数据根为 `D:\Caches\KnowledgeClipper\floating-quick-live-entry`，结果为 `D:\Artifacts\KnowledgeClipper\floating-quick\validation\floating-entry-live.json`。失败后保留这两处，继续使用原路径，不新建第二套。固定样本在日后清理后应退役；不可再次把清理后的记录当新来源导入。

构建（按全局长命令规范从 `windows-terminal-timeout` 包装器执行）：

```powershell
$dotnet = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
& $dotnet build '.\tests\KnowledgeClipper.FloatingEntry.Live\KnowledgeClipper.FloatingEntry.Live.csproj' -c Release --nologo `
  '-p:KnowledgeClipperBuildOutput=D:\Artifacts\KnowledgeClipper\floating-entry-live' `
  '-p:KnowledgeClipperBuildCache=D:\Caches\KnowledgeClipper\floating-entry-live'
```

准备 / 真跑 / 冷回读共用以下入口，分别传 `--prepare`、`--live`、`--readback-only`。真跑前需先确认当次 SDK 修复已经加载；工具不会替用户开启或刷新插件。

```powershell
& '.\tools\Invoke-IsolatedDesktopTest.ps1' -Executable $dotnet -TimeoutSeconds 1800 -Arguments @(
  'D:\Artifacts\KnowledgeClipper\floating-entry-live\KnowledgeClipper.FloatingEntry.Live\Release\net8.0-windows\KnowledgeClipper.FloatingEntry.Live.dll',
  'D:\Projects\RemNote Quick Clipper',
  'D:\Caches\KnowledgeClipper\floating-quick-live-entry',
  'D:\Artifacts\KnowledgeClipper\floating-quick\validation\floating-entry-live.json',
  '--prepare'
)
```

`--readback-only` 不调用模型、不创建草稿、不导入；真实回读 Anki 字段/卡片/牌组、RemNote 原始节点/复习卡/图片哈希/批次空行，比较原身份。每轮保存请求元数据（无请求头、密钥或正文）、窗口观察、状态与失败原因。最终桌面程序集和测试所用 workflow DLL 的 SHA-256 必须一致，否则工具在真实入口前停止。
