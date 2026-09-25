# 图片流程提示词与 Anki 字段

发布日期：2026-09-15。用户已授权把独立试跑通过的提示词与字段放入软件资源目录，图片工作流由工程继续完成。

当前状态：图片入口、调度、媒体导入与结果消费已接入候选构建 `screenshot-routing`，原图链路已进入实际 Anki 验证；完整 OCR 网络验收与切换状态见 [当前验收](D:/Artifacts/KnowledgeClipper/screenshot-routing/validation/README.md)。这三项是内容阶段，不作为三种普通摘录模式登记。题目解答在 `image-modes.json` 注册；OCR 清洗与内容审核作为必要处理阶段读取本地资源。`ExcerptModeLoader` 的 `front/back/cardType` 接口保持原用途。

## 1. 文件位置

| 内容阶段 | 提示词文件 | 结果 Schema | 状态 |
|---|---|---|---|
| MinerU 后文字清洗 | [ocr-cleanup.md](ocr-cleanup.md) | [ocr-cleanup.schema.json](ocr-cleanup.schema.json) | 消费者已接入；真实 ZIP 下载待网络验收 |
| 原图题目解答 | [image-answer.md](image-answer.md) | 复用 [question.schema.json](question.schema.json) | 多图以实际图片传入，原图卡已实际导入 |
| 独立内容审核 | [content-review.md](content-review.md) | [content-review.schema.json](content-review.schema.json) | 字段校验后消费；原图自带答案时跳过 |

工程按本表读取相对文件名，复用 `modes/**` 构建复制和本地覆盖机制。截图与普通摘录共用设置中的本地提示词目录；每次采集保存所用提示词与 Schema 快照，个人覆盖优先。增加解答模式时在 `image-modes.json` 注册唯一 id、提示词和 Schema，输出类型为 `image-answer`。

`image-answer.md` 不覆盖 `question.md`：前者要求实际图片，后者仍服务已有文字问答与追问。两者只复用完全相同的回答结果字段。

## 2. 内容输入与结果

### OCR 清洗

输入 JSON：`source_id` 为本次来源标识，`ocr_text` 为完整 MinerU 原始文字。可附实际原图供核查；路径或文件名不等于图像输入。原始图和 OCR 原始输出由软件保存，清洗结果另存。

| 输出字段 | 类型 | 用途 |
|---|---|---|
| `cleaned_text` | string | 完整清洗文字；允许残句、中英混合及保留不确定字符。不是摘要或解答。输入为空时可返回空字符串，此时没有可用的摘录正面。 |
| `notes` | array | 校对或疑点记录；没有记录时为 `[]`，不为 null。 |
| `notes[].source_quote` | string | 非空、逐字且连续出现在本次 `ocr_text` 中的片段；原始换行照录。 |
| `notes[].reason` | string | 改动依据或为何保留疑点，不自动写入卡面。 |

普通 JSON Schema 不能验证跨输入的逐字匹配、保真和数学语义；这些由实际消费流程独立检查。不能仅因存在 notes 就拒绝所有清洗结果。

### 原图解答

输入是实际图片内容，可以附 `source_id` 和 `instruction`（指定题号或解释深度）。图片必须作为模型的图像内容传入；不能把本地路径放进文字请求后认为已经读图。多图角色与顺序由上游明确提供，不由文件名推断。

当前输入在每个实际图像块之前标记 `role=question`、`index=1..N` 和来源指纹。同一组题图共同构成一张卡片正面，模型只返回一个覆盖本次范围的答案。若收集了答案图，软件直接使用按序题图和答案图作为正反面，不调用解答或内容审核。

| 输出字段 | 类型 | 用途 |
|---|---|---|
| `answer_state` | `answered` / `unresolved` | 本次题目是否已有可用解答。 |
| `answer_text` | string / null | answered 时为非空解答；unresolved 时必须 null。 |
| `unresolved_reasons` | string[] | answered 时为 `[]`；unresolved 时至少一条具体原因。 |

普通裁切、中英混排或无关字迹不清不必阻断；必要尺寸或条件缺失、且无法推出所求时保留 unresolved。未决结果不是一张已有有效答案的卡，不自动把原因或占位话术填进正常背面。

### 内容审核

