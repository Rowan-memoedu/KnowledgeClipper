# 四个语言模式：输出字段与卡面语义

正文更新日期：2026-09-13。本轮放宽内容审核，增加独立的生成例句入口，并稳定问答 MathJax 格式。当前源码与待切换构建的验证见 D:\Artifacts\KnowledgeClipper\content-tolerance\checks；第 9 节起的旧验证记录是历史证据，不代表本轮运行版已切换。

本说明与同目录四份完整提示词共同定义内容与展示。按用户最新确认，本任务统一负责提示词、输出字段与 Anki 模板，连同必要的解析、映射和验收一起维护。模式各自独立调用、独立保存。英语阅读答案区为音标、所选内容的英文解释、整个例句的中文翻译三行；英语书写正面为音标替换选区后的完整句子、英文解释两行，答案区为原选词块、整句中文翻译两行。显示答案时正面保留在上方。

2026-09-23 增量：以上行数仅约束模型生成的语言答案区。程序另用免费接口直接查询选中词块，把翻译、可取得的词性、释义和例句存入独立的 `DictionaryDetails` 卡片字段，放在原答案区最下方。这个字段不进入任何语言提示词、模型输出 Schema 或内容重试；制卡页是否显示它由“划词词典”设置控制，默认隐藏，但新卡片始终保存。词典缺少的项目留空，查询失败保留明确状态；接口实现可替换。

## 1. 发布状态与模式字段

| 模式 | 正文 | 状态 | 模型输出字段集合 |
|---|---|---|---|
| `language-zh` | [language-zh.md](language-zh.md) | 正文已更新；本轮验证见第 10 节 | 共同五字段 + `meaning` |
| `language-en` | [language-en.md](language-en.md) | 正文已更新；本轮验证见第 10 节 | 共同五字段 + `normalizedSentence`、`explanationEn`、`sentenceTranslationZh`、`lemma` |
| `language-writing-zh` | [language-writing-zh.md](language-writing-zh.md) | 正文已更新；本轮验证见第 10 节 | 共同五字段 + `meaning` |
| `language-writing-en` | [language-writing-en.md](language-writing-en.md) | 正文已更新；历史读音未决保留 | 共同五字段 + `normalizedSentence`、`explanationEn`、`sentenceTranslationZh` |

四个模式具有三种输出结构：两个六字段结构、一个八字段结构、一个九字段结构。字段全部必须出现；“允许 null”不等于“允许省略”。禁止额外字段。正文为完整提示词，无需拼接旧 worker 的 system、blocks 或其他协议。

## 2. 共同字段定义

| 字段 | 模型返回类型 | 必填 | 语义及为空条件 |
|---|---|---|---|
| `status` | `"ready" \| "unresolved"` | 是 | 整个模式是否已有可用、结构完整且符合本次输入的学习内容；不是应用导入状态。不得为 null。 |
| `pronunciation` | `string \| null` | 是 | 完整选区在当前语境中的读音。英文片段统一为标准美式 `/IPA/`，保留美式卷舌、重音及当前词形词尾；来源口音或英式拼写不改变此要求，不确定时给出最合理的美式读法，必要时在解释中说明推测；中文片段为带声调拼音，词内连写、词间空格，默认词典本调、轻声不标调；混合目标按原顺序组合。`ready` 必须非空；`unresolved` 必须 null。 |
| `generatedSentence` | `string \| null` | 是 | 仅在 `generated_allowed` 且原上下文为 null，且整体 `ready` 时返回含一次原样目标的自然例句。来源/用户上下文存在时，以及任何 `unresolved` 时，必须 null。不是来源原文副本。 |
| `generatedTargetStart` | `null` | 是 | 模型始终返回 null。应用在接受生成例句后以精确匹配计算 UTF-16 起点，规范化后的应用结果才可为非负整数。不得把模型生成整数当作权威位置。 |
| `unresolvedReasons` | `string[]` | 是 | `ready` 时严格为 `[]`；`unresolved` 时至少一条非空中文原因，具体说明信息缺口或不可靠之处。用于待处理说明，不上正常卡面。 |

