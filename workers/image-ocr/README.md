# Image / OCR Worker

本目录只实现图像与 OCR 模块，不负责截图 UI、文本清洗、制卡、语音或任何输出端。

公开产品展示名为“零碎知识剪藏”，新一代程序集根名为 `KnowledgeClipper`。本 worker 在所有目的端之前运行，只消费 capture bundle 并产出图片/OCR artifacts；它不知道、选择或调用任何末端输出端。

## 已确认边界

- common task manifest / worker request 只引用 `kind: capture-bundle` 的 artifact；本模块不定义或复制 common schema。
- capture bundle 必须提供 `questionImages[]` 和 `answerImages[]` 两个显式有序 artifactRef 数组；两组均可多图，answer 可空。
- `original_passthrough` 回传原始 artifactRef，并额外发布 destination-neutral `image.original.bundle` 与 `image.processing.provenance`；不加载 MinerU，也不访问网络。
- `ocr_mineru` 表示本次图片允许上传 MinerU。Token 只从进程环境变量 `MINERU_API_TOKEN` 获取。
- 任一图片失败时结果为 `partial` 或 `failed`，禁止自动下游。重试只处理失败图片，成功图片按原 hash 和旧 attempt 引用复用。
- 每次运行只在 common request 指定的 `runs/<run>/stages/image-ocr/attempts/<attempt>/` 追加；已有结果、产物和回执都拒绝覆盖。
- common `cancelSignal` 贯穿 MinerU 与发布边界；`timeout` 映射为可重试 `failed`，其余原因映射为不可重试 `cancelled`。
- request/result 仅携带 `retentionDays: 30` 元数据；当前没有生命周期执行器，不会自动删除整包，能力状态必须保持 `unsupported`。

## Capture bundle 边界

桌面端的权威结构是 `contracts/desktop-automation/v1/capture-bundle.v1.schema.json`。模块只读取显式数组，不从文件名、`artifacts[]` 排列或目录遍历推断角色：

    {
      "schemaVersion": "capture-bundle.v1",
      "captureId": "capture-001",
      "modeSnapshot": { "imageHandling": "ocr" },
      "artifacts": [
        {
          "artifactRef": "capture.image.question.0001",
          "relativePath": "inputs/images/0001.png",
          "sha256": "...",
          "bytes": 123,
          "mediaType": "image/png"
        }
      ],
      "questionImages": ["capture.image.question.0001"],
      "answerImages": []
    }

`questionImages[]` 整组在前、`answerImages[]` 整组在后；各组内部数组顺序保持不变。一次 bundle 的全部图片属于同一张卡。

由于原图直通必须原样保留 capture artifactRef，而 common artifact identifier 上限为 128 字符，本 adapter 对 capture artifactRef 同样执行 128 字符上限；超限返回 `capture_ref_common_incompatible`，不做静默别名。

## Common adapter

权威 workerId 是 `image.ocr.mineru`。registry 生产入口只使用计划路径 EXE 与冻结的 worker-cli-v1：

    KnowledgeClipper.ImageOcr.Worker.exe describe --json
    KnowledgeClipper.ImageOcr.Worker.exe run --request C:\...\jobs\<taskId>\runs\<run>\stages\image-ocr\attempts\<attempt>\request.json

`run --request` 只接受实际存在的绝对 `request.json`，并从 request 位置与 `attemptPath` 反推 `jobs/<taskId>` 边界。stdout 只输出 progress NDJSON；provider 诊断只走 stderr；`result.json` 通过同目录临时文件和原子 create-once 发布。

common request envelope 通过冻结 schema 与路径校验后，后续 capture 完整性、capture 内容或 provider 配置的 preflight 失败也必须发布 schema-valid terminal `result.json`，并保持 `outputs: []`；只有无法建立可信 job/attempt/result 边界的 envelope/CLI 错误才只写 stderr。显式配置的 `mineruScriptPath` 若不存在会直接返回 `provider_unavailable`，不得静默改用其他 provider 路径。