输入应提供 `task_kind`（`ocr` 或 `image`）、来源标识、原始文字及候选 `candidate`，并附本次实际字段约定 `field_contract`；核图任务还需传实际原图。审核针对制卡正文字段，不把 `notes.source_quote` 的原始格式误认为最终卡面格式。

输出为 `verdict`、`errors`、`warnings`；后两者是 `{kind,evidence}` 数组。pass 时 errors 为空，fail 时 errors 至少一项；warnings 可以为空。审核意见不上卡，不能替代确定性字段、公式格式和来源检查，也不将其自报 pass 当作 Anki 导入成功。

## 3. 与软件卡片字段的对应

本次交付字段语义，不新增或修改通用 Anki 模板、手动模式、挖空规则、导入身份或媒体逻辑。

现有模板使用的 `ClipId`、`Source` 等身份与来源字段继续由软件生成和保存，不由这三份提示词返回；本表只说明内容字段 `Front`、`Back` 的来源。

| 内容结果 | 软件／Anki `Front` | 软件／Anki `Back` | 后续边界 |
|---|---|---|---|
| OCR 清洗后选择“摘录” | `cleaned_text`，由软件按纯文本转义 | 空字符串 | 交给普通摘录工作流；需要生成其他卡时由用户主动选模式。 |
| OCR 清洗后选择“语言” | 由已有语言流程根据清洗文字和明确选区组装 | 由已有语言提示词组装 | 此处不改四种语言卡片字段与模板。 |
| 原图题目且 answered | 全部题图的媒体引用，按组内顺序 | `answer_text`，按纯文本转义并保留 MathJax | 媒体上传、指纹和卡面顺序回读已接入。 |
| 原图已提供答案图 | 全部题图媒体，按组内顺序 | 全部答案图媒体，按组内顺序 | 脚本直接配对；不调用模型。 |
| 原图摘录 | 全部题图媒体，按组内顺序 | 已有答案图；没有时为空 | 可直接导入，题目模式是可选后续路径。 |
| 原图题目且 unresolved | 原图保留在来源或预览中 | 不生成正常解答卡 | 保留原因供后续处理。 |
| 审核与清洗 notes | 不映射 | 不映射 | 留在审核或操作状态中。 |

当前普通摘录接口对 `front` 做纯文本 HTML 转义，因此不能把 `<img ...>` 作为模型返回的 `front` 塞进去，否则可能显示标签文字。原图引用必须经软件的媒体路径处理。模板名称、媒体字段落盘和正反面显示行为继续由软件已有基础模板负责；本次没有另造 Basic 或 Cloze 模板。

图文答案使用 Anki MathJax：行内 `\(...\)`、独立公式 `\[...\]`。每个完整公式的解码正文处于一个物理行；矩阵的 TeX `\\` 行分隔符保留。正文段落可转为 HTML 换行，但不能将公式内换行混入其中，也不能二次转义 LaTeX。

## 4. 验证与工程后续

独立内容验收见 [试跑记录](D:/Artifacts/KnowledgeClipper/prompt-lab/20260915/README.md)、[卡面预览](D:/Artifacts/KnowledgeClipper/prompt-lab/20260915/preview.html) 和 [独立审核](D:/Artifacts/KnowledgeClipper/prompt-lab/20260915/independent-audit.md)：6 份正例可制卡、1 份缺条件输入合理未决，12 处公式渲染通过，错误算式和旧公式换行反例被拦住。样本包含历史测试原始数据和独立 fixture，不是全部真实课程截图的广泛验收。

本次接口资源回读见 [资源核验](D:/Artifacts/KnowledgeClipper/prompt-lab/20260915/interface-checks.json)：检查已发布提示词与已验收版本的内容规则一致、结果符合对应 Schema、相对路径存在，以及 Anki 字段来源。

工程接入与新的多图验收见 [当前验收](D:/Artifacts/KnowledgeClipper/screenshot-routing/validation/README.md)。原图多图解答、答案图直通、OCR 原文卡三种路线已真实导入并回读 Anki；此前失败的原始 OCR 图片经同一批次直连恢复，ZIP、解包内容、根 Markdown、清洗和审核均通过。调用桥接已兼容 Windows 长路径，程序和桌面入口已切换。原始失败证据与各阶段通过记录分别保留。