所有字符串均为纯文本；非空指至少含一个非空白字符。学习字符串采用单段文本，不嵌入换行、编号或字段标题；展示换行由程序负责。两个英语提示词要求先输出解释、再输出读音，帮助按已确定的义项选择发音；JSON 属性顺序不是卡面顺序，程序必须按字段名组装指定行。无需模型重新返回目标原文、上下文、来源信息、模式 ID、卡片正反面或 HTML。

## 3. 各模式学习字段

| 字段 | 适用模式 | 类型 | 必填 | 语义及为空条件 |
|---|---|---|---|---|
| `meaning` | `language-zh` | `string \| null` | 是 | 完整目标的简洁中文语境义或表达作用。阅读解释允许必要的目标引用，不应用书写禁字规则；不得循环释义。`ready` 非空，`unresolved` 为 null。 |
| `meaning` | `language-writing-zh` | `string \| null` | 是 | 完整目标的中文意义线索。不得直接给出英文答案/lemma/英文同义词或中文目标的独立实义词，不靠拆字、拼字等公布答案；普通虚词与必要共同汉字不机械排除。保持准确语境义。`ready` 非空，`unresolved` 为 null。 |
| `normalizedSentence` | 两个英语模式 | `string \| null` | 是 | source/user 且 ready 时可返回转换后的例句，优先将混合上下文译成英文；原选词块逐字保留且唯一出现。全英文来源通常为 null；不能可靠转换或译后目标失配时为 null，回用已验证选区的来源句。生成分支或 unresolved 时必须 null。与 generatedSentence 不得同时非空。原始来源另存，不覆盖。 |
| `explanationEn` | 两个英语模式 | `string \| null` | 是 | 用英文解释所选内容在当前语境中的完整意思或作用，优先使用英文；词组、整句不缩成一个词。阅读允许目标的英文拼写及有用的词形说明；书写作为正面线索，允许近义改述，但不复述答案、不用 lemma/拼字等提示直接公布写法，普通功能词的必要重合不机械禁用。混合目标同样解释完整选区，不缩小选区或擅改答案。`ready` 非空，`unresolved` 为 null。 |
| `sentenceTranslationZh` | 两个英语模式 | `string \| null` | 是 | 完整例句的中文翻译：使用完整有效英文例句：`normalizedSentence ?? generatedSentence ?? contextText`。翻译包含原样目标的句子，不翻译音标替换后的卡面；不能只译选区，也不能译英文解释来替代整句译文。普通英文目标及嵌在中文里的英文也须译成中文，原样保留规则不适用于译文；必要专名和明确的字面字符串除外。仅在背面展示。`ready` 非空，`unresolved` 为 null。 |
| `lemma` | `language-en` | `string \| null` | 是 | 保留既有英语阅读词形信息。目标整体为一个英语单词或固定词条表达时，给出符合当前词性和义项的词典形式；已词汇化形容词不机械退回动词。整句、普通组合短语、中英混合目标无法作为单一完整英语词条时为 null。不确定时允许为 null，不因此阻止其他可用内容制卡。所有 `unresolved` 时为 null。 |

两个英语模式不再返回旧的中文 `meaning`。英语阅读的 `lemma` 不单独增加第四行；所需词形说明可以自然写入第二行英文解释。英语书写不返回 `lemma`，其英文解释用于正面第二行，整句中文译文只用于背面第二行。汉语书写仍使用中文 `meaning` 线索，不受本轮英语卡面修改影响。

## 4. 来源内容与程序组装

程序保留的共同事实：`selectedText` 原样目标、来源/用户 `contextText`、`contextOrigin`、选区位置及已有来源元数据。模型生成读音、解释/线索、英语模式的整句译文及适用 lemma；中英混合来源另返回 normalizedSentence，只有 generated_allowed 才自由生成例句。

