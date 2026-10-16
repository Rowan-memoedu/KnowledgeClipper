# 发布与验收历史

本页归档于 2026-10-16，保留此前 README 的发布叙事与本机证据链接，便于追溯。其中“本轮”“当前”“正式包”均指各条记录当时的状态，不代表现役版本、持续权限或待执行指令。唯一现役入口见 [README](../README.md)，使用与恢复约定见 [问答工作流](chat-workflow.md)。旧包、失败回执和回滚材料仍保留在 D 盘，不因归档重新执行导入、迁移或恢复。

## Tab 关闭、撤回和拖拽排序（2026-10-16）

原对话与分支均可点 × 关闭、Ctrl+Z 逐次撤回关闭、拖拽到目标前后排序；关闭历史和显示顺序随原草稿持久保存，消息、原生身份和共享卡片来源保留。关闭最后 Tab 自动补空白页，输入框及卡片区的原有撤销保留，在途回复继续归原会话。249 项 Chat、26 项专项界面、18 组业务回归与综合聊天界面通过，含原生鼠标拖拽、真实键盘及重开恢复。[验收记录](D:/Artifacts/KnowledgeClipper/tab-controls-20261016/validation.md)、[切换回执](D:/Artifacts/KnowledgeClipper/tab-controls-20261016/deployment.json)。

## 同页分叉 Tab 与共享卡片（2026-10-16）

在消息操作右侧新增分叉按钮；分支、空白新对话与原对话通过同页 Tab 切换，右侧多卡片共用。Codex 使用原生指定回合分叉及新身份回读，API 保留指定前缀；输入、模型、流式回复和错误绑定原 Tab。跨分支来源仍使用原卡片编辑、撤销及冻结导出合同。新增持久字段兼容旧稿，不批量迁移。

真实 Codex/API 上下文隔离、RemNote/Anki 四图组合卡及丢回执恢复、业务和界面回归通过。实际进程、快捷方式与开机任务切换；1,137 份既有数据与配置字节未变，RemNote 服务未改。[验收记录](D:/Artifacts/KnowledgeClipper/chat-tabs-20261016/validation.md)、[切换回执](D:/Artifacts/KnowledgeClipper/chat-tabs-20261016/deployment.json)。

## 性能优化，功能保持不变（2026-10-16）

每轮历史加载共享只读草稿快照，批量排版原有列表；重开先显示保存的投影并后台核对磁盘，打开记录仍使用当前草稿。聊天页缓存只用于导入状态显示、按文件变化失效，缩小页面消息序列化开销；同一进程保留空白 WebView2 控制器，Mermaid 按需加载离线模块。RemNote 合并同次健康检查并并行读取独立 SDK 属性；Anki 并行只读查询、分批核验媒体。写入顺序、冻结身份、Unknown 恢复和完整回读保持不变。

19 组业务回归、18 类界面检查、127 项插件与 48 项 host 测试通过，真实双端纯文字/四图卡和原生 Mermaid 显示均通过。531 条既有历史投影一致；切换前后 1,121 份既有数据与配置字节未变。桌面、快捷方式、开机任务和同 ID 的 RemNote host/plugin 已更新；[完整流程](workflow-overview.md)、[性能/验收记录](D:/Artifacts/KnowledgeClipper/performance-20261016/validation.md)、[部署回执](D:/Artifacts/KnowledgeClipper/performance-20261016/deployment.json)。

## 当时正式版：Mermaid 完整链路（2026-10-15）

聊天消息、卡片预览、RemNote 原生插件与 Anki 卡面均可显示 Mermaid 矢量图；保留可编辑源码，实时生成 SVG。问答及组合正反面卡共用转换路径，支持中文图示、公式和分层正文。组件随包离线提供，Anki 夜间卡面使用独立浅色图形画布。

