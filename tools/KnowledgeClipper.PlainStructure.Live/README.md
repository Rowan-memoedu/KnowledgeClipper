# 普通目录与空行的真实 SDK 验收

通过独立产品 SDK 的 43111 接口验证目录和批次空节点没有背面数组与复习卡。固定夹具位于 `D:\Caches\KnowledgeClipper\plain-structure-live-20260919`，每次原始请求、响应、错误与身份均保留；不修改用户设置或本地正式草稿。

从仓库根目录运行以下命令，并使用 Windows 进度超时工具包裹：

```powershell
node tools/KnowledgeClipper.PlainStructure.Live/run.mjs --mode prepare
node tools/KnowledgeClipper.PlainStructure.Live/run.mjs --mode run
node tools/KnowledgeClipper.PlainStructure.Live/run.mjs --mode readback
node tools/KnowledgeClipper.PlainStructure.Live/run.mjs --mode editor-canary
```

`prepare` 只创建本地固定 manifest。`run` 在已保存的来源上新建一条公开原文与一张两行答案卡，在隔离 document 下验证全部内容后仅一个空节点。发请求前持久化未知状态；后续运行先纯回读，只有服务明确返回尚未产生内容的 `notStarted` 才恢复同一来源。已验证记录不再次导入，不自动换新 ID。原源码、新目录、空节点、复习卡与同级顺序独立核验。

`editor-canary` 必须在本轮固定来源已有持久化身份后显式运行。专用测试驱动先保存原身份，再在该公开测试空节点上准备 `[]` 背面，调用正式受保护 editor 清除方法。它断言清除背面成功、原页面和编辑位置已恢复、全部 ID 不变。中途失败保留同一测试 journal，后续恢复不会补新节点。

旧正式记录可用 `--mode repair-preview --draft <原草稿绝对路径> --run-root <独立D盘证据目录>` 进行纯回读与修复预览。`--mode repair` 执行已授权的精确清除；脚本先比较冻结内容与原身份，再提交当前完整结构快照，之后独立回读正文、父子顺序和目标节点。不能核验的来源保持失败，不调用 export 或修改本地草稿。

`backTextState` 必须保持 SDK 的 `absent` / `null` / `present` 实值。前两种无背面，`present` 空数组仍有卡片分隔符；不能只用“复习卡为零”判定无制卡符。旧 SDK 声明中的 `setBackText(undefined)` 与本机实现不一致，其真实拒绝保留为失败证据。