| 模式 | 正面由程序组装 | 背面由程序组装 |
|---|---|---|
| 汉语理解与朗读 | 保留完整例句并突出指定选区 | 原样 `selectedText`、`pronunciation`、`meaning` |
| 英语理解与朗读 | 使用完整有效英文例句并突出指定选区 | 严格三个展示行：① `pronunciation`；② `explanationEn`；③ `sentenceTranslationZh`。不附加独立的 lemma、目标原文或中文词义行。 |
| 汉语书写 | 保留完整例句，只把指定选区替换为 `（pronunciation；meaning）` | 原样 `selectedText` |
| 英语书写 | 严格两行：① 有效英文例句中仅将指定选区替换为 `pronunciation`，前后原文逐字不变，不另加括号、标题或解释；② `explanationEn`。 | 严格两行：① 原样 `selectedText`；② `sentenceTranslationZh`。 |

英语“有效例句”为 `normalizedSentence ?? generatedSentence ?? contextText`；混合来源优先译成英文后再高亮或替换选区；若转换不可靠、改变目标或造成目标定位不唯一，允许回用原来源句，不因此阻断制卡。纯英文原句优先保持不变。汉语例句规则不变。来源类型由程序保留。“三行”“两行”指内容按顺序分行，屏幕自然折行不改变字段语义。显示答案时必须保留原正面，在下方追加答案区；背面“三行／两行”仅指新增答案区，不包括上方复现的正面。Anki 背面模板应以 `{{FrontSide}}<hr id="answer">` 开始，再追加规定字段；无需模型重复输出正面。工程模板一致性校验也须与此同步，不能只手改 Anki 模板而留下旧的严格校验。原新语言模板缺少 FrontSide 的问题已在源码和实际 Anki 笔记类型中修复；实际朗读扩展原样保留，详见第 11 节。

这些英语格式适用于单词、词组和整句选区：英文解释对应完整选区，中文译文对应完整例句。英语书写选中整句时，正面第一行就是该整句的完整音标；背面仍保留原选整句。

英语书写的第一行严格为 `有效英文例句选区前文本 + pronunciation + 有效英文例句选区后文本`，不把整个句子转成音标，也不把英文解释塞回原句；若英语目标自身含中文，保留完整目标并接受混合卡面，不因此拒绝；不得静默删中文或缩小目标。来源中的换行本来就是原文的一部分，程序必须保留；“其他不变”优先于人为压成单个物理文本行。

程序对纯文本做必要转义并负责高亮、选区替换和来源显示。提示词不决定音频、牌组、模板名称、分类路由或两个模式的执行顺序。书写防泄题规则约束模型线索；原文其他位置本来已出现相同答案时，程序仍需在预览中识别这一情况，不能声称仅靠线索检查就保证整张卡不泄题，也不能让模型擅自删除来源文字或多处挖空。

## 5. 审核：可用即通过，保留必要结构检查

- `ready`：该模式已有最合理的可用解释，全部必需学习字段非空；`unresolvedReasons=[]`；来源与生成规则一致。上下文片段、中英混杂、拼写疑误、近似翻译或读音推测均可通过；解释中用“可能／大意”或 likely 标明实质不确定性，lemma 不确定可为 null。
- `unresolved`：除 `status`、`unresolvedReasons` 外，该模式全部字段为 null，包括已有部分把握的内容和已拟出的例句。仅在完全无法形成可用解释，或必需结构输入缺失时使用；普通歧义、上下文不完整和无法绝对准确不作为单独拒绝理由。
- 输入缺口不可通过偷偷纠错、改词形、改选区或切换生成模式解决。`source/user` 缺失上下文不得自动退到生成；`unavailable` 不进入正常生产，若误传给模型应返回 `unresolved`。
- 工程必须区分内容待处理与正常卡片，保留原因并阻止未解决结果进入常规卡面/导入。不得把旧的“读音待确认”“含义待确认”字符串当作已验证内容。解析失败、字段组合矛盾、来源目标匹配失败或非完整响应也不能降级为 `ready`。
- 模型的 `ready` 是候选内容声明，不替代程序的输入、结构、精确定位与字段组合校验，也不构成语言正确性的永久保证。代表性内容验证的结论只覆盖实际样本。

## 6. 新增输入与位置边界

现有四项输入仍保留。为解决重复目标仅给 UTF-16 数字时容易误读选区的问题，新增一个程序派生字段。验证中分别提供前后文切片仍出现过误选，因此正式接口采用原文中显式标记选区的表示，不要求工程再增加前后文两个字段。

