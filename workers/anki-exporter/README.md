# Anki Exporter

本模块属于中文产品“零碎知识剪藏”，新一代 ASCII 产品、程序集与 namespace 根名为 `KnowledgeClipper`。

独立消费公共 Artifact 所承载的 `LearningBundle` / `CardBundle`，通过本机 AnkiConnect 把已定型卡片幂等写入 Anki。该 worker 不读取原始剪藏、不执行 AI/OCR，不依赖或协调任何其他终端 exporter。`export.anki` 与 `export.remnote` 是平级、可拔插的末端，二者都不得反向改变上游业务语义。

公共 registry 身份固定为 `export.anki`；`anki-exporter` 只保留为源码目录名称，不得再用于 common request/result、descriptor 或 artifact producer 身份。

planned 权威发布入口固定为 `workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe`。`workers/anki/RemNoteQuickClipper.AnkiExporter.Worker.exe` 仅作为旧 registry/release 的 legacy compatibility alias，保持其冻结的原版本与 SHA-256，不具有新权威身份；`build_exe.ps1` 绝不写入该旧路径。使用 `build_exe.ps1` 通过固定 PyInstaller 6.21.0 构建单文件 console worker；已有新 EXE 默认拒绝覆盖，只有显式 `-Force` 才重建。追加式模块发布必须传独立的 `-OutputDirectory`，或使用 `python build_formal_release.py --release-root <new-release>/win-x64`；后者会把 EXE、设置/probe 局部契约、配置样例、describe snapshot 和 payload manifest 一次性写入全新的 formal release-root，存在同名根目录时失败关闭。

## 接口

公共入口只认工程权威契约：

- `contracts/v1/schemas/common/task-manifest.schema.json`
- `contracts/v1/schemas/common/artifact.schema.json`
- `contracts/v1/schemas/common/worker-request.schema.json`
- `contracts/v1/schemas/common/worker-result.schema.json`

`export` / `validate` Worker Request 必须引用一个 `card-bundle`/`learning-bundle` Artifact 和一个 `anki-export-config` Artifact；`probe` 只允许引用一个 `anki-export-config`。Artifact 的 `metadata.entrypoint` 指向模块专用 JSON；所有 Bundle 媒体必须同时列入该 Artifact 的 `files`。`schemas/card-bundle.v1.schema.json`、`config/anki-export.v1.example.json` 与 `contracts/` 下的设置/probe schema 只定义 Anki 模块局部语义，不是 common schema。

这里的 `LearningBundle` 指已经按 Anki 局部 schema 定型、仍具有 `cards`/`media` 结构且 `bundle_kind=LearningBundle` 的卡片投影，不是 content-worker 的原始 `learning-bundle/1.0.0`。生产 DAG 会由 Core 同时核验原始 CardBundle/LearningBundle，再只向本 worker 交付派生的 Anki CardBundle；禁止绕过 Core 把 content-worker 原始 LearningBundle 直接交给 Exporter。

`export.anki` 1.2.2 同时接受两类导出输入，且不修改冻结 common v1 或初始 `task.manifest`：

- 初始输入：Request 引用与 `task.manifest.inputRefs` 中身份和 SHA-256 完全一致的 `artifacts/<artifactRef>/artifact.json`。
- 可信派生输入：Request 根 Artifact 必须由 `orchestrator.core-adapter` 1.0.0 产生并位于当前 `runs/<runId>/adapters/<stageId>/<artifactId>/v0001/artifact.json`；递归 ancestry 的中间 worker 输出允许来自同一 job 的较早 run，但每个 manifest、payload、相对路径、producer stage/attempt 和 SHA-256 均须通过校验，路径中的 run/stage/attempt 还必须与该 attempt 的不可变 `result.json`、worker/version、输出 ref/hash 完全一致，`unknown_external_state` 不能成为可信输入。所有叶节点仍必须落在初始 `task.manifest.inputRefs`。Core alias 只能复用已验证父 Artifact 文件；任意 job-root 旁路文件、断链、伪造 producer、hash/size 篡改或路径越界均失败关闭。

