# 内容理解与制卡：KnowledgeClipper 目标中立迁移验收

时间：2026-08-26

## 迁移结果

- 中文展示名固定为“零碎知识剪藏”。
- npm 权威包名从旧产品中心化名称迁移为 `knowledge-clipper-content-worker@0.3.0`。
- 新权威程序集/namespace 为 `KnowledgeClipper.Content.Worker`，可执行入口为 `KnowledgeClipper.Content.Worker.exe`。
- 稳定业务身份保持 `workerId=content.deepseek`；`describe --json` 不含下游目标名称。
- `LearningBundle` / `CardBundle` schema 仍为 `1.0.0`，公共 `contracts/v1` 未修改。
- common artifact metadata 新增 `productId=KnowledgeClipper`、`productDisplayName=零碎知识剪藏`、`moduleRole=content-understanding-and-card-generation`、`destinationNeutral=true`。
- CardBundle 门禁由下游枚举式文案迁移为 `card-bundle.destination-neutral`，只证明输出不携带目标路由字段。
- 开放 MD Prompt 与开放 category registry 保持原路径和扩充机制，不植入任何下游假设。

旧程序集项目与旧 Node 环境变量仅作为明确标注的 legacy compatibility alias 保留；旧契约、回执、release 与已回执固定的 projection v1 记录均未改写。当前 artifact 语义权威为 `docs/DESTINATION-NEUTRAL-ARTIFACT-SEMANTICS.md`。

## 前后对照

| 表面 | 迁移前 | 迁移后 |
| --- | --- | --- |
| npm package | `remnote-quick-clipper-content-worker@0.2.0` | `knowledge-clipper-content-worker@0.3.0` |
| 权威程序集 | `RemNoteQuickClipper.Content.Worker` | `KnowledgeClipper.Content.Worker` |
| Artifact metadata | 仅 bundle schema 版本 | 产品身份、模块角色、`destinationNeutral=true`、bundle schema 版本 |
| CardBundle gate | 文案枚举具体下游 | 只声明不含任何目标路由字段 |
| workerId | `content.deepseek` | `content.deepseek`（不变） |

## 可读离线样例 A：常规学习内容

证据类型：离线 replay fixture，外部 API 调用为 0。

原始文本 1：

> 负迁移是已掌握的旧知识干扰新知识学习；当两个情境表面相似但规则不同时，先比较适用条件可以减少误用。

结果：清洗状态 `ready`，文本逐字保持；分类为 `general_learning / transfer_strategy`；生成 `concept_transfer` 卡：

- 正面：遇到表面相似但规则不同的新情境时，如何减少负迁移？
- 背面：当两个情境表面相似但规则不同时，先比较适用条件可以减少误用。
- 答案来源：`source`

原始文本 2：

> 解一元一次方程时，先去括号，再移项并合并同类项，最后把未知数的系数化为 1。每一步都要保持等式两边同作相同变换。

结果：清洗状态 `ready`，分类为 `general_learning / worked_procedure`；生成 `procedure` 卡：

- 正面：解一元一次方程的操作顺序和不变原则是什么？
- 背面：完整保留原文步骤与不变原则。
- Bundle 状态：`ready`；无 withheld unit。

## 可读离线样例 B：局部格式异常

证据类型：离线 replay fixture，外部 API 调用为 0。

原始文本：

```text
地球绕太阳公转。

公式草稿：$x + = 5$
```

结果：清洗不得擅自修复公式，`cleaned_text` 与原文一致；检测到 `broken_formula / requires_human`，理由为“公式在加号后缺少操作数”。独立干净分段仍生成 `basic_qa` 卡：

- 正面：地球绕什么天体公转？
- 背面：地球绕太阳公转。
- 异常公式单元：`unresolved`，`format_anomaly_requires_human`，进入 `withheld_units`。
- Bundle 状态：`partial`，没有把局部异常误扩散为全部拒绝。

## 验证证据

- TypeScript build + Node test：46/46 PASS。
- projection fixture validator：PASS；LearningBundle/CardBundle schema `1.0.0`；4 类 category、5 类 template、3 类 block 均覆盖。
- .NET 8.0.424：新权威与 legacy alias 两项目均 0 warning / 0 error。
- 新 EXE `describe --json`：`content.deepseek` / `0.3.0`；旧 alias 输出与其相同。
- 权威表面扫描：`src`、`dist/src`、`prompts`、`config`、`schemas`、npm package、新 csproj、MSBuild props、当前 artifact 语义文档，禁止目标名命中数 0。
- stable checkpoint 复算：21 files / 0 mismatch；权威 receipt SHA-256 保持 `1CB738FE133F6645B9757E246E2FBC210785086342F70BAD32B5015BD19E1B1E`。

## 模块外边界

registry、release assembler 与主桌面入口迁移属于总控/Core/Desktop；本模块没有修改其配置，也没有发布 release 或执行真实目标写入。它们应在新的 append-only checkpoint 中选择 `KnowledgeClipper.Content.Worker.exe`，并保留旧入口为兼容 alias，最终再做跨模块 E2E。
