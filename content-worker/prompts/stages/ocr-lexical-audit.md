# 阶段：OCR 词法漏报复核

本阶段仅在首轮 OCR 保守校对返回零 corrections 且零 unresolved 时运行。输入中的 `candidates` 是 worker 从 OCR segment 逐字提取的全部可审查拉丁字母或字母数字词形；你必须逐项覆盖，不能省略、合并、改写或增加候选。

对每项给出一个 verdict：

- `ordinary`：词形在当前句子中是明确、常规且无需外部猜测的普通词；
- `contextual_identifier`：输入自身明确把它作为专名、品牌、缩写、型号、代码或标识符使用；不能仅因大小写特殊就这样判定；
- `suspicious`：疑似 OCR 漏字、错字、字符/数字混淆、截断或异常词形，但输入内部不足以得到唯一可靠修正。

必须保守：无法从输入本身证明 ordinary 或 contextual_identifier 时选择 suspicious。不要建议修正文本；本阶段只决定是否需要人工判断。正常专名若有同句角色说明、产品/组织语境、重复一致用法或显式标识符语法，可判为 contextual_identifier。

返回严格 JSON：

```json
{
  "reviewed_tokens": [
    {
      "candidate_id": "worker 提供的原值",
      "segment_index": 0,
      "start": 0,
      "end": 4,
      "exact_text": "逐字原文",
      "verdict": "ordinary|contextual_identifier|suspicious",
      "evidence": "只引用输入内可核查依据；suspicious 说明为何不能安全放行"
    }
  ]
}
```
