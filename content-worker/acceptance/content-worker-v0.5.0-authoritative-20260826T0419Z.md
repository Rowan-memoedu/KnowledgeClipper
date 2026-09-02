# Content worker v0.5.0 authoritative acceptance

Evidence type: offline replay only. DeepSeek live calls: 0. Exporter/Desktop calls: 0.

## Question workflow

Initial raw question:

> 为什么 TCP 建立连接需要三次握手？

Final answer:

> 三次握手让双方确认彼此的发送与接收能力，并同步初始序列号。

Follow-up raw question:

> 如果只有两次握手，会留下什么风险？

Final appended answer:

> 服务端无法确认客户端是否收到了自己的确认，历史失效连接请求也可能造成半开连接和资源浪费。

Final card from the persisted complete chain:

- Template: `basic_qa`
- Front: `如果只有两次握手，会留下什么风险？`
- Back: `服务端无法确认客户端是否收到了自己的确认，历史失效连接请求也可能造成半开连接和资源浪费。`
- Provenance: source

Initial and follow-up attempts publish LearningBundle + `question.answer`, never CardBundle. The make-cards attempt publishes LearningBundle + CardBundle, never a new answer artifact.

## Language practice `both`

Chinese writing card:

- Front: `他阅读完补充材料后，对这个结论仍然心存（yí dòu；疑问或疑惑）。`
- Back: `疑窦`
- Front ends in CR/LF: false

English writing card:

- Front: `The committee reached a （/ˈtentətɪv/；暂定的） conclusion after reviewing the evidence.`
- Back: `tentative`
- Front ends in CR/LF: false

The same CardBundle contains exactly two cards per ready unit, ordered `language_context` then `language_writing`.

## Independent Canary receipts

- Initial answer: `canary/jobs/20260826T041414850Z-offline-question-answer-v3-replay-e97f7734/canary-receipt.json`, SHA-256 `91AC1095871D58F769E92B5AC5AAD7064FBA381AFBA786756C98A2F58E55E3A6`.
- Follow-up: `canary/jobs/20260826T041415935Z-offline-question-followup-v3-replay-36595671/canary-receipt.json`, SHA-256 `90C0D3130F78545B250C60DF41C515210BEFBB8284B5C3DDCC61D6E95348000A`.
- Make cards: `canary/jobs/20260826T041417060Z-offline-question-make-cards-v3-replay-2aed0b49/canary-receipt.json`, SHA-256 `9D67250A4547BCAAADA07598940DD748E8F37D915F4E2F97E2D232D392681395`.
- Language both: `canary/jobs/20260826T041418150Z-offline-language-practice-v2-replay-e7deb47e/canary-receipt.json`, SHA-256 `6E7DDC2B71D7647BAB24B89A28EE288CC11CF7050D012D4C49ADAE63A3CB9E63`.

All four results are `completed`. Independent module validation additionally passed common describe/request/result/artifact schemas and every referenced module input/output schema for all four receipts.

## Regression gates

- `npm test`: 58/58 PASS.
- `npm run test:question-fixtures`: eight v3 fixture expectations PASS.
- Frozen common checkpoint: 21 files checked, 0 mismatch.
