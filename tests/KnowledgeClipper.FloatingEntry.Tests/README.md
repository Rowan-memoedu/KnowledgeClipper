# 浮点快捷入口隔离回归

所有入口都通过 `tools/Invoke-IsolatedDesktopTest.ps1` 运行在独立 Windows 桌面，不切换用户桌面。先将 `DesktopAssemblyPath` 指向本次候选包构建，再运行准确测试 DLL。

- 默认入口保留快捷成功/失败、历史并发、取消及图片路由回归。
- `--image-history` 只跑图片处理中打开历史、上下文选择及同一任务复用。
- `--quick-language` 验证“读”默认快捷模式、显式关闭的兼容性，以及五组生产语言流程：选中文字朗读＋后台导入、朗读失败回原草稿、关闭快捷后的正常预览、图片文字入口的朗读＋后台导入、关闭自动朗读仍可后台导入。

`--quick-language` 使用真正的 `LanguageDesktopFlow`、`StreamingSpeech` 与本地 Python 进程，但 helper 仅记录朗读参数并输出离线假音频；内容响应和 RemNote 导入均为隔离夹具。不会请求真实模型、TTS 或知识库，也不访问系统剪贴板。它检查 `play=true`、原文只请求一次、默认卡片只生成和导入一次、朗读完成前后台任务继续等待、成功期间没有预览，以及朗读失败仍使用原草稿和已核验卡片身份。这些证据不代表真实外部服务或可听音频重新验收。

```powershell
$dotnet = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
& '.\tools\Invoke-IsolatedDesktopTest.ps1' -Executable $dotnet -TimeoutSeconds 300 -Arguments @(
  '<本次构建的 KnowledgeClipper.FloatingEntry.Tests.dll>',
  'D:\Artifacts\KnowledgeClipper\quick-language-default-20260921\validation\speech-next',
  '--quick-language'
)
```

每轮使用新的独立 D 缓存并保存 `validation.json` 与逐场景证据；上一轮失败不覆盖。首次开发回归的离线 helper 把生产多行 JSON 当成单行读取，已改为读取标准输入至结束；失败记录保留在 `speech-01`，修订工具后的 `speech-02` 为 47 项通过。浮点设置默认/关闭保存/外部设置同步回归另见 `SelectionUi.Tests --floating-settings`。
