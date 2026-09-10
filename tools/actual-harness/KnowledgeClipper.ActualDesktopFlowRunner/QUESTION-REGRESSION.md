# Question 独立回归模式（离线准备）

本批未获 live 许可。只有 Desktop 单一 launcher 在候选/root 核验与总控调度后才能运行一次新 Actual；不能重开 T083307 或旧事务。

入口：`app/KnowledgeClipper.ActualDesktopFlowRunner.exe --actual --question-regression --candidate <sealed-desktop-root> --evidence-root <new-root> --authorization <existing-launch-authorization> --pipe <candidate-pipe> --data-root <new-isolated-root> --anki-endpoint http://127.0.0.1:8765 --deck 零碎知识剪藏::Canary::PhaseB-20260828T081109Z`

与 `--sequence-matrix`、`--capture-fixture` 互斥。原四动作模式不变，不重新验收其业务。`--question-regression-plan` 输出固定输入、SHA、ID 和边界；`--question-regression-self-test`、`--question-disk-proof-self-test <new-D-artifact-root>` 只做离线验证。

1. 初问：“在读写数据时，什么是幂等操作？为什么网络超时后直接重试可能产生重复记录？”
2. Enter 追问：“请用提交学习笔记的例子，比较安全重试和重复写入。”
3. 按钮追问：“怎样用唯一请求标识和结果查询，把这个流程整理成三个可执行步骤？”

初问 ID：`knowledge-clipper.desktop.question.inline-input`；追问输入 ID：`knowledge-clipper.desktop.flashcard-preview.follow-up-input`；按钮 ID：`knowledge-clipper.desktop.flashcard-preview.follow-up`。初问和第一追问用 ValuePattern 设置并读回原输入，再向该进程 TextBox HWND 投递一次 Return KeyDown/KeyUp，触发既有生产事件；第二追问用 InvokePattern。不是物理键盘输入，不反射或调用后台提交。

每轮先等待实际 Cards HelpText 的新 operation，再只读 SDK 比较稿 revision/digest/实际正反面。沿用 PreviewReadBarrier，每卡重新获取控件；不缓存跨 render 句柄。不手造期望回答、卡片、模板或分类。Card count 由最终实际稿决定，严格 1..2。每轮输入文本以 session answer-recorded → immutable input manifest/payload SHA 核验；三轮配置 artifact identity/manifest SHA 必须完全相同，deck_routes 必须只指向上述既有 Canary deck。目的端配置只在第一轮设置冻结。

初问与两次追问均先验 worker ledger 0 exporter、Anki baseline 无新增；最后仅确认当前第三稿一次。确认只等待同一 operation 的新 terminal command/cursor。Anki 使用 findNotes/findCards/notesInfo/cardsInfo 独立只读回读，exact IDs/deck/front/back 与最后实际预览一致；仅 1 exporter，6 Content worker invocations（每轮 answer+make-cards），0 Speech/OCR/RemNote。provider 请求次数不能由 worker 次数推断。

最后还读取实际 status Label HWND 的 WM_GETTEXT，要求 Completed 文案、Cards HelpText 相同 operation/revision/digest 且 ready=false，确认/追问/输入均 disabled。该证据来自 UI，不来自后端状态复制。每轮新稿须重新启用追问输入，设置非空文本后按钮实际 enabled。

取消/失败/未知/超时一律保留 evidence 并停止；不自动重发任何 UI 命令。只读观察轮询不等于业务重试。现有 owned-process runner 生命周期仅管理它自己启动的候选进程，不接管其他用户进程。

离线证据只证明测试逻辑/负例与闭包，不证明真实 UI 消息或生产业务 E2E。Desktop owner 已确认生产 ID 和 Enter 路径；实际候选仍须未来一次受控 Actual 验证。