1.1.1 进一步拒绝 `.`、`..`、反斜杠和 Windows ADS 冒号形成的非规范相对路径；这些路径即使解析后仍位于 job root，也不能作为同一不可变文件的别名。卡片查重则先按全局 `RQC_ID` 搜索，再核对目标 model，避免 note type 映射变化后尝试创建第二份笔记。1.1.2 将 AnkiConnect endpoint 收紧为精确的 `http://127.0.0.1:<port>`，拒绝 userinfo、路径、查询、fragment 和非法端口；同时在 validate-only 阶段校验有限正超时、布尔 provision 开关、deck 名与无歧义 note field mapping，避免把配置错误推迟到 live 阶段。1.1.3 只迁移上层产品与发布身份到 `KnowledgeClipper`，不改变卡片、媒体、幂等或 AnkiConnect 业务语义。1.1.4 完成 `language_writing` / both 严格 finalized-bundle loader：拒绝 `choice`/decision token 等上游决策字段下渗、未映射字段与 note type 串线。1.1.5 在保持 producer、当前 run/stage/attempt、manifest/payload SHA 与逐层 ancestry 校验的前提下，兼容公共 worker 当前发布的 `outputs/*.artifact.v1.json` 扁平 manifest；任意扩展名、跨 run/stage/attempt 与旁路文件仍拒绝。Core additive v2.1 `ankiProjection.language_writing` 与 Content 纠正 authority 已落盘并通过 Core→Anki planned EXE validate-only；旧 `033810` 输出保持降级，不再被活动验收读取。

1.2.0 新增模块专用轻量设置 adapter 和 `probe` operation。设置 adapter 只把项目设置投影为新任务的 destination snapshot、Core tags patch 与 SHA 绑定 `anki-export-config` Artifact；`WorkerRequest.configSnapshot` 只允许空对象或 `ankiConfigArtifactId` 身份引用，不复制配置正文。`provision_managed` 默认 `false`，初始化缺失 model/deck 仍必须由单独且明确有外部写副作用的动作触发。`probe` 只消费一个 config Artifact，仅允许 `requestPermission`、版本/profile/API action 与 model/deck 读取，严格禁止 create/store/add/update/tag/changeDeck；输出 `anki-connection-status`，不读取卡片也不写 Anki。模块专用 descriptor/schema 位于 `contracts/`，不得被当作 common authority。

1.2.1 将 companion descriptor 补齐为固定 `default` 单实例、用户 `enabled` 只影响新 task destinations、registry `enabledByDefault` 仅表示 worker 可用，以及 common/advanced/diagnostic、next-task/migration-required/read-only 的机器可读分层。1.2.2 仅追加跨-run ancestry 兼容与 producer-result 交叉验证，不改变设置、probe、卡片、媒体、幂等或 AnkiConnect 业务语义。`validate` 明确为零网络的本地只读校验；`probe` 是需要用户显式触发、可能出现 AnkiConnect permission 提示的 loopback provider-read-only 操作，只会写本地不可变 status/receipt，不改变 provider 状态。status 默认仅保留 profile/model/deck 的 SHA-256，不输出这些标识明文；`secretRefs` 仍为空。

1.3.0 新增尚未激活的 original-media destination candidate：CardBundle 1.1 用 `role=questionImages|answerImages` 和各自从 0 连续的 `ordinal` 表达 CaptureBundle 的两组有序原图，`anki-export-config.v2` 以固定 `ordered-original-images.v1` 投影到 `question→Front`、`answer→Back`。题图必须整体先于答图；文本、图片之间及相邻图片之间精确使用 `<br>`，图片引用为 `<img src="rqc_<sha256>.<ext>">`。顺序、role、ordinal、kind 与媒体 SHA 全部进入 `content_hash`；空 answer 和空 `answerImages[]` 均允许。媒体仍使用既有内容寻址文件名、同名异内容拒绝、写后 readback、全局 RQC_ID、revision 冲突与 unknown state 语义。