Windows 上传给 provider 的下载目标使用长路径兼容形式；MinerU 的长 `dataId` 即使把 ZIP 落点推过传统 260 字符边界，也不能退化为 `artifact_download_failed`。

构建计划 EXE：

    pwsh -File build-worker.ps1

脚本固定 PyInstaller 版本，以 create-once 方式生成 `workers/image-ocr/KnowledgeClipper.ImageOcr.Worker.exe`，随后执行 `describe --json` 身份检查并输出 EXE bytes/SHA-256。MinerU provider 路径按 `configSnapshot.mineruScriptPath`、`KNOWLEDGE_CLIPPER_MINERU_SCRIPT`、legacy 环境变量、开发机既有集成依次发现；显式配置必须是绝对路径。

`RemNoteQuickClipper.ImageOcr.Worker.exe` 明确且仅作为 legacy compatibility alias 保留；它不是新候选入口，不得进入新的 registry/release checkpoint，也不由新构建脚本覆盖。`application/vnd.rqc.*`、`RQC_MINERU_SCRIPT` 与模块 v1 schema 中的 `rqc` URI 同样只作为冻结 v1 compatibility token 保留。

模式完全取自 capture bundle：`imageHandling: original` 走原图直通且不加载 provider；`imageHandling: ocr` 才允许调用 MinerU。adapter 发布：

- common `result.json`，严格使用核心 `worker-result.schema.json`；
- 每个输出的 common `artifact.json` 与 artifactRef；
- 模块诊断 `module-result.json`，保留角色、组内顺序、OCR 引用和重试信息；
- 失败详情、`submission.json`、`resume.json` 与脱敏 provider 回执。

`partial` 会在结果中声明阻断自动下游。后续 attempt 通过 payload 的 `retryModuleResultPath` 只重试失败 item；成功 item 必须通过源图及输出 SHA-256 校验后才能复用。

取消信号在 submission、poll、resume、下载、ZIP 解包、每个 item 以及模块/common 输出发布前连续检查。同步 provider 调用由守护线程承载，主 worker 每 25ms 轮询 cancel signal；`user_stop`、`superseded`、`task_cancel` 发布 `cancelled`，`timeout` 发布可重试 `failed`，所有取消终态的 common outputs 均为空。离线黑盒回归分别覆盖 submission 中断、既有 batch 的 resume/poll 中断，以及 poll 阶段四种 reason 的 2000ms grace。

## 模块内核（测试/诊断）

模块内核的 `ImageRequest v1` 位于 `schemas/`，仅用于隔离测试和 provider 诊断，不是 common 契约替代品：

    python image_ocr_worker.py --package <task-package> --request inputs/image-request.v1.json

旧 `--package/--common-request/--mineru-script` 入口也只保留内部测试与诊断，不得登记为 registry entrypoint。

MinerU OCR：

    python image_ocr_worker.py --package <task-package> --request inputs/image-request.v1.json --mineru-script <mineru_ocr.py>

重试失败图片：

    python image_ocr_worker.py --package <task-package> --retry-result stages/image-ocr/<old-attempt>/result.json --mineru-script <mineru_ocr.py>

恢复已提交的 MinerU batch：

    python image_ocr_worker.py --package <task-package> --resume-submission stages/image-ocr/<old-attempt>/submission.json --mineru-script <mineru_ocr.py>

## 精简设置 companion（worker 1.2.3 候选）

`image_ocr_settings_adapter.py` 是模块内、目的端中性的设置投影层。它只接受：

- 常用：`mode: ocr | original`；
- 进阶：`modelVersion`、`language`、`enableTable`、`enableFormula`、`timeoutSeconds`、`pollIntervalSeconds`；
- 部署：绝对 `mineruScriptPath`；
- 唯一本地凭据引用：`local-credential://image-ocr/mineru-api-token`，不接受或保存 Token 原值。

查看 capability descriptor 与投影一个设置文件：

    python image_ocr_settings_adapter.py describe --json
    python image_ocr_settings_adapter.py adapt --settings C:\absolute\image-ocr-settings.json

