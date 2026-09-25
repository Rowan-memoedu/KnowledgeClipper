# 原图路线真实验收

该控制台入口复用正式的图片材料、MinerU、内容生成、草稿冻结、Anki 和 RemNote 导入代码。它不创建窗口，也不读写系统剪贴板。

首次运行只在新的固定目录 `D:\Caches\KnowledgeClipper\image-entry-live-20260919` 创建两张公开合成图片：一张标有 9 cm × 7 cm 的长方形，以及一张只有 `lead` 的文字图片。原图、生成结果、草稿 ID 和冻结计划在后续运行中保留；缺少清单但目录非空时拒绝初始化。凭据使用正式环境变量解析，不写入清单或回执。

从项目根目录构建本项目后运行：

```text
KnowledgeClipper.ImageEntry.Live <项目绝对路径> prepare-questions
KnowledgeClipper.ImageEntry.Live <项目绝对路径> recover-questions
KnowledgeClipper.ImageEntry.Live <项目绝对路径> prepare-language
KnowledgeClipper.ImageEntry.Live <项目绝对路径> recover-language
KnowledgeClipper.ImageEntry.Live <项目绝对路径> import-questions
KnowledgeClipper.ImageEntry.Live <项目绝对路径> import-language
KnowledgeClipper.ImageEntry.Live <项目绝对路径> quarantine-language-failure
KnowledgeClipper.ImageEntry.Live <项目绝对路径> readback-language
KnowledgeClipper.ImageEntry.Live <项目绝对路径> readback
```

- `prepare-questions` 调用真实视觉模型回答首轮及一轮追问，然后冻结两张 RemNote 卡片计划；此阶段不写 RemNote。已成功的轮次不重新生成。
- 首次测试误设了自定义 RemNote 根标题，但正式 SDK 只接受固定根“零碎知识剪藏”，因此那两份计划没有发送导出。`recover-questions` 保留原计划及文件哈希，从同图片任务建立一次新父草稿，逐字段核对并复用已经验收的原始模型回答，使用正式 `QuestionCards` 重新冻结默认根下的“问答”路径；不重做模型，不修改答案，不把无效旧计划记成同 ID 导入成功。
- `prepare-language` 经真实 MinerU OCR、清洗、核对，再调用正式英语理解与朗读提示词，冻结一张原图正面的 Anki 卡片计划；此阶段不写 Anki。
- `recover-language` 专用于本次实际发现的 OCR 标题标记问题：复用同一图片任务及已完成的 OCR，用修复后的正式材料适配创建一次新父草稿，登记新旧关系并锁定失败草稿、原图和 OCR 证据的哈希。后续重试保持这一新身份，不重置旧候选，不重新 OCR。
- `import-*` 写入已经存在的有效计划，并按该计划 ID 回读实际卡片。Anki 只使用测试牌组 `KnowledgeClipper验收::原图路线20260919`；RemNote 使用正式的“零碎知识剪藏 / 问答”，两张公开测试卡的 exact Rem/Card ID 在回执中保存，便于精确回收。测试更正后不再使用最初无效的自定义根。
- `quarantine-language-failure` 只能在替代语言卡导入且回读通过后运行；按 exact note/card ID、fixture 标签、原始字段及图片哈希核验旧的失败测试样本，完整备份后将它移至测试根下“失败样本”牌组并暂停复习，不删除或修改卡面。
- `readback` 对同两张问答卡及同一张语言卡独立回读，核对正反面、原图哈希、目录及已保存的卡片身份。它不新建卡片。
- `readback-language` 可在问答导入尚未开始时单独核对正确的语言卡；同样只读。

每次尝试在 `D:\Artifacts\KnowledgeClipper\image-entry-live-20260919\attempt-NNN.json` 留下结果，失败记录不覆盖。成功 API 返回及回读正文保存在同一固定缓存根下，HTTP 请求头和请求正文不记录。`PREPARED_REAL_CONTENT_NO_EXPORT` 只表示内容与计划准备完成，只有实际导入及独立回读均通过才记为 `PASS_REAL_EXPORT_AND_READBACK`。

最终回读区分既有目录和本轮新节点：既有目录必须无背面、CardItem/Extra/MultiLine 角色及实际卡片，名称、父级和身份与首次导入回执一致；不会为了测试改写既有 `practiceEnabled` 或默认方向。无背面且无卡时，这两个元数据不等同于已制卡。新建批次空行仍严格要求方向 `none`、复习禁用。`attempt-014` 因最初将新节点的元数据要求套到既有目录而失败，原回执保留；语义修正后只对同卡纯回读，没有再次导入。

正常目标为两张问答卡和一张语言卡。本次保留了一张首次生成的失败语言样本，并允许一次可追溯替代，因此实际新增上限为四张，其中失败样本保留且暂停复习。测试范围不包含系统截图、用户剪贴板、浏览器操作、窗口交互或应用部署。
