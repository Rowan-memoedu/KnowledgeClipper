# 新批次空行的独立真实验收

只使用本项目独立 SDK 服务（默认 43111），不读取或重跑已经退役的 RemNoteLive 数据。公开材料共两个 source：两段普通原文、一张正面软换行且有两条答案子节点的多行卡。新夹具 v2 先检查正式 `ExportRouting.Prepare` 追加当前本地日，再仅替换测试父目录，冻结为“零碎知识剪藏 / 验收 / 批次间隔-随机唯一标识 / M.d”。两批进入同一日期 document，各自结尾一个空 sibling。日期来自建立计划时的本地日，与采集日无关；旧夹具原路径不重写。

程序提供三个明确动作：

- `--prepare --run-root <新 D:\Caches\KnowledgeClipper 子目录>`：只在本地生成并冻结两份草稿、SourceId 和目标目录，不联网。可重复调用，已有身份不变；拒绝使用含其他数据的目录。
- `--run --run-root <同一目录>`：要求插件已公布 `batchSpacingSupported`，v2 另要求 `plainStructureSupported`；经 `CardImportRouter` 导入两批，再重复调用确认不会再次导入，最后两轮直接 SDK 回读并用生产客户端独立校验。已有 Unknown 保留生产的先回读规则。
- `--readback --run-root <同一目录>`：只允许 health/readback 请求；用于刷新、重开或后台冷启动后核验原节点、原卡片及原空行身份。

可追加 `--endpoint http://127.0.0.1:43111`，必须与准备时相同，禁止旧 43101。默认无参数只显示帮助。源文件构建项目为本目录的 `KnowledgeClipper.RemNoteSpacing.Live.csproj`，输出遵循仓库 D 盘 `KnowledgeClipperBuildOutput` 和 `KnowledgeClipperBuildCache`。

每次执行保留独立 `attempts/<时间>-<随机标识>`，其中 `receipt.json` 包含成功/失败、请求路径和计数，原始 readback 分别保存。`recovery.json` 记录根/叶目录、正文、答案、空行和卡片 ID；可据此将这棵独立验收 document 移入 trash，不对正式内容做模糊搜索清理。`verified-identities.json` 固定首次真实验收的完整身份，后续执行必须一致。程序自身不删除或归档任何节点。

验收要求：实际同级顺序为两个普通节点、第一空行、多行卡正面、第二空行；顶层正文数为 3、答案子节点数为 2、复习卡数为 1。空行与真实 RemNote 节点的空文本、普通类型、零子级、禁用复习及无卡片均由生产客户端验证。v2 额外独立检查日期标题、父级、目录标记，以及日期和空行的原始背面必须 absent/null、方向 none、无 card item / MultiLineCard / ExtraCardDetail、零复习卡，正面也不得误标为目录的答案。