1.3.1 对 ancestry 做来源中立的零复制兼容：普通中间 artifact 仅可透明引用其递归父链中、已由 canonical `task.manifest` 初始 artifact manifest SHA 绑定且文件 `path/sha256/bytes/mediaType` 完全一致的初始文件。任意未声明同目录文件、断开的初始根、descriptor 不一致或非 canonical manifest 仍以 `ARTIFACT_FILE_BYPASS`/既有门禁拒绝。最终 Core adapter 仍应在自身 `v0001` 目录创建内容地址化硬链接或复制文件，避免把上游路径直接写入 CardBundle。

1.3.2 将扁平公共 artifact manifest 从硬编码的 `*.artifact.v1.json` 修正为无前导零的正整数版本 `*.artifact.vN.json`，覆盖 Content 的 `learning-bundle.artifact.v2.json` / v3 等 additive contract 版本；stage/run/attempt 仍逐段使用公共 identifier 规则并与 producer/result 精确相等。`..`、反斜杠、百分号编码别名、非法版本段、身份不一致与跨 job 路径继续 fail closed。

1.3.3 为显式只读 `probe` 增加严格 bootstrap 失败回执：当 WorkerRequest 本身通过完整公共字段、路径、config 引用校验，但生产者遗漏强制 `task.manifest.v1.json` 时，worker 在构造 `AnkiClient` 前发布 `failed/FILE_MISSING`、`sideEffects=[]` 的公共 result，避免被上层误判成 `unknown_external_state`。该路径不会补造或跳过 task manifest，不适用于 `export`，也不改变任何外部写入后的 unknown-state 语义。

1.4.0 将 managed model/deck 初始化收口为独立 `initialize-managed` operation 与同名 CLI。请求只消费一个 SHA 绑定 `anki-export-config`，要求 `provision_managed=true`、`requestedOutputs=[anki-managed-initialization-status]`，并携带精确绑定 task/config 的 `sideEffectAuthorization`（`mode=explicit-user-action`、`scope=managed-models-and-decks`）。worker 先只读核验 permission/API/profile、全部现存 model 的 fields/template/CSS 与 deck 集合；有冲突时在任何写入前失败，只对缺失资源调用 `createModel`/`createDeck`，随后逐项精确 readback。它绝不调用 note、tag、changeDeck 或 media 写操作，也不修改既有 model/deck。响应丢失或写后读回不确定时返回 `unknown_external_state`、`retryable=false`，只允许用户再次显式触发新任务包，重放时先查询并仅创建仍缺失项。普通 `export` 不再因 `provision_managed=true` 隐式建资源；缺失时提示先执行显式初始化。

1.4.1 为 `anki-export-evidence` 追加 `readback.json`（`anki-export-readback.v1`）。每个成功/已存在卡片按 CardBundle 顺序保存脱敏后的 `notesInfo`/`cardsInfo` 语义正文，包括 note/card ID、全部受管字段（明确排除 `UserNotes`）、必需字段、实际/必需标签和 deck；逐卡 canonical body SHA-256、整个卡片数组 SHA-256、receipt 引用 SHA-256 与 Artifact manifest 文件 SHA-256 共同绑定证据。profile、endpoint、modelName、UserNotes 和秘密不进入该证据。空 card IDs、重复 note/card ID、缺失/错配 fields/tags/deck 均失败关闭；写入、幂等与 `unknown_external_state` 语义不变。

