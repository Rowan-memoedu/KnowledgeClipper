# KnowledgeClipper RemNote 插件

独立的剪藏官方 SDK 插件，插件 ID `knowledge-clipper`。不依赖 MCP Bridge、MCP server、原 fork 的源码、插件存储或运行服务。源码与桌面剪藏工具在同一个仓库。

安装本地插件 URL：`http://localhost:43113/`。连接专用 host 的 `ws://127.0.0.1:43112`。HTTP 客户端使用 43111。启用时默认允许导入，可在插件设置关闭。

`npm ci`、`npm run typecheck`、`npm test`、`npm run build`。构建默认输出 `D:/Artifacts/KnowledgeClipper/standalone-remnote/plugin`，可设置 `KC_PLUGIN_OUTPUT`。

## 受限协议

hello 为 `{type:'hello',protocol:'knowledge-clipper.transport.v1',pluginId:'knowledge-clipper',version:'1.0.1',capabilities:[...]}`。请求 `{id,action,payload}`，响应 `{id,result}` 或 `{id,error}`。仅允许 `knowledge_clipper_health`、`knowledge_clipper_excerpt_documents`、`knowledge_clipper_reveal`、`knowledge_clipper_reveal_state`、`knowledge_clipper_export`、`knowledge_clipper_readback`、`knowledge_clipper_prepare_image`、`knowledge_clipper_finalize_media`、`knowledge_clipper_finalize_spacing`、`knowledge_clipper_repair_plain_structure`、`knowledge_clipper_test_plain_structure`、`knowledge_clipper_adopt`。目录查询只读，返回“零碎知识剪藏 → 摘录”下直接子 Document 的 ID 与标题；不提供任意脚本或 MCP 工具调用。

后台传输由独立 `transport-worker.js` 维持，SDK 调用仍留在 index widget；导航等待经 host 的有界 `wait` / `wait-result` 消息唤醒。能力标记为 `knowledge-clipper.background-transport.v1`，协议边界与最小化验收要求见 [host 文档](../remnote-host/README.md)。

## 旧身份迁移

新插件的 storage 与旧插件隔离。切换前 host 必须用已冻结计划和原回执迁移每个已导入 source；Unknown 不能当作新 source。

`adopt` 输入 `{plan,expected,verifiedNodes}`。`expected` 为 `clipIdentity(oldReadback)`，`verifiedNodes` 为 `clipNodeSnapshot(oldReadback)`。插件通过 SDK 真实读回所有原 Rem、根和模式目录，核验计划语义、完整 ID、卡片身份和原快照后，只写自己的同步 journal，绝不创建或修改 Rem。媒体字节由 host 对前后回读逐图校验。部分/未知回执无法提供完整证据时阻塞。中断的 journal 迁移可用同一证据重试。

空 journal 的 export 必须显式传 `{plan,newSource:true}`；host 仅在持久化记录证明此前未发起任何导入时允许。缺少 journal 不证明未导入，不能自动降级为 newSource。已有 journal 的重试无需该标记。

## 批次间空行

新导入的每个 source 在其叶目录下，全部正文节点之后追加一个空的普通 text Rem；答案子节点内不插空行。空节点关闭练习，不生成卡片，也不计入导入内容数量。一个多段原文仍只追加一个空节点。

空节点身份与创建阶段保存在该 source 的 journal 中。重试、重开与纯回读只核验原身份、原始空文本、节点类型及同级顺序，不追加重复空行；创建阶段不确定时继续保留待核对状态。旧 journal 没有空行记录时保持原状，不回填历史内容。

若创建中断但 journal 已持有原空节点身份，纯回读可返回 `batchSeparatorPending` 与 `spacingRecovery`。这不是导入成功。客户端先保存完整身份，再显式调用 `finalize-spacing`：只在原始正文、父级、卡片身份及全部快照一致时，对本批连续的既有节点恢复顺序并关闭原空节点的练习；不创建 Rem、不改正文。只允许已记录的空节点从无父级挂回原目录，跨目录、用户编辑、混入其他节点或已完成 journal 均不作为恢复性重排对象。恢复能力版本为 `knowledge-clipper.batch-spacing-finalize.v2`。

新内容与答案始终显式指定 append 位置，不依赖 SDK 的默认插入位置。普通节点使用 createRem 的默认类型，不将读取时归一化的数值 `0` 传入 `setType`；实际 SDK setter 的默认类型参数与读回表示不同。

新回读在卡片证据之外提供 `batchSeparator: {remId, previousRemId, node}` 和叶目录的 `documentChildren`；`node` 为原始 SDK 节点证据。客户端独立核验这些字段并将身份保存在原回执。插件能力 `knowledge-clipper.batch-spacing.v1` 可用于检查当前运行代码是否已更新。

## 普通结构节点与制卡符

本机 SDK 中 `setEnablePractice(false)` 只停止复习，`backText: []` 仍会保留卡片分隔符。新目录和新批次空行从不写入背面值，关闭练习并将方向设为 `none`；原始 `backText` 缺失与 `null` 均表示没有背面，不能与空数组混同。回读新增 `rawBackText`、`backTextState`、`isMultiLineCard`，并返回 `directoryNodes`；旧客户端仍可读取归一化的 `backText`。

能力 `knowledge-clipper.plain-structure.v3` 包含当前本机兼容修复。SDK 类型声明虽然接受 `setBackText(undefined)`，本机实现只接受数组；不得调用该写法，也不能以 `[]` 冒充清除。已完成的新版空节点若出现背面数组，普通回读仍拒绝。新多行卡只有答案子节点标记为 card item，顶层正面不能添加 `MultiLineCard`，否则会让父目录出现制卡符。旧 journal 及正面曾带该标记的身份仍可回读，不静默修复或重建。

`repair-plain-structure` 是显式的同 ID 修复：先核验原 source、正文、卡片身份、目录、同级顺序与完整原始快照；只处理已记录目录和原空节点，空节点必须没有正文、子节点和复习卡。出现用户答案、真实卡片属性、挪动或编辑时拒绝。清除旧空背面数组使用官方 editor API，保存原页面、窗格、焦点和选区，写前核对精确 Rem 与编辑器正文，最后核验原状态已恢复。不能恢复的阅读器选择在导航前拒绝。该路径不创建 Rem、不改正文或父级，不由普通导入自动触发。

`test-plain-structure` 仅供 [公开固定验收驱动](../tools/KnowledgeClipper.PlainStructure.Live/README.md) 使用。它只接受独立公开 fixture 的来源与路径、原计划和已持久化空节点 ID；在该原节点上准备一次空背面并验证正式清除方法，不修改正常导入逻辑或用户来源。测试 journal 与原身份保留以恢复未完成的验证。

## 来源

SDK 适配器与 24 个已验证回归用例移自本项目此前部署的 RemNoteMCP 本地扩展，并在本目录独立维护；新增 5 个迁移测试与 3 个独立传输测试，共 32 项通过。不会从原仓库加载源码或依赖。官方 SDK 固定 `@remnote/plugin-sdk@0.0.46`。原 Bridge 工程为 Robert Spiegel 的 MIT 项目；本目录不包含其 MCP 运行时。

本轮增加批次空行、旧 journal 兼容、异常中断、节点改动与顺序核验测试；本地完整验收回执保存在 `D:\Artifacts\KnowledgeClipper\floating-quick\validation`，真实 SDK 验收由桌面端整合后统一执行。
