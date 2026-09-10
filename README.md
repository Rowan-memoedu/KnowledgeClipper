# 零碎知识剪藏

KnowledgeClipper 是 Windows 悬浮式知识采集与处理工具：从文本、选区、剪贴板或图片取得素材，按任务进入内容处理、问答、制卡和语音流程，再导出到 Anki、RemNote 等并列目标。RemNote 只是导出边界，不是产品或核心架构身份。

## 当前入口与使用

截至 **2026-09-09**，已交付基础版的唯一入口是 [KnowledgeClipper.exe](D:/Artifacts/KnowledgeClipper/desktop-read-to-anki/desktop-whole-window-zoom-20260909-01/KnowledgeClipper.exe)，位于 [整体缩放修复候选目录](D:/Artifacts/KnowledgeClipper/desktop-read-to-anki/desktop-whole-window-zoom-20260909-01)。旧 T172000 保留为历史交付，不覆盖。

- 保留并使用整个目录；不要只复制 EXE，也不要混入源码构建输出、旧 DLL、SDK 或 worker。
- 若旧版仍在运行，先从旧版退出，再启动此入口。拖动边角应让文字、按钮、状态区和输入框一起缩放；右键“恢复默认大小”应一起恢复。设置中的 Anki“只读连接检查”按明确选择的 route 检查，不创建或修改目标。
- 悬浮窗提供上下文/原图模式、摘录/朗读/截图操作与底部文字提问入口。存在入口不代表每条路径均已在此候选验收。
- 已验收的文本制卡路径使用卡片预览与显式确认；不能再按“仅异常时才有确认窗口”理解。文字提问可显示答案、继续追问，并确认最终卡片。
- 语言练习有含义与读音、书写、双向三种策略；设置选择已有真实路径证据，不能再标成“默认策略尚未接线”。双向保留两张卡。
- 朗读播放与内容/制卡进度独立。停止只针对当前播放轮次，不取消内容或导出；有可用业务结果及已验证播放终态后才可重播。重播复用音频，不重新合成、制卡或导出。
- 基础版允许手动体验，不等于已签名安装版或整体项目完成。产品、候选与历史证据保持冻结。

[当前交付回执](D:/Artifacts/KnowledgeClipper/whole-window-zoom/20260909-01/delivery/receipt.json) SHA-256：7446CF03C1D1E7603E59D5C44164B9E4C7AC7A5ECA99AD506EB8DED136801FDA。候选 manifest SHA-256：6BAC404621750CC9D7049736BED23274AAB1DB88012EE7B5AC898107C0DE0561。3506 个 payload 文件中，3505 个与 T172000 相同，仅 Desktop DLL 更新；Core、SDK、authority 与 Speech 1.2.6 overlay 原样继承，没有改写旧 checkpoint。后续 [实际 Anki 只读检查资格](D:/Artifacts/KnowledgeClipper/whole-window-zoom/20260909-01/actual-probe-02/qualification.json) 追加于包外，SHA-256：9869AB2AB7891E6767C620B52F7817996DC3DB45CF56FF64FE4BC0BA2A93034A。

## 真实验收范围

各批次分别绑定各自候选；**历史通过不等于当前缩放修复候选对全部路径重新验收**。真实 UI 操作由自动化触发，不能据此声称物理键鼠或跨应用捕获全部通过。

| 证据绑定 | 实际验收范围 | 可读结果与回执 |
|---|---|---|
| **当前 whole-window-zoom** | 69 项缩放回归覆盖八边拖拽消息、直接单轴 ClientSize / SetWindowPos、持久尺寸恢复、10 个文字控件与几何比例、no-activate。设置 probe 的 typed fixture 64 项、UI 25 项含取消后旧结果迟到隔离。真实生产 SettingsForm 仅按一次检查按钮，Core → export.anki 1.4.1 probe 完成，界面“当前目标可用 · route=canary · Succeeded”；worker status externalMutation=false、mutatingActionsObserved=[]，独立前后牌组/模型/笔记/卡片清单相同。无语音/内容/OCR/RemNote 调用。 | [缩放说明](D:/Artifacts/KnowledgeClipper/whole-window-zoom/20260909-01/FIX.md)、[69 项回执](D:/Artifacts/KnowledgeClipper/whole-window-zoom/20260909-01/verified/receipt.json)、[实际 UI 原文](D:/Artifacts/KnowledgeClipper/whole-window-zoom/20260909-01/actual-probe-02/ui-after.json)、[实际检查证据链](D:/Artifacts/KnowledgeClipper/whole-window-zoom/20260909-01/actual-probe-02/qualification.json) |
| 历史 T172000 | 一次 Read → Stop → 双向两卡预览 → Confirm → 自然 Replay → Replay/Stop；合成 1、播放 3、Content 1、exporter 1 次；Anki 隔离 deck 新增 2 notes / 2 cards，独立精确读回。三个播放终态为停止/自然完成/停止，无自动重试。另有原范围 56 项缩放/布局断言，不覆盖后来发现的直接单轴和旧尺寸恢复缺口。 | [交付摘要](D:/Artifacts/KnowledgeClipper/playback-control/desktop-final-resize-speech-20260908T172000Z/delivery-receipt.json)、[Actual](D:/Artifacts/KnowledgeClipper/read-to-anki/actual/desktop-playback-final-resize-speech-20260908T172600Z/actual-run.receipt.json)、[独立读回](D:/Artifacts/KnowledgeClipper/actual-launch/desktop-playback-final-resize-speech-20260908T172600Z/exact-readback.json) |
| 历史 T083307 | 朗读含义与读音、朗读书写、摘录双向、朗读双向四动作；真实 Desktop → Core → Content / Speech / Anki；新增 6 notes / 6 cards、12 个卡面精确读回；摘录未调用语音。输入来自已授权测试捕获队列。 | [原文→卡片](D:/Artifacts/KnowledgeClipper/actual-launch/desktop-rta-20260908T083307Z/READABLE.md)、[最终回执](D:/Artifacts/KnowledgeClipper/actual-launch/desktop-rta-20260908T083307Z/final-receipt.json) |
| 历史 T121038 | 悬浮窗初问 → Enter 追问 → 按钮追问，共三轮真实答案与卡片预览；每轮确认前 exporter 为 0，最后确认一次；新增 1 note / 1 card，精确读回。未调用语音。 | [问题→答案→卡片](D:/Artifacts/KnowledgeClipper/actual-launch/desktop-question-20260908T121038Z/READABLE.md)、[最终回执](D:/Artifacts/KnowledgeClipper/actual-launch/desktop-question-20260908T121038Z/final-receipt.json) |