1.5.0 新增独立 `semantic-readback` provider-read-only postflight。它消费与导出相同的 SHA 绑定 CardBundle/config，但只调用 `requestPermission`、`version`、`getActiveProfile`、`apiReflect`、`findNotes`、`notesInfo`、`cardsInfo` 和必要的 `retrieveMediaFile`；不调用任何 create/store/add/update/tag/changeDeck。请求必须逐 ordinal 携带最终预览的 stable `cardId`、`front`、`back`、唯一目标 deck，并可携带该次导出返回的 `noteId`/`ankiCardId` 做精确身份绑定。缺卡、内容/顺序/deck/归属漂移均确定性失败且 `sideEffects=[]`，绝不自行 reconcile 或再次 export；只有原导出写后状态未知时，编排器才应先调用该 readback，再由新的显式导出任务决定是否幂等收敛。`semantic_readback_gate.py` 进一步在离线环境把 preview、完整确认命令、WorkerRequest/Result、Artifact manifest 与 readback 的 SHA 串联起来，拒绝“数量相同但内容或顺序错误”。该 gate 的 `externalMutation=false` 只描述本次验证，不宣称此前场景没有发生写入。

原图模式的唯一目的端固定为 Anki，正式投影为 `ordered-original-images.v1`；RemNote 不接收该目的端分支。Core 仍必须通过正式 typed input contract 证明 CaptureBundle 的 `questionImages[]`/`answerImages[]`、源 Artifact hash 与 ancestry，并生成符合 `contracts/CORE-ORIGINAL-MEDIA-PROJECTION.md` 的派生 Artifact。若某一输入尚不满足 typed contract，resolver 应把 Anki 该目的端标为 `conditional/unsupported`，不能把整项任务误报成功；这不再表示产品路线延期。真实 Anki 写入仍只允许既定隔离 deck，并须另行显式授权。

候选绑定离线验收使用 `python tests/run_formal_release_settings_probe_canary.py --release-root <absolute-win-x64>`。该脚本通过 PowerShell 7 加载已构建的真实 Core 程序集，调用 `TaskSettingsAdapter` 冻结 settings snapshot，再由 `OrchestratorEngine` 生成 WorkerRequest 并从指定 release-root 启动 worker；validate-only 不构造 AnkiClient，probe 仅连接随机端口的 Fake Anki。任务包、失败包和最终 receipt 均只追加，不覆盖；禁止用该命令连接真实 `127.0.0.1:8765`。

`language_writing` 的生产 readiness 可用 `python .\check_language_writing_readiness.py` 只读检查；默认读取 `20260826T041418150Z-offline-language-practice-v2-replay-e7deb47e` 纠正 authority，也可用工程相对 outputs 目录覆盖 `--language-output <path>`。该命令不 trim Exporter 字段、不运行 Core、不连接 Anki，并以 JSON 明示 `ready` 或 `pending_upstream_schema`。只有纠正 authority 的有序 `language_context` + `language_writing` 两卡、Core 独立普通正向投影以及 Anki `Front`/`Back` 映射全部出现后，consumer acceptance 才会进入全量 validate-only。写作卡正面必须把原句中唯一一次 `target_term` 完整替换为全角 `（读音；意思）`，正面不得残留原词，背面逐字保留 `target_term`；`language_context` 不变。Exporter 只透明消费这些最终字段，不读取或决定 `choice`/decision token。

这只是 Anki loader 对公共 manifest/artifact 的消费规则，不新增或替代 common schema；`contracts/v1` 仍是唯一权威。

早期的本地 manifest 已降级为 `fixtures/legacy-anki-adapter-manifest.v0.schema.json`，只用于迁移取证，运行时代码不再读取，其他模块不得引用。

卡片 `content_hash` 是以下规范化 JSON 的 SHA-256：`card_id`、`revision`、`deck_route_key`、`note_type_key`、`fields`、排序后的 `tags`，以及按 `media_id/field` 排序且带媒体 SHA-256 的 `media_refs`。可直接调用 `compute_content_hash()` 生成。

媒体只能来自公共 job root 内、且由输入 Artifact 明确拥有的相对路径。字段用 `{{media:<media_id>}}` 指定插入点；Exporter 转换为 Anki 图片或音频语法。Anki 文件名为 `rqc_<完整内容哈希>.<扩展名>`，同名异内容会失败而不是覆盖。