| 输入字段 | 类型 | 提供规则 | 原因 |
|---|---|---|---|
| `markedContext` | `string \| null` | 来源/用户上下文中，用唯一一对 `<selected>...</selected>` 包住程序已验证的选区；原文文本先进行 XML 文本转义。无上下文时省略或为 null。 | 将同形文字中的本次选区直接显示给模型，避免计算下标或在分离切片中误读第一次出现的位置。 |

推荐工程为全部来源/用户上下文提供 `markedContext`；重复目标时是可靠处理的必要输入。过渡期间可以省略或为 null：只有目标在上下文中唯一时才继续；出现多次则待处理。提供了不一致或不唯一的标记时同样不能继续。这些输入检查由工程在请求前执行，不依赖模型保证；如果仍误传重复且未标记的材料，提示词要求返回 `unresolved`。

发送前由程序验证选区在范围内、选区内容与 `selectedText` 原样相等，再按已验证的 `targetStart` 和目标 UTF-16 长度切出 before/target/after。设 `escapeText` 依次转义 `&`、`<`、`>` 为 `&amp;`、`&lt;`、`&gt;`，则派生字符串为 `escapeText(before) + "<selected>" + escapeText(target) + "</selected>" + escapeText(after)`。原文自带的标签也被转义，不能冒充选区标记。去除唯一的程序标记并解码后必须还原完整 `contextText`。

该字段仅作模型定位视图，不替换或修改保存的来源，不进入卡面或整句翻译；原始 `contextText` 仍是来源依据；本轮英语卡面与中文译文使用有效例句（优先英文，允许混合来源回退）。模型不计算下标，也不返回新的来源位置。

生成例句接受前由程序检查目标原样连续出现，允许重复或重叠，选择首次原样匹配并计算起点；缺失原样目标仍拒绝。normalizedSentence 保留唯一匹配检查，来源转换失配或重复则清空 normalizedSentence，回用原来源句及其已验证位置。来源原句始终使用已验证的 targetStart，不因为重复改选第一次。转换后的例句另行计算应用内部位置（可称 effectiveTargetStart），不能沿用原始 targetStart；模型仍不返回新位置。任何 Unicode 规范化、大小写修正、替词或猜测位置都不能替代精确匹配。

## 7. 最小 JSON 示例

以下均为模型原始输出，`generatedTargetStart` 始终为 null。

六字段结构，汉语阅读，目标“清澈”，已有上下文“溪水清澈见底。”：

```json
{"status":"ready","pronunciation":"qīngchè","meaning":"水清而透明。","generatedSentence":null,"generatedTargetStart":null,"unresolvedReasons":[]}
```

九字段结构，英语阅读，目标“books”，已有上下文“She borrowed two books from the library.”：

```json
{"status":"ready","generatedSentence":null,"normalizedSentence":null,"explanationEn":"Two written works with pages bound together, which she took out on loan.","sentenceTranslationZh":"她从图书馆借了两本书。","lemma":"book","pronunciation":"/bʊks/","generatedTargetStart":null,"unresolvedReasons":[]}
```

八字段结构，英语书写，目标“stayed”，获准生成上下文：

```json
{"status":"ready","generatedSentence":"We stayed at a small hotel near the station.","normalizedSentence":null,"explanationEn":"Spent some time in a place without leaving it.","sentenceTranslationZh":"我们住在车站附近的一家小旅馆里。","pronunciation":"/steɪd/","generatedTargetStart":null,"unresolvedReasons":[]}
```

这一例子的正面为 `We /steɪd/ at a small hotel near the station.`，下一行是 `explanationEn`；背面第一行为 `stayed`，第二行为整句中文译文。

六字段结构，缺少来源上下文：

```json
{"status":"unresolved","pronunciation":null,"meaning":null,"generatedSentence":null,"generatedTargetStart":null,"unresolvedReasons":["来源上下文缺失，需要补充原句，或由用户选择允许生成例句。"]}
```

九字段结构，重复目标未提供标记上下文：

