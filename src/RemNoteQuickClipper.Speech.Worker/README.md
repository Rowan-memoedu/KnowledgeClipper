# 零碎知识剪藏 · KnowledgeClipper Speech Worker

`speech.edge` 是目标端中立的语音 worker。它消费公共 `worker-request 1.0.0` 外壳中的冻结 `SpeechRequest 1.0.0` payload，校验不可变 `spokenText` 工件，只负责 Edge TTS 合成与本地播放。公共 JSON Schema 由“核心契约与任务编排”模块拥有，本目录不复制公共 schema。

当前模块提供：

- `SpeechRequestReader`：相对路径、目录逃逸、UTF-8、bytes、SHA-256 与冻结策略校验。
- `VoiceRouter`：首版整段单语言自动路由或显式 BCP-47 override。
- `SpeechSettingsAdapter`：把 GUI/Core 提供的轻量设置草案与已发现的 voice catalog 转为冻结 `SpeechRequest`、合成/播放 `configSnapshot` 及只读部署诊断；不做持久化。
- `SpeechVoiceDiscovery`：只读查询 Edge 自然声音或验证 Core/Desktop 提供的缓存快照，返回 revision/SHA-256 绑定目录；不写设置、不写缓存、不播放。
- `SpeechSpeakerPreview`：仅在显式 `speaker.preview` 动作和副作用授权下，使用当前草案合成一个内置短样例，恰好调用一次播放器，自然完成后回到 `idle`；不创建摘录、卡片、导出或公共 job/attempt。
- `SynthesisWorker`：Edge TTS 仅接触 `%TEMP%\\kcsp\\<GUID>` 下的短文本/音频路径；校验后复制到 attempt 的同父目录暂存区并原子发布版本化音频 artifact，支持最终路径超过 Windows `MAX_PATH`，完成后清理短工作区。
- `PlaybackCoordinator`：单活动播放，新播放取代旧播放，完成、停止和取代终态互不串写。
- `PlaybackWorker`：先复核不可变音频的 bytes/SHA-256，再复制到每次播放独有的 `%TEMP%\\kcsp\\<GUID>\\p.mp3`；播放器只接触短路径，自然结束、停止或取消后清理。重播由上游创建新的 playback attempt 并复用任务内成功音频。

## Worker CLI v1

- 权威候选文件：`KnowledgeClipper.Speech.Worker.exe`
- Registry 探测：`KnowledgeClipper.Speech.Worker.exe describe --json`
- 标准执行：`KnowledgeClipper.Speech.Worker.exe run --request <absolute-request.json>`
- 1.2.6 修复标准入口的长路径解析：request、attempt 和 job 父目录统一使用 `LongPathFileSystem`；保留 `jobs/<taskId>`、attempt、outputs 与取消信号边界校验。离线回归覆盖 266 字符 request、440 字符 request 与超过 260 字符的 job 根。
- 设置查询：`KnowledgeClipper.Speech.Worker.exe settings describe --json`
- 设置校验/快照：`KnowledgeClipper.Speech.Worker.exe settings adapt --input <absolute-settings-adapter-input.json> --json`；这是 Core/Desktop 接线接口，不是 registry stage entrypoint。
- 项目设置 companion：
  - `settings companion describe --json`：静态字段、scope/effectiveAt、请求路径绑定、固定策略、conditional 接线与副作用边界。
  - `settings companion status --input <absolute-json> --json`：只输出离线 readiness、voice catalog、路由与绑定状态，不返回请求快照。
  - `settings companion probe --input <absolute-json> --json`：额外返回已冻结的 synth/playback WorkerRequest 与预期 result 契约投影；只接受 `action=validate_only`。
  - `settings companion voices --input <absolute-json> --json`：只读发现/验证声音目录，输出 catalog revision、SHA-256、更新时间、availability 与缓存诊断；不会播放。
  - `settings companion preview --input <absolute-json> --json`：独立 `explicit_only` 副作用动作；必须显式授权，使用内置短样例自然播放一次。