`RQC_ID` / `RQC_Revision` / `RQC_ContentHash`、`rqc_*` 媒体名、`rqc_managed`、`RQC Basic v1` 以及现存 `application/vnd.rqc.*` 只是为已存在 Anki 笔记、媒体和 Artifact 保留的 legacy compatibility alias。它们不是新一代产品、程序集或 namespace 权威身份；本版不改名，以避免破坏已有幂等、更新与真实 Canary 数据。

## 运行

公共 CLI 严格遵循 `contracts/v1/worker/worker-cli.v1.json`：`describe` 只向 stdout 输出一个 worker descriptor；`run` 只接受绝对 `request.json` 路径，stdout 只输出 progress NDJSON，并原子发布一次 `result.json`。操作由公共 Worker Request 的 `operation` 决定：`validate` 不接触 Anki，`probe` 只做显式只读连接/状态检查，`semantic-readback` 按最终预览与可选 note/card IDs 做只读 postflight，`export` 执行真实导出，`initialize-managed` 只在显式 side-effect authorization 下创建缺失 managed resources。额外同名 CLI 只是对标准 request 的安全别名，不绕过 task/config/authorization 校验。

```powershell
python .\anki_exporter.py describe --json
python .\anki_exporter.py run --request <absolute-path-to-request.json>
python .\anki_exporter.py initialize-managed --request <absolute-initialize-request.json>
python .\semantic_readback_gate.py --manifest <absolute-gate-input.json> --output <new-append-only-receipt.json>
```

项目级设置 adapter 使用独立模块契约，不改 common descriptor，也不修改既有 task manifest：

```powershell
python .\anki_settings_adapter.py describe --json
python .\anki_settings_adapter.py adapt `
  --settings <absolute-settings.json> `
  --job-root <absolute-new-job-root> `
  --artifact-id <artifact-id> `
  --created-at <RFC3339> `
  --attempt-id <attempt-id>
