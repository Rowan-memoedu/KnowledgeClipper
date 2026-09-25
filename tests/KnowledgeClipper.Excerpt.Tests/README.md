# 摘录与图片工作流检查

先按仓库 `AGENTS.md` 构建本项目到独立 D 盘输出，再运行该输出中的 `KnowledgeClipper.Excerpt.Tests.dll`。以下命令中的 `dotnet` 指仓库规定的 .NET 8 SDK，不使用 `dotnet run`。

- `<project> <receipt.json>`：原有摘录、手动编辑、挖空与 Anki 故障夹具。
- `--images <project> <receipt.json>`：图片与 OCR 原有回归；先用 `tools/New-ImageRoutingFixtures.ps1` 在回执目录的 `fixtures` 子目录生成公开图片。
- `--ocr-parallel <project> <receipt.json>`：最多 3 图并行、乱序完成后的稳定顺序、50 图并发落盘、失败及取消恢复、进度回到原同步上下文、当前和旧审核 Schema、真实离线 Python 子进程重叠。自动生成隔离公开夹具，不调用网络或导入。
- `python tests/KnowledgeClipper.Excerpt.Tests/test_mineru_direct.py`：离线 TLS 握手恢复、保留证书校验、动态 HTTPS DNS、禁止代理隧道和发送失败不重复请求的回归。

真实 OCR Canary 使用独立入口，须有明确的公开样本 API 调用授权：

```powershell
pwsh -NoProfile -File tools/New-OcrParallelLiveFixtures.ps1 -OutputDirectory 'D:\Caches\KnowledgeClipper\ocr-parallel-live-20260919\inputs'
dotnet <测试DLL> --live-ocr-parallel 'D:\Projects\RemNote Quick Clipper' 'D:\Caches\KnowledgeClipper\ocr-parallel-live-20260919' <新的receipt.json> 'D:\Workspaces\新建文件夹\04 Projects\MinerU OCR Integration\mineru_ocr.py'
```

入口读取当前 D 盘语言设置，在隔离任务中冻结副本；不修改运行设置。第一次保存固定 `manifest.json` 和三张公开图片，重跑必须复用它们及原 `jobId`，回执另用新文件名。调用真实 MinerU、清洗与审核，不导入 Anki 或 RemNote。`observe_mineru.py` 只记录上传、轮询和下载的开始／结束时间，所有操作均委托给已配置的共享程序，不记录 URL、Header、Token 或网络载荷。当前共享程序及观察器的哈希在 manifest 中固定，输入或身份漂移会阻断重跑。

MinerU 调用桥接加载 `helpers/mineru_direct.py`。它只在子进程内恢复 HTTPS 连接建立，不重试已发送请求；复用共享程序的物理网卡发现和直连子进程环境，经 HTTPS DNS 动态解析，继续由标准库完成 TLS 与主机名校验。不会修改共享 MinerU 文件、全局代理或系统 DNS。

验收必须区分离线调用并发、真实上传时间重叠、真实 OCR 内容和最终导入。此项目的 OCR Canary 只覆盖前三项；零导入是测试边界。