- 稳定业务身份保持 `workerId=speech.edge`；公开 descriptor 不携带具体知识目标端或产品耦合字段。
- `run` 只接收绝对 `request.json`；job 根与 attempt 目录完全由请求文件位置及 `attemptPath` 反向推导并交叉验证。
- `speech.synthesize` 从 `configSnapshot.edgeTtsPath`、`KNOWLEDGE_CLIPPER_EDGE_TTS` 或 PATH 解析 Edge TTS；`speech.playback` 从 `configSnapshot.ffplayPath`、`KNOWLEDGE_CLIPPER_FFPLAY` 或 PATH 解析播放器。
- 播放消费 `worker-request.cancelSignal.path` 指向的 `attempt-signal-file-v1`，校验 attempt/operation 身份及固定 2000ms grace deadline。`user_stop`、`superseded`、`task_cancel` 对外均发布 `cancelled`；模块播放详情分别为 `stopped`、`superseded`、`cancelled`。`timeout` 发布 `failed / timed_out`。
- 有效 worker-request 若在消费时发现音频 manifest/payload 损坏、audio contract/mediaType 不兼容，或固定的 Edge TTS/ffplay 运行时缺失，仍会在本 attempt 发布不可变 `failed result.json`；播放器不会因无效音频而启动，运行时缺失归类为 `dependency`。

## 边界

- 不捕获选区，不拼接上下文，不执行 OCR、清洗、制卡、目标选择或任何知识目标端写入。
- 语音结果既不读取目标配置，也不改变并列目标端的行为；相同 `SpeechRequest` 在不同导出组合下得到相同语音语义。
- 只允许 `edge-tts` provider；旧 AI TTS 不存在于活动路由。
- `speech.synthesize` 与 `speech.playback` 必须使用不同 stage/attempt；播放只消费已校验的 `audio.speech` artifact。
- 已按公共 `artifact.schema.json`、`worker-request.schema.json`、`worker-result.schema.json`、两份 speech schema 与官方 fixtures 输出；`build-and-test.ps1` 会运行 Draft 2020-12 联合兼容验收。

## 轻量设置 adapter

- 可编辑字段只有 `languageStrategy/languageOverride`、`voicesByLanguage`、`ratePercent`、`volumePercent`、`pitchHz`。adapter 会校验 BCP-47、auto/override 组合、数值边界、支持语言、voice 与语言目录归属和 availability，再将其冻结到 `SpeechRequest`。
- `provider=edge-tts`、既有 `playbackPolicy/cachePolicy` 与 `legacyAiTts=disabled` 只读；设置草案没有可覆盖这些值的字段。
- `edgeTtsPath` 与 `ffplayPath` 是只读部署输入，仅接受绝对路径，分别进入 synthesis/playback 的 `configSnapshot`；可执行文件存在性只作为 `available/missing/not_configured` 诊断展示，最终 attempt 仍由 Worker 再校验。
- Voice catalog 的项目缓存由 Core/Desktop 持久化；adapter 只消费已经给定的目录。只读 discovery 命令可调用 Edge voice-list 接口刷新目录，但不会保存设置或缓存，也不会调用播放器；`validate_only` 完全离线。
- `tests/.../fixtures/speech-settings-five-language.json` 是无扬声器的五语言设置 fixture；单测生成 `five-language-settings-to-request.json`，逐条展示输入文本、设置、最终语言/voice/request/configSnapshot。
- adapter 输入 schemaVersion 为 `speech-settings-adapter-input.v1`，同时携带设置、已发现 voice catalog、部署诊断和 synth/playback WorkerRequest 模板；输出 `speech-settings-adapter-snapshot.v1`，不写文件、不运行 TTS/播放器。

## 项目设置 companion

- companion 版本为 `1.1.0`，descriptor 为 `speech-settings-companion-descriptor.v1`；probe、voice discovery、speaker preview 分别有独立的 v1 输入/输出 schema。
- 设置 scope 固定为 `project`，`effectiveAt=next_task`。Desktop/Core 保存项目草案，并在创建新任务的不可变 WorkerRequest 前调用 adapter；已经创建的任务继续使用自己的冻结快照。
- Desktop 的语言覆盖必须同时写入 `settings.languageStrategy=override` 和 `settings.languageOverride=<BCP-47>`。adapter 精确消费为 `synthesisRequest.payload.language.strategy` 与 `synthesisRequest.payload.language.override`，再从 `payload.voiceSnapshot.voicesByLanguage[language]` 选择 voice。
- `autoPlay=true`、`replay=from_start`、`activePlayback=supersede`、自然结束 `idle_replayable`、task-scope 音频复用及 playback 不复用均是 frozen v1 只读语义，不是 companion 可编辑项。
- Voice catalog 的项目持久化与 Desktop/Core 调用仍是 conditional 集成；discovery 输出 revision/hash-bound 快照，持久化责任仍属于 Desktop/Core。
- `speaker.preview` 是已实现的 `physical_audio`、`explicit_only` 独立动作。设置保存、describe/status/probe/voices 均不会路由到 preview；预览也不会创建知识任务。本轮自动验收只使用 FakePlayer，不运行物理扬声器。
- 模块私有 schema 与接线 fixture 位于 `tests/.../fixtures/settings-companion/`，不会修改 frozen common 或 authority registry。

