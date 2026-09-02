# Image/OCR offline fixtures

`offline-two-image.v1.json` 是纯合成、无隐私、可离线重复的权威模块 fixture。它固定以下语义：

- 一次 capture 的题图和答案图属于同一张卡；
- `questionImages[]` 与 `answerImages[]` 是唯一角色与顺序来源；
- `artifacts[]` 故意按答案图、题图反向排列，不能被 worker 当成业务顺序；
- `success` 验证两图完成和 provider 逆序返回；
- `partial-retry` 首轮只让答案图失败，第二个不可变 attempt 必须只请求失败答案图，并按 hash 复用题图及其 OCR 产物。
- `resume` 从已存在的 MinerU submission receipt 恢复两图，不允许再次执行 submission。
- `original` 在 provider 配置存在时仍不得加载 provider，直接按显式题图/答案图顺序返回两个 `image.original`。

fixture schema 位于 `../schemas/image-ocr-offline-fixture.v1.schema.json`。二进制内容使用 Base64，并同时固定 bytes 与 SHA-256；运行器先验证 schema、引用闭包和内容 hash，再创建独立 Canary 包。