```json
{"status":"unresolved","generatedSentence":null,"normalizedSentence":null,"explanationEn":null,"sentenceTranslationZh":null,"lemma":null,"pronunciation":null,"generatedTargetStart":null,"unresolvedReasons":["上下文含多个相同目标，需要程序提供 markedContext 明确本次选区。"]}
```

八字段结构的未解决结果同样清空全部学习字段：

```json
{"status":"unresolved","generatedSentence":null,"normalizedSentence":null,"explanationEn":null,"sentenceTranslationZh":null,"pronunciation":null,"generatedTargetStart":null,"unresolvedReasons":["所选缩写的读法和含义不明确，需要补充其展开形式或来源说明。"]}
```

## 8. 工程适配要求

本轮字段集合不变。语义审核放宽，仍校验 JSON、必填字段、状态组合及精确目标。来源转换失败回用原句；未解决且未确认的旧结果重试时加载当前提示词和 Schema，同时保留捕获时的模式、牌组与成功兄弟结果。旧版缺字段结果仍需重跑，不能伪装为新版。

此前四字段接口的适配记录：

1. 四模式均增加 `status`、`unresolvedReasons`，学习字段允许在失败分支为 null；增加状态与字段组合校验及明确的待处理流程。不能仅把旧四字段结构换个名称。
2. 两个汉语模式保留 `meaning` 字段名，但区分阅读释义与防泄题线索。两个英语模式改为 `explanationEn`、`sentenceTranslationZh`，英语阅读另保留 `lemma` 词形数据；旧合并模式的中文 `meaning` 不可冒充英文解释或整句翻译。
3. `generatedSentence` 保留；`generatedTargetStart` 仍由模型填 null、程序唯一匹配后计算。分清原始模型返回类型与规范化应用结果类型。
4. 为来源选区增加程序生成的 `markedContext`；未接入时重复目标明确待处理，不用提示词声称已经解决数值下标误判。
5. 工程分别维护四份 Schema、结果解析/保存、预览及 Anki 字段映射。英语阅读背面按三个字段分行；英语书写正面按“音标替换选区后的原句 / 英文解释”、背面按“原选词块 / 整句中文译文”分别组装两行。新增 Schema 或新字段不会自动改变卡片。旧缓存与个人覆盖若仍是旧语义，不能被误认成本次发布格式。

原任务的“只写正文、不维护模板”边界已由用户最新要求替代：本任务统一维护提示词、字段、模板及必要接入验证。音频生成、分类路由与牌组策略不在本次修改范围；现有朗读控件与配置完整保留。文档、构建产物和运行切换仍分别报告状态。

## 8.1 生成例句并行选项与问答公式

正常语言卡片预览的主操作下方提供“用生成例句制卡”。有来源上下文时可用，原内容生成期间也可点击。新草稿复用原目标、模式和牌组，Material 的 contextText/targetStart 置空且 contextOrigin=generated_allowed，因此完整复用无上下文生成流程；原始来源保留在 Original。原卡与例句卡独立预览、确认和导入。重复点击复用同一例句草稿，避免重复卡片。

应用新增 GeneratedExampleDraftId 保存关联，子草稿 ParentDraftId 指向原草稿；这些不是模型输出字段。问答卡和已经无上下文的例句卡不显示该选项。

问答公式采用 Anki MathJax：行内 \\(…\\)，独立公式 \\[…\\]。提示词要求公式内部单行；应用只在已配对的公式中清理 TeX 行注释和物理换行，保留矩阵等 TeX \\\\ 行分隔符及公式外段落。模型原始响应保留用于验证，导出仍通过现有 HTML 转义。字段名称、答案模板和已有正确公式不改变。

## 9. 上一版验证与历史剩余项

### 验证方式与证据

使用现有 ContentCli 构建中的 `ContentModule.RequestAsync` 请求方法及其默认 `deepseek-chat` 配置，对合成学习材料分别调用四份完整提示词，保存未修改的模型原始 JSON。没有使用私人剪藏、桌面模拟、TTS 或 Anki。新结构尚未接入工程，所以本次直接审阅原始回包并按本文核对字段，不经过旧四字段的 Schema/结果解析；不能据此宣称工程适配已通过。

