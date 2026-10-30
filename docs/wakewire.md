当前 Codex 问答仍使用原 ChatCoordinator → CodexChatProcess → helpers/codex_chat.py → 官方 SDK 链。helper 通过 SDK 的 launch_args_override 连接 WakeWire codex-session；真实 Thread 由原生 Codex App Server 创建和执行。

仅替换原生连接的启动入口。原固定 Codex runtime、模型/推理、工作目录、SDK config overrides、developer 规则、用户文字、图片字节校验、流式消息、停止、压缩后规则恢复、分叉与 Unknown 精确回读全部保留。没有 webhook 事件包装、长度裁剪或跨会话转发消息。API 模式、界面、制卡及 RemNote/Anki 导出不经过本次改动。

默认安装：D:/Applications/WakeWire/node_modules/wakewire/dist/cli.js；运行目录：D:/AppData/WakeWire；Windows 后台任务名：WakeWire。WAKEWIRE_NODE、WAKEWIRE_CLI、WAKEWIRE_HOME 可显式覆盖以使用隔离配置。运行时路径仍由 resolve_runtime 按现有固定版本核验，并须位于 WakeWire 的 sessions.codexPaths 许可列表。连接失败按原草稿错误与收据逻辑处理，不直接回退重发。

helper 在每个请求时重新加载。只修改该接口时，先在隔离目录验收，确认没有正在使用旧 helper 的请求，再原子对齐当前桌面包中的 helpers/codex_chat.py 并回读哈希；无需替换 DLL、迁移草稿或重启桌面程序。现役桌面包位置仍以 README 为准。

完整接口合同位于 [WakeWire workflow sessions](/D:/Projects/WakeWire/docs/workflow-sessions.md)。后续开发 Thread 应先读本文件、README 与当前 helper；接入验收记录位于 D:/Artifacts/WakeWireIntegration/2026-10-30。

回归入口：tests/KnowledgeClipper.Chat.Tests/native_contract.py、原 Chat.Tests 离线检查及 --live 产品协调器验收。真实样本使用独立 D 盘草稿目录，不修改已有私人草稿、提示词或导出状态。
