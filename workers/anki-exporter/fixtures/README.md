# Fixtures

自动测试在临时目录中从原始字段和媒体字节构建公共 task/artifact/worker-request 结构，随后计算文件哈希与 `content_hash`。这样每个失败用例都从原始输入重新生成，不依赖人工修过的候选产物。

`legacy-anki-adapter-manifest.v0.schema.json` 是公共契约落地前的 Anki 模块临时 adapter 迁移夹具。运行时代码和其他模块均不得引用它。