原始证据仅追加在一个文件中：[language-prompts-20260911.ndjson](D:/Artifacts/KnowledgeClipper/validation/language-prompts-20260911.ndjson)。每条含 `run`、`case`、完整合成输入、模式、模型、提示词 SHA-256 与 `rawResponse`。历史失败保留原样；修订的是通用提示词与输入表示，再从原始目标/上下文重跑，未手改模型产物。前后文切片改为 `markedContext` 时，标记由原始选区位置程序化生成，不含预期读音或答案。

上一版正文对应的验证批次如下；只采用与当时正文 SHA-256 相同的回包，不从不同版本拼出通过结论：

| 模式 | 最终批次 | 材料数 | 字段与定位检查 | 内容审阅 |
|---|---|---:|---|---|
| 汉语理解与朗读 | `publication-r4` | 4 | 4/4 | 拼音连写、生成目标保真、多音字选区通过 |
| 汉语书写 | `publication-r5` | 5 | 5/5 | 4 条正常线索通过；1 条缺少来源上下文正确返回 unresolved |
| 英语理解与朗读 | `publication-r8` | 12 | 12/12 | 11 条正常结果通过；1 条未标记重复目标正确返回 unresolved |
| 英语书写 | `publication-r11` | 12 | 12/12 | 10 条正常结果通过；1 条未知缩写正确返回 unresolved；1 条读音变体未决 |

合计 33/33 通过字段集合、类型/空值、状态分支、原样目标唯一匹配、模型不计下标等检查。30 条 `ready` 回包中，29 条通过本次语言内容审阅，1 条不计通过；另有 3 条符合预期的 `unresolved`。覆盖词、词组、完整句、中英混合、同形异音、屈折形式、陌生缩写、缺失上下文及输入内命令文字。最小 JSON 示例另作可解析性及字段结构核对。

已修订并回归的历史问题包括：拼音逐字分隔、误选重复词、把整句读音当作选区读音、混合句保留未译英文、生成例句替字、写作线索泄露 lemma、无依据推断被动句施事及混淆生成权限。出现过的失败不因后续通过而被删除，也不作为最终版本的通过证据。