companion `1.3.0` 将静态 descriptor、实时本地状态和测试动作分离：

    python image_ocr_settings_adapter.py status --settings C:\absolute\image-ocr-settings.json --credential-metadata C:\absolute\credential-metadata.json --json
    python image_ocr_settings_adapter.py validate --settings C:\absolute\image-ocr-settings.json --json
    python image_ocr_settings_adapter.py test --settings C:\absolute\image-ocr-settings.json --credential-metadata C:\absolute\credential-metadata.json --action image.ocr.test.offline-fake-provider --json

可直接消费的静态文件是 `descriptors/image-ocr-capability-config-descriptor.v5.json`。四个 actionId 为 `image.ocr.status.offline-readiness`、`image.ocr.test.validate-only`、`image.ocr.test.offline-fake-provider`、`image.ocr.test.live-connectivity`。前三者禁止网络和外部上传；live action 仅声明为 `delegated-external-upload-v1`、`explicit-user-action-only`，并明确 `externalUpload=true`、`requiresConsent=true`，本模块不会把它当作自动 status probe，也不会在离线测试中运行。

投影结果分别给出 capture 的 `modeSnapshot.imageHandling`、common `WorkerRequest.configSnapshot`、内部派生的 `mode/cloudUploadAuthorized`、进程环境 secret binding 和只读 Core stage 事实。original 投影会主动省略全部 provider 配置，且运行时 resolver 不会读取凭据。OCR Token 原值只允许由 Desktop DPAPI CurrentUser store 在启动 Core 前解析并注入父进程环境，Worker 仅继承 `MINERU_API_TOKEN`；settings、task、stdout/stderr、result 与 receipt 均不得包含原值。实时 status 的 credential 对象严格只有 `configured`、`verified`、`sourceType`。

严格范围为：model=`vlm|pipeline|MinerU-HTML`，language 为 1–32 字符 provider code，timeout=30–3600 秒，poll=1–60 秒且必须小于 timeout，table/formula 必须是真布尔值。未知键、字符串布尔、布尔型数字、相对脚本路径和原始 Token 字段均在 provider 启动前失败。

状态不会把跨模块缺口冒充可用：当前 Core 的 `originalFullPipeline` 为 `unsupported`，`partialFailedOnlyAutomaticContinuation` 已由 Core v2.5 验证为 `available`，`retentionExecution` 为 `unsupported`。30 天仅是现有 request/result 元数据，不代表存在自动删除执行器。provider timeout 与只读 Core stage timeout/retry 分开显示。

可读输入与预期映射位于 `fixtures/settings-ocr.v1.json`、`fixtures/settings-original.v1.json`、`fixtures/credential-metadata-dpapi-verified.v1.json` 和 `fixtures/settings-adapter-readable-example.v2.json`。Core 对 original、partial 与人工 accept/correct continuation 的模块边界证据位于 `fixtures/core-continuation-contract-evidence.v4.json`。人工决定默认不重跑 OCR，必须复用原图与成功 Markdown/structured 的 SHA；Desktop review renderer 与目的端媒体消费仍是待接线项。

正式只追加候选由 worker `1.2.3` 与 companion `1.3.0` 共同承载：

    pwsh -File build-companion-candidate.ps1

release-root 位于 `candidates/1.2.3/`，包含 `KnowledgeClipper.ImageOcr.Worker.exe`、`KnowledgeClipper.ImageOcr.Settings.exe`、descriptor、schema、Core continuation evidence 和 `build-receipt.json`。它不会覆盖当前 authority；在 Core/Desktop provenance admission 与新 registry/release checkpoint 建立前，onboarding 保持 NO-GO。

## 验证

    python -m unittest discover -s tests -v

离线测试使用合成 PNG 和假 provider，并直接用仓库中的权威 capture/common schemas 验证 request、result 和 output artifact manifests，不接触 MinerU、任何输出端或其他生产服务。

`fixtures/offline-two-image.v1.json` 是模块的纯合成、无隐私 fixture；其 schema、图片 bytes/hash、显式题图/答案图数组、故意反向的 `artifacts[]` 和恢复预期均为可校验输入。