```

未来真实只读连接测试的统一入口是已绑定 release-root 中的 `workers\anki\KnowledgeClipper.AnkiExporter.Worker.exe run --request <absolute-probe-request.json>`。该 request 的 `operation` 必须为 `probe`，只引用 SHA 绑定的 `anki-export-config`，`configSnapshot` 仅含 `ankiConfigArtifactId`；控制面必须作为显式用户动作调用，不得复用 export live preflight，也不得把 registry availability 当作 destination enabled。

发布 EXE 与独立验证：

```powershell
.\build_exe.ps1
python .\validate_exe.py
```

真实模式要求 Anki 正在运行、AnkiConnect API v6 可达，且 `expected_profile` 已填写并与活动 profile 精确一致。缺少环境时返回 `BLOCKED_ENVIRONMENT`；不会直接写 `collection.anki2`，也不会自动同步 AnkiWeb。

Attempt 路径严格使用公共结构 `runs/<runId>/stages/<stageId>/attempts/<attemptId>/`：请求为 `request.json`，结果为 `result.json`，取消信号为 `cancel.json`，grace 固定 2000 ms。Worker 只在 Request 指定的 `outputs/` 写模块回执；进度经 stdout 交给 orchestrator 写入 `progress.ndjson`。存在既有结果或输出目录时拒绝覆盖。`operations.ndjson` 不记录卡片正文或媒体字节。

Anki 写入开始后若响应或终态发布失败，结果必须是 `unknown_external_state`、`retryable=false`、零 outputs，禁止编排器自动 retry。后续只能显式 reconcile：先查询固定 `RQC_ID` 对应的 note、media 和 deck，再用新任务包按 revision 收敛，并在新 Request/Canary 回执中关联旧 attempt。

未分类的内部异常也必须发布 common result：尚未尝试副作用时为 `failed/WORKER_INTERNAL_ERROR`；已尝试副作用后为 `unknown_external_state`、`retryable=false`、零 outputs。回执只记录异常类型，不把异常正文或潜在敏感数据写入公共错误消息。

## 身份与更新

- `RQC_ID` 是模型第一字段和唯一业务身份。
- 更高 `revision` 更新托管字段并保留 `UserNotes`。
- 同 revision 同 hash 为 `ALREADY_CURRENT`；同 revision 异 hash 为 `REVISION_CONFLICT`；旧 revision 为 `STALE_INPUT`。
- Bundle 缺少某卡不代表删除；`unresolved` 被记录为 `BLOCKED_UPSTREAM`。
- 模型、字段或重复身份冲突时失败关闭。

## 测试

```powershell
python -m unittest discover -s tests -v
```

测试使用真实 common task/artifact/request/result 结构和独立 loopback 假 AnkiConnect，覆盖 validate-only 零连接与畸形配置、创建、重跑幂等、升级更新、deck 重路由、note type 冲突的全局身份保护、媒体大小门禁、媒体读回、note/media 响应丢失及显式 reconcile、AnkiConnect 集合响应类型错误的失败回执、初始输入回归、可信 Core 派生 ancestry、伪造 producer、断链、manifest/payload 篡改、路径越界、非规范路径别名、旁路文件拒绝和环境阻塞。真实 Anki Canary 需要另行在 `零碎知识剪藏::Canary` 中执行并保留固定 Canary 卡。

模块级 `content-to-anki.v1` consumer acceptance 使用不可变的 projection-v1 基础夹具，并固定消费 Content authority `20260826T041418150Z-offline-language-practice-v2-replay-e7deb47e` 的真实双向语言 CardBundle/LearningBundle；标准无参数入口会先核验 authority receipt、artifact manifest 和 payload 的精确 SHA-256，再执行当前 Core adapter 和 planned EXE validate-only。它验证 content hash、字段、route/note type、四卡顺序与派生 ancestry，并验证 `media_qa`/`asset_ref` 在 Core 发布 Artifact 前显式拒绝。每次通过都会从该轮真实输入、派生产物与 Worker Result 自动生成可读样例并把路径/SHA 写入同轮回执。该测试不连接真实 Anki，也不作为桌面端全链 Canary 门禁：

```powershell
pwsh -File ..\..\tests\RemNoteQuickClipper.AnkiExporter.Tests\run-content-adapter-compatibility.ps1
```

真实 Content 0.5 DeepSeek 目的端 Canary 使用独立 deck `零碎知识剪藏 Canary`。脚本只接受完成的 live `content.deepseek` 0.5.0 CardBundle/LearningBundle，调用当前 Core `content-to-anki.v1` 后执行首次写入、同 revision 幂等重跑和逐卡 AnkiConnect 回读。所有 deck route 都钉死到该 Canary deck；若任一 `RQC_ID` 已存在于其他 deck 会在写入前中止；不删除、不清理、不输出活动 profile 或配置正文：

```powershell
python .\run_content_live_destination_canary.py `
  --content-canary-root '<已完成的 Content 0.5 live Canary 根目录>'
```

本机 Anki 与 AnkiConnect 就绪后运行真实固定 Canary：

```powershell
python .\run_live_canary.py --profile '<当前活动 Anki profile 的精确名称>'
```

若需收敛一个已记录为 `unknown_external_state` 的旧 attempt：

```powershell
python .\run_live_canary.py `
  --profile '<当前活动 Anki profile 的精确名称>' `
  --reconcile-request '<旧 attempt 的绝对 request.json>'
```

Canary 使用固定 `RQC_ID=rqc-canary-fixed`，每次选择更高 revision，执行写入、同 revision 重跑、再次更新，并独立回读 `UserNotes`、卡组、媒体与身份唯一性。每次运行写入唯一 `canary/<runId>/`，旧回执不覆盖。

registry 身份迁移后可只做同 revision 的真实 no-op 回读；该模式要求 operation log 中不存在任何 mutating Anki action：

```powershell
python .\run_live_canary.py --profile '<当前活动 Anki profile 的精确名称>' --noop-readback
```
