# KnowledgeClipper Orchestrator v2

“零碎知识剪藏”的独立 `.NET 8` 主编排器。核心只负责任务、阶段、恢复、复用、取消和 worker 调度，不实现桌面交互、OCR、内容 AI、TTS 或任何目标端业务。

## 身份与架构

- 程序集与 namespace 根：`KnowledgeClipper`
- componentId：`knowledge-clipper.orchestrator`
- 处理 profile：`learning`、`cards`
- 可选目标端：`remnote`、`anki`
- 默认目标端为空；新任务必须显式选择目标端，或者仅生成中间学习产物。
- `export.remnote` 与 `export.anki` 是并列、可选的末端 worker；两者可由同一 `cards` pipeline 并行调度。

## 兼容边界

旧程序集、namespace、componentId 和 `parity-remnote` / `cards-remnote` / `cards-anki` 仅作为显式 legacy compatibility alias 接受。别名会在 resolved pipeline 的 `routing.source` 中留下可审计标记，不是新权威身份。

公共冻结 v1 不改写；新身份、manifest、intent、profile resolver 与 pipeline 使用 `contracts/core/v2` 的追加式契约。

## 构建

- SDK：`.NET 8 SDK 8.0.424`
- canonical project：`orchestrator/KnowledgeClipper.Orchestrator.csproj`
- canonical executable：`KnowledgeClipper.Orchestrator.exe`
- legacy project-file alias：`orchestrator/RemNoteQuickClipper.Orchestrator.csproj`
