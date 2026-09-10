# Read 播放控制 SDK（增量 1）

本包只替换 Core v3 runtime 与 typed SDK。旧 authority、Content/Speech/Anki worker、Desktop 源码和已通过的 Actual 均不变。版本身份以包 manifest 和每个程序集 SHA 为准；既有 AssemblyVersion 保持兼容。

## Desktop 接线

1. 用 `GetReadPlaybackControlAsync(readOperationId)` 取得 `ReadPlaybackControlSnapshot`。仅 `CanStop=true` 时保留该快照的 `Target`；不要在点击后重新解析为下一轮。
2. 点击停止：`StopReadPlaybackAsync(new ReadPlaybackStopRequest(uniqueRequestId, capturedTarget))`。`ReadPlaybackTarget` 为 OperationId、opaque TargetToken、ActionCommandId、AttemptId，不含路径。
3. 返回 `ReadPlaybackStopSubmission.Command` 含 CommandId/AcceptedSequence。Accepted 只证明请求已发送到该轮。用 `WaitReadPlaybackStopAsync(submission, timeout)` 等待同一 target 的 `Stopped` 或 `IdleReplayable`（自然结束赢得竞态）；`Unknown` 不能显示已停止，也不自动重试。目标轮次变化会拒绝消费另一轮结果。
4. 重播仍用现有 `ReplayReadAloudCardsAsync`，完整保留返回 `OperationSubmission` 的 OperationId、CommandId、AcceptedSequence；交给 `WaitReadAloudCardsActionAsync(submission)`，禁止用仅 operationId 的旧终态观察结束新重播。
5. Replay 保留原两卡、草稿 token、settings/context、目的端回读证据。仅新建 playback attempt、重新校验复用的音频；不再次 synthesize/Content/export。已确认 Stopped 与 IdleReplayable 均可重播。Read 的业务结果尚未落盘时不抢先重播；待当前业务结果到达后可重播，不要求先确认草稿。

Stop 是独立控制记录，不能替换 Read/Clip 的业务 observer、清空草稿或写 task/operation cancel。UI 的停止回显只能更新 capturedTarget 对应的播放轮次。

## 内部实现位置（Desktop 不复制）

- `KernelApplication.Playback.cs`：既有 command transport 内的 typed stop 路由、持久命令索引、拒绝/中断回执；不写 Read 生命周期流。
- `PlaybackAttemptControl.cs`：进程存活状态、唯一 active 解析、一次性轮次 token、原子 cancel signal 和 append-only resolved/terminal evidence。
- `WorkerProcessRuntime.cs`：真实 result 身份与 user_stop signal proof 验证。只有 cancelled + playback.user_stop + stopped 才投影 Stopped。
- `ReadAloudCardsOperation.cs`：Replay 同 operation 新 action 身份，保留真实卡片数/草稿；Unknown/不完整音频拒绝自动重播。
- `PlaybackClient.cs`：公开 typed Stop request/submission/projection 与等待接口。仍使用内部封装的 Named Pipe，不增加 GUI 的原始 JSON/文件操作。

## 验收与边界

定向回归使用独立 FakeWorker 子进程，不是物理播放；包含停止时 Content 仍等待并随后成功、导出确认不受影响、迟到 N 轮 Stop 不碰 N+1、自然结束竞态、重播仅播放/两卡保留、旧终态拒绝、错误/歧义目标、hash drift 和恢复不重发信号。

旧全量 Phase2 含迁移前 `D:/Projects/RemNote Quick Clipper/releases/...v1.2.41` 路径引用，相关旧启动检查失败；原始失败回执保留。不回填旧目录，不将该全量门禁宣称 PASS。本包使用当前 sealed base 的真实 Host 只读 startup/query，业务提交为 0。Desktop 接线与用户授权后的实际停止/重播仍是后续验证边界。