17 组业务回归（含 192 项聊天检查）、127 项插件测试、48 项 host 测试、17 项 Mermaid 专项界面检查及完整界面/性能检查通过。RemNote 原生 widget 的 SVG 与源码哈希已核验；Anki 26.09.3 原生复习器完成隔离显示验收，真实生产验收卡的字段、模板和离线脚本与该实例逐项一致，全程未评分。程序、快捷方式和开机任务已切至[当前运行包](D:/Artifacts/KnowledgeClipper/mermaid-chain-20261015/candidate/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，切换前后 1,101 份既有数据与配置字节未变。见[验收记录](D:/Artifacts/KnowledgeClipper/mermaid-chain-20261015/validation.md)及[部署回执](D:/Artifacts/KnowledgeClipper/mermaid-chain-20261015/deployment.json)。

## 同版功能：问答历史按会话归组与多卡恢复（2026-10-15）

同一场对话生成的组合卡共用一条历史记录，导出子草稿继续保存各自的冻结内容、身份和状态。历史“查看 / 继续制卡”及旧单卡入口均回到原会话的多卡页面；误入旧单卡页面后新增的卡稿、批注和图片也会恢复到原会话，真实新对话分支和孤立草稿保持可访问。历史行显示卡片总数、已导入数量及待核对状态。

192 项聊天回归、20 项多卡重开检查、51 项历史分页与状态检查，以及 33 项按轮制卡、13 项语言历史检查通过。真实数据只读对照中，11 条导出子卡记录归回所属会话；一场原有 8 张卡的会话恢复为 10 张。程序、快捷方式与开机任务已切至[修复包](D:/Artifacts/KnowledgeClipper/history-session-grouping-20261015/candidate/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，切换前后 1,099 份已有数据与配置字节未变。见[验收记录](D:/Artifacts/KnowledgeClipper/history-session-grouping-20261015/validation.md)及[部署回执](D:/Artifacts/KnowledgeClipper/history-session-grouping-20261015/deployment.json)。

## 同版更新：补充要求快捷选项（2026-10-15）

“补充要求”文本框底行靠右增加“生成 Mermaid 图”和“去除无关信息”，可独立、多选、取消，选中显示蓝色。执行整合、书面化或图转文字时，与手写要求一起进入原辅助模型输入；勾选本身不调用模型。Mermaid 保留原任务的完整结果并补充图示，清洗保留有效题干、条件、公式与解答。已随当前合并运行包生效，15 项实际发布包界面检查（含旧缓存升级、窄屏、键盘和输入快照）通过，见[验收记录](D:/Artifacts/KnowledgeClipper/supplemental-options-20261015/validation.md)。

## 上一正式版：重做与按钮即时反馈（2026-10-15）

卡片编辑支持 Ctrl+Z 撤销、Ctrl+Y 重做，以及对应按钮；双面正文、批注、图片引用和整合新增卡共用持久化记录，重开后可继续重做，新编辑清除旧重做分支。有效按钮点击时在前端同步变灰，不等待桌面端、模型厂商或操作完成；对应操作结束或失败后解除等待，防止重复点击和旧回执提前解锁。

174 项聊天回归、28 项即时反馈与快捷键检查、47 项组合编辑、38 项书面化、28 项批注图片和完整隔离界面/性能检查通过。9 类按钮反馈最长约 0.7ms；实际进程、快捷方式和开机任务已切至[正式包](D:/Artifacts/KnowledgeClipper/redo-feedback-20261015/candidate/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，切换前后 1,094 份既有数据与配置文件字节未变。见[验收记录](D:/Artifacts/KnowledgeClipper/redo-feedback-20261015/validation.md)及[部署回执](D:/Artifacts/KnowledgeClipper/redo-feedback-20261015/deployment.json)。

## 本轮更新：统一问答历史与完整制卡编辑（2026-10-15）

问答历史的“查看”和“继续制卡”统一打开当前制卡页，旧追问面板及执行入口已移除。旧消息、手工答案、未发送输入、原图及冻结导出身份继续保留；打开历史不自动生成或导入，重复点击复用同一会话。

正反面均有书面化、添加批注、补充图片与图转文字。底部“全部书面化”一次处理双面正文与批注，缺少一面时补齐；各批注框可单独书面化，预览靠右。辅助模型独立，结果直接替换并支持撤销。验收与实际运行状态见[本轮验收记录](D:/Artifacts/KnowledgeClipper/all-formalize-20261015/validation.md)。下列章节保留并行更新的原始交付证据。

已直接对当前合并运行包验收：305 项界面检查（历史入口32项）、174 项问答逻辑检查、18 组业务回归与实际双端导出通过。程序、启动项及快捷方式一致，最终核对期间1,204份既有数据和配置哈希未变。中间候选未单独部署，最终采用已合入本轮代码的运行包，详见上述验收记录。

## 上一正式版：制卡窗口取消持续置顶（2026-10-15）

聊天制卡、语言预览和摘录制卡以普通独立窗口打开，可以正常被其他应用覆盖。任务栏入口、最小化、恢复、关闭重开与原稿保存沿用既有行为；采集浮条继续置顶。修复去掉制卡窗口对置顶浮条的所有者关系，避免原生窗口隐式继承置顶。

23 项真实窗口层级检查和 17 组业务回归通过，完整构建零警告、零错误。实际进程、快捷方式和开机任务已切到[正式包](D:/Artifacts/KnowledgeClipper/window-layering-20261015/candidate/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)。验收、数据保护与回滚入口见[记录](D:/Artifacts/KnowledgeClipper/window-layering-20261015/validation.md)和[部署回执](D:/Artifacts/KnowledgeClipper/window-layering-20261015/deployment.json)。

## 同版更新：问答导入日期目录与顺序（2026-10-15）

新问答与组合卡保留精确绑定的“零碎知识制卡 / 提问类型”document，在其中复用普通摘录的当日 M.d 日期 document、有序追加和批次尾空行。旧冻结路径保留。图文正反面在答案建好后启用制卡，避免中间状态生成额外 forward 卡；后台选区等待不再被首次慢 SDK 查询提前耗尽。

559 项本地回归、五组真实样本的 29 项检查、全程最小化窗口监测和独立 MCP 顺序回读通过。实际验收与正在运行的 Anki 背面更新包 DLL 完全相同，保留同版 Anki 修复；切换基线 1,091 份数据及配置哈希未变。四棵公开测试目录已可恢复移入 trash。见[验收记录](D:/Artifacts/KnowledgeClipper/question-dated-import-20261015/validation.md)及[运行核对](D:/Artifacts/KnowledgeClipper/question-dated-import-20261015/deployment-observed.json)。

## 上一正式版：Anki 背面保留正面（2026-10-15）

问答、翻译和普通摘录的 Anki 背面与朗读卡片一致：上方保留完整正面（包括图片和上下文），分割线下显示答案，原有批注分区保留。应用专用笔记类型的精确旧模板可幂等升级，保留已有朗读控制和样式；自定义模板不覆盖。已导入卡片同步生效，正文与复习进度不重建。

17 组业务回归、专用模板迁移及原朗读模板回归通过。实际 12 张既有卡片均已回读确认正面保留，正文、标签、身份与复习进度未变；新导入的图片、上下文及批注卡也通过。完整包零警告、零错误，运行程序 PID 3568、启动任务与快捷方式已切至[正式包](D:/Artifacts/KnowledgeClipper/anki-answer-front-20261015/candidate/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，1,120 份既有剪藏数据与配置文件字节未变。见[验收记录](D:/Artifacts/KnowledgeClipper/anki-answer-front-20261015/validation.md)及[部署回执](D:/Artifacts/KnowledgeClipper/anki-answer-front-20261015/deployment.json)。

## 上一正式版：卡片导入后自动定位（2026-10-15）

普通制卡导入 RemNote 后也会自动打开内部页面并选中正文，复用摘录已验证的后台定位。定位接在共同导入路由核验成功之后，已导入卡片不重复定位；定位失败独立提示，保留导入成功。摘录仍在整批完成后仅定位最后一项。

普通问答、挖空、图片与含批注组合卡的 17 项真实检查、全程最小化窗口监测及 334 项本地回归通过。运行进程、启动任务和快捷方式已切换至[正式包](D:/Artifacts/KnowledgeClipper/card-navigation-20261015/candidate/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，1,086 份既有数据及配置文件哈希不变。见[验收记录](D:/Artifacts/KnowledgeClipper/card-navigation-20261015/validation.md)和[部署回执](D:/Artifacts/KnowledgeClipper/card-navigation-20261015/deployment.json)。

## 上一正式版：辅助操作直接替换、批注图片与公式显示（2026-10-15）

整合、生成书面化问题和图转文字完成后直接替换原稿，不再显示候选或模型原回复。批注可在光标处粘贴图片，背面图转文字同时转换正文与批注图片并保留分区。卡片编辑区 Ctrl+Z 与撤销按钮共用持久化记录；整合新增多卡也可批量撤销，并保护后续改稿和已冻结导出。问答与预览统一 KaTeX HTML/MathML 和依赖版本，独立公式左对齐，长公式局部滚动。

93 项聊天、28 项批注图片、27 项图文、47 项制卡撤销、24 项辅助操作、34 项入口、26 项界面、10 项公式及 17 组业务回归通过；最终包真实 API/Codex 辅助与图文共 10 次调用通过。运行程序 PID 53516、启动任务及快捷方式已切换至[正式包](D:/Artifacts/KnowledgeClipper/annotation-media-20261015/candidate/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，1004 个既有数据与配置文件哈希不变，RemNote 后台 Worker 保持原部署。见[验收记录](D:/Artifacts/KnowledgeClipper/annotation-media-20261015/validation.md)和[部署回执](D:/Artifacts/KnowledgeClipper/annotation-media-20261015/deployment.json)。

## 上一正式版：RemNote 最小化后台摘录修复（2026-10-15）

剪藏插件的连接、心跳与重连移到专用 Worker，重连间隔最多 5 秒；定位等待由本地 Node 服务计时，不再依赖隐藏页面的定时器。摘录前的只读健康检查容许短暂重连，并区分未连接、SDK 暂未响应和未允许写入；不自动重发结果未知的导入。仍只更新 RemNote 内部页面和选区，不激活窗口。

7 类真实输入在刚最小化、最小化空闲 6 分钟、窗口被遮挡和后台断线重连后均通过导入、正文/媒体回读与实际选区核验。服务中断 35 秒后自动恢复，后续 22 项检查通过。运行程序、快捷方式和启动任务已更新，1,034 份既有配置及草稿哈希未变；本轮测试笔记已可恢复地移入 trash。见[验收记录](D:/Artifacts/KnowledgeClipper/background-sdk-20261015/validation.md)和[部署回执](D:/Artifacts/KnowledgeClipper/background-sdk-20261015/deployment.json)。

## 上一正式版：截图提题、图转文字与批注（2026-10-14）

“生成书面化问题”移到正面“补充图片”左侧，支持从背面单张或多张题目＋答案截图提取完整题干，保留条件、公式和小问，排除答案；已有正面仍整理原问题。正、背面补图左移，原右侧位置增加“图转文字”，通过独立辅助视觉模型转换对应卡面并保留前后正文、另一面及批注。结果仍先成为候选，采用才改稿，原图保留，可撤销恢复。

背面增加“添加批注”，点击在正文下方展开独立输入框。RemNote 的背面顶格“批注”节点承载批注子层级；Anki 正文后显示分割线及左侧“批注：”和内容。批注、补图、模型候选采用、展开状态均沿用撤销链，重开及编辑冻结卡为新草稿保留。

79 项聊天、27 项图文请求、34 项真实视觉流程、47 项制卡/复制/撤销、23 项辅助设置与故障、34 项入口、26 项界面与性能及 17 组业务回归通过。真实 API/Codex 读图、RemNote 节点层级回读、Anki HTML 回读均通过；构建零警告、零错误。程序 PID 154448、快捷方式及开机任务已切换至[正式包](D:/Artifacts/KnowledgeClipper/formalize-back-images-20261014/candidate/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，900 个既有数据和配置文件哈希不变。见[验收记录](D:/Artifacts/KnowledgeClipper/formalize-back-images-20261014/validation.md)及[部署回执](D:/Artifacts/KnowledgeClipper/formalize-back-images-20261014/deployment.json)。

## 上一正式版：拖入图片链接兼容修复（2026-10-14）

左侧图片拖到卡片正面或背面后，显示用的本地链接会核对已登记素材并转换为 `asset:` 图片引用，沿用直接粘贴图片的预览及 RemNote/Anki 导入路径。未冻结旧草稿中的裸链接和 Markdown 图片链接也可正常解析，无需重新贴图；代码原文、剪贴板粘贴和原始 LaTeX 复制保留。

35 项制卡/复制/图片、34 项入口、26 项聊天界面及 17 组业务回归通过；构建零警告、零错误。运行进程、桌面快捷方式和开机任务已切换至[正式包](D:/Artifacts/KnowledgeClipper/drag-media-compatibility-20261014/candidate/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，900 个既有数据和配置文件哈希不变，RemNote 原服务健康。见[验收记录](D:/Artifacts/KnowledgeClipper/drag-media-compatibility-20261014/validation.md)及[部署回执](D:/Artifacts/KnowledgeClipper/drag-media-compatibility-20261014/deployment.json)。

## 上一正式版：摘录后打开页面并选中正文（2026-10-13）

手动和快捷摘录核验导入后，RemNote 自动切到目标页面并选中本批最后一项的正文块；多段正文保留完整选区。只调整内部页面和编辑器，后台或最小化时不抢前台。重复打开已导入记录不会重新定位；定位失败单独提示，已成功导入的内容不会重发。

七类真实输入、22 项完整流程检查、最小化期间连续窗口监测和独立 MCP 回读通过；350 项本地回归通过。运行进程、快捷方式和启动任务已切换至[正式包](D:/Artifacts/KnowledgeClipper/excerpt-navigation-20261013/candidate/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，961 份既有数据和配置文件哈希未变。RemNote 已恢复正常窗口启动。见[验收记录](D:/Artifacts/KnowledgeClipper/excerpt-navigation-20261013/validation.md)和[部署回执](D:/Artifacts/KnowledgeClipper/excerpt-navigation-20261013/deployment.json)。

## 上一正式版：独立辅助模型与生成书面化问题（2026-10-13）

“整合所选对话”和“生成书面化问题”共用按钮下一行的紧凑服务/模型/推理控件，默认 API（当前 `deepseek-flash`、服务默认 `high`），可独立修改并保存，不跟随主对话切换。辅助会话随原草稿保存，API 和 Codex 各自保留身份；Codex 辅助回合不改主 Thread。

正面已有内容时整理原问题；正面为空时，根据反面和真实上下文生成问题，背面由代码逐字保留。结果仍是需明确采用的候选。42 项聊天、23 项辅助界面/故障、32 项制卡/复制/粘贴、34 项入口及 25 项聊天界面与性能回归通过；真实 API 三轮、Codex 一轮及最终构建原身份重开通过。完整包零警告、零错误，启动自检通过，原数据及配置哈希不变。正式入口见[部署回执](D:/Artifacts/KnowledgeClipper/auxiliary-models-20261013/deployment.json)，范围与证据见[验收记录](D:/Artifacts/KnowledgeClipper/auxiliary-models-20261013/validation.md)。用法见[问答工作流](chat-workflow.md)。

## 上一正式版：连续制卡、原始公式复制与图片粘贴（2026-10-12）

手工编辑后继续“加入正面/背面”会实际追加原始内容，保留已有正文；仅切换卡片类型不再误标手工编辑，编辑单面不覆盖另一面的新内容。提问和卡片正反面支持直接粘贴图片。左侧增加“复制整条”；选中文字和公式后按 Ctrl+C 会取回原始 LaTeX，保留来源定界符，选中公式局部时复制完整公式。

32 项制卡/复制/粘贴、34 项入口、26 项聊天与旧缓存升级、140 项浮点及 17 组业务回归通过，构建零警告、零错误。运行进程、桌面快捷方式和开机任务已切换至[正式包](D:/Artifacts/KnowledgeClipper/card-composition-append-fix-20261011/candidate/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，870 个既有草稿/配置文件哈希不变，RemNote 服务健康。见[验收记录](D:/Artifacts/KnowledgeClipper/card-composition-append-fix-20261011/validation.md)、[复制与粘贴设计](D:/Artifacts/KnowledgeClipper/card-composition-append-fix-20261011/clipboard-design.md)和[部署回执](D:/Artifacts/KnowledgeClipper/card-composition-append-fix-20261011/deployment.json)。用法见[问答工作流](chat-workflow.md)。

## 上一正式版：卡按问题类型自动匹配模型（2026-10-11）

进入普通文字或单图“卡”后，模型固定为 `gpt-6.1-sol`；切换问题类型时自动应用推理等级：快速答案使用 `low`，其他类型使用 `high`，无需手动选择模型或推理。当前模型不支持 `instant`，本轮按用户确认采用 `low`。重开和新建对话保留默认策略，已有回合快照不变；显式切换 API、右侧“问”的 Flash 自动回答和原快捷制卡继续保留。

全部 9 种类型的真实页面选择、同一 Codex Thread 的 low / high 两轮真实回答、34 项隔离入口、37 项真实入口、25 项聊天界面、140 项浮点行为及 17 组业务回归通过。运行进程、快捷方式及开机任务已切换至[正式包](D:/Artifacts/KnowledgeClipper/card-type-defaults-20261011/candidate/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，869 个既有草稿/配置文件哈希不变，RemNote 服务健康。详见[验收记录](D:/Artifacts/KnowledgeClipper/card-type-defaults-20261011/validation.md)及[部署回执](D:/Artifacts/KnowledgeClipper/card-type-defaults-20261011/deployment.json)。

## 上一正式版：统一图文提问与快捷问（2026-10-11）

主悬浮窗输入框支持粘贴图片、纯图片和图文混合提问，按 Enter 后统一进入现有问答页面，可以正常切换 API/Codex、模型和问题类型，不再按问号或文字默认路径分流。原浮点“问”改名为“卡”，原普通/快捷制卡功能保留；最右新增“问”，以 API、当前 Flash 模型和快速问答直接发送一次，不必再点发送，不自动导出，不覆盖手动偏好。

真实 Codex 与 Flash 回答、服务及模型切换、图片粘贴及移除、重复就绪防重发、无效模型输入保留均已验收；17 组业务回归、140 项浮点/快捷入口、104 项剪贴板图片、26 项聊天界面、host 40 项及 plugin 94 项通过。进程、桌面快捷方式与开机任务已指向[正式包](D:/Artifacts/KnowledgeClipper/question-entry-unification-20261011/candidate/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，852 个既有草稿/配置文件哈希未变，RemNote 原服务健康。用法见[问答工作流](chat-workflow.md)，证据与边界见[验收记录](D:/Artifacts/KnowledgeClipper/question-entry-unification-20261011/validation.md)和[部署回执](D:/Artifacts/KnowledgeClipper/question-entry-unification-20261011/deployment.json)。

## 上一正式版：首次发送白屏修复（2026-10-09）

已有 API 路由旁增加通过 ChatGPT 登录的官方 Codex App Server 路由。普通“问”先打开对话草稿，支持真实模型/推理选项、逐轮类型与 MD、流式文字及图片、原回合恢复，以及多条消息选入正反面、编辑、整合候选和问题书面化。新问答/组合卡按精确类型目录导入“零碎知识制卡 / 提问类型 / 当日日期”document，复用普通摘录的有序追加及批次尾空行；旧冻结导出仍保留原路径。

用户首次发送后的白屏已在旧包复现：assistant-ui 在等待原生回复时创建临时回答项，界面错误读取了不存在的持久化消息。已修复临时项显示，加入可恢复的异常提示和本机日志；模型目录未就绪时禁止发送，在途界面重载不启动竞争恢复。

已通过真实普通输入入口、文字/纯图片首次发送、多轮、模型/类型切换、MD 更新、书面化/整合、双端导出、停止/精确恢复及重开验收。报告白屏的原草稿在修复版正常显示，没有重发。进程、桌面快捷方式和开机任务已指向 [chat-first-send-fix-20261009 正式包](D:/Artifacts/KnowledgeClipper/chat-first-send-fix-20261009/candidate/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，RemNote 原服务健康。308 份问答/语言草稿、236 份摘录草稿及个人配置字节未变，既有 25 张卡完整结构未变；测试卡已清理并保留恢复凭据。用法及自动压缩边界见 [问答工作流](chat-workflow.md)，本轮范围、失败记录和回读证据见 [验收记录](D:/Artifacts/KnowledgeClipper/chat-first-send-fix-20261009/validation.md)及[切换回执](D:/Artifacts/KnowledgeClipper/chat-first-send-fix-20261009/deployment.json)。

## 上一正式版：滚轮拖选浮点修复（2026-10-07）

按住左键选取时滚动滚轮，会保留同一来源窗口内的选取手势；松开后读取实际选区并在松开点显示浮点。横向滚动及滚动后鼠标坐标不变同样支持。滚动仍使旧快照失效，跨窗口、其他按钮和超时继续取消选取。

2026-10-07 当时运行进程、桌面快捷方式与开机任务统一为 [wheel-selection-20261007/release](D:/Artifacts/KnowledgeClipper/wheel-selection-20261007/release/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，现保留作回滚入口。修复前回归复现失败；修复后 124 项选区检查（含 32 项滚轮检查）及 39 项界面检查通过，构建零警告、零错误，启动自检通过，个人设置哈希不变。证据与物理操作验收边界见[验收记录](D:/Artifacts/KnowledgeClipper/wheel-selection-20261007/validation.md)。

## 上一正式版：快捷反馈与单图摘录（2026-10-05）

快捷任务完成后使用含义弹窗的同一小消息样式显示结果；朗读成功保留含义，不追加成功消息。失败显示原因并保留原草稿供重试；Anki 未打开时显示已保存、等待导入，朗读失败仍单独报告。消息不抢焦点，沿用含义弹窗的悬停、失焦和外部点击关闭行为。

图片浮点“摘”在快捷模式下直接保留当前 PNG，不依赖原图开关，也不调用 OCR 或模型。单张原图使用摘录模式的导出设置，进入 RemNote“零碎知识剪藏 → 摘录 → 当日”普通节点；多图“图”的原图与配对流程保持独立，旧冻结导入目标不迁移。

2026-10-05 当时运行程序、桌面快捷方式和开机任务均指向 [quick-feedback-20261005/release-final](D:/Artifacts/KnowledgeClipper/quick-feedback-20261005/release-final/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)。170 项快捷反馈与入口检查、15 项等待导入检查、29 项弹窗检查通过；公开单图经真实导入及独立回读确认位于“摘录 / 10.5”，媒体字节一致、零复习卡。构建零警告、零错误，包清单及启动自检通过，个人设置哈希不变，详见[验收记录](D:/Artifacts/KnowledgeClipper/quick-feedback-20261005/validation.md)。下文为按日期保留的历史验收，当时的“当前”不代表最新运行版本。

## 上一正式版：浮点首次朗读启动提速（2026-10-02）

文字浮点出现时刷新过期的空闲语音进程，提前建立一条 TLS 连接；点击后直接复用就绪连接。预热不发送 HTTP 请求、凭据或选中文字；未就绪、过期、连接失败或需代理时沿用原请求路径，不等待预热。首遍和重读仍使用相同原始 PCM 及保存的播放参数。

两组独立公开文本、交错顺序的真实接口对照中，首段 PCM 中位等待由 2.03 秒降到 1.57 秒，约减少 23%；这是小样本传输耗时，不是音频设备的实际出声测量，服务响应仍会波动。零警告、零错误构建，启动自检与朗读回归通过，见[验收记录](D:/Artifacts/KnowledgeClipper/speech-latency-20261001/validation.md)。用户允许后已切换并重启，实际进程 PID 10392、桌面快捷方式及开机任务均指向 release-final；个人设置哈希不变，语音 helper 已启用预热，见[切换回执](D:/Artifacts/KnowledgeClipper/speech-latency-20261001/validation/deployment.json)。实际首音体验尚待用户确认。

## 上一正式版：含义弹窗重读声音一致性修复（2026-10-01）

首遍朗读直接播放 MiMo 原始 PCM，旧重读路径播放有损 MP3，可能改变声音质感。新候选保留首遍 PCM，重读使用同一批样本及原有语速、音量参数；MP3 仍保留作卡片导出。原始录音与 MP3 通过哈希绑定，损坏时报错，旧历史只有 MP3 时继续播放原文件。此能力由新版请求显式启用，已运行的旧版及其他 MiMo 接入不改变播放行为。

点击含义弹窗绑定该弹窗的选区录音；首遍未完成时等待录音保存，不中断后另行合成。连续点击合并等待，弹窗关闭或后续朗读开始后取消待重读请求。当前运行进程、桌面快捷方式和开机任务已统一切换到 [meaning-audio-20261001](D:/Artifacts/KnowledgeClipper/meaning-audio-20261001/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，零警告、零错误构建，启动自检与定向回归通过，详见[验收记录](D:/Artifacts/KnowledgeClipper/meaning-audio-20261001/validation.md)和[切换回执](D:/Artifacts/KnowledgeClipper/meaning-audio-20261001/validation/deployment.json)。新进程回读正常，个人设置哈希不变；用户已确认声音一致性问题解决。

## 上次正式版与选区源码基线（2026-09-28）

2026-09-28 当时运行进程、桌面快捷方式和开机任务统一指向 [restored-selection 正式包](D:/Artifacts/MimoTTS/production/20260928/KnowledgeClipper/restored-selection/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，现保留作恢复入口。它从本仓库当时源码整体构建，包含此前的快捷键、弹窗和 MiMo 语音更新；选区监听与取词保护恢复到此前已稳定使用的 `1724643` 源码，本次语音修复沿用此选区基线。后续开发必须从本仓库现有源码继续合并，不得拿旧产物或旧提交覆盖其他更新。

浮点锚点固定为鼠标松开位置，不再于松开后追踪指针或等待下一次停顿。此前的追踪候选虽能在短选区显示，但用户发现长段拖选会偏离鼠标，故已撤回该选区改动。92 项稳定版选区隔离测试及发布包启动自检通过；用户已在当前进程确认长段真实拖选后，浮点正常出现且位置正确。旧 [pointer-20260927 验收记录](D:/Artifacts/KnowledgeClipper/pointer-20260927/validation.md)仅供追溯。下文“当前”均指各条验收当时的版本。

## Anki 折叠词典卡面核验修复（2026-09-27）

折叠词典的固定标题曾被错误计入学习字段，导致已写入 Anki 的语言卡误报卡面不一致并停在 Unknown。核验现仅排除精确匹配的词典折叠标题，继续逐字核对词典正文、学习字段、卡片身份、牌组与可复习状态。四种语言卡面及错误内容拒绝共 31 项回归通过；独立公开卡片真实导入与重复确认通过。11 张原失败卡片经只读 Anki 请求恢复为 Verified，没有新增、编辑或移动原笔记。

当前运行程序和桌面入口已切换至 [anki-dictionary-recovery-20260927/release](D:/Artifacts/KnowledgeClipper/anki-dictionary-recovery-20260927/release/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，个人设置文件哈希未变。构建零警告、零错误；[恢复回执](D:/Artifacts/KnowledgeClipper/anki-dictionary-recovery-20260927/validation/receipt.json)及[独立导入验收](D:/Artifacts/KnowledgeClipper/anki-dictionary-recovery-20260927/validation/independent-live.json)保留在本机。下文为按日期保留的历史验收，“当前”均指相应日期当时的运行版本。

## 点击含义弹窗重读（2026-09-26）

左键点击划词“读”后的含义弹窗，调用现有“重读上一条”入口并保留弹窗；右键不触发重读。点击外部或切换焦点仍自动关闭。当前运行版本为 [meaning-replay-20260926/release](D:/Artifacts/KnowledgeClipper/meaning-replay-20260926/release/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，29 项隔离弹窗检查通过，见[验收记录](D:/Artifacts/KnowledgeClipper/meaning-replay-20260926/validation.md)。

## 全局快捷键（2026-09-26）

设置新增“快捷键”页，集中配置“RemNote 导入目标”和“重读上一条”，默认分别为 `Alt+Shift+《`、`Alt+Shift+/`；个人配置已初始化为同一组合。前者打开原文摘录目录面板，仍只作用于新纯原文摘录；后者复用已有最近一条语言音频，不重新合成。`《`、`<`、`,` 使用同一个物理逗号键；`/`、`?` 使用同一个物理斜杠键，Shift 按配置显式指定。

快捷键支持编辑、留空关闭、恢复默认、保存后立即生效、重复组合检查和系统占用提示；重复比较包含标点别名与修饰键顺序。注册使用 Windows 全局热键并禁止按住重复触发，重新配置及退出时释放两个绑定。当前程序与桌面快捷方式指向 [hotkeys-20260926/release](D:/Artifacts/KnowledgeClipper/hotkeys-20260926/release/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)。零警告构建、16 项定向检查、53 项隔离界面检查通过；实际进程与两个全局绑定已核验，其他个人设置语义哈希保持一致。见[验收记录](D:/Artifacts/KnowledgeClipper/hotkeys-20260926/validation.md)。

## 含义弹窗失焦关闭（2026-09-25）

划词浮点“读”后的含义弹窗在原窗口内切换焦点、切到其他窗口或点击弹窗外时自动关闭；点击弹窗内仍可继续阅读。弹窗保持不抢焦点，关闭后移除临时鼠标监听，迟到的释义结果不再重新显示。当前桌面程序位于 [meaning-focus-20260925/release](D:/Artifacts/KnowledgeClipper/meaning-focus-20260925/release/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，见[验收记录](D:/Artifacts/KnowledgeClipper/meaning-focus-20260925/validation.md)。

## 当时源码与验证（2026-09-25）

无上下文语言卡先从 Tatoeba 取得包含原样目标的真实整句；查不到时尝试 Wiktionary 的完整例句，仍无结果则保留草稿并要求补充上下文。模型处理已选定例句的学习字段，不再新造英文主例句。有上下文时沿用原句。词典栏保留 Wiktionary 返回的全部词性、释义、例句及可取得的词源，不再把 MyMemory 的机器互译当作词义。新增卡片的词典栏可折叠；已确认卡片保持冻结。

本次隔离构建位于 [language-quality-20260925/release](D:/Artifacts/KnowledgeClipper/language-quality-20260925/release/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)。四模式字段契约、词典夹具和真实接口、`kinetic` 真实模型译文及设置页已验收。正式提示词尚未发布；仓库 `modes` 文件是现有工程配置。下文按日期保留历史版本证据，不代表当前源码行为。

桌面快捷方式已指向本次构建，2026-09-25 运行进程回读为该可执行文件。此前已确认的错误 `kinetic` 卡片保留原身份与内容，未静默改写；后续新卡使用修正流程。

## 原文摘录目标快速切换（2026-09-24）

当时桌面程序位于 [excerpt-destination-20260924/final](D:/Artifacts/KnowledgeClipper/excerpt-destination-20260924/final/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，快捷方式曾指向此版本。设置的“导出对象”页可查看原文摘录目标和更改面板快捷键，默认 `Ctrl+Alt+Space`；托盘另有“原文摘录目标…”入口。面板从独立 RemNote 插件实时读取“零碎知识剪藏 → 摘录”下的直接子 Document，可选按日期导入或某个现有子 Document。选择保存后用于新建的纯原文摘录；摘录制卡仍按日期导入，已确认导入的目标不变。所选 Document 的 ID 与标题会冻结到新导入计划中，写入时再次核对目录位置，拒绝投向同名或已移动的目录。

源码已通过桌面零警告构建、13 项摘录目标定向检查、89 项插件检查、40 项 host 检查和隔离 Windows 桌面设置检查。个人设置已绑定包含测试目录的现用“零碎知识剪藏”根节点 ID，避免误入同名空根目录。独立 host 与插件已更新。插件刷新后，实时目录接口返回“题目”“AI”“投资”和两个日期 Document；一条公开测试原文导入“AI”后，经独立回读确认父级是所选 Document，普通笔记无制卡属性。软件仍默认按日期导入，用户可从面板按材料类型切换。

## 划词浮点位置（2026-09-24，已切换版本）

鼠标拖选期间不显示浮点；松开左键、选区稳定后再读取。浮点以鼠标松开处为锚点，向上、向下拖选均如此；键盘选区继续使用辅助功能提供的位置。当时程序位于 [selection-endpoint-20260924/release](D:/Artifacts/KnowledgeClipper/selection-endpoint-20260924/release/KnowledgeClipper.exe)，构建、隔离测试及运行进程回读见 [验收记录](D:/Artifacts/KnowledgeClipper/selection-endpoint-20260924/validation.md)。

## 划词词典字段（2026-09-23，上一版验收）

新生成的四种语言卡片另存 `DictionaryDetails`：程序直接向免费互译和词典接口查询所选词块，保存翻译及可取得的词性、释义、例句和来源。查询与语言提示词、模型输出和重试相互独立。Anki 使用新的专用笔记类型，旧冻结卡片仍按原类型核对；RemNote 导出把词典内容排在原答案之后。设置“采集与练习 → 划词词典”控制制卡页是否显示该栏，默认关闭，不影响卡片保存。当前接口是可替换的初始实现，词典资料不按来源语境消歧；免费服务缺项或失败时不编造内容。

上一版程序位于 [dictionary-20260923/release](D:/Artifacts/KnowledgeClipper/dictionary-20260923/release/KnowledgeClipper.exe)，当时实际进程、快捷方式与个人设置已回读；见[验收记录](D:/Artifacts/KnowledgeClipper/dictionary-20260923/validation.md)。

Windows 悬浮式知识采集工具。活动源码位于 `D:\Projects\RemNote Quick Clipper`，应用数据位于 `D:\AppData\KnowledgeClipper`，构建和验证产物位于 `D:\Artifacts\KnowledgeClipper`。

## 内容重试与 Anki 待导入恢复（2026-09-22，上一版验收）

上一版程序位于 [import-recovery-20260922/release](D:/Artifacts/KnowledgeClipper/import-recovery-20260922/release/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，当时实际进程、快捷方式和个人设置已回读；见[完整验收](D:/Artifacts/KnowledgeClipper/import-recovery-20260922/validation/README.md)。

- 内容校验失败时，重试带上上一候选和具体错误，修正此前反复重放相同请求的问题。原 Curriculum 失败已复现、修复并从原材料重新生成，原草稿经待导入队列实际导入 Anki，独立回读成功。
- 生成例句允许目标重复出现，程序选首次原样匹配；来源原句仍使用实际选中的位置。保留目标原样与字段校验，不手改模型候选。
- Anki 已运行且集合就绪时直接导入；不可连接时持久保存待导入内容。剪藏运行时每 5 秒检查集合，恢复后自动提交并核验；剪藏重启也恢复待导入记录。确认内容、媒体引用和身份保持不变，断连后的 Unknown 不重复添加。
- 不自动启动 Anki：当前版本的官方启动流程主动显示并激活窗口，无法保证隐式启动不打扰用户，因此采用用户授权的等待方案。无需修改 Anki 或 RemNote 插件。

最终队列 21 项、待导入界面 14 项、全部入口 139 项、快捷朗读 47 项、历史复用 72 项、历史界面 51 项通过；内容重试 11 项、正式语言 111 项、Basic Anki 15 项通过。真实恢复测试以受控连接拒绝模拟离线，不关闭用户 Anki；自动提交与随后独立回读使用真实 AnkiConnect。个人设置保持不变。

## 中文词义浮窗与历史录音复用（2026-09-22，上一版验收）

上一版程序位于 [word-history-20260922/release](D:/Artifacts/KnowledgeClipper/word-history-20260922/release/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，现由上方版本替代；见[当时验收记录](D:/Artifacts/KnowledgeClipper/word-history-20260922/validation/README.md)。

- 浮点“读”在选区旁显示简短中文词义，按本次来源句解释。辅助提示词开放在 `modes/lookup-meaning.md`，不改已有制卡提示词。首次释义需要等待 API；同词同句可复用已有释义。缺少来源句时显示未识别到当前句子。
- 关闭上下文时直接播放已有词块录音，不等待释义；开启上下文时先匹配来源句或当前读音。词义与录音来源分别保存，避免把新的词义当作旧录音的发音依据。
- 浮点快捷“读”遇到历史词块时保留一条新记录，但不自动重复制卡；可从历史进入该记录继续制卡。首次处理未结束时连续点击也只自动制卡一次。新词沿用默认制卡导入，主“语言”入口保留原面板。
- 后续朗读优先于迟到的旧查词结果；历史音频失效会报错，不悄悄重新合成。播放索引后台初始化，点击只查询对应词块。

最终包历史复用 72 项、快捷朗读 47 项、完整入口 137 项、历史 51 项通过；选区 82 项、释义结构 13 项与公开双语境真实释义通过。原录音到真实接收进程约 230 毫秒的验证不涉及音频设备，不能等同于实际听觉延迟。个人设置和 RemNote 插件未改。

## 历史性能、语言快捷与日期导入（2026-09-21，上一版验收）

上一版程序位于 [history-daily-20260921/release-final](D:/Artifacts/KnowledgeClipper/history-daily-20260921/release-final/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，现由上方版本替代；见[当时完整验收](D:/Artifacts/KnowledgeClipper/history-daily-20260921/validation/README.md)。

- 历史后台读取、每页 20 条且全部可翻页访问，重新激活不再重读及重建全库，同页刷新复用控件。公开 220 条记录的隔离实测：首页约 0.8–1.1 秒、刷新毫秒级，查看卡片后返回约 6–48 毫秒；单卡初次显示仍可能约 1 秒。
- 浮点“读”默认快捷朗读与制卡导入，成功不弹窗，异常回原草稿，设置仍可关闭。本机只开启 quickLanguage，其余个人设置语义不变。
- 新 RemNote 计划在模式最下层目录下按确认当日本地日期添加 `M.d` document，例如 `9.21`。同日共用、按批次连续追加且各带一个普通尾空行；旧确认目标保留，旧内容不整理。

最终包历史 51 项、朗读快捷 47 项、完整入口 137 项通过；日期客户端 134 项、插件 88 项和两批真实导入/独立纯读通过。无需刷新 RemNote 插件。性能旧超时及测试边界保留在验收记录，不将离线语音夹具当作真实 TTS 音质验收。

## 图片入口、并行 OCR 与普通节点修复（2026-09-20，上一版验收）

上一版程序位于 [image-routing-refinement/release-final](D:/Artifacts/KnowledgeClipper/image-routing-refinement/release-final/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，现由上方版本替代。当时实际进程和快捷方式已切换，个人设置未改；见[完整验收与边界](D:/Artifacts/KnowledgeClipper/image-routing-refinement/validation/README.md)和[切换回读](D:/Artifacts/KnowledgeClipper/image-routing-refinement/validation/switch.json)。

- 图片浮点为“图 / 读 / 问 / 摘”。“图”和悬浮窗“截图”进入多图收集，界面统一使用正面、反面；其余按钮只处理当次单图，明确动作优先于多图默认路径。
- 关闭原图：先 OCR，再摘录、语言学习或问答。开启原图：摘录直接保留图片；语言先 OCR、正面保留图片，且只用理解与朗读；普通问答使用视觉模型，快捷问答先 OCR，两者正面都保留图片。缺少上下文时沿用原有选择页面。
- OCR 最多 3 图并行，输出顺序保持不变，已完成结果和中断步骤可恢复。MinerU 保持直连。
- 原图问答的上下文和本轮问题放在背面，历史按文字问题展示；冻结卡片和图片身份持续核验，重复导入复用原 ID。
- 新 RemNote 目录和批次空行不制造空背面；多行卡仅把答案子项标为卡片项，不再让所属目录误作卡片正面。3 个旧空行受 SDK 原位编辑能力限制尚未清除，3 条内容已变更或缺失的旧卡拒绝自动修复。

最终包 137 项桌面流程、46 项路由导出检查通过。三张真实图片上传及解析达到 3 路并行；实际图片问答、原图语言和无窗口的原图快捷摘录均完成导入、身份和托管图片字节回读。OCR 标题符曾进入语言目标，已修正并从原图恢复；失败样本保留且暂停复习，未手改生成卡面。测试配置错误、失败和旧记录拒绝回执均见上方验收说明。

## 浮点悬停与选区核验（2026-09-16，上一版验收）

上一版程序位于 [selection-refinement/release-final](D:/Artifacts/KnowledgeClipper/selection-refinement/release-final/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，已由上方版本替代；当时实际进程与快捷方式切换、个人设置保全见[验收记录](D:/Artifacts/KnowledgeClipper/selection-refinement/validation/README.md)和[切换回读](D:/Artifacts/KnowledgeClipper/selection-refinement/validation/switch.json)。

- 文字、图片浮点悬停时保持显示；每次移开重新计时 2 秒，重新进入会取消隐藏倒计时。新选区、新剪贴板和来源变化仍使旧快照失效。
- 明确触发全局摘录后，用受保护复制读取当前真实选区，不直接使用浮点保存的 UIA 文字候选。复制结果必须来自当前窗口进程且剪贴板序号已更新；输入、来源、lease 和剪贴板保全检查继续生效。
- 英文边界补词保持原逻辑，只使用精确匹配的相邻上下文。复制失败时不拿旧选区生成草稿或快捷导入；导入阶段失败仍回到原草稿。
- 已完成当前 PDF 的[离线诊断](D:/Artifacts/KnowledgeClipper/selection-refinement/pdf-diagnosis/diagnosis.md)。PDF 文字层顺序正常，保存后的卡面与捕获原文一致；现有历史不能证明用户当时的选择范围。自动回归使用隔离原生文本控件，不模拟浏览器键鼠，也不将离线检查表述为当前 Chrome 选区的实测通过。
- 文字选区 80 项、图片浮点 99 项通过；最终包的原生复制 11 个场景 / 113 项通过，包含中英文范围、英文补词、空选区拒绝、PID 核验与剪贴板恢复。首次隔离前台限制、并发剪贴板与用户输入中断记录保留；最终串行运行未放宽断言。

## 浮点与快捷导入（2026-09-16，上一版验收）

上一版程序为 [floating-quick/release](D:/Artifacts/KnowledgeClipper/floating-quick/release/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，现由上方版本替代；见[当时切换回读](D:/Artifacts/KnowledgeClipper/floating-quick/validation/switch.json)和[当时验收](D:/Artifacts/KnowledgeClipper/floating-quick/validation/README.md)。独立 RemNote host 与插件继续部署在 `standalone-remnote/host`、`standalone-remnote/plugin`。

- 文字浮点顺序改为“读 / 问 / 摘”。剪贴板新增图片时，光标旁出现“图 / 摘”，在出现位置停留 2 秒；不因开启监听而重放旧剪贴板图片。
- 图片“图”以当次图片开始已有截图收集，可继续添加、撤销、分组；图片“摘”及主窗口“摘录”保留现有图片处理流程。
- 设置新增“浮点”页，集中管理两类浮点及四条快捷路径；托盘也可分别开关文字、图片浮点。设置页采用单行标签，重复打开复用原窗口；托盘更改会同步已打开的设置页，并保留其余未保存编辑。
- 快捷模式成功时后台导入，不弹预览；失败或需选择时回到原草稿。默认仅开启文字“摘”：直接保存原文。语言按默认练习格式制卡，提问仅生成第一轮，图片“图”仅保存单张原图，不生成答案；后三项默认关闭。导出对象沿用逐模式设置。
- 处理中从历史打开同一份语言或摘录记录，复用正在处理的草稿与导入锁，避免重复请求和重复提交。
- RemNote 每个新导入计划的全部正文后追加一个同级空节点，不产生复习卡。重复导入保持原节点，旧记录不追溯修改。原失败批次恢复、新批次导入和后台冷启动回读已通过。
- 四条快捷路径完成真实导入并核验内容、身份、复习卡、图片字节和历史状态，成功期间没有弹窗。最终显示修正包另通过 57 项纯回读，无新增生成或导入；主窗原图按钮显示开关状态，删除旧的固定 Anki 文案。

## 剪藏专用 RemNote 插件（2026-09-16，接入历史与当前独立服务）

剪藏 SDK 接入已拆为本仓库中的 [remnote-plugin](../remnote-plugin/README.md) 与 [remnote-host](../remnote-host/README.md)。插件 ID 为 `knowledge-clipper`，显示名“KnowledgeClipper 剪藏”；独立 HTTP 43111、WebSocket 43112、本地插件安装地址 `http://localhost:43113/`。正常运行不依赖 MCP Bridge、MCP server、其 fork 代码或启动任务，可以分别升级。插件代码固定官方 SDK 版本，并与剪藏的制卡协议一起验收。

该阶段程序曾切换到 [standalone-remnote](D:/Artifacts/KnowledgeClipper/standalone-remnote/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，现已由上方浮点版本替代；独立服务架构继续使用。11 条旧测试来源完成同 ID 迁移，另 1 条旧图片测试卡因内容与冻结记录不符被拒绝，未重建，历史失败证据保留。迁移说明见 [身份迁移](../tools/KnowledgeClipper.RemNoteMigration/README.md)。

新客户端把旧标准 SDK 地址 43101 解析到独立服务 43111，保留自定义地址与既有草稿的冻结内容／目标；Unknown 和已有身份仍先纯回读。仅安装插件并不等于端到端验收通过。独立产物位于 `D:\Artifacts\KnowledgeClipper\standalone-remnote`。

客户端 94 项、插件 32 项、独立服务 24 项本地测试通过。真实矩阵覆盖 20 个 RemNote 来源：23 条顶层内容、26 条答案子节点、17 张复习卡，另有 1 张默认导向 Anki 的语言卡。覆盖四语言模板、原文、手动、挖空、公式、OCR 文字、多图、题目、翻译、问答与追问；独立服务冷启动后回读同 ID，新增导入请求为 0，SDK 请求全部使用 43111。见[最终验收与清理](D:/Artifacts/KnowledgeClipper/standalone-remnote/validation/README.md)。本轮未调用模型、OCR 或 TTS，不代表这些上游生成链被重新验收。

正式模式目录已补齐，含根共 14 个 document。按用户要求，两轮测试的 36 条顶层内容及 40 条子节点已移至根级 trash 下的测试回收目录，正式目录保留；两条 Anki 测试笔记已备份并删除。固定测试数据集已退役，不再将清理后的状态当作可继续重跑的在线样本。个人设置保持切换前的内容。

## RemNote SDK 导出（2026-09-16，历史验收）

当时程序为 [remnote-sdk](D:/Artifacts/KnowledgeClipper/remnote-sdk/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)，现已由上方独立插件版本替代。历史结果见[当时验收](D:/Artifacts/KnowledgeClipper/remnote-sdk/validation/README.md)、[当时进程与设置回读](D:/Artifacts/KnowledgeClipper/remnote-sdk/validation/switch.json)及[当时界面检查](D:/Artifacts/KnowledgeClipper/remnote-sdk/validation/deployed-ui.json)。

- 设置中的“导出对象”可逐模式选择 Anki / RemNote。未配置时语言制卡默认 Anki，摘录、图片、翻译、问答等模式默认 RemNote；已确认草稿仍使用原内容与目标。
- RemNote 导入直接通过本机 HTTP 和官方 SDK，复用现有本机连接，不经过 MCP 制卡。SDK 插件须保持启用；本机后台通过现有 Windows 登录任务启动，不依赖本次工具会话。
- 内容进入“零碎知识剪藏”根 document 下的模式 document。普通空背面内容保存为并列笔记；正反面内容转为原生多行卡，正面换行留在同一个 Rem；挖空按 Anki 分组转为原生挖空，不使用 LLM 转换。
- 多图按原顺序上传为 RemNote 托管图片，逐图核对字节。失败重试先回读已有身份；图片恢复仅完成原节点的托管，不新建替代卡片。历史状态仍以全部有效内容核验导入为准。

真实验收包含 13 条顶层 Rem 内容、10 张 RemNote 复习卡和 1 条 Anki 笔记／卡片。原失败图片卡保留原身份完成恢复，独立新图片卡直接导入通过；重开和后台冷启动后全部回读相同身份，未新增导入或媒体恢复请求。测试使用公开手写材料；本轮不重新验证内容模型、TTS 或跨设备同步。

## 重播与统一历史记录（2026-09-16，历史验收）

当时程序为 [unified-history](D:/Artifacts/KnowledgeClipper/unified-history/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)。[历史验收](D:/Artifacts/KnowledgeClipper/unified-history/validation/README.md)包含交互、卡片状态、启动包和[当时的进程回读](D:/Artifacts/KnowledgeClipper/unified-history/validation/switch.json)。以下行为保留在当前版本中。

- 单击重播上一条语言音频；双击只打开“历史记录”。单击等待系统双击间隔，避免双击同时启动音频；右键也可打开历史。
- 历史汇总全部摘录、语言、问答与翻译记录，不再限制为最近 20 / 30 条。既有语言子模式和追问仍归入原记录，两个存储及各自的预览、导入流程保持独立。
- 只有语言历史记录可播放。其他记录可打开原有卡片页面，查看或继续制卡。
- 至少一张卡已经核验导入，且当前所有已制成的卡都已导入时显示“查看”；其余显示“继续制卡”。统计包括已生成但后来取消勾选的语言模式、后续问答及摘录的有效卡片；被合并替代的挖空草稿不重复计入。导入或新增卡片后自动更新按钮。

150 项检查通过：历史及按钮状态 48、基础工作流 37、摘录 31、独立语言模式 34。历史界面在隔离 Windows 桌面用 66 条公开测试记录检查，播放调用真实播放器读取缓存音频；导入状态使用隔离 fixture，本轮未重新调用内容 API 或写入 Anki。现有截图与 MinerU 直连实现保留。

## 截图与默认路径更新（2026-09-15，历史验收）

当时程序为 [screenshot-routing](D:/Artifacts/KnowledgeClipper/screenshot-routing/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)。实际进程、控件和个人设置回读见[历史切换核验](D:/Artifacts/KnowledgeClipper/screenshot-routing/validation/switch.json)，完整结果见[截图验收](D:/Artifacts/KnowledgeClipper/screenshot-routing/validation/README.md)。以下功能保留在当前版本中。

- 截图保留多图收集、题图／答案图分组、继续和撤销。点“完成”后把明确排序的原材料交给当前工作流。点击摘录时，剪贴板最新内容若为图片则进入相同图片路线；浮点的明确文字选区继续走文字摘录。
- 关闭原图时，逐图调用现有 MinerU 程序，保存原始结果，再调用已发布清洗和审核提示词。OCR 与 Ask 选择并行；完成后进入所选摘录或语言工作流。失败步骤可重试，已完成 OCR 的结果下载可以恢复，不重新上传。
- 原图有答案图时，题图组成正面、答案图组成反面，不调用内容模型。没有答案图时调用 `image-modes.json` 所选模式，实际图片按 role / index 传给模型；默认“题目解答”，也可选择不生成答案的原图摘录。已有摘录模板承载多张原图，媒体存储和顺序均独立回读。
- “默认路径与图片”设置页分别保存文字、OCR 图片、原图默认路径。Ask 或失效的默认模式都会显示选择窗。文字框旁的选项已移除，默认恢复“结尾问号提问，否则翻译”；提示文字随设置更新。
- 右键“最近图片”可打开结果或继续未完成的材料。原图、清洗结果、疑点、审核意见和导入状态分开保存；疑点与审核意见不上卡。

190 项本地检查通过，两种原图路线和 OCR 原文卡均已真实导入 Anki 并回读，共三张卡，重复导入不新增。此前失败的 OCR 图片复用原批次完成直连下载、ZIP／Markdown 校验、DeepSeek 清洗与审核；调用桥接支持 Windows 长路径，避免下载后解包失败。MinerU 全程直连，物理网卡恢复由已有 MinerU 程序负责，没有修改系统代理或个人设置。旧下载失败与解包失败证据保留。

## 独立摘录更新（2026-09-15，历史验收）

历史已验收版本：[KnowledgeClipper.exe](D:/Artifacts/KnowledgeClipper/independent-excerpt/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)。该次发布的实际按钮、文字输入四个选项、文件指纹与设置见 [历史发布回读](D:/Artifacts/KnowledgeClipper/independent-excerpt/validation/switch.json)。以下记录该次更新，当前文字输入行为以上方章节为准。

主窗口“摘录”和“语言”分别进入独立流程，浮点提供“读 / 摘 / 问”三个小按钮。文字输入明确选择“语言 / 摘录 / 翻译 / 问答”，不按文字、问号或 AI 分类改变功能。语言入口沿用已发布的四种语言模式与默认练习设置；“采集与练习 → 语言入口”可关闭触发后的自动朗读，手动播放仍可用。

摘录页初始只有原文卡，正面为选中原文，反面为空，可直接导入 Anki。勾选“手动”后可编辑独立的正、反面并添加新卡；开启“挖空”后，在手动正面选中文字即产生一张 `{{c1::文字}}` 预览。每次选区独立制卡，合并所勾选的挖空卡时，各空统一为 `c1`，在 Anki 中共同组成一张卡。合并替换待导入原卡并保留恢复信息；尚未确认导入时可撤销合并，已导入卡片不修改。

所有派生卡与原文卡独立，可逐张导入或导入全部未导入项。已确认卡片保持固定，超时、未知写入状态先核对，不盲目重加。摘录历史在右键“最近摘录”，与语言历史分开保存。

新提示词接口在 [摘录输出字段](../modes/excerpt-output-fields.md)，工程 Schema 为 `modes/excerpt-card.schema.json`，注册表为 `modes/excerpt-modes.json`。本轮注册表为空；原文与手动流程已经接通，新提示词正文和效果测试等用户发布后进行。可以设置本地提示词目录，并在摘录页刷新，多选模式各生成一张独立卡。

该次独立摘录发布后，图片流程提示词与结果字段交付到 `modes`；当前接入状态见 [图片流程字段](../modes/image-output-fields.md) 和上方截图更新。

验收：31 项摘录领域检查、15 项生产界面组件检查、104 项旧导入回归通过；界面在隔离的 Windows 测试桌面渲染。Anki 独立验收牌组真实生成 3 条笔记 / 3 张卡，字段、卡面、牌组和可复习状态均回读，重开与重复导入没有新增。回执见 [摘录领域](D:/Artifacts/KnowledgeClipper/independent-excerpt/validation/excerpt-domain.json)、[界面](D:/Artifacts/KnowledgeClipper/independent-excerpt/validation/excerpt-ui.json)、[实际 Anki](D:/Artifacts/KnowledgeClipper/independent-excerpt/validation/excerpt-live-anki.json)。本轮没有测试生产摘录提示词，也没有重复调整用户已经验收的旧功能。

## 已验收修复记录（2026-09-11）

当时修复版为 `selection-speech-fixes`。默认制卡格式移到“采集与练习”页首行，三种选项均已验证保存和立即生效。选词浮点当时由 90×46 调整为 45×23，优先靠近选区右上角，显示器边缘自动避让；本次增加独立“摘”按钮后为 66×23，保留各按钮的小尺寸。

输入钩子将观察事件交给独立队列处理，监视消息泵、未被钩子接收的输入和定期续接；启动失败、消息泵退出及钩子丢失可自动恢复。旧读取完成不能清除新读取，取消和读取上限避免长期占用取词槽。现有选区、前台窗口与剪贴板保护继续生效。

Flux 原记录的两个音标都是 `/flʌks/`，语音设置为 Ava 多语种音色。当前路由对 Ava、Andrew、Brian、Emma 使用同名美式英语专用音色；Flux 已从原记录重生成、播放并重播，新的已验证音频放入独立缓存键，供历史记录后续重播使用。用户已确认这一批功能测试通过、无需继续调整，该阶段关闭；本次更新保留现有语音实现。Edge TTS 不接收 IPA 的技术边界不变。

本轮 140 项工程检查与边界见 [修复验收](D:/Artifacts/KnowledgeClipper/selection-speech-fixes/checks/README.md)，实际运行进程、设置首屏控件、快捷方式与个人设置回读见 [switch.json](D:/Artifacts/KnowledgeClipper/selection-speech-fixes/checks/switch.json)。故障恢复测试不等于长时间真实操作观察，Windows 音频输出也不等于逐音素听辨验收。

## 正式提示词接入记录（2026-09-11）

提示词接入时曾按授权切换到 `prompt-integration` 构建；该次进程、六页设置、文件指纹与快捷方式回读见 [历史 switch.json](D:/Artifacts/KnowledgeClipper/prompt-integration/checks/switch.json)。现行入口以上方最新部署状态为准。

四个模式按 [language-output-fields.md](../modes/language-output-fields.md) 接入独立 Schema、`ready` / `unresolved`、输入选区标记和字段保存。原文重复词按已验证的 UTF-16 位置标记；生成例句要求唯一精确匹配，由程序计算位置。来源原文保留，原文中的 XML / HTML 字符会被转义。

英语阅读背面严格为音标、英文解释、整句中文翻译三行，lemma 独立保存。英语书写正面为原句内音标替换选区及英文解释，背面为原选区及整句中文翻译，各两行。汉语书写以“（拼音；意义线索）”替换本次选区，背面只保留原选区。阅读预览高亮本次目标；重复答案留在原文其他位置时显示书写提醒。待处理结果保存原因、禁止导入，可重试；已就绪的其他模式保持独立。

本次字段带 `language-content.2026-09-11` 标识。新版卡片使用所选语言笔记类型名称加“ · 语言20260911”，首次导入创建；旧草稿和已确认卡片保留原配置、字段和模板。现有笔记类型与用户卡片不被迁移或覆盖。

验证与边界见 [验收记录](D:/Artifacts/KnowledgeClipper/prompt-integration/checks/README.md)：四个模式的来源原文与允许生成场景均执行真实 API；实际 Anki 四条独立笔记 / 四张卡已逐张回读。原文损坏样本返回 `unresolved`。额外未知字母串 `qzxvpt` 被模型标为 `ready`，缺少读音依据，保留为内容未决项，不计入通过结果也不导入；提示词发布记录中的 `were postponed` 读音变体仍未决。工程接入不等于所有模型输出已完成语言学验证。四份提示词正文未修改。

## 已包含的设置修复（2026-09-11）

以下设置修复已包含在上方提示词接入版；原验证输出保留在 `settings-editing`。

当时“设置…”与“其他功能设置…”改为打开并复用同一个窗口，读取真正使用的 `language-settings.json`。旧入口不再打开与朗读、内容和 Anki 流程脱节的设置面板。当时先完成已接入功能，OCR 与 RemNote 随后已在上方版本接入；当前浮点版本只保留一个“设置…”入口。

36 个设置控件均可编辑，分为采集与练习、音色与播放、Anki 导入、内容处理、运行程序、模式与提示词六页。语音可选择中文、英文或其他语种音色，修改速度、音量、音调及自动播放；进入音色页会读取可用列表，成功查询的目录保存在 `D:\Caches\KnowledgeClipper\voices`，重开可直接选择，手动刷新失败仍保留已有列表和选项。试听使用本页尚未保存的参数，关闭设置会停止试听。自动播放关闭后仍保存音频，明确点击重播或句子播放时立即播放。

内容 API、模型、单次超时和失败重试次数均作用于后续请求。默认保持 90 秒、首次失败后重试一次；重试不包含首次请求，取消或认证失败不重试。Python、Node.js、ffplay 的完整路径可修改，新路径不存在时保存会被拒绝。API Key 在安全输入框修改，留空保留已有值，通过本机加密凭据存储保存。上下文、练习、牌组、模式提示词等修改继续使用当前直接流程；已保存的任务保留自己的设置快照。

验证记录：[checks](D:/Artifacts/KnowledgeClipper/settings-editing/checks)。桌面构建 0 警告、0 错误；共 212 项相关检查通过：全部设置 44、牌组设置与列表 51、选词与托盘 23、内容接口和请求策略 37、语音用途与缓存 20、既有工作流 37。全部设置检查通过生产菜单和控件修改、保存、重开、下一次操作快照验证，并使用隔离配置完成真实中英文音色切换、试听、自动播放关闭与手动重播。改过的速度、音量、音调也实际传入语音服务。测试未改动用户个人配置，Anki 列表和导入回归使用明确标注的本地 fixture。

音色目录查询和一次中文试听曾出现服务失败，相同中文音色与参数随后成功；失败日志与成功音频均保留，不能据此保证外部服务始终可用。目录缓存用于保留可选项，试听失败会显示错误并可再次点击。六页设置截图已生成，当前 DPI 下检查了控件、滚动和固定保存按钮。

## 上一批工程版记录（2026-09-11）

历史工程版保存在 `maintenance-integration`。程序使用现有单实例机制，已有进程运行时，双击另一份 EXE 会恢复该进程，切换需先退出旧进程。

上一批接入了四个具体模式的独立处理、设置和预览，当时正式提示词尚未发布，验收仅覆盖接口；本页上方为收到正式提示词后的接入结果。

当时按用户要求暂停并保留工程证据；现已按后续请求恢复接入。

本批使用方式：

1. 在设置开启全局选词。一次抓取失败只影响当次选区，监视继续；无需为了下一次选词重新开启设置。选区本身仍按变化、来源切换及超时规则失效。
2. 主悬浮窗的 `×` 隐藏到已有系统托盘；双击托盘或选择“显示悬浮窗”恢复。菜单“退出”才结束进程和监视。
3. 当时浮点左侧在“读 / 摘”之间切换；2026-09-15 起改为“读 / 摘 / 问”三个独立按钮。“问”直接解释本次选区并进入已有追问流程，不先弹输入框，不依赖末尾问号。原选区和来源上下文独立保留。
4. 只选到英文首尾部分字母时，共用捕获根据可核验的邻接原文补齐词界；多词之间原样保留。没有可靠原文时不猜测，不扩成整句；关闭制卡上下文不会关闭词界补全。
5. 默认朗读选中的内容；预览句子旁的 `▶` 才播放当前句子。翻译在译文完成后朗读完整译文，重播使用有效译文。选区、句子、译文的缓存按用途、文本及音色等配置隔离，句子播放不覆盖默认重播音频。
6. “读取可用音色”加载当前 Edge TTS 的音色，中文与英文可分别选择。自动选择时，纯中文沿用中文音色，英文及中英混合使用英文音色；混合文本在送往语音服务前跳过汉字，保留词间边界，因此用户选用多语言音色也不依赖服务自行跳过中文。原始材料和制卡上下文保持完整。关闭自动后使用指定音色朗读完整选区。新安装的英文默认值为 `en-US-AnaNeural`，既有音色选择不自动改写。
7. 根牌组和模式页共用 AnkiConnect 的真实牌组列表；选择根牌组后，各模式可选择它下面任意层级的已有子牌组，或填写相对子路径并点击“新建此子牌组”。新建后刷新列表；每个模式独立保存目标。设置改变只影响后续新操作，不迁移既有卡片，不改冻结草稿的目标。
8. “两者”对同一次捕获分别执行理解与朗读、书写两个模式，独立预览、编辑和导入，自动 TTS 一次。底部导入在点击时固定本次选择，全部核验为 `Verified` 才关闭预览；失败、缺配置、缺失绑定或未知写入结果保留窗口。“导入当前模式”成功后也关闭，其他未完成草稿可从最近草稿恢复；若后台仍有内容处理，等它保存完成后再关闭。仅追问卡片旁的“导入本张”保留窗口。
9. 缺少提示词或 Schema 时仍可打开设置、修改牌组及普通选项；保存不会创建空提示词、抹掉已有覆盖或把缺配置标为已完成。关闭预览仍保留草稿，已导入内容继续跳过，不盲目重写。

10. 右键“提问…”和 `--open-question` 是明确问答入口，无需末尾问号。旧文字框的自动分流已在 2026-09-15 更新中改为四项明确选择。

四个独立模式如下；翻译与问答继续使用各自现有模式。`modes.json` 的 `schemaFile` 是可配置引用；本次由工程按正式字段定义维护四个模式的 Schema。

| 模式 ID | 根牌组下的默认相对子牌组 | 已发布提示词 |
|---|---|---|
| `language-zh` | `语言学习::汉语::理解与朗读` | `language-zh.md` |
| `language-writing-zh` | `语言学习::汉语::书写练习` | `language-writing-zh.md` |
| `language-en` | `语言学习::英语::理解与朗读` | `language-en.md` |
| `language-writing-en` | `语言学习::英语::书写练习` | `language-writing-en.md` |

已有处理成功或确认冻结的草稿使用自己的配置快照。缺配置的独立子稿可点击“重试内容”补齐本次正式处理器，保留原目标和已成功的其他模式。旧四字段结果不会被重新解释为本次字段格式。

重新构建工程版：

~~~powershell
pwsh -NoLogo -NoProfile -File 'D:\Projects\RemNote Quick Clipper\build.ps1' -BuildOutput 'D:\Artifacts\KnowledgeClipper\maintenance-integration' -BuildCache 'D:\Caches\KnowledgeClipper\maintenance-integration'
~~~

隔离构建不会修改桌面快捷方式；明确传入 `-InstallShortcut` 才将快捷方式指向此输出，`-Start` 用于启动构建结果。默认正式输出仍为 `D:\Artifacts\KnowledgeClipper\build`。脚本会拒绝覆盖正在使用的目标。直接调用 MSBuild 时，同时指定 `KnowledgeClipperBuildOutput` 和 `KnowledgeClipperBuildCache`，用 `-getProperty:TargetPath,IntermediateOutputPath` 核对输出与缓存。界面检查的 `DesktopAssemblyPath` 必须指向同一桌面 DLL。测试先 `dotnet build`，再按隔离绝对路径运行 DLL；本机 .NET 8 的 `dotnet run` 曾在构建隔离输出后启动旧默认产物，该失败日志保留。

本批检查与真实外部验证保存在 [engineering-checks](D:/Artifacts/KnowledgeClipper/maintenance-integration/engineering-checks)。接口和界面 fixture、真实内容 API、真实语音以及 Anki 写入分别说明证据范围。独立模式 Anki Canary 使用公开英文样本和两份明确标注的接口结果，仅验证每模式一张实际卡、独立牌组、回读与重复导入；测试卡保留在 `KnowledgeClipper Canary::Engineering 20260911`，不会混入正式牌组。

验证结果：桌面构建 0 警告、0 错误；相关接口及语义界面检查共 260 项通过。真实 API 分别通过显式选区解释和中文翻译；Ava 多语言音色的混合样本完成合成和未静音进程输出，服务输入已排除汉字，听感仍未由用户核听。Anki 实际产生两条独立笔记／两张卡并逐张回读，重复导入为零外部调用。设置截图已检查，连续选词与浮点形状复用同一源码的既有证据，托盘生命周期在当前工程版语义界面检查中通过。上述结果不替代四模式正式提示词的验收。

本批证据：选词失败复现与修复、托盘在 [selection](D:/Artifacts/KnowledgeClipper/maintenance/selection)，实际 DPI 浮点在 [dot](D:/Artifacts/KnowledgeClipper/maintenance/dot/validation.json)，真实音色与语音样本在 [maintenance-speech-live](D:/Artifacts/KnowledgeClipper/validation/maintenance-speech-live)。中文先前的 `NoAudioReceived` 已保留；随后同文本、同音色的生产助手与参数对照成功，未证实稳定的代码或参数故障。设备静音时只认定合成和进程音频流，不认定可听效果。UI 的本地 HTTP 与导入状态 fixture 只验证界面和状态边界，不代替真实 Anki 写入或正式提示词验收。

## 已验收运行版记录（2026-09-10）

**M1、M2 的工程流程均已通过用户验收；现已切换到正式 `KnowledgeClipper` 牌组，并完成分层牌组自测与测试卡清理。提示词与产出效果优化后置。**

现有朗读/摘录入口已直接连接 .NET 语言流程：取得一次材料，独立启动 TTS 和内容处理，显示可编辑预览，一次确认后通过 AnkiConnect 写入并核对实际卡片。新路径不启动旧 Core / Host / Worker 调度。

用户已通过现有朗读入口完成真实取词，来源上下文、预览和声音均出现。初次手测捕获后约 5.2 秒才有声音，且首次 UI Automation 初始化导致浮窗物理尺寸减半。现已提前启动独立语音、预热一个不含文本的助手并调整播放器缓冲；启动时固定 DPI、保存逻辑尺寸并保持浮窗完整位于屏幕内。13:10 的用户复测确认两项正常：点击至声音约 3.00 秒、捕获后约 2.58 秒，实际窗口仍为 696×424。当前按工程流程可运行验收；提示词保持开放可编辑，由用户后续在独立提示词 Thread 继续处理，不作为 M2 的前置门槛。

M2 只增加选区入口：选中文字后显示浮点，点击后把当次文字和来源交给普通摘录/朗读共用的流程。内容、TTS、预览和 Anki 导入均复用既有实现；浮点显示时不创建草稿或发起内容请求。后续如接入查词等工具，也应进入共用内容流程，不能只作用于某个入口。用户已在 Chrome、记事本、资源管理器、Obsidian 通过选词与启停验收；Chrome 和 Obsidian 各有一条浮点制卡草稿写入并回读，共 4 张可复习卡片。

文字框输入、中文翻译和追问已迁入同一主流程，并通过本阶段真实集成与用户验收。入口只注入材料和 `InputMetadata`；分类、处理、追问、预览与导入共用后端，不根据来源窗口另建内容流程。

| 输入 | 入口元数据与默认行为 |
|---|---|
| 文字框末尾为 `?` 或 `？`（忽略末尾空白） | `questionMarkRouting=true`，直接问答，不自动朗读 |
| 文字框无末尾问号 | `intent=lookup`，无上下文；中文或中文为主的材料译成英文，其他自然语言词句走语言学习；自动朗读由设置控制 |
| 选中文字按钮、全局浮点 | `intent=auto`，保留本次来源上下文状态；普通中文仍默认语言学习；是否朗读保持原按钮含义 |
| 已有材料后的主动追问 | 携带原材料、已有内容和有序问答，直接回答本轮，不按问号重新分类、不重新捕获 |

模式确定后，`outputKind`、`followUpDefault` 和 `requiresContext` 随草稿保存，分别控制字段映射、追问默认展开和语言上下文处理。翻译使用简单单卡：正面原始输入，反面英文译文。问答每轮一张卡，正面为本轮问题，反面为本轮答案；前序问答默认放入正面的独立辅助字段，可在设置中关闭。语言模式仍用原有练习选择；默认收起追问，用户可主动展开。

## 旧运行版使用记录

以下记录对应先前已验收版本；本批工程版的入口、构建方式和差异以上文为准。旧运行入口仍为 [KnowledgeClipper.exe](D:/Artifacts/KnowledgeClipper/build/RemNoteQuickClipper.Desktop/Release/net8.0-windows/win-x64/KnowledgeClipper.exe)。本机 .NET SDK 为 8.0.424；`-DotnetPath` 可指定其他 .NET 8 SDK，NuGet 缓存位于 `D:\Caches\NuGet\packages`。

1. 在普通应用选中文字，点击悬浮窗“朗读”或“摘录”。
2. 上下文开启时使用可靠的来源原文；提取失败时选择允许生成、补充原文或取消。补充上下文不会自动重播。
3. 在预览核对读音、语境义、句子及练习类型。可选理解与朗读、书写或两者；关闭窗口保留草稿和未确认的编辑。
4. 点击固定在预览底部的“确认制卡并导入 Anki”，等待 Anki 实际回读。Anki 不可用时草稿保留；不确定的写入先查询，不盲目再添加。
5. 点击悬浮窗“重播”，或右键“重播上一条 / 最近朗读”，立即播放上一条并打开最近 20 条朗读记录。每条可点击文字或“播放”重听，点击“继续制卡”打开该条预览。完整音频直接复用；关闭记录面板不停止播放。
6. 右键“设置…”调整语音、内容 API、Anki 目标和已有提示词；“最近草稿 / 朗读”也包含仅摘录的草稿。问答等非语言草稿隐藏播放按钮，点击文字可查看内容；每个会话只显示一条记录，内部逐轮卡片不重复列出。旧版单卡窗口中独立继续的问答或未发送输入仍可访问。停止朗读不取消制卡。
7. 在“设置…”开启“全局语言学习”并保存，可在普通应用拖选、双击或用 Shift＋方向键、Ctrl+A 选词；点击蓝色“读”点进入同一流程。关闭开关立即停止监视，原入口继续可用。浮点不抢焦点，选区变化、取消、切换来源或约 12 秒未使用会收起。
8. 在悬浮窗文字框输入后按 Enter 提交；右键“提问…”或启动参数 `--open-question` 打开可换行的大文字框，Ctrl+Enter 提交。中文翻译不要求来源例句，不限制为单词长度。
9. 问答预览按轮次显示问题、可编辑答案和辅助上下文。底部默认一次导入全部待确认问答；也可取消勾选，或点某轮“导入本张”。已导入轮次自动跳过；确认时固定本批次全部卡面，中途失败保留已完成结果和剩余卡面，重试先核对身份。旧草稿已确认的卡片及模板保持不变，不重复添加。
10. 语言、翻译预览可点底部“追问”，并可“返回原卡片”；底部导入按钮跟随当前视图。继续输入不要求问号，发送失败保留文字，关闭后可从最近草稿恢复。未解答的轮次不创建空卡，后续追问继续使用有序历史和当前有效的编辑或冻结答案。
11. 设置中可开关“文字框输入后自动朗读”和“问答卡正面附带前序问答上下文”；设置用于后续新建会话，已确认卡面保持冻结。现有模式页可切换语言、翻译、问答，编辑各自提示词与默认展开追问。旧个人模式覆盖与新内置模式按 ID 合并，保留已有编辑。
12. 常用设置中的“Anki 根牌组”控制后续新会话的根目录；模式页中的“子牌组路径”分别控制语言学习、翻译、问答的相对路径。用 `::` 分隔任意多层，`{语种}` 和 `{格式}` 会在制卡时替换；例如 `语言学习::{语种}::{格式}`。预览会显示实际目标，切换练习后立即更新。旧草稿保留当次设置快照，已确认卡片的目标不会随新设置改变。

当前本机根牌组为 `KnowledgeClipper`，默认层级如下：

| 卡片 | 根牌组下的路径 |
|---|---|
| 汉语语言学习 | `语言学习::汉语::理解与朗读`、`语言学习::汉语::书写练习` |
| 英语语言学习 | `语言学习::英语::理解与朗读`、`语言学习::英语::书写练习` |
| 中文译英文 | `翻译::中译英` |
| 问答及任意材料后的追问 | `问答` |

语种按选中目标的文字判别，不使用外围原文；混合目标归入“中英混合”，其他文字归入“其他语言”，相应牌组在需要时创建。该本地规则不做完整语言鉴定。语言笔记仍是一份共享字段配两个原生模板，按所选练习生成一张或两张卡，再将每张卡放入相应牌组；写入中断时保留冻结的分配方案，恢复不会再次添加笔记，也不会改回用户另行移动的卡片。

翻译与旧问答使用“零碎知识剪藏 · 问答与翻译”。新问答使用“零碎知识剪藏 · 多轮问答”的一个“正反面”模板：`Front` 和 `Back` 分别保存本轮问题与答案，`Context` 独立保存前序问答，以较小字号显示在正面辅助区域；关闭上下文时该字段为空。已有用户模板不会被覆盖。

正式切换自测通过后，已删除可追溯的 79 条测试笔记／86 张卡，保留正式层级为空牌组；另 25 条身份不明确的旧笔记未动。本地历史草稿和旧验收证据保留，旧记录中的卡片回读表示当时的验收结果。恢复包及精确删除清单位于 [恢复目录](D:/Quarantine/KnowledgeClipper/20260910-formal-decks)，[当前验证](D:/Artifacts/KnowledgeClipper/validation/formal-decks/validation.json)记录正式设置、分流回读与清理结果。

## 配置和模块

- [desktop](../desktop/RemNoteQuickClipper.Desktop.csproj)：现有 WinForms 界面与直接流程连接。
- [workflow](../workflow/KnowledgeClipper.Workflow.csproj)：材料、草稿、内容 API、独立语音及 Anki 接口。
- [modes](../modes/modes.json)：模式识别说明、公共分类提示词、处理提示词与字段要求。每次操作保留生效快照，分类请求只发送候选说明。
- [helpers](../helpers/package.json)：selection-hook 2.1.1；Python edge-tts 7.2.8 将音频块直接送给 ffplay。
- 获准生成例句时，程序只接受目标原样出现一次的结果，并确定 UTF-16 下标；来源上下文始终使用捕获时的确切位置。
- 本机设置和草稿在 `D:\AppData\KnowledgeClipper`，提示词覆盖文件在其 `modes` 子目录，完整任务音频在 `D:\Caches\KnowledgeClipper\audio`。
- API Key 使用设置中的安全输入框或既有环境配置；不进入仓库、提示词、命令行参数或回执。

本历史阶段中，截图、原图、OCR 和 RemNote 尚未迁入新主流程；它们后续的接入与验收以上方当前版本及对应历史更新为准。旧候选、SDK 和历史报告不作为当前流程的运行前提或当前验收证据。

共用内容 CLI 接受 `selectedText`、可选上下文及 `input` 元数据；指定 `followUp` 时，附带可选 `initialContent` 和 `conversation`，直接调用同一追问模块。例如：

~~~powershell
& 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe' 'D:\Artifacts\KnowledgeClipper\build\KnowledgeClipper.ContentCli\Release\net8.0\KnowledgeClipper.ContentCli.dll' --input 'D:\Artifacts\KnowledgeClipper\validation\m3-typed-zh.json' --modes 'D:\Projects\RemNote Quick Clipper\modes' --output 'D:\Artifacts\KnowledgeClipper\validation\m3-typed-zh-result.json'
~~~

有限重试、输出结构校验及原文保真由生产模块负责。中文翻译只返回 `translation`；问答返回回答状态、答案或未解决原因。无法回答时不创建空卡，可以补充条件继续追问。接口不承诺无限模型上下文，超时或截断会保留草稿并报错。

## 已核验的边界

| 检查 | 当前证据 |
|---|---|
| 中文、英文、混合内容 | 共用内容 CLI 已有真实 API 输出；语义效果优化留待用户后续处理，不作为当前工程验收门槛 |
| 语音 | 最新预热后的首次调用至实际输出约 3.94 秒，首块约 2.80 秒；同轮后续播放约 2.48 秒，重播约 0.33 秒。首次首块后的约 1.14 秒等待仍是体验限制 |
| 语音设置与状态 | 速度、音量、音色实际生效；新预热实现的停止约 78 毫秒，停止和退出能回收本次播放器与空助手 |
| Anki | 正式层级真实 4 条笔记／6 张卡；汉语、英语、翻译、问答归属逐张回读，真实写后丢响应恢复仍正确分牌组且未再次添加；随后全部移除测试卡 |
| 预览与确认 | 确认按钮固定在底部，长内容与最小窗口均无需滚动寻找；编辑持久化、冻结确认；原失败草稿正常重试后产生 2 张实际卡片，重复确认不再调用导入器 |
| 重播与记录 | 生产界面检查验证立即重播、逐条实际播放已存音频、继续同一草稿、关闭面板不停播、最近播放记录重启后保留；用户复测全部正常 |
| 捕获局部 | 多格式剪贴板保护、并发复制、辅助进程故障、超过 200k 字符后的真实 UIA 邻接范围 |
| 真实朗读入口 | 修复后用户复测通过；点击至实际输出 3.00 秒、预览 3.54 秒，预览早于播放结束；真实来源材料和两条分支均保留在本次草稿中 |
| 浮窗与 DPI | 当前实际 192 DPI 下首次 UIA、状态、预览开关尺寸稳定；手动缩放、逻辑尺寸保存恢复、启动后完全可见均已检查 |
| M2 自动选词 | 35 项取词局部检查与 18 项界面检查通过；四个指定应用由用户实测正常，两个浮点草稿共 4 张卡片已只读核验 |
| 文字框与追问 | 路由/会话 29 项、单卡 Anki 局部 15 项、预览 39 项；真实桌面集成 62 项通过，一张翻译卡与一张追问子卡已写入和回读；用户三项手测通过，另有一张正式问答卡只读核验 |
| 逐轮问答与批量导入 | 真实桌面集成 103 项通过：先单独导入第 2 轮，再导入剩余 2 轮，恰好 3 张卡；另一个关闭上下文的会话只导入所选一轮，共 4 张实际卡片，字段、卡面、数量及可复习状态均回读 |

证据集中在 [validation](D:/Artifacts/KnowledgeClipper/validation)：[语音](D:/Artifacts/KnowledgeClipper/validation/speech.json)、[最终播放状态](D:/Artifacts/KnowledgeClipper/validation/speech-state.json)、[Anki 真实丢响应恢复](D:/Artifacts/KnowledgeClipper/validation/anki-mixed-writing-lost-response.json)、[预览确认](D:/Artifacts/KnowledgeClipper/validation/m1-preview-confirmation.json)、[窗口缩小复现](D:/Artifacts/KnowledgeClipper/validation/window-size-before.json)。首次取词自动检查未取得前台、静音、Anki 模板大小写及 HTTP chunked 兼容失败均保留；它们不覆盖后续真实结果。

本次修复的相关结果：[首次语音](D:/Artifacts/KnowledgeClipper/validation/speech-prepared-first.json)、[预热与生命周期](D:/Artifacts/KnowledgeClipper/validation/speech-latency.json)、[窗口尺寸及可见范围](D:/Artifacts/KnowledgeClipper/validation/window-size-visible-check.txt)、[预览固定按钮](D:/Artifacts/KnowledgeClipper/validation/preview-fixed-footer.txt)、[重播记录](D:/Artifacts/KnowledgeClipper/validation/history-ui-check.json)。普通接口回归 37 项、预览局部 20 项、重播界面 9 项通过；固定响应检查仅证明结构与状态。

用户复测对应草稿为 `（历史草稿 ID 见本地验收回执）`，运行数据留在本机应用目录。重播与继续制卡复测后，笔记 `1789018923487` 的 2 张实际卡片已只读回查：确认字段一致、卡面有内容、未暂停或埋藏，见[用户重播与制卡复测](D:/Artifacts/KnowledgeClipper/validation/history-user-retest.json)。人工验证由工程先准备测试条件并确认悬浮窗可见，再通过默认提问框通知和收集结果，不额外询问是否准备好。提示词 Thread 已按用户要求结束本轮优化，现有修改与评估保留，后续由用户继续。

M2 证据：[局部取词](D:/Artifacts/KnowledgeClipper/m2-local/selection-checks.txt)、[界面连接](D:/Artifacts/KnowledgeClipper/validation/selection-ui-check.json)、[用户验收与实际卡片](D:/Artifacts/KnowledgeClipper/validation/m2-user-retest.json)。浮点携带的选区时间、来源和原文位置已在实际草稿中核对；未新增查词或独立内容流程。当前检查环境为实际 192 DPI；无可用文本范围时点使用鼠标位置或来源窗口位置，未收到输入事件的程序内部自动改选由过期机制收起。

文字框与追问证据：[上一阶段验收](D:/Artifacts/KnowledgeClipper/validation/m3-input-validation.json)、[真实桌面与 Anki](D:/AppData/KnowledgeClipper/canary/input-ui/input-ui-canary.json)、[用户制卡回读](D:/Artifacts/KnowledgeClipper/validation/m3-user-anki-readback.json)。用户确认中文/英文/问号分流、追问制卡和保留行为均正常；正式悬浮窗手测后仍为 696×424、192 DPI。追问请求提供原材料、初始内容和当前有效问答；当前视图包含用户编辑或已确认冻结的答案，原始历史单独保留。新增/删除模式面板与内容效果优化仍后置。

逐轮批量导入证据：[本轮验证汇总](D:/Artifacts/KnowledgeClipper/validation/m4-question-cards-validation.json)、[真实桌面与 4 张 Anki 卡片](D:/AppData/KnowledgeClipper/canary/question-batch/input-ui-canary.json)、[首次完整集成日志](D:/Artifacts/KnowledgeClipper/validation/m4-question-batch-ui-first.txt)、[预览局部检查](D:/Artifacts/KnowledgeClipper/question-preview-local/preview-checks.txt)、[旧草稿可访问性](D:/Artifacts/KnowledgeClipper/validation/m4-routing-history-local.txt)。最近草稿不显示内部重复卡片，非语言条目无播放按钮；当前问答窗口关闭后保留历史、编辑及逐轮导入身份。局部检查为问答卡 33 项、预览 53 项、路由与历史 34 项；最终构建 0 警告、0 错误，使用原 4 张实际卡片完成 64 项恢复和回读检查，无新增卡片。用户复测基本通过：其中一轮模型返回 `unresolved` 且没有答案，按规则不生成空卡；其余两轮已写入并只读核验。该回答策略样本已留给提示词 Thread，由用户后续继续，不扩大当前工程验收范围。

常规接口检查使用 [Workflow.Tests](../tests/KnowledgeClipper.Workflow.Tests/KnowledgeClipper.Workflow.Tests.csproj)；内容、语音、Anki 和捕获各有薄 CLI，均调用生产实现。真实写入和播放检查只针对明确的隔离输入与目标，不作为每次构建的自动步骤。没有新变化或未解决问题时，不重跑整批外部检查。

当前实施依据：[单一工程接管说明](D:/Artifacts/KnowledgeClipper/architecture/knowledge-clipper-engineering-brief.md)。本任务接续 `49491fd` 和其后的既有未提交改动。旧架构文件中的双工程线、跨任务汇报和固定文件交接约定已失效；由此工程任务统一实现并直接向用户交付。历史版本由 Git 保留，不复制活动源码目录。
