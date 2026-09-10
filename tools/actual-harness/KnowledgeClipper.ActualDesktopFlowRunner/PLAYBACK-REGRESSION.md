# 单任务停止/重播回归（离线准备）

本入口本轮只做外置 runner 离线构建，不能据此宣称真实停止/播放 PASS。固定绑定现有 `D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-final-resize-speech-20260908T172000Z`，manifest SHA `7087B3084EB7B66D6CB0208B01E57BC16EA2406C0A73F3F9361764A57FD4832C`；不复制或修改产品。旧 T153520 与 T164049 runner 保留；本次程序仅重绑定目标路径及 manifest 哈希，逻辑与 fixture 不变。

当前状态：菜单选择器离线已修复；status HelpText 实测为空。Desktop 的独立跨进程证据已确认原生 UIAutomationCore `UiaNodeFromHandle` + `UiaGetPropertyValue(30090,30094)` 能读取 Legacy description；本 runner 使用完全相同渠道，不调用 SDK 填充 UI。最终 Actual 必须等总控核验和单次调度，本轮没有运行。

运行格式：

```text
app/KnowledgeClipper.ActualDesktopFlowRunner.exe --actual --playback-regression --candidate <唯一新Desktop候选> --evidence-root <全新证据根> --data-root <全新隔离AppData根> --pipe <候选Pipe> --authorization <已有单次Actual调度文件> --capture-fixture <本包capture-fixture.json> --anki-endpoint http://127.0.0.1:8765 --deck 零碎知识剪藏::Canary::PhaseB-20260828T081109Z
```

SDK/runtime 必须绑定已交付的 `sdk-runtime-20260908-01`，程序会核验候选 Client/Kernel SHA。旧 Question 与 T083307 候选及 runner 不变。默认项目构建仍绑定旧 SDK；只有本增量构建传 EnablePlaybackRegression=true 和 CoreV3SdkRoot，才启用新入口。

输入严格复用已通过样本：selectedText=`gradually`，prefix=`Regular practice can `，suffix=` improve your confidence.`。只通过既有单条 desktop-test-capture.v1 端口注入；不伪造结果、不变语速/播放速度。采集物理输入不在本次验证范围。

执行顺序：

1. 真实设置页选择“提问与制卡”→`knowledge-clipper.desktop.settings.question.language-card-default`→`both`，保存；目的端仅既定 Canary deck；启用上下文。
2. 真实 Read 点击；typed 当前轮次就绪后，对 owned 浮窗发送 WM_CONTEXTMENU。Stop 只按精确 Name `knowledge-clipper.desktop.playback.stop` 选择（实测 AutomationId 为空），要求 owned PID、可见 Menu 根、可见 MenuItem、唯一逻辑项、enabled 与 InvokePattern；不从 Window 根收集镜像项。核对当前 Legacy description 的 operation/action/attempt 和 CanStop，再 Invoke 一次。
3. 必须等同轮 Worker 终态 Stopped，不能将 Accepted/StopRequested 当通过。等待 Content 完成；通过原 PreviewReadBarrier 读取实际两卡 UI，核对 Front/Back/模板/上下文与不可变稿。
4. 真实 Confirm 一次；独立 notesInfo/cardsInfo 精确读回两 note/card/deck/正反面。
5. 真实 status 按钮 `knowledge-clipper.desktop.status`，CanReplay=true 且原生按钮文字含“重播”，点击一次；等新 CommandId/AcceptedSequence/attempt 自然 IdleReplayable。
6. 再真实 Replay 一次，新轮途中从真实 Stop 菜单停止。最终 1 synth / 3 playback / 1 Content / 1 exporter / 2 notes；草稿、媒体 SHA、notes/card IDs 保持。

每一轮 SDK 终态之后均重新获取真实 status 控件，验证实际 HWND/PID、status AutomationId、Button 类型及可见性；保存原始 HelpText（允许为空）、原始 Legacy description JSON、原生 WM_GETTEXT、HWND/Enabled/渠道。Legacy 不可用、空值、无效 JSON 或字段类型错误均拒绝，不回退。必须同 operation/action/attempt、Playback 终态正确且 CanStop=false；确认后和最终两轮另要求实际 Pipeline=Completed、CanReplay=true、控件enabled且原生文字含重播。不得用 SDK 值填 UI 字段，不把 Legacy 内容标成 HelpText。

SDK 仅用于 discovery/query/artifact identity 观察，代码不调用 SDK 的 Stop/Replay/Confirm/Read 提交接口。菜单 Name 与 Legacy 当前属性由 Desktop 跨进程探针实证；本包另对原始记录运行定向离线解析，记录回放不代表重新运行 UI。允许模拟控件事件的历史授权不代表本轮可运行 Actual。

菜单 missing、disabled、InvokePattern 缺失、status 空/无效 JSON 分别报告；SDK 观察到的实际播放状态另列，不再合并为“too-short”。不换输入、不改播放器、不自动重跑。任何未知外部结果均停止本次运行，保留 Anki 与本地证据；业务失败不自动重新点击。

Replay 刷新期间 ActionCommandId/AttemptId 同时为 null 是合法 pending UI；15 秒内有界重读并保存原始观察，不将其当终态。终态必须绑定相同真实轮次、CanStop=false；完成后还须 Completed/CanReplay/按钮重播文字。缺字段、错误类型、半个 target 均拒绝。

离线命令：`--playback-regression-plan`、`--playback-regression-self-test`。20 项状态/轮次/账本测试（含 SDK 先终态、UI null-target 后绑定）、17 项菜单选择测试、22 项 Legacy 读取解析与 owner/control 负例、9 项 PreviewReadBarrier。附加 `--menu-observation <json> --status-observation <json>` 运行 3 项 Desktop 原始记录回放（原树出现 2 次 Stop，但只选中 Menu 根 1 项；Legacy 状态身份与字段正确）。真实 WinForms Invoke/播放时序仍未执行，不将记录回放或合成测试称作 Actual PASS。