保留可直接查看真实输入与输出的版本化离线 Canary：

    python run-offline-canary.py
    python run-offline-canary.py --scenario partial-retry
    python run-offline-canary.py --scenario resume
    python run-offline-canary.py --scenario original
    python run-offline-canary.py --scenario provider-failed
    python run-offline-canary.py --scenario tampered-input
    python run-offline-canary.py --scenario unknown-external-state

`success` 通过计划 EXE 运行显式题图和答案图，并故意让 capture `artifacts[]` 与 provider 返回顺序相反；除逐图 Markdown/structured 外还发布 provenance。`partial-retry` 的首个 attempt 只让答案图失败并发布阻断下游的 `partial`，第二个 attempt 通过 `retryModuleResultPath` 只调用失败答案图；题图 item 和两份 OCR 产物必须按旧 hash 复用，provenance 明确 `defaultOcrRerun=false`，最终发布 `completed`。`resume` 从已有 batch receipt 恢复 provider poll，必须发布 `resume.json` 且不得产生新的 `submission.json`。`original` 使用同一输入包验证原图直通：发布逐图 `image.original`、一个有序 `image.original.bundle` 和一个 provenance；输入 SHA 必须逐项保持，provider 不得启动，也不得生成 submission/resume 回执。`provider-failed` 注入两图全量 Provider 失败，要求 terminal `failed`、逐 item 诊断和空 common outputs。`tampered-input` 在不可变包首次写入时故意写入与声明 hash 不符的题图；图片内容完整性由模块输入解析层检查，因此要求模块和 common result 均为 `integrity_mismatch`、空 outputs 且 Provider 零调用。`unknown-external-state` 在 submission 抛出异常，模拟无法确认 MinerU 是否已受理：必须发布非重试 `unknown_external_state`、空 outputs，且不得伪造 `submission.json`。七个场景都保留 common `result.json`、每 attempt 的 stdout/stderr 与脱敏回执；有模块执行的场景另保留 `module-result.json`，OCR 成功场景保留 Markdown 预览和 provider 调用记录。每次执行新建唯一 Canary 包，不覆盖旧结果；运行器自身异常也会追加 `failure-diagnostic.json`。

`run-live-canary.ps1` 会新建唯一、不可覆盖的 Canary 包，并生成两张题图与两张答案图。`-ImageHandling ocr` 通过中性候选 EXE 执行真实 MinerU OCR，保留原图、submission/脱敏 provider 结果、原始 ZIP/解包结构、逐图 Markdown/structured JSON、按显式角色顺序聚合的 `readable-ocr.md` 和仅供 Core `content-input.v1` adapter 消费的 `content-adapter-handoff.json`。凭据只接受调用方进程提供的 `MINERU_API_TOKEN`，本模块不从任何目的端或知识库发现凭据。

`-ImageHandling original` 对相同的四张合成图做原图直通：运行前主动清空子进程的 token 和 provider 脚本环境，要求四个 `image.original` 的 SHA-256 与输入逐一一致，同时发布显式 question/answer 顺序、media type 与 SHA 的 destination-neutral media bundle 和 provenance，且不得出现 submission、resume 或 raw MinerU 痕迹。两种模式都保留 common `result.json`、`module-result.json`、stdout/stderr、逐文件 SHA 和可诊断的 terminal/partial 语义；只有 `completed` OCR 才会发布 Content adapter 交接清单。

不调用 MinerU 的包布局与身份预演：

    pwsh -File run-live-canary.ps1 -PrepareOnly -ImageHandling ocr
    pwsh -File run-live-canary.ps1 -PrepareOnly -ImageHandling original

预演同样创建唯一任务包和 `preparation-receipt.json`，但明确记录 `providerCalled: false`、`credentialRead: false`。

公开身份审计：

    python audit-public-identity.py

审计要求 candidate 文件名、`describe --json`、源码诊断和 live Canary 均不含任何末端产品身份；旧文件名只允许出现在明确标注的 legacy compatibility alias 记录中。