T172000 样例原句为“Regular practice can gradually improve your confidence.”，选中 gradually。含义与读音卡保留原句；书写卡正面为“Regular practice can （/ˈɡrædʒuəli/；逐渐地） improve your confidence.”，背面精确为“gradually”。两张卡与独立 Anki 读回逐字一致。worker 调用次数不等于底层网络请求或 mutation 次数。

## 未完成与未验收边界

**基础主线已交付，整体 goal 未完成。** 既定需求仍有未接入或未核实项，不能把这次交付外推为全产品 E2E 通过。

- 当前候选 catalog 恰有 8 项：content.settings.describe、artifact.publish、anki.probe、question.initial、question.follow-up、question.make-cards、speech.runtime.internal、clip.read-aloud-cards。没有 image.ocr、remnote.export、retention 等 v3 公开业务 operation；旧模块源码或 authority 中有 worker，不代表本候选已接通该业务。
- 当前候选未重新验收 OCR/MinerU、OCR 人工纠正后继续、原图媒体目的端或 RemNote。历史模块证据不能自动升级为当前候选全链通过；也不再沿用 2026-08-28 的 Bridge 超时或 OCR 凭据缺失作为当前实时故障结论。
- 69 项缩放/布局验证不等于物理跨屏混合 DPI 拖拽；其他应用上下文、真实选区/图片剪贴板及截图拖拽仍需各自范围的验证。测试捕获 fixture 不能代替这些证据。
- 历史 T172000 有物理语音播放与停止/重播，本次修复没有再次播放。所有声音、设备、语速或设置组合，以及项目设置全字段、存储与 retention 均未由本次检查证明。
- 尚无本批已验收的 installer / signing。模块 PASS、文件存在或旧矩阵不能替代正式发布判断。

## 架构与维护入口

活动源码位于 D:\Projects\RemNote Quick Clipper；交付与证据位于 D:\Artifacts\KnowledgeClipper；应用数据位于 D:\AppData\KnowledgeClipper。旧 C 盘路径不是活动入口。源码项目目录名及部分 RemNoteQuickClipper 标识属于 legacy compatibility，不能替代 KnowledgeClipper 产品身份。

- [Core 分层入口](<D:/Projects/RemNote Quick Clipper/core-v3/README.md>)：Contracts、Kernel、typed Client、独立 Host；[公共契约目录](<D:/Projects/RemNote Quick Clipper/contracts/core/v3/README.md>)负责 wire、command/query、event、artifact 与 capability 边界。
- [Desktop Application 边界](<D:/Projects/RemNote Quick Clipper/src/KnowledgeClipper.Desktop.Application/README.md>)：UI → 统一命令 → use cases / reducers → ports → Core typed SDK；[停止/重播 SDK](<D:/Projects/RemNote Quick Clipper/core-v3/docs/PLAYBACK-CONTROL-SDK.md>)说明轮次目标与终态约束。模块 README 中的阶段性状态可能较旧，本轮未跨 owner 改写；交付状态以本页所链接的精确候选回执为准。
- 每次操作使用不可变、版本化任务包；模块独立测试，集成以精确候选与 authority 绑定。修改后的二进制不能沿用旧候选验收。
- 不运行旧 SmokeTest.exe / TtsCanary.exe 作为日常启动或安全验收，它们可能触发外部服务/写入。独立 Actual runner 是测试工具，不是用户入口；不得自行重复真实写入或播放。
- Canary 写入只限获准隔离目标；未知外部状态不得自动重试。凭据不得进入任务包、日志、回执或候选。不得按目录年龄清理真实 jobs，也不在文档同步中删除历史证据。

入口文档同步仅更新本 README；封存候选及历史证据不改写，其他模块文档、规则与全局 Memory 不变。中间失败证据保留，未进行清理。
