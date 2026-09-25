# 有界取词检查

`plain` 使用独立 Windows 桌面上的原生 Edit 窗口，调用生产 `CapturePlainTextAsync`，并让 UIA 返回不可用，以验证受保护的 Ctrl+C 后备路径。复制前核对精确 HWND 与本工具 PID，再向该 Edit 发送 `WM_COPY`；真实剪贴板保护、序号与 PID 核验、捕获策略及英文补词均执行生产代码；不调用 `SendInput`，不读取或操作浏览器。

覆盖多取、漏取、错误相邻候选、无候选、中文换行与空白、英文部分词（上下文开／关）、折叠或空选区、空白选区以及错误来源 PID。`plain-blocked` 验证 UIA 优先且无需复制、UIA 不可用时回退受保护复制、两条路径均失败时拒绝旧候选和旧剪贴板文字。

先完整保存原剪贴板格式，再使用公开 text／HTML／binary 夹具；每次取词检查格式恢复，退出时条件恢复原剪贴板。遇到并发外部复制不覆盖。JSON 不记录用户剪贴板正文，只记录公开测试文字、检查状态和程序集哈希。

构建时指定 `DesktopAssemblyPath` 为要验收的生产 DLL，并将 `KnowledgeClipperBuildOutput` / `KnowledgeClipperBuildCache` 指向独立 D 盘目录。测试同时核验自身复制的桌面及 Workflow DLL 与目标包哈希一致。

```powershell
& '.\tools\Invoke-IsolatedDesktopTest.ps1' -Executable $dotnet -TimeoutSeconds 180 -Arguments @(
  $captureCheckDll, 'plain', $projectRoot, $receiptJson, $productionDesktopDll
)
```

该测试证明隔离原生 Edit 的捕获策略与保护边界，不验证实际快捷键发送，不能替代某个 PDF 阅读器的实际选区验收。`chrome-range` 使用独立 Chrome 配置打开本地禁复制页；`notepad-range` 在临时打开的记事本测试文件中验证 UIA 精确选区。其他已有模式仍为 `source`、`context`、`clipboard`、`faults`。
