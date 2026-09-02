# Content v0.5.0 readable acceptance

All examples below are offline replay fixtures. No live DeepSeek request and no exporter or desktop action was performed.

## Initial question → answer

Raw question:

> 为什么 TCP 建立连接需要三次握手？

Final `question.answer` text:

> 三次握手让双方确认彼此的发送与接收能力，并同步初始序列号。

Outputs: `learning-bundle`, `question.answer`; no CardBundle.

Receipt: `canary/jobs/20260826T041219587Z-offline-question-answer-v3-replay-9979a7c4/canary-receipt.json`  
SHA-256: `5daa5887b55e77e38b69de18a2124211d50eeaac06084063590ff891d0727ecf`

## Follow-up → appended answer

Persisted question/answer prefix is preserved, then the new question is appended:

> 如果只有两次握手，会留下什么风险？

Final new answer:

> 服务端无法确认客户端是否收到了自己的确认，历史失效连接请求也可能造成半开连接和资源浪费。

Outputs: `learning-bundle`, `question.answer`; the answer artifact contains turns `[0, 1]`; no CardBundle.

Receipt: `canary/jobs/20260826T041220650Z-offline-question-followup-v3-replay-259db840/canary-receipt.json`  
SHA-256: `0ec3602fde99528a9d1b5b1945a3ef50e0af7b98d20dad9d1348cfbc91a422cf`

## Complete chain → card

The persisted four-segment chain is `user, assistant, user, assistant`. Generated card:

- Template: `basic_qa`
- Front: `如果只有两次握手，会留下什么风险？`
- Back: `服务端无法确认客户端是否收到了自己的确认，历史失效连接请求也可能造成半开连接和资源浪费。`
- Answer provenance: `source`

Outputs: `learning-bundle`, `card-bundle`; no new `question.answer`.

Receipt: `canary/jobs/20260826T041221751Z-offline-question-make-cards-v3-replay-d2dfa06d/canary-receipt.json`  
SHA-256: `b4880bce94a5d83ffa0690368beaadd066a807db644d9b32beb53de6b999f81c`

## Language writing terminal-newline boundary

Raw LearningBundle segment, with visible whitespace:

`First·term·line.<LF>Second·line·keeps·two·spaces.··<LF>`

Generated front:

`First·（/tɜːm/；术语）·line.<LF>Second·line·keeps·two·spaces.··`

The internal LF and two trailing spaces remain; only the source container's terminal LF is excluded from the card front. Back is exact `term`.

Receipt: `canary/jobs/20260826T041222821Z-offline-language-writing-whitespace-v2-replay-1c4f2121/canary-receipt.json`  
SHA-256: `424e3cc9421f519bc9c40cc33c851f00d2c352f32595335eeeeea7892256ce89`

## Gates

- `npm test`: 58/58 PASS, including all eight explicit question workflow runtime cases and the legacy ordinary question→card cases.
- `npm run test:question-fixtures`: eight fixtures checked against `standard-input/3.0.0`; expected schema/runtime matrix PASS.
- `test/validate_canary.py`: all four receipts PASS common request/result/artifact plus module input/output bundle validation.
