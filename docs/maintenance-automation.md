# KnowledgeClipper 自动维护

GitHub Actions 接收任务并排队，本机的官方 Windows runner 执行 Codex CLI、构建和测试。Codex 使用 ChatGPT 订阅登录，权限为 `danger-full-access`，审批为 `never`。

## 提交任务

在仓库的 **Actions → Codex maintenance → Run workflow** 中选择 `main`，填写 `task`。也可以通过 GitHub CLI 提交同一输入：

```powershell
gh workflow run codex-maintenance.yml --repo Rowan-memoedu/KnowledgeClipper --ref main -f 'task=修复……；复现步骤……；期望……'
gh run list --repo Rowan-memoedu/KnowledgeClipper --workflow codex-maintenance.yml
```

输入按文本传给 Codex，不作为 PowerShell 命令执行。当前只接入手动 dispatch；录音处理、文档监听和其他项目路由不在此流程中。

## 执行与结果

1. 只接受仓库所有者在私有仓库 `main` 上发起的任务。
2. Actions 在专用 checkout 创建 `codex/maintenance-<run id>` 分支。不会使用日常开发目录，也不会包含其中尚未提交的修改。
3. Codex 阅读仓库说明、修改代码并执行必要的针对性检查。
4. 脚本执行桌面 Release 构建、Workflow/Chat 离线测试、RemNote host/plugin 测试及 plugin 类型检查。测试 DLL 来自本次构建，产物进入独立 D 盘目录。
5. 全部通过后推送任务分支并创建草稿 PR。没有变化时不创建空 PR；同一 run 重试时沿用分支和现有开放 PR，不强推。
6. Actions 页面显示结果和 PR 链接，检查摘要保留 14 天。Codex 原始日志和登录凭据不会上传为 artifact。

流程不自动合并、发布应用或运行真实的 RemNote/Anki 导入。PR 合并前仍由人检查需求是否真正满足。`GITHUB_TOKEN` 创建的 PR 所触发的其他工作流可能需要 GitHub 批准，所以本流程在创建 PR 前已经执行所列检查。

单个 runner 同时执行一个作业，由 GitHub 负责排队。不要给同一个 Codex 登录目录再配置并行 runner；每个独立 runner 应拥有独立登录会话。CLI 按官方机制更新并持久保存登录状态，不在每次任务前覆盖 `auth.json`。

runner 已注册为 Windows 自动启动服务，运行账户为 `NT AUTHORITY\NETWORK SERVICE`。服务名称是 `actions.runner.Rowan-memoedu-KnowledgeClipper.knowledgeclipper-windows-local`，与 ChatGPT 桌面端中的 Codex 是否打开无关。PowerShell 7.6.5 和 Codex CLI 0.163.0 使用已验证签名的官方运行文件；runner 2.338.2 来自 GitHub 官方发行包并校验发行 SHA-256。runner 保留官方自动更新，Codex/PowerShell 更新时应先验证新文件再替换专用工具目录。

runner 的官方 `.env` 文件为服务补充专用工具路径，workflow 使用 Actions 原生 `shell: pwsh`。修改该环境文件后，在没有作业运行时重启 runner 服务生效。

聊天离线测试会启动 Python fixture。runner 通过 `KNOWLEDGECLIPPER_CODEX_PYTHON` 指向专用目录中的 Python 3.14.6，使用已验证签名的官方解释器和标准库，不访问用户目录下的 Python 安装。不设置该变量时，应用继续使用原来的 `codex-python` 虚拟环境；生产应用及其运行环境无需调整。

## 本机目录

| 用途 | 位置 |
| --- | --- |
| 官方 runner | `D:\AppData\GitHubActions\KnowledgeClipper\runner` |
| Codex 登录 | `D:\AppData\GitHubActions\KnowledgeClipper\codex` |
| PowerShell / Codex / Python 程序 | `D:\AppData\GitHubActions\KnowledgeClipper\tools` |
| Actions 临时 checkout | `D:\Artifacts\GitHubActions\KnowledgeClipper\work` |
| 每次执行的日志与产物 | `D:\Artifacts\GitHubActions\KnowledgeClipper\runs\<run id>\<attempt>` |
| 构建缓存 | `D:\Caches\GitHubActions\KnowledgeClipper` |
| .NET SDK | `D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424` |

完整访问意味着 Codex 拥有 runner 运行账户的本机权限，不能把提示中的目录约束当成安全沙箱。仅为这个私有仓库注册 runner；不要添加来自 fork 或任意 PR 评论的自动触发入口。

## 登录失效与故障

登录需要在上表的专用 `CODEX_HOME` 中完成一次。CLI 的 `login status` 应显示 ChatGPT；没有 API key 回退。若登录过期且无法刷新，重新登录该目录即可，代码和任务仍保留在 GitHub。

```powershell
$env:CODEX_HOME = 'D:\AppData\GitHubActions\KnowledgeClipper\codex'
& 'D:\AppData\GitHubActions\KnowledgeClipper\tools\codex\codex.exe' -c 'cli_auth_credentials_store="file"' login --device-auth
```

构建或测试失败时，先查看失败步骤及 `checks.json`，修复原因后使用 GitHub 的 **Re-run all jobs**。Codex 故障详情保留在本机该 run 的 `codex-stderr.log` 和 `codex-events.jsonl`。取消运行使用 GitHub Actions 的 Cancel；不要用终止用户桌面应用的方式清理作业。

本机休眠、关机或 runner 离线时，任务等待可用 runner。当前 Windows 电源设置由用户管理，安装流程不会更改。

## 停用与移除

临时停用可在 Windows 服务管理中停止上述服务，或者先停用 GitHub 上的 `Codex maintenance` workflow。移除 runner 使用 GitHub 仓库 **Settings → Actions → Runners → Remove** 显示的官方 `config.cmd remove` 命令，在 runner 目录的管理员终端执行；移除令牌只在本机使用，不写入脚本或日志。卸载服务后仍保留 D 盘的登录、日志和工具文件，按用户确认的保留范围清理，不自动删除。

## 依据

- [OpenAI：在 CI/CD 中维护 Codex 账号登录](https://learn.chatgpt.com/docs/auth/ci-cd-auth)
- [OpenAI：非交互运行](https://learn.chatgpt.com/docs/non-interactive-mode)
- [GitHub：添加自托管 runner](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/add-runners)
- [GitHub：将 runner 配置为服务](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/configure-the-application?platform=windows)
