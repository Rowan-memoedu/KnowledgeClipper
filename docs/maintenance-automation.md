# KnowledgeClipper 自动维护

GitHub Actions 接收任务并排队，本机的官方 Windows runner 执行 Codex CLI、构建和测试。Codex 使用 ChatGPT 订阅登录，权限为 `danger-full-access`，审批为 `never`。

## 提交任务

现在统一从 [SoftwareMaintenance 集中维护入口](https://github.com/Rowan-memoedu/SoftwareMaintenance/actions/workflows/maintenance.yml) 提交任务：在集中仓库的 **Run workflow** 表单中填写目标项目 `KnowledgeClipper` 和需求（要修改的内容、复现步骤及期望结果）。不要再从 KnowledgeClipper 的旧单仓工作流提交新需求。

集中维护继续使用 ChatGPT 订阅和本机 Windows runner，在专用 checkout 中修改目标仓库。后续工作流独立执行既有六组构建与离线检查，通过后提供 KnowledgeClipper 草稿 PR，由人审阅后决定是否合并；不自动合并或部署。执行进度、失败步骤和结果链接从集中仓库的本次 Actions 运行查看。仅修改文档的任务，Agent 核对文档与链接即可，无需重复整个测试套件。

需求按文本传给 Codex，不作为 PowerShell 命令执行。

## 原单仓工作流（历史记录）

原 [Codex maintenance 工作流](https://github.com/Rowan-memoedu/KnowledgeClipper/actions/workflows/codex-maintenance.yml) 及仓库内的 [工作流文件](../.github/workflows/codex-maintenance.yml) 保留用于追溯旧运行，不再作为新任务入口。以下执行细节、服务名称、版本和目录记录的是原单仓配置，不代表集中维护的当前配置；排错时以集中仓库对应运行的日志、目录和 runner 配置为准。

### 原执行与结果

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

### 原本机目录

| 用途 | 位置 |
| --- | --- |
| 官方 runner | `D:\AppData\GitHubActions\KnowledgeClipper\runner` |
| Codex 登录 | `D:\AppData\GitHubActions\KnowledgeClipper\codex` |
| PowerShell / Codex / Python 程序 | `D:\AppData\GitHubActions\KnowledgeClipper\tools` |
| Actions 临时 checkout | `D:\Artifacts\GitHubActions\KnowledgeClipper\work` |
| 每次执行的日志与产物 | `D:\Artifacts\GitHubActions\KnowledgeClipper\runs\<run id>\<attempt>` |
| 构建缓存 | `D:\Caches\GitHubActions\KnowledgeClipper` |
| .NET SDK | `D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424` |

完整访问意味着 Codex 拥有 runner 运行账户的本机权限，不能把提示中的目录约束当成安全沙箱。runner 应仅供受信任的维护任务使用；不要添加来自 fork 或任意 PR 评论的自动触发入口。

## 登录失效与故障

登录需要在执行该任务的 runner 所用的专用 `CODEX_HOME` 中完成一次。CLI 的 `login status` 应显示 ChatGPT；没有 API key 回退。若登录过期且无法刷新，重新登录该目录即可，代码和任务仍保留在 GitHub。下面保留原单仓的登录示例，使用前须核对当前 runner 的实际目录：

```powershell
$env:CODEX_HOME = 'D:\AppData\GitHubActions\KnowledgeClipper\codex'
& 'D:\AppData\GitHubActions\KnowledgeClipper\tools\codex\codex.exe' -c 'cli_auth_credentials_store="file"' login --device-auth
```

构建或测试失败时，先在集中仓库的对应运行中查看失败步骤及 `checks.json`，修复原因后对该运行使用 GitHub 的 **Re-run all jobs**，不要转到旧单仓入口重提。Codex 故障详情保留在本机该 run 的 `codex-stderr.log` 和 `codex-events.jsonl`。取消运行使用该次 GitHub Actions 运行的 Cancel；不要用终止用户桌面应用的方式清理作业。

本机休眠、关机或 runner 离线时，任务等待可用 runner。当前 Windows 电源设置由用户管理，安装流程不会更改。

## 停用与移除

临时停用集中维护应在集中仓库停用 `maintenance.yml` 工作流；停用旧 `Codex maintenance` 不会停用集中入口。若需停止或移除 runner，先核对当前注册仓库和对应 Windows 服务，并确认没有作业运行及对其他集中维护任务的影响。移除 runner 使用其注册仓库 **Settings → Actions → Runners → Remove** 显示的官方 `config.cmd remove` 命令，在 runner 目录的管理员终端执行；移除令牌只在本机使用，不写入脚本或日志。卸载服务后仍保留 D 盘的登录、日志和工具文件，按用户确认的保留范围清理，不自动删除。

## 依据

- [OpenAI：在 CI/CD 中维护 Codex 账号登录](https://learn.chatgpt.com/docs/auth/ci-cd-auth)
- [OpenAI：非交互运行](https://learn.chatgpt.com/docs/non-interactive-mode)
- [GitHub：添加自托管 runner](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/add-runners)
- [GitHub：将 runner 配置为服务](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/configure-the-application?platform=windows)