拼音按词拼写的依据是[《汉语拼音正词法基本规则》](https://www.moe.gov.cn/ewebeditor/uploadfile/2015/01/13/20150113091717604.pdf)。同形异音及语法边界核验参考了 [Cambridge 的 lead 发音](https://dictionary.cambridge.org/pronunciation/english/lead)、[discussion 发音](https://dictionary.cambridge.org/us/pronunciation/english/discussion)及[被动句施事说明](https://dictionary.cambridge.org/grammar/british-grammar/passives-with-and-)；这些资料用于独立审阅，没有把当前样本答案写进提示词。

### 真实剩余项

1. **读音变体未决**：`publication-r11 / en-writing-definition-canary`，目标 `were postponed`，回包为 `/wɜːr pəˈspoʊnd/`。本次查得 [Cambridge](https://dictionary.cambridge.org/pronunciation/english/postpone)列美式 `/poʊstˈpoʊn/`，[Oxford Advanced American Dictionary](https://www.oxfordlearnersdictionaries.com/definition/american_english/postpone)列 `/poʊˈspoʊn/`；有无 `/t/` 本身不能简单判错，但模型首音节弱化为 `/ə/` 尚缺本次直接美式词典证据。该条保留未决，不计入内容通过，也不把模型的 `ready` 当作已确认读音。提示词已写明优先清晰、常见美式词典读音；这一边界仍需后续验证，不能宣称所有输出已经稳定可靠。
2. **工程接入记录**：提示词核验时四个登记的独立 Schema 文件尚不存在。后续工程接入已实现字段/状态解析、输入标记、精确定位校验、保存、预览及三行/两行卡面映射，具体证据与当前切换状态见 [工程接入验收](D:/Artifacts/KnowledgeClipper/prompt-integration/checks/README.md)。该接入记录对应下列上一版正文指纹；本轮新增 normalizedSentence 及美式限定需要工程另行适配并验证，不能据此前接入记录宣称本轮已在应用生效。
3. **验证范围有限**：本次只完成上述材料的内容与字段检查，不是全词典或长期稳定性保证。任何后续错误仍需保留为待处理结果并从原始输入重跑，不能人工修回包后宣称自动流程通过。

### 上一版正文指纹（保留溯源）

| 文件 | SHA-256 |
|---|---|
| `language-zh.md` | `A850E3B08DD33906650416FFEA2359FE7A6C391A92AF260210D977916919E9F0` |
| `language-en.md` | `C9CD3E2545824026549EA3CE853C9B674D70CC5F29877AC546F15681204651B0` |
| `language-writing-zh.md` | `2FDF4C52F496FC103D8C14BD810B7F488F8EB58C75A2090F6EFC6CA1033B73C0` |
| `language-writing-en.md` | `445D0046761FEAD26BEF3A88B7196BFF797F478306DE1C2C419C2C25A25DAC3B` |

## 10. 本轮增量验证

本轮检查四份正文的美式限定、英语字段集合、转换分支与卡面位置规则，以及本文 JSON 示例。此项是提示词与字段定义发布，不代表 Schema、应用或 Anki 已接入。本轮真实 API 批次 `american-normalization-r1` 的 4 条原始回包已追加到第 9 节同一证据文件，分别覆盖英语阅读混合来源、英语书写独立混合来源、英国地点上下文下的美式读音、获准生成例句。4/4 通过字段集合、状态、有效例句无中文、原选目标唯一匹配及模型不返回位置检查；人工审阅两份来源译句保持原意。tomato 返回 /təˈmeɪtoʊ/，没有因 London 语境切换英式。汉语两模式本轮仅检查美式规则文本修改，未重跑 API；历史未决读音不因此宣告解决。

混合来源书写样本原句为“这项任务需要 teamwork，任何人都无法独自完成。”，新增字段返回 `This task requires teamwork; no one can complete it alone.`。正面第一行为 `This task requires /ˈtiːmˌwɜːrk/; no one can complete it alone.`，第二行为英文解释；显示答案时正面保留，下方再显示 teamwork 与整句中文译文。

本轮正文指纹：

| 文件 | SHA-256 |
|---|---|
| language-zh.md | AD2D949DD4FC7FC5A3A3E3F1023E454CFE85A3A400B10085D3314692C91D2364 |
| language-en.md | F652F9A7990B04D6FA012DA9A1B17D30EE696F3E30C96105638553E397C543F2 |
| language-writing-zh.md | 87D25A967CF6BE68FC7328161F9E83F310C81E47756E6A860770380698BDDA64 |
| language-writing-en.md | 3DC7845BFD074767A0EB84D7B1C07ACF273446E43A3E63BCE8A457D77E93DFFB |

## 11. 模板与字段统一接入验收

2026-09-12 显示微调：汉语、英语“理解与朗读”答案区均在音标行之后直接插入 HTML `<hr>`，将音标与后续解释/译文分开。共享模板同时适用于已有及后续卡片；不新增内容行或输出字段，书写模式不变。实际 Anki 已更新，9 张阅读卡回读通过；用户允许后剪藏程序已重启切换至修正版，进程 PID 27388，快捷方式已更新，个人设置未变，见[分割线验收](D:/Artifacts/KnowledgeClipper/reading-divider/checks/README.md)。

按用户最新授权，本任务已完成 Anki 模板修复及 normalizedSentence 必要工程接入。答案界面保留正面，下方依次显示答案区；实际朗读控件保持原样。迁移仅识别已知旧模板，其他自定义内容不会覆盖。英语转换例句经过 Schema、解析、保存、预览与制卡共同使用，并重新精确定位目标，来源原文不覆盖。

95 项字段/模板检查、19 项预览检查通过；实际两个 Anki 模板已更新，5 张现有卡片回读确认正面与答案区顺序正确，字段和复习进度不变。桌面修正版构建成功、包内提示词与 Schema 已核对；用户确认后已完成重启切换，新进程 PID 137292，快捷方式已更新，个人设置指纹未变。完整证据、恢复材料和路径见[统一修复验收](D:/Artifacts/KnowledgeClipper/template-fix/checks/README.md)。历史模型内容未决仍保留，不因模板修复自动转为通过。