## 独立编译与验收

当前机器没有 .NET SDK。测试目录中的 `build-and-test.ps1` 使用系统自带的 .NET Framework `csc.exe` 编译本目录源码，不修改根构建。

- `build-and-test.ps1`：模块单测、进程级 Worker CLI、公共 schema/fixture 联合验收及权威候选身份审计。
- `run-core-reuse-compatibility.ps1`：对冻结核心预编译联合套件做兼容验收；其中旧核心可执行文件名只是历史 checkpoint 输入，不构成语音候选身份。
- `run-live-canary.ps1`：通过标准 `run --request` 调用真实 Edge TTS，覆盖中、英、西、日、韩五种整段语言路由，不播放。
- `run-backend-content-speech-canary.ps1`：保留 Content 模块的真实 DeepSeek live 清洗证据，将其最终 `cleaned_text` 适配成不可变 `text.spoken` 工件，覆盖中英文 auto/override 的真实 Edge TTS 合成，以及不触发物理扬声器的自然完成、重播与四种取消原因。
- `validate-backend-content-speech-canary.py`：独立校验上述后端 Canary 的来源链、公共 request/result/artifact/cancel schema、语音与声音路由、每个音频 payload 的 bytes/SHA-256，以及重播新 attempt 和 playback 零复用语义。
- `run-v123-candidate-backend-canary.ps1`：精确绑定 authority v1.2.3 checkpoint、release tree 与其中的 `speech.edge 1.2.0`，运行五语言 auto/override 真实 Edge 合成；播放阶段仅注入 FakePlayer，并把 Core 的 synth 复用与 playback 不复用作为独立调度证据记录。
- `validate-v123-candidate-backend-canary.py`：逐文件复算 authority release tree，验证候选身份、十个五语言音频、六种播放终态及安全边界。
- `validate-v123-candidate-contracts.py`：对 candidate-bound 运行的公共 speech request/result/artifact/cancel 契约和全部 payload bytes/SHA 做补充联合校验。
- `run-live-playback-canary.ps1`：合成一条真实中文音频并通过 ffplay 执行自然完成及四种取消原因；该脚本会短暂从当前默认扬声器发声。
- `validate-live-playback-canary.py`：独立验证真实播放 Canary 的公共 request/result/artifact/cancel schema，以及 manifest 与音频 payload 的 bytes/SHA-256。
- `publish-canonical-release.ps1`：在授权测试目录内创建追加式 `speech.edge/v<version>/candidate-<sha256>/workers/speech/` 搬运根，验证从任意 cwd 执行 descriptor、标准 run、取消/重播与 playback 不复用语义，并生成 release receipt；根 `workers/speech/` 由 Quality 按回执哈希复制。
- `publish-formal-win-x64.ps1`：为当前 `speech.edge` 版本创建时间戳加候选哈希绑定的 append-only formal `win-x64/workers/speech/` release-root；从该根执行 descriptor，验证 settings snapshot 正式 WorkerRequest、离线/实时只读 voice discovery、FakePlayer 显式 preview，以及真实五语言深路径 Edge 合成；不运行物理扬声器，生成树/EXE 哈希和独立回执。

## Legacy compatibility（非权威身份）

- 历史源码目录 `src/RemNoteQuickClipper.Speech.Worker/` 与测试目录名暂时保留，只是仓库迁移路径；程序集与 namespace 根已迁移为 `KnowledgeClipper`。
- `RemNoteQuickClipper.Speech.Worker.exe` 仅由模块构建生成与权威候选逐字节相同的 deprecated alias；新 registry、release layout 与 candidateHash 不得登记该别名。
- `REMNOTE_QUICK_CLIPPER_EDGE_TTS`、`REMNOTE_QUICK_CLIPPER_FFPLAY` 仅在新环境变量未设置时作为 fallback；新配置不得再写入这些名称。
- 旧 `--command synthesize --job-root ...` 参数仅保留为会在 stderr 明确提示 `DEPRECATED` 的开发兼容入口，registry 不得登记该入口。
